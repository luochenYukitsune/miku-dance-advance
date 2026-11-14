using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

// Verified scope and execution evidence: docs/stages/2026-10-24-native-diffusion.md.
public sealed class D3D12DiffusionOptionsTests
{
    [Fact]
    public void DefaultsAreDisabledButRetainReferenceStrengthAndExtent()
    {
        var options = new D3D12DiffusionOptions();
        options.Validate();
        Assert.False(options.IsActive);
        Assert.Equal(.7f, options.Strength);
        Assert.Equal(.002f, options.Extent);
        Assert.Equal(options, D3D12DiffusionOptions.Off);
    }

    public static IEnumerable<object[]> InvalidOptions()
    {
        foreach (bool enabled in new[] { false, true })
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -.001f })
        {
            yield return [new D3D12DiffusionOptions(enabled, Strength: invalid)];
            yield return [new D3D12DiffusionOptions(enabled, Extent: invalid)];
        }
        yield return [new D3D12DiffusionOptions(false, Strength: 1.001f)];
        yield return [new D3D12DiffusionOptions(false, Extent: .0101f)];
        yield return [new D3D12DiffusionOptions(true, Strength: 0, Extent: float.NaN)];
    }

    [Theory, MemberData(nameof(InvalidOptions))]
    public void InvalidScalarsFailEvenWhenDisabledOrZeroStrength(D3D12DiffusionOptions options)
        => Assert.Throws<ArgumentOutOfRangeException>(options.Validate);

    [Fact]
    public void InclusiveEndpointsAndZeroExtentRetainRequestedValues()
    {
        foreach (float strength in new[] { 0f, 1f })
        foreach (float extent in new[] { 0f, .01f })
        {
            var options = new D3D12DiffusionOptions(true, strength, extent);
            options.Validate();
            Assert.Equal(strength > 0, options.IsActive);
            Assert.Equal(extent, options.Extent);
        }
        Assert.True(new D3D12DiffusionOptions(true, Extent: 0).IsActive);
    }

    [Theory]
    [InlineData(1920,1080,2.16f)]
    [InlineData(1440,1440,2.88f)]
    [InlineData(3840,2160,4.32f)]
    public void DefaultTapSpacingUsesOutputHeightOnBothAxes(int width, int height, float expectedPixels)
    {
        var uv = new D3D12DiffusionOptions(true).GetTapStepUv(width,height);
        Assert.InRange(MathF.Abs(uv.X*width-expectedPixels),0,1e-5f);
        Assert.InRange(MathF.Abs(uv.Y*height-expectedPixels),0,1e-5f);
    }

    [Theory]
    [InlineData(0,1440)]
    [InlineData(1440,0)]
    [InlineData(4097,1440)]
    [InlineData(1440,4097)]
    public void InvalidOutputDimensionsAreNotUsedToCreateTapConstants(int width,int height)
        => Assert.Throws<ArgumentOutOfRangeException>(()=>new D3D12DiffusionOptions(true).GetTapStepUv(width,height));

    [Fact]
    public void ActiveAcceptsStraightAndRejectsOnlyNoncompositableLegacyAlpha()
    {
        var options = new D3D12DiffusionOptions(true);
        options.ValidateOutput(FrameAlphaOutput.Straight,1);
        options.ValidateOutput(FrameAlphaOutput.Straight,0);
        options.ValidateOutput(FrameAlphaOutput.Straight,.5f);
        Assert.Throws<NotSupportedException>(()=>options.ValidateOutput(default,.5f));
        Assert.Throws<NotSupportedException>(()=>options.ValidateOutput(default,0));
        options.ValidateOutput(default,1);
    }

    [Fact]
    public void DisabledAndZeroStrengthKeepExistingTransparentPaths()
    {
        new D3D12DiffusionOptions().ValidateOutput(FrameAlphaOutput.Straight,0);
        new D3D12DiffusionOptions(true,Strength:0).ValidateOutput(FrameAlphaOutput.Straight,0);
        Assert.False(new D3D12DiffusionOptions(true,Strength:0).IsActive);
    }
}
