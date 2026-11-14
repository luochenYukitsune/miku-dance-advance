using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12TemporalGiTests
{
    private const int Width = 96, Height = 72;

    [Fact]
    public void HistoryTracksEffectiveExternalGiAndLightingValuesWithoutResettingEqualCopies()
    {
        var frame = Frame(); var options = new D3D12TemporalUpscalingOptions(); var history = new D3D12TemporalHistory();
        var gi = Gi(LightingMode.SoftwareRayTracing);
        history.Commit(frame, options, 0, 8, gi);
        Assert.Equal("None", history.ResetReason(frame, options, gi with { }));
        Assert.Equal("GiConfigurationChanged", history.ResetReason(frame, options, gi with { Strength = gi.Strength + 1 }));
        Assert.Equal("GiConfigurationChanged", history.ResetReason(frame, options, gi with { Mode = LightingMode.HardwareRayTracing }));
        Assert.Equal("GiConfigurationChanged", history.ResetReason(frame, options));
        var inherited = Copy(frame, options: frame.Options! with { RayGi = gi });
        Assert.Equal("None", history.ResetReason(inherited, options));
        Assert.Equal("None", history.ResetReason(Copy(frame, options: frame.Options! with { RayGi = gi with { Strength = 1 } }), options, gi));
        history.Commit(frame, options, 0, 8);
        Assert.Equal("None", history.ResetReason(frame, options, RayGiOptions.Off with { Strength = 99 }));

        var light = new NativeSceneLight(Guid.NewGuid(), "fill", SceneLightKind.Point, new(0, 1, -2), Quaternion.Identity, Vector3.One);
        var lit = Copy(frame, lighting: new([light]));
        history.Commit(lit, options, 0, 8, gi);
        Assert.Equal("None", history.ResetReason(Copy(frame, lighting: new([light with { }])), options, gi));
        Assert.Equal("SceneStructureChanged", history.ResetReason(Copy(frame, lighting: new([light with { Intensity = 2 }])), options, gi));
        history.Commit(frame, options, 0, 8, gi);
        var changedDirection = Copy(frame, new(frame.Surface.Snapshot.WithLighting(Vector3.UnitY), frame.Surface.Materials, frame.Surface.Profile));
        Assert.Equal("SceneStructureChanged", history.ResetReason(changedDirection, options, gi));
    }

    [Fact]
    public void UvShadingEditsResetHistoryWhilePositionDeformationRetainsIt()
    {
        var frame = Frame(); var options = new D3D12TemporalUpscalingOptions(); var history = new D3D12TemporalHistory();
        history.Commit(frame, options, 0, 8);
        var source = frame.Surface.Snapshot; var vertices = source.Vertices.ToArray();
        vertices[0] = vertices[0] with { Position = vertices[0].Position + Vector3.UnitX };
        var deformed = Copy(frame, new(source.WithDeformedVertices(vertices), frame.Surface.Materials, frame.Surface.Profile));
        Assert.Equal("None", history.ResetReason(deformed, options));
        vertices[0] = vertices[0] with { Uv = new(.25f, .75f) };
        var uvChanged = Copy(frame, new(source.WithDeformedVertices(vertices), frame.Surface.Materials, frame.Surface.Profile));
        Assert.Equal("SceneStructureChanged", history.ResetReason(uvChanged, options));
        var uv = new SceneAdditionalUvs(1, new SceneAdditionalUvVertex[source.Vertices.Length]);
        var withUv = Copy(frame, new(source.WithAdditionalUvs(uv), frame.Surface.Materials, frame.Surface.Profile));
        history.Commit(withUv, options, 0, 8);
        Assert.Equal("None", history.ResetReason(Copy(frame, new(source.WithAdditionalUvs(new(1, uv.Vertices)), frame.Surface.Materials, frame.Surface.Profile)), options));
        var values = uv.Vertices.ToArray(); values[0] = values[0] with { Uv1 = Vector4.One };
        Assert.Equal("SceneStructureChanged", history.ResetReason(Copy(frame, new(source.WithAdditionalUvs(new(1, values)), frame.Surface.Materials, frame.Surface.Profile)), options));
    }

    [Theory, Trait("Category", "Gpu")]
    [InlineData(D3D12TemporalUpscalingMode.Fsr, LightingMode.SoftwareRayTracing)]
    [InlineData(D3D12TemporalUpscalingMode.Fsr, LightingMode.HardwareRayTracing)]
    [InlineData(D3D12TemporalUpscalingMode.XeSs, LightingMode.SoftwareRayTracing)]
    [InlineData(D3D12TemporalUpscalingMode.XeSs, LightingMode.HardwareRayTracing)]
    public void ActualVendorReconstructionConsumesGiAndRetainsContinuousHistory(D3D12TemporalUpscalingMode vendor, LightingMode mode)
    {
        using var session = new D3D12FrameSession(); Require(session, mode);
        var frame = Frame(); var temporal = Temporal(vendor); var gi = Gi(mode);
        var off = session.Capture(frame, Width, Height, temporalUpscaling: temporal);
        var enabled = session.Capture(frame, Width, Height, rayGiOptions: gi, temporalUpscaling: temporal);
        Assert.Equal("GiConfigurationChanged", session.TemporalUpscalingStatistics!.ResetReason);
        Assert.True(session.TemporalUpscalingStatistics.ResetHistory);
        Assert.True(CenterRed(enabled) > CenterRed(off) + 20, "GI must change the actual reconstructed receiver, not only reported mode.");
        Assert.Equal(mode, session.RayGiStatistics!.Effective);
        Assert.Contains(session.LastRecordings, p => p.Channel == (vendor == D3D12TemporalUpscalingMode.Fsr ? "fsr:temporal" : "xess:temporal"));
        var dimensions = session.TemporalUpscalingStatistics!;
        Assert.Equal(Width, dimensions.DisplayWidth); Assert.Equal(Height, dimensions.DisplayHeight);
        Assert.Equal(Width, dimensions.RenderWidth); Assert.Equal(Height, dimensions.RenderHeight); // NativeAA.

        for (int i = 0; i < 4; i++)
        {
            using var output = session.Render(frame, Width, Height, rayGiOptions: gi, temporalUpscaling: temporal);
            Assert.False(output.Statistics.TemporalUpscaling!.ResetHistory);
            Assert.Equal("None", output.Statistics.TemporalUpscaling.ResetReason);
            Assert.Equal(0, output.Statistics.ReadbackCount);
            Assert.Equal(1, output.Statistics.SubmissionCount);
        }
        var warm = session.Statistics!;
        for (int i = 0; i < 12; i++)
        {
            using var output = session.Render(frame, Width, Height, rayGiOptions: gi, temporalUpscaling: temporal);
            Assert.False(output.Statistics.TemporalUpscaling!.ResetHistory);
            Assert.InRange(output.Statistics.PendingFrames, 0, 3);
        }
        Assert.Equal(warm.CachedResources, session.Statistics!.CachedResources);
        Assert.Equal(warm.LiveFrameResources, session.Statistics.LiveFrameResources);
        Assert.Equal(warm.TotalReadbacks, session.Statistics.TotalReadbacks);
        Assert.Equal(warm.ShaderCompilations, session.Statistics.ShaderCompilations);
        Assert.Equal(warm.PipelineCreations, session.Statistics.PipelineCreations);

        var filtered = gi with { Composition = RayGiComposition.ToonPalette, Denoiser = RayGiDenoiser.Spatial };
        using (var changed = session.Render(frame, Width, Height, rayGiOptions: filtered, temporalUpscaling: temporal))
        {
            Assert.True(changed.Statistics.TemporalUpscaling!.ResetHistory);
            Assert.Equal("GiConfigurationChanged", changed.Statistics.TemporalUpscaling.ResetReason);
        }
        using (var same = session.Render(frame, Width, Height, rayGiOptions: filtered with { }, temporalUpscaling: temporal))
            Assert.False(same.Statistics.TemporalUpscaling!.ResetHistory);
        // Off removes both optional paths and restores the unchanged accepted raster bytes.
        var baseline = session.Capture(frame, Width, Height);
        using var original = new D3D12OriginalStyleRenderer();
        Assert.Equal(original.Render(frame, Width, Height).Rgba, baseline.Rgba);
    }

    [Theory, Trait("Category", "Gpu")]
    [InlineData(D3D12TemporalUpscalingMode.Fsr)]
    [InlineData(D3D12TemporalUpscalingMode.XeSs)]
    public void GiVendorAbortSeekResizeAndQualityChangesRecoverWithoutCommittingAbortedHistory(D3D12TemporalUpscalingMode vendor)
    {
        using var session = new D3D12FrameSession(); var frame = Frame(); var temporal = Temporal(vendor);
        var gi = Gi(LightingMode.SoftwareRayTracing);
        D3D12TemporalUpscalingStatistics Render(int width = Width, int height = Height)
        {
            using var output = session.Render(frame, width, height, rayGiOptions: gi, temporalUpscaling: temporal);
            Assert.Equal(0, output.Statistics.ReadbackCount);
            return output.Statistics.TemporalUpscaling!;
        }
        Assert.True(Render().ResetHistory); Assert.False(Render().ResetHistory);
        long submitted = session.TemporalUpscalingStatistics!.SubmittedFrames;
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(frame, Width, Height, gi,
            (_, _) => throw new OperationCanceledException("Injected after vendor dispatch and GI recording"), temporalUpscaling: temporal));
        Assert.False(session.Runtime.IsRecording);
        var recovered = Render();
        Assert.Equal("AbortedFrame", recovered.ResetReason); Assert.True(recovered.ResetHistory);
        Assert.Equal(submitted + 1, recovered.SubmittedFrames);
        temporal = temporal with { HistoryGeneration = 1 };
        Assert.Equal("SeekOrRestart", Render().ResetReason);
        Assert.Equal("ConfigurationChanged", Render(120, 90).ResetReason);
        temporal = temporal with { QualityPreset = D3D12FsrQualityPreset.Quality, XeSsQuality = D3D12XeSsQuality.Quality };
        var upscaled = Render(120, 90);
        Assert.Equal("ConfigurationChanged", upscaled.ResetReason);
        Assert.InRange(upscaled.RenderWidth, 1, 119); Assert.InRange(upscaled.RenderHeight, 1, 89);
        Assert.False(Render(120, 90).ResetHistory);
        gi = gi with { Strength = .25f };
        Assert.Equal("GiConfigurationChanged", Render(120, 90).ResetReason);
        temporal = temporal with { CameraCutKey = 1 };
        Assert.Equal("CameraCut", Render(120, 90).ResetReason);
        session.WaitForIdle();
    }

    private static D3D12TemporalUpscalingOptions Temporal(D3D12TemporalUpscalingMode mode) => new()
    {
        Mode = mode, QualityPreset = D3D12FsrQualityPreset.NativeAa, XeSsQuality = D3D12XeSsQuality.AntiAliasing,
        LoaderPath = Environment.GetEnvironmentVariable(mode == D3D12TemporalUpscalingMode.Fsr ? "MMD_FSR_LOADER_PATH" : "MMD_XESS_RUNTIME_PATH")
            ?? throw new InvalidOperationException("Set the actual optional vendor SDK runtime path for this GPU test.")
    };
    private static RayGiOptions Gi(LightingMode mode) => new(mode, Strength: 4, SamplesPerPixel: 16, Seed: 17,
        MaxRayDistance: 50, RayEpsilon: .001f, DirectionalIrradiance: new(3));
    private static OriginalFrameScene Frame()
    {
        var original = D3D12RayGiTests.Bounce();
        var mesh = original.Surface.Snapshot.WithCamera(original.Surface.Snapshot.Camera with { VerticalFovDegrees = 45 });
        var bindings = original.Surface.Materials.ToArray(); bindings[0] = bindings[0] with { ToonShadowColor = new(.25f) };
        return Copy(original, new(mesh, bindings, original.Surface.Profile));
    }
    private static OriginalFrameScene Copy(OriginalFrameScene frame, OriginalStyleScene? surface = null, OriginalFrameOptions? options = null,
        SceneLightingSnapshot? lighting = null) => new(surface ?? frame.Surface, frame.Outline, frame.OutlineOptions,
            options ?? frame.Options, frame.Materials, lighting ?? frame.Lighting, frame.EditorOverlay);
    private static long CenterRed(RenderedFrame frame)
    {
        long total = 0;
        for (int y = Height / 2 - 3; y <= Height / 2 + 3; y++)
            for (int x = Width / 2 - 3; x <= Width / 2 + 3; x++) total += frame.Rgba[(y * Width + x) * 4];
        return total;
    }
    private static void Require(D3D12FrameSession session, LightingMode mode)
    {
        var capabilities = session.QueryRayGiCapabilities();
        Assert.True(mode == LightingMode.HardwareRayTracing ? capabilities.HardwareAvailable : capabilities.SoftwareAvailable,
            capabilities.UnavailableReason ?? "Required real GPU GI backend is unavailable.");
    }
}
