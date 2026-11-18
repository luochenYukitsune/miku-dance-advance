using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

internal static class D3D12ViewShaderPrewarm
{
    internal static D3D12SurfaceStyle? ResolveStylizedFamily(string? pipelineId) => pipelineId switch
    {
        "genshin" => D3D12SurfaceStyle.GenshinPmx,
        "star-rail" => D3D12SurfaceStyle.StarRailPmx,
        "wuthering-waves" => D3D12SurfaceStyle.WutheringPmx,
        "endfield" => D3D12SurfaceStyle.EndfieldPmx,
        _ => null,
    };

    internal static D3D12ShaderWarmupSpec BuildFromViewRequest(
        ViewQualityOptions quality,
        IEnumerable<D3D12ViewCharacterMaterialBinding>? characterMaterials = null,
        IEnumerable<D3D12ViewFaceSdfBinding>? faceDataBindings = null,
        bool hasNativeLights = false,
        bool hasTextureMorph = false,
        bool hasSubTexture = false,
        bool hasCharacterToon = false,
        bool hasCharacterDirectPalette = false,
        bool hasOutline = true,
        bool hasCharacterOutline = false,
        bool hasDynamicVertices = true,
        bool straightAlpha = false,
        bool hasLightGizmos = false,
        IReadOnlyList<D3D12ShaderStyleWarmupVariant>? explicitStylizedVariants = null)
    {
        ArgumentNullException.ThrowIfNull(quality);
        var gi = quality.EffectiveGi;
        bool giEnabled = gi.Enabled && gi.Strength > 0;
        bool temporal = quality.EffectiveTemporalUpscaling.Mode != ViewTemporalUpscalingMode.Off;

        IReadOnlyList<D3D12ShaderStyleWarmupVariant> variants;
        if (explicitStylizedVariants is not null)
        {
            variants = explicitStylizedVariants;
        }
        else
        {
            var set = new HashSet<D3D12ShaderStyleWarmupVariant>();
            if (characterMaterials is not null)
            {
                foreach (var binding in characterMaterials)
                {
                    var s = binding.Style;
                    if (s.GenshinNonMetallicSpecular is not null || s.GenshinHairShadowAtlas is not null || s.GenshinMetallic is not null)
                    {
                        set.Add(new D3D12ShaderStyleWarmupVariant(
                            D3D12SurfaceStyle.GenshinPmx,
                            GenshinHair: s.GenshinNonMetallicSpecular is not null,
                            GenshinAtlas: s.GenshinHairShadowAtlas is not null,
                            GenshinMetallic: s.GenshinMetallic is not null,
                            GenshinMetallicSpecular: s.GenshinMetallic?.Specular is not null));
                    }
                    if (s.EndfieldHair is not null || s.EndfieldRim is not null || s.EndfieldMatcap is not null)
                    {
                        set.Add(new D3D12ShaderStyleWarmupVariant(
                            D3D12SurfaceStyle.EndfieldPmx,
                            EndfieldHair: s.EndfieldHair is not null,
                            EndfieldRim: s.EndfieldRim is not null,
                            EndfieldMatcap: s.EndfieldMatcap is not null,
                            EndfieldPalette: s.EndfieldMatcap?.Palette is not null,
                            EndfieldBaseImage: s.EndfieldMatcap?.Palette?.BaseImage is not null));
                    }
                }
            }
            if (faceDataBindings is not null)
            {
                foreach (var face in faceDataBindings)
                {
                    if (face.Encoding == D3D12FaceSdfEncoding.GenshinSoftFace)
                        set.Add(new D3D12ShaderStyleWarmupVariant(D3D12SurfaceStyle.GenshinPmx, FaceSdf: true));
                }
            }
            variants = set.ToArray();
        }

        return new D3D12ShaderWarmupSpec
        {
            Gi = giEnabled,
            GiMode = gi.Mode,
            GiComposition = gi.Composition,
            IncludeGiDenoiser = giEnabled && gi.Denoiser != RayGiDenoiser.Off,
            IncludeGiTemporal = giEnabled && gi.Denoiser == RayGiDenoiser.TemporalSpatial,
            IncludePositionGather = giEnabled && gi.Mode == LightingMode.HardwareRayTracing && hasDynamicVertices,
            NativeLights = hasNativeLights,
            TextureMorph = hasTextureMorph,
            SubTexture = hasSubTexture,
            CharacterToon = hasCharacterToon,
            CharacterDirectPalette = hasCharacterDirectPalette,
            Outline = hasOutline,
            CharacterOutline = hasCharacterOutline,
            StylizedVariants = variants,
            SmaaQuality = quality.Antialiasing == FrameAntialiasing.Smaa1X ? (quality.SmaaQuality ?? SmaaQuality.High) : null,
            SmaaAlphaCoverage = straightAlpha,
            IncludeFsr1 = quality.EffectiveUpscaling.Mode != ViewUpscalingMode.Off,
            IncludeTemporalInputs = temporal || hasLightGizmos,
            IncludeTemporalCharacter = temporal && (hasCharacterOutline || hasSubTexture || hasTextureMorph || variants.Count > 0),
            IncludeTemporalReactive = temporal && quality.EffectiveTemporalUpscaling.Mode == ViewTemporalUpscalingMode.XeSs,
            IncludeTemporalEditorVisibility = hasLightGizmos,
            IncludeTemporalColorTransfer = temporal,
            IncludeDiffusion = quality.EffectiveDiffusion.Enabled && quality.EffectiveDiffusion.Strength > 0,
            IncludeDiffusionAlphaCoverage = straightAlpha,
            IncludeAlphaResolve = straightAlpha,
            IncludeSpatialAccumulation = (quality.SpatialSamples ?? 1) > 1,
            IncludeLightGizmos = hasLightGizmos,
        };
    }
}
