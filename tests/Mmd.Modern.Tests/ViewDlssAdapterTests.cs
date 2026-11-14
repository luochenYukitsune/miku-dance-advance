using System.Runtime.InteropServices;
using Mmd.Modern.Application;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class ViewDlssAdapterTests
{
    [Theory]
    [InlineData(ViewDlssQuality.Performance, D3D12DlssQuality.Performance)]
    [InlineData(ViewDlssQuality.Balanced, D3D12DlssQuality.Balanced)]
    [InlineData(ViewDlssQuality.Quality, D3D12DlssQuality.Quality)]
    [InlineData(ViewDlssQuality.UltraPerformance, D3D12DlssQuality.UltraPerformance)]
    [InlineData(ViewDlssQuality.UltraQuality, D3D12DlssQuality.UltraQuality)]
    [InlineData(ViewDlssQuality.NativeAa, D3D12DlssQuality.NativeAa)]
    public void QualityAndPathsAreMappedIndependentlyWithNoPredictedDimensions(ViewDlssQuality quality, D3D12DlssQuality expected)
    {
        var request = new ViewQualityOptions(TemporalUpscaling: new() { Mode = ViewTemporalUpscalingMode.Dlss, DlssQuality = quality });
        var state = new ViewTemporalFrameState { FrameTimeMilliseconds = 20, HistoryGeneration = 8, CameraCutKey = 13 };
        var mapped = D3D12ViewQuality.Temporal(request, state, "D:/amd/amd_fidelityfx_loader_dx12.dll", "D:/intel/libxess.dll",
            "D:/nvidia/mmd_dlss.dll", "D:/nvidia/sdk");
        Assert.Equal(D3D12TemporalUpscalingMode.Dlss, mapped.Mode); Assert.Equal(expected, mapped.DlssQuality);
        Assert.Equal("D:/nvidia/mmd_dlss.dll", mapped.LoaderPath); Assert.Equal("D:/nvidia/sdk", mapped.DlssSdkDirectory);
        Assert.Equal(20, mapped.FrameTimeMilliseconds); Assert.Equal(8, mapped.HistoryGeneration); Assert.Equal(13, mapped.CameraCutKey);
        Assert.Throws<InvalidOperationException>(() => mapped.GetDimensions(1920, 1080));
        var unspecified = D3D12ViewQuality.Temporal(request, state, "D:/amd/amd_fidelityfx_loader_dx12.dll", "D:/intel/libxess.dll");
        Assert.Null(unspecified.LoaderPath); Assert.Null(unspecified.DlssSdkDirectory);
    }
}

[Trait("Category", "Gpu")]
public sealed class ViewDlssAdapterGpuTests
{
    [Fact]
    public async Task ExplicitCaptureUsesDlssAndKeepsIndependentViewHistory()
    {
        var options = D3D12CharacterDlssSessionTests.Options();
        var request = new ExportRequest(new(192, 144, new(30), 0, 1), new(TemporalUpscaling: new()
            { Mode = ViewTemporalUpscalingMode.Dlss })).Render;
        await using var first = new D3D12ViewRenderer(request, "D:/unused/amd.dll", "D:/unused/intel.dll", options.LoaderPath, options.DlssSdkDirectory);
        await using var second = new D3D12ViewRenderer(request, dlssBridgePath: options.LoaderPath, dlssSdkDirectory: options.DlssSdkDirectory);
        var scene = D3D12CharacterFsrSessionTests.Character(); var plan = new OfflineFramePlan(192, 144, new(30), 0, 1);
        OfflineFrame Frame(int index) => new(plan.GetSample(index), 192, 144, scene, null, null, null);
        using (var pixels = await first.CaptureAsync(Frame(0), TestContext.Current.CancellationToken))
            Assert.Equal(192 * 144 * 4, pixels.Pixels.Length);
        var initial = first.Statistics!.TemporalUpscaling!;
        Assert.Equal(D3D12TemporalUpscalingMode.Dlss, initial.Mode); Assert.True(initial.ResetHistory);
        Assert.Equal(D3D12DlssQuality.NativeAa, initial.DlssDeviceQuery!.Quality); Assert.True(initial.DlssDeviceQuery.Initialized);
        Assert.Equal(initial.DlssDeviceQuery.InputWidth, initial.RenderWidth); Assert.Equal(initial.DlssDeviceQuery.InputHeight, initial.RenderHeight);
        using (await first.CaptureAsync(Frame(1), TestContext.Current.CancellationToken)) { }
        Assert.False(first.Statistics!.TemporalUpscaling!.ResetHistory);
        using (await second.CaptureAsync(Frame(0), TestContext.Current.CancellationToken)) { }
        Assert.True(second.Statistics!.TemporalUpscaling!.ResetHistory);
        using (await first.CaptureAsync(Frame(0), TestContext.Current.CancellationToken)) { }
        Assert.Equal("ExplicitRequest", first.Statistics!.TemporalUpscaling!.ResetReason);
    }

    [Fact]
    public async Task RealtimePresentsAndResizesUsingDlssWithoutReadback()
    {
        nint window = CreateWindowExW(0, "STATIC", "DLSS view adapter test", 0x80000000, 0, 0, 240, 180, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            var options = D3D12CharacterDlssSessionTests.Options();
            var request = new ViewRenderRequest(192, 144, new(TemporalUpscaling: new() { Mode = ViewTemporalUpscalingMode.Dlss }));
            await using var renderer = new D3D12RealtimeViewRenderer(window, request,
                dlssBridgePath: options.LoaderPath, dlssSdkDirectory: options.DlssSdkDirectory);
            var scene = D3D12CharacterFsrSessionTests.Character();
            var frame = new PreviewFrame(192, 144, 0, scene, null, null, null);
            await renderer.PresentAsync(frame, TestContext.Current.CancellationToken);
            await renderer.PresentAsync(frame, TestContext.Current.CancellationToken);
            Assert.Equal(2, renderer.Statistics.PresentCount); Assert.Equal(0, renderer.Statistics.Frame!.TotalReadbacks);
            Assert.Equal(D3D12TemporalUpscalingMode.Dlss, renderer.Statistics.Frame.TemporalUpscaling!.Mode);
            Assert.False(renderer.Statistics.Frame.TemporalUpscaling.ResetHistory);
            await renderer.ResizeAsync(240, 180, TestContext.Current.CancellationToken);
            await renderer.PresentAsync(new(240, 180, 0, scene, null, null, null), TestContext.Current.CancellationToken);
            Assert.Equal(1, renderer.Statistics.ResizeCount); Assert.Equal(0, renderer.Statistics.Frame!.TotalReadbacks);
            Assert.Equal("ConfigurationChanged", renderer.Statistics.Frame.TemporalUpscaling!.ResetReason);
        }
        finally { DestroyWindow(window); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extended, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
