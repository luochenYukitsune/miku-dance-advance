using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>The actual RGB surface shader family selected for a material draw.</summary>
public enum D3D12SurfaceStyle { Mmd, GenshinPmx, StarRailPmx, WutheringPmx, EndfieldPmx }

/// <summary>Renderer-owned immutable shader selections in the original frame's material order.
/// The ordinary-PMX families retain shared geometry, coverage, lighting and resource ownership.</summary>
public sealed partial class D3D12SurfaceStyleMap
{
    public IReadOnlyList<D3D12SurfaceStyle> Materials { get; }
    public IReadOnlyList<D3D12CharacterMaterialStyle> CharacterMaterials { get; }
    public int Count => Materials.Count;
    public bool IsDefault { get; }
    private SceneTexture?[]? validatedEndfieldHairTextures;
    private SceneRawRadialSeeds? validatedEndfieldRawSeeds;
    private SceneTexture?[]? validatedFaceTextures;
    private SceneTexture?[]? validatedSecondaryFaceTextures;
    private SceneTexture?[]? validatedGenshinHairTextures;
    private SceneTexture?[]? validatedGenshinAtlasLightmaps;
    private SceneTexture?[]? validatedGenshinAtlasColors;
    private SceneTexture?[]? validatedStarMcHighlightTextures;
    private SceneTexture?[]? validatedStarMcShadowTextures;
    internal bool HasConsumedFaceSdf => Enumerable.Range(0, Count).Any(i => EffectiveFaceSdf(i) is not null);
    internal bool HasConsumedPackedFaceSdf => Enumerable.Range(0, Count)
        .Any(i => EffectiveFaceSdf(i)?.HasSecondaryImage == true);
    internal bool HasConsumedGenshinNonMetallicSpecular => Enumerable.Range(0, Count)
        .Any(i => EffectiveGenshinNonMetallicSpecular(i) is not null);
    internal bool HasConsumedGenshinHairShadowAtlas => Enumerable.Range(0, Count)
        .Any(i => EffectiveGenshinHairShadowAtlas(i) is not null);
    internal bool HasConsumedStarMcHair => Enumerable.Range(0, Count)
        .Any(i => EffectiveStarMcHair(i) is not null);

    internal bool HasConsumedEndfieldRim => Enumerable.Range(0, Count)
        .Any(i => EffectiveEndfieldRim(i) is not null);

    internal bool HasConsumedEndfieldHair => Enumerable.Range(0, Count)
        .Any(i => EffectiveEndfieldHair(i) is not null);

    public D3D12SurfaceStyleMap(IEnumerable<D3D12SurfaceStyle> materials,
        IEnumerable<D3D12CharacterMaterialStyle>? characterMaterials = null)
    {
        ArgumentNullException.ThrowIfNull(materials);
        Materials = Array.AsReadOnly(materials.ToArray());
        CharacterMaterials = Array.AsReadOnly(characterMaterials?.Select(material =>
            material is { PmxMaterialTreatment: { } treatment }
                ? material with { PmxMaterialTreatment = treatment with { } } : material).ToArray()
            ?? Enumerable.Repeat(new D3D12CharacterMaterialStyle(), Materials.Count).ToArray());
        IsDefault = Materials.All(style => style == D3D12SurfaceStyle.Mmd);
    }

    /// <summary>Rejects unsupported or incomplete selections before any GPU recording or SDK work.</summary>
    public void Validate(OriginalFrameScene frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (CharacterMaterials.Count != Count || Count != frame.Materials.Count || Count != frame.Surface.Materials.Count
            || Count != frame.Surface.Snapshot.Materials.Count
            || (frame.Outline is not null && Count != frame.Outline.Materials.Count))
            throw new ArgumentException("One surface shader family per frame flag, surface binding and scene material is required.", nameof(frame));
        foreach (var style in Materials)
            if (!Enum.IsDefined(style)) throw new ArgumentOutOfRangeException(nameof(Materials), "Unknown surface shader family.");
        foreach (var material in CharacterMaterials)
        {
            if (material is null) throw new ArgumentException("Character material settings cannot contain null entries.", nameof(CharacterMaterials));
            material.Validate();
        }
        if (HasConsumedEndfieldHair)
        {
            var snapshot = frame.Surface.Snapshot;
            var seeds = snapshot.RawRadialSeeds;
            if (seeds is null || seeds.Vertices.Length != snapshot.VertexCount)
                throw new ArgumentException("Dedicated Endfield radial hair requires a matching immutable raw radial seed stream.", nameof(frame));
            // Immutable seeds are already finite. Identity avoids a redundant full-stream scan.
            if (snapshot.World != System.Numerics.Matrix4x4.Identity)
            foreach (var seed in seeds.Vertices)
            {
                var worldSeed = System.Numerics.Vector3.TransformNormal(seed, snapshot.World);
                if (!float.IsFinite(worldSeed.X) || !float.IsFinite(worldSeed.Y) || !float.IsFinite(worldSeed.Z))
                    throw new ArgumentException("Endfield raw radial seed direction overflows its scene World transform.", nameof(frame));
            }
        }
        for (int i = 0; i < Count; i++)
        {
            if (EffectivePmxMaterialTreatment(i) is not null)
                CharacterToonMath.Validate(EffectiveToonResponse(i, frame.Materials[i].CharacterToonResponse)!);
            if (CharacterMaterials[i].FaceSdf is { } sdf)
            {
                bool supported = sdf.Encoding switch
                {
                    D3D12FaceSdfEncoding.GrayRed => Materials[i] is D3D12SurfaceStyle.StarRailPmx or D3D12SurfaceStyle.WutheringPmx,
                    D3D12FaceSdfEncoding.EndfieldRgbCmM => Materials[i] == D3D12SurfaceStyle.EndfieldPmx,
                    D3D12FaceSdfEncoding.GenshinSoftFace => Materials[i] == D3D12SurfaceStyle.GenshinPmx,
                    _ => false,
                };
                if (!supported)
                    throw new NotSupportedException("FaceSDF encoding must match its explicitly supported surface family.");
                if (sdf.CombinedTextureIndex >= frame.Surface.Snapshot.Textures.Count)
                    throw new ArgumentOutOfRangeException(nameof(CharacterMaterials), "FaceSDF combined texture index is outside the actual frame texture table.");
                if (sdf.SecondaryCombinedTextureIndex >= frame.Surface.Snapshot.Textures.Count)
                    throw new ArgumentOutOfRangeException(nameof(CharacterMaterials), "FaceSDF secondary combined texture index is outside the actual frame texture table.");
            }
            if (CharacterMaterials[i].GenshinNonMetallicSpecular is { } hair)
            {
                if (Materials[i] != D3D12SurfaceStyle.GenshinPmx)
                    throw new NotSupportedException("Dedicated Genshin nonmetallic specular requires its explicitly selected Genshin surface family.");
                if (hair.CombinedTextureIndex >= frame.Surface.Snapshot.Textures.Count)
                    throw new ArgumentOutOfRangeException(nameof(CharacterMaterials), "Genshin nonmetallic specular combined texture index is outside the actual frame texture table.");
            }
            if (CharacterMaterials[i].GenshinHairShadowAtlas is { } atlas)
            {
                if (Materials[i] != D3D12SurfaceStyle.GenshinPmx)
                    throw new NotSupportedException("Genshin hair shadow atlas requires its explicitly selected Genshin surface family.");
                if (atlas.LightmapCombinedTextureIndex >= frame.Surface.Snapshot.Textures.Count
                    || atlas.AtlasCombinedTextureIndex >= frame.Surface.Snapshot.Textures.Count)
                    throw new ArgumentOutOfRangeException(nameof(CharacterMaterials), "Genshin hair atlas texture index is outside the actual frame texture table.");
            }
            if (CharacterMaterials[i].EndfieldRim is not null && Materials[i] != D3D12SurfaceStyle.EndfieldPmx)
                throw new NotSupportedException("Explicit Endfield rim requires its explicitly selected Endfield surface family.");
            ValidateEndfieldMatcap(i, frame);
            ValidateEndfieldPalette(i, frame);
            ValidateEndfieldBaseImage(i, frame);
            ValidateGenshinMetallic(i, frame);
            ValidateStarMcBody(i, frame);
            if (CharacterMaterials[i].EndfieldHair is { } radialHair)
            {
                if (Materials[i] != D3D12SurfaceStyle.EndfieldPmx)
                    throw new NotSupportedException("Dedicated Endfield radial hair requires its explicitly selected Endfield surface family.");
                if (radialHair.CombinedTextureIndex >= frame.Surface.Snapshot.Textures.Count)
                    throw new ArgumentOutOfRangeException(nameof(CharacterMaterials), "Endfield numerical Green texture is outside the frame texture table.");
            }
            if (CharacterMaterials[i].StarMcHair is { } starMcHair)
            {
                if (Materials[i] is not (D3D12SurfaceStyle.StarRailPmx or D3D12SurfaceStyle.WutheringPmx))
                    throw new NotSupportedException("Dedicated Star/Wuthering hair requires its explicitly selected Star Rail or Wuthering surface family.");
                if (Materials[i] == D3D12SurfaceStyle.StarRailPmx && starMcHair.HighlightChannel != D3D12HairHighlightChannel.Blue)
                    throw new NotSupportedException("Dedicated Star Rail hair requires numerical Blue highlight data; Alpha is explicitly Wuthering only.");
                if (starMcHair.HighlightCombinedTextureIndex >= frame.Surface.Snapshot.Textures.Count)
                    throw new ArgumentOutOfRangeException(nameof(CharacterMaterials), "Star/Wuthering highlight combined texture index is outside the actual frame texture table.");
                if (starMcHair.ShadowCombinedTextureIndex >= frame.Surface.Snapshot.Textures.Count)
                    throw new ArgumentOutOfRangeException(nameof(CharacterMaterials), "Star/Wuthering shadow combined texture index is outside the actual frame texture table.");
            }
        }
        if (!IsDefault && frame.Options is null)
            throw new NotSupportedException("Non-MMD surface shader families require explicit modern frame options.");
    }

    /// <summary>Also validates the finite diffuse domain consumed by the selected native-light budget.
    /// The single-argument overload retains structural validation for internal recording.</summary>
    public void Validate(OriginalFrameScene frame, RayGiOptions? rayGiOptions)
    {
        Validate(frame);
        D3D12StylizedLightBudgetValidator.Validate(frame, this, rayGiOptions ?? frame.Options?.RayGi);
    }

    internal D3D12FaceSdfBinding? EffectiveFaceSdf(int materialIndex)
        => CharacterMaterials[materialIndex].Role == D3D12CharacterMaterialRole.Face
            && CharacterMaterials[materialIndex].FaceNormalShadeStrength != 0
                ? CharacterMaterials[materialIndex].FaceSdf : null;

    internal D3D12GenshinNonMetallicSpecularBinding? EffectiveGenshinNonMetallicSpecular(int materialIndex)
        => Materials[materialIndex] == D3D12SurfaceStyle.GenshinPmx
            && CharacterMaterials[materialIndex].Role == D3D12CharacterMaterialRole.Hair
                ? CharacterMaterials[materialIndex].GenshinNonMetallicSpecular : null;

    internal D3D12GenshinHairShadowAtlasBinding? EffectiveGenshinHairShadowAtlas(int materialIndex)
        => Materials[materialIndex] == D3D12SurfaceStyle.GenshinPmx
            && CharacterMaterials[materialIndex].Role is D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair
                ? CharacterMaterials[materialIndex].GenshinHairShadowAtlas : null;

    internal D3D12StarMcHairBinding? EffectiveStarMcHair(int materialIndex)
        => (Materials[materialIndex] is D3D12SurfaceStyle.StarRailPmx or D3D12SurfaceStyle.WutheringPmx)
            && CharacterMaterials[materialIndex].Role == D3D12CharacterMaterialRole.Hair
                ? CharacterMaterials[materialIndex].StarMcHair : null;

    internal D3D12EndfieldRimBinding? EffectiveEndfieldRim(int materialIndex)
        => Materials[materialIndex] == D3D12SurfaceStyle.EndfieldPmx
            && CharacterMaterials[materialIndex].Role is D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair
            && CharacterMaterials[materialIndex].EndfieldRim is { EdgeStrength: not 0 } rim ? rim : null;

    internal D3D12EndfieldHairBinding? EffectiveEndfieldHair(int materialIndex)
        => Materials[materialIndex] == D3D12SurfaceStyle.EndfieldPmx
            && CharacterMaterials[materialIndex].Role == D3D12CharacterMaterialRole.Hair
                ? CharacterMaterials[materialIndex].EndfieldHair : null;

    /// <summary>Detached values consumed only by explicitly opted-in ordinary modern PMX materials.
    /// Original MMD, absent and identity treatments preserve the established response.</summary>
    public D3D12PmxMaterialTreatment? EffectivePmxMaterialTreatment(int materialIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(materialIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(materialIndex, Count);
        if (Materials[materialIndex] is not (D3D12SurfaceStyle.GenshinPmx or D3D12SurfaceStyle.StarRailPmx
            or D3D12SurfaceStyle.WutheringPmx or D3D12SurfaceStyle.EndfieldPmx)) return null;
        var material = CharacterMaterials[materialIndex];
        if (material.Role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair)
            || material.FaceSdf is not null || material.GenshinNonMetallicSpecular is not null || material.StarMcHair is not null || material.EndfieldHair is not null || material.EndfieldRim is not null || material.EndfieldMatcap is not null || material.GenshinHairShadowAtlas is not null || material.GenshinMetallic is not null || material.StarMcBody is not null)
            return null;
        return material.PmxMaterialTreatment is { } treatment
            && (treatment.Saturation != 1 || treatment.TransitionWidthScale != 1
                || treatment.ValueContrast != 0 || treatment.ShadeGamma != 1 || treatment.HairHighlightStrength != 0) ? treatment : null;
    }

    // Called after the existing modern response selection during recording, and with
    // frame flags during validation. Both use the same modern default when needed.
    internal CharacterToonResponse? EffectiveToonResponse(int materialIndex, CharacterToonResponse? response)
    {
        if (EffectivePmxMaterialTreatment(materialIndex) is not { } treatment) return response;
        response ??= new CharacterToonResponse();
        return response with { TransitionWidth = response.TransitionWidth * treatment.TransitionWidthScale };
    }

    // A detached snapshot is committed only after Submit. Reusing a public map with a
    // new frame texture table must not overwrite the previous frame's comparison data.
    internal D3D12SurfaceStyleMap CaptureFaceTextures(OriginalFrameScene frame)
    {
        if (!HasConsumedFaceSdf && !HasConsumedGenshinNonMetallicSpecular && !HasConsumedStarMcHair && !HasConsumedEndfieldHair && !HasConsumedGenshinHairShadowAtlas && !HasConsumedEndfieldMatcap && !HasConsumedGenshinMetallic && !HasConsumedStarMcBody) return this;
        var captured = new D3D12SurfaceStyleMap(Materials, CharacterMaterials)
        {
            validatedGenshinAtlasLightmaps = Enumerable.Range(0, Count).Select(i => EffectiveGenshinHairShadowAtlas(i) is { } atlas
                ? frame.Surface.Snapshot.Textures[atlas.LightmapCombinedTextureIndex] : null).ToArray(),
            validatedGenshinAtlasColors = Enumerable.Range(0, Count).Select(i => EffectiveGenshinHairShadowAtlas(i) is { } atlas
                ? frame.Surface.Snapshot.Textures[atlas.AtlasCombinedTextureIndex] : null).ToArray(),
            validatedEndfieldRawSeeds = HasConsumedEndfieldHair ? frame.Surface.Snapshot.RawRadialSeeds : null,
            validatedEndfieldHairTextures = Enumerable.Range(0, Count).Select(i => EffectiveEndfieldHair(i) is { } hair
                ? frame.Surface.Snapshot.Textures[hair.CombinedTextureIndex] : null).ToArray(),
            validatedFaceTextures = Enumerable.Range(0, Count).Select(i => EffectiveFaceSdf(i) is { } sdf
                ? frame.Surface.Snapshot.Textures[sdf.CombinedTextureIndex] : null).ToArray(),
            validatedSecondaryFaceTextures = Enumerable.Range(0, Count).Select(i => EffectiveFaceSdf(i) is
                { HasSecondaryImage: true } sdf
                    ? frame.Surface.Snapshot.Textures[sdf.SecondaryCombinedTextureIndex] : null).ToArray(),
            validatedGenshinHairTextures = Enumerable.Range(0, Count).Select(i => EffectiveGenshinNonMetallicSpecular(i) is { } hair
                ? frame.Surface.Snapshot.Textures[hair.CombinedTextureIndex] : null).ToArray(),
            validatedStarMcHighlightTextures = Enumerable.Range(0, Count).Select(i => EffectiveStarMcHair(i) is { } hair
                ? frame.Surface.Snapshot.Textures[hair.HighlightCombinedTextureIndex] : null).ToArray(),
            validatedStarMcShadowTextures = Enumerable.Range(0, Count).Select(i => EffectiveStarMcHair(i) is { } hair
                ? frame.Surface.Snapshot.Textures[hair.ShadowCombinedTextureIndex] : null).ToArray()
        };
        captured.CaptureEndfieldMatcap(frame);
        captured.CaptureEndfieldPalette(frame);
        captured.CaptureEndfieldBaseImage(frame);
        captured.CaptureGenshinMetallic(frame);
        captured.CaptureStarMcBody(frame);
        return captured;
    }

    /// <summary>The consumed shader value. Body, Eye, Hair and MMD materials always retain one.</summary>
    public float EffectiveNormalShadeStrength(int materialIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(materialIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(materialIndex, Count);
        return (Materials[materialIndex] is D3D12SurfaceStyle.GenshinPmx or D3D12SurfaceStyle.StarRailPmx
            or D3D12SurfaceStyle.WutheringPmx or D3D12SurfaceStyle.EndfieldPmx)
            && CharacterMaterials[materialIndex].Role == D3D12CharacterMaterialRole.Face
                ? CharacterMaterials[materialIndex].FaceNormalShadeStrength : 1;
    }

    /// <summary>Explicit Face or Eye attenuation of computed geometric shadow visibility.
    /// One retains the original cast shadow; MMD, Body and Hair always retain one.</summary>
    public float EffectiveGeometricShadowStrength(int materialIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(materialIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(materialIndex, Count);
        if (Materials[materialIndex] is not (D3D12SurfaceStyle.GenshinPmx or D3D12SurfaceStyle.StarRailPmx
            or D3D12SurfaceStyle.WutheringPmx or D3D12SurfaceStyle.EndfieldPmx)) return 1;
        var material = CharacterMaterials[materialIndex];
        return material.Role switch
        {
            D3D12CharacterMaterialRole.Face => material.FaceGeometricShadowStrength,
            D3D12CharacterMaterialRole.Eye => material.EyeGeometricShadowStrength,
            _ => 1,
        };
    }

    /// <summary>Null and all-MMD maps select the same established default. Other maps compare actual families,
    /// including material order, independently of preset names or scene instance identities.</summary>
    public static bool Equivalent(D3D12SurfaceStyleMap? a, D3D12SurfaceStyleMap? b)
    {
        if (ReferenceEquals(a, b)) return true;
        bool aDefault = a is null || a.IsDefault, bDefault = b is null || b.IsDefault;
        if (aDefault || bDefault) return aDefault && bDefault;
        if (!a!.Materials.SequenceEqual(b!.Materials)) return false;
        for (int i = 0; i < a.Count; i++)
            if (a.EffectiveEndfieldRim(i) != b.EffectiveEndfieldRim(i)
                || a.EffectiveNormalShadeStrength(i) != b.EffectiveNormalShadeStrength(i)
                || a.EffectiveGeometricShadowStrength(i) != b.EffectiveGeometricShadowStrength(i)
                || !D3D12PmxMaterialTreatment.ConsumedEquivalent(a.EffectivePmxMaterialTreatment(i), b.EffectivePmxMaterialTreatment(i))
                || !D3D12FaceSdfBinding.ConsumedEquivalent(a.EffectiveFaceSdf(i), b.EffectiveFaceSdf(i),
                    a.validatedFaceTextures is null || b.validatedFaceTextures is null)
                || !D3D12GenshinNonMetallicSpecularBinding.ConsumedEquivalent(a.EffectiveGenshinNonMetallicSpecular(i),
                    b.EffectiveGenshinNonMetallicSpecular(i),
                    a.validatedGenshinHairTextures is null || b.validatedGenshinHairTextures is null)
                || !D3D12GenshinHairShadowAtlasBinding.ConsumedEquivalent(a.EffectiveGenshinHairShadowAtlas(i),
                    b.EffectiveGenshinHairShadowAtlas(i),
                    a.validatedGenshinAtlasLightmaps is null || b.validatedGenshinAtlasLightmaps is null)
                || !D3D12EndfieldHairBinding.ConsumedEquivalent(a.EffectiveEndfieldHair(i), b.EffectiveEndfieldHair(i),
                    a.validatedEndfieldHairTextures is null || b.validatedEndfieldHairTextures is null)
                || !D3D12EndfieldMatcapBinding.ConsumedEquivalent(a.EffectiveEndfieldMatcap(i), b.EffectiveEndfieldMatcap(i),
                    a.validatedMatcapNormalTextures is null || b.validatedMatcapNormalTextures is null)
                || !D3D12GenshinMetallicBinding.ConsumedEquivalent(a.EffectiveGenshinMetallic(i),b.EffectiveGenshinMetallic(i),
                    a.validatedGenshinMetallicLightmaps is null || b.validatedGenshinMetallicLightmaps is null)
                || !D3D12StarMcHairBinding.ConsumedEquivalent(a.EffectiveStarMcHair(i), b.EffectiveStarMcHair(i),
                    a.validatedStarMcHighlightTextures is null || b.validatedStarMcHighlightTextures is null
                    || a.validatedStarMcShadowTextures is null || b.validatedStarMcShadowTextures is null)) return false;
        if (a.validatedFaceTextures is not null || b.validatedFaceTextures is not null)
            for (int i = 0; i < a.Count; i++)
            {
                var binding = a.EffectiveFaceSdf(i);
                if (!SameChannels(a.validatedFaceTextures?[i], b.validatedFaceTextures?[i], binding?.HasSecondaryImage == true ? 7 : 1)) return false;
                if (binding?.HasSecondaryImage == true && !SameChannels(a.validatedSecondaryFaceTextures?[i], b.validatedSecondaryFaceTextures?[i],
                    binding.Encoding == D3D12FaceSdfEncoding.GenshinSoftFace ? 10 : 3)) return false;
            }
        if (a.validatedGenshinAtlasLightmaps is not null || b.validatedGenshinAtlasLightmaps is not null)
            for (int i = 0; i < a.Count; i++)
            {
                if (!SameChannels(a.validatedGenshinAtlasLightmaps?[i], b.validatedGenshinAtlasLightmaps?[i], 0x2)) return false;
                if (!SameChannels(a.validatedGenshinAtlasColors?[i], b.validatedGenshinAtlasColors?[i], 0x7)) return false;
            }
        if (a.validatedGenshinHairTextures is not null || b.validatedGenshinHairTextures is not null)
            for (int i = 0; i < a.Count; i++)
                if (!SameChannels(a.validatedGenshinHairTextures?[i], b.validatedGenshinHairTextures?[i], 0x5)) return false;
        if (a.validatedStarMcHighlightTextures is not null || b.validatedStarMcHighlightTextures is not null
            || a.validatedStarMcShadowTextures is not null || b.validatedStarMcShadowTextures is not null)
            for (int i = 0; i < a.Count; i++)
            {
                int highlightMask = a.EffectiveStarMcHair(i)?.HighlightChannel == D3D12HairHighlightChannel.Alpha ? 0x8 : 0x4;
                if (!SameChannels(a.validatedStarMcHighlightTextures?[i], b.validatedStarMcHighlightTextures?[i], highlightMask)) return false;
                if (!SameChannels(a.validatedStarMcShadowTextures?[i], b.validatedStarMcShadowTextures?[i], 0x1)) return false;
            }
        if (a.validatedEndfieldHairTextures is not null || b.validatedEndfieldHairTextures is not null)
        {
            for (int i = 0; i < a.Count; i++)
                if (!SameChannels(a.validatedEndfieldHairTextures?[i], b.validatedEndfieldHairTextures?[i], 0x2)) return false;
            if (!SameSeeds(a.validatedEndfieldRawSeeds, b.validatedEndfieldRawSeeds)) return false;
        }
        return SameEndfieldBaseImageInputs(a, b) && SameEndfieldPaletteInputs(a, b) && SameEndfieldMatcapInputs(a, b) && SameGenshinMetallicInputs(a,b) && SameStarMcBodyInputs(a,b);
    }

    private static bool SameSeeds(SceneRawRadialSeeds? a, SceneRawRadialSeeds? b)
        => ReferenceEquals(a, b) || a is not null && b is not null && a.Vertices.SequenceEqual(b.Vertices);

    private static bool SameChannels(SceneTexture? left, SceneTexture? right, int channelMask)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Width != right.Width || left.Height != right.Height) return false;
        for (int offset = 0; offset < left.Rgba.Length; offset += 4)
            for (int channel = 0; channel < 4; channel++)
                if ((channelMask & (1 << channel)) != 0 && left.Rgba[offset + channel] != right.Rgba[offset + channel]) return false;
        return true;
    }
}
