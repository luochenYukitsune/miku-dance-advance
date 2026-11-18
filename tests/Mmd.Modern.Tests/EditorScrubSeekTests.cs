using System.Collections.Concurrent;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Runtime;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorScrubSeekTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly Pointer Mouse = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);

    [Fact]
    public async Task RapidRulerScrubCoalescesAndCancelsIntermediateRequestsAndMatchesSynchronousSeek() => await session.Dispatch(async () =>
    {
        var factory = new BlockingPhysicsFactory(120);
        await using var harness = await CreateHarnessAsync(factory.CreateProvider);
        harness.AddActorAndMotion(endFrame: 60, translationX: 18);

        InteractionDiagnostics.Start(new InteractionDiagnosticsOptions { Capacity = 4096 });
        try
        {
            factory.GateWorkerStep();
            PressRuler(harness.Window, harness.Timeline, frame: 1);
            await factory.WaitForWorkerEntryAsync(Token);

            // While frame 1 is stalled on the worker, rapid pointer moves must coalesce via
            // latest-request-wins without executing intermediate frames.
            for (int frame = 2; frame <= 15; frame++)
                MoveRuler(harness.Window, harness.Timeline, frame);

            Assert.True(harness.Shell.IsScrubbing);
            Assert.True(harness.Shell.HasPendingScrubSeek);
            Assert.Equal(15, harness.Shell.CurrentFrame);

            factory.OpenGate();
            ReleaseRuler(harness.Window, harness.Timeline, frame: 15);
            await harness.WaitScrubSettledAsync();

            Assert.False(harness.Shell.IsScrubbing);
            Assert.False(harness.Shell.HasPendingScrubSeek);
            Assert.Equal(15, harness.Shell.CurrentFrame);
            Assert.Equal(15, harness.Shell.Workspace.Session!.CurrentFrame);

            // AddModel (0) + CommitMotion (1) + cancelled frame-1 worker world (2) + published frame-15 world (3).
            var worlds = factory.Worlds.ToArray();
            Assert.Equal(4, worlds.Length);
            Assert.True(worlds[0].Disposed);
            Assert.True(worlds[1].Disposed);
            Assert.True(worlds[2].Disposed);
            Assert.InRange(worlds[2].StepSeconds.Count, 1, 119);
            Assert.False(worlds[3].Disposed);
            Assert.Equal(120, worlds[3].StepSeconds.Count);
            Assert.All(worlds[3].StepSeconds, dt => Assert.Equal(1f / 120, dt));

            // Compare exact final vertices against a fresh project performing synchronous Seek(15).
            var syncFactory = new BlockingPhysicsFactory(120);
            await using var syncHarness = await CreateHarnessAsync(syncFactory.CreateProvider);
            syncHarness.AddActorAndMotion(endFrame: 60, translationX: 18);
            syncHarness.Shell.Workspace.Session!.Seek(15, Token);
            Assert.Equal(
                syncHarness.Shell.Workspace.Session.Frame.Surface.Snapshot.Vertices,
                harness.Shell.Workspace.Session.Frame.Surface.Snapshot.Vertices);

            var events = InteractionDiagnostics.Snapshot().Events.Select(e => e.Stage).ToArray();
            Assert.Contains("scrub.request", events);
            Assert.Contains("scrub.admitted", events);
            Assert.Contains("scrub.superseded", events);
            Assert.Contains("scrub.cancelled", events);
            Assert.Contains("scrub.prepare", events);
            Assert.Contains("scrub.published", events);
        }
        finally
        {
            InteractionDiagnostics.Stop();
        }
        return true;
    }, Token);

    [Fact]
    public async Task EditUndoRedoAndPoseDraftDuringPendingScrubNeverAllowStaleOverwrite() => await session.Dispatch(async () =>
    {
        var factory = new BlockingPhysicsFactory(16);
        await using var harness = await CreateHarnessAsync(factory.CreateProvider);
        Guid actorId = harness.AddActorAndMotion(endFrame: 60, translationX: 10);

        // Create an undoable prior motion edit before starting the scrub sequence.
        harness.Shell.Workspace.CommitMotion(actorId, Motion(60, 20));
        Assert.True(harness.Shell.Workspace.CanUndo);

        // 1. CommitMotion during pending scrub cancels the stale worker request and commits at CurrentFrame.
        factory.GateWorkerStep();
        PressRuler(harness.Window, harness.Timeline, frame: 2);
        await factory.WaitForWorkerEntryAsync(Token);
        MoveRuler(harness.Window, harness.Timeline, frame: 20);
        Assert.True(harness.Shell.HasPendingScrubSeek);

        harness.Shell.Animation.CommitMotion!(actorId, Motion(60, 30));
        Assert.False(harness.Shell.HasPendingScrubSeek);
        Assert.Equal(20, harness.Shell.Workspace.Session!.CurrentFrame);
        factory.OpenGate();
        ReleaseRuler(harness.Window, harness.Timeline, frame: 20);
        await harness.WaitScrubSettledAsync();

        var expectedAfterCommit = harness.Shell.Workspace.Session.Frame.Surface.Snapshot.Vertices.ToArray();
        Assert.Equal(11, MathF.Round(expectedAfterCommit[0].Position.X)); // Rest vertex at X=1 + 20/60*30 motion.

        // 2. UndoCommand during pending scrub restores prior state at displayed frame and ignores stale worker.
        factory.GateWorkerStep();
        PressRuler(harness.Window, harness.Timeline, frame: 3);
        await factory.WaitForWorkerEntryAsync(Token);
        MoveRuler(harness.Window, harness.Timeline, frame: 24);
        Assert.True(harness.Shell.HasPendingScrubSeek);

        harness.Shell.UndoCommand.Execute(null);
        Assert.False(harness.Shell.HasPendingScrubSeek);
        Assert.True(harness.Shell.Workspace.CanRedo);
        Assert.Equal(24, harness.Shell.Workspace.Session.CurrentFrame);
        var expectedAfterUndo = harness.Shell.Workspace.Session.Frame.Surface.Snapshot.Vertices.ToArray();

        factory.OpenGate();
        ReleaseRuler(harness.Window, harness.Timeline, frame: 24);
        await harness.WaitScrubSettledAsync();
        Assert.True(harness.Shell.Workspace.CanRedo);
        Assert.Equal(24, harness.Shell.Workspace.Session.CurrentFrame);
        Assert.Equal(expectedAfterUndo, harness.Shell.Workspace.Session.Frame.Surface.Snapshot.Vertices.ToArray());

        // 3. RedoCommand during pending scrub re-applies state and ignores stale worker.
        factory.GateWorkerStep();
        PressRuler(harness.Window, harness.Timeline, frame: 4);
        await factory.WaitForWorkerEntryAsync(Token);
        MoveRuler(harness.Window, harness.Timeline, frame: 24);
        harness.Shell.RedoCommand.Execute(null);
        Assert.False(harness.Shell.HasPendingScrubSeek);
        Assert.False(harness.Shell.Workspace.CanRedo);
        var expectedAfterRedo = harness.Shell.Workspace.Session.Frame.Surface.Snapshot.Vertices.ToArray();

        factory.OpenGate();
        ReleaseRuler(harness.Window, harness.Timeline, frame: 24);
        await harness.WaitScrubSettledAsync();
        Assert.False(harness.Shell.Workspace.CanRedo);
        Assert.Equal(expectedAfterRedo, harness.Shell.Workspace.Session.Frame.Surface.Snapshot.Vertices.ToArray());

        // 4. Bone pose draft edit during pending scrub cancels scrub and syncs session to CurrentFrame.
        harness.Shell.Browser.SelectedBoneIndex = 0;
        factory.GateWorkerStep();
        PressRuler(harness.Window, harness.Timeline, frame: 5);
        await factory.WaitForWorkerEntryAsync(Token);
        MoveRuler(harness.Window, harness.Timeline, frame: 18);
        harness.Shell.Animation.PositionX = 4m;
        Assert.True(harness.Shell.Animation.HasAnyDraft);
        Assert.False(harness.Shell.HasPendingScrubSeek);
        Assert.Equal(18, harness.Shell.Workspace.Session.CurrentFrame);

        factory.OpenGate();
        ReleaseRuler(harness.Window, harness.Timeline, frame: 18);
        await harness.WaitScrubSettledAsync();
        Assert.True(harness.Shell.Animation.HasAnyDraft);
        Assert.Equal(18, harness.Shell.Workspace.Session.CurrentFrame);
        return true;
    }, Token);

    [Fact]
    public async Task PlaybackStartAndExplicitSyncSeekDuringPendingScrubAreDeterministic() => await session.Dispatch(async () =>
    {
        var factory = new BlockingPhysicsFactory(16);
        await using var harness = await CreateHarnessAsync(factory.CreateProvider);
        harness.AddActorAndMotion(endFrame: 60, translationX: 30);

        // 1. PlayCommand while scrub is pending synchronizes playhead to 18 and rejects stale worker.
        factory.GateWorkerStep();
        PressRuler(harness.Window, harness.Timeline, frame: 1);
        await factory.WaitForWorkerEntryAsync(Token);
        MoveRuler(harness.Window, harness.Timeline, frame: 18);
        Assert.True(harness.Shell.HasPendingScrubSeek);

        harness.Shell.PlayCommand.Execute(null);
        StopPlaybackTimer(harness.Shell);
        Assert.True(harness.Shell.IsPlaying);
        Assert.False(harness.Shell.HasPendingScrubSeek);
        Assert.Equal(18, harness.Shell.Workspace.Session!.CurrentFrame);
        harness.Shell.Workspace.Session.Advance(1d / 60, Token);
        double advancedFrame = harness.Shell.Workspace.Session.CurrentFrame;
        Assert.InRange(advancedFrame, 18.4, 18.6);

        factory.OpenGate();
        ReleaseRuler(harness.Window, harness.Timeline, frame: 18);
        await harness.WaitScrubSettledAsync();
        Assert.True(harness.Shell.IsPlaying);
        Assert.Equal(advancedFrame, harness.Shell.Workspace.Session.CurrentFrame);
        harness.Shell.StopPlayback();

        // 2. Explicit synchronous CurrentFrame seek while released background scrub is still in-flight wins.
        factory.GateWorkerStep();
        PressRuler(harness.Window, harness.Timeline, frame: 22);
        await factory.WaitForWorkerEntryAsync(Token);
        ReleaseRuler(harness.Window, harness.Timeline, frame: 22);
        Assert.True(harness.Shell.HasPendingScrubSeek);

        harness.Shell.CurrentFrame = 6;
        Assert.False(harness.Shell.HasPendingScrubSeek);
        Assert.Equal(6, harness.Shell.Workspace.Session.CurrentFrame);

        factory.OpenGate();
        await harness.WaitScrubSettledAsync();
        Assert.Equal(6, harness.Shell.CurrentFrame);
        Assert.Equal(6, harness.Shell.Workspace.Session.CurrentFrame);
        return true;
    }, Token);

    [Fact]
    public async Task ProjectResetAndDisposeDuringPendingScrubDrainSafelyWithoutExceptions() => await session.Dispatch(async () =>
    {
        var factory = new BlockingPhysicsFactory(16);
        await using var harness = await CreateHarnessAsync(factory.CreateProvider);
        harness.AddActorAndMotion(endFrame: 60, translationX: 12);

        // Workspace.New while worker is mid-prepare cancels pending work cleanly.
        factory.GateWorkerStep();
        PressRuler(harness.Window, harness.Timeline, frame: 14);
        await factory.WaitForWorkerEntryAsync(Token);
        harness.Shell.Workspace.New();
        Assert.False(harness.Shell.HasProject);
        Assert.False(harness.Shell.HasPendingScrubSeek);

        factory.OpenGate();
        ReleaseRuler(harness.Window, harness.Timeline, frame: 14);
        await harness.WaitScrubSettledAsync();
        Assert.False(harness.Shell.HasProject);
        Assert.All(factory.Worlds, w => Assert.True(w.Disposed));

        // Direct Shell.Dispose while worker finishes a slow step drains cleanly without publishing or deadlock.
        harness.AddActorAndMotion(endFrame: 60, translationX: 12);
        factory.GateWorkerStep();
        PressRuler(harness.Window, harness.Timeline, frame: 10);
        await factory.WaitForWorkerEntryAsync(Token);
        _ = Task.Run(async () =>
        {
            await Task.Delay(30, Token);
            factory.OpenGate();
        }, Token);
        await harness.DisposeAsync();
        Assert.All(factory.Worlds, w => Assert.True(w.Disposed));
        return true;
    }, Token);

    [Fact]
    public async Task RealBulletHeadlessTimelineScrubMatchesSynchronousSeek() => await session.Dispatch(async () =>
    {
        IProjectPhysicsBackendProvider CreateBullet() => ProjectPhysicsRuntime.CreateProjectProvider(120);
        await using var harness = await CreateHarnessAsync(CreateBullet);
        harness.AddActorAndMotion(endFrame: 60, translationX: 15);

        PressRuler(harness.Window, harness.Timeline, frame: 4);
        for (int frame = 5; frame <= 24; frame++)
        {
            MoveRuler(harness.Window, harness.Timeline, frame);
            Dispatcher.UIThread.RunJobs();
        }
        ReleaseRuler(harness.Window, harness.Timeline, frame: 24);
        await harness.WaitScrubSettledAsync();

        Assert.Equal(24, harness.Shell.CurrentFrame);
        Assert.Equal(24, harness.Shell.Workspace.Session!.CurrentFrame);

        await using var syncHarness = await CreateHarnessAsync(CreateBullet);
        syncHarness.AddActorAndMotion(endFrame: 60, translationX: 15);
        syncHarness.Shell.Workspace.Session!.Seek(24, Token);
        Assert.Equal(
            syncHarness.Shell.Workspace.Session.Frame.Surface.Snapshot.Vertices,
            harness.Shell.Workspace.Session.Frame.Surface.Snapshot.Vertices);
        return true;
    }, Token);

    private static async Task<ScrubWindowHarness> CreateHarnessAsync(Func<IProjectPhysicsBackendProvider> physicsFactory)
    {
        string directory = Path.Combine(Path.GetTempPath(), "mda-scrub-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var preferences = new EditorPreferences { RecoveryDirectory = Path.Combine(directory, "recovery"), AutoSaveEnabled = false };
        var shell = new EditorShellViewModel();
        shell.Workspace.PackageCacheDirectory = Path.Combine(directory, "cache");
        shell.Workspace.PhysicsProviderFactory = physicsFactory;
        var window = new EditorWindow(false, preferences) { Width = 1400, Height = 900, DataContext = shell };
        window.FindControl<EditorViewportHost>("SceneViewport")!.IsSuspended = true;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var timeline = window.FindControl<EditorTimeline>("FrameTimeline")!;
        return new ScrubWindowHarness(directory, shell, window, timeline);
    }

    private static void StopPlaybackTimer(EditorShellViewModel shell)
    {
        var timer = (DispatcherTimer)typeof(EditorShellViewModel)
            .GetField("playbackTimer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(shell)!;
        timer.Stop();
    }

    private static VmdMotion Motion(uint end, float translationX) => new()
    {
        ModelName = "scrub-actor",
        BoneKeyframes =
        [
            new() { BoneName = "root", Frame = 0 },
            new() { BoneName = "root", Frame = end, Translation = new(translationX, 0, 0) }
        ]
    };

    private static Point RulerPoint(int frame) => new(220 + frame * 8, 10);

    private static void PressRuler(Window window, EditorTimeline timeline, int frame) =>
        timeline.RaiseEvent(new PointerPressedEventArgs(timeline, Mouse, window,
            timeline.TranslatePoint(RulerPoint(frame), window)!.Value, 1,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));

    private static void MoveRuler(Window window, EditorTimeline timeline, int frame) =>
        timeline.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, timeline, Mouse, window,
            timeline.TranslatePoint(RulerPoint(frame), window)!.Value, 2,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));

    private static void ReleaseRuler(Window window, EditorTimeline timeline, int frame) =>
        timeline.RaiseEvent(new PointerReleasedEventArgs(timeline, Mouse, window,
            timeline.TranslatePoint(RulerPoint(frame), window)!.Value, 3,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));

    private sealed class ScrubWindowHarness(
        string directory,
        EditorShellViewModel shell,
        EditorWindow window,
        EditorTimeline timeline) : IAsyncDisposable
    {
        private bool disposed;
        internal EditorShellViewModel Shell { get; } = shell;
        internal EditorWindow Window { get; } = window;
        internal EditorTimeline Timeline { get; } = timeline;

        internal Guid AddActorAndMotion(uint endFrame, float translationX)
        {
            var model = PhysicsModel();
            string path = Path.Combine(directory, "actor.pmx");
            File.WriteAllBytes(path, PmxWriter.ToBytes(model));
            Guid id = Shell.Workspace.AddModel(path, EditorProjectWorkspace.CreateModelAsset(model, path, Token), ModelRole.Character);
            Shell.Workspace.CommitMotion(id, Motion(endFrame, translationX));
            Shell.RefreshWorkspace(id);
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            return id;
        }

        internal async Task WaitScrubSettledAsync()
        {
            await Shell.WaitForScrubIdleAsync().WaitAsync(TimeSpan.FromSeconds(10), Token);
            Dispatcher.UIThread.RunJobs();
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            await Window.CompleteRecoveryShutdownAsync();
            Window.DataContext = null;
            Window.Close();
            Shell.Dispose();
            Dispatcher.UIThread.RunJobs();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static PmxModel PhysicsModel()
    {
        var m = MorphApplicationTests.Model([]);
        return new()
        {
            Header = m.Header, Name = "scrub-actor", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = m.Vertices, Faces = m.Faces, Textures = m.Textures, Materials = m.Materials,
            Bones =
            [
                m.Bones[0],
                new()
                {
                    Name = "tail", EnglishName = "", Position = new(0, 2, 0), ParentIndex = 0,
                    DeformDepth = 1, Flags = PmxBoneFlags.AllowRotate | PmxBoneFlags.AllowTranslate
                }
            ],
            Morphs = m.Morphs, DisplayFrames = m.DisplayFrames,
            RigidBodies =
            [
                new()
                {
                    Name = "root-body", EnglishName = "", BoneIndex = 0, Group = 0, CollisionGroup = ushort.MaxValue,
                    Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = Vector3.Zero,
                    Rotation = Vector3.Zero, Mass = 0, TranslationDamping = 0, RotationDamping = 0,
                    Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Static
                },
                new()
                {
                    Name = "tail-body", EnglishName = "", BoneIndex = 1, Group = 0, CollisionGroup = ushort.MaxValue,
                    Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = new(0, 2, 0),
                    Rotation = Vector3.Zero, Mass = 1, TranslationDamping = 0, RotationDamping = 0,
                    Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Dynamic
                }
            ],
            Joints = [], SoftBodies = []
        };
    }

    private sealed class BlockingPhysicsFactory(int substeps)
    {
        private readonly ManualResetEventSlim gate = new(initialState: true);
        private volatile bool gateNextWorker;
        private TaskCompletionSource? workerEntered;
        internal readonly ConcurrentQueue<ProbeBackend> Worlds = new();

        internal IProjectPhysicsBackendProvider CreateProvider()
            => new ProjectSettingsBackendProvider((_, _) => Create(), _ => Create(), poseInitializationSubsteps: substeps);

        internal void GateWorkerStep()
        {
            workerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.Reset();
            gateNextWorker = true;
        }

        internal async Task WaitForWorkerEntryAsync(CancellationToken cancellationToken)
        {
            if (workerEntered is not null)
                await workerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }

        internal void OpenGate()
        {
            gateNextWorker = false;
            gate.Set();
        }

        private ProbeBackend Create()
        {
            var world = new ProbeBackend(this);
            Worlds.Enqueue(world);
            return world;
        }

        internal void OnStep()
        {
            if (!gateNextWorker || Dispatcher.UIThread.CheckAccess()) return;
            workerEntered?.TrySetResult();
            gate.Wait(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class ProbeBackend(BlockingPhysicsFactory factory) : ICharacterPhysicsMutableBackend
    {
        private readonly List<(PhysicsBodyDefinition Def, Matrix4x4 World)> bodies = [];
        internal readonly List<float> StepSeconds = [];
        internal volatile bool Disposed;
        public string Name => "blocking scrub probe";
        public IReadOnlyList<string> Diagnostics => [];

        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms)
        {
            bodies.Clear();
            for (int i = 0; i < definition.Bodies.Count; i++)
                bodies.Add((definition.Bodies[i], transforms[i]));
        }

        public int AddBody(PhysicsBodyDefinition body, Matrix4x4 transform)
        {
            bodies.Add((body, transform));
            return bodies.Count - 1;
        }

        public int AddJoint(PhysicsJointDefinition joint, Matrix4x4 a, Matrix4x4 b) => 0;
        public void RemoveBody(int index) { }
        public void RemoveJoint(int index) { }
        public void ResetBody(int index, Matrix4x4 transform) { }
        public void SetBodyKinematic(int index, bool state) { }
        public void SetKinematicTarget(int index, Matrix4x4 transform, float stepSeconds)
            => bodies[index] = (bodies[index].Def, transform);
        public Matrix4x4 GetBodyWorldTransform(int index) => bodies[index].World;
        public void SetGravity(Vector3 gravity) { }
        public void ApplyImpulse(int index, Vector3 linear, Vector3 angular) { }

        public void Step(float seconds)
        {
            StepSeconds.Add(seconds);
            if (bodies.Count >= 2)
            {
                // Deterministically couple dynamic rigid body 1 to kinematic body 0's position.
                var world = bodies[1].World;
                world.Translation = new(bodies[0].World.Translation.X, world.Translation.Y - seconds, world.Translation.Z);
                bodies[1] = (bodies[1].Def, world);
            }
            factory.OnStep();
        }

        public void Dispose() => Disposed = true;
    }

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
