using Mmd.Modern.Application;
using Xunit;

namespace Mmd.Modern.ViewOutput.Tests;

public sealed class ExportCoordinatorTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NtscPlanUsesExistingClockAndKeepsOnlyOneBorrowedFrame()
    {
        var fixture = new OutputFixture(); var sink = new TestSink();
        var coordinator = fixture.Coordinator();
        var result = await coordinator.RunAsync(new(new(2, 2, new(60000, 1001), 0, 30)), sink, cancellationToken: Token);
        Assert.Equal(60, result.CompletedFrames);
        Assert.Equal(118, result.SimulationTicks);
        Assert.Equal(new OfflineFrameTime(1001, 60000), sink.Samples[1].PresentationTime);
        Assert.Equal(new OfflineFrameTime(1, 2), sink.Samples[1].SampledSourceFrame);
        Assert.Equal(1, fixture.Renderer.MaximumOutstanding);
        Assert.Equal(60, fixture.Renderer.Releases);
        Assert.Equal(1, fixture.Renderer.Disposals);
        Assert.True(sink.Completed); Assert.False(sink.Aborted); Assert.True(sink.Disposed);
        Assert.Equal(ExportTaskState.Completed, coordinator.State);
        Assert.Throws<ObjectDisposedException>(() => fixture.Session!.Frame);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RunAsync(new(new(2, 2, new(30), 0, 0)), new TestSink(), cancellationToken: Token));
    }

    [Fact]
    public async Task SlowSinkPreventsTheNextCaptureUntilWriteFinishes()
    {
        var fixture = new OutputFixture(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new TestSink { Writing = async (_, ct) => { entered.TrySetResult(); await unblock.Task.WaitAsync(ct); } };
        var run = fixture.Coordinator().RunAsync(new(new(2, 2, new(1), 0, 30)), sink, cancellationToken: Token);
        await entered.Task.WaitAsync(Token);
        Assert.Equal(1, fixture.Renderer.Captures); Assert.Equal(0, fixture.Renderer.Releases);
        unblock.SetResult(); await run;
        Assert.Equal(2, fixture.Renderer.Releases);
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("write")]
    [InlineData("capture")]
    [InlineData("complete")]
    public async Task FailuresAbortAndReleaseEveryOwnedResource(string stage)
    {
        var fixture = new OutputFixture(); var expected = new IOException(stage);
        var sink = new TestSink { FailureStage = stage, Failure = expected };
        if (stage == "capture") fixture.Renderer.CaptureFailure = expected;
        var coordinator = fixture.Coordinator();
        var actual = await Assert.ThrowsAsync<IOException>(() => coordinator.RunAsync(new(new(2, 2, new(30), 0, 0)), sink, cancellationToken: Token));
        Assert.Same(expected, actual); Assert.True(sink.Aborted); Assert.True(sink.Disposed);
        Assert.Equal(1, fixture.Renderer.Disposals); Assert.Equal(0, fixture.Renderer.Outstanding);
        Assert.Throws<ObjectDisposedException>(() => fixture.Session!.Frame);
        Assert.Equal(ExportTaskState.Failed, coordinator.State);
    }

    [Fact]
    public async Task CancelledWriteAbortsWithUsableCleanupTokenAndDisposesLease()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var fixture = new OutputFixture();
        var sink = new TestSink { Writing = (_, ct) => { cancel.Cancel(); ct.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; } };
        var coordinator = fixture.Coordinator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RunAsync(new(new(2, 2, new(30), 0, 30)), sink, cancellationToken: cancel.Token));
        Assert.True(sink.Aborted); Assert.False(sink.AbortTokenWasCancelled);
        Assert.Equal(1, fixture.Renderer.Captures); Assert.Equal(1, fixture.Renderer.Releases);
        Assert.Equal(ExportTaskState.Cancelled, coordinator.State);
    }

    [Fact]
    public async Task RendererFactoryFailureStillDisposesSessionAndSink()
    {
        var fixture = new OutputFixture(); var sink = new TestSink();
        var coordinator = new ExportCoordinator(fixture.CreateSession,
            (_, _) => throw new IOException("create renderer"));
        await Assert.ThrowsAsync<IOException>(() => coordinator.RunAsync(new(new(2, 2, new(30), 0, 0)), sink, cancellationToken: Token));
        Assert.True(sink.Aborted); Assert.True(sink.Disposed);
        Assert.Throws<ObjectDisposedException>(() => fixture.Session!.Frame);
    }

    [Fact]
    public async Task InvalidCaptureDimensionsAreRejectedBeforeWritingAndLeaseIsReturned()
    {
        var fixture = new OutputFixture(); fixture.Renderer.WrongSize = true; var sink = new TestSink();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator().RunAsync(new(new(2, 2, new(30), 0, 0)), sink, cancellationToken: Token));
        Assert.Empty(sink.Samples); Assert.Equal(1, fixture.Renderer.Releases); Assert.True(sink.Aborted);
    }

    [Fact]
    public async Task CleanupFailurePreservesTheOriginalFailureAndAttemptsEveryCleanup()
    {
        var fixture = new OutputFixture(); fixture.Renderer.DisposeFailure = true;
        var original = new IOException("write"); var sink = new TestSink { FailureStage = "write", Failure = original, AbortFailure = true };
        var actual = await Assert.ThrowsAsync<IOException>(() => fixture.Coordinator().RunAsync(new(new(2, 2, new(30), 0, 0)), sink, cancellationToken: Token));
        Assert.Same(original, actual); Assert.True(sink.Disposed);
        Assert.Throws<ObjectDisposedException>(() => fixture.Session!.Frame);
        Assert.NotNull(actual.Data["ViewOutputCleanupErrors"]);
    }

    [Fact]
    public async Task CapabilityRejectionHappensBeforeSinkBeginOrCapture()
    {
        var fixture = new OutputFixture(); fixture.Renderer.Capabilities = new("test", 1, 1, true, false); var sink = new TestSink();
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Coordinator().RunAsync(new(new(2, 2, new(30), 0, 0)), sink, cancellationToken: Token));
        Assert.False(sink.Begun); Assert.Equal(0, fixture.Renderer.Captures); Assert.True(sink.Disposed);
    }

    [Fact]
    public async Task ProgressIncludesTheFinalStateAndCountsOnlyWrittenOutputFrames()
    {
        var fixture = new OutputFixture(); var progress = new ProgressSink();
        await fixture.Coordinator().RunAsync(new(new(2, 2, new(2), 15, 30, 15)), new TestSink(), progress, Token);
        Assert.Equal(new long[] { 0, 1, 2, 2 }, progress.Values.Select(value => value.CompletedFrames));
        Assert.Equal(ExportTaskState.Completed, progress.Values[^1].State);
        Assert.Equal(120, progress.Values[^1].SimulationTicks);
        Assert.Equal(new OfflineFrameTime(1, 2), progress.Values[^1].PresentationTime);
    }

    [Fact]
    public async Task PreCancellationDisposesSinkWithoutCreatingSceneOrRenderer()
    {
        var fixture = new OutputFixture(); var sink = new TestSink(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var coordinator = fixture.Coordinator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RunAsync(new(new(2, 2, new(30), 0, 0)), sink, cancellationToken: cancel.Token));
        Assert.Null(fixture.Session); Assert.Equal(0, fixture.Renderer.Captures);
        Assert.False(sink.Begun); Assert.True(sink.Aborted); Assert.True(sink.Disposed);
        Assert.Equal(ExportTaskState.Cancelled, coordinator.State);
    }

    private sealed class ProgressSink : IProgress<ExportTaskProgress>
    {
        public List<ExportTaskProgress> Values { get; } = [];
        public void Report(ExportTaskProgress value) => Values.Add(value);
    }
}
