using System.Numerics;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bepu;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class BepuMutableBackendTests
{
    private const float Dt = 1f / 120;

    [Fact]
    public void RemovedSlotsAreNotReusedAndOtherBodyMomentumSurvives()
    {
        using var backend = Empty();
        int a = backend.AddBody(Body(), Matrix4x4.Identity);
        int b = backend.AddBody(Body(), At(10));
        backend.ApplyImpulse(b, new(2, 0, 0), Vector3.Zero);
        backend.Step(Dt);
        float before = backend.GetBodyWorldTransform(b).Translation.X;
        backend.RemoveBody(a);
        int c = backend.AddBody(Body(), At(-10));
        Assert.True(c > b);
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.GetBodyWorldTransform(a));
        backend.Step(Dt);
        Near(Dt, backend.GetBodyWorldTransform(b).Translation.X - before);
    }

    [Fact]
    public void ResetClearsOnlySelectedMomentumAndContactPairs()
    {
        using var backend = new BepuCharacterPhysicsBackend(Vector3.Zero, 0);
        backend.Initialize(new([], [], []), []);
        int touching = backend.AddBody(Body(), At(0, .4f));
        int other = backend.AddBody(Body(), At(10, 5));
        backend.ApplyImpulse(other, new(2, 0, 0), Vector3.Zero);
        for (int i = 0; i < 10; i++) backend.Step(Dt);
        float previousX = backend.GetBodyWorldTransform(other).Translation.X;
        backend.ResetBody(touching, At(0, 5));
        Assert.Equal(At(0, 5), backend.GetBodyWorldTransform(touching));
        backend.Step(Dt);
        Near(5, backend.GetBodyWorldTransform(touching).Translation.Y);
        Near(Dt, backend.GetBodyWorldTransform(other).Translation.X - previousX);
    }

    [Fact]
    public void DynamicRestoreUsesAuthoredMassAndUnchangedStatePreservesMomentum()
    {
        using var backend = Empty();
        int body = backend.AddBody(Body(), Matrix4x4.Identity);
        backend.ApplyImpulse(body, new(2, 0, 0), Vector3.Zero);
        backend.SetBodyKinematic(body, false);
        backend.Step(Dt);
        Near(Dt, backend.GetBodyWorldTransform(body).Translation.X);
        backend.SetBodyKinematic(body, true);
        backend.ApplyImpulse(body, new(100, 0, 0), Vector3.Zero);
        backend.Step(Dt);
        Near(Dt, backend.GetBodyWorldTransform(body).Translation.X);
        backend.SetKinematicTarget(body, At(1), Dt);
        backend.SetBodyKinematic(body, true); // Must not clear the target velocity.
        backend.Step(Dt);
        Near(1, backend.GetBodyWorldTransform(body).Translation.X);
        backend.SetBodyKinematic(body, false);
        backend.ApplyImpulse(body, new(2, 0, 0), Vector3.Zero);
        backend.Step(Dt);
        Near(1 + Dt, backend.GetBodyWorldTransform(body).Translation.X);
    }

    [Fact]
    public void AuthoredKinematicAndZeroMassBodiesCannotBeMadeDynamic()
    {
        using var backend = Empty();
        foreach (var config in new[] { Body() with { Mode = PhysicsBodyMode.Kinematic }, Body() with { Mass = 0 } })
        {
            int body = backend.AddBody(config, Matrix4x4.Identity);
            backend.SetBodyKinematic(body, false);
            backend.ApplyImpulse(body, Vector3.One, Vector3.One);
            backend.Step(Dt);
            Assert.Equal(Matrix4x4.Identity, backend.GetBodyWorldTransform(body));
        }
    }

    [Fact]
    public void JointSurvivesAllKinematicTransitionAndResetWithoutLosingLocalFrames()
    {
        using var backend = Empty();
        int a = backend.AddBody(Body() with { Mode = PhysicsBodyMode.Kinematic }, Matrix4x4.Identity);
        int b = backend.AddBody(Body(), At(2));
        int joint = backend.AddJoint(Locked(a, b), At(1), At(-1));
        int diagnosticCount = backend.Diagnostics.Count;
        for (int toggle = 0; toggle < 3; toggle++)
        {
            backend.SetBodyKinematic(b, true);
            backend.SetBodyKinematic(b, false);
            backend.ResetBody(b, At(2));
            backend.ApplyImpulse(b, new(20, 0, 0), Vector3.Zero);
            for (int i = 0; i < 60; i++) backend.Step(Dt);
            Assert.InRange(backend.GetBodyWorldTransform(b).Translation.X, 1.98f, 2.02f);
        }
        Assert.Equal(diagnosticCount, backend.Diagnostics.Count);
        backend.RemoveJoint(joint);
        backend.ApplyImpulse(b, new(20, 0, 0), Vector3.Zero);
        for (int i = 0; i < 60; i++) backend.Step(Dt);
        Assert.True(backend.GetBodyWorldTransform(b).Translation.X > 4);
    }

    [Fact]
    public void ReferencingJointMustBeRemovedBeforeBodyAndJointSlotsStayStable()
    {
        using var backend = Empty();
        int a = backend.AddBody(Body(), Matrix4x4.Identity);
        int b = backend.AddBody(Body(), At(2));
        int j = backend.AddJoint(Locked(a, b), At(1), At(-1));
        Assert.Throws<InvalidOperationException>(() => backend.RemoveBody(a));
        Assert.Equal(Matrix4x4.Identity, backend.GetBodyWorldTransform(a));
        backend.RemoveJoint(j);
        int next = backend.AddJoint(Locked(a, b), At(1), At(-1));
        Assert.True(next > j);
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.RemoveJoint(j));
        backend.RemoveJoint(next);
        backend.RemoveBody(a);
        backend.Step(Dt);
        Assert.Equal(At(2), backend.GetBodyWorldTransform(b));
    }

    [Fact]
    public void IncrementalBodiesShareContactsAndAuthoredCollisionMasks()
    {
        using var backend = Empty();
        int a = backend.AddBody(Body() with { CollisionMask = ushort.MaxValue }, At(-.35f));
        int b = backend.AddBody(Body() with { CollisionMask = ushort.MaxValue }, At(.35f));
        for (int i = 0; i < 30; i++) backend.Step(Dt);
        Assert.True(Vector3.Distance(backend.GetBodyWorldTransform(a).Translation, backend.GetBodyWorldTransform(b).Translation) > .95f);
        backend.ResetBody(a, At(-.35f)); backend.ResetBody(b, At(.35f));
        backend.SetBodyKinematic(a, true); backend.SetBodyKinematic(a, false);
        for (int i = 0; i < 30; i++) backend.Step(Dt);
        Assert.True(Vector3.Distance(backend.GetBodyWorldTransform(a).Translation, backend.GetBodyWorldTransform(b).Translation) > .95f);
    }

    [Fact]
    public void InvalidIncrementalInputDoesNotConsumeSlotsOrModifyExistingState()
    {
        using var backend = Empty();
        int a = backend.AddBody(Body(), Matrix4x4.Identity);
        int b = backend.AddBody(Body(), At(2));
        Assert.Throws<ArgumentException>(() => backend.AddBody(Body() with { Mass = -1 }, Matrix4x4.Identity));
        Assert.Throws<ArgumentException>(() => backend.ResetBody(a, Matrix4x4.CreateScale(2)));
        Assert.Throws<NotSupportedException>(() => backend.AddJoint(Locked(a, b) with { Type = 1 }, At(1), At(-1)));
        Assert.Equal(0, backend.AddJoint(Locked(a, b), At(1), At(-1)));
        Assert.Equal(2, backend.AddBody(Body(), At(10)));
        Assert.Equal(Matrix4x4.Identity, backend.GetBodyWorldTransform(a));
    }

    private static BepuCharacterPhysicsBackend Empty()
    { var result = new BepuCharacterPhysicsBackend(Vector3.Zero, null); result.Initialize(new([], [], []), []); return result; }
    private static PhysicsBodyDefinition Body() => new("body", -1, PhysicsShapeKind.Sphere, new(.5f), Vector3.Zero,
        Vector3.Zero, 2, 0, 0, 0, 0, 0, 0, PhysicsBodyMode.Dynamic);
    private static PhysicsJointDefinition Locked(int a, int b) => new("lock", a, b, 0, Vector3.Zero, Vector3.Zero,
        Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero);
    private static Matrix4x4 At(float x, float y = 0) => Matrix4x4.CreateTranslation(x, y, 0);
    private static void Near(float expected, float actual) => Assert.InRange(actual, expected - 5e-5f, expected + 5e-5f);
}
