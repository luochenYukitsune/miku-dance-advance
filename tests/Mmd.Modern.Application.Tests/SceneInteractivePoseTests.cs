using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

/// <summary>Fixed-frame interactive preview ownership and publication; deterministic CPU backends only.</summary>
public sealed class SceneInteractivePoseTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RepeatedBoneDraftsKeepOneWarmSolverAndPublishMatchingBonesAndGeometryAtTheSameClock()
    {
        var liveFactory = new Factory();
        using var source = Create(liveFactory, out var id);
        var sourceFrame = source.Frame;
        var sourceMotion = Assert.Single(source.CaptureAuthoredContent().Motions).Motion;
        var liveWorld = Assert.Single(liveFactory.Worlds);
        int liveInitializations = liveWorld.Initializations;
        var request = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion>());
        Assert.True(request.SupportsInteractiveBones);
        var inputs = new[] { new SceneObjectRenderRequest(id, [0], includeSourceGeometry: true) };
        var baseline = source.CaptureRenderSnapshot(inputs);
        var workerFactory = new Factory();
        var results = await Task.Run(() =>
        {
            using var owner = request.CreateSession(workerFactory.Provider(), Token);
            var world = Assert.Single(workerFactory.Worlds);
            int initializations = world.Initializations;
            Assert.Equal(120, world.Steps);
            owner.PreviewInteractiveBones(id, Bones(15), 1d / 60, Token);
            var first = owner.CaptureRenderSnapshot(inputs);
            owner.PreviewInteractiveBones(id, Bones(23), 1d / 60, Token);
            var second = owner.CaptureRenderSnapshot(inputs);
            owner.PreviewInteractiveBones(id, new Dictionary<int, BoneLocalTransform>(), 1d / 60, Token);
            var restored = owner.CaptureRenderSnapshot(inputs);
            Assert.Single(workerFactory.Worlds);
            Assert.Equal(initializations, world.Initializations);
            Assert.Equal(126, world.Steps);
            Assert.Equal(7, owner.CurrentFrame);
            Assert.Equal(7, Assert.Single(owner.Actors).LocalFrame);
            Assert.Same(sourceMotion, Assert.Single(owner.CaptureAuthoredContent().Motions).Motion);
            Assert.False(owner.CanUndo); Assert.False(owner.CanRedo);
            return (first, second, restored);
        }, Token);

        AssertPose(results.first, id, 15);
        AssertPose(results.second, id, 23);
        Assert.Equal(baseline.Scene.Surface.Snapshot.Vertices.ToArray(), results.restored.Scene.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(baseline.SceneInputs[id].BoneWorldMatrices[0], results.restored.SceneInputs[id].BoneWorldMatrices[0]);
        Assert.Equal(baseline.RenderingStyles.ToArray(), results.first.RenderingStyles.ToArray());
        Assert.Equal(baseline.Scene.Materials.ToArray(), results.first.Scene.Materials.ToArray());
        Assert.Same(sourceFrame, source.Frame);
        Assert.Same(sourceMotion, Assert.Single(source.CaptureAuthoredContent().Motions).Motion);
        Assert.Equal(7, source.CurrentFrame);
        Assert.Equal(liveInitializations, liveWorld.Initializations); Assert.Equal(120, liveWorld.Steps);
        Assert.True(request.IsCurrent(source)); Assert.False(source.CanUndo); Assert.False(source.CanRedo);
    }

    [Fact]
    public void LiveAndOrdinarySynchronousOwnersCannotBypassTheTransactionThroughInteractiveEvaluation()
    {
        var factory = new Factory();
        using var source = Create(factory, out var id);
        var before = source.Frame; int created = factory.Worlds.Count;
        Assert.Throws<InvalidOperationException>(() => source.PreviewInteractiveBones(id, Bones(15), 1d / 60, Token));
        Assert.Same(before, source.Frame); Assert.Equal(created, factory.Worlds.Count);
        using var ordinary = source.CreatePosePreview(new Dictionary<Guid, VmdMotion>(), Token);
        Assert.Throws<InvalidOperationException>(() => ordinary.PreviewInteractiveBones(id, Bones(15), 1d / 60, Token));
        Assert.False(source.CanUndo); Assert.False(source.CanRedo);
    }

    [Theory]
    [InlineData("parents")]
    [InlineData("soft")]
    public void UnsupportedGraphsRetainTheCompleteRequestPathAndRejectInteractiveEvaluationBeforeStepping(string graph)
    {
        var live = new Factory();
        using var source = Create(live, out var id, graph);
        var before = source.Frame;
        var request = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion>());
        Assert.False(request.SupportsInteractiveBones);
        var worker = new Factory();
        using var owner = request.CreateSession(worker.Provider(), Token);
        var completed = owner.Frame;
        int steps = worker.Worlds.Sum(world => world.Steps);
        int initializations = worker.Worlds.Sum(world => world.Initializations);
        Assert.Throws<NotSupportedException>(() => owner.PreviewInteractiveBones(id, Bones(15), 1d / 60, Token));
        Assert.Equal(steps, worker.Worlds.Sum(world => world.Steps));
        Assert.Equal(initializations, worker.Worlds.Sum(world => world.Initializations));
        Assert.Same(completed, owner.Frame); Assert.False(owner.IsFaulted);
        Assert.Equal(7, owner.CaptureRenderSnapshot().SourceFrame);
        Assert.Same(before, source.Frame); Assert.True(request.IsCurrent(source));
    }

    [Theory]
    [InlineData("shared")]
    [InlineData("multiple")]
    [InlineData("pmm")]
    public void RigidGraphsRetainWarmOwnersAndPublishBoneDraftsWithoutRecreatingTheWorld(string graph)
    {
        using var source = Create(new Factory(), out var id, graph);
        var sourceFrame = source.Frame;
        var request = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion>());
        Assert.True(request.SupportsInteractiveBones);
        var worker = new Factory();
        using var owner = request.CreateSession(worker.Provider(), Token);
        var before = owner.CaptureRenderSnapshot([new(id, [0])]);
        int worlds = worker.Worlds.Count, initializations = worker.Worlds.Sum(world => world.Initializations);
        owner.PreviewInteractiveBones(id, Bones(15), 1d / 60, Token);
        var first = owner.CaptureRenderSnapshot([new(id, [0])]);
        owner.PreviewInteractiveBones(id, Bones(23), 1d / 60, Token);
        var second = owner.CaptureRenderSnapshot([new(id, [0])]);
        Assert.NotEqual(before.SceneInputs[id].BoneWorldMatrices[0], first.SceneInputs[id].BoneWorldMatrices[0]);
        Assert.NotEqual(first.SceneInputs[id].BoneWorldMatrices[0], second.SceneInputs[id].BoneWorldMatrices[0]);
        Assert.Equal(worlds, worker.Worlds.Count);
        Assert.Equal(initializations, worker.Worlds.Sum(world => world.Initializations));
        Assert.Equal(7, owner.CurrentFrame);
        Assert.All(owner.Actors, actor => Assert.Equal(7, actor.LocalFrame));
        Assert.Same(sourceFrame, source.Frame); Assert.True(request.IsCurrent(source));
        Assert.False(owner.CanUndo); Assert.False(owner.CanRedo);
    }

    [Fact]
    public async Task SourceComparisonUsesCapturedIdentitiesAndRemainsSafeAfterSourceDisposal()
    {
        var factory = new Factory();
        using var source = Create(factory, out var id);
        var empty = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion>());
        var draft = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion> { [id] = Motion(60) });
        Assert.True(empty.HasSameSource(draft)); Assert.True(draft.HasSameSource(empty));
        source.Seek(source.CurrentFrame, Token);
        var afterSeek = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion>());
        Assert.False(empty.HasSameSource(afterSeek));
        source.Dispose();
        Assert.True(await Task.Run(() => empty.HasSameSource(draft), Token));
        Assert.False(await Task.Run(() => empty.HasSameSource(afterSeek), Token));
        Assert.False(empty.IsCurrent(source));
    }

    [Fact]
    public void AlreadyCancelledInteractiveRequestDoesNotStepOrFaultItsWarmOwner()
    {
        var factory = new Factory();
        using var source = Create(factory, out var id);
        var request = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion>());
        var worker = new Factory();
        using var owner = request.CreateSession(worker.Provider(), Token);
        var before = owner.Frame; var world = Assert.Single(worker.Worlds);
        int initializations = world.Initializations;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => owner.PreviewInteractiveBones(id, Bones(15), 1d / 60, cancellation.Token));
        Assert.Same(before, owner.Frame); Assert.False(owner.IsFaulted);
        Assert.Equal(initializations, world.Initializations); Assert.Equal(120, world.Steps);
        owner.PreviewInteractiveBones(id, Bones(15), 1d / 60, Token);
        Assert.Equal(122, world.Steps);
    }

    private static Dictionary<int, BoneLocalTransform> Bones(float x) => new() { [0] = new(new(x, 0, 0), Quaternion.Identity) };

    private static void AssertPose(SceneProjectRenderSnapshot snapshot, Guid id, float x)
    {
        Assert.Equal(7, snapshot.SourceFrame);
        Assert.Equal(new Vector3(x, 0, 0), snapshot.SceneInputs[id].BoneWorldMatrices[0].Translation);
        Assert.Equal(new Vector3(x, 0, 0), snapshot.Scene.Surface.Snapshot.Vertices[0].Position);
        Assert.Equal(snapshot.Scene.Surface.Snapshot.Vertices.ToArray(), snapshot.SceneInputs[id].SourceGeometry!.Vertices.ToArray());
        Assert.Equal(snapshot.Scene.Surface.Snapshot.Vertices[0].Position, snapshot.Scene.Outline!.Vertices[0].Position);
    }

    private static SceneProjectSession Create(Factory factory, out Guid id, string graph = "independent")
    {
        var model = graph == "soft" ? PmxSoftBodyTests.MaterialFixture() : SharedPhysicsFixture.Model(PmxRigidBodyOperation.Static);
        var layout = new SceneCompositionEditor();
        var actor = layout.Add("actor", Path.GetFullPath("interactive-pose-fixture.pmx"), ModelRole.Character, SharedPhysicsFixture.Rest(model));
        id = actor.Id;
        var motions = new List<SceneActorMotion> { new(id, model, Motion(30), PhysicsEnabled: graph != "soft") };
        SceneOutsideParentBinding[] parents = [];
        if (graph is "multiple" or "parents")
        {
            var second = layout.Add("second", Path.GetFullPath("interactive-pose-second.pmx"), ModelRole.Character, SharedPhysicsFixture.Rest(model));
            motions.Add(new(second.Id, model, Motion(30)));
            if (graph == "parents") parents = [new(id, -1, second.Id, 0, Matrix4x4.Identity)];
        }
        return new(layout, new(), motions, factory.Provider(),
            new() { Physics = new() { SharedWorld = graph == "shared", GroundEnabled = false } }, outsideParents: parents,
            initialFrame: 7, cancellationToken: Token, pmmControls: graph == "pmm" ? Controls(id, model) : null);
    }

    private static VmdMotion Motion(float x) => new()
    {
        BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 30, Translation = new(x, 0, 0) }]
    };

    private static ScenePmmControlState Controls(Guid id, PmxModel model)
    {
        PmmString S(string value) => new(0, value, []);
        byte[] linear = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 4).SelectMany(value => value).ToArray();
        var document = new PmmDocument
        {
            AnsiCodePage = 932, PanelStates = new byte[7], OutputWidth = 640, OutputHeight = 480, CameraFov = 30,
            Models = [new() { Index = 0, Name = S("actor"), Path = S("actor.pmx"), BoneNames = [S("root")],
                SelectedMorphIndices = [-1, -1, -1, -1], Visible = true, LastFrame = 30,
                InitialBoneKeyframes = [new() { ObjectIndex = 0, Rotation = Quaternion.Identity, Interpolation = linear }],
                InitialModelKeyframe = new() { Visible = true },
                BoneStates = [new(0, Vector3.Zero, Quaternion.Identity, false, false, 0)] }],
            Camera = new() { InitialKeyframe = new() { Interpolation = new byte[24], Fov = 30, ParentModelIndex = -1, ParentBoneIndex = -1 } },
            Settings = new() { PhysicsSimulationMode = 1, EdgeColorRgb = [0, 0, 0], UnknownMatrix = new byte[64], SelectionTrailerFlag = 0 },
            Gravity = new() { InitialKeyframe = new() { Acceleration = 1, Direction = -Vector3.UnitY } }
        };
        return ScenePmmControlState.Create(document, [new(id, 0, model)]);
    }

    private sealed class Factory
    {
        internal readonly List<TrackingSharedBackend> Worlds = [];
        internal ProjectSettingsBackendProvider Provider() => new((_, _) => Create(), _ => Create(), poseInitializationSubsteps: 120);
        private TrackingSharedBackend Create()
        {
            var world = new TrackingSharedBackend(); Worlds.Add(world); return world;
        }
    }
}
