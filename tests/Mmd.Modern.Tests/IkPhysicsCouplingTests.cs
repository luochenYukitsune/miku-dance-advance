using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Application;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bepu;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class IkPhysicsCouplingTests
{
    private static void Near(Vector3 expected, Vector3 actual)
        => Assert.True(Vector3.Distance(expected, actual) < 5e-5f, $"Expected {expected}; actual {actual}");

    private static CharacterAnimator FixedAnimator(Vector3 tip, Vector3 goal, Vector3? axis,
        Quaternion? baseRotation = null, IkAnglePolicy policy = IkAnglePolicy.Mmd)
    {
        var rig = new CharacterRig([new("joint", Vector3.Zero, -1, FixedAxis: axis), new("tip", tip, 0),
            new("goal", goal, -1, DeformLayer: 1, Ik: new(1, 1, MathF.PI, [new(0)], policy))], [], []);
        return new(rig, new(baseRotation is { } rotation
            ? [new BoneTrack("joint", [new(0, Vector3.Zero, rotation, BoneCurves.Linear)])] : []));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, -1)]
    [InlineData(-1, 1)]
    [InlineData(-1, -1)]
    public void FixedZUsesSignedProjectedAngleRatherThanAnUnrestrictedCrossAxis(int y, int z)
    {
        var goal = Vector3.Normalize(new Vector3(1, y, z));
        var sample = FixedAnimator(Vector3.UnitX, goal, Vector3.UnitZ).Sample(0);
        float angle = MathF.Acos(goal.X) * y / MathF.Sqrt(2);
        Near(new(MathF.Cos(angle), MathF.Sin(angle), 0), sample.BoneWorldMatrices[1].Translation);
        Near(Vector3.UnitZ, Vector3.TransformNormal(Vector3.UnitZ, sample.BoneWorldMatrices[0]));
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    [InlineData(1, 2, 3)]
    public void FixedAxesReachAnAnalyticGoalAndNormalizeInput(float x, float y, float z)
    {
        var axis = Vector3.Normalize(new Vector3(x, y, z));
        var tip = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.Y) < .9f ? Vector3.UnitY : Vector3.UnitZ));
        var goal = Vector3.Transform(tip, Quaternion.CreateFromAxisAngle(axis, .7f));
        var sample = FixedAnimator(tip, goal, axis * 3).Sample(0);
        Near(goal, sample.BoneWorldMatrices[1].Translation);
        Near(axis, Vector3.TransformNormal(axis, sample.BoneWorldMatrices[0]));
    }

    [Fact]
    public void FixedAxisIsInTheParentFrameRatherThanTheAnimatedJointFrame()
    {
        var basis = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .4f);
        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .7f) * basis;
        var goal = Vector3.Transform(Vector3.UnitX, expected);
        var sample = FixedAnimator(Vector3.UnitX, goal, Vector3.UnitZ, basis).Sample(0);
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            Near(Vector3.Transform(axis, expected), Vector3.TransformNormal(axis, sample.BoneWorldMatrices[0]));
    }

    [Fact]
    public void SabaAnglePolicyRetainsItsUnrestrictedPath()
    {
        var goal = Vector3.Normalize(Vector3.One);
        Near(goal, FixedAnimator(Vector3.UnitX, goal, Vector3.UnitZ, policy: IkAnglePolicy.Saba).Sample(0).BoneWorldMatrices[1].Translation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PhysicsDrivenEffectorParentSuppressesIkInBothPhases(bool after)
    {
        var rig = new CharacterRig([new("joint", Vector3.Zero, -1), new("tip", Vector3.UnitX, 0),
            new("goal", Vector3.UnitY, -1, AfterPhysics: after, Ik: new(1, 8, MathF.PI, [new(0)]))], [], []);
        var animator = new CharacterAnimator(rig, new([]));
        Near(Vector3.UnitX, animator.Sample(0, new(PhysicsDrivenBoneIndices: new[] { 0 })).BoneWorldMatrices[1].Translation);
        Near(Vector3.UnitY, animator.Sample(0).BoneWorldMatrices[1].Translation);
    }

    [Fact]
    public void PhysicsSnapshotDoesNotFollowCallerMutationsAndRejectsInvalidBoneIndices()
    {
        var animator = new CharacterAnimator(new([new("joint", Vector3.Zero, -1), new("tip", Vector3.UnitX, 0),
            new("goal", Vector3.UnitY, -1, AfterPhysics: true, Ik: new(1, 8, MathF.PI, [new(0)]))], [], []), new([]));
        var indices = new List<int> { 0 };
        using var evaluation = animator.BeginPhysicsSample(0, new(PhysicsDrivenBoneIndices: indices));
        indices.Clear();
        Near(Vector3.UnitX, evaluation.Complete(evaluation.BeforePhysicsBoneWorldMatrices.ToArray(), []).BoneWorldMatrices[1].Translation);
        Assert.Throws<ArgumentException>(() => animator.Sample(0, new(PhysicsDrivenBoneIndices: new[] { -1 })));
        Assert.Throws<ArgumentException>(() => animator.Sample(0, new(PhysicsDrivenBoneIndices: new[] { 3 })));
    }

    [Theory]
    [InlineData(false, PhysicsBodyMode.Kinematic)]
    [InlineData(true, PhysicsBodyMode.Kinematic)]
    [InlineData(false, PhysicsBodyMode.Dynamic)]
    [InlineData(true, PhysicsBodyMode.Dynamic)]
    [InlineData(false, PhysicsBodyMode.DynamicAligned)]
    [InlineData(true, PhysicsBodyMode.DynamicAligned)]
    public void RealPhysicsPlaybackPassesOnlyDynamicDriversAndResetDoesNotAccumulate(bool after, PhysicsBodyMode mode)
    {
        RigBone[] bones = [new("joint", Vector3.Zero, -1), new("tip", Vector3.UnitX, 0),
            new("goal", Vector3.UnitY, -1, AfterPhysics: after, Ik: new(1, 8, MathF.PI, [new(0)]))];
        var animator = new CharacterAnimator(new(bones, [], [], evaluationSchedule: BoneEvaluationSchedule.Mmd), new([]));
        var definition = new CharacterPhysicsDefinition(bones.Select(b => new PhysicsBoneDefinition(b.ParentIndex,
            Matrix4x4.CreateTranslation(b.RestPosition))),
            [new("driver", 0, PhysicsShapeKind.Sphere, new(.2f), Vector3.Zero, Vector3.Zero,
                mode == PhysicsBodyMode.Kinematic ? 0 : 1, 0, 0, 0, .5f, 0, ushort.MaxValue, mode)], []);
        using var playback = new CharacterPhysicsPlayback(animator, definition, new BepuCharacterPhysicsBackend(Vector3.Zero, null));
        var expected = mode == PhysicsBodyMode.Kinematic ? Vector3.UnitY : Vector3.UnitX;
        var reset = playback.ResetAt(0);
        Near(expected, reset.BoneWorldMatrices[1].Translation);
        Near(expected, playback.Advance(1, 1d / 30).BoneWorldMatrices[1].Translation);
        Assert.Equal(reset.BoneWorldMatrices, playback.ResetAt(0).BoneWorldMatrices);
        Near(Vector3.UnitY, animator.Sample(0).BoneWorldMatrices[1].Translation);
    }

    [Fact]
    public void ProjectionIsAppliedBeforeTheOriginalMmdStepCap()
    {
        var goal = Vector3.Normalize(Vector3.One);
        var rig = new CharacterRig([new("joint", Vector3.Zero, -1, FixedAxis: Vector3.UnitZ),
            new("tip", Vector3.UnitX, 0), new("goal", goal, -1, Ik: new(1, 1, .1f, [new(0)]))], [], []);
        var sample = new CharacterAnimator(rig, new([])).Sample(0);
        Near(new(MathF.Cos(.4f), MathF.Sin(.4f), 0), sample.BoneWorldMatrices[1].Translation);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidFixedAxesFailAtRigConstruction(float axis)
        => Assert.Throws<ArgumentException>(() => new CharacterRig([new("joint", Vector3.Zero, -1,
            FixedAxis: new Vector3(axis))], [], []));

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void UnconstrainedFastPathStillRejectsInvalidPhysicsDriverIndices(int index)
    {
        var animator = new CharacterAnimator(new([new("root", Vector3.Zero, -1)], [], []), new([]));
        Assert.Throws<ArgumentException>(() => animator.Sample(0, new(PhysicsDrivenBoneIndices: new[] { index })));
        Assert.Equal(animator.Sample(0).BoneWorldMatrices,
            animator.Sample(0, new(PhysicsDrivenBoneIndices: new[] { 0 })).BoneWorldMatrices);
    }
}
