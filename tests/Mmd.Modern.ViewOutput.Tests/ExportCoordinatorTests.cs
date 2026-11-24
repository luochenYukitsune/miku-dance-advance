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
        Assert.True(coordinator.OutputPublished);
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
        Assert.False(coordinator.OutputPublished);
        Assert.Equal(false, actual.Data[ExportCoordinator.OutputPublishedDataKey]);
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
    public async Task FinalProgressFailurePreservesThePublishedOutputAndOriginalError()
    {
        var fixture = new OutputFixture();
        var coordinator = fixture.Coordinator();
        var sink = new TestSink();
        var original = new IOException("final notification failed");
        var progress = new ProgressSink(value =>
        {
            if (value.State == ExportTaskState.Completed) throw original;
        });

        var error = await Assert.ThrowsAsync<IOException>(() =>
            coordinator.RunAsync(new(new(2, 2, new(30), 0, 0)), sink, progress, Token));

        Assert.Same(original, error);
        Assert.True(coordinator.OutputPublished);
        Assert.Equal(true, error.Data[ExportCoordinator.OutputPublishedDataKey]);
        Assert.Equal("progress.final", error.Data["MDA.ViewOutput.Phase"]);
        Assert.Equal(ExportTaskState.Failed, coordinator.State);
        Assert.True(sink.Completed);
        Assert.False(sink.Aborted);
        Assert.True(sink.Disposed);
        Assert.Equal(1, fixture.Renderer.Disposals);
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

    [Fact]
    public async Task OverlappedExportMatchesSerialSamplesOrderAndKeepsOneOutstandingLease()
    {
        async Task<(ExportResult Result, List<OfflineFrameSample> Samples, int MaxOutstanding, int Captures, int Releases, long[] ProgressFrames)> Run(bool overlap)
        {
            var fixture = new OutputFixture();
            var coordinator = fixture.Coordinator();
            coordinator.OverlapCapture = overlap;
            var sink = new TestSink();
            var progress = new ProgressSink();
            var result = await coordinator.RunAsync(new(new(4, 4, new(60), 0, 6)), sink, progress, Token);
            return (result, sink.Samples, fixture.Renderer.MaximumOutstanding, fixture.Renderer.Captures, fixture.Renderer.Releases,
                progress.Values.Select(v => v.CompletedFrames).ToArray());
        }
        var serial = await Run(false);
        var overlapped = await Run(true);
        Assert.Equal(serial.Result, overlapped.Result);
        Assert.Equal(serial.Samples, overlapped.Samples);
        Assert.Equal(serial.ProgressFrames, overlapped.ProgressFrames);
        Assert.Equal(1, overlapped.MaxOutstanding);
        Assert.Equal(overlapped.Captures, overlapped.Releases);
    }

    [Fact]
    public async Task OverlappedExportDrainsInFlightCaptureOnCancellationAndPreservesFirstFailure()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var fixture = new OutputFixture();
        var enteredSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        var sink = new TestSink
        {
            Writing = async (_, ct) =>
            {
                Interlocked.Increment(ref active);
                try
                {
                    if (fixture.Renderer.Captures == 2)
                    {
                        enteredSecond.TrySetResult();
                        cancel.Cancel();
                        await Task.Delay(30, CancellationToken.None);
                        ct.ThrowIfCancellationRequested();
                    }
                }
                finally { Interlocked.Decrement(ref active); }
            }
        };
        var coordinator = fixture.Coordinator();
        Assert.True(coordinator.OverlapCapture);
        var run = coordinator.RunAsync(new(new(4, 4, new(30), 0, 10)), sink, cancellationToken: cancel.Token);
        await enteredSecond.Task.WaitAsync(Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(0, Volatile.Read(ref active));
        Assert.Equal(0, fixture.Renderer.Outstanding);
        Assert.Equal(fixture.Renderer.Captures, fixture.Renderer.Releases);
        Assert.Equal(1, fixture.Renderer.Disposals);
        Assert.True(sink.Aborted);
        Assert.True(sink.Disposed);
        Assert.Equal(ExportTaskState.Cancelled, coordinator.State);
    }

    [Fact]
    public async Task OverlappedExportWriteFailurePreservesWritePhaseAndCleansUp()
    {
        var fixture = new OutputFixture();
        var expected = new IOException("overlapped write failure");
        var sink = new TestSink
        {
            Writing = (_, _) => fixture.Renderer.Captures == 2 ? throw expected : ValueTask.CompletedTask
        };
        var coordinator = fixture.Coordinator();
        Assert.True(coordinator.OverlapCapture);
        var actual = await Assert.ThrowsAsync<IOException>(() => coordinator.RunAsync(new(new(4, 4, new(30), 0, 10)), sink, cancellationToken: Token));
        Assert.Same(expected, actual);
        Assert.Equal("sink.write", actual.Data["MDA.ViewOutput.Phase"]);
        Assert.Equal(0, fixture.Renderer.Outstanding);
        Assert.Equal(fixture.Renderer.Captures, fixture.Renderer.Releases);
        Assert.Equal(1, fixture.Renderer.Disposals);
        Assert.True(sink.Aborted);
        Assert.True(sink.Disposed);
        Assert.Equal(ExportTaskState.Failed, coordinator.State);
    }

    private sealed class ProgressSink(Action<ExportTaskProgress>? report = null) : IProgress<ExportTaskProgress>
    {
        public List<ExportTaskProgress> Values { get; } = [];
        public void Report(ExportTaskProgress value)
        {
            Values.Add(value);
            report?.Invoke(value);
        }
    }
}
