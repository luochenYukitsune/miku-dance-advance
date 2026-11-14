using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class InteractiveBoneAnimationTests
{
    private static readonly SceneVertex Vertex = new(new(1, 2, 3), Vector3.UnitY, new(.2f, .7f));
    private static VmdBoneKeyframe Key(string name, uint frame, Vector3 translation, Quaternion rotation)
    {
        var interpolation = new byte[64];
        for (int channel = 0; channel < 4; channel++)
        {
            interpolation[channel * 16 + 8] = 127;
            interpolation[channel * 16 + 12] = 127;
        }
        return new() { BoneName = name, Frame = frame, Translation = translation, Rotation = rotation, Interpolation = interpolation };
    }

    private static CharacterRig Rig(BoneEvaluationSchedule schedule, bool constrained, bool afterPhysics = false)
        => new([
            new("root", Vector3.Zero, -1), new("tip", Vector3.UnitX, 0),
            new("goal", Vector3.UnitY, -1, AfterPhysics: afterPhysics,
                Ik: constrained ? new(1, 16, .4f, [new(0)]) : null),
            new("grant", new(2, 0, 0), -1, AfterPhysics: afterPhysics,
                Append: constrained ? new(0, .4f, false, true, true) : null)],
            Enumerable.Repeat(Vertex, 4), Enumerable.Range(0, 4)
                .Select(index => new SkinBinding(SkinningKind.Bdef1, index, -1, -1, -1, new(1, 0, 0, 0))),
            evaluationSchedule: schedule);

    private static VmdMotion Motion() => new()
    {
        BoneKeyframes = [Key("root", 0, new(1, 0, 0), Quaternion.Identity),
            Key("root", 20, new(2, 3, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .8f)),
            Key("goal", 0, Vector3.Zero, Quaternion.Identity), Key("grant", 0, new(.2f, .1f, 0), Quaternion.Identity)]
    };

    private static MorphPose Morphs(bool enabled) => new(Enumerable.Repeat(Vector3.Zero, 4),
        [enabled ? new(.1f, .2f, -.1f) : Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero],
        [enabled ? Quaternion.CreateFromAxisAngle(Vector3.UnitX, .31f) : Quaternion.Identity,
            Quaternion.Identity, Quaternion.Identity, Quaternion.Identity]);

    private static void EqualSample(CharacterSample expected, CharacterSample actual)
    {
        Assert.Equal(expected.BoneWorldMatrices, actual.BoneWorldMatrices);
        Assert.Equal(expected.SkinMatrices, actual.SkinMatrices);
        Assert.Equal(expected.Vertices, actual.Vertices);
    }

    [Theory]
    [InlineData(BoneEvaluationSchedule.Saba, false, false)]
    [InlineData(BoneEvaluationSchedule.Mmd, false, false)]
    [InlineData(BoneEvaluationSchedule.Saba, false, true)]
    [InlineData(BoneEvaluationSchedule.Mmd, false, true)]
    [InlineData(BoneEvaluationSchedule.Saba, true, false)]
    [InlineData(BoneEvaluationSchedule.Mmd, true, false)]
    [InlineData(BoneEvaluationSchedule.Saba, true, true)]
    [InlineData(BoneEvaluationSchedule.Mmd, true, true)]
    public void AbsoluteOverridesMatchTemporaryVmdKeysBeforeMorphAppendAndIk(
        BoneEvaluationSchedule schedule, bool constrained, bool boneMorphs)
    {
        var rig = Rig(schedule, constrained);
        var motion = Motion();
        var overrides = new Dictionary<int, BoneLocalTransform>
        {
            [0] = new(new(.3f, -.2f, .1f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .37f) * 7),
            [2] = new(new(.4f, .7f, .2f), Quaternion.Identity)
        };
        var temporary = new VmdMotion { BoneKeyframes = [.. motion.BoneKeyframes,
            Key("root", 9, overrides[0].Translation, overrides[0].Rotation),
            Key("goal", 9, overrides[2].Translation, overrides[2].Rotation)] };
        var options = new CharacterEvaluationOptions(Morphs: Morphs(boneMorphs));
        var animator = new CharacterAnimator(rig, MmdBoneMotionAdapter.Convert(motion));
        var expected = new CharacterAnimator(rig, MmdBoneMotionAdapter.Convert(temporary)).Sample(9, options);
        EqualSample(expected, animator.Sample(9, options with { LocalBoneOverrides = overrides }));
        // Interactive values are absolute raw motion values; they never edit the clip or accumulate.
        EqualSample(new CharacterAnimator(rig, MmdBoneMotionAdapter.Convert(motion)).Sample(9, options), animator.Sample(9, options));
    }

    [Theory]
    [InlineData(BoneEvaluationSchedule.Saba, false)]
    [InlineData(BoneEvaluationSchedule.Mmd, false)]
    [InlineData(BoneEvaluationSchedule.Saba, true)]
    [InlineData(BoneEvaluationSchedule.Mmd, true)]
    public void PhysicsPhasesKeepAnOwnedOverrideSnapshotAndMatchTemporaryVmd(
        BoneEvaluationSchedule schedule, bool boneMorphs)
    {
        var rig = Rig(schedule, true, afterPhysics: true);
        var motion = Motion();
        var replacement = new BoneLocalTransform(new(.3f, -.2f, .1f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .37f) * 7);
        var overrides = new Dictionary<int, BoneLocalTransform>
        {
            [0] = replacement,
            [2] = new(new(.4f, .7f, .2f), Quaternion.Identity),
            [3] = new(new(.2f, -.3f, .1f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -.2f))
        };
        var options = new CharacterEvaluationOptions(Morphs: Morphs(boneMorphs));
        var temporary = new VmdMotion { BoneKeyframes = [.. motion.BoneKeyframes,
            Key("root", 9, replacement.Translation, replacement.Rotation),
            Key("goal", 9, overrides[2].Translation, overrides[2].Rotation),
            Key("grant", 9, overrides[3].Translation, overrides[3].Rotation)] };
        var expectedAnimator = new CharacterAnimator(rig, MmdBoneMotionAdapter.Convert(temporary));
        using var expected = expectedAnimator.BeginPhysicsSample(9, options);
        var animator = new CharacterAnimator(rig, MmdBoneMotionAdapter.Convert(motion));
        using var actual = animator.BeginPhysicsSample(9, options with { LocalBoneOverrides = overrides });
        Assert.Equal(expected.BeforePhysicsBoneWorldMatrices.ToArray(), actual.BeforePhysicsBoneWorldMatrices.ToArray());
        overrides[0] = new(new(100, 200, 300), Quaternion.Identity);
        overrides[2] = new(new(-100, -200, -300), Quaternion.Identity);
        overrides.Remove(3);
        overrides[999] = default;
        EqualSample(expected.Complete(expected.BeforePhysicsBoneWorldMatrices, []),
            actual.Complete(actual.BeforePhysicsBoneWorldMatrices, []));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyOverrideOptionsPreserveExistingSamplesExactly(bool constrained)
    {
        var animator = new CharacterAnimator(Rig(BoneEvaluationSchedule.Mmd, constrained), MmdBoneMotionAdapter.Convert(Motion()));
        foreach (double frame in new[] { 0, 2.5, 9, 20 })
        {
            EqualSample(animator.Sample(frame), animator.Sample(frame, new(LocalBoneOverrides: new Dictionary<int, BoneLocalTransform>())));
            using var expected = animator.BeginPhysicsSample(frame);
            using var actual = animator.BeginPhysicsSample(frame, new(LocalBoneOverrides: new Dictionary<int, BoneLocalTransform>()));
            EqualSample(expected.Complete(expected.BeforePhysicsBoneWorldMatrices, []),
                actual.Complete(actual.BeforePhysicsBoneWorldMatrices, []));
        }
    }

    [Theory]
    [InlineData(float.MaxValue)]
    [InlineData(float.Epsilon)]
    public void FiniteExtremeRotationsUseTheSameNormalizationAsBoneKeyframe(float component)
    {
        var rig = Rig(BoneEvaluationSchedule.Saba, false);
        var replacement = new BoneLocalTransform(new(2, 3, 4), new(component, component, component, component));
        var temporary = new VmdMotion { BoneKeyframes = [Key("root", 0, replacement.Translation, replacement.Rotation)] };
        var animator = new CharacterAnimator(rig, new([]));
        var expected = new CharacterAnimator(rig, MmdBoneMotionAdapter.Convert(temporary)).Sample(0);
        EqualSample(expected, animator.Sample(0, new(LocalBoneOverrides: new Dictionary<int, BoneLocalTransform> { [0] = replacement })));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void InvalidOverridesFailBeforePublishingAnyPose(int invalidCase)
    {
        (int index, Vector3 translation, Quaternion rotation) = invalidCase switch
        {
            0 => (-1, Vector3.Zero, Quaternion.Identity),
            1 => (4, Vector3.Zero, Quaternion.Identity),
            2 => (0, new(float.NaN, 0, 0), Quaternion.Identity),
            3 => (0, new(0, float.PositiveInfinity, 0), Quaternion.Identity),
            4 => (0, Vector3.Zero, default),
            5 => (0, Vector3.Zero, new(float.NaN, 0, 0, 1)),
            _ => (0, Vector3.Zero, new(0, 0, 0, float.NegativeInfinity))
        };
        var animator = new CharacterAnimator(Rig(BoneEvaluationSchedule.Saba, false), MmdBoneMotionAdapter.Convert(Motion()));
        var expected = animator.Sample(9);
        var options = new CharacterEvaluationOptions(LocalBoneOverrides: new Dictionary<int, BoneLocalTransform> { [index] = new(translation, rotation) });
        Assert.Throws<ArgumentException>(() => animator.Sample(9, options));
        Assert.Throws<ArgumentException>(() => animator.BeginPhysicsSample(9, options));
        EqualSample(expected, animator.Sample(9));
    }
}
