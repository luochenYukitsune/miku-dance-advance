using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class CharacterIkTests
{
    private static BoneTrack Track(string name, Vector3 translation, Quaternion? rotation = null) => new(name,
        [new(0, translation, rotation ?? Quaternion.Identity, BoneCurves.Linear)]);
    private static CharacterAnimator Animator(RigBone[] bones, params BoneTrack[] tracks) => new(new(bones, [], []), new(tracks));
    private static void Near(Vector3 expected, Vector3 actual, float tolerance = .00005f)
        => Assert.True(Vector3.Distance(expected, actual) < tolerance, $"Expected {expected}; actual {actual}");
    private static Vector3 Direction(Matrix4x4 matrix, Vector3 direction) => Vector3.TransformNormal(direction, matrix);
    private static RigBone[] OneLink(Vector3 goal, Vector3? min = null, Vector3? max = null, float angle = MathF.PI, int iterations = 16) =>
        [new("joint", Vector3.Zero, -1), new("tip", Vector3.UnitX, 0),
            new("goal", goal, -1, Ik: new(1, iterations, angle, [new(0, min, max)], IkAnglePolicy.Saba))];

    [Fact]
    public void OriginalMmdHalfAngleStepUsesPointFourRadiansForFirstLink()
    {
        var bones = OneLink(Vector3.UnitY, angle: .1f, iterations: 1);
        bones[2] = bones[2] with { Ik = new(1, 1, .1f, [new(0)]) };
        Near(new(MathF.Cos(.4f), MathF.Sin(.4f), 0), Animator(bones).Sample(0).BoneWorldMatrices[1].Translation);
    }

    [Fact]
    public void UnrestrictedIkReachesGoalAndBothExternalSwitchesDisableIt()
    {
        var animator = Animator(OneLink(Vector3.UnitY));
        Near(Vector3.UnitY, animator.Sample(0).BoneWorldMatrices[1].Translation);
        Near(Vector3.UnitX, animator.Sample(0, new(EnableIk: false)).BoneWorldMatrices[1].Translation);
        Near(Vector3.UnitX, animator.Sample(0, new(IkEnabledOverrides: new Dictionary<int, bool> { [2] = false })).BoneWorldMatrices[1].Translation);
        Near(Vector3.UnitY, animator.Sample(0).BoneWorldMatrices[1].Translation);
    }

    [Fact]
    public void KneePlaneLimitsAndPerIterationStepAreApplied()
    {
        var animator = Animator([new("knee", Vector3.Zero, -1), new("tip", Vector3.UnitY, 0),
            new("goal", -Vector3.UnitZ, -1, Ik: new(1, 20, .1f, [new(0, new(-1, 0, 0), Vector3.Zero)]))]);
        Near(new(0, MathF.Cos(1), -MathF.Sin(1)), animator.Sample(0).BoneWorldMatrices[1].Translation);
        var oneStep = Animator(OneLink(Vector3.UnitY, angle: .1f, iterations: 1));
        Near(new(MathF.Cos(.1f), MathF.Sin(.1f), 0), oneStep.Sample(0).BoneWorldMatrices[1].Translation);
    }

    [Fact]
    public void GeneralEulerLimitClampsMultipleAxesAndPreservesFiniteRotation()
    {
        var bones = OneLink(Vector3.UnitY, new(-.1f), new(.2f));
        var result = Animator(bones).Sample(0);
        Near(new(MathF.Cos(.2f), MathF.Sin(.2f), 0), result.BoneWorldMatrices[1].Translation);
    }

    [Fact]
    public void OppositeAndZeroLengthDirectionsRemainFinite()
    {
        Near(-Vector3.UnitX, Animator(OneLink(-Vector3.UnitX)).Sample(0).BoneWorldMatrices[1].Translation);
        var same = Animator([new("joint", Vector3.Zero, -1), new("goal", Vector3.UnitX, -1, Ik: new(0, 10, 1, [new(0)]))]);
        Assert.Equal(Matrix4x4.Identity, same.Sample(0).BoneWorldMatrices[0]);
        var zeroGoal = Animator(OneLink(Vector3.Zero));
        Near(Vector3.UnitX, zeroGoal.Sample(0).BoneWorldMatrices[1].Translation);
    }

    [Fact]
    public void SamplingIsAbsoluteAndReturnedArraysAreIndependent()
    {
        var bones = OneLink(Vector3.UnitY);
        var animator = Animator(bones, new BoneTrack("goal", [new(0, Vector3.Zero, Quaternion.Identity, BoneCurves.Linear),
            new(10, new(0, -2, 0), Quaternion.Identity, BoneCurves.Linear)]));
        var first = animator.Sample(0); animator.Sample(10); animator.Sample(3);
        Assert.Equal(first.BoneWorldMatrices, animator.Sample(0).BoneWorldMatrices);
        first.BoneWorldMatrices[0] = default;
        Near(Vector3.UnitY, animator.Sample(0).BoneWorldMatrices[1].Translation);
    }

    [Fact]
    public void AppendRotationAndTranslationRespectWeightAndRowVectorQuaternionOrder()
    {
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var local = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .4f);
        var animator = Animator([new("source", Vector3.Zero, -1),
            new("grant", new(3, 0, 0), -1, Append: new(0, .5f, false, true, true))],
            Track("source", new(2, 4, 6), turn), Track("grant", Vector3.UnitX, local));
        var matrix = animator.Sample(0).BoneWorldMatrices[1];
        Near(new(5, 2, 3), matrix.Translation);
        Near(Vector3.Transform(Vector3.UnitY, local * Quaternion.Slerp(Quaternion.Identity, turn, .5f)), Direction(matrix, Vector3.UnitY));
    }

    [Fact]
    public void NonlocalAppendUsesEvaluatedSourceWhileLocalUsesSourceAnimation()
    {
        var animator = Animator([new("source", Vector3.Zero, -1),
            new("middle", Vector3.Zero, -1, DeformLayer: 1, Append: new(0, .5f, false, true, true)),
            new("nonlocal", Vector3.Zero, -1, DeformLayer: 2, Append: new(1, 1, false, true, true)),
            new("local", Vector3.Zero, -1, DeformLayer: 2, Append: new(1, 1, true, true, true))],
            Track("source", new(4, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1)),
            Track("middle", new(0, 3, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, .3f)));
        var sample = animator.Sample(0);
        Near(new(2, 3, 0), sample.BoneWorldMatrices[2].Translation);
        Near(new(0, 3, 0), sample.BoneWorldMatrices[3].Translation);
        // Original source-local policy includes middle's own X rotation as well as its granted Z rotation.
        Near(new(MathF.Cos(.5f), MathF.Sin(.5f)*MathF.Cos(.3f), MathF.Sin(.5f)*MathF.Sin(.3f)), Direction(sample.BoneWorldMatrices[2], Vector3.UnitX));
        Near(Vector3.Transform(Vector3.UnitY, Quaternion.CreateFromAxisAngle(Vector3.UnitX, .3f)), Direction(sample.BoneWorldMatrices[3], Vector3.UnitY));
    }

    [Fact]
    public void DeformLayerIsStableAndAfterPhysicsRunsInSecondPhase()
    {
        RigBone[] bones = [new("source", Vector3.Zero, -1),
            new("early", Vector3.Zero, -1, DeformLayer: -2, Append: new(2, 1, false, true, false)),
            new("middle", Vector3.Zero, -1, DeformLayer: 0, Append: new(0, 1, false, true, false)),
            new("late", Vector3.Zero, -1, DeformLayer: -10, AfterPhysics: true, Append: new(2, 1, false, true, false))];
        var sample = Animator(bones, Track("source", Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1))).Sample(0);
        Near(Vector3.UnitX, Direction(sample.BoneWorldMatrices[1], Vector3.UnitX));
        Near(new(MathF.Cos(1), MathF.Sin(1), 0), Direction(sample.BoneWorldMatrices[3], Vector3.UnitX));
    }

    [Fact]
    public void IkIncludesAnimatedJointRotationAndAppendCanReadSolvedIk()
    {
        var bones = OneLink(Vector3.UnitY).Concat([new RigBone("grant", Vector3.Zero, -1, DeformLayer: 1,
            Append: new(0, 1, false, true, false))]).ToArray();
        var sample = Animator(bones, Track("joint", Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitX, .3f))).Sample(0);
        Near(Vector3.UnitY, sample.BoneWorldMatrices[1].Translation);
        Near(Direction(sample.BoneWorldMatrices[0], Vector3.UnitZ), Direction(sample.BoneWorldMatrices[3], Vector3.UnitZ));
    }

    [Fact]
    public void MetadataIsValidatedAndIkLinksAreCopied()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IkDefinition(0, 2049, 1, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IkDefinition(0, 1, float.NaN, []));
        Assert.Throws<ArgumentException>(() => new IkDefinition(0, 1, 1, Enumerable.Repeat(new IkLinkDefinition(0), 257)));
        Assert.Throws<ArgumentException>(() => Animator([new("a", Vector3.Zero, -1, Append: new(2, 1, false, true, false))]));
        Assert.Throws<ArgumentException>(() => Animator([new("a", Vector3.Zero, -1, Ik: new(2, 1, 1, []))]));
        Assert.Throws<ArgumentException>(() => Animator(OneLink(Vector3.UnitY, Vector3.One, Vector3.Zero)));
        Assert.Throws<ArgumentException>(() => Animator([new("a", Vector3.Zero, -1, Append: new(0, 1, false, true, false))]));
        IkLinkDefinition[] links = [new(0)]; var ik = new IkDefinition(1, 10, 1, links); links[0] = new(99);
        Assert.Equal(0, ik.Links[0].BoneIndex);
    }

    [Fact]
    public void RollbackKeepsBestPoseWhenAControllerMovesWithItsOwnIkLink()
    {
        var bones = OneLink(Vector3.UnitY, iterations: 100);
        bones[2] = bones[2] with { ParentIndex = 0 };
        // Rotating the joint also rotates its goal. Float roundoff makes the second pose slightly
        // closer; a further iteration must restore it rather than accumulate another quarter-turn.
        var best = Animator(bones).Sample(0);
        bones[2] = bones[2] with { Ik = new(1, 2, MathF.PI, [new(0)], IkAnglePolicy.Saba) };
        Assert.Equal(Animator(bones).Sample(0).BoneWorldMatrices, best.BoneWorldMatrices);
        Near(-Vector3.UnitX, best.BoneWorldMatrices[1].Translation);
    }

    [Fact]
    public void TwoLinkNonplanarChainReachesGoalUnderRotatedParent()
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new(1, 2, 3)), .7f);
        RigBone[] bones = [new("parent", new(2, 3, 4), -1), new("base", new(2, 3, 4), 0),
            new("joint", new(3, 3, 4), 1), new("tip", new(4, 3, 4), 2),
            new("goal", new(3, 4, 4), -1, Ik: new(3, 100, .4f, [new(2), new(1)]))];
        var sample = Animator(bones, Track("parent", Vector3.Zero, rotation), Track("goal", new(-.2f, -.3f, .3f))).Sample(0);
        // CCD uses single-precision acos(dot); its last dot product rounds to one at this distance.
        Near(sample.BoneWorldMatrices[4].Translation, sample.BoneWorldMatrices[3].Translation, .0005f);
    }

    [Fact]
    public void ControllerLinkOrdinalScalesOriginalStepLimit()
    {
        // MMD 9.32 BA200 stops the controller when the first link's unit directions agree.
        RigBone[] bones = [new("base", new(1.5f, .1f, 0), -1), new("joint", Vector3.Zero, 0),
            new("tip", Vector3.UnitX, 1), new("goal", new(2, 0, 0), -1, Ik: new(2, 1, .1f, [new(1), new(0)]))];
        Near(Vector3.UnitX, Animator(bones).Sample(0).BoneWorldMatrices[2].Translation);

        // A zero-length effector direction skips only this link. Ordinal two still gets the .8rad cap.
        bones[1] = new("joint", Vector3.UnitX, 0);
        Near(new(1.5f - .5f * MathF.Cos(.8f) + .1f * MathF.Sin(.8f),
            .1f - .5f * MathF.Sin(.8f) - .1f * MathF.Cos(.8f), 0), Animator(bones).Sample(0).BoneWorldMatrices[2].Translation);
    }

    [Fact]
    public void ZeroIterationsAndDisabledIkPreserveUnconstrainedPathExactly()
    {
        var bones = OneLink(new(1, 2, 3), iterations: 0);
        BoneTrack[] tracks = [Track("joint", new(.2f, -.3f, .7f), Quaternion.CreateFromAxisAngle(Vector3.Normalize(new(2, 3, 1)), .9f)),
            Track("tip", new(.7f, .1f, -.2f), Quaternion.CreateFromAxisAngle(Vector3.Normalize(new(3, 1, 2)), .4f))];
        var plainBones = bones.Select(bone => bone with { Ik = null }).ToArray();
        var expected = Animator(plainBones, tracks).Sample(0);
        var actual = Animator(bones, tracks).Sample(0);
        // The legacy path remains byte exact when no new metadata exists; constrained quaternions
        // are normalized as part of solving, so compare the dormant constraint within float roundoff.
        for (int i = 0; i < bones.Length; i++) Near(expected.BoneWorldMatrices[i].Translation, actual.BoneWorldMatrices[i].Translation);
        Assert.Equal(expected.BoneWorldMatrices, Animator(plainBones, tracks).Sample(0, new(EnableIk: false)).BoneWorldMatrices);
    }

    [Fact]
    public void NegativeAppendWeightsExtrapolateAndSeekResetsGrantState()
    {
        var animator = Animator([new("source", Vector3.Zero, -1),
            new("grant", Vector3.Zero, -1, Append: new(0, -1, false, true, true))],
            new BoneTrack("source", [new(0, Vector3.Zero, Quaternion.Identity, BoneCurves.Linear),
                new(10, new(2, 4, 6), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .7f), BoneCurves.Linear)]));
        var last = animator.Sample(10);
        Near(new(-2, -4, -6), last.BoneWorldMatrices[1].Translation);
        Near(Vector3.Transform(Vector3.UnitX, Quaternion.CreateFromAxisAngle(Vector3.UnitY, -.7f)), Direction(last.BoneWorldMatrices[1], Vector3.UnitX));
        Assert.Equal(Matrix4x4.Identity, animator.Sample(0).BoneWorldMatrices[1]);
        Assert.Equal(last.BoneWorldMatrices, animator.Sample(10).BoneWorldMatrices);
    }

    [Fact]
    public void SolvedIkWorldDrivesBothBdefAndSdefVerticesAndNormals()
    {
        var vertex = new SceneVertex(Vector3.UnitX, Vector3.UnitX, Vector2.Zero);
        SkinBinding[] bindings = [new(SkinningKind.Bdef1, 1, -1, -1, -1, new(1, 0, 0, 0)),
            new(SkinningKind.Sdef, 0, 1, -1, -1, new(.5f, .5f, 0, 0))];
        var rig = new CharacterRig(OneLink(Vector3.UnitY), [vertex, vertex], bindings);
        var result = new CharacterAnimator(rig, new([])).Sample(0);
        foreach (var skinned in result.Vertices) { Near(Vector3.UnitY, skinned.Position); Near(Vector3.UnitY, skinned.Normal); }
    }

    [Fact]
    public void OriginalKnownAppendTranslationUsesSourceSampledDelta()
    {
        var animator = Animator([new("source", Vector3.Zero, -1),
            new("grant", Vector3.Zero, -1, Append: new(0, .25f, false, false, true))],
            Track("source", new(4, 6, 8)), Track("grant", new(2, 3, 4)));
        Near(new(3, 4.5f, 6), animator.Sample(0).BoneWorldMatrices[1].Translation);
    }

    [Fact]
    public void HalfZeroMultiAxisLimitsUseGeneralSolverAndReportCompatibilityBoundary()
    {
        var animator = Animator(OneLink(-Vector3.UnitZ, Vector3.Zero, new(.1f, .4f, 0)));
        Near(new(MathF.Cos(.4f), 0, -MathF.Sin(.4f)), animator.Sample(0).BoneWorldMatrices[1].Translation);
        Assert.Contains(animator.Diagnostics, diagnostic => diagnostic.Contains("alternative Euler orders", StringComparison.Ordinal));
    }
}
