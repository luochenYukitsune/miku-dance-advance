using Mmd.Modern.Application;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Animation;
using Mmd.Modern.Formats;
using Avalonia.Threading;
using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace Mmd.Modern.App;

/// <summary>The visible preview policy. SettledBones means the bounded settling budget finished;
/// it does not claim convergence or equivalence to a fresh complete evaluation.</summary>
public enum EditorPosePreviewState { Source, Complete, InteractiveBones, ReleasedBones, SettledBones }

/// <summary>Owns an unregistered editing pose. Synchronous sessions stay on the caller's thread;
/// background sessions and their solvers stay on one worker. Neither path changes the live owner.</summary>
public sealed class EditorPosePreview : IDisposable
{
    private const int ReleaseSettleSubsteps = 120;
    private const int SettleBatchSubsteps = 2;
    private const double PhysicsStepSeconds = 1d / 120;
    private readonly Func<IProjectPhysicsBackendProvider> createPhysicsProvider;
    private SceneProjectSession? preview;
    private IReadOnlyList<EditorAnimationPoseDraft> current = Array.Empty<EditorAnimationPoseDraft>();
    private IReadOnlyDictionary<Guid, VmdMotion> currentMotions = new Dictionary<Guid, VmdMotion>();
    private bool disposed;
    private readonly object workerGate = new();
    private AsyncWork? pendingWork;
    private AsyncWork? activeWork;
    private CancellationTokenSource? activeCancellation;
    private bool workerRunning;
    private bool workerResetRequested;
    private bool workerHasWarmOwner;
    private long workerGeneration;
    // These three fields are accessed only by ProcessPending, including retirement after Clear.
    private SceneProjectSession? warmOwner;
    private ScenePosePreviewRequest? warmSource;
    private long warmLastInputTimestamp;
    private long asyncGeneration;
    private long asyncPublicationEpoch;
    private long lastPublishedGeneration;
    private ScenePosePreviewRequest? asyncSource;
    private IReadOnlyList<EditorAnimationPoseDraft> asyncDrafts = [];
    private IReadOnlyList<SceneObjectRenderRequest> asyncInputs = [];
    private SceneProjectRenderSnapshot? asyncSnapshot;
    private AsyncPreviewKind asyncKind;
    private bool asyncPrewarm;
    private ScenePosePreviewRequest? interactiveBaseline;
    private Guid interactiveActor;
    private IReadOnlyList<EditorAnimationPoseDraft> fixedDrafts = [];
    private int[] baselineBoneIndices = [];
    private bool preparingVisiblePose;
    /// <summary>Uses the production solver factory unless a host supplies its own fresh-owner factory.</summary>
    public EditorPosePreview() : this(EditorProjectWorkspace.CreatePhysicsProvider) { }
    public EditorPosePreview(Func<IProjectPhysicsBackendProvider> createPhysicsProvider)
        => this.createPhysicsProvider = createPhysicsProvider ?? throw new ArgumentNullException(nameof(createPhysicsProvider));

    public bool IsPreparingPreview => preparingVisiblePose;
    public EditorPosePreviewState State { get; private set; }
    /// <summary>True after the latest requested raw input has reached a published snapshot.
    /// ReleasedBones can satisfy this while its bounded physical continuation is still running.</summary>
    public bool HasLatestInputSnapshot => asyncSnapshot is not null && lastPublishedGeneration == asyncGeneration;
    public int RemainingSettleSteps { get; private set; }
    public event EventHandler? PreparationChanged;
    public event EventHandler? StateChanged;
    public event EventHandler? SnapshotPublished;
    private void SetState(EditorPosePreviewState value)
    {
        if (State == value) return;
        State = value; StateChanged?.Invoke(this, EventArgs.Empty);
    }
    private void SetPreparingPreview(bool value)
    {
        if (preparingVisiblePose == value) return;
        preparingVisiblePose = value; PreparationChanged?.Invoke(this, EventArgs.Empty);
    }
    private enum AsyncPreviewKind { Complete, InteractiveBones, ReleasedBones, SettlingBones, Prewarm }
    private sealed record AsyncWork(long Generation, long PublicationEpoch, SceneProjectSession Source, ScenePosePreviewRequest Request,
        IReadOnlyList<SceneObjectRenderRequest> Inputs, CancellationTokenSource Cancellation,
        Action RequestRender, Action<Exception>? ReportError, AsyncPreviewKind Kind,
        Guid BoneObjectId, IReadOnlyDictionary<int, BoneLocalTransform>? Bones, long InputTimestamp,
        ScenePosePreviewRequest? PrewarmAfter, int RemainingSettleSteps = 0, long NotBeforeTimestamp = 0,
        long InputId = 0, long InputSessionId = 0)
    {
        internal long DiagnosticInputId => InputSessionId == InteractionDiagnostics.SessionId ? InputId : 0;
    }

    /// <summary>UI-thread nonblocking path. While one detached graph evaluates, subsequent drafts
    /// replace the sole pending request. Only a still-current, complete snapshot returns to the UI.
    /// A supported bone drag and its release share a worker-owned physical graph. Release publishes
    /// the latest target without advancing time, then continues a bounded 120 fixed substeps in
    /// paced batches. Numeric/morph edits and unsupported graphs retain complete evaluation.
    /// Prewarming publishes no pose and never requests a render.</summary>
    public SceneProjectRenderSnapshot? TryGetOrRequest(SceneProjectSession? source, EditorAnimationViewModel editor,
        IReadOnlyList<SceneObjectRenderRequest> requests, Action requestRender, Action<Exception>? reportError = null,
        bool interactive = false, bool prewarm = false)
    {
        Dispatcher.UIThread.VerifyAccess();
        using var requestMeasure = InteractionDiagnostics.Measure("pose.request", EditorInteractionTrace.CurrentInput);
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(editor); ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(requestRender);
        var drafts = editor.SnapshotDrafts();
        if (source is null || source.CurrentFrame != editor.CurrentFrame || (drafts.Count == 0 && !prewarm))
        { ClearAsync(); return null; }

        bool sameSource = asyncSource?.IsCurrent(source) == true;
        bool sameInputs = SameInputs(requests, asyncInputs);
        var boneObject = editor.ObjectId ?? Guid.Empty;
        var boneDrafts = drafts.Where(draft => draft.ObjectId == boneObject && draft.BoneIndex.HasValue).ToArray();
        var stableDrafts = drafts.Where(draft => draft.ObjectId != boneObject || !draft.BoneIndex.HasValue).ToArray();
        bool continueRelease = !interactive && sameSource && sameInputs && interactiveActor == boneObject
            && (asyncKind == AsyncPreviewKind.InteractiveBones
                || asyncKind == AsyncPreviewKind.ReleasedBones && drafts.SequenceEqual(asyncDrafts))
            && interactiveBaseline?.IsCurrent(source) == true && stableDrafts.SequenceEqual(fixedDrafts)
            && RetainsBaselineBones(boneDrafts);
        var kind = AsyncPreviewKind.Complete;
        ScenePosePreviewRequest? committed = null;
        if ((interactive || prewarm || continueRelease) && boneObject != Guid.Empty
            && (prewarm || drafts.Count == 0 || boneDrafts.Length != 0))
        {
            // PMM restores saved poses on other models, including morphs. Freeze those once
            // as the drag baseline; only this actor's changing raw bone values travel per input.
            if (interactiveBaseline?.IsCurrent(source) != true || interactiveActor != boneObject
                || !stableDrafts.SequenceEqual(fixedDrafts)
                || !RetainsBaselineBones(boneDrafts))
            {
                var baselineMotions = drafts.GroupBy(draft => draft.ObjectId)
                    .ToDictionary(group => group.Key, group => CreateMotion(source, group.Key, group.ToArray()));
                interactiveBaseline = source.CapturePosePreviewRequest(baselineMotions);
                interactiveActor = boneObject; fixedDrafts = stableDrafts;
                baselineBoneIndices = boneDrafts.Select(draft => draft.BoneIndex!.Value).ToArray();
            }
            committed = interactiveBaseline;
            if (committed.SupportsInteractiveBones)
                kind = drafts.Count == 0 ? AsyncPreviewKind.Prewarm
                    : interactive ? AsyncPreviewKind.InteractiveBones
                    : continueRelease ? AsyncPreviewKind.ReleasedBones : AsyncPreviewKind.Complete;
            else committed = null;
        }
        if (drafts.Count == 0 && kind != AsyncPreviewKind.Prewarm) { ClearAsync(); return null; }
        bool prepareAfter = prewarm && committed is not null && kind == AsyncPreviewKind.Complete;
        if (sameSource && sameInputs && kind == asyncKind && prepareAfter == asyncPrewarm
            && drafts.SequenceEqual(asyncDrafts)) return kind == AsyncPreviewKind.Prewarm ? null : asyncSnapshot;
        bool changedBaseline = kind != AsyncPreviewKind.Complete && !ReferenceEquals(committed, asyncSource);
        if (!sameSource || !sameInputs || kind == AsyncPreviewKind.Prewarm || changedBaseline) asyncSnapshot = null;
        if (!sameSource || !sameInputs || kind != asyncKind || changedBaseline) asyncPublicationEpoch++;
        ScenePosePreviewRequest captured;
        IReadOnlyDictionary<int, BoneLocalTransform>? bones = null;
        if (kind == AsyncPreviewKind.Complete)
        {
            var motions = drafts.GroupBy(draft => draft.ObjectId)
                .ToDictionary(group => group.Key, group => CreateMotion(source, group.Key, group.ToArray()));
            captured = source.CapturePosePreviewRequest(motions);
        }
        else
        {
            captured = committed!;
            if (kind is AsyncPreviewKind.InteractiveBones or AsyncPreviewKind.ReleasedBones)
                bones = new System.Collections.ObjectModel.ReadOnlyDictionary<int, BoneLocalTransform>(boneDrafts
                    .ToDictionary(draft => draft.BoneIndex!.Value, draft => new BoneLocalTransform(draft.Translation, draft.Rotation)));
        }
        var inputs = Array.AsReadOnly(requests.ToArray());
        asyncSource = captured; asyncDrafts = drafts; asyncInputs = inputs; asyncKind = kind; asyncPrewarm = prepareAfter;
        asyncGeneration++;
        RemainingSettleSteps = kind == AsyncPreviewKind.ReleasedBones ? ReleaseSettleSubsteps : 0;
        SetState(kind switch
        {
            AsyncPreviewKind.Prewarm => EditorPosePreviewState.Source,
            AsyncPreviewKind.InteractiveBones => EditorPosePreviewState.InteractiveBones,
            AsyncPreviewKind.ReleasedBones => EditorPosePreviewState.ReleasedBones,
            _ => EditorPosePreviewState.Complete
        });
        SetPreparingPreview(kind != AsyncPreviewKind.Prewarm);
        var work = new AsyncWork(asyncGeneration, asyncPublicationEpoch, source, captured, inputs, new(), requestRender, reportError,
            kind, boneObject, bones, Stopwatch.GetTimestamp(), prepareAfter ? committed : null,
            kind == AsyncPreviewKind.ReleasedBones ? ReleaseSettleSubsteps : 0, InputId: EditorInteractionTrace.CurrentInput,
            InputSessionId: InteractionDiagnostics.SessionId);
        lock (workerGate)
        {
            workerGeneration = work.Generation;
            // Same-source pointer updates must not restart either the initial warm-up or a short
            // continuous physical step. Their completed snapshot is still generation-checked below.
            bool preserveWarmStep = !workerResetRequested && activeWork is { Kind: not AsyncPreviewKind.Complete } active
                && kind != AsyncPreviewKind.Complete && ReferenceEquals(active.Request, captured);
            if (!preserveWarmStep) activeCancellation?.Cancel();
            if (pendingWork is { } superseded) InteractionDiagnostics.Record("pose.superseded", superseded.DiagnosticInputId, superseded.Generation);
            pendingWork?.Cancellation.Cancel(); pendingWork?.Cancellation.Dispose();
            pendingWork = work;
            if (InteractionDiagnostics.IsEnabled) InteractionDiagnostics.Record("pose.admitted", work.DiagnosticInputId, work.Generation, detail: work.Kind.ToString());
            Monitor.PulseAll(workerGate);
            StartWorker();
        }
        return kind == AsyncPreviewKind.Prewarm ? null : asyncSnapshot;
    }

    // Called under workerGate. A retired task may leave its graph cached for the next worker task;
    // the workerRunning flag still means actual outstanding work, preserving host idle diagnostics.
    private void StartWorker()
    {
        if (!workerRunning) { workerRunning = true; _ = Task.Run(ProcessPending); }
    }

    private void ProcessPending()
    {
        while (true)
        {
            AsyncWork? work;
            bool reset;
            lock (workerGate)
            {
                // Only low-priority settling waits. A new input or Clear replaces/wakes this
                // slot immediately; the same worker retains exclusive ownership of the solver.
                while (!workerResetRequested && pendingWork is { Kind: AsyncPreviewKind.SettlingBones } delayed)
                {
                    double milliseconds = (delayed.NotBeforeTimestamp - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency;
                    if (milliseconds <= 0) break;
                    Monitor.Wait(workerGate, TimeSpan.FromMilliseconds(milliseconds));
                }
                reset = workerResetRequested; workerResetRequested = false;
                work = pendingWork; pendingWork = null;
                if (work is null && !reset) { workerRunning = false; return; }
                activeWork = work; activeCancellation = work?.Cancellation;
            }
            if (reset) RetireWarmOwner();
            if (work is null) continue;
            if (InteractionDiagnostics.IsEnabled) InteractionDiagnostics.Record("pose.dequeued", work.DiagnosticInputId, work.Generation,
                elapsedTicks: Stopwatch.GetTimestamp() - work.InputTimestamp);
            SceneProjectRenderSnapshot? snapshot = null;
            Exception? failure = null;
            try
            {
                using var evaluationMeasure = InteractionDiagnostics.Measure("pose.evaluate", work.DiagnosticInputId, work.Generation);
                var token = work.Cancellation.Token;
                token.ThrowIfCancellationRequested();
                if (work.Kind == AsyncPreviewKind.Complete)
                {
                    RetireWarmOwner();
                    // This stateless host factory always constructs fresh Bullet/Bepu/soft owners.
                    // Never call CreatePosePreview or UpdatePosePreviewMotions on the live session.
                    SceneProjectSession owner;
                    using (PreviewMeasure.Start("complete.create-session"))
                        owner = work.Request.CreateSession(createPhysicsProvider(), token);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        using var captureMeasure = PreviewMeasure.Start("complete.capture-snapshot");
                        snapshot = owner.CaptureRenderSnapshot(work.Inputs);
                    }
                    finally
                    {
                        using var disposeMeasure = PreviewMeasure.Start("complete.dispose-session");
                        owner.Dispose();
                    }
                }
                else
                {
                    if (warmOwner is null || !ReferenceEquals(warmSource, work.Request))
                    {
                        RetireWarmOwner();
                        warmOwner = work.Request.CreateSession(createPhysicsProvider(), token);
                        warmSource = work.Request;
                        lock (workerGate) workerHasWarmOwner = true;
                    }
                    if (work.Kind is AsyncPreviewKind.InteractiveBones or AsyncPreviewKind.ReleasedBones or AsyncPreviewKind.SettlingBones)
                    {
                        double elapsed = work.Kind switch
                        {
                            // Apply the last raw pointer input before doing any further physical work.
                            AsyncPreviewKind.ReleasedBones => 0,
                            // This is a simulation budget, not measured wall-clock elapsed time.
                            AsyncPreviewKind.SettlingBones => SettleBatchSubsteps * PhysicsStepSeconds,
                            _ => warmLastInputTimestamp == 0 ? PhysicsStepSeconds
                                : Math.Clamp((work.InputTimestamp - warmLastInputTimestamp) / (double)Stopwatch.Frequency,
                                    PhysicsStepSeconds, 1d / 30)
                        };
                        using (PreviewMeasure.Start(work.Kind switch
                        {
                            AsyncPreviewKind.ReleasedBones => "release.apply-latest",
                            AsyncPreviewKind.SettlingBones => "release.settle-batch",
                            _ => "interactive.step"
                        }))
                            warmOwner.PreviewInteractiveBones(work.BoneObjectId, work.Bones!, elapsed, token);
                        if (work.Kind != AsyncPreviewKind.SettlingBones) warmLastInputTimestamp = work.InputTimestamp;
                        token.ThrowIfCancellationRequested();
                        snapshot = warmOwner.CaptureRenderSnapshot(work.Inputs);
                    }
                }
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (work.Cancellation.IsCancellationRequested) { }
            catch (Exception error) { failure = error; }
            bool cancelled;
            lock (workerGate)
            {
                cancelled = work.Cancellation.IsCancellationRequested;
                activeWork = null; activeCancellation = null; work.Cancellation.Dispose();
            }
            if (cancelled || failure is not null) RetireWarmOwner();
            if (cancelled) continue;
            // Prewarming is an optional preparation, never a visible update or error notification.
            if (work.Kind == AsyncPreviewKind.Prewarm) continue;
            var completed = snapshot; var errorResult = failure;
            long publicationPostedAt = InteractionDiagnostics.IsEnabled ? Stopwatch.GetTimestamp() : 0;
            InteractionDiagnostics.Record("pose.publish-post", work.DiagnosticInputId, work.Generation);
            Dispatcher.UIThread.Post(() =>
            {
                InteractionDiagnostics.Record("pose.publish-callback", work.DiagnosticInputId, work.Generation,
                    elapsedTicks: publicationPostedAt == 0 ? 0 : Stopwatch.GetTimestamp() - publicationPostedAt);
                if (disposed || asyncPublicationEpoch != work.PublicationEpoch || work.Generation < lastPublishedGeneration
                    || !work.Request.IsCurrent(work.Source)) return;
                if (work.Kind == AsyncPreviewKind.InteractiveBones)
                {
                    // A short complete step may finish while a newer pointer sample is pending.
                    // Publish it monotonically within this drag, or fast input could starve display.
                    // A source/input change, release, Clear or disposal closes the publication epoch.
                    if (asyncKind != AsyncPreviewKind.InteractiveBones || asyncSource?.HasSameSource(work.Request) != true
                        || !SameInputs(asyncInputs, work.Inputs)) return;
                }
                else if (asyncGeneration != work.Generation || !ReferenceEquals(asyncSource, work.Request)) return;
                if (asyncGeneration == work.Generation) SetPreparingPreview(false);
                // IsCurrent is deliberately called only on the original UI owner thread.
                if (errorResult is not null)
                {
                    if (asyncGeneration == work.Generation)
                    {
                        if (work.Kind is AsyncPreviewKind.InteractiveBones or AsyncPreviewKind.ReleasedBones or AsyncPreviewKind.SettlingBones)
                            RequestCompleteFallback(work);
                        else work.ReportError?.Invoke(errorResult);
                    }
                }
                else if (completed is not null)
                {
                    if (work.InputSessionId == InteractionDiagnostics.SessionId)
                        EditorInteractionTrace.Published(completed, work.DiagnosticInputId, work.Generation);
                    InteractionDiagnostics.Record("pose.published", work.DiagnosticInputId, work.Generation);
                    lastPublishedGeneration = work.Generation; asyncSnapshot = completed;
                    if (work.Kind is AsyncPreviewKind.ReleasedBones or AsyncPreviewKind.SettlingBones)
                    {
                        RemainingSettleSteps = work.RemainingSettleSteps;
                        if (RemainingSettleSteps == 0) SetState(EditorPosePreviewState.SettledBones);
                    }
                    SnapshotPublished?.Invoke(this, EventArgs.Empty);
                    work.RequestRender();
                }
            }, DispatcherPriority.Background);
            if (failure is null && work.Kind is (AsyncPreviewKind.ReleasedBones or AsyncPreviewKind.SettlingBones)
                && work.RemainingSettleSteps > 0)
            {
                lock (workerGate)
                {
                    // Pace each pair of 1/120 steps by at least 1/60 wall seconds. Heavy sampling
                    // may take longer; never rush a backlog or replace the latest user request.
                    if (!workerResetRequested && workerGeneration == work.Generation && pendingWork is null)
                        pendingWork = work with { Cancellation = new(), Kind = AsyncPreviewKind.SettlingBones,
                            RemainingSettleSteps = work.RemainingSettleSteps - SettleBatchSubsteps,
                            NotBeforeTimestamp = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency / 60d) };
                }
            }
            if (failure is null && work.PrewarmAfter is { } nextWarm)
            {
                lock (workerGate)
                {
                    // After an ordinary complete numeric/morph preview, prepare its baseline for
                    // the next drag. Only one graph exists at a time; pending user work wins.
                    if (!workerResetRequested && workerGeneration == work.Generation && pendingWork is null)
                        pendingWork = work with { Request = nextWarm, Cancellation = new(), Kind = AsyncPreviewKind.Prewarm,
                            Bones = null, PrewarmAfter = null };
                }
            }
        }
    }

    private void RequestCompleteFallback(AsyncWork failed)
    {
        // A failed warm boundary may already have advanced native state. The worker retires it;
        // reconstruct a canonical detached request from the still-current UI drafts only once.
        try
        {
            var motions = asyncDrafts.GroupBy(draft => draft.ObjectId)
                .ToDictionary(group => group.Key, group => CreateMotion(failed.Source, group.Key, group.ToArray()));
            var captured = failed.Source.CapturePosePreviewRequest(motions);
            asyncSource = captured; asyncKind = AsyncPreviewKind.Complete; asyncPrewarm = false;
            asyncGeneration++; asyncPublicationEpoch++;
            RemainingSettleSteps = 0; SetState(EditorPosePreviewState.Complete); SetPreparingPreview(true);
            var retry = failed with { Generation = asyncGeneration, PublicationEpoch = asyncPublicationEpoch,
                Request = captured, Cancellation = new(), Kind = AsyncPreviewKind.Complete, Bones = null,
                PrewarmAfter = null, RemainingSettleSteps = 0, NotBeforeTimestamp = 0 };
            lock (workerGate)
            {
                workerGeneration = retry.Generation;
                activeCancellation?.Cancel();
                pendingWork?.Cancellation.Cancel(); pendingWork?.Cancellation.Dispose(); pendingWork = retry;
                Monitor.PulseAll(workerGate); StartWorker();
            }
        }
        catch (Exception error)
        { SetPreparingPreview(false); failed.ReportError?.Invoke(error); }
    }

    private void RetireWarmOwner()
    {
        using var measure = PreviewMeasure.Start("retire-warm-owner");
        var previous = warmOwner;
        warmOwner = null; warmSource = null; warmLastInputTimestamp = 0;
        lock (workerGate) workerHasWarmOwner = false;
        previous?.Dispose();
    }

    [EventSource(Name = "MDA-PosePreview")]
    private sealed class PreviewEventSource : EventSource
    {
        internal static readonly PreviewEventSource Log = new();
        [Event(1, Level = EventLevel.Informational)]
        public void Phase(string stage,long elapsedTicks,long allocatedBytes)
            => WriteEvent(1,stage,elapsedTicks,allocatedBytes);
    }

    private readonly struct PreviewMeasure : IDisposable
    {
        private readonly string? stage;
        private readonly long startTicks,startBytes;
        private PreviewMeasure(string stage)
        { this.stage=stage;startTicks=Stopwatch.GetTimestamp();startBytes=GC.GetAllocatedBytesForCurrentThread(); }
        internal static PreviewMeasure Start(string stage)=>PreviewEventSource.Log.IsEnabled()?new(stage):default;
        public void Dispose()
        {
            if(stage is not null)PreviewEventSource.Log.Phase(stage,Stopwatch.GetTimestamp()-startTicks,
                GC.GetAllocatedBytesForCurrentThread()-startBytes);
        }
    }

    private static bool SameInputs(IReadOnlyList<SceneObjectRenderRequest> a, IReadOnlyList<SceneObjectRenderRequest> b)
        => a.Count == b.Count && a.Zip(b).All(pair => pair.First.ObjectId == pair.Second.ObjectId
            && pair.First.IncludeSourceGeometry == pair.Second.IncludeSourceGeometry
            && pair.First.BoneIndices.SequenceEqual(pair.Second.BoneIndices)
            && pair.First.LocalTextureIndices.SequenceEqual(pair.Second.LocalTextureIndices)
            && pair.First.OptionalLocalTextureIndices.SequenceEqual(pair.Second.OptionalLocalTextureIndices));

    private bool RetainsBaselineBones(EditorAnimationPoseDraft[] drafts)
    {
        // SnapshotDrafts orders each actor's bones by index. Missing overrides must
        // not resurrect a bone stored in the initial settled baseline. Value-only
        // pointer changes keep that baseline and its warm physical owner.
        foreach (int bone in baselineBoneIndices)
        {
            int first=0,last=drafts.Length-1;
            while(first<=last)
            {
                int middle=first+(last-first)/2, value=drafts[middle].BoneIndex!.Value;
                if(value==bone){first=middle;break;}
                if(value<bone)first=middle+1;else last=middle-1;
            }
            if(first>=drafts.Length||drafts[first].BoneIndex!=bone)return false;
        }
        return true;
    }

    private void ClearAsync()
    {
        interactiveBaseline = null; interactiveActor = Guid.Empty; fixedDrafts = []; baselineBoneIndices = [];
        SetPreparingPreview(false);
        asyncGeneration++; asyncPublicationEpoch++; asyncSource = null; asyncDrafts = []; asyncInputs = []; asyncSnapshot = null;
        asyncKind = AsyncPreviewKind.Complete; asyncPrewarm = false;
        RemainingSettleSteps = 0; SetState(EditorPosePreviewState.Source);
        lock (workerGate)
        {
            workerGeneration = asyncGeneration;
            activeCancellation?.Cancel();
            pendingWork?.Cancellation.Cancel(); pendingWork?.Cancellation.Dispose(); pendingWork = null;
            Monitor.PulseAll(workerGate);
            // Solver disposal stays on the worker and never blocks the UI on native cleanup.
            if (workerRunning || workerHasWarmOwner) { workerResetRequested = true; StartWorker(); }
        }
    }

    /// <summary>Returns the detached preview, or null when the host should render its live source.
    /// Repeated viewport captures reuse the session across selection changes. Seek, commit and Undo/Redo
    /// invalidate its source. A changed actor's drafts rebind only that actor where its graph permits.</summary>
    public SceneProjectSession? Update(SceneProjectSession? source, EditorAnimationViewModel editor,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ArgumentNullException.ThrowIfNull(editor);
        var drafts = editor.SnapshotDrafts();
        if (source is null || drafts.Count == 0
            || source.CurrentFrame != editor.CurrentFrame)
        {
            Clear(); return null;
        }

        bool reuse = preview is not null && preview.CanReusePosePreview(source);
        if (reuse && drafts.SequenceEqual(current)) return preview;

        var motions = new Dictionary<Guid, VmdMotion>();
        foreach (var group in drafts.GroupBy(draft => draft.ObjectId))
            motions.Add(group.Key, reuse && group.SequenceEqual(current.Where(draft => draft.ObjectId == group.Key))
                && currentMotions.TryGetValue(group.Key, out var prior) ? prior : CreateMotion(source, group.Key, group.ToArray()));
        if (reuse)
        {
            preview!.UpdatePosePreviewMotions(motions, cancellationToken);
            current = drafts; currentMotions = motions;
        }
        else
        {
            // Prepare before retiring the previous result. Failures never publish a partial preview.
            var candidate = source.CreatePosePreview(motions, cancellationToken);
            var previous = preview;
            preview = candidate; current = drafts; currentMotions = motions;
            previous?.Dispose();
        }
        return preview;
    }

    private static VmdMotion CreateMotion(SceneProjectSession source, Guid objectId, IReadOnlyList<EditorAnimationPoseDraft> drafts)
    {
        var binding = source.CaptureAuthoredContent().Motions.SingleOrDefault(item => item.ObjectId == objectId)
            ?? throw new InvalidOperationException("The selected model has no editable motion binding.");
        var bones = new List<VmdBoneKeyframe>();
        var morphs = new List<VmdMorphKeyframe>();
        foreach (var draft in drafts)
        {
            uint frame = checked((uint)draft.Frame);
            if (draft.BoneIndex is { } boneIndex)
            {
                if ((uint)boneIndex >= (uint)binding.Model.Bones.Count) throw new ArgumentOutOfRangeException(nameof(drafts));
                var bone = binding.Model.Bones[boneIndex];
                var template = binding.Motion.BoneKeyframes.LastOrDefault(key => key.BoneName == bone.Name && key.Frame == frame);
                bones.Add(EditorAnimationViewModel.CreateDraftKey(bone, frame, draft.Translation, draft.Rotation, template));
            }
            else if (draft.MorphIndex is { } morphIndex && draft.MorphWeight is { } weight)
            {
                if ((uint)morphIndex >= (uint)binding.Model.Morphs.Count) throw new ArgumentOutOfRangeException(nameof(drafts));
                morphs.Add(new() { MorphName = binding.Model.Morphs[morphIndex].Name, Frame = frame, Weight = weight });
            }
            else throw new InvalidOperationException("An editing preview requires a valid bone or morph draft.");
        }

        // Existing registration validation/name provenance and raw local-pose semantics are reused.
        // This private editor owns only its temporary motion; the project's Undo and keys are untouched.
        var editor = new MotionEditor(binding.Motion);
        editor.Apply(editor.PrepareRegister(new() { BoneKeyframes = bones, MorphKeyframes = morphs }), confirmOverwrite: true);
        return editor.ExportMotion();
    }

    public void Clear()
    {
        ClearAsync();
        var previous = preview;
        preview = null; current = Array.Empty<EditorAnimationPoseDraft>(); currentMotions = new Dictionary<Guid, VmdMotion>();
        previous?.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        Clear(); disposed = true;
    }
}
