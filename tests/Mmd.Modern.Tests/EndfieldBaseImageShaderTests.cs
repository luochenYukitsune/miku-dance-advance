using System.Numerics;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EndfieldBaseImageShaderTests
{
    public static IEnumerable<object[]> Variants()=>EndfieldPaletteShaderTests.Variants();

    [Theory,MemberData(nameof(Variants))]
    public void All48NativeGiMorphSubHairRimVariantsCompileWithOneCapturedCpre(
        string entry,bool native,bool gi,bool morph,bool sub,bool hair,bool rim)
    {
        string moduleId=T8dTestHelpers.ResolveEndfieldModuleId(hair,rim,matcap:true,palette:true,baseImage:true);
        string oldModuleId=T8dTestHelpers.ResolveEndfieldModuleId(hair,rim,matcap:true,palette:true,baseImage:false);
        byte[] shader=T8dTestHelpers.CompileEndfieldPixel(moduleId,entry,native,gi,morph,sub);
        Assert.Equal(new byte[]{68,88,66,67},shader[..4]);
        string source=T8dTestHelpers.AssembleEndfieldPixelSource(moduleId,entry,native,gi,morph,sub);
        string old=T8dTestHelpers.AssembleEndfieldPixelSource(oldModuleId,entry,native,gi,morph,sub);
        Assert.DoesNotContain("BaseImageColorMap",old);
        foreach(string slot in new[]{"b0, space2","t6, space2","t7, space2","t8, space2","t13, space2"}) Assert.Contains($"register({slot})",source);
        Assert.Equal(1,Count(source,"float3 effectiveCpre = EndfieldBaseImageCpre(v, front, uv, baseGeometric, basePerturbed, rangeQ, camBackward, isOrtho);"));
        Assert.Equal(2,Count(source,"EndfieldMatcapMappedNormal("));
        Assert.Contains("float3 initial = effectiveCpre;",source);
        Assert.Contains("float3 oldHair = MatcapBaseBaseline.rgb * p;",source);
        Assert.Contains("EndfieldMatcapOverlay(MatcapBaseBaseline.rgb, tint, saturate(r))",source);
        Assert.Contains("float3 changed = effectiveCpre * ((1 - gate) + gate * screen);",source);
        Assert.Contains("return emission - initial * MatcapBaseBaseline.w - oldHair - oldRim;",source);
        Assert.DoesNotContain("color.a*=",source);
        Assert.DoesNotContain("color.a *=",source);
        Assert.DoesNotContain("BaseImageColorMap.SampleLevel(MdaLinearWrapSampler, uv, 0).a",source);
    }

    [Fact]
    public void EightVectorHostAbiMatchesExplicitShaderInputs()
    {
        var matcap=T8dTestHelpers.EndfieldMatcap(coupleEndfieldPalette:true);
        var palette=new EndfieldPaletteSpec(Vector3.Zero).ToBinding();
        var baseImg=new EndfieldBaseImageSpec(new(.1f,.2f,.3f),new(.4f,.5f,.6f),new(.7f,.8f,.9f),new(1,2,3),
            ShadowFactor:.2f,WarmTint:.3f,Contrast:.4f,Saturation:.5f,DetailBlue:.6f,ShadowStrength:.7f).ToBinding();
        var draw=T8dTestHelpers.BuildEndfieldDrawData([matcap,palette,baseImg])!;
        var vecs=T8dTestHelpers.ReadCbvVectors(draw);
        Assert.Equal(18,vecs.Length); // 7 matcap + 3 palette + 8 base-image (128 bytes)
        Assert.Equal(new Vector4(.1f,.2f,.3f,.4f),vecs[10]);
        Assert.Equal(new Vector4(.4f,.5f,.6f,.2f),vecs[11]);
        Assert.Equal(new Vector4(.7f,.8f,.9f,.3f),vecs[12]);
        Assert.Equal(new Vector4(1,2,3,.5f),vecs[13]);
        Assert.Equal(new Vector4(.6f,.7f,0,0),vecs[14]);
        Assert.Equal(Vector4.Zero,vecs[15]);
    }

    [Fact]
    public void UnsupportedEntryFailsBeforeCompilation()=>Assert.Throws<ArgumentOutOfRangeException>(()=>
        T8dTestHelpers.AssembleEndfieldPixelSource("matcap-palette-base","PSDepth",false,false,false,false));

    private static int Count(string source,string text)=>source.Split(text,StringSplitOptions.None).Length-1;
}
