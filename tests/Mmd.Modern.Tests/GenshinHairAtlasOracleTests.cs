using System.Numerics;
using Xunit;

namespace Mmd.Modern.Tests;

// Independent source-contract vectors; runtime acceptance lives in the GPU tests.
public sealed class GenshinHairAtlasOracleTests
{
    [Theory]
    [InlineData(1, .45f, .95f)]
    [InlineData(2, .35f, .85f)]
    [InlineData(3, .25f, .75f)]
    [InlineData(4, .15f, .65f)]
    [InlineData(5, .05f, .55f)]
    public void ExplicitRowsKeepSavedAtlasOrder(float row, float night, float day)
    {
        var coordinate = GenshinHairAtlasOracle.Coordinates(0, .5f, row, .5f, .5f, 1, 1, 0, 0);
        Assert.Equal(night, coordinate.NightY, 6);
        Assert.Equal(day, coordinate.DayY, 6);
    }

    [Theory]
    [InlineData(0, .5f, 1)]
    [InlineData(.5f, 0, 1)]
    [InlineData(.5f, .5f, 0)]
    public void ZeroDivisorsReturnExactQOne(float position, float width, float vertexWidth)
    {
        var coordinate = GenshinHairAtlasOracle.Coordinates(0, .5f, 1, position, width, vertexWidth, 1, 0, 0);
        Assert.Equal(1, coordinate.Q);
    }

    [Fact]
    public void ExactShadowThresholdIsLitAndAlwaysLitThresholdIsStrict()
    {
        Assert.Equal(0, GenshinHairAtlasOracle.Coordinates(0, .5f, 1, .5f, 1, 1, 1, 0, 0).Gate);
        Assert.Equal(1, GenshinHairAtlasOracle.Coordinates(0, .95f, 1, 1, 1, 1, 1, 0, 0).Gate);
        Assert.Equal(0, GenshinHairAtlasOracle.Coordinates(0, MathF.BitIncrement(.95f), 1, 1, 1, 1, 1, 0, 0).Gate);
    }

    [Fact]
    public void SourceVertexWidthIsIndependentFromLightmapGreen()
    {
        var a = GenshinHairAtlasOracle.Coordinates(0, .25f, 1, .8f, .4f, .1f, 1, 0, 0);
        var b = GenshinHairAtlasOracle.Coordinates(0, .75f, 1, .8f, .4f, .1f, 1, 0, 0);
        var c = GenshinHairAtlasOracle.Coordinates(0, .25f, 1, .8f, .4f, .9f, 1, 0, 0);
        Assert.Equal(a.Q, b.Q); Assert.NotEqual(a.Q, c.Q);
    }

    [Fact]
    public void ClosestExtendUsesEdgesAndIgnoresAtlasAlpha()
    {
        byte[] pixels = [0,0,0,0, 255,128,64,0];
        Assert.Equal(Vector3.Zero, GenshinHairAtlasOracle.ClosestExtend(pixels, 2, 1, -1, 0, false));
        Assert.Equal(new Vector3(1,128f/255,64f/255), GenshinHairAtlasOracle.ClosestExtend(pixels, 2, 1, 1, 2, false));
        Assert.Equal(Vector3.Zero, GenshinHairAtlasOracle.ClosestExtend(pixels, 2, 1, MathF.BitDecrement(.5f), 0, false));
        Assert.NotEqual(Vector3.Zero, GenshinHairAtlasOracle.ClosestExtend(pixels, 2, 1, .5f, 0, false));
    }

    [Fact]
    public void DayNightMixesColorsInsteadOfInterpolatingRows()
    {
        var color = GenshinHairAtlasOracle.Palette(Vector3.UnitX, Vector3.UnitZ, .25f, 1);
        Assert.Equal(new Vector3(.75f,0,.25f), color);
        Assert.Equal(Vector3.One, GenshinHairAtlasOracle.Palette(Vector3.Zero, Vector3.Zero, .25f, 0));
        Assert.Equal(Vector3.Zero, GenshinHairAtlasOracle.Palette(Vector3.Zero, Vector3.Zero, .25f, 1));
    }

    [Fact]
    public void ExplicitEncodingDecodesOnceAndVisibilityDoesNotApplyAnOldNormalPalette()
    {
        Assert.Equal(.21404114f, GenshinHairAtlasOracle.Decode(.5f), 6);
        var atlas = new Vector3(.2f,.4f,.8f);
        var dark = new Vector3(.1f,.1f,.1f);
        Assert.Equal(atlas, GenshinHairAtlasOracle.MdaVisibility(atlas, dark, 1));
        Assert.Equal(dark, GenshinHairAtlasOracle.MdaVisibility(atlas, dark, 0));
        Assert.True(Vector3.Distance(new Vector3(.15f,.25f,.45f), GenshinHairAtlasOracle.MdaVisibility(atlas, dark, .5f)) < 1e-6f);
    }
}
