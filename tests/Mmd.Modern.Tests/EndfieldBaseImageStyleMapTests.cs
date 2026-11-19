using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EndfieldBaseImageStyleMapTests
{
    private static readonly EndfieldBaseImageSpec Input = new(new(.2f,.3f,.4f),new(.5f),Vector3.One,Vector3.One,
        ShadowStrength:.7f, BaseInputMode:T8dTestHelpers.ModeTexture,BaseSlot:3,
        DetailInputMode:T8dTestHelpers.ModeTexture,DetailSlot:4,
        ShadowInputMode:T8dTestHelpers.ModeTexture,ShadowSlot:5);
    private static StyleMaterialBindingData MatcapParent(EndfieldBaseImageSpec? input = null)
        => T8dTestHelpers.EndfieldMatcap(0,1,2,T8dTestHelpers.EncodingLinearRgb,0,
            new(.2f),new(.4f),coupleEndfieldPalette:true,
            palette:new EndfieldPaletteSpec(Vector3.Zero),
            baseImage:input??Input);
    private static D3D12SurfaceStyleMap Map(EndfieldBaseImageSpec? input = null, OriginalFrameScene? frame = null, bool validate = false)
        => T8dTestHelpers.CreateDirectSurfaceStyleMap(frame ?? Frame(), ["endfield"],
            [new(D3D12CharacterMaterialRole.Body)], [[MatcapParent(input)]], validatePlugin: validate);
    private static SceneTexture[] Images() => Enumerable.Range(0,6).Select(i=>new SceneTexture("explicit image",1,1,[(byte)(40+i),80,120,230])).ToArray();
    private static OriginalFrameScene Frame(SceneTexture[]? images=null) => D3D12EndfieldHairBindingTests.Frame(textures:images??Images());
    private static D3D12SurfaceStyleMap Captured(EndfieldBaseImageSpec b,SceneTexture[]? images=null)
    { var frame=Frame(images); var map=Map(b,frame); map.Validate(frame); return map.CaptureResolvedInputs(frame); }

    [Fact]
    public void SelectedImagesGuardBoundsAndKindIncludingDormantShadow()
    {
        var frame=Frame();
        foreach(bool dormant in new[]{false,true})
        foreach(int slot in new[]{3,4,5})
        {
            var input=Input with{ShadowStrength=dormant?0:.7f};
            var invalid=slot switch {3=>input with{BaseSlot=6},4=>input with{DetailSlot=6},_=>input with{ShadowSlot=6}};
            Assert.False(T8dTestHelpers.ValidateEndfield(MatcapParent(invalid),StyleMaterialRole.Body,6).IsValid);
            if(!dormant||slot!=5)
                Assert.Throws<ArgumentOutOfRangeException>(()=>Map(invalid,frame).Validate(frame));
            var guard=D3D12MaterialBindingLayout.Capture(frame).CaptureTexture(slot);
            var images=Images();images[slot]=SceneTexture.ProceduralToon();
            Assert.Throws<ArgumentException>(()=>D3D12MaterialBindingLayout.Capture(Frame(images)).ValidateTexture(guard));
        }
    }

    [Fact]
    public void DetachedHistoryCapturesBoundTexturesAndIsolatesSnapshots()
    {
        var original=Captured(Input);
        var renamed=Images();
        renamed[3]=new("renamed base",1,1,renamed[3].Rgba);
        renamed[4]=new("renamed detail",1,1,renamed[4].Rgba);
        renamed[5]=new("renamed shadow",1,1,renamed[5].Rgba);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(original,Captured(Input,renamed)));
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
        var rebased=Input with{BaseSlot=4,DetailSlot=3};
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first,Captured(rebased,images)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(Input),Map(rebased)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first,Captured(Input with{BaseEncoding=T8dTestHelpers.EncodingSrgbRgb})));
    }

    [Fact]
    public void CombinedCpreDomainRejectsExtremeProductsBeforeRecordingAndAllowsSignedHdr()
    {
        var extreme=Input with{BaseInputMode=T8dTestHelpers.ModeUniform,BaseSlot=null,
            BaseImageLinearRgb=new(16),Contrast=4,ColorMultiplier=new(16),DetailInputMode=T8dTestHelpers.ModeUniform,
            DetailSlot=null,DetailBlue=16};
        Assert.False(T8dTestHelpers.ValidateEndfield(MatcapParent(extreme),StyleMaterialRole.Body,6).IsValid);
        Assert.Throws<ArgumentException>(()=>Map(extreme,validate:true));
        var safe=extreme with{BaseImageLinearRgb=new(-.2f,.5f,1.1f),Contrast=-.2f,ColorMultiplier=new(.8f,1.1f,.9f),DetailBlue=-.3f};
        Assert.True(T8dTestHelpers.ValidateEndfield(MatcapParent(safe),StyleMaterialRole.Body,6).IsValid);
        Map(safe,validate:true).Validate(Frame());
    }
}
