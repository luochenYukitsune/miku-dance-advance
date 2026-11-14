using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.ViewOutput.Tests;

public sealed class ViewTemporalQualityTests
{
    private static ViewTemporalUpscalingOptions Fsr => new() { Mode = ViewTemporalUpscalingMode.Fsr };

    [Fact]
    public void TemporalDefaultsOffAndViewsDoNotShareConfiguration()
    {
        var preview = new ViewRenderRequest(1920, 1080, new(TemporalUpscaling: Fsr));
        var export = new ExportRequest(new(1920, 1080, new(30000, 1001), 0, 30), preview.Quality);
        Assert.Equal(ViewTemporalUpscalingMode.Off, new ViewQualityOptions().EffectiveTemporalUpscaling.Mode);
        Assert.Equal(ViewTemporalQualityPreset.NativeAa, new ViewTemporalUpscalingOptions().QualityPreset);
        Assert.Equal(ViewXeSsQuality.NativeAa, new ViewTemporalUpscalingOptions().XeSsQuality);
        Assert.Equal(1000f / 60, preview.EffectiveQuality.EffectiveTemporalUpscaling.FrameTimeMilliseconds);
        Assert.Equal(1000f * 1001 / 30000, export.Render.EffectiveQuality.EffectiveTemporalUpscaling.FrameTimeMilliseconds);
        Assert.NotSame(preview.Quality, export.Render.Quality);
    }

    [Fact]
    public void TemporalRejectsStackedAlgorithmsAndInheritedSceneAaWithoutSilentlyChangingThem()
    {
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(TemporalUpscaling: Fsr,
            Upscaling: new() { Mode = ViewUpscalingMode.Fsr1 }).Validate());
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(TemporalUpscaling: Fsr, SpatialSamples: 2).Validate());
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(TemporalUpscaling: Fsr,
            Antialiasing: FrameAntialiasing.Smaa1X).Validate());
        var scene = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false,
            Antialiasing: FrameAntialiasing.Smaa1X, SpatialSamples: 4);
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(TemporalUpscaling: Fsr).ApplyTo(scene));
        var accepted = new ViewQualityOptions(TemporalUpscaling: Fsr,
            Antialiasing: FrameAntialiasing.Off, SpatialSamples: 1).ApplyTo(scene);
        Assert.Equal(FrameAntialiasing.Off, accepted.Antialiasing); Assert.Equal(1, accepted.SpatialSamples);
        Assert.Equal(4, scene.SpatialSamples);
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void TemporalGiConfigurationPreservesIndependentViewQuality(LightingMode mode)
    {
        var gi = new RayGiOptions(mode, Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.Spatial);
        var quality = new ViewQualityOptions(Gi: gi, TemporalUpscaling: Fsr);
        quality.Validate();
        var applied = quality.ApplyTo(new(OriginalAlphaBlendMode.Normal, true, false));
        Assert.Same(gi, applied.RayGi);
        Assert.Equal(FrameAntialiasing.Off, applied.Antialiasing);
        Assert.Equal(1, applied.SpatialSamples);
        Assert.False(new ViewQualityOptions().EffectiveGi.Enabled);
    }

    [Theory]
    [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(0)] [InlineData(-1)]
    public void TemporalTimingMustBePositiveAndFinite(float time)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => (Fsr with { FrameTimeMilliseconds = time }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewTemporalFrameState { FrameTimeMilliseconds = time }.Validate());
    }

    [Fact]
    public void TemporalInvalidEnumsAreRejectedEvenWhenOff()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewTemporalUpscalingOptions { Mode = (ViewTemporalUpscalingMode)99 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewTemporalUpscalingOptions { QualityPreset = (ViewTemporalQualityPreset)99 }.Validate());
    }
}
