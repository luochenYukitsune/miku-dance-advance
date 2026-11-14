using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

/// <summary>The oracle publishes each 1/120 boundary through the ordinary one-step request.
/// Batching may omit intermediate skins, but cannot change any solver input or final product.</summary>
public sealed class SceneInteractiveDeferredSkinningTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(true, false, 2)]
    [InlineData(true, false, 8)]
    [InlineData(false, true, 0)]
    [InlineData(false, true, 1)]
    [InlineData(false, true, 2)]
    [InlineData(false, true, 8)]
    [InlineData(true, true, 0)]
    [InlineData(true, true, 1)]
    [InlineData(true, true, 2)]
    [InlineData(true, true, 8)]
    public void BatchedInteractiveBoundariesMatchIndividualFullPublicationsIncludingFrozenDisabledParentChains(
        bool shared, bool pmm, int steps)
    {
        var model = Model(); var rest = SharedPhysicsFixture.Rest(model);
        var layout = new SceneCompositionEditor();
        var actors = Enumerable.Range(0, 4).Select(i => layout.Add("actor" + i,
            Path.GetFullPath("interactive-deferred-" + i + ".pmx"), ModelRole.Character, rest)).ToArray();
        for (int i = 0; i < actors.Length; i++)
            layout.SetTransform(actors[i].Id, new(i * 3, i, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, i * .1f), 1);
        // actor0 is edited after its motion ended; actor1 has rigid metadata but physics is disabled.
        // actor2 freezes behind that nonphysical parent; actor3 is an unrelated frozen collider.
        var motions = actors.Select((actor, i) => new SceneActorMotion(actor.Id, model, Motion(), PhysicsEnabled: i != 1)).ToArray();
        SceneOutsideParentBinding[] parents = pmm ? [] :
            [new(actors[1].Id, 0, actors[0].Id, 0, Matrix4x4.Identity),
             new(actors[2].Id, 0, actors[1].Id, 0, Matrix4x4.Identity)];
        var controls = pmm ? Controls(actors, model) : null;
        using var source = new SceneProjectSession(layout, new(), motions, new Factory().Provider(),
            new() { Physics = new() { SharedWorld = shared, GroundEnabled = false } },
            outsideParents: parents, initialFrame: 7, pmmControls: controls, cancellationToken: Token);
        var request = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion>());
        var expectedFactory = new Factory(); var actualFactory = new Factory();
        using var expected = request.CreateSession(expectedFactory.Provider(), Token);
        using var actual = request.CreateSession(actualFactory.Provider(), Token);
        var inputs = actors.Select(actor => new SceneObjectRenderRequest(actor.Id, [0, 1, 2, 3, 4], includeSourceGeometry: true)).ToArray();
        var previous = actual.CaptureRenderSnapshot(inputs);
        var previousVertices = previous.Scene.Surface.Snapshot.Vertices.ToArray();
        var previousBones = actors.Select(actor => previous.SceneInputs[actor.Id].BoneWorldMatrices.ToArray()).ToArray();
        var document = controls is null ? null : PmmWriter.ToBytes(actual.PmmControls!.ExportDocument());
        int[] initializations = actualFactory.Worlds.Select(world => world.Inner.Initializations).ToArray();

        // Two different raw local drafts, a release-style zero-time publication and a cleared draft
        // exercise reuse of the warm solver and frozen attachment anchors across entire requests.
        foreach (var draft in new[] { Bones(4), Bones(9), new Dictionary<int, BoneLocalTransform>() })
        {
            foreach (var world in expectedFactory.Worlds) world.Clear();
            foreach (var world in actualFactory.Worlds) world.Clear();
            if (steps == 0) expected.PreviewInteractiveBones(actors[0].Id, draft, 0, Token);
            else for (int step = 0; step < steps; step++)
                expected.PreviewInteractiveBones(actors[0].Id, draft, CharacterPhysicsSession.FixedStepSeconds, Token);
            actual.PreviewInteractiveBones(actors[0].Id, draft, steps * CharacterPhysicsSession.FixedStepSeconds, Token);
            var reference = expected.CaptureRenderSnapshot(inputs); var result = actual.CaptureRenderSnapshot(inputs);
            if (pmm && draft.TryGetValue(0, out var raw))
                Assert.Equal(raw.Translation, result.SceneInputs[actors[0].Id].BoneWorldMatrices[0].Translation);
            Assert.Equal(reference.Scene.Surface.Snapshot.Vertices.ToArray(), result.Scene.Surface.Snapshot.Vertices.ToArray());
            Assert.Equal(reference.Scene.Outline!.Vertices, result.Scene.Outline!.Vertices);
            AssertSpanBitsEqual<SceneVertex>(reference.Scene.Surface.Snapshot.Vertices, result.Scene.Surface.Snapshot.Vertices);
            AssertBitsEqual(reference.Scene.Outline.Vertices, result.Scene.Outline.Vertices);
            Assert.Equal(reference.Scene.Materials, result.Scene.Materials);
            for (int actor = 0; actor < actors.Length; actor++)
            {
                var id = actors[actor].Id;
                Assert.Equal(reference.SceneInputs[id].BoneWorldMatrices, result.SceneInputs[id].BoneWorldMatrices);
                AssertBoneBitsEqual(reference.SceneInputs[id].BoneWorldMatrices, result.SceneInputs[id].BoneWorldMatrices);
                Assert.Equal(reference.SceneInputs[id].SourceGeometry!.Vertices.ToArray(), result.SceneInputs[id].SourceGeometry!.Vertices.ToArray());
                AssertSpanBitsEqual<SceneVertex>(reference.SceneInputs[id].SourceGeometry!.Vertices, result.SceneInputs[id].SourceGeometry!.Vertices);
                Assert.Equal(previousBones[actor], previous.SceneInputs[id].BoneWorldMatrices);
                AssertBoneBitsEqual(previousBones[actor], previous.SceneInputs[id].BoneWorldMatrices);
            }
            Assert.Equal(expectedFactory.Worlds.Count, actualFactory.Worlds.Count);
            for (int world = 0; world < actualFactory.Worlds.Count; world++)
            {
                var a = expectedFactory.Worlds[world]; var b = actualFactory.Worlds[world];
                Assert.Equal(a.Boundaries, b.Boundaries); Assert.Equal(a.Targets, b.Targets);
                Assert.Equal(a.Inner.Bodies.Select(body => (body.Transform, body.Velocity, body.Kinematic)),
                    b.Inner.Bodies.Select(body => (body.Transform, body.Velocity, body.Kinematic)));
                AssertBitsEqual(a.Inner.Bodies.Select(body => body.Transform), b.Inner.Bodies.Select(body => body.Transform));
                AssertBitsEqual(a.Inner.Bodies.Select(body => body.Velocity), b.Inner.Bodies.Select(body => body.Velocity));
                Assert.Equal(0, b.Impulses);
                // Independent PMM leaves the unrelated frozen world untouched; shared worlds keep
                // all actors in the coherent collision batch, including the unrelated frozen actor.
                Assert.Equal(!shared && world == 2 ? 0 : steps, b.Boundaries.Count);
            }
            Assert.Equal(7, result.SourceFrame); Assert.All(actual.Actors, actor => Assert.Equal(1, actor.LocalFrame));
            Assert.Equal(initializations, actualFactory.Worlds.Select(world => world.Inner.Initializations));
        }
        Assert.Equal(previousVertices, previous.Scene.Surface.Snapshot.Vertices.ToArray());
        AssertSpanBitsEqual<SceneVertex>(previousVertices, previous.Scene.Surface.Snapshot.Vertices);
        if (document is not null) Assert.Equal(document, PmmWriter.ToBytes(actual.PmmControls!.ExportDocument()));
        Assert.False(actual.CanUndo); Assert.False(actual.CanRedo); Assert.True(request.IsCurrent(source));
    }

    [Fact]
    public void CancelledUnpublishedBatchRetainsPublishedSnapshotsAndRetiresThePartiallyAdvancedOwner()
    {
        var model = Model(); var layout = new SceneCompositionEditor();
        var actor = layout.Add("actor", Path.GetFullPath("interactive-deferred-cancel.pmx"), ModelRole.Character, SharedPhysicsFixture.Rest(model));
        using var source = new SceneProjectSession(layout, new(), [new(actor.Id, model, Motion())], new Factory().Provider(),
            new() { Physics = new() { SharedWorld = true, GroundEnabled = false } }, initialFrame: 7, cancellationToken: Token);
        var sourceFrame = source.Frame; var request = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion>());
        var factory = new Factory(); using var owner = request.CreateSession(factory.Provider(), Token);
        var frame = owner.Frame; var retainedVertices = frame.Surface.Snapshot.Vertices.ToArray();
        using var cancelled = new CancellationTokenSource();
        var backend = Assert.Single(factory.Worlds); backend.AfterStep = cancelled.Cancel;
        Assert.ThrowsAny<OperationCanceledException>(() => owner.PreviewInteractiveBones(actor.Id, Bones(4),
            4 * CharacterPhysicsSession.FixedStepSeconds, cancelled.Token));
        Assert.Single(backend.Boundaries); Assert.True(owner.IsFaulted); Assert.Same(frame, owner.Frame);
        Assert.Equal(retainedVertices, frame.Surface.Snapshot.Vertices.ToArray());
        AssertSpanBitsEqual<SceneVertex>(retainedVertices, frame.Surface.Snapshot.Vertices);
        Assert.Throws<InvalidOperationException>(() => owner.PreviewInteractiveBones(actor.Id, Bones(8), CharacterPhysicsSession.FixedStepSeconds, Token));
        Assert.Same(sourceFrame, source.Frame); Assert.True(request.IsCurrent(source));
    }

    [Fact]
    public void IntermediateBoundariesDoNotAllocateVertexSizedProductsForActiveOrFrozenParentedActors()
    {
        const int vertexCount = 20000;
        var model = Model(vertexCount); var layout = new SceneCompositionEditor();
        var rest = SharedPhysicsFixture.Rest(model);
        var actors = Enumerable.Range(0, 3).Select(i => layout.Add("actor" + i,
            Path.GetFullPath("interactive-deferred-allocation-" + i + ".pmx"), ModelRole.Character, rest)).ToArray();
        SceneOutsideParentBinding[] parents = [new(actors[1].Id, 0, actors[0].Id, 0, Matrix4x4.Identity),
            new(actors[2].Id, 0, actors[1].Id, 0, Matrix4x4.Identity)];
        using var source = new SceneProjectSession(layout, new(), actors.Select((actor, index) =>
            new SceneActorMotion(actor.Id, model, Motion(), PhysicsEnabled: index != 1)), new Factory().Provider(),
            new() { Physics = new() { SharedWorld = true, GroundEnabled = false } },
            outsideParents: parents, initialFrame: 7, cancellationToken: Token);
        var factory = new Factory();
        using var owner = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion>()).CreateSession(factory.Provider(), Token);
        var draft = Bones(4);
        owner.PreviewInteractiveBones(actors[0].Id, draft, 8 * CharacterPhysicsSession.FixedStepSeconds, Token);
        var inputs = new[] { new SceneObjectRenderRequest(actors[1].Id, [0, 1, 2, 3, 4], includeSourceGeometry: true) };
        var beforeRepeat = owner.CaptureRenderSnapshot(inputs);
        var retainedGeometry = beforeRepeat.SceneInputs[actors[1].Id].SourceGeometry!;
        var retainedVertices = retainedGeometry.Vertices.ToArray();
        var backend = Assert.Single(factory.Worlds); backend.Clear();
        owner.PreviewInteractiveBones(actors[0].Id, draft, 8 * CharacterPhysicsSession.FixedStepSeconds, Token);
        Assert.Equal(8, backend.BoundaryAllocatedBytes.Count);
        // Step entry i -> i+1 includes reflection/after-physics IK for i and preparation for i+1.
        // The repeated draft leaves the nonphysical parent's attachment unchanged: all seven
        // intervals, including the final preparation, retain its already complete skin.
        for (int pair = 0; pair + 1 < backend.BoundaryAllocatedBytes.Count; pair++)
        {
            long bytes = backend.BoundaryAllocatedBytes[pair + 1] - backend.BoundaryAllocatedBytes[pair];
            Assert.True(bytes < vertexCount * 24L,
                $"Unchanged Step {pair + 1} -> {pair + 2} allocated vertex-sized output: {bytes} bytes.");
        }
        Assert.Same(retainedGeometry, owner.CaptureRenderSnapshot(inputs).SceneInputs[actors[1].Id].SourceGeometry);

        // A real parent-target change must invalidate that complete display. Intermediate
        // boundaries still defer its skin, while final preparation must materialize the new
        // geometry before the physical child consumes it. The independent eight-step byte
        // oracles above continue to compare every solver input and complete final product.
        backend.Clear();
        owner.PreviewInteractiveBones(actors[0].Id, Bones(9), 8 * CharacterPhysicsSession.FixedStepSeconds, Token);
        Assert.Equal(8, backend.BoundaryAllocatedBytes.Count);
        for (int pair = 0; pair + 2 < backend.BoundaryAllocatedBytes.Count; pair++)
        {
            long bytes = backend.BoundaryAllocatedBytes[pair + 1] - backend.BoundaryAllocatedBytes[pair];
            Assert.True(bytes < vertexCount * 24L,
                $"Changed-parent intermediate Step {pair + 1} -> {pair + 2} allocated vertex-sized output: {bytes} bytes.");
        }
        long finalPreparationBytes = backend.BoundaryAllocatedBytes[^1] - backend.BoundaryAllocatedBytes[^2];
        Assert.True(finalPreparationBytes >= vertexCount * 24L,
            $"Changed-parent final Step 7 -> 8 must include the nonphysical parent's new skin; observed {finalPreparationBytes} bytes.");
        var changed = owner.CaptureRenderSnapshot(inputs);
        Assert.NotSame(retainedGeometry, changed.SceneInputs[actors[1].Id].SourceGeometry);
        Assert.NotEqual(beforeRepeat.SceneInputs[actors[1].Id].BoneWorldMatrices[0], changed.SceneInputs[actors[1].Id].BoneWorldMatrices[0]);
        AssertSpanBitsEqual<SceneVertex>(retainedVertices, retainedGeometry.Vertices);
    }

    [Fact]
    public void SoftMetadataRetainsCompleteFallbackAndRejectsWarmBatchBeforeAnySolverMutation()
    {
        var model = PmxSoftBodyTests.MaterialFixture();
        var layout = new SceneCompositionEditor();
        var actor = layout.Add("soft", Path.GetFullPath("interactive-deferred-soft.pmx"), ModelRole.Character, SharedPhysicsFixture.Rest(model));
        using var source = new SceneProjectSession(layout, new(), [new(actor.Id, model, SharedPhysicsFixture.Motion(1), PhysicsEnabled: false)],
            new Factory().Provider(), initialFrame: 7, cancellationToken: Token);
        var request = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion>());
        Assert.False(request.SupportsInteractiveBones);
        var factory = new Factory(); using var complete = request.CreateSession(factory.Provider(), Token);
        var before = complete.Frame;
        Assert.Throws<NotSupportedException>(() => complete.PreviewInteractiveBones(actor.Id, Bones(4), 2 * CharacterPhysicsSession.FixedStepSeconds, Token));
        Assert.Same(before, complete.Frame); Assert.False(complete.IsFaulted);
        Assert.All(factory.Worlds, world => Assert.Empty(world.Boundaries));
    }

    private static Dictionary<int, BoneLocalTransform> Bones(float x) => new()
    {
        [0] = new(new(x, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, x * .01f)),
        [1] = new(new(.2f, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, x * .02f))
    };

    private static void AssertBoneBitsEqual(IEnumerable<KeyValuePair<int, Matrix4x4>> expected,
        IEnumerable<KeyValuePair<int, Matrix4x4>> actual)
    {
        var a = expected.OrderBy(pair => pair.Key).ToArray(); var b = actual.OrderBy(pair => pair.Key).ToArray();
        Assert.Equal(a.Select(pair => pair.Key), b.Select(pair => pair.Key));
        AssertBitsEqual(a.Select(pair => pair.Value), b.Select(pair => pair.Value));
    }

    // Byte comparison preserves negative zero and NaN payload distinctions hidden by float.Equals.
    // These public value types contain only numeric fields; all owned snapshots remain retained.
    private static void AssertBitsEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual) where T : unmanaged
    {
        var a = expected.ToArray(); var b = actual.ToArray();
        Assert.Equal(MemoryMarshal.AsBytes(a.AsSpan()).ToArray(), MemoryMarshal.AsBytes(b.AsSpan()).ToArray());
    }

    private static void AssertSpanBitsEqual<T>(ReadOnlySpan<T> expected, ReadOnlySpan<T> actual) where T : unmanaged
        => Assert.Equal(MemoryMarshal.AsBytes(expected).ToArray(), MemoryMarshal.AsBytes(actual).ToArray());

    private static VmdMotion Motion() => new()
    {
        BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 1, Translation = new(.1f, 0, 0) }],
        MorphKeyframes = [new() { MorphName = "vertex", Frame = 1, Weight = .4f }, new() { MorphName = "bone", Frame = 1, Weight = .7f }]
    };

    private static PmxModel Model(int vertexCount = 3)
    {
        var basic = SharedPhysicsFixture.Model(PmxRigidBodyOperation.Dynamic);
        PmxBone Bone(string name, Vector3 position, int parent, PmxBoneFlags flags = PmxBoneFlags.None) =>
            new() { Name = name, EnglishName = "", Position = position, ParentIndex = parent, DeformDepth = 0, Flags = flags };
        return new()
        {
            Header = basic.Header, Name = basic.Name, EnglishName = "", Comment = "", EnglishComment = "",
            Textures = basic.Textures, Faces = basic.Faces, Materials = basic.Materials, DisplayFrames = [], Joints = [], SoftBodies = [],
            RigidBodies = basic.RigidBodies,
            Bones = [Bone("root", Vector3.Zero, -1), Bone("link", Vector3.Zero, 0), Bone("tip", Vector3.UnitX, 1),
                new() { Name = "goal", EnglishName = "", Position = Vector3.UnitY, ParentIndex = 0, DeformDepth = 1,
                    Flags = PmxBoneFlags.DeformAfterPhysics | PmxBoneFlags.Ik, IkTargetBoneIndex = 2,
                    IkIterationCount = 16, IkLimit = MathF.PI, IkLinks = [new(1, false, Vector3.Zero, Vector3.Zero)] },
                new() { Name = "grant", EnglishName = "", Position = new(2, 0, 0), ParentIndex = 0, DeformDepth = 2,
                    Flags = PmxBoneFlags.DeformAfterPhysics | PmxBoneFlags.AppendTranslate | PmxBoneFlags.AppendRotate,
                    AppendBoneIndex = 1, AppendWeight = .3f }],
            Vertices = Enumerable.Range(0, vertexCount).Select(index => basic.Vertices[index % basic.Vertices.Count]).Select(vertex => new PmxVertex
            { Position = vertex.Position, Normal = vertex.Normal, Uv = vertex.Uv, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef2,
                BoneIndices = [2, 4, -1, -1], BoneWeights = [.3f, .7f, 0, 0], EdgeScale = 1 }).ToArray(),
            Morphs = [new() { Name = "vertex", EnglishName = "", ControlPanel = 4, Type = PmxMorphType.Position,
                    PositionOffsets = [new(0, new(.1f, .2f, .3f))] },
                new() { Name = "bone", EnglishName = "", ControlPanel = 4, Type = PmxMorphType.Bone,
                    BoneOffsets = [new(4, new(.1f, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, .1f))] }]
        };
    }

    private static ScenePmmControlState Controls(EditableSceneObject[] actors, PmxModel model)
    {
        PmmString S(string value) => new(0, value, []);
        byte[] linear = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 4).SelectMany(value => value).ToArray();
        var models = actors.Select((actor, i) => new PmmModel
        {
            Index = (byte)i, Name = S(actor.Name), Path = S("actor" + i + ".pmx"), BoneNames = model.Bones.Select(bone => S(bone.Name)).ToArray(),
            SelectedMorphIndices = [-1, -1, -1, -1], Visible = true, LastFrame = 1,
            OutsideParentSubjectBoneIndices = i is 1 or 2 ? [0] : [],
            OutsideParentStates = i is 1 or 2 ? [new(0, 0, int.MaxValue, new(0, i - 1, 0))] : [],
            InitialBoneKeyframes = model.Bones.Select((bone, index) => new PmmBoneKeyframe { ObjectIndex = index,
                Rotation = Quaternion.Identity, Interpolation = linear, PhysicsDisabled = i == 0 && index == 0 }).ToArray(),
            InitialModelKeyframe = new() { Visible = true, OutsideParents = i is 1 or 2 ? [new(0, i - 1, 0)] : [] },
            BoneStates = model.Bones.Select((bone, index) => new PmmBoneState(index, Vector3.Zero, Quaternion.Identity, false, false, 0)).ToArray()
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
        internal readonly List<string> Boundaries = [], Targets = [];
        internal readonly List<long> BoundaryAllocatedBytes = [];
        internal Action? AfterStep;
        internal int Impulses;
        public string Name => "interactive deferred skin orchestration probe";
        public IReadOnlyList<string> Diagnostics => [];
        internal void Clear() { Boundaries.Clear(); Targets.Clear(); BoundaryAllocatedBytes.Clear(); Impulses = 0; }
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms) => Inner.Initialize(definition, transforms);
        public int AddBody(PhysicsBodyDefinition body, Matrix4x4 transform) => Inner.AddBody(body, transform);
        public int AddJoint(PhysicsJointDefinition joint, Matrix4x4 a, Matrix4x4 b) => Inner.AddJoint(joint, a, b);
        public void RemoveBody(int index) => Inner.RemoveBody(index);
        public void RemoveJoint(int index) => Inner.RemoveJoint(index);
        public void ResetBody(int index, Matrix4x4 transform) => Inner.ResetBody(index, transform);
        public void SetBodyKinematic(int index, bool state) => Inner.SetBodyKinematic(index, state);
        public void SetKinematicTarget(int index, Matrix4x4 transform, float seconds)
        { Targets.Add(index + ":" + Bits(transform) + ":" + BitConverter.SingleToInt32Bits(seconds)); Inner.SetKinematicTarget(index, transform, seconds); }
        public Matrix4x4 GetBodyWorldTransform(int index) => Inner.GetBodyWorldTransform(index);
        public void SetGravity(Vector3 gravity) => Inner.SetGravity(gravity);
        public void ApplyImpulse(int index, Vector3 linear, Vector3 angular) { Impulses++; Inner.ApplyImpulse(index, linear, angular); }
        public void Step(float seconds)
        {
            BoundaryAllocatedBytes.Add(GC.GetAllocatedBytesForCurrentThread());
            Boundaries.Add(BitConverter.SingleToInt32Bits(seconds) + ":" + Bits(Matrix4x4.CreateTranslation(Inner.Gravity)) + ":"
                + string.Join("|", Inner.Bodies.Select(body => Bits(body.Transform) + ":" + Bits(Matrix4x4.CreateTranslation(body.Velocity))
                    + ":" + body.Kinematic + ":" + body.Removed)));
            Inner.Step(seconds); AfterStep?.Invoke();
        }
        private static string Bits(Matrix4x4 value) => string.Join(",", new[]
            { value.M11, value.M12, value.M13, value.M14, value.M21, value.M22, value.M23, value.M24,
                value.M31, value.M32, value.M33, value.M34, value.M41, value.M42, value.M43, value.M44 }.Select(BitConverter.SingleToInt32Bits));
        public void Dispose() => Inner.Dispose();
    }
}
