using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Rendering.D3D12;
using Vortice.D3DCompiler;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class ShaderPrewarmAndFxcCacheTests : IDisposable
{
    private const string Compute = """
        #ifndef VALUE
        #define VALUE 1
        #endif
        RWStructuredBuffer<uint> Output : register(u0);
        [numthreads(1, 1, 1)]
        void CSMain(uint3 id : SV_DispatchThreadID) { Output[id.x] = VALUE; }
        [numthreads(1, 1, 1)]
        void Alternate(uint3 id : SV_DispatchThreadID) { Output[id.x] = VALUE + 1; }
        """;

    private readonly string tempDir = Path.Combine(CacheRoot(), "prewarm-tests-" + Guid.NewGuid().ToString("N"));

    public ShaderPrewarmAndFxcCacheTests() => Directory.CreateDirectory(tempDir);

    [Fact]
    public async Task DxcCompilerDeduplicatesConcurrentSameKeyRequestsIntoOneProcess()
    {
        var ct = TestContext.Current.CancellationToken;
        string cacheDir = Path.Combine(tempDir, "dxc-dedupe");
        var compiler = new DxcShaderCompiler(DxcShaderCompiler.ResolveCompilerPath(), cacheDir);

        const int callers = 6;
        using var startGate = new ManualResetEventSlim(false);
        var tasks = new Task<DxcCompilationResult>[callers];
        for (int i = 0; i < callers; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                startGate.Wait(ct);
                return compiler.CompileCompute(Compute, defines: new Dictionary<string, string?> { ["VALUE"] = "11" });
            }, ct);
        }
        startGate.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, compiler.CompileInvocationCount);
        var first = results[0];
        Assert.Equal("DXBC"u8.ToArray(), first.Dxil[..4]);
        for (int i = 1; i < callers; i++)
            Assert.Equal(first.Dxil, results[i].Dxil);

        // Caller array isolation.
        results[0].Dxil[0] = 0;
        var cached = compiler.CompileCompute(Compute, defines: new Dictionary<string, string?> { ["VALUE"] = "11" });
        Assert.Equal((byte)'D', cached.Dxil[0]);
        Assert.True(cached.CacheHit);
        Assert.Equal(1, compiler.CompileInvocationCount);
    }

    [Fact]
    public async Task DxcCompilerCompilesDistinctKeysConcurrentlyAndReusesDiskCacheAcrossInstances()
    {
        var ct = TestContext.Current.CancellationToken;
        string cacheDir = Path.Combine(tempDir, "dxc-disk");
        var compiler1 = new DxcShaderCompiler(DxcShaderCompiler.ResolveCompilerPath(), cacheDir);

        var t1 = Task.Run(() => compiler1.CompileCompute(Compute, defines: new Dictionary<string, string?> { ["VALUE"] = "21" }), ct);
        var t2 = Task.Run(() => compiler1.CompileCompute(Compute, defines: new Dictionary<string, string?> { ["VALUE"] = "22" }), ct);
        var results = await Task.WhenAll(t1, t2);
        Assert.Equal(2, compiler1.CompileInvocationCount);

        // Fresh compiler instance pointing to same cacheDir must hit disk without invoking dxc.exe.
        var compiler2 = new DxcShaderCompiler(DxcShaderCompiler.ResolveCompilerPath(), cacheDir);
        var hit = compiler2.CompileCompute(Compute, defines: new Dictionary<string, string?> { ["VALUE"] = "21" });
        Assert.True(hit.CacheHit);
        Assert.Equal(0, compiler2.CompileInvocationCount);
        Assert.Equal(1, compiler2.DiskCacheHitCount);
        Assert.Equal(results[0].Dxil, hit.Dxil);

        // Corrupt disk cache file -> fresh instance silently falls back, recompiles cleanly and heals file.
        string diskCacheFile = Path.Combine(cacheDir, hit.CacheKey, "cache.bin");
        Assert.True(File.Exists(diskCacheFile));
        File.WriteAllBytes(diskCacheFile, [0xDE, 0xAD, 0xBE, 0xEF, 1, 2, 3]);

        var compiler3 = new DxcShaderCompiler(DxcShaderCompiler.ResolveCompilerPath(), cacheDir);
        var rebuilt = compiler3.CompileCompute(Compute, defines: new Dictionary<string, string?> { ["VALUE"] = "21" });
        Assert.Equal(1, compiler3.CompileInvocationCount);
        Assert.Equal(0, compiler3.DiskCacheHitCount);
        Assert.Equal(results[0].Dxil, rebuilt.Dxil);
    }

    [Fact]
    public async Task DxcPrewarmAsyncWarmsValidVariantsAndNeverCachesFailures()
    {
        var ct = TestContext.Current.CancellationToken;
        string cacheDir = Path.Combine(tempDir, "dxc-prewarm");
        var compiler = new DxcShaderCompiler(DxcShaderCompiler.ResolveCompilerPath(), cacheDir);
        Assert.Equal(Math.Max(1, Environment.ProcessorCount / 2), DxcShaderCompiler.DefaultPrewarmParallelism);

        await compiler.PrewarmAsync(
        [
            new DxcShaderWarmupRequest(Compute, "CSMain", "cs_6_0", new Dictionary<string, string?> { ["VALUE"] = "31" }),
            new DxcShaderWarmupRequest(Compute, "NoSuchEntry", "cs_6_0", new Dictionary<string, string?> { ["VALUE"] = "32" }),
            new DxcShaderWarmupRequest(Compute, "Alternate", "cs_6_0", new Dictionary<string, string?> { ["VALUE"] = "33" }),
        ], maxParallelism: 2, cancellationToken: ct);

        int invocationsAfterPrewarm = compiler.CompileInvocationCount;
        Assert.Equal(3, invocationsAfterPrewarm);

        var ok1 = compiler.CompileCompute(Compute, "CSMain", "cs_6_0", new Dictionary<string, string?> { ["VALUE"] = "31" });
        var ok2 = compiler.CompileCompute(Compute, "Alternate", "cs_6_0", new Dictionary<string, string?> { ["VALUE"] = "33" });
        Assert.True(ok1.CacheHit);
        Assert.True(ok2.CacheHit);
        Assert.Equal(invocationsAfterPrewarm, compiler.CompileInvocationCount);

        // Failure must NOT be cached: direct compile still invokes DXC and throws.
        Assert.Throws<DxcShaderCompilationException>(() =>
            compiler.CompileCompute(Compute, "NoSuchEntry", "cs_6_0", new Dictionary<string, string?> { ["VALUE"] = "32" }));
        Assert.Equal(invocationsAfterPrewarm + 1, compiler.CompileInvocationCount);
    }

    [Fact]
    public async Task FxcCacheDeduplicatesInFlightPersistsToDiskAndFallsBackOnCorruption()
    {
        var ct = TestContext.Current.CancellationToken;
        string fxcCacheDir = Path.Combine(tempDir, "fxc-cache");
        const string shaderSource = """
            float4 VSMain(uint id : SV_VertexID) : SV_Position { return float4(0, 0, 0, 1); }
            float4 PSMain() : SV_Target { return float4(0.25, 0.5, 0.75, 1); }
            """;
        // Include unique tag per test run so in-memory Compiled dict starts cold for this key.
        string taggedSource = $"// test-{Guid.NewGuid():N}\n" + shaderSource;

        int beforeCount = D3D12FxcShaderCache.InvocationCount;
        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
        {
            gate.Wait(ct);
            return D3D12FxcShaderCache.Compile(taggedSource, "PSMain", "fxc-test.hlsl", "ps_5_1",
                ShaderFlags.OptimizationLevel3, fxcCacheDir, ct);
        }, ct)).ToArray();
        gate.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(beforeCount + 1, D3D12FxcShaderCache.InvocationCount);
        for (int i = 1; i < results.Length; i++)
            Assert.Equal(results[0], results[i]);

        // Isolated cache directory forces disk lookup; verify disk round-trip & corrupt fallback.
        string diskDir2 = Path.Combine(tempDir, "fxc-disk2");
        string tagged2 = $"// test-disk-{Guid.NewGuid():N}\n" + shaderSource;
        byte[] firstDisk = D3D12FxcShaderCache.Compile(tagged2, "VSMain", "fxc-disk.hlsl", "vs_5_1",
            ShaderFlags.OptimizationLevel3, diskDir2, ct);
        string key2 = D3D12FxcShaderCache.ComputeKey(tagged2, "VSMain", "fxc-disk.hlsl", "vs_5_1", ShaderFlags.OptimizationLevel3);
        string diskFile = Path.Combine(diskDir2, key2 + ".bin");
        Assert.True(File.Exists(diskFile));
        Assert.Equal(firstDisk, D3D12FxcShaderCache.TryLoadFromDisk(diskDir2, key2));

        // Corrupt & truncated disk files must silently delete and return null fallback.
        File.WriteAllBytes(diskFile, [1, 2, 3, 4]);
        Assert.Null(D3D12FxcShaderCache.TryLoadFromDisk(diskDir2, key2));
        Assert.False(File.Exists(diskFile));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.Cancel();
        await D3D12ShaderPrewarmer.PrewarmAsync(new D3D12ShaderWarmupSpec { SmaaQuality = SmaaQuality.Low }, 2, cts.Token);
        Assert.True(firstDisk.Length > 0);
    }

    [Fact]
    public void EditorParallelModelAssetMatchesSequentialEditorTextureLoaderPixelsAndWorkspaceCleansUp()
    {
        var ct = TestContext.Current.CancellationToken;
        string modelDir = Path.Combine(tempDir, "model-parity");
        Directory.CreateDirectory(modelDir);

        byte[] redPixels = [220, 40, 20, 255, 10, 200, 30, 128];
        byte[] toonPixels = [255, 255, 255, 255, 64, 64, 96, 255];
        File.WriteAllBytes(Path.Combine(modelDir, "diffuse.png"), Png(2, 1, redPixels));
        File.WriteAllBytes(Path.Combine(modelDir, "toon.png"), Png(1, 2, toonPixels));

        using var fixtureStream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx"));
        var basePmx = PmxReader.Read(fixtureStream);
        var templateMat = basePmx.Materials[0];
        var pmx = new PmxModel
        {
            Header = basePmx.Header,
            Name = basePmx.Name,
            EnglishName = basePmx.EnglishName,
            Comment = basePmx.Comment,
            EnglishComment = basePmx.EnglishComment,
            Vertices = basePmx.Vertices,
            Faces = basePmx.Faces,
            Materials =
            [
                new PmxMaterial
                {
                    Name = "mat0",
                    EnglishName = "mat0",
                    Diffuse = Vector4.One,
                    Specular = Vector3.Zero,
                    SpecularPower = 1,
                    Ambient = Vector3.Zero,
                    DrawMode = templateMat.DrawMode,
                    EdgeColor = Vector4.Zero,
                    EdgeSize = 0,
                    TextureIndex = 0,
                    SphereTextureIndex = -1,
                    SphereMode = PmxSphereMode.None,
                    ToonMode = PmxToonMode.Separate,
                    ToonTextureIndex = 1,
                    Memo = "",
                    FaceVertexCount = basePmx.Faces.Count * 3,
                }
            ],
            Textures = ["diffuse.png", "toon.png"],
            Bones = basePmx.Bones,
            Morphs = [],
            DisplayFrames = [],
            RigidBodies = [],
            Joints = [],
            SoftBodies = [],
        };

        string pmxPath = Path.Combine(modelDir, "test.pmx");
        var sequential = EditorProjectWorkspace.CreateModelAsset(pmx, pmxPath,
            rel => EditorTextureLoader.Load(modelDir, rel, ct), ct);
        var parallel = EditorProjectWorkspace.CreateModelAsset(pmx, pmxPath, ct);

        Assert.Equal(sequential.Source.Surface.Snapshot.Textures.Count, parallel.Source.Surface.Snapshot.Textures.Count);
        for (int i = 0; i < sequential.Source.Surface.Snapshot.Textures.Count; i++)
        {
            Assert.Equal(
                sequential.Source.Surface.Snapshot.Textures[i].Rgba.ToArray(),
                parallel.Source.Surface.Snapshot.Textures[i].Rgba.ToArray());
        }

        // Verify workspace LoadModelAsset + AddModel + SaveSnapshot + Open (triggers background prewarm) + project switch + Dispose cleanly.
        string realModelPath = Path.Combine(modelDir, "humanoid.pmd");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "humanoid.pmd"), realModelPath, overwrite: true);
        var loadedReal = EditorProjectWorkspace.LoadModelAsset(realModelPath, ct);
        string pmaPath = Path.Combine(tempDir, "roundtrip.pma");
        using var workspace = new EditorProjectWorkspace();
        workspace.PackageCacheDirectory = Path.Combine(tempDir, "pkg-cache");
        workspace.AddModel(realModelPath, loadedReal, ModelRole.Character);
        workspace.SaveSnapshot(pmaPath, ct);
        workspace.Open(pmaPath, ct);
        Assert.NotNull(workspace.Session);
        workspace.New();
        Assert.Null(workspace.Session);
    }

    private static string CacheRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("MMD_DXC_TEST_CACHE");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "mda", "AGENTS.md"))
                    || File.Exists(Path.Combine(directory.FullName, "MMD932_modern", "AGENTS.md")))
                    return Path.Combine(directory.FullName, ".dev-cache", "ray-shader-toolchain-20261018", "test-shaders");
        return Path.Combine(Path.GetTempPath(), "mda-dxc-tests");
    }

    private static byte[] Png(int width, int height, byte[] rgba)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 6;
        Chunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            for (int row = 0; row < height; row++)
            {
                zlib.WriteByte(0);
                zlib.Write(rgba.AsSpan(row * width * 4, width * 4));
            }
        }
        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();

        static void Chunk(Stream dst, string type, byte[] data)
        {
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
            dst.Write(len);
            byte[] tag = System.Text.Encoding.ASCII.GetBytes(type);
            dst.Write(tag);
            dst.Write(data);
            uint crc = 0xFFFFFFFF;
            foreach (byte b in tag) crc = UpdateCrc(crc, b);
            foreach (byte b in data) crc = UpdateCrc(crc, b);
            BinaryPrimitives.WriteUInt32BigEndian(len, ~crc);
            dst.Write(len);
        }

        static uint UpdateCrc(uint crc, byte b)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            return crc;
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
    }
}
