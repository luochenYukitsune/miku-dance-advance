using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.ViewOutput.Tests;

public sealed class ViewXeSsQualityTests
{
    [Fact]
    public void XeSsSelectionIsIndependentOfFsrPresetAndKeepsOfflineFrameDuration()
    {
        var options = new ViewTemporalUpscalingOptions
        {
            Mode = ViewTemporalUpscalingMode.XeSs,
            XeSsQuality = ViewXeSsQuality.UltraQualityPlus,
            QualityPreset = ViewTemporalQualityPreset.Performance
        };
        options.Validate();
        var preview = new ViewRenderRequest(1920, 1080, new(TemporalUpscaling: options));
        var export = new ExportRequest(new(1920, 1080, new(24000, 1001), 0, 1), preview.Quality).Render;
        export.Validate();
        Assert.Equal(ViewXeSsQuality.UltraQualityPlus, export.EffectiveQuality.EffectiveTemporalUpscaling.XeSsQuality);
        Assert.Equal(ViewTemporalQualityPreset.Performance, export.EffectiveQuality.EffectiveTemporalUpscaling.QualityPreset);
        Assert.InRange(export.EffectiveQuality.EffectiveTemporalUpscaling.FrameTimeMilliseconds, 41.7083f, 41.7084f);
        Assert.Equal(1000f / 60, preview.EffectiveQuality.EffectiveTemporalUpscaling.FrameTimeMilliseconds);
        Assert.Equal(ViewTemporalUpscalingMode.Off, new ViewQualityOptions().EffectiveTemporalUpscaling.Mode);
    }

    [Fact]
    public void InvalidXeSsQualityAndUnsupportedStackedAaAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewTemporalUpscalingOptions
            { XeSsQuality = (ViewXeSsQuality)999 }.Validate());
        var temporal = new ViewTemporalUpscalingOptions { Mode = ViewTemporalUpscalingMode.XeSs };
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(TemporalUpscaling: temporal,
            Antialiasing: FrameAntialiasing.Smaa1X).Validate());
        Assert.Throws<NotSupportedException>(() => new ViewQualityOptions(TemporalUpscaling: temporal,
            SpatialSamples: 32).Validate());
    }
}
