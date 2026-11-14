using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class CharacterMorphAnimationTests
{
    private static BoneTrack Track(string name, Vector3 translation, Quaternion? rotation = null) => new(name,
        [new(0, translation, rotation ?? Quaternion.Identity, BoneCurves.Linear)]);
    private static void Near(Vector3 expected, Vector3 actual, float tolerance = .00005f)
        => Assert.True(Vector3.Distance(expected, actual) < tolerance, $"Expected {expected}; actual {actual}");
    private static void Same(CharacterSample expected, CharacterSample actual)
    {
        Assert.Equal(expected.Vertices, actual.Vertices); Assert.Equal(expected.BoneWorldMatrices, actual.BoneWorldMatrices);
        Assert.Equal(expected.SkinMatrices, actual.SkinMatrices);
    }
    private static CharacterRig Rig(bool constrained = false)
    {
        var vertex = new SceneVertex(new(4, 7, 9), Vector3.Normalize(new(1, 2, 3)), new(.2f, .8f));
        return new([new("root", new(2, -1, 3), -1), new("child", new(4, 2, 5), 0, AfterPhysics: true,
                Append: constrained ? new(0, .3f, false, true, true) : null)], Enumerable.Repeat(vertex, 4),
            [new(SkinningKind.Bdef1, 0, -1, -1, -1, new(1, 0, 0, 0)),
             new(SkinningKind.Bdef2, 0, 1, -1, -1, new(.3f, .7f, 0, 0)),
             new(SkinningKind.Bdef4, 0, 1, -1, -1, new(.4f, .6f, 0, 0)),
             new(SkinningKind.Sdef, 0, 1, -1, -1, new(.3f, .7f, 0, 0), new(1, 2, 3), new(2, 4, 6), new(-3, 1, 5))]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroAndAbsentMorphsKeepEveryLegacySkinningPathExact(bool constrained)
    {
        var rig = Rig(constrained);
        var animator = new CharacterAnimator(rig, new([new("root", [
            new(0, Vector3.UnitX, new(.13f, .47f, .63f, .29f), BoneCurves.Linear),
            new(10, new(0, 3, 2), new(.61f, -.31f, .2f, -.7f), BoneCurves.Linear)])]));
        var zero = new MorphPose(new Vector3[4], new Vector3[2], [Quaternion.Identity, -Quaternion.Identity]);
        foreach (double frame in new[] { -1, 0, 2.5, 10, 50 })
        {
            var expected = animator.Sample(frame);
            Same(expected, animator.Sample(frame, new()));
            Same(expected, animator.Sample(frame, new(Morphs: zero)));
            using var evaluation = animator.BeginPhysicsSample(frame, new(Morphs: zero));
            Same(expected, evaluation.Complete(evaluation.BeforePhysicsBoneWorldMatrices, []));
        }
    }

    [Fact]
    public void VertexMorphDeltaIsTransformedBeforeEachBdefAndSphericalSdefSkinning()
    {
        var rig = Rig();
        var animator = new CharacterAnimator(rig, new([Track("root", new(2, 3, 4), Quaternion.CreateFromAxisAngle(Vector3.UnitX, .6f)),
            Track("child", new(-1, 2, 1), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.1f))]));
        var delta = new Vector3(.7f, -1.3f, 2.1f);
        var morphs = new MorphPose(Enumerable.Repeat(delta, 4), new Vector3[2], [Quaternion.Identity, Quaternion.Identity]);
        var baseline = animator.Sample(0); var actual = animator.Sample(0, new(Morphs: morphs));
        for (int i = 0; i < 3; i++)
        {
            var binding = rig.Bindings[i];
            var offset = Vector3.TransformNormal(delta, baseline.SkinMatrices[0]) * binding.Weights.X
                + (binding.Weights.Y == 0 ? Vector3.Zero : Vector3.TransformNormal(delta, baseline.SkinMatrices[1]) * binding.Weights.Y);
            Near(baseline.Vertices[i].Position + offset, actual.Vertices[i].Position);
        }
        var q0 = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(baseline.BoneWorldMatrices[0]));
        var q1 = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(baseline.BoneWorldMatrices[1]));
        var sphericalOffset = Vector3.Transform(delta, Quaternion.Normalize(Quaternion.Slerp(q0, q1, .7f)));
        Near(baseline.Vertices[3].Position + sphericalOffset, actual.Vertices[3].Position);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(baseline.Vertices[i].Normal, actual.Vertices[i].Normal);
            Assert.Equal(baseline.Vertices[i].Uv, actual.Vertices[i].Uv);
        }
        Assert.Equal(baseline.BoneWorldMatrices, actual.BoneWorldMatrices);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoneMorphBaseRotationPrecedesVmdInOriginalHamiltonOrder(bool constrained)
    {
        var vmd = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .7f);
        var morph = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .9f);
        var rig = new CharacterRig([new("root", new(2, 3, 4), -1),
            new("child", new(2, 3, 5), 0, Append: constrained ? new(0, 0, false, false, false) : null)],
            [new SceneVertex(new(3, 3, 4), Vector3.UnitZ, Vector2.Zero)],
            [new(SkinningKind.Bdef1, 0, -1, -1, -1, new(1, 0, 0, 0))]);
        var animator = new CharacterAnimator(rig, new([Track("root", new(1, 2, 3), vmd)]));
        var morphs = new MorphPose([new(.2f, .3f, .4f)], [new(-2, .5f, 1), Vector3.Zero], [morph, Quaternion.Identity]);
        var actual = animator.Sample(0, new(Morphs: morphs));
        var combined = Quaternion.Normalize(morph * vmd);
        Near(new(1, 5.5f, 8), actual.BoneWorldMatrices[0].Translation);
        Near(new Vector3(1, 5.5f, 8) + Vector3.Transform(new Vector3(1.2f, .3f, .4f), combined), actual.Vertices[0].Position);
        Near(Vector3.Transform(Vector3.UnitZ, combined), actual.Vertices[0].Normal);
        Assert.True(Vector3.Distance(Vector3.Transform(Vector3.UnitZ, vmd * morph), Vector3.Transform(Vector3.UnitZ, morph * vmd)) > .1f);
    }

    [Fact]
    public void AppendReadsTheCombinedVmdMorphSourceTransform()
    {
        var vmd = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .7f); var morph = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .9f);
        var rig = new CharacterRig([new("source", Vector3.Zero, -1),
            new("grant", Vector3.Zero, -1, Append: new(0, .5f, false, true, true))], [], []);
        var animator = new CharacterAnimator(rig, new([Track("source", new(2, 4, 6), vmd)]));
        var actual = animator.Sample(0, new(Morphs: new([], [new(4, 2, -2), Vector3.Zero], [morph, Quaternion.Identity])));
        Near(new(3, 3, 2), actual.BoneWorldMatrices[1].Translation);
        Near(Vector3.Transform(Vector3.UnitZ, Quaternion.Slerp(Quaternion.Identity, morph * vmd, .5f)),
            Vector3.TransformNormal(Vector3.UnitZ, actual.BoneWorldMatrices[1]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IkCompensatesCombinedVmdAndMorphBaseRotation(bool planar)
    {
        var link = planar ? new IkLinkDefinition(0, new(0, 0, -MathF.PI), new(0, 0, MathF.PI)) : new(0);
        var rig = new CharacterRig([new("joint", Vector3.Zero, -1), new("tip", Vector3.UnitX, 0),
            new("goal", Vector3.UnitY, -1, Ik: new(1, 64, MathF.PI, [link]))], [], []);
        var animator = new CharacterAnimator(rig, new([Track("joint", Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitX, .35f))]));
        var morphs = new MorphPose([], new Vector3[3], [Quaternion.CreateFromAxisAngle(Vector3.UnitY, .42f), Quaternion.Identity, Quaternion.Identity]);
        var actual = animator.Sample(0, new(Morphs: morphs));
        Near(actual.BoneWorldMatrices[2].Translation, actual.BoneWorldMatrices[1].Translation, .0002f);
        var disabled = animator.Sample(0, new(EnableIk: false, Morphs: morphs));
        Near(Vector3.Transform(Vector3.UnitX, Quaternion.CreateFromAxisAngle(Vector3.UnitY, .42f) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, .35f)),
            disabled.BoneWorldMatrices[1].Translation);
    }

    [Fact]
    public void MorphsAreVisibleToBeforePhysicsAndPreservedBySharedCompletionSkinning()
    {
        var rig = new CharacterRig([new("root", Vector3.Zero, -1), new("child", Vector3.UnitX, 0)],
            [new SceneVertex(new(2, 0, 0), Vector3.UnitY, Vector2.Zero)],
            [new(SkinningKind.Bdef1, 1, -1, -1, -1, new(1, 0, 0, 0))]);
        var animator = new CharacterAnimator(rig, new([]));
        var options = new CharacterEvaluationOptions(Morphs: new([Vector3.UnitY], [new(3, 4, 5), Vector3.UnitY],
            [Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), Quaternion.Identity]));
        using var evaluation = animator.BeginPhysicsSample(0, options);
        Near(new(3, 4, 5), evaluation.BeforePhysicsBoneWorldMatrices[0].Translation);
        Near(new(2, 5, 5), evaluation.BeforePhysicsBoneWorldMatrices[1].Translation);
        var expected = animator.Sample(0, options);
        Same(expected, evaluation.Complete(evaluation.BeforePhysicsBoneWorldMatrices, []));
        using var reflected = animator.BeginPhysicsSample(0, options);
        var physics = reflected.BeforePhysicsBoneWorldMatrices.ToArray(); physics[0] = Matrix4x4.CreateTranslation(10, 0, 0);
        var result = reflected.Complete(physics, [0]);
        Near(new(12, 2, 0), result.Vertices[0].Position);
    }

    [Fact]
    public void PoseCopiesInputsAndAlternatingSamplesNeverAccumulateOrMutateRig()
    {
        Vector3[] deltas = [new(1, 2, 3)]; Vector3[] translations = [new(2, 3, 4)];
        Quaternion[] rotations = [Quaternion.CreateFromAxisAngle(Vector3.UnitY, .6f)];
        var pose = new MorphPose(deltas, translations, rotations);
        deltas[0] = new(999); translations[0] = new(888); rotations[0] = Quaternion.Identity;
        Assert.Equal(new(1, 2, 3), pose.VertexDeltas[0]); Assert.Equal(new(2, 3, 4), pose.BoneTranslations[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<Vector3>)pose.VertexDeltas)[0] = Vector3.Zero);
        var rig = new CharacterRig([new("root", Vector3.Zero, -1)], [new SceneVertex(Vector3.UnitX, Vector3.UnitZ, Vector2.Zero)],
            [new(SkinningKind.Bdef1, 0, -1, -1, -1, new(1, 0, 0, 0))]);
        var animator = new CharacterAnimator(rig, new([])); var baseline = animator.Sample(0); var options = new CharacterEvaluationOptions(Morphs: pose);
        var first = animator.Sample(0, options);
        for (int i = 0; i < 3; i++)
        {
            Same(baseline, animator.Sample(0)); Same(first, animator.Sample(100, options));
            using var evaluation = animator.BeginPhysicsSample(0, options);
            Same(first, evaluation.Complete(evaluation.BeforePhysicsBoneWorldMatrices, []));
        }
        Assert.Equal(Vector3.UnitX, rig.Vertices[0].Position);
        Near(new Vector3(2, 3, 4) + Vector3.Transform(new Vector3(2, 2, 3), pose.BoneRotations[0]), first.Vertices[0].Position);
    }

    [Fact]
    public void PoseRejectsNonFiniteZeroQuaternionAndMismatchedRigCounts()
    {
        Assert.Throws<ArgumentNullException>(() => new MorphPose(null!, [], []));
        Assert.Throws<ArgumentException>(() => new MorphPose([new(float.NaN, 0, 0)], [], []));
        Assert.Throws<ArgumentException>(() => new MorphPose([], [new(float.PositiveInfinity, 0, 0)], [Quaternion.Identity]));
        Assert.Throws<ArgumentException>(() => new MorphPose([], [Vector3.Zero], [default]));
        Assert.Throws<ArgumentException>(() => new MorphPose([], [Vector3.Zero], [new(float.NaN, 0, 0, 1)]));
        Assert.Throws<ArgumentException>(() => new MorphPose([], [Vector3.Zero], []));
        var normalized = new MorphPose([], [Vector3.Zero], [new(float.MaxValue, -float.MaxValue, float.MaxValue, float.MaxValue)]);
        Assert.Equal(new(.5f, -.5f, .5f, .5f), normalized.BoneRotations[0]);
        var animator = new CharacterAnimator(Rig(), new([]));
        var wrong = new CharacterEvaluationOptions(Morphs: new([], [], []));
        Assert.Throws<ArgumentException>(() => animator.Sample(0, wrong));
        Assert.Throws<ArgumentException>(() => animator.BeginPhysicsSample(0, wrong));
    }
}
