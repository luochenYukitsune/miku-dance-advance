using Mmd.Modern.App;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EditorPreviewFrameRateMonitorTests
{
    [Fact]
    public void OnlyPresentedFramesContributeAndSamplesAreLimitedToTwicePerSecond()
    {
        var monitor = Start();
        Assert.True(monitor.Snapshot.IsMeasuring);
        Assert.Null(monitor.Sample(TimeSpan.FromSeconds(1)).FramesPerSecond);

        // A deterministic 30 Hz presentation stream, independent of the authored timeline.
        for (int frame = 31; frame <= 60; frame++)
            monitor.RecordPresented(TimeSpan.FromSeconds(frame / 30d));
        var first = monitor.Sample(TimeSpan.FromSeconds(2));
        Assert.Equal(15d, first.FramesPerSecond);
        Assert.False(first.IsLowFrameRateWarningVisible);

        monitor.RecordPresented(TimeSpan.FromSeconds(2.1));
        Assert.Equal(first, monitor.Sample(TimeSpan.FromSeconds(2.1)));
        Assert.NotEqual(first.FramesPerSecond, monitor.Sample(TimeSpan.FromSeconds(2.5)).FramesPerSecond);
    }

    [Fact]
    public void HeartbeatsDetectACompleteStallAfterWarmupAndSustainedLowRate()
    {
        var monitor = Start();
        Assert.Null(monitor.Sample(TimeSpan.FromSeconds(1.5)).FramesPerSecond);
        Assert.Equal(0d, monitor.Sample(TimeSpan.FromSeconds(2)).FramesPerSecond);
        Assert.False(monitor.Sample(TimeSpan.FromSeconds(6.5)).IsLowFrameRateWarningVisible);
        var stalled = monitor.Sample(TimeSpan.FromSeconds(7));
        Assert.Equal(0d, stalled.FramesPerSecond);
        Assert.True(stalled.IsLowFrameRateWarningVisible);
    }

    [Fact]
    public void TemporaryLowRateDoesNotWarnAndRecoveryClearsTheOnlyReminderInAnEpisode()
    {
        var monitor = Start();
        Feed(monitor, 0, 5, 12);
        Assert.False(monitor.Snapshot.IsLowFrameRateWarningVisible);
        Feed(monitor, 5, 10, 30);
        Assert.False(monitor.Snapshot.IsLowFrameRateWarningVisible);
        Feed(monitor, 10, 18, 12);
        Assert.True(monitor.Snapshot.IsLowFrameRateWarningVisible);
        Feed(monitor, 18, 23, 30);
        Assert.False(monitor.Snapshot.IsLowFrameRateWarningVisible);
        Feed(monitor, 23, 33, 12);
        Assert.False(monitor.Snapshot.IsLowFrameRateWarningVisible);
    }

    [Fact]
    public void DismissSuppressesUntilPlaybackRestartsAndPauseClearsImmediately()
    {
        var monitor = Start();
        Feed(monitor, 0, 8, 12);
        Assert.True(monitor.Snapshot.IsLowFrameRateWarningVisible);
        monitor.DismissWarning();
        Assert.False(monitor.Snapshot.IsLowFrameRateWarningVisible);
        Feed(monitor, 8, 18, 12);
        Assert.False(monitor.Snapshot.IsLowFrameRateWarningVisible);

        monitor.SetPlaybackActive(false, TimeSpan.FromSeconds(18));
        Assert.False(monitor.Snapshot.IsMeasuring);
        Assert.Null(monitor.Snapshot.FramesPerSecond);
        monitor.SetPlaybackActive(true, TimeSpan.FromSeconds(18));
        Feed(monitor, 18, 26, 12);
        Assert.True(monitor.Snapshot.IsLowFrameRateWarningVisible);
        monitor.SetPlaybackActive(false, TimeSpan.FromSeconds(26));
        Assert.False(monitor.Snapshot.IsLowFrameRateWarningVisible);
    }

    [Fact]
    public void SuppressionDropsOldFramesAndWarmsUpWithoutRearmingTheSameEpisode()
    {
        var monitor = Start();
        Feed(monitor, 0, 8, 12);
        Assert.True(monitor.Snapshot.IsLowFrameRateWarningVisible);
        monitor.SetMeasurementEnabled(false, TimeSpan.FromSeconds(8));
        monitor.RecordPresented(TimeSpan.FromSeconds(9));
        Assert.Equal(default, monitor.Sample(TimeSpan.FromSeconds(100)));
        monitor.SetMeasurementEnabled(true, TimeSpan.FromSeconds(100));
        Assert.Null(monitor.Sample(TimeSpan.FromSeconds(101.5)).FramesPerSecond);
        Assert.Equal(0d, monitor.Sample(TimeSpan.FromSeconds(102)).FramesPerSecond);
        Feed(monitor, 102, 112, 12);
        Assert.False(monitor.Snapshot.IsLowFrameRateWarningVisible);
    }

    [Fact]
    public void UnmeasuredAndRepeatedSamplesDoNotInventFramesOrAdvanceWarmup()
    {
        var monitor = new EditorPreviewFrameRateMonitor();
        monitor.RecordPresented(TimeSpan.Zero);
        Assert.Equal(default, monitor.Sample(TimeSpan.FromDays(1)));
        monitor.SetMeasurementEnabled(true, TimeSpan.FromDays(1));
        Assert.Equal(default, monitor.Sample(TimeSpan.FromDays(1)));
        monitor.SetPlaybackActive(true, TimeSpan.FromDays(1));
        for (int i = 0; i < 10; i++)
            Assert.Null(monitor.Sample(TimeSpan.FromDays(1)).FramesPerSecond);
        Assert.Equal(0d, monitor.Sample(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(2)).FramesPerSecond);
    }

    [Fact]
    public void HighPresentationRatesStayAccurateWithoutAFrameHistoryCapacityLimit()
    {
        var monitor = Start();
        Feed(monitor, 0, 4, 600);
        Assert.InRange(monitor.Snapshot.FramesPerSecond!.Value, 599, 601);
        Assert.False(monitor.Snapshot.IsLowFrameRateWarningVisible);
    }

    private static EditorPreviewFrameRateMonitor Start()
    {
        var monitor = new EditorPreviewFrameRateMonitor();
        monitor.SetPlaybackActive(true, TimeSpan.Zero);
        monitor.SetMeasurementEnabled(true, TimeSpan.Zero);
        return monitor;
    }

    private static void Feed(EditorPreviewFrameRateMonitor monitor, int fromSecond, int toSecond, int framesPerSecond)
    {
        int framesPerHalfSecond = framesPerSecond / 2;
        for (int halfSecond = fromSecond * 2; halfSecond < toSecond * 2; halfSecond++)
        {
            for (int frame = 1; frame <= framesPerHalfSecond; frame++)
                monitor.RecordPresented(TimeSpan.FromSeconds(halfSecond * 0.5 + frame / (double)framesPerSecond));
            monitor.Sample(TimeSpan.FromSeconds((halfSecond + 1) * 0.5));
        }
    }
}
