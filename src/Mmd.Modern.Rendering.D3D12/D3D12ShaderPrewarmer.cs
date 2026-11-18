using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

public readonly record struct D3D12ShaderStyleWarmupVariant(
    D3D12SurfaceStyle Family,
    bool FaceSdf = false,
    bool GenshinHair = false,
    bool StarMcHair = false,
    bool EndfieldHair = false,
    bool EndfieldRim = false,
    bool GenshinAtlas = false,
    bool EndfieldMatcap = false,
    bool GenshinMetallic = false,
    bool GenshinMetallicSpecular = false,
    bool EndfieldPalette = false,
    bool EndfieldBaseImage = false,
    bool StarMcBody = false);

public sealed record D3D12ShaderWarmupSpec
{
    public bool Gi { get; init; }
    public LightingMode GiMode { get; init; } = LightingMode.HardwareRayTracing;
    public RayGiComposition GiComposition { get; init; } = RayGiComposition.Additive;
    public bool IncludeGiDenoiser { get; init; }
    public bool IncludeGiTemporal { get; init; }
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
            var seen = new HashSet<D3D12ShaderStyleWarmupVariant>();
            int count = Math.Min(frame.Surface.Snapshot.Materials.Count, surfaceStyles.Count);
            for (int i = 0; i < count; i++)
            {
                if (!frame.Materials[i].SurfaceEnabled || frame.Surface.Snapshot.Materials[i].IndexCount == 0) continue;
                var family = surfaceStyles.Materials[i];
                if (family is not (D3D12SurfaceStyle.GenshinPmx or D3D12SurfaceStyle.StarRailPmx
                    or D3D12SurfaceStyle.WutheringPmx or D3D12SurfaceStyle.EndfieldPmx))
                    continue;
                var metallic = surfaceStyles.EffectiveGenshinMetallic(i);
                var matcap = surfaceStyles.EffectiveEndfieldMatcap(i);
                var variant = new D3D12ShaderStyleWarmupVariant(
                    Family: family,
                    FaceSdf: surfaceStyles.EffectiveFaceSdf(i) is not null,
                    GenshinHair: surfaceStyles.EffectiveGenshinNonMetallicSpecular(i) is not null,
                    StarMcHair: surfaceStyles.EffectiveStarMcHair(i) is not null,
                    EndfieldHair: surfaceStyles.EffectiveEndfieldHair(i) is not null,
                    EndfieldRim: surfaceStyles.EffectiveEndfieldRim(i) is not null,
                    GenshinAtlas: surfaceStyles.EffectiveGenshinHairShadowAtlas(i) is not null,
                    EndfieldMatcap: matcap is not null,
                    GenshinMetallic: metallic is not null,
                    GenshinMetallicSpecular: metallic?.Specular is not null,
                    EndfieldPalette: matcap?.Palette is not null,
                    EndfieldBaseImage: matcap?.Palette?.BaseImage is not null,
                    StarMcBody: surfaceStyles.EffectiveStarMcBody(i) is not null);
                if (seen.Add(variant)) variants.Add(variant);
            }
        }

        return new D3D12ShaderWarmupSpec
        {
            Gi = gi,
            GiMode = rayGi?.Mode ?? LightingMode.HardwareRayTracing,
            GiComposition = rayGi?.Composition ?? RayGiComposition.Additive,
            IncludeGiDenoiser = gi && D3D12RayGiDenoiser.IsEnabled(rayGi!),
            IncludeGiTemporal = gi && D3D12RayGiRenderer.IsTemporalEnabled(rayGi),
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
            IncludeFsr1 = spatialUpscaling is { Mode: not D3D12SpatialUpscalingMode.Off },
            IncludeTemporalInputs = temporal || (includeLightGizmos || (frame.EditorOverlay?.Lines.Any(line => line.DeviceDepth is not null) ?? false)),
            IncludeTemporalCharacter = (temporal && (characterOutline || subTexture || textureMorph || surfaceStyles is not null)),
            IncludeTemporalReactive = temporal && temporalUpscaling?.Mode == D3D12TemporalUpscalingMode.XeSs,
            IncludeTemporalEditorVisibility = includeLightGizmos || (frame.EditorOverlay?.Lines.Any(line => line.DeviceDepth is not null) ?? false),
            IncludeTemporalColorTransfer = temporal,
            IncludeDiffusion = diffusion is { IsActive: true },
            IncludeDiffusionAlphaCoverage = straightAlpha,
            IncludeExposure = (frame.Lighting?.Settings.ExposureEv ?? 0f) != 0f,
            IncludeAlphaResolve = straightAlpha,
            IncludeSpatialAccumulation = (o?.SpatialSamples ?? 1) > 1,
            IncludeLightGizmos = includeLightGizmos || (frame.EditorOverlay?.Lines.Count ?? 0) > 0,
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
            if (v.Family is not (D3D12SurfaceStyle.GenshinPmx or D3D12SurfaceStyle.StarRailPmx
                or D3D12SurfaceStyle.WutheringPmx or D3D12SurfaceStyle.EndfieldPmx))
                continue;

            bool n = spec.NativeLights, g = spec.Gi, tm = spec.TextureMorph, st = spec.SubTexture;
            if (v.EndfieldHair)
                Add($"sty:efHairVs:{n}:{g}:{tm}:{st}", () => OriginalFrameShaders.CompileEndfieldHairSurface("VSSurface", true, n, g, tm, st));

            var captured = v;
            string styleKey = $"sty:{captured.Family}:{surfacePixel}:{n}:{g}:{tm}:{st}:"
                + $"{captured.FaceSdf}:{captured.GenshinHair}:{captured.StarMcHair}:{captured.EndfieldHair}:{captured.EndfieldRim}:"
                + $"{captured.GenshinAtlas}:{captured.EndfieldMatcap}:{captured.GenshinMetallic}:{captured.GenshinMetallicSpecular}:"
                + $"{captured.EndfieldPalette}:{captured.EndfieldBaseImage}:{captured.StarMcBody}";
            Add(styleKey, () =>
            {
                _ = captured.StarMcBody ? OriginalFrameShaders.CompileStarMcBodySurface(captured.Family, surfacePixel, n, g, tm, st)
                    : captured.EndfieldBaseImage ? OriginalFrameShaders.CompileEndfieldBaseImageSurface(surfacePixel, n, g, tm, st, captured.EndfieldHair, captured.EndfieldRim)
                    : captured.EndfieldPalette ? OriginalFrameShaders.CompileEndfieldPaletteSurface(surfacePixel, n, g, tm, st, captured.EndfieldHair, captured.EndfieldRim)
                    : captured.GenshinMetallicSpecular ? OriginalFrameShaders.CompileGenshinMetallicSpecularSurface(surfacePixel, n, g, tm, st, captured.GenshinHair, captured.GenshinAtlas)
                    : captured.GenshinMetallic ? OriginalFrameShaders.CompileGenshinMetallicSurface(surfacePixel, n, g, tm, st, captured.GenshinHair, captured.GenshinAtlas)
                    : captured.EndfieldMatcap ? OriginalFrameShaders.CompileEndfieldMatcapSurface(surfacePixel, n, g, tm, st, captured.EndfieldHair, captured.EndfieldRim)
                    : captured.GenshinAtlas ? OriginalFrameShaders.CompileGenshinHairAtlasSurface(surfacePixel, n, g, tm, st, captured.GenshinHair)
                    : captured.EndfieldRim ? OriginalFrameShaders.CompileEndfieldRimSurface(surfacePixel, n, g, tm, st, captured.EndfieldHair)
                    : captured.EndfieldHair ? OriginalFrameShaders.CompileEndfieldHairSurface(surfacePixel, false, n, g, tm, st)
                    : captured.StarMcHair ? OriginalFrameShaders.CompileStarMcHairSurface(captured.Family, surfacePixel, n, g, tm, st)
                    : captured.GenshinHair ? OriginalFrameShaders.CompileGenshinHairSurface(surfacePixel, n, g, tm, st)
                    : captured.FaceSdf ? OriginalFrameShaders.CompileFaceSdfSurface(captured.Family, surfacePixel, n, g, tm, st)
                    : OriginalFrameShaders.CompileStylizedFamilySurface(captured.Family, surfacePixel, n, g, tm, st);
            });
        }

        return actions;
    }

    private static List<DxcShaderWarmupRequest> CollectDxcRequests(D3D12ShaderWarmupSpec spec)
    {
        var requests = new List<DxcShaderWarmupRequest>();
        if (!spec.Gi) return requests;
        bool hardware = spec.GiMode == LightingMode.HardwareRayTracing;
        requests.Add(D3D12RayGiShaders.GetPrewarmRequest(hardware, spec.GiComposition, spec.TextureMorph, spec.NativeLights, spec.SubTexture));
        if (spec.IncludeGiTemporal)
            requests.Add(D3D12RayGiTemporalPass.GetPrewarmRequest());
        if (spec.IncludeGiDenoiser)
            requests.Add(D3D12RayGiDenoiser.GetPrewarmRequest());
        if (spec.IncludePositionGather)
            requests.Add(D3D12HardwareRayGi.GetPositionGatherPrewarmRequest());
        return requests;
    }
}
