using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

public sealed partial class D3D12SurfaceStyleMap
{
    private SceneTexture?[]? validatedEndfieldBaseTextures;
    private SceneTexture?[]? validatedEndfieldDetailTextures;
    private SceneTexture?[]? validatedEndfieldShadowTextures;
    internal bool HasConsumedEndfieldBaseImage => Enumerable.Range(0, Count)
        .Any(i => EffectiveEndfieldMatcap(i)?.Palette?.BaseImage is not null);

    private void ValidateEndfieldBaseImage(int index, OriginalFrameScene frame)
    {
        if (CharacterMaterials[index].EndfieldMatcap?.Palette?.BaseImage is not { } binding) return;
        var textures = frame.Surface.Snapshot.Textures;
        foreach (int? selected in new[] { binding.BaseCombinedTextureIndex, binding.DetailCombinedTextureIndex, binding.ShadowCombinedTextureIndex })
        {
            if (selected is not { } slot) continue;
            if ((uint)slot >= (uint)textures.Count)
                throw new ArgumentOutOfRangeException(nameof(CharacterMaterials), "Endfield base-image selected resource is outside the current frame texture table.");
            if (textures[slot].Kind != SceneTextureKind.Image)
                throw new ArgumentException("Endfield base/detail/shadow inputs require explicitly selected image resources, including dormant inputs.", nameof(CharacterMaterials));
        }
    }

    // Conservative RGB-wide bound includes both raster sides, signed contrast and
    // HSV cross-channel redistribution. This is an MDA finite-target guard, not a source clamp.
    private static double EndfieldBaseImageMagnitude(D3D12EndfieldBaseImageBinding b)
    {
        static double Magnitude(Vector3 v) => Math.Max(Math.Abs((double)v.X), Math.Max(Math.Abs((double)v.Y), Math.Abs((double)v.Z)));
        double x = b.BaseInputMode == D3D12EndfieldPaletteInputMode.Texture ? 1 : Magnitude(b.BaseImageLinearRgb);
        // Facing is a convex mix of M and M squared. Backfacing then squares
        // the composed X again, so signed/HDR inputs can reach fourth power.
        if (b.FacingBase is { Blend: > 0 }) x = Math.Max(x, x*x);
        double back = Math.Max(x, x*x), contrast = Math.Abs((double)b.Contrast);
        double contrasted = back*(1+contrast)+back*back*contrast;
        double detail = b.DetailInputMode == D3D12EndfieldPaletteInputMode.Texture ? 1 : Math.Abs(.5+.5*b.DetailBlue);
        double shadow = Math.Max(1, Math.Max(Magnitude(b.ModelColor), Magnitude(b.ShadowColor)));
        double shadowFactor = Math.Clamp((double)b.ShadowStrength, 0, 1);
        return contrasted*Magnitude(b.ColorMultiplier)*detail*((1-shadowFactor)+shadowFactor*shadow);
    }

    private void CaptureEndfieldBaseImage(OriginalFrameScene frame)
    {
        if (!HasConsumedEndfieldBaseImage) return;
        var textures = frame.Surface.Snapshot.Textures;
        validatedEndfieldBaseTextures = new SceneTexture?[Count];
        validatedEndfieldDetailTextures = new SceneTexture?[Count];
        validatedEndfieldShadowTextures = new SceneTexture?[Count];
        for (int i=0;i<Count;i++)
        {
            if (EffectiveEndfieldMatcap(i)?.Palette?.BaseImage is not { } b) continue;
            if (b.ConsumesBaseTexture) validatedEndfieldBaseTextures[i]=textures[b.BaseCombinedTextureIndex!.Value];
            if (b.ConsumesDetailTexture) validatedEndfieldDetailTextures[i]=textures[b.DetailCombinedTextureIndex!.Value];
            if (b.ConsumesShadowTexture) validatedEndfieldShadowTextures[i]=textures[b.ShadowCombinedTextureIndex!.Value];
        }
    }

    private static bool SameEndfieldBaseImageInputs(D3D12SurfaceStyleMap a,D3D12SurfaceStyleMap b)
    {
        if (a.validatedEndfieldBaseTextures is null && b.validatedEndfieldBaseTextures is null) return true;
        for (int i=0;i<a.Count;i++)
            if (!SameChannels(a.validatedEndfieldBaseTextures?[i],b.validatedEndfieldBaseTextures?[i],7)
                || !SameChannels(a.validatedEndfieldDetailTextures?[i],b.validatedEndfieldDetailTextures?[i],4)
                || !SameChannels(a.validatedEndfieldShadowTextures?[i],b.validatedEndfieldShadowTextures?[i],1)) return false;
        return true;
    }
}
