using System.Numerics;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class BulletPhysicsTests
{
    [Theory]
    [InlineData(PhysicsShapeKind.Sphere)]
    [InlineData(PhysicsShapeKind.Box)]
    [InlineData(PhysicsShapeKind.Capsule)]
    public void RealBulletShapesFallInUnscaledPmxSpace(PhysicsShapeKind shape)
    {
        var definition = Definition([Body(PhysicsBodyMode.Dynamic, new(0, 10, 0)) with { Shape = shape }]);
        using var backend = new BulletCharacterPhysicsBackend(groundY: null);
        backend.Initialize(definition, definition.BodyRestWorldTransforms.ToArray());
        Step(backend, 30);
        Assert.InRange(backend.GetBodyWorldTransform(0).Translation.Y, 6.7f, 7.2f);
        Assert.Contains("2.75", backend.Name);
    }

    [Fact]
    public void KinematicTargetPreservesFullRotationAndTranslation()
    {
        var definition = Definition([Body(PhysicsBodyMode.Kinematic, Vector3.Zero)]);
        using var backend = new BulletCharacterPhysicsBackend(groundY: null);
        backend.Initialize(definition, definition.BodyRestWorldTransforms.ToArray());
        var target = Matrix4x4.CreateRotationX(.7f) * Matrix4x4.CreateRotationY(2.7f) * Matrix4x4.CreateTranslation(3, 4, -5);
        backend.SetKinematicTarget(0, target, 1f / 120); backend.Step(1f / 120);
        Near(target, backend.GetBodyWorldTransform(0));
    }

    [Fact]
    public void SuccessfulNativePoseCallsDoNotAllocatePerBodyDiagnostics()
    {
        var definition = Definition([Body(PhysicsBodyMode.Kinematic, Vector3.Zero)]);
        using var backend = new BulletCharacterPhysicsBackend(groundY: null);
        backend.Initialize(definition, definition.BodyRestWorldTransforms.ToArray());
        var target = Matrix4x4.CreateRotationX(.7f) * Matrix4x4.CreateRotationY(2.7f) * Matrix4x4.CreateTranslation(3, 4, -5);
        const float step = 1f / 120;
        for (int i = 0; i < 256; i++)
        {
            backend.SetKinematicTarget(0, target, step);
            _ = backend.GetBodyWorldTransform(0);
        }

        const int count = 4096;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < count; i++) backend.SetKinematicTarget(0, target, step);
        long targetBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Matrix4x4 actual = default;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < count; i++) actual = backend.GetBodyWorldTransform(0);
        long poseBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Near(target, actual);
        backend.Step(step);
        Near(target, backend.GetBodyWorldTransform(0));
        Assert.True(targetBytes <= count && poseBytes <= count,
            $"Native pose calls allocated {targetBytes} setter bytes and {poseBytes} getter bytes for {count} calls each.");
    }

    [Theory]
    [InlineData((ushort)1, (ushort)2, true)]
    [InlineData((ushort)0, (ushort)2, false)]
    [InlineData((ushort)1, (ushort)0, false)]
    public void ActualContactsRequireBothCollisionMasks(ushort sphereMask, ushort floorMask, bool contact)
    {
        var definition = Definition([
            Body(PhysicsBodyMode.Dynamic, new(0, 5, 0)) with { CollisionGroup = 1, CollisionMask = sphereMask },
            Body(PhysicsBodyMode.Kinematic, new(0, 2, 0)) with { Shape = PhysicsShapeKind.Box, Size = new(4, .1f, 4), CollisionMask = floorMask }]);
        using var backend = new BulletCharacterPhysicsBackend(groundY: null);
        backend.Initialize(definition, definition.BodyRestWorldTransforms.ToArray()); Step(backend, 120);
        float y = backend.GetBodyWorldTransform(0).Translation.Y;
        if (contact) Assert.InRange(y, 2.55f, 2.65f); else Assert.True(y < -30, $"Unexpected collision at {y}");
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    public void SixDofJointActuallySuspendsBody(byte type)
    {
        var definition = Definition([Body(PhysicsBodyMode.Kinematic, new(0, 7, 0)), Body(PhysicsBodyMode.Dynamic, new(0, 5, 0))],
            [Joint(type)]);
        using var backend = new BulletCharacterPhysicsBackend(groundY: null);
        backend.Initialize(definition, definition.BodyRestWorldTransforms.ToArray()); Step(backend, 120);
        Assert.InRange(backend.GetBodyWorldTransform(1).Translation.Y, 4.97f, 5.03f);
    }

    [Fact]
    public void Spring6DofActuallyResistsGravityOnFreeAxis()
    {
        var joint = Joint(0) with { LinearLower = new(0, 1, 0), LinearUpper = new(0, -1, 0), LinearSpring = new(0, 300, 0) };
        var definition = Definition([Body(PhysicsBodyMode.Kinematic, new(0, 7, 0)), Body(PhysicsBodyMode.Dynamic, new(0, 5, 0))], [joint]);
        using var backend = new BulletCharacterPhysicsBackend(groundY: null);
        backend.Initialize(definition, definition.BodyRestWorldTransforms.ToArray()); Step(backend, 120);
        Assert.InRange(backend.GetBodyWorldTransform(1).Translation.Y, 3.5f, 5.1f);
    }

    [Theory]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    [InlineData((byte)5)]
    public void Pmx21JointsHoldTheirAnchorsUnderGravity(byte type)
    {
        var joint = Joint(type) with { LinearSpring = new(1, .3f, 1), AngularLower = new(.5f, .5f, .5f) };
        if (type is 4 or 5) joint = joint with { AngularLower = Vector3.Zero };
        var definition = Definition([Body(PhysicsBodyMode.Kinematic, new(0, 7, 0)), Body(PhysicsBodyMode.Dynamic, new(0, 5, 0))], [joint]);
        using var backend = new BulletCharacterPhysicsBackend(groundY: null);
        backend.Initialize(definition, definition.BodyRestWorldTransforms.ToArray()); Step(backend, 120);
        Assert.InRange(backend.GetBodyWorldTransform(1).Translation.Y, 4.95f, 5.05f);
        Assert.Empty(backend.Diagnostics);
    }

    [Fact]
    public void Pmx21HingeUsesFrameAForPositiveMotorDirection()
    {
        var joint = Joint(5) with
        {
            Position = Vector3.Zero, LinearSpring = new(1, .3f, 1),
            AngularLower = new(1, 0, 0), AngularUpper = new(-1, 0, 0), AngularSpring = new(1, 1, 10)
        };
        var definition = Definition([Body(PhysicsBodyMode.Kinematic, Vector3.Zero) with { CollisionMask = 0 }, Body(PhysicsBodyMode.Dynamic, Vector3.Zero) with { CollisionMask = 0 }], [joint]);
        using var backend = new BulletCharacterPhysicsBackend(gravity: Vector3.Zero, groundY: null);
        backend.Initialize(definition, definition.BodyRestWorldTransforms.ToArray()); Step(backend, 30);
        var matrix = backend.GetBodyWorldTransform(1);
        // PMX/nanoem reference-frame-A convention: positive motor turns B toward positive Z.
        Assert.True(MathF.Atan2(matrix.M12, matrix.M11) > .1f, $"Unexpected motor direction: {matrix.M12}");
    }

    [Fact]
    public void Pmx21HingeKeepsPositiveAngleInsideAsymmetricLimits()
    {
        var joint = Joint(5) with { Position = Vector3.Zero, LinearSpring = new(.9f, .3f, 1), AngularLower = new(.2f, 0, 0), AngularUpper = new(.8f, 0, 0) };
        var definition = Definition([Body(PhysicsBodyMode.Kinematic, Vector3.Zero) with { CollisionMask = 0 }, Body(PhysicsBodyMode.Dynamic, Vector3.Zero) with { CollisionMask = 0 }], [joint]);
        using var backend = new BulletCharacterPhysicsBackend(gravity: Vector3.Zero, groundY: null);
        backend.Initialize(definition, [Matrix4x4.Identity, Matrix4x4.CreateRotationZ(.6f)]); Step(backend, 30);
        var matrix = backend.GetBodyWorldTransform(1);
        Assert.InRange(MathF.Atan2(matrix.M12, matrix.M11), .59f, .61f);
    }

    [Fact]
    public void ReinitializeDestroysOldWorldAndWarmState()
    {
        var definition = Definition([Body(PhysicsBodyMode.Dynamic, new(0, 10, 0))]);
        using var backend = new BulletCharacterPhysicsBackend(groundY: null);
        for (int cycle = 0; cycle < 30; cycle++)
        {
            backend.Initialize(definition, definition.BodyRestWorldTransforms.ToArray());
            Assert.Equal(10, backend.GetBodyWorldTransform(0).Translation.Y);
            Step(backend, 30); Assert.InRange(backend.GetBodyWorldTransform(0).Translation.Y, 6.7f, 7.2f);
        }
    }

    [Fact]
    public void DisposedAndUninitializedBackendsRejectAccess()
    {
        var backend = new BulletCharacterPhysicsBackend();
        Assert.Throws<InvalidOperationException>(() => backend.Step(1f / 120));
        backend.Dispose(); backend.Dispose();
        Assert.Throws<ObjectDisposedException>(() => backend.GetBodyWorldTransform(0));
        Assert.Throws<ObjectDisposedException>(() => backend.Initialize(Definition([]), []));
    }

    [Fact]
    public void InvalidInputDoesNotDestroyPublishedWorld()
    {
        var definition = Definition([Body(PhysicsBodyMode.Dynamic, new(0, 10, 0))]);
        using var backend = new BulletCharacterPhysicsBackend();
        backend.Initialize(definition, definition.BodyRestWorldTransforms.ToArray());
        Assert.Throws<ArgumentException>(() => backend.Initialize(definition, [Matrix4x4.CreateScale(2)]));
        Assert.Equal(10, backend.GetBodyWorldTransform(0).Translation.Y);
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.Step(float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.GetBodyWorldTransform(1));
        Assert.Throws<ArgumentException>(() => backend.SetKinematicTarget(0, Matrix4x4.Identity, 1f / 120));
    }

    [Fact]
    public void UnknownJointTypeIsExplicitDiagnostic()
    {
        var definition = Definition([Body(PhysicsBodyMode.Kinematic, new(0, 7, 0)), Body(PhysicsBodyMode.Dynamic, new(0, 5, 0))], [Joint(255)]);
        using var backend = new BulletCharacterPhysicsBackend();
        backend.Initialize(definition, definition.BodyRestWorldTransforms.ToArray());
        Assert.Contains(backend.Diagnostics, message => message.Contains("255", StringComparison.Ordinal) && message.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
    }

    private static PhysicsBodyDefinition Body(PhysicsBodyMode mode, Vector3 position) => new("body", -1,
        PhysicsShapeKind.Sphere, new(.5f, 1, .5f), position, Vector3.Zero, 1, 0, 0, 0, .5f, 0, ushort.MaxValue, mode);
    private static PhysicsJointDefinition Joint(byte type) => new("joint", 0, 1, type, new(0, 6, 0), Vector3.Zero,
        Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero);
    private static CharacterPhysicsDefinition Definition(PhysicsBodyDefinition[] bodies, PhysicsJointDefinition[]? joints = null) => new([], bodies, joints ?? []);
    private static void Step(BulletCharacterPhysicsBackend backend, int count) { for (int i = 0; i < count; i++) backend.Step(1f / 120); }
    private static void Near(Matrix4x4 expected, Matrix4x4 actual)
    {
        Assert.InRange(Vector3.Distance(expected.Translation, actual.Translation), 0, 1e-4f);
        var a = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(expected));
        var b = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(actual));
        Assert.InRange(MathF.Min((a - b).LengthSquared(), (a + b).LengthSquared()), 0, 1e-8f);
    }
}
