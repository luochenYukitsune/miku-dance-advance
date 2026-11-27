using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class SmaaQualityOptionsTests
{
    [Fact]
    public void DefaultsPreserveOffHighAndOneSpatialSample()
    {
        var options = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false);

        Assert.Equal(FrameAntialiasing.Off, options.Antialiasing);
        Assert.Equal(SmaaQuality.High, options.SmaaQuality);
        Assert.Equal(1, options.SpatialSamples);
        Assert.Equal(new[] { "Low", "Medium", "High", "Ultra" }, Enum.GetNames<SmaaQuality>());
        OriginalFrameMath.Validate(options);
    }

    [Theory]
    [InlineData(SmaaQuality.Low)]
    [InlineData(SmaaQuality.Medium)]
    [InlineData(SmaaQuality.High)]
    [InlineData(SmaaQuality.Ultra)]
    public void EachQualityCanBeSelectedIndependentlyOfAntialiasing(SmaaQuality quality)
    {
        foreach (var antialiasing in Enum.GetValues<FrameAntialiasing>())
        {
            var options = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false,
                Antialiasing: antialiasing, SmaaQuality: quality);

            OriginalFrameMath.Validate(options);
            Assert.Equal(quality, options.SmaaQuality);
            Assert.Equal(1, options.SpatialSamples);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void EachSpatialSampleCountCanBeCombinedWithEveryQualityAndAntialiasing(int samples)
    {
        foreach (var quality in Enum.GetValues<SmaaQuality>())
        foreach (var antialiasing in Enum.GetValues<FrameAntialiasing>())
        {
            var options = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false,
                Antialiasing: antialiasing, SmaaQuality: quality, SpatialSamples: samples);

            if (samples > 1 && antialiasing is FrameAntialiasing.Taa or FrameAntialiasing.Msaa2X or FrameAntialiasing.Msaa4X or FrameAntialiasing.Msaa8X)
            {
                Assert.Throws<NotSupportedException>(() => OriginalFrameMath.Validate(options));
                continue;
            }

            OriginalFrameMath.Validate(options);
            Assert.Equal(samples, options.SpatialSamples);
            Assert.Equal(quality, options.SmaaQuality);
            Assert.Equal(antialiasing, options.Antialiasing);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void UnknownQualityIsRejectedEvenWhenAntialiasingIsOff(int quality)
    {
        foreach (var antialiasing in Enum.GetValues<FrameAntialiasing>())
        {
            var options = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false,
                Antialiasing: antialiasing, SmaaQuality: (SmaaQuality)quality);

            Assert.Throws<ArgumentException>(() => OriginalFrameMath.Validate(options));
        }
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(15)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(int.MaxValue)]
    public void UnsupportedSpatialSampleCountsAreRejected(int samples)
    {
        foreach (var antialiasing in Enum.GetValues<FrameAntialiasing>())
        {
            var options = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false,
                Antialiasing: antialiasing, SpatialSamples: samples);

            Assert.Throws<ArgumentException>(() => OriginalFrameMath.Validate(options));
        }
    }
}
