using Mmd.Modern.Rendering.D3D12;
using Vortice.D3DCompiler;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class SubTextureTemporalShaderTests
{
    [Theory]
    [InlineData("VSCharacter", "vs_5_1")]
    [InlineData("PSOpaque", "ps_5_1")]
    [InlineData("PSReactive", "ps_5_1")]
    public void SharedTemporalVariantCompilesExistingTechniqueAlphaWithoutChangingOrdinarySource(string entry, string profile)
    {
        string ordinary = D3D12TemporalInputPass.CharacterShaderSource(false);
        string source = D3D12TemporalInputPass.CharacterShaderSource(true);
        Assert.Contains("float alpha = PSSurface(surface).a;", source);
        Assert.Contains("Options.y>=3?v.AdditionalUv", source);
        Assert.Contains("alpha*=c+ToonShadowColor.w*(1-c)", source);
        Assert.Contains("original.Position = Extent.z == 3 ? v.SourcePosition : v.Current;", source);
        Assert.DoesNotContain("PSSurface(surface)", ordinary);
        Assert.DoesNotContain("CoverageInput original", ordinary);
        Assert.NotEmpty(Compiler.Compile(source, entry, "SubTextureTemporal.hlsl", profile,
            ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness).ToArray());
        Assert.Equal(ordinary, D3D12TemporalInputPass.CharacterShaderSource(false));
    }
}
