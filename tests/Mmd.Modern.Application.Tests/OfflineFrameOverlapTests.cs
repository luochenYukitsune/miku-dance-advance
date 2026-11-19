using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class OfflineFrameOverlapTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class ProgressSink : IProgress<OfflineFrameProgress>
    {
        public List<OfflineFrameProgress> Items { get; } = [];
        public void Report(OfflineFrameProgress value) => Items.Add(value);
    }

    [Fact]
    public async Task OverlapCapturesTheSerialFramesInOrderWhileTheNextFrameIsSimulated()
    {
        var plan = new OfflineFramePlan(10, 10, new(7), 10, 30, 5);
        async Task<(OfflineFrameRunResult Result, List<(OfflineFrameSample Sample, float[] Y)> Frames, List<OfflineFrameProgress> Progress, int Steps, bool Overlapped)> Run(bool overlap)
        {
            var backend = new SceneAnimationSessionTests.FallingBackend(); using var session = OfflineFrameTests.Session(backend);
            var frames = new List<(OfflineFrameSample, float[])>(); var progress = new ProgressSink(); bool overlapped = true;
            int active = 0;
            var result = await OfflineFrameRunner.RunAsync(session, plan, (frame, token) =>
            {
                Assert.Equal(1, Interlocked.Increment(ref active));
                frames.Add((frame.Sample, frame.Scene.Surface.Snapshot.Vertices.ToArray().Select(vertex => vertex.Position.Y).ToArray()));
                // Only an overlapped run lets the owner reach the next sample while this capture is still running.
                if (overlap && frame.Sample.Index + 1 < plan.FrameCount)
                {
                    double next = plan.GetSample(frame.Sample.Index + 1).SampledSourceFrame.ToDouble();
                    overlapped &= SpinWait.SpinUntil(() => session.CurrentFrame >= next, TimeSpan.FromSeconds(30));
                }
                Interlocked.Decrement(ref active);
                return ValueTask.CompletedTask;
            }, progress, Token, overlap);
            return (result, frames, progress.Items, backend.Steps, overlapped);
        }
        var serial = await Run(false); var overlapped = await Run(true);
        Assert.True(overlapped.Overlapped);
        Assert.Equal(serial.Result, overlapped.Result); Assert.Equal(serial.Steps, overlapped.Steps);
        Assert.Equal(serial.Progress, overlapped.Progress);
        Assert.Equal(serial.Frames.Select(frame => frame.Sample), overlapped.Frames.Select(frame => frame.Sample));
        Assert.Equal(serial.Frames.Select(frame => frame.Y), overlapped.Frames.Select(frame => frame.Y));
        Assert.Equal(plan.FrameCount, overlapped.Frames.Select(frame => frame.Y[0]).Distinct().Count());
    }

    [Fact]
    public async Task OverlappedCaptureFailureKeepsItsOwnFrameContextAndPreventsTheNextCapture()
    {
        var plan = new OfflineFramePlan(10, 10, new(7), 10, 30, 5);
        async Task<(IOException Error, List<long> Captures)> Run(bool overlap)
        {
            var backend = new SceneAnimationSessionTests.FallingBackend(); using var session = OfflineFrameTests.Session(backend);
            var captures = new List<long>();
            var error = await Assert.ThrowsAsync<IOException>(() => OfflineFrameRunner.RunAsync(session, plan, (frame, _) =>
            {
                captures.Add(frame.Sample.Index);
                return frame.Sample.Index == 1 ? throw new IOException("fixture capture failed") : ValueTask.CompletedTask;
            }, cancellationToken: Token, overlapCapture: overlap));
            return (error, captures);
        }
        var serial = await Run(false); var overlapped = await Run(true);
        Assert.Equal(new long[] { 0, 1 }, overlapped.Captures);
        var expected = Assert.IsType<OfflineFrameFailureContext>(serial.Error.Data[OfflineFrameFailureContext.DataKey]);
        Assert.Equal("capture", expected.Phase); Assert.Equal(1, expected.Sample!.Index);
        Assert.Equal(expected, overlapped.Error.Data[OfflineFrameFailureContext.DataKey]);
    }

    [Fact]
    public async Task SimulationFailureDrainsTheInFlightCaptureAndKeepsTheSerialContext()
    {
        var plan = new OfflineFramePlan(4, 3, new(60), 0, 1, includeEndFrame: false);
        var contexts = new List<OfflineFrameFailureContext>(); bool drained = false, stopped = false;
        foreach (bool overlap in new[] { false, true })
        {
            var backend = new SceneAnimationSessionTests.FallingBackend(); using var session = OfflineFrameTests.Session(backend);
            backend.FailStep = true; // Frame 0 needs no tick; frame 1's first tick fails while frame 0 may still be capturing.
            var error = await Assert.ThrowsAsync<SceneActorAnimationException>(() => OfflineFrameRunner.RunAsync(session, plan, (_, token) =>
            {
                if (!overlap) return ValueTask.CompletedTask;
                // The owner's failure cancels the in-flight capture; finishing it still counts this frame as written.
                stopped = SpinWait.SpinUntil(() => token.IsCancellationRequested, TimeSpan.FromSeconds(30));
                Thread.Sleep(50); drained = true;
                return ValueTask.CompletedTask;
            }, cancellationToken: Token, overlapCapture: overlap));
            Assert.Equal("fixture step failed", error.InnerException!.Message);
            contexts.Add(Assert.IsType<OfflineFrameFailureContext>(error.Data[OfflineFrameFailureContext.DataKey]));
        }
        Assert.True(stopped); Assert.True(drained);
        Assert.Equal("simulation", contexts[0].Phase); Assert.Equal(1, contexts[0].CompletedFrames); Assert.Equal(1, contexts[0].AttemptingSimulationTick);
        Assert.Equal(contexts[0], contexts[1]);
    }

    [Fact]
    public async Task CancellationDuringAnOverlappedRunWaitsForTheCaptureAndStartsNoOther()
    {
        var backend = new SceneAnimationSessionTests.FallingBackend(); using var session = OfflineFrameTests.Session(backend);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token); var progress = new ProgressSink();
        int captures = 0, active = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OfflineFrameRunner.RunAsync(session,
            new(10, 10, new(30), 0, 30), async (frame, token) =>
            {
                Interlocked.Increment(ref active); captures++;
                if (frame.Sample.Index == 2) { cancel.Cancel(); await Task.Delay(50, CancellationToken.None); }
                Interlocked.Decrement(ref active);
            }, progress, cancel.Token, overlapCapture: true));
        Assert.Equal(3, captures); Assert.Equal(0, Volatile.Read(ref active));
        Assert.Equal(3, progress.Items[^1].CompletedFrames);
        Assert.Equal(0, backend.DisposeCount); // A cancellation may land inside the concurrent tick; the caller still owns the session.
    }
}
