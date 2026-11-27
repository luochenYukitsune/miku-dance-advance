using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Vortice.DXGI;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>A snapshot from the active viewport owner, never from a short-lived capability probe.</summary>
public sealed record EditorPreviewRuntimeInfo(string? AdapterName, bool IsWarp, int Width, int Height,
    EditorViewportPresentationStatistics? Statistics = null, ulong? LocalVideoMemoryBudgetBytes = null)
{
    public string DeviceSummary => string.IsNullOrWhiteSpace(AdapterName)
        ? Text("视口尚未启动，设备信息将在首次呈现后显示。")
        : Format("{0} · {1}", AdapterName, IsWarp ? Text("D3D12 软件渲染（WARP）") : Text("D3D12 硬件渲染"));
    public string ResolutionSummary
    {
        get
        {
            int width = Statistics?.Width ?? Width, height = Statistics?.Height ?? Height;
            if (Statistics?.Frame?.TemporalUpscaling is { } temporal)
                return Format("显示 {0} × {1} · 内部渲染 {2} × {3}", width, height, temporal.RenderWidth, temporal.RenderHeight);
            if (Statistics?.Frame is { UpscalingInputWidth: > 0, UpscalingInputHeight: > 0 } frame)
                return Format("显示 {0} × {1} · 内部渲染 {2} × {3}", width, height, frame.UpscalingInputWidth, frame.UpscalingInputHeight);
            return Format("视口 {0} × {1} 像素", width, height);
        }
    }
    public string PerformanceSummary => Statistics?.Timing is { } timing
        ? Format("最近呈现：CPU 提交 {0:0.0} ms · 呈现等待 {1:0.0} ms · 合计 {2:0.0} ms",
            timing.RecordAndSubmitMilliseconds, timing.PresentWaitMilliseconds, timing.TotalMilliseconds)
        : Text("尚无已呈现帧的耗时记录。");
    public string PerformanceHint => Text("这里记录 CPU 提交与等待时间，不是 GPU 耗时或显卡占用率。静止画面按需刷新。");
    public string SamplingSummary => Statistics is { } statistics
        ? EditorPreviewFeedback.SamplingSummary(statistics) : "";
}

/// <summary>Observed SDK/device results supplied by the renderer owner, never inferred from an adapter name.</summary>
public sealed record EditorPreviewCapabilities(
    D3D12RayGiCapabilities? Gi = null,
    bool SpatialUpscalingAvailable = false,
    D3D12FsrRuntimeProbeResult? Fsr = null,
    IReadOnlyList<D3D12XeSsDeviceQuery>? XeSsQueries = null,
    IReadOnlyList<D3D12DlssDeviceQuery>? DlssQueries = null)
{
    public bool FsrAvailable => Fsr?.Status is D3D12FsrRuntimeStatus.ContextCreated or D3D12FsrRuntimeStatus.Executed;
    public bool XeSsAvailable => XeSsQueries?.Any(query => query.Initialized && query.Failure is null) == true;
    public bool DlssAvailable => DlssQueries?.Any(query => query.Available && query.Initialized && query.Failure is null) == true;
    public string? AdapterName { get; init; }
    public ulong? LocalVideoMemoryBudgetBytes { get; init; }
    public int QueryWidth { get; init; }
    public int QueryHeight { get; init; }
    public string? DeviceUnavailableReason { get; init; }
    public string? GiUnavailableReason { get; init; }
    public string? FsrUnavailableReason { get; init; }
    public string? XeSsUnavailableReason { get; init; }
    public string? DlssUnavailableReason { get; init; }
    /// <summary>Source templates, translated by the UI when read; native diagnostics remain in their query records.</summary>
    public IReadOnlyList<string> Reasons => new[] { DeviceUnavailableReason, GiUnavailableReason, FsrUnavailableReason, XeSsUnavailableReason, DlssUnavailableReason }
        .Where(reason => !string.IsNullOrWhiteSpace(reason)).Select(reason => reason!).ToArray();
    public string UnavailableReason => string.Join(Environment.NewLine, Reasons);

    private readonly record struct CapabilityCacheKey(
        string FsrPath,
        string XessPath,
        string DlssBridge,
        string DlssSdkDirectory,
        string AdapterName);

    private static readonly ConcurrentDictionary<CapabilityCacheKey, EditorPreviewCapabilities> CapabilityCache = new();

    private static string NormalizeKeyPart(string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToUpperInvariant();

    private static CapabilityCacheKey MakeCacheKey(EditorOptionalRuntimePaths paths, string? adapterName) => new(
        NormalizeKeyPart(paths.FsrLoaderPath),
        NormalizeKeyPart(paths.XessLibraryPath),
        NormalizeKeyPart(paths.DlssBridgePath),
        NormalizeKeyPart(paths.DlssSdkDirectory),
        NormalizeKeyPart(adapterName));

    /// <summary>Returns cached hardware/component capability probe results for the current runtime paths and adapter.</summary>
    public static bool TryGetCached(EditorOptionalRuntimePaths paths, string? adapterName, out EditorPreviewCapabilities? cached)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return CapabilityCache.TryGetValue(MakeCacheKey(paths, adapterName), out cached);
    }

    internal static void ClearCapabilityCache() => CapabilityCache.Clear();

    /// <summary>Reuses session-cached capabilities unless <paramref name="forceRefresh"/> is requested.</summary>
    public static EditorPreviewCapabilities QueryInstalledCached(int displayWidth, int displayHeight, string? adapterName,
        bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var paths = EditorOptionalRuntimePaths.Resolve();
        if (!forceRefresh && TryGetCached(paths, adapterName, out var cached) && cached is not null)
            return cached;

        var result = QueryInstalled(displayWidth, displayHeight, paths, cancellationToken);
        if (result.DeviceUnavailableReason is null)
        {
            if (!string.IsNullOrWhiteSpace(adapterName) || string.IsNullOrWhiteSpace(result.AdapterName))
                CapabilityCache[MakeCacheKey(paths, adapterName)] = result;
            else
                CapabilityCache[MakeCacheKey(paths, "")] = result;

            if (!string.IsNullOrWhiteSpace(result.AdapterName))
                CapabilityCache[MakeCacheKey(paths, result.AdapterName)] = result;
        }
        return result;
    }

    /// <summary>Blocking native probes; run on a worker. No application frame or project is created.</summary>
    public static EditorPreviewCapabilities QueryInstalled(CancellationToken cancellationToken = default)
        => QueryInstalled(1280, 720, cancellationToken);

    /// <summary>SDK quality initialization uses the requested display size, matching its actual device API.</summary>
    public static EditorPreviewCapabilities QueryInstalled(int displayWidth, int displayHeight, CancellationToken cancellationToken = default)
        => QueryInstalled(displayWidth, displayHeight, EditorOptionalRuntimePaths.Resolve(), cancellationToken);

    /// <summary>Uses the same installed component snapshot that the owner supplies to preview and export.</summary>
    public static EditorPreviewCapabilities QueryInstalled(int displayWidth, int displayHeight, EditorOptionalRuntimePaths paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (displayWidth is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(displayWidth));
        if (displayHeight is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(displayHeight));
        cancellationToken.ThrowIfCancellationRequested();
        var result = new EditorPreviewCapabilities() { QueryWidth = displayWidth, QueryHeight = displayHeight };
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            return result with { DeviceUnavailableReason = "设备检查需要 Windows 64 位 D3D12 运行环境。" };

        string? fsrPath = paths.FsrLoaderPath;
        string? xessPath = paths.XessLibraryPath;
        string? dlssBridge = paths.DlssBridgePath;
        string? dlssDirectory = paths.DlssSdkDirectory;
        bool dlssComponentsPresent = dlssBridge is not null && dlssDirectory is not null;
        result = result with
        {
            FsrUnavailableReason = fsrPath is null ? "FSR：未配置完整的可选运行时组件。" : null,
            XeSsUnavailableReason = xessPath is null ? "XeSS：未配置可选运行时组件。" : null,
            DlssUnavailableReason = dlssComponentsPresent ? null : "DLSS：未配置完整的可选桥接与运行时组件。"
        };

        try
        {
            using var renderer = new D3D12FrameRenderer();
            result = result with
            {
                AdapterName = renderer.AdapterName,
                SpatialUpscalingAvailable = true,
                LocalVideoMemoryBudgetBytes = QueryLocalVideoMemoryBudget(renderer.AdapterLuid, renderer.IsWarp)
            };
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var gi = renderer.QueryRayGiCapabilities();
                result = result with { Gi = gi, GiUnavailableReason = !gi.CompilerAvailable
                    ? "GI：未找到所需的 DXC 着色器组件。"
                    : !gi.SoftwareAvailable ? "GI：当前设备未通过软件光照检查。"
                    : !gi.HardwareAvailable ? "硬件 GI：当前设备未通过 DXR 检查，可使用软件 GI。" : null };
            }
            catch (Exception error) when (RecoverableProbeError(error))
            { result = result with { GiUnavailableReason = "GI：设备查询未完成，当前未开放。" }; }
            cancellationToken.ThrowIfCancellationRequested();

            if (xessPath is not null)
            {
                var queries = new List<D3D12XeSsDeviceQuery>();
                foreach (var quality in Enum.GetValues<D3D12XeSsQuality>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var query = renderer.QueryXeSsCapabilities(displayWidth, displayHeight, new()
                        {
                            Mode = D3D12TemporalUpscalingMode.XeSs, LoaderPath = xessPath, XeSsQuality = quality
                        });
                        queries.Add(query);
                        if (query.CreateCode is { } createCode && createCode != 0)
                            break;
                    }
                    catch (Exception error) when (RecoverableProbeError(error))
                    { queries.Add(new(renderer.AdapterName, quality) { Failure = error.Message }); }
                }
                result = result with { XeSsQueries = queries.AsReadOnly(), XeSsUnavailableReason =
                    queries.Any(query => query.Initialized && query.Failure is null) ? null
                    : "XeSS：安装的组件未通过当前设备与画质检查。" };
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (dlssComponentsPresent)
            {
                var queries = new List<D3D12DlssDeviceQuery>();
                foreach (var quality in Enum.GetValues<D3D12DlssQuality>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var query = renderer.QueryDlssCapabilities(displayWidth, displayHeight, new()
                        {
                            Mode = D3D12TemporalUpscalingMode.Dlss, LoaderPath = dlssBridge,
                            DlssSdkDirectory = dlssDirectory, DlssQuality = quality
                        });
                        queries.Add(query);
                        if (!query.Available || query.NeedsUpdatedDriver)
                            break;
                    }
                    catch (Exception error) when (RecoverableProbeError(error))
                    { queries.Add(new(renderer.AdapterName, quality) { Failure = error.Message }); }
                }
                result = result with { DlssQueries = queries.AsReadOnly(), DlssUnavailableReason =
                    queries.Any(query => query.Available && query.Initialized && query.Failure is null) ? null
                    : queries.Any(query => query.NeedsUpdatedDriver) ? "DLSS：当前驱动需要更新。"
                    : "DLSS：安装的组件未通过当前设备与画质检查。" };
            }
        }
        catch (Exception error) when (RecoverableProbeError(error))
        { result = result with { DeviceUnavailableReason = "D3D12 设备检查未完成，可重试或检查显卡驱动。" }; }
        cancellationToken.ThrowIfCancellationRequested();

        if (fsrPath is not null)
        {
            try
            {
                var fsr = D3D12FsrRuntimeProbe.ProbeCurrentDevice(fsrPath, createContexts: true);
                result = result with { Fsr = fsr, FsrUnavailableReason = fsr.Status switch
                {
                    D3D12FsrRuntimeStatus.ContextCreated or D3D12FsrRuntimeStatus.Executed => null,
                    D3D12FsrRuntimeStatus.NotInstalled => "FSR：可选运行时组件不完整。",
                    D3D12FsrRuntimeStatus.NotSupported => "FSR：安装的组件未通过当前设备检查。",
                    D3D12FsrRuntimeStatus.LoadFailed => "FSR：可选运行时组件加载失败。",
                    _ => "FSR：运行时查询或上下文初始化未通过。"
                } };
            }
            catch (Exception error) when (RecoverableProbeError(error))
            { result = result with { FsrUnavailableReason = "FSR：设备查询未完成，当前未开放。" }; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static ulong? QueryLocalVideoMemoryBudget(long adapterLuid, bool isWarp)
    {
        if (!OperatingSystem.IsWindows() || isWarp) return null;
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
            using var adapter = factory.EnumAdapterByLuid<IDXGIAdapter3>(Vortice.Luid.FromInt64(adapterLuid));
            var memory = adapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
            return memory.Budget > 0 ? memory.Budget : null;
        }
        catch (Exception error) when (RecoverableProbeError(error))
        {
            return null;
        }
    }

    private static bool RecoverableProbeError(Exception error) => error is not (OperationCanceledException or OutOfMemoryException);
}

/// <summary>A detached per-view edit. The caller persists the accepted options locally, outside the project.</summary>
public sealed class EditorPreviewSettingsViewModel : INotifyPropertyChanged
{
    private static readonly int[] SampleValues = [1, 2, 4, 8, 16, 32];
    private static readonly int[] ShadowResolutions = [0, 512, 1024, 2048, 4096];
    private static readonly ShadowFilterMode[] ShadowFilterValues = [ShadowFilterMode.Linear, ShadowFilterMode.Point, ShadowFilterMode.PcfSoft];
    private static readonly int[] AnisotropicValues = [1, 2, 4, 8, 16];
    private static readonly int[] SsaoSampleCounts = [4, 8, 16];
    private static readonly FrameAntialiasing?[] AntialiasingValues =
    [
        null,
        FrameAntialiasing.Off,
        FrameAntialiasing.Fxaa,
        FrameAntialiasing.Smaa1X,
        FrameAntialiasing.Taa,
        FrameAntialiasing.Msaa2X,
        FrameAntialiasing.Msaa4X,
        FrameAntialiasing.Msaa8X
    ];
    private static readonly RenderPipelineMode[] PipelineModeValues =
    [
        RenderPipelineMode.Forward,
        RenderPipelineMode.ClusteredForward,
        RenderPipelineMode.PathTracing
    ];
    private static readonly PathTracingShadingMode[] PtShadingModeValues =
    [
        PathTracingShadingMode.ToonHybrid,
        PathTracingShadingMode.Physical
    ];

    private readonly ViewQualityOptions appliedOptions;
    private ViewQualityOptions baseline;
    private readonly EditorPreviewRuntimeInfo? runtime;
    private readonly string? customPresetsPath;
    private readonly bool persistCustomPresets;
    private readonly Func<ViewQualityOptions?>? getExportQuality;
    private readonly Action<ViewQualityOptions>? copyToExportQuality;
    private readonly ObservableCollection<EditorPreviewCustomPreset> customPresets = [];
    private EditorPreviewCapabilities capabilities;
    private LightingMode[] giValues;
    private ViewTemporalUpscalingMode[] temporalValues;
    private int selectedCategoryIndex;
    private int customPresetIndex = -1;
    private string customPresetName = "";
    private int giIndex, antialiasingIndex, smaaIndex, spatialIndex, upscalingIndex, fsr1QualityIndex, surfaceOrderIndex;
    private int temporalIndex, temporalQualityIndex, giCompositionIndex, giDenoiserIndex, giToonResponseIndex;
    private int shadowResolutionIndex, shadowFilterIndex, anisotropicFilteringIndex, ssaoSampleCountIndex;
    private int pipelineModeIndex, ptShadingModeIndex, ptDenoiserIndex;
    private decimal? giStrength, giSamples, giOcclusionStrength, giOcclusionRadius, giMaxRayDistance, giDirectScale, giRayEpsilon, giSeed;
    private decimal? diffusionStrength, diffusionExtent, rcasSharpness, standaloneRcasSharpness, renderScale, previewOutlineWidthScale;
    private decimal? shadowSoftness, ssaoStrength, ssaoRadius;
    private decimal? ptSamplesPerFrame, ptMaxBounces, ptMaxAccumulatedFrames, ptInteractiveSamplesPerFrame, ptFireflyClamp;
    private bool previewGiAcceleration, diffusionEnabled, rcasEnabled, standaloneRcasEnabled, useEnvironmentMipmaps, adaptiveVramSampling, previewOutlineEnabled;
    private bool ssaoEnabled, ptProgressiveAccumulation, ptUseHardwareRayTracing;
    private bool refreshingLocalization, suppressCustomPresetLoad, pendingUltraGiFallback;
    private bool hasExplicitShadow, hasExplicitShadowFilter, hasExplicitShadowSoftness, hasExplicitAnisotropicFiltering, hasExplicitSharpening, hasExplicitSsao, hasExplicitPathTracing;
    private ViewTemporalUpscalingOptions loadedTemporalBaseline = ViewTemporalUpscalingOptions.Off;
    private bool isProbingCapabilities, canRefreshCapabilities;
    private Exception? validationError;
    private string validationMessage = "";
    private string statusMessage = "";
    private string statusTemplate = "";
    private object?[] statusArgs = [];
    private string activeHelpTopicId = CategoryHelpTopic(0);

    public EditorPreviewSettingsViewModel(
        ViewQualityOptions? current = null,
        EditorPreviewCapabilities? capabilities = null,
        EditorPreviewRuntimeInfo? runtime = null,
        bool capabilitiesPending = false,
        bool isClassicEngine = false,
        string? customPresetsPath = null,
        Func<ViewQualityOptions?>? getExportQuality = null,
        Action<ViewQualityOptions>? copyToExportQuality = null,
        bool persistCustomPresets = true)
    {
        appliedOptions = current ?? new();
        appliedOptions.Validate();
        baseline = appliedOptions;
        this.capabilities = capabilities ?? new();
        this.runtime = runtime;
        IsClassicEngine = isClassicEngine;
        this.customPresetsPath = customPresetsPath;
        this.getExportQuality = getExportQuality;
        this.copyToExportQuality = copyToExportQuality;
        this.persistCustomPresets = persistCustomPresets;
        isProbingCapabilities = capabilitiesPending;
        giValues = [];
        temporalValues = [];
        LoadFromOptions(appliedOptions, resetCustomSelection: true);
        if (persistCustomPresets)
        {
            foreach (var preset in EditorPreferences.LoadCustomPreviewPresets(out _, customPresetsPath))
                customPresets.Add(preset);
        }
        Observe(this, static model => model.RefreshLocalization());
    }

    private void LoadFromOptions(ViewQualityOptions options, bool resetCustomSelection)
    {
        bool previousSuppress = refreshingLocalization;
        refreshingLocalization = true;
        try
        {
            baseline = options;
            var effectiveGi = options.EffectiveGi;
            loadedTemporalBaseline = options.EffectiveTemporalUpscaling;
            hasExplicitShadow = options.Shadow is not null;
            hasExplicitShadowFilter = options.ShadowFilter is not null;
            hasExplicitShadowSoftness = options.ShadowSoftness is not null;
            hasExplicitAnisotropicFiltering = options.AnisotropicFiltering is not null;
            hasExplicitSharpening = options.Sharpening is not null;
            hasExplicitSsao = options.ScreenSpaceAo is not null;
            hasExplicitPathTracing = options.PathTracing is not null;

            giValues = BuildGiValues(capabilities, appliedOptions.EffectiveGi.Mode, effectiveGi.Mode);
            giIndex = Array.IndexOf(giValues, effectiveGi.Mode);
            temporalValues = BuildTemporalValues(capabilities, appliedOptions.EffectiveTemporalUpscaling.Mode, loadedTemporalBaseline.Mode);
            temporalIndex = Array.IndexOf(temporalValues, loadedTemporalBaseline.Mode);
            temporalQualityIndex = InitialTemporalQualityIndexFrom(options);

            int aaIdx = Array.IndexOf(AntialiasingValues, options.Antialiasing);
            antialiasingIndex = aaIdx >= 0 ? aaIdx : 0;
            smaaIndex = options.SmaaQuality is { } smaa ? (int)smaa + 1 : 0;
            surfaceOrderIndex = options.SurfaceOrder == SurfaceDrawOrder.OpaqueFirst ? 0 : 1;
            spatialIndex = options.SpatialSamples is { } samples ? Array.IndexOf(SampleValues, samples) + 1 : 0;
            upscalingIndex = options.EffectiveUpscaling.Mode == ViewUpscalingMode.Fsr1 ? 1 : 0;
            fsr1QualityIndex = (int)options.EffectiveUpscaling.QualityPreset;
            rcasEnabled = options.EffectiveUpscaling.EnableRcas;
            rcasSharpness = (decimal)options.EffectiveUpscaling.RcasSharpness;
            standaloneRcasEnabled = options.EffectiveSharpening.Enabled;
            standaloneRcasSharpness = (decimal)options.EffectiveSharpening.Sharpness;
            renderScale = (decimal)options.RenderScale;

            var shadow = options.EffectiveShadow;
            shadowResolutionIndex = Math.Max(0, Array.IndexOf(ShadowResolutions, shadow.Resolution));
            var resolvedShadowFilter = options.ShadowFilter
                ?? (shadow.Sampling == OriginalTextureSampling.PointClamp ? ShadowFilterMode.Point : ShadowFilterMode.Linear);
            shadowFilterIndex = Math.Max(0, Array.IndexOf(ShadowFilterValues, resolvedShadowFilter));
            shadowSoftness = (decimal)options.EffectiveShadowSoftness;
            anisotropicFilteringIndex = Math.Max(0, Array.IndexOf(AnisotropicValues, options.EffectiveAnisotropicFiltering));

            var ssao = options.EffectiveScreenSpaceAo;
            ssaoEnabled = ssao.Enabled;
            ssaoStrength = (decimal)ssao.Strength;
            ssaoRadius = (decimal)ssao.Radius;
            ssaoSampleCountIndex = Math.Max(0, Array.IndexOf(SsaoSampleCounts, ssao.SampleCount));

            useEnvironmentMipmaps = options.UseEnvironmentMipmaps;
            adaptiveVramSampling = options.AdaptiveVramSampling;
            previewOutlineEnabled = options.PreviewOutlineEnabled;
            previewOutlineWidthScale = (decimal)options.PreviewOutlineWidthScale;

            pipelineModeIndex = Math.Max(0, Array.IndexOf(PipelineModeValues, options.PipelineMode));
            var pt = options.EffectivePathTracing;
            ptShadingModeIndex = Math.Max(0, Array.IndexOf(PtShadingModeValues, pt.ShadingMode));
            ptSamplesPerFrame = pt.SamplesPerFrame;
            ptMaxBounces = pt.MaxBounces;
            ptProgressiveAccumulation = pt.ProgressiveAccumulation;
            ptMaxAccumulatedFrames = pt.MaxAccumulatedFrames;
            ptInteractiveSamplesPerFrame = pt.InteractiveSamplesPerFrame;
            ptFireflyClamp = (decimal)pt.FireflyClamp;
            ptDenoiserIndex = (int)pt.Denoiser;
            ptUseHardwareRayTracing = pt.UseHardwareRayTracing;

            giStrength = (decimal)effectiveGi.Strength;
            giSamples = effectiveGi.SamplesPerPixel;
            giCompositionIndex = (int)effectiveGi.Composition;
            giDenoiserIndex = (int)effectiveGi.Denoiser;
            giToonResponseIndex = (int)effectiveGi.ToonResponse;
            giOcclusionStrength = (decimal)effectiveGi.OcclusionStrength;
            giOcclusionRadius = (decimal)effectiveGi.OcclusionRadius;
            giMaxRayDistance = (decimal)effectiveGi.MaxRayDistance;
            giDirectScale = (decimal)effectiveGi.DirectScale;
            giRayEpsilon = (decimal)effectiveGi.RayEpsilon;
            giSeed = effectiveGi.Seed;
            previewGiAcceleration = effectiveGi.PreviewAcceleration;

            diffusionEnabled = options.EffectiveDiffusion.Enabled;
            diffusionStrength = (decimal)options.EffectiveDiffusion.Strength;
            diffusionExtent = (decimal)options.EffectiveDiffusion.Extent;

            if (resetCustomSelection)
            {
                suppressCustomPresetLoad = true;
                try { customPresetIndex = -1; }
                finally { suppressCustomPresetLoad = false; }
            }
        }
        finally { refreshingLocalization = previousSuppress; }
    }

    private void NotifyAllChanged()
    {
        bool prev = refreshingLocalization;
        refreshingLocalization = true;
        try { Changed(null); }
        finally { refreshingLocalization = prev; }
        foreach (string name in IndexProperties) Changed(name);
    }

    private static readonly string[] IndexProperties =
    [
        nameof(SelectedCategoryIndex), nameof(CustomPresetIndex),
        nameof(GiIndex), nameof(AntialiasingIndex), nameof(SmaaIndex), nameof(SpatialIndex),
        nameof(SurfaceOrderIndex), nameof(UpscalingIndex), nameof(Fsr1QualityIndex),
        nameof(TemporalIndex), nameof(TemporalQualityIndex),
        nameof(GiCompositionIndex), nameof(GiDenoiserIndex), nameof(GiToonResponseIndex),
        nameof(ShadowResolutionIndex), nameof(ShadowFilterIndex), nameof(ShadowSamplingIndex),
        nameof(AnisotropicFilteringIndex), nameof(SsaoSampleCountIndex),
        nameof(PipelineModeIndex), nameof(PtShadingModeIndex), nameof(PtDenoiserIndex)
    ];

    private static LightingMode[] BuildGiValues(EditorPreviewCapabilities caps, params LightingMode[] preserve)
    {
        var modes = new List<LightingMode> { LightingMode.Legacy };
        if (caps.Gi?.SoftwareAvailable == true) modes.Add(LightingMode.SoftwareRayTracing);
        if (caps.Gi?.HardwareAvailable == true) modes.Add(LightingMode.HardwareRayTracing);
        foreach (var extra in preserve)
            if (!modes.Contains(extra)) modes.Add(extra);
        return modes.ToArray();
    }

    private static ViewTemporalUpscalingMode[] BuildTemporalValues(EditorPreviewCapabilities caps, params ViewTemporalUpscalingMode[] preserve)
    {
        var temporal = new List<ViewTemporalUpscalingMode> { ViewTemporalUpscalingMode.Off };
        if (caps.FsrAvailable) temporal.Add(ViewTemporalUpscalingMode.Fsr);
        if (caps.XeSsAvailable) temporal.Add(ViewTemporalUpscalingMode.XeSs);
        if (caps.DlssAvailable) temporal.Add(ViewTemporalUpscalingMode.Dlss);
        foreach (var extra in preserve)
            if (!temporal.Contains(extra)) temporal.Add(extra);
        return temporal.ToArray();
    }

    public bool IsClassicEngine { get; }
    public bool IsModernEngine => !IsClassicEngine;
    public string ClassicEngineNotice => Text("Mda.Text.Preview.ClassicNotice");

    public bool IsProbingCapabilities => isProbingCapabilities;
    public bool CapabilitiesReady => !isProbingCapabilities;
    public bool CanRefreshCapabilities
    {
        get => canRefreshCapabilities;
        set
        {
            if (canRefreshCapabilities == value) return;
            canRefreshCapabilities = value;
            Changed(nameof(CanRefreshCapabilities));
        }
    }
    public string CapabilityProbingNotice => Text("Mda.Text.Ux.Preview.ProbingCapabilities");
    public string RefreshCapabilitiesLabel => Text("Mda.Text.Ux.Preview.RefreshCapabilities");
    public bool NeedsCapabilitiesForApply => isProbingCapabilities && (
        (CurrentGiMode != LightingMode.Legacy && CurrentGiMode != appliedOptions.EffectiveGi.Mode)
        || (CurrentTemporalMode != ViewTemporalUpscalingMode.Off && CurrentTemporalMode != appliedOptions.EffectiveTemporalUpscaling.Mode)
        || (IsFsr1 && appliedOptions.EffectiveUpscaling.Mode != ViewUpscalingMode.Fsr1));

    // 6-category left navigation
    public IReadOnlyList<string> Categories =>
    [
        Text("Mda.Text.Preview.Cat.Overview"),
        Text("Mda.Text.Preview.Cat.Antialiasing"),
        Text("Mda.Text.Preview.Cat.Shadows"),
        Text("Mda.Text.Preview.Cat.Gi"),
        Text("Mda.Text.Preview.Cat.Post"),
        Text("Mda.Text.Preview.Cat.Diagnostics")
    ];
    public int SelectedCategoryIndex
    {
        get => selectedCategoryIndex;
        set
        {
            if (refreshingLocalization) return;
            int clamped = Math.Clamp(value, 0, 5);
            if (selectedCategoryIndex == clamped) return;
            selectedCategoryIndex = clamped;
            Changed(nameof(SelectedCategoryIndex));
            Changed(nameof(IsCategoryOverview));
            Changed(nameof(IsCategoryAntialiasing));
            Changed(nameof(IsCategoryShadowDetail));
            Changed(nameof(IsCategoryGlobalIllumination));
            Changed(nameof(IsCategoryPostDiffusion));
            Changed(nameof(IsCategoryDiagnostics));
            SetActiveHelpTopicInternal(CategoryHelpTopic(clamped));
        }
    }
    public bool IsCategoryOverview => selectedCategoryIndex == 0;
    public bool IsCategoryAntialiasing => selectedCategoryIndex == 1;
    public bool IsCategoryShadowDetail => selectedCategoryIndex == 2;
    public bool IsCategoryGlobalIllumination => selectedCategoryIndex == 3;
    public bool IsCategoryPostDiffusion => selectedCategoryIndex == 4;
    public bool IsCategoryDiagnostics => selectedCategoryIndex == 5;

    // Presets & Export Sync Labels
    public string PresetsLabel => Text("Mda.Text.Preview.PresetsLabel");
    public string PresetPerformanceLabel => Text("Mda.Text.Preview.Preset.Performance");
    public string PresetBalancedLabel => Text("Mda.Text.Preview.Preset.Balanced");
    public string PresetHighLabel => Text("Mda.Text.Preview.Preset.High");
    public string PresetUltraRayTracedLabel => Text("Mda.Text.Preview.Preset.UltraRayTraced");
    public string PresetPerformanceDesc => Text("Mda.Text.Preview.Preset.PerformanceDesc");
    public string PresetBalancedDesc => Text("Mda.Text.Preview.Preset.BalancedDesc");
    public string PresetHighDesc => Text("Mda.Text.Preview.Preset.HighDesc");
    public string PresetUltraRayTracedDesc => Text("Mda.Text.Preview.Preset.UltraRayTracedDesc");
    public string CustomPresetLabel => Text("Mda.Text.Preview.CustomPresetLabel");
    public string CustomPresetPlaceholder => Text("Mda.Text.Preview.CustomPresetPlaceholder");
    public string CustomPresetNameWatermark => Text("Mda.Text.Preview.CustomPresetNameWatermark");
    public string CustomPresetSaveNewLabel => Text("Mda.Text.Preview.CustomPresetSaveNew");
    public string CustomPresetOverwriteLabel => Text("Mda.Text.Preview.CustomPresetOverwrite");
    public string CustomPresetRenameLabel => Text("Mda.Text.Preview.CustomPresetRename");
    public string CustomPresetDeleteLabel => Text("Mda.Text.Preview.CustomPresetDelete");
    public string CopyToExportLabel => Text("Mda.Text.Preview.CopyToExport");
    public string LoadFromExportLabel => Text("Mda.Text.Preview.LoadFromExport");
    public string OverviewTitle => Text("Mda.Text.Preview.OverviewTitle");
    public string ActivePresetLabel => Text("Mda.Text.Preview.ActivePresetLabel");
    public string BuiltInPresetsTitle => Text("Mda.Text.Preview.BuiltInPresetsTitle");
    public string ApplyPresetActionLabel => Text("Mda.Text.Preview.ApplyPresetAction");

    public ObservableCollection<EditorPreviewCustomPreset> CustomPresets => customPresets;
    public int CustomPresetIndex
    {
        get => customPresetIndex;
        set
        {
            if (refreshingLocalization || customPresetIndex == value) return;
            customPresetIndex = value;
            Changed(nameof(CustomPresetIndex));
            Changed(nameof(HasSelectedCustomPreset));
            Changed(nameof(CanOverwriteCustomPreset));
            Changed(nameof(CanRenameCustomPreset));
            Changed(nameof(CanDeleteCustomPreset));
            if (!suppressCustomPresetLoad && value >= 0 && value < customPresets.Count)
            {
                var chosen = customPresets[value];
                CustomPresetName = chosen.Name;
                pendingUltraGiFallback = false;
                LoadFromOptions(chosen.Quality, resetCustomSelection: false);
                ClearStatusAndValidation();
                NotifyAllChanged();
            }
        }
    }
    public string CustomPresetName
    {
        get => customPresetName;
        set
        {
            if (refreshingLocalization || customPresetName == value) return;
            customPresetName = value ?? "";
            Changed(nameof(CustomPresetName));
            Changed(nameof(CanSaveCustomPreset));
            Changed(nameof(CanRenameCustomPreset));
        }
    }
    public bool HasSelectedCustomPreset => customPresetIndex >= 0 && customPresetIndex < customPresets.Count;
    public bool CanSaveCustomPreset => IsModernEngine && !string.IsNullOrWhiteSpace(customPresetName);
    public bool CanOverwriteCustomPreset => IsModernEngine && HasSelectedCustomPreset;
    public bool CanRenameCustomPreset => IsModernEngine && HasSelectedCustomPreset && !string.IsNullOrWhiteSpace(customPresetName);
    public bool CanDeleteCustomPreset => IsModernEngine && HasSelectedCustomPreset;
    public bool CanCopyToExport => IsModernEngine && copyToExportQuality is not null;
    public bool CanLoadFromExport => IsModernEngine && getExportQuality is not null;

    public bool IsPerformancePresetActive => TryGetMatchedBuiltIn(out var kind) && kind == BuiltInPreviewPresetKind.Performance;
    public bool IsBalancedPresetActive => TryGetMatchedBuiltIn(out var kind) && kind == BuiltInPreviewPresetKind.Balanced;
    public bool IsHighPresetActive => TryGetMatchedBuiltIn(out var kind) && kind == BuiltInPreviewPresetKind.High;
    public bool IsUltraRayTracedPresetActive => TryGetMatchedBuiltIn(out var kind) && kind == BuiltInPreviewPresetKind.UltraRayTraced;

    public string ActivePresetSummary
    {
        get
        {
            if (TryGetMatchedBuiltIn(out var kind))
            {
                return kind switch
                {
                    BuiltInPreviewPresetKind.Performance => PresetPerformanceLabel,
                    BuiltInPreviewPresetKind.Balanced => PresetBalancedLabel,
                    BuiltInPreviewPresetKind.High => PresetHighLabel,
                    BuiltInPreviewPresetKind.UltraRayTraced => PresetUltraRayTracedLabel,
                    _ => Text("Mda.Text.Preview.Preset.CustomActive")
                };
            }
            if (HasSelectedCustomPreset && TryBuildDraft(out var draft, validateCompatibility: false) && draft is not null
                && ViewQualityPresets.AreEquivalent(draft, customPresets[customPresetIndex].Quality))
                return customPresets[customPresetIndex].Name;
            return Text("Mda.Text.Preview.Preset.CustomActive");
        }
    }

    public string OverviewConfigurationSummary
    {
        get
        {
            string pipeline = PipelineModeChoices[Math.Clamp(pipelineModeIndex, 0, PipelineModeChoices.Count - 1)];
            if (IsPathTracing)
            {
                string ptStyle = PtShadingModeChoices[Math.Clamp(ptShadingModeIndex, 0, PtShadingModeChoices.Count - 1)];
                string ptDenoise = PtDenoiserChoices[Math.Clamp(ptDenoiserIndex, 0, PtDenoiserChoices.Count - 1)];
                string ptBounces = Format("Mda.Text.Preview.OverviewPtBounces", ptMaxBounces ?? 4);
                return $"{pipeline} · {ptStyle} · {ptSamplesPerFrame ?? 1} SPP · {ptBounces} · {ptDenoise}";
            }
            string aa = CurrentTemporalMode != ViewTemporalUpscalingMode.Off
                ? TemporalModes[Math.Clamp(temporalIndex, 0, TemporalModes.Count - 1)]
                : IsFsr1
                    ? "FSR 1"
                    : CurrentAntialiasing == FrameAntialiasing.Smaa1X
                        ? $"SMAA ({SmaaQualities[Math.Clamp(smaaIndex, 0, SmaaQualities.Count - 1)]})"
                        : AntialiasingModes[Math.Clamp(antialiasingIndex, 0, AntialiasingModes.Count - 1)];
            string ssaa = SpatialSamples[Math.Clamp(spatialIndex, 0, SpatialSamples.Count - 1)];
            string shadow = ShadowResolutions[Math.Clamp(shadowResolutionIndex, 0, ShadowResolutions.Length - 1)] is int r && r > 0
                ? $"{r}×{r} ({ShadowFilterChoices[Math.Clamp(shadowFilterIndex, 0, ShadowFilterChoices.Count - 1)]})"
                : Text("Mda.Text.Context.OptionOff");
            string gi = GiModes[Math.Clamp(giIndex, 0, GiModes.Count - 1)];
            string ssao = SsaoEnabled ? $"SSAO {ssaoStrength ?? 0.4m:0.##}" : $"SSAO {Text("Mda.Text.Context.OptionOff")}";
            string diff = DiffusionEnabled ? Text("Mda.Text.1251a1baefb5") : Text("Mda.Text.Context.OptionOff");
            return $"{pipeline} · {aa} · SSAA {ssaa} · {RenderScaleLabel} {(renderScale ?? 1m):0.##}× · {ShadowResolutionLabel} {shadow} · {ssao} · GI {gi} · {DiffusionLabel} {diff}";
        }
    }

    private bool TryGetMatchedBuiltIn(out BuiltInPreviewPresetKind kind)
    {
        kind = default;
        return TryBuildDraft(out var draft, validateCompatibility: false) && draft is not null
            && ViewQualityPresets.TryMatchBuiltIn(draft, out kind);
    }

    // Core UI labels
    public string Title => Text("预览画质");
    public string RuntimeLabel => Text("当前视口");
    public string PerformanceDetailsLabel => Text("性能记录");
    public string SupportLabel => Text("支持情况");
    public bool HasRuntimeInfo => runtime is not null;
    public string DeviceSummary => runtime?.DeviceSummary ?? "";
    public string ResolutionSummary => runtime?.ResolutionSummary ?? "";
    public string SamplingSummary => runtime?.SamplingSummary ?? "";
    public string PerformanceSummary => runtime?.PerformanceSummary ?? "";
    public string PerformanceHint => runtime?.PerformanceHint ?? "";
    public string AntialiasingLabel => Text("抗锯齿");
    public string ModeLabel => Text("模式");
    public string SmaaLabel => Text("边缘平滑质量");
    public string SurfaceOrderLabel => Text("Mda.Text.SurfaceOrder.Label");
    public string SpatialLabel => Text("精细采样");
    public string SamplingHint => Text("Mda.Text.Preview.SamplingHint");
    public string RenderScaleLabel => Text("Mda.Text.Preview.RenderScaleLabel");
    public string RenderScaleHint => Text("Mda.Text.Preview.RenderScaleHint");
    public string StandaloneSharpeningSectionLabel => Text("Mda.Text.Preview.StandaloneSharpeningSection");
    public string StandaloneSharpeningEnabledLabel => Text("Mda.Text.Preview.StandaloneSharpeningEnabled");
    public string StandaloneSharpeningAmountLabel => Text("Mda.Text.Preview.StandaloneSharpeningAmount");
    public string StandaloneSharpeningHint => Text("Mda.Text.Preview.StandaloneSharpeningHint");
    public string AnisotropicFilteringLabel => Text("Mda.Text.Preview.AnisotropicFilteringLabel");
    public string AnisotropicFilteringHint => Text("Mda.Text.Preview.AnisotropicFilteringHint");
    public string PipelineSectionLabel => Text("Mda.Text.Preview.PipelineSection");
    public string PipelineModeLabel => Text("Mda.Text.Preview.PipelineModeLabel");
    public string PipelineModeHint => Text("Mda.Text.Preview.PipelineModeHint");
    public string PathTracingSectionLabel => Text("Mda.Text.Preview.PathTracingSection");
    public string PtShadingModeLabel => Text("Mda.Text.Preview.PtShadingModeLabel");
    public string PtSamplesPerFrameLabel => Text("Mda.Text.Preview.PtSamplesPerFrameLabel");
    public string PtMaxBouncesLabel => Text("Mda.Text.Preview.PtMaxBouncesLabel");
    public string PtProgressiveLabel => Text("Mda.Text.Preview.PtProgressiveLabel");
    public string PtDenoiserLabel => Text("Mda.Text.Preview.PtDenoiserLabel");
    public string PtMaxAccumulatedFramesLabel => Text("Mda.Text.Preview.PtMaxAccumulatedFramesLabel");
    public string PtInteractiveSamplesLabel => Text("Mda.Text.Preview.PtInteractiveSamplesLabel");
    public string PtFireflyClampLabel => Text("Mda.Text.Preview.PtFireflyClampLabel");
    public string PtUseHardwareRtLabel => Text("Mda.Text.Preview.PtUseHardwareRtLabel");
    public string PtUseHardwareRtHint => Text("Mda.Text.Preview.PtUseHardwareRtHint");
    public string GiLabel => Text("一次间接光照（GI）");
    public string StrengthLabel => Text("强度");
    public string GiSamplesLabel => Text("每像素采样");
    public string CompositionLabel => Text("受光方式");
    public string DenoiserLabel => Text("GI 降噪");
    public string GiToonResponseLabel => Text("Mda.Text.Preview.GiToonResponseLabel");
    public string GiOcclusionStrengthLabel => Text("Mda.Text.Preview.GiOcclusionStrengthLabel");
    public string GiOcclusionRadiusLabel => Text("Mda.Text.Preview.GiOcclusionRadiusLabel");
    public string GiMaxRayDistanceLabel => Text("Mda.Text.Preview.GiMaxRayDistanceLabel");
    public string GiDirectScaleLabel => Text("Mda.Text.Preview.GiDirectScaleLabel");
    public string GiRayEpsilonLabel => Text("Mda.Text.Preview.GiRayEpsilonLabel");
    public string GiSeedLabel => Text("Mda.Text.Preview.GiSeedLabel");
    public string PreviewGiAccelerationLabel => Text("Mda.Text.Preview.GiAccelerationLabel");
    public string PreviewGiAccelerationHint => Text("Mda.Text.Preview.GiAccelerationHint");
    public string GiHint => Text("降噪用于卡通色阶受光；角色保持中性补光。");
    public string UpscalingLabel => Text("超分与时域重建");
    public string TemporalLabel => Text("性能与清晰度");
    public string ReconstructionLabel => Text("重建方式");
    public string ReconstructionHint => Text("画质模式用较低内部分辨率提升速度；原生抗锯齿保持分辨率并平滑边缘。可用方式取决于设备与已安装组件。");
    public string AdvancedSamplingLabel => Text("高级采样与空间超分");
    public string AdvancedLightingLabel => Text("高级光照设置");
    public string AdvancedDiffusionLabel => Text("柔光细节");
    public string AdvancedShadowDetailLabel => Text("Mda.Text.Preview.AdvancedShadowDetailLabel");
    public string LightingLabel => Text("光照");
    public string SceneAppearanceHint => Text("场景阴影与模型描边沿用工程数据；预览画质不会改写材质或画风。");
    public string QualityLabel => Text("画质");
    public string Fsr1Label => Text("空间超分");
    public string RcasLabel => Text("启用 RCAS 锐化");
    public string SharpnessLabel => Text("锐化衰减");
    public string RcasHint => Text("0 最锐利，2 最柔和。");
    public string TemporalHint => Text("Mda.Text.Preview.TemporalHint");
    public string CapabilityHint => isProbingCapabilities
        ? Text("Mda.Text.Ux.Preview.CheckingCapabilitiesHint")
        : string.Join(Environment.NewLine,
            new[] { Text("仅显示通过当前设备与已安装组件检查的选项。") }.Concat(capabilities.Reasons.Select(Text)));
    public string DiffusionLabel => Text("柔光");
    public string DiffusionEnabledLabel => Text("Mda.Text.1251a1baefb5");
    public string ExtentLabel => Text("范围");
    public string DiffusionHint => Text("最后处理场景画面，辅助标记保持清晰。");
    public string ShadowsSectionLabel => Text("Mda.Text.Preview.ShadowsSection");
    public string ShadowResolutionLabel => Text("Mda.Text.Preview.ShadowResolutionLabel");
    public string ShadowSamplingLabel => Text("Mda.Text.Preview.ShadowSamplingLabel");
    public string ShadowSoftnessLabel => Text("Mda.Text.Preview.ShadowSoftnessLabel");
    public string SsaoSectionLabel => Text("Mda.Text.Preview.SsaoSection");
    public string SsaoEnabledLabel => Text("Mda.Text.Preview.SsaoEnabledLabel");
    public string SsaoStrengthLabel => Text("Mda.Text.Preview.SsaoStrengthLabel");
    public string SsaoRadiusLabel => Text("Mda.Text.Preview.SsaoRadiusLabel");
    public string SsaoSampleCountLabel => Text("Mda.Text.Preview.SsaoSampleCountLabel");
    public string SsaoHint => Text("Mda.Text.Preview.SsaoHint");
    public string DetailSectionLabel => Text("Mda.Text.Preview.DetailSection");
    public string EnvironmentMipmapsLabel => Text("Mda.Text.Preview.EnvironmentMipmapsLabel");
    public string EnvironmentMipmapsHint => Text("Mda.Text.Preview.EnvironmentMipmapsHint");
    public string PreviewOutlineEnabledLabel => Text("Mda.Text.Preview.PreviewOutlineEnabledLabel");
    public string PreviewOutlineWidthScaleLabel => Text("Mda.Text.Preview.PreviewOutlineWidthScaleLabel");
    public string PreviewOutlineHint => Text("Mda.Text.Preview.PreviewOutlineHint");
    public string PreviewOutlineDisabledHint => Text("Mda.Text.Preview.PreviewOutlineDisabledHint");
    public string VramSectionLabel => Text("Mda.Text.Preview.VramSection");
    public string AdaptiveVramSamplingLabel => Text("Mda.Text.Preview.AdaptiveVramSamplingLabel");
    public string AdaptiveVramSamplingHint => Text("Mda.Text.Preview.AdaptiveVramSamplingHint");
    public string LocalHint => Text("仅保存在本机，不修改工程；输出画质在输出窗口单独设置。");
    public string ApplyLabel => Text("应用");
    public string CancelLabel => Text("取消");

    // Inline mutual-exclusion & reason notices
    public bool HasAntialiasingConflictHint => IsPathTracing || IsTemporal || IsTaa || IsMsaa;
    public string AntialiasingConflictHint => IsPathTracing
        ? Text("Mda.Text.Preview.Hint.PathTracingLocksAa")
        : IsTemporal
            ? Text("Mda.Text.Preview.Hint.TemporalLocksSpatial")
            : IsTaa
                ? Text("Mda.Text.Preview.Hint.TaaLocksSpatial")
                : IsMsaa
                    ? Text("Mda.Text.Preview.Hint.MsaaLocksSpatial")
                    : "";
    public bool HasTemporalConflictHint => IsPathTracing || IsTemporal || IsTaa || IsMsaa;
    public string TemporalConflictHint => IsPathTracing
        ? Text("Mda.Text.Preview.Hint.PathTracingLocksTemporal")
        : IsTemporal
            ? Text("Mda.Text.Preview.Hint.TemporalLocksSpatial")
            : Text("Mda.Text.Preview.Hint.AaLocksTemporal");
    public bool HasUpscalingOrSsaa => IsTemporal || IsFsr1 || CurrentSpatialSamples > 1;
    public bool HasRenderScaleConflictHint => IsPathTracing || HasUpscalingOrSsaa;
    public string RenderScaleConflictHint => IsPathTracing
        ? Text("Mda.Text.Preview.Hint.PathTracingLocksRenderScale")
        : renderScale is { } s && s != 1m && HasUpscalingOrSsaa
            ? Text(IsTemporal || IsFsr1 ? "Mda.Text.Preview.Conflict.RenderScaleWithUpscaling" : "Mda.Text.Preview.Conflict.RenderScaleWithSsaa")
            : Text("Mda.Text.Preview.Hint.UpscalingLocksRenderScale");
    public bool ShowStandaloneRcasFsrNotice => !IsPathTracing && IsFsr1 && RcasEnabled;
    public string StandaloneRcasFsrNotice => StandaloneRcasEnabled
        ? Text("Mda.Text.Preview.Hint.DoubleSharpeningWarning")
        : Text("Mda.Text.Preview.Hint.StandaloneRcasVsFsr");
    public bool IsShadowDisabled => IsPathTracing || shadowResolutionIndex == 0;
    public string ShadowDisabledHint => IsPathTracing
        ? Text("Mda.Text.Preview.Hint.PathTracingLocksShadow")
        : Text("Mda.Text.Preview.ShadowDisabledHint");
    public bool ShowShadowSoftnessHint => IsShadowEnabled && CurrentShadowFilter != ShadowFilterMode.PcfSoft;
    public string ShadowSoftnessHint => Text("Mda.Text.Preview.Hint.ShadowSoftnessRequiresPcf");
    public bool ShowSsaoNotice => IsPathTracing || IsRayGiOcclusionActive;
    public string SsaoNotice => IsPathTracing
        ? Text("Mda.Text.Preview.Hint.PathTracingLocksSsao")
        : IsRayGiOcclusionActive && SsaoEnabled
            ? Text("Mda.Text.Preview.Conflict.SsaoWithRayGiOcclusion")
            : IsRayGiOcclusionActive
                ? Text("Mda.Text.Preview.Hint.RayGiOcclusionLocksSsao")
                : "";
    public bool ShowPathTracingLocksGiNotice => IsPathTracing;
    public string PathTracingLocksGiNotice => Text("Mda.Text.Preview.Hint.PathTracingLocksGi");
    public bool ShowPtHardwareFallbackNotice => IsPathTracing && PtUseHardwareRayTracing && !isProbingCapabilities && capabilities.Gi is { HardwareAvailable: false };
    public string PtHardwareFallbackNotice => Text("Mda.Text.Preview.Hint.PtHardwareFallback");
    public bool ShowGiPaletteConflictHint => IsGiEnabled && GiCompositionIndex != (int)RayGiComposition.ToonPalette;
    public string GiPaletteConflictHint => Text("Mda.Text.Preview.Hint.GiRequiresToonPalette");
    public bool HasGiOcclusionConflict => IsGiEnabled && giOcclusionStrength is > 0m && giOcclusionRadius is { } r && giRayEpsilon is { } e && r <= e;
    public string GiOcclusionConflictHint => Text("Mda.Text.Preview.Conflict.GiOcclusionRadius");
    public bool ShowGiOcclusionLockedBySsaoHint => IsGiEnabled && SsaoEnabled;
    public string GiOcclusionLockedBySsaoHint => (giOcclusionStrength ?? 0m) > 0m
        ? Text("Mda.Text.Preview.Conflict.SsaoWithRayGiOcclusion")
        : Text("Mda.Text.Preview.Hint.SsaoLocksRayGiOcclusion");

    // Lists
    public IReadOnlyList<string> AntialiasingModes =>
    [
        Text("沿用场景"),
        Text("Mda.Text.Context.OptionOff"),
        Text("Mda.Text.Preview.AaFxaa"),
        Text("边缘平滑（SMAA）"),
        Text("Mda.Text.Preview.AaTaa"),
        Text("Mda.Text.Preview.AaMsaa2x"),
        Text("Mda.Text.Preview.AaMsaa4x"),
        Text("Mda.Text.Preview.AaMsaa8x")
    ];
    public IReadOnlyList<string> SmaaQualities => [Text("沿用场景"), Text("低"), Text("中"), Text("高"), Text("超高")];
    public IReadOnlyList<EditorSurfaceOrderChoice> SurfaceOrders { get; } = EditorSurfaceOrderChoice.Create();
    public IReadOnlyList<string> SpatialSamples => [Text("沿用场景"), Text("1× · 关闭"), "2×", "4×", "8×", "16×", "32×"];
    public IReadOnlyList<string> ShadowResolutionChoices => [Text("Mda.Text.Preview.ShadowResolutionOff"), "512 × 512", "1024 × 1024", "2048 × 2048", "4096 × 4096"];
    public IReadOnlyList<string> ShadowFilterChoices => [Text("Mda.Text.Preview.ShadowSamplingLinear"), Text("Mda.Text.Preview.ShadowSamplingPoint"), Text("Mda.Text.Preview.ShadowFilterPcfSoft")];
    public IReadOnlyList<string> ShadowSamplingChoices => ShadowFilterChoices;
    public IReadOnlyList<string> AnisotropicFilteringChoices => [Text("Mda.Text.Preview.AnisoOff"), "2×", "4×", "8×", "16×"];
    public IReadOnlyList<string> SsaoSampleCountChoices => ["4", "8", "16"];
    public IReadOnlyList<string> PipelineModeChoices =>
    [
        Text("Mda.Text.Preview.PipelineForward"),
        Text("Mda.Text.Preview.PipelineClusteredForward"),
        Text("Mda.Text.Preview.PipelinePathTracing")
    ];
    public IReadOnlyList<string> PtShadingModeChoices =>
    [
        Text("Mda.Text.Preview.PtShadingToonHybrid"),
        Text("Mda.Text.Preview.PtShadingPhysical")
    ];
    public IReadOnlyList<string> PtDenoiserChoices => [Text("Mda.Text.Context.OptionOff"), Text("空间降噪"), Text("时空降噪")];
    public IReadOnlyList<string> GiModes => giValues.Select(mode => WithAvailability(mode switch
    { LightingMode.Legacy => Text("Mda.Text.Context.OptionOff"), LightingMode.SoftwareRayTracing => Text("软件一次 GI"), _ => Text("硬件一次 GI") }, GiAvailable(mode))).ToArray();
    public IReadOnlyList<string> GiCompositions => [Text("原色叠加"), Text("卡通色阶")];
    public IReadOnlyList<string> GiDenoisers => [Text("Mda.Text.Context.OptionOff"), Text("空间降噪"), Text("时空降噪")];
    public IReadOnlyList<string> GiToonResponses => [Text("Mda.Text.Preview.GiToonResponseContinuous"), Text("Mda.Text.Preview.GiToonResponsePreserve")];
    public IReadOnlyList<string> UpscalingModes => capabilities.SpatialUpscalingAvailable || isProbingCapabilities || appliedOptions.EffectiveUpscaling.Mode == ViewUpscalingMode.Fsr1 || baseline.EffectiveUpscaling.Mode == ViewUpscalingMode.Fsr1
        ? [Text("Mda.Text.Context.OptionOff"), WithAvailability("FSR 1", isProbingCapabilities || capabilities.SpatialUpscalingAvailable)]
        : [Text("Mda.Text.Context.OptionOff")];
    public IReadOnlyList<string> Fsr1Qualities => [Text("超画质"), Text("画质"), Text("均衡"), Text("性能")];
    public IReadOnlyList<string> TemporalModes => temporalValues.Select(mode => WithAvailability(mode switch
    { ViewTemporalUpscalingMode.Off => Text("Mda.Text.Context.OptionOff"), ViewTemporalUpscalingMode.Fsr => "FSR", ViewTemporalUpscalingMode.XeSs => "XeSS", _ => "DLSS" }, TemporalAvailable(mode))).ToArray();
    public IReadOnlyList<string> TemporalQualities => CurrentTemporalQualityValues.Select(value => WithAvailability(TemporalQualityName(value), TemporalQualityAvailable(value))).ToArray();

    // Editable Properties
    public int GiIndex
    {
        get => giIndex;
        set
        {
            if (!Set(ref giIndex, value)) return;
            pendingUltraGiFallback = false;
            SetActiveHelpTopicInternal(ResolveDynamicHelpTopic("gi.mode"));
        }
    }
    public int AntialiasingIndex
    {
        get => antialiasingIndex;
        set
        {
            if (!Set(ref antialiasingIndex, value)) return;
            if (IsTaa || IsMsaa)
            {
                if (CurrentSpatialSamples > 1)
                {
                    spatialIndex = 1; // 1× Off
                    Changed(nameof(SpatialIndex));
                }
                if (upscalingIndex == 1)
                {
                    upscalingIndex = 0; // Off
                    Changed(nameof(UpscalingIndex));
                }
                RefreshDerived();
            }
            SetActiveHelpTopicInternal(ResolveDynamicHelpTopic("aa.mode"));
        }
    }
    public int SmaaIndex
    {
        get => smaaIndex;
        set { if (Set(ref smaaIndex, value)) SelectHelpTopic("help.aa.smaaQuality"); }
    }
    public int SurfaceOrderIndex
    {
        get => surfaceOrderIndex;
        set { if (Set(ref surfaceOrderIndex, value)) SetActiveHelpTopicInternal(ResolveDynamicHelpTopic("surface.order")); }
    }
    public int SpatialIndex
    {
        get => spatialIndex;
        set
        {
            if (!Set(ref spatialIndex, value)) return;
            if (CurrentSpatialSamples > 1 && renderScale != 1m)
            {
                renderScale = 1m;
                Changed(nameof(RenderScale));
                RefreshDerived();
            }
            SelectHelpTopic("help.aa.ssaa");
        }
    }
    public int UpscalingIndex
    {
        get => upscalingIndex;
        set
        {
            if (!Set(ref upscalingIndex, value)) return;
            if (upscalingIndex == 1 && renderScale != 1m)
            {
                renderScale = 1m;
                Changed(nameof(RenderScale));
                RefreshDerived();
            }
            SetActiveHelpTopicInternal(ResolveDynamicHelpTopic("fsr1.mode"));
        }
    }
    public int Fsr1QualityIndex
    {
        get => fsr1QualityIndex;
        set { if (Set(ref fsr1QualityIndex, value)) SelectHelpTopic("help.fsr1.quality"); }
    }
    public int TemporalIndex
    {
        get => temporalIndex;
        set
        {
            if (!Set(ref temporalIndex, value)) return;
            temporalQualityIndex = InitialTemporalQualityIndex;
            if (IsTemporal && renderScale != 1m)
            {
                renderScale = 1m;
                Changed(nameof(RenderScale));
                RefreshDerived();
            }
            Changed(nameof(TemporalQualities)); Changed(nameof(TemporalQualityIndex));
            SetActiveHelpTopicInternal(ResolveDynamicHelpTopic("temporal.mode"));
        }
    }
    public int TemporalQualityIndex
    {
        get => temporalQualityIndex;
        set { if (Set(ref temporalQualityIndex, value)) SelectHelpTopic("help.temporal.quality"); }
    }
    public int GiCompositionIndex
    {
        get => giCompositionIndex;
        set { if (Set(ref giCompositionIndex, value)) SetActiveHelpTopicInternal(ResolveDynamicHelpTopic("gi.comp")); }
    }
    public int GiDenoiserIndex
    {
        get => giDenoiserIndex;
        set { if (Set(ref giDenoiserIndex, value)) SelectHelpTopic("help.gi.denoiser"); }
    }
    public int GiToonResponseIndex
    {
        get => giToonResponseIndex;
        set { if (Set(ref giToonResponseIndex, value)) SelectHelpTopic("help.gi.toonResponse"); }
    }
    public int ShadowResolutionIndex
    {
        get => shadowResolutionIndex;
        set { if (Set(ref shadowResolutionIndex, value)) { hasExplicitShadow = true; SelectHelpTopic("help.shadow.res"); } }
    }
    public int ShadowFilterIndex
    {
        get => shadowFilterIndex;
        set
        {
            if (!Set(ref shadowFilterIndex, value)) return;
            hasExplicitShadow = true;
            hasExplicitShadowFilter = true;
            Changed(nameof(ShadowSamplingIndex));
            SetActiveHelpTopicInternal(ResolveDynamicHelpTopic("shadow.filter"));
        }
    }
    public int ShadowSamplingIndex
    {
        get => shadowFilterIndex;
        set => ShadowFilterIndex = value;
    }
    public int AnisotropicFilteringIndex
    {
        get => anisotropicFilteringIndex;
        set
        {
            if (!Set(ref anisotropicFilteringIndex, value)) return;
            hasExplicitAnisotropicFiltering = true;
            SetActiveHelpTopicInternal(ResolveDynamicHelpTopic("aniso"));
        }
    }
    public int SsaoSampleCountIndex
    {
        get => ssaoSampleCountIndex;
        set
        {
            if (!Set(ref ssaoSampleCountIndex, value)) return;
            hasExplicitSsao = true;
            SelectHelpTopic("help.ssao.samples");
        }
    }
    public int PipelineModeIndex
    {
        get => pipelineModeIndex;
        set
        {
            if (!Set(ref pipelineModeIndex, value)) return;
            if (IsPathTracing)
            {
                hasExplicitPathTracing = true;
                if (renderScale != 1m)
                {
                    renderScale = 1m;
                    Changed(nameof(RenderScale));
                    RefreshDerived();
                }
            }
            SetActiveHelpTopicInternal(ResolveDynamicHelpTopic("pipeline.mode"));
        }
    }
    public int PtShadingModeIndex
    {
        get => ptShadingModeIndex;
        set { if (Set(ref ptShadingModeIndex, value)) { hasExplicitPathTracing = true; SetActiveHelpTopicInternal(ResolveDynamicHelpTopic("pt.shading")); } }
    }
    public int PtDenoiserIndex
    {
        get => ptDenoiserIndex;
        set { if (Set(ref ptDenoiserIndex, value)) { hasExplicitPathTracing = true; SetActiveHelpTopicInternal(ResolveDynamicHelpTopic("pt.denoiser")); } }
    }

    public decimal? GiStrength { get => giStrength; set => Set(ref giStrength, value); }
    public decimal? GiSamples { get => giSamples; set => Set(ref giSamples, value); }
    public decimal? GiOcclusionStrength { get => giOcclusionStrength; set => Set(ref giOcclusionStrength, value); }
    public decimal? GiOcclusionRadius { get => giOcclusionRadius; set => Set(ref giOcclusionRadius, value); }
    public decimal? GiMaxRayDistance { get => giMaxRayDistance; set => Set(ref giMaxRayDistance, value); }
    public decimal? GiDirectScale { get => giDirectScale; set => Set(ref giDirectScale, value); }
    public decimal? GiRayEpsilon { get => giRayEpsilon; set => Set(ref giRayEpsilon, value); }
    public decimal? GiSeed { get => giSeed; set => Set(ref giSeed, value); }
    public decimal? DiffusionStrength { get => diffusionStrength; set => Set(ref diffusionStrength, value); }
    public decimal? DiffusionExtent { get => diffusionExtent; set => Set(ref diffusionExtent, value); }
    public decimal? RcasSharpness { get => rcasSharpness; set => Set(ref rcasSharpness, value); }
    public decimal? StandaloneRcasSharpness
    {
        get => standaloneRcasSharpness;
        set { if (Set(ref standaloneRcasSharpness, value)) hasExplicitSharpening = true; }
    }
    public decimal? RenderScale { get => renderScale; set => Set(ref renderScale, value); }
    public decimal? PreviewOutlineWidthScale { get => previewOutlineWidthScale; set => Set(ref previewOutlineWidthScale, value); }
    public decimal? ShadowSoftness
    {
        get => shadowSoftness;
        set { if (Set(ref shadowSoftness, value)) hasExplicitShadowSoftness = true; }
    }
    public decimal? SsaoStrength
    {
        get => ssaoStrength;
        set { if (Set(ref ssaoStrength, value)) hasExplicitSsao = true; }
    }
    public decimal? SsaoRadius
    {
        get => ssaoRadius;
        set { if (Set(ref ssaoRadius, value)) hasExplicitSsao = true; }
    }
    public decimal? PtSamplesPerFrame
    {
        get => ptSamplesPerFrame;
        set { if (Set(ref ptSamplesPerFrame, value)) hasExplicitPathTracing = true; }
    }
    public decimal? PtMaxBounces
    {
        get => ptMaxBounces;
        set { if (Set(ref ptMaxBounces, value)) hasExplicitPathTracing = true; }
    }
    public decimal? PtMaxAccumulatedFrames
    {
        get => ptMaxAccumulatedFrames;
        set { if (Set(ref ptMaxAccumulatedFrames, value)) hasExplicitPathTracing = true; }
    }
    public decimal? PtInteractiveSamplesPerFrame
    {
        get => ptInteractiveSamplesPerFrame;
        set { if (Set(ref ptInteractiveSamplesPerFrame, value)) hasExplicitPathTracing = true; }
    }
    public decimal? PtFireflyClamp
    {
        get => ptFireflyClamp;
        set { if (Set(ref ptFireflyClamp, value)) hasExplicitPathTracing = true; }
    }

    public bool PreviewGiAcceleration { get => previewGiAcceleration; set => Set(ref previewGiAcceleration, value); }
    public bool DiffusionEnabled { get => diffusionEnabled; set => Set(ref diffusionEnabled, value); }
    public bool RcasEnabled { get => rcasEnabled; set => Set(ref rcasEnabled, value); }
    public bool StandaloneRcasEnabled
    {
        get => standaloneRcasEnabled;
        set { if (Set(ref standaloneRcasEnabled, value)) hasExplicitSharpening = true; }
    }
    public bool UseEnvironmentMipmaps { get => useEnvironmentMipmaps; set => Set(ref useEnvironmentMipmaps, value); }
    public bool AdaptiveVramSampling { get => adaptiveVramSampling; set => Set(ref adaptiveVramSampling, value); }
    public bool PreviewOutlineEnabled { get => previewOutlineEnabled; set => Set(ref previewOutlineEnabled, value); }
    public bool SsaoEnabled
    {
        get => ssaoEnabled;
        set { if (Set(ref ssaoEnabled, value)) hasExplicitSsao = true; }
    }
    public bool PtProgressiveAccumulation
    {
        get => ptProgressiveAccumulation;
        set { if (Set(ref ptProgressiveAccumulation, value)) hasExplicitPathTracing = true; }
    }
    public bool PtUseHardwareRayTracing
    {
        get => ptUseHardwareRayTracing;
        set { if (Set(ref ptUseHardwareRayTracing, value)) hasExplicitPathTracing = true; }
    }

    // Derived enabling states
    public bool IsPathTracing => CurrentPipelineMode == RenderPipelineMode.PathTracing;
    public bool IsPathTracingEditable => IsModernEngine && IsPathTracing;
    public bool IsRasterPipeline => !IsPathTracing;
    public bool IsGiSectionEnabled => IsModernEngine && !IsPathTracing;
    public bool IsGiEnabled => IsModernEngine && !IsPathTracing && giIndex >= 0 && giIndex < giValues.Length && giValues[giIndex] != LightingMode.Legacy;
    public bool IsGiToonPaletteEnabled => IsGiEnabled && GiCompositionIndex == (int)RayGiComposition.ToonPalette;
    public bool IsGiDenoiserEnabled => IsGiToonPaletteEnabled;
    public bool IsGiOcclusionStrengthEditable => IsGiEnabled && (!SsaoEnabled || (giOcclusionStrength ?? 0m) > 0m);
    public bool IsRayGiOcclusionActive => !IsPathTracing && CurrentGiMode != LightingMode.Legacy && (giOcclusionStrength ?? 0m) > 0m;
    public bool IsTemporal => !IsPathTracing && CurrentTemporalMode != ViewTemporalUpscalingMode.Off;
    public bool IsTemporalModeEditable => IsModernEngine && !IsPathTracing && !IsTaa && !IsMsaa;
    public bool IsTemporalEnabled => IsModernEngine && IsTemporal;
    public bool IsSpatialAaEnabled => IsModernEngine && !IsPathTracing && !IsTemporal;
    public bool IsTaa => !IsPathTracing && !IsTemporal && CurrentAntialiasing == FrameAntialiasing.Taa;
    public bool IsMsaa => !IsPathTracing && !IsTemporal && CurrentAntialiasing is { } aa && OriginalFrameMath.IsMsaa(aa);
    public bool IsSmaa => IsModernEngine && !IsPathTracing && !IsTemporal && CurrentAntialiasing == FrameAntialiasing.Smaa1X;
    public bool IsSpatialSamplesEditable => IsModernEngine && !IsPathTracing && !IsTemporal && ((!IsTaa && !IsMsaa) || CurrentSpatialSamples > 1);
    public bool IsFsr1ModeEditable => IsModernEngine && !IsPathTracing && !IsTemporal && ((!IsTaa && !IsMsaa) || upscalingIndex == 1);
    public bool IsFsr1 => IsModernEngine && !IsPathTracing && !IsTemporal && upscalingIndex == 1;
    public bool IsRcas => IsFsr1 && RcasEnabled;
    public bool IsStandaloneRcasSharpnessEditable => IsModernEngine && StandaloneRcasEnabled;
    public bool IsRenderScaleEditable => IsModernEngine && !IsPathTracing && (!HasUpscalingOrSsaa || (renderScale is { } s && s != 1m));
    public bool IsShadowResolutionEditable => IsModernEngine && !IsPathTracing;
    public bool IsShadowEnabled => IsModernEngine && !IsPathTracing && ShadowResolutionIndex > 0;
    public bool IsShadowSoftnessEditable => IsShadowEnabled && CurrentShadowFilter == ShadowFilterMode.PcfSoft;
    public bool IsSsaoToggleEnabled => IsModernEngine && !IsPathTracing && (!IsRayGiOcclusionActive || SsaoEnabled);
    public bool IsSsaoEditable => IsModernEngine && !IsPathTracing && SsaoEnabled && (!IsRayGiOcclusionActive || !SsaoEnabled);
    public bool IsPtProgressiveMaxFramesEditable => IsModernEngine && IsPathTracing && PtProgressiveAccumulation;
    public bool IsPreviewOutlineEditable => IsModernEngine && PreviewOutlineEnabled;
    public bool IsPreviewOutlineDisabled => !PreviewOutlineEnabled;
    public bool IsDiffusionEditable => IsModernEngine && DiffusionEnabled;
    public string ValidationMessage => validationError?.Message ?? Text(validationMessage);
    public bool HasValidationMessage => ValidationMessage.Length > 0;
    public string StatusMessage => string.IsNullOrEmpty(statusTemplate) ? statusMessage : Format(statusTemplate, statusArgs);
    public bool HasStatusMessage => StatusMessage.Length > 0;

    // Right Sidebar: Upper Help Section
    public string HelpCardHeader => Text("Mda.Text.Preview.Help.Header");
    public string ActiveHelpTopicId => activeHelpTopicId;
    public string ActiveHelpTitle => Text(activeHelpTopicId + ".Title");
    public string ActiveHelpBody => Text(activeHelpTopicId + ".Body");

    public void SelectHelpTopic(string? topicOrControlKey)
    {
        if (string.IsNullOrWhiteSpace(topicOrControlKey))
        {
            SetActiveHelpTopicInternal(CategoryHelpTopic(selectedCategoryIndex));
            return;
        }
        SetActiveHelpTopicInternal(ResolveDynamicHelpTopic(topicOrControlKey));
    }

    public void ResetHelpToCategoryDefault() => SetActiveHelpTopicInternal(CategoryHelpTopic(selectedCategoryIndex));

    private void SetActiveHelpTopicInternal(string topicId)
    {
        if (topicId.StartsWith("help.", StringComparison.Ordinal))
            topicId = "Mda.Text.Preview.Help." + topicId["help.".Length..];
        if (StringComparer.Ordinal.Equals(activeHelpTopicId, topicId)) return;
        activeHelpTopicId = topicId;
        Changed(nameof(ActiveHelpTopicId));
        Changed(nameof(ActiveHelpTitle));
        Changed(nameof(ActiveHelpBody));
    }

    private static string CategoryHelpTopic(int categoryIndex) => categoryIndex switch
    {
        1 => "Mda.Text.Preview.Help.Cat.Antialiasing",
        2 => "Mda.Text.Preview.Help.Cat.Shadows",
        3 => "Mda.Text.Preview.Help.Cat.Gi",
        4 => "Mda.Text.Preview.Help.Cat.Post",
        5 => "Mda.Text.Preview.Help.Cat.Diagnostics",
        _ => "Mda.Text.Preview.Help.Cat.Overview"
    };

    private string ResolveDynamicHelpTopic(string key) => key switch
    {
        "aa.mode" => CurrentAntialiasing switch
        {
            null => "Mda.Text.Preview.Help.Aa.FollowScene",
            FrameAntialiasing.Off => "Mda.Text.Preview.Help.Aa.Off",
            FrameAntialiasing.Fxaa => "Mda.Text.Preview.Help.Aa.Fxaa",
            FrameAntialiasing.Smaa1X => "Mda.Text.Preview.Help.Aa.Smaa",
            FrameAntialiasing.Taa => "Mda.Text.Preview.Help.Aa.Taa",
            FrameAntialiasing.Msaa2X => "Mda.Text.Preview.Help.Aa.Msaa2x",
            FrameAntialiasing.Msaa4X => "Mda.Text.Preview.Help.Aa.Msaa4x",
            FrameAntialiasing.Msaa8X => "Mda.Text.Preview.Help.Aa.Msaa8x",
            _ => "Mda.Text.Preview.Help.Aa.Smaa"
        },
        "temporal.mode" => CurrentTemporalMode switch
        {
            ViewTemporalUpscalingMode.Fsr => "Mda.Text.Preview.Help.Temporal.Fsr",
            ViewTemporalUpscalingMode.XeSs => "Mda.Text.Preview.Help.Temporal.XeSs",
            ViewTemporalUpscalingMode.Dlss => "Mda.Text.Preview.Help.Temporal.Dlss",
            _ => "Mda.Text.Preview.Help.Temporal.Off"
        },
        "fsr1.mode" => upscalingIndex == 1 ? "Mda.Text.Preview.Help.Fsr1.On" : "Mda.Text.Preview.Help.Fsr1.Off",
        "surface.order" => SurfaceOrderIndex == 1 ? "Mda.Text.Preview.Help.SurfaceOrder.Original" : "Mda.Text.Preview.Help.SurfaceOrder.OpaqueFirst",
        "shadow.filter" => CurrentShadowFilter switch
        {
            ShadowFilterMode.Point => "Mda.Text.Preview.Help.ShadowFilter.Point",
            ShadowFilterMode.PcfSoft => "Mda.Text.Preview.Help.ShadowFilter.PcfSoft",
            _ => "Mda.Text.Preview.Help.ShadowFilter.Linear"
        },
        "aniso" => CurrentAnisotropicFiltering > 1 ? "Mda.Text.Preview.Help.Aniso.On" : "Mda.Text.Preview.Help.Aniso.Off",
        "pipeline.mode" => CurrentPipelineMode switch
        {
            RenderPipelineMode.ClusteredForward => "Mda.Text.Preview.Help.Pipeline.ClusteredForward",
            RenderPipelineMode.PathTracing => "Mda.Text.Preview.Help.Pipeline.PathTracing",
            _ => "Mda.Text.Preview.Help.Pipeline.Forward"
        },
        "pt.shading" => CurrentPtShadingMode switch
        {
            PathTracingShadingMode.Physical => "Mda.Text.Preview.Help.Pt.ShadingPhysical",
            _ => "Mda.Text.Preview.Help.Pt.ShadingToonHybrid"
        },
        "pt.denoiser" => (RayGiDenoiser)Math.Clamp(ptDenoiserIndex, 0, 2) switch
        {
            RayGiDenoiser.Spatial => "Mda.Text.Preview.Help.Pt.DenoiserSpatial",
            RayGiDenoiser.TemporalSpatial => "Mda.Text.Preview.Help.Pt.DenoiserTemporalSpatial",
            _ => "Mda.Text.Preview.Help.Pt.DenoiserOff"
        },
        "gi.mode" => CurrentGiMode switch
        {
            LightingMode.SoftwareRayTracing => "Mda.Text.Preview.Help.Gi.Software",
            LightingMode.HardwareRayTracing => "Mda.Text.Preview.Help.Gi.Hardware",
            _ => "Mda.Text.Preview.Help.Gi.Off"
        },
        "gi.comp" => GiCompositionIndex == (int)RayGiComposition.ToonPalette
            ? "Mda.Text.Preview.Help.Gi.CompToonPalette"
            : "Mda.Text.Preview.Help.Gi.CompAdditive",
        _ when key.StartsWith("help.", StringComparison.Ordinal) => "Mda.Text.Preview.Help." + key["help.".Length..],
        _ when key.StartsWith("Mda.Text.", StringComparison.Ordinal) => key,
        _ => CategoryHelpTopic(selectedCategoryIndex)
    };

    // Right Sidebar: Lower VRAM & Resource Estimate Section
    public string EstimateHeader => Text("Mda.Text.Preview.Estimate.Header");
    public string EstimateAdapterLabel => Text("Mda.Text.Preview.Estimate.AdapterLabel");
    public string EstimateViewportLabel => Text("Mda.Text.Preview.Estimate.ViewportLabel");
    public string EstimateInternalResolutionLabel => Text("Mda.Text.Preview.Estimate.InternalResolutionLabel");
    public string EstimateVramLabel => Text("Mda.Text.Preview.Estimate.VramLabel");
    public string EstimateLoadLabel => Text("Mda.Text.Preview.Estimate.LoadLabel");
    public string EstimateDisclaimer => Text("Mda.Text.Preview.Estimate.Disclaimer");

    public PreviewQualityResourceEstimate ResourceEstimate
    {
        get
        {
            var draft = TryBuildDraft(out var built, validateCompatibility: false) && built is not null ? built : baseline;
            int vw = runtime?.Statistics?.Width ?? runtime?.Width ?? 1280;
            int vh = runtime?.Statistics?.Height ?? runtime?.Height ?? 720;
            ulong? budget = runtime?.LocalVideoMemoryBudgetBytes ?? capabilities.LocalVideoMemoryBudgetBytes;
            return PreviewQualityResourceEstimator.Estimate(draft, vw, vh, budget, IsClassicEngine);
        }
    }

    public string EstimateAdapterText
    {
        get
        {
            string? name = !string.IsNullOrWhiteSpace(runtime?.AdapterName) ? runtime.AdapterName : capabilities.AdapterName;
            if (string.IsNullOrWhiteSpace(name))
                return Text("Mda.Text.Preview.Estimate.AdapterUnavailable");
            return runtime?.IsWarp == true ? Format("{0} (WARP)", name) : name;
        }
    }

    public string EstimateViewportText
    {
        get
        {
            var est = ResourceEstimate;
            return $"{est.ViewportWidth} × {est.ViewportHeight}";
        }
    }

    public string EstimateInternalResolutionText
    {
        get
        {
            var est = ResourceEstimate;
            if (IsClassicEngine)
                return $"{est.RenderWidth} × {est.RenderHeight}";
            int scalePct = (int)Math.Round(est.RenderWidth * 100.0 / Math.Max(1, est.ViewportWidth));
            return $"{est.RenderWidth} × {est.RenderHeight} ({scalePct}%)";
        }
    }

    public string EstimateVramText
    {
        get
        {
            var est = ResourceEstimate;
            if (est.AdapterBudgetMiB is { } budgetMiB && budgetMiB > 0)
            {
                double pct = Math.Min(999.9, (est.BudgetUsageRatio ?? 0) * 100.0);
                return Format("Mda.Text.Preview.Estimate.VramWithBudget", est.EstimatedRenderBufferMiB, budgetMiB, pct);
            }
            return Format("Mda.Text.Preview.Estimate.VramOnly", est.EstimatedRenderBufferMiB);
        }
    }

    public double EstimateVramProgressPercent
    {
        get
        {
            var est = ResourceEstimate;
            double ratio = est.BudgetUsageRatio ?? (est.EstimatedRenderBufferMiB / 2048.0);
            return Math.Clamp(ratio * 100.0, 0.0, 100.0);
        }
    }

    public bool IsOverVramBudget => !IsClassicEngine && ResourceEstimate.IsOverBudget;
    public string VramOverBudgetWarning => Text("Mda.Text.Preview.Estimate.OverBudgetWarning");
    public string EstimateBudgetSourceNote => ResourceEstimate.AdapterBudgetMiB is not null
        ? ""
        : Text("Mda.Text.Preview.Estimate.BudgetUnavailable");
    public bool HasEstimateBudgetUnavailableNote => IsModernEngine && ResourceEstimate.AdapterBudgetMiB is null;
    public string EstimateClassicNotice => Text("Mda.Text.Preview.Estimate.ClassicNotice");

    public PreviewPerformanceLoadTier PerformanceLoadTier => ResourceEstimate.LoadTier;
    public string PerformanceLoadBadgeText => PerformanceLoadTier switch
    {
        PreviewPerformanceLoadTier.Light => Text("Mda.Text.Preview.Estimate.LoadLight"),
        PreviewPerformanceLoadTier.Moderate => Text("Mda.Text.Preview.Estimate.LoadModerate"),
        PreviewPerformanceLoadTier.High => Text("Mda.Text.Preview.Estimate.LoadHigh"),
        _ => Text("Mda.Text.Preview.Estimate.LoadHeavy")
    };
    public bool IsLoadLight => PerformanceLoadTier == PreviewPerformanceLoadTier.Light;
    public bool IsLoadModerate => PerformanceLoadTier == PreviewPerformanceLoadTier.Moderate;
    public bool IsLoadHigh => PerformanceLoadTier == PreviewPerformanceLoadTier.High;
    public bool IsLoadHeavy => PerformanceLoadTier == PreviewPerformanceLoadTier.Heavy;

    private FrameAntialiasing? CurrentAntialiasing => antialiasingIndex >= 0 && antialiasingIndex < AntialiasingValues.Length
        ? AntialiasingValues[antialiasingIndex] : null;
    private int CurrentSpatialSamples => spatialIndex > 0 && spatialIndex <= SampleValues.Length ? SampleValues[spatialIndex - 1] : 1;
    private ShadowFilterMode CurrentShadowFilter => shadowFilterIndex >= 0 && shadowFilterIndex < ShadowFilterValues.Length
        ? ShadowFilterValues[shadowFilterIndex] : ShadowFilterMode.Linear;
    private int CurrentAnisotropicFiltering => anisotropicFilteringIndex >= 0 && anisotropicFilteringIndex < AnisotropicValues.Length
        ? AnisotropicValues[anisotropicFilteringIndex] : 1;
    private int CurrentSsaoSampleCount => ssaoSampleCountIndex >= 0 && ssaoSampleCountIndex < SsaoSampleCounts.Length
        ? SsaoSampleCounts[ssaoSampleCountIndex] : 8;
    private RenderPipelineMode CurrentPipelineMode => pipelineModeIndex >= 0 && pipelineModeIndex < PipelineModeValues.Length
        ? PipelineModeValues[pipelineModeIndex] : RenderPipelineMode.Forward;
    private PathTracingShadingMode CurrentPtShadingMode => ptShadingModeIndex >= 0 && ptShadingModeIndex < PtShadingModeValues.Length
        ? PtShadingModeValues[ptShadingModeIndex] : PathTracingShadingMode.ToonHybrid;
    private LightingMode CurrentGiMode => giIndex >= 0 && giIndex < giValues.Length ? giValues[giIndex] : LightingMode.Legacy;
    private ViewTemporalUpscalingMode CurrentTemporalMode => temporalIndex >= 0 && temporalIndex < temporalValues.Length
        ? temporalValues[temporalIndex] : ViewTemporalUpscalingMode.Off;

    private int[] CurrentTemporalQualityValues
    {
        get
        {
            IEnumerable<int> values = CurrentTemporalMode switch
            {
                ViewTemporalUpscalingMode.XeSs => (capabilities.XeSsQueries ?? []).Where(query => query.Initialized && query.Failure is null)
                    .Select(query => query.Quality == D3D12XeSsQuality.AntiAliasing ? (int)ViewXeSsQuality.NativeAa : (int)query.Quality - 100),
                ViewTemporalUpscalingMode.Dlss => (capabilities.DlssQueries ?? []).Where(query => query.Available && query.Initialized && query.Failure is null).Select(query => (int)query.Quality),
                _ => Enum.GetValues<ViewTemporalQualityPreset>().Select(value => (int)value)
            };
            var result = values.Distinct().Order().ToList();
            int baselineValue = BaselineTemporalQuality;
            if (CurrentTemporalMode == loadedTemporalBaseline.Mode && !result.Contains(baselineValue)) result.Add(baselineValue);
            return result.ToArray();
        }
    }
    private int BaselineTemporalQuality => CurrentTemporalMode switch
    { ViewTemporalUpscalingMode.XeSs => (int)loadedTemporalBaseline.XeSsQuality,
      ViewTemporalUpscalingMode.Dlss => (int)loadedTemporalBaseline.DlssQuality,
      _ => (int)loadedTemporalBaseline.QualityPreset };
    private int InitialTemporalQualityIndex => Math.Max(0, Array.IndexOf(CurrentTemporalQualityValues, BaselineTemporalQuality));
    private int InitialTemporalQualityIndexFrom(ViewQualityOptions options)
    {
        int value = options.EffectiveTemporalUpscaling.Mode switch
        {
            ViewTemporalUpscalingMode.XeSs => (int)options.EffectiveTemporalUpscaling.XeSsQuality,
            ViewTemporalUpscalingMode.Dlss => (int)options.EffectiveTemporalUpscaling.DlssQuality,
            _ => (int)options.EffectiveTemporalUpscaling.QualityPreset
        };
        return Math.Max(0, Array.IndexOf(CurrentTemporalQualityValues, value));
    }
    private bool ActualGiAvailable(LightingMode mode) => mode switch
    { LightingMode.Legacy => true, LightingMode.SoftwareRayTracing => capabilities.Gi?.SoftwareAvailable == true, _ => capabilities.Gi?.HardwareAvailable == true };
    private bool GiAvailable(LightingMode mode) => ActualGiAvailable(mode) || (isProbingCapabilities && (mode == appliedOptions.EffectiveGi.Mode || mode == baseline.EffectiveGi.Mode)) || (isProbingCapabilities && pendingUltraGiFallback && mode == LightingMode.HardwareRayTracing);
    private bool ActualTemporalAvailable(ViewTemporalUpscalingMode mode) => mode switch
    { ViewTemporalUpscalingMode.Off => true, ViewTemporalUpscalingMode.Fsr => capabilities.FsrAvailable, ViewTemporalUpscalingMode.XeSs => capabilities.XeSsAvailable, _ => capabilities.DlssAvailable };
    private bool TemporalAvailable(ViewTemporalUpscalingMode mode) => ActualTemporalAvailable(mode) || (isProbingCapabilities && (mode == appliedOptions.EffectiveTemporalUpscaling.Mode || mode == baseline.EffectiveTemporalUpscaling.Mode || mode == loadedTemporalBaseline.Mode));
    private bool ActualTemporalQualityAvailable(int value) => CurrentTemporalMode switch
    {
        ViewTemporalUpscalingMode.XeSs => (capabilities.XeSsQueries ?? []).Any(query => query.Initialized && query.Failure is null
            && (query.Quality == D3D12XeSsQuality.AntiAliasing ? (int)ViewXeSsQuality.NativeAa : (int)query.Quality - 100) == value),
        ViewTemporalUpscalingMode.Dlss => (capabilities.DlssQueries ?? []).Any(query => query.Available && query.Initialized && query.Failure is null && (int)query.Quality == value),
        _ => true
    };
    private bool TemporalQualityAvailable(int value) => ActualTemporalQualityAvailable(value)
        || (isProbingCapabilities && CurrentTemporalMode == loadedTemporalBaseline.Mode && value == BaselineTemporalQuality);
    private string TemporalQualityName(int value) => CurrentTemporalMode switch
    {
        ViewTemporalUpscalingMode.XeSs => Text(((ViewXeSsQuality)value) switch
        { ViewXeSsQuality.UltraPerformance => "极限性能", ViewXeSsQuality.Performance => "性能", ViewXeSsQuality.Balanced => "均衡", ViewXeSsQuality.Quality => "画质", ViewXeSsQuality.UltraQuality => "超画质", ViewXeSsQuality.UltraQualityPlus => "超画质+", _ => "原生抗锯齿" }),
        ViewTemporalUpscalingMode.Dlss => Text(((ViewDlssQuality)value) switch
        { ViewDlssQuality.UltraPerformance => "极限性能", ViewDlssQuality.Performance => "性能", ViewDlssQuality.Balanced => "均衡", ViewDlssQuality.Quality => "画质", ViewDlssQuality.UltraQuality => "超画质", _ => "原生抗锯齿" }),
        _ => Text(((ViewTemporalQualityPreset)value) switch
        { ViewTemporalQualityPreset.UltraPerformance => "极限性能", ViewTemporalQualityPreset.Performance => "性能", ViewTemporalQualityPreset.Balanced => "均衡", ViewTemporalQualityPreset.Quality => "画质", _ => "原生抗锯齿" })
    };
    private static string WithAvailability(string name, bool available) => available ? name : Format("{0}（不可用）", name);

    public void SetValidationMessage(string message) { validationError = null; validationMessage = message; Changed(nameof(ValidationMessage)); Changed(nameof(HasValidationMessage)); }
    private void SetStatus(string template, params object?[] args)
    {
        statusTemplate = template;
        statusArgs = args;
        statusMessage = "";
        Changed(nameof(StatusMessage));
        Changed(nameof(HasStatusMessage));
    }
    private void ClearStatusAndValidation()
    {
        validationError = null;
        validationMessage = "";
        statusTemplate = "";
        statusArgs = [];
        statusMessage = "";
        Changed(nameof(ValidationMessage));
        Changed(nameof(HasValidationMessage));
        Changed(nameof(StatusMessage));
        Changed(nameof(HasStatusMessage));
    }

    public void ApplyBuiltInPreset(BuiltInPreviewPresetKind kind)
    {
        if (IsClassicEngine) return;
        ClearStatusAndValidation();
        if (kind == BuiltInPreviewPresetKind.UltraRayTraced)
        {
            bool hwAvailable = isProbingCapabilities || capabilities.Gi?.HardwareAvailable == true;
            bool swAvailable = isProbingCapabilities || capabilities.Gi?.SoftwareAvailable == true;
            pendingUltraGiFallback = isProbingCapabilities;
            var preset = ViewQualityPresets.Create(kind, hwAvailable, swAvailable);
            LoadFromOptions(preset, resetCustomSelection: true);
            NotifyAllChanged();
            if (!isProbingCapabilities)
            {
                if (!hwAvailable && swAvailable)
                    SetStatus("Mda.Text.Preview.Preset.UltraFallbackNotice");
                else if (!hwAvailable && !swAvailable)
                    SetStatus("Mda.Text.Preview.Preset.UltraUnavailableNotice");
            }
            return;
        }
        pendingUltraGiFallback = false;
        LoadFromOptions(ViewQualityPresets.Create(kind), resetCustomSelection: true);
        NotifyAllChanged();
    }

    public bool SaveNewCustomPreset()
    {
        if (IsClassicEngine) return false;
        if (!TryCreate(out var quality) || quality is null) return false;
        try
        {
            string name = EditorPreferences.ValidateCustomPresetName(customPresetName);
            EditorPreviewCustomPreset created = persistCustomPresets
                ? EditorPreferences.SaveCustomPreviewPreset(name, quality, existingId: null, path: customPresetsPath)
                : new("custom-" + Guid.NewGuid().ToString("N"), name, quality);
            bool prevRefreshing = refreshingLocalization;
            refreshingLocalization = true;
            suppressCustomPresetLoad = true;
            try
            {
                customPresets.Add(created);
                customPresetIndex = customPresets.Count - 1;
                customPresetName = created.Name;
            }
            finally { suppressCustomPresetLoad = false; refreshingLocalization = prevRefreshing; }
            SetValidationMessage("");
            SetStatus("Mda.Text.Preview.CustomPresetSaved", created.Name);
            NotifyAllChanged();
            return true;
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            validationError = error;
            Changed(nameof(ValidationMessage));
            Changed(nameof(HasValidationMessage));
            return false;
        }
    }

    public bool OverwriteSelectedCustomPreset()
    {
        if (IsClassicEngine || !HasSelectedCustomPreset) return false;
        if (!TryCreate(out var quality) || quality is null) return false;
        try
        {
            var existing = customPresets[customPresetIndex];
            EditorPreviewCustomPreset updated = persistCustomPresets
                ? EditorPreferences.SaveCustomPreviewPreset(existing.Name, quality, existing.Id, customPresetsPath)
                : existing with { Quality = quality };
            bool prevRefreshing = refreshingLocalization;
            refreshingLocalization = true;
            suppressCustomPresetLoad = true;
            try
            {
                customPresets[customPresetIndex] = updated;
            }
            finally { suppressCustomPresetLoad = false; refreshingLocalization = prevRefreshing; }
            SetValidationMessage("");
            SetStatus("Mda.Text.Preview.CustomPresetOverwritten", updated.Name);
            NotifyAllChanged();
            return true;
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            validationError = error;
            Changed(nameof(ValidationMessage));
            Changed(nameof(HasValidationMessage));
            return false;
        }
    }

    public bool RenameSelectedCustomPreset()
    {
        if (IsClassicEngine || !HasSelectedCustomPreset) return false;
        try
        {
            string newName = EditorPreferences.ValidateCustomPresetName(customPresetName);
            var existing = customPresets[customPresetIndex];
            if (persistCustomPresets)
                _ = EditorPreferences.RenameCustomPreviewPreset(existing.Id, newName, customPresetsPath);
            bool prevRefreshing = refreshingLocalization;
            refreshingLocalization = true;
            suppressCustomPresetLoad = true;
            try
            {
                customPresets[customPresetIndex] = existing with { Name = newName };
                customPresetName = newName;
            }
            finally { suppressCustomPresetLoad = false; refreshingLocalization = prevRefreshing; }
            SetValidationMessage("");
            SetStatus("Mda.Text.Preview.CustomPresetRenamed", newName);
            NotifyAllChanged();
            return true;
        }
        catch (Exception error) when (error is ArgumentException or KeyNotFoundException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            validationError = error;
            Changed(nameof(ValidationMessage));
            Changed(nameof(HasValidationMessage));
            return false;
        }
    }

    public bool DeleteSelectedCustomPreset()
    {
        if (IsClassicEngine || !HasSelectedCustomPreset) return false;
        try
        {
            int index = customPresetIndex;
            var existing = customPresets[index];
            if (persistCustomPresets)
                _ = EditorPreferences.DeleteCustomPreviewPreset(existing.Id, customPresetsPath);
            bool prevRefreshing = refreshingLocalization;
            refreshingLocalization = true;
            suppressCustomPresetLoad = true;
            try
            {
                customPresets.RemoveAt(index);
                customPresetIndex = -1;
            }
            finally { suppressCustomPresetLoad = false; refreshingLocalization = prevRefreshing; }
            SetValidationMessage("");
            SetStatus("Mda.Text.Preview.CustomPresetDeleted", existing.Name);
            NotifyAllChanged();
            return true;
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            validationError = error;
            Changed(nameof(ValidationMessage));
            Changed(nameof(HasValidationMessage));
            return false;
        }
    }

    public bool CopyToExport()
    {
        if (!CanCopyToExport || copyToExportQuality is null) return false;
        if (!TryCreate(out var quality) || quality is null) return false;
        try
        {
            copyToExportQuality(quality);
            SetValidationMessage("");
            SetStatus("Mda.Text.Preview.CopiedToExport");
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            validationError = error;
            Changed(nameof(ValidationMessage));
            Changed(nameof(HasValidationMessage));
            return false;
        }
    }

    public bool LoadFromExport()
    {
        if (!CanLoadFromExport || getExportQuality is null) return false;
        var exportQuality = getExportQuality();
        if (exportQuality is null)
        {
            SetValidationMessage("Mda.Text.Preview.ExportLoadUnavailable");
            return false;
        }
        pendingUltraGiFallback = false;
        LoadFromOptions(exportQuality, resetCustomSelection: true);
        SetValidationMessage("");
        SetStatus("Mda.Text.Preview.LoadedFromExport");
        NotifyAllChanged();
        return true;
    }

    public void BeginCapabilitiesProbe()
    {
        isProbingCapabilities = true;
        NotifyCapabilitiesUpdated();
    }

    public void ApplyCapabilities(EditorPreviewCapabilities updatedCapabilities)
    {
        ArgumentNullException.ThrowIfNull(updatedCapabilities);
        var selectedGi = CurrentGiMode;
        if (pendingUltraGiFallback && selectedGi == LightingMode.HardwareRayTracing && updatedCapabilities.Gi?.HardwareAvailable != true)
        {
            if (updatedCapabilities.Gi?.SoftwareAvailable == true)
            {
                selectedGi = LightingMode.SoftwareRayTracing;
                SetStatus("Mda.Text.Preview.Preset.UltraFallbackNotice");
            }
            else
            {
                selectedGi = LightingMode.Legacy;
                SetStatus("Mda.Text.Preview.Preset.UltraUnavailableNotice");
            }
        }
        pendingUltraGiFallback = false;

        var selectedTemporal = CurrentTemporalMode;
        int[] priorQualities = CurrentTemporalQualityValues;
        int selectedTemporalQuality = temporalQualityIndex >= 0 && temporalQualityIndex < priorQualities.Length
            ? priorQualities[temporalQualityIndex] : BaselineTemporalQuality;
        bool selectedFsr1 = !IsTemporal && upscalingIndex == 1;

        capabilities = updatedCapabilities;
        isProbingCapabilities = false;
        giValues = BuildGiValues(updatedCapabilities, appliedOptions.EffectiveGi.Mode, selectedGi);
        giIndex = Array.IndexOf(giValues, selectedGi);
        temporalValues = BuildTemporalValues(updatedCapabilities, appliedOptions.EffectiveTemporalUpscaling.Mode, loadedTemporalBaseline.Mode, selectedTemporal);
        temporalIndex = Array.IndexOf(temporalValues, selectedTemporal);

        int[] updatedQualities = CurrentTemporalQualityValues;
        int qualityMatch = Array.IndexOf(updatedQualities, selectedTemporalQuality);
        temporalQualityIndex = qualityMatch >= 0
            ? qualityMatch
            : Math.Max(0, Array.IndexOf(updatedQualities, BaselineTemporalQuality));
        upscalingIndex = selectedFsr1 && (updatedCapabilities.SpatialUpscalingAvailable || appliedOptions.EffectiveUpscaling.Mode == ViewUpscalingMode.Fsr1 || baseline.EffectiveUpscaling.Mode == ViewUpscalingMode.Fsr1)
            ? 1 : 0;

        NotifyCapabilitiesUpdated();
    }

    private void NotifyCapabilitiesUpdated()
    {
        refreshingLocalization = true;
        try
        {
            Changed(nameof(GiModes));
            Changed(nameof(TemporalModes));
            Changed(nameof(TemporalQualities));
            Changed(nameof(UpscalingModes));
        }
        finally { refreshingLocalization = false; }

        Changed(nameof(IsProbingCapabilities));
        Changed(nameof(CapabilitiesReady));
        Changed(nameof(CapabilityHint));
        Changed(nameof(GiIndex));
        Changed(nameof(TemporalIndex));
        Changed(nameof(TemporalQualityIndex));
        Changed(nameof(UpscalingIndex));
        RefreshDerived();
    }

    public bool TryCreate(out ViewQualityOptions? result) => TryBuildDraft(out result, validateCompatibility: true);

    private bool TryBuildDraft(out ViewQualityOptions? result, bool validateCompatibility)
    {
        result = null;
        try
        {
            bool pathTracing = CurrentPipelineMode == RenderPipelineMode.PathTracing;
            if (validateCompatibility)
            {
                if (!pathTracing && (giIndex < 0 || giIndex >= giValues.Length || (!isProbingCapabilities && !GiAvailable(giValues[giIndex]))))
                    throw Error("请选择当前设备可用的 GI 模式。");
                if (!pathTracing && (temporalIndex < 0 || temporalIndex >= temporalValues.Length || (!isProbingCapabilities && !TemporalAvailable(CurrentTemporalMode))))
                    throw Error("请选择当前设备可用的时域重建模式。");
                if (!pathTracing && !IsTemporal && (UpscalingIndex < 0 || UpscalingIndex > 1 || (!isProbingCapabilities && UpscalingIndex == 1 && !capabilities.SpatialUpscalingAvailable)))
                    throw Error("请选择当前设备可用的空间超分模式。");
            }

            var gi = pathTracing
                ? RayGiOptions.Off
                : baseline.EffectiveGi with { Mode = CurrentGiMode, PreviewAcceleration = PreviewGiAcceleration };
            if (!pathTracing && CurrentGiMode != LightingMode.Legacy)
            {
                if (validateCompatibility)
                {
                    decimal samples = Number(GiSamples, 1, 1024, "每像素采样");
                    if (decimal.Truncate(samples) != samples) throw Error("{0}需要整数。", Label("每像素采样"));
                    decimal seedDec = Number(GiSeed, 0, uint.MaxValue, "Mda.Text.Preview.GiSeedLabel");
                    if (decimal.Truncate(seedDec) != seedDec) throw Error("{0}需要整数。", Label("Mda.Text.Preview.GiSeedLabel"));
                    var composition = Choice<RayGiComposition>(GiCompositionIndex, "受光方式");
                    float maxRay = FloatNumber(GiMaxRayDistance, 0.001m, 1000000m, "Mda.Text.Preview.GiMaxRayDistanceLabel", gi.MaxRayDistance);
                    float rayEps = FloatNumber(GiRayEpsilon, 0.00001m, 1000m, "Mda.Text.Preview.GiRayEpsilonLabel", gi.RayEpsilon);
                    float occStrength = FloatNumber(GiOcclusionStrength, 0m, 1m, "Mda.Text.Preview.GiOcclusionStrengthLabel", gi.OcclusionStrength);
                    float occRadius = FloatNumber(GiOcclusionRadius, 0.0001m, 1000m, "Mda.Text.Preview.GiOcclusionRadiusLabel", gi.OcclusionRadius);
                    float directScale = FloatNumber(GiDirectScale, 0m, 4m, "Mda.Text.Preview.GiDirectScaleLabel", gi.DirectScale);
                    gi = gi with
                    {
                        Strength = FloatNumber(GiStrength, 0, 100, "GI 强度", gi.Strength),
                        SamplesPerPixel = (int)samples,
                        Seed = (uint)seedDec,
                        MaxRayDistance = maxRay,
                        RayEpsilon = rayEps,
                        DirectScale = directScale,
                        OcclusionStrength = occStrength,
                        OcclusionRadius = occRadius,
                        Composition = composition,
                        Denoiser = composition == RayGiComposition.ToonPalette ? Choice<RayGiDenoiser>(GiDenoiserIndex, "GI 降噪") : RayGiDenoiser.Off,
                        ToonResponse = composition == RayGiComposition.ToonPalette ? Choice<RayGiToonResponse>(GiToonResponseIndex, "Mda.Text.Preview.GiToonResponseLabel") : RayGiToonResponse.Continuous
                    };
                }
                else
                {
                    var composition = Enum.IsDefined(typeof(RayGiComposition), GiCompositionIndex) ? (RayGiComposition)GiCompositionIndex : gi.Composition;
                    gi = gi with
                    {
                        Strength = (float)(giStrength ?? (decimal)gi.Strength),
                        SamplesPerPixel = (int)(giSamples ?? gi.SamplesPerPixel),
                        Seed = (uint)Math.Clamp(giSeed ?? gi.Seed, 0m, uint.MaxValue),
                        MaxRayDistance = (float)(giMaxRayDistance ?? (decimal)gi.MaxRayDistance),
                        RayEpsilon = (float)(giRayEpsilon ?? (decimal)gi.RayEpsilon),
                        DirectScale = (float)(giDirectScale ?? (decimal)gi.DirectScale),
                        OcclusionStrength = (float)(giOcclusionStrength ?? (decimal)gi.OcclusionStrength),
                        OcclusionRadius = (float)(giOcclusionRadius ?? (decimal)gi.OcclusionRadius),
                        Composition = composition,
                        Denoiser = composition == RayGiComposition.ToonPalette && Enum.IsDefined(typeof(RayGiDenoiser), GiDenoiserIndex) ? (RayGiDenoiser)GiDenoiserIndex : RayGiDenoiser.Off,
                        ToonResponse = composition == RayGiComposition.ToonPalette && Enum.IsDefined(typeof(RayGiToonResponse), GiToonResponseIndex) ? (RayGiToonResponse)GiToonResponseIndex : RayGiToonResponse.Continuous
                    };
                }
            }

            var temporal = pathTracing
                ? ViewTemporalUpscalingOptions.Off
                : baseline.EffectiveTemporalUpscaling with { Mode = CurrentTemporalMode };
            if (!pathTracing && IsTemporal)
            {
                int[] values = CurrentTemporalQualityValues;
                if (validateCompatibility && (TemporalQualityIndex < 0 || TemporalQualityIndex >= values.Length || (!isProbingCapabilities && !TemporalQualityAvailable(values[TemporalQualityIndex]))))
                    throw Error("请选择当前设备可用的重建画质。");
                int value = TemporalQualityIndex >= 0 && TemporalQualityIndex < values.Length ? values[TemporalQualityIndex] : BaselineTemporalQuality;
                temporal = CurrentTemporalMode switch
                { ViewTemporalUpscalingMode.XeSs => temporal with { XeSsQuality = (ViewXeSsQuality)value },
                  ViewTemporalUpscalingMode.Dlss => temporal with { DlssQuality = (ViewDlssQuality)value },
                  _ => temporal with { QualityPreset = (ViewTemporalQualityPreset)value } };
            }

            var upscaling = pathTracing
                ? ViewUpscalingOptions.Off
                : baseline.EffectiveUpscaling with { Mode = IsFsr1 ? ViewUpscalingMode.Fsr1 : ViewUpscalingMode.Off };
            if (!pathTracing && IsFsr1)
            {
                upscaling = upscaling with
                {
                    QualityPreset = validateCompatibility ? Choice<ViewFsr1QualityPreset>(Fsr1QualityIndex, "画质") : (ViewFsr1QualityPreset)Math.Clamp(Fsr1QualityIndex, 0, 3),
                    EnableRcas = RcasEnabled,
                    RcasSharpness = RcasEnabled && validateCompatibility
                        ? FloatNumber(RcasSharpness, 0, 2, "锐化衰减", upscaling.RcasSharpness)
                        : RcasEnabled ? (float)(rcasSharpness ?? (decimal)upscaling.RcasSharpness) : upscaling.RcasSharpness
                };
            }

            var sharpening = baseline.EffectiveSharpening with
            {
                Enabled = StandaloneRcasEnabled,
                Sharpness = StandaloneRcasEnabled && validateCompatibility
                    ? FloatNumber(StandaloneRcasSharpness, 0, 2, "Mda.Text.Preview.StandaloneSharpeningAmount", baseline.EffectiveSharpening.Sharpness)
                    : (float)(standaloneRcasSharpness ?? (decimal)baseline.EffectiveSharpening.Sharpness)
            };

            var diffusion = baseline.EffectiveDiffusion with
            {
                Enabled = DiffusionEnabled,
                Strength = DiffusionEnabled && validateCompatibility
                    ? FloatNumber(DiffusionStrength, 0, 1, "柔光强度", baseline.EffectiveDiffusion.Strength)
                    : (float)(diffusionStrength ?? (decimal)baseline.EffectiveDiffusion.Strength),
                Extent = DiffusionEnabled && validateCompatibility
                    ? FloatNumber(DiffusionExtent, 0, .01m, "柔光范围", baseline.EffectiveDiffusion.Extent)
                    : (float)(diffusionExtent ?? (decimal)baseline.EffectiveDiffusion.Extent)
            };

            int shadowRes = shadowResolutionIndex >= 0 && shadowResolutionIndex < ShadowResolutions.Length ? ShadowResolutions[shadowResolutionIndex] : 2048;
            var shadowFilter = CurrentShadowFilter;
            var shadowSampling = shadowFilter == ShadowFilterMode.Point ? OriginalTextureSampling.PointClamp : OriginalTextureSampling.LinearClamp;
            var shadow = new ViewShadowOptions(shadowRes, shadowSampling);
            float resolvedShadowSoftness = ShadowFilterIndex == 2 && validateCompatibility && !pathTracing && shadowRes > 0
                ? FloatNumber(ShadowSoftness, 0m, 4m, "Mda.Text.Preview.ShadowSoftnessLabel", baseline.EffectiveShadowSoftness)
                : (float)(shadowSoftness ?? (decimal)baseline.EffectiveShadowSoftness);

            var ssao = baseline.EffectiveScreenSpaceAo with
            {
                Enabled = SsaoEnabled,
                Strength = SsaoEnabled && validateCompatibility && !pathTracing
                    ? FloatNumber(SsaoStrength, 0m, 1m, "Mda.Text.Preview.SsaoStrengthLabel", baseline.EffectiveScreenSpaceAo.Strength)
                    : (float)(ssaoStrength ?? (decimal)baseline.EffectiveScreenSpaceAo.Strength),
                Radius = SsaoEnabled && validateCompatibility && !pathTracing
                    ? FloatNumber(SsaoRadius, 0.1m, 8m, "Mda.Text.Preview.SsaoRadiusLabel", baseline.EffectiveScreenSpaceAo.Radius)
                    : (float)(ssaoRadius ?? (decimal)baseline.EffectiveScreenSpaceAo.Radius),
                SampleCount = CurrentSsaoSampleCount
            };

            var pathTracingOptions = baseline.EffectivePathTracing;
            if (pathTracing || hasExplicitPathTracing)
            {
                int spp = validateCompatibility && pathTracing
                    ? WholeNumber(PtSamplesPerFrame, 1, 64, "Mda.Text.Preview.PtSamplesPerFrameLabel")
                    : Math.Clamp((int)(ptSamplesPerFrame ?? pathTracingOptions.SamplesPerFrame), 1, 64);
                int bounces = validateCompatibility && pathTracing
                    ? WholeNumber(PtMaxBounces, 1, 8, "Mda.Text.Preview.PtMaxBouncesLabel")
                    : Math.Clamp((int)(ptMaxBounces ?? pathTracingOptions.MaxBounces), 1, 8);
                int maxAccum = validateCompatibility && pathTracing && PtProgressiveAccumulation
                    ? WholeNumber(PtMaxAccumulatedFrames, 1, 4096, "Mda.Text.Preview.PtMaxAccumulatedFramesLabel")
                    : Math.Clamp((int)(ptMaxAccumulatedFrames ?? pathTracingOptions.MaxAccumulatedFrames), 1, 4096);
                int interactiveSpp = validateCompatibility && pathTracing
                    ? WholeNumber(PtInteractiveSamplesPerFrame, 1, 16, "Mda.Text.Preview.PtInteractiveSamplesLabel")
                    : Math.Clamp((int)(ptInteractiveSamplesPerFrame ?? pathTracingOptions.InteractiveSamplesPerFrame), 1, 16);
                float firefly = validateCompatibility && pathTracing
                    ? FloatNumber(PtFireflyClamp, 1m, 1000m, "Mda.Text.Preview.PtFireflyClampLabel", pathTracingOptions.FireflyClamp)
                    : Math.Clamp((float)(ptFireflyClamp ?? (decimal)pathTracingOptions.FireflyClamp), 1f, 1000f);
                var denoiser = Enum.IsDefined(typeof(RayGiDenoiser), ptDenoiserIndex) ? (RayGiDenoiser)ptDenoiserIndex : pathTracingOptions.Denoiser;
                pathTracingOptions = pathTracingOptions with
                {
                    ShadingMode = CurrentPtShadingMode,
                    SamplesPerFrame = spp,
                    MaxBounces = bounces,
                    ProgressiveAccumulation = PtProgressiveAccumulation,
                    MaxAccumulatedFrames = maxAccum,
                    InteractiveSamplesPerFrame = interactiveSpp,
                    FireflyClamp = firefly,
                    Denoiser = denoiser,
                    UseHardwareRayTracing = PtUseHardwareRayTracing
                };
            }

            float resolvedRenderScale = validateCompatibility && !pathTracing
                ? FloatNumber(RenderScale, 0.5m, 1.5m, "Mda.Text.Preview.RenderScaleLabel", baseline.RenderScale)
                : pathTracing ? 1f : (float)(renderScale ?? (decimal)baseline.RenderScale);
            float resolvedOutlineScale = PreviewOutlineEnabled && validateCompatibility
                ? FloatNumber(PreviewOutlineWidthScale, 0m, 4m, "Mda.Text.Preview.PreviewOutlineWidthScaleLabel", baseline.PreviewOutlineWidthScale)
                : (float)(previewOutlineWidthScale ?? (decimal)baseline.PreviewOutlineWidthScale);

            FrameAntialiasing? resolvedAa = pathTracing || IsTemporal
                ? FrameAntialiasing.Off
                : CurrentAntialiasing;
            int? resolvedSpatial = pathTracing || IsTemporal
                ? 1
                : SpatialIndex == 0
                    ? null
                    : SpatialIndex > 0 && SpatialIndex <= SampleValues.Length
                        ? SampleValues[SpatialIndex - 1]
                        : throw Error("请选择 SSAA 采样数。");

            var quality = baseline with
            {
                Gi = gi == baseline.EffectiveGi ? baseline.Gi : gi,
                TemporalUpscaling = temporal == baseline.EffectiveTemporalUpscaling ? baseline.TemporalUpscaling : temporal,
                Upscaling = upscaling == baseline.EffectiveUpscaling ? baseline.Upscaling : upscaling,
                Antialiasing = resolvedAa,
                SmaaQuality = SmaaIndex == 0 ? null : Choice<SmaaQuality>(SmaaIndex - 1, "SMAA 质量"),
                SpatialSamples = resolvedSpatial,
                SurfaceOrder = SurfaceOrderIndex switch { 0 => SurfaceDrawOrder.OpaqueFirst, 1 => SurfaceDrawOrder.Original, _ => throw Error("Mda.Text.SurfaceOrder.Invalid") },
                PipelineMode = CurrentPipelineMode,
                PathTracing = (hasExplicitPathTracing || pathTracing) && pathTracingOptions != baseline.EffectivePathTracing
                    ? pathTracingOptions
                    : (pathTracing && baseline.PathTracing is null ? pathTracingOptions : baseline.PathTracing),
                Diffusion = diffusion == baseline.EffectiveDiffusion ? baseline.Diffusion : diffusion,
                Sharpening = (hasExplicitSharpening || sharpening != baseline.EffectiveSharpening) ? sharpening : baseline.Sharpening,
                AnisotropicFiltering = (hasExplicitAnisotropicFiltering || CurrentAnisotropicFiltering != baseline.EffectiveAnisotropicFiltering)
                    ? CurrentAnisotropicFiltering
                    : baseline.AnisotropicFiltering,
                Shadow = (hasExplicitShadow || shadow != baseline.EffectiveShadow) ? shadow : baseline.Shadow,
                ShadowFilter = (hasExplicitShadowFilter || (baseline.ShadowFilter is { } bsf && bsf != shadowFilter) || shadowFilter == ShadowFilterMode.PcfSoft)
                    ? shadowFilter
                    : baseline.ShadowFilter,
                ShadowSoftness = (hasExplicitShadowSoftness || resolvedShadowSoftness != baseline.EffectiveShadowSoftness)
                    ? resolvedShadowSoftness
                    : baseline.ShadowSoftness,
                ScreenSpaceAo = (hasExplicitSsao || ssao != baseline.EffectiveScreenSpaceAo) ? ssao : baseline.ScreenSpaceAo,
                UseEnvironmentMipmaps = UseEnvironmentMipmaps,
                AdaptiveVramSampling = AdaptiveVramSampling,
                RenderScale = resolvedRenderScale,
                PreviewOutlineEnabled = PreviewOutlineEnabled,
                PreviewOutlineWidthScale = resolvedOutlineScale
            };

            if (validateCompatibility)
            {
                var report = ViewQualityCompatibility.Evaluate(quality);
                if (!report.IsValid)
                    throw Error(report.Issues[0].LocalizationKey);
                quality.Validate();
                SetValidationMessage("");
            }

            result = quality;
            return true;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or OverflowException)
        {
            if (validateCompatibility)
            {
                validationError = error is EditorLocalizedArgumentException ? error : Error("配置未通过检查，请调整画质选项。");
                Changed(nameof(ValidationMessage)); Changed(nameof(HasValidationMessage));
            }
            return false;
        }
    }

    private static int WholeNumber(decimal? value, int minimum, int maximum, string label)
    {
        decimal number = Number(value, minimum, maximum, label);
        if (decimal.Truncate(number) != number)
            throw Error("{0}需要整数。", Label(label));
        return (int)number;
    }

    private static decimal Number(decimal? value, decimal minimum, decimal maximum, string label) => value is { } number && number >= minimum && number <= maximum
        ? number : throw Error("{0}应为 {1}–{2}。", Label(label), minimum, maximum);
    private static float FloatNumber(decimal? value, decimal minimum, decimal maximum, string label, float original)
    {
        decimal number = Number(value, minimum, maximum, label);
        return number == (decimal)original ? original : (float)number;
    }
    private static T Choice<T>(int index, string label) where T : struct, Enum => Enum.IsDefined(typeof(T), index)
        ? (T)Enum.ToObject(typeof(T), index) : throw Error("请选择{0}。", Label(label));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (refreshingLocalization || EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Changed(name);
        RefreshDerived();
        return true;
    }
    private void RefreshDerived()
    {
        foreach (string property in DerivedProperties) Changed(property);
    }

    private static readonly string[] DerivedProperties =
    [
        nameof(IsPathTracing), nameof(IsPathTracingEditable), nameof(IsRasterPipeline), nameof(IsGiSectionEnabled),
        nameof(IsGiEnabled), nameof(IsGiToonPaletteEnabled), nameof(IsGiDenoiserEnabled),
        nameof(IsGiOcclusionStrengthEditable), nameof(IsRayGiOcclusionActive),
        nameof(IsTemporal), nameof(IsTemporalModeEditable), nameof(IsTemporalEnabled),
        nameof(IsSpatialAaEnabled), nameof(IsTaa), nameof(IsMsaa), nameof(IsSmaa),
        nameof(IsSpatialSamplesEditable), nameof(IsFsr1ModeEditable), nameof(IsFsr1), nameof(IsRcas),
        nameof(IsStandaloneRcasSharpnessEditable), nameof(IsRenderScaleEditable),
        nameof(IsShadowResolutionEditable), nameof(IsShadowEnabled), nameof(IsShadowDisabled), nameof(IsShadowSoftnessEditable),
        nameof(ShowShadowSoftnessHint), nameof(ShadowDisabledHint),
        nameof(IsSsaoToggleEnabled), nameof(IsSsaoEditable), nameof(ShowSsaoNotice), nameof(SsaoNotice),
        nameof(IsPtProgressiveMaxFramesEditable), nameof(ShowPathTracingLocksGiNotice), nameof(ShowPtHardwareFallbackNotice),
        nameof(IsPreviewOutlineEditable), nameof(IsPreviewOutlineDisabled), nameof(IsDiffusionEditable),
        nameof(HasAntialiasingConflictHint), nameof(AntialiasingConflictHint),
        nameof(HasTemporalConflictHint), nameof(TemporalConflictHint),
        nameof(HasUpscalingOrSsaa), nameof(HasRenderScaleConflictHint), nameof(RenderScaleConflictHint),
        nameof(ShowStandaloneRcasFsrNotice), nameof(StandaloneRcasFsrNotice),
        nameof(ShowGiPaletteConflictHint), nameof(HasGiOcclusionConflict), nameof(ShowGiOcclusionLockedBySsaoHint), nameof(GiOcclusionLockedBySsaoHint),
        nameof(IsPerformancePresetActive), nameof(IsBalancedPresetActive), nameof(IsHighPresetActive), nameof(IsUltraRayTracedPresetActive),
        nameof(ActivePresetSummary), nameof(OverviewConfigurationSummary),
        nameof(ResourceEstimate), nameof(EstimateAdapterText), nameof(EstimateViewportText), nameof(EstimateInternalResolutionText),
        nameof(EstimateVramText), nameof(EstimateVramProgressPercent), nameof(IsOverVramBudget),
        nameof(HasEstimateBudgetUnavailableNote), nameof(EstimateBudgetSourceNote), nameof(PerformanceLoadTier), nameof(PerformanceLoadBadgeText),
        nameof(IsLoadLight), nameof(IsLoadModerate), nameof(IsLoadHigh), nameof(IsLoadHeavy),
        nameof(ActiveHelpTitle), nameof(ActiveHelpBody)
    ];
    private void Changed(string? name) => PropertyChanged?.Invoke(this, new(name));
    private void RefreshLocalization()
    {
        foreach (var choice in SurfaceOrders) choice.RefreshLocalization();
        refreshingLocalization = true;
        try { Changed(null); }
        finally { refreshingLocalization = false; }
        foreach (string name in IndexProperties) Changed(name);
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
