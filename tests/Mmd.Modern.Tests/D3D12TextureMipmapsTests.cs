using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12TextureMipmapsTests
{
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 2)]
    [InlineData(4, 4, 3)]
    [InlineData(4, 1, 3)]
    [InlineData(1, 4, 3)]
    [InlineData(5, 3, 3)]
    [InlineData(256, 256, 9)]
    [InlineData(4096, 4096, 13)]
    public void MipLevelCountMatchesExpectedChainLength(int width, int height, int expected)
        => Assert.Equal(expected, D3D12TextureMipmaps.MipLevelCount(width, height));

    [Theory]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    [InlineData(-2, 4)]
    public void MipLevelCountRejectsNonPositiveDimensions(int width, int height)
        => Assert.Throws<ArgumentOutOfRangeException>(() => D3D12TextureMipmaps.MipLevelCount(width, height));

    [Fact]
    public void BuildMipChainDownsamplesCheckerboardAndNonPowerOfTwoTo1x1()
    {
        var checker = new byte[4 * 4 * 4];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                byte v = (byte)((x + y) % 2 == 0 ? 255 : 0);
                int p = (y * 4 + x) * 4;
                checker[p] = v; checker[p + 1] = v; checker[p + 2] = v; checker[p + 3] = 255;
            }
        }
        var chain = D3D12TextureMipmaps.BuildMipChain(4, 4, checker);
        Assert.Equal(3, chain.Length);
        Assert.Equal((4, 4), (chain[0].Width, chain[0].Height));
        Assert.Equal((2, 2), (chain[1].Width, chain[1].Height));
        Assert.Equal((1, 1), (chain[2].Width, chain[2].Height));
        Assert.Equal(checker, chain[0].Pixels);
        for (int i = 0; i < 4; i++)
        {
            int p = i * 4;
            Assert.Equal((128, 128, 128, 255), (chain[1].Pixels[p], chain[1].Pixels[p + 1], chain[1].Pixels[p + 2], chain[1].Pixels[p + 3]));
        }
        Assert.Equal(new byte[] { 128, 128, 128, 255 }, chain[2].Pixels);

        var npot = new byte[5 * 3 * 4];
        for (int i = 0; i < 5 * 3; i++)
        {
            int p = i * 4;
            npot[p] = 100; npot[p + 1] = 150; npot[p + 2] = 200; npot[p + 3] = 220;
        }
        var npotChain = D3D12TextureMipmaps.BuildMipChain(5, 3, npot);
        Assert.Equal(3, npotChain.Length);
        Assert.Equal((2, 1), (npotChain[1].Width, npotChain[1].Height));
        Assert.Equal((1, 1), (npotChain[2].Width, npotChain[2].Height));
        Assert.Equal(new byte[] { 100, 150, 200, 220 }, npotChain[2].Pixels);
    }

    [Fact]
    public void BuildMipChainAlphaWeightingPreventsBlackEdgesOnCutoutAndZeroAlpha()
    {
        byte[] fringe =
        [
            255, 160, 80, 255,
            0, 0, 0, 0,
            0, 0, 0, 0,
            0, 0, 0, 0,
        ];
        var fringeChain = D3D12TextureMipmaps.BuildMipChain(2, 2, fringe);
        Assert.Equal(2, fringeChain.Length);
        Assert.Equal(new byte[] { 255, 160, 80, 64 }, fringeChain[1].Pixels);

        byte[] allTransparent =
        [
            200, 100, 50, 0,
            200, 100, 50, 0,
            200, 100, 50, 0,
            200, 100, 50, 0,
        ];
        var zeroAlphaChain = D3D12TextureMipmaps.BuildMipChain(2, 2, allTransparent);
        Assert.Equal(new byte[] { 200, 100, 50, 0 }, zeroAlphaChain[1].Pixels);
    }

    [Fact]
    public void ComputeMipmapTexturesAllowsOnlyExclusiveImageDiffuseUsedByMipEnabledMaterials()
    {
        var texEnvOnly = new SceneTexture("env-only", 4, 4, new byte[4 * 4 * 4]);
        var texCharOnly = new SceneTexture("char-only", 4, 4, new byte[4 * 4 * 4]);
        var texSharedDiffuse = new SceneTexture("shared-diffuse", 4, 4, new byte[4 * 4 * 4]);
        var texSharedToon = new SceneTexture("shared-toon", 4, 4, new byte[4 * 4 * 4]);
        var texSharedSphere = new SceneTexture("shared-sphere", 4, 4, new byte[4 * 4 * 4]);
        var texProceduralToon = SceneTexture.ProceduralToon(1);
        var texUnused = new SceneTexture("unused", 4, 4, new byte[4 * 4 * 4]);

        SceneVertex[] verts =
        [
            new(new(-1, -1, 0), -Vector3.UnitZ, Vector2.Zero),
            new(new(1, -1, 0), -Vector3.UnitZ, Vector2.UnitX),
            new(new(1, 1, 0), -Vector3.UnitZ, Vector2.One),
        ];
        uint[] idx = [0, 2, 1];
        SceneMaterial Mat(string name, int diff, int toon = -1)
            => new(name, 0, 0, Vector4.One, Vector3.Zero, Vector3.Zero, 1, diff, toon);

        var materials = new[]
        {
            Mat("env", 0) with { StartIndex = 0, IndexCount = 3 },
            Mat("char", 1) with { StartIndex = 3, IndexCount = 0 },
            Mat("shared-env", 2) with { StartIndex = 3, IndexCount = 0 },
            Mat("shared-char", 2) with { StartIndex = 3, IndexCount = 0 },
            Mat("env-with-toon", 3, toon: 3) with { StartIndex = 3, IndexCount = 0 },
            Mat("env-with-sphere", 4) with { StartIndex = 3, IndexCount = 0 },
            Mat("env-with-proc-toon-as-diffuse", 5) with { StartIndex = 3, IndexCount = 0 },
        };
        var bindings = new[]
        {
            new OriginalStyleMaterialBinding(Vector3.One),
            new OriginalStyleMaterialBinding(Vector3.One),
            new OriginalStyleMaterialBinding(Vector3.One),
            new OriginalStyleMaterialBinding(Vector3.One),
            new OriginalStyleMaterialBinding(Vector3.One),
            new OriginalStyleMaterialBinding(Vector3.One, SphereTexture: 4, SphereMode: OriginalSphereMode.Multiply),
            new OriginalStyleMaterialBinding(Vector3.One),
        };
        var flags = new[]
        {
            new OriginalFrameMaterialFlags(false, false, false) { UseMipmaps = true },
            new OriginalFrameMaterialFlags(false, false, false) { UseMipmaps = false },
            new OriginalFrameMaterialFlags(false, false, false) { UseMipmaps = true },
            new OriginalFrameMaterialFlags(false, false, false) { UseMipmaps = false },
            new OriginalFrameMaterialFlags(false, false, false) { UseMipmaps = true },
            new OriginalFrameMaterialFlags(false, false, false) { UseMipmaps = true },
            new OriginalFrameMaterialFlags(false, false, false) { UseMipmaps = true },
        };
        var snapshot = new SceneSnapshot(
            verts, idx, materials,
            [texEnvOnly, texCharOnly, texSharedDiffuse, texSharedToon, texSharedSphere, texProceduralToon, texUnused]);
        var frame = new OriginalFrameScene(new(snapshot, bindings), materials: flags);

        bool[] result = D3D12FrameRenderer.ComputeMipmapTextures(frame);
        Assert.Equal(new[] { true, false, false, false, false, false, false }, result);
    }
}

[Trait("Category", "Gpu")]
public sealed class D3D12TextureMipmapsGpuTests
{
    [Theory]
    [InlineData(OriginalTextureSampling.LinearWrap)]
    [InlineData(OriginalTextureSampling.LinearClamp)]
    [InlineData(OriginalTextureSampling.PointWrap)]
    [InlineData(OriginalTextureSampling.PointClamp)]
    public void CharacterAndUnspecifiedPathsMatchSingleMipBaselineByteForByte(OriginalTextureSampling sampling)
    {
        var charPlane = ObliqueCheckerPlane(zNear: 1f, zFar: 55f, uvRepeat: 18f, sampling, useMipmaps: false);
        var policyChar = SceneModelShadingPolicy.Apply(charPlane, ModelRole.Character);
        var policyUnspecified = SceneModelShadingPolicy.Apply(charPlane, ModelRole.Unspecified);

        // Render baseline scene containing only UseMipmaps=false (1-mip resource)
        byte[] baselineRgba;
        using (var baselineSession = new D3D12FrameSession())
        {
            baselineRgba = baselineSession.Capture(charPlane, 256, 192).Rgba;
            Assert.Empty(baselineSession.OriginalRecorder.LastDebugMessages);
        }

        // Render Character & Unspecified policies inside a session that also uploads a mipmapped texture
        var envPlane = ObliqueCheckerPlane(zNear: 1f, zFar: 55f, uvRepeat: 18f, sampling, useMipmaps: true);
        using var mixedSession = new D3D12FrameSession();
        _ = mixedSession.Capture(envPlane, 256, 192);
        var charResult = mixedSession.Capture(policyChar, 256, 192);
        var unspecResult = mixedSession.Capture(policyUnspecified, 256, 192);

        Assert.Equal(baselineRgba, charResult.Rgba);
        Assert.Equal(baselineRgba, unspecResult.Rgba);
        Assert.Empty(mixedSession.OriginalRecorder.LastDebugMessages);
    }

    [Fact]
    public void GpuCacheIsolatesSingleMipAndFullMipResourcesAnd1x1ReusesSingleMip()
    {
        var tex64 = CheckerTexture(64, 64, 16, 240);
        var tex1x1 = new SceneTexture("one-by-one", 1, 1, [90, 140, 200, 255]);
        using var runtime = new D3D12GpuRuntime();
        using var cache = new D3D12GpuCache(runtime);

        var ctx = runtime.Begin();
        try
        {
            var single = cache.Texture(tex64);
            var singleExplicit = cache.Texture(tex64, generateMipmaps: false);
            var full = cache.Texture(tex64, generateMipmaps: true);

            Assert.Same(single, singleExplicit);
            Assert.NotSame(single, full);
            Assert.Equal((ushort)1, single.Description.MipLevels);
            Assert.Equal((ushort)D3D12TextureMipmaps.MipLevelCount(64, 64), full.Description.MipLevels);
            Assert.Same(full, cache.Texture(tex64, generateMipmaps: true));

            var oneA = cache.Texture(tex1x1, generateMipmaps: false);
            var oneB = cache.Texture(tex1x1, generateMipmaps: true);
            Assert.Same(oneA, oneB);
            Assert.Equal((ushort)1, oneA.Description.MipLevels);
            runtime.Wait(runtime.Submit());
        }
        finally
        {
            runtime.Abort();
        }
    }

    [Theory]
    [InlineData(ModelRole.Environment)]
    [InlineData(ModelRole.Prop)]
    public void HighFrequencyCheckerboardUnderObliqueDistanceReducesAliasingWithoutBlackEdges(ModelRole role)
    {
        var baseScene = ObliqueCheckerPlane(zNear: 1f, zFar: 65f, uvRepeat: 24f, OriginalTextureSampling.LinearWrap, useMipmaps: false);
        var mipScene = SceneModelShadingPolicy.Apply(baseScene, role);

        // Also test slight camera jitter to measure temporal shimmering reduction in the far band.
        var jitterCamera = baseScene.Surface.Snapshot.Camera with
        {
            ProjectionJitterPixels = new Vector2(0.37f, 0.29f),
        };
        var baseJittered = new OriginalFrameScene(baseScene.Surface, baseScene.Outline, baseScene.OutlineOptions,
            baseScene.Options! with { CameraOverride = jitterCamera }, baseScene.Materials);
        var mipJittered = SceneModelShadingPolicy.Apply(baseJittered, role);

        using var session = new D3D12FrameSession();
        var noMipA = session.Capture(baseScene, 256, 192);
        var noMipB = session.Capture(baseJittered, 256, 192);
        var mipA = session.Capture(mipScene, 256, 192);
        var mipB = session.Capture(mipJittered, 256, 192);
        Assert.Empty(session.OriginalRecorder.LastDebugMessages);

        // Sample the distant oblique region (top third of the plane inside the viewport).
        static (double Mean, double StdDev, double TemporalDiff) FarBandMetrics(byte[] a, byte[] b, int width)
        {
            var values = new List<double>();
            double diffSum = 0;
            for (int y = 98; y <= 116; y++)
            {
                for (int x = 64; x < 192; x++)
                {
                    int p = (y * width + x) * 4;
                    // Verify finite non-blackened output and opaque alpha.
                    Assert.Equal(255, a[p + 3]);
                    values.Add(a[p]);
                    diffSum += Math.Abs(a[p] - b[p]);
                }
            }
            double mean = values.Average();
            double variance = values.Select(v => (v - mean) * (v - mean)).Average();
            return (mean, Math.Sqrt(variance), diffSum / values.Count);
        }

        var noMipStats = FarBandMetrics(noMipA.Rgba, noMipB.Rgba, 256);
        var mipStats = FarBandMetrics(mipA.Rgba, mipB.Rgba, 256);

        Assert.InRange(mipStats.Mean, 118, 138);
        Assert.True(mipStats.StdDev < noMipStats.StdDev * 0.25,
            $"Expected mipmaps to sharply reduce spatial moire stddev (noMip={noMipStats.StdDev:F2}, mip={mipStats.StdDev:F2}).");
        Assert.True(mipStats.TemporalDiff < noMipStats.TemporalDiff * 0.25,
            $"Expected mipmaps to sharply reduce jitter shimmering (noMip={noMipStats.TemporalDiff:F2}, mip={mipStats.TemporalDiff:F2}).");

        // Alpha-weighted mip test: alternating warm opaque texels and transparent (0,0,0,0) texels
        // must preserve warm RGB (240,200,160) in downsampled mips rather than darkening toward black.
        var cutout = CutoutFringeScene();
        var cutoutMip = SceneModelShadingPolicy.Apply(cutout, role);
        var cutoutFrame = session.Capture(cutoutMip, 256, 192);
        Assert.Empty(session.OriginalRecorder.LastDebugMessages);

        // Background is (240,240,240,255); 50% alpha-weighted average over it yields ~(240,220,200).
        // Unweighted downsampling would drop RGB to (120,100,80) -> blended ~(180,170,160).
        int tintedPixels = 0;
        for (int y = 96; y <= 116; y++)
        {
            for (int x = 104; x < 152; x++)
            {
                int p = (y * 256 + x) * 4;
                byte r = cutoutFrame.Rgba[p], g = cutoutFrame.Rgba[p + 1], b = cutoutFrame.Rgba[p + 2];
                if (b < 235)
                {
                    tintedPixels++;
                    Assert.True(r >= 225 && g >= 208 && b >= 190,
                        $"Unexpected dark halo at ({x},{y}): RGBA=({r},{g},{b},{cutoutFrame.Rgba[p + 3]}).");
                }
            }
        }
        Assert.True(tintedPixels > 50, $"Expected visible minified cutout pixels, found {tintedPixels}.");
    }

    [Fact]
    public void SharedTextureBetweenCharacterAndEnvironmentKeepsSingleMipOnBoth()
    {
        var sharedTex = CheckerTexture(128, 128, 16, 240);

        // Build two identical quads side-by-side in one scene sharing `sharedTex`.
        SceneVertex[] Quad(float xOffset) =>
        [
            new(new(-1.5f + xOffset, 0f, 1f), Vector3.UnitY, new(0, 0)),
            new(new(1.5f + xOffset, 0f, 1f), Vector3.UnitY, new(16, 0)),
            new(new(1.5f + xOffset, 0f, 55f), Vector3.UnitY, new(16, 16)),
            new(new(-1.5f + xOffset, 0f, 55f), Vector3.UnitY, new(0, 16)),
        ];
        var vertices = Quad(-2f).Concat(Quad(2f)).ToArray();
        uint[] indices = [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7];
        var camera = new SceneCamera(new(0, 0.9f, -2.5f), new(0, 0f, 12f), Vector3.UnitY, 4, 0.5f, 120f, VerticalFovDegrees: 45f);
        var materials = new[]
        {
            new SceneMaterial("char-mat", 0, 6, Vector4.One, Vector3.Zero, Vector3.Zero, 1, DiffuseTexture: 0),
            new SceneMaterial("env-mat", 6, 6, Vector4.One, Vector3.Zero, Vector3.Zero, 1, DiffuseTexture: 0),
        };
        var bindings = new[]
        {
            new OriginalStyleMaterialBinding(Vector3.One, DoubleSided: true),
            new OriginalStyleMaterialBinding(Vector3.One, DoubleSided: true),
        };
        var snapshot = new SceneSnapshot(vertices, indices, materials, [sharedTex], camera: camera, lightDirection: Vector3.UnitY);
        var profile = new OriginalStyleProfile(Vector3.One, Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.LinearWrap);
        var options = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false);

        var allOffScene = new OriginalFrameScene(new(snapshot, bindings, profile), options: options,
            materials: [new(false, false, false), new(false, false, false)]);
        var mixedSharedScene = new OriginalFrameScene(new(snapshot, bindings, profile), options: options,
            materials:
            [
                new(false, false, false) { UseMipmaps = false },
                new(false, false, false) { UseMipmaps = true },
            ]);

        Assert.Equal(new[] { false }, D3D12FrameRenderer.ComputeMipmapTextures(mixedSharedScene));

        using var session = new D3D12FrameSession();
        var baseline = session.Capture(allOffScene, 256, 192);
        var mixed = session.Capture(mixedSharedScene, 256, 192);
        Assert.Equal(baseline.Rgba, mixed.Rgba);

        // Now give the Environment material a separate texture instance with the same bytes:
        // Character keeps 1-mip, Environment upgrades to mipmapped!
        var envSeparateTex = CheckerTexture(128, 128, 16, 240);
        var splitSnapshot = new SceneSnapshot(vertices, indices,
            [materials[0], materials[1] with { DiffuseTexture = 1 }],
            [sharedTex, envSeparateTex], camera: camera, lightDirection: Vector3.UnitY);
        var splitScene = new OriginalFrameScene(new(splitSnapshot, bindings, profile), options: options,
            materials:
            [
                new(false, false, false) { UseMipmaps = false },
                new(false, false, false) { UseMipmaps = true },
            ]);
        Assert.Equal(new[] { false, true }, D3D12FrameRenderer.ComputeMipmapTextures(splitScene));

        var split = session.Capture(splitScene, 256, 192);
        Assert.Empty(session.OriginalRecorder.LastDebugMessages);

        // RH camera looking toward +Z maps -X (Character) to screen-right (x >= 136) and +X (Environment) to screen-left (x < 120).
        // Character side stays byte-identical to the 1-mip baseline; Environment side differs due to mipfiltering.
        bool envHalfDiffers = false;
        for (int y = 0; y < 192; y++)
        {
            for (int x = 136; x < 256; x++)
            {
                int p = (y * 256 + x) * 4;
                Assert.Equal(baseline.Rgba.AsSpan(p, 4).ToArray(), split.Rgba.AsSpan(p, 4).ToArray());
            }
            for (int x = 0; x < 120; x++)
            {
                int p = (y * 256 + x) * 4;
                if (!baseline.Rgba.AsSpan(p, 4).SequenceEqual(split.Rgba.AsSpan(p, 4)))
                    envHalfDiffers = true;
            }
        }
        Assert.True(envHalfDiffers);
    }

    private static SceneTexture CheckerTexture(int width, int height, byte dark, byte light)
    {
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte v = (x + y) % 2 == 0 ? light : dark;
                int p = (y * width + x) * 4;
                rgba[p] = v; rgba[p + 1] = v; rgba[p + 2] = v; rgba[p + 3] = 255;
            }
        }
        return new($"checker-{width}x{height}", width, height, rgba);
    }

    private static OriginalFrameScene ObliqueCheckerPlane(
        float zNear, float zFar, float uvRepeat, OriginalTextureSampling sampling, bool useMipmaps)
    {
        var tex = CheckerTexture(256, 256, 16, 240);
        SceneVertex[] vertices =
        [
            new(new(-8f, 0f, zNear), Vector3.UnitY, new(0, 0)),
            new(new(8f, 0f, zNear), Vector3.UnitY, new(uvRepeat, 0)),
            new(new(8f, 0f, zFar), Vector3.UnitY, new(uvRepeat, uvRepeat)),
            new(new(-8f, 0f, zFar), Vector3.UnitY, new(0, uvRepeat)),
        ];
        uint[] indices = [0, 1, 2, 0, 2, 3];
        var camera = new SceneCamera(new(0, 0.9f, -2.5f), new(0, 0f, 12f), Vector3.UnitY, 4, 0.5f, 150f, VerticalFovDegrees: 45f);
        var material = new SceneMaterial("plane", 0, 6, Vector4.One, Vector3.Zero, Vector3.Zero, 1, DiffuseTexture: 0);
        var snapshot = new SceneSnapshot(vertices, indices, [material], [tex], camera: camera, lightDirection: Vector3.UnitY);
        var surface = new OriginalStyleScene(snapshot, [new(Vector3.One, DoubleSided: true)],
            new(Vector3.One, Vector3.Zero, Matrix4x4.Identity, sampling));
        return new OriginalFrameScene(surface,
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: new(0.1f, 0.12f, 0.16f, 1f)),
            materials: [new OriginalFrameMaterialFlags(false, false, false) { UseMipmaps = useMipmaps }]);
    }

    private static OriginalFrameScene CutoutFringeScene()
    {
        const int size = 64;
        var rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int p = (y * size + x) * 4;
                bool opaque = ((x + y) & 1) == 0;
                if (opaque)
                {
                    rgba[p] = 240; rgba[p + 1] = 200; rgba[p + 2] = 160; rgba[p + 3] = 255;
                }
                else
                {
                    rgba[p] = 0; rgba[p + 1] = 0; rgba[p + 2] = 0; rgba[p + 3] = 0;
                }
            }
        }
        var tex = new SceneTexture("cutout-fringe", size, size, rgba);
        SceneVertex[] vertices =
        [
            new(new(-8f, 0f, 1f), Vector3.UnitY, new(0, 0)),
            new(new(8f, 0f, 1f), Vector3.UnitY, new(16, 0)),
            new(new(8f, 0f, 65f), Vector3.UnitY, new(16, 16)),
            new(new(-8f, 0f, 65f), Vector3.UnitY, new(0, 16)),
        ];
        uint[] indices = [0, 1, 2, 0, 2, 3];
        var camera = new SceneCamera(new(0, 0.9f, -2.5f), new(0, 0f, 12f), Vector3.UnitY, 4, 0.5f, 150f, VerticalFovDegrees: 45f);
        var material = new SceneMaterial("cutout", 0, 6, Vector4.One, Vector3.Zero, Vector3.Zero, 1, DiffuseTexture: 0, AlphaCutoff: 0f);
        var snapshot = new SceneSnapshot(vertices, indices, [material], [tex], camera: camera, lightDirection: Vector3.UnitY);
        var surface = new OriginalStyleScene(snapshot, [new(Vector3.One, DoubleSided: true)],
            new(Vector3.One, Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.LinearWrap));
        return new OriginalFrameScene(surface,
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: new(240f / 255f, 240f / 255f, 240f / 255f, 1f)),
            materials: [new OriginalFrameMaterialFlags(false, false, false)]);
    }
}
