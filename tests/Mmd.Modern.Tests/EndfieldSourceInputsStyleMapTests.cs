using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EndfieldSourceInputsStyleMapTests
{
    private static readonly EndfieldBaseImageSpec Basis=new(new(.4f),new(.5f),new(.6f),Vector3.One,
        FacingBase:new(.5f),DirectionalShadow:new(Vector3.UnitZ));
    private static StyleMaterialBindingData MatcapParent(EndfieldBaseImageSpec b)
        =>T8dTestHelpers.EndfieldMatcap(0,1,2,T8dTestHelpers.EncodingLinearRgb,0,new(.2f),Vector3.Zero,
            baselineStrength:1,coupleEndfieldPalette:true,palette:new EndfieldPaletteSpec(Vector3.Zero),baseImage:b);
    private static D3D12SurfaceStyleMap Map(EndfieldBaseImageSpec b,bool validate=false)
        =>T8dTestHelpers.CreateDirectSurfaceStyleMap(Frame(),["endfield"],[new(D3D12CharacterMaterialRole.Body)],
            [[MatcapParent(b)]],validatePlugin:validate);
    private static OriginalFrameScene Frame()=>D3D12EndfieldHairBindingTests.Frame(textures:
        Enumerable.Range(0,3).Select(_=>new SceneTexture("explicit",1,1,[128,128,128,255])).ToArray());

    [Fact]
    public void FacingCompositionExtendsHdrMagnitudeGuardBeforeRasterBackfaceSquare()
    {
        var explicitOnly=Basis with {BaseImageLinearRgb=new(2.8f),Contrast=4,ColorMultiplier=new(4),
            ShadowStrength=0,FacingBase=null,DirectionalShadow=null};
        Assert.True(T8dTestHelpers.ValidateEndfield(MatcapParent(explicitOnly),StyleMaterialRole.Body,3).IsValid);
        Map(explicitOnly,validate:true).Validate(Frame());
        var facing=explicitOnly with{FacingBase=new(.5f)};
        Assert.False(T8dTestHelpers.ValidateEndfield(MatcapParent(facing),StyleMaterialRole.Body,3).IsValid);
        Assert.Throws<ArgumentException>(()=>Map(facing,validate:true));
        var safe=facing with{BaseImageLinearRgb=new(-.2f,.5f,1.1f),Contrast=-.2f,ColorMultiplier=Vector3.One};
        Assert.True(T8dTestHelpers.ValidateEndfield(MatcapParent(safe),StyleMaterialRole.Body,3).IsValid);
        Map(safe,validate:true).Validate(Frame());
    }

    [Fact]
    public void DirectionalHistoryTracksDirectionWidthStrengthAndFacingBlend()
    {
        foreach(var provider in new[]{new EndfieldDirectionalShadowSpec(Vector3.UnitY),
            new(Vector3.UnitZ,.7f),new(Vector3.UnitZ,.3f,1)})
            Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(Basis),Map(Basis with{DirectionalShadow=provider})));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(Basis),Map(Basis with{FacingBase=new(.7f)})));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(Map(Basis),Map(Basis with{DirectionalShadow=new(Vector3.UnitZ,.3f,2f)})));
    }
}
