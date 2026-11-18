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
        byte[] bytes = OriginalFrameShaders.CompileGenshinMetallicSpecularSurface(entry, native, gi, morph, sub, rb, atlas);
        Assert.Equal(new byte[] { 68, 88, 66, 67 }, bytes[..4]);
        string source = OriginalFrameShaders.GenshinMetallicSpecularSurfaceSource(native, gi, morph, sub, rb, atlas);
        foreach (string register in new[] { "b12", "t21", "s8" }) Assert.Equal(1, Count(source, $"register({register})"));
        Assert.Equal(1, Count(source, "float4 PSSurfaceMetallicSpecularBase(SurfaceOutput v):SV_Target"));
        Assert.Equal(1, Count(source, "float4 PSSurface(SurfaceOutput v):SV_Target"));
        Assert.Equal(1, Count(source, "color.rgb+=saturate(raw)*GenshinMetallicSpecularLocal("));
        Assert.Equal(1, Count(source, "float4 color=PSSurfaceMetallicSpecularBase(v);"));
        Assert.Contains("if(raw>0)", source);
        Assert.Contains("float blue=GenshinMetallicCubicLightmap(v.Uv).b;", source);
        Assert.Contains("GenshinMetallicSpecularRamp.SampleLevel(GenshinMetallicLinearRepeat,float2(p,.5),0).rgb", source);
        Assert.Contains("GenshinMetallicSpecularCameraIncoming(v)", source);
        Assert.Contains("GenshinMetallicSpecularSafeNormalize(incoming+light)", source);
        Assert.Contains("float lit=GenshinMetallicParameters.w==1?GenshinMetallicDarkLit.w:GenshinMetallicPrimaryLit(v);", source);
        Assert.Contains("+highlight*originalC;", source);
        Assert.DoesNotContain("GenshinMetallicSpecularLocal(v)", source);
        Assert.DoesNotContain("color.a*=", source);
        Assert.DoesNotContain("GenshinMetallicSpecularLinearRepeat", source);
        if (rb)
        {
            Assert.Contains("float3 highlight=GenshinHairHighlight(v,n,h)*saturate(1-GenshinMetallicRaw(v));", source);
            Assert.DoesNotContain("highlight*=saturate(1-metallicSpecularRaw)", source);
        }
        else Assert.Equal(1, Count(source, "if(metallicSpecularRaw>0) highlight*=saturate(1-metallicSpecularRaw);"));
        if (sub) Assert.Contains("originalC<1) alpha*=originalC+ToonShadowColor.w*(1-originalC)", source);
        if (native) Assert.Contains("#define NATIVE_LIGHT_RESPONSE(v,n,l) GenshinLightResponse(dot(n,l),1)", source);
    }

    [Fact]
    public void LocalEquationPreservesOperationOrderAndThreeVectorAbi()
    {
        string source = OriginalFrameShaders.GenshinMetallicSpecularSurfaceSource(false, false, false, false, false, false);
        int start = source.IndexOf("cbuffer GenshinMetallicSpecularConstants", StringComparison.Ordinal);
        int end = source.IndexOf("};", start, StringComparison.Ordinal);
        string buffer = source[start..end];
        Assert.Equal(3, Count(buffer, "float4 "));
        Assert.True(buffer.IndexOf("GenshinMetallicSpecularColorShininess;", StringComparison.Ordinal)
            < buffer.IndexOf("GenshinMetallicSpecularSharpScale;", StringComparison.Ordinal));
        Assert.True(buffer.IndexOf("GenshinMetallicSpecularSharpScale;", StringComparison.Ordinal)
            < buffer.IndexOf("GenshinMetallicSpecularOptions;", StringComparison.Ordinal));
        Assert.Contains("precise float shifted=nh+(1-1);", source);
        Assert.Contains("float h=max(shifted,.001);", source);
        Assert.Contains("float p=saturate(GenshinMetallicSpecularSharpScale.w*pow(h,GenshinMetallicSpecularColorShininess.w));", source);
        Assert.Contains("float3 ordinary=blue*q;", source);
        Assert.Contains("GenshinMetallicSpecularOptions.y<p?GenshinMetallicGamma(GenshinMetallicSpecularSharpScale.rgb):ordinary", source);
        Assert.Contains("return selected*lerp(1,GenshinMetallicSpecularOptions.x,saturate(1-lit));", source);
        Assert.DoesNotContain("saturate(selected)", source);
        Assert.DoesNotContain("saturate(nh)", source);
    }

    private static int Count(string text, string needle) => text.Split(needle, StringSplitOptions.None).Length - 1;
}
