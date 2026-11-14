using Mmd.Modern.Rendering.D3D12;
using Xunit;
using Vortice.D3DCompiler;
using Vortice.Direct3D12.Shader;

namespace Mmd.Modern.Tests;

public sealed class StarMcBodyShaderTests
{
    public static IEnumerable<object[]> Variants()
    {
        foreach (var family in new[] { D3D12SurfaceStyle.StarRailPmx, D3D12SurfaceStyle.WutheringPmx })
        foreach (var variant in D3D12GenshinSurfaceTests.SurfaceVariants())
            yield return [family, .. variant];
    }

    [Theory, MemberData(nameof(Variants))]
    public void MappedBodyAndSharedHairCompileForNativeGiMorphAndSubTexture(
        D3D12SurfaceStyle family, string entry, bool native, bool gi, bool morph, bool sub)
    {
        var code = OriginalFrameShaders.CompileStarMcBodySurface(family, entry, native, gi, morph, sub);
        Assert.Equal(new byte[] { 68, 88, 66, 67 }, code[..4]);
        var vertexCode = sub ? OriginalFrameShaders.CompileSubTexture("VSSurface", true, native, gi)
            : OriginalFrameShaders.Compile("VSSurface", true);
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

    [Theory, InlineData(D3D12SurfaceStyle.Mmd), InlineData(D3D12SurfaceStyle.GenshinPmx)]
    public void UnsupportedFamilyFailsBeforeCompilation(D3D12SurfaceStyle family)
        => Assert.Throws<ArgumentOutOfRangeException>(() => OriginalFrameShaders.StarMcBodySurfaceSource(family, "PSSurface", false, false, false, false));
}
