using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

/// <summary>Reference-only performance diagnostic. No product rebuild, GPU utilization estimate,
/// external OS input, persistence or visual acceptance. The owned HWND is initially off screen.</summary>
internal static class EditorPreviewPerformanceProbe
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    private static readonly List<string> Checks = [];
    private static readonly List<PresentSample> Presents = [];
    private static readonly List<CaptureSample> Captures = [];
    private static readonly List<StageSample> Decorations = [];
    private static readonly List<TickSample> Ticks = [];
    private static readonly List<CpuSample> Cpu = [];
    private static readonly List<DraftSample> Drafts = [];
    private static readonly List<AsyncCallSample> AsyncCalls = [];
    private static readonly List<object> AsyncExpiration = [];
    private static readonly List<WarmCallSample> WarmCalls = [];
    private static readonly List<WarmSnapshotSample> WarmSnapshots = [];
    private static readonly List<string> ExplicitSkip = [];
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static string output = "", pma = "", pmaBefore = "", motionsBefore = "", motionsAfter = "";
    private static string? adapter, motionPath, motionFileHash;
    private static bool warp, sampling, dirtyBaseline;
    private static int nativeFrames, viewChanges, width, height;
    private static double playbackWallMilliseconds, playbackStartFrame, playbackEndFrame;
    private static double playbackTargetMilliseconds;
    private static int asyncHeartbeats, asyncBurstHeartbeats, asyncRenderCallbacks;
    private static double asyncBurstMilliseconds, asyncCompletionMilliseconds, asyncSynchronousComparisonMilliseconds;
    private static string? asyncGeometryHash, synchronousGeometryHash, asyncBoneHash, synchronousBoneHash;
    private static bool asyncSupported;
    private static bool warmSupported, warmOwnerReused, warmOwnerReleased;
    private static double warmPrewarmMilliseconds, warmBurstMilliseconds, warmDrainMilliseconds, warmReleaseMilliseconds,
        warmSynchronousComparisonMilliseconds;
    private static int warmHeartbeats, warmPrewarmHeartbeats, warmBurstHeartbeats, warmSnapshotsDuringBurst;
    private static string? warmFinalGeometryHash, warmReferenceGeometryHash, warmFinalBoneHash, warmReferenceBoneHash;
    private static string? warmSourceGeometryHash, warmInteractiveSourceGeometryHash, warmInteractiveBoneHash;
    private static object? probeBoneSelection;
    private static EditorWindow? window;
    private static EditorShellViewModel? shell;
    private static EditorViewportHost? viewport;
    private static Exception? viewportError;
    private static ViewQualityOptions? qualityBefore, qualityForRun;
    private static Stopwatch runClock = new();

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length is < 2 or > 4)
            throw new ArgumentException("Usage: probe <output-directory> <read-only-existing.pma> [--quality=none|smaa] [--motion=absolute.vmd]");
        output = Path.GetFullPath(args[0]); pma = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        try
        {
            pmaBefore = Hash(pma);
            AppBuilder.Configure<App>().UsePlatformDetect().UseSkia().SetupWithoutStarting();
            shell = new EditorShellViewModel(); shell.Workspace.Open(pma);
            shell.RefreshWorkspace(shell.Workspace.Session!.Objects.First().Id);
            string? qualityOption = null;
            foreach (string option in args.Skip(2))
            {
                if (option.StartsWith("--motion=", StringComparison.Ordinal))
                {
                    Check(motionPath is null, "only one explicit diagnostic motion is supplied");
                    motionPath = option[9..];
                    Check(Path.IsPathFullyQualified(motionPath), "diagnostic motion path is absolute");
                    motionPath = Path.GetFullPath(motionPath); motionFileHash = Hash(motionPath);
                }
                else if (option is "--quality=none" or "--quality=smaa")
                {
                    Check(qualityOption is null, "only one explicit quality option is supplied");
                    qualityOption = option;
                }
                else throw new ArgumentException("Unknown diagnostic option: " + option);
            }
            if (motionPath is not null)
            {
                var selected = shell.Browser.SelectedModel ?? throw new InvalidOperationException("Motion import requires a selected PMX.");
                shell.Workspace.BindMotion(selected.ObjectId, EditorProjectInterchange.PrepareVmd(motionPath, selected.PmxModel));
                shell.RefreshWorkspace(selected.ObjectId);
            }
            dirtyBaseline = shell.Workspace.IsDirty;
            motionsBefore = MotionHash();
            window = new EditorWindow(false) { DataContext = shell, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-20000, -20000) };
            viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
            viewport.Error += error => viewportError = error;
            viewport.FramePresented += _ => nativeFrames++;
            qualityBefore = viewport.Quality;
            if (qualityOption is not null)
            {
                viewport.Quality = qualityOption switch
                {
                    "--quality=none" => viewport.Quality with { Antialiasing = FrameAntialiasing.Off, SpatialSamples = 1 },
                    "--quality=smaa" => viewport.Quality with { Antialiasing = FrameAntialiasing.Smaa1X, SmaaQuality = SmaaQuality.High, SpatialSamples = 1 },
                    _ => throw new ArgumentException("Unknown quality option: " + qualityOption)
                };
            }
            qualityForRun = viewport.Quality;
            window.Show(); Pump(); WaitForFrame("initial real PMA frame");
            Check(viewport.HasChildWindow && !viewport.IsWarp, "owned child HWND uses actual hardware D3D12");
            adapter = viewport.AdapterName; warp = viewport.IsWarp;
            width = viewport.Statistics!.Width; height = viewport.Statistics.Height;
            Check(width > 0 && height > 0, "actual swapchain dimensions are available");
            Check(shell.Workspace.IsDirty == dirtyBaseline && !shell.Animation.HasAnyDraft, "loaded PMA and optional in-memory motion retain baseline dirty state with no drafts");
            shell.CurrentFrame = 9; Pump(); WaitForFrame("target frame nine initialization");
            Check(shell.Workspace.Session.DurationFrames >= 90, "motion has room for bounded playback and subsequent fixed advances");
            playbackTargetMilliseconds = Math.Min(10000, (shell.Workspace.Session.DurationFrames - 9 - 24) / 30d * 800);
            SamplePlayback();
            SampleFixedAdvances();
            SampleDrafts();
            SampleAsyncDrafts();
            SampleWarmDrafts();
            motionsAfter = MotionHash();
            Check(motionsAfter == motionsBefore, "registered motions retain exact JSON SHA256");
            Check(Hash(pma) == pmaBefore, "read-only PMA retains exact SHA256");
            Check(motionPath is null || Hash(motionPath) == motionFileHash, "explicit source VMD retains exact SHA256");
            Check(shell.Workspace.IsDirty == dirtyBaseline && !shell.Animation.HasAnyDraft, "diagnostic restores drafts and retains baseline project dirty state");
            WriteReport(null); Console.WriteLine("PASS " + Checks.Count); return 0;
        }
        catch (Exception error)
        {
            if (shell is not null) { shell.StopPlayback(); motionsAfter = MotionHash(); }
            WriteReport(error.ToString()); Console.Error.WriteLine(error); return 1;
        }
        finally
        {
            sampling = false;
            if (window is not null)
            {
                typeof(EditorWindow).GetField("_closingConfirmed", Private)!.SetValue(window, true);
                window.Close();
            }
        }
    }

    private static void SamplePlayback()
    {
        var s = shell!; var v = viewport!;
        var originalSnapshot = v.SnapshotProvider ?? throw new InvalidOperationException("Formal snapshot provider missing.");
        var originalDecorator = v.FrameDecorator ?? throw new InvalidOperationException("Formal editor decorator missing.");
        var timer = (DispatcherTimer)typeof(EditorShellViewModel).GetField("playbackTimer", Private)!.GetValue(s)!;
        var timestamp = typeof(EditorShellViewModel).GetField("lastPlaybackTimestamp", Private)!;
        EventHandler viewChanged = (_, _) => { if (sampling) viewChanges++; };
        EventHandler tick = (_, _) =>
        {
            if (sampling) Ticks.Add(new(runClock.Elapsed.TotalMilliseconds, s.Workspace.Session!.CurrentFrame,
                Stopwatch.GetElapsedTime((long)timestamp.GetValue(s)!).TotalMilliseconds));
        };
        void Presented(EditorViewportPresentationStatistics statistics)
        {
            if (sampling) Presents.Add(new(runClock.Elapsed.TotalMilliseconds, s.Workspace.Session!.CurrentFrame, statistics));
        }
        v.SnapshotProvider = requests =>
        {
            var watch = Stopwatch.StartNew(); var snapshot = originalSnapshot(requests);
            if (sampling) Captures.Add(new(runClock.Elapsed.TotalMilliseconds, snapshot.SourceFrame, watch.Elapsed.TotalMilliseconds,
                snapshot.SceneInputs.Sum(item => item.Value.BoneWorldMatrices.Count)));
            return snapshot;
        };
        v.FrameDecorator = frame =>
        {
            var watch = Stopwatch.StartNew(); var result = originalDecorator(frame);
            if (sampling) Decorations.Add(new(runClock.Elapsed.TotalMilliseconds, frame.SourceFrame, watch.Elapsed.TotalMilliseconds));
            return result;
        };
        s.ViewStateChanged += viewChanged; timer.Tick += tick; v.FramePresented += Presented;
        try
        {
            playbackStartFrame = s.Workspace.Session!.CurrentFrame;
            runClock.Restart(); sampling = true;
            s.PlayCommand.Execute(null);
            Check(s.IsPlaying, "formal PlayCommand starts actual DispatcherTimer playback");
            PumpUntil(() => runClock.Elapsed.TotalMilliseconds >= playbackTargetMilliseconds || viewportError is not null || !s.IsPlaying,
                "bounded real timer playback", 30);
            s.StopPlayback(); playbackWallMilliseconds = runClock.Elapsed.TotalMilliseconds;
            playbackEndFrame = s.Workspace.Session.CurrentFrame;
            Check(viewportError is null, "no viewport exception during real timer playback");
            Check(playbackWallMilliseconds >= playbackTargetMilliseconds && playbackEndFrame > playbackStartFrame && Presents.Count >= 3,
                "bounded playback window contains advancing source clock and actual FramePresented callbacks");
        }
        finally
        {
            sampling = false; s.StopPlayback(); timer.Tick -= tick; s.ViewStateChanged -= viewChanged;
            v.FramePresented -= Presented; v.SnapshotProvider = originalSnapshot; v.FrameDecorator = originalDecorator;
        }
    }

    private static void SampleFixedAdvances()
    {
        var s = shell!; var v = viewport!; var session = s.Workspace.Session!;
        var c = v.BoneController ?? throw new InvalidOperationException("Formal bone controller missing.");
        var advancing = typeof(EditorShellViewModel).GetField("advancing", Private)!;
        var rendererRequests = (IReadOnlyList<SceneObjectRenderRequest>)typeof(EditorViewportHost)
            .GetField("sceneInputRequests", Private)!.GetValue(v)!;
        // Suspend queueing GPU work for the independent CPU measurements; no model option changes.
        v.IsSuspended = true; Pump();
        var worker = (Task)typeof(EditorViewportHost).GetField("workerTask", Private)!.GetValue(v)!;
        PumpUntil(() => worker.IsCompleted, "drain prior viewport work before CPU-only samples", 30);
        worker.GetAwaiter().GetResult();
        for (int index = 0; index < 24; index++)
        {
            Check(!s.Animation.HasAnyDraft, "fixed advance " + index + " has no draft");
            var total = Stopwatch.StartNew(); var stage = Stopwatch.StartNew();
            double before = session.CurrentFrame;
            session.Advance(1d / 30);
            double advance = stage.Elapsed.TotalMilliseconds;
            stage.Restart();
            // Mirror the formal timer's integer inspector refresh without a discontinuous Seek.
            advancing.SetValue(s, true);
            try { s.CurrentFrame = (int)session.CurrentFrame; }
            finally { advancing.SetValue(s, false); }
            double inspector = stage.Elapsed.TotalMilliseconds;
            stage.Restart(); var requests = c.SnapshotRequests(rendererRequests);
            double requestTime = stage.Elapsed.TotalMilliseconds;
            stage.Restart(); var snapshot = session.CaptureRenderSnapshot(requests);
            double capture = stage.Elapsed.TotalMilliseconds;
            stage.Restart();
            var frame = new PreviewFrame(width, height, snapshot.SourceFrame, snapshot.Scene, snapshot.Camera, snapshot.Light, snapshot.SelfShadow)
            { ResolvedCamera = snapshot.ResolvedCamera, SceneInputs = snapshot.SceneInputs,
                RenderingStyles = snapshot.RenderingStyles, MorphChannels = snapshot.MorphChannels };
            var decorated = c.DecorateFrame(frame);
            double decorate = stage.Elapsed.TotalMilliseconds;
            Cpu.Add(new(index, before, session.CurrentFrame, advance, inspector, requestTime, capture, decorate,
                total.Elapsed.TotalMilliseconds, decorated.Scene.EditorOverlay?.Lines.Count ?? 0));
        }
        Check(Cpu.Count == 24 && Cpu.All(row => Math.Abs(row.SourceFrame - row.BeforeFrame - 1) < 1e-7),
            "24 independent fixed 1/30-second advances retain the exact source step");
    }

    private static void SampleDrafts()
    {
        var s = shell!; var session = s.Workspace.Session!;
        // The viewport remains suspended, so a setter cannot secretly evaluate an extra draft capture.
        s.CurrentFrame = 9; s.PosePreview.Clear();
        var model = s.Browser.SelectedModel ?? throw new InvalidOperationException("Selected real model missing.");
        int bone = SelectProbeBone(model.PmxModel);
        s.Browser.SelectedBoneIndex = bone;
        Check(s.Animation.IsBoneSelected && s.Animation.RotationY.HasValue && s.Animation.CanRotate,
            "real selected PMX bone exposes a valid editable rotation");
        string history = History();
        decimal originalY = s.Animation.RotationY!.Value;
        try
        {
            for (int index = 0; index < 2; index++)
            {
                var watch = Stopwatch.StartNew(); s.Animation.RotationY = originalY + (index + 1) * .5m;
                double setter = watch.Elapsed.TotalMilliseconds;
                watch.Restart(); var preview = s.PosePreview.Update(session, s.Animation);
                double update = watch.Elapsed.TotalMilliseconds;
                Check(preview is not null && s.Animation.HasAnyDraft, "real rotation draft " + index + " prepares a detached preview");
                watch.Restart(); var snapshot = preview!.CaptureRenderSnapshot();
                Drafts.Add(new(index, bone, model.PmxModel.Bones[bone].Name, setter, update, watch.Elapsed.TotalMilliseconds,
                    session.CurrentFrame, snapshot.SourceFrame, MotionHash() == motionsBefore));
            }
        }
        finally { s.Animation.InvalidateDrafts(); s.PosePreview.Clear(); }
        Check(History() == history, "both draft previews leave project Undo/Redo histories unchanged");
        Check(!s.Animation.HasAnyDraft && s.Animation.RotationY == originalY,
            "discarding diagnostic drafts restores the exact original local channel");
    }

    private static void SampleAsyncDrafts()
    {
        var method = typeof(EditorPosePreview).GetMethod("TryGetOrRequest", BindingFlags.Instance | BindingFlags.Public);
        if (method is null)
        {
            ExplicitSkip.Add("Loaded stage has no EditorPosePreview.TryGetOrRequest API; async draft responsiveness, equivalence and expiration are not tested by this baseline.");
            return;
        }
        asyncSupported = true;
        var s = shell!; var session = s.Workspace.Session!;
        Check(viewport!.IsSuspended, "async draft diagnostic keeps viewport requests suspended");
        s.CurrentFrame = 9; s.Animation.InvalidateDrafts(); s.PosePreview.Clear();
        var selected = s.Browser.SelectedModel ?? throw new InvalidOperationException("Selected PMX missing.");
        int bone = s.Animation.SelectedBoneIndex ?? throw new InvalidOperationException("Synchronous draft did not leave a selected bone.");
        decimal originalY = s.Animation.RotationY ?? throw new InvalidOperationException("Selected bone rotation is unavailable.");
        IReadOnlyList<SceneObjectRenderRequest> requests = Array.AsReadOnly(new[] {
            new SceneObjectRenderRequest(selected.ObjectId, Enumerable.Range(0, selected.PmxModel.Bones.Count), includeSourceGeometry: true) });
        string history = History(), sourceGeometry = GeometryHash(session.CaptureRenderSnapshot(requests));
        string sourceBones = BoneHash(session.CaptureRenderSnapshot(requests));
        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        heartbeat.Tick += (_, _) => asyncHeartbeats++;
        Exception? asyncError = null;
        SceneProjectRenderSnapshot? completed = null;
        var owner = s.PosePreview;
        Action render = () => asyncRenderCallbacks++;
        Action<Exception> error = exception => asyncError = exception;
        var total = Stopwatch.StartNew();
        heartbeat.Start();
        try
        {
            for (int index = 0; index < 16; index++)
            {
                var watch = Stopwatch.StartNew();
                s.Animation.RotationY = originalY + (index + 1) * .125m;
                double setter = watch.Elapsed.TotalMilliseconds;
                watch.Restart(); completed = InvokeAsync(method, owner, session, requests, render, error);
                AsyncCalls.Add(new(index, total.Elapsed.TotalMilliseconds, setter, watch.Elapsed.TotalMilliseconds,
                    s.Animation.RotationY!.Value, completed is not null));
                PumpForMilliseconds(10);
            }
            asyncBurstMilliseconds = total.Elapsed.TotalMilliseconds; asyncBurstHeartbeats = asyncHeartbeats;
            decimal expectedY = originalY + 2m;
            Check(s.Animation.RotationY == expectedY && AsyncCalls.Count == 16,
                "16 rapid real rotation drafts leave the exact latest input active");
            // TryGetOrRequest can retain a previous complete pose while replacing it. Drain the
            // latest-only worker before reading, so an earlier retained snapshot cannot pass early.
            WaitForAsyncIdle(owner);
            completed = InvokeAsync(method, owner, session, requests, render, error);
            asyncCompletionMilliseconds = total.Elapsed.TotalMilliseconds;
            Check(asyncError is null && completed is not null, "latest async draft publishes a complete snapshot without error");
            Check(asyncBurstHeartbeats > 0 && asyncHeartbeats > asyncBurstHeartbeats,
                "10ms UI DispatcherTimer continues during rapid requests and full async initialization");
            Check(completed!.SourceFrame == 9 && completed.SceneInputs[selected.ObjectId].BoneWorldMatrices.Count == selected.PmxModel.Bones.Count,
                "async snapshot retains source frame nine and every explicitly requested solved bone");
            var comparisonWatch = Stopwatch.StartNew(); var synchronous = owner.Update(session, s.Animation)
                ?? throw new InvalidOperationException("Synchronous comparison preview missing.");
            var reference = synchronous.CaptureRenderSnapshot(requests);
            asyncSynchronousComparisonMilliseconds = comparisonWatch.Elapsed.TotalMilliseconds;
            asyncGeometryHash = GeometryHash(completed); synchronousGeometryHash = GeometryHash(reference);
            asyncBoneHash = BoneHash(completed); synchronousBoneHash = BoneHash(reference);
            Check(asyncGeometryHash == synchronousGeometryHash && asyncBoneHash == synchronousBoneHash,
                "latest async geometry/source geometry and bone-world SHA256 match same-draft synchronous output bit for bit");
            Check(s.Animation.RotationY == expectedY && session.CurrentFrame == 9 && History() == history
                && MotionHash() == motionsBefore && GeometryHash(session.CaptureRenderSnapshot(requests)) == sourceGeometry
                && BoneHash(session.CaptureRenderSnapshot(requests)) == sourceBones,
                "rapid async work and synchronous comparison preserve live source pose, clock, registered motion and Undo/Redo");
        }
        finally { heartbeat.Stop(); s.Animation.InvalidateDrafts(); owner.Clear(); WaitForAsyncIdle(owner); }
        VerifyAsyncExpiration(method, requests, originalY, "clear");
        VerifyAsyncExpiration(method, requests, originalY, "seek");
        VerifyAsyncExpiration(method, requests, originalY, "dispose");
        Check(s.Animation.RotationY == originalY && !s.Animation.HasAnyDraft && History() == history
            && MotionHash() == motionsBefore && session.CurrentFrame == 9,
            "async expiration checks restore frame nine/local channel and retain registered motion/history");
    }

    private static int SelectProbeBone(PmxModel model)
    {
        // Diagnostic selection follows actual positive skin weights, never a translated bone name.
        // A Y rotation must affect off-axis source vertices; an unweighted operation-center helper
        // can otherwise make an exact final-pose comparison vacuous.
        var physical = model.RigidBodies.Where(body => body.Operation is PmxRigidBodyOperation.Dynamic or PmxRigidBodyOperation.DynamicAndBoneMerge)
            .Select(body => body.BoneIndex).ToHashSet();
        var ikLinks = model.Bones.SelectMany(bone => bone.IkLinks).Select(link => link.BoneIndex).ToHashSet();
        var direct = new int[model.Bones.Count]; var offYAxis = new int[model.Bones.Count];
        foreach (var vertex in model.Vertices)
            for (int slot = 0; slot < Math.Min(vertex.BoneIndices.Count, vertex.BoneWeights.Count); slot++)
            {
                int bone = vertex.BoneIndices[slot];
                if ((uint)bone >= (uint)model.Bones.Count || vertex.BoneWeights[slot] <= 0) continue;
                direct[bone]++;
                var delta = vertex.Position - model.Bones[bone].Position;
                if (delta.X * delta.X + delta.Z * delta.Z > 1e-8f) offYAxis[bone]++;
            }
        const PmxBoneFlags excluded = PmxBoneFlags.Ik | PmxBoneFlags.FixedAxis | PmxBoneFlags.AppendRotate
            | PmxBoneFlags.AppendTranslate | PmxBoneFlags.DeformAfterPhysics;
        int selected = Enumerable.Range(0, model.Bones.Count).Where(index =>
            model.Bones[index].Flags.HasFlag(PmxBoneFlags.Visible | PmxBoneFlags.AllowRotate | PmxBoneFlags.AllowControl)
            && (model.Bones[index].Flags & excluded) == 0 && !physical.Contains(index) && !ikLinks.Contains(index)
            && offYAxis[index] > 0).OrderByDescending(index => offYAxis[index]).ThenBy(index => index).First();
        probeBoneSelection = new { index = selected, model.Bones[selected].Name,
            positiveWeightSlots = direct[selected], positiveWeightSlotsOffLocalYAxis = offYAxis[selected],
            flags = model.Bones[selected].Flags.ToString(), physicsDriven = physical.Contains(selected), ikLink = ikLinks.Contains(selected),
            rule = "Highest positive direct skin-weight count off the bone's local Y rotation axis among visible rotatable controllable ordinary nonphysical/non-IK/non-append bones; no name guessing." };
        Check(offYAxis[selected] > 0,
            "diagnostic bone has authored positive off-axis skin weights and is not the unweighted operation-center helper");
        return selected;
    }

    private static void VerifyAsyncExpiration(MethodInfo method, IReadOnlyList<SceneObjectRenderRequest> requests,
        decimal originalY, string kind)
    {
        var s = shell!; var source = s.Workspace.Session!;
        using var owner = new EditorPosePreview();
        s.CurrentFrame = 9; s.Animation.InvalidateDrafts(); s.Animation.RotationY = originalY + .375m;
        int callbacks = 0; Exception? error = null;
        _ = InvokeAsync(method, owner, source, requests, () => callbacks++, exception => error = exception);
        if (kind == "clear") { owner.Clear(); s.Animation.InvalidateDrafts(); }
        else if (kind == "dispose") { owner.Dispose(); s.Animation.InvalidateDrafts(); }
        else if (kind == "seek") s.CurrentFrame = 10;
        else throw new ArgumentException("Unknown expiration case.", nameof(kind));
        double expectedFrame = source.CurrentFrame;
        string expectedGeometry = GeometryHash(source.CaptureRenderSnapshot(requests));
        string expectedBones = BoneHash(source.CaptureRenderSnapshot(requests));
        WaitForAsyncIdle(owner);
        Check(callbacks == 0 && error is null && source.CurrentFrame == expectedFrame
            && GeometryHash(source.CaptureRenderSnapshot(requests)) == expectedGeometry
            && BoneHash(source.CaptureRenderSnapshot(requests)) == expectedBones && MotionHash() == motionsBefore,
            "expired async " + kind + " result cannot request rendering/report error or mutate the live source");
        AsyncExpiration.Add(new { kind, callbacks, error = error?.ToString(), expectedFrame,
            finalFrame = source.CurrentFrame, geometrySha256 = expectedGeometry, boneWorldSha256 = expectedBones });
        s.Animation.InvalidateDrafts(); s.CurrentFrame = 9; s.PosePreview.Clear();
    }

    private static void SampleWarmDrafts()
    {
        var method = typeof(EditorPosePreview).GetMethod("TryGetOrRequest", BindingFlags.Instance | BindingFlags.Public);
        var warmOwnerField = typeof(EditorPosePreview).GetField("warmOwner", Private);
        if (method is null || method.GetParameters().Length < 7 || warmOwnerField is null)
        {
            ExplicitSkip.Add("Loaded stage has no interactive/prewarm TryGetOrRequest parameters or warm owner; continuous warm physics is not tested by this stage.");
            return;
        }
        warmSupported = true;
        var s = shell!; var source = s.Workspace.Session!; var owner = s.PosePreview;
        Check(viewport!.IsSuspended, "continuous warm diagnostic keeps native viewport requests suspended");
        s.CurrentFrame = 9; s.Animation.InvalidateDrafts(); owner.Clear(); WaitForAsyncIdle(owner);
        var selected = s.Browser.SelectedModel ?? throw new InvalidOperationException("Selected PMX missing.");
        int bone = s.Animation.SelectedBoneIndex ?? throw new InvalidOperationException("Warm diagnostic requires selected bone.");
        decimal originalY = s.Animation.RotationY ?? throw new InvalidOperationException("Selected bone rotation unavailable.");
        IReadOnlyList<SceneObjectRenderRequest> requests = Array.AsReadOnly(new[] {
            new SceneObjectRenderRequest(selected.ObjectId, Enumerable.Range(0, selected.PmxModel.Bones.Count), includeSourceGeometry: true) });
        string history = History();
        var sourceBefore = source.CaptureRenderSnapshot(requests);
        string sourceGeometry = GeometryHash(sourceBefore), sourceBones = BoneHash(sourceBefore);
        warmSourceGeometryHash = SourceGeometryHash(sourceBefore, selected.ObjectId);
        Exception? warmError = null;
        var clock = Stopwatch.StartNew();
        var snapshotField = typeof(EditorPosePreview).GetField("asyncSnapshot", Private)!;
        var generationField = typeof(EditorPosePreview).GetField("lastPublishedGeneration", Private)!;
        Action render = () =>
        {
            var snapshot = snapshotField.GetValue(owner) as SceneProjectRenderSnapshot;
            if (snapshot is null) { warmError = new InvalidOperationException("Warm callback has no completed snapshot."); return; }
            WarmSnapshots.Add(new(clock.Elapsed.TotalMilliseconds, snapshot.SourceFrame,
                snapshot.SceneInputs.Values.Sum(input => input.BoneWorldMatrices.Count),
                (long)generationField.GetValue(owner)!));
        };
        Action<Exception> error = exception => warmError = exception;
        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        heartbeat.Tick += (_, _) => warmHeartbeats++;
        heartbeat.Start();
        try
        {
            Check(!s.Animation.HasAnyDraft, "warm preheating starts from committed frame-nine inputs without drafts");
            var prewarmWatch = Stopwatch.StartNew();
            var initial = InvokeAsync(method, owner, source, requests, render, error, interactive: false, prewarm: true);
            WaitForAsyncIdle(owner);
            warmPrewarmMilliseconds = prewarmWatch.Elapsed.TotalMilliseconds; warmPrewarmHeartbeats = warmHeartbeats;
            object? originalWarmOwner = warmOwnerField.GetValue(owner); // Worker is idle at this read.
            Check(initial is null && originalWarmOwner is not null && WarmSnapshots.Count == 0 && warmError is null,
                "prewarm completes a private physical graph without publishing a draft or requesting rendering");
            int beforeBurstHeartbeats = warmHeartbeats;
            var burstWatch = Stopwatch.StartNew();
            for (int index = 0; index < 32; index++)
            {
                var callWatch = Stopwatch.StartNew(); s.Animation.RotationY = originalY + (index + 1) * .0625m;
                double setter = callWatch.Elapsed.TotalMilliseconds;
                callWatch.Restart();
                var snapshot = InvokeAsync(method, owner, source, requests, render, error, interactive: true, prewarm: true);
                WarmCalls.Add(new(index, burstWatch.Elapsed.TotalMilliseconds, setter, callWatch.Elapsed.TotalMilliseconds,
                    s.Animation.RotationY!.Value, WarmSnapshots.Count, snapshot is not null));
                PumpForMilliseconds(10);
            }
            warmBurstMilliseconds = burstWatch.Elapsed.TotalMilliseconds;
            warmBurstHeartbeats = warmHeartbeats - beforeBurstHeartbeats;
            warmSnapshotsDuringBurst = WarmSnapshots.Count;
            var drainWatch = Stopwatch.StartNew(); WaitForAsyncIdle(owner);
            warmDrainMilliseconds = drainWatch.Elapsed.TotalMilliseconds;
            object? finalWarmOwner = warmOwnerField.GetValue(owner); // Read identity only after worker idle.
            warmOwnerReused = ReferenceEquals(originalWarmOwner, finalWarmOwner);
            var lastInteractive = InvokeAsync(method, owner, source, requests, render, error, interactive: true, prewarm: true);
            Check(warmError is null && warmOwnerReused && lastInteractive is not null,
                "all continuous bone requests reuse the exact prewarmed private physics owner");
            warmInteractiveSourceGeometryHash = SourceGeometryHash(lastInteractive!, selected.ObjectId);
            warmInteractiveBoneHash = BoneHash(lastInteractive!);
            Check(lastInteractive!.SceneInputs[selected.ObjectId].BoneWorldMatrices[bone]
                    != sourceBefore.SceneInputs[selected.ObjectId].BoneWorldMatrices[bone]
                && warmInteractiveBoneHash != sourceBones && warmInteractiveSourceGeometryHash != warmSourceGeometryHash,
                "continuous warm rotation changes the selected ordinary solved bone and actual source-local vertex geometry from the committed pose");
            Check(WarmCalls.Count == 32 && s.Animation.RotationY == originalY + 2m,
                "32 continuous .0625-degree rotation inputs retain the same final two-degree draft");
            Check(warmSnapshotsDuringBurst > 1 && warmBurstHeartbeats > 0 && warmPrewarmHeartbeats > 0,
                "multiple complete warm snapshots publish while 10ms UI heartbeat continues through prewarm and input burst");
            Check(WarmSnapshots.All(row => row.SourceFrame == 9 && row.RequestedBoneWorlds == selected.PmxModel.Bones.Count)
                && WarmSnapshots.Zip(WarmSnapshots.Skip(1)).All(pair => pair.First.Generation < pair.Second.Generation),
                "continuous warm publications retain all requested bones at frame nine in strictly increasing generations");
            var releaseWatch = Stopwatch.StartNew();
            _ = InvokeAsync(method, owner, source, requests, render, error, interactive: false, prewarm: false);
            WaitForAsyncIdle(owner);
            var final = InvokeAsync(method, owner, source, requests, render, error, interactive: false, prewarm: false);
            warmReleaseMilliseconds = releaseWatch.Elapsed.TotalMilliseconds;
            heartbeat.Stop();
            Check(final is not null && warmError is null && final.SourceFrame == 9,
                "release without tail prewarming publishes the full initialized exact final draft");
            var comparisonWatch = Stopwatch.StartNew();
            var referenceOwner = owner.Update(source, s.Animation)
                ?? throw new InvalidOperationException("Warm release synchronous reference missing.");
            var reference = referenceOwner.CaptureRenderSnapshot(requests);
            warmSynchronousComparisonMilliseconds = comparisonWatch.Elapsed.TotalMilliseconds;
            warmFinalGeometryHash = GeometryHash(final!); warmReferenceGeometryHash = GeometryHash(reference);
            warmFinalBoneHash = BoneHash(final!); warmReferenceBoneHash = BoneHash(reference);
            Check(warmFinalGeometryHash == warmReferenceGeometryHash && warmFinalBoneHash == warmReferenceBoneHash,
                "warm release geometry/source geometry and solved bone-world SHA256 match synchronous final output bit for bit");
            Check(source.CurrentFrame == 9 && History() == history && MotionHash() == motionsBefore
                && GeometryHash(source.CaptureRenderSnapshot(requests)) == sourceGeometry
                && BoneHash(source.CaptureRenderSnapshot(requests)) == sourceBones,
                "prewarm, continuous physics and exact release preserve live source clock, pose, registered motion and Undo/Redo");
        }
        finally
        {
            heartbeat.Stop(); s.Animation.InvalidateDrafts(); owner.Clear(); WaitForAsyncIdle(owner);
            warmOwnerReleased = warmOwnerField.GetValue(owner) is null; // No worker access remains.
        }
        Check(warmOwnerReleased && !s.Animation.HasAnyDraft && s.Animation.RotationY == originalY && History() == history,
            "Clear retires the worker-owned warm graph and restores the original local draft channel/history");
    }

    private static SceneProjectRenderSnapshot? InvokeAsync(MethodInfo method, EditorPosePreview owner,
        SceneProjectSession source, IReadOnlyList<SceneObjectRenderRequest> requests, Action render, Action<Exception> error,
        bool interactive = false, bool prewarm = false)
    {
        try
        {
            object?[] values = [source, shell!.Animation, requests, render, error];
            var parameters = method.GetParameters();
            if (parameters.Length > values.Length)
                values = values.Concat(parameters.Skip(values.Length).Select(parameter => parameter.DefaultValue)).ToArray();
            if (parameters.Length >= 6) values[5] = interactive;
            if (parameters.Length >= 7) values[6] = prewarm;
            return (SceneProjectRenderSnapshot?)method.Invoke(owner, values);
        }
        catch (TargetInvocationException failure) when (failure.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.InnerException).Throw(); throw; }
    }
    private static void WaitForAsyncIdle(EditorPosePreview owner)
    {
        var gate = typeof(EditorPosePreview).GetField("workerGate", Private)!.GetValue(owner)!;
        var running = typeof(EditorPosePreview).GetField("workerRunning", Private)!;
        PumpUntil(() => { lock (gate) return !(bool)running.GetValue(owner)!; }, "async draft worker retirement", 30);
        Pump();
    }
    private static void PumpForMilliseconds(int milliseconds)
    {
        var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1) };
        timer.Tick += (_, _) => { if (watch.Elapsed.TotalMilliseconds >= milliseconds) frame.Continue = false; };
        timer.Start(); try { Dispatcher.UIThread.PushFrame(frame); } finally { timer.Stop(); }
        Pump();
    }
    private static string GeometryHash(SceneProjectRenderSnapshot snapshot)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(snapshot.Scene.Surface.Snapshot);
        foreach (var input in snapshot.SceneInputs.OrderBy(pair => pair.Key))
            if (input.Value.SourceGeometry is { } geometry) Add(geometry);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
        void Add(SceneSnapshot scene)
        {
            hash.AppendData(MemoryMarshal.AsBytes(scene.Vertices));
            hash.AppendData(MemoryMarshal.AsBytes(scene.Indices));
            hash.AppendData(MemoryMarshal.AsBytes(new[] { scene.World }.AsSpan()));
        }
    }
    private static string BoneHash(SceneProjectRenderSnapshot snapshot)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var input in snapshot.SceneInputs.OrderBy(pair => pair.Key))
        {
            hash.AppendData(input.Key.ToByteArray());
            foreach (var bone in input.Value.BoneWorldMatrices.OrderBy(pair => pair.Key))
            {
                hash.AppendData(BitConverter.GetBytes(bone.Key));
                hash.AppendData(MemoryMarshal.AsBytes(new[] { bone.Value }.AsSpan()));
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private static string SourceGeometryHash(SceneProjectRenderSnapshot snapshot, Guid objectId)
    {
        var geometry = snapshot.SceneInputs[objectId].SourceGeometry
            ?? throw new InvalidOperationException("Diagnostic requires the actual source-local geometry.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(MemoryMarshal.AsBytes(geometry.Vertices));
        hash.AppendData(MemoryMarshal.AsBytes(geometry.Indices));
        hash.AppendData(MemoryMarshal.AsBytes(new[] { geometry.World }.AsSpan()));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string History()
    {
        int Count(string name) => ((ICollection)typeof(EditorProjectWorkspace).GetField(name, Private)!.GetValue(shell!.Workspace)!).Count;
        return Count("undo") + "/" + Count("redo");
    }
    private static string MotionHash() => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        shell!.Workspace.Motions.Select(actor => new { actor.ObjectId, actor.Motion }).ToArray(), Json)));
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("FAIL: " + message); Checks.Add(message); }
    private static void Pump() { Dispatcher.UIThread.RunJobs(); window?.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static void WaitForFrame(string operation)
    {
        int before = nativeFrames; viewport!.RequestRender();
        PumpUntil(() => nativeFrames > before || viewportError is not null, operation, 30);
        if (viewportError is not null) throw new InvalidOperationException(operation, viewportError);
        Check(nativeFrames > before && viewport.Statistics is not null, operation + ": actual native frame");
    }
    private static void PumpUntil(Func<bool> condition, string operation, int seconds)
    {
        if (condition()) { Pump(); return; }
        var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew(); Exception? failure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (condition()) frame.Continue = false;
                else if (watch.Elapsed.TotalSeconds >= seconds) { failure = new TimeoutException(operation); frame.Continue = false; }
            }
            catch (Exception error) { failure = error; frame.Continue = false; }
        };
        timer.Start(); try { Dispatcher.UIThread.PushFrame(frame); } finally { timer.Stop(); }
        Pump(); if (failure is not null) throw failure;
    }
    private static object Distribution(IEnumerable<double> input)
    {
        var values = input.Order().ToArray();
        double? At(double quantile) => values.Length == 0 ? null : values[Math.Clamp((int)Math.Ceiling(values.Length * quantile) - 1, 0, values.Length - 1)];
        return new { count = values.Length, p50 = At(.5), p95 = At(.95), max = At(1), mean = values.Length == 0 ? (double?)null : values.Average() };
    }
    private static void WriteReport(string? failure)
    {
        File.WriteAllText(Path.Combine(output, "preview-performance-raw.json"), JsonSerializer.Serialize(new
        { presents = Presents, captures = Captures, decorations = Decorations, ticks = Ticks, fixedCpu = Cpu, drafts = Drafts,
            asyncCalls = AsyncCalls, asyncExpiration = AsyncExpiration, warmCalls = WarmCalls, warmSnapshots = WarmSnapshots }, Json));
        File.WriteAllText(Path.Combine(output, "preview-performance.json"), JsonSerializer.Serialize(new
        {
            success = failure is null, passed = Checks.Count, checks = Checks, explicitSkip = ExplicitSkip, failure,
            pmaPath = pma, originalPmaSha256 = pmaBefore, finalPmaSha256 = File.Exists(pma) ? Hash(pma) : null,
            motionPath, originalVmdSha256 = motionFileHash, finalVmdSha256 = motionPath is not null && File.Exists(motionPath) ? Hash(motionPath) : null,
            dirtyBaseline, finalDirtyState = shell?.Workspace.IsDirty, durationFrames = shell?.Workspace.Session?.DurationFrames,
            originalMotionSha256 = motionsBefore, finalMotionSha256 = motionsAfter,
            assemblyPath = typeof(App).Assembly.Location, assemblySha256 = Hash(typeof(App).Assembly.Location),
            adapter, isWarp = warp, width, height, qualityBefore, qualityForRun,
            probeBoneSelection,
            playback = new { playbackTargetMilliseconds, playbackWallMilliseconds, playbackStartFrame, playbackEndFrame, presents = Presents.Count,
                presentationCallbackRate = Presents.Count * 1000 / Math.Max(1, playbackWallMilliseconds), ticks = Ticks.Count, viewChanges,
                sourceFramesPerWallSecond = (playbackEndFrame - playbackStartFrame) * 1000 / Math.Max(1, playbackWallMilliseconds),
                tickWorkMilliseconds = Distribution(Ticks.Select(row => row.TickWorkMilliseconds)),
                snapshotMilliseconds = Distribution(Captures.Select(row => row.Milliseconds)),
                decorationMilliseconds = Distribution(Decorations.Select(row => row.Milliseconds)),
                interPresentMilliseconds = Distribution(Presents.Select(row => row.Statistics.Timing?.InterPresentMilliseconds ?? 0)),
                recordAndSubmitMilliseconds = Distribution(Presents.Select(row => row.Statistics.Timing?.RecordAndSubmitMilliseconds ?? 0)),
                recordCpuMilliseconds = Distribution(Presents.Select(row => row.Statistics.Frame?.RecordCpuMilliseconds ?? 0)),
                geometryCacheMilliseconds = Distribution(Presents.Select(row => row.Statistics.Frame?.GeometryCacheMilliseconds ?? 0)),
                slotWaitMilliseconds = Distribution(Presents.Select(row => row.Statistics.Frame?.LastSlotWaitMilliseconds ?? 0)),
                presentWaitMilliseconds = Distribution(Presents.Select(row => row.Statistics.Timing?.PresentWaitMilliseconds ?? 0)) },
            fixedCpu = new { advanceMilliseconds = Distribution(Cpu.Select(row => row.AdvanceMilliseconds)),
                inspectorMilliseconds = Distribution(Cpu.Select(row => row.InspectorMilliseconds)),
                requestMilliseconds = Distribution(Cpu.Select(row => row.RequestMilliseconds)),
                snapshotMilliseconds = Distribution(Cpu.Select(row => row.SnapshotMilliseconds)),
                boneDecorationMilliseconds = Distribution(Cpu.Select(row => row.DecorateMilliseconds)),
                totalMilliseconds = Distribution(Cpu.Select(row => row.TotalMilliseconds)) },
            draftUpdateMilliseconds = Distribution(Drafts.Select(row => row.UpdateMilliseconds)),
            asyncDrafts = new { supported = asyncSupported, requestCalls = AsyncCalls.Count,
                requestMilliseconds = Distribution(AsyncCalls.Select(row => row.RequestMilliseconds)),
                setterMilliseconds = Distribution(AsyncCalls.Select(row => row.SetterMilliseconds)),
                asyncBurstMilliseconds, asyncBurstHeartbeats, asyncCompletionMilliseconds, asyncHeartbeats, asyncRenderCallbacks,
                asyncSynchronousComparisonMilliseconds, asyncGeometryHash, synchronousGeometryHash, asyncBoneHash, synchronousBoneHash },
            warmDrafts = new { supported = warmSupported, requestCalls = WarmCalls.Count,
                requestMilliseconds = Distribution(WarmCalls.Select(row => row.RequestMilliseconds)),
                setterMilliseconds = Distribution(WarmCalls.Select(row => row.SetterMilliseconds)),
                warmPrewarmMilliseconds, warmPrewarmHeartbeats, warmBurstMilliseconds, warmBurstHeartbeats,
                warmSnapshotsDuringBurst, completedSnapshotCallbacks = WarmSnapshots.Count, warmDrainMilliseconds,
                warmOwnerReused, warmReleaseMilliseconds, warmSynchronousComparisonMilliseconds, warmOwnerReleased,
                warmFinalGeometryHash, warmReferenceGeometryHash, warmFinalBoneHash, warmReferenceBoneHash,
                warmSourceGeometryHash, warmInteractiveSourceGeometryHash, warmInteractiveBoneHash },
            scope = new[] {
                "Probe-owned real EditorWindow(false), initially at (-20000,-20000), real child HWND hardware swapchain. Off-screen/occluded presentation can affect timing; this is not foreground FPS or visual acceptance.",
                "Bounded playback uses formal PlayCommand and DispatcherTimer for playbackTargetMilliseconds (up to ten seconds, shortened for this exact motion so 24 subsequent advances fit). Present samples retain all existing D3D12 statistics. SourceClockAtCallback is the UI owner clock at callback, not a guarantee of the exact rendered source frame; capture samples record their exact source frame separately.",
                "TickWorkMilliseconds runs from the product's lastPlaybackTimestamp to an appended timer observer, including Advance plus integer inspector refresh and synchronous event handlers. It is not GPU duration.",
                "24 fixed advances suspend new viewport requests and time Advance, mirrored inspector sync, bone requests, already evaluated snapshot, and bone-only decoration separately. No model physics/solver setting is reduced; default 120 initialization policy remains.",
                "Two rotation setters and detached PosePreview.Update calls are timed with viewport requests suspended, then drafts are discarded. No registration, save, export, external OS input, GPU utilization percentage, or user visual approval is claimed.",
                "Where the loaded assembly exposes TryGetOrRequest, sixteen rotation changes separated by pumped 10ms intervals measure nonblocking request work with a 10ms UI heartbeat. The latest fully initialized snapshot is compared bit for bit with synchronous Update for the identical final draft and requests. Clear/seek/dispose cases require no expired render/error callback or live-source mutation.",
                "Async request latency is UI scheduling responsiveness, not model pose latency or 60fps bone following. All 120 physics initialization substeps remain; complete physical preparation was about 0.7 seconds in the prior baseline and is measured again here. The viewport is suspended during async-specific measurements, so these do not exercise actual-present publication/picking or claim foreground visual performance.",
                "Where the loaded API supports interactive/prewarm, a committed private graph is fully preheated first, then 32 rotation changes use its continuous physics history. Prewarm cost, UI request latency, completed-snapshot callbacks and ten-millisecond heartbeat are reported separately. Owner identity is inspected only with its worker idle. Release explicitly disables prewarm and must match a fresh full synchronous initialization bit for bit; Clear must retire the warm graph.",
                "Continuous warm snapshots are worker-completed editor poses while the actual HWND viewport remains suspended for this section. Callback frequency and request duration are not actual displayed HWND FPS, picking validation, foreground interaction or user visual approval. The exact release still performs the complete 120-step policy and has its own measured latency." }
        }, Json));
    }
    private sealed record PresentSample(double WallMilliseconds, double SourceClockAtCallback, EditorViewportPresentationStatistics Statistics);
    private sealed record CaptureSample(double WallMilliseconds, double SourceFrame, double Milliseconds, int RequestedBoneWorlds);
    private sealed record StageSample(double WallMilliseconds, double SourceFrame, double Milliseconds);
    private sealed record TickSample(double WallMilliseconds, double SourceFrame, double TickWorkMilliseconds);
    private sealed record CpuSample(int Index, double BeforeFrame, double SourceFrame, double AdvanceMilliseconds,
        double InspectorMilliseconds, double RequestMilliseconds, double SnapshotMilliseconds, double DecorateMilliseconds,
        double TotalMilliseconds, int OverlayPrimitives);
    private sealed record DraftSample(int Index, int BoneIndex, string BoneName, double SetterMilliseconds,
        double UpdateMilliseconds, double SnapshotMilliseconds, double LiveSourceFrame, double PreviewSourceFrame, bool RegisteredMotionUnchanged);
    private sealed record AsyncCallSample(int Index, double WallMilliseconds, double SetterMilliseconds,
        double RequestMilliseconds, decimal RotationY, bool ReturnedCompletedSnapshot);
    private sealed record WarmCallSample(int Index, double WallMilliseconds, double SetterMilliseconds,
        double RequestMilliseconds, decimal RotationY, int CompletedSnapshotsSoFar, bool ReturnedCompletedSnapshot);
    private sealed record WarmSnapshotSample(double WallMilliseconds, double SourceFrame, int RequestedBoneWorlds, long Generation);
}
