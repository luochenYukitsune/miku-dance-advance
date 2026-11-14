using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class BackendCoreAuditTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void PlainRigRejectsInvalidIkOverridesInBothSamplingPaths(int index)
    {
        var animator = new CharacterAnimator(new([new("root", Vector3.Zero, -1)], [], []), new([]));
        var options = new CharacterEvaluationOptions(IkEnabledOverrides: new Dictionary<int, bool> { [index] = false });
        Assert.Throws<ArgumentException>(() => animator.Sample(0, options));
        Assert.Throws<ArgumentException>(() => animator.BeginPhysicsSample(0, options));
        Assert.Equal(Matrix4x4.Identity, animator.Sample(0).BoneWorldMatrices[0]);
    }

    [Fact]
    public void AllSceneLightEntryPointsRejectDirectionsWhoseNormalizationOverflows()
    {
        var huge = new Vector3(float.MaxValue, 1, 0);
        Assert.Throws<ArgumentException>(() => SceneSnapshot.CreateEmpty(lightDirection: huge));
        Assert.Throws<ArgumentException>(() => SceneSnapshot.CreateEmpty().WithLighting(huge));
        Assert.Throws<ArgumentException>(() => Triangle(huge));
        Assert.Equal(new Vector3(0, 2, 0), Triangle(new(0, 2, 0)).LightDirection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedReflectionRejectsCorruptBackendAffineColumnAndFaultsWorld(bool nonfinite)
    {
        var backend = new CorruptPoseBackend();
        using var world = new SharedCharacterPhysicsSession(backend);
        var actor = Guid.NewGuid();
        var body = new PhysicsBodyDefinition("body", 0, PhysicsShapeKind.Sphere, new(.5f), Vector3.Zero,
            Vector3.Zero, 1, 0, 0, 0, 0, 0, 0, PhysicsBodyMode.Dynamic);
        world.AddActor(actor, new([new(-1, Matrix4x4.Identity)], [body], []), Matrix4x4.Identity, [Matrix4x4.Identity]);
        backend.CorruptColumn = nonfinite ? float.NaN : 2;
        SharedPhysicsActorSample[] samples = [new(actor, [Matrix4x4.Identity])];

        Assert.Throws<ArgumentException>(() => world.Reflect(samples));
        Assert.True(world.IsFaulted);
        Assert.Throws<InvalidOperationException>(() => world.Step(samples));
        backend.CorruptColumn = 0;
        world.ResetAll(samples);
        Assert.False(world.IsFaulted);
        Assert.Equal(Matrix4x4.Identity, world.Reflect(samples)[actor][0]);
    }

    private static SceneSnapshot Triangle(Vector3 light) => new(
        [new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero), new(Vector3.UnitX, Vector3.UnitZ, Vector2.Zero),
            new(Vector3.UnitY, Vector3.UnitZ, Vector2.Zero)], [0, 1, 2],
        [new("triangle", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 0)], lightDirection: light);

    // A fault boundary double; numerical physics behavior is covered by the existing Bepu/Bullet tests.
    private sealed class CorruptPoseBackend : ICharacterPhysicsMutableBackend
    {
        private readonly Dictionary<int, Matrix4x4> poses = [];
        private int next;
        public float CorruptColumn { get; set; }
        public string Name => "corrupt-pose audit backend";
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms)
        { poses.Clear(); next = 0; }
        public int AddBody(PhysicsBodyDefinition body, Matrix4x4 transform)
        { int slot = next++; poses.Add(slot, transform); return slot; }
        public void RemoveBody(int bodyIndex) => poses.Remove(bodyIndex);
        public int AddJoint(PhysicsJointDefinition joint, Matrix4x4 localFrameA, Matrix4x4 localFrameB) => throw new NotSupportedException();
        public void RemoveJoint(int jointIndex) => throw new NotSupportedException();
        public Matrix4x4 GetBodyWorldTransform(int bodyIndex)
        { var pose = poses[bodyIndex]; pose.M14 = CorruptColumn; return pose; }
        public void ResetBody(int bodyIndex, Matrix4x4 transform) => poses[bodyIndex] = transform;
        public void SetKinematicTarget(int bodyIndex, Matrix4x4 transform, float seconds) => ResetBody(bodyIndex, transform);
        public void SetBodyKinematic(int bodyIndex, bool kinematic) { }
        public void ApplyImpulse(int bodyIndex, Vector3 linear, Vector3 angular) { }
        public void SetGravity(Vector3 gravity) { }
        public void Step(float seconds) { }
        public void Dispose() => poses.Clear();
    }
}
