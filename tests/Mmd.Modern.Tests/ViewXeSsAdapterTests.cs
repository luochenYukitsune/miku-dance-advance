using System.Runtime.InteropServices;
using Mmd.Modern.Application;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class ViewXeSsAdapterTests
{
    [Theory]
    [InlineData(ViewXeSsQuality.UltraPerformance, D3D12XeSsQuality.UltraPerformance)]
    [InlineData(ViewXeSsQuality.Performance, D3D12XeSsQuality.Performance)]
    [InlineData(ViewXeSsQuality.Balanced, D3D12XeSsQuality.Balanced)]
    [InlineData(ViewXeSsQuality.Quality, D3D12XeSsQuality.Quality)]
    [InlineData(ViewXeSsQuality.UltraQuality, D3D12XeSsQuality.UltraQuality)]
    [InlineData(ViewXeSsQuality.UltraQualityPlus, D3D12XeSsQuality.UltraQualityPlus)]
    [InlineData(ViewXeSsQuality.NativeAa, D3D12XeSsQuality.AntiAliasing)]
    public void OfficialQualityMappingUsesSeparateProviderPathsAndNoPredictedResolution(ViewXeSsQuality quality,
        D3D12XeSsQuality expected)
    {
        var request = new ViewQualityOptions(TemporalUpscaling: new()
            { Mode = ViewTemporalUpscalingMode.XeSs, XeSsQuality = quality });
        var state = new ViewTemporalFrameState { FrameTimeMilliseconds = 20, HistoryGeneration = 8, CameraCutKey = 13 };
        var mapped = D3D12ViewQuality.Temporal(request, state, "D:/amd/amd_fidelityfx_loader_dx12.dll", "D:/intel/libxess.dll");
        Assert.Equal(D3D12TemporalUpscalingMode.XeSs, mapped.Mode); Assert.Equal(expected, mapped.XeSsQuality);
        Assert.Equal("D:/intel/libxess.dll", mapped.LoaderPath);
        Assert.Equal(20, mapped.FrameTimeMilliseconds); Assert.Equal(8, mapped.HistoryGeneration); Assert.Equal(13, mapped.CameraCutKey);
        Assert.Throws<InvalidOperationException>(() => mapped.GetDimensions(1920, 1080));
        Assert.Null(D3D12ViewQuality.Temporal(request, state, "D:/amd/amd_fidelityfx_loader_dx12.dll").LoaderPath);
        var fsr = request with { TemporalUpscaling = new() { Mode = ViewTemporalUpscalingMode.Fsr } };
        Assert.Equal("D:/amd/amd_fidelityfx_loader_dx12.dll",
            D3D12ViewQuality.Temporal(fsr, state, "D:/amd/amd_fidelityfx_loader_dx12.dll", "D:/intel/libxess.dll").LoaderPath);
    }
}

[Trait("Category", "Gpu")]
public sealed class ViewXeSsAdapterGpuTests
{
    private static string Library => Environment.GetEnvironmentVariable("MMD_XESS_RUNTIME_PATH")
        ?? throw new InvalidOperationException("Set MMD_XESS_RUNTIME_PATH for the actual XeSS adapter GPU tests.");

    [Theory]
    [InlineData(ViewXeSsQuality.Quality)]
    [InlineData(ViewXeSsQuality.NativeAa)]
    public async Task CaptureSelectsActualXeSsAndKeepsIndependentExportHistory(ViewXeSsQuality quality)
    {
        var request = new ExportRequest(new(96, 72, new(30), 0, 1), new(TemporalUpscaling: new()
            { Mode = ViewTemporalUpscalingMode.XeSs, XeSsQuality = quality })).Render;
        // A configured AMD path belongs only to FSR, including when both optional providers are installed.
        await using var first = new D3D12ViewRenderer(request, "D:/unused/amd_fidelityfx_loader_dx12.dll", Library);
        await using var second = new D3D12ViewRenderer(request, xessLibraryPath: Library);
        var scene = D3D12CharacterFsrSessionTests.Character();
        var plan = new OfflineFramePlan(96, 72, new(30), 0, 1);
        OfflineFrame Frame(int index) => new(plan.GetSample(index), 96, 72, scene, null, null, null);
        using (var pixels = await first.CaptureAsync(Frame(0), TestContext.Current.CancellationToken))
        { Assert.Equal(96 * 72 * 4, pixels.Pixels.Length); }
        Assert.False(first.IsWarp);
        var initial = first.Statistics!.TemporalUpscaling!;
        Assert.True(initial.ResetHistory); Assert.Equal(D3D12TemporalUpscalingMode.XeSs, initial.Mode);
        Assert.True(initial.XeSsDeviceQuery!.Initialized);
        Assert.Equal(initial.XeSsDeviceQuery.InputWidth, initial.RenderWidth);
        if (quality == ViewXeSsQuality.NativeAa) { Assert.Equal(96, initial.RenderWidth); Assert.Equal(72, initial.RenderHeight); }
        using (await first.CaptureAsync(Frame(1), TestContext.Current.CancellationToken)) { }
        Assert.False(first.Statistics!.TemporalUpscaling!.ResetHistory);
        using (await second.CaptureAsync(Frame(0), TestContext.Current.CancellationToken)) { }
        Assert.True(second.Statistics!.TemporalUpscaling!.ResetHistory);
        using (await first.CaptureAsync(Frame(0), TestContext.Current.CancellationToken)) { }
        Assert.True(first.Statistics!.TemporalUpscaling!.ResetHistory);
    }

    [Fact]
    public async Task RealtimeSelectsXeSsAndPresentsWithoutReadback()
    {
        nint window = CreateWindowExW(0, "STATIC", "XeSS view adapter test", 0x80000000, 0, 0, 120, 90, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            var request = new ViewRenderRequest(96, 72, new(TemporalUpscaling: new()
                { Mode = ViewTemporalUpscalingMode.XeSs }));
            await using var renderer = new D3D12RealtimeViewRenderer(window, request, xessLibraryPath: Library);
            var scene = D3D12CharacterFsrSessionTests.Character();
            var frame = new PreviewFrame(96, 72, 0, scene, null, null, null);
            await renderer.PresentAsync(frame, TestContext.Current.CancellationToken);
            await renderer.PresentAsync(frame, TestContext.Current.CancellationToken);
            Assert.False(renderer.IsWarp); Assert.Equal(2, renderer.Statistics.PresentCount);
            Assert.Equal(0, renderer.Statistics.Frame!.TotalReadbacks);
            Assert.Equal(D3D12TemporalUpscalingMode.XeSs, renderer.Statistics.Frame.TemporalUpscaling!.Mode);
            Assert.False(renderer.Statistics.Frame.TemporalUpscaling.ResetHistory);
        }
        finally { DestroyWindow(window); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extended, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
