namespace Mmd.Modern.App;

/// <summary>A UI-neutral view of completed viewport presentations, not the authored timeline rate.</summary>
internal readonly record struct EditorPreviewFrameRateSnapshot(
    bool IsMeasuring,
    double? FramesPerSecond,
    bool IsLowFrameRateWarningVisible);

/// <summary>
/// Measures successful viewport presentation callbacks on its owning thread. Callers supply a
/// nondecreasing elapsed time from a monotonic clock, such as Stopwatch.GetElapsedTime. Callback
/// time describes the host observing Present return; it does not measure physical display scanout.
/// Sample must also be called when no frame completes, so a stalled viewport can reach zero FPS.
/// </summary>
internal sealed class EditorPreviewFrameRateMonitor
{
    private const long BucketTicks = TimeSpan.TicksPerMillisecond * 50;
    private const long WindowTicks = TimeSpan.TicksPerSecond * 2;
    private const long SampleIntervalTicks = TimeSpan.TicksPerMillisecond * 500;
    private const long LowRateDurationTicks = TimeSpan.TicksPerSecond * 5;
    private const long RecoveryDurationTicks = TimeSpan.TicksPerSecond * 2;
    private const int BucketCount = (int)(WindowTicks / BucketTicks) + 1;
    private const double LowFramesPerSecond = 24;
    private const double RecoveredFramesPerSecond = 27;

    // Fixed time buckets retain arbitrary presentation rates without per-frame allocation or an
    // unbounded timestamp queue. Only the oldest partial bucket needs an approximate contribution.
    private readonly long[] bucketNumbers = new long[BucketCount];
    private readonly int[] bucketFrames = new int[BucketCount];
    private bool playbackActive;
    private bool measurementEnabled;
    private bool reminderShown;
    private long lastObservedTicks;
    private long measurementStartedTicks;
    private long? lastSampleTicks;
    private long? lowRateStartedTicks;
    private long? recoveryStartedTicks;

    public EditorPreviewFrameRateSnapshot Snapshot { get; private set; }

    /// <summary>A playback stop ends the reminder episode; a subsequent start may remind again.</summary>
    public void SetPlaybackActive(bool active, TimeSpan now)
    {
        ObserveTime(now);
        if (playbackActive == active) return;
        playbackActive = active;
        reminderShown = false;
        ResetMeasurement(now.Ticks);
    }

    /// <summary>
    /// Suspend for loading, busy/export work, errors, or an unavailable viewport. A resume starts
    /// a fresh warmup, while preserving the one-reminder limit of the current playback episode.
    /// </summary>
    public void SetMeasurementEnabled(bool enabled, TimeSpan now)
    {
        ObserveTime(now);
        if (measurementEnabled == enabled) return;
        measurementEnabled = enabled;
        ResetMeasurement(now.Ticks);
    }

    /// <summary>Record exactly one successful, deduplicated viewport presentation callback.</summary>
    public void RecordPresented(TimeSpan now)
    {
        ObserveTime(now);
        if (!IsMeasuring) return;
        long bucket = now.Ticks / BucketTicks;
        int index = (int)(bucket % BucketCount);
        if (bucketNumbers[index] != bucket)
        {
            bucketNumbers[index] = bucket;
            bucketFrames[index] = 0;
        }
        if (bucketFrames[index] < int.MaxValue) bucketFrames[index]++;
    }

    /// <summary>Refresh at most twice per second, including a zero-FPS result during a stall.</summary>
    public EditorPreviewFrameRateSnapshot Sample(TimeSpan now)
    {
        ObserveTime(now);
        if (!IsMeasuring) return Snapshot;
        if (lastSampleTicks is { } sampled && now.Ticks - sampled < SampleIntervalTicks) return Snapshot;
        lastSampleTicks = now.Ticks;

        // A full window avoids interpreting startup, a seek, or renderer recreation as low FPS.
        if (now.Ticks - measurementStartedTicks < WindowTicks) return Snapshot;
        double frames = 0;
        for (int i = 0; i < BucketCount; i++)
        {
            long ageTicks = now.Ticks - bucketNumbers[i] * BucketTicks;
            if (ageTicks < 0 || ageTicks >= WindowTicks + BucketTicks) continue;
            double contribution = Math.Min(1d, (WindowTicks + BucketTicks - ageTicks) / (double)BucketTicks);
            frames += bucketFrames[i] * contribution;
        }
        double framesPerSecond = frames / 2d;
        bool warningVisible = Snapshot.IsLowFrameRateWarningVisible;
        if (warningVisible)
        {
            if (framesPerSecond > RecoveredFramesPerSecond)
            {
                recoveryStartedTicks ??= now.Ticks;
                if (now.Ticks - recoveryStartedTicks.Value >= RecoveryDurationTicks) warningVisible = false;
            }
            else recoveryStartedTicks = null;
        }
        else if (!reminderShown)
        {
            if (framesPerSecond < LowFramesPerSecond)
            {
                lowRateStartedTicks ??= now.Ticks;
                if (now.Ticks - lowRateStartedTicks.Value >= LowRateDurationTicks)
                {
                    warningVisible = true;
                    reminderShown = true;
                }
            }
            else lowRateStartedTicks = null;
        }

        Snapshot = new(true, framesPerSecond, warningVisible);
        return Snapshot;
    }

    /// <summary>Hide and suppress the reminder until playback stops and starts again.</summary>
    public void DismissWarning()
    {
        reminderShown = true;
        Snapshot = Snapshot with { IsLowFrameRateWarningVisible = false };
    }

    private bool IsMeasuring => playbackActive && measurementEnabled;

    private void ResetMeasurement(long nowTicks)
    {
        Array.Clear(bucketNumbers);
        Array.Clear(bucketFrames);
        measurementStartedTicks = nowTicks;
        lastSampleTicks = null;
        lowRateStartedTicks = null;
        recoveryStartedTicks = null;
        Snapshot = IsMeasuring ? new(true, null, false) : default;
    }

    private void ObserveTime(TimeSpan now)
    {
        if (now.Ticks < lastObservedTicks)
            throw new ArgumentOutOfRangeException(nameof(now), "Presentation measurements require nondecreasing elapsed time.");
        lastObservedTicks = now.Ticks;
    }
}
