using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class ModelVisibilityTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static ICharacterPhysicsBackend NoPhysics(EditableSceneObject _) => throw new InvalidOperationException("No bodies.");
    static PmxModel Model(bool physics = false)
    {
        var source = EditedMotionProjectTests.Model();
        return new() { Header = source.Header, Name = source.Name, EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = source.Vertices, Faces = source.Faces, Textures = source.Textures, Materials = source.Materials,
            Bones = source.Bones, Morphs = [], DisplayFrames = [], Joints = [], SoftBodies = [],
            RigidBodies = physics ? [new() { Name = "body", EnglishName = "", BoneIndex = 0, Group = 0, CollisionGroup = 0,
                Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = Vector3.Zero, Rotation = Vector3.Zero,
                Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0,
                Operation = PmxRigidBodyOperation.Dynamic }] : [] };
    }
    static VmdMotion Motion(bool hide = true) => new() {
        BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 20, Translation = new(20, 0, 0) }],
        IkKeyframes = hide ? [new() { Frame = 0, Visible = true }, new() { Frame = 5, Visible = false }, new() { Frame = 10, Visible = true }] : [] };
    static (SceneCompositionEditor Layout, EditableSceneObject Actor) Layout(PmxModel model)
    {
        var layout = new SceneCompositionEditor();
        return (layout, layout.Add("actor", "actor.pmx", ModelRole.Character, EditedMotionProjectTests.Rest(model)));
    }

    [Fact]
    public void HiddenActorHasNoGeometryFlagsTexturesOrOutlineAndReappearsAtCurrentPose()
    {
        var model = Model(); var (layout, actor) = Layout(model);
        using var session = new SceneAnimationSession(layout.Objects, [new(actor.Id, model, Motion())], NoPhysics, cancellationToken: Token);
        var original = session.Frame; session.Seek(5, Token); var hidden = session.Frame;
        Assert.Empty(hidden.Surface.Snapshot.Vertices.ToArray()); Assert.Empty(hidden.Surface.Snapshot.Indices.ToArray());
        Assert.Empty(hidden.Surface.Materials); Assert.Empty(hidden.Materials); Assert.Empty(hidden.Surface.Snapshot.Textures);
        Assert.NotNull(hidden.Outline); Assert.Empty(hidden.Outline.Vertices); Assert.Empty(hidden.Outline.Materials);
        Assert.Equal(5, session.CurrentFrame); Assert.Equal(5, session.Actors[0].LocalFrame);
        Assert.False(session.Actors[0].Visible); Assert.True(session.Actors[0].IkEnabled);
        Assert.InRange(Vector3.Distance(new(5, 0, 0), session.GetBoneWorldMatrices(actor.Id)[0].Translation), 0, 1e-4f);
        Assert.Same(hidden, session.Advance(0, Token));
        session.Seek(10, Token);
        Assert.True(session.Actors[0].Visible);
        Assert.Equal(3, session.Frame.Surface.Snapshot.Vertices.Length);
        Assert.InRange(Vector3.Distance(new(10, 0, 0), session.Frame.Surface.Snapshot.Vertices[0].Position), 0, 1e-4f);
        Assert.Equal(3, original.Surface.Snapshot.Vertices.Length); Assert.Empty(hidden.Surface.Snapshot.Vertices.ToArray());
    }

    [Fact]
    public void VisibleSubsetChangesRebuildTopologyInsteadOfReusingTheWrongActor()
    {
        var model = Model(); var (layout, actor) = Layout(model);
        var second = layout.Add("second", "second.pmx", ModelRole.Character, EditedMotionProjectTests.Rest(model));
        layout.SetTransform(second.Id, new(100, 0, 0), Quaternion.Identity, 1);
        var alternate = new VmdMotion { BoneKeyframes = Motion(false).BoneKeyframes,
            IkKeyframes = [new() { Frame = 0, Visible = false }, new() { Frame = 5, Visible = true }, new() { Frame = 10, Visible = false }] };
        using var session = new SceneAnimationSession(layout.Objects,
            [new(actor.Id, model, Motion()), new(second.Id, model, alternate)], NoPhysics, cancellationToken: Token);
        Assert.Equal(3, session.Frame.Surface.Snapshot.Vertices.Length);
        session.Seek(5, Token); Assert.Equal(3, session.Frame.Surface.Snapshot.Vertices.Length);
        Assert.InRange(Vector3.Distance(new(105, 0, 0), session.Frame.Surface.Snapshot.Vertices[0].Position), 0, 1e-4f);
        Assert.Equal(new uint[] { 0, 1, 2 }, session.Frame.Surface.Snapshot.Indices.ToArray());
        session.Seek(10, Token); Assert.InRange(session.Frame.Surface.Snapshot.Vertices[0].Position.X, 9.9999f, 10.0001f);
    }

    [Fact]
    public void HiddenPhysicsContinuesAndReshowDoesNotResetBackend()
    {
        var model = Model(true); var (layout, actor) = Layout(model);
        var backend = new SceneAnimationSessionTests.FallingBackend();
        using var session = new SceneAnimationSession(layout.Objects, [new(actor.Id, model, Motion())], _ => backend, cancellationToken: Token);
        // Use real frame increments: a single five-frame jump intentionally hits MaximumSubsteps=10.
        for (int i = 0; i < 5; i++) session.Advance(1.0 / 30, Token);
        Assert.Equal(20, backend.Steps); Assert.Empty(session.Frame.Surface.Snapshot.Indices.ToArray());
        for (int i = 0; i < 5; i++) session.Advance(1.0 / 30, Token);
        Assert.Equal(40, backend.Steps);
        Assert.Equal(3, session.Frame.Surface.Snapshot.Indices.Length); Assert.Equal(0, backend.DisposeCount);
        Assert.InRange(session.GetBoneWorldMatrices(actor.Id)[0].Translation.Y, -1f / 3 - 1e-5f, -1f / 3 + 1e-5f);
    }

    [Fact]
    public void VisibilityOnlyMotionIsPlayableAndUsesFirstKeyClampThenStepHold()
    {
        var model = Model(); var (layout, actor) = Layout(model);
        var motion = new VmdMotion { IkKeyframes = [new() { Frame = 3, Visible = false }, new() { Frame = 8, Visible = true }] };
        using var session = new SceneAnimationSession(layout.Objects, [new(actor.Id, model, motion)], NoPhysics, cancellationToken: Token);
        Assert.Empty(session.Frame.Surface.Snapshot.Indices.ToArray()); Assert.Equal(8u, session.DurationFrames);
        session.Seek(7, Token); Assert.Empty(session.Frame.Surface.Snapshot.Indices.ToArray());
        session.Seek(8, Token); Assert.Equal(3, session.Frame.Surface.Snapshot.Indices.Length);
        session.Seek(0, Token); Assert.Empty(session.Frame.Surface.Snapshot.Indices.ToArray());
    }

    [Fact]
    public void ClearingHiddenMotionRestoresStaticObjectWithoutChangingOriginalSource()
    {
        var model = Model(); var (layout, actor) = Layout(model);
        using var session = new SceneAnimationSession(layout.Objects, [new(actor.Id, model, Motion())], NoPhysics, cancellationToken: Token);
        session.Seek(6, Token); Assert.Empty(session.Frame.Surface.Snapshot.Indices.ToArray());
        session.ClearMotion(actor.Id);
        Assert.Equal(3, session.Frame.Surface.Snapshot.Indices.Length); Assert.False(session.Actors[0].HasMotion);
        Assert.Equal(Vector3.Zero, session.Frame.Surface.Snapshot.Vertices[0].Position);
        Assert.Equal(3, actor.Source.Surface.Snapshot.Indices.Length);
    }

    [Theory]
    [InlineData(.25)]
    [InlineData(.5)]
    public void NonphysicalSamplingAndAdvancePreserveFractionalBoneTime(double frame)
    {
        var model = Model(); var rest = EditedMotionProjectTests.Rest(model);
        using var controller = new CharacterAnimationController(model, Motion(false), rest.Surface, rest.Outline!);
        var pose = controller.SampleAt(frame);
        Assert.InRange(pose.Surface.Snapshot.Vertices[0].Position.X, (float)frame - 1e-4f, (float)frame + 1e-4f);
        controller.SampleAt(0);
        pose = controller.Advance(frame, frame / 30);
        Assert.InRange(pose.Surface.Snapshot.Vertices[0].Position.X, (float)frame - 1e-4f, (float)frame + 1e-4f);
        var (layout, actor) = Layout(model);
        using var session = new SceneAnimationSession(layout.Objects, [new(actor.Id, model, Motion(false))], NoPhysics, cancellationToken: Token);
        session.Advance(frame / 30, Token);
        Assert.InRange(session.Frame.Surface.Snapshot.Vertices[0].Position.X, (float)frame - 1e-4f, (float)frame + 1e-4f);
        Assert.Equal(frame, session.CurrentFrame);
    }

    [Fact]
    public void VisibilityTrackIsDiscreteLastWinsAndRetainsSnapshotWhenSourceListChanges()
    {
        var source = new List<VmdIkKeyframe> { new() { Frame = 10, Visible = false }, new() { Frame = 5, Visible = true },
            new() { Frame = 10, Visible = true }, new() { Frame = 20, Visible = false } };
        var track = new MmdModelVisibilityTrack(new() { IkKeyframes = source }); source.Clear();
        Assert.True(track.HasTrack); Assert.True(track.Evaluate(0)); Assert.True(track.Evaluate(9.99));
        Assert.True(track.Evaluate(10)); Assert.False(track.Evaluate(20)); Assert.False(track.Evaluate(uint.MaxValue));
        Assert.True(new MmdModelVisibilityTrack(new()).Evaluate(100));
        foreach (double invalid in new[] { -1, double.NaN, double.PositiveInfinity, (double)uint.MaxValue + 1 })
            Assert.Throws<ArgumentOutOfRangeException>(() => track.Evaluate(invalid));
    }

    [Fact]
    public void ExplicitEmptySnapshotStaysValidWithoutWeakeningOrdinaryMeshValidation()
    {
        var camera = SceneCamera.Default with { Eye = new(5, 6, -20) };
        var empty = SceneSnapshot.CreateEmpty(camera, Vector3.UnitY);
        Assert.True(empty.IsEmpty); Assert.Empty(empty.Indices.ToArray()); Assert.Empty(empty.Materials);
        Assert.Equal(camera, empty.Camera); Assert.Equal(Vector3.UnitY, empty.LightDirection);
        var updated = empty.WithDeformedVertices([]).WithMaterials([]).WithLighting(Vector3.UnitX).WithCamera(SceneCamera.Default);
        Assert.True(updated.IsEmpty); Assert.True(updated.HasDynamicVertices);
        var frame = new OriginalFrameScene(new(empty, []), OriginalOutlineData.Empty,
            new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, -10), 30, 1));
        Assert.Empty(frame.Materials); Assert.Empty(frame.Outline!.Vertices);
        Assert.Throws<ArgumentException>(() => new SceneSnapshot([], [], []));
        Assert.Throws<ArgumentException>(() => new OriginalOutlineData([], []));
        Assert.Throws<ArgumentException>(() => SceneSnapshot.CreateEmpty(lightDirection: Vector3.Zero));
        Assert.Throws<ArgumentException>(() => SceneSnapshot.CreateEmpty(camera with { Target = camera.Eye }));
    }

    [Fact]
    public void ControllerGravityChangeForwardsWithoutResettingPoseOrSolver()
    {
        var model = Model(true); var rest = EditedMotionProjectTests.Rest(model);
        using var controller = new CharacterAnimationController(model, Motion(false), rest.Surface, rest.Outline!);
        Assert.Throws<InvalidOperationException>(() => controller.SetGravity(Vector3.UnitY));
        var backend = new GravityBackend(); controller.EnablePhysicsAt(backend, 0);
        controller.Advance(1, 1.0 / 30); int initializations = backend.Initializations, steps = backend.Inner.Steps;
        var pose = controller.BoneWorldMatrices;
        controller.SetGravity(new(1, -2, 3));
        Assert.Equal(new(1, -2, 3), backend.Gravity); Assert.Equal(initializations, backend.Initializations);
        Assert.Same(pose, controller.BoneWorldMatrices); Assert.Equal(steps, backend.Inner.Steps);
        controller.Advance(2, 1.0 / 30); Assert.Equal(steps + 4, backend.Inner.Steps);
    }

    sealed class GravityBackend : ICharacterPhysicsBackend, ICharacterGravityBackend
    {
        public readonly SceneAnimationSessionTests.FallingBackend Inner = new();
        public int Initializations; public Vector3 Gravity;
        public string Name => "gravity forwarding fixture";
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> input)
        { Initializations++; Inner.Initialize(definition, input); }
        public void SetKinematicTarget(int index, Matrix4x4 world, float seconds) => Inner.SetKinematicTarget(index, world, seconds);
        public Matrix4x4 GetBodyWorldTransform(int index) => Inner.GetBodyWorldTransform(index);
        public void Step(float seconds) => Inner.Step(seconds);
        public void SetGravity(Vector3 acceleration) => Gravity = acceleration;
        public void Dispose() => Inner.Dispose();
    }
}
