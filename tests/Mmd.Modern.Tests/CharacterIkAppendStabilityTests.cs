using System.Numerics;
using Mmd.Modern.Animation;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class CharacterIkAppendStabilityTests
{
    private static BoneTrack Track(string name, Quaternion rotation) => new(name,
        [new(0, Vector3.Zero, rotation, BoneCurves.Linear)]);

    private static CharacterAnimator Animator(RigBone[] bones, params BoneTrack[] tracks)
        => new(new(bones, [], []), new(tracks));

    // The source, tip and goal have separate roots except for tip -> joint. A source rotation
    // therefore cannot carry either endpoint along and conceal an incorrect IK correction.
    private static RigBone[] AppendChain(Vector3 tip, Vector3 goal, Vector3? minimum = null,
        Vector3? maximum = null, int iterations = 64, IkAnglePolicy policy = IkAnglePolicy.Saba) =>
        [new("source", new(-3, 2, 4), -1),
            new("joint", Vector3.Zero, -1, DeformLayer: 1, Append: new(0, 1, false, true, false)),
            new("tip", tip, 1, DeformLayer: 1),
            new("goal", goal, -1, DeformLayer: 2, Ik: new(2, iterations, MathF.PI, [new(1, minimum, maximum)], policy))];

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = .00005f)
        => Assert.True(Vector3.Distance(expected, actual) <= tolerance,
            $"Expected {expected}; actual {actual}; distance {Vector3.Distance(expected, actual)}");

    private static void MatrixNear(Matrix4x4 expected, Matrix4x4 actual, float tolerance = .00005f)
    {
        Near(expected.Translation, actual.Translation, tolerance);
        Near(Vector3.TransformNormal(Vector3.UnitX, expected), Vector3.TransformNormal(Vector3.UnitX, actual), tolerance);
        Near(Vector3.TransformNormal(Vector3.UnitY, expected), Vector3.TransformNormal(Vector3.UnitY, actual), tolerance);
        Near(Vector3.TransformNormal(Vector3.UnitZ, expected), Vector3.TransformNormal(Vector3.UnitZ, actual), tolerance);
    }

    private static void ReachesGoal(CharacterSample sample, int tip = 2, int goal = 3)
        => Near(sample.BoneWorldMatrices[goal].Translation, sample.BoneWorldMatrices[tip].Translation, .0005f);

    [Fact]
    public void AppendRotatedLinkReachesIndependentGoalInOneIteration()
    {
        var animator = Animator(AppendChain(Vector3.UnitY, Vector3.UnitX, iterations: 1),
            Track("source", Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2)));

        Near(Vector3.UnitZ, animator.Sample(0, new(EnableIk: false)).BoneWorldMatrices[2].Translation);
        var solved = animator.Sample(0);
        Near(Vector3.UnitX, solved.BoneWorldMatrices[2].Translation);
        Near(new(-3, 2, 4), solved.BoneWorldMatrices[0].Translation);
    }

    [Theory]
    [InlineData(IkAnglePolicy.Saba)]
    [InlineData(IkAnglePolicy.Mmd)]
    public void NoncommutingAnimationAndAppendReachKnownReachableGoal(IkAnglePolicy policy)
    {
        var goal = Vector3.Normalize(new(-.2f, .7f, .5f));
        var animator = Animator(AppendChain(Vector3.UnitX, goal, policy: policy),
            Track("source", Quaternion.CreateFromAxisAngle(Vector3.UnitY, .65f)),
            Track("joint", Quaternion.CreateFromAxisAngle(Vector3.UnitX, .4f)));

        var sample = animator.Sample(0);
        ReachesGoal(sample);
        Near(goal, sample.BoneWorldMatrices[2].Translation, .0005f);
        MatrixNear(Matrix4x4.CreateRotationY(.65f) * Matrix4x4.CreateTranslation(-3, 2, 4), sample.BoneWorldMatrices[0]);
    }

    [Fact]
    public void XOnlyKneeLimitRemovesAppendTwistInNativeModelCoordinates()
    {
        const float bend = -.65f;
        var expected = Matrix4x4.CreateRotationX(bend);
        var goal = new Vector3(0, MathF.Cos(bend), MathF.Sin(bend));
        var animator = Animator(AppendChain(Vector3.UnitY, goal, new(-1.4f, 0, 0), Vector3.Zero),
            Track("source", Quaternion.CreateFromAxisAngle(Vector3.UnitY, .35f)),
            Track("joint", Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .2f)));

        var sample = animator.Sample(0);
        ReachesGoal(sample);
        MatrixNear(expected, sample.BoneWorldMatrices[1], .0005f);
        // The endpoint alone cannot detect twist around the lower leg; all three axes must agree.
        Near(Vector3.UnitX, Vector3.TransformNormal(Vector3.UnitX, sample.BoneWorldMatrices[1]));
    }

    [Fact]
    public void YOnlyLimitRemovesAppendTwistAndPreservesNativePositiveBend()
    {
        const float bend = .55f;
        var goal = new Vector3(MathF.Sin(bend), 0, MathF.Cos(bend));
        var animator = Animator(AppendChain(Vector3.UnitZ, goal, Vector3.Zero, new(0, 1.3f, 0)),
            Track("source", Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .4f)),
            Track("joint", Quaternion.CreateFromAxisAngle(Vector3.UnitX, -.2f)));

        var sample = animator.Sample(0);
        ReachesGoal(sample);
        MatrixNear(Matrix4x4.CreateRotationY(bend), sample.BoneWorldMatrices[1], .0005f);
        Near(Vector3.UnitY, Vector3.TransformNormal(Vector3.UnitY, sample.BoneWorldMatrices[1]));
    }

    [Fact]
    public void ZOnlyLimitRemovesAppendTwistAndPreservesNativePositiveBend()
    {
        const float bend = .8f;
        var goal = new Vector3(MathF.Cos(bend), MathF.Sin(bend), 0);
        var animator = Animator(AppendChain(Vector3.UnitX, goal, Vector3.Zero, new(0, 0, 1.4f)),
            Track("source", Quaternion.CreateFromAxisAngle(Vector3.UnitX, .5f)),
            Track("joint", Quaternion.CreateFromAxisAngle(Vector3.UnitY, -.2f)));

        var sample = animator.Sample(0);
        ReachesGoal(sample);
        MatrixNear(Matrix4x4.CreateRotationZ(bend), sample.BoneWorldMatrices[1], .0005f);
        Near(Vector3.UnitZ, Vector3.TransformNormal(Vector3.UnitZ, sample.BoneWorldMatrices[1]));
    }

    [Fact]
    public void MultiAxisFixedLimitsConstrainCompleteLocalRotationIncludingAppend()
    {
        var angles = new Vector3(.22f, -.31f, .18f);
        // Quaternion XYZ composition applies Z, Y, X to a row vector. These fixed bounds
        // define one exact orientation, independent of CCD's underdetermined free-joint twist.
        var expected = Matrix4x4.CreateRotationZ(angles.Z) * Matrix4x4.CreateRotationY(angles.Y)
            * Matrix4x4.CreateRotationX(angles.X);
        var tip = Vector3.Normalize(new(.2f, 1, .4f));
        var goal = Vector3.TransformNormal(tip, expected);
        var animator = Animator(AppendChain(tip, goal, angles, angles),
            Track("source", Quaternion.CreateFromAxisAngle(Vector3.UnitY, .55f)),
            Track("joint", Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -.25f)));

        var sample = animator.Sample(0);
        MatrixNear(expected, sample.BoneWorldMatrices[1]);
        Near(goal, sample.BoneWorldMatrices[2].Translation);
        ReachesGoal(sample);
    }

    [Fact]
    public void RotatingParentKeepsKneeLimitLocalAndFootAtWorldGoal()
    {
        var origin = new Vector3(1.5f, 2, -.5f);
        var parentRotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new(1, 2, 3)), .8f);
        var parentWorld = Matrix4x4.CreateFromQuaternion(parentRotation) * Matrix4x4.CreateTranslation(origin);
        const float bend = -.7f;
        var localGoal = new Vector3(0, MathF.Cos(bend), MathF.Sin(bend));
        var worldGoal = Vector3.Transform(localGoal, parentWorld);
        RigBone[] bones = [new("source", new(-3, 2, 4), -1), new("parent", origin, -1),
            new("joint", origin, 1, DeformLayer: 1, Append: new(0, 1, false, true, false)),
            new("tip", origin + Vector3.UnitY, 2, DeformLayer: 1),
            new("goal", worldGoal, -1, DeformLayer: 2,
                Ik: new(3, 64, MathF.PI, [new(2, new(-1.4f, 0, 0), Vector3.Zero)], IkAnglePolicy.Saba))];
        var animator = Animator(bones,
            new BoneTrack("parent", [new(0, Vector3.Zero, Quaternion.Identity, BoneCurves.Linear),
                new(8, Vector3.Zero, parentRotation, BoneCurves.Linear)]),
            Track("source", Quaternion.CreateFromAxisAngle(Vector3.UnitY, .3f)),
            Track("joint", Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -.15f)));

        var sample = animator.Sample(8);
        MatrixNear(parentWorld, sample.BoneWorldMatrices[1]);
        Assert.True(Matrix4x4.Invert(sample.BoneWorldMatrices[1], out var inverseParent));
        MatrixNear(Matrix4x4.CreateRotationX(bend), sample.BoneWorldMatrices[2] * inverseParent, .0005f);
        Near(worldGoal, sample.BoneWorldMatrices[3].Translation, .0005f);
        ReachesGoal(sample, 3, 4);
    }

    [Fact]
    public void RepeatedAndOutOfOrderSamplesDoNotAccumulateAppendOrIk()
    {
        var animator = Animator(AppendChain(Vector3.UnitX, Vector3.UnitY),
            new BoneTrack("source", [new(0, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, .3f), BoneCurves.Linear),
                new(10, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, -.6f), BoneCurves.Linear)]),
            Track("joint", Quaternion.CreateFromAxisAngle(Vector3.UnitX, .2f)));
        var expected = new Dictionary<int, CharacterSample>();
        foreach (int frame in new[] { 0, 3, 10 })
        {
            expected[frame] = animator.Sample(frame);
            ReachesGoal(expected[frame]);
        }

        foreach (int frame in new[] { 10, 0, 3, 10, 3, 0, 0 })
        {
            var sample = animator.Sample(frame);
            Assert.Equal(expected[frame].BoneWorldMatrices, sample.BoneWorldMatrices);
            ReachesGoal(sample);
        }
    }

    [Fact]
    public void BothIkSwitchesPreserveAppendAndReenablingRestoresSolvedPose()
    {
        const float bend = .6f;
        var goal = new Vector3(MathF.Cos(bend), MathF.Sin(bend), 0);
        var animator = Animator(AppendChain(Vector3.UnitX, goal, Vector3.Zero, new(0, 0, 1.4f)),
            Track("source", Quaternion.CreateFromAxisAngle(Vector3.UnitX, .45f)),
            Track("joint", Quaternion.CreateFromAxisAngle(Vector3.UnitY, -.2f)));
        var solved = animator.Sample(0);
        MatrixNear(Matrix4x4.CreateRotationZ(bend), solved.BoneWorldMatrices[1], .0005f);
        var unsolvedRotation = Matrix4x4.CreateRotationX(.45f) * Matrix4x4.CreateRotationY(-.2f);

        foreach (var options in new[] { new CharacterEvaluationOptions(EnableIk: false),
            new CharacterEvaluationOptions(IkEnabledOverrides: new Dictionary<int, bool> { [3] = false }) })
        {
            var disabled = animator.Sample(0, options);
            MatrixNear(unsolvedRotation, disabled.BoneWorldMatrices[1]);
            Near(Vector3.TransformNormal(Vector3.UnitX, unsolvedRotation), disabled.BoneWorldMatrices[2].Translation);
            Assert.Equal(solved.BoneWorldMatrices, animator.Sample(0).BoneWorldMatrices);
        }
    }

    [Fact]
    public void NoAppendFreeAndSingleAxisPathsKeepAnalyticResults()
    {
        var freeBones = AppendChain(Vector3.UnitX, Vector3.UnitY);
        freeBones[1] = freeBones[1] with { Append = null };
        var free = Animator(freeBones, Track("joint", Quaternion.CreateFromAxisAngle(Vector3.UnitX, .4f))).Sample(0);
        Near(Vector3.UnitY, free.BoneWorldMatrices[2].Translation);

        const float bend = -.65f;
        var goal = new Vector3(0, MathF.Cos(bend), MathF.Sin(bend));
        var kneeBones = AppendChain(Vector3.UnitY, goal, new(-1.4f, 0, 0), Vector3.Zero);
        kneeBones[1] = kneeBones[1] with { Append = null };
        var knee = Animator(kneeBones).Sample(0);
        MatrixNear(Matrix4x4.CreateRotationX(bend), knee.BoneWorldMatrices[1]);
        Near(goal, knee.BoneWorldMatrices[2].Translation);
    }

    [Fact]
    public void AlreadyBentAnimatedKneeAtGoalDoesNotStraightenInFirstIteration()
    {
        const float bend = -MathF.PI / 2;
        var bones = AppendChain(Vector3.UnitY, -Vector3.UnitZ, new(-MathF.PI, 0, 0), Vector3.Zero, iterations: 1);
        bones[1] = bones[1] with { Append = null };
        var sample = Animator(bones, Track("joint", Quaternion.CreateFromAxisAngle(Vector3.UnitX, bend))).Sample(0);

        MatrixNear(Matrix4x4.CreateRotationX(bend), sample.BoneWorldMatrices[1]);
        Near(-Vector3.UnitZ, sample.BoneWorldMatrices[2].Translation);
    }

    [Fact]
    public void AnimatedKneeAddsSmallCorrectionToExistingBendInFirstIteration()
    {
        const float bend = -.8f;
        var goal = new Vector3(0, MathF.Cos(bend), MathF.Sin(bend));
        var bones = AppendChain(Vector3.UnitY, goal, new(-MathF.PI, 0, 0), Vector3.Zero, iterations: 1);
        bones[1] = bones[1] with { Append = null };
        bones[3] = bones[3] with { Ik = new(2, 1, .4f, [new(1, new(-MathF.PI, 0, 0), Vector3.Zero)], IkAnglePolicy.Saba) };
        var sample = Animator(bones, Track("joint", Quaternion.CreateFromAxisAngle(Vector3.UnitX, -.7f))).Sample(0);

        MatrixNear(Matrix4x4.CreateRotationX(bend), sample.BoneWorldMatrices[1]);
        Near(goal, sample.BoneWorldMatrices[2].Translation);
    }

    [Theory]
    [InlineData(0, -.6f)]
    [InlineData(1, .6f)]
    [InlineData(2, -.6f)]
    public void PureAxisAnimationAndAppendAlreadyAtGoalKeepBendForEitherQuaternionSign(int component, float bend)
    {
        var axis = component == 0 ? Vector3.UnitX : component == 1 ? Vector3.UnitY : Vector3.UnitZ;
        var tip = component == 0 ? Vector3.UnitY : component == 1 ? Vector3.UnitZ : Vector3.UnitX;
        var expected = component == 0 ? Matrix4x4.CreateRotationX(bend)
            : component == 1 ? Matrix4x4.CreateRotationY(bend) : Matrix4x4.CreateRotationZ(bend);
        var goal = Vector3.TransformNormal(tip, expected);
        var minimum = bend < 0 ? axis * -1.2f : Vector3.Zero;
        var maximum = bend > 0 ? axis * 1.2f : Vector3.Zero;
        var animation = Quaternion.CreateFromAxisAngle(axis, bend * .4f);
        var append = Quaternion.CreateFromAxisAngle(axis, bend * .6f);
        Matrix4x4? first = null;

        foreach (bool negate in new[] { false, true })
        {
            var signedAnimation = negate
                ? new Quaternion(-animation.X, -animation.Y, -animation.Z, -animation.W) : animation;
            var animator = Animator(AppendChain(tip, goal, minimum, maximum, iterations: 1),
                Track("source", append), Track("joint", signedAnimation));
            var sample = animator.Sample(0);

            MatrixNear(expected, sample.BoneWorldMatrices[1]);
            Near(goal, sample.BoneWorldMatrices[2].Translation);
            if (first is { } original) MatrixNear(original, sample.BoneWorldMatrices[1]);
            else first = sample.BoneWorldMatrices[1];
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(-1, false)]
    [InlineData(-1, true)]
    public void HalfTurnAnimatedHingeAtLimitKeepsPoseForEitherQuaternionSign(int boundSign, bool negate)
    {
        float bend = boundSign * MathF.PI;
        var minimum = boundSign < 0 ? new Vector3(-MathF.PI, 0, 0) : Vector3.Zero;
        var maximum = boundSign > 0 ? new Vector3(MathF.PI, 0, 0) : Vector3.Zero;
        var bones = AppendChain(Vector3.UnitY, -Vector3.UnitY, minimum, maximum, iterations: 1);
        bones[1] = bones[1] with { Append = null };
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, bend);
        if (negate) rotation = new(-rotation.X, -rotation.Y, -rotation.Z, -rotation.W);

        var sample = Animator(bones, Track("joint", rotation)).Sample(0);

        MatrixNear(Matrix4x4.CreateRotationX(bend), sample.BoneWorldMatrices[1]);
        Near(-Vector3.UnitY, sample.BoneWorldMatrices[2].Translation);
    }

    [Fact]
    public void TwoLinkLegKeepsFootAtFixedGroundGoalWhileHipMovesAndSamplesSeek()
    {
        RigBone[] bones = [new("hip", new(0, 1.8f, 0), -1), new("knee", new(0, .8f, 0), 0),
            new("foot", new(0, -.2f, 0), 1), new("footIK", Vector3.Zero, -1, DeformLayer: 1,
                Ik: new(2, 256, .4f, [new(1, new(-MathF.PI, 0, 0), Vector3.Zero), new(0)]))];
        // Keep the initial leg slightly bent on both sides of its downward axis so CCD does
        // not first straighten it into the unrelated collinear, fully extended singularity.
        var hipRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .1f);
        var animator = Animator(bones,
            new BoneTrack("hip", [new(0, new(0, -.1f, 0), hipRotation, BoneCurves.Linear),
                new(10, new(0, .1f, 0), hipRotation, BoneCurves.Linear),
                new(20, new(0, -.1f, 0), hipRotation, BoneCurves.Linear)]),
            Track("knee", Quaternion.CreateFromAxisAngle(Vector3.UnitX, -.2f)));
        var frames = new[] { 0d, 5d, 10d, 12.5d, 20d };
        var snapshots = new Dictionary<double, CharacterSample>();

        foreach (double frame in frames)
        {
            var sample = animator.Sample(frame);
            snapshots.Add(frame, sample);
            var hip = sample.BoneWorldMatrices[0];
            Near(Vector3.Zero, sample.BoneWorldMatrices[3].Translation);
            Assert.True(Vector3.Distance(sample.BoneWorldMatrices[2].Translation, Vector3.Zero) < .002f,
                $"Frame {frame}: foot {sample.BoneWorldMatrices[2].Translation} missed its fixed ground goal.");
            Assert.InRange(hip.Translation.Y, 1.69999f, 1.90001f);
            Assert.True(Matrix4x4.Invert(hip, out var inverseHip));
            var kneeLocal = sample.BoneWorldMatrices[1] * inverseHip;
            float bend = MathF.Atan2(kneeLocal.M23, kneeLocal.M22);
            Assert.InRange(bend, -MathF.PI, 0);
            // A hinge may change its bend, but its complete local orientation must remain a
            // pure model-native X rotation. Checking only the foot would miss knee twist.
            MatrixNear(Matrix4x4.CreateRotationX(bend) * Matrix4x4.CreateTranslation(0, -1, 0), kneeLocal);
            Assert.True(MathF.Abs(bend) > .1f, $"Frame {frame}: reachable shortened leg must remain bent.");
        }

        foreach (double frame in new[] { 20d, 0d, 12.5d, 10d, 5d, 20d, 0d })
            Assert.Equal(snapshots[frame].BoneWorldMatrices, animator.Sample(frame).BoneWorldMatrices);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void EquivalentHingeAngleInShiftedTurnRangeDoesNotResetBend(int sign)
    {
        float bend = -.4f * sign;
        var minimum = new Vector3(sign > 0 ? MathF.Tau - .8f : -MathF.Tau + .1f, 0, 0);
        var maximum = new Vector3(sign > 0 ? MathF.Tau - .1f : -MathF.Tau + .8f, 0, 0);
        var expected = Matrix4x4.CreateRotationX(bend);
        var goal = Vector3.TransformNormal(Vector3.UnitY, expected);
        var bones = AppendChain(Vector3.UnitY, goal, minimum, maximum, iterations: 1);
        bones[1] = bones[1] with { Append = null };
        var sample = Animator(bones, Track("joint", Quaternion.CreateFromAxisAngle(Vector3.UnitX, bend))).Sample(0);
        MatrixNear(expected, sample.BoneWorldMatrices[1]);
        Near(goal, sample.BoneWorldMatrices[2].Translation);
    }
}
