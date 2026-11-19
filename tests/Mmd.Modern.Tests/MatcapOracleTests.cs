using System.Numerics;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class MatcapOracleTests
{
    [Fact]
    public void PackedRgNormalPreservesInterpolatedNiLengthAndStrengthExtrapolation()
    {
        var ni = new Vector3(0, 0, 2);
        var tangent = new Vector4(1, 0, 0, 1);
        var rg = new Vector2(.8f, .6f);
        var expectedMapped = Vector3.Normalize(new Vector3(.6f, .4f, 2 * MathF.Sqrt(.6f)));
        Near(expectedMapped, MatcapOracle.PackedNormal(rg, ni, tangent, 1, true, 1));
        Near(Vector3.Normalize(2 * expectedMapped - Vector3.UnitZ), MatcapOracle.PackedNormal(rg, ni, tangent, 1, true, 2));
        Near(Vector3.UnitZ, MatcapOracle.PackedNormal(rg, ni, tangent, 1, true, 0));
        Near(Vector3.UnitZ, MatcapOracle.PackedNormal(rg, ni, tangent, 1, true, -3));
        Near(Vector3.UnitZ, MatcapOracle.PackedNormal(new(.5f), ni, tangent, 1, true, 2));
        Near(Vector3.Normalize(new Vector3(1, 1, 0)), MatcapOracle.PackedNormal(Vector2.One,
            Vector3.UnitZ, tangent, 1, true, 1));
    }

    [Fact]
    public void RasterBackfaceAndObjectReflectionEachApplyTheirOwnHandednessExactlyOnce()
    {
        var rg = new Vector2(.7f, .8f); var tangent = new Vector4(1, 0, 0, 1);
        var front = MatcapOracle.PackedNormal(rg, Vector3.UnitZ, tangent, 1, true, 1);
        var back = MatcapOracle.PackedNormal(rg, Vector3.UnitZ, tangent, 1, false, 1);
        var reflected = MatcapOracle.PackedNormal(rg, Vector3.UnitZ, tangent, -1, true, 1);
        var mirroredUv = MatcapOracle.PackedNormal(rg, Vector3.UnitZ, new(1, 0, 0, -1), 1, true, 1);
        Near(-front, back);
        Near(new(front.X, -front.Y, front.Z), reflected);
        Near(reflected, mirroredUv);
    }

    [Fact]
    public void CameraYRotationUsesInvertedZAndTopFirstVWithRoll()
    {
        var n = new Vector3(.36f, .48f, .8f);
        Near(new Vector2(.68f, .26f), MatcapOracle.Coordinates(n, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, 0));
        Near(new Vector2(.1f, .26f), MatcapOracle.Coordinates(n, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, MathF.PI / 2));
        Near(new Vector2(.9f, .26f), MatcapOracle.Coordinates(n, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, -MathF.PI / 2));
        Near(new Vector2(.74f, .68f), MatcapOracle.Coordinates(n, Vector3.UnitY, -Vector3.UnitX, Vector3.UnitZ, 0));
    }

    [Fact]
    public void SourceRangeCanReachOnePointFiveBeforePartiallyMaskedGateAndDegenerateHardnessIsZero()
    {
        var source = MatcapOracle.SourceGate(Vector3.UnitZ, Vector3.UnitZ, .5f, .25f, .25f, 0);
        Assert.Equal(1.5f, source.Range); Assert.Equal(.75f, source.Gate);
        Assert.Equal(1, MatcapOracle.SourceGate(Vector3.UnitZ, Vector3.UnitZ, .5f, .7f, .7f, 0).Gate);
        Assert.Equal(0, MatcapOracle.SourceGate(-Vector3.UnitZ, Vector3.UnitZ, .5f, 1, 1, 0).Gate);
        Assert.Equal(0, MatcapOracle.SourceGate(Vector3.UnitZ, Vector3.UnitZ, 0, 1, 1, 0).Gate);
        Assert.Equal(0, MatcapOracle.SourceGate(Vector3.UnitZ, Vector3.UnitZ, 1, 1, 1, 0).Gate);
        Assert.Equal(.75f, MatcapOracle.SourceGate(Vector3.UnitZ, Vector3.UnitZ, .5f, 1, 1, -.5f).Gate);
    }

    [Fact]
    public void SrgbDecodesIndividualTexelsBeforeLinearInterpolationAndRepeatIncludesDiskBoundary()
    {
        byte[] image = [0, 0, 0, 0, 255, 255, 255, 255];
        var mid = MatcapOracle.BilinearRepeat(image, 2, 1, new(.5f, .5f), true);
        Assert.Equal(new Vector4(.5f), mid);
        Assert.True(MathF.Abs(mid.X - MatcapOracle.Srgb(.5f)) > .25f);
        Assert.Equal(mid, MatcapOracle.BilinearRepeat(image, 2, 1, new(0, .5f), true));
        Assert.Equal(mid, MatcapOracle.BilinearRepeat(image, 2, 1, new(1, .5f), true));
        Assert.Equal(mid, MatcapOracle.BilinearRepeat(image, 2, 1, new(-1, 1.5f), true));
        byte[] numerical = [128, 64, 32, 128];
        var linear = MatcapOracle.BilinearRepeat(numerical, 1, 1, new(.5f), false);
        var srgb = MatcapOracle.BilinearRepeat(numerical, 1, 1, new(.5f), true);
        Assert.Equal(linear.W, srgb.W); Assert.True(srgb.X < linear.X);
    }

    [Fact]
    public void StraightImageAlphaOnlyChangesScreenColorAndWhiteTintIsExactIdentity()
    {
        var initial = new Vector3(.2f, .5f, 2);
        var image = new Vector4(.4f, .8f, .2f, .25f);
        Near(initial, MatcapOracle.MaterialColor(initial, Vector3.One, image, .7f));
        Near(initial, MatcapOracle.MaterialColor(initial, Vector3.Zero, image, 0));
        Near(initial * new Vector3(.1f, .2f, .05f), MatcapOracle.MaterialColor(initial, Vector3.Zero, image, 1));
        Near(initial * new Vector3(1, .2f, .05f), MatcapOracle.MaterialColor(initial, Vector3.UnitX, image, 1));
        Near(Vector3.Zero, MatcapOracle.MaterialColor(initial, Vector3.Zero, image with { W = 0 }, 1));
        // HDR output and signed material delta are intentionally unclamped.
        var changed = MatcapOracle.MaterialColor(initial, new(2), image, 1);
        Assert.True(changed.Z > initial.Z);
        Assert.True(MatcapOracle.Delta(initial, Vector3.Zero, 1).Z < -1);
    }

    [Fact]
    public void CoupledDeltaIncludesBothRimOverlayAndDynamicRadialCrossTermsWithoutRepeatingThem()
    {
        var c0 = new Vector3(.2f, .7f, 2); var cm = new Vector3(.1f, .4f, 1.5f); var rimColor = new Vector3(.3f, .6f, .2f);
        const float baseline = .8f, rim = .4f, radial = .3f;
        var e0 = MatcapOracle.Overlay(c0, rimColor, rim) * (baseline + rim + radial);
        var em = MatcapOracle.Overlay(cm, rimColor, rim) * (baseline + rim + radial);
        Near(em - e0, MatcapOracle.Delta(c0, cm, baseline, rim, radial, rimColor));
        Near(Vector3.Zero, MatcapOracle.Delta(c0, c0, baseline, rim, radial, rimColor));
        Assert.NotEqual(MatcapOracle.Delta(c0, cm, baseline, rim, radial, rimColor),
            MatcapOracle.Delta(c0, cm, baseline, rim, 0, rimColor));
    }

    [Fact]
    public void BindingRejectsMissingIndicesEncodingUvAndNonfiniteInputsBeforeGpu()
    {
        var valid = T8dTestHelpers.EndfieldMatcap(0, 1, 2, T8dTestHelpers.EncodingSrgbRgb, 0, new(.2f), Vector3.Zero);
        Assert.True(T8dTestHelpers.ValidateEndfield(valid).IsValid);
        var draw = T8dTestHelpers.BuildEndfieldDrawData([valid]);
        Assert.NotNull(draw);
        Assert.Equal(new Vector4(Vector3.UnitZ, 0), T8dTestHelpers.ReadCbvVectors(draw)[3]);
        foreach (var bad in new[]
        {
            T8dTestHelpers.EndfieldMatcap(-1, 1, 2, T8dTestHelpers.EncodingSrgbRgb, 0, new(.2f), Vector3.Zero),
            T8dTestHelpers.EndfieldMatcap(0, -1, 2, T8dTestHelpers.EncodingSrgbRgb, 0, new(.2f), Vector3.Zero),
            T8dTestHelpers.EndfieldMatcap(0, 1, -1, T8dTestHelpers.EncodingSrgbRgb, 0, new(.2f), Vector3.Zero),
            T8dTestHelpers.EndfieldMatcap(0, 1, 2, T8dTestHelpers.EncodingSrgbRgb, -1, new(.2f), Vector3.Zero),
            T8dTestHelpers.EndfieldMatcap(0, 1, 2, T8dTestHelpers.EncodingSrgbRgb, 5, new(.2f), Vector3.Zero),
            T8dTestHelpers.EndfieldMatcap(0, 1, 2, "InvalidEncoding", 0, new(.2f), Vector3.Zero),
            T8dTestHelpers.EndfieldMatcap(0, 1, 2, T8dTestHelpers.EncodingSrgbRgb, 0, new(.2f), Vector3.Zero, normalStrength: float.NaN),
            T8dTestHelpers.EndfieldMatcap(0, 1, 2, T8dTestHelpers.EncodingSrgbRgb, 0, new(.2f), Vector3.Zero, rotationRadians: float.PositiveInfinity),
            T8dTestHelpers.EndfieldMatcap(0, 1, 2, T8dTestHelpers.EncodingSrgbRgb, 0, new(.2f), Vector3.Zero, highlightCoefficient: float.NaN),
            T8dTestHelpers.EndfieldMatcap(0, 1, 2, T8dTestHelpers.EncodingSrgbRgb, 0, new(.2f), Vector3.Zero, shadowHardness: float.NaN),
            T8dTestHelpers.EndfieldMatcap(0, 1, 2, T8dTestHelpers.EncodingSrgbRgb, 0, new(.2f), Vector3.Zero, baselineStrength: float.NaN),
            T8dTestHelpers.EndfieldMatcap(0, 1, 2, T8dTestHelpers.EncodingSrgbRgb, 0, new(.2f), Vector3.Zero, lightControlDirection: Vector3.Zero),
            T8dTestHelpers.EndfieldMatcap(0, 1, 2, T8dTestHelpers.EncodingSrgbRgb, 0, new(-1), Vector3.Zero),
            T8dTestHelpers.EndfieldMatcap(0, 1, 2, T8dTestHelpers.EncodingSrgbRgb, 0, new(.2f), new(float.NaN)),
        })
            Assert.False(T8dTestHelpers.ValidateEndfield(bad).IsValid);
        var edgeValid = T8dTestHelpers.EndfieldMatcap(0, 1, 2, T8dTestHelpers.EncodingSrgbRgb, 4, new(.2f), new(16), normalStrength: -2, baselineStrength: -2);
        Assert.True(T8dTestHelpers.ValidateEndfield(edgeValid).IsValid);
    }

    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0, 2e-6f);
    private static void Near(Vector2 expected, Vector2 actual) => Assert.InRange(Vector2.Distance(expected, actual), 0, 2e-6f);
}
