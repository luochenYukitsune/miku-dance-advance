using System.Numerics;
using Mmd.Modern.Animation;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class CharacterIkMmdLimitsTests
{
    // Original BA200 stores these single-precision constants; pi / 2 and 88 degrees
    // are useful descriptions, but do not have the same boundary bits.
    private const float OriginalHalfPi = 1.570796f;
    private const float SingularCap = 1.535889f;

    // The labels describe quaternion order. System.Numerics transforms row vectors,
    // so independent axis matrices apply in the reverse order.
    private static Matrix4x4 Rotation(string order, Vector3 angles) => order switch
    {
        "YXZ" => Matrix4x4.CreateRotationZ(angles.Z) * Matrix4x4.CreateRotationX(angles.X)
            * Matrix4x4.CreateRotationY(angles.Y),
        "ZYX" => Matrix4x4.CreateRotationX(angles.X) * Matrix4x4.CreateRotationY(angles.Y)
            * Matrix4x4.CreateRotationZ(angles.Z),
        "XZY" => Matrix4x4.CreateRotationY(angles.Y) * Matrix4x4.CreateRotationZ(angles.Z)
            * Matrix4x4.CreateRotationX(angles.X),
        "XYZ" => Matrix4x4.CreateRotationZ(angles.Z) * Matrix4x4.CreateRotationY(angles.Y)
            * Matrix4x4.CreateRotationX(angles.X),
        _ => throw new ArgumentOutOfRangeException(nameof(order))
    };

    private static (Vector3 Minimum, Vector3 Maximum) Bounds(string order, float middleLimit = .8f)
        => order switch
        {
            "YXZ" => (new(-middleLimit, -MathF.PI, -MathF.PI), new(middleLimit, MathF.PI, MathF.PI)),
            "ZYX" => (new(-MathF.PI, -middleLimit, -MathF.PI), new(MathF.PI, middleLimit, MathF.PI)),
            "XZY" => (new(-MathF.PI, -MathF.PI, -middleLimit), new(MathF.PI, MathF.PI, middleLimit)),
            _ => throw new ArgumentOutOfRangeException(nameof(order))
        };

    private static Quaternion Constrain(Matrix4x4 rotation, Vector3 minimum, Vector3 maximum,
        int iteration = 2, int iterations = 4)
        => MmdIkRotationLimits.Constrain(Quaternion.CreateFromRotationMatrix(rotation),
            minimum, maximum, iteration, iterations);

    private static void MatrixNear(Matrix4x4 expected, Matrix4x4 actual, float tolerance = .00005f)
    {
        float[] expectedValues = [expected.M11, expected.M12, expected.M13, expected.M14,
            expected.M21, expected.M22, expected.M23, expected.M24,
            expected.M31, expected.M32, expected.M33, expected.M34,
            expected.M41, expected.M42, expected.M43, expected.M44];
        float[] actualValues = [actual.M11, actual.M12, actual.M13, actual.M14,
            actual.M21, actual.M22, actual.M23, actual.M24,
            actual.M31, actual.M32, actual.M33, actual.M34,
            actual.M41, actual.M42, actual.M43, actual.M44];
        for (int i = 0; i < expectedValues.Length; i++)
            Assert.True(float.IsFinite(actualValues[i]) && MathF.Abs(expectedValues[i] - actualValues[i]) <= tolerance,
                $"Matrix element {i}: expected {expectedValues[i]}; actual {actualValues[i]}");
    }

    private static void RotationNear(Matrix4x4 expected, Quaternion actual)
    {
        Assert.True(float.IsFinite(actual.X) && float.IsFinite(actual.Y)
            && float.IsFinite(actual.Z) && float.IsFinite(actual.W), $"Nonfinite quaternion {actual}");
        Assert.InRange(actual.LengthSquared(), .99995f, 1.00005f);
        MatrixNear(expected, Matrix4x4.CreateFromQuaternion(actual));
    }

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = .0005f)
        => Assert.True(Vector3.Distance(expected, actual) <= tolerance,
            $"Expected {expected}; actual {actual}; distance {Vector3.Distance(expected, actual)}");

    [Theory]
    [InlineData("YXZ", .35f, 2.1f, -.4f)]
    [InlineData("ZYX", 2.1f, -.45f, .6f)]
    [InlineData("XZY", 2.1f, -2.2f, .35f)]
    public void SelectedOrderPreservesNoncommutingInRangeRotationForEitherQuaternionSign(
        string order, float x, float y, float z)
    {
        var expected = Rotation(order, new(x, y, z));
        var (minimum, maximum) = Bounds(order);
        var rotation = Quaternion.CreateFromRotationMatrix(expected);

        foreach (var signed in new[] { rotation, new Quaternion(-rotation.X, -rotation.Y, -rotation.Z, -rotation.W) })
            RotationNear(expected, MmdIkRotationLimits.Constrain(signed, minimum, maximum, 2, 4));

        // All three components are nonzero: changing multiplication order changes this pose.
        Assert.True(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitX, expected),
            Vector3.TransformNormal(Vector3.UnitX, Rotation("XYZ", new(x, y, z)))) > .01f);
    }

    [Theory]
    [InlineData("YXZ", 1)]
    [InlineData("YXZ", -1)]
    [InlineData("ZYX", 1)]
    [InlineData("ZYX", -1)]
    [InlineData("XZY", 1)]
    [InlineData("XZY", -1)]
    public void NearSingularMiddleAngleIsCappedAtOriginal88DegreeConstant(string order, int sign)
    {
        float nearSingular = sign * 89f * MathF.PI / 180;
        var angles = order switch
        {
            "YXZ" => new Vector3(nearSingular, .31f, -.27f),
            "ZYX" => new Vector3(.31f, nearSingular, -.27f),
            _ => new Vector3(.31f, -.27f, nearSingular)
        };
        var capped = order switch
        {
            "YXZ" => angles with { X = sign * SingularCap },
            "ZYX" => angles with { Y = sign * SingularCap },
            _ => angles with { Z = sign * SingularCap }
        };
        var (minimum, maximum) = Bounds(order, OriginalHalfPi - .001f);

        RotationNear(Rotation(order, capped), Constrain(Rotation(order, angles), minimum, maximum));
    }

    [Theory]
    [InlineData(.1f, .5f, 0, 4)]
    [InlineData(1.1f, .7f, 1, 4)]
    [InlineData(1.1f, .9f, 2, 4)]
    [InlineData(.1f, .5f, 1, 5)]
    [InlineData(.1f, .3f, 2, 5)]
    [InlineData(-1f, .3f, 0, 4)]
    [InlineData(2.2f, .9f, 0, 4)]
    [InlineData(.1f, .3f, 0, 1)]
    public void ReflectionRequiresEarlyIterationAndReflectedAngleInsideBothBounds(
        float input, float expected, int iteration, int iterations)
    {
        // YXZ keeps the full signed Y angle; the .3 .. .9 interval makes each
        // reflected result (.5 or .7) and each rejected result (1.6 or -.4) explicit.
        var minimum = new Vector3(-.2f, .3f, -.2f);
        var maximum = new Vector3(.2f, .9f, .2f);

        RotationNear(Matrix4x4.CreateRotationY(expected),
            Constrain(Matrix4x4.CreateRotationY(input), minimum, maximum, iteration, iterations));
    }

    [Fact]
    public void ExactOriginalXBoundarySelectsZyxWhileOneUlpInsideSelectsYxz()
    {
        float inside = MathF.BitDecrement(OriginalHalfPi);
        foreach (int sign in new[] { -1, 1 })
        {
            var minimum = new Vector3(-inside, -.3f, -MathF.PI);
            var maximum = new Vector3(inside, .3f, MathF.PI);
            var input = Rotation("YXZ", new(sign * .2f, 2.2f, 0));
            RotationNear(Rotation("YXZ", new(sign * .2f, .3f, 0)), Constrain(input, minimum, maximum));

            foreach (float boundary in new[] { OriginalHalfPi, MathF.BitIncrement(OriginalHalfPi), MathF.PI / 2 })
            {
                var atMinimum = sign < 0 ? minimum with { X = -boundary } : minimum;
                var atMaximum = sign > 0 ? maximum with { X = boundary } : maximum;
                RotationNear(Matrix4x4.CreateRotationX(sign * boundary),
                    Constrain(Matrix4x4.CreateRotationX(sign * 2.2f), atMinimum, atMaximum));
            }
        }
    }

    [Fact]
    public void ExactOriginalYBoundarySelectsXzyWhileOneUlpInsideSelectsZyx()
    {
        float inside = MathF.BitDecrement(OriginalHalfPi);
        foreach (int sign in new[] { -1, 1 })
        {
            var minimum = new Vector3(-MathF.PI, -inside, -.3f);
            var maximum = new Vector3(MathF.PI, inside, .3f);
            var input = Rotation("ZYX", new(.2f, sign * .2f, 1.2f));
            RotationNear(Rotation("ZYX", new(.2f, sign * .2f, .3f)), Constrain(input, minimum, maximum));

            foreach (float boundary in new[] { OriginalHalfPi, MathF.BitIncrement(OriginalHalfPi), MathF.PI / 2 })
            {
                var atMinimum = sign < 0 ? minimum with { Y = -boundary } : minimum;
                var atMaximum = sign > 0 ? maximum with { Y = boundary } : maximum;
                RotationNear(Matrix4x4.CreateRotationY(sign * boundary),
                    Constrain(Matrix4x4.CreateRotationY(sign * 2.2f), atMinimum, atMaximum));
            }
        }
    }

    private static CharacterAnimator Animator(Vector3 tip, Vector3 goal, Vector3 minimum, Vector3 maximum,
        IkAnglePolicy policy = IkAnglePolicy.Mmd, params BoneTrack[] tracks)
        => new(new([new("joint", Vector3.Zero, -1), new("tip", tip, 0),
            new("goal", goal, -1, DeformLayer: 1,
                Ik: new(1, 1, MathF.PI, [new(0, minimum, maximum)], policy))], [], []), new(tracks));

    private static (Vector3 Minimum, Vector3 Maximum) WideYBounds(string order) => order == "YXZ"
        ? (new(-.1f, -MathF.PI, -.1f), new(.1f, MathF.PI, .1f))
        : (new(-MathF.PI, -MathF.PI, -.1f), new(MathF.PI, MathF.PI, .1f));

    private static Vector3 FixedAngles(string order)
        => order == "YXZ" ? new(.22f, -.31f, .18f) : new(2f, 2.1f, .25f);

    [Theory]
    [InlineData("YXZ")]
    [InlineData("XZY")]
    public void PublicAnimatorUsesSelectedMmdOrderAndRetainsWideYBend(string order)
    {
        var goal = Vector3.TransformNormal(Vector3.UnitX, Matrix4x4.CreateRotationY(2.2f));
        var (minimum, maximum) = WideYBounds(order);
        var wideBend = Animator(Vector3.UnitX, goal, minimum, maximum).Sample(0);
        Near(goal, wideBend.BoneWorldMatrices[1].Translation);
        MatrixNear(Matrix4x4.CreateRotationY(2.2f), wideBend.BoneWorldMatrices[0]);

        // A pure Y bend can also be represented by Saba's continuous XYZ branch.
        // Fixed noncommuting angles expose the actual difference in reconstruction order.
        var angles = FixedAngles(order);
        var expected = Rotation(order, angles);
        goal = Vector3.TransformNormal(Vector3.UnitX, expected);
        var constrained = Animator(Vector3.UnitX, goal, angles, angles).Sample(0);
        MatrixNear(expected, constrained.BoneWorldMatrices[0]);
        Near(goal, constrained.BoneWorldMatrices[1].Translation);
    }

    [Theory]
    [InlineData("YXZ")]
    [InlineData("XZY")]
    public void ExplicitSabaPolicyRetainsItsOwnXyzReconstruction(string order)
    {
        var angles = FixedAngles(order);
        var mmdExpected = Rotation(order, angles);
        var sabaExpected = Rotation("XYZ", angles);
        var goal = Vector3.TransformNormal(Vector3.UnitX, mmdExpected);
        var sample = Animator(Vector3.UnitX, goal, angles, angles, IkAnglePolicy.Saba).Sample(0);

        MatrixNear(sabaExpected, sample.BoneWorldMatrices[0]);
        Near(Vector3.TransformNormal(Vector3.UnitX, sabaExpected), sample.BoneWorldMatrices[1].Translation);
        Assert.True(Vector3.Distance(goal, sample.BoneWorldMatrices[1].Translation) > .01f);
    }

    [Fact]
    public void PublicAnimatorSingleAxisPathsRetainAnalyticBends()
    {
        foreach (int component in new[] { 0, 1, 2 })
        {
            var axis = component == 0 ? Vector3.UnitX : component == 1 ? Vector3.UnitY : Vector3.UnitZ;
            var tip = component == 0 ? Vector3.UnitY : component == 1 ? Vector3.UnitZ : Vector3.UnitX;
            float bend = component == 0 ? -.65f : .6f;
            var expected = component == 0 ? Matrix4x4.CreateRotationX(bend)
                : component == 1 ? Matrix4x4.CreateRotationY(bend) : Matrix4x4.CreateRotationZ(bend);
            var goal = Vector3.TransformNormal(tip, expected);
            var minimum = bend < 0 ? axis * -1.2f : Vector3.Zero;
            var maximum = bend > 0 ? axis * 1.2f : Vector3.Zero;
            var sample = Animator(tip, goal, minimum, maximum).Sample(0);

            MatrixNear(expected, sample.BoneWorldMatrices[0]);
            Near(goal, sample.BoneWorldMatrices[1].Translation);
        }
    }

    [Fact]
    public void PublicAnimatorSwitchesAndOutOfOrderSamplingDoNotAccumulateLimits()
    {
        var angles = FixedAngles("YXZ");
        var goal = Vector3.TransformNormal(Vector3.UnitX, Rotation("YXZ", angles));
        var animator = Animator(Vector3.UnitX, goal, angles, angles, IkAnglePolicy.Mmd,
            new BoneTrack("goal", [new(0, Vector3.Zero, Quaternion.Identity, BoneCurves.Linear),
                new(10, new(0, .2f, 0), Quaternion.Identity, BoneCurves.Linear)]));
        var expected = new Dictionary<double, CharacterSample>();
        foreach (double frame in new[] { 0d, 3d, 10d }) expected[frame] = animator.Sample(frame);

        foreach (var options in new[] { new CharacterEvaluationOptions(EnableIk: false),
            new CharacterEvaluationOptions(IkEnabledOverrides: new Dictionary<int, bool> { [2] = false }) })
        {
            var disabled = animator.Sample(0, options);
            MatrixNear(Matrix4x4.Identity, disabled.BoneWorldMatrices[0]);
            Near(Vector3.UnitX, disabled.BoneWorldMatrices[1].Translation);
            Assert.Equal(expected[0].BoneWorldMatrices, animator.Sample(0).BoneWorldMatrices);
        }

        foreach (double frame in new[] { 10d, 0d, 3d, 10d, 3d, 0d, 0d })
            Assert.Equal(expected[frame].BoneWorldMatrices, animator.Sample(frame).BoneWorldMatrices);
    }
}
