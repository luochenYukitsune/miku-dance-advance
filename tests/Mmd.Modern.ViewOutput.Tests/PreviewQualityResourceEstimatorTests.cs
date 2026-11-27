using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.ViewOutput.Tests;

public sealed class PreviewQualityResourceEstimatorTests
{
    [Theory]
    [InlineData(1280, 720, 0.5f, 640, 360)]
    [InlineData(1280, 720, 0.75f, 960, 540)]
    [InlineData(1280, 720, 1.0f, 1280, 720)]
    [InlineData(1280, 720, 1.5f, 1920, 1080)]
    [InlineData(3840, 2160, 1.5f, 4096, 2304)]
    public void DerivesInternalResolutionFromRenderScaleWith4kClamp(
        int vw, int vh, float renderScale, int expectedRw, int expectedRh)
    {
        var quality = new ViewQualityOptions { RenderScale = renderScale };
        var estimate = PreviewQualityResourceEstimator.Estimate(quality, vw, vh);
        Assert.Equal(vw, estimate.ViewportWidth);
        Assert.Equal(vh, estimate.ViewportHeight);
        Assert.Equal(expectedRw, estimate.RenderWidth);
        Assert.Equal(expectedRh, estimate.RenderHeight);
    }

    [Fact]
    public void DerivesInternalResolutionFromFsr1AndTemporalPresets()
    {
        var fsr1 = new ViewQualityOptions(Upscaling: new ViewUpscalingOptions
        {
            Mode = ViewUpscalingMode.Fsr1,
            QualityPreset = ViewFsr1QualityPreset.Performance
        });
        var fsr1Est = PreviewQualityResourceEstimator.Estimate(fsr1, 1920, 1080);
        Assert.Equal(960, fsr1Est.RenderWidth);
        Assert.Equal(540, fsr1Est.RenderHeight);

        var dlss = new ViewQualityOptions(TemporalUpscaling: new ViewTemporalUpscalingOptions
        {
            Mode = ViewTemporalUpscalingMode.Dlss,
            DlssQuality = ViewDlssQuality.Quality
        });
        var dlssEst = PreviewQualityResourceEstimator.Estimate(dlss, 1920, 1080);
        Assert.Equal(1280, dlssEst.RenderWidth);
        Assert.Equal(720, dlssEst.RenderHeight);
    }

    [Fact]
    public void EstimatedRenderBufferMiBScalesAcrossMsaaShadowsSsaoGiAndPathTracing()
    {
        var baseline = PreviewQualityResourceEstimator.Estimate(new ViewQualityOptions(Antialiasing: FrameAntialiasing.Off)
        {
            Shadow = new ViewShadowOptions(0)
        }, 1280, 720);

        var msaa2 = PreviewQualityResourceEstimator.Estimate(new ViewQualityOptions(Antialiasing: FrameAntialiasing.Msaa2X)
        {
            Shadow = new ViewShadowOptions(0)
        }, 1280, 720);
        var msaa8 = PreviewQualityResourceEstimator.Estimate(new ViewQualityOptions(Antialiasing: FrameAntialiasing.Msaa8X)
        {
            Shadow = new ViewShadowOptions(0)
        }, 1280, 720);
        Assert.True(msaa2.EstimatedRenderBufferBytes > baseline.EstimatedRenderBufferBytes);
        Assert.True(msaa8.EstimatedRenderBufferBytes > msaa2.EstimatedRenderBufferBytes);

        var shadow1k = PreviewQualityResourceEstimator.Estimate(new ViewQualityOptions { Shadow = new ViewShadowOptions(1024) }, 1280, 720);
        var shadow4k = PreviewQualityResourceEstimator.Estimate(new ViewQualityOptions { Shadow = new ViewShadowOptions(4096) }, 1280, 720);
        Assert.True(shadow4k.EstimatedRenderBufferBytes > shadow1k.EstimatedRenderBufferBytes);

        var ssaoOn = PreviewQualityResourceEstimator.Estimate(new ViewQualityOptions
        {
            Shadow = new ViewShadowOptions(0),
            ScreenSpaceAo = new ScreenSpaceAoOptions(true, 0.5f, 1.5f, 8)
        }, 1280, 720);
        Assert.True(ssaoOn.EstimatedRenderBufferBytes > baseline.EstimatedRenderBufferBytes);

        var giOn = PreviewQualityResourceEstimator.Estimate(new ViewQualityOptions(
            Gi: new RayGiOptions(LightingMode.SoftwareRayTracing, Denoiser: RayGiDenoiser.TemporalSpatial)), 1280, 720);
        Assert.True(giOn.EstimatedRenderBufferBytes > shadow1k.EstimatedRenderBufferBytes);

        var ptOn = PreviewQualityResourceEstimator.Estimate(new ViewQualityOptions(
            PipelineMode: RenderPipelineMode.PathTracing,
            PathTracing: PathTracingOptions.Default), 1280, 720);
        Assert.True(ptOn.EstimatedRenderBufferBytes > baseline.EstimatedRenderBufferBytes);
        Assert.Equal(PreviewPerformanceLoadTier.Heavy, ptOn.LoadTier);

        var fsrNoRcas = PreviewQualityResourceEstimator.Estimate(new ViewQualityOptions(
            Upscaling: new ViewUpscalingOptions { Mode = ViewUpscalingMode.Fsr1, EnableRcas = false })
        {
            Shadow = new ViewShadowOptions(0)
        }, 1280, 720);
        var fsrWithRcas = PreviewQualityResourceEstimator.Estimate(new ViewQualityOptions(
            Upscaling: new ViewUpscalingOptions { Mode = ViewUpscalingMode.Fsr1, EnableRcas = true })
        {
            Shadow = new ViewShadowOptions(0)
        }, 1280, 720);
        Assert.True(fsrWithRcas.EstimatedRenderBufferBytes > fsrNoRcas.EstimatedRenderBufferBytes);
    }

    [Fact]
    public void BuiltInPresetsMapToExpectedLoadTiersAndBudgetOverrunTriggersWarning()
    {
        Assert.Equal(PreviewPerformanceLoadTier.Light,
            PreviewQualityResourceEstimator.Estimate(ViewQualityPresets.Create(BuiltInPreviewPresetKind.Performance), 1280, 720).LoadTier);
        Assert.Equal(PreviewPerformanceLoadTier.Moderate,
            PreviewQualityResourceEstimator.Estimate(ViewQualityPresets.Create(BuiltInPreviewPresetKind.Balanced), 1280, 720).LoadTier);
        Assert.Equal(PreviewPerformanceLoadTier.High,
            PreviewQualityResourceEstimator.Estimate(ViewQualityPresets.Create(BuiltInPreviewPresetKind.High), 1280, 720).LoadTier);
        Assert.Equal(PreviewPerformanceLoadTier.Heavy,
            PreviewQualityResourceEstimator.Estimate(ViewQualityPresets.Create(BuiltInPreviewPresetKind.UltraRayTraced), 1280, 720).LoadTier);

        var high = ViewQualityPresets.Create(BuiltInPreviewPresetKind.High);
        var smallBudget = PreviewQualityResourceEstimator.Estimate(high, 1920, 1080, adapterBudgetBytes: 64UL * 1024 * 1024);
        Assert.True(smallBudget.IsOverBudget);
        Assert.True(smallBudget.BudgetUsageRatio > 1.0);

        var largeBudget = PreviewQualityResourceEstimator.Estimate(high, 1920, 1080, adapterBudgetBytes: 8192UL * 1024 * 1024);
        Assert.False(largeBudget.IsOverBudget);
        Assert.InRange(largeBudget.BudgetUsageRatio!.Value, 0.01, 0.5);

        var classic = PreviewQualityResourceEstimator.Estimate(high, 1280, 720, isClassicEngine: true);
        Assert.True(classic.IsClassicEngine);
        Assert.Equal(PreviewPerformanceLoadTier.Light, classic.LoadTier);
    }
}
