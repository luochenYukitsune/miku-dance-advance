using System.Diagnostics;
using Avalonia.Threading;
using Mmd.Modern.Application;

namespace Mmd.Modern.App;

/// <summary>
/// Coalesces rapid timeline-scrub seeks off the UI thread (latest-request-wins), cancels
/// superseded requests, and publishes prepared candidates back on the UI thread.
/// </summary>
internal sealed class EditorScrubSeekCoordinator : IDisposable
{
    private readonly Func<SceneProjectSession?> sessionGetter;
    private readonly Action<SceneProjectSession> onPublished;
    private readonly Action<Exception> onFailed;
    private readonly object gate = new();

    private Work? pendingWork;
    private CancellationTokenSource? activeCancellation;
    private Task workerTask = Task.CompletedTask;
    private bool workerRunning;
    private long workerEpoch;

    private long generation;
    private long epoch;
    private SceneProjectSession? pendingSession;
    private int? pendingTargetFrame;
    private bool scrubInterrupted;
    private bool physicsResetPending;
    private bool disposed;

    internal EditorScrubSeekCoordinator(
        Func<SceneProjectSession?> sessionGetter,
        Action<SceneProjectSession> onPublished,
        Action<Exception> onFailed)
    {
        this.sessionGetter = sessionGetter ?? throw new ArgumentNullException(nameof(sessionGetter));
        this.onPublished = onPublished ?? throw new ArgumentNullException(nameof(onPublished));
        this.onFailed = onFailed ?? throw new ArgumentNullException(nameof(onFailed));
    }

    public bool SkipPhysicsPreRollDuringScrub { get; set; } = true;
    internal bool IsScrubbing { get; private set; }
    internal bool HasPendingSeek { get; private set; }
    internal bool HasPendingPhysicsReset => physicsResetPending;
    internal int? PendingTargetFrame => pendingTargetFrame;
    internal event EventHandler? SeekSettled;

    private sealed record Work(
        long Generation,
        long Epoch,
        SceneProjectSession Session,
        SceneSeekRequest Request,
        int TargetFrame,
        CancellationTokenSource Cancellation,
        long RequestedAt,
        long InputId,
        long InputSessionId);

    internal void BeginScrub()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (disposed) return;
        IsScrubbing = true;
        scrubInterrupted = false;
    }

    internal void EndScrub(SceneProjectSession? session, int targetFrame)
    {
        Dispatcher.UIThread.VerifyAccess();
        bool interrupted = scrubInterrupted;
        bool needsPhysicsReset = physicsResetPending;
        scrubInterrupted = false;
        IsScrubbing = false;
        physicsResetPending = false;
        if (disposed || session is null || interrupted) return;
        if (HasPendingSeek && ReferenceEquals(pendingSession, session) && pendingTargetFrame == targetFrame)
            return;
        if (needsPhysicsReset || session.CurrentFrame != targetFrame)
            RequestSeek(session, targetFrame);
    }

    internal void RequestSeek(SceneProjectSession session, int targetFrame)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (disposed) return;
        ArgumentNullException.ThrowIfNull(session);

        long inputId = EditorInteractionTrace.CurrentInput;
        long inputSessionId = InteractionDiagnostics.SessionId;
        using var requestMeasure = InteractionDiagnostics.Measure("scrub.request", inputId, targetFrame);

        if (IsScrubbing && SkipPhysicsPreRollDuringScrub && session.RequiresPhysicsPreRoll)
        {
            CancelPendingWork(preserveScrubbing: true);
            using (InteractionDiagnostics.Measure("scrub.preview-no-preroll", inputId, targetFrame))
                session.SeekWithoutPhysicsPreRoll(targetFrame);
            physicsResetPending = true;
            InteractionDiagnostics.RecordForSession(inputSessionId, "scrub.published-without-physics",
                inputId, targetFrame);
            onPublished(session);
            SeekSettled?.Invoke(this, EventArgs.Empty);
            return;
        }

        var request = session.TryCaptureSeekRequest(targetFrame);
        if (request is null)
        {
            CancelPendingWork(preserveScrubbing: IsScrubbing);
            physicsResetPending = false;
            session.Seek(targetFrame);
            onPublished(session);
            SeekSettled?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Clearing physicsResetPending here relies on HasPendingSeek staying true until publication,
        // so an intervening Cancel() still reports pending work and forces a synchronous full Seek.
        physicsResetPending = false;
        generation++;
        HasPendingSeek = true;
        pendingSession = session;
        pendingTargetFrame = targetFrame;
        var cancellation = new CancellationTokenSource();
        var work = new Work(generation, epoch, session, request, targetFrame, cancellation,
            Stopwatch.GetTimestamp(), inputId, inputSessionId);

        lock (gate)
        {
            if (pendingWork is { } superseded)
            {
                InteractionDiagnostics.RecordForSession(superseded.InputSessionId, "scrub.superseded",
                    superseded.InputId, superseded.TargetFrame, superseded.Generation);
                superseded.Cancellation.Cancel();
                superseded.Cancellation.Dispose();
            }
            activeCancellation?.Cancel();
            pendingWork = work;
            InteractionDiagnostics.RecordForSession(work.InputSessionId, "scrub.admitted",
                work.InputId, work.TargetFrame, work.Generation);
            if (!workerRunning)
            {
                workerRunning = true;
                workerTask = Task.Run(ProcessPending);
            }
        }
    }

    /// <summary>Cancels any queued or in-flight background seek, invalidates late callbacks, and
    /// clears un-warmed scrub state. Returns true if a seek or physics warmup was still pending.</summary>
    internal bool Cancel()
    {
        Dispatcher.UIThread.VerifyAccess();
        return CancelPendingWork();
    }

    private bool CancelPendingWork(bool preserveScrubbing = false)
    {
        bool hadPending = HasPendingSeek || physicsResetPending;
        if (!preserveScrubbing)
        {
            if (IsScrubbing)
                scrubInterrupted = true;
            IsScrubbing = false;
            physicsResetPending = false;
        }
        HasPendingSeek = false;
        pendingSession = null;
        pendingTargetFrame = null;
        generation++;
        epoch++;
        lock (gate)
        {
            workerEpoch = epoch;
            if (pendingWork is { } cancelled)
            {
                InteractionDiagnostics.RecordForSession(cancelled.InputSessionId, "scrub.cancelled",
                    cancelled.InputId, cancelled.TargetFrame, cancelled.Generation);
                cancelled.Cancellation.Cancel();
                cancelled.Cancellation.Dispose();
                pendingWork = null;
            }
            activeCancellation?.Cancel();
        }
        return hadPending;
    }

    internal Task WaitForWorkerIdleAsync()
    {
        lock (gate) return workerTask;
    }

    private void ProcessPending()
    {
        while (true)
        {
            Work? work;
            lock (gate)
            {
                work = pendingWork;
                pendingWork = null;
                if (work is null)
                {
                    workerRunning = false;
                    return;
                }
                activeCancellation = work.Cancellation;
            }

            InteractionDiagnostics.RecordForSession(work.InputSessionId, "scrub.dequeued",
                work.InputId, work.TargetFrame, work.Generation,
                elapsedTicks: Stopwatch.GetTimestamp() - work.RequestedAt);

            ScenePreparedSeek? prepared = null;
            Exception? failure = null;
            bool wasCancelled = false;
            try
            {
                var token = work.Cancellation.Token;
                token.ThrowIfCancellationRequested();
                using var prepareMeasure = InteractionDiagnostics.Measure("scrub.prepare", work.InputId, work.TargetFrame);
                prepared = work.Request.Prepare(token);
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (work.Cancellation.IsCancellationRequested)
            {
                wasCancelled = true;
            }
            catch (Exception error)
            {
                failure = error;
            }

            bool skipPublish;
            lock (gate)
            {
                if (ReferenceEquals(activeCancellation, work.Cancellation))
                    activeCancellation = null;
                skipPublish = wasCancelled || work.Cancellation.IsCancellationRequested
                    || work.Epoch != workerEpoch || pendingWork is not null;
            }
            work.Cancellation.Dispose();

            if (skipPublish)
            {
                prepared?.Dispose();
                InteractionDiagnostics.RecordForSession(work.InputSessionId,
                    wasCancelled || work.Epoch != workerEpoch ? "scrub.cancelled" : "scrub.superseded",
                    work.InputId, work.TargetFrame, work.Generation);
                continue;
            }

            long postedAt = InteractionDiagnostics.IsEnabled ? Stopwatch.GetTimestamp() : 0;
            InteractionDiagnostics.RecordForSession(work.InputSessionId, "scrub.publish-post",
                work.InputId, work.TargetFrame, work.Generation);

            var completed = prepared;
            var errorResult = failure;
            Dispatcher.UIThread.Post(() =>
            {
                InteractionDiagnostics.RecordForSession(work.InputSessionId, "scrub.publish-callback",
                    work.InputId, work.TargetFrame, work.Generation,
                    elapsedTicks: postedAt == 0 ? 0 : Stopwatch.GetTimestamp() - postedAt);

                if (disposed || epoch != work.Epoch || generation != work.Generation
                    || !ReferenceEquals(sessionGetter(), work.Session))
                {
                    completed?.Dispose();
                    InteractionDiagnostics.RecordForSession(work.InputSessionId, "scrub.discarded",
                        work.InputId, work.TargetFrame, work.Generation);
                    return;
                }

                HasPendingSeek = false;
                pendingSession = null;
                pendingTargetFrame = null;

                if (errorResult is not null)
                {
                    completed?.Dispose();
                    onFailed(errorResult);
                    SeekSettled?.Invoke(this, EventArgs.Empty);
                    return;
                }

                try
                {
                    bool published;
                    using (InteractionDiagnostics.Measure("scrub.publish", work.InputId, work.TargetFrame))
                        published = work.Session.TryPublishSeek(completed!);

                    if (published)
                    {
                        InteractionDiagnostics.RecordForSession(work.InputSessionId, "scrub.published",
                            work.InputId, work.TargetFrame, work.Generation);
                        onPublished(work.Session);
                    }
                    else
                    {
                        InteractionDiagnostics.RecordForSession(work.InputSessionId, "scrub.discarded",
                            work.InputId, work.TargetFrame, work.Generation);
                    }
                }
                catch (Exception error)
                {
                    onFailed(error);
                }
                finally
                {
                    SeekSettled?.Invoke(this, EventArgs.Empty);
                }
            }, DispatcherPriority.Background);
        }
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (disposed) return;
        disposed = true;
        IsScrubbing = false;
        CancelPendingWork();
        Task running;
        lock (gate) running = workerTask;
        running.GetAwaiter().GetResult();
    }
}
