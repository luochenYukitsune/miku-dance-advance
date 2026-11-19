using System.Numerics;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EndfieldLiveMaterialShaderTests
{
    [Theory, MemberData(nameof(EndfieldPaletteShaderTests.Variants), MemberType=typeof(EndfieldPaletteShaderTests))]
    public void DynamicConsumersUseOneEvaluatedPackedSample(string entry,bool native,bool gi,bool morph,bool sub,bool hair,bool rim)
    {
        string moduleId=T8dTestHelpers.ResolveEndfieldModuleId(hair,rim,matcap:true,palette:true,baseImage:true);
        string source=T8dTestHelpers.AssembleEndfieldPixelSource(moduleId,entry,native,gi,morph,sub);
        Assert.Equal(1,Count(source,"MatcapNormalMap.SampleLevel(MdaLinearWrapSampler, uv, 0).rg"));
        Assert.Contains("EndfieldSharedNormal(geometric, perturbed, 1)",source);
        Assert.Contains("return MatcapReserved.y + (MatcapReserved.z * k) * saturate(q * k);",source);
        Assert.Contains("emission += addition * EndfieldLiveScale(EndfieldLiveAvailableBudget(v, primitive), max(addition, 0));",source);
        Assert.Contains("return max(budget - max(direct + indirect, 0), 0);",source);
        Assert.Contains("PaletteModes.x == 1 ? PaletteReceivedLightMap",source);
        Assert.Contains("if (PaletteModes.x == 2",source);
        if(native) Assert.Contains("NativeVisibility(i, light, v.WorldPosition, geometric, l, 0, 0, sample)",source);
    }

    [Fact]
    public void SevenVectorMatcapAbiCarriesOptionalNumericalRange()
    {
        var binding=T8dTestHelpers.EndfieldMatcap(0,1,2,T8dTestHelpers.EncodingLinearRgb,0,new(.2f),Vector3.One,
            highlightRange:new(.3f,.8f,new(Vector3.UnitX,.35f,1.5f)));
        var draw=T8dTestHelpers.BuildEndfieldDrawData([binding])!;
        var vecs=T8dTestHelpers.ReadCbvVectors(draw);
        Assert.Equal(7,vecs.Length);
        Assert.Equal(new Vector4(1,.3f,.8f,1.5f),vecs[4]);
        Assert.Equal(new Vector4(1,0,0,.35f),vecs[5]);
    }

    [Fact]
    public void LiveEnumIsRejectedByAllNonReceivedBaseImageInputs()
    {
        var matcap=T8dTestHelpers.EndfieldMatcap(coupleEndfieldPalette:true);
        var palette=new EndfieldPaletteSpec(Vector3.Zero).ToBinding();
        var binding=new EndfieldBaseImageSpec(Vector3.One,Vector3.One,Vector3.One,Vector3.One);
        Assert.False(T8dTestHelpers.ValidateEndfield([matcap,palette,(binding with {BaseInputMode=T8dTestHelpers.ModeRendererDiffuse}).ToBinding()]).IsValid);
        Assert.False(T8dTestHelpers.ValidateEndfield([matcap,palette,(binding with {DetailInputMode=T8dTestHelpers.ModeRendererDiffuse}).ToBinding()]).IsValid);
        Assert.False(T8dTestHelpers.ValidateEndfield([matcap,palette,(binding with {ShadowInputMode=T8dTestHelpers.ModeRendererDiffuse}).ToBinding()]).IsValid);
    }

    private static int Count(string source,string text)=>source.Split(text,StringSplitOptions.None).Length-1;
}
