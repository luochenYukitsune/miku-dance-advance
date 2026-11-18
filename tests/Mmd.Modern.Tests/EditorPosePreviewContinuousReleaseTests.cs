using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Avalonia.Headless;
using Mmd.Modern.Animation;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>Dispatcher/worker contracts with a deterministic history-sensitive solver. These tests
/// compare an explicit continuation sequence, not a fresh canonical pose, after pointer release.</summary>
[Collection(QualityLoadingCollection.Name)]
public sealed class EditorPosePreviewContinuousReleaseTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static object? Field(object owner, string name)
        => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);
    private static Dictionary<int, BoneLocalTransform> Bones(float x) => new() { [0] = new(new(x, 0, 0), Quaternion.Identity) };

    [Fact]
    public async Task LatestReleaseInputPublishesBeforeSettlingAndTheSameOwnerMatchesAContinuationOracle() => await session.Dispatch(async () =>
    {
        await using var f = new Fixture();
        var liveFrame = f.Source.Frame;
        f.Editor.PositionX = 1;
        f.Request(interactive: true);
        await f.WaitLatest();
        await f.WaitIdle();
        var owner = Field(f.Preview, "warmOwner");
        var baseline = Assert.IsType<ScenePosePreviewRequest>(Field(f.Preview, "interactiveBaseline"));
        var backend = Assert.Single(f.Worker.Worlds);
        int initializations = backend.Initializations, steps = backend.Steps;
        Assert.Equal(1, steps);

        var oracleFactory = new Factory();
        using var oracle = baseline.CreateSession(oracleFactory.Provider(), Token);
        oracle.PreviewInteractiveBones(f.Id, Bones(1), 1d / 120, Token);
        oracle.PreviewInteractiveBones(f.Id, Bones(20), 0, Token);
        var expectedLatest = oracle.CaptureRenderSnapshot(f.Requests);
        SceneProjectRenderSnapshot? firstReleased = null;
        f.Preview.SnapshotPublished += (_, _) =>
        {
            if (f.Preview.State == EditorPosePreviewState.ReleasedBones && f.Preview.RemainingSettleSteps == 120)
                firstReleased ??= f.Snapshot();
        };

        // The final raw input need not have been sampled by an earlier drag work item.
        f.Editor.PositionX = 20;
        f.Request(interactive: false);
        Assert.False(f.Preview.HasLatestInputSnapshot);
        await f.Wait(() => firstReleased is not null);
        AssertPose(expectedLatest, firstReleased!, f.Id);
        Assert.Equal(new Vector3(20, 0, 0), firstReleased!.SceneInputs[f.Id].BoneWorldMatrices[0].Translation);
        Assert.Same(owner, Field(f.Preview, "warmOwner"));

        await f.WaitSettled();
        for (int batch = 0; batch < 60; batch++) oracle.PreviewInteractiveBones(f.Id, Bones(20), 2d / 120, Token);
        AssertPose(oracle.CaptureRenderSnapshot(f.Requests), f.Snapshot(), f.Id);
        Assert.Equal(steps + 120, backend.Steps);
        Assert.Equal(initializations, backend.Initializations);
        Assert.Same(owner, Field(f.Preview, "warmOwner"));
        Assert.True(f.Preview.HasLatestInputSnapshot); Assert.Equal(0, f.Preview.RemainingSettleSteps);
        Assert.Equal(EditorPosePreviewState.SettledBones, f.Preview.State);
        Assert.Same(liveFrame, f.Source.Frame); Assert.Equal(7, f.Source.CurrentFrame);
        Assert.False(f.Source.CanUndo); Assert.False(f.Source.CanRedo);
        // This fixture deliberately accumulates physical history; canonical reset is a different result.
        using var canonical = f.Canonical();
        Assert.NotEqual(canonical.CaptureRenderSnapshot(f.Requests).SceneInputs[f.Id].BoneWorldMatrices[1],
            f.Snapshot().SceneInputs[f.Id].BoneWorldMatrices[1]);
        return true;
    }, Token);

    [Fact]
    public async Task NewDragPreemptsSettlingAndClearRetiresTheOwnerWithoutLatePublications() => await session.Dispatch(async () =>
    {
        await using var f = new Fixture();
        f.Editor.PositionX = 1; f.Request(interactive: true); await f.WaitLatest();
        var owner = Field(f.Preview, "warmOwner");
        var backend = Assert.Single(f.Worker.Worlds);
        int initializations = backend.Initializations;
        f.Editor.PositionX = 2; f.Request(interactive: false); await f.WaitLatest();
        f.Editor.PositionX = 4; f.Request(interactive: true); await f.WaitLatest(); await f.WaitIdle();
        Assert.Same(owner, Field(f.Preview, "warmOwner"));
        Assert.Equal(initializations, backend.Initializations);
        Assert.Equal(EditorPosePreviewState.InteractiveBones, f.Preview.State);
        Assert.Equal(new Vector3(4, 0, 0), f.Snapshot().SceneInputs[f.Id].BoneWorldMatrices[0].Translation);
        var last = f.Snapshot();
        await Task.Delay(30, Token); Assert.Same(last, f.Snapshot());

        f.Editor.PositionX = 5; f.Request(interactive: false); await f.WaitLatest();
        f.Preview.Clear();
        int publications = f.Publications;
        await f.WaitIdle(); await Task.Delay(20, Token);
        Assert.Equal(publications, f.Publications); Assert.True(backend.Disposed);
        Assert.Null(Field(f.Preview, "warmOwner")); Assert.Null(Field(f.Preview, "asyncSnapshot"));
        Assert.Equal(EditorPosePreviewState.Source, f.Preview.State);
        Assert.False(f.Preview.HasLatestInputSnapshot); Assert.Equal(0, f.Preview.RemainingSettleSteps);
        return true;
    }, Token);

    [Fact]
    public async Task SameClockSeekInvalidatesContinuationAndKeepsCanonicalEvaluation() => await session.Dispatch(async () =>
    {
        await using var f = new Fixture();
        f.Editor.PositionX = 1; f.Request(interactive: true); await f.WaitLatest();
        f.Editor.PositionX = 2; f.Request(interactive: false); await f.WaitLatest();
        var backend = Assert.Single(f.Worker.Worlds);
        f.Source.Seek(7, Token);
        f.Request(interactive: false);
        await f.WaitLatest(); await f.WaitIdle();
        Assert.True(backend.Disposed); Assert.Null(Field(f.Preview, "warmOwner"));
        Assert.Equal(EditorPosePreviewState.Complete, f.Preview.State);
        using var canonical = f.Canonical();
        AssertPose(canonical.CaptureRenderSnapshot(f.Requests), f.Snapshot(), f.Id);
        Assert.False(f.Source.CanUndo);
        return true;
    }, Token);

    [Fact]
    public async Task FailedSettleRetiresItsFaultedOwnerAndPublishesAnExplicitCompleteFallback() => await session.Dispatch(async () =>
    {
        await using var f = new Fixture();
        f.Editor.PositionX = 1; f.Request(interactive: true); await f.WaitLatest(); await f.WaitIdle();
        var backend = Assert.Single(f.Worker.Worlds);
        backend.FailNextStep = true;
        f.Editor.PositionX = 2; f.Request(interactive: false);
        await f.Wait(() => f.Preview.State == EditorPosePreviewState.Complete && f.Preview.HasLatestInputSnapshot);
        await f.WaitIdle();
        Assert.True(backend.Disposed); Assert.Null(Field(f.Preview, "warmOwner"));
        Assert.Equal(2, f.Worker.Worlds.Count); Assert.All(f.Worker.Worlds, world => Assert.True(world.Disposed));
        Assert.Equal(0, f.Preview.RemainingSettleSteps); Assert.False(f.Preview.IsPreparingPreview);
        using var canonical = f.Canonical();
        AssertPose(canonical.CaptureRenderSnapshot(f.Requests), f.Snapshot(), f.Id);
        Assert.False(f.Source.CanUndo);
        return true;
    }, Token);

    [Fact]
    public async Task NumericEditAndRegistrationAfterReleaseRetainCanonicalKeysUndoAndDetachedOutput() => await session.Dispatch(async () =>
    {
        await using var f = new Fixture();
        var original = f.Source.Frame;
        f.Editor.PositionX = 1; f.Request(interactive: true); await f.WaitLatest();
        f.Editor.PositionX = 2; f.Request(interactive: false); await f.WaitSettled();
        // An idle numeric change is a new ordinary complete request, not a resumed drag.
        f.Editor.PositionX = 9; f.Request(interactive: false); await f.WaitLatest(); await f.WaitIdle();
        Assert.Equal(EditorPosePreviewState.Complete, f.Preview.State);
        using var canonical = f.Canonical();
        AssertPose(canonical.CaptureRenderSnapshot(f.Requests), f.Snapshot(), f.Id);
        Assert.Same(original, f.Source.Frame); Assert.False(f.Source.CanUndo);

        var authored = f.Source.CaptureAuthoredContent().Motions.Single();
        var motion = f.Editor.PrepareDraftMotions([authored]).Single().CreateMotion(confirmOverwrite: true);
        f.Source.Edit(edit => edit.Motions[0] = authored with { Motion = motion }, Token);
        f.Preview.Clear(); await f.WaitIdle();
        Assert.True(f.Source.CanUndo);
        Assert.Equal(new Vector3(9, 0, 0), f.Source.CaptureRenderSnapshot(f.Requests).SceneInputs[f.Id].BoneWorldMatrices[0].Translation);
        var committed = f.Source.CaptureAuthoredContent().Motions.Single().Motion;
        Assert.Contains(committed.BoneKeyframes, key => key.BoneName == "root" && key.Frame == 7 && key.Translation.X == 9);
        // Detached output starts from authored keys and the ordinary initialization policy.
        using var output = f.Source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion>()).CreateSession(new Factory().Provider(), Token);
        AssertPose(f.Source.CaptureRenderSnapshot(f.Requests), output.CaptureRenderSnapshot(f.Requests), f.Id);
        Assert.True(f.Source.Undo(Token));
        Assert.Equal(Vector3.Zero, f.Source.CaptureRenderSnapshot(f.Requests).SceneInputs[f.Id].BoneWorldMatrices[0].Translation);
        Assert.True(f.Source.Redo(Token));
        Assert.Equal(new Vector3(9, 0, 0), f.Source.CaptureRenderSnapshot(f.Requests).SceneInputs[f.Id].BoneWorldMatrices[0].Translation);
        return true;
    }, Token);

    private static void AssertPose(SceneProjectRenderSnapshot expected, SceneProjectRenderSnapshot actual, Guid id)
    {
        Assert.Equal(expected.SceneInputs[id].BoneWorldMatrices.ToArray(), actual.SceneInputs[id].BoneWorldMatrices.ToArray());
        Assert.Equal(expected.Scene.Surface.Snapshot.Vertices.ToArray(), actual.Scene.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(expected.SourceFrame, actual.SourceFrame);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Factory Worker = new();
        internal readonly SceneProjectSession Source;
        internal readonly EditorAnimationViewModel Editor = new();
        internal readonly EditorPosePreview Preview;
        internal readonly Guid Id;
        internal readonly SceneObjectRenderRequest[] Requests;
        internal int Publications;
        private Exception? failure;
        internal Fixture()
        {
            var model = Model();
            var rest = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
                PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, 10), 30, 1),
                materials: PmxOriginalFrameAdapter.FromMaterials(model));
            var layout = new SceneCompositionEditor();
            Id = layout.Add("continuous", Path.GetFullPath("continuous-release-fixture.pmx"), ModelRole.Character, rest).Id;
            VmdMotion motion = new() { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 30 }] };
            Source = new(layout, new(), [new(Id, model, motion)], new Factory().Provider(),
                new() { Physics = new() { GroundEnabled = false } }, initialFrame: 7, cancellationToken: Token);
            Requests = [new(Id, [0, 1])];
            Editor.SetActor(Id, model, motion); Editor.CurrentFrame = 7; Editor.SelectBone(0);
            Preview = new(() => Worker.Provider());
        }
        internal void Request(bool interactive) => Preview.TryGetOrRequest(Source, Editor, Requests,
            () => Publications++, error => failure = error, interactive: interactive);
        internal SceneProjectRenderSnapshot Snapshot() => Assert.IsType<SceneProjectRenderSnapshot>(Field(Preview, "asyncSnapshot"));
        internal Task WaitLatest() => Wait(() => Preview.HasLatestInputSnapshot && !Preview.IsPreparingPreview);
        internal Task WaitSettled() => Wait(() => Preview.State == EditorPosePreviewState.SettledBones
            && Preview.HasLatestInputSnapshot && Preview.RemainingSettleSteps == 0);
        internal Task WaitIdle() => Wait(() => !(bool)Field(Preview, "workerRunning")!);
        internal async Task Wait(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (failure is not null) throw new InvalidOperationException("Continuous preview failed", failure);
                if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Continuous preview did not publish/settle/retire.");
                await Task.Delay(1, Token);
            }
            if (failure is not null) throw new InvalidOperationException("Continuous preview failed", failure);
        }
        internal SceneProjectSession Canonical()
        {
            var motions = Editor.PrepareDraftMotions(Source.CaptureAuthoredContent().Motions)
                .ToDictionary(plan => plan.ObjectId, plan => plan.CreateMotion(confirmOverwrite: true));
            return Source.CapturePosePreviewRequest(motions).CreateSession(new Factory().Provider(), Token);
        }
        public async ValueTask DisposeAsync()
        { Preview.Dispose(); await WaitIdle(); Source.Dispose(); }
    }

    private static PmxModel Model()
    {
        var m = MorphApplicationTests.Model([]);
        PmxRigidBody Body(string name, int bone, PmxRigidBodyOperation operation, Vector3 position) => new()
        {
            Name = name, EnglishName = "", BoneIndex = bone, Group = 0, CollisionGroup = 0,
            Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = position,
            Rotation = Vector3.Zero, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0,
            Mass = operation == PmxRigidBodyOperation.Static ? 0 : 1, Operation = operation
        };
        return new()
        {
            Header = m.Header, Name = m.Name, EnglishName = m.EnglishName, Comment = m.Comment, EnglishComment = m.EnglishComment,
            Vertices = m.Vertices, Faces = m.Faces, Textures = m.Textures, Materials = m.Materials,
            Bones = [m.Bones[0], new() { Name = "history", EnglishName = "", Position = new(0, 3, 0), ParentIndex = -1,
                DeformDepth = 1, Flags = PmxBoneFlags.AllowRotate | PmxBoneFlags.AllowTranslate }],
            Morphs = m.Morphs, DisplayFrames = m.DisplayFrames,
            RigidBodies = [Body("target", 0, PmxRigidBodyOperation.Static, Vector3.Zero),
                Body("history", 1, PmxRigidBodyOperation.Dynamic, new(0, 3, 0))], Joints = [], SoftBodies = []
        };
    }

    private sealed class Factory
    {
        internal readonly ConcurrentQueue<HistoryBackend> Worlds = new();
        internal ProjectSettingsBackendProvider Provider() => new((_, _) =>
        { var backend = new HistoryBackend(); Worlds.Enqueue(backend); return backend; },
            _ => throw new InvalidOperationException("This fixture uses independent physics."), poseInitializationSubsteps: 0);
    }

    private sealed class HistoryBackend : ICharacterPhysicsBackend, ICharacterGravityBackend
    {
        private Matrix4x4[] worlds = [];
        internal int Initializations, Steps;
        internal volatile bool Disposed, FailNextStep;
        public string Name => "deterministic continuation oracle";
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms)
        { Initializations++; worlds = transforms.ToArray(); }
        public void SetGravity(Vector3 gravity) { }
        public void SetKinematicTarget(int bodyIndex, Matrix4x4 worldTransform, float stepSeconds) => worlds[bodyIndex] = worldTransform;
        public Matrix4x4 GetBodyWorldTransform(int bodyIndex) => worlds[bodyIndex];
        public void Step(float seconds)
        {
            if (FailNextStep) { FailNextStep = false; throw new InvalidOperationException("Injected continuous settle failure"); }
            Steps++;
            // The dynamic body remembers the target trajectory, so recreating/resetting a solver
            // or discarding a physical batch produces a different independently calculable pose.
            worlds[1].Translation += new Vector3(worlds[0].Translation.X * seconds, -seconds, 0);
        }
        public void Dispose() => Disposed = true;
    }

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
