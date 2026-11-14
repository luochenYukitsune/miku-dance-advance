using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EndfieldSourceInputsShaderTests
{
    [Fact]
    public void OneCapturedPackedSampleFeedsIndependentSourceNormalsAndSingleCpre()
    {
        string source=OriginalFrameShaders.EndfieldBaseImageSurfaceSource("PSSurfaceGiNative",true,true,true,true,true,true);
        Assert.Equal(1,Count(source,"MatcapNormalMap.SampleLevel(MatcapSampler,uv,0).rg"));
        Assert.Equal(1,Count(source,"MatcapCorner a=MatcapCorners[primitive*3]"));
        Assert.Equal(1,Count(source,"effectiveCpre=EndfieldBaseImageCpre(v,primitive,front,uv,baseGeometric,basePerturbed,rangeQ);"));
        Assert.Contains("baseGeometric=geometric;basePerturbed=perturbed;return true;",source);
        Assert.Contains("EndfieldBaseImageSourceNormal(geometric,perturbed,1)",source);
        Assert.Contains("EndfieldBaseImageSourceNormal(geometric,perturbed,BaseImageSourceOptions.w)",source);
        Assert.Contains("BaseImageSourceView.xyz-v.WorldPosition",source);
        Assert.Contains("exponent==0?1:pow(facing,exponent)",source);
        Assert.Contains("precise float a=BaseImageSourceDirectionWidth.w-.5;",source);
        Assert.Contains("precise float maximum=1-a;",source);
        Assert.Contains("return clamp(q,0,1.5);",source);
        Assert.DoesNotContain("BaseImageSource",OriginalFrameShaders.EndfieldPaletteSurfaceSource("PSSurfaceGiNative",true,true,true,true,true,true));
    }

    [Theory,InlineData(false),InlineData(true)]
    public void NineVectorAbiKeepsExplicitWorldDirectionAndProjectionCorrectIncoming(bool perspective)
    {
        Type host=typeof(D3D12OriginalStyleRenderer).GetNestedType("EndfieldBaseImageConstants",BindingFlags.NonPublic)!;
        Assert.Equal(144,Marshal.SizeOf(host));
        var binding=new D3D12EndfieldBaseImageBinding(new(.2f),new(.3f),Vector3.One,Vector3.One,
            FacingBase:new(.65f),DirectionalShadow:new(Vector3.UnitX,.45f,3));
        var camera=new SceneCamera(new(3,4,5),new(1,2,2),Vector3.UnitY,10,.1f,100,VerticalFovDegrees:perspective?45:null);
        object value=host.GetMethod("From",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[binding,camera])!;
        Assert.Equal(new Vector4(1,0,0,.45f),host.GetProperty("SourceDirectionWidth")!.GetValue(value));
        Assert.Equal(new Vector4(1,.65f,1,3),host.GetProperty("SourceOptions")!.GetValue(value));
        Assert.Equal(new Vector4(perspective?camera.Eye:Vector3.Normalize(camera.Eye-camera.Target),perspective?0:1),host.GetProperty("SourceView")!.GetValue(value));
        Assert.Equal(96,Marshal.OffsetOf(host,"<SourceDirectionWidth>k__BackingField").ToInt32());
        Assert.Equal(112,Marshal.OffsetOf(host,"<SourceOptions>k__BackingField").ToInt32());
        Assert.Equal(128,Marshal.OffsetOf(host,"<SourceView>k__BackingField").ToInt32());
    }

    private static int Count(string source,string text)=>source.Split(text,StringSplitOptions.None).Length-1;
}
