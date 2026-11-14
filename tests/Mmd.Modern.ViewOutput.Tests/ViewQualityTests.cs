using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.ViewOutput.Tests;

public sealed class ViewQualityTests
{
    [Fact]
    public void DefaultsPreserveSceneAntialiasingAndSamplesWhileGiRemainsOff()
    {
        var scene = new OriginalFrameOptions(OriginalAlphaBlendMode.Additive, false, true,
            Antialiasing: FrameAntialiasing.Smaa1X, SmaaQuality: SmaaQuality.Ultra, SpatialSamples: 32);
        var quality = new ViewQualityOptions();
        var resolved = quality.ApplyTo(scene);
        Assert.Null(quality.Antialiasing); Assert.Null(quality.SmaaQuality); Assert.Null(quality.SpatialSamples);
        Assert.Equal(scene with { RayGi = RayGiOptions.Off }, resolved);
        Assert.Equal(ViewUpscalingMode.Off, quality.EffectiveUpscaling.Mode);
    }

    [Fact]
    public void ExplicitGiKeepsItsOriginalPerViewSemantics()
    {
        var gi = new RayGiOptions(Strength: 7, SamplesPerPixel: 8);
        var quality = new ViewQualityOptions(gi, SpatialSamples: 2);
        Assert.Same(gi, quality.EffectiveGi);
        Assert.Same(gi, quality.ApplyTo(null).RayGi);
        Assert.Equal(FrameAntialiasing.Off, quality.ApplyTo(null).Antialiasing);
    }

    [Fact]
    public void PreviewAndExportOverridesAreIndependentAndDoNotMutateScene()
    {
        var scene = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false,
            Antialiasing: FrameAntialiasing.Smaa1X, SmaaQuality: SmaaQuality.High, SpatialSamples: 4);
        var preview = new ViewRenderRequest(1920, 1080, new(SpatialSamples: 1,
            Upscaling: new() { Mode = ViewUpscalingMode.Fsr1 }));
        var export = new ExportRequest(new(1920, 1080, new(30), 0, 0),
            new(Antialiasing: FrameAntialiasing.Off, SmaaQuality: SmaaQuality.Ultra, SpatialSamples: 16));
        var previewScene = preview.EffectiveQuality.ApplyTo(scene);
        var exportScene = export.Render.EffectiveQuality.ApplyTo(scene);
        Assert.Equal(1, previewScene.SpatialSamples); Assert.Equal(SmaaQuality.High, previewScene.SmaaQuality);
        Assert.Equal(16, exportScene.SpatialSamples); Assert.Equal(FrameAntialiasing.Off, exportScene.Antialiasing);
        Assert.Equal(SmaaQuality.Ultra, exportScene.SmaaQuality);
        Assert.Equal(4, scene.SpatialSamples); Assert.Equal(SmaaQuality.High, scene.SmaaQuality);
        Assert.Equal(ViewUpscalingMode.Off, export.Render.EffectiveQuality.EffectiveUpscaling.Mode);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)] [InlineData(8)] [InlineData(16)] [InlineData(32)]
    public void AllExistingSpatialSampleCountsAreAccepted(int samples)
    {
        foreach (var preset in Enum.GetValues<SmaaQuality>())
            new ViewQualityOptions(Antialiasing: FrameAntialiasing.Smaa1X, SmaaQuality: preset, SpatialSamples: samples).Validate();
    }

    [Theory]
    [InlineData(0)] [InlineData(3)] [InlineData(64)]
    public void UnsupportedSpatialCountsAreRejected(int samples) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewQualityOptions(SpatialSamples: samples).Validate());

    [Fact]
    public void InvalidEnumsAreRejectedEvenWhenFeatureIsOff()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewQualityOptions(Antialiasing: (FrameAntialiasing)99).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewQualityOptions(SmaaQuality: (SmaaQuality)99).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewUpscalingOptions { Mode = (ViewUpscalingMode)99 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewUpscalingOptions { QualityPreset = (ViewFsr1QualityPreset)99 }.Validate());
    }

    [Theory]
    [InlineData(-.1f)] [InlineData(2.1f)] [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)]
    public void SharpnessMustBeFiniteStopsInRange(float sharpness) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewQualityOptions(Upscaling: new() { RcasSharpness = sharpness }).Validate());
}
