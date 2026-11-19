using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class GenshinMetallicShaderTests
{
    internal static string ResolveMetallicModuleId(bool rb, bool atlas, bool specular) =>
        (specular, atlas, rb) switch
        {
            (true, true, true) => "metallic-specular-atlas-rb",
            (true, true, false) => "metallic-specular-atlas",
            (true, false, true) => "metallic-specular-rb",
            (true, false, false) => "metallic-specular",
            (false, true, true) => "metallic-atlas-rb",
            (false, true, false) => "metallic-atlas",
            (false, false, true) => "metallic-rb",
            (false, false, false) => "metallic",
        };

    public static IEnumerable<object[]> Variants()
    {
        foreach (var variant in D3D12GenshinSurfaceTests.SurfaceVariants())
        foreach (bool rb in new[] { false, true })
        foreach (bool atlas in new[] { false, true })
            yield return [.. variant, rb, atlas];
    }

    [Theory, MemberData(nameof(Variants))]
    public void EveryConsumedVariantCompilesAndUsesOneComposedVisibleAndBudgetEndpoint(
        string entry, bool native, bool gi, bool morph, bool sub, bool rb, bool atlas)
    {
        string moduleId = ResolveMetallicModuleId(rb, atlas, specular: false);
        var bytes = T8bTestHelpers.CompileGenshinPixel(moduleId, entry, native, gi, morph, sub);
        Assert.Equal(new byte[] { 68, 88, 66, 67 }, bytes[..4]);
        string source = T8bTestHelpers.AssembleGenshinPixelSource(moduleId, entry, native, gi, morph, sub);
        foreach (string register in new[] { "register(b0, space2)", "register(t3, space2)", "register(t4, space2)", "register(s0, space2)" })
            Assert.Equal(1, Count(source, register));
        Assert.Equal(1, Count(source, "float4 GenshinCubicSample(Texture2D<float4> tex, float2 uv)"));
        Assert.Contains("GenshinCubicSample(GenshinMetallicLightmap, v.Uv).r > .9 ? 1 : 0", source);
        Assert.Contains("float3 tangent = cross(incoming, negativeUp);", source);
        Assert.Contains("float3 bitangent = cross(incoming, tangent);", source);
        Assert.DoesNotContain("normalize(tangent)", source);
        Assert.DoesNotContain("normalize(bitangent)", source);
        Assert.Contains("float3 normal = normalize(v.Normal);", source);
        Assert.Contains("GenshinCubicSample(GenshinMetallicMatcap, float2(sourceUv.x, 1 - sourceUv.y)).rgb", source);
        Assert.Contains("dot(image * GenshinMetallicParameters.z, float3(.2126, .7152, .0722))", source);
        Assert.Contains("saturate(lit - .8)", source);
        Assert.Contains("nonmetal, saturate(1 - raw)", source);
        Assert.Contains("tint, saturate(raw)", source);
        Assert.DoesNotContain("GenshinMetallicMatcap.Load", source);
        Assert.DoesNotContain("image.a", source);
        Assert.DoesNotContain("color.a *=", source);
        Assert.DoesNotContain("GenshinMetallicCameraRight", source);
        Assert.DoesNotContain("SV_IsFrontFace", source);
        Assert.Contains(rb ? "float3 highlight = GenshinHairHighlight(v, n, h);" : "float3 highlight = saturate(pow(max(0, dot(n, h)), SpecularPower.w) * SpecularPower.rgb);", source);
        if (rb) Assert.Contains("highlight *= saturate(1 - metallicRaw);", source);
        if (atlas)
        {
            Assert.DoesNotContain("baseColor * GenshinAtlasPalette", source);
            Assert.Contains("float3 nonmetal = GenshinAtlasPalette(v, nDotL, visibility);", source);
            Assert.Contains("return GenshinMetallicPalette(v, nonmetal, blend);", source);
        }
        else
        {
            Assert.Contains("float3 nonmetal = MdaPaletteFactorDefault(blend);", source);
            Assert.Contains("return GenshinMetallicPalette(v, nonmetal, blend);", source);
            Assert.Contains("color = baseColor * MdaStylePaletteFactor(v, c, nDotL, visibility) + (inShadowMap ? highlight * originalC : highlight);", source);
        }
        if (native || gi)
        {
            Assert.Contains("precise float3 toonFactor = MdaStylePaletteFactor(v, c, nDotL, visibility);", source);
            Assert.DoesNotContain("return fill * GenshinAtlasPalette", source);
            Assert.Contains("increment = diffuseLit * max(1 - toonFactor, 0) * fill;", source);
        }
        if (native)
        {
            // The metal palette no longer feeds an additional-light budget; lights use the family band.
            Assert.Equal(0, Count(source, "max(1 - GenshinMetallicPalette(v,"));
            Assert.Contains("#define NATIVE_LIGHT_RESPONSE(v, n, l) MdaStyleLightResponse(v, n, l, 1)", source);
            Assert.DoesNotContain("color.rgb *= GenshinMetallic", source);
        }
        if (sub) Assert.Contains("originalC < 1) alpha *= originalC + ToonShadowColor.w * (1 - originalC)", source);
    }

    [Fact]
    public void ConstantAbiUsesExactlyFourOrderedFloat4FieldsAndBothImagesUseTheirOwnDimensions()
    {
        string source = T8bTestHelpers.AssembleGenshinPixelSource("metallic", "PSSurface", false, false, false, false);
        int start = source.IndexOf("cbuffer PluginStyleConstants", StringComparison.Ordinal);
        int end = source.IndexOf("};", start, StringComparison.Ordinal);
        string buffer = source[start..end];
        string[] fields = ["GenshinMetallicParameters;", "GenshinMetallicDarkLit;", "GenshinMetallicLight;", "GenshinMetallicShadow;"];
        Assert.Equal(4, Count(buffer, "float4 "));
        int previous = -1;
        foreach (string field in fields)
        {
            int at = buffer.IndexOf(field, StringComparison.Ordinal);
            Assert.True(at > previous); previous = at;
        }
        Assert.Equal(64, T8bTestHelpers.BuildGenshinDrawData(D3D12CharacterMaterialRole.Body, binding: TestStylePlugins.GenshinMetallic(0, 1))!.ConstantBufferData.Length);
        Assert.Contains("tex.GetDimensions(width, height);", source);
        Assert.Equal(4, Count(source, "tex.SampleLevel(MdaLinearWrapSampler,"));
        Assert.Contains("GenshinCubicSample(GenshinMetallicLightmap,", source);
        Assert.Contains("GenshinCubicSample(GenshinMetallicMatcap,", source);
    }

    private static int Count(string text, string needle) => text.Split(needle, StringSplitOptions.None).Length - 1;
}
