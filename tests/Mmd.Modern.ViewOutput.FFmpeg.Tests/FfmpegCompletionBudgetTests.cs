using Mmd.Modern.Application;
using Xunit;

namespace Mmd.Modern.ViewOutput.FFmpeg.Tests;

public sealed class FfmpegCompletionBudgetTests
{
    [Fact]
    public void ShortClipsKeepTheConfiguredTimeoutAsTheirFloor()
    {
        var plan = new OfflineFramePlan(64, 64, new(30), 0, 1);
        var budget = FfmpegFrameSink.ValidationBudget(plan, TimeSpan.FromMinutes(2));
        Assert.InRange(budget, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ValidationBudgetGrowsWithDecodedPixelsSoLongExportsAreNotDeletedAfterEncoding()
    {
        // Five minutes of 4K at 60 fps: the old shared 120 s budget could not cover three full decode passes.
        var plan = new OfflineFramePlan(3840, 2160, new(60), 0, 5 * 60 * 30);
        var budget = FfmpegFrameSink.ValidationBudget(plan, TimeSpan.FromMinutes(2));
        Assert.True(budget > TimeSpan.FromHours(1), budget.ToString());
        Assert.True(budget <= TimeSpan.FromDays(1));
    }
}
