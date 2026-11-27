using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>Non-GPU CPU validation & HLSL compilation checks for all PR2 passes and variants.</summary>
public sealed class D3D12Pr2ShaderCompileTests
{
    [Fact]
    public void AllPr2PassShadersCompileCleanlyWithFxc()
    {
        var requests = new List<D3D12FxcShaderRequest>();
        requests.AddRange(D3D12FxaaPass.GetPrewarmRequests(alphaCoverage: false));
        requests.AddRange(D3D12FxaaPass.GetPrewarmRequests(alphaCoverage: true));
        requests.AddRange(D3D12NativeTaaPass.GetPrewarmRequests());
        requests.AddRange(D3D12RcasPass.GetPrewarmRequests());
        requests.AddRange(D3D12ScreenSpaceAoPass.GetPrewarmRequests());

        Assert.True(requests.Count >= 12);
        foreach (var req in requests)
        {
            byte[] bytecode = D3D12FxcShaderCache.Compile(req.Source, req.EntryPoint, req.SourceName, req.Profile, req.Flags,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotEmpty(bytecode);
        }
    }

    [Fact]
    public void PcfShadowHelperCompilesAcrossAllOriginalAndStyleShaderVariants()
    {
        Assert.NotEmpty(OriginalFrameShaders.Compile("VSSurface", true));
        Assert.NotEmpty(OriginalFrameShaders.CompilePcfShadow("PSSurface", false, false, false, false, false, false));
        Assert.NotEmpty(OriginalFrameShaders.CompilePcfShadow("PSSurfaceGi", false, true, false, false, false, false));
        Assert.NotEmpty(OriginalFrameShaders.CompilePcfShadow("PSSurfaceNative", true, false, false, false, false, false));
        Assert.NotEmpty(OriginalFrameShaders.CompilePcfShadow("PSSurfaceGiNative", true, true, false, false, false, false));
        Assert.NotEmpty(OriginalFrameShaders.CompilePcfShadow("PSSurface", false, false, true, false, false, false));
        Assert.NotEmpty(OriginalFrameShaders.CompilePcfShadow("PSSurfaceGiNative", true, true, true, false, false, false));
        Assert.NotEmpty(OriginalFrameShaders.CompilePcfShadow("PSSurface", false, false, false, true, false, false));
        Assert.NotEmpty(OriginalFrameShaders.CompilePcfShadow("PSSurfaceGiNative", true, true, false, true, false, false));
        Assert.NotEmpty(OriginalFrameShaders.CompilePcfShadow("PSSurface", false, false, false, false, true, false));
        Assert.NotEmpty(OriginalFrameShaders.CompilePcfShadow("PSSurfaceGiNative", true, true, true, true, true, true));

        var hostModule = D3D12StyleShaderAssembler.DefaultStylizedHostModule;
        foreach (string entry in new[] { "PSSurface", "PSSurfaceGi", "PSSurfaceNative", "PSSurfaceGiNative" })
        {
            bool gi = entry.Contains("Gi", StringComparison.Ordinal);
            bool native = entry.Contains("Native", StringComparison.Ordinal);
            byte[] bytecode = D3D12StyleShaderAssembler.CompilePixel(
                "stylized-test", entry, hostModule, hostModule.EffectiveHooks,
                D3D12StyleGeometryRequirements.None, native, gi, true, false, true, native);
            Assert.NotEmpty(bytecode);
        }
    }

    [Fact]
    public void SharpeningOptionsValidateBounds()
    {
        Assert.False(D3D12SharpeningOptions.Off.IsActive);
        D3D12SharpeningOptions.Off.Validate();
        new D3D12SharpeningOptions(true, 0f).Validate();
        new D3D12SharpeningOptions(true, 0.2f).Validate();
        new D3D12SharpeningOptions(true, 2f).Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12SharpeningOptions(true, -0.01f).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12SharpeningOptions(true, 2.01f).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12SharpeningOptions(true, float.NaN).Validate());
    }

    [Fact]
    public void ViewShaderPrewarmIncludesPr2PassesWhenConfigured()
    {
        var fxaaSpec = D3D12ViewShaderPrewarm.BuildFromViewRequest(new ViewQualityOptions(Antialiasing: FrameAntialiasing.Fxaa)
        {
            Sharpening = new(true, 0.3f),
            ScreenSpaceAo = new(true, 0.5f, 1.5f, 8),
        });
        Assert.True(fxaaSpec.IncludeFxaa);
        Assert.False(fxaaSpec.IncludeNativeTaa);
        Assert.True(fxaaSpec.IncludeStandaloneRcas);
        Assert.True(fxaaSpec.IncludeScreenSpaceAo);

        var taaSpec = D3D12ViewShaderPrewarm.BuildFromViewRequest(new ViewQualityOptions(Antialiasing: FrameAntialiasing.Taa));
        Assert.True(taaSpec.IncludeNativeTaa);
        Assert.True(taaSpec.IncludeTemporalInputs);
        Assert.True(taaSpec.IncludeTemporalCharacter);
        Assert.False(taaSpec.IncludeFxaa);
        Assert.False(taaSpec.IncludeStandaloneRcas);
        Assert.False(taaSpec.IncludeScreenSpaceAo);

        // Inherited scene options (FXAA, straight-alpha, active SSAO) are honored when view overrides are null.
        var inheritedScene = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false,
            Antialiasing: FrameAntialiasing.Fxaa, AlphaOutput: FrameAlphaOutput.Straight)
        {
            ScreenSpaceAo = new(Enabled: true, Strength: 0.4f),
        };
        var inheritedSpec = D3D12ViewShaderPrewarm.BuildFromViewRequest(new ViewQualityOptions(), sceneOptions: inheritedScene);
        Assert.True(inheritedSpec.IncludeFxaa);
        Assert.True(inheritedSpec.IncludeFxaaAlphaCoverage);
        Assert.True(inheritedSpec.IncludeScreenSpaceAo);
    }
}
