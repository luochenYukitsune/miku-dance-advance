using System.Runtime.ExceptionServices;
using Mmd.Modern.Application;

namespace Mmd.Modern.ViewOutput;

/// <summary>A single-use export task. Factories must create independently owned instances, never a live preview's
/// session or renderer. Frames are captured and written one at a time in output order; capture and write of frame N
/// run beside the simulation of frame N+1, so at most one captured CPU frame is owned by this task.</summary>
public sealed class ExportCoordinator
{
    private readonly Func<CancellationToken, ValueTask<ViewPlaybackOwner>> sessionFactory;
    private readonly Func<ViewRenderRequest, CancellationToken, ValueTask<IViewRenderer>> rendererFactory;
    private int state;
    /// <summary>Overlaps capture and write with simulation of the next frame. Off reproduces the fully serial order,
    /// in which a capture may still observe the owner at its own sample.</summary>
    internal bool OverlapCapture { get; set; } = true;
    public ExportTaskState State => (ExportTaskState)Volatile.Read(ref state);

    public ExportCoordinator(Func<CancellationToken, ValueTask<SceneAnimationSession>> sessionFactory,
        Func<ViewRenderRequest, CancellationToken, ValueTask<IViewRenderer>> rendererFactory)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        this.sessionFactory = async token => ViewPlaybackOwner.From(await sessionFactory(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The session factory returned null."));
        this.rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
    }

    private ExportCoordinator(Func<CancellationToken, ValueTask<ViewPlaybackOwner>> sessionFactory,
        Func<ViewRenderRequest, CancellationToken, ValueTask<IViewRenderer>> rendererFactory)
    { this.sessionFactory = sessionFactory; this.rendererFactory = rendererFactory; }

    /// <summary>The factory must return a newly owned PMM playback, never a live preview's owner.</summary>
    public static ExportCoordinator ForPmm(Func<CancellationToken, ValueTask<PmmProjectPlayback>> playbackFactory,
        Func<ViewRenderRequest, CancellationToken, ValueTask<IViewRenderer>> rendererFactory)
    {
        ArgumentNullException.ThrowIfNull(playbackFactory); ArgumentNullException.ThrowIfNull(rendererFactory);
        return new(async token => ViewPlaybackOwner.From(await playbackFactory(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The PMM playback factory returned null.")), rendererFactory);
    }

    /// <summary>The factory must return a newly owned project, never a live preview's owner.</summary>
    public static ExportCoordinator ForProject(Func<CancellationToken, ValueTask<SceneProjectSession>> projectFactory,
        Func<ViewRenderRequest, CancellationToken, ValueTask<IViewRenderer>> rendererFactory)
    {
        ArgumentNullException.ThrowIfNull(projectFactory); ArgumentNullException.ThrowIfNull(rendererFactory);
        return new(async token => ViewPlaybackOwner.From(await projectFactory(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The project factory returned null.")), rendererFactory);
    }

    /// <summary>The factory returns a newly loaded PMA. Its session and extracted resources remain owned
    /// throughout capture, writing and renderer cleanup, and are released on success, failure or cancellation.</summary>
    public static ExportCoordinator ForPma(Func<CancellationToken, ValueTask<PmaLoadedProject>> projectFactory,
        Func<ViewRenderRequest, CancellationToken, ValueTask<IViewRenderer>> rendererFactory)
    {
        ArgumentNullException.ThrowIfNull(projectFactory); ArgumentNullException.ThrowIfNull(rendererFactory);
        return new(async token => ViewPlaybackOwner.From(await projectFactory(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The PMA project factory returned null.")), rendererFactory);
    }

    /// <summary>Takes ownership of sink once started, including validation, factory, cancellation and Begin failures.
    /// Cleanup always runs with a usable token; secondary cleanup errors remain on the original exception's Data.</summary>
    public async Task<ExportResult> RunAsync(ExportRequest request, IFrameSink sink,
        IProgress<ExportTaskProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(sink);
        if (Interlocked.CompareExchange(ref state, (int)ExportTaskState.Running, (int)ExportTaskState.Ready) != (int)ExportTaskState.Ready)
            throw new InvalidOperationException("An export coordinator can run only once.");
        ViewPlaybackOwner? session = null; IViewRenderer? renderer = null;
        OfflineFrameRunResult? result = null; string backendId = ""; bool published = false;
        Exception? failure = null; var cleanupErrors = new List<Exception>();
        var tracker = new ProgressTracker(progress);
        string phase = "request.validate";
        try
        {
            request.Validate(); cancellationToken.ThrowIfCancellationRequested();
            phase = "session.create";
            session = await sessionFactory(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The session factory returned null.");
            phase = "renderer.create";
            renderer = await rendererFactory(request.Render, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The renderer factory returned null.");
            phase = "renderer.preflight";
            renderer.Capabilities.Validate(request.Render, capture: true); backendId = renderer.Capabilities.BackendId;
            var sceneInputRequests = ViewSceneInputRequests.Capture(renderer);
            if (renderer is IViewRenderingStylePreflight || renderer is IViewSceneInputPreflight || sceneInputRequests.Count != 0)
            {
                var current = session.Snapshot(request.Plan.Width, request.Plan.Height, sceneInputRequests);
                if (renderer is IViewRenderingStylePreflight preflight)
                    preflight.ValidateRenderingStyles(current.Scene, current.RenderingStyles);
                if (renderer is IViewSceneInputPreflight inputPreflight) inputPreflight.ValidateSceneInputs(current);
            }
            if (request.Plan.EndFrame > session.DurationFrames)
                throw new ArgumentOutOfRangeException(nameof(request), "The output interval exceeds the session's motion duration.");
            phase = "sink.begin";
            await sink.BeginAsync(request, cancellationToken).ConfigureAwait(false);
            phase = "offline.evaluate";
            result = await session.RunAsync(request.Plan, async (frame, token) =>
            {
                // This may run beside the next simulation step, so its phase stays with its own failure.
                string step = "renderer.capture";
                try
                {
                    using var pixels = await renderer.CaptureAsync(frame, token).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The renderer returned no pixel lease.");
                    if (pixels.Width != frame.Width || pixels.Height != frame.Height)
                        throw new InvalidOperationException("Captured dimensions differ from the requested output dimensions.");
                    _ = pixels.Pixels; // Validate the lease is still alive before handing it to a consumer.
                    step = "sink.write";
                    await sink.WriteAsync(frame.Sample, pixels, token).ConfigureAwait(false);
                }
                catch (Exception error) { AddFailurePhase(error, step); throw; }
            }, tracker, cancellationToken, sceneInputRequests, OverlapCapture).ConfigureAwait(false);
            phase = "sink.complete";
            cancellationToken.ThrowIfCancellationRequested();
            await sink.CompleteAsync(cancellationToken).ConfigureAwait(false);
            published = true;
        }
        catch (Exception error) { if (!HasFailurePhase(error)) AddFailurePhase(error, phase); failure = error; }
        if (!published)
            await CleanupAsync(() => sink.AbortAsync(failure, CancellationToken.None), cleanupErrors, "sink.abort").ConfigureAwait(false);
        if (renderer is not null) await CleanupAsync(renderer.DisposeAsync, cleanupErrors, "renderer.dispose").ConfigureAwait(false);
        if (session is not null)
        {
            try { session.Dispose(); } catch (Exception error) { AddFailurePhase(error, "session.dispose"); cleanupErrors.Add(error); }
        }
        await CleanupAsync(sink.DisposeAsync, cleanupErrors, "sink.dispose").ConfigureAwait(false);
        if (failure is null && cleanupErrors.Count > 0) failure = cleanupErrors[0];
        if (failure is not null && cleanupErrors.Count > 0) AddFailureData(failure, "ViewOutputCleanupErrors", cleanupErrors.AsReadOnly());
        var finalState = failure is null ? ExportTaskState.Completed
            : failure is OperationCanceledException && cancellationToken.IsCancellationRequested ? ExportTaskState.Cancelled : ExportTaskState.Failed;
        Volatile.Write(ref state, (int)finalState);
        // A notification failure cannot mask the task's original failure or skip cleanup.
        try { tracker.ReportFinal(finalState, request.Plan?.FrameCount ?? 0); }
        catch (Exception error)
        {
            if (failure is null) { failure = error; Volatile.Write(ref state, (int)ExportTaskState.Failed); }
            else AddFailureData(failure, "ViewOutputProgressError", error);
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return new(result!.CompletedFrames, result.SimulationTicks, backendId);
    }

    private static async ValueTask CleanupAsync(Func<ValueTask> cleanup, List<Exception> errors, string phase)
    { try { await cleanup().ConfigureAwait(false); } catch (Exception error) { AddFailurePhase(error, phase); errors.Add(error); } }

    private const string PhaseKey = "MDA.ViewOutput.Phase";
    private static void AddFailurePhase(Exception error, string phase) => AddFailureData(error, PhaseKey, phase);
    private static bool HasFailurePhase(Exception error)
    { try { return error.Data.Contains(PhaseKey); } catch { return false; } }

    private static void AddFailureData(Exception error, string key, object value)
    { try { error.Data[key] = value; } catch { } }

    private sealed class ProgressTracker(IProgress<ExportTaskProgress>? target) : IProgress<OfflineFrameProgress>
    {
        private OfflineFrameProgress last = new(0, 0, 0, new(0, 1));
        public void Report(OfflineFrameProgress value)
        {
            last = value;
            target?.Report(new(ExportTaskState.Running, value.CompletedFrames, value.TotalFrames, value.SimulationTicks, value.PresentationTime));
        }
        public void ReportFinal(ExportTaskState finalState, long totalFrames)
            => target?.Report(new(finalState, last.CompletedFrames, totalFrames, last.SimulationTicks, last.PresentationTime));
    }
}
