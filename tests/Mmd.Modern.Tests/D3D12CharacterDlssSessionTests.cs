using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12DlssProductContractTests
{
    [Fact]
    public void DlssKeepsExistingModeValuesAndRequiresNativeResolutionQuery()
    {
        Assert.Equal(0, (int)D3D12TemporalUpscalingMode.Off);
        Assert.Equal(1, (int)D3D12TemporalUpscalingMode.Fsr);
        Assert.Equal(2, (int)D3D12TemporalUpscalingMode.XeSs);
        Assert.Equal(3, (int)D3D12TemporalUpscalingMode.Dlss);
        var options = new D3D12TemporalUpscalingOptions { Mode = D3D12TemporalUpscalingMode.Dlss };
        Assert.Equal(D3D12DlssQuality.NativeAa, options.DlssQuality);
        Assert.Throws<InvalidOperationException>(() => options.GetDimensions(1920, 1080));
        Assert.Throws<ArgumentOutOfRangeException>(() => (options with { DlssQuality = (D3D12DlssQuality)999 }).Validate(1920, 1080));
    }
}

[Trait("Category", "Gpu")]
public sealed class D3D12CharacterDlssSessionTests(ITestOutputHelper output)
{
    private const int Width = 192, Height = 144;
    internal static D3D12TemporalUpscalingOptions Options(D3D12DlssQuality quality = D3D12DlssQuality.NativeAa) => new()
    {
        Mode = D3D12TemporalUpscalingMode.Dlss, DlssQuality = quality,
        LoaderPath = Environment.GetEnvironmentVariable("MMD_DLSS_BRIDGE_PATH")
            ?? throw new InvalidOperationException("Set MMD_DLSS_BRIDGE_PATH to the separately compiled NGX bridge."),
        DlssSdkDirectory = Environment.GetEnvironmentVariable("MMD_DLSS_SDK_DIRECTORY")
            ?? throw new InvalidOperationException("Set MMD_DLSS_SDK_DIRECTORY to the optional official DLSS component directory.")
    };

    [Theory]
    [InlineData(D3D12DlssQuality.Quality)]
    [InlineData(D3D12DlssQuality.NativeAa)]
    public void FullCharacterUsesActualQueriedDimensionsAndRestoresOffBytes(D3D12DlssQuality quality)
    {
        var frame = D3D12CharacterFsrSessionTests.Character(); var options = Options(quality);
        using var session = new D3D12FrameSession(); using var original = new D3D12OriginalStyleRenderer();
        var baseline = original.Render(frame, Width, Height);
        Assert.Equal(baseline.Rgba, session.Capture(frame, Width, Height).Rgba);
        var query = session.QueryDlssCapabilities(Width, Height, options);
        Assert.True(query.Initialized, query.Failure); Assert.True(query.Available);
        Assert.Equal(quality, query.Quality); Assert.False(query.NeedsUpdatedDriver);
        for (int i = 0; i < 8; i++)
        {
            using var gpu = session.Render(frame, Width, Height, temporalUpscaling: options);
            var temporal = gpu.Statistics.TemporalUpscaling!;
            Assert.Equal(0, gpu.Statistics.ReadbackCount); Assert.Equal(1, gpu.Statistics.SubmissionCount);
            Assert.Equal(i == 0, temporal.ResetHistory); Assert.Equal(i + 1, temporal.SubmittedFrames);
            Assert.Equal(query.InputWidth, temporal.RenderWidth); Assert.Equal(query.InputHeight, temporal.RenderHeight);
        }
        var captured = session.Capture(frame, Width, Height, temporalUpscaling: options);
        var stats = session.TemporalUpscalingStatistics!;
        Assert.Equal(D3D12TemporalUpscalingMode.Dlss, stats.Mode); Assert.StartsWith("DLSS ", stats.ProviderName);
        Assert.Equal(0UL, stats.ProviderId); Assert.Null(stats.XeSsDeviceQuery);
        Assert.True(stats.DlssDeviceQuery!.Initialized); Assert.True(stats.DlssDeviceQuery.Available);
        Assert.Equal(query.ComponentFileVersion, stats.DlssDeviceQuery.ComponentFileVersion);
        Assert.False(D3D12DlssTemporalPass.Failed(stats.DlssDeviceQuery.CreateResult));
        if (quality == D3D12DlssQuality.NativeAa)
        { Assert.Equal(Width, stats.RenderWidth); Assert.Equal(Height, stats.RenderHeight); }
        else
        { Assert.InRange(stats.RenderWidth, 1, Width - 1); Assert.InRange(stats.RenderHeight, 1, Height - 1); }
        Assert.Contains(session.LastRecordings, item => item.Channel == "original-frame");
        Assert.Contains(session.LastRecordings, item => item.Channel == "dlss:temporal");
        Assert.DoesNotContain(session.LastRecordings, item => item.Channel is "fsr:temporal" or "xess:temporal");
        Assert.Equal(Width * Height * 4, captured.Rgba.Length); Assert.Equal(1, session.Statistics!.ReadbackCount);
        // Stable composited background and interior body color exercise the shared linear color bridge.
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(Math.Abs(captured.Rgba[channel] - baseline.Rgba[channel]), 0, 2);
        int inside = (80 * Width + 72) * 4;
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(Math.Abs(captured.Rgba[inside + channel] - baseline.Rgba[inside + channel]), 0, 10);
        Assert.True(captured.Rgba.Where((_, index) => index % 4 == 0).Count(value => value > 60) > 100);
        Assert.Equal(baseline.Rgba, session.Capture(frame, Width, Height).Rgba);
        Assert.Null(session.TemporalUpscalingStatistics);
        using var reenabled = session.Render(frame, Width, Height, temporalUpscaling: options);
        Assert.Equal("ModeChanged", reenabled.Statistics.TemporalUpscaling!.ResetReason);
        output.WriteLine(JsonSerializer.Serialize(stats));
        D3D12OriginalFrameTests.Save("character-dlss-" + quality, captured);
    }

    [Fact]
    public void ResetResizeQualityVendorSwitchAndAbortRecreateOnlySubmittedHistory()
    {
        using var session = new D3D12FrameSession(); var frame = D3D12CharacterFsrSessionTests.Character(); var options = Options();
        D3D12TemporalUpscalingStatistics Render(D3D12TemporalUpscalingOptions value, int width = Width, int height = Height)
        { using var result = session.Render(frame, width, height, temporalUpscaling: value); return result.Statistics.TemporalUpscaling!; }
        Assert.True(Render(options).ResetHistory); Assert.False(Render(options).ResetHistory);
        options = options with { HistoryGeneration = 1 }; Assert.Equal("SeekOrRestart", Render(options).ResetReason);
        options = options with { CameraCutKey = 1 }; Assert.Equal("CameraCut", Render(options).ResetReason);
        Assert.Equal("ExplicitRequest", Render(options with { ResetHistory = true }).ResetReason);
        Assert.Equal("ConfigurationChanged", Render(options, 240, 180).ResetReason);
        options = options with { DlssQuality = D3D12DlssQuality.Quality };
        Assert.Equal("ConfigurationChanged", Render(options, 240, 180).ResetReason);
        long before = session.TemporalUpscalingStatistics!.SubmittedFrames;
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(frame, 240, 180, null,
            (_, _) => throw new OperationCanceledException("Injected after native DLSS evaluate"), temporalUpscaling: options));
        Assert.False(session.Runtime.IsRecording);
        long submissions = session.Runtime.Submissions;
        var recovered = Render(options, 240, 180);
        Assert.True(recovered.ResetHistory); Assert.Equal("AbortedFrame", recovered.ResetReason);
        Assert.Equal(before + 1, recovered.SubmittedFrames);
        // A new NGX feature initialization submission proves native history was recreated after abort.
        Assert.Equal(submissions + 2, session.Runtime.Submissions);
        for (int i = 0; i < 5; i++) Render(options, 240, 180);
        var warm = session.Statistics!;
        for (int i = 0; i < 16; i++) Render(options, 240, 180);
        var last = session.Statistics!;
        Assert.Equal(warm.LiveFrameResources, last.LiveFrameResources); Assert.Equal(warm.CachedResources, last.CachedResources);
        Assert.Equal(warm.ShaderCompilations, last.ShaderCompilations); Assert.Equal(warm.PipelineCreations, last.PipelineCreations);
        Assert.Equal(warm.TotalReadbacks, last.TotalReadbacks);
        var fsr = options with { Mode = D3D12TemporalUpscalingMode.Fsr,
            LoaderPath = Environment.GetEnvironmentVariable("MMD_FSR_LOADER_PATH")
                ?? throw new InvalidOperationException("Set MMD_FSR_LOADER_PATH for the actual vendor switch test.") };
        Assert.Equal("ModeChanged", Render(fsr).ResetReason);
        Assert.Equal("ModeChanged", Render(options).ResetReason);
        output.WriteLine(JsonSerializer.Serialize(new { recovered, warm, last }));
    }

    [Fact]
    public void MissingComponentFailsBeforeMainFrameAndValidConfigurationRecovers()
    {
        using var session = new D3D12FrameSession(); var frame = D3D12CharacterFsrSessionTests.Character(); var options = Options();
        var missing = options with { DlssSdkDirectory = Path.Combine(Path.GetDirectoryName(options.LoaderPath)!, "absent-dlss-component") };
        long frameId = session.Runtime.FrameId, submissions = session.Runtime.Submissions;
        var query = session.QueryDlssCapabilities(Width, Height, missing);
        Assert.False(query.Initialized); Assert.False(query.Available); Assert.Contains("missing", query.Failure!);
        var error = Assert.Throws<FileNotFoundException>(() => session.Render(frame, Width, Height, temporalUpscaling: missing));
        Assert.EndsWith("nvngx_dlss.dll", error.FileName);
        Assert.False(session.Runtime.IsRecording); Assert.Equal(frameId, session.Runtime.FrameId);
        Assert.Equal(submissions, session.Runtime.Submissions); Assert.Null(session.Statistics);
        using (var recovered = session.Render(frame, Width, Height, temporalUpscaling: options))
        { Assert.True(recovered.Statistics.TemporalUpscaling!.ResetHistory); Assert.Equal(0, recovered.Statistics.ReadbackCount); }
        // SDK directory is part of the configuration key; a different directory cannot silently reuse a warm context.
        long submitted = session.TemporalUpscalingStatistics!.SubmittedFrames;
        submissions = session.Runtime.Submissions;
        Assert.Throws<FileNotFoundException>(() => session.Render(frame, Width, Height, temporalUpscaling: missing));
        Assert.Equal(submissions, session.Runtime.Submissions);
        using var retry = session.Render(frame, Width, Height, temporalUpscaling: options);
        Assert.True(retry.Statistics.TemporalUpscaling!.ResetHistory);
        Assert.Equal(submitted + 1, retry.Statistics.TemporalUpscaling.SubmittedFrames);
    }

    [Fact]
    public void UnsupportedCoverageAndStackedSamplingRejectBeforeLoadingOptionalSdk()
    {
        using var session = new D3D12FrameSession(); var frame = D3D12CharacterFsrSessionTests.Character();
        var options = new D3D12TemporalUpscalingOptions { Mode = D3D12TemporalUpscalingMode.Dlss,
            LoaderPath = "D:/absent-dlss/mmd_dlss.dll", DlssSdkDirectory = "D:/absent-dlss" };
        OriginalFrameScene Copy(OriginalFrameOptions value) => new(frame.Surface, frame.Outline, frame.OutlineOptions, value, frame.Materials);
        Assert.Throws<NotSupportedException>(() => session.Render(Copy(frame.Options! with { SpatialSamples = 2 }), Width, Height, temporalUpscaling: options));
        Assert.Throws<NotSupportedException>(() => session.Render(Copy(frame.Options! with { Antialiasing = FrameAntialiasing.Smaa1X }), Width, Height, temporalUpscaling: options));
        Assert.Throws<NotSupportedException>(() => session.Render(Copy(frame.Options! with { BackgroundColor = new(0, 0, 0, 0) }), Width, Height, temporalUpscaling: options));
        Assert.Throws<NotSupportedException>(() => session.Render(Copy(frame.Options! with { AlphaOutput = FrameAlphaOutput.Straight }), Width, Height, temporalUpscaling: options));
        Assert.Throws<NotSupportedException>(() => session.Render(Copy(frame.Options! with { CameraOverride = frame.Surface.Snapshot.Camera with
            { VerticalFovDegrees = null } }), Width, Height, temporalUpscaling: options));
        Assert.Equal(0, session.Runtime.Submissions); Assert.False(session.Runtime.IsRecording);
        using var ordinary = session.Render(frame, Width, Height, temporalUpscaling: options with { Mode = D3D12TemporalUpscalingMode.Off });
        Assert.Null(ordinary.Statistics.TemporalUpscaling);
    }
}
