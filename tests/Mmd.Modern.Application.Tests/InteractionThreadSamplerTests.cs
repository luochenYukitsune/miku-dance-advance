using Xunit;

namespace Mmd.Modern.Application.Tests;

[Collection("Interaction diagnostics")]
public sealed class InteractionThreadSamplerTests : IDisposable
{
    public InteractionThreadSamplerTests() => InteractionDiagnostics.Stop();
    public void Dispose() => InteractionDiagnostics.Stop();

    [Fact]
    public void RecordingEpochIsZeroWhenStoppedAndNeverReusesAnEarlierSession()
    {
        Assert.Equal(0, InteractionDiagnostics.SessionId);
        InteractionDiagnostics.Start(new() { CaptureSlowWindows = false });
        long first = InteractionDiagnostics.SessionId;
        Assert.True(first > 0);
        InteractionDiagnostics.Stop(); Assert.Equal(0, InteractionDiagnostics.SessionId);
        InteractionDiagnostics.Start(new() { CaptureSlowWindows = false });
        Assert.True(InteractionDiagnostics.SessionId > first);
        Assert.False(InteractionDiagnostics.Snapshot().Options.SampleThreads);
        Assert.Equal(InteractionDiagnostics.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            InteractionDiagnostics.Snapshot().Metadata["diagnosticsSessionId"]);
    }

    [Fact]
    public async Task WorkerBoundsEachSamplingBatchAndCompletionAllowsAQuiescentStop()
    {
        if (!OperatingSystem.IsWindows()) return;
        var observed = new TaskCompletionSource<InteractionThreadSamplingBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        int callbackCount = 0;
        var sampler = InteractionThreadSampler.Start(_ => Interlocked.Increment(ref callbackCount),
            value => observed.TrySetResult(value), new() { MaximumThreadsPerSample = 1, MaximumCachedThreads = 1,
                WorkBudget = TimeSpan.FromMilliseconds(25) });
        try
        {
            var batch = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(batch.TotalThreads > 0);
            Assert.InRange(batch.InspectedThreads, 0, 1);
            Assert.InRange(batch.PublishedThreads, 0, batch.InspectedThreads);
            Assert.True(batch.ElapsedTicks >= 0); Assert.True(batch.AllocatedBytes >= 0);
        }
        finally
        {
            sampler.Dispose(); sampler.Dispose();
            await sampler.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        int stopped = Volatile.Read(ref callbackCount);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(stopped, Volatile.Read(ref callbackCount));
    }

    [Fact]
    public async Task EnabledSessionLabelsPeriodicSamplesWithoutClaimingAnEtwSchedulingTrace()
    {
        if (!OperatingSystem.IsWindows()) return;
        InteractionDiagnostics.Start(new() { Capacity = 4096, CaptureSlowWindows = false, SampleThreads = true });
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        InteractionDiagnosticsReport report;
        do
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            report = InteractionDiagnostics.Snapshot();
        } while (!report.Events.Any(entry => entry.Stage == "thread.sample-batch") && DateTime.UtcNow < deadline);
        Assert.Contains("not ETW", report.Metadata["threadSampling"]);
        Assert.Contains(report.Events, entry => entry.Stage == "thread.sample-batch");
        Assert.All(report.Events.Where(entry => entry.Stage == "thread.sample"), entry =>
        {
            Assert.True(entry.RelatedId > 0);
            Assert.Contains("periodic-nonETW", entry.Detail!);
            Assert.True(entry.ThreadCpu100Nanoseconds >= -1);
        });
    }

    [Fact]
    public async Task StoppingARecordingCannotSendOldSamplerCallbacksIntoTheNextSession()
    {
        if (!OperatingSystem.IsWindows()) return;
        InteractionDiagnostics.Start(new() { CaptureSlowWindows = false, SampleThreads = true });
        await Task.Delay(150, TestContext.Current.CancellationToken);
        InteractionDiagnostics.Stop();
        long stoppedCount = InteractionDiagnostics.Snapshot().Events.Length;
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(stoppedCount, InteractionDiagnostics.Snapshot().Events.Length);
        InteractionDiagnostics.Start(new() { CaptureSlowWindows = false });
        InteractionDiagnostics.Record("new-session");
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal("new-session", Assert.Single(InteractionDiagnostics.Snapshot().Events).Stage);
    }
}
