using System.Numerics;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bepu;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class CharacterPhysicsTests
{
    [Fact]
    public void RawPmxBodyRotationUsesZThenXThenYAndOffsetRoundTrips()
    {
        var bone = Matrix4x4.CreateRotationY(.7f) * Matrix4x4.CreateTranslation(2, 3, 4);
        var body = Body(PhysicsBodyMode.Kinematic, new Vector3(4, 5, 6)) with { Rotation = new(.2f, .3f, .4f) };
        var definition = Definition([new(-1, bone)], [body]);
        var expected = Matrix4x4.CreateRotationZ(.4f) * Matrix4x4.CreateRotationX(.2f) * Matrix4x4.CreateRotationY(.3f)
            * Matrix4x4.CreateTranslation(4, 5, 6);
        Near(expected, definition.BodyRestWorldTransforms[0]);
        Near(expected, definition.BodyBoneOffsets[0] * bone);
    }

    [Fact]
    public void KinematicBodyFollowsAnimatedBoneWithRotatedOffsetWithoutMutatingInput()
    {
        using var session = Session(Definition([new(-1, Matrix4x4.Identity)], [Body(PhysicsBodyMode.Kinematic, Vector3.UnitX)]));
        Matrix4x4[] input = [Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(3, 4, 0)];
        var copy = input[0];
        var result = session.Advance(1.0 / 30, input);
        Near(new Vector3(3, 5, 0), session.GetBodyWorldTransform(0).Translation);
        Assert.Equal(copy, input[0]);
        Assert.Equal(copy, result[0]);
        Assert.Equal(4, session.LastStepCount);
    }

    [Fact]
    public void KinematicLargeRotationHitsAuthoredPoseAtTheStepBoundary()
    {
        using var session = Session(Definition([new(-1, Matrix4x4.Identity)], [Body(PhysicsBodyMode.Kinematic, Vector3.Zero)]));
        Matrix4x4[] input = [Matrix4x4.CreateRotationY(2.7f) * Matrix4x4.CreateTranslation(3, 4, 0)];
        session.Advance(1.0 / 120, input);
        Near(input[0], session.GetBodyWorldTransform(0));
    }

    [Theory]
    [InlineData(PhysicsShapeKind.Sphere)]
    [InlineData(PhysicsShapeKind.Box)]
    [InlineData(PhysicsShapeKind.Capsule)]
    public void ActualPmxShapesFallAtSabaGravityAndDriveBones(PhysicsShapeKind shape)
    {
        var definition = Definition([new(-1, Matrix4x4.CreateTranslation(0, 10, 0))],
            [Body(PhysicsBodyMode.Dynamic, new(0, 11, 0)) with { Shape = shape }]);
        using var session = Session(definition);
        Matrix4x4[] input = [Matrix4x4.CreateTranslation(0, 10, 0)];
        Matrix4x4[] result = [];
        for (int i = 0; i < 30; i++) result = session.Advance(1.0 / 120, input);
        Assert.InRange(result[0].Translation.Y, 6.7f, 7.2f);
        Assert.Equal(10, input[0].Translation.Y);
        Assert.InRange(session.GetBodyWorldTransform(0).Translation.Y - result[0].Translation.Y, .999f, 1.001f);
    }

    [Fact]
    public void AlignedModeKeepsAnimatedBoneTranslationWhileBodyRemainsDynamic()
    {
        using var session = Session(Definition([new(-1, Matrix4x4.CreateTranslation(0, 10, 0)), new(0, Matrix4x4.CreateTranslation(0, 12, 0))],
            [Body(PhysicsBodyMode.DynamicAligned, new(0, 10, 0))]));
        Matrix4x4[] input = [Matrix4x4.CreateTranslation(3, 10, 0), Matrix4x4.CreateTranslation(3, 12, 0)];
        Matrix4x4[] result = [];
        for (int i = 0; i < 30; i++) result = session.Advance(1.0 / 120, input);
        Near(new Vector3(3, 10, 0), result[0].Translation);
        Near(new Vector3(3, 12, 0), result[1].Translation);
        Assert.InRange(session.GetBodyWorldTransform(0).Translation.Y, 6.7f, 7.2f);
    }

    [Theory]
    [InlineData((ushort)1, (ushort)2, true)]
    [InlineData((ushort)0, (ushort)2, false)]
    [InlineData((ushort)1, (ushort)0, false)]
    public void BothGroupMasksMustPermitActualBodyContact(ushort sphereMask, ushort floorMask, bool contact)
    {
        PhysicsBodyDefinition[] bodies = [
            Body(PhysicsBodyMode.Dynamic, new(0, 5, 0)) with { BoneIndex = -1, CollisionGroup = 1, CollisionMask = sphereMask },
            Body(PhysicsBodyMode.Kinematic, new(0, 2, 0)) with { BoneIndex = -1, Shape = PhysicsShapeKind.Box, Size = new(4, .1f, 4), CollisionGroup = 0, CollisionMask = floorMask }
        ];
        using var session = Session(Definition([], bodies));
        for (int i = 0; i < 120; i++) session.Advance(1.0 / 120, []);
        float y = session.GetBodyWorldTransform(0).Translation.Y;
        if (contact) Assert.InRange(y, 2.57f, 2.63f); else Assert.True(y < -30, $"Unexpected contact at {y}");
    }

    [Fact]
    public void LockedSixAxisJointReallySuspendsDynamicBody()
    {
        PhysicsBodyDefinition[] bodies = [Body(PhysicsBodyMode.Kinematic, new(0, 7, 0)) with { BoneIndex = -1 }, Body(PhysicsBodyMode.Dynamic, new(0, 5, 0)) with { BoneIndex = -1 }];
        var joint = new PhysicsJointDefinition("locked", 0, 1, 0, new(0, 6, 0), Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero);
        using var session = Session(Definition([], bodies, [joint]));
        for (int i = 0; i < 120; i++) session.Advance(1.0 / 120, []);
        Assert.InRange(session.GetBodyWorldTransform(1).Translation.Y, 4.97f, 5.03f);
        Assert.Contains(session.Diagnostics, item => item.Contains("Bullet", StringComparison.Ordinal));
    }

    [Fact]
    public void RotatedJointFramesReconstructTheSameRawPmxWorldFrame()
    {
        PhysicsBodyDefinition[] bodies = [Body(PhysicsBodyMode.Kinematic, new(2, 3, 4)) with { BoneIndex = -1, Rotation = new(.2f, .4f, .6f) },
            Body(PhysicsBodyMode.Dynamic, new(-2, 5, 1)) with { BoneIndex = -1, Rotation = new(-.3f, .5f, .7f) }];
        var joint = new PhysicsJointDefinition("rotated", 0, 1, 0, new(1, 4, 2), new(.8f, -.2f, .4f), Vector3.Zero, Vector3.Zero,
            Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero);
        var definition = Definition([], bodies, [joint]);
        var expected = Matrix4x4.CreateRotationX(.8f) * Matrix4x4.CreateRotationY(-.2f) * Matrix4x4.CreateRotationZ(.4f) * Matrix4x4.CreateTranslation(1, 4, 2);
        Near(expected, definition.JointLocalFramesA[0] * definition.BodyRestWorldTransforms[0]);
        Near(expected, definition.JointLocalFramesB[0] * definition.BodyRestWorldTransforms[1]);
        bodies[0] = bodies[0] with { Position = new(99) };
        Assert.Equal(new Vector3(2, 3, 4), definition.Bodies[0].Position);
    }

    [Fact]
    public void ActualLinearSpringSupportsLoadAtAnElasticEquilibrium()
    {
        PhysicsBodyDefinition[] bodies = [Body(PhysicsBodyMode.Kinematic, new(0, 7, 0)) with { BoneIndex = -1 }, Body(PhysicsBodyMode.Dynamic, new(0, 5, 0)) with { BoneIndex = -1 }];
        var joint = new PhysicsJointDefinition("spring", 0, 1, 0, new(0, 6, 0), Vector3.Zero, Vector3.One, -Vector3.One,
            Vector3.Zero, Vector3.Zero, new(0, 400, 0), Vector3.Zero);
        using var session = Session(Definition([], bodies, [joint]));
        for (int i = 0; i < 240; i++) session.Advance(1.0 / 120, []);
        Assert.InRange(session.GetBodyWorldTransform(1).Translation.Y, 4.7f, 4.8f);
    }

    [Fact]
    public void ActualAngularConstraintFollowsAKinematicAnchor()
    {
        PhysicsBodyDefinition[] bodies = [Body(PhysicsBodyMode.Kinematic, Vector3.Zero), Body(PhysicsBodyMode.Dynamic, Vector3.Zero) with { BoneIndex = -1 }];
        var joint = new PhysicsJointDefinition("angular", 0, 1, 0, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero,
            Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero);
        using var session = new CharacterPhysicsSession(Definition([new(-1, Matrix4x4.Identity)], bodies, [joint]), new BepuCharacterPhysicsBackend(Vector3.Zero, null));
        Matrix4x4[] input = [Matrix4x4.CreateRotationZ(.5f)];
        for (int i = 0; i < 120; i++) session.Advance(1.0 / 120, input);
        Near(input[0], session.GetBodyWorldTransform(1));
    }

    [Fact]
    public void ResetRecreatesBroadphaseAndClearsVelocityAndFractionalTime()
    {
        using var session = Session(Definition([new(-1, Matrix4x4.CreateTranslation(0, 10, 0))], [Body(PhysicsBodyMode.Dynamic, new(0, 10, 0))]));
        Matrix4x4[] input = [Matrix4x4.CreateTranslation(0, 10, 0)];
        for (int i = 0; i < 10; i++) session.Advance(1.0 / 30, input);
        var first = session.GetBodyWorldTransform(0);
        session.Advance(1.0 / 1000, input);
        session.Reset(input);
        Assert.Equal(0, session.LastStepCount);
        Near(input[0], session.GetBodyWorldTransform(0));
        for (int i = 0; i < 10; i++) session.Advance(1.0 / 30, input);
        Near(first, session.GetBodyWorldTransform(0));
    }

    [Fact]
    public void FixedStepCapsAtTenAndDoesNotCatchUpDiscardedTime()
    {
        using var session = Session(Definition([], []));
        session.Advance(1, []);
        Assert.Equal(10, session.LastStepCount);
        Assert.InRange(session.DiscardedSeconds, .9166, .9167);
        session.Advance(0, []);
        Assert.Equal(0, session.LastStepCount);
        session.Advance(1.0 / 240, []);
        Assert.Equal(0, session.LastStepCount);
        session.Advance(1.0 / 240, []);
        Assert.Equal(1, session.LastStepCount);
    }

    [Theory]
    [InlineData(1.004f, 9.1f, 9.3f)]
    [InlineData(-.25f, -40f, -38f)]
    public void RawOutOfRangeDampingIsPreservedAndClampedOnlyWhenSimulated(float rawDamping, float minimumY, float maximumY)
    {
        var raw = Body(PhysicsBodyMode.Dynamic, new(0, 10, 0)) with { BoneIndex = -1, LinearDamping = rawDamping, AngularDamping = rawDamping };
        var definition = Definition([], [raw]);
        Assert.Equal(rawDamping, definition.Bodies[0].LinearDamping);
        Assert.Equal(rawDamping, definition.Bodies[0].AngularDamping);
        using var session = Session(definition);
        Assert.Contains(session.Diagnostics, message => message.Contains("clamp", StringComparison.OrdinalIgnoreCase));
        for (int i = 0; i < 120; i++) session.Advance(1.0 / 120, []);
        Assert.InRange(session.GetBodyWorldTransform(0).Translation.Y, minimumY, maximumY);
        Assert.Equal(rawDamping, definition.Bodies[0].LinearDamping);
    }

    [Fact]
    public void ValidationRejectsCyclesBadShapesAndNonFinitePose()
    {
        Assert.Throws<ArgumentException>(() => Definition([new(1, Matrix4x4.Identity), new(0, Matrix4x4.Identity)], []));
        Assert.Throws<ArgumentException>(() => Definition([], [Body(PhysicsBodyMode.Dynamic, Vector3.Zero) with { BoneIndex = -1, Size = Vector3.Zero }]));
        using var session = Session(Definition([new(-1, Matrix4x4.Identity)], []));
        Assert.Throws<ArgumentException>(() => session.Advance(.01, [Matrix4x4.CreateTranslation(float.NaN, 0, 0)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Advance(-.01, [Matrix4x4.Identity]));
    }

    [Fact]
    public void EveryNonFiniteMatrixComponentAndSubtleShearAreRejectedAtAllPublicPoseEntrypoints()
    {
        var definition = Definition([new(-1, Matrix4x4.Identity)], [Body(PhysicsBodyMode.Kinematic, Vector3.Zero)]);
        using var session = Session(definition);
        using var backend = new BepuCharacterPhysicsBackend(Vector3.Zero, null);
        backend.Initialize(definition, [Matrix4x4.Identity]);
        for (int component = 0; component < 16; component++)
        {
            float[] values = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
            values[component] = float.NaN;
            var invalid = new Matrix4x4(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7],
                values[8], values[9], values[10], values[11], values[12], values[13], values[14], values[15]);
            Assert.Throws<ArgumentException>(() => session.Advance(.01, [invalid]));
            Assert.Throws<ArgumentException>(() => backend.Initialize(definition, [invalid]));
            Assert.Throws<ArgumentException>(() => backend.SetKinematicTarget(0, invalid, 1f / 120));
        }
        var shear = Matrix4x4.Identity; shear.M12 = .001f;
        Assert.Throws<ArgumentException>(() => session.Advance(.01, [shear]));
        Assert.Throws<ArgumentException>(() => backend.Initialize(definition, [shear]));
        Assert.Throws<ArgumentException>(() => backend.SetKinematicTarget(0, shear, 1f / 120));
        Near(Matrix4x4.Identity, backend.GetBodyWorldTransform(0));
    }

    private static CharacterPhysicsDefinition Definition(PhysicsBoneDefinition[] bones, PhysicsBodyDefinition[] bodies, PhysicsJointDefinition[]? joints = null)
        => new(bones, bodies, joints ?? []);
    private static CharacterPhysicsSession Session(CharacterPhysicsDefinition definition) => new(definition, new BepuCharacterPhysicsBackend(new Vector3(0, -98, 0), groundY: null));
    private static PhysicsBodyDefinition Body(PhysicsBodyMode mode, Vector3 position) => new("body", 0, PhysicsShapeKind.Sphere, new(.5f, .5f, .5f), position, Vector3.Zero, 1, 0, 0, 0, .5f, 0, 0, mode);
    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0, 1e-4f);
    private static void Near(Matrix4x4 expected, Matrix4x4 actual)
    {
        Near(expected.Translation, actual.Translation);
        Assert.InRange(MathF.Abs(Quaternion.Dot(Quaternion.CreateFromRotationMatrix(expected), Quaternion.CreateFromRotationMatrix(actual))), .99999f, 1.00001f);
    }
}
