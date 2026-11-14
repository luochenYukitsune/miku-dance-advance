using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class ViewQualityAdapterTests
{
    [Theory]
    [InlineData(ViewFsr1QualityPreset.UltraQuality, D3D12Fsr1QualityPreset.UltraQuality, 1476, 830)]
    [InlineData(ViewFsr1QualityPreset.Quality, D3D12Fsr1QualityPreset.Quality, 1280, 720)]
    [InlineData(ViewFsr1QualityPreset.Balanced, D3D12Fsr1QualityPreset.Balanced, 1129, 635)]
    [InlineData(ViewFsr1QualityPreset.Performance, D3D12Fsr1QualityPreset.Performance, 960, 540)]
    public void SpatialFallbackMapsSettingsAndKeepsRenderAndDisplayDimensionsSeparate(
        ViewFsr1QualityPreset neutral, D3D12Fsr1QualityPreset backend, int width, int height)
    {
        var options = D3D12ViewQuality.Upscaling(new(Upscaling: new()
        { Mode = ViewUpscalingMode.Fsr1, QualityPreset = neutral, EnableRcas = false, RcasSharpness = 1.75f }));
        Assert.Equal(D3D12SpatialUpscalingMode.Fsr1, options.Mode); Assert.Equal(backend, options.QualityPreset);
        Assert.False(options.EnableRcas); Assert.Equal(1.75f, options.RcasSharpness);
        Assert.Equal(new D3D12SpatialUpscalingDimensions(width, height, 1920, 1080), options.GetDimensions(1920, 1080));
    }

    [Fact]
    public void DefaultMappingUsesNativeDimensions()
    {
        var options = D3D12ViewQuality.Upscaling(new());
        Assert.Equal(D3D12SpatialUpscalingMode.Off, options.Mode);
        Assert.Equal(new D3D12SpatialUpscalingDimensions(1920, 1080, 1920, 1080), options.GetDimensions(1920, 1080));
    }
}
