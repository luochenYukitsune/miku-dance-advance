using System.Numerics;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bepu;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class PhysicsKinematicTargetTests
{
    private const float StepSeconds = 1f / 120;

    [Fact]
    public void RepresentableTinyRotationsReachTheTargetThroughTheActualIntegrator()
    {
        foreach (float angle in new[] { 1e-8f, -1e-8f, 1e-10f })
        {
            using var backend = Backend();
            backend.Initialize(Definition(), [Matrix4x4.Identity]);
            var target = Matrix4x4.CreateRotationY(angle);
            backend.SetKinematicTarget(0, target, StepSeconds);
            // Targets supply contact velocity; they must not teleport the pose before solving.
            Assert.Equal(Matrix4x4.Identity, backend.GetBodyWorldTransform(0));
            backend.Step(StepSeconds);
            var actual = backend.GetBodyWorldTransform(0);
            Assert.InRange(MathF.Abs(actual.M13 - target.M13), 0, MathF.Abs(angle) * .001f);
            Assert.InRange(MathF.Abs(actual.M31 - target.M31), 0, MathF.Abs(angle) * .001f);
        }
    }

    [Fact]
    public void CompoundWorldRotationAndTranslationReachEverySuccessiveTarget()
    {
        using var backend = Backend();
        backend.Initialize(Definition(), [Matrix4x4.CreateRotationX(.7f) * Matrix4x4.CreateTranslation(2, 3, 4)]);
        for (int i = 0; i < 64; i++)
        {
            var target = Matrix4x4.CreateFromYawPitchRoll(i * .37f, -.8f + i * .03f, i * -.17f)
                * Matrix4x4.CreateTranslation(i * .2f, 3 - i * .1f, i * -.3f);
            backend.SetKinematicTarget(0, target, StepSeconds);
            backend.Step(StepSeconds);
            Near(target, backend.GetBodyWorldTransform(0));
        }
    }

    [Fact]
    public void ResubmittingTheReachedPoseDoesNotAccumulatePreviousVelocity()
    {
        using var backend = Backend();
        backend.Initialize(Definition(), [Matrix4x4.Identity]);
        var target = Matrix4x4.CreateRotationZ(2.8f) * Matrix4x4.CreateTranslation(1, 2, 3);
        for (int i = 0; i < 32; i++)
        {
            backend.SetKinematicTarget(0, target, StepSeconds);
            backend.Step(StepSeconds);
            Near(target, backend.GetBodyWorldTransform(0));
        }
    }

    [Fact]
    public void FractionalAdvanceLeavesTheBodyUntilAnActualSubstepAndUsesTheLatestTarget()
    {
        using var session = new CharacterPhysicsSession(Definition(), Backend());
        session.Advance(CharacterPhysicsSession.FixedStepSeconds / 2, [Matrix4x4.CreateTranslation(99, 0, 0)]);
        Assert.Equal(0, session.LastStepCount);
        Near(Matrix4x4.Identity, session.GetBodyWorldTransform(0));
        var target = Matrix4x4.CreateRotationX(.6f) * Matrix4x4.CreateTranslation(3, 2, 1);
        session.Advance(CharacterPhysicsSession.FixedStepSeconds / 2, [target]);
        Assert.Equal(1, session.LastStepCount);
        Near(target, session.GetBodyWorldTransform(0));
    }

    [Fact]
    public void SeekResetClearsKinematicVelocityAndThePendingFraction()
    {
        using var session = new CharacterPhysicsSession(Definition(), Backend());
        session.Advance(CharacterPhysicsSession.FixedStepSeconds, [Matrix4x4.CreateTranslation(10, 0, 0)]);
        session.Advance(CharacterPhysicsSession.FixedStepSeconds / 2, [Matrix4x4.CreateTranslation(99, 0, 0)]);
        var reset = Matrix4x4.CreateRotationY(-.8f) * Matrix4x4.CreateTranslation(-2, 3, 1);
        session.Reset([reset]);
        session.Advance(CharacterPhysicsSession.FixedStepSeconds / 2, [reset]);
        Assert.Equal(0, session.LastStepCount);
        session.Advance(CharacterPhysicsSession.FixedStepSeconds / 2, [reset]);
        Assert.Equal(1, session.LastStepCount);
        Near(reset, session.GetBodyWorldTransform(0));
    }

    [Fact]
    public void MultiSubstepAdvanceReachesTheTargetAndDoesNotCatchUpDiscardedTime()
    {
        using var session = new CharacterPhysicsSession(Definition(), Backend());
        var target = Matrix4x4.CreateFromYawPitchRoll(2.7f, -.9f, .4f) * Matrix4x4.CreateTranslation(10, 2, -3);
        session.Advance(1, [target]);
        Assert.Equal(CharacterPhysicsSession.MaximumSubsteps, session.LastStepCount);
        Near(target, session.GetBodyWorldTransform(0));
        session.Advance(0, [Matrix4x4.Identity]);
        Assert.Equal(0, session.LastStepCount);
        Near(target, session.GetBodyWorldTransform(0));
    }

    [Fact]
    public void MovingKinematicBodyActuallyTransfersVelocityThroughContact()
    {
        var kinematic = Body(PhysicsBodyMode.Kinematic) with { Position = -Vector3.UnitX, CollisionMask = 1 };
        var dynamicBody = Body(PhysicsBodyMode.Dynamic) with { BoneIndex = -1, CollisionMask = 1 };
        var definition = new CharacterPhysicsDefinition([new(-1, Matrix4x4.Identity)], [kinematic, dynamicBody], []);
        using var backend = Backend();
        backend.Initialize(definition, [Matrix4x4.CreateTranslation(-1, 0, 0), Matrix4x4.Identity]);
        var target = Matrix4x4.CreateTranslation(-.9f, 0, 0);
        backend.SetKinematicTarget(0, target, StepSeconds);
        backend.Step(StepSeconds);
        Near(target, backend.GetBodyWorldTransform(0));
        Assert.InRange(backend.GetBodyWorldTransform(1).Translation.X, .05f, .15f);
    }

    private static BepuCharacterPhysicsBackend Backend() => new(Vector3.Zero, null);
    private static CharacterPhysicsDefinition Definition() => new([new(-1, Matrix4x4.Identity)], [Body(PhysicsBodyMode.Kinematic)], []);
    private static PhysicsBodyDefinition Body(PhysicsBodyMode mode) => new("body", 0, PhysicsShapeKind.Sphere,
        new(.5f), Vector3.Zero, Vector3.Zero, 1, 0, 0, 0, .5f, 0, 0, mode);
    private static void Near(Matrix4x4 expected, Matrix4x4 actual)
    {
        Assert.InRange(Vector3.Distance(expected.Translation, actual.Translation), 0, 1e-5f);
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            Assert.InRange(Vector3.Distance(Vector3.TransformNormal(axis, expected), Vector3.TransformNormal(axis, actual)), 0, 1e-5f);
    }
}
