using System.Numerics;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

/// <summary>Compare batched initialization with the full publication path at every boundary.
/// The deterministic backend exposes orchestration equality, not Bullet collision accuracy.</summary>
public sealed class SharedRigidInitializationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ProjectAndDraftPreparationMatchPublicInitialFramesAndEverySolverBoundary(bool shared, bool pmm)
    {
        const int steps = 120;
        var model = SharedPhysicsFixture.Model(); var layout = new SceneCompositionEditor();
        var actors = Enumerable.Range(0, 4).Select(index => layout.Add("actor" + index, "actor" + index + ".pmx",
            ModelRole.Character, SharedPhysicsFixture.Rest(model))).ToArray();
        for (int index = 0; index < actors.Length; index++)
            layout.SetTransform(actors[index].Id, new(index * 3, index, 0),
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, .1f * index), 1);
        var motions = actors.Select((actor, index) => new SceneActorMotion(actor.Id, model,
            SharedPhysicsFixture.Motion(1, new(.2f * index, 0, 0)), PhysicsEnabled: index != 2)).ToArray();
        SceneOutsideParentBinding[] parents = pmm ? [] :
            [new(actors[0].Id, 0, actors[2].Id, 0, Matrix4x4.Identity),
             new(actors[2].Id, 0, actors[1].Id, 0, Matrix4x4.Identity)];
        var controls = pmm ? Controls(actors, model, nonphysicalAttached: true) : null;
        var settings = new ProjectSettings { Physics = new() { SharedWorld = shared, GroundEnabled = false } };
        var expectedFactory = new Factory(); var actualFactory = new Factory();
        using var expected = Reference(motions, expectedFactory);
        var initial = expected.Frame;
        var initialVertices = Bytes(initial.Surface.Snapshot.Vertices);
        var initialOutline = Bytes<OriginalOutlineVertex>(initial.Outline!.Vertices.ToArray());
        expected.StabilizePhysicsAtCurrentFrame(steps, Token);
        using var phases = new InitializationPhases();
        using var actual = new SceneProjectSession(layout, new(), motions,
            new ProjectSettingsBackendProvider((_, _) => actualFactory.Create(), _ => actualFactory.Create(),
                poseInitializationSubsteps: steps), settings, initialFrame: 7, outsideParents: parents,
            pmmControls: controls, cancellationToken: Token);
        Assert.Equal(0, phases.Count("session.constructor.compose"));
        Assert.Equal(1, phases.Count("initialization.compose"));
        Assert.Equal(shared || pmm ? 1 : 0, phases.Count("initialization.graph-plan"));
        SameOutput(expected, actual);
        SameHistory(expectedFactory.Worlds, actualFactory.Worlds);
        Assert.Equal(initialVertices, Bytes(initial.Surface.Snapshot.Vertices));
        Assert.Equal(initialOutline, Bytes<OriginalOutlineVertex>(initial.Outline.Vertices.ToArray()));

        var retained = actual.Frame;
        var retainedVertices = Bytes(retained.Surface.Snapshot.Vertices);
        var retainedBones = actors.Select(actor => actual.CaptureRenderSnapshot(
            [new SceneObjectRenderRequest(actor.Id, [0])]).SceneInputs[actor.Id].BoneWorldMatrices[0]).ToArray();
        var draft = SharedPhysicsFixture.Motion(1, new(4, 2, 1));
        var draftMotions = motions.Select(motion => motion.ObjectId == actors[2].Id
            ? motion with { Motion = draft } : motion).ToArray();
        var draftFactory = new Factory();
        using var expectedDraft = Reference(draftMotions, draftFactory);
        expectedDraft.StabilizePhysicsAtCurrentFrame(steps, Token);
        using var preview = actual.CreatePosePreview(actors[2].Id, draft, Token);
        SameOutput(expectedDraft, preview);
        SameHistory(draftFactory.Worlds, actualFactory.Worlds.Skip(expectedFactory.Worlds.Count).ToArray());
        Assert.Same(retained, actual.Frame); Assert.Equal(retainedVertices, Bytes(retained.Surface.Snapshot.Vertices));
        for (int index = 0; index < actors.Length; index++)
            Assert.Equal(retainedBones[index], actual.CaptureRenderSnapshot(
                [new SceneObjectRenderRequest(actors[index].Id, [0])]).SceneInputs[actors[index].Id].BoneWorldMatrices[0]);
        Assert.True(preview.CanReusePosePreview(actual)); Assert.False(actual.CanUndo); Assert.False(actual.CanRedo);
        if (controls is not null) Assert.Equal(PmmWriter.ToBytes(controls.ExportDocument()), PmmWriter.ToBytes(actual.PmmControls!.ExportDocument()));

        SceneAnimationSession Reference(SceneActorMotion[] bindings, Factory factory) => new(layout.Objects, bindings,
            _ => factory.Create(), initialFrame: 7, outsideParents: parents, pmmControls: controls,
            sharedPhysicsFactory: shared ? () => factory.Create() : null,
            sharedWorldGravity: shared ? settings.Physics.Gravity : null, cancellationToken: Token);
        void SameOutput(SceneAnimationSession reference, SceneProjectSession result)
        {
            Assert.Equal(Bytes(reference.Frame.Surface.Snapshot.Vertices), Bytes(result.Frame.Surface.Snapshot.Vertices));
            Assert.Equal(Bytes(reference.Frame.Surface.Snapshot.Indices), Bytes(result.Frame.Surface.Snapshot.Indices));
            Assert.Equal(Bytes<OriginalOutlineVertex>(reference.Frame.Outline!.Vertices.ToArray()),
                Bytes<OriginalOutlineVertex>(result.Frame.Outline!.Vertices.ToArray()));
            Assert.Equal(reference.Frame.Surface.Materials, result.Frame.Surface.Materials);
            Assert.Equal(reference.Frame.Materials, result.Frame.Materials);
            foreach (var actor in actors)
            {
                var worlds = result.CaptureRenderSnapshot([new SceneObjectRenderRequest(actor.Id, [0])])
                    .SceneInputs[actor.Id].BoneWorldMatrices.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
                Assert.Equal(Bytes<Matrix4x4>(reference.GetBoneWorldMatrices(actor.Id).ToArray()), Bytes<Matrix4x4>(worlds));
            }
        }
        static void SameHistory(IReadOnlyList<RecordingBackend> reference, IReadOnlyList<RecordingBackend> result)
        {
            Assert.Equal(reference.Count, result.Count);
            for (int index = 0; index < reference.Count; index++)
            {
                Assert.Equal(steps, result[index].History.Count);
                Assert.Equal(reference[index].History, result[index].History);
                Assert.Equal(reference[index].Inner.Initializations, result[index].Inner.Initializations);
                Assert.Equal(reference[index].Impulses, result[index].Impulses);
            }
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 120)]
    [InlineData(true, 0)]
    public void ZeroBudgetAndNonphysicalGraphsKeepTheirImmediateInitialCombinedFrame(bool physical, int steps)
    {
        var model = SharedPhysicsFixture.Model(physics: physical); var (layout, actor, _) = SharedPhysicsFixture.Layout(model);
        var motions = new[] { new SceneActorMotion(actor.Id, model, SharedPhysicsFixture.Motion(1, Vector3.UnitX)) };
        var factory = new Factory();
        using var phases = new InitializationPhases();
        using var project = new SceneProjectSession(layout, new(), motions,
            new ProjectSettingsBackendProvider((_, _) => factory.Create(), _ => factory.Create(), poseInitializationSubsteps: steps),
            initialFrame: 7, cancellationToken: Token);
        Assert.Equal(1, phases.Count("session.constructor.compose"));
        Assert.Equal(0, phases.Count("initialization.compose"));
        Assert.Equal(0, phases.Count("initialization.graph-plan"));
        Assert.NotEmpty(project.Frame.Surface.Snapshot.Vertices.ToArray());
        Assert.All(factory.Worlds, world => Assert.Empty(world.History));
        using var expected = new SceneAnimationSession(layout.Objects, motions, _ => new TrackingSharedBackend(), initialFrame: 7,
            cancellationToken: Token);
        Assert.Equal(Bytes(expected.Frame.Surface.Snapshot.Vertices), Bytes(project.Frame.Surface.Snapshot.Vertices));
        Assert.Equal(Bytes<OriginalOutlineVertex>(expected.Frame.Outline!.Vertices.ToArray()),
            Bytes<OriginalOutlineVertex>(project.Frame.Outline!.Vertices.ToArray()));
    }

    private static byte[] Bytes<T>(ReadOnlySpan<T> values) where T : unmanaged => MemoryMarshal.AsBytes(values).ToArray();

    private sealed class InitializationPhases : EventListener
    {
        private readonly int thread = Environment.CurrentManagedThreadId;
        private readonly List<string> stages = [];
        protected override void OnEventSourceCreated(EventSource source)
        { if (source.Name == "MDA-PoseEvaluation") EnableEvents(source, EventLevel.Informational); }
        protected override void OnEventWritten(EventWrittenEventArgs value)
        {
            if (Environment.CurrentManagedThreadId == thread && value.EventId == 1 && value.Payload?[0] is string stage)
                stages.Add(stage);
        }
        internal int Count(string stage) => stages.Count(value => value == stage);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void DeferredSkinsPreserveEveryPhysicalBoundaryAndFinalCrossParentScene(bool shared, bool pmm, bool nonphysicalAttached)
    {
        const int steps = 120;
        var model = SharedPhysicsFixture.Model(); var rest = SharedPhysicsFixture.Rest(model);
        var layout = new SceneCompositionEditor();
        var actors = Enumerable.Range(0, 4).Select(i => layout.Add("actor" + i, "actor" + i + ".pmx", ModelRole.Character, rest)).ToArray();
        for (int i = 0; i < actors.Length; i++)
            layout.SetTransform(actors[i].Id, new(3 * i, i, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .1f * i), 1);
        var motions = actors.Select((actor, i) => new SceneActorMotion(actor.Id, model,
            SharedPhysicsFixture.Motion(1, new(.2f * i, 0, 0)), PhysicsEnabled: i != 2)).ToArray();
        // The disabled actor still has PMX rigid-body metadata. In the second chain it
        // follows a moving physical parent and supplies bones to another physical child.
        // Its intermediate sample must not invent physics-driven bones or change ordering.
        SceneOutsideParentBinding[] parents = pmm ? [] : nonphysicalAttached
            ? [new(actors[0].Id, 0, actors[2].Id, 0, Matrix4x4.Identity),
               new(actors[2].Id, 0, actors[1].Id, 0, Matrix4x4.Identity)]
            : [new(actors[0].Id, 0, actors[1].Id, 0, Matrix4x4.Identity),
               new(actors[1].Id, 0, actors[2].Id, 0, Matrix4x4.Identity)];
        var controls = pmm ? Controls(actors, model, nonphysicalAttached) : null;
        var expectedFactory = new Factory(); var actualFactory = new Factory();
        using var expected = new SceneAnimationSession(layout.Objects, motions, _ => expectedFactory.Create(),
            initialFrame: 7, outsideParents: parents, pmmControls: controls,
            sharedPhysicsFactory: shared ? () => expectedFactory.Create() : null, cancellationToken: Token);
        using var actual = new SceneAnimationSession(layout.Objects, motions, _ => actualFactory.Create(),
            initialFrame: 7, outsideParents: parents, pmmControls: controls,
            sharedPhysicsFactory: shared ? () => actualFactory.Create() : null, cancellationToken: Token);
        var before = actual.Frame; var beforeVertices = before.Surface.Snapshot.Vertices.ToArray();
        var beforeWorlds = actors.Select(actor => actual.GetBoneWorldMatrices(actor.Id)).ToArray();
        var beforeWorldCopies = beforeWorlds.Select(worlds => worlds.ToArray()).ToArray();
        var authored = controls is null ? null : PmmWriter.ToBytes(controls.ExportDocument());

        // With a one-step call every boundary completes its skin, outline and local scene. The
        // batched call may omit those intermediate products but must give each solver identical inputs.
        for (int step = 0; step < steps; step++) expected.StabilizePhysicsAtCurrentFrame(1, Token);
        actual.StabilizePhysicsAtCurrentFrame(steps, Token);

        Assert.Equal(7, actual.CurrentFrame); Assert.All(actual.Actors, actor => Assert.Equal(1, actor.LocalFrame));
        Assert.Equal(expected.Frame.Surface.Snapshot.Vertices.ToArray(), actual.Frame.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(expected.Frame.Outline!.Vertices, actual.Frame.Outline!.Vertices);
        Assert.Equal(expected.Frame.Materials, actual.Frame.Materials);
        foreach (var actor in actors)
            Assert.Equal(expected.GetBoneWorldMatrices(actor.Id), actual.GetBoneWorldMatrices(actor.Id));
        Assert.Equal(expectedFactory.Worlds.Count, actualFactory.Worlds.Count);
        for (int i = 0; i < actualFactory.Worlds.Count; i++)
        {
            var reference = expectedFactory.Worlds[i]; var result = actualFactory.Worlds[i];
            Assert.Equal(steps, result.History.Count); Assert.Equal(reference.History, result.History);
            Assert.Equal(reference.Inner.Initializations, result.Inner.Initializations);
            Assert.Equal(0, result.Impulses);
            Assert.Equal(reference.Inner.Bodies.Select(body => (body.Transform, body.Velocity, body.Kinematic)),
                result.Inner.Bodies.Select(body => (body.Transform, body.Velocity, body.Kinematic)));
        }
        Assert.Equal(beforeVertices, before.Surface.Snapshot.Vertices.ToArray());
        for (int i = 0; i < actors.Length; i++)
            Assert.Equal(beforeWorldCopies[i], beforeWorlds[i]);
        if (authored is not null) Assert.Equal(authored, PmmWriter.ToBytes(actual.PmmControls!.ExportDocument()));
        Assert.False(actual.CanUndoOutsideParent); Assert.False(actual.CanRedoOutsideParent);
    }

    private static ScenePmmControlState Controls(EditableSceneObject[] actors, PmxModel model, bool nonphysicalAttached)
    {
        PmmString S(string value) => new(0, value, []);
        int Parent(int index) => nonphysicalAttached ? index switch { 0 => 2, 2 => 1, _ => -1 } : index < 2 ? index + 1 : -1;
        byte[] linear = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 4).SelectMany(value => value).ToArray();
        var models = actors.Select((_, i) => new PmmModel
        {
            Index = (byte)i, Name = S("actor" + i), Path = S("actor" + i + ".pmx"), BoneNames = [S("root")],
            SelectedMorphIndices = [-1, -1, -1, -1], Visible = true, LastFrame = 1,
            OutsideParentSubjectBoneIndices = Parent(i) >= 0 ? [0] : [],
            OutsideParentStates = Parent(i) >= 0 ? [new(0, 0, int.MaxValue, new(0, Parent(i), 0))] : [],
            InitialBoneKeyframes = [new() { ObjectIndex = 0, Rotation = Quaternion.Identity, Interpolation = linear, PhysicsDisabled = i == 3 }],
            InitialModelKeyframe = new() { Visible = true, OutsideParents = Parent(i) >= 0 ? [new(0, Parent(i), 0)] : [] },
            BoneStates = [new(0, Vector3.Zero, Quaternion.Identity, false, false, 0)]
        }).ToArray();
        var document = new PmmDocument
        {
            AnsiCodePage = 932, PanelStates = new byte[7], OutputWidth = 640, OutputHeight = 480, CameraFov = 30, Models = models,
            Camera = new() { InitialKeyframe = new() { Interpolation = new byte[24], Fov = 30, ParentModelIndex = -1, ParentBoneIndex = -1 } },
            Settings = new() { PhysicsSimulationMode = 1, EdgeColorRgb = [0, 0, 0], UnknownMatrix = new byte[64], SelectionTrailerFlag = 0 },
            Gravity = new() { InitialKeyframe = new() { Acceleration = 1, Direction = -Vector3.UnitY } }
        };
        return ScenePmmControlState.Create(document, actors.Select((actor, i) => new ScenePmmActorSource(actor.Id, (byte)i, model)));
    }

    private sealed class Factory
    {
        internal readonly List<RecordingBackend> Worlds = [];
        internal RecordingBackend Create() { var backend = new RecordingBackend(); Worlds.Add(backend); return backend; }
    }

    private sealed class RecordingBackend : ICharacterPhysicsMutableBackend
    {
        internal readonly TrackingSharedBackend Inner = new();
        internal readonly List<string> History = [];
        internal int Impulses;
        public string Name => "deferred rigid initialization probe";
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms) => Inner.Initialize(definition, transforms);
        public int AddBody(PhysicsBodyDefinition body, Matrix4x4 transform) => Inner.AddBody(body, transform);
        public int AddJoint(PhysicsJointDefinition joint, Matrix4x4 a, Matrix4x4 b) => Inner.AddJoint(joint, a, b);
        public void RemoveBody(int index) => Inner.RemoveBody(index);
        public void RemoveJoint(int index) => Inner.RemoveJoint(index);
        public void ResetBody(int index, Matrix4x4 transform) => Inner.ResetBody(index, transform);
        public void SetBodyKinematic(int index, bool state) => Inner.SetBodyKinematic(index, state);
        public void SetKinematicTarget(int index, Matrix4x4 transform, float seconds) => Inner.SetKinematicTarget(index, transform, seconds);
        public Matrix4x4 GetBodyWorldTransform(int index) => Inner.GetBodyWorldTransform(index);
        public void SetGravity(Vector3 gravity) => Inner.SetGravity(gravity);
        public void ApplyImpulse(int index, Vector3 linear, Vector3 angular) { Impulses++; Inner.ApplyImpulse(index, linear, angular); }
        public void Step(float seconds)
        {
            // Bitwise serialization avoids tolerant comparisons hiding a changed parent, disabled
            // body mode, clock or dynamic history at any individual physical boundary.
            var values = new List<int> { BitConverter.SingleToInt32Bits(seconds),
                BitConverter.SingleToInt32Bits(Inner.Gravity.X), BitConverter.SingleToInt32Bits(Inner.Gravity.Y),
                BitConverter.SingleToInt32Bits(Inner.Gravity.Z) };
            foreach (var body in Inner.Bodies)
            {
                var matrix = body.Transform;
                foreach (float value in new[] { matrix.M11, matrix.M12, matrix.M13, matrix.M14, matrix.M21, matrix.M22, matrix.M23, matrix.M24,
                    matrix.M31, matrix.M32, matrix.M33, matrix.M34, matrix.M41, matrix.M42, matrix.M43, matrix.M44,
                    body.Velocity.X, body.Velocity.Y, body.Velocity.Z }) values.Add(BitConverter.SingleToInt32Bits(value));
                values.Add(body.Kinematic ? 1 : 0); values.Add(body.Removed ? 1 : 0);
            }
            History.Add(string.Join(",", values)); Inner.Step(seconds);
        }
        public void Dispose() => Inner.Dispose();
    }
}
