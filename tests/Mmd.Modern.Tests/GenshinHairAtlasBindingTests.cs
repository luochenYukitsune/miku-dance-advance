using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class GenshinHairAtlasBindingTests
{
    [Fact]
    public void ExplicitSharedPaletteAllowsBodyAndHairButKeepsRbHairOnly()
    {
        var binding=GenshinHairAtlasSurfaceTests.Binding;
        var frame=GenshinHairAtlasSurfaceTests.Frame(GenshinHairAtlasSurfaceTests.Atlas());
        var map=GenshinHairAtlasSurfaceTests.Map(binding,new(0));
        map.Validate(frame);Assert.True(map.HasConsumedGenshinHairShadowAtlas);
        var body=new D3D12SurfaceStyleMap([D3D12SurfaceStyle.GenshinPmx],[new(GenshinHairShadowAtlas:binding)]);
        body.Validate(frame);Assert.True(body.HasConsumedGenshinHairShadowAtlas);Assert.Same(binding,body.EffectiveGenshinHairShadowAtlas(0));
        Assert.Throws<ArgumentException>(()=>new D3D12CharacterMaterialStyle(GenshinHairShadowAtlas:binding,GenshinNonMetallicSpecular:new(0)).Validate());
        foreach(var role in new[]{D3D12CharacterMaterialRole.Face,D3D12CharacterMaterialRole.Eye})
            Assert.Throws<ArgumentException>(()=>new D3D12CharacterMaterialStyle(role,GenshinHairShadowAtlas:binding).Validate());
        foreach(var family in Enum.GetValues<D3D12SurfaceStyle>().Where(f=>f!=D3D12SurfaceStyle.GenshinPmx))
            Assert.Throws<NotSupportedException>(()=>new D3D12SurfaceStyleMap([family],map.CharacterMaterials).Validate(frame));
        Assert.Throws<ArgumentException>(()=>new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Hair,
            GenshinHairShadowAtlas:binding,PmxMaterialTreatment:new()).Validate());
        Assert.Throws<ArgumentException>(()=>new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Hair,
            GenshinHairShadowAtlas:binding,EndfieldHair:new(0,System.Numerics.Vector3.One)).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(()=>GenshinHairAtlasSurfaceTests.Map(binding with {LightmapCombinedTextureIndex=3}).Validate(frame));
        Assert.Throws<ArgumentOutOfRangeException>(()=>GenshinHairAtlasSurfaceTests.Map(binding with {AtlasCombinedTextureIndex=3}).Validate(frame));
    }

    [Fact]
    public void FiniteSignedSourceDomainRetainsSafeZeroAndRejectsMalformedInputs()
    {
        var b=GenshinHairAtlasSurfaceTests.Binding;
        (b with {ShadowPosition=0,ShadowRampWidth=0,VertexWidth=0,VertexAo=-16}).Validate();
        (b with {ShadowPosition=-16,ShadowRampWidth=-16,VertexWidth=16,VertexAo=16,ExplicitRowId=2.5f}).Validate();
        foreach(var bad in new[]{b with {LightmapCombinedTextureIndex=-1},b with {AtlasCombinedTextureIndex=-1},
            b with {AtlasEncoding=(D3D12GenshinAtlasColorEncoding)99},b with {ExplicitRowId=.9f},b with {ExplicitRowId=5.1f},
            b with {DayNight=1.1f},b with {UseLightmapAo=-.1f},b with {UseVertexColorAo=1.1f},
            b with {ShadowPosition=17},b with {ShadowRampWidth=float.NaN},b with {VertexWidth=float.PositiveInfinity},
            b with {VertexAo=float.NegativeInfinity}})
            Assert.Throws<ArgumentOutOfRangeException>(bad.Validate);
    }

    [Fact]
    public void EveryConsumedScalarAndRemovalChangesDetachedKeyButUnusedChannelsDoNot()
    {
        var b=GenshinHairAtlasSurfaceTests.Binding;
        var map=GenshinHairAtlasSurfaceTests.Map(b);
        var frame=GenshinHairAtlasSurfaceTests.Frame(GenshinHairAtlasSurfaceTests.Atlas());
        var first=map.CaptureFaceTextures(frame);
        Assert.NotSame(map,first);
        foreach(var changed in new[]{b with {AtlasEncoding=D3D12GenshinAtlasColorEncoding.SrgbRgb},b with {ExplicitRowId=2},
            b with {DayNight=.5f},b with {ShadowPosition=.7f},b with {ShadowRampWidth=.7f},b with {VertexWidth=.7f},
            b with {VertexAo=.7f},b with {UseLightmapAo=1},b with {UseVertexColorAo=1}})
            Assert.False(D3D12SurfaceStyleMap.Equivalent(first,GenshinHairAtlasSurfaceTests.Map(changed).CaptureFaceTextures(frame)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first,GenshinHairAtlasSurfaceTests.Map(null)));
        var colors=GenshinHairAtlasSurfaceTests.Atlas();for(int i=3;i<colors.Length;i+=4)colors[i]=255;
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first,map.CaptureFaceTextures(GenshinHairAtlasSurfaceTests.Frame(colors,unusedAlpha:255))));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first,map.CaptureFaceTextures(GenshinHairAtlasSurfaceTests.Frame(colors,green:80))));
        colors[0]++;
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first,map.CaptureFaceTextures(GenshinHairAtlasSurfaceTests.Frame(colors))));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first,map.CaptureFaceTextures(frame)));
    }

    [Fact]
    public void BothSelectedSlotsRebaseByConsumedImageContentsWithoutMutatingOldSnapshot()
    {
        var frame=GenshinHairAtlasSurfaceTests.Frame(GenshinHairAtlasSurfaceTests.Atlas());
        var b=GenshinHairAtlasSurfaceTests.Binding;
        var first=GenshinHairAtlasSurfaceTests.Map(b).CaptureFaceTextures(frame);
        var old=frame.Surface.Snapshot;
        var swapped=new SceneSnapshot(old.Vertices.ToArray(),old.Indices.ToArray(),old.Materials,
            [old.Textures[1],old.Textures[0],old.Textures[2]],camera:old.Camera,lightDirection:old.LightDirection);
        var rebuilt=new OriginalFrameScene(new(swapped,frame.Surface.Materials,frame.Surface.Profile),options:frame.Options);
        var second=GenshinHairAtlasSurfaceTests.Map(b with {LightmapCombinedTextureIndex=1,AtlasCombinedTextureIndex=0}).CaptureFaceTextures(rebuilt);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first,second));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first,GenshinHairAtlasSurfaceTests.Map(b).CaptureFaceTextures(frame)));
    }
}
