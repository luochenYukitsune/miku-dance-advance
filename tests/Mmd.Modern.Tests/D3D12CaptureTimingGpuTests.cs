using System.Text.Json;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12CaptureTimingGpuTests
{
    [Fact]
    public void QueueTimestampRequiresItsFenceAndMapsOnlyOnce()
    {
        using var runtime = new D3D12GpuRuntime();
        var context = runtime.Begin();
        const int bytes = 4 * 1024 * 1024;
        var source = context.Own(context.Device.CreateCommittedResource(HeapType.Upload,
            ResourceDescription.Buffer(bytes), ResourceStates.GenericRead));
        source.Map<byte>(0, bytes).Clear(); source.Unmap(0);
        var destination = context.Own(context.Device.CreateCommittedResource(HeapType.Default,
            ResourceDescription.Buffer(bytes), ResourceStates.CopyDest));
        var timing = new D3D12CaptureTiming(context, "32 MiB GPU buffer copies");
        timing.Begin();
        for (int i = 0; i < 8; i++) context.Commands.CopyBufferRegion(destination, 0, source, 0, bytes);
        timing.EndAndResolve();
        Assert.Throws<InvalidOperationException>(() => timing.ReadResult());
        var fence = runtime.Submit(); runtime.Wait(fence);
        var result = timing.ReadResult();
        Assert.True(result.Frequency > 0);
        Assert.True(result.EndTicks >= result.BeginTicks);
        Assert.True(double.IsFinite(result.GpuMilliseconds));
        Assert.Same(result, timing.ReadResult());
        Assert.Equal(1, timing.ReadbackCount);
        Assert.Equal(1, runtime.Submissions);
        Assert.Equal(fence, result.SubmittedFence);
        Assert.Equal(0, runtime.Readbacks); // The caller accounts the explicit diagnostic, not this helper.
        string? directory = Environment.GetEnvironmentVariable("MMD_TIMESTAMP_ARTIFACTS");
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
            {
                adapter = runtime.AdapterName, warp = runtime.IsWarp, result,
                explicitDiagnosticReadbacks = timing.ReadbackCount, submissions = runtime.Submissions,
                debugRequested = runtime.DebugRequested, debugEnabled = runtime.DebugLayerEnabled,
                infoQueueAvailable = runtime.DebugQueue is not null,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
