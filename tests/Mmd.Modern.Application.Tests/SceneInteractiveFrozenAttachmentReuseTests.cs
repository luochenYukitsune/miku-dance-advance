using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneInteractiveFrozenAttachmentReuseTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CompleteUnchangedAttachmentsRetainTheirGeometryAndMatchForcedFullSkinsAndEveryPhysicsBoundary(bool shared, bool pmm)
    {
        var model = Model(); var layout = new SceneCompositionEditor();
        var actors = Enumerable.Range(0, 4).Select(index => layout.Add("actor" + index,
            Path.GetFullPath("frozen-attachment-reuse-" + index + ".pmx"), ModelRole.Character,
            SharedPhysicsFixture.Rest(model))).ToArray();
        var controls = pmm ? Controls(actors, model) : null;
        SceneOutsideParentBinding[] parents = pmm ? [] :
            [new(actors[1].Id, 0, actors[0].Id, 1, Matrix4x4.Identity),
             new(actors[2].Id, 0, actors[1].Id, 0, Matrix4x4.Identity),
             new(actors[3].Id, 0, actors[2].Id, 0, Matrix4x4.Identity)];
        using var source = new SceneProjectSession(layout, new(), actors.Select((actor, index) =>
                new SceneActorMotion(actor.Id, model, SharedPhysicsFixture.Motion(1), PhysicsEnabled: index is 0 or 3)),
            new Factory().Provider(), new() { Physics = new() { SharedWorld = shared, GroundEnabled = false } },
            initialFrame: 7, outsideParents: parents, pmmControls: controls, cancellationToken: Token);
        var request = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion>());
        var expectedFactory = new Factory(); var actualFactory = new Factory();
        using var expected = request.CreateSession(expectedFactory.Provider(), Token);
        using var actual = request.CreateSession(actualFactory.Provider(), Token);
        var inputs = actors.Select(actor => new SceneObjectRenderRequest(actor.Id, [0, 1], includeSourceGeometry: true)).ToArray();
        var previous = actual.CaptureRenderSnapshot(inputs);
        var retainedVertices = Bytes(previous.Scene.Surface.Snapshot.Vertices);
        var retainedBones = actors.Select(actor => BoneBytes(previous, actor.Id)).ToArray();

        // Editing root leaves the separate attachment bone unchanged. Both frozen descendants
        // must retain their complete geometry while the physical descendant still steps.
        Compare(Bones(0, 4), 1, retain: true);
        Compare(Bones(0, 9), 8, retain: true);
        // Moving the actual parent invalidates the complete skin. Deferred intermediate bone
        // completions must never be accepted as a complete display at the final boundary.
        Compare(Bones(1, 3), 8, retain: false);
        Compare(Bones(1, 3), 2, retain: true);
        Compare(new Dictionary<int, BoneLocalTransform>(), 1, retain: false);
        Compare(new Dictionary<int, BoneLocalTransform>(), 0, retain: true);
        for (int batch = 0; batch < 60; batch++) Compare(new Dictionary<int, BoneLocalTransform>(), 2, retain: true);

        if (!pmm)
        {
            // Graph replacement and option changes construct fresh matching display state;
            // compare against full evaluation after each change rather than testing a guard flag.
            foreach (var owner in new[] { expected, actual })
                Animation(owner).SetActorOptions(actors[1].Id, ik: false, morphs: false, physics: false, cancellationToken: Token);
            Compare(Bones(0, 2), 1, retain: true);
            foreach (var owner in new[] { expected, actual }) Animation(owner).RemoveOutsideParent(actors[1].Id, 0, Token);
            Compare(Bones(0, 2), 1, retain: true);
            foreach (var owner in new[] { expected, actual })
                Animation(owner).SetOutsideParent(actors[1].Id, 0, actors[0].Id, 1, keepWorld: false,
                    offset: Matrix4x4.CreateTranslation(0, 2, 0), token: Token);
            Compare(Bones(0, 2), 1, retain: true);
            foreach (var owner in new[] { expected, actual })
                Animation(owner).SetActorOptions(actors[1].Id, ik: true, morphs: true, physics: true, cancellationToken: Token);
            Compare(Bones(0, 2), 1, retain: false);
        }
        Assert.Equal(retainedVertices, Bytes(previous.Scene.Surface.Snapshot.Vertices));
        for (int index = 0; index < actors.Length; index++) Assert.Equal(retainedBones[index], BoneBytes(previous, actors[index].Id));
        Assert.True(request.IsCurrent(source)); Assert.False(actual.CanUndo); Assert.False(actual.CanRedo);

        void Compare(IReadOnlyDictionary<int, BoneLocalTransform> bones, int steps, bool retain)
        {
            var before = actual.CaptureRenderSnapshot(inputs);
            // A missing cached rendered sample is an existing legitimate full-evaluation state.
            // Keep its exact bone worlds/anchors and solver history, force the reference owner
            // through the old full-skin route, and compare public products rather than guard flags.
            foreach (int index in new[] { 1, 2 })
                typeof(CharacterAnimationController).GetField("sampledPose", Hidden)!.SetValue(Controller(expected, actors[index].Id), null);
            expected.PreviewInteractiveBones(actors[0].Id, bones, steps * CharacterPhysicsSession.FixedStepSeconds, Token);
            actual.PreviewInteractiveBones(actors[0].Id, bones, steps * CharacterPhysicsSession.FixedStepSeconds, Token);
            var reference = expected.CaptureRenderSnapshot(inputs); var result = actual.CaptureRenderSnapshot(inputs);
            Assert.Equal(Bytes(reference.Scene.Surface.Snapshot.Vertices), Bytes(result.Scene.Surface.Snapshot.Vertices));
            Assert.Equal(Bytes(reference.Scene.Surface.Snapshot.Indices), Bytes(result.Scene.Surface.Snapshot.Indices));
            Assert.Equal(Bytes<OriginalOutlineVertex>(reference.Scene.Outline!.Vertices.ToArray()), Bytes<OriginalOutlineVertex>(result.Scene.Outline!.Vertices.ToArray()));
            Assert.Equal(reference.Scene.Surface.Materials, result.Scene.Surface.Materials);
            Assert.Equal(reference.Scene.Materials, result.Scene.Materials);
            foreach (var actor in actors)
            {
                Assert.Equal(BoneBytes(reference, actor.Id), BoneBytes(result, actor.Id));
                Assert.Equal(Bytes(reference.SceneInputs[actor.Id].SourceGeometry!.Vertices), Bytes(result.SceneInputs[actor.Id].SourceGeometry!.Vertices));
            }
            if (retain)
                foreach (int index in new[] { 1, 2 }) Assert.Same(before.SceneInputs[actors[index].Id].SourceGeometry, result.SceneInputs[actors[index].Id].SourceGeometry);
            else Assert.NotSame(before.SceneInputs[actors[1].Id].SourceGeometry, result.SceneInputs[actors[1].Id].SourceGeometry);
            Assert.Equal(expectedFactory.Worlds.Count, actualFactory.Worlds.Count);
            for (int index = 0; index < actualFactory.Worlds.Count; index++)
            {
                Assert.Equal(expectedFactory.Worlds[index].History, actualFactory.Worlds[index].History);
                Assert.Equal(expectedFactory.Worlds[index].Inner.Steps, actualFactory.Worlds[index].Inner.Steps);
                Assert.Equal(expectedFactory.Worlds[index].Inner.Initializations, actualFactory.Worlds[index].Inner.Initializations);
                Assert.Equal(expectedFactory.Worlds[index].Targets, actualFactory.Worlds[index].Targets);
                Assert.Equal(expectedFactory.Worlds[index].Inner.Bodies.Select(body => (body.Transform, body.Velocity, body.Kinematic)),
                    actualFactory.Worlds[index].Inner.Bodies.Select(body => (body.Transform, body.Velocity, body.Kinematic)));
            }
        }
    }

    private static SceneAnimationSession Animation(SceneProjectSession owner) =>
        (SceneAnimationSession)typeof(SceneProjectSession).GetField("animation", Hidden)!.GetValue(owner)!;

    private static CharacterAnimationController Controller(SceneProjectSession owner, Guid id)
    {
        var entries = (Array)typeof(SceneAnimationSession).GetField("entries", Hidden)!.GetValue(Animation(owner))!;
        foreach (object entry in entries)
            if (((EditableSceneObject)entry.GetType().GetProperty("Object")!.GetValue(entry)!).Id == id)
                return (CharacterAnimationController)entry.GetType().GetProperty("Controller")!.GetValue(entry)!;
        throw new InvalidOperationException("Missing test actor.");
    }

    private static byte[] BoneBytes(SceneProjectRenderSnapshot snapshot, Guid id) => Bytes<Matrix4x4>(snapshot.SceneInputs[id]
        .BoneWorldMatrices.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray());
    private static byte[] Bytes<T>(ReadOnlySpan<T> values) where T : unmanaged => MemoryMarshal.AsBytes(values).ToArray();
    private static Dictionary<int, BoneLocalTransform> Bones(int bone, float x) => new() { [bone] = new(new(x, 0, 0), Quaternion.Identity) };

    private static PmxModel Model()
    {
        var basic = SharedPhysicsFixture.Model(PmxRigidBodyOperation.Static);
        return new()
        {
            Header = basic.Header, Name = basic.Name, EnglishName = "", Comment = "", EnglishComment = "",
            Textures = basic.Textures, Faces = basic.Faces, Materials = basic.Materials, Vertices = basic.Vertices,
            RigidBodies = basic.RigidBodies, DisplayFrames = [], Joints = [], SoftBodies = [], Morphs = [],
            Bones = [basic.Bones[0], new() { Name = "stable", EnglishName = "", Position = Vector3.Zero,
                ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None }]
        };
    }

    private static ScenePmmControlState Controls(EditableSceneObject[] actors, PmxModel model)
    {
        PmmString S(string value) => new(0, value, []);
        var linear = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 4).SelectMany(value => value).ToArray();
        var models = actors.Select((actor, index) => new PmmModel
        {
            Index = (byte)index, Name = S(actor.Name), Path = S("actor" + index + ".pmx"),
            BoneNames = model.Bones.Select(bone => S(bone.Name)).ToArray(), SelectedMorphIndices = [-1, -1, -1, -1], Visible = true, LastFrame = 1,
            OutsideParentSubjectBoneIndices = index == 0 ? [] : [0],
            OutsideParentStates = index == 0 ? [] : [new(0, 0, int.MaxValue, new(0, index - 1, index == 1 ? 1 : 0))],
            InitialBoneKeyframes = model.Bones.Select((bone, boneIndex) => new PmmBoneKeyframe
                { ObjectIndex = boneIndex, Rotation = Quaternion.Identity, Interpolation = linear }).ToArray(),
            InitialModelKeyframe = new() { Visible = true, OutsideParents = index == 0 ? [] : [new(0, index - 1, index == 1 ? 1 : 0)] },
            BoneStates = model.Bones.Select((bone, boneIndex) => new PmmBoneState(boneIndex, Vector3.Zero, Quaternion.Identity, false, false, 0)).ToArray()
        }).ToArray();
        var document = new PmmDocument
        {
            AnsiCodePage = 932, PanelStates = new byte[7], OutputWidth = 640, OutputHeight = 480, CameraFov = 30, Models = models,
            Camera = new() { InitialKeyframe = new() { Interpolation = new byte[24], Fov = 30, ParentModelIndex = -1, ParentBoneIndex = -1 } },
            Settings = new() { PhysicsSimulationMode = 1, EdgeColorRgb = [0, 0, 0], UnknownMatrix = new byte[64], SelectionTrailerFlag = 0 },
            Gravity = new() { InitialKeyframe = new() { Acceleration = 1, Direction = -Vector3.UnitY } }
        };
        return ScenePmmControlState.Create(document, actors.Select((actor, index) => new ScenePmmActorSource(actor.Id, (byte)index, model)));
    }

    private sealed class Factory
    {
        internal readonly List<RecordingBackend> Worlds = [];
        internal ProjectSettingsBackendProvider Provider() => new((_, _) => Create(), _ => Create(), poseInitializationSubsteps: 0);
        private RecordingBackend Create() { var world = new RecordingBackend(); Worlds.Add(world); return world; }
    }

    private sealed class RecordingBackend : ICharacterPhysicsMutableBackend
    {
        internal readonly TrackingSharedBackend Inner = new();
        internal readonly List<string> History = [], Targets = [];
        public string Name => "frozen attachment reuse oracle";
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms) => Inner.Initialize(definition, transforms);
        public int AddBody(PhysicsBodyDefinition body, Matrix4x4 transform) => Inner.AddBody(body, transform);
        public int AddJoint(PhysicsJointDefinition joint, Matrix4x4 a, Matrix4x4 b) => Inner.AddJoint(joint, a, b);
        public void RemoveBody(int index) => Inner.RemoveBody(index);
        public void RemoveJoint(int index) => Inner.RemoveJoint(index);
        public void ResetBody(int index, Matrix4x4 transform) => Inner.ResetBody(index, transform);
        public void SetBodyKinematic(int index, bool state) => Inner.SetBodyKinematic(index, state);
        public void SetKinematicTarget(int index, Matrix4x4 transform, float seconds)
        { Targets.Add(index + ":" + Convert.ToHexString(Bytes<Matrix4x4>(new[] { transform })) + ":" + BitConverter.SingleToInt32Bits(seconds)); Inner.SetKinematicTarget(index, transform, seconds); }
        public Matrix4x4 GetBodyWorldTransform(int index) => Inner.GetBodyWorldTransform(index);
        public void SetGravity(Vector3 gravity) => Inner.SetGravity(gravity);
        public void ApplyImpulse(int index, Vector3 linear, Vector3 angular) => Inner.ApplyImpulse(index, linear, angular);
        public void Step(float seconds)
        {
            History.Add(BitConverter.SingleToInt32Bits(seconds) + ":" + Convert.ToHexString(Bytes<Vector3>(new[] { Inner.Gravity }))
                + ":" + string.Join("|", Inner.Bodies.Select(body =>
                    Convert.ToHexString(Bytes<Matrix4x4>(new[] { body.Transform })) + ":"
                    + Convert.ToHexString(Bytes<Vector3>(new[] { body.Velocity })) + ":" + body.Kinematic + ":" + body.Removed)));
            Inner.Step(seconds);
        }
        public void Dispose() => Inner.Dispose();
    }
}
