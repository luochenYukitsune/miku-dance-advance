using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.Rendering.D3D9.Compatibility.Tests;

// Uses only fake API calls on the real owner thread; no graphics library is loaded.
public sealed class SurfacePreviewSessionTests
{
    [Fact]
    public async Task PreviewUsesTheSharedOwnerAndDrainsAcceptedWorkBeforeRetryableDestroy()
    {
        var fake = new PreviewApi { BlockPresent = true, DestroyFailures = 1 };
        var session = await Create(fake);
        var active = session.PresentAsync(SurfaceSessionTests.Snapshot(), cancellationToken: TestContext.Current.CancellationToken);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var queued = session.ResizeAsync(8, 4, TestContext.Current.CancellationToken);
        var cleanup = session.DisposeAsync().AsTask();
        Assert.False(cleanup.IsCompleted);
        Assert.DoesNotContain(fake.Calls, call => call.Name is "destroy" or "unload");
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.PresentAsync(SurfaceSessionTests.Snapshot(), cancellationToken: TestContext.Current.CancellationToken));
        fake.Release.Set();
        var result = await active;
        await queued;
        await Assert.ThrowsAsync<D3D9SurfaceNativeException>(() => cleanup);
        Assert.DoesNotContain(fake.Calls, call => call.Name == "unload");
        await session.DisposeAsync();
        Assert.Equal(new[] { "query", "create preview", "present", "resize", "destroy", "destroy", "unload" }, fake.Calls.Select(call => call.Name));
        Assert.Single(fake.Calls.Select(call => call.OsThread).Distinct());
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, fake.Calls.Select(call => call.ManagedThread));
        Assert.Equal(4706, result.SourceFrame);
        Assert.Equal(1UL, result.CompletedPresents);
    }

    [Fact]
    public async Task ImmutableModesRejectTheOtherEntryBeforeQueueAdmission()
    {
        var fake = new PreviewApi { BlockPresent = true };
        var session = await Create(fake, capacity: 1);
        var active = session.PresentAsync(SurfaceSessionTests.Snapshot(), cancellationToken: TestContext.Current.CancellationToken);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var admitted = session.ResizeAsync(4, 2, TestContext.Current.CancellationToken);
        try
        {
            Assert.Throws<InvalidOperationException>(() => { _ = session.CaptureAsync(SurfaceSessionTests.Snapshot(), cancellationToken: TestContext.Current.CancellationToken); });
        }
        finally { fake.Release.Set(); }
        await active;
        await admitted;
        await session.DisposeAsync();
        var captureFake = new PreviewApi();
        await using var capture = await ExperimentalD3D9SurfaceSession.CreateForTestingAsync(() => captureFake, new(4, 2), TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => { _ = capture.PresentAsync(SurfaceSessionTests.Snapshot(), cancellationToken: TestContext.Current.CancellationToken); });
        Assert.DoesNotContain(captureFake.Calls, call => call.Name == "present");
    }

    [Theory]
    [InlineData(1UL, true)]
    [InlineData(129UL, false)]
    public async Task UnsupportedPreviewIsRejectedBeforeEitherCreateAndStillUnloads(ulong features, bool exports)
    {
        var fake = new PreviewApi { Features = features, SupportsPreview = exports };
        await Assert.ThrowsAsync<NotSupportedException>(() => Create(fake));
        Assert.Equal(new[] { "query", "unload" }, fake.Calls.Select(call => call.Name));
    }

    [Fact]
    public async Task PreviewWithoutUvRejectsUvBeforePresentAndCanStillPresentLegacyPackets()
    {
        var fake = new PreviewApi { Features = 129 };
        await using var session = await Create(fake);
        var uv = SurfaceAdditionalUvSessionTests.UvPackets();
        await Assert.ThrowsAsync<NotSupportedException>(() => session.PresentAsync(SurfaceAdditionalUvSessionTests.Copy(uv.Frame, uv.Visual), cancellationToken: TestContext.Current.CancellationToken));
        Assert.DoesNotContain(fake.Calls, call => call.Name == "present");
        await session.PresentAsync(SurfaceSessionTests.Snapshot(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(fake.Calls, call => call.Name == "present");
    }

    [Fact]
    public async Task CancellationSkipsQueuedAndWaitingPresentAndWaitsForAnInFlightNativeReturn()
    {
        var fake = new PreviewApi { BlockPresent = true };
        var session = await Create(fake, capacity: 1);
        using var activeCancellation = new CancellationTokenSource();
        using var queuedCancellation = new CancellationTokenSource();
        using var waitingCancellation = new CancellationTokenSource();
        using var beforeCancellation = new CancellationTokenSource();
        beforeCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.PresentAsync(SurfaceSessionTests.Snapshot(), cancellationToken: beforeCancellation.Token));
        var active = session.PresentAsync(SurfaceSessionTests.Snapshot(), cancellationToken: activeCancellation.Token);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var queued = session.PresentAsync(SurfaceSessionTests.Snapshot(), cancellationToken: queuedCancellation.Token);
        var waiting = session.PresentAsync(SurfaceSessionTests.Snapshot(), cancellationToken: waitingCancellation.Token);
        waitingCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        queuedCancellation.Cancel();
        activeCancellation.Cancel();
        Assert.False(active.IsCompleted);
        var cleanup = session.DisposeAsync().AsTask();
        Assert.False(cleanup.IsCompleted);
        fake.Release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        await cleanup;
        Assert.Equal(new[] { "query", "create preview", "present", "destroy", "unload" }, fake.Calls.Select(call => call.Name));
    }

    [Fact]
    public async Task NativePresentErrorWinsOverConcurrentCancellationAndTheLaneRemainsUsable()
    {
        var fake = new PreviewApi { BlockPresent = true, PresentFailures = 1 };
        await using var session = await Create(fake);
        using var cancellation = new CancellationTokenSource();
        var active = session.PresentAsync(SurfaceSessionTests.Snapshot(), cancellationToken: cancellation.Token);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        fake.Release.Set();
        var error = await Assert.ThrowsAsync<D3D9SurfaceNativeException>(() => active);
        Assert.Equal("present", error.Operation);
        await session.PresentAsync(SurfaceSessionTests.Snapshot(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, fake.Calls.Count(call => call.Name == "present"));
    }

    [Fact]
    public async Task CancelledPreviewInitializationRetainsTheSameOwnerForFailedDestroyRetry()
    {
        var fake = new PreviewApi { BlockCreate = true, DestroyFailures = 1 };
        using var cancellation = new CancellationTokenSource();
        var creating = ExperimentalD3D9SurfaceSession.CreatePreviewForTestingAsync(() => fake, (nint)0x1234, new(4, 2), cancellation.Token);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.False(creating.IsCompleted);
        fake.Release.Set();
        var error = await Assert.ThrowsAsync<D3D9SurfaceInitializationCleanupException>(() => creating);
        var causes = Assert.IsType<AggregateException>(error.InnerException);
        Assert.IsAssignableFrom<OperationCanceledException>(causes.InnerExceptions[0]);
        Assert.IsType<D3D9SurfaceNativeException>(causes.InnerExceptions[1]);
        Assert.DoesNotContain(fake.Calls, call => call.Name == "unload");
        await error.DisposeAsync();
        Assert.Equal(new[] { "query", "create preview", "destroy", "destroy", "unload" }, fake.Calls.Select(call => call.Name));
        Assert.Single(fake.Calls.Select(call => call.OsThread).Distinct());
    }

    [Fact]
    public async Task ZeroBorrowedWindowIsRejectedBeforeLoading()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ExperimentalD3D9SurfaceSession.CreatePreviewAsync(
            Path.GetFullPath("missing.dll"), 0, new(4, 2), TestContext.Current.CancellationToken));
    }

    private static Task<ExperimentalD3D9SurfaceSession> Create(PreviewApi fake, int capacity = 2) =>
        ExperimentalD3D9SurfaceSession.CreatePreviewForTestingAsync(() => fake, (nint)0x1234,
            new(4, 2, queueCapacity: capacity), TestContext.Current.CancellationToken);

    private sealed class PreviewApi : ID3D9SurfaceNativeApi
    {
        internal readonly ConcurrentQueue<(string Name, int ManagedThread, uint OsThread)> Calls = new();
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        internal bool BlockPresent, BlockCreate;
        internal int PresentFailures, DestroyFailures;
        internal ulong Features = 131;
        public bool SupportsPreview { get; init; } = true;
        private void Record(string name) => Calls.Enqueue((name, Environment.CurrentManagedThreadId,
            OperatingSystem.IsWindows() ? GetCurrentThreadId() : (uint)Environment.CurrentManagedThreadId));
        public NativeAbi QueryAbi() { Record("query"); return NativeAbi.Valid() with { SupportedFeatures = Features }; }
        public ulong Create(D3D9SurfaceSessionOptions options) { Record("create"); return 7; }
        public ulong CreatePreview(nint targetHwnd, D3D9SurfaceSessionOptions options)
        {
            Assert.Equal((nint)0x1234, targetHwnd);
            Record("create preview");
            if (BlockCreate) Block();
            return 7;
        }
        public D3D9SurfaceCapture Capture(ulong handle, D3D9SurfaceSnapshot snapshot, D3D9SurfaceRenderOrder order) =>
            throw new InvalidOperationException("Capture was not expected.");
        public D3D9SurfacePresentation Present(ulong handle, D3D9SurfaceSnapshot snapshot, D3D9SurfaceRenderOrder order)
        {
            Record("present");
            Assert.Equal(7UL, handle);
            if (BlockPresent) Block();
            if (PresentFailures-- > 0) throw new D3D9SurfaceNativeException("present", -9, "fake present failure", 0, false);
            return new(new NativePresentResult { Width = (uint)snapshot.Width, Height = (uint)snapshot.Height,
                SourceFrame = snapshot.SourceFrame, CompletedPresents = 1 });
        }
        public void Resize(ulong handle, int width, int height) { Record("resize"); Assert.Equal(7UL, handle); }
        public void Destroy(ulong handle)
        {
            Record("destroy");
            if (DestroyFailures-- > 0) throw new D3D9SurfaceNativeException("destroy", -9, "fake destroy failure", 0, false);
        }
        public void Dispose() => Record("unload");
        private void Block()
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Fake owner was not released.");
        }
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    }
}
