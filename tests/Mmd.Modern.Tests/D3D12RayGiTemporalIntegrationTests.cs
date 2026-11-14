using System.Numerics;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12RayGiTemporalIntegrationTests(ITestOutputHelper output)
{
    private const int Width = 96, Height = 72;

    [Fact]
    public void ActualDxrTemporalGiAccumulatesAndDisablingItRestoresOriginalBytes()
    {
        using var session = new D3D12FrameSession();
        using var original = new D3D12OriginalStyleRenderer();
        var frame = Frame(); var gi = Gi(LightingMode.HardwareRayTracing);
        Require(session, gi.Mode);
        var baseline = original.Render(frame, Width, Height);
        var first = session.Capture(frame, Width, Height, gi);
        Assert.True(ChangedPixels(baseline, first) > 100);
        AssertGi(session, gi, 1, "FirstFrame");
        Write(session, "dxr-first");
        for (ulong i = 2; i <= 5; i++)
        {
            using var gpu = session.Render(frame, Width, Height, gi);
            Assert.Equal(0, gpu.Statistics.ReadbackCount);
            Assert.Equal(1, gpu.Statistics.SubmissionCount);
            AssertGi(session, gi, i, "None");
        }
        var accumulated = session.Capture(frame, Width, Height, gi);
        AssertGi(session, gi, 6, "None");
        Assert.True(ChangedPixels(first, accumulated) > 0, "Independent ray samples must reach the actual filtered output.");
        Write(session, "dxr-accumulated");
        Assert.Equal(baseline.Rgba, session.Capture(frame, Width, Height, RayGiOptions.Off).Rgba);
        Assert.Null(session.RayGiStatistics);
    }

    [Theory]
    [InlineData(D3D12TemporalUpscalingMode.Fsr, LightingMode.SoftwareRayTracing)]
    [InlineData(D3D12TemporalUpscalingMode.Fsr, LightingMode.HardwareRayTracing)]
    [InlineData(D3D12TemporalUpscalingMode.XeSs, LightingMode.SoftwareRayTracing)]
    [InlineData(D3D12TemporalUpscalingMode.XeSs, LightingMode.HardwareRayTracing)]
    [InlineData(D3D12TemporalUpscalingMode.Dlss, LightingMode.SoftwareRayTracing)]
    [InlineData(D3D12TemporalUpscalingMode.Dlss, LightingMode.HardwareRayTracing)]
    public void ActualNativeAaAndTemporalGiRetainBothHistoriesAndRecoverFromCutsAndSeeks(
        D3D12TemporalUpscalingMode vendor, LightingMode mode)
    {
        using var session = new D3D12FrameSession();
        using var original = new D3D12OriginalStyleRenderer();
        Require(session, mode);
        var frame = Frame(); var temporal = Temporal(vendor); var gi = Gi(mode);
        var baseline = original.Render(frame, Width, Height);
        long readbacks = session.Runtime.Readbacks;
        for (ulong i = 1; i <= 5; i++)
        {
            using var gpu = session.Render(frame, Width, Height, gi, temporalUpscaling: temporal);
            Assert.Equal(0, gpu.Statistics.ReadbackCount);
            Assert.Equal(readbacks, gpu.Statistics.TotalReadbacks);
            Assert.Equal(1, gpu.Statistics.SubmissionCount);
            AssertGi(session, gi, i, i == 1 ? "FirstFrame" : "None");
            var aa = gpu.Statistics.TemporalUpscaling!;
            Assert.Equal(i == 1, aa.ResetHistory);
            Assert.Equal(i == 1 ? "FirstFrame" : "None", aa.ResetReason);
            Assert.Equal((long)i, aa.SubmittedFrames);
            Assert.Equal(Width, aa.RenderWidth); Assert.Equal(Height, aa.RenderHeight);
            Assert.Equal(Width, aa.DisplayWidth); Assert.Equal(Height, aa.DisplayHeight);
            Assert.Equal(vendor, aa.Mode);
            Assert.Contains(session.LastRecordings, p => p.Channel == (vendor switch
                { D3D12TemporalUpscalingMode.Fsr => "fsr:temporal", D3D12TemporalUpscalingMode.XeSs => "xess:temporal", _ => "dlss:temporal" }));
        }
        Write(session, "native-aa-accumulated");
        var reconstructed = session.Capture(frame, Width, Height, gi, temporalUpscaling: temporal);
        Assert.True(ChangedPixels(baseline, reconstructed) > 100, "GI must change pixels reconstructed by the actual SDK.");
        AssertGi(session, gi, 6, "None");

        gi = gi with { TemporalHistoryGeneration = 7 };
        temporal = temporal with { HistoryGeneration = 7 };
        RenderAndAssertReset("GenerationChanged", "SeekOrRestart");
        gi = gi with { TemporalCameraCutKey = 3 };
        temporal = temporal with { CameraCutKey = 3 };
        RenderAndAssertReset("CameraCut", "CameraCut");

        Assert.Equal(baseline.Rgba, session.Capture(frame, Width, Height, RayGiOptions.Off).Rgba);
        Assert.Null(session.TemporalUpscalingStatistics);
        Assert.Null(session.RayGiStatistics);

        void RenderAndAssertReset(string giReason, string aaReason)
        {
            using (var reset = session.Render(frame, Width, Height, gi, temporalUpscaling: temporal))
            {
                Assert.Equal(0, reset.Statistics.ReadbackCount);
                AssertGi(session, gi, 1, giReason);
                Assert.True(reset.Statistics.TemporalUpscaling!.ResetHistory);
                Assert.Equal(aaReason, reset.Statistics.TemporalUpscaling.ResetReason);
                Write(session, giReason);
            }
            using var continued = session.Render(frame, Width, Height, gi, temporalUpscaling: temporal);
            Assert.Equal(0, continued.Statistics.ReadbackCount);
            AssertGi(session, gi, 2, "None");
            Assert.False(continued.Statistics.TemporalUpscaling!.ResetHistory);
            Assert.Equal("None", continued.Statistics.TemporalUpscaling.ResetReason);
        }
    }

    [Fact]
    public void TemporalAaSsaaRejectionPrecedesVendorLoadingAndGiSsaaRemainsAvailable()
    {
        using var session = new D3D12FrameSession(); using var control = new D3D12FrameSession();
        var frame = Frame(); var gi = Gi(LightingMode.SoftwareRayTracing);
        Assert.Equal(control.Capture(frame, Width, Height, gi).Rgba, session.Capture(frame, Width, Height, gi).Rgba);
        using (session.Render(frame, Width, Height, gi)) { }
        using (control.Render(frame, Width, Height, gi)) { }
        var ssaa = Copy(frame, options: frame.Options! with { SpatialSamples = 2 });
        long frameId = session.Runtime.FrameId, submissions = session.Runtime.Submissions;
        long compilations = session.Runtime.ShaderCompilations;
        // A nonexistent DLL proves the combination is rejected before trying the vendor loader.
        foreach (var vendor in new[] { D3D12TemporalUpscalingMode.Fsr, D3D12TemporalUpscalingMode.XeSs, D3D12TemporalUpscalingMode.Dlss })
        {
            var invalidLoader = new D3D12TemporalUpscalingOptions
            {
                Mode = vendor, LoaderPath = Path.Combine(Path.GetTempPath(), "absent-gi-temporal-runtime", "missing.dll")
            };
            Assert.Throws<NotSupportedException>(() => session.Render(ssaa, Width, Height, gi, temporalUpscaling: invalidLoader));
        }
        Assert.False(session.Runtime.IsRecording);
        Assert.Equal(frameId, session.Runtime.FrameId);
        Assert.Equal(submissions, session.Runtime.Submissions);
        Assert.Equal(compilations, session.Runtime.ShaderCompilations);
        var expected = control.Capture(frame, Width, Height, gi);
        Assert.Equal(expected.Rgba, session.Capture(frame, Width, Height, gi).Rgba);
        AssertGi(session, gi, 3, "None");
        Assert.Equal(control.RayGiStatistics!.Seed, session.RayGiStatistics!.Seed);
        Write(session, "ssaa-rejection-recovered");
        using(var combined=session.Render(ssaa,Width,Height,gi))
        {
            Assert.Equal(1,combined.Statistics.SubmissionCount);Assert.Equal(0,combined.Statistics.ReadbackCount);
            Assert.Equal(2,session.RayGiStatistics!.DispatchCount);Assert.Equal(4,session.RayGiStatistics.DenoiseDispatchCount);
            Assert.Equal(1UL,session.RayGiStatistics.TemporalHistoryFrames);
        }
        using var spatial = session.Render(ssaa, Width, Height, gi with { Denoiser = RayGiDenoiser.Spatial });
        Assert.Equal(2, spatial.Statistics.SpatialSamples);
        Assert.Equal(2, spatial.Statistics.SpatialAccumulationDispatches);
        Assert.Equal(0, spatial.Statistics.ReadbackCount);
        Assert.Equal(RayGiDenoiser.Spatial, session.RayGiStatistics!.Denoiser);
        Assert.Equal(0UL, session.RayGiStatistics.TemporalHistoryFrames);
        Assert.Equal(2, session.RayGiStatistics.DenoiseDispatchCount);
        Write(session, "spatial-gi-ssaa");
    }

    private static void AssertGi(D3D12FrameSession session, RayGiOptions options, ulong frames, string reset)
    {
        var stats = Assert.IsType<D3D12RayGiStatistics>(session.RayGiStatistics);
        Assert.Equal(options.Mode, stats.Requested); Assert.Equal(options.Mode, stats.Effective);
        Assert.Equal(RayGiDenoiser.TemporalSpatial, stats.Denoiser);
        Assert.Equal(1, stats.DispatchCount); Assert.Equal(2, stats.DenoiseDispatchCount);
        Assert.Equal(frames, stats.TemporalHistoryFrames);
        Assert.Equal(reset != "None", stats.TemporalResetHistory); Assert.Equal(reset, stats.TemporalResetReason);
        Assert.Equal(unchecked(options.Seed + (uint)(frames - 1) * 0x9e3779b9u), stats.Seed);
        Assert.Equal(Width * Height * 64L, stats.TemporalHistoryBytes);
        if (options.Mode == LightingMode.HardwareRayTracing) Assert.StartsWith("DXR-raw-", session.RayGiBackendIdentity);
        else Assert.Equal("GPU-BVH", session.RayGiBackendIdentity);
    }

    private void Write(D3D12FrameSession session, string stage) => output.WriteLine(JsonSerializer.Serialize(new
    {
        stage, session.AdapterName, session.IsWarp, session.RayGiBackendIdentity,
        gi = session.RayGiStatistics, vendor = session.TemporalUpscalingStatistics,
        session.Statistics
    }));

    private static D3D12TemporalUpscalingOptions Temporal(D3D12TemporalUpscalingMode mode) => new()
    {
        Mode = mode, QualityPreset = D3D12FsrQualityPreset.NativeAa, XeSsQuality = D3D12XeSsQuality.AntiAliasing,
        LoaderPath = Environment.GetEnvironmentVariable(mode switch
            { D3D12TemporalUpscalingMode.Fsr => "MMD_FSR_LOADER_PATH", D3D12TemporalUpscalingMode.XeSs => "MMD_XESS_RUNTIME_PATH", _ => "MMD_DLSS_BRIDGE_PATH" })
            ?? throw new InvalidOperationException("Set the installed official vendor runtime path for the actual GPU integration test."),
        DlssSdkDirectory = mode == D3D12TemporalUpscalingMode.Dlss ? Environment.GetEnvironmentVariable("MMD_DLSS_SDK_DIRECTORY") : null
    };

    private static RayGiOptions Gi(LightingMode mode) => new(mode, Strength: 4, SamplesPerPixel: 16, Seed: 17,
        MaxRayDistance: 50, RayEpsilon: .001f, DirectionalIrradiance: new(3),
        Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.TemporalSpatial);

    private static OriginalFrameScene Frame()
    {
        var source = D3D12RayGiTests.Bounce();
        var mesh = source.Surface.Snapshot.WithCamera(source.Surface.Snapshot.Camera with { VerticalFovDegrees = 45 });
        var bindings = source.Surface.Materials.ToArray(); bindings[0] = bindings[0] with { ToonShadowColor = new(.25f) };
        return Copy(source, new(mesh, bindings, source.Surface.Profile));
    }

    private static OriginalFrameScene Copy(OriginalFrameScene frame, OriginalStyleScene? surface = null, OriginalFrameOptions? options = null)
        => new(surface ?? frame.Surface, frame.Outline, frame.OutlineOptions, options ?? frame.Options,
            frame.Materials, frame.Lighting, frame.EditorOverlay);

    private static int ChangedPixels(RenderedFrame a, RenderedFrame b)
    {
        Assert.Equal(a.Rgba.Length, b.Rgba.Length);
        int changed = 0;
        for (int i = 0; i < a.Rgba.Length; i += 4)
            if (!a.Rgba.AsSpan(i, 3).SequenceEqual(b.Rgba.AsSpan(i, 3))) changed++;
        return changed;
    }

    private static void Require(D3D12FrameSession session, LightingMode mode)
    {
        var capabilities = session.QueryRayGiCapabilities();
        Assert.True(mode == LightingMode.HardwareRayTracing ? capabilities.HardwareAvailable : capabilities.SoftwareAvailable,
            capabilities.UnavailableReason ?? $"Required actual GPU backend unavailable: {session.AdapterName}; tier={capabilities.RawRaytracingTier}, sm={capabilities.ShaderModel}.");
    }
}
