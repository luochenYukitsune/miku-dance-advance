using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EndfieldBaseImageStyleMapTests
{
    private static readonly D3D12EndfieldBaseImageBinding Input = new(new(.2f,.3f,.4f),new(.5f),Vector3.One,Vector3.One,
        ShadowStrength:.7f, BaseInputMode:D3D12EndfieldPaletteInputMode.Texture,BaseCombinedTextureIndex:3,
        DetailInputMode:D3D12EndfieldPaletteInputMode.Texture,DetailCombinedTextureIndex:4,
        ShadowInputMode:D3D12EndfieldPaletteInputMode.Texture,ShadowCombinedTextureIndex:5);
    private static D3D12SurfaceStyleMap Map(D3D12EndfieldBaseImageBinding? input = null)
        => new([D3D12SurfaceStyle.EndfieldPmx],[new(EndfieldMatcap:new(0,1,2,D3D12MatcapColorEncoding.LinearRgb,0,
            new(.2f),new(.4f),CoupleEndfieldPalette:true,Palette:new(Vector3.Zero,BaseImage:input??Input)))]);
    private static SceneTexture[] Images() => Enumerable.Range(0,6).Select(i=>new SceneTexture("explicit image",1,1,[(byte)(40+i),80,120,230])).ToArray();
    private static OriginalFrameScene Frame(SceneTexture[]? images=null) => D3D12EndfieldHairBindingTests.Frame(textures:images??Images());
    private static D3D12SurfaceStyleMap Captured(D3D12EndfieldBaseImageBinding b,SceneTexture[]? images=null)
    { var map=Map(b);var frame=Frame(images);map.Validate(frame);return map.CaptureFaceTextures(frame); }

    [Fact]
    public void SelectedImagesGuardBoundsAndKindIncludingDormantShadow()
    {
        foreach(bool dormant in new[]{false,true})
        foreach(int slot in new[]{3,4,5})
        {
            var input=Input with{ShadowStrength=dormant?0:.7f};
            var invalid=slot switch {3=>input with{BaseCombinedTextureIndex=6},4=>input with{DetailCombinedTextureIndex=6},_=>input with{ShadowCombinedTextureIndex=6}};
            Assert.Throws<ArgumentOutOfRangeException>(()=>Map(invalid).Validate(Frame()));
            var images=Images();images[slot]=SceneTexture.ProceduralToon();
            Assert.Throws<ArgumentException>(()=>Map(input).Validate(Frame(images)));
        }
    }

    [Fact]
    public void DetachedHistoryConsumesOnlyBaseRgbDetailBlueAndShadowRed()
    {
        var original=Captured(Input);
        var ignored=Images();
        ignored[3]=new("base alpha only",1,1,[43,80,120,0]);
        ignored[4]=new("detail ignored RGA",1,1,[1,2,120,3]);
        ignored[5]=new("shadow ignored GBA",1,1,[45,2,3,4]);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(original,Captured(Input,ignored)));
        foreach((int slot,int channel) in new[]{(3,0),(3,1),(3,2),(4,2),(5,0)})
        {
            var images=Images();var rgba=images[slot].Rgba.ToArray();rgba[channel]++;
            images[slot]=new("consumed channel changed",1,1,rgba);
            Assert.False(D3D12SurfaceStyleMap.Equivalent(original,Captured(Input,images)));
        }
        Assert.True(D3D12SurfaceStyleMap.Equivalent(original,Captured(Input)));
    }

    [Fact]
    public void DormantShadowHistoryIgnoresQContentsButActiveHistoryTracksIt()
    {
        var dormant=Input with{ShadowStrength=0};var images=Images();images[5]=new("q edited",1,1,[255,0,0,0]);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(Captured(dormant),Captured(dormant,images)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(Captured(Input),Captured(Input,images)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(Captured(dormant),Captured(Input)));
    }

    [Fact]
    public void RebasedSnapshotsCompareConsumedImagesAndNotTransientSlots()
    {
        var images=Images();var first=Captured(Input,images);
        (images[3],images[4])=(images[4],images[3]);
        var rebased=Input with{BaseCombinedTextureIndex=4,DetailCombinedTextureIndex=3};
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first,Captured(rebased,images)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(Input),Map(rebased)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first,Captured(Input with{BaseEncoding=D3D12MatcapColorEncoding.SrgbRgb})));
    }

    [Fact]
    public void CombinedCpreDomainRejectsExtremeProductsBeforeRecordingAndAllowsSignedHdr()
    {
        var extreme=Input with{BaseInputMode=D3D12EndfieldPaletteInputMode.Uniform,BaseCombinedTextureIndex=null,
            BaseImageLinearRgb=new(16),Contrast=4,ColorMultiplier=new(16),DetailInputMode=D3D12EndfieldPaletteInputMode.Uniform,
            DetailCombinedTextureIndex=null,DetailBlue=16};
        extreme.Validate();Assert.Throws<ArgumentException>(()=>Map(extreme).Validate(Frame()));
        Map(extreme with{BaseImageLinearRgb=new(-.2f,.5f,1.1f),Contrast=-.2f,ColorMultiplier=new(.8f,1.1f,.9f),DetailBlue=-.3f}).Validate(Frame());
    }
}
