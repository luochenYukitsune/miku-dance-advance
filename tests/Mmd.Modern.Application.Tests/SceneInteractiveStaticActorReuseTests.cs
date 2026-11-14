using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneInteractiveStaticActorReuseTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(true, false, 2)]
    [InlineData(true, true, 0)]
    [InlineData(true, true, 1)]
    [InlineData(true, true, 2)]
    [InlineData(false, true, 1)]
    [InlineData(false, true, 2)]
    public void StoppedUnattachedRoomRetainsLocalStorageWhilePhysicsAndAttachedActorsKeepTheirBoundaries(
        bool shared, bool pmm, int steps)
    {
        var small = SharedPhysicsFixture.Model(PmxRigidBodyOperation.Static);
        var room = Room(20000); var layout = new SceneCompositionEditor();
        var models = new[] { small, room, small, small, small };
        var actors = models.Select((model, index) => layout.Add("actor" + index,
            Path.GetFullPath("interactive-static-reuse-" + index + ".pmx"), ModelRole.Character, SharedPhysicsFixture.Rest(model))).ToArray();
        var motions = actors.Select((actor, index) => new SceneActorMotion(actor.Id, models[index],
            SharedPhysicsFixture.Motion(1), PhysicsEnabled: index is 0 or 3)).ToArray();
        SceneOutsideParentBinding[] actualParents =
            [new(actors[2].Id, 0, actors[0].Id, 0, Matrix4x4.Identity),
             new(actors[3].Id, 0, actors[1].Id, 0, Matrix4x4.Identity)];
        // The oracle's room has a constant identity attachment. This is numerically identical to
        // an unattached room, but deliberately retains the complete frozen-attachment route.
        var oracleParents = actualParents.Append(new SceneOutsideParentBinding(actors[1].Id, 0,
            actors[4].Id, 0, Matrix4x4.Identity)).ToArray();
        var controls = pmm ? Controls(actors, models) : null;
        var settings = new ProjectSettings { Physics = new() { SharedWorld = shared, GroundEnabled = false } };
        using var source = new SceneProjectSession(layout, new(), motions, new Factory().Provider(), settings,
            outsideParents: actualParents, initialFrame: 7, pmmControls: controls, cancellationToken: Token);
        using var oracleSource = new SceneProjectSession(layout, new(), motions, new Factory().Provider(), settings,
            outsideParents: oracleParents, initialFrame: 7, pmmControls: controls, cancellationToken: Token);
        var sourceFrame = source.Frame;
        var factory = new Factory(); var oracleFactory = new Factory();
        var request = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion>());
        using var actual = request.CreateSession(factory.Provider(), Token);
        using var expected = oracleSource.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion>()).CreateSession(oracleFactory.Provider(), Token);
        var inputs = actors.Select(actor => new SceneObjectRenderRequest(actor.Id, [0], includeSourceGeometry: true)).ToArray();
        var before = actual.CaptureRenderSnapshot(inputs);
        var roomStorage = before.SceneInputs[actors[1].Id].SourceGeometry!;
        var roomBytes = Bytes(roomStorage.Vertices);
        var sceneBytes = Bytes(before.Scene.Surface.Snapshot.Vertices);
        var rootBones = before.SceneInputs[actors[1].Id].BoneWorldMatrices.ToArray();
        int initialSteps = factory.Worlds.Sum(world => world.Steps);

        foreach (float target in new[] { 4f, 9f })
        {
            var oldAttachedStorage = actual.CaptureRenderSnapshot(inputs).SceneInputs[actors[2].Id].SourceGeometry;
            actual.PreviewInteractiveBones(actors[0].Id, Bones(target), steps * CharacterPhysicsSession.FixedStepSeconds, Token);
            if (steps == 0) expected.PreviewInteractiveBones(actors[0].Id, Bones(target), 0, Token);
            else for (int step = 0; step < steps; step++)
                expected.PreviewInteractiveBones(actors[0].Id, Bones(target), CharacterPhysicsSession.FixedStepSeconds, Token);
            var result = actual.CaptureRenderSnapshot(inputs);
            Assert.Same(roomStorage, result.SceneInputs[actors[1].Id].SourceGeometry);
            Assert.Equal(roomBytes, Bytes(result.SceneInputs[actors[1].Id].SourceGeometry!.Vertices));
            Assert.Equal(rootBones, result.SceneInputs[actors[1].Id].BoneWorldMatrices);
            Assert.NotSame(oldAttachedStorage, result.SceneInputs[actors[2].Id].SourceGeometry);
            Assert.Equal(new Vector3(target, 0, 0), result.SceneInputs[actors[2].Id].BoneWorldMatrices[0].Translation);
            AssertSameOutput(expected.CaptureRenderSnapshot(inputs), result);
        }
        Assert.Equal(initialSteps + 2 * steps, factory.Worlds.Sum(world => world.Steps));
        Assert.Equal(roomBytes, Bytes(roomStorage.Vertices));
        Assert.Equal(sceneBytes, Bytes(before.Scene.Surface.Snapshot.Vertices));

        // Selecting the formerly retained room must publish its raw local draft. Its physical
        // child must read the new room bones from the same ordered boundary.
        actual.PreviewInteractiveBones(actors[1].Id, Bones(11), CharacterPhysicsSession.FixedStepSeconds, Token);
        expected.PreviewInteractiveBones(actors[1].Id, Bones(11), CharacterPhysicsSession.FixedStepSeconds, Token);
        var editedRoom = actual.CaptureRenderSnapshot(inputs);
        Assert.NotSame(roomStorage, editedRoom.SceneInputs[actors[1].Id].SourceGeometry);
        Assert.Equal(new Vector3(11, 0, 0), editedRoom.SceneInputs[actors[1].Id].BoneWorldMatrices[0].Translation);
        Assert.Equal(new Vector3(11, 0, 0), editedRoom.SceneInputs[actors[3].Id].BoneWorldMatrices[0].Translation);
        AssertSameOutput(expected.CaptureRenderSnapshot(inputs), editedRoom);

        // Switching selection back preserves the room's completed edited pose, rather than
        // recovering authored bones or accidentally leaving it in a bone-only cache state.
        actual.PreviewInteractiveBones(actors[0].Id, Bones(13), CharacterPhysicsSession.FixedStepSeconds, Token);
        expected.PreviewInteractiveBones(actors[0].Id, Bones(13), CharacterPhysicsSession.FixedStepSeconds, Token);
        var retainedEditedRoom = actual.CaptureRenderSnapshot(inputs);
        Assert.Same(editedRoom.SceneInputs[actors[1].Id].SourceGeometry, retainedEditedRoom.SceneInputs[actors[1].Id].SourceGeometry);
        Assert.Equal(new Vector3(11, 0, 0), retainedEditedRoom.SceneInputs[actors[1].Id].BoneWorldMatrices[0].Translation);
        AssertSameOutput(expected.CaptureRenderSnapshot(inputs), retainedEditedRoom);
        Assert.Same(sourceFrame, source.Frame); Assert.True(request.IsCurrent(source));
        Assert.False(actual.CanUndo); Assert.False(actual.CanRedo);
        Assert.Equal(7, actual.CurrentFrame);
    }

    private static Dictionary<int, BoneLocalTransform> Bones(float x) => new()
        { [0] = new(new(x, 0, 0), Quaternion.Identity) };

    private static void AssertSameOutput(SceneProjectRenderSnapshot expected, SceneProjectRenderSnapshot actual)
    {
        Assert.Equal(Bytes(expected.Scene.Surface.Snapshot.Vertices), Bytes(actual.Scene.Surface.Snapshot.Vertices));
        var a = expected.Scene.Outline!.Vertices.ToArray(); var b = actual.Scene.Outline!.Vertices.ToArray();
        Assert.Equal(MemoryMarshal.AsBytes(a.AsSpan()).ToArray(), MemoryMarshal.AsBytes(b.AsSpan()).ToArray());
        Assert.Equal(expected.Scene.Materials, actual.Scene.Materials);
        foreach (var (id, entry) in expected.SceneInputs)
        {
            var expectedBones = entry.BoneWorldMatrices.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
            var actualBones = actual.SceneInputs[id].BoneWorldMatrices.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
            Assert.Equal(MemoryMarshal.AsBytes(expectedBones.AsSpan()).ToArray(), MemoryMarshal.AsBytes(actualBones.AsSpan()).ToArray());
            Assert.Equal(Bytes(entry.SourceGeometry!.Vertices), Bytes(actual.SceneInputs[id].SourceGeometry!.Vertices));
        }
    }

    private static byte[] Bytes(ReadOnlySpan<SceneVertex> values) => MemoryMarshal.AsBytes(values).ToArray();

    private static PmxModel Room(int vertices)
    {
        var basic = SharedPhysicsFixture.Model(physics: false);
        return new()
        {
            Header = basic.Header, Name = "room", EnglishName = "", Comment = "", EnglishComment = "",
            Bones = basic.Bones, Morphs = basic.Morphs, Materials = basic.Materials, Textures = basic.Textures,
            Faces = basic.Faces, DisplayFrames = basic.DisplayFrames, RigidBodies = basic.RigidBodies,
            Joints = basic.Joints, SoftBodies = basic.SoftBodies,
            Vertices = Enumerable.Range(0, vertices).Select(index => basic.Vertices[index % basic.Vertices.Count]).ToArray()
        };
    }

    private static ScenePmmControlState Controls(EditableSceneObject[] actors, PmxModel[] models)
    {
        PmmString S(string value) => new(0, value, []);
        byte[] linear = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 4).SelectMany(value => value).ToArray();
        var tracks = actors.Select((actor, index) => new PmmModel
        {
            Index = (byte)index, Name = S(actor.Name), Path = S("actor" + index + ".pmx"), BoneNames = [S("root")],
            SelectedMorphIndices = [-1, -1, -1, -1], Visible = true, LastFrame = 1,
            InitialBoneKeyframes = [new() { ObjectIndex = 0, Rotation = Quaternion.Identity, Interpolation = linear }],
            InitialModelKeyframe = new() { Visible = true },
            BoneStates = [new(0, Vector3.Zero, Quaternion.Identity, false, false, 0)]
        }).ToArray();
        var document = new PmmDocument
        {
            AnsiCodePage = 932, PanelStates = new byte[7], OutputWidth = 640, OutputHeight = 480, CameraFov = 30, Models = tracks,
            Camera = new() { InitialKeyframe = new() { Interpolation = new byte[24], Fov = 30, ParentModelIndex = -1, ParentBoneIndex = -1 } },
            Settings = new() { PhysicsSimulationMode = 1, EdgeColorRgb = [0, 0, 0], UnknownMatrix = new byte[64], SelectionTrailerFlag = 0 },
            Gravity = new() { InitialKeyframe = new() { Acceleration = 1, Direction = -Vector3.UnitY } }
        };
        return ScenePmmControlState.Create(document, actors.Select((actor, index) => new ScenePmmActorSource(actor.Id, (byte)index, models[index])));
    }

    private sealed class Factory
    {
        internal readonly List<TrackingSharedBackend> Worlds = [];
        internal ProjectSettingsBackendProvider Provider() => new((_, _) => Create(), _ => Create(), poseInitializationSubsteps: 0);
        private TrackingSharedBackend Create() { var world = new TrackingSharedBackend(); Worlds.Add(world); return world; }
    }
}
