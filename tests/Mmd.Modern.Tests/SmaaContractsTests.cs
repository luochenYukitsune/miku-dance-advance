using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class SmaaContractsTests
{
    [Fact]
    public void PublicOptionDefaultsOffAndRejectsUnknownMode()
    {
        var property = typeof(OriginalFrameOptions).GetProperty("Antialiasing");
        Assert.NotNull(property);
        Assert.True(property.PropertyType.IsPublic && property.PropertyType.IsEnum);
        Assert.Equal(new[] { "Off", "Smaa1X", "Fxaa", "Taa", "Msaa2X", "Msaa4X", "Msaa8X" }, Enum.GetNames(property.PropertyType));
        Assert.Equal(0, (int)FrameAntialiasing.Off);
        Assert.Equal(1, (int)FrameAntialiasing.Smaa1X);
        var options = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false);
        Assert.Equal("Off", property.GetValue(options)!.ToString());
        property.SetValue(options, Enum.ToObject(property.PropertyType, 99));
        Assert.Throws<ArgumentException>(() => OriginalFrameMath.Validate(options));
    }
}
