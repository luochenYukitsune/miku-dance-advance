using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Mmd.Modern.Application.Tests;

[CollectionDefinition("Interaction diagnostics", DisableParallelization = true)]
public sealed class InteractionDiagnosticsCollection;

[Collection("Interaction diagnostics")]
public sealed class InteractionDiagnosticsTests : IDisposable
{
    public InteractionDiagnosticsTests() => InteractionDiagnostics.Stop();
    public void Dispose() => InteractionDiagnostics.Stop();

    [Fact]
    public void DisabledRecordingAllocatesNothingAndDoesNotCreateASession()
    {
        var previous = InteractionDiagnostics.Snapshot();
        InteractionDiagnostics.Record("warmup");
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; ++i) InteractionDiagnostics.Record("disabled", i);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        Assert.False(InteractionDiagnostics.IsEnabled);
        Assert.Equal(previous.Events.Length, InteractionDiagnostics.Snapshot().Events.Length);
    }

    [Fact]
    public void RingRetainsNewestEventsAndReportsOverwritesAndCorrelation()
    {
        InteractionDiagnostics.Start(new() { Capacity = 16, CaptureSlowWindows = false });
        for (int i = 0; i < 40; ++i) InteractionDiagnostics.Record("input", i, i - 1, i);
        var report = InteractionDiagnostics.Snapshot();
        Assert.Equal(16, report.Events.Length);
        Assert.Equal(24, report.OverwrittenEvents);
        Assert.Equal(24, report.Events[0].CorrelationId);
        Assert.Equal(39, report.Events[^1].CorrelationId);
        Assert.Equal(38, report.Events[^1].RelatedId);
        Assert.True(report.Events[^1].NativeThreadId > 0);
        Assert.True(report.Events[^1].Timestamp >= report.Events[0].Timestamp);
        Assert.Equal(Stopwatch.Frequency, report.TimestampFrequency);
    }

    [Fact]
    public void ScopeCapturesDurationAllocationAndExplicitSlowTrigger()
    {
        InteractionDiagnostics.Start(new() { Capacity = 32, SlowThreshold = TimeSpan.Zero, CaptureSlowWindows = false });
        using (InteractionDiagnostics.Measure("pose", 71, 70)) { GC.KeepAlive(new byte[1024]); }
        var entry = Assert.Single(InteractionDiagnostics.Snapshot().Events);
        Assert.Equal("pose", entry.Stage);
        Assert.Equal(71, entry.CorrelationId);
        Assert.True(entry.ElapsedTicks >= 0);
        Assert.True(entry.AllocatedBytes >= 1024);
        Assert.True(entry.Slow);
    }

    [Fact]
    public void StoppingRetainsPendingSlowWindowAndReportsMissingAfterWindow()
    {
        InteractionDiagnostics.Start(new() { Capacity = 16, SlowThreshold = TimeSpan.FromMilliseconds(50), AfterSlowWindow = TimeSpan.FromSeconds(10) });
        InteractionDiagnostics.Record("before", 1);
        InteractionDiagnostics.Record("slow", 2, elapsedTicks: Stopwatch.Frequency / 10);
        InteractionDiagnostics.Record("after", 3);
        InteractionDiagnostics.Stop();
        var report = InteractionDiagnostics.Snapshot();
        var window = Assert.Single(report.SlowWindows);
        Assert.Equal(["before", "slow", "after"], window.Events.Select(e => e.Stage).ToArray());
        Assert.True(window.AfterTruncated);
        Assert.False(report.Recording);
    }

    [Fact]
    public void EnabledDirectRecordingHasNoPerEventManagedAllocation()
    {
        InteractionDiagnostics.Start(new() { Capacity = 16, CaptureSlowWindows = false });
        InteractionDiagnostics.Record("warmup", 1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; ++i) InteractionDiagnostics.Record("input", i);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void ParallelWritersRemainBoundedAndAccountForEveryAttempt()
    {
        InteractionDiagnostics.Start(new() { Capacity = 32, CaptureSlowWindows = false });
        Parallel.For(0, 4_000, i => InteractionDiagnostics.Record("parallel", i));
        var report = InteractionDiagnostics.Snapshot();
        Assert.InRange(report.Events.Length, 1, 32);
        Assert.Equal(4_000, report.Events.Length + report.OverwrittenEvents + report.ContendedEvents);
        Assert.Equal(report.Events.Length, report.Events.Select(e => e.Sequence).Distinct().Count());
    }

    [Fact]
    public async Task ExportFreezesSessionAndKeepsMetadataWithoutRequiringRecordingToStop()
    {
        string path = Path.Combine(Path.GetTempPath(), "mda-diagnostics-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            InteractionDiagnostics.Start(new() { Capacity = 16, CaptureSlowWindows = false }, new Dictionary<string, string> { ["quality"] = "SMAA High" });
            InteractionDiagnostics.Record("present", 5);
            await InteractionDiagnostics.ExportAsync(path, TestContext.Current.CancellationToken);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal("SMAA High", json.RootElement.GetProperty("Metadata").GetProperty("quality").GetString());
            Assert.Equal("present", json.RootElement.GetProperty("Events")[0].GetProperty("Stage").GetString());
            Assert.True(InteractionDiagnostics.IsEnabled);
            InteractionDiagnostics.Record("next", 6);
            Assert.Equal(2, InteractionDiagnostics.Snapshot().Events.Length);
        }
        finally { File.Delete(path); }
    }
}
