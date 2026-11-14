using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Animation;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneInteractiveBoundaryGraphTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReversedParentChainRebuildsAfterPmmSwitchAndSeekAndMatchesEveryIndividualBoundary(bool shared)
    {
        var model = SharedPhysicsFixture.Model(); var layout = new SceneCompositionEditor();
        // Source order deliberately puts the physical child before its nonphysical parent and host.
        var actors = new[] { "child", "middle", "host", "other-host" }.Select(name => layout.Add(name,
            Path.GetFullPath("interactive-boundary-graph-" + name + ".pmx"), ModelRole.Character,
            SharedPhysicsFixture.Rest(model))).ToArray();
        var controls = Controls(actors, model);
        var motions = actors.Select((actor, index) => new SceneActorMotion(actor.Id, model,
            SharedPhysicsFixture.Motion(1, index == 3 ? new Vector3(40, 0, 0) : Vector3.Zero),
            PhysicsEnabled: index != 1)).ToArray();
        using var source = new SceneProjectSession(layout, new(), motions, new Factory().Provider(),
            new() { Physics = new() { SharedWorld = shared, GroundEnabled = false } },
            initialFrame: 7, pmmControls: controls, cancellationToken: Token);
        var sourceFrame = source.Frame;
        var authored = PmmWriter.ToBytes(source.PmmControls!.ExportDocument());
        var request = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion>());
        var actualFactory = new Factory(); var expectedFactory = new Factory();
        using var actual = request.CreateSession(actualFactory.Provider(), Token);
        using var expected = request.CreateSession(expectedFactory.Provider(), Token);
        var inputs = actors.Select(actor => new SceneObjectRenderRequest(actor.Id, [0], includeSourceGeometry: true)).ToArray();
        var retained = actual.CaptureRenderSnapshot(inputs);
        var retainedVertices = Bytes(retained.Scene.Surface.Snapshot.Vertices);
        var retainedEdges = Bytes<OriginalOutlineVertex>(retained.Scene.Outline!.Vertices.ToArray());
        var retainedLocal = actors.Select(actor => Bytes(retained.SceneInputs[actor.Id].SourceGeometry!.Vertices)).ToArray();
        var retainedBones = actors.Select(actor => BoneBytes(retained.SceneInputs[actor.Id])).ToArray();

        foreach (var gesture in new[] { (Frame: 7, Actor: 2, X: 8f, MiddleX: 8f),
            (Frame: 12, Actor: 2, X: 11f, MiddleX: 40f),
            (Frame: 12, Actor: 3, X: 19f, MiddleX: 19f),
            (Frame: 7, Actor: 2, X: 23f, MiddleX: 23f) })
        {
            if (actual.CurrentFrame != gesture.Frame)
            { actual.Seek(gesture.Frame, Token); expected.Seek(gesture.Frame, Token); }
            var draft = new Dictionary<int, BoneLocalTransform> { [0] = new(new(gesture.X, 0, 0), Quaternion.Identity) };
            // The reference rebuilds its request graph and publishes a complete skin at every tick.
            for (int step = 0; step < 8; step++)
                expected.PreviewInteractiveBones(actors[gesture.Actor].Id, draft, CharacterPhysicsSession.FixedStepSeconds, Token);
            actual.PreviewInteractiveBones(actors[gesture.Actor].Id, draft, 8 * CharacterPhysicsSession.FixedStepSeconds, Token);
            var result = actual.CaptureRenderSnapshot(inputs);
            AssertSameOutput(expected.CaptureRenderSnapshot(inputs), result, actors);
            Assert.Equal(new Vector3(gesture.MiddleX, 0, 0), result.SceneInputs[actors[1].Id].BoneWorldMatrices[0].Translation);
            Assert.Equal(expectedFactory.Worlds.Select(world => world.Steps), actualFactory.Worlds.Select(world => world.Steps));
            for (int world = 0; world < actualFactory.Worlds.Count; world++)
            {
                var a = expectedFactory.Worlds[world]; var b = actualFactory.Worlds[world];
                Assert.Equal(a.Bodies.Select(body => body.Kinematic), b.Bodies.Select(body => body.Kinematic));
                Assert.Equal(Bytes<Matrix4x4>(a.Bodies.Select(body => body.Transform).ToArray()),
                    Bytes<Matrix4x4>(b.Bodies.Select(body => body.Transform).ToArray()));
                Assert.Equal(Bytes<Vector3>(a.Bodies.Select(body => body.Velocity).ToArray()),
                    Bytes<Vector3>(b.Bodies.Select(body => body.Velocity).ToArray()));
            }
        }

        // Borrowed parent palettes and every earlier published scene remain immutable across plans.
        Assert.Equal(retainedVertices, Bytes(retained.Scene.Surface.Snapshot.Vertices));
        Assert.Equal(retainedEdges, Bytes<OriginalOutlineVertex>(retained.Scene.Outline!.Vertices.ToArray()));
        for (int index = 0; index < actors.Length; index++)
        {
            Assert.Equal(retainedLocal[index], Bytes(retained.SceneInputs[actors[index].Id].SourceGeometry!.Vertices));
            Assert.Equal(retainedBones[index], BoneBytes(retained.SceneInputs[actors[index].Id]));
        }
        Assert.Equal(authored, PmmWriter.ToBytes(actual.PmmControls!.ExportDocument()));
        Assert.Same(sourceFrame, source.Frame); Assert.True(request.IsCurrent(source));
        Assert.False(actual.CanUndo); Assert.False(actual.CanRedo);
    }

    private static void AssertSameOutput(SceneProjectRenderSnapshot expected, SceneProjectRenderSnapshot actual, EditableSceneObject[] actors)
    {
        Assert.Equal(expected.SourceFrame, actual.SourceFrame);
        Assert.Equal(Bytes(expected.Scene.Surface.Snapshot.Vertices), Bytes(actual.Scene.Surface.Snapshot.Vertices));
        Assert.Equal(Bytes<OriginalOutlineVertex>(expected.Scene.Outline!.Vertices.ToArray()),
            Bytes<OriginalOutlineVertex>(actual.Scene.Outline!.Vertices.ToArray()));
        Assert.Equal(expected.Scene.Materials, actual.Scene.Materials);
        Assert.Equal(expected.Scene.Surface.Materials, actual.Scene.Surface.Materials);
        foreach (var actor in actors)
        {
            Assert.Equal(BoneBytes(expected.SceneInputs[actor.Id]), BoneBytes(actual.SceneInputs[actor.Id]));
            Assert.Equal(Bytes(expected.SceneInputs[actor.Id].SourceGeometry!.Vertices), Bytes(actual.SceneInputs[actor.Id].SourceGeometry!.Vertices));
        }
    }

    private static byte[] BoneBytes(SceneObjectRenderInputs inputs)
        => Bytes<Matrix4x4>(inputs.BoneWorldMatrices.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray());
    private static byte[] Bytes<T>(ReadOnlySpan<T> values) where T : unmanaged => MemoryMarshal.AsBytes(values).ToArray();

    private static ScenePmmControlState Controls(EditableSceneObject[] actors, PmxModel model)
    {
        PmmString S(string value) => new(0, value, []);
        byte[] linear = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 4).SelectMany(value => value).ToArray();
        var models = actors.Select((actor, index) => new PmmModel
        {
            Index = (byte)index, Name = S(actor.Name), Path = S(actor.Name + ".pmx"), BoneNames = [S("root")],
            SelectedMorphIndices = [-1, -1, -1, -1], Visible = true, LastFrame = 1,
            OutsideParentSubjectBoneIndices = index < 2 ? [0] : [],
            OutsideParentStates = index < 2 ? [new(0, 0, int.MaxValue, new(0, index + 1, 0))] : [],
            InitialBoneKeyframes = [new() { ObjectIndex = 0, Rotation = Quaternion.Identity,
                Interpolation = linear, PhysicsDisabled = index >= 2 }],
            InitialModelKeyframe = new() { Visible = true, OutsideParents = index < 2 ? [new(0, index + 1, 0)] : [] },
            BoneStates = [new(0, Vector3.Zero, Quaternion.Identity, false, index >= 2, 0)]
        }).ToArray();
        var document = new PmmDocument
        {
            AnsiCodePage = 932, PanelStates = new byte[7], OutputWidth = 640, OutputHeight = 480, CameraFov = 30, Models = models,
            Camera = new() { InitialKeyframe = new() { Interpolation = new byte[24], Fov = 30, ParentModelIndex = -1, ParentBoneIndex = -1 } },
            Settings = new() { PhysicsSimulationMode = 1, EdgeColorRgb = [0, 0, 0], UnknownMatrix = new byte[64], SelectionTrailerFlag = 0 },
            Gravity = new() { InitialKeyframe = new() { Acceleration = 1, Direction = -Vector3.UnitY } }
        };
        var editor = new PmmTrackEditor(document);
        editor.Apply(editor.PrepareRegister([new(new(PmmTrackKind.Model, 1, "", 10),
            new PmmModelKeyframe { Visible = true, OutsideParents = [new(0, 3, 0)] })]));
        return ScenePmmControlState.Create(editor.ExportDocument(), actors.Select((actor, index) => new ScenePmmActorSource(actor.Id, (byte)index, model)));
    }

    private sealed class Factory
    {
        internal readonly List<TrackingSharedBackend> Worlds = [];
        internal ProjectSettingsBackendProvider Provider() => new((_, _) => Create(), _ => Create(), poseInitializationSubsteps: 0);
        private TrackingSharedBackend Create() { var world = new TrackingSharedBackend(); Worlds.Add(world); return world; }
    }
}
