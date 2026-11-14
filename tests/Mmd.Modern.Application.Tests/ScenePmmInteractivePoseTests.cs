using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class ScenePmmInteractivePoseTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FixedClockPmmDraftsKeepWorldsControlsAndCrossParentChildrenCoherent(bool shared)
    {
        var live = new Factory();
        using var source = Create(live, shared, out var ids);
        var sourceFrame = source.Frame; var sourceDocument = PmmWriter.ToBytes(source.PmmControls!.ExportDocument());
        var request = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion>());
        Assert.True(request.SupportsInteractiveBones);
        var worker = new Factory();
        using var owner = request.CreateSession(worker.Provider(), Token);
        var requests = ids.Select(id => new SceneObjectRenderRequest(id, [0])).ToArray();
        var before = owner.CaptureRenderSnapshot(requests);
        var worlds = worker.Worlds.ToArray(); var initializations = worlds.Select(w => w.Initializations).ToArray();
        var steps = worlds.Select(w => w.Steps).ToArray();
        owner.PreviewInteractiveBones(ids[0], Bones(15), 1d / 60, Token);
        var first = owner.CaptureRenderSnapshot(requests);
        owner.PreviewInteractiveBones(ids[0], Bones(23), 1d / 60, Token);
        var second = owner.CaptureRenderSnapshot(requests);
        Assert.Equal(new Vector3(15, 0, 0), first.SceneInputs[ids[0]].BoneWorldMatrices[0].Translation);
        Assert.Equal(new Vector3(15, 0, 0), first.SceneInputs[ids[1]].BoneWorldMatrices[0].Translation);
        Assert.Equal(new Vector3(23, 0, 0), second.SceneInputs[ids[0]].BoneWorldMatrices[0].Translation);
        Assert.Equal(new Vector3(23, 0, 0), second.SceneInputs[ids[1]].BoneWorldMatrices[0].Translation);
        Assert.Equal(before.SceneInputs[ids[2]].BoneWorldMatrices[0], second.SceneInputs[ids[2]].BoneWorldMatrices[0]);
        Assert.Equal(initializations, worlds.Select(w => w.Initializations)); Assert.Equal(worlds, worker.Worlds);
        if (shared) Assert.Equal(steps[0] + 4, worlds[0].Steps);
        else
        {
            Assert.Equal(steps[0] + 4, worlds[0].Steps);
            Assert.Equal(steps[1] + 4, worlds[1].Steps);
            Assert.Equal(steps[2], worlds[2].Steps);
        }
        // PMM frame7 disables the edited root's dynamic rigid: the active control is still applied.
        Assert.True(worlds[0].Bodies[0].Kinematic);
        Assert.Equal(7, owner.CurrentFrame); Assert.All(owner.Actors, actor => Assert.Equal(1, actor.LocalFrame));
        Assert.Equal(before.Camera, second.Camera); Assert.Equal(before.CameraParentTransform, second.CameraParentTransform);
        Assert.Equal(sourceDocument, PmmWriter.ToBytes(owner.PmmControls!.ExportDocument()));
        Assert.Same(sourceFrame, source.Frame); Assert.Equal(sourceDocument, PmmWriter.ToBytes(source.PmmControls.ExportDocument()));
        Assert.True(request.IsCurrent(source)); Assert.False(source.CanUndo); Assert.False(owner.CanUndo);
        Assert.All(live.Worlds, world => Assert.Equal(0, world.Steps));
    }

    [Fact]
    public void AlreadyCancelledPmmGestureRetainsTheLastPublishedFrameAndCanContinue()
    {
        using var source = Create(new Factory(), false, out var ids);
        var worker = new Factory();
        using var owner = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion>()).CreateSession(worker.Provider(), Token);
        var before = owner.Frame; var steps = worker.Worlds.Select(w => w.Steps).ToArray();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => owner.PreviewInteractiveBones(ids[0], Bones(15), 1d / 60, cancellation.Token));
        Assert.Same(before, owner.Frame); Assert.False(owner.IsFaulted); Assert.Equal(steps, worker.Worlds.Select(w => w.Steps));
        owner.PreviewInteractiveBones(ids[0], Bones(15), 1d / 60, Token);
        Assert.NotSame(before, owner.Frame); Assert.False(owner.IsFaulted);
    }

    [Fact]
    public void ClearingTheOnlyDraftRestoresAuthoredLocalBonesWithoutChangingMotionOrAllocatingSolvers()
    {
        using var source = Create(new Factory(), false, out var ids);
        var worker = new Factory();
        using var owner = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion>()).CreateSession(worker.Provider(), Token);
        var motions = owner.CaptureAuthoredContent().Motions.Select(m => m.Motion).ToArray();
        int count = worker.Worlds.Count;
        owner.PreviewInteractiveBones(ids[0], Bones(15), 1d / 60, Token);
        owner.PreviewInteractiveBones(ids[0], new Dictionary<int, BoneLocalTransform>(), 1d / 60, Token);
        var restored = owner.CaptureRenderSnapshot(ids.Take(2).Select(id => new SceneObjectRenderRequest(id, [0])));
        Assert.Equal(Vector3.Zero, restored.SceneInputs[ids[0]].BoneWorldMatrices[0].Translation);
        Assert.Equal(Vector3.Zero, restored.SceneInputs[ids[1]].BoneWorldMatrices[0].Translation);
        Assert.Equal(count, worker.Worlds.Count);
        Assert.Equal(motions, owner.CaptureAuthoredContent().Motions.Select(m => m.Motion));
        Assert.False(owner.CanUndo); Assert.False(owner.CanRedo);
    }

    private static Dictionary<int, BoneLocalTransform> Bones(float x) => new() { [0] = new(new(x, 0, 0), Quaternion.Identity) };
    private static SceneProjectSession Create(Factory factory, bool shared, out Guid[] ids)
    {
        var model = SharedPhysicsFixture.Model(PmxRigidBodyOperation.Dynamic);
        var layout = new SceneCompositionEditor(); var rest = SharedPhysicsFixture.Rest(model);
        ids = Enumerable.Range(0, 3).Select(i => layout.Add("actor" + i, Path.GetFullPath("pmm-interactive-" + i + ".pmx"), ModelRole.Character, rest).Id).ToArray();
        var motions = ids.Select(id => new SceneActorMotion(id, model, SharedPhysicsFixture.Motion(1))).ToArray();
        return new(layout, new(), motions, factory.Provider(), new() { Physics = new() { SharedWorld = shared, GroundEnabled = false } },
            initialFrame: 7, cancellationToken: Token, pmmControls: Controls(ids, model));
    }
    private static ScenePmmControlState Controls(Guid[] ids, PmxModel model)
    {
        PmmString S(string value) => new(0, value, []);
        byte[] linear = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 4).SelectMany(value => value).ToArray();
        var models = ids.Select((_, i) => new PmmModel
        {
            Index = (byte)i, Name = S("actor" + i), Path = S("actor" + i + ".pmx"), BoneNames = [S("root")],
            SelectedMorphIndices = [-1, -1, -1, -1], Visible = true, LastFrame = 1,
            OutsideParentSubjectBoneIndices = i == 1 ? [0] : [],
            OutsideParentStates = i == 1 ? [new(0, 0, int.MaxValue, new(0, 0, 0))] : [],
            InitialBoneKeyframes = [new() { ObjectIndex = 0, Rotation = Quaternion.Identity, Interpolation = linear, PhysicsDisabled = i == 0 }],
            InitialModelKeyframe = new() { Visible = true, OutsideParents = i == 1 ? [new(0, 0, 0)] : [] },
            BoneStates = [new(0, Vector3.Zero, Quaternion.Identity, false, false, 0)]
        }).ToArray();
        var document = new PmmDocument
        {
            AnsiCodePage = 932, PanelStates = new byte[7], OutputWidth = 640, OutputHeight = 480, CameraFov = 30, Models = models,
            Camera = new() { InitialKeyframe = new() { Interpolation = new byte[24], Fov = 30, ParentModelIndex = -1, ParentBoneIndex = -1 } },
            Settings = new() { PhysicsSimulationMode = 1, EdgeColorRgb = [0, 0, 0], UnknownMatrix = new byte[64], SelectionTrailerFlag = 0 },
            Gravity = new() { InitialKeyframe = new() { Acceleration = 1, Direction = -Vector3.UnitY } }
        };
        return ScenePmmControlState.Create(document, ids.Select((id, i) => new ScenePmmActorSource(id, (byte)i, model)));
    }
    private sealed class Factory
    {
        internal readonly List<TrackingSharedBackend> Worlds = [];
        internal ProjectSettingsBackendProvider Provider() => new((_, _) => Create(), _ => Create(), poseInitializationSubsteps: 0);
        private TrackingSharedBackend Create() { var world = new TrackingSharedBackend(); Worlds.Add(world); return world; }
    }
}
