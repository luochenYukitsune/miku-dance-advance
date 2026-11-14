using Mmd.Modern.Application;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class XeSsDispatchThreadTests
{
    [Fact]
    public async Task ConcurrentCallersUseOneDedicatedThreadAndKeepExceptionsAndNestedCalls()
    {
        int active = 0, highWater = 0;
        var calls = Enumerable.Range(0, 16).Select(_ => Task.Run(() => D3D12XeSsDispatchThread.Invoke(() =>
        {
            int count = Interlocked.Increment(ref active);
            highWater = Math.Max(highWater, count);
            try { return D3D12XeSsDispatchThread.Invoke(() => Environment.CurrentManagedThreadId); }
            finally { Interlocked.Decrement(ref active); }
        }))).ToArray();
        int[] threads = await Task.WhenAll(calls);
        Assert.Single(threads.Distinct()); Assert.Equal(1, highWater); Assert.Equal(0, active);
        var expected = new InvalidOperationException("SDK failure identity");
        var actual = Assert.Throws<InvalidOperationException>(() => D3D12XeSsDispatchThread.Invoke<int>(() => throw expected));
        Assert.Same(expected, actual);
        Assert.Equal(threads[0], D3D12XeSsDispatchThread.Invoke(() => Environment.CurrentManagedThreadId));
    }
}

[Trait("Category", "Gpu")]
public sealed class XeSsConcurrencyTests
{
    [Fact]
    public async Task IndependentViewsCanInitializeExecuteAndDisposeFromConcurrentAsyncCallers()
    {
        string library = Environment.GetEnvironmentVariable("MMD_XESS_RUNTIME_PATH")
            ?? throw new InvalidOperationException("Set MMD_XESS_RUNTIME_PATH for actual concurrent XeSS GPU execution.");
        var request = new ExportRequest(new(96, 72, new(30), 0, 2), new(TemporalUpscaling: new()
            { Mode = ViewTemporalUpscalingMode.XeSs })).Render;
        var plan = new OfflineFramePlan(96, 72, new(30), 0, 2);
        var scene = D3D12CharacterFsrSessionTests.Character();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            await using var renderer = new D3D12ViewRenderer(request, xessLibraryPath: library);
            for (int index = 0; index < 3; index++)
            {
                await Task.Yield();
                var frame = new OfflineFrame(plan.GetSample(index), 96, 72, scene, null, null, null);
                using var pixels = await renderer.CaptureAsync(frame, TestContext.Current.CancellationToken);
                Assert.Equal(96 * 72 * 4, pixels.Pixels.Length);
                Assert.False(renderer.IsWarp);
                var state = renderer.Statistics!.TemporalUpscaling!;
                Assert.Equal(index == 0, state.ResetHistory);
                Assert.Equal(index + 1, state.SubmittedFrames);
                Assert.True(state.XeSsDeviceQuery!.Initialized);
                Assert.Equal(0, state.XeSsDeviceQuery.InitializeCode);
            }
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(tasks);
    }
}
