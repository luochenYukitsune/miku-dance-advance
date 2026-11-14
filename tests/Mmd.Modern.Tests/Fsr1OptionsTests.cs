using Mmd.Modern.Rendering.D3D12;
using Xunit;
namespace Mmd.Modern.Tests;

public sealed class Fsr1OptionsTests
{
    [Theory]
    [InlineData(D3D12Fsr1QualityPreset.UltraQuality, 1.3)]
    [InlineData(D3D12Fsr1QualityPreset.Quality, 1.5)]
    [InlineData(D3D12Fsr1QualityPreset.Balanced, 1.7)]
    [InlineData(D3D12Fsr1QualityPreset.Performance, 2.0)]
    public void PresetsChooseRenderExtentAndKeepRequestedDisplayExtent(D3D12Fsr1QualityPreset preset,double scale)
    {
        var options=new D3D12SpatialUpscalingOptions {Mode=D3D12SpatialUpscalingMode.Fsr1,QualityPreset=preset};
        var size=options.GetDimensions(97,73);
        Assert.Equal(Math.Max(1,(int)Math.Floor(97/scale)),size.RenderWidth);
        Assert.Equal(Math.Max(1,(int)Math.Floor(73/scale)),size.RenderHeight);
        Assert.Equal(97,size.DisplayWidth);Assert.Equal(73,size.DisplayHeight);
        Assert.Equal(1,options.GetDimensions(1,1).RenderWidth);
    }
    [Fact]
    public void OffKeepsExtentAndInvalidOptionsFailBeforeGpuRecording()
    {
        Assert.Equal(new D3D12SpatialUpscalingDimensions(97,73,97,73),D3D12SpatialUpscalingOptions.Off.GetDimensions(97,73));
        Assert.Throws<ArgumentOutOfRangeException>(()=>new D3D12SpatialUpscalingOptions {RcasSharpness=float.NaN}.GetDimensions(97,73));
        Assert.Throws<ArgumentOutOfRangeException>(()=>new D3D12SpatialUpscalingOptions {QualityPreset=(D3D12Fsr1QualityPreset)999}.GetDimensions(97,73));
        Assert.Throws<ArgumentOutOfRangeException>(()=>D3D12SpatialUpscalingOptions.Off.GetDimensions(4097,73));
    }
    [Theory]
    [InlineData(-.01f)]
    [InlineData(2.01f)]
    [InlineData(float.PositiveInfinity)]
    public void OfficialRcasSharpnessStopsRejectOutOfRangeValues(float sharpness)
        =>Assert.Throws<ArgumentOutOfRangeException>(()=>new D3D12SpatialUpscalingOptions{RcasSharpness=sharpness}.GetDimensions(97,73));
}
