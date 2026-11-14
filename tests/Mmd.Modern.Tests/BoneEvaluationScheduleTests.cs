using System.Numerics;
using Mmd.Modern.Animation;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class BoneEvaluationScheduleTests
{
    private static CharacterAnimator Animator(bool afterPhysics, BoneEvaluationSchedule schedule,
        bool recipientBeforeController = true, float weight = 1)
    {
        int grant = recipientBeforeController ? 2 : 3;
        int goal = recipientBeforeController ? 3 : 2;
        var bones = new RigBone[5];
        bones[0] = new("joint", Vector3.Zero, -1, AfterPhysics: afterPhysics);
        bones[1] = new("tip", Vector3.UnitX, 0, AfterPhysics: afterPhysics);
        bones[grant] = new("grant", Vector3.Zero, -1, AfterPhysics: afterPhysics,
            Append: new(0, weight, false, true, false));
        bones[goal] = new("goal", Vector3.UnitY, -1, AfterPhysics: afterPhysics,
            Ik: new(1, 8, MathF.PI, [new(0)]));
        bones[4] = new("grantChild", Vector3.UnitX, grant, AfterPhysics: afterPhysics);
        return new(new(bones, [], [], evaluationSchedule: schedule), new([]));
    }

    private static void Near(Vector3 expected, Vector3 actual)
        => Assert.True(Vector3.Distance(expected, actual) < .00005f,
            $"Expected {expected}; actual {actual}");

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void MmdRebuildsIkDrivenGrantAndDescendantInEitherPhaseAndIndexOrder(bool afterPhysics, bool early)
    {
        var animator = Animator(afterPhysics, BoneEvaluationSchedule.Mmd, early);
        var sample = animator.Sample(0);
        Near(Vector3.UnitY, sample.BoneWorldMatrices[1].Translation);
        Near(Vector3.UnitY, sample.BoneWorldMatrices[4].Translation);
        int grant = early ? 2 : 3;
        Near(Vector3.UnitY, Vector3.TransformNormal(Vector3.UnitX, sample.BoneWorldMatrices[grant]));
    }

    [Theory]
    [InlineData(.5f)]
    [InlineData(-.5f)]
    [InlineData(1.5f)]
    public void MmdLateGrantRetainsSignedWeight(float weight)
    {
        var sample = Animator(false, BoneEvaluationSchedule.Mmd, weight: weight).Sample(0);
        float angle = weight * MathF.PI / 2;
        Near(new(MathF.Cos(angle), MathF.Sin(angle), 0), sample.BoneWorldMatrices[4].Translation);
    }

    [Fact]
    public void ExplicitSabaRetainsItsStoredInterleaving()
    {
        var sample = Animator(false, BoneEvaluationSchedule.Saba).Sample(0);
        Near(Vector3.UnitY, sample.BoneWorldMatrices[1].Translation);
        Near(Vector3.UnitX, sample.BoneWorldMatrices[4].Translation);
        Near(Vector3.UnitY, Animator(false, BoneEvaluationSchedule.Saba, false).Sample(0).BoneWorldMatrices[4].Translation);
    }

    [Fact]
    public void MmdDisabledAndOutOfOrderSamplesDoNotKeepAnOldIkGrant()
    {
        var animator = Animator(false, BoneEvaluationSchedule.Mmd);
        var expected = animator.Sample(0);
        foreach (var options in new[] { new CharacterEvaluationOptions(EnableIk: false),
            new CharacterEvaluationOptions(IkEnabledOverrides: new Dictionary<int, bool> { [3] = false }) })
        {
            var disabled = animator.Sample(10, options);
            Near(Vector3.UnitX, disabled.BoneWorldMatrices[4].Translation);
            Assert.Equal(expected.BoneWorldMatrices, animator.Sample(0).BoneWorldMatrices);
        }
        foreach (double frame in new[] { 8d, 0d, 3d, 8d })
            Assert.Equal(expected.BoneWorldMatrices, animator.Sample(frame).BoneWorldMatrices);
        Near(Vector3.UnitY, expected.BoneWorldMatrices[4].Translation);
    }

    [Fact]
    public void PhysicsCompletionRetainsBeforePoseAndRebuildsOnlyAfterPhase()
    {
        var animator = Animator(true, BoneEvaluationSchedule.Mmd);
        using var evaluation = animator.BeginPhysicsSample(0);
        Near(Vector3.UnitX, evaluation.BeforePhysicsBoneWorldMatrices[4].Translation);
        var sample = evaluation.Complete(evaluation.BeforePhysicsBoneWorldMatrices.ToArray(), []);
        Near(Vector3.UnitY, sample.BoneWorldMatrices[4].Translation);
        Near(Vector3.UnitX, evaluation.BeforePhysicsBoneWorldMatrices[4].Translation);
        Assert.Equal(animator.Sample(0).BoneWorldMatrices, sample.BoneWorldMatrices);
    }

    [Fact]
    public void ScheduleDefaultsAreExplicitAndInvalidValuesFailBeforeSampling()
    {
        Assert.Equal(BoneEvaluationSchedule.Saba, new CharacterRig([], [], []).EvaluationSchedule);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterRig([], [], [],
            evaluationSchedule: (BoneEvaluationSchedule)33));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IkEffectorKeepsItsLayerAndUsesTheAppendBeforeSolving(bool after)
    {
        RigBone[] bones = [new("joint", Vector3.Zero, -1, AfterPhysics: after),
            new("tip", Vector3.UnitX, 0, AfterPhysics: after, Append: new(2, 1, false, false, true)),
            new("translationSource", Vector3.Zero, -1, AfterPhysics: after),
            new("goal", Vector3.UnitY, -1, AfterPhysics: after, Ik: new(1, 8, MathF.PI, [new(0)]))];
        var animator = new CharacterAnimator(new(bones, [], [], evaluationSchedule: BoneEvaluationSchedule.Mmd),
            new([new BoneTrack("translationSource", [new(0, Vector3.UnitY, Quaternion.Identity, BoneCurves.Linear)])]));
        // Original 9.32 B9F60/BA200, in both phases: an effector is type 6, so the
        // loader does not defer it like an ordinary child of an IK link. The tip's
        // +Y append is already present when the joint turns towards the +Y goal.
        Near(new(0, MathF.Sqrt(2), 0), animator.Sample(0).BoneWorldMatrices[1].Translation);
    }

    [Fact]
    public void PrepareDoesNotDoubleCommitGrantTranslationAndRebuildReachesDescendants()
    {
        RigBone[] bones = [new("joint", Vector3.Zero, -1), new("tip", Vector3.UnitX, 0),
            new("grant", Vector3.Zero, -1, Append: new(0, 1, false, true, true)),
            new("goal", new(2, 1, 0), -1, Ik: new(1, 8, MathF.PI, [new(0)])),
            new("child", Vector3.UnitX, 2, Append: new(2, .5f, false, false, true))];
        BoneTrack Track(string name, Vector3 value) => new(name, [new(0, value, Quaternion.Identity, BoneCurves.Linear)]);
        var animator = new CharacterAnimator(new(bones, [], [], evaluationSchedule: BoneEvaluationSchedule.Mmd),
            new([Track("joint", new(2, 0, 0)), Track("grant", Vector3.UnitY)]));
        var sample = animator.Sample(0);
        Near(new(2, 1, 0), sample.BoneWorldMatrices[2].Translation);
        Near(new(1.5f, 3, 0), sample.BoneWorldMatrices[4].Translation);
    }

    [Fact]
    public void AfterPhysicsGrantReadsTheImportedSourceLocalRotation()
    {
        RigBone[] bones = [new("source", Vector3.Zero, -1),
            new("grant", Vector3.Zero, -1, AfterPhysics: true, Append: new(0, .5f, false, true, false))];
        var animator = new CharacterAnimator(new(bones, [], [], evaluationSchedule: BoneEvaluationSchedule.Mmd), new([]));
        using var evaluation = animator.BeginPhysicsSample(0);
        var physics = evaluation.BeforePhysicsBoneWorldMatrices.ToArray();
        physics[0] = Matrix4x4.CreateRotationZ(MathF.PI / 2);
        var sample = evaluation.Complete(physics, [0]);
        Near(new(MathF.Sqrt(.5f), MathF.Sqrt(.5f), 0),
            Vector3.TransformNormal(Vector3.UnitX, sample.BoneWorldMatrices[1]));
    }
}
