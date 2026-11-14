using System.Numerics;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bepu;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class PhysicsLockedJointTests
{
    private const float StepSeconds = 1f / 120;
    // Reaches the backend's 60 Hz cap, making a competing zero-target spring observable.
    private const float StrongSpring = 150000;

    [Theory]
    [InlineData(0, .6f)]
    [InlineData(0, -.6f)]
    [InlineData(1, .6f)]
    [InlineData(1, -.6f)]
    [InlineData(2, .6f)]
    [InlineData(2, -.6f)]
    public void NonzeroLinearLockReachesTheAuthoredOffsetWithOrWithoutSpring(int axis, float offset)
    {
        var direction = Axis(axis);
        var joint = Joint() with { LinearLower = direction * offset, LinearUpper = direction * offset };
        var withoutSpring = Simulate(joint, Matrix4x4.Identity);
        var withSpring = Simulate(joint with { LinearSpring = direction * StrongSpring }, Matrix4x4.Identity);

        Near(Matrix4x4.CreateTranslation(direction * offset), withoutSpring);
        Near(withoutSpring, withSpring);
    }

    [Theory]
    [InlineData(0, .4f)]
    [InlineData(0, -.4f)]
    [InlineData(1, .4f)]
    [InlineData(1, -.4f)]
    [InlineData(2, .4f)]
    [InlineData(2, -.4f)]
    public void NonzeroAngularLockReachesTheAuthoredAngleWithOrWithoutSpring(int axis, float angle)
    {
        var direction = Axis(axis);
        // Isolate one twist axis: the other angular axes remain free.
        var joint = Joint() with
        {
            AngularLower = Replace(Vector3.One, axis, angle),
            AngularUpper = Replace(-Vector3.One, axis, angle)
        };
        var withoutSpring = Simulate(joint, Matrix4x4.Identity);
        var withSpring = Simulate(joint with { AngularSpring = direction * StrongSpring }, Matrix4x4.Identity);

        Near(Matrix4x4.CreateFromAxisAngle(direction, angle), withoutSpring);
        Near(withoutSpring, withSpring);
    }

    [Fact]
    public void ZeroLocksRestoreAPerturbedBodyWithOrWithoutSprings()
    {
        var joint = Joint() with { AngularLower = Vector3.Zero, AngularUpper = Vector3.Zero };
        var initial = Matrix4x4.CreateFromYawPitchRoll(.2f, -.1f, .15f) * Matrix4x4.CreateTranslation(.3f, -.2f, .1f);
        var withoutSpring = Simulate(joint, initial);
        var withSpring = Simulate(joint with { LinearSpring = new(StrongSpring), AngularSpring = new(StrongSpring) }, initial);

        Near(Matrix4x4.Identity, withoutSpring);
        Near(Matrix4x4.Identity, withSpring);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnlockedLinearAxisStillReturnsToSpringEquilibrium(bool limited)
    {
        var joint = Joint() with
        {
            LinearLower = new(limited ? -1 : 1, 0, 0),
            LinearUpper = new(limited ? 1 : -1, 0, 0)
        };
        var initial = Matrix4x4.CreateTranslation(.5f, 0, 0);
        var withoutSpring = Simulate(joint, initial);
        var withSpring = Simulate(joint with { LinearSpring = new(25, 0, 0) }, initial);

        Near(initial, withoutSpring);
        Near(Matrix4x4.Identity, withSpring);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnlockedAngularAxisStillReturnsToSpringEquilibrium(bool limited)
    {
        var joint = Joint() with
        {
            AngularLower = new(1, 1, limited ? -1 : 1),
            AngularUpper = new(-1, -1, limited ? 1 : -1)
        };
        var initial = Matrix4x4.CreateRotationZ(.4f);
        var withoutSpring = Simulate(joint, initial);
        var withSpring = Simulate(joint with { AngularSpring = new(0, 0, 25) }, initial);

        Near(initial, withoutSpring);
        Near(Matrix4x4.Identity, withSpring);
    }

    private static Matrix4x4 Simulate(PhysicsJointDefinition joint, Matrix4x4 initialDynamicPose)
    {
        // Same rest frames, no contacts, gravity or damping: only the joint can move the body.
        var definition = new CharacterPhysicsDefinition([], [Body(PhysicsBodyMode.Kinematic), Body(PhysicsBodyMode.Dynamic)], [joint]);
        using var backend = new BepuCharacterPhysicsBackend(Vector3.Zero, groundY: null);
        backend.Initialize(definition, [Matrix4x4.Identity, initialDynamicPose]);
        for (int i = 0; i < 240; i++) backend.Step(StepSeconds);
        Near(Matrix4x4.Identity, backend.GetBodyWorldTransform(0));
        return backend.GetBodyWorldTransform(1);
    }

    private static PhysicsBodyDefinition Body(PhysicsBodyMode mode) => new("body", -1, PhysicsShapeKind.Sphere,
        Vector3.One, Vector3.Zero, Vector3.Zero, 1, 0, 0, 0, 0, 0, 0, mode);

    private static PhysicsJointDefinition Joint() => new("joint", 0, 1, 0, Vector3.Zero, Vector3.Zero,
        Vector3.Zero, Vector3.Zero, Vector3.One, -Vector3.One, Vector3.Zero, Vector3.Zero);

    private static Vector3 Axis(int axis) => axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
    private static Vector3 Replace(Vector3 value, int axis, float component) => axis switch
    {
        0 => new(component, value.Y, value.Z),
        1 => new(value.X, component, value.Z),
        _ => new(value.X, value.Y, component)
    };

    private static void Near(Matrix4x4 expected, Matrix4x4 actual)
    {
        Assert.InRange(Vector3.Distance(expected.Translation, actual.Translation), 0, .001f);
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            Assert.InRange(Vector3.Distance(Vector3.TransformNormal(axis, expected), Vector3.TransformNormal(axis, actual)), 0, .001f);
    }
}
