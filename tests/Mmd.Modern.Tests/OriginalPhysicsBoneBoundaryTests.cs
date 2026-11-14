using System.Numerics;
using Mmd.Modern.Animation;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class OriginalPhysicsBoneBoundaryTests
{
    [Theory]
    [InlineData(BoneEvaluationSchedule.Mmd, true, 2f)]
    [InlineData(BoneEvaluationSchedule.Mmd, false, .5f)]
    [InlineData(BoneEvaluationSchedule.Saba, true, .5f)]
    public void AfterPhysicsTranslationGrantSelectsDynamicWorkingPose(
        BoneEvaluationSchedule schedule, bool driven, float expected)
    {
        RigBone[] bones = [new("source", Vector3.Zero, -1),
            new("grant", Vector3.Zero, -1, AfterPhysics: true,
                Append: new(0, .5f, false, false, true))];
        var animator = new CharacterAnimator(new(bones, [], [], evaluationSchedule: schedule),
            new([new BoneTrack("source", [new(0, Vector3.UnitX, Quaternion.Identity, BoneCurves.Linear)])]));
        using var pending = animator.BeginPhysicsSample(0, new(PhysicsDrivenBoneIndices: driven ? [0] : []));
        var worlds = pending.BeforePhysicsBoneWorldMatrices.ToArray();
        worlds[0] = Matrix4x4.CreateTranslation(4, 0, 0);
        var result = pending.Complete(worlds, [0]);
        Near(new(expected, 0, 0), result.BoneWorldMatrices[1].Translation);
        Near(Vector3.UnitX, pending.BeforePhysicsBoneWorldMatrices[0].Translation);
    }

    [Theory]
    [InlineData(BoneEvaluationSchedule.Mmd, true, false, 3f)]
    [InlineData(BoneEvaluationSchedule.Mmd, true, true, 3f)]
    [InlineData(BoneEvaluationSchedule.Mmd, false, false, 12f)]
    [InlineData(BoneEvaluationSchedule.Saba, true, false, 12f)]
    public void LateRebuildUsesOrdinaryParentWithoutChangingOrdinaryOrSabaAttachments(
        BoneEvaluationSchedule schedule, bool late, bool after, float expectedX)
    {
        // Original skin translation 1 + subject rest 2 = absolute world 3.
        // Ordinary snapshot 10,20,30 + target rest 1,2,3 maps to target world 11,22,33.
        RigBone[] bones = [new("source", Vector3.Zero, -1, AfterPhysics: after),
            new("subject", new(2, 0, 0), -1, AfterPhysics: after,
                Append: late ? new(0, 0, false, false, true) : null),
            new("tip", Vector3.UnitX, 0, AfterPhysics: after),
            new("controller", Vector3.Zero, -1, AfterPhysics: after, Ik: new(2, 0, 1, [new(0)])),
            new("child", new(2, 1, 0), 1, AfterPhysics: after)];
        var animator = new CharacterAnimator(new(bones, [], [], evaluationSchedule: schedule),
            new([new BoneTrack("subject", [new(0, Vector3.UnitX, Quaternion.Identity, BoneCurves.Linear)])]));
        var result = animator.Sample(0, new(OutsideParents: new Dictionary<int, Matrix4x4>
            { [1] = Matrix4x4.CreateTranslation(11, 22, 33) }));
        var expected = new Vector3(expectedX, expectedX == 3 ? 0 : 22, expectedX == 3 ? 0 : 33);
        Near(expected, result.BoneWorldMatrices[1].Translation);
        Near(expected + Vector3.UnitY, result.BoneWorldMatrices[4].Translation);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InnerDynamicLinkRefreshPreservesWorldButStillSolvesOrdinaryDescendants(bool driven)
    {
        // Native BA200 mode3: direct effector parent is ordinary, so IK still runs.
        RigBone[] bones = [new("root", Vector3.Zero, -1), new("joint", Vector3.UnitX, 0),
            new("tip", new(2, 0, 0), 1),
            new("controller", new(4, 1, 0), -1, AfterPhysics: true, Ik: new(2, 1, 1, [new(1), new(0)]))];
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .3f);
        var animator = new CharacterAnimator(new(bones, [], [], evaluationSchedule: BoneEvaluationSchedule.Mmd),
            new([new BoneTrack("root", [new(0, new(3, 0, 0), rotation, BoneCurves.Linear)])]));
        using var pending = animator.BeginPhysicsSample(0, new(PhysicsDrivenBoneIndices: driven ? [0] : []));
        var worlds = pending.BeforePhysicsBoneWorldMatrices.ToArray();
        worlds[0] = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(3, 0, 0);
        var result = pending.Complete(worlds, [0]);
        // Full original skin-matrix goldens, converted by T(rest)*skin to absolute worlds.
        float cosine = driven ? .06327196955680847f : .18065983057022095f;
        float sine = driven ? .9979963898658752f : .98354572057724f;
        float x = driven ? 3.8920645713806152f : 3.8028860092163086f;
        float y = driven ? -.7024761438369751f : -.8028861880302429f;
        var skin = new Matrix4x4(cosine, sine, 0, 0, -sine, cosine, 0, 0, 0, 0, 1, 0, x, y, 0, 1);
        MatrixNear(skin, result.SkinMatrices[1]);
        MatrixNear(skin, result.SkinMatrices[2]);
        if (driven) MatrixNear(worlds[0], result.BoneWorldMatrices[0]);
        else
        {
            var expectedRoot = new Matrix4x4(.98354572057724f, .18065953254699707f, 0, 0,
                -.18065953254699707f, .98354572057724f, 0, 0, 0, 0, 1, 0, 3, 0, 0, 1);
            MatrixNear(expectedRoot, result.BoneWorldMatrices[0]);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void LateOutsideParentReflectionUsesTheSameOrdinaryParentAsItsWorld(int attachment)
    {
        RigBone[] bones = [new("source", Vector3.Zero, -1),
            new("subject", new(2, 0, 0), -1, Append: new(0, 0, false, false, true)),
            new("tip", Vector3.UnitX, 0),
            new("controller", Vector3.Zero, -1, Ik: new(2, 0, 1, [new(0)])),
            new("child", new(2, 1, 0), 1)];
        var animator = new CharacterAnimator(new(bones, [], [], evaluationSchedule: BoneEvaluationSchedule.Mmd),
            new([new BoneTrack("subject", [new(0, Vector3.UnitX, Quaternion.Identity, BoneCurves.Linear)])]));
        using var pending = animator.BeginPhysicsSample(0, new(PhysicsDrivenBoneIndices: [1],
            OutsideParents: new Dictionary<int, Matrix4x4> { [attachment] = Matrix4x4.CreateTranslation(11, 22, 33) }));
        var before = pending.BeforePhysicsBoneWorldMatrices.ToArray();
        Near(new(3, 0, 0), before[1].Translation);
        var result = pending.Complete(before, [1]);
        MatrixNear(before[1], result.BoneWorldMatrices[1]);
        MatrixNear(before[4], result.BoneWorldMatrices[4]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void DynamicAfterPhysicsRotationRemovesTheGrantBeforeSelectingItsBase(bool parented, bool noncommuting)
    {
        RigBone[] bones = [new("source", Vector3.Zero, -1),
            new("subject", Vector3.Zero, parented ? 2 : -1, AfterPhysics: true,
                Append: new(0, .5f, false, true, false)),
            new("parent", Vector3.Zero, -1)];
        var animator = new CharacterAnimator(new(bones, [], [], evaluationSchedule: BoneEvaluationSchedule.Mmd),
            new([new BoneTrack("source", [new(0, Vector3.Zero,
                Quaternion.CreateFromAxisAngle(noncommuting ? Vector3.UnitX : Vector3.UnitZ, .4f), BoneCurves.Linear)])]));
        using var pending = animator.BeginPhysicsSample(0, new(PhysicsDrivenBoneIndices: [1]));
        var worlds = pending.BeforePhysicsBoneWorldMatrices.ToArray();
        worlds[1] = Matrix4x4.CreateRotationZ(.7f);
        var result = pending.Complete(worlds, [1]);
        // C3460 removes the root grant on the opposite side from a parented bone.
        // Preserve that original noncommuting result, rather than forcing world preservation.
        var expected = !parented && noncommuting
            ? new Matrix4x4(.76484215259552f, .6313762664794922f, -.12798622250556946f, 0,
                -.6313762664794922f, .7741237878799438f, .04578736796975136f, 0,
                .12798622250556946f, .04578733816742897f, .990718424320221f, 0, 0, 0, 0, 1)
            : worlds[1];
        MatrixNear(expected, result.BoneWorldMatrices[1]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ReflectedRotationGrantReadsTheCompleteEffectiveSourcePose(bool subjectFirst, bool sourceAppend)
    {
        RigBone[] bones = [new("origin", Vector3.Zero, -1),
            new("source", Vector3.Zero, -1, Append: sourceAppend ? new(0, .5f, false, true, false) : null),
            new("subject", Vector3.Zero, -1, AfterPhysics: true, Append: new(1, .5f, false, true, false))];
        BoneTrack Track(string name, float angle) => new(name, [new(0, Vector3.Zero,
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle), BoneCurves.Linear)]);
        var animator = new CharacterAnimator(new(bones, [], [], evaluationSchedule: BoneEvaluationSchedule.Mmd),
            new([Track("origin", .2f), Track("source", sourceAppend ? .3f : .4f)]));
        using var pending = animator.BeginPhysicsSample(0, new(PhysicsDrivenBoneIndices: [1, 2]));
        var worlds = pending.BeforePhysicsBoneWorldMatrices.ToArray();
        worlds[1] = Matrix4x4.CreateRotationZ(.8f);
        worlds[2] = Matrix4x4.CreateRotationZ(.7f);
        var result = pending.Complete(worlds, subjectFirst ? [2, 1] : [1, 2]);
        // Capture all effective locals first. The source's selected base may have
        // its own grant removed, but recipients still read its effective .8 local.
        MatrixNear(worlds[1], result.BoneWorldMatrices[1]);
        MatrixNear(worlds[2], result.BoneWorldMatrices[2]);
    }

    private static void MatrixNear(Matrix4x4 expected, Matrix4x4 actual)
    {
        Near(Vector3.Transform(Vector3.Zero, expected), Vector3.Transform(Vector3.Zero, actual));
        Near(Vector3.Transform(Vector3.UnitX, expected), Vector3.Transform(Vector3.UnitX, actual));
        Near(Vector3.Transform(Vector3.UnitY, expected), Vector3.Transform(Vector3.UnitY, actual));
        Near(Vector3.Transform(Vector3.UnitZ, expected), Vector3.Transform(Vector3.UnitZ, actual));
    }

    private static void Near(Vector3 expected, Vector3 actual) => Assert.True(
        Vector3.Distance(expected, actual) < 5e-5f, $"Expected {expected}; actual {actual}");
}
