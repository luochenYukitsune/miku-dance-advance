using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

internal static class D3D12ViewShaderPrewarm
{
    internal static D3D12ShaderWarmupSpec BuildFromViewRequest(
        ViewQualityOptions quality,
        D3D12ViewStyleCatalog? styleCatalog = null,
        IEnumerable<D3D12ViewCharacterMaterial>? characterMaterials = null,
        IEnumerable<D3D12ViewPluginMaterialBinding>? pluginBindings = null,
        D3D12StylePresetCatalog? stylePresets = null,
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

        bool anyMaterialToon = hasCharacterToon
            || (characterMaterials?.Any(static m => m?.ToonResponse is not null) ?? false);
        bool anyMaterialDirectPalette = hasCharacterDirectPalette
            || (characterMaterials?.Any(static m => m?.PreserveDirectLightPalette == true) ?? false);

        IReadOnlyList<D3D12ShaderStyleWarmupVariant> variants;
        if (explicitStylizedVariants is not null)
        {
            variants = explicitStylizedVariants;
        }
        else
        {
            variants = CollectStyleVariants(
                styleCatalog,
                stylePresets,
                pluginBindings,
                hasSubTexture,
                anyMaterialToon,
                anyMaterialDirectPalette);
        }

        return new D3D12ShaderWarmupSpec
        {
            Gi = giEnabled,
            GiMode = gi.Mode,
            GiComposition = gi.Composition,
            IncludeGiDenoiser = giEnabled && gi.Denoiser != RayGiDenoiser.Off,
            IncludeGiTemporal = giEnabled && gi.Denoiser == RayGiDenoiser.TemporalSpatial,
            IncludeGiHalfRes = giEnabled && gi.PreviewAcceleration,
            IncludePositionGather = giEnabled && gi.Mode == LightingMode.HardwareRayTracing && hasDynamicVertices,
            NativeLights = hasNativeLights,
            TextureMorph = hasTextureMorph,
            SubTexture = hasSubTexture,
            CharacterToon = anyMaterialToon,
            CharacterDirectPalette = anyMaterialDirectPalette,
            Outline = hasOutline,
            CharacterOutline = hasCharacterOutline,
            StylizedVariants = variants,
            SmaaQuality = quality.Antialiasing == FrameAntialiasing.Smaa1X ? (quality.SmaaQuality ?? SmaaQuality.High) : null,
            SmaaAlphaCoverage = straightAlpha,
            IncludeFxaa = quality.Antialiasing == FrameAntialiasing.Fxaa,
            IncludeFxaaAlphaCoverage = straightAlpha,
            IncludeNativeTaa = quality.Antialiasing == FrameAntialiasing.Taa,
            IncludeStandaloneRcas = quality.EffectiveSharpening.IsActive,
            IncludeScreenSpaceAo = quality.EffectiveScreenSpaceAo.IsActive,
            IncludeFsr1 = quality.EffectiveUpscaling.Mode != ViewUpscalingMode.Off,
            IncludeTemporalInputs = temporal || quality.Antialiasing == FrameAntialiasing.Taa || hasLightGizmos,
            IncludeTemporalCharacter = (temporal || quality.Antialiasing == FrameAntialiasing.Taa) && (hasCharacterOutline || hasSubTexture || hasTextureMorph || variants.Count > 0),
            IncludeTemporalReactive = temporal && quality.EffectiveTemporalUpscaling.Mode == ViewTemporalUpscalingMode.XeSs,
            IncludeTemporalEditorVisibility = hasLightGizmos,
            IncludeTemporalColorTransfer = temporal,
            IncludeDiffusion = quality.EffectiveDiffusion.Enabled && quality.EffectiveDiffusion.Strength > 0,
            IncludeDiffusionAlphaCoverage = straightAlpha,
            IncludeAlphaResolve = straightAlpha,
            IncludeSpatialAccumulation = (quality.SpatialSamples ?? 1) > 1,
            IncludeLightGizmos = hasLightGizmos,
            IncludeGpuSkinning = hasDynamicVertices,
        };
    }

    private static D3D12ShaderStyleWarmupVariant[] CollectStyleVariants(
        D3D12ViewStyleCatalog? styleCatalog,
        D3D12StylePresetCatalog? stylePresets,
        IEnumerable<D3D12ViewPluginMaterialBinding>? pluginBindings,
        bool hasSubTexture,
        bool anyMaterialToon,
        bool anyMaterialDirectPalette)
    {
        var registrations = new Dictionary<string, D3D12PluginStyleRegistration>(StringComparer.Ordinal);
        if (styleCatalog is not null)
        {
            foreach (var reg in styleCatalog.Registrations)
                registrations[reg.StyleId] = reg;
        }
        if (stylePresets is not null)
        {
            foreach (var (styleId, reg) in stylePresets.Plugins)
                registrations.TryAdd(styleId, reg);
        }

        if (registrations.Count == 0)
            return [];

        HashSet<string>? referencedStyles = null;
        if (pluginBindings is not null)
        {
            foreach (var binding in pluginBindings)
            {
                if (binding?.Binding is not null && !string.IsNullOrWhiteSpace(binding.Binding.StyleId))
                    (referencedStyles ??= new HashSet<string>(StringComparer.Ordinal)).Add(binding.Binding.StyleId);
            }
        }

        var variants = new List<D3D12ShaderStyleWarmupVariant>();
        var seen = new HashSet<(string StyleId, string ModuleId, string ContentHash, D3D12StyleShaderHookMask Hooks, D3D12StyleGeometryRequirements Geom, bool Toon, bool DirectPalette)>();

        void AddModule(string styleId, D3D12PluginShaderModule module, CharacterToonResponse? defaultToon)
        {
            var hooks = module.EffectiveHooks;
            var geom = D3D12StyleShaderAssembler.ResolveEffectiveGeometry(module, hasSubTexture);
            bool matToon = anyMaterialToon
                || defaultToon is not null
                || hooks.HasFlag(D3D12StyleShaderHookMask.AllowStylizedShadingWrapper)
                || hooks.HasFlag(D3D12StyleShaderHookMask.LightResponse);
            bool preserveDirect = anyMaterialDirectPalette
                || hooks.HasFlag(D3D12StyleShaderHookMask.PreserveDirectLightPalette);

            var dedupKey = (styleId, module.ModuleId, module.EffectiveContentHash, hooks, geom, matToon, preserveDirect);
            if (seen.Add(dedupKey))
            {
                variants.Add(new D3D12ShaderStyleWarmupVariant(
                    styleId,
                    module,
                    hooks,
                    geom,
                    matToon,
                    preserveDirect));
            }
        }

        foreach (var reg in registrations.Values)
        {
            if (string.Equals(reg.StyleId, D3D12SurfaceStyleMap.DefaultStyleId, StringComparison.Ordinal))
                continue;

            // Warm all active registered styles (and ensure any explicitly referenced active style is included).
            _ = referencedStyles;
            if (reg.ShaderModules is { Count: > 0 } modules)
            {
                foreach (var module in modules.Values)
                    AddModule(reg.StyleId, module, reg.DefaultToonResponse);

                if (!modules.ContainsKey("default"))
                    AddModule(reg.StyleId, D3D12StyleShaderAssembler.DefaultStylizedHostModule, reg.DefaultToonResponse);
            }
            else
            {
                AddModule(reg.StyleId, D3D12StyleShaderAssembler.DefaultStylizedHostModule, reg.DefaultToonResponse);
            }
        }

        return variants.ToArray();
    }
}
