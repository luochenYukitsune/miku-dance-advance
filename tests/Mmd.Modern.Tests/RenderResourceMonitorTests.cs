using Mmd.Modern.App;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class RenderResourceMonitorTests
{
    private static readonly DateTimeOffset StartTime = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CpuUsesProcessTimeDeltaAndAllAvailableLogicalProcessors()
    {
        var clock = new SampleClock();
        var source = new FakeSource { CpuTime = TimeSpan.FromSeconds(10), LogicalProcessorCount = 8 };
        await using var monitor = Create(source, clock);
        var first = monitor.CollectSnapshot();
        Assert.Null(first.ProcessCpuPercent);
        Assert.NotNull(first.CpuUnavailableReason);
        clock.Advance(TimeSpan.FromSeconds(2));
        source.CpuTime += TimeSpan.FromSeconds(4);
        var busy = monitor.CollectSnapshot();
        Assert.Equal(25d, busy.ProcessCpuPercent);
        Assert.Null(busy.CpuUnavailableReason);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0d, monitor.CollectSnapshot().ProcessCpuPercent);
    }

    [Fact]
    public async Task ProcessReadFailuresRemainUnavailableAndCpuRecoveryWarmsUpAgain()
    {
        var clock = new SampleClock();
        var source = new FakeSource();
        await using var monitor = Create(source, clock);
        monitor.CollectSnapshot();
        clock.Advance(TimeSpan.FromSeconds(1));
        source.CpuFailure = new InvalidOperationException("CPU unavailable");
        source.MemoryFailure = new InvalidOperationException("Memory unavailable");
        var failed = monitor.CollectSnapshot();
        Assert.Null(failed.ProcessCpuPercent);
        Assert.Null(failed.ProcessWorkingSetBytes);
        Assert.NotNull(failed.CpuUnavailableReason);
        Assert.NotNull(failed.MemoryUnavailableReason);
        source.CpuFailure = null;
        source.MemoryFailure = null;
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(monitor.CollectSnapshot().ProcessCpuPercent);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0d, monitor.CollectSnapshot().ProcessCpuPercent);
    }

    [Fact]
    public async Task RendererIdentityAndOriginalVideoMemoryTimestampArePreserved()
    {
        var clock = new SampleClock();
        var source = new FakeSource { Gpu = new(72.5) };
        var memory = Memory(42, 100, 1000);
        await using var monitor = Create(source, clock, () => memory);
        clock.Advance(TimeSpan.FromSeconds(20));
        var snapshot = monitor.CollectSnapshot();
        Assert.Equal(42, source.LastAdapterLuid);
        Assert.Equal(42, snapshot.AdapterLuid);
        Assert.Equal("Selected GPU", snapshot.AdapterName);
        Assert.Equal(72.5, snapshot.GpuUtilizationPercent);
        Assert.Equal(100UL, snapshot.ProcessLocalVideoMemoryUsageBytes);
        Assert.Equal(1000UL, snapshot.LocalVideoMemoryBudgetBytes);
        Assert.Equal(StartTime, snapshot.VideoMemorySampledAtUtc);
        Assert.Equal(StartTime.AddSeconds(20), snapshot.SampledAtUtc);
        Assert.Null(snapshot.VideoMemoryUnavailableReason);
    }

    [Fact]
    public async Task MissingRendererDoesNotSelectADefaultGpuOrReportZero()
    {
        var source = new FakeSource();
        RenderGpuMemorySnapshot? memory = null;
        await using var monitor = Create(source, new SampleClock(), () => memory);
        var snapshot = monitor.CollectSnapshot();
        Assert.Equal(0, source.GpuReads);
        Assert.Null(snapshot.AdapterLuid);
        Assert.Null(snapshot.GpuUtilizationPercent);
        Assert.Null(snapshot.ProcessLocalVideoMemoryUsageBytes);
        Assert.Null(snapshot.LocalVideoMemoryBudgetBytes);
        Assert.NotNull(snapshot.GpuUnavailableReason);
        Assert.NotNull(snapshot.VideoMemoryUnavailableReason);
        memory = Memory(42, 123, 1000) with { AdapterLuid = null, UnavailableReason = "No actual device" };
        snapshot = monitor.CollectSnapshot();
        Assert.Equal(0, source.GpuReads);
        Assert.Null(snapshot.ProcessLocalVideoMemoryUsageBytes);
        Assert.Equal("No actual device", snapshot.VideoMemoryUnavailableReason);
    }

    [Fact]
    public async Task FailingRendererAndGpuCounterSourcesPreserveOtherResources()
    {
        var source = new FakeSource { WorkingSet = 1234 };
        await using (var missing = Create(source, new SampleClock(),
            () => throw new InvalidOperationException("device removed")))
        {
            var failed = missing.CollectSnapshot();
            Assert.Equal(1234UL, failed.ProcessWorkingSetBytes);
            Assert.Null(failed.ProcessLocalVideoMemoryUsageBytes);
            Assert.Contains("InvalidOperationException", failed.VideoMemoryUnavailableReason);
        }
        source = new FakeSource { GpuFailure = new InvalidOperationException("counter unavailable") };
        await using var monitor = Create(source, new SampleClock(), () => Memory(42, 0, 1000));
        var snapshot = monitor.CollectSnapshot();
        Assert.Equal(0UL, snapshot.ProcessLocalVideoMemoryUsageBytes);
        Assert.Null(snapshot.GpuUtilizationPercent);
        Assert.NotNull(snapshot.GpuUnavailableReason);
    }

    [Fact]
    public async Task ExportObservedPeaksSurviveUnavailableSamplesAndVramResetsOnAdapterChange()
    {
        var source = new FakeSource { WorkingSet = 100 };
        var memory = Memory(42, 200, 1000);
        await using var monitor = Create(source, new SampleClock(), () => memory);
        monitor.CollectSnapshot();
        source.WorkingSet = 80;
        memory = Memory(42, 150, 1000);
        var lower = monitor.CollectSnapshot();
        Assert.Equal(100UL, lower.ProcessPeakWorkingSetBytes);
        Assert.Equal(200UL, lower.PeakProcessLocalVideoMemoryUsageBytes);
        source.MemoryFailure = new InvalidOperationException();
        memory = memory with { ProcessLocalVideoMemoryUsageBytes = null, UnavailableReason = "Device unavailable" };
        var missing = monitor.CollectSnapshot();
        Assert.Equal(100UL, missing.ProcessPeakWorkingSetBytes);
        Assert.Equal(200UL, missing.PeakProcessLocalVideoMemoryUsageBytes);
        Assert.Null(missing.ProcessWorkingSetBytes);
        Assert.Null(missing.ProcessLocalVideoMemoryUsageBytes);
        memory = Memory(43, 20, 500);
        Assert.Equal(20UL, monitor.CollectSnapshot().PeakProcessLocalVideoMemoryUsageBytes);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task InvalidGpuPercentagesRemainUnavailable(double percent)
    {
        var source = new FakeSource { Gpu = new(percent) };
        await using var monitor = Create(source, new SampleClock(), () => Memory(42, 1, 1000));
        var snapshot = monitor.CollectSnapshot();
        Assert.Null(snapshot.GpuUtilizationPercent);
        Assert.NotNull(snapshot.GpuUnavailableReason);
    }

    [Fact]
    public void PdhAggregationMatchesExactPidAndLuidAndUsesBusiestEngineWithoutSumming()
    {
        long luid = unchecked((long)0x12345678ABCDEF01UL);
        RenderGpuEngineCounterSample[] counters =
        [
            new("pid_123_luid_0x12345678_0xabcdef01_phys_0_eng_0_engtype_3D", 30),
            new("pid_123_luid_0x12345678_0xabcdef01_phys_0_eng_1_engtype_Copy", 60, 1),
            new("pid_1234_luid_0x12345678_0xabcdef01_phys_0_eng_0_engtype_3D", 99),
            new("pid_123_luid_0x12345678_0xabcdef02_phys_0_eng_0_engtype_3D", 98),
            new("pid_123_luid_0x12345678_0xabcdef01_phys_0_eng_3_engtype_Compute", 97, 0xC0000BBA),
            new("pid_123_luid_0x12345678_0xabcdef01_phys_0_eng_4_engtype_Compute", double.NaN),
            new("pid_123_luid_0x12345678_0xabcdef01_phys_0_eng_5_engtype_Compute", 101)
        ];
        Assert.Equal(60d, PdhProcessGpuSampler.SelectBusiestEngine(counters, 123, luid).Percent);
        Assert.Null(PdhProcessGpuSampler.SelectBusiestEngine(counters, 12, luid).Percent);
        Assert.True(PdhProcessGpuSampler.MatchesInstance(
            "PID_123_LUID_0X12345678_0XABCDEF01_phys_0_eng_0_engtype_3D", 123, luid));
        Assert.False(PdhProcessGpuSampler.MatchesInstance(
            "pid_123_luid_0x12345678_0xabcdef010_phys_0_eng_0_engtype_3D", 123, luid));
        Assert.Null(PdhProcessGpuSampler.SelectBusiestEngine([], 123, luid).Percent);
    }

    [Fact]
    public void PdhAggregationAcceptsRealZeroAndSignedLuidHighBits()
    {
        long luid = unchecked((long)0xFFFFFFFF80000001UL);
        RenderGpuEngineCounterSample[] counters =
            [new("pid_99_luid_0xffffffff_0x80000001_phys_0_eng_0_engtype_3D", 0)];
        Assert.Equal(0d, PdhProcessGpuSampler.SelectBusiestEngine(counters, 99, luid).Percent);
    }

    [Fact]
    public async Task StartRunsOffCallerThreadAndStopDrainsThenDisposesExactlyOnce()
    {
        using var collected = new ManualResetEventSlim();
        var source = new FakeSource { BeforeCpuRead = () => collected.Set() };
        int callingThread = Environment.CurrentManagedThreadId;
        var delivered = new TaskCompletionSource<RenderResourceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new RenderResourceMonitor(() => null, source, TimeProvider.System, TimeSpan.FromHours(1));
        monitor.SnapshotAvailable += _ => throw new InvalidOperationException("Subscriber failure is isolated");
        monitor.SnapshotAvailable += snapshot => delivered.TrySetResult(snapshot);
        monitor.Start(TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => monitor.Start(TestContext.Current.CancellationToken));
        // Keep the caller occupied until the worker has entered. An async test caller can
        // otherwise yield its thread to Task.Run and legitimately reuse the same thread ID.
        Assert.True(collected.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var result = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Same(result, monitor.LatestSnapshot);
        Assert.NotEqual(callingThread, source.ReadThreadId);
        await monitor.StopAsync();
        Assert.True(monitor.Completion.IsCompletedSuccessfully);
        Assert.Equal(1, source.CpuReads);
        Assert.Equal(1, source.DisposeCount);
        await monitor.StopAsync();
        await monitor.DisposeAsync();
        Assert.Equal(1, source.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => monitor.Start(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AlreadyCanceledStartPublishesNothingAndClosesSource()
    {
        var source = new FakeSource();
        await using var monitor = Create(source, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        monitor.Start(cancellation.Token);
        await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Null(monitor.LatestSnapshot);
        Assert.Equal(0, source.CpuReads);
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task CancellationDuringSampleDrainsWithoutPublishingOrOverlapping()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var source = new FakeSource { BeforeCpuRead = () => { entered.TrySetResult(); release.Wait(); } };
        await using var monitor = Create(source, TimeProvider.System);
        monitor.Start(TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var stopping = monitor.StopAsync();
            Assert.False(stopping.IsCompleted);
            Assert.Equal(0, source.DisposeCount);
            release.Set();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Null(monitor.LatestSnapshot);
            Assert.Equal(1, source.CpuReads);
            Assert.Equal(1, source.DisposeCount);
        }
        finally { release.Set(); }
    }

    private static RenderResourceMonitor Create(FakeSource source, TimeProvider clock,
        Func<RenderGpuMemorySnapshot?>? memory = null)
        => new(memory ?? (() => null), source, clock, TimeSpan.FromSeconds(1));

    private static RenderGpuMemorySnapshot Memory(long luid, ulong usage, ulong budget) => new()
    {
        SampledAtUtc = StartTime, AdapterName = "Selected GPU", AdapterLuid = luid,
        ProcessLocalVideoMemoryUsageBytes = usage, LocalVideoMemoryBudgetBytes = budget
    };

    private sealed class SampleClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => StartTime.AddTicks(ticks);
        internal void Advance(TimeSpan elapsed) => ticks += elapsed.Ticks;
    }

    private sealed class FakeSource : IRenderResourceSampleSource
    {
        internal TimeSpan CpuTime;
        internal ulong WorkingSet;
        internal Exception? CpuFailure;
        internal Exception? MemoryFailure;
        internal Exception? GpuFailure;
        internal RenderGpuUtilizationSample Gpu = new(0);
        internal long? LastAdapterLuid;
        internal int CpuReads, GpuReads, DisposeCount, ReadThreadId;
        internal Action? BeforeCpuRead;
        public int LogicalProcessorCount { get; set; } = 8;
        public TimeSpan ReadProcessCpuTime()
        {
            Interlocked.Increment(ref CpuReads);
            ReadThreadId = Environment.CurrentManagedThreadId;
            BeforeCpuRead?.Invoke();
            if (CpuFailure is not null) throw CpuFailure;
            return CpuTime;
        }
        public ulong ReadProcessWorkingSetBytes()
        {
            if (MemoryFailure is not null) throw MemoryFailure;
            return WorkingSet;
        }
        public RenderGpuUtilizationSample ReadGpuUtilization(long adapterLuid)
        {
            ++GpuReads;
            LastAdapterLuid = adapterLuid;
            if (GpuFailure is not null) throw GpuFailure;
            return Gpu;
        }
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }
}
