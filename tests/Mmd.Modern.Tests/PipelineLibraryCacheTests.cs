using System.Buffers.Binary;
using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class PipelineLibraryCacheTests : IDisposable
{
    private readonly string cacheDir = Path.Combine(Path.GetTempPath(), "mda-pso-cache-" + Guid.NewGuid().ToString("N"));

    public PipelineLibraryCacheTests() => Directory.CreateDirectory(cacheDir);

    [Fact]
    public void PipelineKeysTrackShaderBytecodeRootSignatureAndPipelineState()
    {
        var rootDesc1 = new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout,
            [new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All)]);
        var rootDesc2 = new RootSignatureDescription(RootSignatureFlags.None,
            [new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All)]);

        string rHash1 = D3D12PipelineLibraryCache.ComputeRootSignatureHash(rootDesc1, RootSignatureVersion.Version10);
        string rHash2 = D3D12PipelineLibraryCache.ComputeRootSignatureHash(rootDesc2, RootSignatureVersion.Version10);
        Assert.NotEqual(rHash1, rHash2);

        byte[] vs1 = [1, 2, 3, 4];
        byte[] vs2 = [1, 2, 3, 5];
        byte[] ps1 = [9, 8, 7, 6];

        var cullBack = RasterizerDescription.CullNone;
        cullBack.CullMode = CullMode.Back;

        GraphicsPipelineStateDescription MakeGfx(byte[] vs, RasterizerDescription raster, Format rtFormat) => new()
        {
            VertexShader = vs,
            PixelShader = ps1,
            BlendState = BlendDescription.Opaque,
            SampleMask = uint.MaxValue,
            RasterizerState = raster,
            DepthStencilState = DepthStencilDescription.Default,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = [rtFormat],
            DepthStencilFormat = Format.D24_UNorm_S8_UInt,
            SampleDescription = new(1, 0),
        };

        var gfxBase = MakeGfx(vs1, RasterizerDescription.CullNone, Format.R8G8B8A8_UNorm);
        string kBase = D3D12PipelineLibraryCache.ComputeGraphicsKey(gfxBase, rHash1);
        Assert.Equal(kBase, D3D12PipelineLibraryCache.ComputeGraphicsKey(gfxBase, rHash1));
        Assert.NotEqual(kBase, D3D12PipelineLibraryCache.ComputeGraphicsKey(gfxBase, rHash2));
        Assert.NotEqual(kBase, D3D12PipelineLibraryCache.ComputeGraphicsKey(
            MakeGfx(vs2, RasterizerDescription.CullNone, Format.R8G8B8A8_UNorm), rHash1));
        Assert.NotEqual(kBase, D3D12PipelineLibraryCache.ComputeGraphicsKey(
            MakeGfx(vs1, cullBack, Format.R8G8B8A8_UNorm), rHash1));
        Assert.NotEqual(kBase, D3D12PipelineLibraryCache.ComputeGraphicsKey(
            MakeGfx(vs1, RasterizerDescription.CullNone, Format.R16G16B16A16_Float), rHash1));

        string kc = D3D12PipelineLibraryCache.ComputeComputeKey(new ComputePipelineStateDescription { ComputeShader = vs1 }, rHash1);
        Assert.NotEqual(kc, D3D12PipelineLibraryCache.ComputeComputeKey(new ComputePipelineStateDescription { ComputeShader = vs2 }, rHash1));
        Assert.NotEqual(kc, D3D12PipelineLibraryCache.ComputeComputeKey(new ComputePipelineStateDescription { ComputeShader = vs1 }, rHash2));
    }

    [Fact]
    public void PipelineFileHeaderRejectsAdapterDriverVersionAndIntegrityMismatches()
    {
        var identity = new D3D12AdapterIdentity(12345, 0x10DE, 0x2800, 0x1111, 1, 0x0001_0002_0003_0004L, false);
        byte[] file = D3D12PipelineLibraryCache.BuildFileBytes(identity, ["key-a", "key-b"], [10, 20, 30, 40]);

        Assert.True(D3D12PipelineLibraryCache.TryParseFileBytes(file, identity, out string[] keys, out byte[] blob));
        Assert.Equal(["key-a", "key-b"], keys);
        Assert.Equal([10, 20, 30, 40], blob);

        // Adapter LUID mismatch -> rejected.
        Assert.False(D3D12PipelineLibraryCache.TryParseFileBytes(file, identity with { AdapterLuid = 99999 }, out _, out _));

        // Driver version upgrade -> rejected.
        Assert.False(D3D12PipelineLibraryCache.TryParseFileBytes(file, identity with { DriverVersion = 0x0002_0000_0000_0000L }, out _, out _));

        // Hardware vendor/device change -> rejected.
        Assert.False(D3D12PipelineLibraryCache.TryParseFileBytes(file, identity with { DeviceId = 0x2900 }, out _, out _));

        // Magic / format mismatch -> rejected.
        byte[] badMagic = (byte[])file.Clone();
        badMagic[0] = (byte)'X';
        Assert.False(D3D12PipelineLibraryCache.TryParseFileBytes(badMagic, identity, out _, out _));

        // Corrupted payload checksum -> rejected.
        byte[] badBit = (byte[])file.Clone();
        badBit[file.Length - 40] ^= 0xFF;
        Assert.False(D3D12PipelineLibraryCache.TryParseFileBytes(badBit, identity, out _, out _));

        // Truncated file -> rejected.
        Assert.False(D3D12PipelineLibraryCache.TryParseFileBytes(file.AsSpan(0, file.Length / 2), identity, out _, out _));
        Assert.False(D3D12PipelineLibraryCache.TryParseFileBytes(file.AsSpan(0, 12), identity, out _, out _));

        // Wrong version header -> rejected.
        byte[] wrongVersion = D3D12PipelineLibraryCache.BuildFileBytes(identity, [], [1, 2]);
        BinaryPrimitives.WriteUInt32LittleEndian(wrongVersion.AsSpan(8, 4), 99);
        Assert.False(D3D12PipelineLibraryCache.TryParseFileBytes(wrongVersion, identity, out _, out _));
    }

    [Trait("Category", "Gpu")]
    [Fact]
    public void PipelineLibraryRoundTripsOnGpuAndFallsBackCleanlyWhenCorruptedOrTruncated()
    {
        var baseScene = D3D12OriginalFrameTests.Shadow(true);
        var smaaScene = new OriginalFrameScene(
            baseScene.Surface,
            baseScene.Outline,
            baseScene.OutlineOptions,
            baseScene.Options! with { Antialiasing = FrameAntialiasing.Smaa1X, SmaaQuality = SmaaQuality.Low },
            baseScene.Materials,
            baseScene.Lighting,
            baseScene.EditorOverlay);

        byte[] coldPixels;
        string? cachePath;
        using (var first = new D3D12FrameSession(cacheDir))
        {
            Assert.False(first.IsPipelineLibraryLoadedFromDisk);
            coldPixels = first.Capture(smaaScene, 257, 191).Rgba;
            cachePath = first.PipelineLibraryCachePath;
        }

        Assert.False(string.IsNullOrWhiteSpace(cachePath));
        Assert.True(File.Exists(cachePath));
        Assert.True(new FileInfo(cachePath!).Length > 64);

        // Second session in same isolated directory loads PSOs from ID3D12PipelineLibrary and matches pixel bytes identically.
        using (var second = new D3D12FrameSession(cacheDir))
        {
            Assert.True(second.IsPipelineLibraryLoadedFromDisk);
            var warm = second.Capture(smaaScene, 257, 191);
            Assert.True(second.PipelineLibraryHits > 0);
            Assert.Equal(coldPixels, warm.Rgba);
        }

        // Corrupt cache file -> fallback must silently recover and produce byte-identical output.
        File.WriteAllBytes(cachePath!, [0xBA, 0xAD, 0xF0, 0x0D, 1, 2, 3, 4, 5]);
        using (var corruptFallback = new D3D12FrameSession(cacheDir))
        {
            Assert.False(corruptFallback.IsPipelineLibraryLoadedFromDisk);
            var recovered = corruptFallback.Capture(smaaScene, 257, 191);
            Assert.Equal(coldPixels, recovered.Rgba);
        }
        Assert.True(File.Exists(cachePath));

        // Truncate valid cache file in half -> fallback must silently delete/rebuild cleanly without crashing.
        byte[] validBytes = File.ReadAllBytes(cachePath!);
        File.WriteAllBytes(cachePath!, validBytes[..(validBytes.Length / 2)]);
        using (var truncatedFallback = new D3D12FrameSession(cacheDir))
        {
            Assert.False(truncatedFallback.IsPipelineLibraryLoadedFromDisk);
            var recovered = truncatedFallback.Capture(smaaScene, 257, 191);
            Assert.Equal(coldPixels, recovered.Rgba);
        }

        // After recovery rebuilt valid cache, the next session hits disk again!
        using (var rebuiltHit = new D3D12FrameSession(cacheDir))
        {
            Assert.True(rebuiltHit.IsPipelineLibraryLoadedFromDisk);
            var warmAfterRecovery = rebuiltHit.Capture(smaaScene, 257, 191);
            Assert.True(rebuiltHit.PipelineLibraryHits > 0);
            Assert.Equal(coldPixels, warmAfterRecovery.Rgba);
        }
    }

    [Trait("Category", "Gpu")]
    [Fact]
    public void PipelineLibraryPersistsComputeAndGiPipelinesAcrossSessions()
    {
        string giCacheDir = Path.Combine(cacheDir, "gi-pso");
        Directory.CreateDirectory(giCacheDir);

        var scene = D3D12OriginalFrameTests.Shadow(true);
        var gi = new RayGiOptions(
            Mode: LightingMode.HardwareRayTracing,
            Strength: 1f,
            SamplesPerPixel: 1,
            Composition: RayGiComposition.ToonPalette,
            Denoiser: RayGiDenoiser.TemporalSpatial);

        byte[] firstPixels;
        using (var s1 = new D3D12FrameSession(giCacheDir))
        {
            if (!s1.QueryRayGiCapabilities().HardwareAvailable) return;
            firstPixels = s1.Capture(scene, 129, 97, rayGiOptions: gi).Rgba;
        }

        using (var s2 = new D3D12FrameSession(giCacheDir))
        {
            Assert.True(s2.IsPipelineLibraryLoadedFromDisk);
            var secondPixels = s2.Capture(scene, 129, 97, rayGiOptions: gi).Rgba;
            Assert.True(s2.PipelineLibraryHits > 0);
            Assert.Equal(firstPixels, secondPixels);
        }
    }

    [Trait("Category", "Gpu")]
    [Fact]
    public async Task ConcurrentSessionsSharingCacheDirectoryPreserveExactDiffusionSamplerState()
    {
        string sharedDir = Path.Combine(cacheDir, "concurrent-pso");
        Directory.CreateDirectory(sharedDir);

        var shadowScene = D3D12OriginalFrameTests.Shadow(true);
        var smaaScene = new OriginalFrameScene(
            shadowScene.Surface,
            shadowScene.Outline,
            shadowScene.OutlineOptions,
            shadowScene.Options! with { Antialiasing = FrameAntialiasing.Smaa1X, SmaaQuality = SmaaQuality.Low },
            shadowScene.Materials,
            shadowScene.Lighting,
            shadowScene.EditorOverlay);

        var pixels = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int p = (y * 8 + x) * 4;
                pixels[p] = (byte)(x * 35);
                pixels[p + 1] = (byte)(y * 35);
                pixels[p + 2] = (byte)((x + y) % 2 == 0 ? 240 : 20);
                pixels[p + 3] = 255;
            }
        SceneVertex[] vertices =
        [
            new(new(-1.5f, -1.5f, 0), -Vector3.UnitZ, new(0, 1)),
            new(new(1.5f, -1.5f, 0), -Vector3.UnitZ, new(1, 1)),
            new(new(1.5f, 1.5f, 0), -Vector3.UnitZ, new(1, 0)),
            new(new(-1.5f, 1.5f, 0), -Vector3.UnitZ, new(0, 0)),
        ];
        var snapshot = new SceneSnapshot(vertices, [0, 2, 1, 0, 3, 2],
            [new("diff-mat", 0, 6, Vector4.One, Vector3.Zero, Vector3.Zero, 1, 0)],
            [new("diff-tex", 8, 8, pixels)],
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 3, .1f, 30),
            lightDirection: -Vector3.UnitZ);
        var diffusionPointScene = new OriginalFrameScene(
            new(snapshot, [new(Vector3.One)], new(Vector3.One, Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false));
        var diffusionLinearScene = new OriginalFrameScene(
            new(snapshot, [new(Vector3.One)], new(Vector3.One, Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.LinearWrap)),
            options: new(OriginalAlphaBlendMode.Normal, true, false));

        using var barrier = new Barrier(6);
        Task RunWorker(int index) => Task.Run(() =>
        {
            barrier.SignalAndWait();
            using var session = new D3D12FrameSession(sharedDir);
            switch (index % 3)
            {
                case 0:
                    _ = session.Capture(diffusionPointScene, 65, 49, diffusion: new(true, 0.7f, 0.01f));
                    _ = session.Capture(diffusionLinearScene, 65, 49);
                    break;
                case 1:
                    _ = session.Capture(shadowScene, 129, 97);
                    _ = session.Capture(diffusionPointScene, 1, 17, diffusion: new(true, 1f, 0.01f));
                    break;
                default:
                    _ = session.Capture(smaaScene, 129, 97, diffusion: new(true, 0.7f, 0.01f));
                    break;
            }
        });

        await Task.WhenAll(Enumerable.Range(0, 6).Select(RunWorker));

        // Cross-lifetime interleaving: short-lived diffusion session disposes while a concurrent
        // session still holds the shared store and appends DXIL compute GPU skinning PSOs before serializing.
        var skinModel = new SceneSkinningModel(
            vertices,
            Enumerable.Repeat(new SceneSkinBinding(SceneSkinKind.Bdef1, 0, -1, -1, -1, Vector4.UnitX), vertices.Length).ToArray(),
            [],
            boneCount: 1);
        var deformation = new SceneDeformation(skinModel, [Matrix4x4.Identity], [], [], [], null, () => vertices);
        var deferredScene = new OriginalFrameScene(
            new(snapshot.WithDeferredDeformation(deformation), [new(Vector3.One)],
                new(Vector3.One, Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false));
        using (var skinSession = new D3D12FrameSession(sharedDir))
        {
            using (var shortDiff = new D3D12FrameSession(sharedDir))
            {
                _ = shortDiff.Capture(diffusionPointScene, 65, 49, diffusion: new(true, 0.7f, 0.01f));
            }
            _ = skinSession.Capture(deferredScene, 65, 49);
        }

        using var verify = new D3D12FrameSession(sharedDir);
        Assert.True(verify.IsPipelineLibraryLoadedFromDisk);
        foreach (var (w, h, s, ext) in new[] { (1, 17, 1f, 0.01f), (65, 49, 0.7f, 0.01f) })
        {
            var baseImg = verify.Capture(diffusionPointScene, w, h);
            var actual = verify.Capture(diffusionPointScene, w, h, diffusion: new(true, s, ext));
            var expected = DiffusionReferenceImage.Apply(baseImg.Rgba, w, h, s, ext);
            for (int i = 0; i < actual.Rgba.Length; i++)
            {
                if (i % 4 == 3) Assert.Equal(255, actual.Rgba[i]);
                else Assert.InRange(Math.Abs(actual.Rgba[i] - expected[i]), 0, 2);
            }
        }
        Assert.True(verify.PipelineLibraryHits > 0);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true); } catch { }
    }
}
