using System.Numerics;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.ViewOutput;

namespace Mmd.Modern.App;

/// <summary>
/// Formal editor viewport. The UI owns and evaluates the borrowed project; only detached
/// render snapshots cross to the serialized GPU worker. This host never advances or disposes it.
/// </summary>
public sealed partial class EditorViewportHost : NativeControlHost, IDisposable
{
    private static readonly PmmCameraPose OriginalCameraPose = new(new(0, 10, 0), Vector3.Zero, -45, 30, true);
    private readonly object workerGate = new();
    private readonly EditorPoseFrameAdmission poseFrameAdmission = new();
    private Task workerTask = Task.CompletedTask;
    private CancellationTokenSource? frameWaitCancellation;
    private CancellationTokenSource? aheadCancellation;
    private bool framePermitReady;
    private bool playbackActive, recordingAheadReady;
    private long recordingEpoch;
    private int presentedWidth, presentedHeight;
    private RenderWork? pending;
    private bool workerRunning, accepting, rendererReady, failed, disposing, disposed, capturePosted, captureInProgress;
    private long demandSequence, capturedDemandSequence, capturePostToken;
    private long revision, historyGeneration, cameraCutKey, preparationToken;
    private long capturePostedAt;
    private DiagnosticTimelineObservation latestDiagnosticTimeline;
    private IReadOnlyList<SceneObjectRenderRequest> sceneInputRequests = [];
    // The renderer is accessed only by the worker, or after StopRenderer has joined it.
    private IEditorViewportRenderer? renderer;
    private IEditorRenderInitializationCleanup? failedCreationCleanup;
    private readonly Func<(int Width, int Height)>? captureSizeProvider;
    private readonly Func<ViewRenderRequest, IEditorViewportRenderer>? headlessRendererFactory;
    private long rendererRevision = -1;
    private int rendererWidth, rendererHeight;
    private nint childWindow, previousWindowProcedure;
    private WindowProcedure? inputProcedure;
    private SceneProjectSession? project;
    private SceneCamera emptyCamera = OriginalCameraPose.CreateCamera();
    private SceneCamera currentCamera = OriginalCameraPose.CreateCamera();
    private PmmCameraPose? manualCameraPose, sampledCameraPose;
    private PmmLightPose? manualLightPose;
    private bool showWorldGuides = true;
    private ViewQualityOptions quality = EditorPreferences.DefaultPreviewQuality;
    private IEditorRenderEngine renderEngine = EditorModernRenderEngine.Instance;
    private IEditorRenderBinding renderBinding = EditorModernRenderEngine.Instance.EmptyBinding;
    private uint dragButton;
    private int pointerX, pointerY;
    private PmmCameraPose dragCamera;
    private PmmCameraPose? cameraBeforeDrag;

    public EditorViewportHost()
    {
        Focusable = true;
        SizeChanged += (_, _) => { lock (workerGate) InvalidateRecordingAheadLocked(); RequestRender(); };
    }

    internal EditorViewportHost(Func<(int Width, int Height)> captureSize,
        Func<ViewRenderRequest, IEditorViewportRenderer> rendererFactory) : this()
    {
        ArgumentNullException.ThrowIfNull(captureSize); ArgumentNullException.ThrowIfNull(rendererFactory);
        Dispatcher.UIThread.VerifyAccess();
        captureSizeProvider = captureSize; headlessRendererFactory = rendererFactory; accepting = true;
    }

    internal EditorViewportHost(Func<(int Width, int Height)> captureSize, IEditorRenderEngine engine) : this()
    {
        ArgumentNullException.ThrowIfNull(captureSize);
        Dispatcher.UIThread.VerifyAccess();
        captureSizeProvider = captureSize;
        SetRenderEngine(engine);
        accepting = true;
    }

    internal IEditorRenderEngine RenderEngine => renderEngine;
    internal long PresentationGeneration { get { lock (workerGate) return revision; } }

    internal void SetRenderEngine(IEditorRenderEngine engine)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(engine);
        if (ReferenceEquals(renderEngine, engine)) return;
        var empty = EditorRenderEngineBinding.Require(engine, engine.EmptyBinding);
        renderEngine = engine;
        renderBinding = empty;
        quality = EditorRenderEngineOptions.DefaultPreviewQuality(engine);
        RestartRenderer();
    }

    private bool HasCaptureSurface => childWindow != nint.Zero || captureSizeProvider is not null;

    private bool TryGetCaptureSize(out int width, out int height)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (captureSizeProvider is not null) (width, height) = captureSizeProvider();
        else if (childWindow != nint.Zero && GetClientRect(childWindow, out var bounds))
        { width = bounds.Right - bounds.Left; height = bounds.Bottom - bounds.Top; }
        else { width = height = 0; }
        // The D3D12 renderer and swapchain accept 1..4096 per axis (the capability probe already clamps the same
        // way). A 5K/8K or multi-monitor window used to fail on the first frame and latch the preview as failed.
        width = Math.Min(width, MaximumRenderDimension); height = Math.Min(height, MaximumRenderDimension);
        return width > 0 && height > 0;
    }

    internal const int MaximumRenderDimension = 4096;

    /// <summary>Borrowed UI-thread session. Replacing it releases GPU caches, never the session.</summary>
    public SceneProjectSession? Project
    {
        get => project;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            if (ReferenceEquals(project, value)) return;
            project = value; manualCameraPose = sampledCameraPose = null;
            renderBinding = EditorRenderEngineBinding.Require(renderEngine, renderEngine.EmptyBinding);
            historyGeneration++; cameraCutKey++;
            RestartRenderer();
        }
    }

    public SceneCamera EmptyCamera
    {
        get => emptyCamera;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            ArgumentNullException.ThrowIfNull(value);
            _ = value.ViewProjection(1, 1);
            emptyCamera = value;
            if (project is null && manualCameraPose is null) currentCamera = value;
            RequestRender();
        }
    }

    public bool ShowWorldGuides
    {
        get => showWorldGuides;
        set { Dispatcher.UIThread.VerifyAccess(); if (showWorldGuides == value) return; showWorldGuides = value; RequestRender(); }
    }

    /// <summary>Per-view quality. Project rendering parameters remain unchanged.</summary>
    public ViewQualityOptions Quality
    {
        get => quality;
        set
        {
            Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(value); value.Validate();
            if (quality == value) return;
            quality = value; PrepareNextFrame();
        }
    }

    public SceneCamera CurrentCamera => currentCamera;
    /// <summary>Machine-local camera controls; object/bone/light gestures keep screen-space direction.</summary>
    public bool InvertCameraMouseX { get; set; }
    public bool InvertCameraMouseY { get; set; }
    public PmmCameraPose? ManualCameraPose => manualCameraPose;
    /// <summary>The current world-space orbit center, including a manual or sampled camera. Read only on the UI thread.</summary>
    public PmmCameraPose LightCreationCamera
    {
        get { Dispatcher.UIThread.VerifyAccess(); return EditableCamera(); }
    }
    public bool HasChildWindow => childWindow != nint.Zero;
    public string? AdapterName { get; private set; }
    public bool IsWarp { get; private set; }
    public EditorViewportPresentationStatistics? Statistics { get; private set; }
    public event Action<Exception>? Error;
    public event Action<SceneCamera>? CameraChanged;
    public event Action<PmmCameraPose>? CameraPoseChanged;
    public event Action<EditorViewportPresentationStatistics>? FramePresented;
    public event EventHandler<EditorViewportKeyEventArgs>? EditorKeyPressed;
    public Func<IReadOnlyList<SceneObjectRenderRequest>, SceneProjectRenderSnapshot>? SnapshotProvider { get; set; }
    // Read only after SnapshotProvider has requested the latest raw pose input on the UI thread.
    internal Func<bool>? PoseInputPending { get; set; }
    internal long RepeatedPoseFramesSuppressed { get { lock (workerGate) return poseFrameAdmission.SuppressedFrames; } }
    public Func<PreviewFrame, PreviewFrame>? FrameDecorator { get; set; }
    public EditorLightViewportController? LightController { get; set; }
    public EditorBoneViewportController? BoneController { get; set; }
    public bool RotateLightHandle { get; set; }
    private bool isSuspended;
    private bool requireCurrentPoseInput;
    private Func<bool>? currentPoseInputPending;
    /// <summary>After restoring editor drafts, retain the previous image until the first snapshot
    /// contains the latest input. Pose publication already requests the next capture.</summary>
    internal void RequireCurrentPoseInputForNextFrame(Func<bool>? pendingInput = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!disposed) { requireCurrentPoseInput = true; currentPoseInputPending = pendingInput; }
    }
    public bool IsSuspended
    {
        get => isSuspended;
        set
        {
            if (isSuspended == value) return;
            isSuspended = value;
            lock (workerGate)
            {
                poseFrameAdmission.Clear();
                if (value) { InvalidateRecordingAheadLocked(); frameWaitCancellation?.Cancel(); framePermitReady = false; pending = null; capturedDemandSequence = demandSequence; capturePosted = false; capturePostToken++; }
            }
            if (!value) RequestRender();
        }
    }
    public bool IsPreparingFrame { get; private set; }
    public bool HasRendererError { get; private set; }
    public event EventHandler? PreparationChanged;
    private void SetPreparingFrame(bool value)
    {
        if (IsPreparingFrame == value) return;
        IsPreparingFrame = value; PreparationChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Keep preparation visible until a frame captured after this request is presented.</summary>
    public void PrepareNextFrame()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (disposed || HasRendererError) return;
        lock (workerGate) InvalidateRecordingAheadLocked();
        preparationToken++;
        SetPreparingFrame(true);
        RequestRender();
    }

    public void SetLightPose(PmmLightPose? pose)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (manualLightPose != pose) lock (workerGate) InvalidateRecordingAheadLocked();
        manualLightPose = pose; RequestRender();
    }

    /// <summary>Freeze the selected engine's inputs after project load, edit or style changes.</summary>
    internal void SetRenderBinding(IEditorRenderBinding binding)
    {
        Dispatcher.UIThread.VerifyAccess();
        var selected = EditorRenderEngineBinding.Require(renderEngine, binding);
        if (ReferenceEquals(renderBinding, selected)) return;
        renderBinding = selected;
        RestartRenderer();
    }

    /// <summary>Null returns to the project's sampled camera. PMM parented drafts retain local
    /// camera-space values and resolve against the target's currently evaluated world transform.</summary>
    public void SetCameraPose(PmmCameraPose? pose)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (pose is { } camera)
        {
            _ = ResolveCamera(camera).ViewProjection(1, 1);
        }
        if (manualCameraPose != pose) lock (workerGate) InvalidateRecordingAheadLocked();
        manualCameraPose = pose; RequestRender();
    }

    /// <summary>Call after seek, Undo, reload, or a discontinuous camera edit.</summary>
    public void InvalidateTemporalHistory(bool cameraCut = false)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (cameraCut) cameraCutKey++; else historyGeneration++;
        lock (workerGate) InvalidateRecordingAheadLocked();
        RequestRender();
    }

    internal void SetPlaybackActive(bool active)
    {
        Dispatcher.UIThread.VerifyAccess();
        lock (workerGate)
        {
            if (playbackActive == active) return;
            playbackActive = active;
            if (!active) InvalidateRecordingAheadLocked();
        }
        if (!active) RequestRender();
    }

    private void InvalidateRecordingAheadLocked()
    {
        recordingEpoch++;
        recordingAheadReady = false;
        if (aheadCancellation is not null)
        {
            aheadCancellation.Cancel();
            poseFrameAdmission.Clear();
        }
    }

    private bool CanRecordAheadLocked() => playbackActive && recordingAheadReady
        && quality.EffectiveGi is { Strength: > 0, Mode: LightingMode.HardwareRayTracing }
        && quality.EffectiveTemporalUpscaling.Mode == ViewTemporalUpscalingMode.Off;

    /// <summary>Coalesces requests behind UI mutations/layout; captures the project only on the UI thread.</summary>
    public void RequestRender()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RequestRender, DispatcherPriority.Background);
            return;
        }
        if (!disposing && !disposed) ObserveDiagnosticTimeline();
        if (disposing || disposed || IsSuspended || !HasCaptureSurface) return;
        lock (workerGate)
        {
            if (!accepting || failed) return;
            demandSequence++;
            // A not-yet-dequeued snapshot must not delay a newer demand.
            pending = null;
            ScheduleCaptureLocked();
        }
    }

    // Caller holds workerGate. Posting is a notification only; the worker never waits for UI.
    private void ScheduleCaptureLocked()
    {
        if (!accepting || failed || disposed || IsSuspended || !HasCaptureSurface ||
            workerRunning || captureInProgress || capturePosted || demandSequence == capturedDemandSequence) return;
        // Wait before borrowing the UI-owned scene, not after capturing an already aging pose.
        // During this wait only demand is coalesced; no snapshot or UI callback is retained.
        if (rendererReady && !framePermitReady && !CanRecordAheadLocked())
        {
            workerRunning = true;
            var cancellation = frameWaitCancellation = new CancellationTokenSource();
            long permitRevision = revision;
            workerTask = Task.Run(() => WaitForFramePermitAsync(permitRevision, cancellation));
            return;
        }
        capturePosted = true;
        long version = revision, token = ++capturePostToken;
        capturePostedAt = InteractionDiagnostics.IsEnabled ? Stopwatch.GetTimestamp() : 0;
        InteractionDiagnostics.Record("capture.post", EditorInteractionTrace.CurrentInput);
        Dispatcher.UIThread.Post(() => CapturePendingFrame(version, token), DispatcherPriority.Background);
    }

    /// <summary>Retries a failed backend or rebuilds it after explicit mapping changes.</summary>
    public void RestartRenderer()
    {
        Dispatcher.UIThread.VerifyAccess();
        lock (workerGate) { InvalidateRecordingAheadLocked(); frameWaitCancellation?.Cancel(); framePermitReady = false; revision++; rendererReady = false; failed = false; pending = null; capturePosted = false; capturePostToken++; capturedDemandSequence = demandSequence; sceneInputRequests = []; poseFrameAdmission.Clear(); }
        HasRendererError = false; PrepareNextFrame();
    }

    /// <summary>Releases preview GPU resources before offline export while retaining the native
    /// viewport and its editing state. The caller keeps preview capture suspended until export ends.</summary>
    public async Task SuspendAndReleaseResourcesAsync(CancellationToken cancellationToken = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposing || disposed, this);
        if (!IsSuspended) throw new InvalidOperationException("Suspend preview capture before releasing its resources.");
        cancellationToken.ThrowIfCancellationRequested();
        Task<Exception?> release;
        lock (workerGate)
        {
            InvalidateRecordingAheadLocked();
            frameWaitCancellation?.Cancel(); framePermitReady = false;
            pending = null; revision++; rendererReady = false; capturePosted = false; capturePostToken++; capturedDemandSequence = demandSequence; sceneInputRequests = []; poseFrameAdmission.Clear();
            var running = workerTask;
            // Track cleanup as the worker too: native teardown must join it before destroying
            // the HWND. No UI callback is awaited by either the render worker or this cleanup.
            release = Task.Run(async () =>
            {
                try
                {
                    try { await running.ConfigureAwait(false); }
                    finally { await ReleaseRendererAsync().ConfigureAwait(false); }
                    return (Exception?)null;
                }
                // The awaiting operation reports cleanup errors. Keep the lifetime join task
                // successful so an already reported error cannot interrupt later HWND teardown.
                catch (Exception error) { return error; }
            });
            workerTask = release;
        }
        // Resource disposal is atomic. Finish it before observing cancellation so the caller
        // cannot resume preview capture while the old renderer is still being released.
        var releaseError = await release;
        ObjectDisposedException.ThrowIf(disposed, this);
        AdapterName = null; Statistics = null;
        if (releaseError is not null) ExceptionDispatchInfo.Capture(releaseError).Throw();
        cancellationToken.ThrowIfCancellationRequested();
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (captureSizeProvider is not null) throw new InvalidOperationException("A headless viewport cannot attach a native child.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The MDA editor viewport requires Windows.");
        ObjectDisposedException.ThrowIf(disposing || disposed, this);
        // SS_NOTIFY makes the STATIC child accept real mouse hit testing.
        childWindow = CreateWindowExW(0, "STATIC", "MDA editor viewport", 0x40000000 | 0x10000000 | 0x04000000 | 0x02000000 | 0x100,
            0, 0, 1, 1, parent.Handle, nint.Zero, nint.Zero, nint.Zero);
        if (childWindow == nint.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            inputProcedure = OnWindowMessage;
            previousWindowProcedure = SetWindowLongPtrW(childWindow, -4, Marshal.GetFunctionPointerForDelegate(inputProcedure));
            if (previousWindowProcedure == nint.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            lock (workerGate) { accepting = true; revision++; failed = false; poseFrameAdmission.Clear(); }
            RequestRender();
            return new PlatformHandle(childWindow, "HWND");
        }
        catch
        {
            DetachInput(); DestroyWindow(childWindow); childWindow = nint.Zero; throw;
        }
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        // Explicit window disposal may already have destroyed this owned child. Never
        // destroy a stale handle that Windows could since have reused for another window.
        if (control.Handle != childWindow) return;
        DetachInput();
        StopRenderer(); // Present/resize and GPU disposal finish while the HWND is still valid.
        if (control.Handle != nint.Zero) DestroyWindow(control.Handle);
        childWindow = nint.Zero;
    }

    private void CapturePendingFrame(long version, long token)
        => RunCapture(version, token, CapturePendingFrameCore);

    // Shared by the real callback and deterministic headless tests. Capture is always UI-owned.
    private void RunCapture(long version, long token, Action capture)
    {
        lock (workerGate)
        {
            if (!capturePosted || token != capturePostToken || version != revision) return;
            capturePosted = false;
            if (!accepting || failed || disposed || IsSuspended || !HasCaptureSurface) return;
            if (workerRunning || captureInProgress) return;
            captureInProgress = true;
            capturedDemandSequence = demandSequence;
        }
        try { capture(); }
        catch (Exception error)
        {
            long failureToken;
            lock (workerGate)
            {
                // Suspension invalidates the capture even if it resumes synchronously
                // without changing the renderer revision before this callback throws.
                if (revision != version || token != capturePostToken || !accepting || disposed || IsSuspended) return;
                failed = true; pending = null; capturedDemandSequence = demandSequence; capturePosted = false;
                failureToken = ++capturePostToken; poseFrameAdmission.Clear();
            }
            if (IsCurrentCapture(version, failureToken))
            {
                HasRendererError = true; SetPreparingFrame(false);
                if (IsCurrentCapture(version, failureToken)) Error?.Invoke(error);
            }
        }
        finally
        {
            lock (workerGate) { captureInProgress = false; ScheduleCaptureLocked(); }
        }
    }

    private void CapturePendingFrameCore()
    {
        using var captureMeasure = InteractionDiagnostics.Measure("capture.execute", EditorInteractionTrace.CurrentInput);
        if (capturePostedAt != 0) InteractionDiagnostics.Record("capture.queue-wait", EditorInteractionTrace.CurrentInput,
            elapsedTicks: Stopwatch.GetTimestamp() - capturePostedAt);
        if (disposed || IsSuspended || !TryGetCaptureSize(out int width, out int height)) return;
        long version;
        long epoch;
        bool ready;
        bool recordAhead;
        IReadOnlyList<SceneObjectRenderRequest> requests;
        lock (workerGate)
        {
            if (!accepting || failed) return;
            version = revision; epoch = recordingEpoch; ready = rendererReady; requests = sceneInputRequests;
            recordAhead = ready && !framePermitReady;
            if (recordAhead && (!CanRecordAheadLocked() || width != presentedWidth || height != presentedHeight))
            {
                InvalidateRecordingAheadLocked();
                demandSequence++;
                return;
            }
        }
        {
            // Initialization discovers the renderer's selected input requests first. Never
            // present a dedicated material with an incomplete first-frame input snapshot.
            SceneProjectRenderSnapshot? origin = null;
            long diagnosticSession = InteractionDiagnostics.SessionId;
            long captureStartedAt = diagnosticSession == 0 ? 0 : Stopwatch.GetTimestamp();
            var frame = ready ? CaptureFrame(width, height, requests, out origin) : null;
            if (frame is not null && requireCurrentPoseInput)
            {
                if ((currentPoseInputPending ?? PoseInputPending)?.Invoke() == true) return;
                requireCurrentPoseInput = false;
                currentPoseInputPending = null;
            }
            long captureCompletedAt = diagnosticSession == 0 ? 0 : Stopwatch.GetTimestamp();
            var timeline = ObserveDiagnosticTimeline();
            var candidate = new EditorPoseFrameCandidate(childWindow, width, height, version, quality, renderBinding,
                frame, preparationToken, origin, BoneController?.PresentationIdentity, LightController?.PresentationIdentity,
                BoneController?.IsDragging == true, LightController?.IsDragging == true,
                BoneController?.IsDragging == true && PoseInputPending?.Invoke() == true);
            var work = new RenderWork(childWindow, width, height, version, quality, renderBinding, frame, preparationToken, candidate,
                EditorInteractionTrace.InputFor(origin), diagnosticSession == 0 ? 0 : InteractionDiagnostics.NextId(),
                diagnosticSession == 0 ? 0 : Stopwatch.GetTimestamp(), diagnosticSession,
                diagnosticSession == 0 ? null : new(captureStartedAt, captureCompletedAt,
                    timeline.SessionId == diagnosticSession && timeline.Revision == version ? timeline.SourceFrame : null,
                    timeline.SessionId == diagnosticSession && timeline.Revision == version ? timeline.InputId : 0))
            { RecordingEpoch = epoch, RecordAhead = recordAhead };
            if (frame is not null) RecordWorkStage("capture.snapshot", work,
                elapsedTicks: captureStartedAt == 0 ? 0 : captureCompletedAt - captureStartedAt);
            QueueWork(work);
        }
    }

    private PreviewFrame CaptureFrame(int width, int height, IReadOnlyList<SceneObjectRenderRequest> requests,
        out SceneProjectRenderSnapshot? origin)
    {
        origin = null;
        PreviewFrame frame;
        if (project is { } owner)
        {
            var snapshot = SnapshotProvider?.Invoke(requests) ?? owner.CaptureRenderSnapshot(requests);
            origin = snapshot;
            // Keep this transport aligned with ViewPlaybackOwner.ProjectOwner.Snapshot.
            frame = new(width, height, snapshot.SourceFrame, snapshot.Scene, snapshot.Camera, snapshot.Light, snapshot.SelfShadow)
            {
                ResolvedCamera = snapshot.ResolvedCamera,
                CameraParentTransform = snapshot.CameraParentTransform,
                MorphChannels = snapshot.MorphChannels,
                RenderingStyles = snapshot.RenderingStyles,
                SceneInputs = snapshot.SceneInputs
            };
        }
        else
        {
            var empty = new OriginalFrameScene(new(SceneSnapshot.CreateEmpty(emptyCamera), []));
            frame = new(width, height, 0, empty, null, null, null) { ResolvedCamera = emptyCamera };
        }
        sampledCameraPose = frame.Camera;
        if (manualLightPose is { } light) frame = frame with { Light = light };
        if (manualCameraPose is { } editorCamera)
            frame = frame with
            {
                Camera = editorCamera, ResolvedCamera = ResolveCamera(editorCamera),
                CameraParentTransform = editorCamera.ParentModelIndex >= 0 ? project?.ResolveCameraParentTransform(editorCamera) : null
            };
        else if (frame.Camera is null && frame.ResolvedCamera is null)
            frame = frame with { ResolvedCamera = emptyCamera };

        currentCamera = frame.ResolvedCamera ?? frame.Camera?.CreateCamera() ?? emptyCamera;
        var scene = frame.Scene;
        var options = (scene.Options ?? new(OriginalAlphaBlendMode.Normal, true, false)) with { BackgroundColor = Vector4.One };
        var overlay = scene.EditorOverlay;
        if (showWorldGuides)
        {
            var guides = WorldGuides.BuildOverlay(currentCamera, width, height);
            var existing = overlay?.Lines.ToArray() ?? [];
            overlay = new(guides.Lines.Take(ViewportOverlaySnapshot.MaxLines - existing.Length).Concat(existing));
        }
        scene = new(scene.Surface, scene.Outline, scene.OutlineOptions, options, scene.Materials, scene.Lighting, overlay);
        frame = frame with { Scene = scene, TemporalState = new() { HistoryGeneration = historyGeneration, CameraCutKey = cameraCutKey } };
        return FrameDecorator?.Invoke(frame) ?? frame;
    }

    private void QueueWork(RenderWork work)
    {
        lock (workerGate)
        {
            if (!accepting || failed || disposed || IsSuspended || work.Revision != revision) return;
            if (pending is { } replaced) RecordWorkStage("render.superseded", replaced);
            pending = work;
            if (workerRunning) return;
            workerRunning = true;
            workerTask = Task.Run(RenderPendingAsync);
        }
    }

    // Admission belongs to the snapshot actually taken by the worker. A pending snapshot
    // superseded before dequeue must never become the last admitted pose.
    private RenderWork? TakePendingWork()
    {
        lock (workerGate)
        {
            var work = pending; pending = null;
            if (work is null || !accepting || failed || disposed || IsSuspended ||
                work.Revision != revision || demandSequence != capturedDemandSequence) return null;
            if (work.RecordAhead && (work.RecordingEpoch != recordingEpoch || !CanRecordAheadLocked())) return null;
            if (!poseFrameAdmission.TryAdmit(work.PoseCandidate))
            { RecordWorkStage("render.suppressed", work); return null; }
            if (work.Frame is not null) framePermitReady = false;
            if (work.RecordAhead) aheadCancellation = new();
            RecordWorkStage("render.admitted", work);
            return work;
        }
    }

    private async Task WaitForFramePermitAsync(long version, CancellationTokenSource cancellation)
    {
        try
        {
            long startedAt = InteractionDiagnostics.IsEnabled ? Stopwatch.GetTimestamp() : 0;
            await renderer!.WaitForNextFrameAsync(cancellation.Token).ConfigureAwait(false);
            if (startedAt != 0) InteractionDiagnostics.Record("render.frame-permit-wait", 0,
                elapsedTicks: Stopwatch.GetTimestamp() - startedAt);
            lock (workerGate)
            {
                if (!cancellation.IsCancellationRequested && accepting && !disposed && !IsSuspended && revision == version)
                    framePermitReady = true;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { await FailRendererAsync(error, version).ConfigureAwait(false); }
        finally
        {
            lock (workerGate)
            {
                frameWaitCancellation = null;
                cancellation.Dispose();
                CompleteActiveWorkLocked();
            }
        }
    }

    private async Task RenderPendingAsync()
    {
        {
            var work = TakePendingWork();
            if (work is null) { CompleteActiveWork(); return; }
            var cancellation = work.RecordAhead ? aheadCancellation! : null;
            RecordWorkStage("render.dequeued", work,
                elapsedTicks: work.QueuedAt == 0 ? 0 : Stopwatch.GetTimestamp() - work.QueuedAt);
            try
            {
                if (renderer is null || rendererRevision != work.Revision)
                {
                    await ReleaseRendererAsync().ConfigureAwait(false);
                    try
                    {
                        renderer = headlessRendererFactory is { } factory
                            ? factory(new(work.Width, work.Height, work.Quality))
                                ?? throw new InvalidOperationException("The viewport renderer factory returned null.")
                            : work.Binding.CreatePreview(work.Window, new(work.Width, work.Height, work.Quality))
                                ?? throw new InvalidOperationException("The selected render engine returned no viewport renderer.");
                    }
                    catch (Exception error)
                    {
                        if (error is IEditorRenderInitializationCleanup cleanup) failedCreationCleanup = cleanup;
                        throw;
                    }
                    rendererRevision = work.Revision; rendererWidth = work.Width; rendererHeight = work.Height;
                    var requests = Array.AsReadOnly(renderer.SceneInputRequests.ToArray());
                    string adapter = renderer.AdapterName;
                    bool warp = renderer.IsWarp;
                    lock (workerGate)
                    {
                        if (accepting && revision == work.Revision) { sceneInputRequests = requests; rendererReady = true; framePermitReady = false; demandSequence++; }
                    }
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!IsCurrent(work.Revision)) return;
                        AdapterName = adapter; IsWarp = warp;
                    }, DispatcherPriority.Background);
                    return;
                }
                if (work.Frame is not { } frame) return;
                await renderer.UpdateQualityAsync(work.Quality).ConfigureAwait(false);
                if (rendererWidth != work.Width || rendererHeight != work.Height)
                {
                    await renderer.ResizeAsync(work.Width, work.Height).ConfigureAwait(false);
                    rendererWidth = work.Width; rendererHeight = work.Height;
                }
                long presentStarted = InteractionDiagnostics.IsEnabled ? Stopwatch.GetTimestamp() : 0;
                RecordWorkStage("render.present-start", work);
                if (cancellation is not null)
                    await renderer.PresentAheadAsync(frame, cancellation.Token).ConfigureAwait(false);
                else await renderer.PresentAsync(frame).ConfigureAwait(false);
                RecordWorkStage("render.present-return", work,
                    elapsedTicks: presentStarted == 0 ? 0 : Stopwatch.GetTimestamp() - presentStarted);
                var statistics = renderer.Statistics;
                lock (workerGate)
                {
                    if (work.RecordingEpoch == recordingEpoch)
                    {
                        recordingAheadReady = renderer.SupportsRecordingAhead;
                        presentedWidth = work.Width; presentedHeight = work.Height;
                    }
                }
                if (InteractionDiagnostics.IsEnabled)
                {
                    void Stage(string name, double milliseconds, long value = 0, long bytes = 0)
                        => RecordWorkStage(name, work, value,
                            elapsedTicks: (long)(milliseconds * Stopwatch.Frequency / 1000d), allocatedBytes: bytes);
                    if (statistics.Frame is { } gpu)
                    {
                        if (gpu.RecordCpuMilliseconds is { } record)
                            Stage("render.record", record, gpu.FrameId ?? 0, gpu.RecordAllocatedBytes ?? 0);
                        if (gpu.LastSlotWaitMilliseconds is { } slotWait) Stage("render.slot-fence-wait", slotWait);
                        if (gpu.SubmitCpuMilliseconds is { } submit) Stage("render.submit", submit, gpu.SubmissionCount ?? 0);
                        if (gpu.SubmittedFence is { } submittedFence) Stage("gpu.submitted-fence", 0, unchecked((long)submittedFence));
                        if (gpu.CompletedFence is { } completedFence) Stage("gpu.completed-fence", 0, unchecked((long)completedFence));
                        if (gpu.PendingFrames is { } pendingFrames) Stage("gpu.pending-frames", 0, pendingFrames);
                        if (gpu.SceneRecordCpu is { } scene)
                        {
                            Stage("render.surface-bounds", scene.SurfaceBoundsBuild.CpuMilliseconds);
                            Stage("render.outline-bounds", scene.OutlineBoundsBuild.CpuMilliseconds);
                        }
                    }
                    if (statistics.Timing is { } timing) Stage("render.present-wait", timing.PresentWaitMilliseconds);
                }
                long callbackPostedAt = InteractionDiagnostics.IsEnabled ? Stopwatch.GetTimestamp() : 0;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsCurrent(work.Revision) || work.RecordAhead && !IsCurrentRecordingEpoch(work.RecordingEpoch)) return;
                    ObserveDiagnosticTimeline(); // UI-only clock refresh; this is not a scan-out observation.
                    RecordWorkStage("render.ui-presented", work,
                        elapsedTicks: callbackPostedAt == 0 ? 0 : Stopwatch.GetTimestamp() - callbackPostedAt);
                    BoneController?.FramePresented(frame);
                    LightController?.FramePresented(frame);
                    Statistics = statistics;
                    if (work.PreparationToken == preparationToken) SetPreparingFrame(false);
                    FramePresented?.Invoke(statistics);
                }, DispatcherPriority.Background);
            }
            catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true) { }
            catch (Exception error)
            {
                await FailRendererAsync(error, work.Revision).ConfigureAwait(false);
            }
            finally
            {
                lock (workerGate)
                {
                    aheadCancellation = null;
                    cancellation?.Dispose();
                    CompleteActiveWorkLocked();
                }
            }
        }
    }

    private async Task FailRendererAsync(Exception error, long version)
    {
        try { await ReleaseRendererAsync().ConfigureAwait(false); }
        catch (Exception cleanupError) { error.Data["ViewportCleanupError"] = cleanupError; }
        lock (workerGate)
        {
            if (revision == version) { failed = true; rendererReady = false; framePermitReady = false; pending = null; capturedDemandSequence = demandSequence; capturePosted = false; capturePostToken++; poseFrameAdmission.Clear(); }
        }
        Dispatcher.UIThread.Post(() => { if (IsCurrent(version)) { HasRendererError = true; SetPreparingFrame(false); Error?.Invoke(error); } }, DispatcherPriority.Background);
    }

    private void CompleteActiveWork()
    {
        lock (workerGate) CompleteActiveWorkLocked();
    }

    private void CompleteActiveWorkLocked()
    {
        workerRunning = false;
        ScheduleCaptureLocked();
    }

    // Only the UI thread reads the borrowed project clock. A worker sees the latest published
    // observation, not an assertion about the timeline at GPU execution or physical scan-out.
    private DiagnosticTimelineObservation ObserveDiagnosticTimeline()
    {
        long session = InteractionDiagnostics.SessionId;
        if (session == 0) return default;
        Dispatcher.UIThread.VerifyAccess();
        double? sourceFrame;
        try { sourceFrame = project?.CurrentFrame; }
        catch (ObjectDisposedException) { sourceFrame = null; } // Diagnostics must not fault teardown.
        long observedAt = Stopwatch.GetTimestamp(), input = EditorInteractionTrace.CurrentInput;
        lock (workerGate)
        {
            var observation = new DiagnosticTimelineObservation(session, revision, sourceFrame, observedAt, input);
            latestDiagnosticTimeline = observation;
            return observation;
        }
    }

    private void RecordWorkStage(string stage, RenderWork work, long value = 0,
        long elapsedTicks = 0, long allocatedBytes = 0)
    {
        long session = work.InputSessionId;
        if (!InteractionDiagnostics.IsEnabled || session == 0 || session != InteractionDiagnostics.SessionId) return;
        DiagnosticTimelineObservation timeline;
        lock (workerGate) timeline = latestDiagnosticTimeline;
        if (timeline.SessionId != session || timeline.Revision != work.Revision) timeline = default;
        long observedAt = Stopwatch.GetTimestamp();
        var capture = work.DiagnosticCapture;
        long completedAt = capture?.CompletedAt ?? 0;
        static string Number(double? frame) => frame?.ToString("R", CultureInfo.InvariantCulture) ?? "n/a";
        // These are Host transport creation timestamps, not the original pose/storage creation
        // time. Existing stage ElapsedTicks keep their original duration meanings. GPU statistics
        // are emitted after Present returns; stageObservedAt is the metadata observation time.
        string detail = string.Create(CultureInfo.InvariantCulture,
            $"sourceFrame={Number(work.Frame?.SourceFrame)};timelineAtCapture={Number(capture?.TimelineFrameAtCapture)};latestObservedTimelineFrame={Number(timeline.SourceFrame)};timelineObservedAt={timeline.ObservedAt};timelineInputId={timeline.InputId};captureInputId={capture?.ObservedInputId ?? 0};captureStartedAt={capture?.StartedAt ?? 0};captureCompletedAt={completedAt};queuedAt={work.QueuedAt};stageObservedAt={observedAt};captureAgeTicks={(completedAt == 0 ? -1 : observedAt - completedAt)};queueAgeTicks={(work.QueuedAt == 0 ? -1 : observedAt - work.QueuedAt)}");
        // RecordForSession pins one session reference before writing, closing the Start/Stop
        // race between the fast guard above and insertion into the bounded diagnostic ring.
        InteractionDiagnostics.RecordForSession(session, stage, work.InputId, work.FrameId, value,
            detail, elapsedTicks, allocatedBytes);
    }

    private bool IsCurrent(long version)
    {
        lock (workerGate) return !disposed && accepting && version == revision;
    }

    private bool IsCurrentRecordingEpoch(long epoch)
    {
        lock (workerGate) return epoch == recordingEpoch;
    }

    private bool IsCurrentCapture(long version, long token)
    {
        lock (workerGate) return !disposed && accepting && !IsSuspended && version == revision && token == capturePostToken;
    }

    private async ValueTask ReleaseRendererAsync()
    {
        if (failedCreationCleanup is { } cleanup)
        {
            await cleanup.DisposeAsync().ConfigureAwait(false);
            failedCreationCleanup = null;
        }
        if (renderer is { } previous)
        {
            await previous.DisposeAsync().ConfigureAwait(false);
            renderer = null;
        }
        rendererRevision = -1;
    }

    private void StopRenderer()
    {
        Task running;
        lock (workerGate)
        {
            accepting = false; pending = null; rendererReady = false; revision++; capturePosted = false; capturePostToken++; capturedDemandSequence = demandSequence; poseFrameAdmission.Clear();
            InvalidateRecordingAheadLocked();
            frameWaitCancellation?.Cancel(); framePermitReady = false;
            running = workerTask;
        }
        // Worker never awaits a UI callback. Both waits are bounded: a GPU that hangs without a driver reset, or a
        // stuck native call, used to freeze the window forever. Fence waits time out on their own (60 s); past this
        // bound the remaining release continues in the background so the window can still close.
        if (!WaitBounded(running, "render worker")) return;
        var release = Task.Run(async () => await ReleaseRendererAsync().ConfigureAwait(false));
        if (!WaitBounded(release, "renderer release")) return;
        release.GetAwaiter().GetResult();
        lock (workerGate) sceneInputRequests = [];
        AdapterName = null; Statistics = null;
        SetPreparingFrame(false);
    }

    internal static TimeSpan CloseWaitTimeout { get; set; } = TimeSpan.FromSeconds(15);

    private bool WaitBounded(Task task, string what)
    {
        try { if (task.Wait(CloseWaitTimeout)) return true; }
        catch (AggregateException) { return true; } // Observed by the caller or already reported by the worker.
        System.Diagnostics.Trace.TraceWarning($"Editor viewport {what} did not finish within {CloseWaitTimeout.TotalSeconds:0} s; continuing to close.");
        AdapterName = null; Statistics = null; SetPreparingFrame(false);
        return false;
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (disposed) return;
        currentPoseInputPending = null;
        disposing = true; DetachInput(); StopRenderer();
        var ownedChild = childWindow;
        childWindow = nint.Zero;
        if (ownedChild != nint.Zero) DestroyWindow(ownedChild);
        disposed = true;
        GC.SuppressFinalize(this);
    }

    private PmmCameraPose EditableCamera()
    {
        var pose = EditableLocalCamera();
        if (pose.ParentModelIndex < 0) return pose;
        var parent = project!.ResolveCameraParentTransform(pose)!.Value;
        return WorldCameraPose(pose, parent);
    }

    private static PmmCameraPose WorldCameraPose(PmmCameraPose pose, Matrix4x4 parent)
    {
        var world = pose.CreateCamera(parent);
        var forward = Vector3.Normalize(world.Target - world.Eye);
        var right = Vector3.Normalize(Vector3.Cross(world.Up, forward));
        var up = Vector3.Normalize(Vector3.Cross(forward, right));
        // Inverse of the original row-vector view rotation Ry(y)*Rx(x)*Rz(z).
        // Normalize the parent's scale and use a synthetic focus in front of the eye;
        // a parented PMM LookAt stores the camera-space eye, not an orbit center.
        float horizontal = MathF.Sqrt(forward.X * forward.X + forward.Z * forward.Z);
        float x = MathF.Atan2(forward.Y, horizontal);
        float y = MathF.Atan2(-forward.X, forward.Z);
        float z = MathF.Atan2(-right.Y, up.Y);
        if (horizontal < 1e-5f)
        { z = MathF.Atan2(up.X, right.X); y = 0; }
        float depth = MathF.Max(.01f, MathF.Abs(pose.CameraDepth));
        return pose with { LookAt = world.Eye + forward * depth, Angle = new(x, y, z),
            Distance = -depth, ParentModelIndex = -1, ParentBoneIndex = 0, IgnoreParentRotation = false, ViewOffset = Vector2.Zero };
    }

    private static PmmCameraPose PanCamera(PmmCameraPose pose, float dx, float dy, int height)
    {
        var camera = pose.CreateCamera();
        float unitsPerPixel = camera.OrthographicHeight / height;
        if (pose.ParentModelIndex >= 0)
            return pose with { LookAt = pose.LookAt + new Vector3(-dx, dy, 0) * unitsPerPixel };
        var forward = Vector3.Normalize(camera.Target - camera.Eye);
        var right = Vector3.Normalize(Vector3.Cross(camera.Up, forward));
        var up = Vector3.Normalize(Vector3.Cross(forward, right));
        return pose with { LookAt = pose.LookAt + (-dx * right + dy * up) * unitsPerPixel };
    }

    private static PmmCameraPose ZoomCamera(PmmCameraPose pose, int delta)
    {
        float depth = Math.Clamp(MathF.Abs(pose.CameraDepth) * MathF.Pow(.9f, delta / 120f), .05f, 1_000_000);
        depth = pose.CameraDepth > 0 ? depth : -depth;
        return pose.ParentModelIndex >= 0 ? pose with { LookAt = pose.LookAt with { Z = depth } }
            : pose with { Distance = depth };
    }

    private PmmCameraPose EditableLocalCamera()
    {
        if (manualCameraPose is { } edited) return edited;
        if (sampledCameraPose is { } sampled) return sampled;
        if (project?.CameraPose is { } current) return current;
        if (currentCamera == OriginalCameraPose.CreateCamera()) return OriginalCameraPose;
        var forward = Vector3.Normalize(currentCamera.Target - currentCamera.Eye);
        float fov = currentCamera.VerticalFovDegrees ?? 30;
        float distance = MathF.Max(.05f, currentCamera.OrthographicHeight / (2 * MathF.Tan(fov * MathF.PI / 360)));
        return new(currentCamera.Eye + forward * distance,
            new(MathF.Asin(Math.Clamp(forward.Y, -1, 1)), MathF.Atan2(-forward.X, forward.Z), 0),
            -distance, fov, currentCamera.VerticalFovDegrees is not null);
    }

    private void ChangeCamera(PmmCameraPose pose)
    {
        lock (workerGate) InvalidateRecordingAheadLocked();
        manualCameraPose = pose;
        currentCamera = ResolveCamera(pose);
        CameraPoseChanged?.Invoke(pose); CameraChanged?.Invoke(currentCamera);
        RequestRender();
    }

    private SceneCamera ResolveCamera(PmmCameraPose pose)
    {
        Matrix4x4? parent = null;
        if (pose.ParentModelIndex >= 0)
            parent = project?.ResolveCameraParentTransform(pose)
                ?? throw new InvalidDataException("The camera's follow object or bone is unavailable in the current project.");
        return pose.CreateCamera(parent);
    }

    private nint OnWindowMessage(nint window, uint message, nint wParam, nint lParam)
    {
        var previous = previousWindowProcedure;
        try
        {
            int x = unchecked((short)(lParam.ToInt64() & 0xffff)), y = unchecked((short)((lParam.ToInt64() >> 16) & 0xffff));
            if (message == 0x5) // WM_SIZE
            {
                lock (workerGate) { poseFrameAdmission.Clear(); InvalidateRecordingAheadLocked(); }
                RequestRender();
            }
            if (message == 0xf)
            {
                // A native invalidation requires repaint even if a bone input is pending
                // and its current pose payload is otherwise identical to the last one.
                lock (workerGate) poseFrameAdmission.Clear();
                ValidateRect(window, nint.Zero); RequestRender(); return nint.Zero;
            }
            if (message == 0x14) return new nint(1); // Renderer owns background erasure.
            if ((!IsEffectivelyEnabled || IsSuspended) && message is >= 0x100 and <= 0x20e)
                return CallWindowProcW(previous, window, message, wParam, lParam);
            if (message is 0x201 or 0x204 or 0x207)
            {
                if (dragButton != 0) return nint.Zero;
                if (message == 0x201 && GetClientRect(window, out var lightBounds)
                    && LightController?.TryBeginDrag(x, y, lightBounds.Right, lightBounds.Bottom, currentCamera, RotateLightHandle) == true)
                { SetFocus(window); if (LightController.IsDragging) SetCapture(window); return nint.Zero; }
                if (message == 0x201 && GetClientRect(window, out var boneBounds)
                    && BoneController?.TryBeginDrag(x, y, boneBounds.Right, boneBounds.Bottom, currentCamera) == true)
                { SetFocus(window); if (BoneController.IsDragging) SetCapture(window); return nint.Zero; }
                SetFocus(window); cameraBeforeDrag = manualCameraPose; dragCamera = EditableLocalCamera();
                dragButton = message; pointerX = x; pointerY = y; SetCapture(window); return nint.Zero;
            }
            if (message == 0x200 && LightController?.IsDragging == true)
            { LightController.MoveDrag(x, y); return nint.Zero; }
            if (message == 0x200 && BoneController?.IsDragging == true)
            {
                long input = InteractionDiagnostics.IsEnabled
                    ? EditorInteractionTrace.BeginInput("pointer.move", unchecked((long)(uint)GetMessageTime())) : 0;
                using var moveMeasure = InteractionDiagnostics.Measure("pointer.move-handler", input);
                BoneController.MoveDrag(x, y); return nint.Zero;
            }
            if (message == 0x202 && LightController?.IsDragging == true)
            { LightController.EndDrag(); ReleaseCapture(); return nint.Zero; }
            if (message == 0x202 && BoneController?.IsDragging == true)
            { EditorInteractionTrace.BeginInput("pointer.release"); BoneController.EndDrag(); ReleaseCapture(); return nint.Zero; }
            if ((message == 0x215 || message == 0x100 && wParam.ToInt64() == 27) && LightController?.IsDragging == true)
            { LightController.CancelDrag(); ReleaseCapture(); return nint.Zero; }
            if ((message == 0x215 || message == 0x100 && wParam.ToInt64() == 27) && BoneController?.IsDragging == true)
            { BoneController.CancelDrag(); ReleaseCapture(); return nint.Zero; }
            if (message == 0x200 && dragButton != 0)
            {
                float dx = x - pointerX, dy = y - pointerY;
                if (InvertCameraMouseX) dx = -dx;
                if (InvertCameraMouseY) dy = -dy;
                if (dragButton == 0x201)
                {
                    const float radiansPerPixel = .3f * MathF.PI / 180;
                    ChangeCamera(dragCamera with { Angle = dragCamera.Angle + new Vector3(dy * radiansPerPixel, dx * radiansPerPixel, 0) });
                }
                else if (GetClientRect(window, out var bounds) && bounds.Bottom > bounds.Top)
                {
                    ChangeCamera(PanCamera(dragCamera, dx, dy, bounds.Bottom - bounds.Top));
                }
                return nint.Zero;
            }
            if (message is 0x202 or 0x205 or 0x208)
            {
                if (dragButton != 0 && message == dragButton + 1) { dragButton = 0; ReleaseCapture(); }
                return nint.Zero;
            }
            if (message == 0x215) dragButton = 0;
            if (message == 0x100 && wParam.ToInt64() == 27 && dragButton != 0)
            {
                lock (workerGate) InvalidateRecordingAheadLocked();
                dragButton = 0; ReleaseCapture(); manualCameraPose = cameraBeforeDrag;
                if (manualCameraPose is { } restored) ChangeCamera(restored);
                else
                {
                    currentCamera = ResolveCamera(dragCamera);
                    CameraPoseChanged?.Invoke(dragCamera); CameraChanged?.Invoke(currentCamera);
                    RequestRender();
                }
                return nint.Zero;
            }
            if (message == 0x100 && TryMapEditorKey(wParam, out var key)
                && !((key is Key.Enter or Key.K) && (lParam.ToInt64() & (1L << 30)) != 0))
            {
                var args = new EditorViewportKeyEventArgs(key, ReadKeyModifiers());
                EditorKeyPressed?.Invoke(this, args);
                if (args.Handled) return nint.Zero;
            }
            if (message == 0x20a)
            {
                int delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xffff));
                var camera = EditableLocalCamera();
                ChangeCamera(ZoomCamera(camera, delta));
                return nint.Zero;
            }
        }
        catch (Exception error) { dragButton = 0; ReleaseCapture(); Error?.Invoke(error); }
        return previous == nint.Zero ? nint.Zero : CallWindowProcW(previous, window, message, wParam, lParam);
    }

    private static bool TryMapEditorKey(nint virtualKey, out Key key)
    {
        key = virtualKey.ToInt64() switch
        {
            0x0d => Key.Enter,
            0x1b => Key.Escape,
            0x2e => Key.Delete,
            0x4b => Key.K,
            0x4e => Key.N,
            0x4f => Key.O,
            0x53 => Key.S,
            0x59 => Key.Y,
            0x5a => Key.Z,
            _ => Key.None
        };
        return key != Key.None;
    }

    private static KeyModifiers ReadKeyModifiers()
    {
        var modifiers = KeyModifiers.None;
        if (GetKeyState(0x11) < 0) modifiers |= KeyModifiers.Control;
        if (GetKeyState(0x10) < 0) modifiers |= KeyModifiers.Shift;
        if (GetKeyState(0x12) < 0) modifiers |= KeyModifiers.Alt;
        return modifiers;
    }

    private void DetachInput()
    {
        if (dragButton != 0) { dragButton = 0; ReleaseCapture(); }
        if (childWindow != nint.Zero && previousWindowProcedure != nint.Zero)
            SetWindowLongPtrW(childWindow, -4, previousWindowProcedure);
        previousWindowProcedure = nint.Zero; inputProcedure = null;
    }

    private readonly record struct DiagnosticTimelineObservation(long SessionId, long Revision, double? SourceFrame, long ObservedAt, long InputId);
    private sealed record DiagnosticFrameCapture(long StartedAt, long CompletedAt, double? TimelineFrameAtCapture, long ObservedInputId);

    private sealed record RenderWork(nint Window, int Width, int Height, long Revision,
        ViewQualityOptions Quality, IEditorRenderBinding Binding, PreviewFrame? Frame,
        long PreparationToken, EditorPoseFrameCandidate PoseCandidate, long InputId = 0, long FrameId = 0,
        long QueuedAt = 0, long InputSessionId = 0, DiagnosticFrameCapture? DiagnosticCapture = null)
    {
        internal long DiagnosticInputId => InputSessionId == InteractionDiagnostics.SessionId ? InputId : 0;
        internal long RecordingEpoch { get; init; }
        internal bool RecordAhead { get; init; }
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern nint CallWindowProcW(nint previous, nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern short GetKeyState(int virtualKey);
    [DllImport("user32.dll")] private static extern int GetMessageTime();
    [DllImport("user32.dll")] private static extern bool ValidateRect(nint window, nint rectangle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(int extendedStyle, string className, string windowName, int style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint window, out NativeRectangle rectangle);
}
