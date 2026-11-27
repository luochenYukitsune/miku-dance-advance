using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

public readonly record struct D3D12ShaderStyleWarmupVariant(
    string StyleId,
    D3D12PluginShaderModule Module,
    D3D12StyleShaderHookMask? EffectiveHooksOverride = null,
    D3D12StyleGeometryRequirements? EffectiveGeometryOverride = null,
    bool CharacterToon = true,
    bool PreserveDirectLightPalette = false);

public sealed record D3D12ShaderWarmupSpec
{
    public bool Gi { get; init; }
    public LightingMode GiMode { get; init; } = LightingMode.HardwareRayTracing;
    public RayGiComposition GiComposition { get; init; } = RayGiComposition.Additive;
    public bool IncludeGiDenoiser { get; init; }
    public bool IncludeGiTemporal { get; init; }
    public bool IncludeGiHalfRes { get; init; }
    public bool IncludePositionGather { get; init; }
    public bool NativeLights { get; init; }
    public bool TextureMorph { get; init; }
    public bool SubTexture { get; init; }
    public bool CharacterToon { get; init; }
    public bool CharacterDirectPalette { get; init; }
    public bool Outline { get; init; } = true;
    public bool CharacterOutline { get; init; }
    public IReadOnlyList<D3D12ShaderStyleWarmupVariant> StylizedVariants { get; init; } = [];
    public SmaaQuality? SmaaQuality { get; init; }
    public bool SmaaAlphaCoverage { get; init; }
    public bool IncludeFxaa { get; init; }
    public bool IncludeFxaaAlphaCoverage { get; init; }
    public bool IncludeNativeTaa { get; init; }
    public bool IncludeStandaloneRcas { get; init; }
    public bool IncludeScreenSpaceAo { get; init; }
    public bool IncludeFsr1 { get; init; }
    public bool IncludeTemporalInputs { get; init; }
    public bool IncludeTemporalCharacter { get; init; }
    public bool IncludeTemporalReactive { get; init; }
    public bool IncludeTemporalEditorVisibility { get; init; }
    public bool IncludeTemporalColorTransfer { get; init; }
    public bool IncludeDiffusion { get; init; }
    public bool IncludeDiffusionAlphaCoverage { get; init; }
    public bool IncludeExposure { get; init; }
    public bool IncludeAlphaResolve { get; init; }
    public bool IncludeSpatialAccumulation { get; init; }
    public bool IncludeLightGizmos { get; init; }
    public bool IncludeGpuSkinning { get; init; }
}

/// <summary>Asynchronous bounded-concurrency shader prewarmer for FXC and DXC variants.
/// Prewarming is strictly best-effort: compile failures are never cached and surface
/// only when a shader is actually requested during rendering.</summary>
public static class D3D12ShaderPrewarmer
{
    public static int DefaultMaxParallelism => DxcShaderCompiler.DefaultPrewarmParallelism;

    public static Task PrewarmAsync(
        D3D12ShaderWarmupSpec spec,
        int maxParallelism = 0,
        CancellationToken cancellationToken = default)
        => PrewarmAsync(spec, maxParallelism, explicitDxcCompiler: null, cancellationToken);

    internal static Task PrewarmAsync(
        D3D12ShaderWarmupSpec spec,
        int maxParallelism,
        DxcShaderCompiler? explicitDxcCompiler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentOutOfRangeException.ThrowIfNegative(maxParallelism);
        if (cancellationToken.IsCancellationRequested) return Task.CompletedTask;

        var fxcRequests = new List<D3D12FxcShaderRequest>();
        var fxcActions = CollectSurfaceCompileActions(spec);

        if (spec.SmaaQuality is { } smaaQuality)
            fxcRequests.AddRange(D3D12SmaaPass.GetPrewarmRequests(smaaQuality, spec.SmaaAlphaCoverage));
        if (spec.IncludeFxaa)
            fxcRequests.AddRange(D3D12FxaaPass.GetPrewarmRequests(spec.IncludeFxaaAlphaCoverage));
        if (spec.IncludeNativeTaa)
            fxcRequests.AddRange(D3D12NativeTaaPass.GetPrewarmRequests());
        if (spec.IncludeStandaloneRcas)
            fxcRequests.AddRange(D3D12RcasPass.GetPrewarmRequests());
        if (spec.IncludeScreenSpaceAo)
            fxcRequests.AddRange(D3D12ScreenSpaceAoPass.GetPrewarmRequests());
        if (spec.IncludeFsr1)
            fxcRequests.AddRange(D3D12Fsr1Pass.GetPrewarmRequests());
        if (spec.IncludeDiffusion)
            fxcRequests.AddRange(D3D12DiffusionPass.GetPrewarmRequests(spec.IncludeDiffusionAlphaCoverage));
        if (spec.IncludeExposure)
            fxcRequests.AddRange(D3D12ExposurePass.GetPrewarmRequests());
        if (spec.IncludeAlphaResolve)
            fxcRequests.AddRange(D3D12AlphaResolvePass.GetPrewarmRequests());
        if (spec.IncludeSpatialAccumulation)
            fxcRequests.AddRange(D3D12SpatialAccumulationPass.GetPrewarmRequests());
        if (spec.IncludeTemporalInputs)
            fxcRequests.AddRange(D3D12TemporalInputPass.GetPrewarmRequests());
        if (spec.IncludeTemporalCharacter || spec.IncludeTemporalEditorVisibility)
            fxcRequests.AddRange(D3D12TemporalInputPass.GetCharacterPrewarmRequests(spec.SubTexture, spec.IncludeTemporalReactive, spec.IncludeTemporalEditorVisibility));
        if (spec.IncludeTemporalColorTransfer)
            fxcRequests.AddRange(D3D12TemporalColorTransfer.GetPrewarmRequests());
        if (spec.IncludeLightGizmos)
            fxcRequests.AddRange(D3D12LightGizmoPass.GetPrewarmRequests());

        var dxcRequests = CollectDxcRequests(spec);

        int totalItems = fxcActions.Count + fxcRequests.Count + dxcRequests.Count;
        if (totalItems == 0) return Task.CompletedTask;

        int concurrency = Math.Min(totalItems, maxParallelism == 0 ? DefaultMaxParallelism : maxParallelism);
        return Task.Run(async () =>
        {
            DxcShaderCompiler? dxcCompiler = explicitDxcCompiler;
            if (dxcCompiler is null && dxcRequests.Count > 0)
            {
                try { dxcCompiler = DxcShaderCompiler.GetOrCreateShared(); }
                catch (Exception) { dxcRequests.Clear(); }
            }

            using var semaphore = new SemaphoreSlim(concurrency, concurrency);
            var tasks = new List<Task>(totalItems);

            foreach (var req in dxcRequests)
            {
                var captured = req;
                tasks.Add(RunBounded(semaphore, () =>
                {
                    dxcCompiler?.TryPrewarmSingle(captured, cancellationToken);
                }, cancellationToken));
            }

            foreach (var action in fxcActions)
            {
                var captured = action;
                tasks.Add(RunBounded(semaphore, () =>
                {
                    try { if (!cancellationToken.IsCancellationRequested) captured(); }
                    catch (Exception) { }
                }, cancellationToken));
            }

            foreach (var req in fxcRequests)
            {
                var captured = req;
                tasks.Add(RunBounded(semaphore, () =>
                {
                    try
                    {
                        if (!cancellationToken.IsCancellationRequested)
                            D3D12FxcShaderCache.Compile(captured.Source, captured.EntryPoint, captured.SourceName, captured.Profile, captured.Flags, cancellationToken: cancellationToken);
                    }
                    catch (Exception) { }
                }, cancellationToken));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }, CancellationToken.None);
    }

    internal static D3D12ShaderWarmupSpec SpecFromScene(
        OriginalFrameScene frame,
        D3D12SurfaceStyleMap? surfaceStyles = null,
        RayGiOptions? rayGi = null,
        D3D12SpatialUpscalingOptions? spatialUpscaling = null,
        D3D12TemporalUpscalingOptions? temporalUpscaling = null,
        D3D12DiffusionOptions? diffusion = null,
        bool includeLightGizmos = false)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var o = frame.Options;
        bool gi = rayGi is { Enabled: true, Strength: > 0 };
        bool subTexture = frame.Surface.Materials.Any(binding => binding.SphereMode == OriginalSphereMode.SubTexture && binding.SphereTexture >= 0);
        bool textureMorph = subTexture || frame.Surface.Materials.Any(binding => !binding.TextureFactors.IsIdentity);
        bool nativeLights = D3D12OriginalStyleRenderer.HasNativeLights(frame);
        bool characterToon = frame.Materials.Any(flags => flags.CharacterToonResponse is not null);
        bool characterDirectPalette = nativeLights && frame.Materials.Any(flags => flags.PreserveDirectLightPalette);
        bool outline = frame.Outline is not null && frame.OutlineOptions is { Eligible: true, ModelWidth: > 0 };
        bool characterOutline = outline && frame.Materials.Any(flags => flags.CharacterOutlineStyle is not null);
        bool straightAlpha = o?.AlphaOutput == FrameAlphaOutput.Straight;
        bool temporal = temporalUpscaling is { Mode: not D3D12TemporalUpscalingMode.Off };

        var variants = new List<D3D12ShaderStyleWarmupVariant>();
        if (surfaceStyles is not null)
        {
            var seen = new HashSet<(string StyleId, string ModuleId, string Hash, D3D12StyleShaderHookMask Hooks, D3D12StyleGeometryRequirements Geom, bool Toon, bool DirectPalette)>();
            int count = Math.Min(frame.Surface.Snapshot.Materials.Count, surfaceStyles.Count);
            for (int i = 0; i < count; i++)
            {
                if (!frame.Materials[i].SurfaceEnabled || frame.Surface.Snapshot.Materials[i].IndexCount == 0) continue;
                if (surfaceStyles.IsDefaultStyle(i)) continue;
                var module = surfaceStyles.ResolveShaderModule(i);
                if (module is null) continue;
                var hooks = surfaceStyles.EffectiveHookMask(i);
                var geom = D3D12StyleShaderAssembler.ResolveEffectiveGeometry(module, subTexture);
                var pluginReg = surfaceStyles.ResolvePlugin(i);
                bool matToon = (frame.Materials[i].CharacterToonResponse
                    ?? surfaceStyles.CharacterMaterials[i].ToonResponse
                    ?? pluginReg?.DefaultToonResponse) is not null
                    || hooks.HasFlag(D3D12StyleShaderHookMask.AllowStylizedShadingWrapper)
                    || hooks.HasFlag(D3D12StyleShaderHookMask.LightResponse);
                bool preserveDirect = surfaceStyles.EffectivePreserveDirectLightPalette(i, frame.Materials[i].PreserveDirectLightPalette);
                var dedupKey = (surfaceStyles.StyleIds[i], module.ModuleId, module.EffectiveContentHash, hooks, geom, matToon, preserveDirect);
                if (seen.Add(dedupKey))
                {
                    variants.Add(new D3D12ShaderStyleWarmupVariant(
                        surfaceStyles.StyleIds[i],
                        module,
                        hooks,
                        geom,
                        matToon,
                        preserveDirect));
                }
            }
        }

        return new D3D12ShaderWarmupSpec
        {
            Gi = gi,
            GiMode = rayGi?.Mode ?? LightingMode.HardwareRayTracing,
            GiComposition = rayGi?.Composition ?? RayGiComposition.Additive,
            IncludeGiDenoiser = gi && D3D12RayGiDenoiser.IsEnabled(rayGi!),
            IncludeGiTemporal = gi && D3D12RayGiRenderer.IsTemporalEnabled(rayGi),
            IncludeGiHalfRes = gi && rayGi!.PreviewAcceleration,
            IncludePositionGather = gi && (rayGi?.Mode ?? LightingMode.HardwareRayTracing) == LightingMode.HardwareRayTracing && frame.Surface.Snapshot.HasDynamicVertices,
            NativeLights = nativeLights,
            TextureMorph = textureMorph,
            SubTexture = subTexture,
            CharacterToon = characterToon,
            CharacterDirectPalette = characterDirectPalette,
            Outline = outline,
            CharacterOutline = characterOutline,
            StylizedVariants = variants,
            SmaaQuality = o?.Antialiasing == FrameAntialiasing.Smaa1X ? o.SmaaQuality : null,
            SmaaAlphaCoverage = straightAlpha,
            IncludeFxaa = o?.Antialiasing == FrameAntialiasing.Fxaa,
            IncludeFxaaAlphaCoverage = straightAlpha,
            IncludeNativeTaa = o?.Antialiasing == FrameAntialiasing.Taa,
            IncludeScreenSpaceAo = o?.ScreenSpaceAo is { IsActive: true },
            IncludeFsr1 = spatialUpscaling is { Mode: not D3D12SpatialUpscalingMode.Off },
            IncludeTemporalInputs = temporal || o?.Antialiasing == FrameAntialiasing.Taa || (includeLightGizmos || (frame.EditorOverlay?.Lines.Any(line => line.DeviceDepth is not null) ?? false)),
            IncludeTemporalCharacter = ((temporal || o?.Antialiasing == FrameAntialiasing.Taa) && (characterOutline || subTexture || textureMorph || surfaceStyles is not null)),
            IncludeTemporalReactive = temporal && temporalUpscaling?.Mode == D3D12TemporalUpscalingMode.XeSs,
            IncludeTemporalEditorVisibility = includeLightGizmos || (frame.EditorOverlay?.Lines.Any(line => line.DeviceDepth is not null) ?? false),
            IncludeTemporalColorTransfer = temporal,
            IncludeDiffusion = diffusion is { IsActive: true },
            IncludeDiffusionAlphaCoverage = straightAlpha,
            IncludeExposure = (frame.Lighting?.Settings.ExposureEv ?? 0f) != 0f,
            IncludeAlphaResolve = straightAlpha,
            IncludeSpatialAccumulation = (o?.SpatialSamples ?? 1) > 1,
            IncludeLightGizmos = includeLightGizmos || (frame.EditorOverlay?.Lines.Count ?? 0) > 0,
            IncludeGpuSkinning = frame.Surface.Snapshot.HasDeferredDeformation,
        };
    }

    private static Task RunBounded(SemaphoreSlim semaphore, Action work, CancellationToken cancellationToken)
        => Task.Run(async () =>
        {
            try { await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try { work(); }
            finally { semaphore.Release(); }
        }, CancellationToken.None);

    private static List<Action> CollectSurfaceCompileActions(D3D12ShaderWarmupSpec spec)
    {
        var actions = new List<Action>();
        var keys = new HashSet<string>(StringComparer.Ordinal);

        void Add(string key, Action compile)
        {
            if (keys.Add(key)) actions.Add(compile);
        }

        string surfacePixel = spec.NativeLights
            ? (spec.Gi ? "PSSurfaceGiNative" : "PSSurfaceNative")
            : (spec.Gi ? "PSSurfaceGi" : "PSSurface");

        if (spec.SubTexture)
        {
            bool n = spec.NativeLights, g = spec.Gi;
            Add($"sub:VS:{n}:{g}", () => OriginalFrameShaders.CompileSubTexture("VSSurface", true, n, g));
            if (n && g)
                Add("sub:VS:false:true", () => OriginalFrameShaders.CompileSubTexture("VSSurface", true, false, true));
            if (!spec.CharacterDirectPalette && !spec.CharacterToon)
                Add("sub:PS:" + surfacePixel + $":{n}:{g}", () => OriginalFrameShaders.CompileSubTexture(surfacePixel, false, n, g));
        }
        else
        {
            Add("base:VSSurface", () => OriginalFrameShaders.Compile("VSSurface", true));
        }

        if (spec.CharacterDirectPalette)
        {
            bool g = spec.Gi, tm = spec.TextureMorph, st = spec.SubTexture, ct = spec.CharacterToon;
            Add($"cdp:{surfacePixel}:{g}:{tm}:{st}:{ct}", () => OriginalFrameShaders.CompileCharacterDirectPalette(surfacePixel, g, tm, st, ct));
        }
        else if (spec.CharacterToon)
        {
            bool n = spec.NativeLights, g = spec.Gi, tm = spec.TextureMorph, st = spec.SubTexture;
            Add($"ct:{surfacePixel}:{n}:{g}:{tm}:{st}", () => OriginalFrameShaders.CompileCharacterToon(surfacePixel, n, g, tm, st));
        }
        else if (!spec.SubTexture)
        {
            if (spec.TextureMorph)
            {
                bool n = spec.NativeLights, g = spec.Gi;
                Add($"tm:{surfacePixel}:{n}:{g}", () => OriginalFrameShaders.CompileTextureMorph(surfacePixel, n, g));
            }
            else if (spec.NativeLights)
            {
                Add($"nat:{surfacePixel}", () => OriginalFrameShaders.CompileNative(surfacePixel, false));
            }
            else if (spec.Gi && surfacePixel == "PSSurfaceGi")
            {
                Add("gi:PSSurfaceGi", () => OriginalFrameShaders.CompileGi("PSSurfaceGi", false));
            }
            else
            {
                Add("base:" + surfacePixel, () => OriginalFrameShaders.Compile(surfacePixel, false));
            }
        }

        Add("base:VSDepth", () => OriginalFrameShaders.Compile("VSDepth", true));
        Add("base:PSDepth", () => OriginalFrameShaders.Compile("PSDepth", false));
        Add("base:VSGround", () => OriginalFrameShaders.Compile("VSGround", true));
        Add("base:PSColor", () => OriginalFrameShaders.Compile("PSColor", false));

        if (spec.NativeLights)
        {
            if (spec.SubTexture)
            {
                bool g = spec.Gi;
                Add($"sub:VSNativeDepth:true:{g}", () => OriginalFrameShaders.CompileSubTexture("VSNativeDepth", true, true, g));
                Add($"sub:PSNativeDepth:true:{g}", () => OriginalFrameShaders.CompileSubTexture("PSNativeDepth", false, true, g));
            }
            else
            {
                Add("nat:VSNativeDepth", () => OriginalFrameShaders.CompileNative("VSNativeDepth", true));
                Add("nat:PSNativeDepth", () => OriginalFrameShaders.CompileNative("PSNativeDepth", false));
            }
        }

        if (spec.Gi)
        {
            if (spec.SubTexture)
                Add("sub:PSGBuffer:gi", () => OriginalFrameShaders.CompileSubTexture("PSGBuffer", false, gi: true));
            else
                Add("gi:PSGBuffer", () => OriginalFrameShaders.CompileGi("PSGBuffer", false));
        }

        if (spec.Outline)
            Add("outline:base", () => OriginalOutlineShaders.Compile());
        if (spec.CharacterOutline)
        {
            bool st = spec.SubTexture;
            Add($"charOutline:{st}", () => OriginalFrameShaders.CompileCharacterOutline(st));
        }

        foreach (var v in spec.StylizedVariants)
        {
            if (v.Module is null || string.IsNullOrWhiteSpace(v.StyleId)
                || string.Equals(v.StyleId, D3D12SurfaceStyleMap.DefaultStyleId, StringComparison.Ordinal))
                continue;

            bool n = spec.NativeLights, g = spec.Gi, tm = spec.TextureMorph, st = spec.SubTexture;
            var module = v.Module;
            var hooks = v.EffectiveHooksOverride ?? module.EffectiveHooks;
            var geom = v.EffectiveGeometryOverride ?? D3D12StyleShaderAssembler.ResolveEffectiveGeometry(module, st);
            var maskedVsGeom = geom & (D3D12StyleGeometryRequirements.SubTextureUv
                | D3D12StyleGeometryRequirements.RawRadialSeeds
                | D3D12StyleGeometryRequirements.ModelSpacePosition
                | D3D12StyleGeometryRequirements.ModelSpaceNormal);

            string vsKey = $"styVs:{maskedVsGeom}:{n}:{g}:{st}";
            Add(vsKey, () => D3D12StyleShaderAssembler.CompileVertex(module, geom, n, g, st));

            var captured = v;
            string styleKey = $"styPs:{captured.StyleId}:{module.ModuleId}:{module.EffectiveContentHash}:{hooks}:{geom}:{surfacePixel}:{n}:{g}:{tm}:{st}:{captured.CharacterToon}:{captured.PreserveDirectLightPalette}";
            Add(styleKey, () => D3D12StyleShaderAssembler.CompilePixel(
                captured.StyleId, surfacePixel, module, hooks, geom,
                n, g, tm, st, captured.CharacterToon, captured.PreserveDirectLightPalette));
        }

        return actions;
    }

    private static List<DxcShaderWarmupRequest> CollectDxcRequests(D3D12ShaderWarmupSpec spec)
    {
        var requests = new List<DxcShaderWarmupRequest>();
        if (spec.IncludeGpuSkinning)
        {
            bool hwGi = spec.Gi && spec.GiMode == LightingMode.HardwareRayTracing;
            bool temporal = spec.IncludeTemporalInputs || spec.IncludeTemporalCharacter || spec.IncludeGiTemporal;
            requests.AddRange(D3D12GpuSkinning.GetPrewarmRequests(spec.Outline, temporal, hwGi));
        }
        if (!spec.Gi) return requests;
        bool hardware = spec.GiMode == LightingMode.HardwareRayTracing;
        requests.Add(D3D12RayGiShaders.GetPrewarmRequest(hardware, spec.GiComposition, spec.TextureMorph, spec.NativeLights, spec.SubTexture));
        if (spec.IncludeGiTemporal)
            requests.Add(D3D12RayGiTemporalPass.GetPrewarmRequest());
        if (spec.IncludeGiDenoiser)
            requests.Add(D3D12RayGiDenoiser.GetPrewarmRequest());
        if (spec.IncludeGiHalfRes)
            requests.AddRange(D3D12RayGiHalfResPass.GetPrewarmRequests());
        if (spec.IncludePositionGather)
            requests.Add(D3D12HardwareRayGi.GetPositionGatherPrewarmRequest());
        return requests;
    }
}
