using System.Runtime.InteropServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class ViewGiTemporalAdapterTests
{
    internal static RayGiOptions Options => new(LightingMode.SoftwareRayTracing, Strength: .5f,
        SamplesPerPixel: 4, Seed: 37, MaxRayDistance: 50, RayEpsilon: .001f,
        Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.TemporalSpatial);

    [Fact]
    public void GiHistoryMarkersMapWithoutTemporalAaAndDoNotMutateSharedQuality()
    {
        var quality = new ViewQualityOptions(Gi: Options);
        var state = new ViewTemporalFrameState { HistoryGeneration = 42, CameraCutKey = 9, ResetHistory = true };
        var mapped = D3D12ViewQuality.Gi(quality, state);
        Assert.Equal(ViewTemporalUpscalingMode.Off, quality.EffectiveTemporalUpscaling.Mode);
        Assert.Equal(42, mapped.TemporalHistoryGeneration);
        Assert.Equal(9, mapped.TemporalCameraCutKey);
        Assert.True(mapped.ResetTemporalHistory);
        Assert.Equal(quality.EffectiveGi with
        {
            TemporalHistoryGeneration = 42, TemporalCameraCutKey = 9, ResetTemporalHistory = true
        }, mapped);
        Assert.Equal(0, quality.EffectiveGi.TemporalHistoryGeneration);
        Assert.Equal(0, quality.EffectiveGi.TemporalCameraCutKey);
        Assert.False(quality.EffectiveGi.ResetTemporalHistory);
        Assert.False(D3D12ViewQuality.Gi(quality, new()).ResetTemporalHistory);
    }

    [Theory]
    [InlineData(RayGiDenoiser.Off)]
    [InlineData(RayGiDenoiser.Spatial)]
    public void ExistingOffAndSpatialOptionsKeepTheirIdentityAndFields(RayGiDenoiser denoiser)
    {
        var options = Options with { Denoiser = denoiser };
        Assert.Same(options, D3D12ViewQuality.Gi(new(Gi: options),
            new() { HistoryGeneration = 42, CameraCutKey = 9, ResetHistory = true }));
        Assert.Same(RayGiOptions.Off, D3D12ViewQuality.Gi(new(), new() { ResetHistory = true }));
    }

    [Fact]
    public void CaptureClockResetsOnlyDiscontinuousCommittedSamplesAndKeepsOwnersIndependent()
    {
        var quality = new ViewQualityOptions(Gi: Options);
        var plan = new OfflineFramePlan(96, 72, new(30000, 1001), 10, 20);
        var capture = new D3D12ViewTemporalClock();
        var freshExport = new D3D12ViewTemporalClock();
        RayGiOptions Resolve(int index, ViewTemporalFrameState? state = null)
            => D3D12ViewQuality.Gi(quality, capture.Resolve(plan.GetSample(index), state));
        Assert.False(Resolve(0).ResetTemporalHistory);
        capture.Commit(plan.GetSample(0));
        Assert.False(Resolve(1).ResetTemporalHistory);
        Assert.False(Resolve(1).ResetTemporalHistory); // resolving an unsuccessful attempt cannot consume history
        var marked = Resolve(1, new() { HistoryGeneration = 7, CameraCutKey = 3 });
        Assert.Equal(7, marked.TemporalHistoryGeneration);
        Assert.Equal(3, marked.TemporalCameraCutKey);
        capture.Commit(plan.GetSample(1));
        Assert.True(Resolve(1).ResetTemporalHistory);
        Assert.True(Resolve(0).ResetTemporalHistory);
        Assert.True(Resolve(3).ResetTemporalHistory);
        var jumped = plan.GetSample(2) with { RequestedSourceFrame = new(24, 1) };
        Assert.True(D3D12ViewQuality.Gi(quality, capture.Resolve(jumped)).ResetTemporalHistory);
        Assert.False(D3D12ViewQuality.Gi(quality, freshExport.Resolve(plan.GetSample(2))).ResetTemporalHistory);
    }
}

[Trait("Category", "Gpu")]
public sealed class ViewGiTemporalAdapterGpuTests
{
    private const int Width = 96, Height = 72;

    [Theory, InlineData(1), InlineData(2)]
    public async Task PublicCaptureAccumulatesWithAaOffAndResetsSeekCutSkipAndRepeatToFreshSeed(int samples)
    {
        var request = Request(samples);
        var scene = Scene();
        var plan = new OfflineFramePlan(Width, Height, new(30), 10, 30);
        OfflineFrame Frame(int index) => new(plan.GetSample(index), Width, Height, scene, null, null, null);
        await using var renderer = new D3D12ViewRenderer(request);
        await using var independent = new D3D12ViewRenderer(request);
        var token = TestContext.Current.CancellationToken;
        async Task<byte[]> Capture(D3D12ViewRenderer owner, int index, ViewTemporalFrameState? state = null)
        {
            using var pixels = await owner.CaptureAsync(Frame(index), state ?? new(), token);
            Assert.Null(owner.Statistics!.TemporalUpscaling);
            Assert.Equal(1, owner.Statistics.ReadbackCount);
            Assert.Equal(1, owner.Statistics.SubmissionCount);
            Assert.Equal(samples, owner.Statistics.SpatialSamples);
            Assert.Equal(samples, owner.RayGiStatistics!.DispatchCount);
            Assert.Equal(2 * samples, owner.RayGiStatistics.DenoiseDispatchCount);
            Assert.Equal((long)Width * Height * 64 * samples, owner.RayGiStatistics.TemporalHistoryBytes);
            Assert.Equal(RayGiDenoiser.TemporalSpatial, owner.RayGiStatistics!.Denoiser);
            return pixels.Pixels.ToArray();
        }

        var first = await Capture(renderer, 0);
        Assert.False(renderer.IsWarp);
        AssertReset(renderer.RayGiStatistics!, "FirstFrame");
        var second = await Capture(renderer, 1);
        AssertAccumulated(renderer.RayGiStatistics!, 2);
        Assert.False(first.AsSpan().SequenceEqual(second), "Independent temporal samples must affect visible GI.");
        var isolatedFirst = await Capture(independent, 0);
        Assert.Equal(first, isolatedFirst);
        AssertReset(independent.RayGiStatistics!, "FirstFrame");
        Assert.Equal(second, await Capture(independent, 1));
        AssertAccumulated(independent.RayGiStatistics!, 2);

        var generation = new ViewTemporalFrameState { HistoryGeneration = 5 };
        Assert.Equal(first, await Capture(renderer, 2, generation));
        AssertReset(renderer.RayGiStatistics!, "GenerationChanged");
        await Capture(renderer, 3, generation);
        AssertAccumulated(renderer.RayGiStatistics!, 2);
        var cameraCut = generation with { CameraCutKey = 7 };
        Assert.Equal(first, await Capture(renderer, 4, cameraCut));
        AssertReset(renderer.RayGiStatistics!, "CameraCut");
        Assert.Equal(first, await Capture(renderer, 5, cameraCut with { ResetHistory = true }));
        AssertReset(renderer.RayGiStatistics!, "ExplicitReset");
        await Capture(renderer, 6, cameraCut);
        AssertAccumulated(renderer.RayGiStatistics!, 2);
        Assert.Equal(first, await Capture(renderer, 8, cameraCut));
        AssertReset(renderer.RayGiStatistics!, "ExplicitReset");
        Assert.Equal(first, await Capture(renderer, 8, cameraCut));
        AssertReset(renderer.RayGiStatistics!, "ExplicitReset");
        Assert.Equal(first, await Capture(renderer, 0, cameraCut));
        AssertReset(renderer.RayGiStatistics!, "ExplicitReset");
        Assert.Equal(second, await Capture(renderer, 1, cameraCut));
        AssertAccumulated(renderer.RayGiStatistics!, 2);
    }

    [Theory, InlineData(1), InlineData(2)]
    public async Task PublicRealtimeAccumulatesPausedPoseWithoutReadbackAndExportStartsIndependent(int samples)
    {
        nint window = CreateWindowExW(0, "STATIC", "GI temporal view adapter test", 0x80000000,
            0, 0, 120, 90, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            var request = Request(samples);
            var scene = Scene();
            var frame = new PreviewFrame(Width, Height, 12, scene, null, null, null);
            var token = TestContext.Current.CancellationToken;
            await using var preview = new D3D12RealtimeViewRenderer(window, request);
            await preview.PresentAsync(frame, token);
            AssertReset(preview.Statistics.RayGi!, "FirstFrame");
            Assert.Equal(samples, Assert.IsType<D3D12FrameStatistics>(preview.Statistics.Frame).SpatialSamples);
            Assert.Equal(samples, preview.Statistics.RayGi!.DispatchCount);
            Assert.Equal(2 * samples, preview.Statistics.RayGi.DenoiseDispatchCount);
            await preview.PresentAsync(frame, token);
            AssertAccumulated(preview.Statistics.RayGi!, 2);

            var plan = new OfflineFramePlan(Width, Height, new(30), 12, 13);
            await using var export = new D3D12ViewRenderer(request);
            using (await export.CaptureAsync(new(plan.GetSample(0), Width, Height, scene, null, null, null), token)) { }
            AssertReset(export.RayGiStatistics!, "FirstFrame");
            await preview.PresentAsync(frame, token);
            AssertAccumulated(preview.Statistics.RayGi!, 3);
            await preview.PresentAsync(frame with { TemporalState = new() { HistoryGeneration = 2 } }, token);
            AssertReset(preview.Statistics.RayGi!, "GenerationChanged");
            await preview.PresentAsync(frame with { TemporalState = new() { HistoryGeneration = 2, CameraCutKey = 3 } }, token);
            AssertReset(preview.Statistics.RayGi!, "CameraCut");
            Assert.False(preview.IsWarp);
            Assert.Equal(5, preview.Statistics.PresentCount);
            Assert.Equal(0, preview.Statistics.ExplicitCaptureCount);
            Assert.Equal(0, preview.Statistics.Frame!.TotalReadbacks);
            Assert.Null(preview.Statistics.Frame.TemporalUpscaling);
        }
        finally { DestroyWindow(window); }
    }

    private static ViewRenderRequest Request(int samples) => new(Width, Height,
        new(Gi: ViewGiTemporalAdapterTests.Options, Antialiasing: FrameAntialiasing.Off, SpatialSamples: samples));

    private static OriginalFrameScene Scene()
    {
        var source = D3D12RayGiTests.Bounce();
        return new(new(source.Surface.Snapshot,
            source.Surface.Materials.Select((material, index) => index == 0 ? material with { ToonShadowColor = new(.2f) } : material),
            source.Surface.Profile), source.Outline, source.OutlineOptions, source.Options, source.Materials);
    }

    private static void AssertReset(D3D12RayGiStatistics statistics, string reason)
    {
        Assert.True(statistics.TemporalResetHistory);
        Assert.Equal(reason, statistics.TemporalResetReason);
        Assert.Equal(1UL, statistics.TemporalHistoryFrames);
        Assert.Equal(ViewGiTemporalAdapterTests.Options.Seed, statistics.Seed);
    }

    private static void AssertAccumulated(D3D12RayGiStatistics statistics, ulong frames)
    {
        Assert.False(statistics.TemporalResetHistory);
        Assert.Equal("None", statistics.TemporalResetReason);
        Assert.Equal(frames, statistics.TemporalHistoryFrames);
        Assert.Equal(unchecked(ViewGiTemporalAdapterTests.Options.Seed + (uint)(frames - 1) * 0x9e3779b9u), statistics.Seed);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extended, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
