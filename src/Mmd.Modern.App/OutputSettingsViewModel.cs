using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.FFmpeg;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

public enum OutputMediaKind { PngImage, PngSequence, Video }

/// <summary>A detached host configuration. Applying it does not start an export or change the live scene.</summary>
public sealed record OutputSettingsDraft(ViewOutputPresetDefinition Preset, ExportRequest Request,
    OutputMediaKind MediaKind, string Destination, bool TransparentBackground, Vector4 BackgroundColor,
    FfmpegOutputOptions? VideoOptions, bool EndFrameFollowsAnimation = false)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Preset);
        ArgumentNullException.ThrowIfNull(Request);
        Request.Validate();
        if (!Enum.IsDefined(MediaKind)) throw Error("请选择输出类型。");
        ArgumentException.ThrowIfNullOrWhiteSpace(Destination);
        _ = Path.GetFullPath(Destination);
        if (MediaKind == OutputMediaKind.PngImage && Request.Plan.FrameCount != 1)
            throw Error("单张图片只能包含一帧。");
        if (MediaKind == OutputMediaKind.PngImage && !Path.GetExtension(Destination).Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw Error("图片路径需要以 .png 结尾。");
        if (MediaKind == OutputMediaKind.Video)
        {
            if (TransparentBackground) throw Error("当前视频格式不保留透明度，请使用 PNG 图片或序列。");
            if (VideoOptions is null) throw Error("请设置视频编码参数。");
            if (!string.Equals(VideoOptions.OutputPath, Destination, StringComparison.Ordinal))
                throw Error("视频编码路径与输出路径不同。");
            VideoOptions.Validate(Request.Plan);
        }
        if (TransparentBackground && ((Request.Quality?.EffectiveUpscaling.Mode ?? ViewUpscalingMode.Off) != ViewUpscalingMode.Off
            || (Request.Quality?.EffectiveTemporalUpscaling.Mode ?? ViewTemporalUpscalingMode.Off) != ViewTemporalUpscalingMode.Off))
            throw Error("透明图片请关闭超分与时域重建；可使用 SMAA 和 SSAA。");
        if (TransparentBackground && Request.Quality?.Antialiasing == FrameAntialiasing.Taa)
            throw Error("Mda.Text.Output.TransparentRejectsTaa");
        OriginalFrameMath.Validate(new(OriginalAlphaBlendMode.Normal, true, false,
            BackgroundColor: BackgroundColor, AlphaOutput: FrameAlphaOutput.Straight));
        if (BackgroundColor.W != (TransparentBackground ? 0 : 1))
            throw Error("背景透明度与输出设置不一致。");
    }
}

/// <summary>Editable presentation copy, validated by the existing ViewOutput and FFmpeg contracts.</summary>
public sealed class OutputSettingsViewModel : INotifyPropertyChanged
{
    private readonly ViewOutputPresetDefinition _sourcePreset;
    private readonly ViewQualityOptions _sourceQuality;
    private readonly FfmpegOutputOptions _sourceEncoding;
    private readonly Vector4 _sourceBackground;
    private readonly decimal _initialFramesPerSecond;
    private readonly bool _hasPaletteReceiver;
    private readonly bool _supportsCaptureMemory;
    private readonly FrameAntialiasing? _preservedAntialiasing;
    private int _captureMemoryIndex;
    private int _mediaKindIndex = 2, _encoderIndex = 2, _rateControlIndex, _speedIndex = 1, _profileIndex, _levelIndex;
    private int _antialiasingIndex, _smaaIndex, _spatialIndex, _surfaceOrderIndex;
    private int _giIndex, _giCompositionIndex, _giDenoiserIndex;
    private decimal? _giStrength, _giSamples;
    private EditorPreviewCapabilities _giCapabilities = new();
    private bool _giCapabilitiesReady;
    private decimal? _width = 1920, _height = 1080, _framesPerSecond = 30;
    private decimal? _startFrame, _endFrame, _preRoll, _quality = 23, _threads = 2, _timeoutSeconds = 120;
    private bool _endFrameFollowsAnimation;
    private decimal? _targetBitrate = 12_000_000, _maximumBitrate = 20_000_000, _bufferSize = 40_000_000, _keyframeInterval = 60;
    private decimal? _diffusionStrength = .7m, _diffusionExtent = .002m;
    private bool _transparentBackground, _limitBitrate, _customKeyframeInterval, _diffusionEnabled;
    private string _destination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MDA-output.mp4");
    private string _validationMessage = string.Empty;
    private Exception? _validationError;

    private static readonly VideoEncoder[] EncoderValues = [VideoEncoder.RawVideo, VideoEncoder.H264Software,
        VideoEncoder.HevcSoftware, VideoEncoder.Av1AomSoftware, VideoEncoder.Av1SvtSoftware,
        VideoEncoder.H264Nvenc, VideoEncoder.HevcNvenc, VideoEncoder.Av1Nvenc];
    private static readonly int[] SpatialValues = [1, 2, 4, 8, 16, 32];
    private static readonly LightingMode[] GiValues = [LightingMode.Legacy, LightingMode.SoftwareRayTracing, LightingMode.HardwareRayTracing];

    public OutputSettingsViewModel(OutputSettingsDraft? current = null, uint currentFrame = 0, uint lastKeyframe = 0,
        bool hasPaletteReceiver = false, ViewQualityOptions? defaultQuality = null, bool supportsCaptureMemory = true)
    {
        _hasPaletteReceiver = hasPaletteReceiver;
        _supportsCaptureMemory = supportsCaptureMemory;
        _sourcePreset = current?.Preset ?? new("local.editor-output", Text("编辑器输出"), new(1280, 720),
            new(1920, 1080, new(30), defaultQuality ?? new(Antialiasing: FrameAntialiasing.Off, SpatialSamples: 1)));
        _sourceQuality = _sourcePreset.Export.Quality ?? new();
        _sourceBackground = current?.BackgroundColor ?? Vector4.One;
        _sourceEncoding = current?.VideoOptions ?? (_sourcePreset.Encoding is { } encoding
            ? FfmpegEncodingPreset.ToOptions(encoding, EncodingValidationPath(encoding),
                _sourcePreset.Export.CreateRequest(0, 0).Plan)
            : new("MDA-output.mp4") { BackgroundRed = 255, BackgroundGreen = 255, BackgroundBlue = 255 });
        var export = _sourcePreset.Export;
        _captureMemoryIndex = supportsCaptureMemory ? (int)(current?.Request.CaptureMemory ?? export.CaptureMemory) : 0;
        _width = export.Width; _height = export.Height;
        decimal sourceFps = (decimal)export.FrameRate.Numerator / export.FrameRate.Denominator;
        _initialFramesPerSecond = decimal.Round(sourceFps, sourceFps < .001m ? 6 : 3);
        _framesPerSecond = _initialFramesPerSecond;
        // A new animation export covers its authored range, independently of the editor cursor.
        _startFrame = current?.Request.Plan.StartFrame ?? 0u;
        _endFrameFollowsAnimation = current?.EndFrameFollowsAnimation ?? true;
        _endFrame = _endFrameFollowsAnimation ? lastKeyframe : current!.Request.Plan.EndFrame;
        _preRoll = current?.Request.Plan.PhysicsPreRollFrames ?? 0;
        if (current is not null)
        {
            current.Validate();
            _mediaKindIndex = (int)current.MediaKind; _destination = current.Destination;
            _transparentBackground = current.TransparentBackground;
        }
        _encoderIndex = Array.IndexOf(EncoderValues, _sourceEncoding.Encoder);
        _rateControlIndex = (int)_sourceEncoding.RateControl; _speedIndex = (int)_sourceEncoding.Speed;
        _profileIndex = (int)_sourceEncoding.Profile; _levelIndex = (int)_sourceEncoding.Level;
        _quality = _sourceEncoding.Quality; _threads = _sourceEncoding.Threads;
        _timeoutSeconds = (decimal)_sourceEncoding.Timeout.TotalSeconds;
        _targetBitrate = _sourceEncoding.TargetBitrate ?? 12_000_000;
        _maximumBitrate = _sourceEncoding.MaximumBitrate ?? 20_000_000;
        _bufferSize = _sourceEncoding.BufferSize ?? 40_000_000;
        _limitBitrate = _sourceEncoding.MaximumBitrate.HasValue;
        _customKeyframeInterval = _sourceEncoding.KeyframeInterval.HasValue;
        _keyframeInterval = _sourceEncoding.KeyframeInterval ?? 60;
        if (current is null && _encoderIndex == 0) _destination = Path.ChangeExtension(_destination, ".avi");
        _preservedAntialiasing = _sourceQuality.Antialiasing is { } sourceAa
            && sourceAa is not (FrameAntialiasing.Off or FrameAntialiasing.Smaa1X)
            ? sourceAa
            : null;
        var aaModes = new List<OutputSettingsChoice>
        {
            new(() => Text("沿用场景")),
            new(() => Text("Mda.Text.Context.OptionOff")),
            new(() => "SMAA"),
        };
        if (_preservedAntialiasing is { } preservedAa)
            aaModes.Add(new(() => PreservedAntialiasingLabel(preservedAa)));
        AntialiasingModes = aaModes.AsReadOnly();
        _antialiasingIndex = _sourceQuality.Antialiasing switch
        {
            null => 0,
            FrameAntialiasing.Off => 1,
            FrameAntialiasing.Smaa1X => 2,
            _ => 3
        };
        _smaaIndex = _sourceQuality.SmaaQuality is { } smaa ? (int)smaa + 1 : 0;
        _surfaceOrderIndex = _sourceQuality.SurfaceOrder == SurfaceDrawOrder.OpaqueFirst ? 0 : 1;
        _spatialIndex = _sourceQuality.SpatialSamples is { } samples ? Array.IndexOf(SpatialValues, samples) + 1 : 0;
        _diffusionEnabled = _sourceQuality.EffectiveDiffusion.Enabled;
        _diffusionStrength = (decimal)_sourceQuality.EffectiveDiffusion.Strength;
        _diffusionExtent = (decimal)_sourceQuality.EffectiveDiffusion.Extent;
        var gi = _sourceQuality.EffectiveGi;
        _giIndex = Array.IndexOf(GiValues, gi.Mode);
        _giStrength = (decimal)gi.Strength; _giSamples = gi.SamplesPerPixel;
        _giCompositionIndex = (int)gi.Composition; _giDenoiserIndex = (int)gi.Denoiser;
        GiModes = GiValues.Select(mode => new OutputSettingsChoice(() => GiModeChoiceLabel(mode))).ToArray();
        Observe(this, static model => model.RefreshLocalization());
    }

    // Portable encoding data deliberately has no destination. Probe only its enum to choose the validation suffix.
    private static string EncodingValidationPath(ViewOutputEncodingPreset encoding)
    {
        using var json = System.Text.Json.JsonDocument.Parse(encoding.DataJson);
        return json.RootElement.TryGetProperty("encoder", out var encoder) && encoder.GetString() == nameof(VideoEncoder.RawVideo)
            ? "MDA-output.avi" : "MDA-output.mp4";
    }

    public IReadOnlyList<OutputSettingsChoice> MediaKinds { get; } = [new(() => Text("PNG 图片")), new(() => Text("PNG 图片序列")), new(() => Text("视频"))];
    public IReadOnlyList<OutputSettingsChoice> Encoders { get; } = [new(() => Text("Raw · 无压缩")), new(() => Text("H.264 · 软件")), new(() => Text("HEVC · 软件")), new(() => Text("AV1 · AOM 软件")), new(() => Text("AV1 · SVT 软件")), new(() => "H.264 · NVENC"), new(() => "HEVC · NVENC"), new(() => "AV1 · NVENC")];
    public IReadOnlyList<OutputSettingsChoice> RateControls { get; } = [new(() => Text("恒定画质")), new(() => Text("目标码率"))];
    public IReadOnlyList<OutputSettingsChoice> Speeds { get; } = [new(() => Text("快速")), new(() => Text("均衡")), new(() => Text("慢速"))];
    public IReadOnlyList<OutputSettingsChoice> Profiles { get; } = [new(() => Text("自动")), new(() => "H.264 Main"), new(() => "H.264 High"), new(() => "HEVC Main"), new(() => "AV1 Main")];
    public IReadOnlyList<OutputSettingsChoice> Levels { get; } = [new(() => Text("自动")), new(() => "H.264 · 4.1")];
    public IReadOnlyList<OutputSettingsChoice> AntialiasingModes { get; }
    public IReadOnlyList<OutputSettingsChoice> SmaaQualities { get; } = [new(() => Text("沿用场景")), new(() => Text("低")), new(() => Text("中")), new(() => Text("高")), new(() => Text("超高"))];
    public string SurfaceOrderLabel => Text("Mda.Text.SurfaceOrder.Label");
    public bool SupportsCaptureMemory => _supportsCaptureMemory;
    public string CaptureMemoryLabel => Text("Mda.Text.CaptureMemory.Label");
    public IReadOnlyList<EditorCaptureMemoryChoice> CaptureMemoryChoices { get; } = EditorCaptureMemoryChoice.Create();
    public string CaptureMemoryHint => Text(!SupportsCaptureMemory ? "Mda.Text.CaptureMemory.ModernOnly" : CaptureMemoryIndex switch
    {
        1 => "Mda.Text.CaptureMemory.SpeedHint",
        2 => "Mda.Text.CaptureMemory.LowMemoryHint",
        _ => "Mda.Text.CaptureMemory.AutoHint"
    });
    public IReadOnlyList<EditorSurfaceOrderChoice> SurfaceOrders { get; } = EditorSurfaceOrderChoice.Create();
    public IReadOnlyList<OutputSettingsChoice> SpatialSamples { get; } = [new(() => Text("沿用场景")), new(() => Text("1× · 关闭")), new(() => "2×"), new(() => "4×"), new(() => "8×"), new(() => "16×"), new(() => "32×")];
    public string GiLabel => Text("一次间接光照（GI）");
    public string GiModeLabel => Text("模式");
    public string GiStrengthLabel => Text("强度");
    public string GiSamplesLabel => Text("Mda.Text.OutputGi.SamplesPerPixel");
    public string GiCompositionLabel => Text("受光方式");
    public string GiDenoiserLabel => Text("GI 降噪");
    public string GiHint => Text("Mda.Text.OutputGi.Independent");
    public string GiPaletteNotice => Text("Mda.Text.OutputGi.PaletteNotice");
    public string GiCompositionScope => Text("Mda.Text.OutputGi.CompositionScope");
    public bool ShowGiPaletteNotice => _hasPaletteReceiver && IsGiEnabled && GiStrength is > 0
        && GiCompositionIndex == (int)RayGiComposition.Additive;
    public string GiCapabilityStatus => !_giCapabilitiesReady ? Text("Mda.Text.OutputGi.Checking")
        : _giCapabilities.Reasons.Count != 0 ? string.Join(Environment.NewLine, _giCapabilities.Reasons.Select(Text))
        : _giCapabilities.Gi?.HardwareAvailable == true ? Text("Mda.Text.OutputGi.Ready")
        : Text(_giCapabilities.Gi?.SoftwareAvailable == true
            ? "硬件 GI：当前设备未通过 DXR 检查，可使用软件 GI。" : "GI：设备查询未完成，当前未开放。");
    public bool GiCapabilitiesReady => _giCapabilitiesReady;
    public IReadOnlyList<OutputSettingsChoice> GiModes { get; }
    public IReadOnlyList<OutputSettingsChoice> GiCompositions { get; } = [new(() => Text("原色叠加")), new(() => Text("卡通色阶"))];
    public IReadOnlyList<OutputSettingsChoice> GiDenoisers { get; } = [new(() => Text("Mda.Text.Context.OptionOff")), new(() => Text("空间降噪")), new(() => Text("时空降噪"))];

    private static string PreservedAntialiasingLabel(FrameAntialiasing aa) => aa switch
    {
        FrameAntialiasing.Fxaa => Text("Mda.Text.Preview.AaFxaa"),
        FrameAntialiasing.Taa => Text("Mda.Text.Preview.AaTaa"),
        FrameAntialiasing.Msaa2X => Text("Mda.Text.Preview.AaMsaa2x"),
        FrameAntialiasing.Msaa4X => Text("Mda.Text.Preview.AaMsaa4x"),
        FrameAntialiasing.Msaa8X => Text("Mda.Text.Preview.AaMsaa8x"),
        _ => aa.ToString()
    };

    private string GiModeChoiceLabel(LightingMode mode)
    {
        string label = mode switch { LightingMode.Legacy => Text("Mda.Text.Context.OptionOff"),
            LightingMode.SoftwareRayTracing => Text("软件一次 GI"), _ => Text("硬件一次 GI") };
        return mode == LightingMode.Legacy || !_giCapabilitiesReady || GiAvailable(mode)
            ? label : Format("{0}（不可用）", label);
    }

    public void ApplyGiCapabilities(EditorPreviewCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        _giCapabilities = capabilities; _giCapabilitiesReady = true;
        // Availability updates only the labels; the user's pending choice still goes through validation.
        foreach (var choice in GiModes) choice.RefreshLabel();
        PropertyChanged?.Invoke(this, new(nameof(GiCapabilityStatus)));
        PropertyChanged?.Invoke(this, new(nameof(GiCapabilitiesReady)));
    }

    private bool GiAvailable(LightingMode mode) => mode switch
    {
        LightingMode.Legacy => true,
        LightingMode.SoftwareRayTracing => _giCapabilities.Gi?.SoftwareAvailable == true,
        _ => _giCapabilities.Gi?.HardwareAvailable == true
    };

    public int MediaKindIndex
    {
        get => _mediaKindIndex;
        set
        {
            if (!Set(ref _mediaKindIndex, value)) return;
            if (value == 2) TransparentBackground = false;
            UpdateDestinationExtension();
        }
    }
    public int EncoderIndex { get => _encoderIndex; set { if (Set(ref _encoderIndex, value)) UpdateDestinationExtension(); } }
    public int RateControlIndex { get => _rateControlIndex; set => Set(ref _rateControlIndex, value); }
    public int SpeedIndex { get => _speedIndex; set => Set(ref _speedIndex, value); }
    public int ProfileIndex { get => _profileIndex; set => Set(ref _profileIndex, value); }
    public int LevelIndex { get => _levelIndex; set => Set(ref _levelIndex, value); }
    public int AntialiasingIndex { get => _antialiasingIndex; set => Set(ref _antialiasingIndex, value); }
    public int SmaaIndex { get => _smaaIndex; set => Set(ref _smaaIndex, value); }
    public int SurfaceOrderIndex { get => _surfaceOrderIndex; set => Set(ref _surfaceOrderIndex, value); }
    public int CaptureMemoryIndex { get => _captureMemoryIndex; set => Set(ref _captureMemoryIndex, value); }
    public int SpatialIndex { get => _spatialIndex; set => Set(ref _spatialIndex, value); }
    public int GiIndex { get => _giIndex; set => Set(ref _giIndex, value); }
    public decimal? GiStrength { get => _giStrength; set => Set(ref _giStrength, value); }
    public decimal? GiSamples { get => _giSamples; set => Set(ref _giSamples, value); }
    public int GiCompositionIndex { get => _giCompositionIndex; set => Set(ref _giCompositionIndex, value); }
    public int GiDenoiserIndex { get => _giDenoiserIndex; set => Set(ref _giDenoiserIndex, value); }
    public bool IsGiEnabled => _giIndex > 0 && _giIndex < GiValues.Length;
    public bool IsGiDenoiserEnabled => IsGiEnabled && GiCompositionIndex == (int)RayGiComposition.ToonPalette;
    public decimal? Width { get => _width; set => Set(ref _width, value); }
    public decimal? Height { get => _height; set => Set(ref _height, value); }
    public decimal? FramesPerSecond { get => _framesPerSecond; set => Set(ref _framesPerSecond, value); }
    public decimal? StartFrame { get => _startFrame; set => Set(ref _startFrame, value); }
    public decimal? EndFrame
    {
        get => _endFrame;
        set { if (Set(ref _endFrame, value)) _endFrameFollowsAnimation = false; }
    }
    public decimal? PreRoll { get => _preRoll; set => Set(ref _preRoll, value); }
    public decimal? Quality { get => _quality; set => Set(ref _quality, value); }
    public decimal? Threads { get => _threads; set => Set(ref _threads, value); }
    public decimal? TimeoutSeconds { get => _timeoutSeconds; set => Set(ref _timeoutSeconds, value); }
    public decimal? TargetBitrate { get => _targetBitrate; set => Set(ref _targetBitrate, value); }
    public decimal? MaximumBitrate { get => _maximumBitrate; set => Set(ref _maximumBitrate, value); }
    public decimal? BufferSize { get => _bufferSize; set => Set(ref _bufferSize, value); }
    public string MaximumBitrateHint => FormatBitrateHint(MaximumBitrate);
    public string BufferSizeHint => FormatBufferSizeHint(BufferSize);
    public decimal? KeyframeInterval { get => _keyframeInterval; set => Set(ref _keyframeInterval, value); }
    public decimal? DiffusionStrength { get => _diffusionStrength; set => Set(ref _diffusionStrength, value); }
    public decimal? DiffusionExtent { get => _diffusionExtent; set => Set(ref _diffusionExtent, value); }
    public bool TransparentBackground { get => _transparentBackground; set => Set(ref _transparentBackground, value); }
    public bool LimitBitrate { get => _limitBitrate; set => Set(ref _limitBitrate, value); }
    public bool CustomKeyframeInterval { get => _customKeyframeInterval; set => Set(ref _customKeyframeInterval, value); }
    public bool DiffusionEnabled { get => _diffusionEnabled; set => Set(ref _diffusionEnabled, value); }
    public string Destination { get => _destination; set => Set(ref _destination, value); }
    public string ValidationMessage
    {
        get => Text(_validationError is { } error ? FriendlyError(error) : _validationMessage);
        private set
        {
            bool hadError = _validationError is not null; _validationError = null;
            if (!Set(ref _validationMessage, value) && hadError)
            { PropertyChanged?.Invoke(this, new(nameof(ValidationMessage))); PropertyChanged?.Invoke(this, new(nameof(HasValidationMessage))); }
        }
    }
    public bool HasValidationMessage => !string.IsNullOrEmpty(ValidationMessage);
    public bool IsVideo => MediaKindIndex == 2;
    public bool IsImage => !IsVideo;
    public bool IsSequence => MediaKindIndex == 1;
    public bool IsInterval => MediaKindIndex != 0;
    public bool IsCompressed => IsVideo && EncoderIndex > 0;
    public bool IsConstantQuality => IsCompressed && RateControlIndex == 0;
    public bool IsTargetBitrate => IsCompressed && RateControlIndex == 1;
    public bool IsLimitedBitrate => IsCompressed && LimitBitrate;
    public bool IsCustomKeyframeInterval => IsCompressed && CustomKeyframeInterval;
    public bool IsSmaa => AntialiasingIndex == 2;
    public string RequiredExtension => IsVideo ? EncoderIndex == 0 ? ".avi" : ".mp4" : ".png";
    public string DestinationLabel => Text(IsSequence ? "新序列文件夹" : "输出文件");
    public string DestinationHint => IsSequence ? Text("每帧一个 PNG；使用新文件夹。")
        : IsVideo ? Format("{0} · 无音轨", RequiredExtension) : "PNG · sRGB";
    public string QualityHint => Text(EncoderIndex is 3 or 4 or 7 ? "0–63，数值越小画质越高。" : EncoderIndex is >= 5 ? "1–51，数值越小画质越高。" : "0–51，数值越小画质越高。");
    public string Summary
    {
        get
        {
            try
            {
                var plan = CreatePlan();
                decimal fps = FramesPerSecond!.Value;
                return Format("{0} × {1}  ·  {2:0.######} fps  ·  {3:N0} 帧", plan.Width, plan.Height, fps, plan.FrameCount);
            }
            catch (Exception error) when (error is ArgumentException or OverflowException) { return Text("调整尺寸、帧率与帧区间后查看输出摘要。"); }
        }
    }
    public string InheritedQualityStatus => Text(_sourceQuality.EffectiveTemporalUpscaling.Mode != ViewTemporalUpscalingMode.Off
        ? "已保留原配置的时域重建；本页暂不切换设备相关选项。"
        : _sourceQuality.EffectiveUpscaling.Mode != ViewUpscalingMode.Off
            ? "已保留原配置的空间超分；本页暂不切换设备相关选项。"
            : "超分与时域重建暂不可用。");

    public void ClearValidation() => ValidationMessage = string.Empty;
    public void SetValidationMessage(string message) => ValidationMessage = message;

    public bool TryCreate(out OutputSettingsDraft? result)
    {
        result = null;
        try
        {
            var kind = (OutputMediaKind)MediaKindIndex;
            var plan = CreatePlan();
            if (TransparentBackground && IsVideo) throw Error("视频不保留透明度，请选择 PNG 图片或序列。");
            if (_giIndex < 0 || _giIndex >= GiValues.Length || !GiAvailable(GiValues[_giIndex]))
                throw Error("请选择当前设备可用的 GI 模式。");
            var gi = _sourceQuality.EffectiveGi with { Mode = GiValues[_giIndex] };
            if (IsGiEnabled)
            {
                var composition = Choice<RayGiComposition>(GiCompositionIndex, "受光方式");
                gi = gi with
                {
                    Strength = FloatNumber(GiStrength, 0, 100, "GI 强度", gi.Strength),
                    SamplesPerPixel = (int)Whole(GiSamples, 1, 1024, "每像素采样"),
                    Composition = composition,
                    Denoiser = composition == RayGiComposition.ToonPalette ? Choice<RayGiDenoiser>(GiDenoiserIndex, "GI 降噪") : RayGiDenoiser.Off,
                    ToonResponse = composition == RayGiComposition.ToonPalette ? gi.ToonResponse : RayGiToonResponse.Continuous
                };
            }
            var quality = _sourceQuality with
            {
                Gi = gi == _sourceQuality.EffectiveGi ? _sourceQuality.Gi : gi,
                Antialiasing = AntialiasingIndex switch
                {
                    0 => null,
                    1 => FrameAntialiasing.Off,
                    2 => FrameAntialiasing.Smaa1X,
                    3 when _preservedAntialiasing is { } preserved => preserved,
                    _ => throw Error("请选择抗锯齿模式。")
                },
                SmaaQuality = SmaaIndex == 0 ? null : Choice<SmaaQuality>(SmaaIndex - 1, "SMAA 质量"),
                SpatialSamples = SpatialIndex == 0 ? null : SpatialIndex > 0 && SpatialIndex <= SpatialValues.Length
                    ? SpatialValues[SpatialIndex - 1] : throw Error("请选择 SSAA 采样数。"),
                SurfaceOrder = SurfaceOrderIndex switch { 0 => SurfaceDrawOrder.OpaqueFirst, 1 => SurfaceDrawOrder.Original, _ => throw Error("Mda.Text.SurfaceOrder.Invalid") },
                Diffusion = new(DiffusionEnabled,
                    (float)(DiffusionEnabled ? Number(DiffusionStrength, 0, 1, "柔光强度")
                        : RetainedNumber(DiffusionStrength, 0, 1, (decimal)_sourceQuality.EffectiveDiffusion.Strength)),
                    (float)(DiffusionEnabled ? Number(DiffusionExtent, 0, .01m, "柔光范围")
                        : RetainedNumber(DiffusionExtent, 0, .01m, (decimal)_sourceQuality.EffectiveDiffusion.Extent)))
            };
            quality.Validate();
            var captureMemory = Choice<ViewCaptureMemoryMode>(CaptureMemoryIndex, "Mda.Text.CaptureMemory.Label");
            if (!SupportsCaptureMemory && captureMemory != ViewCaptureMemoryMode.Auto)
                throw Error("Mda.Text.CaptureMemory.ModernOnly");
            var request = new ExportRequest(plan, quality, captureMemory); request.Validate();
            var destination = Destination.Trim();
            if (string.IsNullOrWhiteSpace(destination)) throw Error("请选择输出文件或新序列文件夹。");
            FfmpegOutputOptions? video = null;
            if (IsVideo)
            {
                if (EncoderIndex < 0 || EncoderIndex >= EncoderValues.Length) throw Error("请选择编码器。");
                video = _sourceEncoding with
                {
                    OutputPath = destination, Encoder = EncoderValues[EncoderIndex],
                    RateControl = EncoderIndex == 0 ? VideoRateControl.ConstantQuality : Choice<VideoRateControl>(RateControlIndex, "码率模式"),
                    Speed = IsCompressed ? Choice<VideoSpeed>(SpeedIndex, "编码速度") : _sourceEncoding.Speed,
                    Quality = EncoderIndex == 0 ? 23 : IsConstantQuality
                        ? (int)Whole(Quality, int.MinValue, int.MaxValue, "恒定画质") : _sourceEncoding.Quality,
                    TargetBitrate = IsTargetBitrate ? Whole(TargetBitrate, 1000, 2_000_000_000, "目标码率") : null,
                    MaximumBitrate = IsLimitedBitrate ? Whole(MaximumBitrate, 1000, 2_000_000_000, "最大码率") : null,
                    BufferSize = IsLimitedBitrate ? Whole(BufferSize, 1000, 2_000_000_000, "缓冲大小") : null,
                    KeyframeInterval = IsCustomKeyframeInterval ? (int)Whole(KeyframeInterval, 1, 10000, "关键帧间隔") : null,
                    Profile = EncoderIndex == 0 ? VideoProfile.Automatic : Choice<VideoProfile>(ProfileIndex, "Mda.Text.ed7970f0e1c4"),
                    Level = EncoderIndex == 0 ? VideoLevel.Automatic : Choice<VideoLevel>(LevelIndex, "Mda.Text.205e474810d6"),
                    Threads = (int)Whole(Threads, 1, 64, "软件编码线程"),
                    Timeout = TimeSpan.FromSeconds((double)Number(TimeoutSeconds, .001m, 86400, "编码等待时间"))
                };
                video.Validate(plan);
            }
            var export = new ViewExportPreset(plan.Width, plan.Height, plan.FrameRate, quality, captureMemory); export.Validate();
            // Preview is kept exactly as supplied; the preset constructor also detaches mutable runtime history markers.
            var preset = new ViewOutputPresetDefinition(_sourcePreset.StableId, _sourcePreset.Name, _sourcePreset.Preview,
                export, video is null ? _sourcePreset.Encoding : FfmpegEncodingPreset.FromOptions(video));
            // A hidden background must round-trip exactly, without quantizing the source RGB to codec bytes.
            var draft = new OutputSettingsDraft(preset, request, kind, destination, TransparentBackground,
                new(_sourceBackground.X, _sourceBackground.Y, _sourceBackground.Z, TransparentBackground ? 0 : 1), video,
                _endFrameFollowsAnimation);
            draft.Validate(); result = draft; ValidationMessage = string.Empty; return true;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or OverflowException)
        {
            _validationError = error;
            PropertyChanged?.Invoke(this, new(nameof(ValidationMessage)));
            PropertyChanged?.Invoke(this, new(nameof(HasValidationMessage)));
            return false;
        }
    }

    private OfflineFramePlan CreatePlan()
    {
        int width = (int)Whole(Width, 1, 32768, "宽度"), height = (int)Whole(Height, 1, 32768, "高度");
        var frameRate = CreateFrameRate();
        uint start = (uint)Whole(StartFrame, 0, uint.MaxValue, "起始帧");
        uint end = IsInterval ? (uint)Whole(EndFrame, 0, uint.MaxValue, "结束帧") : start;
        uint preRoll = (uint)Whole(PreRoll, 0, uint.MaxValue, "物理预滚帧数");
        if (end < start) throw Error("结束帧不能早于起始帧。");
        if (preRoll > start) throw Error("物理预滚不能越过第 0 帧，请减少预滚或提高起始帧。");
        return new(width, height, frameRate, start, end, preRoll, includeEndFrame: !IsInterval || end == start);
    }

    private OfflineFrameRate CreateFrameRate()
    {
        decimal fps = Number(FramesPerSecond, .000001m, 1_000_000, "帧率");
        // Displaying a rounded fps value must not change an existing exact timing ratio.
        if (fps == _initialFramesPerSecond) return _sourcePreset.Export.FrameRate;
        decimal scaled = fps * 1_000_000;
        if (scaled != decimal.Truncate(scaled)) throw Error("帧率最多保留 6 位小数。");
        long numerator = (long)scaled, denominator = 1_000_000;
        long divisor = (long)BigInteger.GreatestCommonDivisor(numerator, denominator);
        numerator /= divisor; denominator /= divisor;
        if (numerator > 1_000_000) throw Error("当前帧率超出支持范围，请使用常见整数或小数帧率。");
        return new((int)numerator, (int)denominator);
    }

    private void UpdateDestinationExtension()
    {
        if (string.IsNullOrWhiteSpace(Destination)) return;
        try
        {
            if (IsSequence)
                Destination = Path.Combine(Path.GetDirectoryName(Destination) ?? string.Empty, "MDA-frames");
            else Destination = Path.ChangeExtension(Destination, RequiredExtension);
        }
        catch (ArgumentException) { /* Keep incomplete user input editable; Apply reports the invalid path. */ }
    }
    private static long Whole(decimal? value, long minimum, long maximum, string label)
    {
        decimal number = Number(value, minimum, maximum, label);
        if (decimal.Truncate(number) != number) throw Error("{0}需要整数。", Label(label));
        return (long)number;
    }
    private static decimal Number(decimal? value, decimal minimum, decimal maximum, string label)
    {
        if (value is null || value < minimum || value > maximum) throw Error("{0}应为 {1}–{2}。", Label(label), minimum, maximum);
        return value.Value;
    }
    private static float FloatNumber(decimal? value, decimal minimum, decimal maximum, string label, float original)
    {
        decimal number = Number(value, minimum, maximum, label);
        return number == (decimal)original ? original : (float)number;
    }
    // Disabled fields retain valid typed values or their original settings; incomplete edits are not applied.
    private static decimal RetainedNumber(decimal? value, decimal minimum, decimal maximum, decimal original)
        => value is { } number && number >= minimum && number <= maximum ? number : original;
    private static T Choice<T>(int index, string label) where T : struct, Enum => Enum.IsDefined(typeof(T), index)
        ? (T)Enum.ToObject(typeof(T), index) : throw Error("请选择{0}。", Label(label));

    private static string FriendlyError(Exception error)
    {
        if (error is EditorLocalizedArgumentException) return error.Message;
        string text = error.Message;
        if (text.StartsWith("YUV 4:2:0", StringComparison.Ordinal)) return "当前压缩视频要求宽度和高度都是偶数，请调整尺寸。";
        if (text.Contains("explicit constant quality", StringComparison.Ordinal)) return "当前编码器的画质数值超出范围，请按画质下方提示调整。";
        if (text.Contains("Target bitrate must not exceed", StringComparison.Ordinal)) return "目标码率不能高于最大码率。";
        if (text.Contains("profile is incompatible", StringComparison.Ordinal)) return "Mda.Text.ab16f43c2f65";
        if (text.Contains("profiles are verified", StringComparison.Ordinal)) return "Mda.Text.acc377910f55";
        if (text.Contains("does not support the requested lossless", StringComparison.Ordinal)) return "Mda.Text.26523b7bc38b";
        if (text.Contains("H.264 level 4.1 limits", StringComparison.Ordinal)) return "Mda.Text.fed09537e6d7";
        if (text.Contains("requires an explicit", StringComparison.Ordinal)) return "输出文件扩展名与编码格式不一致，请检查 .mp4 或 .avi。";
        if (text.Contains("Temporal reconstruction", StringComparison.Ordinal)) return "时域重建需要关闭 SMAA、SSAA 和空间超分。";
        if (text.Contains("bounded frame allocation", StringComparison.Ordinal)) return "尺寸或总像素超过输出范围，请降低分辨率。";
        if (text.Contains("budget exceeded", StringComparison.Ordinal)) return "输出帧数或物理模拟区间过长，请缩短帧区间。";
        if (text.Any(c => c is >= '\u4e00' and <= '\u9fff')) return text;
        return "配置未通过检查，请检查数值、路径及编码格式的组合。";
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; PropertyChanged?.Invoke(this, new(name));
        foreach (string derived in DerivedProperties) PropertyChanged?.Invoke(this, new(derived));
        return true;
    }
    private static readonly string[] DerivedProperties = [nameof(IsVideo), nameof(IsImage), nameof(IsSequence), nameof(IsInterval),
        nameof(IsCompressed), nameof(IsConstantQuality), nameof(IsTargetBitrate), nameof(IsLimitedBitrate), nameof(IsCustomKeyframeInterval),
        nameof(IsSmaa), nameof(IsGiEnabled), nameof(IsGiDenoiserEnabled), nameof(ShowGiPaletteNotice), nameof(RequiredExtension), nameof(DestinationLabel), nameof(DestinationHint),
        nameof(QualityHint), nameof(MaximumBitrateHint), nameof(BufferSizeHint), nameof(CaptureMemoryHint), nameof(Summary), nameof(HasValidationMessage)];
    public event PropertyChangedEventHandler? PropertyChanged;

    private static string FormatBitrateHint(decimal? bitsPerSecond)
    {
        if (bitsPerSecond is not { } value || value <= 0) return string.Empty;
        var culture = CultureInfo.GetCultureInfo(CurrentLanguage);
        if (value >= 1_000_000m) return string.Concat("≈ ", (value / 1_000_000m).ToString("0.0#", culture), " Mbps");
        if (value >= 1_000m) return string.Concat("≈ ", (value / 1_000m).ToString("0.0#", culture), " kbps");
        return string.Concat("≈ ", value.ToString("0", culture), " bps");
    }

    private static string FormatBufferSizeHint(decimal? bits)
    {
        if (bits is not { } value || value <= 0) return string.Empty;
        var culture = CultureInfo.GetCultureInfo(CurrentLanguage);
        decimal bytes = value / 8m;
        if (bytes >= 1_000_000m) return string.Concat("≈ ", (bytes / 1_000_000m).ToString("0.0#", culture), " MB");
        if (bytes >= 1_000m) return string.Concat("≈ ", (bytes / 1_000m).ToString("0.0#", culture), " KB");
        return string.Concat("≈ ", bytes.ToString("0.#", culture), " B");
    }

    private void RefreshLocalization()
    {
        foreach (var choice in SurfaceOrders) choice.RefreshLocalization();
        foreach (var choice in CaptureMemoryChoices) choice.RefreshLocalization();
        foreach (var choices in new[] { MediaKinds, Encoders, RateControls, Speeds, Profiles, Levels,
            AntialiasingModes, SmaaQualities, SpatialSamples, GiModes, GiCompositions, GiDenoisers })
            foreach (var choice in choices) choice.RefreshLabel();
        // Every ItemsSource and row keeps its identity; refreshing translated text does not edit a draft.
        PropertyChanged?.Invoke(this, new(null));
    }
}
