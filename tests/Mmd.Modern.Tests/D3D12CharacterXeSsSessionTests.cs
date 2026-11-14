using System.Runtime.InteropServices;
using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using System.Text.Json;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12XeSsProductContractTests
{
    [Fact]
    public void XeSsResolutionCannotBeGuessedFromFsrPreset()
    {
        var options = new D3D12TemporalUpscalingOptions { Mode = D3D12TemporalUpscalingMode.XeSs };
        Assert.Throws<InvalidOperationException>(() => options.GetDimensions(1920, 1080));
        options.Validate(1920, 1080);
        Assert.Throws<ArgumentOutOfRangeException>(() => (options with { XeSsQuality = (D3D12XeSsQuality)999 }).Validate(1920, 1080));
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate(0, 1080));
    }
}

[Trait("Category", "Gpu")]
public sealed class D3D12CharacterXeSsSessionTests(ITestOutputHelper output)
{
    private static D3D12TemporalUpscalingOptions Options(D3D12XeSsQuality quality = D3D12XeSsQuality.Quality) => new()
    {
        Mode = D3D12TemporalUpscalingMode.XeSs, XeSsQuality = quality,
        LoaderPath = Environment.GetEnvironmentVariable("MMD_XESS_RUNTIME_PATH")
            ?? throw new InvalidOperationException("Set MMD_XESS_RUNTIME_PATH to the optional official libxess.dll.")
    };

    [Theory]
    [InlineData(D3D12XeSsQuality.Quality)]
    [InlineData(D3D12XeSsQuality.AntiAliasing)]
    public void CompleteToonCharacterUsesQueriedDimensionsAndPreservesNativeOff(D3D12XeSsQuality quality)
    {
        var frame = D3D12CharacterFsrSessionTests.Character();
        using var session = new D3D12FrameSession();
        using var original = new D3D12OriginalStyleRenderer();
        var baseline = original.Render(frame, 96, 72);
        Assert.Equal(baseline.Rgba, session.Capture(frame, 96, 72).Rgba);
        var options = Options(quality);
        for (int i = 0; i < 8; i++)
        {
            using var gpu = session.Render(frame, 96, 72, temporalUpscaling: options);
            Assert.Equal(0, gpu.Statistics.ReadbackCount);
            Assert.Equal(i == 0, gpu.Statistics.TemporalUpscaling!.ResetHistory);
        }
        var captured = session.Capture(frame, 96, 72, temporalUpscaling: options);
        var stats = session.Statistics!.TemporalUpscaling!;
        Assert.Equal(D3D12TemporalUpscalingMode.XeSs, stats.Mode);
        Assert.StartsWith("XeSS ", stats.ProviderName);
        Assert.Equal(0ul, stats.ProviderId); // XeSS has no AMD provider identifier.
        Assert.NotNull(stats.XeSsDeviceQuery);
        Assert.True(stats.XeSsDeviceQuery.Initialized);
        Assert.Equal(0, stats.XeSsDeviceQuery.InitializeCode);
        Assert.Equal(stats.XeSsDeviceQuery.InputWidth, stats.RenderWidth);
        Assert.Equal(stats.XeSsDeviceQuery.InputHeight, stats.RenderHeight);
        Assert.False(string.IsNullOrWhiteSpace(stats.XeSsDeviceQuery.RuntimeVersion));
        if (quality == D3D12XeSsQuality.AntiAliasing)
        { Assert.Equal(96, stats.RenderWidth); Assert.Equal(72, stats.RenderHeight); }
        else Assert.InRange(stats.RenderWidth, 1, 95);
        Assert.Contains(session.LastRecordings, item => item.Channel == "xess:temporal");
        Assert.DoesNotContain(session.LastRecordings, item => item.Channel == "fsr:temporal");
        Assert.Equal(96 * 72 * 4, captured.Rgba.Length);
        int inside = (40 * 96 + 36) * 4;
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(Math.Abs(captured.Rgba[inside + channel] - baseline.Rgba[inside + channel]), 0, 10);
        Assert.Equal(baseline.Rgba, session.Capture(frame, 96, 72).Rgba);
        Assert.Null(session.Statistics!.TemporalUpscaling);
        output.WriteLine(JsonSerializer.Serialize(stats));
    }

    [Fact]
    public void VendorSwitchAbortSeekAndResizeResetOnlySubmittedHistory()
    {
        using var session = new D3D12FrameSession();
        var frame = D3D12CharacterFsrSessionTests.Character(); var options = Options();
        D3D12TemporalUpscalingStatistics Render(D3D12TemporalUpscalingOptions value, int width = 96, int height = 72)
        { using var result = session.Render(frame, width, height, temporalUpscaling: value); return result.Statistics.TemporalUpscaling!; }
        Assert.True(Render(options).ResetHistory); Assert.False(Render(options).ResetHistory);
        var fsr = options with { Mode = D3D12TemporalUpscalingMode.Fsr, QualityPreset = D3D12FsrQualityPreset.Quality,
            LoaderPath = Environment.GetEnvironmentVariable("MMD_FSR_LOADER_PATH")
                ?? throw new InvalidOperationException("Set MMD_FSR_LOADER_PATH for the vendor-switch test.") };
        var switched = Render(fsr); Assert.Equal(D3D12TemporalUpscalingMode.Fsr, switched.Mode);
        Assert.Equal("ModeChanged", switched.ResetReason);
        Assert.Equal("ModeChanged", Render(options).ResetReason);
        options = options with { HistoryGeneration = 1 };
        Assert.Equal("SeekOrRestart", Render(options).ResetReason);
        options = options with { CameraCutKey = 1 };
        Assert.Equal("CameraCut", Render(options).ResetReason);
        Assert.Equal("ConfigurationChanged", Render(options, 120, 90).ResetReason);
        long before = session.Statistics!.TemporalUpscaling!.SubmittedFrames;
        Assert.Throws<InvalidOperationException>(() => session.RenderForPresent(frame,120,90,null,
            (_,_) => throw new InvalidOperationException("Injected after XeSS execute"), temporalUpscaling: options));
        Assert.False(session.Runtime.IsRecording);
        var recovered = Render(options, 120, 90);
        Assert.Equal("AbortedFrame", recovered.ResetReason);
        Assert.Equal(before + 1, recovered.SubmittedFrames);
        for (int i = 0; i < 5; i++) Render(options, 120, 90);
        var warm = session.Statistics!;
        for (int i = 0; i < 24; i++) Render(options, 120, 90);
        var last = session.Statistics!;
        Assert.Equal(warm.LiveFrameResources, last.LiveFrameResources);
        Assert.Equal(warm.CachedResources, last.CachedResources);
        Assert.Equal(warm.ShaderCompilations, last.ShaderCompilations);
        Assert.Equal(warm.PipelineCreations, last.PipelineCreations);
        Assert.Equal(warm.TotalReadbacks, last.TotalReadbacks);
        output.WriteLine(JsonSerializer.Serialize(new { warm, last }));
    }

    [Fact]
    public async Task ContinuousViewCameraOrbitRetainsHistoryDespiteChangingSphereRotation()
    {
        string loader = Environment.GetEnvironmentVariable("MMD_FSR_LOADER_PATH")
            ?? throw new InvalidOperationException("Set MMD_FSR_LOADER_PATH for the camera-orbit regression.");
        var quality = new ViewQualityOptions(TemporalUpscaling: new() { Mode = ViewTemporalUpscalingMode.Fsr, QualityPreset = ViewTemporalQualityPreset.Quality });
        var request = new ExportRequest(new(96, 72, new(30), 0, 2), quality).Render;
        await using var renderer = new D3D12ViewRenderer(request, loader);
        var scene = D3D12CharacterFsrSessionTests.Character();
        var plan = new OfflineFramePlan(96, 72, new(30), 0, 2);
        PmmCameraPose Pose(int index) => new(Vector3.Zero, new(0, index * .08f, 0), -5, 45, true);
        OfflineFrame Frame(int index) => new(plan.GetSample(index), 96, 72, scene, Pose(index), null, null);
        var firstScene = D3D12ViewScene.Resolve(scene, Pose(0), null, null, Frame(0).MorphChannels, quality);
        var secondScene = D3D12ViewScene.Resolve(scene, Pose(1), null, null, Frame(1).MorphChannels, quality);
        Assert.NotEqual(firstScene.Options!.SphereRotationOverride, secondScene.Options!.SphereRotationOverride);
        using (await renderer.CaptureAsync(Frame(0), TestContext.Current.CancellationToken)) { }
        var first = renderer.Statistics!.TemporalUpscaling!;
        Assert.True(first.ResetHistory); Assert.Equal(0, first.JitterPhase);
        using (await renderer.CaptureAsync(Frame(1), TestContext.Current.CancellationToken)) { }
        var second = renderer.Statistics!.TemporalUpscaling!;
        Assert.False(second.ResetHistory); Assert.Equal("None", second.ResetReason);
        Assert.Equal(first.JitterPhase + 1, second.JitterPhase);
        using (await renderer.CaptureAsync(Frame(2), new() { CameraCutKey = 1 }, TestContext.Current.CancellationToken)) { }
        Assert.Equal("CameraCut", renderer.Statistics!.TemporalUpscaling!.ResetReason);
        Assert.Equal(0, renderer.Statistics.TemporalUpscaling.JitterPhase);
    }

    [Fact]
    public void XeSsSwapchainPresentsResizesAndCapturesOnlyExplicitly()
    {
        nint window = CreateWindowExW(0, "STATIC", "MMD XeSS test", 0x80000000, 0, 0, 128, 96, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            using var presenter = new D3D12SwapchainPresenter(window, 96, 72);
            var frame = D3D12CharacterFsrSessionTests.Character(); var options = Options();
            for (int i = 0; i < 4; i++) Assert.True(presenter.Present(frame, 96, 72, temporalUpscaling: options));
            Assert.Equal(D3D12TemporalUpscalingMode.XeSs, presenter.TemporalUpscalingStatistics!.Mode);
            Assert.False(presenter.TemporalUpscalingStatistics.ResetHistory);
            Assert.Equal(0, presenter.Statistics.Frame!.TotalReadbacks);
            Assert.True(presenter.Present(frame, 120, 90, temporalUpscaling: options));
            Assert.True(presenter.TemporalUpscalingStatistics!.ResetHistory);
            Assert.Equal(1, presenter.Statistics.ResizeCount);
            Assert.Equal(0, presenter.Statistics.Frame!.TotalReadbacks);
            Assert.Equal(120, presenter.Capture(frame, temporalUpscaling: options).Width);
            Assert.Equal(1, presenter.Statistics.ExplicitCaptureCount);
        }
        finally { DestroyWindow(window); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x,int y,int width,int height,nint parent,nint menu,nint instance,nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
}
