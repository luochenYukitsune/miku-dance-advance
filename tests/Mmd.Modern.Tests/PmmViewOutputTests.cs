using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class PmmViewOutputTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OfflinePmmUsesExactGridAndAutomaticallySuppliesResolvedParentCamera()
    {
        using var playback = ResolvedOutputCameraFixture.Playback();
        var frames = new List<OfflineFrame>(); var progress = new ProgressLog();
        var result = await OfflineFrameRunner.RunPmmAsync(playback, new(96, 72, new(60000, 1001), 2, 10, 2),
            (frame, _) =>
            {
                Assert.Same(playback.Camera, frame.ResolvedCamera);
                Assert.Equal(playback.CameraPose, frame.Camera);
                Assert.Equal(0, frame.Camera!.Value.ParentModelIndex);
                Assert.NotEqual(frame.Camera.Value.CreateCamera().Eye, frame.ResolvedCamera!.Eye);
                Assert.Equal(playback.Tracks.LightAt(playback.CurrentFrame), frame.Light);
                Assert.Equal(playback.SelfShadowPose!.Value.Mode, frame.SelfShadow!.Value.Mode);
                Assert.Equal(playback.CurrentFrame, frame.Sample.SampledSourceFrame.ToDouble());
                frames.Add(frame); return ValueTask.CompletedTask;
            }, progress, Token);
        Assert.Equal(16, result.CompletedFrames); Assert.Equal(38, result.SimulationTicks);
        Assert.Equal(new OfflineFrameTime(1001, 60000), frames[1].Sample.PresentationTime);
        Assert.Equal(9.5, playback.CurrentFrame);
        Assert.Equal(8, progress.Values[1].SimulationTicks); // Two-frame pre-roll at four ticks/frame.
        Assert.NotEqual(frames[0].ResolvedCamera!.Eye, frames[^1].ResolvedCamera!.Eye);
        Assert.Equal(2, frames[0].Sample.SampledSourceFrame.ToDouble());
    }

    [Fact]
    public async Task OfflinePmmRejectsCaptureClockMutationAndCanRestartAfterCaptureFailure()
    {
        using var playback = ResolvedOutputCameraFixture.Playback();
        var plan = new OfflineFramePlan(96, 72, new(30), 0, 2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => OfflineFrameRunner.RunPmmAsync(playback, plan,
            (_, _) => { playback.Seek(5); return ValueTask.CompletedTask; }, cancellationToken: Token));
        int captures = 0;
        var result = await OfflineFrameRunner.RunPmmAsync(playback, plan,
            (_, _) => { captures++; return ValueTask.CompletedTask; }, cancellationToken: Token);
        Assert.Equal(3, captures); Assert.Equal(8, result.SimulationTicks); Assert.Equal(2, playback.CurrentFrame);
    }

    [Fact]
    public async Task PreviewAndExportOwnIndependentPmmClocksAndFactories()
    {
        var live = ResolvedOutputCameraFixture.Playback(); var renderer = new RecordingRenderer();
        var preview = await PreviewSession.CreatePmmAsync(new(96, 72), _ => ValueTask.FromResult(live),
            (_, _) => ValueTask.FromResult<IRealtimeViewRenderer>(renderer), Token);
        await preview.PresentAsync(.1, Token);
        Assert.Equal(3, live.CurrentFrame); Assert.Equal(3, renderer.Previews[0].SourceFrame);
        Assert.Same(live.Camera, renderer.Previews[0].ResolvedCamera);
        var independent = ResolvedOutputCameraFixture.Playback(); var exportRenderer = new RecordingRenderer(); var sink = new RecordingSink();
        var coordinator = ExportCoordinator.ForPmm(_ => ValueTask.FromResult(independent),
            (_, _) => ValueTask.FromResult<IViewRenderer>(exportRenderer));
        var result = await coordinator.RunAsync(new(new(96, 72, new(30), 0, 10)), sink, cancellationToken: Token);
        Assert.Equal(11, result.CompletedFrames); Assert.Equal(40, result.SimulationTicks);
        Assert.Equal(ExportTaskState.Completed, coordinator.State); Assert.True(sink.Completed); Assert.False(sink.Aborted);
        Assert.Equal(1, exportRenderer.Disposals); Assert.Throws<ObjectDisposedException>(() => independent.Seek(0));
        Assert.Equal(3, live.CurrentFrame); Assert.Equal(0, renderer.Disposals);
        Assert.All(exportRenderer.Captures, frame => Assert.NotNull(frame.ResolvedCamera));
        await preview.SetPausedAsync(true, Token); await preview.ResizeAsync(120, 90, Token); await preview.PresentAsync(.2, Token);
        Assert.Equal(3, live.CurrentFrame); Assert.Equal(120, renderer.Previews[^1].Width);
        await preview.SetPausedAsync(false, Token); await preview.PresentAsync(.1, Token); Assert.Equal(6, live.CurrentFrame);
        await preview.DisposeAsync(); await preview.DisposeAsync();
        Assert.Equal(1, renderer.Disposals); Assert.Throws<ObjectDisposedException>(() => live.Seek(0));
    }

    [Fact]
    public async Task PmmExportCancellationReleasesLeaseRendererPlaybackAndSink()
    {
        var playback = ResolvedOutputCameraFixture.Playback(); var renderer = new RecordingRenderer();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var sink = new RecordingSink { OnWrite = token => { cancel.Cancel(); token.ThrowIfCancellationRequested(); } };
        var coordinator = ExportCoordinator.ForPmm(_ => ValueTask.FromResult(playback),
            (_, _) => ValueTask.FromResult<IViewRenderer>(renderer));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RunAsync(new(new(96, 72, new(30), 0, 10)),
            sink, cancellationToken: cancel.Token));
        Assert.Equal(ExportTaskState.Cancelled, coordinator.State); Assert.Equal(1, renderer.Releases);
        Assert.Equal(1, renderer.Disposals); Assert.True(sink.Aborted); Assert.True(sink.Disposed); Assert.False(sink.AbortCancelled);
        Assert.Throws<ObjectDisposedException>(() => playback.Seek(0));
    }

    [Fact]
    public async Task FailingPmmFactoriesCleanUpAcquiredOwners()
    {
        var live = ResolvedOutputCameraFixture.Playback();
        await Assert.ThrowsAsync<IOException>(async () => await PreviewSession.CreatePmmAsync(new(96, 72),
            _ => ValueTask.FromResult(live), (_, _) => throw new IOException("renderer"), Token));
        Assert.Throws<ObjectDisposedException>(() => live.Seek(0));
        var playback = ResolvedOutputCameraFixture.Playback(); var sink = new RecordingSink();
        var coordinator = ExportCoordinator.ForPmm(_ => ValueTask.FromResult(playback), (_, _) => throw new IOException("renderer"));
        await Assert.ThrowsAsync<IOException>(() => coordinator.RunAsync(new(new(96, 72, new(30), 0, 0)), sink, cancellationToken: Token));
        Assert.True(sink.Aborted); Assert.True(sink.Disposed); Assert.Throws<ObjectDisposedException>(() => playback.Seek(0));
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task AutomaticPmmPacketsCaptureThroughPublicD3D12Output()
    {
        using var playback = ResolvedOutputCameraFixture.Playback();
        await using var renderer = new D3D12ViewRenderer(new(96, 72));
        using var reference = new D3D12FrameSession(); SceneCamera? first = null; int captures = 0;
        await OfflineFrameRunner.RunPmmAsync(playback, new(96, 72, new(3), 0, 10), async (frame, token) =>
        {
            using var pixels = await renderer.CaptureAsync(frame, token);
            var resolved = D3D12ViewScene.Resolve(frame.Scene, frame.Camera, frame.Light, frame.SelfShadow,
                frame.MorphChannels, new(), playback.Camera);
            Assert.Equal(reference.Capture(resolved, 96, 72).Rgba, pixels.Pixels.ToArray());
            if (first is null) first = frame.ResolvedCamera;
            else Assert.NotEqual(first.Eye, frame.ResolvedCamera!.Eye);
            captures++;
        }, cancellationToken: Token);
        Assert.Equal(2, captures);
    }

    private sealed class ProgressLog : IProgress<OfflineFrameProgress>
    {
        internal List<OfflineFrameProgress> Values { get; } = [];
        public void Report(OfflineFrameProgress value) => Values.Add(value);
    }
    private sealed class RecordingRenderer : IViewRenderer, IRealtimeViewRenderer
    {
        public ViewRendererCapabilities Capabilities => new("test-pmm", 4096, 4096, true, true);
        internal List<OfflineFrame> Captures { get; } = [];
        internal List<PreviewFrame> Previews { get; } = [];
        internal int Disposals, Releases;
        public ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Captures.Add(frame);
            return ValueTask.FromResult(new FrameBufferLease(frame.Width, frame.Height, frame.Width * 4,
                new byte[frame.Width * frame.Height * 4], () => Releases++));
        }
        public ValueTask PresentAsync(PreviewFrame frame, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Previews.Add(frame); return ValueTask.CompletedTask; }
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class RecordingSink : IFrameSink
    {
        internal bool Completed, Aborted, Disposed, AbortCancelled;
        internal Action<CancellationToken>? OnWrite;
        public ValueTask BeginAsync(ExportRequest request, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WriteAsync(OfflineFrameSample sample, FrameBufferLease frame, CancellationToken cancellationToken = default)
        { OnWrite?.Invoke(cancellationToken); return ValueTask.CompletedTask; }
        public ValueTask CompleteAsync(CancellationToken cancellationToken = default) { Completed = true; return ValueTask.CompletedTask; }
        public ValueTask AbortAsync(Exception? error, CancellationToken cancellationToken = default)
        { Aborted = true; AbortCancelled = cancellationToken.IsCancellationRequested; return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
