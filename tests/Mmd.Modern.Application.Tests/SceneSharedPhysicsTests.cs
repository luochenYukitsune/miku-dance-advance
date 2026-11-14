using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneSharedPhysicsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public void TwoActorsUseOneWorldAndOneStepPerPhysicalTick()
    {
        var model = SharedPhysicsFixture.Model(); var (layout, a, b) = SharedPhysicsFixture.Layout(model);
        var backend = new TrackingSharedBackend(); int factories = 0;
        using var session = new SceneAnimationSession(layout.Objects,
            [new(a.Id, model, SharedPhysicsFixture.Motion(30)), new(b.Id, model, SharedPhysicsFixture.Motion(30))],
            _ => throw new InvalidOperationException("Independent worlds must not be allocated."),
            sharedPhysicsFactory: () => { factories++; return backend; }, cancellationToken: Token);
        Assert.True(session.UsesSharedPhysicsWorld); Assert.Equal(1, factories); Assert.Equal(1, backend.Initializations);
        Assert.Equal(2, backend.ActiveCount);
        session.Advance(1d / 120, Token);
        Assert.Equal(1, session.SharedPhysicsStepCount); Assert.Equal(1, backend.Steps);
        Assert.All(session.Actors, actor => { Assert.True(actor.PhysicsEnabled); Assert.Contains("shared world", actor.PhysicsBackend!); });
        session.Advance(1d / 120, Token);
        Assert.Equal(2, backend.Steps);
    }

    [Fact]
    public void RemovingAndReplacingActorPreservesOtherBodyMomentumAndWorld()
    {
        var model = SharedPhysicsFixture.Model(); var (layout, a, b) = SharedPhysicsFixture.Layout(model);
        var backend = new TrackingSharedBackend();
        using var session = SharedPhysicsFixture.Session(layout, a, b, model, backend);
        session.Advance(1d / 30, Token); var position = backend.Bodies[1].Transform; var velocity = backend.Bodies[1].Velocity;
        session.ClearMotion(a.Id);
        Assert.Equal(1, backend.ActiveCount); Assert.Equal(position, backend.Bodies[1].Transform);
        Assert.Equal(velocity, backend.Bodies[1].Velocity); Assert.Equal(1, backend.Initializations);
        session.BindMotion(new(a.Id, model, SharedPhysicsFixture.Motion(60)), Token);
        session.BindMotion(new(a.Id, model, SharedPhysicsFixture.Motion(90)), Token);
        Assert.Equal(2, backend.ActiveCount); Assert.Equal(position, backend.Bodies[1].Transform);
        Assert.Equal(velocity, backend.Bodies[1].Velocity); Assert.Equal(1, backend.Initializations);
        session.Advance(1d / 120, Token);
        Assert.True(backend.Bodies[1].Transform.M42 < position.M42);
    }

    [Fact]
    public void ShortActorFreezesWhileLongActorContinuesInSameSolver()
    {
        var model = SharedPhysicsFixture.Model(); var (layout, a, b) = SharedPhysicsFixture.Layout(model);
        var backend = new TrackingSharedBackend();
        using var session = SharedPhysicsFixture.Session(layout, a, b, model, backend, 1, 30);
        session.Advance(1d / 30, Token); var stopped = session.Frame.Surface.Snapshot.Vertices[0].Position;
        for (int i = 0; i < 8; i++) session.Advance(1d / 120, Token);
        Assert.Equal(stopped, session.Frame.Surface.Snapshot.Vertices[0].Position);
        Assert.True(backend.Bodies[0].Kinematic); Assert.False(backend.Bodies[1].Kinematic);
        Assert.True(session.Frame.Surface.Snapshot.Vertices[3].Position.Y < stopped.Y);
        Assert.Equal(1, session.Actors[0].LocalFrame); Assert.Equal(3, session.Actors[1].LocalFrame);
    }

    [Fact]
    public void FrozenChildStillFollowsMovingNonphysicalParentWithoutOneTickLag()
    {
        var child = SharedPhysicsFixture.Model(PmxRigidBodyOperation.DynamicAndBoneMerge);
        var parent = SharedPhysicsFixture.Model(physics: false); var layout = new SceneCompositionEditor();
        var a = layout.Add("child", "child.pmx", ModelRole.Character, SharedPhysicsFixture.Rest(child));
        var b = layout.Add("parent", "parent.pmx", ModelRole.Character, SharedPhysicsFixture.Rest(parent));
        using var session = new SceneAnimationSession(layout.Objects,
            [new(a.Id, child, SharedPhysicsFixture.Motion(1)), new(b.Id, parent, SharedPhysicsFixture.Motion(30, new(30, 0, 0)))],
            _ => throw new InvalidOperationException(), outsideParents: [new(a.Id, -1, b.Id, 0, Matrix4x4.Identity)],
            sharedPhysicsFactory: () => new TrackingSharedBackend(), cancellationToken: Token);
        session.Advance(1d / 30, Token); session.Advance(1d / 30, Token); session.Advance(1d / 30, Token);
        Assert.InRange(MathF.Abs(session.GetBoneWorldMatrices(a.Id)[0].M41 - 3), 0, 1e-4f);
        Assert.InRange(MathF.Abs(session.GetBoneWorldMatrices(b.Id)[0].M41 - 3), 0, 1e-4f);
    }

    [Fact]
    public void SeekRestartsAllBodiesAndReplaysSameTrajectory()
    {
        var model = SharedPhysicsFixture.Model(); var (layout, a, b) = SharedPhysicsFixture.Layout(model);
        var backend = new TrackingSharedBackend();
        using var session = SharedPhysicsFixture.Session(layout, a, b, model, backend);
        for (int i = 0; i < 8; i++) session.Advance(1d / 120, Token);
        var expected = session.Frame.Surface.Snapshot.Vertices.ToArray();
        session.Seek(0, Token); Assert.Equal(0, session.SharedPhysicsStepCount);
        for (int i = 0; i < 8; i++) session.Advance(1d / 120, Token);
        Assert.Equal(expected, session.Frame.Surface.Snapshot.Vertices.ToArray());
    }

    [Fact]
    public void RealBulletResolvesContactsBetweenDifferentSceneActors()
    {
        var model = SharedPhysicsFixture.Model(); var (layout, a, b) = SharedPhysicsFixture.Layout(model, .3f);
        var backend = new BulletCharacterPhysicsBackend(Vector3.Zero, null);
        using var session = new SceneAnimationSession(layout.Objects,
            [new(a.Id, model, SharedPhysicsFixture.Motion(30)), new(b.Id, model, SharedPhysicsFixture.Motion(30))],
            _ => throw new InvalidOperationException(), sharedPhysicsFactory: () => backend, cancellationToken: Token);
        for (int i = 0; i < 12; i++) session.Advance(1d / 120, Token);
        float distance = Vector3.Distance(session.Frame.Surface.Snapshot.Vertices[0].Position,
            session.Frame.Surface.Snapshot.Vertices[3].Position);
        Assert.InRange(distance, .35f, .7f);
    }
}

internal static class SharedPhysicsFixture
{
    internal static VmdMotion Motion(uint end, Vector3 movement = default) => new() { BoneKeyframes =
        [new() { BoneName = "root", Frame = 0 }, new() { BoneName = "root", Frame = end, Translation = movement }] };
    internal static (SceneCompositionEditor Layout, EditableSceneObject A, EditableSceneObject B) Layout(PmxModel model, float spacing = 20)
    {
        var layout = new SceneCompositionEditor(); var rest = Rest(model);
        var a = layout.Add("a", "a.pmx", ModelRole.Character, rest);
        var b = layout.Add("b", "b.pmx", ModelRole.Character, rest);
        layout.SetTransform(b.Id, new(spacing, 0, 0), Quaternion.Identity, 1);
        return (layout, a, layout.Objects[1]);
    }
    internal static SceneAnimationSession Session(SceneCompositionEditor layout, EditableSceneObject a, EditableSceneObject b,
        PmxModel model, TrackingSharedBackend backend, uint shortEnd = 30, uint longEnd = 30) =>
        new(layout.Objects, [new(a.Id, model, Motion(shortEnd)), new(b.Id, model, Motion(longEnd))],
            _ => throw new InvalidOperationException(), sharedPhysicsFactory: () => backend,
            cancellationToken: TestContext.Current.CancellationToken);
    internal static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, 10), 30, 1),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));
    internal static PmxModel Model(PmxRigidBodyOperation operation = PmxRigidBodyOperation.Dynamic, bool physics = true, bool precedingBone = false) => new()
    {
        Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
            VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = "shared fixture", EnglishName = "", Comment = "", EnglishComment = "", Textures = [], Faces = [new(0, 1, 2)],
        Materials = [new() { Name = "triangle", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
            Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, EdgeColor = Vector4.Zero, EdgeSize = 0, TextureIndex = -1,
            SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
        Bones = precedingBone ? [Bone("unused"), Bone("root")] : [Bone("root")],
        Morphs = [], DisplayFrames = [], Joints = [], SoftBodies = [],
        RigidBodies = physics ? [new() { Name = "sphere", EnglishName = "", BoneIndex = precedingBone ? 1 : 0, Group = 0, CollisionGroup = ushort.MaxValue,
            Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = new(0, 2, 0), Rotation = Vector3.Zero,
            Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = operation }] : [],
        Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(position => new PmxVertex
        { Position = position, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
            BoneIndices = [precedingBone ? 1 : 0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray()
    };
    private static PmxBone Bone(string name) => new() { Name = name, EnglishName = "", Position = Vector3.Zero,
        ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None };
}

/// <summary>Deterministic orchestration probe; only the separate real Bullet case asserts collision behavior.</summary>
internal sealed class TrackingSharedBackend : ICharacterPhysicsMutableBackend
{
    internal sealed class Body(PhysicsBodyDefinition definition, Matrix4x4 transform)
    { internal readonly PhysicsBodyDefinition Definition = definition; internal Matrix4x4 Transform = transform;
      internal Vector3 Velocity; internal bool Kinematic = definition.Mode == PhysicsBodyMode.Kinematic; internal bool Removed; }
    internal readonly List<Body> Bodies = [];
    internal int Initializations, Steps;
    internal int ActiveCount => Bodies.Count(body => !body.Removed);
    internal Vector3 Gravity = new(0, -10, 0);
    public string Name => "tracking shared backend";
    public IReadOnlyList<string> Diagnostics => [];
    public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms)
    { Initializations++; Bodies.Clear(); for (int i = 0; i < definition.Bodies.Count; i++) AddBody(definition.Bodies[i], transforms[i]); }
    public int AddBody(PhysicsBodyDefinition body, Matrix4x4 transform) { Bodies.Add(new(body, transform)); return Bodies.Count - 1; }
    public int AddJoint(PhysicsJointDefinition joint, Matrix4x4 a, Matrix4x4 b) => throw new NotSupportedException();
    public void RemoveJoint(int index) => throw new NotSupportedException();
    public void RemoveBody(int index) => Bodies[index].Removed = true;
    public void ResetBody(int index, Matrix4x4 transform) { Bodies[index].Transform = transform; Bodies[index].Velocity = default; }
    public void SetBodyKinematic(int index, bool kinematic)
    {
        var body = Bodies[index]; kinematic |= body.Definition.Mode == PhysicsBodyMode.Kinematic;
        if (body.Kinematic != kinematic) body.Velocity = default;
        body.Kinematic = kinematic;
    }
    public void SetKinematicTarget(int index, Matrix4x4 transform, float seconds) => Bodies[index].Transform = transform;
    public Matrix4x4 GetBodyWorldTransform(int index) => Bodies[index].Removed ? throw new ArgumentOutOfRangeException(nameof(index)) : Bodies[index].Transform;
    public void SetGravity(Vector3 gravity) => Gravity = gravity;
    public void ApplyImpulse(int index, Vector3 linear, Vector3 angular) { if (!Bodies[index].Kinematic) Bodies[index].Velocity += linear / Bodies[index].Definition.Mass; }
    public void Step(float seconds)
    {
        Steps++;
        foreach (var body in Bodies.Where(body => !body.Removed && !body.Kinematic))
        { body.Velocity += Gravity * seconds; var pose = body.Transform; pose.Translation += body.Velocity * seconds; body.Transform = pose; }
    }
    public void Dispose() { }
}
