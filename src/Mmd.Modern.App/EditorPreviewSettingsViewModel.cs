using System.ComponentModel;
using System.Runtime.CompilerServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>A snapshot from the active viewport owner, never from a short-lived capability probe.</summary>
public sealed record EditorPreviewRuntimeInfo(string? AdapterName, bool IsWarp, int Width, int Height,
    EditorViewportPresentationStatistics? Statistics = null)
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

        // A file's presence is installation evidence, never device-support evidence.
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
            result = result with { AdapterName = renderer.AdapterName, SpatialUpscalingAvailable = true };
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
                        queries.Add(renderer.QueryXeSsCapabilities(displayWidth, displayHeight, new()
                        {
                            Mode = D3D12TemporalUpscalingMode.XeSs, LoaderPath = xessPath, XeSsQuality = quality
                        }));
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
                        queries.Add(renderer.QueryDlssCapabilities(displayWidth, displayHeight, new()
                        {
                            Mode = D3D12TemporalUpscalingMode.Dlss, LoaderPath = dlssBridge,
                            DlssSdkDirectory = dlssDirectory, DlssQuality = quality
                        }));
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

        // The renderer's GPU owner is private. The public FSR diagnostic owns and disposes its
        // own short-lived device; it runs after the GI/XeSS/DLSS owner has been released.
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

    private static bool RecoverableProbeError(Exception error) => error is not (OperationCanceledException or OutOfMemoryException);
}

/// <summary>A detached per-view edit. The caller persists the accepted options locally, outside the project.</summary>
public sealed class EditorPreviewSettingsViewModel : INotifyPropertyChanged
{
    private static readonly int[] SampleValues = [1, 2, 4, 8, 16, 32];
    private readonly ViewQualityOptions source;
    private readonly EditorPreviewCapabilities capabilities;
    private readonly EditorPreviewRuntimeInfo? runtime;
    private readonly LightingMode[] giValues;
    private readonly ViewTemporalUpscalingMode[] temporalValues;
    private int giIndex, antialiasingIndex, smaaIndex, spatialIndex, upscalingIndex, fsr1QualityIndex, surfaceOrderIndex;
    private int temporalIndex, temporalQualityIndex, giCompositionIndex, giDenoiserIndex;
    private decimal? giStrength, giSamples, diffusionStrength, diffusionExtent, rcasSharpness;
    private bool previewGiAcceleration, diffusionEnabled, rcasEnabled, refreshingLocalization;
    private Exception? validationError;
    private string validationMessage = "";

    public EditorPreviewSettingsViewModel(ViewQualityOptions? current = null, EditorPreviewCapabilities? capabilities = null,
        EditorPreviewRuntimeInfo? runtime = null)
    {
        source = current ?? new(); source.Validate();
        this.capabilities = capabilities ?? new();
        this.runtime = runtime;
        var modes = new List<LightingMode> { LightingMode.Legacy };
        if (this.capabilities.Gi?.SoftwareAvailable == true) modes.Add(LightingMode.SoftwareRayTracing);
        if (this.capabilities.Gi?.HardwareAvailable == true) modes.Add(LightingMode.HardwareRayTracing);
        if (!modes.Contains(source.EffectiveGi.Mode)) modes.Add(source.EffectiveGi.Mode);
        giValues = modes.ToArray(); giIndex = Array.IndexOf(giValues, source.EffectiveGi.Mode);
        var temporal = new List<ViewTemporalUpscalingMode> { ViewTemporalUpscalingMode.Off };
        if (this.capabilities.FsrAvailable) temporal.Add(ViewTemporalUpscalingMode.Fsr);
        if (this.capabilities.XeSsAvailable) temporal.Add(ViewTemporalUpscalingMode.XeSs);
        if (this.capabilities.DlssAvailable) temporal.Add(ViewTemporalUpscalingMode.Dlss);
        if (!temporal.Contains(source.EffectiveTemporalUpscaling.Mode)) temporal.Add(source.EffectiveTemporalUpscaling.Mode);
        temporalValues = temporal.ToArray(); temporalIndex = Array.IndexOf(temporalValues, source.EffectiveTemporalUpscaling.Mode);
        temporalQualityIndex = InitialTemporalQualityIndex;
        antialiasingIndex = source.Antialiasing switch { null => 0, FrameAntialiasing.Off => 1, _ => 2 };
        smaaIndex = source.SmaaQuality is { } smaa ? (int)smaa + 1 : 0;
        surfaceOrderIndex = source.SurfaceOrder == SurfaceDrawOrder.OpaqueFirst ? 0 : 1;
        spatialIndex = source.SpatialSamples is { } samples ? Array.IndexOf(SampleValues, samples) + 1 : 0;
        upscalingIndex = source.EffectiveUpscaling.Mode == ViewUpscalingMode.Fsr1 ? 1 : 0;
        fsr1QualityIndex = (int)source.EffectiveUpscaling.QualityPreset;
        rcasEnabled = source.EffectiveUpscaling.EnableRcas; rcasSharpness = (decimal)source.EffectiveUpscaling.RcasSharpness;
        giStrength = (decimal)source.EffectiveGi.Strength; giSamples = source.EffectiveGi.SamplesPerPixel;
        giCompositionIndex = (int)source.EffectiveGi.Composition; giDenoiserIndex = (int)source.EffectiveGi.Denoiser;
        previewGiAcceleration = source.EffectiveGi.PreviewAcceleration;
        diffusionEnabled = source.EffectiveDiffusion.Enabled;
        diffusionStrength = (decimal)source.EffectiveDiffusion.Strength; diffusionExtent = (decimal)source.EffectiveDiffusion.Extent;
        Observe(this, static model => model.RefreshLocalization());
    }

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
    public string SamplingHint => Text("流畅编辑建议使用边缘平滑，精细采样保持 1×；提高采样会重复渲染场景。");
    public string GiLabel => Text("一次间接光照（GI）");
    public string StrengthLabel => Text("强度");
    public string GiSamplesLabel => Text("每像素采样");
    public string CompositionLabel => Text("受光方式");
    public string DenoiserLabel => Text("GI 降噪");
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
    public string LightingLabel => Text("光照");
    public string SceneAppearanceHint => Text("场景阴影与模型描边沿用工程数据；预览画质不会改写材质或画风。");
    public string QualityLabel => Text("画质");
    public string Fsr1Label => Text("空间超分");
    public string RcasLabel => Text("启用 RCAS 锐化");
    public string SharpnessLabel => Text("锐化衰减");
    public string RcasHint => Text("0 最锐利，2 最柔和。");
    public string TemporalHint => Text("时域重建启用时，使用独立历史并关闭 SMAA、SSAA 和空间超分。");
    public string CapabilityHint => string.Join(Environment.NewLine,
        new[] { Text("仅显示通过当前设备与已安装组件检查的选项。") }.Concat(capabilities.Reasons.Select(Text)));
    public string DiffusionLabel => Text("柔光");
    public string DiffusionEnabledLabel => Text("Mda.Text.1251a1baefb5");
    public string ExtentLabel => Text("范围");
    public string DiffusionHint => Text("最后处理场景画面，辅助标记保持清晰。");
    public string LocalHint => Text("仅保存在本机，不修改工程；输出画质在输出窗口单独设置。");
    public string ApplyLabel => Text("应用");
    public string CancelLabel => Text("取消");

    public IReadOnlyList<string> AntialiasingModes => [Text("沿用场景"), Text("Mda.Text.Context.OptionOff"), Text("边缘平滑（SMAA）")];
    public IReadOnlyList<string> SmaaQualities => [Text("沿用场景"), Text("低"), Text("中"), Text("高"), Text("超高")];
    public IReadOnlyList<EditorSurfaceOrderChoice> SurfaceOrders { get; } = EditorSurfaceOrderChoice.Create();
    public IReadOnlyList<string> SpatialSamples => [Text("沿用场景"), Text("1× · 关闭"), "2×", "4×", "8×", "16×", "32×"];
    public IReadOnlyList<string> GiModes => giValues.Select(mode => WithAvailability(mode switch
    { LightingMode.Legacy => Text("Mda.Text.Context.OptionOff"), LightingMode.SoftwareRayTracing => Text("软件一次 GI"), _ => Text("硬件一次 GI") }, GiAvailable(mode))).ToArray();
    public IReadOnlyList<string> GiCompositions => [Text("原色叠加"), Text("卡通色阶")];
    public IReadOnlyList<string> GiDenoisers => [Text("Mda.Text.Context.OptionOff"), Text("空间降噪"), Text("时空降噪")];
    public IReadOnlyList<string> UpscalingModes => capabilities.SpatialUpscalingAvailable || source.EffectiveUpscaling.Mode == ViewUpscalingMode.Fsr1
        ? [Text("Mda.Text.Context.OptionOff"), WithAvailability("FSR 1", capabilities.SpatialUpscalingAvailable)]
        : [Text("Mda.Text.Context.OptionOff")];
    public IReadOnlyList<string> Fsr1Qualities => [Text("超画质"), Text("画质"), Text("均衡"), Text("性能")];
    public IReadOnlyList<string> TemporalModes => temporalValues.Select(mode => WithAvailability(mode switch
    { ViewTemporalUpscalingMode.Off => Text("Mda.Text.Context.OptionOff"), ViewTemporalUpscalingMode.Fsr => "FSR", ViewTemporalUpscalingMode.XeSs => "XeSS", _ => "DLSS" }, TemporalAvailable(mode))).ToArray();
    public IReadOnlyList<string> TemporalQualities => CurrentTemporalQualityValues.Select(value => WithAvailability(TemporalQualityName(value), TemporalQualityAvailable(value))).ToArray();

    public int GiIndex { get => giIndex; set => Set(ref giIndex, value); }
    public int AntialiasingIndex { get => antialiasingIndex; set => Set(ref antialiasingIndex, value); }
    public int SmaaIndex { get => smaaIndex; set => Set(ref smaaIndex, value); }
    public int SurfaceOrderIndex { get => surfaceOrderIndex; set => Set(ref surfaceOrderIndex, value); }
    public int SpatialIndex { get => spatialIndex; set => Set(ref spatialIndex, value); }
    public int UpscalingIndex { get => upscalingIndex; set => Set(ref upscalingIndex, value); }
    public int Fsr1QualityIndex { get => fsr1QualityIndex; set => Set(ref fsr1QualityIndex, value); }
    public int TemporalIndex
    {
        get => temporalIndex;
        set
        {
            if (!Set(ref temporalIndex, value)) return;
            temporalQualityIndex = InitialTemporalQualityIndex;
            Changed(nameof(TemporalQualities)); Changed(nameof(TemporalQualityIndex));
        }
    }
    public int TemporalQualityIndex { get => temporalQualityIndex; set => Set(ref temporalQualityIndex, value); }
    public int GiCompositionIndex { get => giCompositionIndex; set => Set(ref giCompositionIndex, value); }
    public int GiDenoiserIndex { get => giDenoiserIndex; set => Set(ref giDenoiserIndex, value); }
    public decimal? GiStrength { get => giStrength; set => Set(ref giStrength, value); }
    public decimal? GiSamples { get => giSamples; set => Set(ref giSamples, value); }
    public decimal? DiffusionStrength { get => diffusionStrength; set => Set(ref diffusionStrength, value); }
    public decimal? DiffusionExtent { get => diffusionExtent; set => Set(ref diffusionExtent, value); }
    public decimal? RcasSharpness { get => rcasSharpness; set => Set(ref rcasSharpness, value); }
    public bool PreviewGiAcceleration { get => previewGiAcceleration; set => Set(ref previewGiAcceleration, value); }
    public bool DiffusionEnabled { get => diffusionEnabled; set => Set(ref diffusionEnabled, value); }
    public bool RcasEnabled { get => rcasEnabled; set => Set(ref rcasEnabled, value); }
    public bool IsGiEnabled => giIndex >= 0 && giIndex < giValues.Length && giValues[giIndex] != LightingMode.Legacy;
    public bool IsGiDenoiserEnabled => IsGiEnabled && GiCompositionIndex == (int)RayGiComposition.ToonPalette;
    public bool IsTemporal => CurrentTemporalMode != ViewTemporalUpscalingMode.Off;
    public bool IsSpatialAaEnabled => !IsTemporal;
    public bool IsSmaa => !IsTemporal && AntialiasingIndex == 2;
    public bool IsFsr1 => !IsTemporal && UpscalingIndex == 1;
    public bool IsRcas => IsFsr1 && RcasEnabled;
    public string ValidationMessage => validationError?.Message ?? Text(validationMessage);
    public bool HasValidationMessage => ValidationMessage.Length > 0;

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
            // A previously saved unsupported selection stays visible, but Apply requires a supported replacement.
            int sourceValue = SourceTemporalQuality;
            if (CurrentTemporalMode == source.EffectiveTemporalUpscaling.Mode && !result.Contains(sourceValue)) result.Add(sourceValue);
            return result.ToArray();
        }
    }
    private int SourceTemporalQuality => CurrentTemporalMode switch
    { ViewTemporalUpscalingMode.XeSs => (int)source.EffectiveTemporalUpscaling.XeSsQuality,
      ViewTemporalUpscalingMode.Dlss => (int)source.EffectiveTemporalUpscaling.DlssQuality,
      _ => (int)source.EffectiveTemporalUpscaling.QualityPreset };
    private int InitialTemporalQualityIndex => Math.Max(0, Array.IndexOf(CurrentTemporalQualityValues, SourceTemporalQuality));
    private bool GiAvailable(LightingMode mode) => mode switch
    { LightingMode.Legacy => true, LightingMode.SoftwareRayTracing => capabilities.Gi?.SoftwareAvailable == true, _ => capabilities.Gi?.HardwareAvailable == true };
    private bool TemporalAvailable(ViewTemporalUpscalingMode mode) => mode switch
    { ViewTemporalUpscalingMode.Off => true, ViewTemporalUpscalingMode.Fsr => capabilities.FsrAvailable, ViewTemporalUpscalingMode.XeSs => capabilities.XeSsAvailable, _ => capabilities.DlssAvailable };
    private bool TemporalQualityAvailable(int value) => CurrentTemporalMode switch
    {
        ViewTemporalUpscalingMode.XeSs => (capabilities.XeSsQueries ?? []).Any(query => query.Initialized && query.Failure is null
            && (query.Quality == D3D12XeSsQuality.AntiAliasing ? (int)ViewXeSsQuality.NativeAa : (int)query.Quality - 100) == value),
        ViewTemporalUpscalingMode.Dlss => (capabilities.DlssQueries ?? []).Any(query => query.Available && query.Initialized && query.Failure is null && (int)query.Quality == value),
        _ => true
    };
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

    public bool TryCreate(out ViewQualityOptions? result)
    {
        result = null;
        try
        {
            if (giIndex < 0 || giIndex >= giValues.Length || !GiAvailable(giValues[giIndex])) throw Error("请选择当前设备可用的 GI 模式。");
            if (temporalIndex < 0 || temporalIndex >= temporalValues.Length || !TemporalAvailable(CurrentTemporalMode)) throw Error("请选择当前设备可用的时域重建模式。");
            if (!IsTemporal && (UpscalingIndex < 0 || UpscalingIndex > 1 || UpscalingIndex == 1 && !capabilities.SpatialUpscalingAvailable)) throw Error("请选择当前设备可用的空间超分模式。");
            var gi = source.EffectiveGi with { Mode = giValues[giIndex], PreviewAcceleration = PreviewGiAcceleration };
            if (IsGiEnabled)
            {
                decimal samples = Number(GiSamples, 1, 1024, "每像素采样");
                if (decimal.Truncate(samples) != samples) throw Error("{0}需要整数。", Label("每像素采样"));
                var composition = Choice<RayGiComposition>(GiCompositionIndex, "受光方式");
                gi = gi with { Strength = FloatNumber(GiStrength, 0, 100, "GI 强度", gi.Strength), SamplesPerPixel = (int)samples,
                    Composition = composition,
                    Denoiser = composition == RayGiComposition.ToonPalette ? Choice<RayGiDenoiser>(GiDenoiserIndex, "GI 降噪") : RayGiDenoiser.Off,
                    ToonResponse = composition == RayGiComposition.ToonPalette ? gi.ToonResponse : RayGiToonResponse.Continuous };
            }
            var temporal = source.EffectiveTemporalUpscaling with { Mode = CurrentTemporalMode };
            if (IsTemporal)
            {
                int[] values = CurrentTemporalQualityValues;
                if (TemporalQualityIndex < 0 || TemporalQualityIndex >= values.Length || !TemporalQualityAvailable(values[TemporalQualityIndex])) throw Error("请选择当前设备可用的重建画质。");
                int value = values[TemporalQualityIndex];
                temporal = CurrentTemporalMode switch
                { ViewTemporalUpscalingMode.XeSs => temporal with { XeSsQuality = (ViewXeSsQuality)value },
                  ViewTemporalUpscalingMode.Dlss => temporal with { DlssQuality = (ViewDlssQuality)value },
                  _ => temporal with { QualityPreset = (ViewTemporalQualityPreset)value } };
            }
            var upscaling = source.EffectiveUpscaling with { Mode = IsFsr1 ? ViewUpscalingMode.Fsr1 : ViewUpscalingMode.Off };
            if (IsFsr1) upscaling = upscaling with { QualityPreset = Choice<ViewFsr1QualityPreset>(Fsr1QualityIndex, "画质"), EnableRcas = RcasEnabled,
                RcasSharpness = RcasEnabled ? FloatNumber(RcasSharpness, 0, 2, "锐化衰减", upscaling.RcasSharpness) : upscaling.RcasSharpness };
            var diffusion = source.EffectiveDiffusion with { Enabled = DiffusionEnabled,
                Strength = DiffusionEnabled ? FloatNumber(DiffusionStrength, 0, 1, "柔光强度", source.EffectiveDiffusion.Strength) : source.EffectiveDiffusion.Strength,
                Extent = DiffusionEnabled ? FloatNumber(DiffusionExtent, 0, .01m, "柔光范围", source.EffectiveDiffusion.Extent) : source.EffectiveDiffusion.Extent };
            var quality = source with
            {
                // Preserve absent overrides and untouched float values exactly, including filter-only edits.
                Gi = gi == source.EffectiveGi ? source.Gi : gi,
                TemporalUpscaling = temporal == source.EffectiveTemporalUpscaling ? source.TemporalUpscaling : temporal,
                Upscaling = upscaling == source.EffectiveUpscaling ? source.Upscaling : upscaling,
                Antialiasing = IsTemporal ? FrameAntialiasing.Off : AntialiasingIndex switch { 0 => null, 1 => FrameAntialiasing.Off, 2 => FrameAntialiasing.Smaa1X, _ => throw Error("请选择抗锯齿模式。") },
                SmaaQuality = SmaaIndex == 0 ? null : Choice<SmaaQuality>(SmaaIndex - 1, "SMAA 质量"),
                SpatialSamples = IsTemporal ? 1 : SpatialIndex == 0 ? null : SpatialIndex > 0 && SpatialIndex <= SampleValues.Length ? SampleValues[SpatialIndex - 1] : throw Error("请选择 SSAA 采样数。"),
                SurfaceOrder = SurfaceOrderIndex switch { 0 => SurfaceDrawOrder.OpaqueFirst, 1 => SurfaceDrawOrder.Original, _ => throw Error("Mda.Text.SurfaceOrder.Invalid") },
                Diffusion = diffusion == source.EffectiveDiffusion ? source.Diffusion : diffusion
            };
            quality.Validate(); result = quality; SetValidationMessage(""); return true;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or OverflowException)
        {
            validationError = error is EditorLocalizedArgumentException ? error : Error("配置未通过检查，请调整画质选项。");
            Changed(nameof(ValidationMessage)); Changed(nameof(HasValidationMessage)); return false;
        }
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
        foreach (string property in DerivedProperties) Changed(property);
        return true;
    }
    private static readonly string[] DerivedProperties = [nameof(IsGiEnabled), nameof(IsGiDenoiserEnabled), nameof(IsTemporal), nameof(IsSpatialAaEnabled), nameof(IsSmaa), nameof(IsFsr1), nameof(IsRcas)];
    private void Changed(string? name) => PropertyChanged?.Invoke(this, new(name));
    private void RefreshLocalization()
    {
        foreach (var choice in SurfaceOrders) choice.RefreshLocalization();
        refreshingLocalization = true;
        try { Changed(null); }
        finally { refreshingLocalization = false; }
        foreach (string name in new[] { nameof(GiIndex), nameof(AntialiasingIndex), nameof(SmaaIndex), nameof(SpatialIndex), nameof(SurfaceOrderIndex), nameof(UpscalingIndex), nameof(Fsr1QualityIndex), nameof(TemporalIndex), nameof(TemporalQualityIndex), nameof(GiCompositionIndex), nameof(GiDenoiserIndex) }) Changed(name);
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
