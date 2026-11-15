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

    [Fact]
    public void DeepPositionsInputSeekAndKeepTheUnseekedTimeline()
    {
        // 90 s into a stream whose first frame is at 0.5 s.
        var args = FfmpegVideoDecodePipe.Arguments("in.mp4", TimeSpan.FromSeconds(90).Ticks, TimeSpan.FromSeconds(.5).Ticks).ToList();
        int ss = args.IndexOf("-ss");
        Assert.True(ss >= 0 && ss < args.IndexOf("-i"), "the seek must be an input option");
        Assert.Equal("90.5000000", args[ss + 1]);
        Assert.Contains("-copyts", args);
        Assert.Contains(args, a => a.Contains("setpts=PTS-5000000,", StringComparison.Ordinal));
    }
}
