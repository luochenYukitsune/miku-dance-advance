using Xunit;

namespace Mmd.Modern.ViewOutput.FFmpeg.Tests;

public sealed class FfmpegVideoDecodeSeekTests
{
    [Fact]
    public void FromStartDecodesEveryFrameAndRebasesOnTheFirstTimestamp()
    {
        var args = FfmpegVideoDecodePipe.Arguments("in.mp4", 0, 0);
        Assert.DoesNotContain("-ss", args);
        Assert.DoesNotContain("-copyts", args);
        Assert.Contains(args, a => a.Contains("setpts=PTS-STARTPTS", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, "1/30", 333333L)]            // 1/30 s; ffprobe's start_time "0.033333" lost three ticks.
    [InlineData(126000, "1/90000", 14000000L)]   // MPEG-TS 1.4 s.
    [InlineData(0, "1/1000", 0L)]
    public void OriginComesExactlyFromStartPtsAndTimeBase(long pts, string timeBase, long expected)
    {
        using var json = System.Text.Json.JsonDocument.Parse(pts.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(expected, FfmpegSceneVideoDecoder.OriginTicks(json.RootElement, timeBase));
    }

    [Fact]
    public void MissingOrInvalidOriginDisablesSeeking()
    {
        using var number = System.Text.Json.JsonDocument.Parse("5");
        Assert.Null(FfmpegSceneVideoDecoder.OriginTicks(default, "1/30"));
        Assert.Null(FfmpegSceneVideoDecoder.OriginTicks(number.RootElement, null));
        Assert.Null(FfmpegSceneVideoDecoder.OriginTicks(number.RootElement, "0/1"));
    }

    [Fact]
    public void DeepPositionsInputSeekAndKeepTheUnseekedTimeline()
    {
        // 90 s into a stream whose first frame is at 0.5 s.
        var args = FfmpegVideoDecodePipe.Arguments("in.mp4", TimeSpan.FromSeconds(90).Ticks, TimeSpan.FromSeconds(.5).Ticks).ToList();
        int ss = args.IndexOf("-ss");
        Assert.True(ss >= 0 && ss < args.IndexOf("-i"), "the seek must be an input option");
        // -ss is an absolute source timestamp (stream origin + media time), so FFmpeg must not add the start time again.
        int absolute = args.IndexOf("-seek_timestamp");
        Assert.True(absolute >= 0 && absolute < args.IndexOf("-i") && args[absolute + 1] == "1");
        Assert.Equal("90.5000000", args[ss + 1]);
        Assert.Contains("-copyts", args);
        Assert.Contains(args, a => a.Contains("setpts=PTS-5000000,", StringComparison.Ordinal));
    }
}
