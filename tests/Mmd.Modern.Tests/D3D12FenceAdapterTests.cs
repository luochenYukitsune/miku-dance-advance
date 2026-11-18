using System.Diagnostics;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Xunit;
using Candidate = Mmd.Modern.Rendering.D3D12.D3D12AdapterSelection.Candidate;

namespace Mmd.Modern.Tests;

public sealed class D3D12AdapterSelectionTests
{
    private static readonly Candidate Igpu = new(1, "igpu", false), Dgpu = new(2, "dgpu", false), Basic = new(3, "basic", true);

    private static (Candidate Adapter, string Device)? Select(Candidate[]? highPerformance, Candidate[] enumerated, List<string> tried, params string[] failing)
        => D3D12AdapterSelection.Select(highPerformance, enumerated, c => c, c =>
        { tried.Add(c.Name); return failing.Contains(c.Name) ? null : "device:" + c.Name; });

    [Fact]
    public void HighPerformanceOrderWinsOverEnumerationOrder()
    {
        var tried = new List<string>();
        Assert.Equal("dgpu", Select([Dgpu, Igpu, Basic], [Igpu, Dgpu, Basic], tried)?.Adapter.Name);
        Assert.Equal(["dgpu"], tried);
    }

    [Fact]
    public void WithoutFactory6KeepsEnumerationOrder()
    {
        var tried = new List<string>();
        Assert.Equal("device:igpu", Select(null, [Basic, Igpu, Dgpu], tried)?.Device);
        Assert.Equal(["igpu"], tried);
    }

    [Fact]
    public void FailedCreationFallsThroughOnceAndSkipsSoftware()
    {
        var tried = new List<string>();
        Assert.Equal("igpu", Select([Basic, Dgpu, Igpu], [Igpu, Dgpu, Basic], tried, "dgpu")?.Adapter.Name);
        Assert.Equal(["dgpu", "igpu"], tried);
        tried.Clear();
        Assert.Equal("igpu", Select([], [Basic, Igpu], tried)?.Adapter.Name);
        tried.Clear();
        Assert.Equal("igpu", Select([Dgpu], [Igpu, Dgpu], tried, "dgpu")?.Adapter.Name);
        Assert.Equal(["dgpu", "igpu"], tried);
        tried.Clear();
        Assert.Null(Select([Dgpu, Basic], [Igpu, Dgpu, Basic], tried, "dgpu", "igpu"));
        Assert.Equal(["dgpu", "igpu"], tried);
    }
}

[Trait("Category", "Gpu")]
public sealed class D3D12FenceWaitGpuTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static Thread Waiting(D3D12FenceWaiter waiter, ulong value)
    {
        var thread = new Thread(() => waiter.Wait(value, CancellationToken.None)) { IsBackground = true };
        thread.Start(); return thread;
    }

    [Fact]
    public void RuntimeWaitReturnsForSubmittedAndAlreadyCompletedFences()
    {
        using var runtime = new D3D12GpuRuntime();
        runtime.Begin(); ulong value = runtime.Submit();
        runtime.WaitForFence(value, CancellationToken.None);
        Assert.True(runtime.CompletedFence >= value);
        runtime.Wait(value); runtime.Wait(0);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        // Existing contract: cancellation is reported even when the fence has already completed.
        Assert.Throws<OperationCanceledException>(() => runtime.WaitForFence(value, cancelled.Token));
    }

    [Fact]
    public void WaiterWakesOnSignalAndKeepsConcurrentWaitersApart()
    {
        using var runtime = new D3D12GpuRuntime();
        using var fence = runtime.Device.CreateFence(0, FenceFlags.None);
        using var waiter = new D3D12FenceWaiter(fence);
        waiter.Wait(0, CancellationToken.None);
        var first = Waiting(waiter, 1); var second = Waiting(waiter, 2);
        Assert.False(first.Join(50)); Assert.True(second.IsAlive);
        fence.Signal(1);
        Assert.True(first.Join(Bound));
        Assert.False(second.Join(50));
        fence.Signal(2);
        Assert.True(second.Join(Bound));
        Assert.Equal(2ul, fence.CompletedValue);
    }

    [Fact]
    public void CancelledWaitThrowsAndPooledEventStillCompletesLaterWaits()
    {
        using var runtime = new D3D12GpuRuntime();
        using var fence = runtime.Device.CreateFence(0, FenceFlags.None);
        using var waiter = new D3D12FenceWaiter(fence);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var timer = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => waiter.Wait(1, cancellation.Token));
        Assert.True(timer.Elapsed < Bound);
        // The cancelled registration for value 1 is still pending on the pooled event and fires first.
        var later = Waiting(waiter, 2);
        fence.Signal(1);
        Assert.False(later.Join(100));
        fence.Signal(2);
        Assert.True(later.Join(Bound));
    }

    [Fact]
    public void PreviewRuntimeAndMeshRendererChooseTheSameAdapter()
    {
        using var runtime = new D3D12GpuRuntime();
        using var mesh = new D3D12MeshRenderer();
        Assert.Equal(runtime.AdapterName, mesh.AdapterName);
        Assert.Equal(runtime.IsWarp, mesh.IsWarp);
        Assert.Equal(runtime.Device.AdapterLuid, mesh.AdapterLuid);
    }
}
