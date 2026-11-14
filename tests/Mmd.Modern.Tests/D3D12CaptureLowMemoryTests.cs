using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12CaptureLowMemoryTests
{
    [Fact]
    public void AutomaticSelectionUsesTheNormalReservationAndNeverChangesTheExplicitPolicy()
    {
        Assert.Equal(D3D12CaptureMemoryMode.Default, D3D12CaptureMemoryOptions.Auto.Select(1000, 1000));
        Assert.Equal(D3D12CaptureMemoryMode.LowMemory, D3D12CaptureMemoryOptions.Auto.Select(1001, 1000));
        Assert.Equal(D3D12CaptureMemoryMode.Default, D3D12CaptureMemoryOptions.Auto.Select(null, 0));
        Assert.Equal(D3D12CaptureMemoryMode.LowMemory, D3D12CaptureMemoryOptions.LowMemory.Select(0, ulong.MaxValue));
        Assert.Equal(D3D12CaptureMemoryMode.Default, D3D12CaptureMemoryOptions.Default.Select(ulong.MaxValue, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12CaptureMemoryOptions((D3D12CaptureMemoryMode)123).Validate());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(16)]
    [InlineData(32)]
    public void SequentialReservationKeepsEverySamplesReadbackButOnlyOneReceiverAndOneFlight(int samples)
    {
        var ordinary = D3D12SsaaGiMemoryBudget.FromResourceSizes(100, 75, samples,
            perSampleDiagnosticBytes: 7);
        var sequential = D3D12SsaaGiMemoryBudget.FromResourceSizes(100, 75, samples,
            reuseReceiverWorkspace: true, inFlightFrames: 1, perSampleDiagnosticBytes: 7);
        Assert.Equal((ulong)(samples * 107 + 75), ordinary.PerFrameBytes);
        Assert.Equal(ordinary.PerFrameBytes * 3, ordinary.InFlightBytes);
        Assert.Equal((ulong)(100 + 75 + samples * 7), sequential.PerFrameBytes);
        Assert.Equal(sequential.PerFrameBytes, sequential.InFlightBytes);
        Assert.Equal(1, sequential.InFlightFrames);
        Assert.Equal(1, sequential.ReceiverWorkspaces);
        Assert.Equal(samples, ordinary.ReceiverWorkspaces);
        Assert.Throws<ArgumentOutOfRangeException>(() => D3D12SsaaGiMemoryBudget.FromResourceSizes(
            100, 75, samples, inFlightFrames: 0));
        Assert.Throws<OverflowException>(() => D3D12SsaaGiMemoryBudget.FromResourceSizes(
            1, 0, samples, reuseReceiverWorkspace: true, inFlightFrames: 1, perSampleDiagnosticBytes: ulong.MaxValue));
    }

    [Fact]
    public void ForcedTemporalCaptureRetiresEvenWhenTheThreeSlotEstimateWouldFit()
    {
        var estimate = D3D12TemporalGiMemoryBudget.FromResourceSizes(65536, 123456, 76543, 16);
        var events = new List<string>();
        var admission = D3D12TemporalGiMemoryBudget.Admit(estimate,
            () => { events.Add("query"); return new(estimate.InFlightBytes * 4, 0, true); },
            () => events.Add("retire"), 69, 51, 16, allowBoundedFlight: true, forceBoundedFlight: true);
        Assert.Equal(new[] { "retire", "query" }, events);
        Assert.Equal(D3D12TemporalGiMemoryBudget.ForBoundedFlight(estimate), admission.Reservation);
        Assert.Equal(estimate.HistoryBytes, admission.Reservation.HistoryBytes);
        Assert.Equal(2, admission.Reservation.HistoryGenerations);
    }

    [Fact]
    public void OneFlightBudgetErrorReportsActualWindowAndRetainsQuality()
    {
        var estimate = D3D12SsaaGiMemoryBudget.FromResourceSizes(100, 75, 32,
            reuseReceiverWorkspace: true, inFlightFrames: 1);
        var error = Assert.Throws<D3D12GiMemoryBudgetExceededException>(() =>
            D3D12SsaaGiMemoryBudget.RequireFits(estimate, new(348, 0, true), 69, 51, 32));
        Assert.Contains("1 in-flight frames", error.Message);
        Assert.Contains("32 spatial samples", error.Message);
        Assert.Equal(175UL, error.RequiredBytes);
        Assert.Equal(174UL, error.AvailableBytes);
        Assert.True(error.IsPreSubmit);
    }

    [Fact, Trait("Category", "Gpu")]
    public void TemporalAaWorkspaceIsReservedForBothGiDenoisersBeforeAnyFrameIsAllocated()
    {
        using var runtime = new D3D12GpuRuntime();
        var frame = D3D12RayGiTests.Bounce();
        var gi = new RayGiOptions(LightingMode.SoftwareRayTracing, Denoiser: RayGiDenoiser.Spatial);
        const int width = 2048, height = 2048, displayWidth = 4096, displayHeight = 4096;
        D3D12SsaaGiMemoryBudget.Estimate Common(bool temporalAa) => D3D12SsaaGiMemoryBudget.Measure(runtime,
            width, height, displayWidth, displayHeight, 1, gi, true, false, false, frame: frame,
            reuseReceiverWorkspace: true, inFlightFrames: 1, temporalUpscaling: temporalAa);
        var plain = Common(false);
        var complete = Common(true);
        ulong temporalWorkspace = D3D12TemporalGiMemoryBudget.MeasureTemporalUpscalingWorkspace(runtime,
            width, height, displayWidth, displayHeight, frame);
        Assert.True(temporalWorkspace > 244UL * 1024 * 1024);
        Assert.Equal(temporalWorkspace, complete.PerFrameBytes - plain.PerFrameBytes);
        Assert.Equal(complete.PerFrameBytes, complete.InFlightBytes);
        var temporalGi = D3D12TemporalGiMemoryBudget.Measure(runtime, width, height, displayWidth, displayHeight,
            gi with { Denoiser = RayGiDenoiser.TemporalSpatial }, true, frame: frame, temporalUpscaling: true,
            reuseReceiverWorkspace: true);
        ulong triangleBytes = runtime.Device.GetResourceAllocationInfo(0,
            [ResourceDescription.Buffer(checked((ulong)(frame.Surface.Snapshot.Indices.Length / 3)
                * (ulong)Marshal.SizeOf<RayGiTriangleGpu>()))]).SizeInBytes;
        Assert.Equal(complete.PerFrameBytes + triangleBytes, temporalGi.PerFrameBytes);
        // The old incomplete estimate fits exactly; the complete request must still reject.
        var budget = new D3D12SsaaGiMemoryBudget.Budget(plain.InFlightBytes * 2, 0, true);
        D3D12SsaaGiMemoryBudget.RequireFits(plain, budget, width, height, 1);
        Assert.Throws<D3D12GiMemoryBudgetExceededException>(() =>
            D3D12SsaaGiMemoryBudget.RequireFits(complete, budget, width, height, 1));
        Assert.Equal(D3D12CaptureMemoryMode.Default, D3D12CaptureMemoryOptions.Auto.Select(plain.InFlightBytes, budget.AvailableBytes));
        Assert.Equal(D3D12CaptureMemoryMode.LowMemory, D3D12CaptureMemoryOptions.Auto.Select(complete.InFlightBytes, budget.AvailableBytes));
        Assert.Equal(0, runtime.FrameId);
        Assert.Equal(0, runtime.LiveFrameResources);
        Assert.False(runtime.IsRecording);
    }

    [Theory, Trait("Category", "Gpu")]
    [InlineData(LightingMode.SoftwareRayTracing, RayGiDenoiser.Off)]
    [InlineData(LightingMode.SoftwareRayTracing, RayGiDenoiser.Spatial)]
    [InlineData(LightingMode.SoftwareRayTracing, RayGiDenoiser.TemporalSpatial)]
    [InlineData(LightingMode.HardwareRayTracing, RayGiDenoiser.Off)]
    [InlineData(LightingMode.HardwareRayTracing, RayGiDenoiser.Spatial)]
    [InlineData(LightingMode.HardwareRayTracing, RayGiDenoiser.TemporalSpatial)]
    public void SequentialGiMatchesEveryByteAcrossSamplesHistoriesAndSubsequentDefaultCapture(
        LightingMode mode, RayGiDenoiser denoiser)
    {
        const int width = 69, height = 51, samples = 8;
        using var ordinary = new D3D12FrameSession();
        using var sequential = new D3D12FrameSession();
        var support = ordinary.QueryRayGiCapabilities();
        Assert.True(mode == LightingMode.HardwareRayTracing ? support.HardwareAvailable : support.SoftwareAvailable,
            support.UnavailableReason);
        var source = D3D12RayGiTests.Bounce();
        var gi = new RayGiOptions(mode, Strength: .5f, SamplesPerPixel: 4, Seed: 31, MaxRayDistance: 50,
            RayEpsilon: .001f, Composition: RayGiComposition.ToonPalette, Denoiser: denoiser);
        long ordinaryWorkspaceBytes = 0;
        for (int frameIndex = 0; frameIndex < 4; frameIndex++)
        {
            var camera = source.Surface.Snapshot.Camera;
            var frame = WithOptions(source, source.Options! with
            {
                SpatialSamples = samples,
                Antialiasing = FrameAntialiasing.Smaa1X,
                CameraOverride = camera with { Eye = camera.Eye + new Vector3(frameIndex * .025f, 0, 0),
                    ProjectionJitterPixels = new(.125f, -.0625f) }
            });
            var expected = ordinary.Capture(frame, width, height, rayGiOptions: gi);
            var actual = sequential.Capture(frame, width, height, rayGiOptions: gi,
                captureMemory: D3D12CaptureMemoryOptions.LowMemory);
            Assert.Equal(expected.Rgba, actual.Rgba);
            Assert.Equal(samples, sequential.RayGiStatistics!.DispatchCount);
            Assert.Equal(ordinary.RayGiStatistics!.Seed, sequential.RayGiStatistics.Seed);
            Assert.Equal(ordinary.RayGiStatistics.TemporalHistoryFrames, sequential.RayGiStatistics.TemporalHistoryFrames);
            Assert.Equal(ordinary.RayGiStatistics.TemporalHistoryBytes, sequential.RayGiStatistics.TemporalHistoryBytes);
            Assert.Equal(samples, ordinary.RayGiStatistics.ReceiverWorkspaceCount);
            Assert.Equal(1, sequential.RayGiStatistics.ReceiverWorkspaceCount);
            ordinaryWorkspaceBytes = ordinary.RayGiStatistics.ReceiverWorkspaceBytes;
            Assert.Equal(ordinaryWorkspaceBytes / samples, sequential.RayGiStatistics.ReceiverWorkspaceBytes);
            Assert.True(sequential.Statistics!.CaptureLowMemory);
            Assert.Equal(1, sequential.Statistics.CaptureInFlightFrames);
            Assert.Equal(samples, sequential.Statistics.SpatialSamples);
            Assert.Equal(samples, sequential.Statistics.SpatialAccumulationDispatches);
            Assert.Equal(3, sequential.Statistics.AntialiasingPassDrawCalls);
            Assert.Equal(1, sequential.Statistics.SubmissionCount);
            Assert.Equal(1, sequential.Statistics.ReadbackCount);
            Assert.True(sequential.Statistics.FrameOwnedAllocations < ordinary.Statistics!.FrameOwnedAllocations);
            if (denoiser == RayGiDenoiser.TemporalSpatial)
            {
                Assert.Equal(1, sequential.Statistics.TemporalGiInFlightFrames);
                Assert.Equal(2, sequential.Statistics.TemporalGiHistoryGenerations);
            }
        }
        // Switching the memory policy does not reset successful temporal history or mutate quality.
        var final = WithOptions(source, source.Options! with { SpatialSamples = samples });
        Assert.Equal(ordinary.Capture(final, width, height, rayGiOptions: gi).Rgba,
            sequential.Capture(final, width, height, rayGiOptions: gi).Rgba);
        Assert.False(sequential.Statistics!.CaptureLowMemory);
        Assert.Equal(samples, sequential.RayGiStatistics!.ReceiverWorkspaceCount);
        Assert.Equal(ordinaryWorkspaceBytes, sequential.RayGiStatistics.ReceiverWorkspaceBytes);
    }

    [Fact, Trait("Category", "Gpu")]
    public void LowMemoryRetiresCompletedSlotsForCapturesWithoutGiAndPreservesThePublicDefault()
    {
        using var session = new D3D12FrameSession();
        var frame = D3D12RayGiTests.Bounce();
        var original = session.Capture(frame, 47, 35);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(original.Rgba, session.Capture(frame, 47, 35,
                captureMemory: D3D12CaptureMemoryOptions.LowMemory).Rgba);
            Assert.Equal(1, session.Statistics!.CaptureInFlightFrames);
            Assert.Equal(0, session.Statistics.PendingFrames);
            Assert.True(session.Statistics.LiveFrameResources <= session.Statistics.FrameOwnedAllocations + 3);
        }
        Assert.Equal(original.Rgba, session.Capture(frame, 47, 35,
            captureMemory: D3D12CaptureMemoryOptions.Default).Rgba);
        Assert.False(session.Statistics!.CaptureLowMemory);
    }

    private static OriginalFrameScene WithOptions(OriginalFrameScene frame, OriginalFrameOptions options)
        => new(frame.Surface, frame.Outline, frame.OutlineOptions, options, frame.Materials, frame.Lighting, frame.EditorOverlay);
}
