using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SharedPhysicsPoseStabilityTests
{
    [Fact]
    public void RejectedSharedSampleReportsTheExactActorBoneMatrixWithoutAdvancingTheWorld()
    {
        var backend = new TrackingSharedBackend { Gravity = Vector3.Zero };
        using var world = new SharedCharacterPhysicsSession(backend);
        var id = Guid.NewGuid(); var valid = RotatedPose();
        world.AddActor(id, Definition(valid, PhysicsBodyMode.Kinematic), Matrix4x4.Identity, valid);
        var rejected = valid.ToArray(); rejected[5] = Matrix4x4.CreateScale(1.01f, 1, 1);
        var failure = Assert.Throws<ArgumentException>(() => world.Step([new(id, rejected) { Frozen = true }]));
        Assert.Equal("samples", failure.ParamName);
        Assert.Equal(id, failure.Data["Physics.ActorId"]); Assert.Equal(5, failure.Data["Physics.BoneIndex"]);
        Assert.Equal(true, failure.Data["Physics.Frozen"]); Assert.Equal(0L, failure.Data["Physics.CompletedWorldSteps"]);
        Assert.Equal(rejected[5], failure.Data["Physics.RejectedBoneWorld"]);
        Assert.Equal(0, world.StepCount); Assert.False(world.IsFaulted);
        Assert.Equal(valid, world.Step([new(id, valid) { Frozen = true }])[id]);
    }

    [Fact]
    public void RepeatedFrozenSharedTicksPreserveAStoppedRotatedHierarchyWhileAnotherActorAdvances()
    {
        var backend = new TrackingSharedBackend { Gravity = new(0, -1, 0) };
        using var world = new SharedCharacterPhysicsSession(backend);
        var frozenId = Guid.NewGuid(); var movingId = Guid.NewGuid();
        var original = RotatedPose();
        var frozen = original.ToArray();
        world.AddActor(frozenId, Definition(original, PhysicsBodyMode.Kinematic), Matrix4x4.Identity, frozen);
        world.AddActor(movingId, Definition([Matrix4x4.Identity], PhysicsBodyMode.Dynamic), Matrix4x4.Identity, [Matrix4x4.Identity]);
        Matrix4x4[] moving = [Matrix4x4.Identity];
        for (int tick = 0; tick < 1000; tick++)
        {
            var failure = Record.Exception(() =>
            {
                var result = world.Step([new(frozenId, frozen) { Frozen = true }, new(movingId, [Matrix4x4.Identity])]);
                frozen = result[frozenId]; moving = result[movingId];
            });
            Assert.True(failure is null, $"Frozen pose drifted at shared tick {tick}; the uncorrected inverse/product feedback fails near tick 863: {failure}");
        }
        Assert.Equal(MemoryMarshal.AsBytes(original.AsSpan()).ToArray(), MemoryMarshal.AsBytes(frozen.AsSpan()).ToArray());
        Assert.True(moving[0].Translation.Y < -10, "The other actor must keep advancing during the frozen hierarchy test.");
        Assert.Equal(1000, world.StepCount); Assert.False(world.IsFaulted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrozenDynamicBodiesRetainTheCompletedPoseWithRotatedPlacement(bool unboundBody)
    {
        var backend = new TrackingSharedBackend { Gravity = Vector3.Zero };
        using var world = new SharedCharacterPhysicsSession(backend);
        var id = Guid.NewGuid(); var original = RotatedPose(); var pose = original.ToArray();
        var definition = Definition(original, PhysicsBodyMode.Dynamic);
        var body = definition.Bodies[0] with { Position = new(1.2f, 3.4f, -2), Rotation = new(.2f, -.5f, .7f) };
        definition = new(definition.Bones, unboundBody ? [body, body with { Name = "unbound", BoneIndex = -1 }] : [body], []);
        var placement = Matrix4x4.CreateFromYawPitchRoll(.4f, -.8f, .23f) * Matrix4x4.CreateTranslation(11, -2, 3);
        world.AddActor(id, definition, placement, pose);
        var unboundWorld = unboundBody ? backend.Bodies[1].Transform : Matrix4x4.Identity;
        for (int tick = 0; tick < 1000; tick++)
            pose = world.Step([new(id, pose) { Frozen = true }])[id];
        Assert.Equal(MemoryMarshal.AsBytes(original.AsSpan()).ToArray(), MemoryMarshal.AsBytes(pose.AsSpan()).ToArray());
        if (unboundBody) Assert.Equal(unboundWorld, backend.Bodies[1].Transform);
        Assert.Equal(1000, world.StepCount); Assert.False(world.IsFaulted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AChangedDrivenRootStillPropagatesThroughRotatedDescendants(bool disabledChild)
    {
        var backend = new TrackingSharedBackend { Gravity = Vector3.Zero };
        using var world = new SharedCharacterPhysicsSession(backend);
        var id = Guid.NewGuid(); var pose = RotatedPose();
        var definition = Definition(pose, PhysicsBodyMode.Dynamic);
        if (disabledChild)
            definition = new(definition.Bones,
                [definition.Bodies[0], definition.Bodies[0] with { Name = "disabled child", BoneIndex = 1 }], []);
        world.AddActor(id, definition, Matrix4x4.Identity, pose);
        var moved = backend.Bodies[0].Transform * Matrix4x4.CreateRotationY(.37f) * Matrix4x4.CreateTranslation(3, 2, -1);
        backend.Bodies[0].Transform = moved;
        var sample = new SharedPhysicsActorSample(id, pose)
        { DisabledBoneIndices = disabledChild ? new HashSet<int> { 1 } : new HashSet<int>() };
        var result = world.Reflect([sample])[id];
        Matrix4x4.Invert(definition.BodyBoneOffsets[0], out var inverseOffset);
        var expected = Affine(inverseOffset * moved);
        AssertMatrixNear(expected, result[0]);
        Assert.True(Vector3.Distance(pose[0].Translation, result[0].Translation) > 1);
        for (int bone = 1; bone < pose.Length; bone++)
        {
            Matrix4x4.Invert(pose[bone - 1], out var inverseParent);
            expected = Affine(Affine(pose[bone] * Affine(inverseParent)) * expected);
            AssertMatrixNear(expected, result[bone]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnchangedParentDoesNotHideCorruptDrivenBodyOutput(bool frozen)
    {
        var backend = new TrackingSharedBackend();
        using var world = new SharedCharacterPhysicsSession(backend);
        var id = Guid.NewGuid();
        world.AddActor(id, Definition([Matrix4x4.Identity], PhysicsBodyMode.Dynamic), Matrix4x4.Identity, [Matrix4x4.Identity]);
        backend.Bodies[0].Transform = Matrix4x4.CreateScale(1.01f, 1, 1);
        Assert.Throws<ArgumentException>(() => world.Reflect([new(id, [Matrix4x4.Identity]) { Frozen = frozen }]));
        Assert.True(world.IsFaulted);
    }

    private static Matrix4x4[] RotatedPose() => Enumerable.Range(0, 8).Select(i =>
        Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(
            .17f + i * .13f, -.23f + i * .07f, .31f + i * .09f)))
        * Matrix4x4.CreateTranslation(2 + i * .2f, 10 + i * .3f, -100 + i * .1f)).ToArray();

    private static CharacterPhysicsDefinition Definition(Matrix4x4[] pose, PhysicsBodyMode mode) =>
        new(pose.Select((matrix, bone) => new PhysicsBoneDefinition(bone - 1, matrix)),
            [new("root body", 0, PhysicsShapeKind.Sphere, new(.1f), Vector3.Zero, Vector3.Zero,
                1, 0, 0, 0, .5f, 0, 0, mode)], []);

    private static Matrix4x4 Affine(Matrix4x4 matrix)
    { matrix.M14 = matrix.M24 = matrix.M34 = 0; matrix.M44 = 1; return matrix; }

    private static void AssertMatrixNear(Matrix4x4 expected, Matrix4x4 actual)
    {
        Assert.InRange(Vector3.Distance(expected.Translation, actual.Translation), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new(expected.M11, expected.M12, expected.M13), new(actual.M11, actual.M12, actual.M13)), 0, 1e-6f);
        Assert.InRange(Vector3.Distance(new(expected.M21, expected.M22, expected.M23), new(actual.M21, actual.M22, actual.M23)), 0, 1e-6f);
        Assert.InRange(Vector3.Distance(new(expected.M31, expected.M32, expected.M33), new(actual.M31, actual.M32, actual.M33)), 0, 1e-6f);
        Assert.Equal(0, actual.M14); Assert.Equal(0, actual.M24); Assert.Equal(0, actual.M34); Assert.Equal(1, actual.M44);
    }
}
