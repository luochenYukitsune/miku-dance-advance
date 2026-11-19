using System.Numerics;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EndfieldSourceInputsShaderTests
{
    [Fact]
    public void OneCapturedPackedSampleFeedsIndependentSourceNormalsAndSingleCpre()
    {
        string source=T8dTestHelpers.AssembleEndfieldPixelSource("hair-rim-matcap-palette-base","PSSurfaceGiNative",true,true,true,true);
        Assert.Equal(1,Count(source,"MatcapNormalMap.SampleLevel(MdaLinearWrapSampler, uv, 0).rg"));
        Assert.Equal(1,Count(source,"MdaTangentCorner a = MatcapCorners[primitive * 3];"));
        Assert.Equal(1,Count(source,"float3 effectiveCpre = EndfieldBaseImageCpre(v, front, uv, baseGeometric, basePerturbed, rangeQ, camBackward, isOrtho);"));
        Assert.Contains("baseGeometric = geometric;",source);
        Assert.Contains("basePerturbed = perturbed;",source);
        Assert.Contains("EndfieldBaseImageSourceNormal(geometric, perturbed, 1)",source);
        Assert.Contains("EndfieldBaseImageSourceNormal(geometric, perturbed, BaseImageSourceOptions.w)",source);
        Assert.Contains("isOrtho ? camBackward : EyePosition.xyz - v.WorldPosition",source);
        Assert.Contains("facing = exponent == 0 ? 1 : pow(facing, exponent);",source);
        Assert.Contains("precise float a = BaseImageSourceDirectionWidth.w - .5;",source);
        Assert.Contains("precise float maximum = 1 - a;",source);
        Assert.Contains("return clamp(q, 0, 1.5);",source);
        Assert.Contains("MdaExtractCameraBasis(camRight, camUp, camForward, camBackward, isOrtho);",source);
        Assert.DoesNotContain("BaseImageSource",T8dTestHelpers.AssembleEndfieldPixelSource("hair-rim-matcap-palette","PSSurfaceGiNative",true,true,true,true));
    }

    [Fact]
    public void EightVectorBaseImageAbiKeepsExplicitWorldDirectionAndFacingBlend()
    {
        var matcap=T8dTestHelpers.EndfieldMatcap(coupleEndfieldPalette:true);
        var palette=new EndfieldPaletteSpec(Vector3.Zero).ToBinding();
        var baseImg=new EndfieldBaseImageSpec(new(.2f),new(.3f),Vector3.One,Vector3.One,
            FacingBase:new(.65f),DirectionalShadow:new(Vector3.UnitX,.45f,3)).ToBinding();
        var draw=T8dTestHelpers.BuildEndfieldDrawData([matcap,palette,baseImg])!;
        var vecs=T8dTestHelpers.ReadCbvVectors(draw);
        Assert.Equal(18,vecs.Length);
        Assert.Equal(new Vector4(1,0,0,.45f),vecs[16]);
        Assert.Equal(new Vector4(1,.65f,1,3),vecs[17]);
    }

    private static int Count(string source,string text)=>source.Split(text,StringSplitOptions.None).Length-1;
}
