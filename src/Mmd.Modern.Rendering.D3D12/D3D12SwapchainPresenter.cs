using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Mmd.Modern.Core;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Mmd.Modern.Rendering.D3D12;

public readonly record struct D3D12ViewportSize(int Width, int Height)
{
    public bool IsSuspended => Width == 0 || Height == 0;
    public static D3D12ViewportSize FromLogical(double width, double height, double scale)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(scale) || width < 0 || height < 0 || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Viewport size and DPI scale must be finite and nonnegative.");
        int w = checked((int)Math.Ceiling(width * scale)), h = checked((int)Math.Ceiling(height * scale));
        if (w > 4096 || h > 4096) throw new ArgumentOutOfRangeException(nameof(width), "Viewport pixel dimensions must be in 0..4096.");
        return new(w, h);
    }
}

public sealed record D3D12PresentationStatistics(long PresentCount, long ExplicitCaptureCount, int ResizeCount,
    int IdleWaitCount, int Width, int Height, int BackbufferCount, D3D12FrameStatistics? Frame)
{
    public D3D12PresentationTiming? Timing { get; init; }
    public int? LastPresentResultCode { get; init; }
    public D3D12RayGiStatistics? RayGi { get; init; }
    public string? RayGiBackendIdentity { get; init; }
    /// <summary>Realtime preview reports the requested and admitted SSAA counts separately.
    /// Other presentation callers retain their actual frame sample count.</summary>
    public int RequestedSpatialSamples { get; init; } = Frame?.SpatialSamples ?? 1;
    public int EffectiveSpatialSamples { get; init; } = Frame?.SpatialSamples ?? 1;
    public string? PreviewSamplingAdjustmentReason { get; init; }
}

public sealed record D3D12PresentationTiming(double RecordAndSubmitMilliseconds, double PresentWaitMilliseconds, double TotalMilliseconds)
{
    public double InterPresentMilliseconds { get; init; }
    public double PrePresentGateMilliseconds { get; init; }
}

/// <summary>Owns an HWND swapchain on the complete-frame session's queue. No CPU image is used to present.</summary>
public sealed class D3D12SwapchainPresenter : IDisposable
{
    private const SwapChainFlags SwapchainFlags = SwapChainFlags.FrameLatencyWaitableObject;
    private readonly D3D12FrameSession session;
    private readonly IDXGISwapChain3 swapchain;
    private readonly D3D12FrameLatencyPermit framePermit;
    private readonly ID3D12Resource[] backbuffers = new ID3D12Resource[2];
    private int width, height, resizeCount, idleWaitCount;
    private long presents, captures;
    private D3D12PresentationTiming? timing;
    private int? lastPresentResultCode;
    private long previousPresentTimestamp;
    private bool disposed;
    public string AdapterName => session.AdapterName;
    public long AdapterLuid => session.AdapterLuid;
    public bool IsWarp => session.IsWarp;
    public bool DebugRequested => session.DebugRequested;
    public bool DebugLayerEnabled => session.DebugLayerEnabled;
    public bool DebugInfoQueueAvailable => session.DebugInfoQueueAvailable;
    public D3D12RayGiStatistics? RayGiStatistics => session.RayGiStatistics;
    public int EditorOverlayDrawCalls=>session.EditorOverlayDrawCalls;
    public D3D12NativeLightStatistics? NativeLightStatistics=>session.NativeLightStatistics;
    public D3D12ClusteredLightStatistics? ClusteredLightStatistics=>session.ClusteredLightStatistics;
    public D3D12PathTracingStatistics? PathTracingStatistics=>session.PathTracingStatistics;
    public D3D12TemporalUpscalingStatistics? TemporalUpscalingStatistics=>session.TemporalUpscalingStatistics;
    internal bool LastNativeTaaReset => session.LastNativeTaaReset;
    internal int NativeTaaPhase => session.NativeTaaPhase;
    public string? RayGiBackendIdentity => session.RayGiBackendIdentity;
    public D3D12RayGiCapabilities QueryRayGiCapabilities() => session.QueryRayGiCapabilities();
    public D3D12PresentationStatistics Statistics => new(presents, captures, resizeCount, idleWaitCount, width, height, backbuffers.Length,
        session.Statistics is { } frame ? frame with { CompletedFence = session.Runtime.CompletedFence, PendingFrames = session.Runtime.PendingFrames,
            LiveFrameResources = session.Runtime.LiveFrameResources } : null)
        { Timing = timing, LastPresentResultCode = lastPresentResultCode, RayGi = session.RayGiStatistics, RayGiBackendIdentity = session.RayGiBackendIdentity };

    public D3D12SwapchainPresenter(IntPtr windowHandle, int width, int height)
    {
        if (windowHandle == IntPtr.Zero) throw new ArgumentException("A live child HWND is required.", nameof(windowHandle));
        ValidateDimensions(width, height);
        session = new();
        IDXGISwapChain3? createdSwapchain = null;
        D3D12FrameLatencyPermit? createdPermit = null;
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
            var description = new SwapChainDescription1
            {
                Width = width, Height = height, Format = Format.R8G8B8A8_UNorm,
                BufferCount = backbuffers.Length, BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new(1, 0), SwapEffect = SwapEffect.FlipDiscard,
                Scaling = Scaling.Stretch, AlphaMode = AlphaMode.Ignore, Flags = SwapchainFlags
            };
            using var initial = factory.CreateSwapChainForHwnd(session.Runtime.Queue, windowHandle, description);
            createdSwapchain = initial.QueryInterface<IDXGISwapChain3>();
            createdSwapchain.MaximumFrameLatency = 1;
            IntPtr handle = createdSwapchain.FrameLatencyWaitableObject;
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                throw new InvalidOperationException("DXGI did not provide a frame-latency waitable object for the preview swapchain.");
            createdPermit = new D3D12FrameLatencyPermit(new D3D12OwnedWaitHandle(handle), ownsHandle: true,
                checkDevice: () => session.Runtime.Device.DeviceRemovedReason.CheckError());
            swapchain = createdSwapchain;
            framePermit = createdPermit;
            this.width = width; this.height = height;
            AcquireBackbuffers();
        }
        catch { ReleaseBackbuffers(); createdPermit?.Dispose(); createdSwapchain?.Dispose(); session.Dispose(); throw; }
    }

    /// <summary>Wait before capturing the current scene for every presentation, including the first.
    /// A cancelled wait never blocks renderer disposal. Only one render loop should wait on this swapchain.</summary>
    public ValueTask WaitForNextFrameAsync(CancellationToken cancellationToken = default)
        => framePermit.WaitAsync(cancellationToken);

    public bool Present(OriginalFrameScene frame, int width, int height, RayGiOptions? rayGiOptions = null,D3D12SpatialUpscalingOptions? upscaling=null,D3D12TemporalUpscalingOptions? temporalUpscaling=null,D3D12SurfaceStyleMap? surfaceStyles=null,D3D12DiffusionOptions? diffusion=null,D3D12SharpeningOptions? sharpening=null)
        => PresentCore(frame, width, height, rayGiOptions, upscaling, temporalUpscaling, surfaceStyles, diffusion, sharpening, null);

    /// <summary>Record CPU commands for offscreen targets while the last frame is on the GPU.
    /// Wait for both gates before accessing the swapchain or executing this list.</summary>
    internal bool PresentRecordedAhead(OriginalFrameScene frame, int width, int height, RayGiOptions rayGiOptions,
        D3D12SpatialUpscalingOptions upscaling, D3D12SurfaceStyleMap? surfaceStyles, D3D12DiffusionOptions diffusion,
        CancellationToken cancellationToken, D3D12SharpeningOptions? sharpening = null,
        D3D12TemporalUpscalingOptions? temporalUpscaling = null)
    {
        if (temporalUpscaling is { Mode: not D3D12TemporalUpscalingMode.Off })
            throw new NotSupportedException("Recording ahead does not support vendor temporal upscaling.");
        return PresentCore(frame, width, height, rayGiOptions, upscaling, temporalUpscaling, surfaceStyles, diffusion, sharpening, cancellationToken);
    }

    private bool PresentCore(OriginalFrameScene frame, int width, int height, RayGiOptions? rayGiOptions,
        D3D12SpatialUpscalingOptions? upscaling, D3D12TemporalUpscalingOptions? temporalUpscaling,
        D3D12SurfaceStyleMap? surfaceStyles, D3D12DiffusionOptions? diffusion, D3D12SharpeningOptions? sharpening, CancellationToken? recordAhead)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        diffusion?.ValidateFrame(frame); // Reject before resize, including suspended requests.
        surfaceStyles?.Validate(frame,rayGiOptions??frame.Options?.RayGi);
        if (width == 0 || height == 0) return false;
        ValidateDimensions(width, height);
        if (recordAhead is { } cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (presents == 0 || this.width != width || this.height != height)
                throw new InvalidOperationException("Recording ahead requires a prior presentation at the same size.");
        }
        else if (this.width != width || this.height != height) Resize(width, height);
        ulong previousFence = session.Runtime.SubmittedFence;
        double prePresentGateMilliseconds = 0;
        var totalWatch = Stopwatch.StartNew();
        using var output = session.RenderForPresent(frame, width, height, rayGiOptions, (context, target) =>
        {
            if (target.Format != Format.R8G8B8A8_UNorm || target.Width != width || target.Height != height)
                throw new InvalidOperationException("Presentation requires an exact RGBA8 target match.");
            if (recordAhead is { } token)
            {
                long started = Stopwatch.GetTimestamp();
                session.Runtime.WaitForFence(previousFence, token);
                framePermit.Wait(token);
                prePresentGateMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            var backbuffer = backbuffers[swapchain.CurrentBackBufferIndex];
            context.Transition(target, ResourceStates.CopySource);
            context.Commands.ResourceBarrierTransition(backbuffer, ResourceStates.Present, ResourceStates.CopyDest);
            context.Commands.CopyResource(backbuffer, target.Resource);
            context.Commands.ResourceBarrierTransition(backbuffer, ResourceStates.CopyDest, ResourceStates.Present);
            recordAhead?.ThrowIfCancellationRequested();
        },upscaling,temporalUpscaling,surfaceStyles,diffusion,sharpening);
        double recordAndSubmit = totalWatch.Elapsed.TotalMilliseconds;
        var presentWatch = Stopwatch.StartNew();
        var result = swapchain.Present(1, PresentFlags.None);
        result.CheckError();
        lastPresentResultCode = result.Code;
        framePermit.OnPresented();
        long now = Stopwatch.GetTimestamp();
        timing = new(recordAndSubmit, presentWatch.Elapsed.TotalMilliseconds, totalWatch.Elapsed.TotalMilliseconds)
        { InterPresentMilliseconds = previousPresentTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(previousPresentTimestamp, now).TotalMilliseconds,
            PrePresentGateMilliseconds = prePresentGateMilliseconds };
        previousPresentTimestamp = now;
        presents++;
        return true;
    }

    public RenderedFrame Capture(OriginalFrameScene frame, RayGiOptions? rayGiOptions = null,D3D12SpatialUpscalingOptions? upscaling=null,D3D12TemporalUpscalingOptions? temporalUpscaling=null,D3D12SurfaceStyleMap? surfaceStyles=null,D3D12DiffusionOptions? diffusion=null,D3D12SharpeningOptions? sharpening=null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var result = session.Capture(frame, width, height, rayGiOptions,upscaling,temporalUpscaling,surfaceStyles,diffusion,sharpening:sharpening);
        captures++;
        return result;
    }

    public void WaitForIdle()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        session.WaitForIdle(); idleWaitCount++;
    }

    private void Resize(int w, int h)
    {
        WaitForIdle();
        ReleaseBackbuffers();
        swapchain.ResizeBuffers(backbuffers.Length, w, h, Format.R8G8B8A8_UNorm, SwapchainFlags).CheckError();
        width = w; height = h; resizeCount++;
        AcquireBackbuffers();
    }
    private static void ValidateDimensions(int w, int h)
    {
        if (w is < 1 or > 4096 || h is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(w), "Viewport dimensions must be in 1..4096.");
    }
    private void AcquireBackbuffers() { for (int i = 0; i < backbuffers.Length; i++) backbuffers[i] = swapchain.GetBuffer<ID3D12Resource>(i); }
    private void ReleaseBackbuffers() { foreach (var resource in backbuffers) resource?.Dispose(); Array.Clear(backbuffers); }
    public void Dispose()
    {
        if (disposed) return;
        framePermit.Dispose();
        try { session.Runtime.WaitForIdleForDisposal(); }
        finally { disposed = true; ReleaseBackbuffers(); swapchain.Dispose(); session.Dispose(); }
    }
}

// GetFrameLatencyWaitableObject gives the application a HANDLE which must be closed.
internal sealed class D3D12OwnedWaitHandle : WaitHandle
{
    public D3D12OwnedWaitHandle(IntPtr handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
}

// Separate lifetime from the render gate: shutdown can interrupt a pending wait without
// acquiring a gate held by that wait, and the HANDLE is closed only after all waiters exit.
internal sealed class D3D12FrameLatencyPermit : IDisposable
{
    private readonly object sync = new();
    private readonly WaitHandle ready;
    private readonly bool ownsHandle;
    private readonly Action? checkDevice;
    private readonly CancellationTokenSource closing = new();
    private int waiters;
    private bool disposed, acquired;

    internal D3D12FrameLatencyPermit(WaitHandle ready, bool ownsHandle, Action? checkDevice = null)
    {
        this.ready = ready;
        this.ownsHandle = ownsHandle;
        this.checkDevice = checkDevice;
    }

    internal ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        if (!BeginWait(cancellationToken)) return ValueTask.CompletedTask;
        try { return new ValueTask(Task.Run(() => WaitForSignal(cancellationToken))); }
        catch { EndWait(); throw; }
    }

    internal void Wait(CancellationToken cancellationToken)
    {
        if (BeginWait(cancellationToken)) WaitForSignal(cancellationToken);
    }

    private bool BeginWait(CancellationToken cancellationToken)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (acquired) return false;
            if (waiters != 0) throw new InvalidOperationException("Only one frame-latency wait may be active per swapchain.");
            waiters++;
            return true;
        }
    }

    private void WaitForSignal(CancellationToken cancellationToken)
    {
        try
        {
            WaitHandle[] handles = [ready, cancellationToken.WaitHandle, closing.Token.WaitHandle];
            // A removed device may never signal the swapchain again. Only check its
            // health on a stalled wait; a slow but healthy display still owns the permit.
            int signaled;
            while ((signaled = WaitHandle.WaitAny(handles, D3D12FenceWaiter.RecheckInterval)) == WaitHandle.WaitTimeout)
                checkDevice?.Invoke();
            // Preserve a consumed signal if the recording is cancelled before Present.
            if (signaled == 0) lock (sync) acquired = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (signaled == 2 || closing.IsCancellationRequested) throw new OperationCanceledException(closing.Token);
        }
        finally { EndWait(); }
    }

    private void EndWait() { lock (sync) { waiters--; Monitor.PulseAll(sync); } }

    internal void OnPresented()
    {
        lock (sync) acquired = false;
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            closing.Cancel();
            while (waiters != 0) Monitor.Wait(sync);
            if (ownsHandle) ready.Dispose();
            closing.Dispose();
        }
    }
}
