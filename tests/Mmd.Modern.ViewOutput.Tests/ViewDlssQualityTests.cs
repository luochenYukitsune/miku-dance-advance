using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.ViewOutput.Tests;

public sealed class ViewDlssQualityTests
{
    [Fact]
    public void DlssAppendsModeAndDefaultsToNativeAaWithExactExportTiming()
    {
        Assert.Equal(0, (int)ViewTemporalUpscalingMode.Off); Assert.Equal(1, (int)ViewTemporalUpscalingMode.Fsr);
        Assert.Equal(2, (int)ViewTemporalUpscalingMode.XeSs); Assert.Equal(3, (int)ViewTemporalUpscalingMode.Dlss);
        var options = new ViewTemporalUpscalingOptions { Mode = ViewTemporalUpscalingMode.Dlss };
        Assert.Equal(ViewDlssQuality.NativeAa, options.DlssQuality);
        var request = new ExportRequest(new(1920, 1080, new(24000, 1001), 0, 1), new(TemporalUpscaling: options)).Render;
        request.Validate(); Assert.Equal(ViewDlssQuality.NativeAa, request.EffectiveQuality.EffectiveTemporalUpscaling.DlssQuality);
        Assert.InRange(request.EffectiveQuality.EffectiveTemporalUpscaling.FrameTimeMilliseconds, 41.7083f, 41.7084f);
        Assert.Equal(ViewTemporalUpscalingMode.Off, new ViewQualityOptions().EffectiveTemporalUpscaling.Mode);
        Assert.Throws<ArgumentOutOfRangeException>(() => (options with { DlssQuality = (ViewDlssQuality)999 }).Validate());
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(TemporalUpscaling: options, SpatialSamples: 2).Validate());
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(TemporalUpscaling: options, Antialiasing: FrameAntialiasing.Smaa1X).Validate());
    }
}
