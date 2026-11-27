using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12ClusteredAndPathTracingGpuTests
{
    [Fact]
    public void ClusteredForwardMatchesForwardPixelOutputAcrossAllNativeLightTypes()
    {
        using var session = new D3D12FrameSession();
        var baseFrame = CreateLitBoxScene();
        var lights = new NativeSceneLight[]
        {
            new(Guid.NewGuid(), "dir", SceneLightKind.Directional, Vector3.Zero,
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI), new Vector3(0.4f, 0.45f, 0.5f), 0.7f, 20f, CastShadows: true),
            new(Guid.NewGuid(), "pt-warm", SceneLightKind.Point, new Vector3(-2.5f, 1.0f, -2.5f),
                Quaternion.Identity, new Vector3(1.0f, 0.5f, 0.2f), 2.2f, 8f, CastShadows: true),
            new(Guid.NewGuid(), "spot-cool", SceneLightKind.Spot, new Vector3(2.5f, 1.5f, -3.0f),
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI), new Vector3(0.2f, 0.6f, 1.0f), 2.4f, 10f, 0.25f, 0.55f, CastShadows: true),
            new(Guid.NewGuid(), "area-fill", SceneLightKind.Area, new Vector3(0f, 3.0f, -2.0f),
                Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI * 0.4f), new Vector3(0.9f, 0.95f, 1.0f), 1.4f, 10f, CastShadows: false, AreaSize: new Vector2(2f, 1.5f)),
            new(Guid.NewGuid(), "far-culled", SceneLightKind.Point, new Vector3(40f, 0f, -2f),
                Quaternion.Identity, new Vector3(1f, 0f, 0f), 5f, 3f, CastShadows: false),
        };

        var forwardFrame = new OriginalFrameScene(baseFrame.Surface,
            options: baseFrame.Options! with { PipelineMode = RenderPipelineMode.Forward, Antialiasing = FrameAntialiasing.Smaa1X },
            materials: baseFrame.Materials, lighting: new SceneLightingSnapshot(lights));
        var clusteredFrame = new OriginalFrameScene(baseFrame.Surface,
            options: baseFrame.Options! with { PipelineMode = RenderPipelineMode.ClusteredForward, Antialiasing = FrameAntialiasing.Smaa1X },
            materials: baseFrame.Materials, lighting: new SceneLightingSnapshot(lights));

        var forwardImg = session.Capture(forwardFrame, 128, 96);
        Assert.Null(session.ClusteredLightStatistics);

        var clusteredImg = session.Capture(clusteredFrame, 128, 96);
        var stats = session.ClusteredLightStatistics;
        Assert.NotNull(stats);
        Assert.True(stats!.Active);
        Assert.Equal(16, stats.GridX);
        Assert.Equal(9, stats.GridY);
        Assert.Equal(24, stats.GridZ);
        Assert.Equal(5, stats.ActiveLightCount);
        Assert.Equal(2, stats.DispatchCount);

        int maxChannelDiff = 0;
        for (int i = 0; i < forwardImg.Rgba.Length; i++)
            maxChannelDiff = Math.Max(maxChannelDiff, Math.Abs(forwardImg.Rgba[i] - clusteredImg.Rgba[i]));
        Assert.InRange(maxChannelDiff, 0, 1);
    }

    [Fact]
    public void ClusteredForwardSupportsOrthographicAndSpatialSupersampling()
    {
        using var session = new D3D12FrameSession();
        var baseFrame = CreateLitBoxScene(orthographic: true);
        var light = new NativeSceneLight(Guid.NewGuid(), "pt", SceneLightKind.Point, new Vector3(0, 0, -2.5f),
            Quaternion.Identity, new Vector3(1f, 0.7f, 0.3f), 2f, 10f, CastShadows: false);

        var forward = new OriginalFrameScene(baseFrame.Surface,
            options: baseFrame.Options! with { PipelineMode = RenderPipelineMode.Forward, SpatialSamples = 2 },
            materials: baseFrame.Materials, lighting: new SceneLightingSnapshot([light]));
        var clustered = new OriginalFrameScene(baseFrame.Surface,
            options: baseFrame.Options! with { PipelineMode = RenderPipelineMode.ClusteredForward, SpatialSamples = 2 },
            materials: baseFrame.Materials, lighting: new SceneLightingSnapshot([light]));

        var fwdImg = session.Capture(forward, 96, 72);
        var cluImg = session.Capture(clustered, 96, 72);
        Assert.True(session.ClusteredLightStatistics?.Active);
        int maxDiff = 0;
        for (int i = 0; i < fwdImg.Rgba.Length; i++)
            maxDiff = Math.Max(maxDiff, Math.Abs(fwdImg.Rgba[i] - cluImg.Rgba[i]));
        Assert.InRange(maxDiff, 0, 1);
    }

    [Theory]
    [InlineData(PathTracingShadingMode.ToonHybrid, false)]
    [InlineData(PathTracingShadingMode.Physical, false)]
    [InlineData(PathTracingShadingMode.ToonHybrid, true)]
    [InlineData(PathTracingShadingMode.Physical, true)]
    public void PathTracingRendersValidShadowsAndRespectsHardwareOrSoftwareFallback(
        PathTracingShadingMode shadingMode, bool preferHardware)
    {
        using var session = new D3D12FrameSession();
        var caps = session.QueryRayGiCapabilities();
        if (!caps.Compute || caps.ShaderModel < 60 || !caps.CompilerAvailable)
            return;

        var litScene = CreateLitBoxScene(withOccluder: false);
        var shadowedScene = CreateLitBoxScene(withOccluder: true);
        var ptOptions = new PathTracingOptions(
            ShadingMode: shadingMode,
            SamplesPerFrame: 4,
            MaxBounces: 4,
            ProgressiveAccumulation: false,
            MaxAccumulatedFrames: 16,
            InteractiveSamplesPerFrame: 1,
            FireflyClamp: 10f,
            Denoiser: RayGiDenoiser.Off,
            UseHardwareRayTracing: preferHardware);

        var litFrame = new OriginalFrameScene(litScene.Surface,
            options: litScene.Options! with { PipelineMode = RenderPipelineMode.PathTracing, PathTracing = ptOptions },
            materials: litScene.Materials, lighting: litScene.Lighting);
        var shadowedFrame = new OriginalFrameScene(shadowedScene.Surface,
            options: shadowedScene.Options! with { PipelineMode = RenderPipelineMode.PathTracing, PathTracing = ptOptions },
            materials: shadowedScene.Materials, lighting: shadowedScene.Lighting);

        var litImg = session.Capture(litFrame, 96, 72);
        var stats = session.PathTracingStatistics;
        Assert.NotNull(stats);
        Assert.True(stats!.Active);
        Assert.Equal(shadingMode, stats.ShadingMode);
        Assert.Equal(preferHardware && caps.HardwareAvailable, stats.UsedHardwareRayTracing);
        Assert.Equal(4, stats.EffectiveSamplesPerFrame);
        Assert.Equal(4, stats.MaxBounces);

        var shadowedImg = session.Capture(shadowedFrame, 96, 72);
        int center = (36 * 96 + 48) * 4;
        int litLuma = litImg.Rgba[center] + litImg.Rgba[center + 1] + litImg.Rgba[center + 2];
        int shadowedLuma = shadowedImg.Rgba[center] + shadowedImg.Rgba[center + 1] + shadowedImg.Rgba[center + 2];
        Assert.True(litLuma > 30, $"Expected lit surface pixel > 30, got {litLuma}");
        Assert.True(litLuma > shadowedLuma + 10, $"Expected occluder shadow (lit={litLuma}, shadowed={shadowedLuma})");
    }

    [Fact]
    public void PathTracingProgressiveAccumulationConvergesAndResetsOnCameraChange()
    {
        using var session = new D3D12FrameSession();
        var caps = session.QueryRayGiCapabilities();
        if (!caps.Compute || caps.ShaderModel < 60 || !caps.CompilerAvailable)
            return;

        var baseScene = CreateLitBoxScene(withOccluder: true);
        var ptOptions = new PathTracingOptions(
            ShadingMode: PathTracingShadingMode.ToonHybrid,
            SamplesPerFrame: 1,
            MaxBounces: 4,
            ProgressiveAccumulation: true,
            MaxAccumulatedFrames: 3,
            FireflyClamp: 10f,
            Denoiser: RayGiDenoiser.TemporalSpatial,
            UseHardwareRayTracing: false);

        var frame = new OriginalFrameScene(baseScene.Surface,
            options: baseScene.Options! with { PipelineMode = RenderPipelineMode.PathTracing, PathTracing = ptOptions },
            materials: baseScene.Materials, lighting: baseScene.Lighting);

        // Frame 1: FirstFrame reset -> AccumulatedFrames = 1
        _ = session.Capture(frame, 64, 48);
        Assert.True(session.PathTracingStatistics!.HistoryReset);
        Assert.Equal(1, session.PathTracingStatistics.AccumulatedFrames);

        // Frame 2: Stationary -> accumulates to 2
        _ = session.Capture(frame, 64, 48);
        Assert.False(session.PathTracingStatistics!.HistoryReset);
        Assert.Equal("None", session.PathTracingStatistics.HistoryResetReason);
        Assert.Equal(2, session.PathTracingStatistics.AccumulatedFrames);

        // Frame 3: Stationary -> reaches MaxAccumulatedFrames = 3
        _ = session.Capture(frame, 64, 48);
        Assert.Equal(3, session.PathTracingStatistics!.AccumulatedFrames);

        // Frame 4: Already converged at cap -> stays at 3 with 0 trace dispatches (only denoise dispatch)
        _ = session.Capture(frame, 64, 48);
        Assert.Equal(3, session.PathTracingStatistics!.AccumulatedFrames);
        Assert.Equal(1, session.PathTracingStatistics.DispatchCount);

        // Frame 5: Move camera -> automatically resets progressive history!
        var movedCam = baseScene.Surface.Snapshot.Camera with { Eye = new Vector3(0.5f, 0f, -6.5f) };
        var movedFrame = new OriginalFrameScene(baseScene.Surface,
            options: frame.Options! with { CameraOverride = movedCam },
            materials: baseScene.Materials, lighting: baseScene.Lighting);
        _ = session.Capture(movedFrame, 64, 48);
        Assert.True(session.PathTracingStatistics!.HistoryReset);
        Assert.Equal("CameraChanged", session.PathTracingStatistics.HistoryResetReason);
        Assert.Equal(1, session.PathTracingStatistics.AccumulatedFrames);
    }

    [Fact]
    public async Task ViewRendererExecutesClusteredForwardAndPathTracingThroughViewQualityOptions()
    {
        var baseScene = CreateLitBoxScene(withOccluder: true);
        var ct = TestContext.Current.CancellationToken;
        var offline = new Mmd.Modern.Application.OfflineFrame(
            new Mmd.Modern.Application.OfflineFrameSample(0, new(0, 30), new(0, 1), new(0, 1)),
            64, 48, baseScene, null, null, null);

        var clusteredReq = new ViewRenderRequest(64, 48, new ViewQualityOptions(
            Antialiasing: FrameAntialiasing.Smaa1X,
            PipelineMode: RenderPipelineMode.ClusteredForward));
        await using (var renderer = new D3D12ViewRenderer(clusteredReq))
        {
            using var lease = await renderer.CaptureAsync(offline, ct);
            Assert.Equal(64, lease.Width);
            Assert.Equal(48, lease.Height);
            Assert.True(renderer.ClusteredLightStatistics?.Active);
        }

        var ptReq = new ViewRenderRequest(64, 48, new ViewQualityOptions(
            Gi: new RayGiOptions(LightingMode.SoftwareRayTracing, Strength: 4f, SamplesPerPixel: 4),
            Antialiasing: FrameAntialiasing.Off,
            SpatialSamples: 1,
            PipelineMode: RenderPipelineMode.PathTracing,
            PathTracing: new PathTracingOptions(SamplesPerFrame: 2, MaxBounces: 3, UseHardwareRayTracing: false)));
        await using (var ptRenderer = new D3D12ViewRenderer(ptReq))
        {
            using var lease = await ptRenderer.CaptureAsync(offline, ct);
            Assert.Equal(64, lease.Width);
            Assert.Equal(48, lease.Height);
            Assert.True(ptRenderer.PathTracingStatistics?.Active);
            Assert.Null(ptRenderer.RayGiStatistics);
        }
    }

    private static OriginalFrameScene CreateLitBoxScene(bool withOccluder = false, bool orthographic = false)
    {
        static SceneVertex[] Quad(float z, float half, Vector3 normal) =>
        [
            new(new(-half, -half, z), normal, new(0, 1)),
            new(new(half, -half, z), normal, new(1, 1)),
            new(new(half, half, z), normal, new(1, 0)),
            new(new(-half, half, z), normal, new(0, 0)),
        ];

        var vertices = new List<SceneVertex>();
        var indices = new List<uint>();
        var materials = new List<SceneMaterial>();
        var bindings = new List<OriginalStyleMaterialBinding>();
        var flags = new List<OriginalFrameMaterialFlags>();

        // Receiver backdrop quad at z = 2
        vertices.AddRange(Quad(2f, 4f, -Vector3.UnitZ));
        indices.AddRange([0u, 1u, 2u, 0u, 2u, 3u]);
        materials.Add(new SceneMaterial("receiver", 0, 6, new Vector4(0.85f, 0.85f, 0.9f, 1f),
            new Vector3(0.15f, 0.15f, 0.15f), new Vector3(0.4f, 0.4f, 0.45f), 16f));
        bindings.Add(new OriginalStyleMaterialBinding(ToonShadowColor: new Vector3(0.35f, 0.38f, 0.45f), DoubleSided: true));
        flags.Add(new OriginalFrameMaterialFlags(CastSelfShadow: false, ReceiveSelfShadow: true, CastGroundShadow: false));

        // Colored side wall for multi-bounce diffuse color bleed
        uint wallBase = (uint)vertices.Count;
        vertices.AddRange([
            new(new(-3.5f, -3.5f, -2f), Vector3.UnitX, new(0, 1)),
            new(new(-3.5f, -3.5f, 2f), Vector3.UnitX, new(1, 1)),
            new(new(-3.5f, 3.5f, 2f), Vector3.UnitX, new(1, 0)),
            new(new(-3.5f, 3.5f, -2f), Vector3.UnitX, new(0, 0)),
        ]);
        indices.AddRange([wallBase, wallBase + 1, wallBase + 2, wallBase, wallBase + 2, wallBase + 3]);
        materials.Add(new SceneMaterial("red-wall", 6, 6, new Vector4(0.95f, 0.15f, 0.1f, 1f),
            Vector3.Zero, new Vector3(0.4f, 0.05f, 0.05f), 4f));
        bindings.Add(new OriginalStyleMaterialBinding(ToonShadowColor: new Vector3(0.4f, 0.1f, 0.1f), DoubleSided: true));
        flags.Add(new OriginalFrameMaterialFlags(CastSelfShadow: true, ReceiveSelfShadow: true, CastGroundShadow: false));

        if (withOccluder)
        {
            uint occBase = (uint)vertices.Count;
            // Small occluder between light and center of receiver, outside camera's direct center ray.
            vertices.AddRange([
                new(new(-0.9f, 0.8f, 0.2f), -Vector3.UnitZ, new(0, 1)),
                new(new(0.9f, 0.8f, 0.2f), -Vector3.UnitZ, new(1, 1)),
                new(new(0.9f, 2.2f, 0.2f), -Vector3.UnitZ, new(1, 0)),
                new(new(-0.9f, 2.2f, 0.2f), -Vector3.UnitZ, new(0, 0)),
            ]);
            indices.AddRange([occBase, occBase + 1, occBase + 2, occBase, occBase + 2, occBase + 3]);
            materials.Add(new SceneMaterial("occluder", 12, 6, new Vector4(0.2f, 0.2f, 0.25f, 1f),
                Vector3.Zero, new Vector3(0.1f, 0.1f, 0.1f), 4f));
            bindings.Add(new OriginalStyleMaterialBinding(ToonShadowColor: new Vector3(0.2f, 0.2f, 0.2f), DoubleSided: true));
            flags.Add(new OriginalFrameMaterialFlags(CastSelfShadow: true, ReceiveSelfShadow: true, CastGroundShadow: false));
        }

        var camera = new SceneCamera(
            Eye: new Vector3(0, 0, -6.5f),
            Target: new Vector3(0, 0, 2f),
            Up: Vector3.UnitY,
            OrthographicHeight: 7f,
            Near: 0.1f,
            Far: 50f,
            VerticalFovDegrees: orthographic ? null : 50f);

        var lightDir = Vector3.Normalize(new Vector3(0f, 0.7f, -0.8f));
        var snapshot = new SceneSnapshot(vertices.ToArray(), indices.ToArray(), materials,
            camera: camera, lightDirection: lightDir);
        var style = new OriginalStyleScene(snapshot, bindings, OriginalStyleProfile.SelectedReference);
        var options = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false,
            BackgroundColor: new Vector4(0.08f, 0.1f, 0.14f, 1f));
        var nativeLight = new NativeSceneLight(Guid.NewGuid(), "key-pt", SceneLightKind.Point,
            new Vector3(0f, 1.4f, -1.2f), Quaternion.Identity, new Vector3(1f, 0.95f, 0.9f), 1.6f, 12f, CastShadows: true);
        return new OriginalFrameScene(style, options: options, materials: flags, lighting: new SceneLightingSnapshot([nativeLight]));
    }
}
