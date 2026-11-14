using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class QdefSkinningTests
{
    private static SkinBinding Binding(Vector4 weights, int a = 0, int b = 1, int c = 2, int d = 3)
        => new(SkinningKind.Qdef, a, b, c, d, weights);
    private static SceneVertex Vertex(Vector3 p, Vector3? normal = null) => new(p, normal ?? Vector3.UnitX, new(.25f, .75f));
    private static BoneTrack Track(string name, Vector3 translation, Quaternion rotation) => new(name,
        [new(0, translation, rotation, BoneCurves.Linear)]);
    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0, 3e-5f);

    [Fact]
    public void SingleBoneUsesInverseBindAndMatchesRigidPointAndNormalRotation()
    {
        var rest = new Vector3(5, 2, 0); var vertex = Vertex(rest + Vector3.UnitX);
        var rig = new CharacterRig([new("bone", rest, -1)], [vertex], [Binding(new(1, 0, 0, 0), 0, -1, -1, -1)]);
        var animator = new CharacterAnimator(rig, new([Track("bone", new(2, 3, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2))]));
        var sample = animator.Sample(0);
        Near(new(7, 6, 0), sample.Vertices[0].Position); Near(Vector3.UnitY, sample.Vertices[0].Normal);
        Assert.Equal(vertex.Uv, sample.Vertices[0].Uv);
        Assert.Equal(sample.Vertices, animator.Sample(0).Vertices);
    }

    [Fact]
    public void OpposedTwistsKeepRadiusRatherThanLinearBlendCollapse()
    {
        RigBone[] bones = [new("a", Vector3.Zero, -1), new("b", Vector3.Zero, -1)];
        var qdef = Binding(new(.5f, .5f, 0, 0), 0, 1, -1, -1);
        var bdef = qdef with { Kind = SkinningKind.Bdef4 };
        var rig = new CharacterRig(bones, [Vertex(Vector3.UnitX), Vertex(Vector3.UnitX)], [qdef, bdef]);
        var clip = new MotionClip([Track("a", Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)),
            Track("b", Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2))]);
        var sample = new CharacterAnimator(rig, clip).Sample(0);
        Near(Vector3.UnitX, sample.Vertices[0].Position);
        Assert.InRange(sample.Vertices[1].Position.Length(), 0, 1e-5f);
        Near(Vector3.UnitX, sample.Vertices[0].Normal);
    }

    [Fact]
    public void TranslationAndRotationBlendUseDualPartRatherThanIndependentTranslationLerp()
    {
        var rig = new CharacterRig([new("a", Vector3.Zero, -1), new("b", Vector3.Zero, -1)], [Vertex(Vector3.UnitX)],
            [Binding(new(.5f, .5f, 0, 0), 0, 1, -1, -1)]);
        var clip = new MotionClip([Track("a", new(2, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)),
            Track("b", new(-2, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2))]);
        var sample = new CharacterAnimator(rig, clip).Sample(0);
        Near(new(1, -2, 0), sample.Vertices[0].Position); Near(Vector3.UnitX, sample.Vertices[0].Normal);
    }

    [Fact]
    public void FourBonesAndZeroInfluencePlaceholdersBlendTranslationsExactly()
    {
        var bones = Enumerable.Range(0, 4).Select(i => new RigBone(i.ToString(), new(i, i * 2, i * 3), -1));
        var rig = new CharacterRig(bones, [Vertex(new(1, 2, 3)), Vertex(Vector3.Zero)],
            [Binding(new(.1f, .2f, .3f, .4f)), Binding(new(0, 0, 0, 1), -1, 999, -1, 3)]);
        var clip = new MotionClip(Enumerable.Range(0, 4).Select(i => Track(i.ToString(), new(i, i * 2, i * 3), Quaternion.Identity)));
        var sample = new CharacterAnimator(rig, clip).Sample(0);
        Near(new(3, 6, 9), sample.Vertices[0].Position); Near(new(3, 6, 9), sample.Vertices[1].Position);
    }

    [Fact]
    public void QuaternionSignAndFirstZeroWeightDoNotChangeHemisphereBlend()
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new(1, 2, 3)), 3.5f);
        var rig = new CharacterRig([new("a", Vector3.Zero, -1), new("b", Vector3.Zero, -1)], [Vertex(new(2, 3, 4))],
            [Binding(new(0, .4f, .6f, 0), -1, 0, 1, -1)]);
        var first = new CharacterAnimator(rig, new([Track("a", new(3, -2, 1), rotation), Track("b", new(3, -2, 1), -rotation)])).Sample(0);
        var second = new CharacterAnimator(rig, new([Track("a", new(3, -2, 1), -rotation), Track("b", new(3, -2, 1), rotation)])).Sample(0);
        Near(Vector3.Transform(new Vector3(2, 3, 4), rotation) + new Vector3(3, -2, 1), first.Vertices[0].Position);
        Near(first.Vertices[0].Position, second.Vertices[0].Position); Near(first.Vertices[0].Normal, second.Vertices[0].Normal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NonRigidExternalScaleShearAndReflectionAreExplicitlyRejected(int mode)
    {
        var rig = new CharacterRig([new("bone", Vector3.Zero, -1)], [Vertex(Vector3.UnitX)], [Binding(new(1, 0, 0, 0), 0, -1, -1, -1)]);
        var matrix = mode == 0 ? Matrix4x4.CreateScale(2) : mode == 1 ? Matrix4x4.Identity : Matrix4x4.CreateScale(-1, 1, 1);
        if (mode == 1) matrix.M12 = .2f;
        var animator = new CharacterAnimator(rig, new([]));
        Assert.Throws<NotSupportedException>(() => animator.Sample(0, new(OutsideParents: new Dictionary<int, Matrix4x4> { [-1] = matrix })));
    }

    [Fact]
    public void MultiAxisInverseBindAndNegativeHemisphereMatchNativeSabaGlmOracle()
    {
        // Independently generated by qdef-oracle.cpp using pinned Saba's GLM dualquat_cast,
        // hemisphere blend and mat3x4_cast. Extracted first pair dot = -0.994323313.
        Vector3[] axes = [new(1, -.9f, 0), new(-.9f, 1, 0), new(1, 2, 3), new(-2, 1, 2)];
        float[] angles = [3.12413936f, 3.12413936f, 1.3f, -2.1f];
        Vector3[] rest = [new(1, 2, 3), new(-2, 1, 4), new(3, -2, 1), new(-1, -3, 2)];
        Vector3[] shifts = [new(2, -1, 3), new(-3, 2, 1), new(1, 4, -2), new(-2, -1, 3)];
        var rig = new CharacterRig(Enumerable.Range(0, 4).Select(i => new RigBone(i.ToString(), rest[i], -1)),
            [Vertex(new(2, 3, 4), Vector3.Normalize(new(1, 2, -1)))], [Binding(new(.2f, .3f, .1f, .4f))]);
        var clip = new MotionClip(Enumerable.Range(0, 4).Select(i => Track(i.ToString(), shifts[i],
            Quaternion.CreateFromAxisAngle(Vector3.Normalize(axes[i]), angles[i]))));
        var sample = new CharacterAnimator(rig, clip).Sample(0);
        Assert.True(Quaternion.Dot(Quaternion.CreateFromRotationMatrix(sample.SkinMatrices[0]),
            Quaternion.CreateFromRotationMatrix(sample.SkinMatrices[1])) < -.99f);
        Near(new(-5.69018745f, -3.8527844f, 6.55878639f), sample.Vertices[0].Position);
        Near(new(-.350647032f, -.638359249f, .685232878f), sample.Vertices[0].Normal);
    }

    [Fact]
    public void InvalidQdefWeightsAndPositiveInfluenceIndicesRemainRejected()
    {
        RigBone[] bones = [new("bone", Vector3.Zero, -1)]; SceneVertex[] vertices = [Vertex(Vector3.Zero)];
        Assert.Throws<ArgumentException>(() => new CharacterRig(bones, vertices, [Binding(new(.5f, .6f, 0, 0), 0, 0, -1, -1)]));
        Assert.Throws<ArgumentException>(() => new CharacterRig(bones, vertices, [Binding(new(0, 1, 0, 0), 0, 1, -1, -1)]));
        Assert.Throws<ArgumentException>(() => new CharacterRig(bones, vertices, [Binding(new(float.NaN, 0, 0, 0), 0, -1, -1, -1)]));
    }
}
