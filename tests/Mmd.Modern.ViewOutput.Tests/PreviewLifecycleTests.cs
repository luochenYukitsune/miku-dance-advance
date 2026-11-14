using Mmd.Modern.Application;
using Xunit;

namespace Mmd.Modern.ViewOutput.Tests;

public sealed class PreviewLifecycleTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PreviewCarriesElapsedTimeAndExplicitSeekAndCameraCutKeys()
    {
        var fixture = new OutputFixture(); var renderer = new PreviewRenderer();
        await using var preview = await PreviewSession.CreateAsync(new(20, 10), fixture.CreateSession,
            (_, _) => ValueTask.FromResult<IRealtimeViewRenderer>(renderer), Token);
        await preview.PresentAsync(.025, Token);
        Assert.Equal(25f, renderer.Frames[0].TemporalState.FrameTimeMilliseconds);
        await preview.InvalidateTemporalHistoryAsync(cancellationToken: Token);
        await preview.PresentAsync(0, Token);
        Assert.Null(renderer.Frames[1].TemporalState.FrameTimeMilliseconds);
        Assert.Equal(1, renderer.Frames[1].TemporalState.HistoryGeneration);
        Assert.Equal(0, renderer.Frames[1].TemporalState.CameraCutKey);
        await preview.InvalidateTemporalHistoryAsync(cameraCut: true, cancellationToken: Token);
        await preview.PresentAsync(.01, Token);
        Assert.Equal(1, renderer.Frames[2].TemporalState.HistoryGeneration);
        Assert.Equal(1, renderer.Frames[2].TemporalState.CameraCutKey);
        Assert.Equal(0, renderer.Frames[0].TemporalState.HistoryGeneration);
    }

    [Fact]
    public async Task PauseAndResizeKeepPresentingWithoutCapturingOrAdvancingThePausedClock()
    {
        var fixture = new OutputFixture(); var renderer = new PreviewRenderer();
        var preview = await PreviewSession.CreateAsync(new(20, 10), fixture.CreateSession,
            (_, _) => ValueTask.FromResult<IRealtimeViewRenderer>(renderer), Token);
        await preview.PresentAsync(.1, Token); Assert.Equal(3, fixture.Session!.CurrentFrame, 8);
        await preview.SetPausedAsync(true, Token); await preview.ResizeAsync(40, 30, Token); await preview.PresentAsync(.5, Token);
        Assert.Equal(3, fixture.Session.CurrentFrame, 8); Assert.Equal(2, renderer.Presented);
        Assert.Equal((40, 30), renderer.Size);
        await preview.SetPausedAsync(false, Token); await preview.PresentAsync(.1, Token);
        Assert.Equal(6, fixture.Session.CurrentFrame, 8);
        await preview.DisposeAsync(); await preview.DisposeAsync();
        Assert.Equal(1, renderer.Disposals);
        Assert.Throws<ObjectDisposedException>(() => fixture.Session.Frame);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await preview.PresentAsync(0, Token));
    }

    [Fact]
    public async Task RendererFactoryFailureDisposesTheNewPreviewSession()
    {
        var fixture = new OutputFixture();
        await Assert.ThrowsAsync<IOException>(async () => await PreviewSession.CreateAsync(new(20, 10), fixture.CreateSession,
            (_, _) => throw new IOException("factory"), Token));
        Assert.Throws<ObjectDisposedException>(() => fixture.Session!.Frame);
    }

    [Fact]
    public async Task ConcurrentExportHasItsOwnClockAndResources()
    {
        var previewFixture = new OutputFixture(); var exportFixture = new OutputFixture(); var renderer = new PreviewRenderer();
        await using var preview = await PreviewSession.CreateAsync(new(20, 10), previewFixture.CreateSession,
            (_, _) => ValueTask.FromResult<IRealtimeViewRenderer>(renderer), Token);
        await preview.PresentAsync(.1, Token);
        await exportFixture.Coordinator().RunAsync(new(new(2, 2, new(30), 0, 30)), new TestSink(), cancellationToken: Token);
        Assert.Equal(3, previewFixture.Session!.CurrentFrame, 8); Assert.Equal(0, renderer.Disposals);
        await preview.PresentAsync(.1, Token); Assert.Equal(6, previewFixture.Session.CurrentFrame, 8);
    }

    [Fact]
    public async Task CloseWaitsForAnInFlightPresentationBeforeDisposingResources()
    {
        var fixture = new OutputFixture(); var renderer = new PreviewRenderer();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        renderer.Presenting = async ct => { entered.SetResult(); await unblock.Task.WaitAsync(ct); };
        var preview = await PreviewSession.CreateAsync(new(20, 10), fixture.CreateSession,
            (_, _) => ValueTask.FromResult<IRealtimeViewRenderer>(renderer), Token);
        var present = preview.PresentAsync(.1, Token).AsTask(); await entered.Task.WaitAsync(Token);
        var closing = preview.DisposeAsync().AsTask();
        Assert.False(closing.IsCompleted); Assert.Equal(0, renderer.Disposals);
        unblock.SetResult(); await present; await closing;
        Assert.Equal(1, renderer.Disposals);
        Assert.Throws<ObjectDisposedException>(() => fixture.Session!.Frame);
    }

    private sealed class PreviewRenderer : IRealtimeViewRenderer
    {
        public ViewRendererCapabilities Capabilities => new("test-present", 4096, 4096, false, true);
        public int Presented, Disposals;
        public List<PreviewFrame> Frames { get; } = [];
        public (int, int) Size;
        public Func<CancellationToken, ValueTask>? Presenting;
        public async ValueTask PresentAsync(PreviewFrame frame, CancellationToken cancellationToken = default)
        { Presented++; Frames.Add(frame); if (Presenting is not null) await Presenting(cancellationToken); }
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default)
        { Size = (width, height); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
