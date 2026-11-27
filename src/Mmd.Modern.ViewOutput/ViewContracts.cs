using Mmd.Modern.Application;
using Mmd.Modern.Core;

namespace Mmd.Modern.ViewOutput;

/// <summary>Independent per-view configuration. Null AA overrides preserve scene settings; GI defaults Off.</summary>
public sealed record ViewQualityOptions(RayGiOptions? Gi = null, FrameAntialiasing? Antialiasing = null,
    SmaaQuality? SmaaQuality = null, int? SpatialSamples = null, ViewUpscalingOptions? Upscaling = null,
    ViewTemporalUpscalingOptions? TemporalUpscaling = null, ViewDiffusionOptions? Diffusion = null,
    SurfaceDrawOrder SurfaceOrder = SurfaceDrawOrder.OpaqueFirst)
{
    public ViewSharpeningOptions? Sharpening { get; init; }
    public int? AnisotropicFiltering { get; init; }
    public ShadowFilterMode? ShadowFilter { get; init; }
    public float? ShadowSoftness { get; init; }
    public ScreenSpaceAoOptions? ScreenSpaceAo { get; init; }

    public ViewDiffusionOptions EffectiveDiffusion => Diffusion ?? ViewDiffusionOptions.Off;
    public RayGiOptions EffectiveGi => Gi ?? RayGiOptions.Off;
    public ViewUpscalingOptions EffectiveUpscaling => Upscaling ?? ViewUpscalingOptions.Off;
    public ViewTemporalUpscalingOptions EffectiveTemporalUpscaling => TemporalUpscaling ?? ViewTemporalUpscalingOptions.Off;
    public ViewSharpeningOptions EffectiveSharpening => Sharpening ?? ViewSharpeningOptions.Off;
    public int EffectiveAnisotropicFiltering => AnisotropicFiltering ?? 1;
    public float EffectiveShadowSoftness => ShadowSoftness ?? 1f;
    public ScreenSpaceAoOptions EffectiveScreenSpaceAo => ScreenSpaceAo ?? ScreenSpaceAoOptions.Off;

    public void Validate()
    {
        EffectiveDiffusion.Validate(); EffectiveGi.Validate(); EffectiveUpscaling.Validate(); EffectiveTemporalUpscaling.Validate();
        EffectiveSharpening.Validate(); EffectiveScreenSpaceAo.Validate();
        if (!Enum.IsDefined(SurfaceOrder)) throw new ArgumentOutOfRangeException(nameof(SurfaceOrder));
        if (Antialiasing is { } antialiasing && !Enum.IsDefined(antialiasing)) throw new ArgumentOutOfRangeException(nameof(Antialiasing));
        if (SmaaQuality is { } smaa && !Enum.IsDefined(smaa)) throw new ArgumentOutOfRangeException(nameof(SmaaQuality));
        if (SpatialSamples is { } samples && samples is not (1 or 2 or 4 or 8 or 16 or 32)) throw new ArgumentOutOfRangeException(nameof(SpatialSamples));
        if (AnisotropicFiltering is { } aniso && !OriginalFrameMath.IsValidAnisotropicFiltering(aniso))
            throw new ArgumentOutOfRangeException(nameof(AnisotropicFiltering), "Anisotropic filtering must be 1, 2, 4, 8, or 16.");
        if (ShadowFilter is { } shadowFilter && !Enum.IsDefined(shadowFilter))
            throw new ArgumentOutOfRangeException(nameof(ShadowFilter));
        if (ShadowSoftness is { } softness && (!float.IsFinite(softness) || softness is < 0f or > 4f))
            throw new ArgumentOutOfRangeException(nameof(ShadowSoftness), "Shadow softness must be finite and between 0 and 4.");
        if (EffectiveTemporalUpscaling.Mode != ViewTemporalUpscalingMode.Off
            && (EffectiveUpscaling.Mode != ViewUpscalingMode.Off || Antialiasing is not (null or FrameAntialiasing.Off)
                || SpatialSamples is > 1))
            throw new NotSupportedException("Temporal reconstruction requires spatial upscaling, frame antialiasing and supersampling to be disabled.");
        if (Antialiasing == FrameAntialiasing.Taa
            && (SpatialSamples is > 1 || EffectiveUpscaling.Mode != ViewUpscalingMode.Off))
            throw new NotSupportedException("TAA cannot be combined with spatial supersampling, FSR1, or temporal upscaling.");
        if (Antialiasing is { } msaa && OriginalFrameMath.IsMsaa(msaa)
            && (SpatialSamples is > 1 || EffectiveUpscaling.Mode != ViewUpscalingMode.Off))
            throw new NotSupportedException("MSAA cannot be combined with spatial supersampling, FSR1, or temporal upscaling.");
        if (EffectiveScreenSpaceAo.Enabled && EffectiveGi.Enabled && EffectiveGi.OcclusionStrength > 0f)
            throw new NotSupportedException("Screen-space ambient occlusion cannot be enabled together with Ray GI occlusion (OcclusionStrength > 0).");
    }

    /// <summary>Returns a per-view copy. Scene settings and other views are never modified.</summary>
    public OriginalFrameOptions ApplyTo(OriginalFrameOptions? sceneOptions)
    {
        Validate();
        var options = sceneOptions ?? new(OriginalAlphaBlendMode.Normal, true, false);
        float resolvedShadowSoftness = ShadowSoftness
            ?? (ShadowFilter is not null && options.SelfShadow is { } existingShadow
                ? options.ResolveShadowSoftness(existingShadow)
                : options.ShadowSoftness);
        var resolved = options with
        {
            RayGi = EffectiveGi,
            Antialiasing = Antialiasing ?? options.Antialiasing,
            SmaaQuality = SmaaQuality ?? options.SmaaQuality,
            SpatialSamples = SpatialSamples ?? options.SpatialSamples,
            SurfaceOrder = SurfaceOrder,
            AnisotropicFiltering = AnisotropicFiltering ?? options.AnisotropicFiltering,
            ShadowFilter = ShadowFilter ?? options.ShadowFilter,
            ShadowSoftness = resolvedShadowSoftness,
            ScreenSpaceAo = ScreenSpaceAo ?? options.ScreenSpaceAo,
        };
        if (resolved.SelfShadow is { } selfShadow && (ShadowFilter is not null || ShadowSoftness is not null))
        {
            var effectiveMode = ShadowFilter ?? selfShadow.EffectiveFilterMode;
            resolved = resolved with
            {
                SelfShadow = selfShadow with
                {
                    FilterMode = ShadowFilter ?? selfShadow.FilterMode,
                    Sampling = effectiveMode == ShadowFilterMode.Point
                        ? OriginalTextureSampling.PointClamp
                        : OriginalTextureSampling.LinearClamp,
                    ShadowSoftness = ShadowSoftness ?? selfShadow.ShadowSoftness,
                },
            };
        }
        if (EffectiveTemporalUpscaling.Mode != ViewTemporalUpscalingMode.Off
            && (resolved.Antialiasing != FrameAntialiasing.Off || resolved.SpatialSamples != 1))
            throw new NotSupportedException("Inherited scene antialiasing/supersampling must be explicitly disabled for temporal reconstruction.");
        if (resolved.Antialiasing == FrameAntialiasing.Taa
            && (resolved.SpatialSamples > 1 || EffectiveUpscaling.Mode != ViewUpscalingMode.Off))
            throw new NotSupportedException("TAA cannot be combined with spatial supersampling, FSR1, or temporal upscaling.");
        if (OriginalFrameMath.IsMsaa(resolved.Antialiasing)
            && (resolved.SpatialSamples > 1 || EffectiveUpscaling.Mode != ViewUpscalingMode.Off))
            throw new NotSupportedException("MSAA cannot be combined with spatial supersampling, FSR1, or temporal upscaling.");
        if (resolved.ScreenSpaceAo is { Enabled: true } && resolved.RayGi is { Enabled: true, OcclusionStrength: > 0f })
            throw new NotSupportedException("Screen-space ambient occlusion cannot be enabled together with Ray GI occlusion (OcclusionStrength > 0).");
        return resolved;
    }
}

public sealed record ViewRenderRequest(int Width, int Height, ViewQualityOptions? Quality = null,
    ViewCaptureMemoryMode CaptureMemory = ViewCaptureMemoryMode.Auto)
{
    public ViewQualityOptions EffectiveQuality => Quality ?? new();
    public void Validate()
    {
        _ = new OfflineFramePlan(Width, Height, new(30), 0, 0);
        if (!Enum.IsDefined(CaptureMemory)) throw new ArgumentOutOfRangeException(nameof(CaptureMemory));
        EffectiveQuality.Validate();
    }
}

public sealed record ExportRequest(OfflineFramePlan Plan, ViewQualityOptions? Quality = null,
    ViewCaptureMemoryMode CaptureMemory = ViewCaptureMemoryMode.Auto)
{
    public ViewRenderRequest Render
    {
        get
        {
            var quality = Quality;
            if (quality is not null && quality.EffectiveTemporalUpscaling.Mode != ViewTemporalUpscalingMode.Off)
                quality = quality with { TemporalUpscaling = quality.EffectiveTemporalUpscaling with
                    { FrameTimeMilliseconds = 1000f * Plan.FrameRate.Denominator / Plan.FrameRate.Numerator } };
            return new(Plan.Width, Plan.Height, quality, CaptureMemory);
        }
    }
    public void Validate() { ArgumentNullException.ThrowIfNull(Plan); Render.Validate(); }
}

/// <summary>Reports the backend that actually executes this instance, not unimplemented backend choices.</summary>
public sealed record ViewRendererCapabilities(string BackendId, int MaximumWidth, int MaximumHeight,
    bool CanCapture, bool CanPresent)
{
    public void Validate(ViewRenderRequest request, bool capture)
    {
        request.Validate();
        if (string.IsNullOrWhiteSpace(BackendId) || MaximumWidth <= 0 || MaximumHeight <= 0)
            throw new InvalidOperationException("The renderer returned invalid capabilities.");
        if (capture ? !CanCapture : !CanPresent) throw new NotSupportedException("This renderer does not support the requested operation.");
        if (request.Width > MaximumWidth || request.Height > MaximumHeight)
            throw new NotSupportedException("The requested dimensions exceed this renderer's capabilities.");
    }
}

/// <summary>One independently owned capture renderer, configured by its factory using ViewRenderRequest.</summary>
public interface IViewRenderer : IAsyncDisposable
{
    ViewRendererCapabilities Capabilities { get; }
    ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame, CancellationToken cancellationToken = default);
}

/// <summary>A single-use export destination. WriteAsync borrows the lease until it returns; it must not dispose it.
/// Begin/Write never replace an existing destination; Complete publishes the verified temporary artifact.
/// Abort must be idempotent and discard unpublished output, including a partially failed Begin or Complete.</summary>
public interface IFrameSink : IAsyncDisposable
{
    ValueTask BeginAsync(ExportRequest request, CancellationToken cancellationToken = default);
    ValueTask WriteAsync(OfflineFrameSample sample, FrameBufferLease frame, CancellationToken cancellationToken = default);
    ValueTask CompleteAsync(CancellationToken cancellationToken = default);
    ValueTask AbortAsync(Exception? error, CancellationToken cancellationToken = default);
}

public enum ExportTaskState { Ready, Running, Completed, Cancelled, Failed }
public sealed record ExportTaskProgress(ExportTaskState State, long CompletedFrames, long TotalFrames,
    long SimulationTicks, OfflineFrameTime PresentationTime);
public sealed record ExportResult(long CompletedFrames, long SimulationTicks, string BackendId);
