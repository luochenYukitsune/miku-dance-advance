using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EndfieldLiveMaterialShaderTests
{
    [Theory, MemberData(nameof(EndfieldPaletteShaderTests.Variants), MemberType=typeof(EndfieldPaletteShaderTests))]
    public void DynamicConsumersUseOneEvaluatedPackedSample(string entry,bool native,bool gi,bool morph,bool sub,bool hair,bool rim)
    {
        string source=OriginalFrameShaders.EndfieldBaseImageSurfaceSource(entry,native,gi,morph,sub,hair,rim);
        Assert.Equal(1,Count(source,"MatcapNormalMap.SampleLevel(MatcapSampler,uv,0).rg"));
        Assert.Contains("EndfieldSharedNormal(geometric,perturbed,1)",source);
        Assert.Contains("return MatcapReserved.y+(MatcapReserved.z*k)*saturate(q*k);",source);
        Assert.Contains("emission+=addition*EndfieldLiveScale(EndfieldLiveAvailableBudget(v,primitive),max(addition,0));",source);
        Assert.Contains("return max(budget-max(direct+indirect,0),0);",source);
        Assert.Contains("PaletteModes.x==1?PaletteReceivedLightMap",source);
        Assert.Contains("if(PaletteModes.x==2)",source);
        if(native) Assert.Contains("NativeVisibility(i,light,v.WorldPosition,geometric,l)",source);
    }

    [Fact]
    public void NineVectorMatcapAbiCarriesOptionalNumericalRange()
    {
        Type host=typeof(D3D12OriginalStyleRenderer).GetNestedType("EndfieldMatcapConstants",BindingFlags.NonPublic)!;
        Assert.Equal(144,Marshal.SizeOf(host));
        var binding=new D3D12EndfieldMatcapBinding(0,1,2,D3D12MatcapColorEncoding.LinearRgb,0,new(.2f),Vector3.One,
            HighlightRange:new(.3f,.8f,new(Vector3.UnitX,.35f,1.5f)));
        object value=host.GetMethod("From",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[binding,SceneCamera.Default])!;
        Assert.Equal(new Vector4(1,.3f,.8f,1.5f),host.GetProperty("Reserved")!.GetValue(value));
        Assert.Equal(new Vector4(1,0,0,.35f),host.GetProperty("RangeDirectionWidth")!.GetValue(value));
        Assert.Equal(128,Marshal.OffsetOf(host,"<RangeDirectionWidth>k__BackingField").ToInt32());
    }

    [Fact]
    public void LiveEnumIsRejectedByAllNonReceivedBaseImageInputs()
    {
        var binding=new D3D12EndfieldBaseImageBinding(Vector3.One,Vector3.One,Vector3.One,Vector3.One);
        Assert.Throws<ArgumentOutOfRangeException>(()=>(binding with {BaseInputMode=D3D12EndfieldPaletteInputMode.RendererDiffuse}).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(()=>(binding with {DetailInputMode=D3D12EndfieldPaletteInputMode.RendererDiffuse}).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(()=>(binding with {ShadowInputMode=D3D12EndfieldPaletteInputMode.RendererDiffuse}).Validate());
    }

    private static int Count(string source,string text)=>source.Split(text,StringSplitOptions.None).Length-1;
}
