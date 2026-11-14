using Mmd.Modern.Core;

namespace Mmd.Modern.App;

/// <summary>Completed viewport presentation data shared by the editor and its selected renderer.</summary>
public sealed record EditorViewportPresentationStatistics(long PresentCount, long ExplicitCaptureCount, int ResizeCount,
    int IdleWaitCount, int Width, int Height, int BackbufferCount, EditorViewportFrameStatistics? Frame)
{
    public EditorViewportPresentationTiming? Timing { get; init; }
    public int RequestedSpatialSamples { get; init; } = Frame?.SpatialSamples ?? 1;
    public int EffectiveSpatialSamples { get; init; } = Frame?.SpatialSamples ?? 1;
    public string? PreviewSamplingAdjustmentReason { get; init; }
}

/// <summary>CPU submission and presentation waits, not GPU execution time or scan-out latency.</summary>
public sealed record EditorViewportPresentationTiming(double RecordAndSubmitMilliseconds,
    double PresentWaitMilliseconds, double TotalMilliseconds)
{
    public double InterPresentMilliseconds { get; init; }
}

/// <summary>Common quality feedback and optional observed frame diagnostics. A backend need not expose fences.</summary>
public sealed record EditorViewportFrameStatistics
{
    public long? FrameId { get; init; }
    public int? SubmissionCount { get; init; }
    public ulong? SubmittedFence { get; init; }
    public ulong? CompletedFence { get; init; }
    public int? PendingFrames { get; init; }
    public double? RecordCpuMilliseconds { get; init; }
    public double? GeometryCacheMilliseconds { get; init; }
    public long? RecordAllocatedBytes { get; init; }
    public double? LastSlotWaitMilliseconds { get; init; }
    public double? SubmitCpuMilliseconds { get; init; }
    public EditorViewportSceneRecordCpuStatistics? SceneRecordCpu { get; init; }
    public int SpatialSamples { get; init; } = 1;
    public FrameAntialiasing Antialiasing { get; init; }
    public SmaaQuality SmaaQuality { get; init; }
    public int UpscalingInputWidth { get; init; }
    public int UpscalingInputHeight { get; init; }
    public EditorViewportTemporalUpscalingStatistics? TemporalUpscaling { get; init; }
}

public sealed record EditorViewportSceneRecordCpuStatistics(EditorViewportCpuStageStatistics SurfaceBoundsBuild,
    EditorViewportCpuStageStatistics OutlineBoundsBuild);

public readonly record struct EditorViewportCpuStageStatistics(double CpuMilliseconds, long AllocatedBytes, int Calls);

public sealed record EditorViewportTemporalUpscalingStatistics(int RenderWidth, int RenderHeight);
