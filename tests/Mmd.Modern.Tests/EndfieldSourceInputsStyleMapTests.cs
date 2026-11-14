using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EndfieldSourceInputsStyleMapTests
{
    private static readonly D3D12EndfieldBaseImageBinding Basis=new(new(.4f),new(.5f),new(.6f),Vector3.One,
        FacingBase:new(.5f),DirectionalShadow:new(Vector3.UnitZ));
    private static D3D12SurfaceStyleMap Map(D3D12EndfieldBaseImageBinding b)=>new([D3D12SurfaceStyle.EndfieldPmx],
        [new(EndfieldMatcap:new(0,1,2,D3D12MatcapColorEncoding.LinearRgb,0,new(.2f),Vector3.Zero,
            BaselineStrength:1,CoupleEndfieldPalette:true,Palette:new(Vector3.Zero,BaseImage:b)))]);
    private static OriginalFrameScene Frame()=>D3D12EndfieldHairBindingTests.Frame(textures:
        Enumerable.Range(0,3).Select(_=>new SceneTexture("explicit",1,1,[128,128,128,255])).ToArray());

    [Fact]
    public void FacingCompositionExtendsHdrMagnitudeGuardBeforeRasterBackfaceSquare()
    {
        var explicitOnly=Basis with {BaseImageLinearRgb=new(2.8f),Contrast=4,ColorMultiplier=new(4),
            ShadowStrength=0,FacingBase=null,DirectionalShadow=null};
        Map(explicitOnly).Validate(Frame());
        var facing=explicitOnly with{FacingBase=new(.5f)};
        facing.Validate();
        Assert.Throws<ArgumentException>(()=>Map(facing).Validate(Frame()));
        Map(facing with{BaseImageLinearRgb=new(-.2f,.5f,1.1f),Contrast=-.2f,ColorMultiplier=Vector3.One}).Validate(Frame());
    }

    [Fact]
    public void DirectionalHistoryIgnoresDormantProviderButTracksActiveDirectionWidthAndStrength()
    {
        var inactive=Basis with{ShadowStrength=0};
        Assert.True(D3D12SurfaceStyleMap.Equivalent(Map(inactive),Map(inactive with{DirectionalShadow=new(Vector3.UnitY,.7f,1)})));
        foreach(var provider in new[]{new D3D12EndfieldDirectionalShadowBinding(Vector3.UnitY),
            new(Vector3.UnitZ,.7f),new(Vector3.UnitZ,.3f,1)})
            Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(Basis),Map(Basis with{DirectionalShadow=provider})));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(Basis),Map(Basis with{FacingBase=new(.7f)})));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(Map(Basis),Map(Basis with{ShadowFactor=-2})));
    }
}
