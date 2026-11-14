using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

public sealed partial class D3D12SurfaceStyleMap
{
    private SceneTexture?[]? validatedEndfieldPaletteEmissionTextures;
    private SceneTexture?[]? validatedEndfieldPaletteLightTextures;

    internal bool HasConsumedEndfieldPalette => Enumerable.Range(0, Count)
        .Any(i => EffectiveEndfieldMatcap(i)?.Palette is not null);

    private void ValidateEndfieldPalette(int index, OriginalFrameScene frame)
    {
        if (CharacterMaterials[index].EndfieldMatcap?.Palette is not { } palette) return;
        ValidateEndfieldPaletteAdapterRange(index, palette);
        var textures = frame.Surface.Snapshot.Textures;
        void Image(int? selected, string role)
        {
            if (selected is not { } slot) return;
            if ((uint)slot >= (uint)textures.Count)
                throw new ArgumentOutOfRangeException(nameof(CharacterMaterials), $"Endfield palette {role} image is outside the current frame texture table.");
            if (textures[slot].Kind != SceneTextureKind.Image)
                throw new ArgumentException($"Endfield palette {role} must be an explicitly selected image resource.", nameof(CharacterMaterials));
        }
        // An authored dormant input still must identify a real image, so re-enabling
        // its factor cannot silently replace or reinterpret a requested resource.
        Image(palette.EmissionCombinedTextureIndex, "numerical Blue");
        Image(palette.ReceivedLightCombinedTextureIndex, "linear received-light RGB");
        if (palette.Density == 0) return;
        // MatCap validation already checked source/corner parity. Read that same
        // draw's current selected UV directly, without rebuilding Mikk tangents.
        var parent = CharacterMaterials[index].EndfieldMatcap!;
        var snapshot = frame.Surface.Snapshot;
        var material = snapshot.Materials[index];
        float scale = 100 * palette.Density;
        foreach (uint vertexIndex in snapshot.Indices.Slice(material.StartIndex, material.IndexCount))
        {
            int vertex = checked((int)vertexIndex);
            float u;
            if (parent.UvChannel == 0) u = snapshot.GetVertex(vertex).Uv.X;
            else
            {
                var additional = snapshot.AdditionalUvs!.Vertices[vertex];
                u = parent.UvChannel switch { 1 => additional.Uv1.X, 2 => additional.Uv2.X,
                    3 => additional.Uv3.X, 4 => additional.Uv4.X, _ => throw new InvalidOperationException() };
            }
            float phaseDomain = u * scale;
            if (!float.IsFinite(phaseDomain) || MathF.Abs(phaseDomain) > 1_000_000)
                throw new ArgumentException("Endfield palette selected effective UV.x * 100 * density must remain finite with absolute value at most 1,000,000.", nameof(frame));
        }
    }

    // This is an MDA FP16 adapter-domain guard, not a recovered source clamp.
    // Bound only this signed palette delta; unrelated generic surface/GI remains
    // owned by the existing renderer. No valid source equation is shader-clamped.
    private void ValidateEndfieldPaletteAdapterRange(int index, D3D12EndfieldPaletteBinding palette)
    {
        var style = CharacterMaterials[index];
        var parent = style.EndfieldMatcap!;
        var rim = style.EndfieldRim;
        double r = Math.Abs((double)(rim?.EdgeStrength ?? 0));
        double p = style.EndfieldHair is { } hair
            ? .75 * Math.Abs((double)hair.Brightness + 1.5001001358032227) : 0;
        double baseline = Math.Abs((double)parent.BaselineStrength);
        double blue = palette.EmissionInputMode == D3D12EndfieldPaletteInputMode.Texture
            ? 1 : Math.Abs((double)palette.UniformEmissionBlue);
        double influence = Math.Clamp((double)palette.LightInfluence, 0, 1);
        var initial = parent.BaseColor;
        double? provided = palette.BaseImage is { } input ? EndfieldBaseImageMagnitude(input) : null;
        var highlight = parent.HighlightColor;
        var light = palette.ReceivedLightLinearRgb;
        var tint = rim?.RimColor ?? System.Numerics.Vector3.Zero;
        double litBound = 0, oldHairBound = 0, oldRimBound = 0, baselineBound = 0;
        for (int channel = 0; channel < 3; channel++)
        {
            double oldInitial = initial[channel];
            double cpre = provided ?? oldInitial;
            // Numerical image channels and filtered MatCap alpha are UNorm [0,1].
            double cmat = cpre * Math.Max(1, highlight[channel]);
            double partial = cmat + cpre * 2 * blue * Math.Abs((double)palette.EmissionStrength)
                + influence * (palette.ReceivedLightInputMode == D3D12EndfieldPaletteInputMode.Texture
                    ? 1 : Math.Abs(light[channel]));
            // A negative rim has factor zero; it still enters the final strength.
            double f = Math.Clamp((double)(rim?.EdgeStrength ?? 0), 0, 1);
            double coefficient = f * (2 * tint[channel] - 1);
            double OverlayBound(double a) => a * Math.Max(1, Math.Max(Math.Abs(1 + coefficient), Math.Abs(1 - coefficient)))
                + Math.Abs(coefficient);
            litBound = Math.Max(litBound, OverlayBound(partial));
            oldHairBound = Math.Max(oldHairBound, oldInitial * p);
            oldRimBound = Math.Max(oldRimBound, r == 0 ? 0 : OverlayBound(oldInitial) * (baseline + r) + oldInitial * baseline);
            baselineBound = Math.Max(baselineBound, cpre * baseline);
        }
        // HSV can redistribute signed RGB between channels while clamping its
        // saturation. Use the RGB-wide magnitude bound, not a per-channel one.
        // V is scaled by .8, and wave is a convex interpolation with the input.
        double final = litBound * (baseline + r + p);
        double delta = final + baselineBound + oldHairBound + oldRimBound;
        if (!double.IsFinite(delta) || delta > 60_000)
            throw new ArgumentException("Endfield downstream palette exceeds the MDA FP16 adapter safety domain; reduce the combined palette colors or strengths before recording.", nameof(CharacterMaterials));
    }

    private void CaptureEndfieldPalette(OriginalFrameScene frame)
    {
        if (!HasConsumedEndfieldPalette) return;
        var textures = frame.Surface.Snapshot.Textures;
        validatedEndfieldPaletteEmissionTextures = new SceneTexture?[Count];
        validatedEndfieldPaletteLightTextures = new SceneTexture?[Count];
        for (int i = 0; i < Count; i++)
        {
            if (EffectiveEndfieldMatcap(i)?.Palette is not { } palette) continue;
            if (palette.ConsumesEmissionTexture)
                validatedEndfieldPaletteEmissionTextures[i] = textures[palette.EmissionCombinedTextureIndex!.Value];
            if (palette.ConsumesReceivedLightTexture)
                validatedEndfieldPaletteLightTextures[i] = textures[palette.ReceivedLightCombinedTextureIndex!.Value];
        }
    }

    private static bool SameEndfieldPaletteInputs(D3D12SurfaceStyleMap a, D3D12SurfaceStyleMap b)
    {
        if (a.validatedEndfieldPaletteEmissionTextures is null && b.validatedEndfieldPaletteEmissionTextures is null) return true;
        for (int i = 0; i < a.Count; i++)
            if (!SameChannels(a.validatedEndfieldPaletteEmissionTextures?[i], b.validatedEndfieldPaletteEmissionTextures?[i], 0x4)
                || !SameChannels(a.validatedEndfieldPaletteLightTextures?[i], b.validatedEndfieldPaletteLightTextures?[i], 0x7)) return false;
        return true;
    }
}
