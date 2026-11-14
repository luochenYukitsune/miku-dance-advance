using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.Rendering.D3D9.Compatibility.Tests;

// Deliberately uses only the fake API. This assembly never loads D3D9 or a GPU DLL.
public sealed class SurfaceSessionTests
{
    [Fact]
    public async Task AllNativeCallsAndUnloadUseOneDedicatedOsThread()
    {
        var caller = Environment.CurrentManagedThreadId;
        var fake = new FakeSurfaceApi();
        var session = await Create(fake);
        var a = await session.CaptureAsync(Snapshot(), cancellationToken: TestContext.Current.CancellationToken);
        await Task.Run(async () => await session.ResizeAsync(12, 8, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var b = await Task.Run(async () => await session.CaptureAsync(Snapshot(), cancellationToken: TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        await Task.Run(session.Dispose, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "query", "create", "capture", "resize", "capture", "destroy", "unload" }, fake.Calls.Select(x => x.Name));
        Assert.Single(fake.Calls.Select(x => x.ManagedThread).Distinct());
        Assert.Single(fake.Calls.Select(x => x.OsThread).Distinct());
        Assert.DoesNotContain(caller, fake.Calls.Select(x => x.ManagedThread));
        Assert.NotSame(a.Rgba, b.Rgba);
        a.Rgba[0] = 99;
        Assert.Equal((byte)42, b.Rgba[0]);
        Assert.Equal(16, a.Stride);
        Assert.Equal(32, a.Rgba.Length);
    }

    [Fact]
    public async Task DisposeDrainsAdmittedWorkAndWaitsForInFlightCapture()
    {
        var fake = new FakeSurfaceApi { BlockCapture = true };
        var session = await Create(fake);
        var active = session.CaptureAsync(Snapshot(), cancellationToken: TestContext.Current.CancellationToken);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var queued = session.ResizeAsync(10, 10, TestContext.Current.CancellationToken);
        var disposed = session.DisposeAsync().AsTask();
        Assert.False(disposed.IsCompleted);
        Assert.DoesNotContain(fake.Calls, x => x.Name is "destroy" or "unload");
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.CaptureAsync(Snapshot(), cancellationToken: TestContext.Current.CancellationToken));
        fake.Release.Set();
        await active;
        await queued;
        await disposed;
        Assert.Equal(new[] { "query", "create", "capture", "resize", "destroy", "unload" }, fake.Calls.Select(x => x.Name));
        session.Dispose();
        await session.DisposeAsync();
        Assert.Single(fake.Calls, x => x.Name == "destroy");
        Assert.Single(fake.Calls, x => x.Name == "unload");
    }

    [Fact]
    public async Task BoundedQueueWaiterIsRejectedWhenDisposalClosesAdmission()
    {
        var fake = new FakeSurfaceApi { BlockCapture = true };
        var session = await Create(fake, capacity: 1);
        var active = session.CaptureAsync(Snapshot(), cancellationToken: TestContext.Current.CancellationToken);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var admitted = session.ResizeAsync(10, 10, TestContext.Current.CancellationToken);
        var waiting = session.ResizeAsync(11, 11, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        var disposed = session.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting);
        fake.Release.Set();
        await active;
        await admitted;
        await disposed;
        Assert.Single(fake.Calls, x => x.Name == "resize");
    }

    [Fact]
    public async Task CancellationBeforeAdmissionAndBeforeNativeCallSkipsWork()
    {
        var fake = new FakeSurfaceApi { BlockCapture = true };
        var session = await Create(fake, capacity: 1);
        using var before = new CancellationTokenSource();
        before.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.CaptureAsync(Snapshot(), cancellationToken: before.Token));
        var active = session.CaptureAsync(Snapshot(), cancellationToken: TestContext.Current.CancellationToken);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        using var queuedCancellation = new CancellationTokenSource();
        var queued = session.ResizeAsync(10, 10, queuedCancellation.Token);
        using var waitingCancellation = new CancellationTokenSource();
        var waiting = session.ResizeAsync(11, 11, waitingCancellation.Token);
        waitingCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        queuedCancellation.Cancel();
        fake.Release.Set();
        await active;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        await session.DisposeAsync();
        Assert.DoesNotContain(fake.Calls, x => x.Name == "resize");
    }

    [Fact]
    public async Task CancellationDuringNativeCallWaitsForReturnThenCancelsResult()
    {
        var fake = new FakeSurfaceApi { BlockCapture = true };
        var session = await Create(fake);
        using var cancellation = new CancellationTokenSource();
        var active = session.CaptureAsync(Snapshot(), cancellationToken: cancellation.Token);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.False(active.IsCompleted);
        var disposed = session.DisposeAsync().AsTask();
        Assert.False(disposed.IsCompleted);
        fake.Release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await disposed;
        Assert.Equal(new[] { "query", "create", "capture", "destroy", "unload" }, fake.Calls.Select(x => x.Name));
    }

    [Fact]
    public async Task OperationFailureKeepsLaneUsableAndStillCleansUp()
    {
        var fake = new FakeSurfaceApi { CaptureFailures = 1 };
        await using var session = await Create(fake);
        var error = await Assert.ThrowsAsync<D3D9SurfaceNativeException>(() => session.CaptureAsync(Snapshot(), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(-9, error.Status);
        Assert.Equal("capture", error.Operation);
        var output = await session.CaptureAsync(Snapshot(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal((byte)42, output.Rgba[0]);
    }

    [Fact]
    public async Task DestroyFailureRetainsDllAndCanBeRetriedOnTheSameOwnerThread()
    {
        var fake = new FakeSurfaceApi { DestroyFailures = 1 };
        var session = await Create(fake);
        await Assert.ThrowsAsync<D3D9SurfaceNativeException>(() => session.DisposeAsync().AsTask());
        Assert.DoesNotContain(fake.Calls, x => x.Name == "unload");
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ResizeAsync(4, 2, TestContext.Current.CancellationToken));
        await session.DisposeAsync();
        Assert.Equal(2, fake.Calls.Count(x => x.Name == "destroy"));
        Assert.Single(fake.Calls, x => x.Name == "unload");
        Assert.Single(fake.Calls.Select(x => x.OsThread).Distinct());
    }

    [Fact]
    public async Task ConcurrentDisposeCallsShareOneCleanupAttempt()
    {
        var fake = new FakeSurfaceApi { BlockCapture = true };
        var session = await Create(fake);
        var active = session.CaptureAsync(Snapshot(), cancellationToken: TestContext.Current.CancellationToken);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var first = session.DisposeAsync().AsTask();
        var second = session.DisposeAsync().AsTask();
        Assert.Same(first, second);
        var synchronous = Task.Run(session.Dispose, TestContext.Current.CancellationToken);
        fake.Release.Set();
        await Task.WhenAll(active, first, second, synchronous);
        Assert.Single(fake.Calls, x => x.Name == "destroy");
        Assert.Single(fake.Calls, x => x.Name == "unload");
    }

    [Fact]
    public async Task FailedCreateUnloadsWithoutDestroyingAnInvalidHandle()
    {
        var fake = new FakeSurfaceApi { CreateFailure = true };
        await Assert.ThrowsAsync<D3D9SurfaceNativeException>(() => Create(fake));
        Assert.Equal(new[] { "query", "create", "unload" }, fake.Calls.Select(x => x.Name));
    }

    [Fact]
    public async Task InvalidAbiIsRejectedBeforeCreateAndUnloads()
    {
        var fake = new FakeSurfaceApi { Abi = NativeAbi.Valid() with { PointerBits = 32 } };
        await Assert.ThrowsAsync<NotSupportedException>(() => Create(fake));
        Assert.Equal(new[] { "query", "unload" }, fake.Calls.Select(x => x.Name));
    }

    [Fact]
    public async Task CancelledInitializationWaitsForCreateAndDestroyBeforeReturning()
    {
        var fake = new FakeSurfaceApi { BlockCreate = true };
        using var cancellation = new CancellationTokenSource();
        var creation = CreateWithCancellation(fake, cancellation.Token);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.False(creation.IsCompleted);
        fake.Release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creation);
        Assert.Equal(new[] { "query", "create", "destroy", "unload" }, fake.Calls.Select(x => x.Name));
    }

    [Fact]
    public async Task StartupAndCleanupDoubleFailurePreservesAnExplicitRetryOwner()
    {
        var fake = new FakeSurfaceApi { BlockCreate = true, DestroyFailures = 1 };
        using var cancellation = new CancellationTokenSource();
        var creation = CreateWithCancellation(fake, cancellation.Token);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        fake.Release.Set();
        var failure = await Assert.ThrowsAsync<D3D9SurfaceInitializationCleanupException>(() => creation);
        var causes = Assert.IsType<AggregateException>(failure.InnerException);
        Assert.IsAssignableFrom<OperationCanceledException>(causes.InnerExceptions[0]);
        Assert.IsType<D3D9SurfaceNativeException>(causes.InnerExceptions[1]);
        Assert.DoesNotContain(fake.Calls, x => x.Name == "unload");
        await failure.DisposeAsync();
        Assert.Equal(2, fake.Calls.Count(x => x.Name == "destroy"));
        Assert.Equal("unload", fake.Calls.Last().Name);
        Assert.Single(fake.Calls.Select(x => x.OsThread).Distinct());
    }

    [Fact]
    public async Task UnexpectedManagedOperationExceptionDoesNotStrandDispose()
    {
        var fake = new FakeSurfaceApi { ThrowManagedCapture = true };
        var session = await Create(fake);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CaptureAsync(Snapshot(), cancellationToken: TestContext.Current.CancellationToken));
        session.Dispose();
        Assert.Equal("unload", fake.Calls.Last().Name);
    }

    [Fact]
    public void SnapshotCopiesBothPacketsAndRejectsMismatchedSidecar()
    {
        var (frame, visual) = Packets();
        var snapshot = D3D9SurfaceSnapshot.CopyIsolatedSurface(frame, visual, D3D9SurfaceIsolation.NoConfiguredMmeOrEmm);
        frame[0] = 0;
        visual[0] = 0;
        Assert.Equal((byte)'M', snapshot.FrameBytes[0]);
        Assert.Equal((byte)'M', snapshot.VisualBytes[0]);
        var (otherFrame, otherVisual) = Packets();
        otherVisual[20] ^= 1; // Corrupt only the VIS1 SHA; preserve the otherwise-valid opaque FRM1.
        var mismatch = Assert.Throws<ArgumentException>(() => D3D9SurfaceSnapshot.CopyIsolatedSurface(otherFrame, otherVisual, D3D9SurfaceIsolation.NoConfiguredMmeOrEmm));
        Assert.Contains("not bound", mismatch.Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => D3D9SurfaceSnapshot.CopyIsolatedSurface(snapshot.FrameBytes, snapshot.VisualBytes, (D3D9SurfaceIsolation)0));
    }

    [Theory]
    [InlineData("relative.dll")]
    [InlineData("C:relative.dll")]
    [InlineData("")]
    public async Task RelativeNativeLibraryPathIsRejectedWithoutLoading(string path)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => ExperimentalD3D9SurfaceSession.CreateAsync(path, new(4, 2), TestContext.Current.CancellationToken));
    }

    private static Task<ExperimentalD3D9SurfaceSession> Create(FakeSurfaceApi fake, int capacity = 2) =>
        ExperimentalD3D9SurfaceSession.CreateForTestingAsync(() => fake, new(4, 2, queueCapacity: capacity), TestContext.Current.CancellationToken);

    // These tests explicitly control initialization cancellation while the owner Thread is blocked inside Create.
    private static Task<ExperimentalD3D9SurfaceSession> CreateWithCancellation(FakeSurfaceApi fake, CancellationToken cancellationToken) =>
        ExperimentalD3D9SurfaceSession.CreateForTestingAsync(() => fake, new(4, 2), cancellationToken);

    internal static D3D9SurfaceSnapshot Snapshot()
    {
        var (frame, visual) = Packets();
        return D3D9SurfaceSnapshot.CopyIsolatedSurface(frame, visual, D3D9SurfaceIsolation.NoConfiguredMmeOrEmm);
    }

    internal static (byte[] Frame, byte[] Visual) Packets()
    {
        var frame = new byte[208];
        "MDA9FRM1"u8.CopyTo(frame);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(16), 2);
        BinaryPrimitives.WriteDoubleLittleEndian(frame.AsSpan(40), 4706);
        BinaryPrimitives.WriteSingleLittleEndian(frame.AsSpan(204), 1);
        var visual = new byte[128];
        "MDA9VIS1"u8.CopyTo(visual);
        BinaryPrimitives.WriteUInt32LittleEndian(visual.AsSpan(8), 2);
        SHA256.HashData(frame).CopyTo(visual.AsSpan(20));
        return (frame, visual);
    }

    private sealed class FakeSurfaceApi : ID3D9SurfaceNativeApi
    {
        internal readonly ConcurrentQueue<(string Name, int ManagedThread, uint OsThread)> Calls = new();
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        internal bool BlockCapture, BlockCreate, CreateFailure, ThrowManagedCapture;
        internal int CaptureFailures, DestroyFailures;
        internal NativeAbi Abi = NativeAbi.Valid();
        private void Record(string name) => Calls.Enqueue((name, Environment.CurrentManagedThreadId,
            OperatingSystem.IsWindows() ? GetCurrentThreadId() : (uint)Environment.CurrentManagedThreadId));
        public NativeAbi QueryAbi() { Record("query"); return Abi; }
        public ulong Create(D3D9SurfaceSessionOptions options)
        {
            Record("create");
            if (BlockCreate) Block();
            if (CreateFailure) throw Error("create");
            return 0x100000002UL;
        }
        public D3D9SurfaceCapture Capture(ulong handle, D3D9SurfaceSnapshot snapshot, D3D9SurfaceRenderOrder order)
        {
            Record("capture");
            Assert.Equal(0x100000002UL, handle);
            if (BlockCapture) Block();
            if (ThrowManagedCapture) throw new InvalidOperationException("fake managed failure");
            if (CaptureFailures-- > 0) throw Error("capture");
            var pixels = new byte[snapshot.Width * snapshot.Height * 4];
            Array.Fill(pixels, (byte)42);
            return new(snapshot.Width, snapshot.Height, pixels,
                new NativeCaptureResult { Width = (uint)snapshot.Width, Height = (uint)snapshot.Height, SourceFrame = snapshot.SourceFrame });
        }
        public void Resize(ulong handle, int width, int height) { Record("resize"); Assert.Equal(0x100000002UL, handle); }
        public void Destroy(ulong handle)
        {
            Record("destroy");
            Assert.Equal(0x100000002UL, handle);
            if (DestroyFailures-- > 0) throw Error("destroy");
        }
        public void Dispose() => Record("unload");
        private void Block()
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("fake lane release was not signaled");
        }
        private static D3D9SurfaceNativeException Error(string operation) => new(operation, -9, "fake device failure", 20, false);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    }
}
