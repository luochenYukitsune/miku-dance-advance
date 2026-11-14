using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class CharacterAnimationTests
{
    private static readonly BoneCurves Linear = BoneCurves.Linear;
    private static BoneKeyframe Key(uint frame, Vector3 position, Quaternion? rotation = null, BoneCurves? curves = null)
        => new(frame, position, rotation ?? Quaternion.Identity, curves ?? Linear);
    private static SkinBinding B1(int bone) => new(SkinningKind.Bdef1, bone, -1, -1, -1, new(1, 0, 0, 0));
    private static SceneVertex Vertex(Vector3 position, Vector3? normal = null) => new(position, normal ?? Vector3.UnitZ, new(.2f, .8f));
    private static CharacterRig OneRig() => new([new("root", Vector3.Zero, -1)], [Vertex(Vector3.UnitX)], [B1(0)]);
    private static void Near(Vector3 expected, Vector3 actual, float tolerance = .00002f)
        => Assert.True(Vector3.Distance(expected, actual) < tolerance, $"Expected {expected}, actual {actual}");

    [Fact]
    public void RestPosePreservesTranslatedHierarchyAndEverySkinningKind()
    {
        var bones = new RigBone[] { new("root", new(2, 3, 4), -1), new("child", new(3, 5, 6), 0) };
        var vertex = Vertex(new(4, 7, 9), Vector3.Normalize(new(1, 2, 3)));
        SkinBinding[] bindings = [B1(0), new(SkinningKind.Bdef2, 0, 1, -1, -1, new(.3f, .7f, 0, 0)),
            new(SkinningKind.Bdef4, 0, 1, -1, -1, new(.4f, .6f, 0, 0)),
            new(SkinningKind.Sdef, 0, 1, -1, -1, new(.3f, .7f, 0, 0), new(1, 2, 3), new(2, 4, 6), new(-3, 1, 5))];
        var rig = new CharacterRig(bones, Enumerable.Repeat(vertex, bindings.Length), bindings);
        var result = new CharacterAnimator(rig, new([])).Sample(0);
        foreach (var actual in result.Vertices) { Near(vertex.Position, actual.Position); Near(vertex.Normal, actual.Normal); Assert.Equal(vertex.Uv, actual.Uv); }
        Assert.Equal(Matrix4x4.Identity, result.SkinMatrices[1]);
    }

    [Fact]
    public void ParentRotationAndChildTranslationUseLocalRestAndRowVectors()
    {
        var rig = new CharacterRig([new("child", new(0, 2, 0), 1), new("root", new(0, 1, 0), -1)],
            [Vertex(new(0, 3, 2), Vector3.UnitY)], [B1(0)]);
        var clip = new MotionClip([new("root", [Key(0, new(2, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2))]),
            new("child", [Key(0, Vector3.UnitX)])]);
        var result = new CharacterAnimator(rig, clip).Sample(0);
        Near(new(0, 2, 2), result.Vertices[0].Position);
        Near(-Vector3.UnitX, result.Vertices[0].Normal);
        Near(new(1, 2, 0), result.BoneWorldMatrices[0].Translation);
    }

    [Fact]
    public void FourIndependentNonSymmetricCurvesAndOwnershipAreExplicit()
    {
        // x(t)=t^3, y(t)=t: at elapsed 1/8 the eased value is 1/2.
        var fast = new BezierCurve(0, 1f / 3, 0, 2f / 3);
        // x(t)=t, y(t)=t^3: at elapsed 1/8 the eased value is 1/512.
        var slow = new BezierCurve(1f / 3, 0, 2f / 3, 0);
        var curves = new BoneCurves(fast, slow, BezierCurve.Linear, fast);
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var track = new BoneTrack("root", [Key(0, Vector3.Zero), Key(8, new(8, 8, 8), turn, curves)]);
        var next = new CharacterAnimator(OneRig(), new([track]));
        var sample = next.Sample(1);
        Near(new(4, .015625f, 1), sample.BoneWorldMatrices[0].Translation);
        Near(new(4 + MathF.Sqrt(.5f), .015625f + MathF.Sqrt(.5f), 1), sample.Vertices[0].Position);
        var previous = new CharacterAnimator(OneRig(), new([track], SegmentCurveOwnership.Previous));
        Near(Vector3.One, previous.Sample(1).BoneWorldMatrices[0].Translation);
        Assert.Equal(SegmentCurveOwnership.Next, new MotionClip([track]).CurveOwnership);
    }

    [Fact]
    public void SamplingClampsEndpointsAndReverseSeeksAreExactAndIndependent()
    {
        var animator = new CharacterAnimator(OneRig(), new([new("root", [Key(10, new(1, 2, 3)), Key(20, new(9, 6, 7))])]));
        Near(new(2, 2, 3), animator.Sample(-100).Vertices[0].Position);
        Near(new(10, 6, 7), animator.Sample(100).Vertices[0].Position);
        var expected = animator.Sample(15.5);
        animator.Sample(100); animator.Sample(0);
        Assert.Equal(expected.Vertices, animator.Sample(15.5).Vertices);
        expected.Vertices[0] = default;
        Near(new(6.4f, 4.2f, 5.2f), animator.Sample(15.5).Vertices[0].Position);
    }

    [Fact]
    public void UintMaxFrameAndAntipodalQuaternionAreStable()
    {
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .8f);
        var track = new BoneTrack("root", [Key(uint.MaxValue - 2, Vector3.Zero, q), Key(uint.MaxValue, new(2, 0, 0), -q)]);
        var sample = new CharacterAnimator(OneRig(), new([track])).Sample(uint.MaxValue - 1d);
        Near(Vector3.Transform(Vector3.UnitX, q) + Vector3.UnitX, sample.Vertices[0].Position);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(1, 0, 1, 0)]
    [InlineData(0, 1, 0, 1)]
    [InlineData(1, 1, 0, 0)]
    public void ExtremeBezierCurvesAreFiniteBoundedMonotonicAndHaveExactEndpoints(float x1, float y1, float x2, float y2)
    {
        var curve = new BezierCurve(x1, y1, x2, y2);
        Assert.Equal(0, curve.Evaluate(-1)); Assert.Equal(1, curve.Evaluate(2));
        double last = 0;
        for (int i = 0; i <= 1000; i++) { double value = curve.Evaluate(i / 1000d); Assert.InRange(value, last - 1e-10, 1); last = value; }
    }

    [Fact]
    public void LinearBezierReturnsPreciseProgress()
    {
        for (int i = 0; i < 100; i++) Assert.Equal(i / 100d, BezierCurve.Linear.Evaluate(i / 100d), 12);
        Assert.Equal(.5, new BezierCurve(0, 1f / 3, 0, 2f / 3).Evaluate(.125), 6);
    }

    [Fact]
    public void BdefFourWeightsBlendTranslationsAndNormalizeNormals()
    {
        var bones = Enumerable.Range(0, 4).Select(i => new RigBone(i.ToString(), Vector3.Zero, -1));
        var rig = new CharacterRig(bones, [Vertex(Vector3.UnitX, Vector3.UnitY)],
            [new(SkinningKind.Bdef4, 0, 1, 2, 3, new(.1f, .2f, .3f, .4f))]);
        var tracks = Enumerable.Range(0, 4).Select(i => new BoneTrack(i.ToString(), [Key(0, new(i, 2 * i, 3 * i),
            i == 3 ? Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2) : Quaternion.Identity)]));
        var sample = new CharacterAnimator(rig, new(tracks)).Sample(0);
        Near(new(2.6f, 4.4f, 6), sample.Vertices[0].Position);
        Near(Vector3.Normalize(new(-.4f, .6f, 0)), sample.Vertices[0].Normal);
    }

    [Fact]
    public void SdefUsesSphericalRotationAndCorrectedRawAnchors()
    {
        var center = new Vector3(2, 1, 3); var r0 = new Vector3(4, 2, 5); var r1 = new Vector3(0, 4, 1);
        var rig = new CharacterRig([new("a", Vector3.Zero, -1), new("b", Vector3.Zero, -1)],
            [Vertex(center + Vector3.UnitX, Vector3.UnitX)],
            [new(SkinningKind.Sdef, 0, 1, -1, -1, new(.25f, .75f, 0, 0), center, r0, r1)]);
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var clip = new MotionClip([new("a", [Key(0, new(4, 0, 0))]), new("b", [Key(0, new(0, 2, 0), q)])]);
        var result = new CharacterAnimator(rig, clip).Sample(0);
        // Corrected anchor0=(3.5,.25,4.5), anchor1=(1.5,1.25,2.5).
        var blendedAnchor = new Vector3(7.5f, .25f, 4.5f) * .25f + new Vector3(-1.25f, 3.5f, 2.5f) * .75f;
        var expectedDirection = new Vector3(MathF.Cos(3 * MathF.PI / 8), MathF.Sin(3 * MathF.PI / 8), 0);
        Near(blendedAnchor + expectedDirection, result.Vertices[0].Position);
        Near(expectedDirection, result.Vertices[0].Normal);
    }

    [Fact]
    public void ZeroWeightMissingBonesAreIgnoredIncludingSdefEndpoints()
    {
        foreach (var kind in new[] { SkinningKind.Bdef2, SkinningKind.Bdef4, SkinningKind.Sdef })
        {
            var rig = new CharacterRig([new("root", Vector3.Zero, -1)], [Vertex(Vector3.UnitX)],
                [new(kind, 0, -1, -1, -1, new(1, 0, 0, 0), new(2, 3, 4), new(5, 6, 7), new(8, 9, 10))]);
            Near(new(3, 0, 0), new CharacterAnimator(rig, new([new("root", [Key(0, new(2, 0, 0))])])).Sample(0).Vertices[0].Position);
        }
    }

    [Fact]
    public void RigAndClipCopyInputCollectionsAndSortKeys()
    {
        BoneKeyframe[] keys = [Key(20, new(2, 0, 0)), Key(0, Vector3.Zero)];
        var track = new BoneTrack("root", keys); keys[0] = Key(20, new(999));
        BoneTrack[] tracks = [track]; var clip = new MotionClip(tracks); tracks[0] = new("other", []);
        SceneVertex[] vertices = [Vertex(Vector3.UnitX)]; RigBone[] bones = [new("root", Vector3.Zero, -1)]; SkinBinding[] bindings = [B1(0)];
        var rig = new CharacterRig(bones, vertices, bindings); vertices[0] = default; bones[0] = new("changed", Vector3.Zero, -1); bindings[0] = default;
        Near(new(2, 0, 0), new CharacterAnimator(rig, clip).Sample(10).Vertices[0].Position);
        Assert.Equal(20u, clip.DurationFrames);
    }

    [Fact]
    public void MissingTracksAndUnsupportedRigBehaviorHaveExplicitDiagnostics()
    {
        var rig = new CharacterRig([new("root", Vector3.Zero, -1)], [], [],
            UnsupportedRigFeatures.InverseKinematics | UnsupportedRigFeatures.AppendTransform | UnsupportedRigFeatures.Physics | UnsupportedRigFeatures.ExternalParent);
        var animator = new CharacterAnimator(rig, new([new("missing", [Key(0, Vector3.Zero)])]));
        Assert.Contains(animator.Diagnostics, d => d.Contains("missing", StringComparison.Ordinal));
        foreach (string feature in new[] { "InverseKinematics", "AppendTransform", "Physics", "ExternalParent" })
            Assert.Contains(animator.Diagnostics, d => d.Contains(feature, StringComparison.Ordinal));
        Assert.Empty(animator.Sample(0).Vertices);
    }

    [Fact]
    public void InvalidHierarchiesAndWeightsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new CharacterRig([new("a", Vector3.Zero, 1), new("b", Vector3.Zero, 0)], [], []));
        Assert.Throws<ArgumentException>(() => new CharacterRig([new("a", Vector3.Zero, 4)], [], []));
        Assert.Throws<ArgumentException>(() => new CharacterRig([new("a", Vector3.Zero, -1), new("a", Vector3.Zero, -1)], [], []));
        Assert.Throws<ArgumentException>(() => new CharacterRig([new("a", Vector3.Zero, -1)], [Vertex(Vector3.Zero)], []));
        foreach (var binding in new[] { B1(-1), B1(1), default, new(SkinningKind.Bdef2, 0, 0, -1, -1, new(.1f, .2f, 0, 0)),
            new(SkinningKind.Bdef4, 0, 0, 0, 0, new(-.1f, .3f, .4f, .4f)) })
            Assert.Throws<ArgumentException>(() => new CharacterRig([new("a", Vector3.Zero, -1)], [Vertex(Vector3.Zero)], [binding]));
    }

    [Fact]
    public void InvalidKeyframesCurvesFramesAndDuplicatesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Key(0, new(float.NaN, 0, 0)));
        Assert.Throws<ArgumentException>(() => Key(0, Vector3.Zero, new Quaternion()));
        Assert.Throws<ArgumentException>(() => Key(0, Vector3.Zero, new Quaternion(float.PositiveInfinity, 0, 0, 1)));
        Assert.Equal(new Quaternion(.5f, -.5f, .5f, .5f),
            Key(0, Vector3.Zero, new Quaternion(float.MaxValue, -float.MaxValue, float.MaxValue, float.MaxValue)).Rotation);
        Assert.Equal(new Quaternion(.5f, .5f, .5f, .5f),
            Key(0, Vector3.Zero, new Quaternion(float.Epsilon, float.Epsilon, float.Epsilon, float.Epsilon)).Rotation);
        Assert.Throws<ArgumentOutOfRangeException>(() => new BezierCurve(-1, 0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BezierCurve(0, float.NaN, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => BezierCurve.Linear.Evaluate(double.NaN));
        Assert.Throws<ArgumentException>(() => new BoneTrack("root", [Key(1, Vector3.Zero), Key(1, Vector3.One)]));
        Assert.Throws<ArgumentException>(() => new MotionClip([new("root", []), new("root", [])]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MotionClip([], (SegmentCurveOwnership)42));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterAnimator(OneRig(), new([])).Sample(double.PositiveInfinity));
    }
}
