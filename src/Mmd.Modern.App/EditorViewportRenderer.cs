using Mmd.Modern.Application;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;

namespace Mmd.Modern.App;

// Internal host boundary: production forwards directly to the native renderer. Tests
// can exercise the real host worker without constructing an HWND or a D3D device.
internal interface IEditorViewportRenderer : IRealtimeViewRenderer
{
    string AdapterName { get; }
    long AdapterLuid => 0;
    bool IsWarp { get; }
    EditorViewportPresentationStatistics Statistics { get; }
    IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests { get; }
    ValueTask WaitForNextFrameAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    /// <summary>True when WaitForNextFrameAsync is a real present-cadence permit (for example a
    /// frame-latency waitable swapchain). Only then may playback be paced by this renderer.</summary>
    bool PacesPresentation => false;
    bool SupportsRecordingAhead => false;
    ValueTask PresentAheadAsync(PreviewFrame frame, CancellationToken cancellationToken) => throw new NotSupportedException();
    ValueTask UpdateQualityAsync(ViewQualityOptions quality, CancellationToken cancellationToken = default);
}

internal sealed class EditorViewportRenderer(D3D12RealtimeViewRenderer native) : IEditorViewportRenderer
{
    public string AdapterName => native.AdapterName;
    public long AdapterLuid => native.AdapterLuid;
    public bool IsWarp => native.IsWarp;
    // Full backend diagnostics stay on the modern adapter. The shared host contract
    // exposes only feedback consumed by the editor, without requiring backend-specific capabilities.
    internal D3D12PresentationStatistics NativeStatistics => native.Statistics;
    public EditorViewportPresentationStatistics Statistics => ToPresentationStatistics(NativeStatistics);
    public IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests => native.SceneInputRequests;
    public ViewRendererCapabilities Capabilities => native.Capabilities;
    public ValueTask WaitForNextFrameAsync(CancellationToken cancellationToken = default)
        => native.WaitForNextFrameAsync(cancellationToken);
    public bool PacesPresentation => true;
    public ValueTask PresentAsync(PreviewFrame frame, CancellationToken cancellationToken = default)
        => native.PresentAsync(frame, cancellationToken);
    public bool SupportsRecordingAhead => true;
    public ValueTask PresentAheadAsync(PreviewFrame frame, CancellationToken cancellationToken)
        => native.PresentRecordedAheadAsync(frame, cancellationToken);
    public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default)
        => native.ResizeAsync(width, height, cancellationToken);
    public ValueTask UpdateQualityAsync(ViewQualityOptions quality, CancellationToken cancellationToken = default)
        => native.UpdateQualityAsync(quality, cancellationToken);
    public ValueTask DisposeAsync() => native.DisposeAsync();

    internal static EditorViewportPresentationStatistics ToPresentationStatistics(D3D12PresentationStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        var frame = statistics.Frame;
        return new(statistics.PresentCount, statistics.ExplicitCaptureCount, statistics.ResizeCount,
            statistics.IdleWaitCount, statistics.Width, statistics.Height, statistics.BackbufferCount,
            frame is null ? null : new EditorViewportFrameStatistics
            {
                FrameId = frame.FrameId,
                SubmissionCount = frame.SubmissionCount,
                SubmittedFence = frame.SubmittedFence,
                CompletedFence = frame.CompletedFence,
                PendingFrames = frame.PendingFrames,
                RecordCpuMilliseconds = frame.RecordCpuMilliseconds,
                GeometryCacheMilliseconds = frame.GeometryCacheMilliseconds,
                RecordAllocatedBytes = frame.RecordAllocatedBytes,
                LastSlotWaitMilliseconds = frame.LastSlotWaitMilliseconds,
                SubmitCpuMilliseconds = frame.SubmitCpuMilliseconds,
                SceneRecordCpu = frame.SceneRecordCpu is not { } scene ? null : new(
                    new(scene.SurfaceBoundsBuild.CpuMilliseconds, scene.SurfaceBoundsBuild.AllocatedBytes, scene.SurfaceBoundsBuild.Calls),
                    new(scene.OutlineBoundsBuild.CpuMilliseconds, scene.OutlineBoundsBuild.AllocatedBytes, scene.OutlineBoundsBuild.Calls)),
                SpatialSamples = frame.SpatialSamples,
                Antialiasing = frame.Antialiasing,
                SmaaQuality = frame.SmaaQuality,
                UpscalingInputWidth = frame.UpscalingInputWidth,
                UpscalingInputHeight = frame.UpscalingInputHeight,
                TemporalUpscaling = frame.TemporalUpscaling is not { } temporal ? null : new(temporal.RenderWidth, temporal.RenderHeight)
            })
        {
            Timing = statistics.Timing is not { } timing ? null : new(timing.RecordAndSubmitMilliseconds,
                timing.PresentWaitMilliseconds, timing.TotalMilliseconds) { InterPresentMilliseconds = timing.InterPresentMilliseconds },
            RequestedSpatialSamples = statistics.RequestedSpatialSamples,
            EffectiveSpatialSamples = statistics.EffectiveSpatialSamples,
            PreviewSamplingAdjustmentReason = statistics.PreviewSamplingAdjustmentReason
        };
    }
}
