using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12TemporalGiBudgetTests
{
    [Fact]
    public void HistoryRetainsFourSeparateBuffersAcrossOwnerAndThreeSlots()
    {
        // Independent committed allocations: even a single pixel needs four 64 KiB buffers.
        var estimate = D3D12TemporalGiMemoryBudget.FromResourceSizes(65536, 100, 200);
        Assert.Equal(262144UL, estimate.HistoryBytes);
        Assert.Equal(1049376UL, estimate.HistoryWindowBytes);
        Assert.Equal(1049676UL, estimate.InFlightBytes);
        Assert.Equal(64, D3D12TemporalGiMemoryBudget.HistoryBytesPerPixel);
    }

    [Fact]
    public void ExactBudgetBoundaryPassesAndNextByteRejectsBeforeGpuWork()
    {
        var estimate = D3D12TemporalGiMemoryBudget.FromResourceSizes(16, 12);
        var fits = new D3D12SsaaGiMemoryBudget.Budget(estimate.InFlightBytes * 2, 0, true);
        D3D12TemporalGiMemoryBudget.RequireFits(estimate, fits, 17, 13);
        var full = fits with { CurrentUsageBytes = fits.BudgetBytes - estimate.InFlightBytes + 1 };
        var error = Assert.Throws<D3D12GiMemoryBudgetExceededException>(() => D3D12TemporalGiMemoryBudget.RequireFits(estimate, full, 17, 13));
        Assert.Contains("Temporal GI at 17x13", error.Message);
        Assert.Contains("4 history generations", error.Message);
        Assert.Contains("3 in-flight frames", error.Message);
        Assert.Contains("Quality was not changed", error.Message);
    }

    [Fact]
    public void UnknownBudgetRemainsBoundedAndOverflowCannotWrapToAnAcceptedEstimate()
    {
        var estimate = D3D12TemporalGiMemoryBudget.FromResourceSizes(65536,
            D3D12SsaaGiMemoryBudget.FallbackReservationBytes);
        var error = Assert.Throws<D3D12GiMemoryBudgetExceededException>(() => D3D12TemporalGiMemoryBudget.RequireFits(
            estimate, new(ulong.MaxValue, 0, false), 1920, 1080));
        Assert.Contains("DXGI unavailable", error.Message);
        Assert.Throws<OverflowException>(() => D3D12TemporalGiMemoryBudget.FromResourceSizes(ulong.MaxValue, 0));
        Assert.Throws<OverflowException>(() => D3D12TemporalGiMemoryBudget.FromResourceSizes(1, ulong.MaxValue));
        Assert.Throws<OverflowException>(() => D3D12TemporalGiMemoryBudget.FromResourceSizes(1, 0, ulong.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => D3D12TemporalGiMemoryBudget.FromResourceSizes(0, 0));
    }

    [Fact, Trait("Category", "Gpu")]
    public void DeviceMeasuresEachHistoryAllocationAndWholeWorkspaceBeforeBeginningAFrame()
    {
        using var runtime = new D3D12GpuRuntime();
        var gi = new RayGiOptions(LightingMode.SoftwareRayTracing, Denoiser: RayGiDenoiser.TemporalSpatial);
        var plain = D3D12TemporalGiMemoryBudget.Measure(runtime, 17, 13, 17, 13, gi, false);
        var description = ResourceDescription.Buffer(17UL * 13 * 16, ResourceFlags.AllowUnorderedAccess);
        ulong committed = runtime.Device.GetResourceAllocationInfo(0, [description]).SizeInBytes;
        Assert.True(committed >= 65536);
        Assert.Equal(committed * 4, plain.HistoryBytes);
        Assert.Equal(plain.HistoryBytes * 4, plain.HistoryWindowBytes);
        Assert.True(plain.PerFrameBytes > 7 * committed); // GBuffer, raw/filter, output/depth and metadata.
        var capture = D3D12TemporalGiMemoryBudget.Measure(runtime, 17, 13, 17, 13, gi, true, true);
        Assert.True(capture.PerFrameBytes > plain.PerFrameBytes);
        var temporal = D3D12TemporalGiMemoryBudget.Measure(runtime, 17, 13, 31, 23, gi, false, temporalUpscaling: true);
        Assert.True(temporal.PerFrameBytes > plain.PerFrameBytes);
        Assert.Equal(0, runtime.FrameId);
        Assert.False(runtime.IsRecording);
        Assert.Equal(0, runtime.LiveFrameResources);
    }

    [Fact, Trait("Category", "Gpu")]
    public void DriverMeasuredMultiSampleHistoryAndDiagnosticWorkspaceScaleWithoutDuplicatingSharedAssets()
    {
        string? saved = Environment.GetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS");
        try
        {
            Environment.SetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS", null);
            using var runtime = new D3D12GpuRuntime();
            var frame = D3D12RayGiTests.Bounce();
            var gi = new RayGiOptions(LightingMode.SoftwareRayTracing, Denoiser: RayGiDenoiser.TemporalSpatial);
            D3D12TemporalGiMemoryBudget.Estimate Measure(int samples, OriginalFrameScene? source = null)
                => D3D12TemporalGiMemoryBudget.Measure(runtime, 17, 13, 17, 13, gi, true,
                    frame: source, spatialSamples: samples);
            var single = Measure(1); var two = Measure(2); var thirtyTwo = Measure(32);
            Assert.Equal(single.HistoryBytes * 2, two.HistoryBytes);
            Assert.Equal(single.HistoryBytes * 32, thirtyTwo.HistoryBytes);
            var common = D3D12SsaaGiMemoryBudget.Measure(runtime,17,13,17,13,2,
                gi with {Denoiser=RayGiDenoiser.Spatial},true,false,false);
            Assert.Equal(common.PerSampleBytes, two.PerFrameBytes-single.PerFrameBytes);
            Assert.Equal(common.PerSampleBytes*30, thirtyTwo.PerFrameBytes-two.PerFrameBytes);
            var sourceTwo = Measure(2, frame); var sourceThirtyTwo = Measure(32, frame);
            Assert.Equal(sourceTwo.HistoryWindowBytes-two.HistoryWindowBytes,
                sourceThirtyTwo.HistoryWindowBytes-thirtyTwo.HistoryWindowBytes);
            Assert.Equal(sourceTwo.PerFrameBytes-two.PerFrameBytes,
                sourceThirtyTwo.PerFrameBytes-thirtyTwo.PerFrameBytes);
            Environment.SetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS", "1");
            ulong deltaTwo = Measure(2).PerFrameBytes-two.PerFrameBytes;
            ulong deltaThirtyTwo = Measure(32).PerFrameBytes-thirtyTwo.PerFrameBytes;
            Assert.True(deltaTwo>0);
            Assert.Equal(deltaTwo*16,deltaThirtyTwo);
            Assert.Equal(0,runtime.FrameId);Assert.False(runtime.IsRecording);
            Assert.Equal(0,runtime.LiveFrameResources);
        }
        finally { Environment.SetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS", saved); }
    }
}
