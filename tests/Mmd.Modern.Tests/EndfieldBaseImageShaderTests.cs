using System.Numerics;
using Mmd.Modern.Core;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EndfieldBaseImageShaderTests
{
    public static IEnumerable<object[]> Variants()=>EndfieldPaletteShaderTests.Variants();

    [Theory,MemberData(nameof(Variants))]
    public void All48NativeGiMorphSubHairRimVariantsCompileWithOneCapturedCpre(
        string entry,bool native,bool gi,bool morph,bool sub,bool hair,bool rim)
    {
        byte[] shader=OriginalFrameShaders.CompileEndfieldBaseImageSurface(entry,native,gi,morph,sub,hair,rim);
        Assert.Equal(new byte[]{68,88,66,67},shader[..4]);
        string source=OriginalFrameShaders.EndfieldBaseImageSurfaceSource(entry,native,gi,morph,sub,hair,rim);
        string old=OriginalFrameShaders.EndfieldPaletteSurfaceSource(entry,native,gi,morph,sub,hair,rim);
        Assert.DoesNotContain("BaseImageColorMap",old);
        foreach(string slot in new[]{"b14","t24","t25","t26","t27"}) Assert.Contains($"register({slot})",source);
        Assert.Equal(1,Count(source,"effectiveCpre=EndfieldBaseImageCpre(v,primitive,front,uv,baseGeometric,basePerturbed,rangeQ);"));
        Assert.Equal(2,Count(source,"EndfieldMatcapMappedNormal("));
        Assert.Contains("float3 initial=effectiveCpre;",source);
        Assert.Contains("float3 oldHair=MatcapBaseBaseline.rgb*p;",source);
        Assert.Contains("EndfieldMatcapOverlay(MatcapBaseBaseline.rgb,tint,saturate(r))",source);
        Assert.Contains("float3 changed=effectiveCpre*((1-gate)+gate*screen);",source);
        Assert.Contains("return emission-initial*MatcapBaseBaseline.w-oldHair-oldRim;",source);
        Assert.DoesNotContain("color.a*=",source);
        Assert.DoesNotContain("BaseImageColorMap.SampleLevel(MatcapSampler,uv,0).a",source);
    }

    [Fact]
    public void NineVectorHostAbiMatchesExplicitShaderInputs()
    {
        Type host=typeof(D3D12OriginalStyleRenderer).GetNestedType("EndfieldBaseImageConstants",BindingFlags.NonPublic)!;
        Assert.NotNull(host); Assert.Equal(144,Marshal.SizeOf(host));
        var binding=new D3D12EndfieldBaseImageBinding(new(.1f,.2f,.3f),new(.4f,.5f,.6f),new(.7f,.8f,.9f),new(1,2,3),
            ShadowFactor:.2f,WarmTint:.3f,Contrast:.4f,Saturation:.5f,DetailBlue:.6f,ShadowStrength:.7f);
        object value=host.GetMethod("From",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[binding,SceneCamera.Default])!;
        Assert.Equal(new Vector4(.1f,.2f,.3f,.4f),host.GetProperty("BaseContrast")!.GetValue(value));
        Assert.Equal(new Vector4(.4f,.5f,.6f,.2f),host.GetProperty("ModelShadow")!.GetValue(value));
        Assert.Equal(new Vector4(.7f,.8f,.9f,.3f),host.GetProperty("ShadowTint")!.GetValue(value));
        Assert.Equal(new Vector4(1,2,3,.5f),host.GetProperty("MultiplierSaturation")!.GetValue(value));
        Assert.Equal(new Vector4(.6f,.7f,0,0),host.GetProperty("Options")!.GetValue(value));
        Assert.Equal(Vector4.Zero,host.GetProperty("Modes")!.GetValue(value));
    }

    [Fact]
    public void UnsupportedEntryFailsBeforeCompilation()=>Assert.Throws<ArgumentOutOfRangeException>(()=>
        OriginalFrameShaders.EndfieldBaseImageSurfaceSource("PSDepth",false,false,false,false));

    private static int Count(string source,string text)=>source.Split(text,StringSplitOptions.None).Length-1;
}
