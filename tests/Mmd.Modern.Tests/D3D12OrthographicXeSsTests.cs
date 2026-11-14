using System.Numerics;
using System.Text.Json;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12OrthographicTemporalHistoryTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EffectiveProjectionChangesDiscardHistory(int change)
    {
        var frame = OrthographicXeSsFixture.Create();
        var camera = frame.Surface.Snapshot.Camera;
        var changed = change switch
        {
            0 => camera with { VerticalFovDegrees = 45 },
            1 => camera with { OrthographicHeight = 3 },
            2 => camera with { Near = .5f },
            3 => camera with { Far = 20 },
            _ => camera with { LeftHanded = false }
        };
        var history = new D3D12TemporalHistory(); var options = new D3D12TemporalUpscalingOptions();
        history.Commit(frame, options, 0, 8);
        Assert.Equal("ProjectionChanged", history.ResetReason(OrthographicXeSsFixture.Camera(frame, changed), options));
        history.Commit(OrthographicXeSsFixture.Camera(frame, changed), options, 0, 8);
        Assert.Equal("None", history.ResetReason(OrthographicXeSsFixture.Camera(frame, changed with { }), options));
    }

    [Fact]
    public void ContinuousViewJitterAndDeformationRetainHistoryAndUseEffectiveOverride()
    {
        var frame = OrthographicXeSsFixture.Create(); var camera = frame.Surface.Snapshot.Camera;
        var options = new D3D12TemporalUpscalingOptions(); var history = new D3D12TemporalHistory();
        history.Commit(OrthographicXeSsFixture.Camera(frame, camera with { ProjectionJitterPixels = new(.2f, -.3f) }), options, 0, 8);
        var moved = camera with { Eye = camera.Eye + new Vector3(.125f, 0, 0), Target = camera.Target + new Vector3(.125f, 0, 0) };
        var current = OrthographicXeSsFixture.Camera(OrthographicXeSsFixture.Deform(frame, .125f, .1f), moved);
        Assert.Equal("None", history.ResetReason(current, options));
        var source = frame.Surface.Snapshot.WithCamera(camera with { VerticalFovDegrees = 45 });
        var overridden = new OriginalFrameScene(new(source, frame.Surface.Materials, frame.Surface.Profile), options: frame.Options! with { CameraOverride = moved });
        Assert.Equal("None", history.ResetReason(overridden, options));
        history.Commit(overridden, options, 0, 8);
        var perspective = OrthographicXeSsFixture.Camera(frame, camera with { VerticalFovDegrees = 45 });
        Assert.Equal("ProjectionChanged", history.ResetReason(perspective, options));
        history.Commit(perspective, options, 0, 8);
        Assert.Equal("None", history.ResetReason(OrthographicXeSsFixture.Camera(frame,
            camera with { VerticalFovDegrees = 45, OrthographicHeight = 99 }), options)); // Unused for perspective.
        Assert.Equal("ProjectionChanged", history.ResetReason(OrthographicXeSsFixture.Camera(frame,
            camera with { VerticalFovDegrees = 55 }), options));
    }
}

[Trait("Category", "Gpu")]
public sealed class D3D12OrthographicXeSsTests(ITestOutputHelper output)
{
    private const int Width = 128, Height = 96;
    private static string Library => Environment.GetEnvironmentVariable("MMD_XESS_RUNTIME_PATH")
        ?? throw new InvalidOperationException("Set MMD_XESS_RUNTIME_PATH to the official locked libxess.dll.");
    private static D3D12TemporalUpscalingOptions Options(D3D12XeSsQuality quality = D3D12XeSsQuality.AntiAliasing)
        => new() { Mode = D3D12TemporalUpscalingMode.XeSs, XeSsQuality = quality, LoaderPath = Library };

    [Theory]
    [InlineData(D3D12XeSsQuality.AntiAliasing)]
    [InlineData(D3D12XeSsQuality.Quality)]
    public void RealOrthographicReconstructionPreservesCutoutFineEdgesAndDisoccludedColor(D3D12XeSsQuality quality)
    {
        var frame = OrthographicXeSsFixture.Create(); var options = Options(quality);
        using var session = new D3D12FrameSession();
        using var native = new D3D12OriginalStyleRenderer();
        var baseline = native.Render(frame, Width, Height);
        Assert.Equal(baseline.Rgba, session.Capture(frame, Width, Height).Rgba);
        for (int i = 0; i < 8; i++)
        {
            using var gpu = session.Render(frame, Width, Height, temporalUpscaling: options);
            Assert.Equal(0, gpu.Statistics.ReadbackCount);
            Assert.Equal(i == 0, gpu.Statistics.TemporalUpscaling!.ResetHistory);
        }
        var stable = session.Capture(frame, Width, Height, temporalUpscaling: options);
        var stats = session.TemporalUpscalingStatistics!;
        Assert.Equal(D3D12TemporalUpscalingMode.XeSs, stats.Mode);
        Assert.True(stats.XeSsDeviceQuery!.Initialized);
        Assert.Equal(stats.XeSsDeviceQuery.InputWidth, stats.RenderWidth);
        Assert.Equal(stats.XeSsDeviceQuery.InputHeight, stats.RenderHeight);
        Assert.Contains(session.LastRecordings, p => p.Channel == "xess:temporal");
        Assert.DoesNotContain(session.LastRecordings, p => p.Channel == "fsr:temporal");
        // Analytical fixture locations: red left half, transparent right half reveals blue.
        AssertNearNative(baseline, stable, -.63f, 0);
        AssertNearNative(baseline, stable, -.26f, 0);
        AssertDominant(stable, -.63f, 0, 0);
        AssertDominant(stable, -.26f, 0, 2);
        AssertFineEdge(stable);
        D3D12OriginalFrameTests.Save($"xess-ortho-{quality}-static", stable);

        OriginalFrameScene moving = frame;
        for (int i = 1; i <= 10; i++)
        {
            // Nonrigid vertex deformation plus camera translation, without scene-structure resets.
            var camera = frame.Surface.Snapshot.Camera;
            camera = camera with { Eye = camera.Eye + new Vector3(i * .01f, 0, 0), Target = camera.Target + new Vector3(i * .01f, 0, 0) };
            moving = OrthographicXeSsFixture.Camera(OrthographicXeSsFixture.Deform(frame, i * .09f, i * .01f), camera);
            using var gpu = session.Render(moving, Width, Height, temporalUpscaling: options);
            Assert.False(gpu.Statistics.TemporalUpscaling!.ResetHistory);
            Assert.Equal(0, gpu.Statistics.ReadbackCount);
        }
        var moved = session.Capture(moving, Width, Height, temporalUpscaling: options);
        var movedNative = native.Render(moving, Width, Height);
        // The former red area is newly visible blue. The final foreground has moved right.
        AssertNearNative(movedNative, moved, -.63f, 0);
        AssertNearNative(movedNative, moved, .17f, 0);
        AssertDominant(moved, -.63f, 0, 2);
        AssertDominant(moved, .17f, 0, 0);
        AssertFineEdge(moved);
        Assert.NotEqual(stable.Rgba, moved.Rgba);
        Assert.Equal(baseline.Rgba, session.Capture(frame, Width, Height).Rgba);
        D3D12OriginalFrameTests.Save($"xess-ortho-{quality}-deformed-disocclusion", moved);
        output.WriteLine(JsonSerializer.Serialize(stats));
    }

    [Fact]
    public void ProjectionSeekResizeAndAbortedExecuteResetOnlySubmittedHistory()
    {
        using var session = new D3D12FrameSession(); var options = Options();
        var frame = OrthographicXeSsFixture.Create(); var camera = frame.Surface.Snapshot.Camera;
        D3D12TemporalUpscalingStatistics Render(OriginalFrameScene scene, int width = Width, int height = Height)
        { using var gpu = session.Render(scene, width, height, temporalUpscaling: options); return gpu.Statistics.TemporalUpscaling!; }
        Assert.Equal("FirstFrame", Render(frame).ResetReason);
        Assert.Equal("None", Render(frame).ResetReason);
        var zoomed = OrthographicXeSsFixture.Camera(frame, camera with { OrthographicHeight = 3 });
        Assert.Equal("ProjectionChanged", Render(zoomed).ResetReason);
        Assert.Equal("None", Render(zoomed).ResetReason);
        var perspective = OrthographicXeSsFixture.Camera(frame, camera with { VerticalFovDegrees = 45 });
        Assert.Equal("ProjectionChanged", Render(perspective).ResetReason);
        Assert.Equal("ProjectionChanged", Render(frame).ResetReason);
        options = options with { HistoryGeneration = 1 };
        Assert.Equal("SeekOrRestart", Render(frame).ResetReason);
        options = options with { CameraCutKey = 1 };
        Assert.Equal("CameraCut", Render(frame).ResetReason);
        Assert.Equal("ConfigurationChanged", Render(frame, 160, 120).ResetReason);
        long submitted = session.TemporalUpscalingStatistics!.SubmittedFrames;
        Assert.Throws<InvalidOperationException>(() => session.RenderForPresent(frame, 160, 120, null,
            (_, _) => throw new InvalidOperationException("Injected after real orthographic XeSS execute"), temporalUpscaling: options));
        Assert.False(session.Runtime.IsRecording);
        var recovered = Render(frame, 160, 120);
        Assert.Equal("AbortedFrame", recovered.ResetReason);
        Assert.Equal(submitted + 1, recovered.SubmittedFrames);
        Assert.Equal(0, recovered.JitterPhase);
        for (int i = 0; i < 4; i++) Render(frame, 160, 120);
        var warm = session.Statistics!;
        for (int i = 0; i < 12; i++) Render(frame, 160, 120);
        var last = session.Statistics!;
        Assert.Equal(warm.ShaderCompilations, last.ShaderCompilations);
        Assert.Equal(warm.PipelineCreations, last.PipelineCreations);
        Assert.Equal(warm.LiveFrameResources, last.LiveFrameResources);
        Assert.Equal(warm.CachedResources, last.CachedResources);
        Assert.Equal(0, last.TotalReadbacks);
        output.WriteLine(JsonSerializer.Serialize(new { warm, last }));
    }

    [Fact]
    public void FsrRejectsOrthographicBeforeLoaderOrFrameAllocationAndDoesNotPoisonXeSs()
    {
        using var session = new D3D12FrameSession(); var frame = OrthographicXeSsFixture.Create();
        var rejected = new D3D12TemporalUpscalingOptions { Mode = D3D12TemporalUpscalingMode.Fsr,
            LoaderPath = "D:/unused-orthographic-preflight/amd_fidelityfx_loader_dx12.dll" };
        var error = Assert.Throws<NotSupportedException>(() => session.Render(frame, Width, Height, temporalUpscaling: rejected));
        Assert.Contains("perspective", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(session.Runtime.IsRecording);
        Assert.Equal(0, session.Runtime.FrameOwnedAllocations);
        Assert.Empty(session.LastRecordings);
        Assert.Null(session.Statistics);
        using var accepted = session.Render(frame, Width, Height, temporalUpscaling: Options());
        Assert.Equal(D3D12TemporalUpscalingMode.XeSs, accepted.Statistics.TemporalUpscaling!.Mode);
        Assert.Equal("FirstFrame", accepted.Statistics.TemporalUpscaling.ResetReason);
    }

    [Fact]
    public async Task PublicViewUsesResolvedOrthographicCameraAndKeepsContinuousHistory()
    {
        var quality = new ViewQualityOptions(TemporalUpscaling: new()
            { Mode = ViewTemporalUpscalingMode.XeSs, XeSsQuality = ViewXeSsQuality.NativeAa });
        var plan = new OfflineFramePlan(Width, Height, new(30), 0, 2);
        var request = new ExportRequest(plan, quality).Render;
        await using var renderer = new D3D12ViewRenderer(request, xessLibraryPath: Library);
        var ortho = OrthographicXeSsFixture.Create(); var camera = ortho.Surface.Snapshot.Camera;
        // The resolved camera must win over a perspective model snapshot without a fake FOV.
        var source = ortho.Surface.Snapshot.WithCamera(camera with { VerticalFovDegrees = 45 });
        var scene = new OriginalFrameScene(new(source, ortho.Surface.Materials, ortho.Surface.Profile), options: ortho.Options);
        OfflineFrame Frame(int index, SceneCamera view) => new(plan.GetSample(index), Width, Height, scene, null, null, null)
            { ResolvedCamera = view };
        var firstFrame = Frame(0, camera);
        using var pixels = await renderer.CaptureAsync(firstFrame, TestContext.Current.CancellationToken);
        var directScene = D3D12ViewScene.Resolve(scene, null, null, null, firstFrame.MorphChannels, request.EffectiveQuality, camera);
        using var reference = new D3D12FrameSession();
        var expected = reference.Capture(directScene, Width, Height, temporalUpscaling: Options() with { FrameTimeMilliseconds = 1000f / 30 });
        Assert.Equal(expected.Rgba, pixels.Pixels.ToArray());
        Assert.Equal("FirstFrame", renderer.Statistics!.TemporalUpscaling!.ResetReason);
        var moved = camera with { Eye = camera.Eye + new Vector3(.1f, 0, 0), Target = camera.Target + new Vector3(.1f, 0, 0) };
        using (await renderer.CaptureAsync(Frame(1, moved), TestContext.Current.CancellationToken)) { }
        Assert.Equal("None", renderer.Statistics!.TemporalUpscaling!.ResetReason);
        using (await renderer.CaptureAsync(Frame(2, moved with { OrthographicHeight = 3 }), TestContext.Current.CancellationToken)) { }
        Assert.Equal("ProjectionChanged", renderer.Statistics!.TemporalUpscaling!.ResetReason);
    }

    private static void AssertNearNative(RenderedFrame native, RenderedFrame temporal, float x, float y)
    {
        int cx = (int)(Width / 2f + x * Height / 2), cy = (int)(Height / 2f - y * Height / 2);
        for (int py = cy - 2; py <= cy + 2; py++) for (int px = cx - 2; px <= cx + 2; px++)
        for (int channel = 0; channel < 3; channel++)
        {
            int p = (py * Width + px) * 4 + channel;
            Assert.InRange(Math.Abs(native.Rgba[p] - temporal.Rgba[p]), 0, 10);
        }
    }

    private static void AssertDominant(RenderedFrame frame, float x, float y, int channel)
    {
        int px = (int)(Width / 2f + x * Height / 2), py = (int)(Height / 2f - y * Height / 2);
        int offset = (py * Width + px) * 4;
        for (int c = 0; c < 3; c++) if (c != channel)
            Assert.True(frame.Rgba[offset + channel] > frame.Rgba[offset + c] + 120,
                $"Expected channel {channel} at ({px},{py}), got {frame.Rgba[offset]}, {frame.Rgba[offset + 1]}, {frame.Rgba[offset + 2]}.");
    }

    private static void AssertFineEdge(RenderedFrame frame)
    {
        // Four-output-pixel slanted strip, roughly two input pixels in Quality. Count genuine green
        // pixels, so an initialized but black/missing thin-geometry reconstruction cannot pass.
        int green = 0;
        for (int y = 22; y < 74; y++) for (int x = 89; x < 104; x++)
        {
            int p = (y * Width + x) * 4;
            if (frame.Rgba[p + 1] > frame.Rgba[p] + 100 && frame.Rgba[p + 1] > frame.Rgba[p + 2] + 100) green++;
        }
        Assert.True(green >= 60, $"Fine slanted edge retained only {green} green pixels.");
    }
}

internal static class OrthographicXeSsFixture
{
    internal static OriginalFrameScene Create()
    {
        SceneVertex V(float x, float y, float z, float u, float v) => new(new(x, y, z), -Vector3.UnitZ, new(u, v));
        SceneVertex[] vertices = [
            V(-1.25f, -.85f, 0, 0, 1), V(1.25f, -.85f, 0, 1, 1), V(1.25f, .85f, 0, 1, 0), V(-1.25f, .85f, 0, 0, 0),
            V(-.75f, -.6f, -1, 0, 1), V(-.15f, -.6f, -1, 1, 1), V(-.15f, .6f, -1, 1, 0), V(-.75f, .6f, -1, 0, 0),
            V(.72f, -.7f, -1, 0, 1), V(.8f, -.7f, -1, 1, 1), V(.66f, .7f, -1, 1, 0), V(.58f, .7f, -1, 0, 0)];
        var mask = new SceneTexture("half-cutout", 4, 1, [255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 0, 255, 255, 255, 0]);
        var snapshot = new SceneSnapshot(vertices, [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7, 8, 9, 10, 8, 10, 11],
            [new("receiver", 0, 6, new(.05f, .1f, .8f, 1), Vector3.Zero, Vector3.Zero, 1),
             new("cutout", 6, 6, new(.8f, .05f, .05f, 1), Vector3.Zero, Vector3.Zero, 1, DiffuseTexture: 0, AlphaCutoff: .5f),
             new("fine-edge", 12, 6, new(.05f, .9f, .05f, 1), Vector3.Zero, Vector3.Zero, 1)], [mask],
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 2, 1, 11, LeftHanded: true), lightDirection: -Vector3.UnitZ);
        return new(new(snapshot, [new(Vector3.One, DoubleSided: true), new(Vector3.One, DoubleSided: true), new(Vector3.One, DoubleSided: true)],
            new(Vector3.One, Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false) { EnableFrustumCulling = false });
    }

    internal static OriginalFrameScene Camera(OriginalFrameScene frame, SceneCamera camera)
        => new(frame.Surface, frame.Outline, frame.OutlineOptions, frame.Options! with { CameraOverride = camera }, frame.Materials, frame.Lighting);

    internal static OriginalFrameScene Deform(OriginalFrameScene frame, float translation, float shear)
    {
        var snapshot = frame.Surface.Snapshot;
        var vertices = snapshot.Vertices.ToArray();
        for (int i = 4; i < 8; i++) vertices[i] = vertices[i] with
            { Position = vertices[i].Position + new Vector3(translation + shear * vertices[i].Position.Y, 0, 0) };
        return new(new(snapshot.WithDeformedVertices(vertices), frame.Surface.Materials, frame.Surface.Profile),
            options: frame.Options, materials: frame.Materials, lighting: frame.Lighting);
    }
}
