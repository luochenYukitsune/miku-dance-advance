using Mmd.Modern.Application;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class ViewTemporalAdapterTests
{
    [Fact]
    public void DetailFirstTemporalDefaultsStayOffUntilSelectedAndMapNativeAaForBothProviders()
    {
        var backend = new D3D12TemporalUpscalingOptions();
        Assert.Equal(D3D12TemporalUpscalingMode.Off, backend.Mode);
        Assert.Equal(D3D12FsrQualityPreset.NativeAa, backend.QualityPreset);
        Assert.Equal(D3D12XeSsQuality.AntiAliasing, backend.XeSsQuality);
        Assert.Equal(new D3D12SpatialUpscalingDimensions(1920, 1080, 1920, 1080),
            (backend with { Mode = D3D12TemporalUpscalingMode.Fsr }).GetDimensions(1920, 1080));
        var fsr = D3D12ViewQuality.Temporal(new(TemporalUpscaling: new() { Mode = ViewTemporalUpscalingMode.Fsr }));
        Assert.Equal(D3D12FsrQualityPreset.NativeAa, fsr.QualityPreset);
        Assert.Equal(new D3D12SpatialUpscalingDimensions(1920, 1080, 1920, 1080), fsr.GetDimensions(1920, 1080));
        var xeSs = D3D12ViewQuality.Temporal(new(TemporalUpscaling: new() { Mode = ViewTemporalUpscalingMode.XeSs }));
        Assert.Equal(D3D12XeSsQuality.AntiAliasing, xeSs.XeSsQuality);
    }

    [Theory]
    [InlineData(ViewTemporalQualityPreset.NativeAa, D3D12FsrQualityPreset.NativeAa, 1920, 1080)]
    [InlineData(ViewTemporalQualityPreset.Quality, D3D12FsrQualityPreset.Quality, 1280, 720)]
    [InlineData(ViewTemporalQualityPreset.Balanced, D3D12FsrQualityPreset.Balanced, 1129, 635)]
    [InlineData(ViewTemporalQualityPreset.Performance, D3D12FsrQualityPreset.Performance, 960, 540)]
    [InlineData(ViewTemporalQualityPreset.UltraPerformance, D3D12FsrQualityPreset.UltraPerformance, 640, 360)]
    public void MappingPreservesProviderPresetTimingAndDiscontinuity(ViewTemporalQualityPreset neutral,
        D3D12FsrQualityPreset expected, int width, int height)
    {
        var result = D3D12ViewQuality.Temporal(new(TemporalUpscaling: new()
            { Mode = ViewTemporalUpscalingMode.Fsr, QualityPreset = neutral }),
            new() { FrameTimeMilliseconds = 40, ResetHistory = true, HistoryGeneration = 42, CameraCutKey = 9 },
            "D:/sdk/amd_fidelityfx_loader_dx12.dll");
        Assert.Equal(D3D12TemporalUpscalingMode.Fsr, result.Mode); Assert.Equal(expected, result.QualityPreset);
        Assert.Equal(new D3D12SpatialUpscalingDimensions(width, height, 1920, 1080), result.GetDimensions(1920, 1080));
        Assert.Equal(40, result.FrameTimeMilliseconds); Assert.True(result.ResetHistory);
        Assert.Equal(42, result.HistoryGeneration); Assert.Equal(9, result.CameraCutKey);
        Assert.Equal("D:/sdk/amd_fidelityfx_loader_dx12.dll", result.LoaderPath);
    }

    [Fact]
    public void OfflineClocksAreIndependentAndDoNotAdvanceUntilCommitted()
    {
        var plan = new OfflineFramePlan(96, 72, new(30000, 1001), 0, 30);
        var first = plan.GetSample(0); var second = plan.GetSample(1);
        var previewClock = new D3D12ViewTemporalClock(); var exportClock = new D3D12ViewTemporalClock();
        Assert.Null(exportClock.Resolve(first).FrameTimeMilliseconds);
        exportClock.Commit(first);
        Assert.InRange(exportClock.Resolve(second).FrameTimeMilliseconds!.Value, 33.3666f, 33.3668f);
        Assert.False(exportClock.Resolve(second).ResetHistory);
        Assert.False(exportClock.Resolve(second).ResetHistory); // a failed/unsubmitted attempt did not consume the sample
        Assert.Null(previewClock.Resolve(second).FrameTimeMilliseconds);
        exportClock.Commit(second);
        Assert.True(exportClock.Resolve(second).ResetHistory); // an explicitly repeated capture starts new history
        Assert.True(exportClock.Resolve(first).ResetHistory);
        Assert.True(exportClock.Resolve(plan.GetSample(4)).ResetHistory);
        var jumped = plan.GetSample(2) with { RequestedSourceFrame = new(24, 1) };
        Assert.True(exportClock.Resolve(jumped).ResetHistory);
        var explicitCut = exportClock.Resolve(plan.GetSample(2), new() { CameraCutKey = 11, HistoryGeneration = 27 });
        Assert.Equal(11, explicitCut.CameraCutKey); Assert.Equal(27, explicitCut.HistoryGeneration);
    }
}

[Trait("Category", "Gpu")]
public sealed class ViewTemporalAdapterGpuTests
{
    [Fact]
    public async Task CaptureUsesRealFsrAndResetMarkersWithIndependentHistories()
    {
        string loader = Environment.GetEnvironmentVariable("MMD_FSR_LOADER_PATH")
            ?? throw new InvalidOperationException("Set MMD_FSR_LOADER_PATH for the explicit SDK GPU tests.");
        var request = new ExportRequest(new(96, 72, new(30), 0, 1), new(TemporalUpscaling: new()
            { Mode = ViewTemporalUpscalingMode.Fsr, QualityPreset = ViewTemporalQualityPreset.Quality })).Render;
        await using var first = new D3D12ViewRenderer(request, loader);
        await using var second = new D3D12ViewRenderer(request, loader);
        var scene = D3D12CharacterFsrSessionTests.Character();
        var plan = new OfflineFramePlan(96, 72, new(30), 0, 1);
        OfflineFrame Frame(int index) => new(plan.GetSample(index), 96, 72, scene, null, null, null);
        using (await first.CaptureAsync(Frame(0), TestContext.Current.CancellationToken)) { }
        Assert.True(first.Statistics!.TemporalUpscaling!.ResetHistory);
        using (await first.CaptureAsync(Frame(1), TestContext.Current.CancellationToken)) { }
        Assert.False(first.Statistics!.TemporalUpscaling!.ResetHistory);
        Assert.Equal(64, first.Statistics.TemporalUpscaling.RenderWidth);
        using (await second.CaptureAsync(Frame(0), TestContext.Current.CancellationToken)) { }
        Assert.True(second.Statistics!.TemporalUpscaling!.ResetHistory);
        using (await first.CaptureAsync(Frame(0), TestContext.Current.CancellationToken)) { }
        Assert.True(first.Statistics!.TemporalUpscaling!.ResetHistory);
        using (await first.CaptureAsync(Frame(1), new() { CameraCutKey = 3 }, TestContext.Current.CancellationToken)) { }
        Assert.Equal("CameraCut", first.Statistics!.TemporalUpscaling!.ResetReason);
    }
}
