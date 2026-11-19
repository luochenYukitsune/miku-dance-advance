using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class GenshinMetallicSpecularShaderTests
{
    public static IEnumerable<object[]> Variants() => GenshinMetallicShaderTests.Variants();

    [Theory, MemberData(nameof(Variants))]
    public void All48ConsumedVariantsCompileWithOneMetalEndpointAndUnchangedCoverage(
        string entry, bool native, bool gi, bool morph, bool sub, bool rb, bool atlas)
    {
        string moduleId = GenshinMetallicShaderTests.ResolveMetallicModuleId(rb, atlas, specular: true);
        byte[] bytes = T8bTestHelpers.CompileGenshinPixel(moduleId, entry, native, gi, morph, sub);
        Assert.Equal(new byte[] { 68, 88, 66, 67 }, bytes[..4]);
        string source = T8bTestHelpers.AssembleGenshinPixelSource(moduleId, entry, native, gi, morph, sub);
        foreach (string register in new[] { "register(b0, space2)", "register(t5, space2)", "register(s0, space2)" })
            Assert.Equal(1, Count(source, register));
        Assert.Equal(1, Count(source, "float4 MdaPSSurface(SurfaceOutput v)"));
        Assert.Equal(1, Count(source, "color = MdaStylePrimarySurfacePost(v, color);"));
        Assert.Equal(1, Count(source, "color.rgb += saturate(raw) * GenshinMetallicSpecularLocal("));
        Assert.Contains("if (raw > 0)", source);
        Assert.Contains("float blue = GenshinCubicSample(GenshinMetallicLightmap, v.Uv).b;", source);
        Assert.Contains("GenshinMetallicSpecularRamp.SampleLevel(MdaLinearWrapSampler, float2(p, .5), 0).rgb", source);
        Assert.Contains("float3 incoming = GenshinMetallicSpecularSafeNormalize(EyePosition.xyz - v.WorldPosition);", source);
        Assert.Contains("GenshinMetallicSpecularSafeNormalize(incoming + light)", source);
        Assert.Contains("float lit = GenshinMetallicParameters.w == 1 ? GenshinMetallicDarkLit.w : GenshinMetallicPrimaryLit(v);", source);
        Assert.Contains("+ (inShadowMap ? highlight * originalC : highlight);", source);
        Assert.DoesNotContain("GenshinMetallicSpecularLocal(v)", source);
        Assert.DoesNotContain("color.a *=", source);
        Assert.DoesNotContain("GenshinMetallicSpecularLinearRepeat", source);
        if (rb)
        {
            Assert.Contains("float3 highlight = GenshinHairHighlight(v, n, h);", source);
            Assert.Contains("highlight *= saturate(1 - metallicRaw);", source);
            Assert.DoesNotContain("if (metallicRaw > 0) highlight *= saturate(1 - metallicRaw);", source);
        }
        else Assert.Equal(1, Count(source, "if (metallicRaw > 0) highlight *= saturate(1 - metallicRaw);"));
        if (sub) Assert.Contains("originalC < 1) alpha *= originalC + ToonShadowColor.w * (1 - originalC)", source);
        if (native) Assert.Contains("#define NATIVE_LIGHT_RESPONSE(v, n, l) MdaStyleLightResponse(v, n, l, 1)", source);
    }

    [Fact]
    public void LocalEquationPreservesOperationOrderAndThreeVectorAbi()
    {
        string source = T8bTestHelpers.AssembleGenshinPixelSource("metallic-specular", "PSSurface", false, false, false, false);
        int start = source.IndexOf("cbuffer PluginStyleConstants", StringComparison.Ordinal);
        int end = source.IndexOf("};", start, StringComparison.Ordinal);
        string buffer = source[start..end];
        Assert.Equal(7, Count(buffer, "float4 "));
        Assert.True(buffer.IndexOf("GenshinMetallicSpecularColorShininess;", StringComparison.Ordinal)
            < buffer.IndexOf("GenshinMetallicSpecularSharpScale;", StringComparison.Ordinal));
        Assert.True(buffer.IndexOf("GenshinMetallicSpecularSharpScale;", StringComparison.Ordinal)
            < buffer.IndexOf("GenshinMetallicSpecularOptions;", StringComparison.Ordinal));
        Assert.Equal(112, T8bTestHelpers.BuildGenshinDrawData(D3D12CharacterMaterialRole.Body,
            binding: TestStylePlugins.GenshinMetallic(0, 1, hasSpecular: true))!.ConstantBufferData.Length);
        Assert.Contains("precise float shifted = nh + (1 - 1);", source);
        Assert.Contains("float h = max(shifted, .001);", source);
        Assert.Contains("float p = saturate(GenshinMetallicSpecularSharpScale.w * pow(h, GenshinMetallicSpecularColorShininess.w));", source);
        Assert.Contains("float3 ordinary = blue * q;", source);
        Assert.Contains("GenshinMetallicSpecularOptions.y < p ? GenshinMetallicGamma(GenshinMetallicSpecularSharpScale.rgb) : ordinary", source);
        Assert.Contains("return selected * lerp(1, GenshinMetallicSpecularOptions.x, saturate(1 - lit));", source);
        Assert.DoesNotContain("saturate(selected)", source);
        Assert.DoesNotContain("saturate(nh)", source);
    }

    private static int Count(string text, string needle) => text.Split(needle, StringSplitOptions.None).Length - 1;
}
