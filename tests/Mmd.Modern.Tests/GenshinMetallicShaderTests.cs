using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class GenshinMetallicShaderTests
{
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
        var bytes = OriginalFrameShaders.CompileGenshinMetallicSurface(entry, native, gi, morph, sub, rb, atlas);
        Assert.Equal(new byte[] { 68, 88, 66, 67 }, bytes[..4]);
        string source = OriginalFrameShaders.GenshinMetallicSurfaceSource(native, gi, morph, sub, rb, atlas);
        foreach (string register in new[] { "b11", "t19", "t20", "s8" })
            Assert.Equal(1, Count(source, $"register({register})"));
        Assert.Equal(1, Count(source, "float4 GenshinMetallicCubicLightmap(float2 uv)"));
        Assert.Equal(1, Count(source, "float4 GenshinMetallicCubicMatcap(float2 uv)"));
        Assert.Contains("GenshinMetallicCubicLightmap(v.Uv).r>.9?1:0", source);
        Assert.Contains("float3 tangent=cross(incoming,negativeUp);", source);
        Assert.Contains("float3 bitangent=cross(incoming,tangent);", source);
        Assert.DoesNotContain("normalize(tangent)", source);
        Assert.DoesNotContain("normalize(bitangent)", source);
        Assert.Contains("float3 normal=normalize(v.Normal);", source);
        Assert.Contains("GenshinMetallicCubicMatcap(float2(sourceUv.x,1-sourceUv.y)).rgb", source);
        Assert.Contains("dot(image*GenshinMetallicParameters.z,float3(.2126,.7152,.0722))", source);
        Assert.Contains("saturate(lit-.8)", source);
        Assert.Contains("nonmetal,saturate(1-raw)", source);
        Assert.Contains("tint,saturate(raw)", source);
        Assert.DoesNotContain("GenshinMetallicMatcap.Load", source);
        Assert.DoesNotContain("image.a", source);
        Assert.DoesNotContain("color.a*=", source);
        Assert.DoesNotContain("GenshinMetallicCameraRight", source);
        Assert.DoesNotContain("SV_IsFrontFace", source);
        Assert.Contains(rb ? "GenshinHairHighlight(v,n,h)*saturate(1-GenshinMetallicRaw(v))" : "float3 highlight=saturate(pow(max(0,dot(n,h)),SpecularPower.w)*SpecularPower.rgb)", source);
        if (atlas)
        {
            Assert.DoesNotContain("baseColor*GenshinAtlasPalette", source);
            Assert.Contains("GenshinMetallicPalette(v,GenshinAtlasPalette(v,dot(n,l),visibility),c)", source);
        }
        else
        {
            Assert.Contains("if(raw<=0) return baseColor*c+baseColor*ToonShadowColor.rgb*(1-c);", source);
            Assert.Contains("else color=GenshinMetallicOrdinaryDiffuse(v,baseColor,c)+highlight*originalC;", source);
        }
        if (native || gi)
        {
            Assert.Contains("precise float3 toonFactor=GenshinMetallicPalette(v,", source);
            Assert.DoesNotContain("if(c==1) return false;", source);
            Assert.DoesNotContain("return fill*GenshinAtlasPalette", source);
            Assert.Contains("increment=diffuseLit*max(1-toonFactor,0)*fill", source);
        }
        if (native)
        {
            Assert.Equal(2, Count(source, "max(1-GenshinMetallicPalette(v,"));
            Assert.Contains("diffuse*remainingPalette*light.ColorIntensity.rgb", source);
            Assert.DoesNotContain("color.rgb*=GenshinMetallic", source);
        }
        if (sub) Assert.Contains("originalC<1) alpha*=originalC+ToonShadowColor.w*(1-originalC)", source);
    }

    [Fact]
    public void ConstantAbiUsesExactlySixOrderedFloat4FieldsAndBothImagesUseTheirOwnDimensions()
    {
        string source = OriginalFrameShaders.GenshinMetallicSurfaceSource(false, false, false, false, false, false);
        int start = source.IndexOf("cbuffer GenshinMetallicConstants", StringComparison.Ordinal);
        int end = source.IndexOf("};", start, StringComparison.Ordinal);
        string buffer = source[start..end];
        string[] fields = ["GenshinMetallicParameters;", "GenshinMetallicDarkLit;", "GenshinMetallicLight;", "GenshinMetallicShadow;", "GenshinMetallicCameraUp;", "GenshinMetallicCameraBackward;"];
        Assert.Equal(6, Count(buffer, "float4 "));
        int previous = -1;
        foreach (string field in fields)
        {
            int at = buffer.IndexOf(field, StringComparison.Ordinal);
            Assert.True(at > previous); previous = at;
        }
        Assert.Contains("GenshinMetallicLightmap.GetDimensions(width,height)", source);
        Assert.Contains("GenshinMetallicMatcap.GetDimensions(width,height)", source);
        Assert.Equal(8, Count(source, ".SampleLevel(GenshinMetallicLinearRepeat,"));
    }

    private static int Count(string text, string needle) => text.Split(needle, StringSplitOptions.None).Length - 1;
}
