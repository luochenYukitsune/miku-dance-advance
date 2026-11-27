using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12Pr2AntialiasingAndShadingGpuTests
{
    [Theory]
    [InlineData(FrameAntialiasing.Msaa2X, 2)]
    [InlineData(FrameAntialiasing.Msaa4X, 4)]
    [InlineData(FrameAntialiasing.Msaa8X, 8)]
    public void HardwareMsaaSmoothsDiagonalAndKeepsOffExact(FrameAntialiasing mode, int expectedMaxSamples)
    {
        using var session = new D3D12FrameSession();
        var baseFrame = DiagonalTriangleScene();
        var baseline = session.Capture(baseFrame, 97, 73);
        var msaaFrame = WithOptions(baseFrame, baseFrame.Options! with { Antialiasing = mode });
        var resolved = session.Capture(msaaFrame, 97, 73);

        Assert.Equal(mode, session.Statistics!.Antialiasing);
        Assert.InRange(session.Statistics.MsaaSampleCount, 2, expectedMaxSamples);
        Assert.Contains("msaa:resolve", session.LastRecordings.Select(r => r.Channel));
        Assert.NotEqual(baseline.Rgba, resolved.Rgba);

        // Intermediate fractional edge pixels must appear after hardware MSAA resolve.
        int intermediate = CountIntermediateGrayPixels(resolved.Rgba);
        Assert.True(intermediate >= 10, $"Expected MSAA intermediate edge pixels, got {intermediate}.");

        // Returning to Off restores exact baseline bytes.
        Assert.Equal(baseline.Rgba, session.Capture(baseFrame, 97, 73).Rgba);
        Assert.Equal(1, session.Statistics.MsaaSampleCount);
    }

    [Fact]
    public void HardwareMsaaWorksWithRayGiGBufferAndWithSsao1xDepthResolve()
    {
        using var session = new D3D12FrameSession();
        var corner = ConcaveCornerScene();

        // MSAA 4x + Software Ray GI (1x G-Buffer alignment).
        var gi = new RayGiOptions(LightingMode.SoftwareRayTracing, Strength: 2f, SamplesPerPixel: 4, OcclusionStrength: 0f);
        var msaaGiFrame = WithOptions(corner, corner.Options! with { Antialiasing = FrameAntialiasing.Msaa4X, RayGi = gi });
        var msaaGi = session.Capture(msaaGiFrame, 96, 72, rayGiOptions: gi);
        Assert.Equal(96 * 72 * 4, msaaGi.Rgba.Length);
        Assert.Contains("msaa:resolve", session.LastRecordings.Select(r => r.Channel));
        Assert.NotNull(session.RayGiStatistics);

        // MSAA 4x + SSAO (1x depth resolve + GTAO).
        var msaaSsaoFrame = WithOptions(corner, corner.Options! with
        {
            Antialiasing = FrameAntialiasing.Msaa4X,
            ScreenSpaceAo = new(Enabled: true, Strength: 0.8f, Radius: 2.0f, SampleCount: 8),
        });
        var msaaSsao = session.Capture(msaaSsaoFrame, 96, 72);
        Assert.Equal(96 * 72 * 4, msaaSsao.Rgba.Length);
        Assert.Contains("msaa:resolve", session.LastRecordings.Select(r => r.Channel));
        Assert.Contains("msaa:depth-resolve", session.LastRecordings.Select(r => r.Channel));
        Assert.Contains("ssao:raw", session.LastRecordings.Select(r => r.Channel));
        Assert.Contains("ssao:composite", session.LastRecordings.Select(r => r.Channel));
        Assert.Equal(3, session.Statistics!.ScreenSpaceAoPassDrawCalls);
    }

    [Fact]
    public void SinglePassFxaa311SmoothsDiagonalAndPreservesConstantSurface()
    {
        using var session = new D3D12FrameSession();
        var baseFrame = DiagonalTriangleScene();
        var baseline = session.Capture(baseFrame, 97, 73);
        var fxaaFrame = WithOptions(baseFrame, baseFrame.Options! with { Antialiasing = FrameAntialiasing.Fxaa });
        var filtered = session.Capture(fxaaFrame, 97, 73);

        Assert.Equal(FrameAntialiasing.Fxaa, session.Statistics!.Antialiasing);
        Assert.Equal(1, session.Statistics.AntialiasingPassDrawCalls);
        Assert.Contains("fxaa:apply", session.LastRecordings.Select(r => r.Channel));
        Assert.NotEqual(baseline.Rgba, filtered.Rgba);
        Assert.True(CountIntermediateGrayPixels(filtered.Rgba) >= 10);

        // Straight-alpha FXAA executes cleanly without errors.
        var straightFrame = WithOptions(baseFrame, baseFrame.Options! with
        {
            Antialiasing = FrameAntialiasing.Fxaa,
            AlphaOutput = FrameAlphaOutput.Straight,
            BackgroundColor = new(0, 0, 0, 0),
        });
        var straight = session.Capture(straightFrame, 64, 48);
        Assert.Equal(64 * 48 * 4, straight.Rgba.Length);
    }

    [Fact]
    public void NativeTaaAccumulatesOverFramesResetsCleanlyAndPairsWithStandaloneRcas()
    {
        using var session = new D3D12FrameSession();
        var baseFrame = DiagonalTriangleScene();
        var taaFrame = WithOptions(baseFrame, baseFrame.Options! with { Antialiasing = FrameAntialiasing.Taa });

        // First frame resets history; subsequent static frames accumulate jittered subpixel samples.
        var frame0 = session.Capture(taaFrame, 97, 73);
        Assert.Equal(FrameAntialiasing.Taa, session.Statistics!.Antialiasing);
        Assert.Equal(1, session.Statistics.AntialiasingPassDrawCalls);
        Assert.Contains("taa:resolve", session.LastRecordings.Select(r => r.Channel));

        RenderedFrame accumulated = frame0;
        for (int i = 0; i < 6; i++)
            accumulated = session.Capture(taaFrame, 97, 73);

        Assert.NotEqual(frame0.Rgba, accumulated.Rgba);
        Assert.True(CountIntermediateGrayPixels(accumulated.Rgba) >= 10);

        // Explicit reset restores first-frame behavior cleanly.
        var resetFrame = session.Capture(taaFrame, 97, 73,
            temporalUpscaling: new D3D12TemporalUpscalingOptions { ResetHistory = true });
        Assert.Equal(frame0.Rgba, resetFrame.Rgba);

        // Standalone RCAS sharpening compared against TAA-without-RCAS at the exact same reset + 6-frame accumulation.
        RenderedFrame unsharpenedSameCount = resetFrame;
        for (int i = 0; i < 6; i++)
            unsharpenedSameCount = session.Capture(taaFrame, 97, 73);

        var resetForSharp = session.Capture(taaFrame, 97, 73,
            temporalUpscaling: new D3D12TemporalUpscalingOptions { ResetHistory = true });
        for (int i = 0; i < 5; i++)
            _ = session.Capture(taaFrame, 97, 73);
        var sharpenedSameCount = session.Capture(taaFrame, 97, 73, sharpening: new D3D12SharpeningOptions(true, 0.2f));

        Assert.Equal(1, session.Statistics.SharpeningPassDrawCalls);
        Assert.Contains("sharpening:rcas", session.LastRecordings.Select(r => r.Channel));
        Assert.NotEqual(unsharpenedSameCount.Rgba, sharpenedSameCount.Rgba);
    }

    [Fact]
    public void SpatialSupersamplingWorksWithScreenSpaceAo()
    {
        using var session = new D3D12FrameSession();
        var corner = ConcaveCornerScene();
        var ssaaNoAo = WithOptions(corner, corner.Options! with { SpatialSamples = 2 });
        var ssaaWithAo = WithOptions(corner, corner.Options! with
        {
            SpatialSamples = 2,
            ScreenSpaceAo = new(Enabled: true, Strength: 0.8f, Radius: 2.0f, SampleCount: 8),
        });

        var baseline = session.Capture(ssaaNoAo, 80, 60);
        var occluded = session.Capture(ssaaWithAo, 80, 60);
        Assert.Equal(2, session.Statistics!.SpatialSamples);
        Assert.Equal(4, session.Statistics.ScreenSpaceAoPassDrawCalls);
        Assert.NotEqual(baseline.Rgba, occluded.Rgba);
    }

    [Fact]
    public void StraightAlphaFxaaSmoothsOpaqueInteriorLumaEdge()
    {
        using var session = new D3D12FrameSession();
        // Full-screen opaque dark quad (alpha=1) with an opaque bright diagonal triangle in front (alpha=1),
        // rendered under Straight alpha output so alpha is uniformly 1 across the interior luma edge.
        SceneVertex[] vertices =
        [
            new(new(-3f, -3f, 0.5f), -Vector3.UnitZ, Vector2.Zero),
            new(new(3f, -3f, 0.5f), -Vector3.UnitZ, Vector2.Zero),
            new(new(3f, 3f, 0.5f), -Vector3.UnitZ, Vector2.Zero),
            new(new(-3f, 3f, 0.5f), -Vector3.UnitZ, Vector2.Zero),
            new(new(-0.8f, -0.7f, 0f), -Vector3.UnitZ, Vector2.Zero),
            new(new(0.8f, -0.4f, 0f), -Vector3.UnitZ, Vector2.Zero),
            new(new(-0.5f, 0.9f, 0f), -Vector3.UnitZ, Vector2.Zero),
        ];
        uint[] indices = [0, 1, 2, 0, 2, 3, 4, 5, 6];
        var snapshot = new SceneSnapshot(vertices, indices,
            [
                new("backdrop", 0, 6, new Vector4(0.05f, 0.05f, 0.05f, 1f), Vector3.Zero, Vector3.Zero, 1),
                new("triangle", 6, 3, Vector4.One, Vector3.Zero, Vector3.One, 1),
            ],
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 2f, 0.1f, 20f),
            lightDirection: -Vector3.UnitZ);
        var style = new OriginalStyleScene(snapshot,
            [new(new Vector3(0.05f), DoubleSided: true), new(Vector3.One, DoubleSided: true)]);
        var offFrame = new OriginalFrameScene(style, options: new(OriginalAlphaBlendMode.Normal, true, false,
            Antialiasing: FrameAntialiasing.Off, AlphaOutput: FrameAlphaOutput.Straight, BackgroundColor: new(0, 0, 0, 0)));
        var fxaaFrame = WithOptions(offFrame, offFrame.Options! with { Antialiasing = FrameAntialiasing.Fxaa });

        var off = session.Capture(offFrame, 97, 73);
        var fxaa = session.Capture(fxaaFrame, 97, 73);
        Assert.NotEqual(off.Rgba, fxaa.Rgba);
        Assert.True(CountIntermediateGrayPixels(fxaa.Rgba) > CountIntermediateGrayPixels(off.Rgba));
    }

    [Fact]
    public void PcfSoftShadowDoesNotSelfShadowUnoccludedSlopedSurface()
    {
        using var session = new D3D12FrameSession();
        // Single unoccluded sloped plane casting and receiving self-shadow (no occluder in front).
        SceneVertex[] vertices =
        [
            new(new(-2f, -2f, -0.8f), Vector3.Normalize(new(0.5f, 0.2f, -1f)), Vector2.Zero),
            new(new(2f, -2f, 0.8f), Vector3.Normalize(new(0.5f, 0.2f, -1f)), Vector2.Zero),
            new(new(2f, 2f, 0.8f), Vector3.Normalize(new(0.5f, 0.2f, -1f)), Vector2.Zero),
            new(new(-2f, 2f, -0.8f), Vector3.Normalize(new(0.5f, 0.2f, -1f)), Vector2.Zero),
        ];
        uint[] indices = [0, 1, 2, 0, 2, 3];
        var lightDir = Vector3.Normalize(new(0.6f, 0.3f, -1f));
        var snapshot = new SceneSnapshot(vertices, indices,
            [new("slope", 0, 6, Vector4.One, Vector3.Zero, Vector3.One, 1)],
            camera: new(new(0f, 0f, -5f), Vector3.Zero, Vector3.UnitY, 4f, 0.1f, 20f, VerticalFovDegrees: 40f),
            lightDirection: lightDir);
        var shadowVp = ModernDirectionalShadowProjection.Fit(snapshot);
        OriginalFrameScene Build(ShadowFilterMode mode, float softness)
        {
            var shadow = new OriginalSelfShadowOptions(shadowVp, 256, 256, true, false,
                OriginalTextureSampling.LinearClamp, OriginalDepthComparison.LessEqual)
            { FilterMode = mode, ShadowSoftness = softness };
            return new(new(snapshot, [new(new Vector3(0.2f), DoubleSided: true)]),
                options: new(OriginalAlphaBlendMode.Normal, true, false, SelfShadow: shadow)
                { ShadowFilter = mode, ShadowSoftness = softness },
                materials: [new OriginalFrameMaterialFlags(CastSelfShadow: true, ReceiveSelfShadow: true, CastGroundShadow: false)]);
        }

        var linear = session.Capture(Build(ShadowFilterMode.Linear, 1.0f), 96, 96);
        var pcf = session.Capture(Build(ShadowFilterMode.PcfSoft, 3.0f), 96, 96);

        // Interior pixels of the unoccluded sloped quad must stay unshadowed under PcfSoft (no slope acne).
        int darkAcnePixels = 0;
        for (int y = 24; y < 72; y++)
            for (int x = 24; x < 72; x++)
            {
                int idx = (y * 96 + x) * 4;
                if (linear.Rgba[idx] >= 200 && pcf.Rgba[idx] + 8 < linear.Rgba[idx])
                    darkAcnePixels++;
            }
        Assert.Equal(0, darkAcnePixels);
    }

    [Fact]
    public void StandaloneRcasWorksDecoupledFromFsr1AcrossAaModes()
    {
        using var session = new D3D12FrameSession();
        var baseFrame = DiagonalTriangleScene();
        var smaaFrame = WithOptions(baseFrame, baseFrame.Options! with { Antialiasing = FrameAntialiasing.Smaa1X });

        var unsharpened = session.Capture(smaaFrame, 97, 73);
        var sharpened = session.Capture(smaaFrame, 97, 73, sharpening: new D3D12SharpeningOptions(true, 0.1f));

        Assert.Equal(D3D12SpatialUpscalingMode.Off, session.Statistics!.SpatialUpscaling);
        Assert.Equal(1, session.Statistics.SharpeningPassDrawCalls);
        Assert.Contains("sharpening:rcas", session.LastRecordings.Select(r => r.Channel));
        Assert.NotEqual(unsharpened.Rgba, sharpened.Rgba);

        // Off sharpening returns exact unsharpened bytes.
        var offAgain = session.Capture(smaaFrame, 97, 73, sharpening: D3D12SharpeningOptions.Off);
        Assert.Equal(0, session.Statistics.SharpeningPassDrawCalls);
        Assert.Equal(unsharpened.Rgba, offAgain.Rgba);
    }

    [Fact]
    public void AnisotropicFilteringChangesObliqueMipmappedPlane()
    {
        using var session = new D3D12FrameSession();
        var plane = ObliqueCheckerPlaneScene();

        var aniso1 = session.Capture(plane, 128, 96);
        Assert.Equal(1, session.Statistics!.AnisotropicFiltering);

        var aniso16Frame = WithOptions(plane, plane.Options! with { AnisotropicFiltering = 16 });
        var aniso16 = session.Capture(aniso16Frame, 128, 96);
        Assert.Equal(16, session.Statistics.AnisotropicFiltering);
        Assert.NotEqual(aniso1.Rgba, aniso16.Rgba);
    }

    [Fact]
    public void PcfSoftShadowSoftensSelfShadowEdgeComparedToPointAndLinear()
    {
        using var session = new D3D12FrameSession();
        var shadowScene = SelfShadowScene(ShadowFilterMode.Linear, 1.0f);
        var linear = session.Capture(shadowScene, 96, 96);
        Assert.Equal(ShadowFilterMode.Linear, session.Statistics!.ShadowFilter);

        var point = session.Capture(SelfShadowScene(ShadowFilterMode.Point, 1.0f), 96, 96);
        Assert.Equal(ShadowFilterMode.Point, session.Statistics.ShadowFilter);

        var pcf = session.Capture(SelfShadowScene(ShadowFilterMode.PcfSoft, 2.5f), 96, 96);
        Assert.Equal(ShadowFilterMode.PcfSoft, session.Statistics.ShadowFilter);

        Assert.NotEqual(point.Rgba, pcf.Rgba);
        Assert.NotEqual(linear.Rgba, pcf.Rgba);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void ScreenSpaceAoDarkensConcaveCornerWhilePreservingBackground(int sampleCount)
    {
        using var session = new D3D12FrameSession();
        var corner = ConcaveCornerScene();
        var withoutAo = session.Capture(corner, 96, 72);
        var withAoFrame = WithOptions(corner, corner.Options! with
        {
            ScreenSpaceAo = new(Enabled: true, Strength: 0.9f, Radius: 2.5f, SampleCount: sampleCount),
        });
        var withAo = session.Capture(withAoFrame, 96, 72);

        Assert.Equal(2, session.Statistics!.ScreenSpaceAoPassDrawCalls);
        Assert.Contains("ssao:raw", session.LastRecordings.Select(r => r.Channel));
        Assert.Contains("ssao:composite", session.LastRecordings.Select(r => r.Channel));

        // Background top-left pixel (0, 0) must remain completely unchanged.
        for (int c = 0; c < 4; c++)
            Assert.Equal(withoutAo.Rgba[c], withAo.Rgba[c]);

        // Interior crease pixels must be measurably darker than without SSAO.
        long sumBefore = 0, sumAfter = 0;
        int darkened = 0;
        for (int i = 0; i < withoutAo.Rgba.Length; i += 4)
        {
            sumBefore += withoutAo.Rgba[i];
            sumAfter += withAo.Rgba[i];
            if (withAo.Rgba[i] + 3 < withoutAo.Rgba[i]) darkened++;
        }
        Assert.True(sumAfter < sumBefore, $"Expected SSAO to darken concave corner (before={sumBefore}, after={sumAfter}).");
        Assert.True(darkened >= 20, $"Expected at least 20 darkened crease pixels, got {darkened}.");
    }

    private static int CountIntermediateGrayPixels(byte[] rgba)
    {
        int count = 0;
        for (int i = 0; i < rgba.Length; i += 4)
            if (rgba[i] is > 25 and < 245) count++;
        return count;
    }

    private static OriginalFrameScene WithOptions(OriginalFrameScene frame, OriginalFrameOptions options)
        => new(frame.Surface, frame.Outline, frame.OutlineOptions, options, frame.Materials, frame.Lighting, frame.EditorOverlay);

    private static OriginalFrameScene DiagonalTriangleScene()
    {
        SceneVertex[] vertices =
        [
            new(new(-0.8f, -0.7f, 0f), -Vector3.UnitZ, Vector2.Zero),
            new(new(0.8f, -0.4f, 0f), -Vector3.UnitZ, Vector2.Zero),
            new(new(-0.5f, 0.9f, 0f), -Vector3.UnitZ, Vector2.Zero),
        ];
        var snapshot = new SceneSnapshot(vertices, [0u, 1u, 2u],
            [new("white", 0, 3, Vector4.One, Vector3.Zero, Vector3.One, 1)],
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 2f, 0.1f, 20f),
            lightDirection: -Vector3.UnitZ);
        return new(new(snapshot, [new(Vector3.One, DoubleSided: true)]),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: new(0, 0, 0, 1)));
    }

    private static OriginalFrameScene ConcaveCornerScene()
    {
        // Two perpendicular white quads meeting along y=0, z=0 forming a 90-degree interior corner.
        SceneVertex[] vertices =
        [
            // Floor quad (y = 0, z in [-2, 0])
            new(new(-2f, 0f, -2f), Vector3.UnitY, Vector2.Zero),
            new(new(2f, 0f, -2f), Vector3.UnitY, Vector2.Zero),
            new(new(2f, 0f, 0f), Vector3.UnitY, Vector2.Zero),
            new(new(-2f, 0f, 0f), Vector3.UnitY, Vector2.Zero),
            // Back wall quad (z = 0, y in [0, 2])
            new(new(-2f, 0f, 0f), -Vector3.UnitZ, Vector2.Zero),
            new(new(2f, 0f, 0f), -Vector3.UnitZ, Vector2.Zero),
            new(new(2f, 2f, 0f), -Vector3.UnitZ, Vector2.Zero),
            new(new(-2f, 2f, 0f), -Vector3.UnitZ, Vector2.Zero),
        ];
        uint[] indices = [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7];
        var snapshot = new SceneSnapshot(vertices, indices,
            [new("corner", 0, indices.Length, new Vector4(0.9f, 0.9f, 0.9f, 1f), Vector3.Zero, Vector3.One, 1)],
            camera: new(new(0f, 2.2f, -4.5f), new(0f, 0.4f, 0f), Vector3.UnitY, 4f, 0.1f, 30f, VerticalFovDegrees: 45f),
            lightDirection: Vector3.Normalize(new(0f, 1f, -1f)));
        return new(new(snapshot, [new(new Vector3(0.8f), DoubleSided: true)]),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: new(0.1f, 0.15f, 0.2f, 1f)));
    }

    private static OriginalFrameScene ObliqueCheckerPlaneScene()
    {
        const int size = 64;
        var checker = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                bool white = ((x / 4) + (y / 4)) % 2 == 0;
                byte v = white ? (byte)255 : (byte)20;
                int idx = (y * size + x) * 4;
                checker[idx] = v; checker[idx + 1] = v; checker[idx + 2] = v; checker[idx + 3] = 255;
            }
        var tex = new SceneTexture("checker", size, size, checker, SceneTextureKind.Image);
        SceneVertex[] vertices =
        [
            new(new(-8f, 0f, -1f), Vector3.UnitY, new(0f, 0f)),
            new(new(8f, 0f, -1f), Vector3.UnitY, new(16f, 0f)),
            new(new(8f, 0f, 30f), Vector3.UnitY, new(16f, 32f)),
            new(new(-8f, 0f, 30f), Vector3.UnitY, new(0f, 32f)),
        ];
        uint[] indices = [0, 1, 2, 0, 2, 3];
        var snapshot = new SceneSnapshot(vertices, indices,
            [new("plane", 0, 6, Vector4.One, Vector3.Zero, Vector3.One, 1, DiffuseTexture: 0)],
            textures: [tex],
            camera: new(new(0f, 0.8f, -2.5f), new(0f, 0.0f, 8f), Vector3.UnitY, 4f, 0.1f, 60f, VerticalFovDegrees: 45f),
            lightDirection: Vector3.UnitY);
        var style = new OriginalStyleScene(snapshot, [new(Vector3.One, DoubleSided: true)],
            OriginalStyleProfile.SelectedReference with { TextureSampling = OriginalTextureSampling.LinearWrap });
        return new(style,
            options: new(OriginalAlphaBlendMode.Normal, true, false),
            materials: [new OriginalFrameMaterialFlags(false, false, false) { UseMipmaps = true }]);
    }

    private static OriginalFrameScene SelfShadowScene(ShadowFilterMode filterMode, float softness)
    {
        // Receiver quad at z = 0 and smaller occluder quad at z = -1 casting shadow onto receiver.
        SceneVertex[] vertices =
        [
            // Receiver
            new(new(-2f, -2f, 0f), -Vector3.UnitZ, Vector2.Zero),
            new(new(2f, -2f, 0f), -Vector3.UnitZ, Vector2.Zero),
            new(new(2f, 2f, 0f), -Vector3.UnitZ, Vector2.Zero),
            new(new(-2f, 2f, 0f), -Vector3.UnitZ, Vector2.Zero),
            // Caster
            new(new(-0.6f, -0.6f, -1f), -Vector3.UnitZ, Vector2.Zero),
            new(new(0.6f, -0.4f, -1f), -Vector3.UnitZ, Vector2.Zero),
            new(new(0.5f, 0.7f, -1f), -Vector3.UnitZ, Vector2.Zero),
            new(new(-0.7f, 0.5f, -1f), -Vector3.UnitZ, Vector2.Zero),
        ];
        uint[] indices = [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7];
        var lightDir = Vector3.Normalize(new(0.25f, 0.2f, -1f));
        var snapshot = new SceneSnapshot(vertices, indices,
            [
                new("receiver", 0, 6, Vector4.One, Vector3.Zero, Vector3.One, 1),
                new("caster", 6, 6, Vector4.One, Vector3.Zero, Vector3.One, 1),
            ],
            camera: new(new(0.3f, 0.1f, -5f), Vector3.Zero, Vector3.UnitY, 4f, 0.1f, 20f, VerticalFovDegrees: 40f),
            lightDirection: lightDir);
        var shadowVp = ModernDirectionalShadowProjection.Fit(snapshot);
        var shadow = new OriginalSelfShadowOptions(shadowVp, 128, 128, true, false,
            filterMode == ShadowFilterMode.Point ? OriginalTextureSampling.PointClamp : OriginalTextureSampling.LinearClamp,
            OriginalDepthComparison.LessEqual)
        { FilterMode = filterMode, ShadowSoftness = softness };
        var style = new OriginalStyleScene(snapshot,
            [new(new Vector3(0.2f, 0.2f, 0.2f), DoubleSided: true), new(new Vector3(0.2f, 0.2f, 0.2f), DoubleSided: true)]);
        return new(style,
            options: new(OriginalAlphaBlendMode.Normal, true, false, SelfShadow: shadow)
            { ShadowFilter = filterMode, ShadowSoftness = softness },
            materials:
            [
                new OriginalFrameMaterialFlags(CastSelfShadow: false, ReceiveSelfShadow: true, CastGroundShadow: false),
                new OriginalFrameMaterialFlags(CastSelfShadow: true, ReceiveSelfShadow: false, CastGroundShadow: false),
            ]);
    }
}
