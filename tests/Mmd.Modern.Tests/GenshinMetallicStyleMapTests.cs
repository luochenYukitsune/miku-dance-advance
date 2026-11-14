using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;
namespace Mmd.Modern.Tests;
public sealed class GenshinMetallicStyleMapTests
{
    private static readonly D3D12GenshinMetallicBinding Binding=new(0,1,D3D12MatcapColorEncoding.SrgbRgb,new(.2f),new(.8f),new(.7f));
    private static SceneTexture Image(byte r=240,byte g=70,byte b=40,byte a=230)=>new("explicit",1,1,[r,g,b,a]);
    private static OriginalFrameScene Frame(SceneTexture[]? images=null)=>D3D12EndfieldHairBindingTests.Frame(textures:images??[Image(),Image()]);
    private static D3D12SurfaceStyleMap Map(D3D12GenshinMetallicBinding? value,D3D12SurfaceStyle family=D3D12SurfaceStyle.GenshinPmx)
        =>new([family],[new(GenshinMetallic:value)]);
    [Fact]
    public void FamilyRolesAndSharedLightmapAreExplicitEvenWhenDisabled()
    {
        Map(Binding).Validate(Frame());
        Assert.True(Map(Binding).HasConsumedGenshinMetallic);
        Assert.False(Map(Binding with{EnableMetallics=0}).HasConsumedGenshinMetallic);
        foreach(var f in new[]{D3D12SurfaceStyle.Mmd,D3D12SurfaceStyle.EndfieldPmx,D3D12SurfaceStyle.StarRailPmx,D3D12SurfaceStyle.WutheringPmx})
            Assert.Throws<NotSupportedException>(()=>Map(Binding,f).Validate(Frame()));
        foreach(var role in new[]{D3D12CharacterMaterialRole.Face,D3D12CharacterMaterialRole.Eye})
            Assert.Throws<ArgumentException>(()=>new D3D12CharacterMaterialStyle(role,GenshinMetallic:Binding).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(()=>Map(Binding with{EnableMetallics=0,MatcapCombinedTextureIndex=2}).Validate(Frame()));
        foreach(var images in new[]{new[]{SceneTexture.ProceduralToon(),Image()},new[]{Image(),SceneTexture.ProceduralToon()}})
        {
            Assert.Throws<ArgumentException>(()=>Map(Binding).Validate(Frame(images)));
            Assert.Throws<ArgumentException>(()=>Map(Binding with{EnableMetallics=0}).Validate(Frame(images)));
        }
        new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Hair,GenshinNonMetallicSpecular:new(0),GenshinMetallic:Binding).Validate();
        Assert.Throws<ArgumentException>(()=>new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Hair,
            GenshinNonMetallicSpecular:new(1),GenshinMetallic:Binding).Validate());
        Assert.Throws<ArgumentException>(()=>new D3D12CharacterMaterialStyle(GenshinMetallic:Binding,PmxMaterialTreatment:new()).Validate());
    }
    [Fact]
    public void HistoryConsumesLightmapRedAndColorRgbButIgnoresBothUnconsumedAlphasAndRebasedSlots()
    {
        var original=Map(Binding).CaptureFaceTextures(Frame());
        var ignored=Map(Binding).CaptureFaceTextures(Frame([Image(g:1,b:1,a:1),Image(a:1)]));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(original,ignored));
        foreach(var images in new[]{new[]{Image(r:1),Image()},new[]{Image(),Image(r:1)},new[]{Image(),Image(g:1)},new[]{Image(),Image(b:1)}})
            Assert.False(D3D12SurfaceStyleMap.Equivalent(original,Map(Binding).CaptureFaceTextures(Frame(images))));
        var rebased=Map(Binding with{LightmapCombinedTextureIndex=1,MatcapCombinedTextureIndex=0}).CaptureFaceTextures(Frame());
        Assert.True(D3D12SurfaceStyleMap.Equivalent(original,rebased));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(Map(null),Map(Binding with{EnableMetallics=0})));
    }
    [Fact]
    public void AllConsumedParametersChangeHistoryWhileUniformLitIsDormantInPrimaryMode()
    {
        foreach(var b in new[]{Binding with{EnableMetallics=.5f},Binding with{TileScale=2},Binding with{Brightness=.5f},
            Binding with{DarkColor=new(.1f)},Binding with{LightColor=new(.9f)},Binding with{ShadowMultiplyColor=new(.4f)},
            Binding with{MatcapEncoding=D3D12MatcapColorEncoding.LinearRgb},Binding with{LitMode=D3D12GenshinMetallicLitMode.Uniform}})
            Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(Binding),Map(b)));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(Map(Binding),Map(Binding with{})));
        // UniformLitFactor is not read in PrimaryResponse mode.
        Assert.True(D3D12SurfaceStyleMap.Equivalent(Map(Binding),Map(Binding with{UniformLitFactor=.2f})));
        var uniform=Binding with{LitMode=D3D12GenshinMetallicLitMode.Uniform};
        Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(uniform),Map(uniform with{UniformLitFactor=.2f})));
    }
}
