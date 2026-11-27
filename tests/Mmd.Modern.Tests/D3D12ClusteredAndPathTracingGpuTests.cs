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
    public void PathTracingRestoresResourceStatesAndHistoryOnSameSizeAndResizedAbort()
    {
        using var renderer = new D3D12FrameRenderer();
        var caps = renderer.QueryRayGiCapabilities();
        if (!caps.Compute || caps.ShaderModel < 60 || !caps.CompilerAvailable)
            return;

        var baseScene = CreateLitBoxScene(withOccluder: true);
        var ptOptions = new PathTracingOptions(
            ShadingMode: PathTracingShadingMode.ToonHybrid,
            SamplesPerFrame: 1,
            MaxBounces: 2,
            ProgressiveAccumulation: true,
            MaxAccumulatedFrames: 8,
            FireflyClamp: 10f,
            Denoiser: RayGiDenoiser.TemporalSpatial,
            UseHardwareRayTracing: false);
        var frame = new OriginalFrameScene(baseScene.Surface,
            options: baseScene.Options! with { PipelineMode = RenderPipelineMode.PathTracing, PathTracing = ptOptions },
            materials: baseScene.Materials, lighting: baseScene.Lighting);

        // 1. Commit frame 1 at 64x48.
        _ = renderer.Render(frame, 64, 48);
        Assert.Equal(1, renderer.PathTracingStatistics!.AccumulatedFrames);

        // 2. Abort a same-size recording after PathTracingRenderer.Record has transitioned persistent history buffers.
        Assert.Throws<InvalidOperationException>(() =>
            renderer.RecordGpu(frame, 64, 48, null, (_, _) => throw new InvalidOperationException("simulated same-size abort")));

        // 3. Subsequent same-size frame must recover cleanly without barrier mismatch and continue accumulation.
        _ = renderer.Render(frame, 64, 48);
        Assert.False(renderer.PathTracingStatistics!.HistoryReset);
        Assert.Equal(2, renderer.PathTracingStatistics.AccumulatedFrames);

        // 4. Abort a resized recording (80x60) before Submit; prior 64x48 history buffers & states must remain intact.
        Assert.Throws<InvalidOperationException>(() =>
            renderer.RecordGpu(frame, 80, 60, null, (_, _) => throw new InvalidOperationException("simulated resize abort")));

        // 5. Rendering at 64x48 again continues cleanly from accumulated frame 2 -> 3.
        _ = renderer.Render(frame, 64, 48);
        Assert.False(renderer.PathTracingStatistics!.HistoryReset);
        Assert.Equal(3, renderer.PathTracingStatistics.AccumulatedFrames);

        // 6. Committing resize to 80x60 resets history cleanly and swaps buffers.
        _ = renderer.Render(frame, 80, 60);
        Assert.True(renderer.PathTracingStatistics!.HistoryReset);
        Assert.Equal("Resized", renderer.PathTracingStatistics.HistoryResetReason);
        Assert.Equal(1, renderer.PathTracingStatistics.AccumulatedFrames);
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

    [Fact]
    public void PathTracingToonHybridWithStraightAlphaAndCharacterOutlineRendersCleanly()
    {
        using var renderer = new D3D12FrameRenderer();
        var caps = renderer.QueryRayGiCapabilities();
        if (!caps.Compute || caps.ShaderModel < 60 || !caps.CompilerAvailable)
            return;

        var vertices = new List<SceneVertex>();
        var outlineVertices = new List<OriginalOutlineVertex>();
        var indices = new List<uint>();

        static void AddBox(
            List<SceneVertex> verts,
            List<OriginalOutlineVertex> outVerts,
            List<uint> idx,
            Vector3 min,
            Vector3 max,
            int materialIndex)
        {
            void AddFace(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 normal)
            {
                uint b = (uint)verts.Count;
                Vector3[] pts = [p0, p1, p2, p3];
                Vector2[] uvs = [new(0, 1), new(1, 1), new(1, 0), new(0, 0)];
                for (int i = 0; i < 4; i++)
                {
                    verts.Add(new SceneVertex(pts[i], normal, uvs[i]));
                    outVerts.Add(new OriginalOutlineVertex(pts[i], normal, OriginalOutlineVertexMode.Expand, 1f, materialIndex));
                }
                idx.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
            }

            AddFace(new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z), -Vector3.UnitZ);
            AddFace(new(max.X, min.Y, max.Z), new(min.X, min.Y, max.Z), new(min.X, max.Y, max.Z), new(max.X, max.Y, max.Z), Vector3.UnitZ);
            AddFace(new(min.X, min.Y, max.Z), new(min.X, min.Y, min.Z), new(min.X, max.Y, min.Z), new(min.X, max.Y, max.Z), -Vector3.UnitX);
            AddFace(new(max.X, min.Y, min.Z), new(max.X, min.Y, max.Z), new(max.X, max.Y, max.Z), new(max.X, max.Y, min.Z), Vector3.UnitX);
            AddFace(new(min.X, max.Y, min.Z), new(max.X, max.Y, min.Z), new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z), Vector3.UnitY);
            AddFace(new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(max.X, min.Y, min.Z), new(min.X, min.Y, min.Z), -Vector3.UnitY);
        }

        // Material 0 (left box): original outline pass; Material 1 (right box): modern character outline pass.
        AddBox(vertices, outlineVertices, indices, new(-1.4f, -0.8f, 1.5f), new(-0.15f, 0.8f, 2.5f), 0);
        int mat0IndexCount = indices.Count;
        AddBox(vertices, outlineVertices, indices, new(0.15f, -0.8f, 1.5f), new(1.4f, 0.8f, 2.5f), 1);
        int mat1IndexCount = indices.Count - mat0IndexCount;

        SceneMaterial[] materials =
        [
            new("orig-outline-box", 0, mat0IndexCount, new Vector4(0.85f, 0.82f, 0.78f, 1f),
                new Vector3(0.2f, 0.2f, 0.2f), new Vector3(0.3f, 0.3f, 0.3f), 8f),
            new("char-outline-box", mat0IndexCount, mat1IndexCount, new Vector4(0.78f, 0.85f, 0.92f, 1f),
                new Vector3(0.2f, 0.2f, 0.2f), new Vector3(0.3f, 0.3f, 0.3f), 8f),
        ];
        OriginalStyleMaterialBinding[] bindings =
        [
            new(ToonShadowColor: new Vector3(0.4f, 0.4f, 0.45f), DoubleSided: true),
            new(ToonShadowColor: new Vector3(0.38f, 0.42f, 0.48f), DoubleSided: true),
        ];
        OriginalFrameMaterialFlags[] flags =
        [
            new(CastSelfShadow: true, ReceiveSelfShadow: true, CastGroundShadow: false) { OutlineWidthScale = 1f },
            new(CastSelfShadow: true, ReceiveSelfShadow: true, CastGroundShadow: false)
            {
                OutlineWidthScale = 1f,
                CharacterOutlineStyle = new CharacterOutlineStyle(Width: 0.35f, Color: new Vector4(0.1f, 0.2f, 0.85f, 1f), Enabled: true),
            },
        ];
        OriginalOutlineMaterial[] outlineMaterials =
        [
            new(Width: 1.2f, Color: new Vector4(0.08f, 0.06f, 0.1f, 1f), DrawEnabled: true),
            new(Width: 1.2f, Color: new Vector4(0.08f, 0.06f, 0.1f, 1f), DrawEnabled: true),
        ];

        var camera = new SceneCamera(
            Eye: new Vector3(0f, 0f, -6.5f),
            Target: new Vector3(0f, 0f, 2f),
            Up: Vector3.UnitY,
            OrthographicHeight: 7f,
            Near: 0.1f,
            Far: 50f,
            VerticalFovDegrees: 50f);
        var snapshot = new SceneSnapshot(vertices.ToArray(), indices.ToArray(), materials,
            camera: camera, lightDirection: Vector3.Normalize(new Vector3(0.2f, 0.6f, -0.8f)));
        var style = new OriginalStyleScene(snapshot, bindings, OriginalStyleProfile.SelectedReference);
        var outline = new OriginalOutlineData(outlineVertices, outlineMaterials);
        var outlineOptions = new OriginalOutlineOptions(
            ReferencePoint: new Vector4(0f, 0f, 2f, 1f),
            DistanceTransform: Matrix4x4.Identity,
            CameraPosition: camera.Eye,
            FovDegrees: 50f,
            ModelWidth: 1f,
            Eligible: true);
        var ptOptions = new PathTracingOptions(
            ShadingMode: PathTracingShadingMode.ToonHybrid,
            SamplesPerFrame: 2,
            MaxBounces: 2,
            ProgressiveAccumulation: true,
            MaxAccumulatedFrames: 8,
            FireflyClamp: 10f,
            Denoiser: RayGiDenoiser.TemporalSpatial,
            UseHardwareRayTracing: false);
        var options = new OriginalFrameOptions(
            OriginalAlphaBlendMode.Normal,
            WriteDepth: true,
            Transp: false,
            BackgroundColor: new Vector4(0f, 0f, 0f, 0f),
            AlphaOutput: FrameAlphaOutput.Straight,
            PipelineMode: RenderPipelineMode.PathTracing,
            PathTracing: ptOptions);
        var frame = new OriginalFrameScene(style, outline, outlineOptions, options, flags);

        var img1 = renderer.Render(frame, 96, 72);
        Assert.True(renderer.PathTracingStatistics!.Active);
        Assert.Equal(1, renderer.PathTracingStatistics.AccumulatedFrames);
        Assert.True(renderer.LastSceneDrawCalls >= 2, $"Expected both original & character outline draws, got {renderer.LastSceneDrawCalls}");

        var img2 = renderer.Render(frame, 96, 72);
        Assert.Equal(2, renderer.PathTracingStatistics!.AccumulatedFrames);

        // Background corner remains clean transparent zero (no premultiplied bleed or alpha corruption).
        int corner = (2 * 96 + 2) * 4;
        Assert.Equal(0, img2.Rgba[corner + 3]);
        Assert.Equal(0, img2.Rgba[corner]);
        Assert.Equal(0, img2.Rgba[corner + 1]);
        Assert.Equal(0, img2.Rgba[corner + 2]);

        // Both boxes are opaque and lit; frame 1 and frame 2 both stay valid.
        int leftBox = (36 * 96 + 38) * 4;
        int rightBox = (36 * 96 + 58) * 4;
        Assert.True(img1.Rgba[leftBox + 3] >= 250 && img2.Rgba[leftBox + 3] >= 250);
        Assert.True(img1.Rgba[rightBox + 3] >= 250 && img2.Rgba[rightBox + 3] >= 250);
        Assert.True(img2.Rgba[leftBox] + img2.Rgba[leftBox + 1] + img2.Rgba[leftBox + 2] > 40);
        Assert.True(img2.Rgba[rightBox] + img2.Rgba[rightBox + 1] + img2.Rgba[rightBox + 2] > 40);
    }

    [Theory]
    [InlineData(PathTracingShadingMode.ToonHybrid)]
    [InlineData(PathTracingShadingMode.Physical)]
    public void PathTracingRendersDistantGeometryBeyondThousandUnits(PathTracingShadingMode shadingMode)
    {
        using var session = new D3D12FrameSession();
        var caps = session.QueryRayGiCapabilities();
        if (!caps.Compute || caps.ShaderModel < 60 || !caps.CompilerAvailable)
            return;

        const float dist = 2500f;
        const float half = 1500f;
        SceneVertex[] vertices =
        [
            new(new(-half, -half, dist), -Vector3.UnitZ, new(0, 1)),
            new(new(half, -half, dist), -Vector3.UnitZ, new(1, 1)),
            new(new(half, half, dist), -Vector3.UnitZ, new(1, 0)),
            new(new(-half, half, dist), -Vector3.UnitZ, new(0, 0)),
        ];
        uint[] indices = [0u, 1u, 2u, 0u, 2u, 3u];
        SceneMaterial[] materials =
        [
            new("distant-quad", 0, 6, new Vector4(0.95f, 0.9f, 0.85f, 1f),
                new Vector3(0.3f, 0.3f, 0.3f), new Vector3(0.2f, 0.2f, 0.2f), 8f),
        ];
        OriginalStyleMaterialBinding[] bindings =
        [
            new(ToonShadowColor: new Vector3(0.45f, 0.45f, 0.45f), DoubleSided: true),
        ];
        OriginalFrameMaterialFlags[] flags =
        [
            new(CastSelfShadow: false, ReceiveSelfShadow: false, CastGroundShadow: false),
        ];
        var camera = new SceneCamera(
            Eye: Vector3.Zero,
            Target: new Vector3(0f, 0f, dist),
            Up: Vector3.UnitY,
            OrthographicHeight: 2000f,
            Near: 0.5f,
            Far: 10000f,
            VerticalFovDegrees: 50f);
        var snapshot = new SceneSnapshot(vertices, indices, materials,
            camera: camera, lightDirection: Vector3.Normalize(new Vector3(0f, 0.2f, -1f)));
        var style = new OriginalStyleScene(snapshot, bindings, OriginalStyleProfile.SelectedReference);
        var ptOptions = new PathTracingOptions(
            ShadingMode: shadingMode,
            SamplesPerFrame: 2,
            MaxBounces: 2,
            ProgressiveAccumulation: false,
            FireflyClamp: 10f,
            Denoiser: RayGiDenoiser.Off,
            UseHardwareRayTracing: false);
        var options = new OriginalFrameOptions(
            OriginalAlphaBlendMode.Normal,
            WriteDepth: true,
            Transp: false,
            BackgroundColor: new Vector4(0f, 0f, 0f, 1f),
            PipelineMode: RenderPipelineMode.PathTracing,
            PathTracing: ptOptions);
        var frame = new OriginalFrameScene(style, options: options, materials: flags);

        var img = session.Capture(frame, 64, 48);
        int center = (24 * 64 + 32) * 4;
        Assert.True(img.Rgba[center] > 60 && img.Rgba[center + 1] > 60 && img.Rgba[center + 2] > 60,
            $"Expected distant geometry at z={dist} to be hit rather than clipped at 1000 (RGBA={img.Rgba[center]},{img.Rgba[center + 1]},{img.Rgba[center + 2]},{img.Rgba[center + 3]}).");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PathTracingPhysicalEnclosedBoxDoesNotLeakEnvironmentStrength(bool preferHardware)
    {
        using var session = new D3D12FrameSession();
        var caps = session.QueryRayGiCapabilities();
        if (!caps.Compute || caps.ShaderModel < 60 || !caps.CompilerAvailable)
            return;

        OriginalFrameScene BuildRoomScene(bool enclosed)
        {
            var verts = new List<SceneVertex>();
            var idx = new List<uint>();
            void AddWall(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 normal)
            {
                uint b = (uint)verts.Count;
                verts.AddRange([
                    new(p0, normal, new(0, 1)),
                    new(p1, normal, new(1, 1)),
                    new(p2, normal, new(1, 0)),
                    new(p3, normal, new(0, 0)),
                ]);
                idx.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
            }

            const float s = 4f;
            // Front wall facing the camera at z = +s (inward normal = -Z)
            AddWall(new(-s, -s, s), new(s, -s, s), new(s, s, s), new(-s, s, s), -Vector3.UnitZ);
            if (enclosed)
            {
                // Back, left, right, floor, ceiling completely enclosing the camera at (0, 0, -1.5)
                AddWall(new(s, -s, -s), new(-s, -s, -s), new(-s, s, -s), new(s, s, -s), Vector3.UnitZ);
                AddWall(new(-s, -s, -s), new(-s, -s, s), new(-s, s, s), new(-s, s, -s), Vector3.UnitX);
                AddWall(new(s, -s, s), new(s, -s, -s), new(s, s, -s), new(s, s, s), -Vector3.UnitX);
                AddWall(new(-s, -s, -s), new(s, -s, -s), new(s, -s, s), new(-s, -s, s), Vector3.UnitY);
                AddWall(new(-s, s, s), new(s, s, s), new(s, s, -s), new(-s, s, -s), -Vector3.UnitY);
            }

            SceneMaterial[] materials =
            [
                new("room-wall", 0, idx.Count, new Vector4(0.9f, 0.9f, 0.9f, 1f),
                    Vector3.Zero, Vector3.Zero, 4f),
            ];
            OriginalStyleMaterialBinding[] bindings =
            [
                new(ToonShadowColor: Vector3.Zero, DoubleSided: true),
            ];
            OriginalFrameMaterialFlags[] flags =
            [
                new(CastSelfShadow: true, ReceiveSelfShadow: true, CastGroundShadow: false),
            ];
            var camera = new SceneCamera(
                Eye: new Vector3(0f, 0f, -1.5f),
                Target: new Vector3(0f, 0f, s),
                Up: Vector3.UnitY,
                OrthographicHeight: 6f,
                Near: 0.1f,
                Far: 50f,
                VerticalFovDegrees: 50f);
            // Primary light comes from behind the front wall (+Z) so open single-wall NEE sun is 0,
            // isolating occluded sky bounces vs unoccluded environment leak.
            var snapshot = new SceneSnapshot(verts.ToArray(), idx.ToArray(), materials,
                camera: camera, lightDirection: Vector3.UnitZ);
            var style = new OriginalStyleScene(snapshot, bindings, OriginalStyleProfile.SelectedReference);
            var ptOptions = new PathTracingOptions(
                ShadingMode: PathTracingShadingMode.Physical,
                SamplesPerFrame: 4,
                MaxBounces: 4,
                ProgressiveAccumulation: false,
                FireflyClamp: 10f,
                Denoiser: RayGiDenoiser.Off,
                UseHardwareRayTracing: preferHardware);
            var options = new OriginalFrameOptions(
                OriginalAlphaBlendMode.Normal,
                WriteDepth: true,
                Transp: false,
                BackgroundColor: new Vector4(0f, 0f, 0f, 1f),
                PipelineMode: RenderPipelineMode.PathTracing,
                PathTracing: ptOptions);
            var lighting = new SceneLightingSnapshot([], settings: new SceneLightingSettings(EnvironmentStrength: 2.0f));
            return new OriginalFrameScene(style, options: options, materials: flags, lighting: lighting);
        }

        var closedImg = session.Capture(BuildRoomScene(enclosed: true), 64, 48);
        int maxClosedChannel = 0;
        for (int i = 0; i < closedImg.Rgba.Length; i += 4)
            maxClosedChannel = Math.Max(maxClosedChannel, Math.Max(closedImg.Rgba[i], Math.Max(closedImg.Rgba[i + 1], closedImg.Rgba[i + 2])));
        Assert.True(maxClosedChannel <= 1, $"Enclosed physical room must not leak EnvironmentStrength; max RGB channel was {maxClosedChannel}.");

        var openImg = session.Capture(BuildRoomScene(enclosed: false), 64, 48);
        int center = (24 * 64 + 32) * 4;
        int openLuma = openImg.Rgba[center] + openImg.Rgba[center + 1] + openImg.Rgba[center + 2];
        Assert.True(openLuma > 30, $"Open physical wall should still receive occluded sky bounce from EnvironmentStrength; center luma was {openLuma}.");
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
