using Mmd.Modern.Rendering.D3D12;
using Xunit;
using Vortice.D3DCompiler;
using Vortice.Direct3D12.Shader;

namespace Mmd.Modern.Tests;

public sealed class StarMcBodyShaderTests
{
    public static IEnumerable<object[]> Variants()
    {
        foreach (var family in new[] { T8cTestHelpers.StarRailStyleId, T8cTestHelpers.WutheringStyleId })
        foreach (var variant in T8cTestHelpers.SurfaceVariants())
            yield return [family, .. variant];
    }

    [Theory, MemberData(nameof(Variants))]
    public void MappedBodyAndSharedHairCompileForNativeGiMorphAndSubTexture(
        string family, string entry, bool native, bool gi, bool morph, bool sub)
    {
        var code = T8cTestHelpers.CompilePluginSurface(family, "body", entry, native, gi, morph, sub);
        Assert.Equal(new byte[] { 68, 88, 66, 67 }, code[..4]);
        var vertexCode = T8cTestHelpers.CompilePluginVertex(family, "body", native, gi, sub);
        using var vertex = Compiler.Reflect<ID3D12ShaderReflection>(vertexCode);
        using var pixel = Compiler.Reflect<ID3D12ShaderReflection>(code);
        foreach (var input in pixel.InputParameters)
        {
            if (input.SemanticName.Equals("SV_PrimitiveID", StringComparison.OrdinalIgnoreCase)
                || input.SemanticName.Equals("SV_IsFrontFace", StringComparison.OrdinalIgnoreCase)) continue;
            var output = Assert.Single(vertex.OutputParameters, parameter =>
                parameter.SemanticName.Equals(input.SemanticName, StringComparison.OrdinalIgnoreCase)
                && parameter.SemanticIndex == input.SemanticIndex);
            Assert.Equal(output.ComponentType, input.ComponentType);
            Assert.Equal(output.Register, input.Register);
        }
    }

    [Fact]
    public void UnsupportedFamilyFailsBeforeCompilation()
    {
        Assert.Throws<KeyNotFoundException>(() => T8cTestHelpers.GetD3D12ShaderModule(T8cTestHelpers.MmdStyleId, "body"));
        Assert.Throws<InvalidOperationException>(() => T8cTestHelpers.CompilePluginSurface(T8cTestHelpers.GenshinStyleId, "body", "PSSurface", false, false, false, false));
    }
}
