using System.Numerics;
using Mmd.Modern.Animation;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class OriginalSingleAxisIkTests
{
    private static CharacterAnimator Animator(Vector3 tip, Vector3 goal, Vector3 minimum, Vector3 maximum,
        int iterations, float angle = MathF.PI, IkAnglePolicy policy = IkAnglePolicy.Mmd,
        params BoneTrack[] tracks)
        => new(new([new("joint", Vector3.Zero, -1), new("tip", tip, 0),
            new("goal", goal, -1, DeformLayer: 1,
                Ik: new(1, iterations, angle, [new(0, minimum, maximum)], policy))], [], []), new(tracks));

    private static void MatrixNear(Matrix4x4 expected, Matrix4x4 actual)
    {
        float[] a = [expected.M11, expected.M12, expected.M13, expected.M14,
            expected.M21, expected.M22, expected.M23, expected.M24,
            expected.M31, expected.M32, expected.M33, expected.M34,
            expected.M41, expected.M42, expected.M43, expected.M44];
        float[] b = [actual.M11, actual.M12, actual.M13, actual.M14,
            actual.M21, actual.M22, actual.M23, actual.M24,
            actual.M31, actual.M32, actual.M33, actual.M34,
            actual.M41, actual.M42, actual.M43, actual.M44];
        for (int i = 0; i < a.Length; i++)
            Assert.True(float.IsFinite(b[i]) && MathF.Abs(a[i] - b[i]) < .00005f,
                $"Matrix element {i}: expected {a[i]}; actual {b[i]}");
    }

    private static void PoseNear(Matrix4x4 rotation, Vector3 tip, CharacterSample sample)
    {
        MatrixNear(rotation, sample.BoneWorldMatrices[0]);
        Assert.True(Vector3.Distance(Vector3.TransformNormal(tip, rotation),
            sample.BoneWorldMatrices[1].Translation) < .00005f);
    }

    [Theory]
    [InlineData(0, 1.2f)]
    [InlineData(0, 2.2f)]
    [InlineData(1, 1.2f)]
    [InlineData(2, 1.2f)]
    public void OneIterationHasNoProjectedHalfAndKeepsOnlyTheSelectedEulerComponent(int component, float range)
    {
        var axis = component == 0 ? Vector3.UnitX : component == 1 ? Vector3.UnitY : Vector3.UnitZ;
        var tip = component == 0 ? Vector3.UnitY : component == 1 ? Vector3.UnitZ : Vector3.UnitX;
        var goal = Vector3.Normalize(component == 0 ? new(.3f, .8f, -.5f)
            : component == 1 ? new(-.5f, .3f, .8f) : new Vector3(.8f, -.5f, .3f));

        // Independent shortest-swing geometry: R = I + [t cross g] +
        // [t cross g]^2 / (1 + t dot g). Read the one retained Euler angle
        // directly from its relevant matrix entries, without the product limit helper.
        float bend = component switch
        {
            0 when range < 1.570796f => MathF.Asin(goal.Z), // YXZ
            0 => MathF.Atan2(goal.Z, 1 - goal.Z * goal.Z / (1 + goal.Y)), // ZYX
            1 => MathF.Atan2(goal.X, goal.Z),
            _ => MathF.Atan2(goal.Y, 1 - goal.Y * goal.Y / (1 + goal.X))
        };
        var expected = component == 0 ? Matrix4x4.CreateRotationX(bend)
            : component == 1 ? Matrix4x4.CreateRotationY(bend) : Matrix4x4.CreateRotationZ(bend);

        PoseNear(expected, tip, Animator(tip, goal, -range * axis, range * axis, 1).Sample(0));
        Assert.True(MathF.Abs(MathF.Abs(bend) - MathF.Acos(Vector3.Dot(tip, goal))) > .01f);
    }

    // For t = Rx(theta) * UnitY and unit g=(a,b,c), transport the orthogonal
    // vector (0,-sin(theta),cos(theta)) by the shortest swing from t to g.
    // Its Y coordinate is -c - sin(theta)*a^2/(1+t dot g). This determines
    // the constrained YXZ X angle independently of quaternion multiplication.
    private static float TransportedX(float theta, Vector3 goal)
        => MathF.Asin(goal.Z + MathF.Sin(theta) * goal.X * goal.X
            / (1 + MathF.Cos(theta) * goal.Y + MathF.Sin(theta) * goal.Z));

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void TwoAndThreeIterationsProjectOnlyTheFirstIteration(int iterations)
    {
        var goal = Vector3.Normalize(new Vector3(.3f, .8f, -.5f));
        float first = -MathF.Acos(goal.Y);
        float second = TransportedX(first, goal);
        float expected = iterations == 2 ? second : TransportedX(second, goal);

        PoseNear(Matrix4x4.CreateRotationX(expected), Vector3.UnitY,
            Animator(Vector3.UnitY, goal, new(-1.2f, 0, 0), new(1.2f, 0, 0), iterations).Sample(0));
        Assert.True(MathF.Abs(expected - first) > .03f);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void TemporaryDistanceIncreaseDuringProjectionMustNotSkipTheUnrestrictedHalf(int iterations)
    {
        var goal = Vector3.Normalize(new Vector3(.3f, .8f, -.5f));
        float first = -MathF.Acos(goal.Y);
        float second = first + MathF.Acos(MathF.Cos(first) * goal.Y + MathF.Sin(first) * goal.Z);
        float expected = TransportedX(TransportedX(second, goal), goal);
        if (iterations == 5) expected = TransportedX(expected, goal);

        // The second projected step gets worse (.38826 versus .31446), but the
        // subsequent ordinary swings improve to about -.558 radians. Saba's
        // distance rollback would otherwise stop before the phase transition.
        Assert.True(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitY, Matrix4x4.CreateRotationX(second)), goal)
            > Vector3.Distance(Vector3.TransformNormal(Vector3.UnitY, Matrix4x4.CreateRotationX(first)), goal));
        PoseNear(Matrix4x4.CreateRotationX(expected), Vector3.UnitY,
            Animator(Vector3.UnitY, goal, new(-1.2f, 0, 0), new(1.2f, 0, 0), iterations).Sample(0));
    }

    [Fact]
    public void ExistingBendBothQuaternionSignsAndOutOfOrderSamplingUseTheCompleteStartingPose()
    {
        var goal = new Vector3(.3f, .8f, -.5f);
        var delta = new Vector3(-.08f, .05f, .12f);
        var samples = new Dictionary<double, CharacterSample>();
        foreach (int sign in new[] { 1, -1 })
        {
            Quaternion Signed(float bend)
            {
                var q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, bend);
                return new(sign * q.X, sign * q.Y, sign * q.Z, sign * q.W);
            }
            var animator = Animator(Vector3.UnitY, goal, new(-1.2f, 0, 0), new(1.2f, 0, 0), 3,
                tracks: [new("joint", [new(0, Vector3.Zero, Signed(.25f), BoneCurves.Linear),
                    new(10, Vector3.Zero, Signed(-.2f), BoneCurves.Linear)]),
                    new("goal", [new(0, Vector3.Zero, Quaternion.Identity, BoneCurves.Linear),
                        new(10, delta, Quaternion.Identity, BoneCurves.Linear)])]);
            var current = new Dictionary<double, CharacterSample>();
            foreach (double frame in new[] { 0d, 3d, 10d })
            {
                float fraction = (float)frame / 10;
                float seed = .25f - .45f * fraction;
                var unitGoal = Vector3.Normalize(goal + fraction * delta);
                float projected = seed - MathF.Acos(MathF.Cos(seed) * unitGoal.Y + MathF.Sin(seed) * unitGoal.Z);
                float expected = TransportedX(TransportedX(projected, unitGoal), unitGoal);
                var sample = current[frame] = animator.Sample(frame);
                PoseNear(Matrix4x4.CreateRotationX(expected), Vector3.UnitY, sample);
                if (sign == 1) samples[frame] = sample;
                else MatrixNear(samples[frame].BoneWorldMatrices[0], sample.BoneWorldMatrices[0]);
            }
            foreach (double frame in new[] { 10d, 0d, 3d, 10d, 3d, 0d })
                Assert.Equal(current[frame].BoneWorldMatrices, animator.Sample(frame).BoneWorldMatrices);
        }
    }

    private static float CappedTransportedX(float theta, Vector3 goal, float step)
    {
        // Rodrigues transport at a prescribed angular step, independently of the
        // solver's quaternion and Euler code. The off-axis term remains nonzero.
        float sine = MathF.Sin(theta), cosine = MathF.Cos(theta);
        float dot = cosine * goal.Y + sine * goal.Z;
        float crossLength = MathF.Sqrt(1 - dot * dot);
        return MathF.Asin(sine * MathF.Cos(step)
            + cosine * (cosine * goal.Z - sine * goal.Y) / crossLength * MathF.Sin(step)
            + sine * goal.X * goal.X / (crossLength * crossLength) * (1 - MathF.Cos(step)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EarlyProjectionReflectsAtNonzeroMinimumWhileTheLaterHalfClamps(int iterations)
    {
        var goal = Vector3.Normalize(new Vector3(.03f, MathF.Cos(.1f), MathF.Sin(.1f)));
        // Mmd step=.02*4=.08. First projected .08 reflects about .3 to .52;
        // later unrestricted steps retain about .44 and .36 after Euler limiting.
        float expected = iterations == 1 ? .3f : CappedTransportedX(.52f, goal, .08f);
        if (iterations == 3) expected = CappedTransportedX(expected, goal, .08f);

        PoseNear(Matrix4x4.CreateRotationX(expected), Vector3.UnitY,
            Animator(Vector3.UnitY, goal, new(.3f, 0, 0), new(.9f, 0, 0), iterations, .02f).Sample(0));
    }

    [Fact]
    public void ExplicitSabaPolicyRetainsItsPlanarFirstStepForAnOffAxisGoal()
    {
        var goal = Vector3.Normalize(new Vector3(.3f, .8f, -.5f));
        var expected = Matrix4x4.CreateRotationX(-MathF.Acos(goal.Y));
        PoseNear(expected, Vector3.UnitY, Animator(Vector3.UnitY, goal,
            new(-1.2f, 0, 0), new(1.2f, 0, 0), 1, policy: IkAnglePolicy.Saba).Sample(0));
    }
}
