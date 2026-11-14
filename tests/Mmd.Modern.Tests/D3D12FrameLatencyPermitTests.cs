using Mmd.Modern.Rendering.D3D12;
using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.DXGI;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12FrameLatencyPermitTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WaitRequiresSignalAndCanBeCancelledThenRetried()
    {
        using var ready = new EventWaitHandle(false, EventResetMode.AutoReset);
        using var permit = new D3D12FrameLatencyPermit(ready, ownsHandle: false);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var wait = permit.WaitAsync(cancel.Token).AsTask();
        Assert.False(wait.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(5), Token));
        var retry = permit.WaitAsync(Token).AsTask();
        ready.Set();
        await retry.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task DisposeUnblocksWaitBeforeReleasingHandleAndRejectsNewWaits()
    {
        using var ready = new EventWaitHandle(false, EventResetMode.AutoReset);
        var permit = new D3D12FrameLatencyPermit(ready, ownsHandle: true);
        var wait = permit.WaitAsync(Token).AsTask();
        await Task.Run(permit.Dispose, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.True(ready.SafeWaitHandle.IsClosed);
        Assert.Throws<ObjectDisposedException>(() => { _ = permit.WaitAsync(Token); });
        permit.Dispose();
    }

    [Fact]
    public async Task UnpresentedPermitSurvivesStopAndIsConsumedByPresentation()
    {
        using var ready = new EventWaitHandle(false, EventResetMode.AutoReset);
        using var permit = new D3D12FrameLatencyPermit(ready, ownsHandle: false);
        ready.Set();
        await permit.WaitAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        await permit.WaitAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        permit.OnPresented();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var next = permit.WaitAsync(cancel.Token).AsTask();
        Assert.False(next.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.WaitAsync(TimeSpan.FromSeconds(5), Token));
    }

    [Fact]
    public async Task HiddenNativeSwapchainKeepsWaitableFlagAndLatencyAcrossResize()
    {
        if (!OperatingSystem.IsWindows()) return;
        var token = Token;
        await Task.Run(() => CheckHiddenNativeSwapchain(token), token);
    }

    private static void CheckHiddenNativeSwapchain(CancellationToken token)
    {
        // WS_POPUP without WS_VISIBLE; never call ShowWindow.
        nint window = CreateWindowExW(0, "STATIC", "MDA frame latency contract", 0x80000000, 0, 0, 128, 96, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            using var presenter = new D3D12SwapchainPresenter(window, 96, 72);
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            var swapchain = (IDXGISwapChain3)typeof(D3D12SwapchainPresenter).GetField("swapchain", hidden)!.GetValue(presenter)!;
            Assert.Equal(1, swapchain.MaximumFrameLatency);
            Assert.Equal(2, swapchain.Description1.BufferCount);
            Assert.True(swapchain.Description1.Flags.HasFlag(SwapChainFlags.FrameLatencyWaitableObject));
            var frame = D3D12OriginalOutlineTests.Box();
            presenter.WaitForNextFrameAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token).GetAwaiter().GetResult();
            Assert.True(presenter.Present(frame, 96, 72));
            typeof(D3D12SwapchainPresenter).GetMethod("Resize", hidden)!.Invoke(presenter, [128, 96]);
            Assert.True(swapchain.Description1.Flags.HasFlag(SwapChainFlags.FrameLatencyWaitableObject));
            Assert.Equal(1, swapchain.MaximumFrameLatency);
            presenter.WaitForNextFrameAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token).GetAwaiter().GetResult();
            Assert.True(presenter.Present(frame, 128, 96));
            Assert.Equal(2, presenter.Statistics.PresentCount);
            Assert.Equal(1, presenter.Statistics.ResizeCount);
        }
        finally { Assert.True(DestroyWindow(window)); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint extended, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
