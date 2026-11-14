using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class GenshinMetallicSpecularStyleMapTests
{
    private static readonly D3D12GenshinMetallicBinding Parent = new(0,1,
        D3D12MatcapColorEncoding.LinearRgb,Vector3.One,Vector3.One,Vector3.One);
    private static readonly D3D12GenshinMetallicSpecularBinding Child = new(new(.2f),new(.8f),
        RampCombinedTextureIndex:2,RampMix:.5f);
    private static SceneTexture Image(byte r=240,byte g=100,byte b=128,byte a=255)
        =>new("explicit numerical image",1,1,[r,g,b,a]);
    private static OriginalFrameScene Frame(SceneTexture[]? images=null)
        =>D3D12EndfieldHairBindingTests.Frame(textures:images??[Image(),Image(),Image()]);
    private static D3D12SurfaceStyleMap Map(D3D12GenshinMetallicBinding binding)
        =>new([D3D12SurfaceStyle.GenshinPmx],[new(GenshinMetallic:binding)]);

    [Fact]
    public void StructuralParentAndExplicitRampGuardsApplyEvenWhenDisabled()
    {
        var parent=Parent with{Specular=Child}; Map(parent).Validate(Frame());
        Assert.True(Map(parent).HasConsumedGenshinMetallicSpecular);
        Assert.False(Map(parent with{EnableMetallics=0}).HasConsumedGenshinMetallicSpecular);
        foreach(float enable in new[]{0f,1f})
        {
            Assert.Throws<ArgumentOutOfRangeException>(()=>Map(parent with{EnableMetallics=enable,
                Specular=Child with{RampCombinedTextureIndex=3}}).Validate(Frame()));
            Assert.Throws<ArgumentException>(()=>Map(parent with{EnableMetallics=enable})
                .Validate(Frame([Image(),Image(),SceneTexture.ProceduralToon()])));
        }
        Map(Parent with{Specular=Child with{RampMix=0,RampCombinedTextureIndex=null}}).Validate(Frame([Image(),Image()]));
    }

    [Fact]
    public void ConsumedSnapshotAddsLightmapBlueAndRampRgbWhileIgnoringBothAlphasAndLightmapGreen()
    {
        var map=Map(Parent with{Specular=Child});
        var initial=map.CaptureFaceTextures(Frame());
        Assert.True(D3D12SurfaceStyleMap.Equivalent(initial,map.CaptureFaceTextures(Frame([
            Image(g:1,a:0),Image(a:0),Image(a:0)]))));
        foreach(var images in new[]{new[]{Image(b:1),Image(),Image()},new[]{Image(),Image(),Image(r:1)},
            new[]{Image(),Image(),Image(g:1)},new[]{Image(),Image(),Image(b:1)}})
            Assert.False(D3D12SurfaceStyleMap.Equivalent(initial,map.CaptureFaceTextures(Frame(images))));
        var rebased=Map(Parent with{LightmapCombinedTextureIndex=2,MatcapCombinedTextureIndex=0,
            Specular=Child with{RampCombinedTextureIndex=1}}).CaptureFaceTextures(Frame());
        Assert.True(D3D12SurfaceStyleMap.Equivalent(initial,rebased));
    }

    [Fact]
    public void UnconsumedRampDoesNotChangeAppearanceButAllLobeParametersDo()
    {
        var map=Map(Parent with{Specular=Child with{RampMix=0}});
        Assert.True(D3D12SurfaceStyleMap.Equivalent(map.CaptureFaceTextures(Frame()),
            map.CaptureFaceTextures(Frame([Image(),Image(),Image(r:1,g:2,b:3)]))));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(map,Map(Parent with{Specular=Child with{RampMix=0,RampCombinedTextureIndex=null}})));
        var active=Map(Parent with{Specular=Child});
        foreach(var child in new[]{Child with{Color=new(.1f)},Child with{SharpColor=new(.7f)},
            Child with{Shininess=17},Child with{Scale=2},Child with{Attenuation=.3f},
            Child with{SharpOffset=.4f},Child with{RampMix=.3f}})
            Assert.False(D3D12SurfaceStyleMap.Equivalent(active,Map(Parent with{Specular=child})));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(Map(Parent with{EnableMetallics=0,Specular=Child}),
            Map(Parent with{EnableMetallics=0})));
    }
}
