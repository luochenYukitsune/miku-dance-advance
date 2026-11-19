using System.Security.Cryptography;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Toolchain")]
public sealed class DxcShaderCompilerTests
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

    private const string RayQuery = """
        RaytracingAccelerationStructure Scene : register(t0);
        RWStructuredBuffer<uint> Output : register(u0);
        [numthreads(1, 1, 1)]
        void CSMain(uint3 id : SV_DispatchThreadID)
        {
            RayDesc ray;
            ray.Origin = float3(0, 0, 0);
            ray.Direction = float3(0, 0, 1);
            ray.TMin = 0.001;
            ray.TMax = 100;
            RayQuery<RAY_FLAG_FORCE_OPAQUE> query;
            query.TraceRayInline(Scene, RAY_FLAG_NONE, 255, ray);
            while (query.Proceed()) { }
            Output[id.x] = query.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? 1 : 0;
        }
        """;

    [Theory]
    [InlineData("cs_6_0", false)]
    [InlineData("cs_6_5", true)]
    public void ActualDxcProducesDxilForComputeAndInlineRayQuery(string profile, bool rayQuery)
    {
        var compiler = CreateCompiler();
        var result = compiler.CompileCompute(rayQuery ? RayQuery : Compute, profile: profile);

        Assert.Equal("DXBC"u8.ToArray(), result.Dxil[..4]);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(result.Dxil)), result.OutputHash);
        Assert.Equal(compiler.CompilerVersion, result.CompilerVersion);
        Assert.Equal(compiler.CompilerHash, result.CompilerHash);
        Assert.Equal(profile, result.Profile);
        Assert.False(result.CacheHit);
        Assert.Equal(1, compiler.CompileInvocationCount);
        Assert.Contains(compiler.CompilerFiles, file => file.Name == "dxcompiler.dll" && file.Sha256.Length == 64);
    }

    [Fact]
    public void CacheOwnsDxilAndCanonicalizesMacrosWithoutStartingAnotherProcess()
    {
        var compiler = CreateCompiler();
        var first = compiler.CompileCompute(Compute, defines: new Dictionary<string, string?> { ["VALUE"] = "7", ["UNUSED"] = null });
        first.Dxil[0] = 0;
        var second = compiler.CompileCompute(Compute, defines: new Dictionary<string, string?> { ["UNUSED"] = null, ["VALUE"] = "7" });

        Assert.True(second.CacheHit);
        Assert.Equal(1, compiler.CompileInvocationCount);
        Assert.Equal(first.CacheKey, second.CacheKey);
        Assert.Equal("DXBC"u8.ToArray(), second.Dxil[..4]);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(second.Dxil)), second.OutputHash);
    }

    [Fact]
    public void IndependentCacheDirectoriesProduceTheSameKeyAndDxil()
    {
        var first = CreateCompiler().CompileCompute(Compute, profile: "cs_6_0");
        var second = CreateCompiler().CompileCompute(Compute, profile: "cs_6_0");
        Assert.Equal(first.CacheKey, second.CacheKey);
        Assert.Equal(first.OutputHash, second.OutputHash);
        Assert.Equal(first.Dxil, second.Dxil);
    }

    [Fact]
    public void PublishedDxilIsReusedByANewCompilerOnTheSameCacheAndAttemptDirectoriesAreRemoved()
    {
        string cache = Path.Combine(CacheRoot(), Guid.NewGuid().ToString("N"));
        var first = new DxcShaderCompiler(DxcShaderCompiler.ResolveCompilerPath(), cache);
        var compiled = first.CompileCompute(Compute, profile: "cs_6_0");
        Assert.False(Directory.Exists(Path.Combine(cache, compiled.CacheKey)));
        Assert.True(File.Exists(Path.Combine(cache, compiled.CacheKey + ".json")));

        var restarted = new DxcShaderCompiler(DxcShaderCompiler.ResolveCompilerPath(), cache);
        var reused = restarted.CompileCompute(Compute, profile: "cs_6_0");
        Assert.True(reused.CacheHit);
        Assert.Equal(0, restarted.CompileInvocationCount);
        Assert.Equal(compiled.Dxil, reused.Dxil);

        File.WriteAllBytes(Path.Combine(cache, compiled.CacheKey + ".dxil"), "DXBCcorrupt"u8.ToArray());
        var rebuilt = new DxcShaderCompiler(DxcShaderCompiler.ResolveCompilerPath(), cache);
        Assert.False(rebuilt.CompileCompute(Compute, profile: "cs_6_0").CacheHit);
        Assert.Equal(1, rebuilt.CompileInvocationCount);
    }

    [Fact]
    public void SourceMacroEntryAndProfileEachInvalidateTheRequest()
    {
        var compiler = CreateCompiler();
        var original = compiler.CompileCompute(Compute);
        var source = compiler.CompileCompute(Compute + "\n// exact source bytes participate");
        var macro = compiler.CompileCompute(Compute, defines: new Dictionary<string, string?> { ["VALUE"] = "3" });
        var entry = compiler.CompileCompute(Compute, entryPoint: "Alternate");
        var profile = compiler.CompileCompute(Compute, profile: "cs_6_0");

        Assert.Equal(5, new[] { original, source, macro, entry, profile }.Select(result => result.CacheKey).Distinct().Count());
        Assert.Equal(5, compiler.CompileInvocationCount);
    }

    [Fact]
    public void InvalidEntryPreservesActualDxcDiagnosticsAndFailuresAreNotCached()
    {
        var compiler = CreateCompiler();
        var error = Assert.Throws<DxcShaderCompilationException>(() => compiler.CompileCompute(Compute, "NoSuchEntry"));
        Assert.NotEqual(0, error.ExitCode);
        Assert.Contains("entry", error.Diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<DxcShaderCompilationException>(() => compiler.CompileCompute(Compute, "NoSuchEntry"));
        Assert.Equal(2, compiler.CompileInvocationCount);
    }

    [Fact]
    public void MissingExplicitCompilerIsAnActionableFailure()
    {
        string path = Path.Combine(CacheRoot(), "intentionally-absent-dxc.exe");
        var error = Assert.Throws<FileNotFoundException>(() => new DxcShaderCompiler(path, CacheRoot()));
        Assert.Equal(path, error.FileName);
        Assert.Contains("setup-dxc.ps1", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("#include \"outside.hlsl\"\n")]
    [InlineData("/* prefix */ # /* comment */ include \"outside.hlsl\"\n")]
    [InlineData("#inc\\\nlude \"outside.hlsl\"\n")]
    public void UntrackedIncludesAreRejectedBeforeStartingTheCompiler(string directive)
    {
        var compiler = CreateCompiler();
        Assert.Throws<ArgumentException>(() => compiler.CompileCompute(directive + Compute));
        Assert.Equal(0, compiler.CompileInvocationCount);
    }

    private static DxcShaderCompiler CreateCompiler() => new(
        DxcShaderCompiler.ResolveCompilerPath(), Path.Combine(CacheRoot(), Guid.NewGuid().ToString("N")));

    private static string CacheRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("MMD_DXC_TEST_CACHE");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "MMD932_modern", "AGENTS.md")))
                    return Path.Combine(directory.FullName, ".dev-cache", "ray-shader-toolchain-20261018", "test-shaders");
        throw new InvalidOperationException("Set MMD_DXC_TEST_CACHE to an explicit D: toolchain test cache outside a workspace.");
    }
}
