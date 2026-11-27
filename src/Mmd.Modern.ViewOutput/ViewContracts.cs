using Mmd.Modern.Application;
using Mmd.Modern.Core;

namespace Mmd.Modern.ViewOutput;

/// <summary>Independent per-view configuration. Null AA overrides preserve scene settings; GI defaults Off.</summary>
public sealed record ViewQualityOptions(RayGiOptions? Gi = null, FrameAntialiasing? Antialiasing = null,
    SmaaQuality? SmaaQuality = null, int? SpatialSamples = null, ViewUpscalingOptions? Upscaling = null,
    ViewTemporalUpscalingOptions? TemporalUpscaling = null, ViewDiffusionOptions? Diffusion = null,
    SurfaceDrawOrder SurfaceOrder = SurfaceDrawOrder.OpaqueFirst,
    RenderPipelineMode PipelineMode = RenderPipelineMode.Forward,
    PathTracingOptions? PathTracing = null)
{
    public ViewDiffusionOptions EffectiveDiffusion => Diffusion ?? ViewDiffusionOptions.Off;
    public RayGiOptions EffectiveGi => Gi ?? RayGiOptions.Off;
    public ViewUpscalingOptions EffectiveUpscaling => Upscaling ?? ViewUpscalingOptions.Off;
    public ViewTemporalUpscalingOptions EffectiveTemporalUpscaling => TemporalUpscaling ?? ViewTemporalUpscalingOptions.Off;
    public PathTracingOptions EffectivePathTracing => PathTracing ?? PathTracingOptions.Default;
    public void Validate()
    {
        EffectiveDiffusion.Validate(); EffectiveGi.Validate(); EffectiveUpscaling.Validate(); EffectiveTemporalUpscaling.Validate();
        if (!Enum.IsDefined(SurfaceOrder)) throw new ArgumentOutOfRangeException(nameof(SurfaceOrder));
        if (!Enum.IsDefined(PipelineMode)) throw new ArgumentOutOfRangeException(nameof(PipelineMode));
        EffectivePathTracing.Validate();
        if (Antialiasing is { } antialiasing && !Enum.IsDefined(antialiasing)) throw new ArgumentOutOfRangeException(nameof(Antialiasing));
        if (SmaaQuality is { } smaa && !Enum.IsDefined(smaa)) throw new ArgumentOutOfRangeException(nameof(SmaaQuality));
        if (SpatialSamples is { } samples && samples is not (1 or 2 or 4 or 8 or 16 or 32)) throw new ArgumentOutOfRangeException(nameof(SpatialSamples));
        if (EffectiveTemporalUpscaling.Mode != ViewTemporalUpscalingMode.Off
            && (EffectiveUpscaling.Mode != ViewUpscalingMode.Off || Antialiasing is not (null or FrameAntialiasing.Off)
                || SpatialSamples is > 1))
            throw new NotSupportedException("Temporal reconstruction currently requires spatial upscaling, SMAA and supersampling to be disabled.");
        if (PipelineMode == RenderPipelineMode.PathTracing
            && (Antialiasing is not (null or FrameAntialiasing.Off) || SpatialSamples is > 1
                || EffectiveUpscaling.Mode != ViewUpscalingMode.Off
                || EffectiveTemporalUpscaling.Mode != ViewTemporalUpscalingMode.Off))
            throw new NotSupportedException("Path tracing provides stochastic subpixel antialiasing and progressive accumulation, and requires raster antialiasing, spatial supersampling, spatial upscaling and temporal upscaling to be disabled.");
    }

    /// <summary>Returns a per-view copy. Scene settings and other views are never modified.</summary>
    public OriginalFrameOptions ApplyTo(OriginalFrameOptions? sceneOptions)
    {
        Validate();
        var options = sceneOptions ?? new(OriginalAlphaBlendMode.Normal, true, false);
        var resolved = options with { RayGi = PipelineMode == RenderPipelineMode.PathTracing ? RayGiOptions.Off : EffectiveGi,
            Antialiasing = Antialiasing ?? options.Antialiasing,
            SmaaQuality = SmaaQuality ?? options.SmaaQuality, SpatialSamples = SpatialSamples ?? options.SpatialSamples,
            SurfaceOrder = SurfaceOrder, PipelineMode = PipelineMode, PathTracing = PathTracing ?? options.PathTracing };
        if (EffectiveTemporalUpscaling.Mode != ViewTemporalUpscalingMode.Off
            && (resolved.Antialiasing != FrameAntialiasing.Off || resolved.SpatialSamples != 1))
            throw new NotSupportedException("Inherited scene SMAA/supersampling must be explicitly disabled for temporal reconstruction.");
        if (PipelineMode == RenderPipelineMode.PathTracing
            && (resolved.Antialiasing != FrameAntialiasing.Off || resolved.SpatialSamples != 1))
            throw new NotSupportedException("Inherited scene antialiasing/supersampling must be explicitly disabled for path tracing.");
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
