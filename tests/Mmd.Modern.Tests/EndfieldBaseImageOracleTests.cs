using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EndfieldBaseImageOracleTests
{
    private static D3D12EndfieldBaseImageBinding Neutral => new(new(.2f,.4f,.7f), new(.5f), Vector3.One, Vector3.One, ShadowStrength:0);

    [Theory, InlineData(0), InlineData(1), InlineData(2)]
    public void FixedTablesAreHalfQuantized257TexelsAndSampleNormalizedCoordinates(int row)
    {
        Assert.Equal(771, D3D12EndfieldShadowLut.Values.Length);
        Assert.Equal(771 * 16, D3D12EndfieldShadowLut.Bytes.Length);
        foreach (float q in new[] {-float.MaxValue,-1,0,.125f,.25f,.499f,.5f,.75f,1,3,float.MaxValue})
        {
            float coordinate = Math.Clamp(q,0,1)*257-.5f;
            int j=(int)MathF.Floor(coordinate); float f=coordinate-j;
            Vector4 a=D3D12EndfieldShadowLut.Values[row*257+Math.Clamp(j,0,256)];
            Vector4 b=D3D12EndfieldShadowLut.Values[row*257+Math.Clamp(j+1,0,256)];
            Near(EndfieldBaseImageOracle.SampleLut(row,q),new Vector3(a.X,a.Y,a.Z)*(1-f)+new Vector3(b.X,b.Y,b.Z)*f);
        }
        foreach (var v in D3D12EndfieldShadowLut.Values)
        { Assert.Equal((float)(Half)v.X,v.X); Assert.Equal((float)(Half)v.Y,v.Y); Assert.Equal((float)(Half)v.Z,v.Z); Assert.Equal(1,v.W); }
        Assert.Equal(.7998046875f,EndfieldBaseImageOracle.SampleLut(0,.5f).X);
        Assert.Equal(.6492919921875f,EndfieldBaseImageOracle.SampleLut(0,.25f).X);
        Near(new(1,.85302734375f,.681640625f),EndfieldBaseImageOracle.SampleLut(1,.5f));
        Near(new(1,.89990234375f,.75f),EndfieldBaseImageOracle.SampleLut(2,.5f));
        Assert.NotEqual(.8f,EndfieldBaseImageOracle.SampleLut(0,.5f).X);
    }

    [Fact]
    public void ShadowUsesStrictLumaBoundaryAndIndependentModelColor()
    {
        Vector3 m = new(.2f,.4f,.7f);
        Near(Vector3.Lerp(m,Vector3.One,.3f),EndfieldBaseImageOracle.Shadow(m,Vector3.One,.3f,0));
        Vector3 colored = new(2, .8f, 0); // luma < 1, despite a channel above white.
        Assert.True(Vector3.Distance(EndfieldBaseImageOracle.Shadow(m,colored,.3f,0),Vector3.Lerp(m,Vector3.One,.3f))>.1f);
        Vector3 whiteHigh = new(0,2,0); // luma > 1, despite two channels below white.
        Near(Vector3.Lerp(m,Vector3.One,.3f),EndfieldBaseImageOracle.Shadow(m,whiteHigh,.3f,0));
        Assert.NotEqual(EndfieldBaseImageOracle.Shadow(m,Vector3.One,.5f,0),EndfieldBaseImageOracle.Shadow(m,Vector3.One,.5f,1));
        Assert.Equal(EndfieldBaseImageOracle.Shadow(m,Vector3.One,.5f,1),EndfieldBaseImageOracle.Shadow(m,Vector3.One,.5f,9));
    }

    [Fact]
    public void BackfaceSquaresBeforeUnclampedContrastAndHsvRetainsSignedHdr()
    {
        var b=Neutral with {BaseImageLinearRgb=new(.2f, .7f, 1.4f),Contrast=-.6f,Saturation=1};
        Vector3 squared=b.BaseImageLinearRgb*b.BaseImageLinearRgb;
        Near(b.BaseImageLinearRgb*1.6f-b.BaseImageLinearRgb*b.BaseImageLinearRgb*.6f,EndfieldBaseImageOracle.Evaluate(b,true));
        Near(squared*1.6f-squared*squared*.6f,EndfieldBaseImageOracle.Evaluate(b,false));
        Near(new(0,.5f,.21428572f),EndfieldBaseImageOracle.Hsv(new(-.2f,.5f,.1f),1));
        Near(new(-1),EndfieldBaseImageOracle.Hsv(new(-2,-1,-3),1));
        Near(new(2),EndfieldBaseImageOracle.Hsv(new(.3f,2,.7f),0));
    }

    [Fact]
    public void BaseDetailAndExplicitShadowAreSeparateInputs()
    {
        var b=Neutral with {ModelColor=new(.1f,.3f,.6f),ShadowStrength=1,DetailBlue=.4f,ShadowFactor=.3f};
        Vector3 expected=b.BaseImageLinearRgb*.7f*b.ModelColor;
        Near(expected,EndfieldBaseImageOracle.Evaluate(b,true,sampledShadowFactor:0));
        Near(new Vector3(.3f,.4f,.8f)*.6f,EndfieldBaseImageOracle.Evaluate(Neutral,true,new(.3f,.4f,.8f),.2f,0));
        Assert.NotEqual(EndfieldBaseImageOracle.Evaluate(b,true),EndfieldBaseImageOracle.Evaluate(b with {ModelColor=new(.9f)},true));
    }

    [Fact]
    public void ValidationRejectsAmbiguousNonfiniteAndUnsupportedInputs()
    {
        Neutral.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(()=>(Neutral with { Contrast=5 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(()=>(Neutral with { Saturation=float.NaN }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(()=>(Neutral with { BaseImageLinearRgb=new(float.PositiveInfinity) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(()=>(Neutral with { DetailInputMode=D3D12EndfieldPaletteInputMode.Texture }).Validate());
        Assert.Throws<ArgumentException>(()=>(Neutral with { BaseCombinedTextureIndex=1 }).Validate());
        Assert.Throws<ArgumentException>(()=>(Neutral with { BaseEncoding=D3D12MatcapColorEncoding.SrgbRgb }).Validate());
    }

    [Fact]
    public void ConsumedEqualityTracksEffectiveInputsAndIgnoresDormantShadow()
    {
        var a=Neutral;
        Assert.True(D3D12EndfieldBaseImageBinding.ConsumedEquivalent(a,a with {ShadowFactor=-5,ShadowColor=new(7),WarmTint=4,ModelColor=new(3)}));
        Assert.False(D3D12EndfieldBaseImageBinding.ConsumedEquivalent(a,a with {Contrast=.2f}));
        var t=a with {BaseInputMode=D3D12EndfieldPaletteInputMode.Texture,BaseCombinedTextureIndex=3};
        Assert.True(t.ConsumesBaseTexture);
        Assert.True(D3D12EndfieldBaseImageBinding.ConsumedEquivalent(t,t with {BaseImageLinearRgb=new(3)}));
        Assert.False(D3D12EndfieldBaseImageBinding.ConsumedEquivalent(t,t with {BaseCombinedTextureIndex=4}));
        Assert.True(D3D12EndfieldBaseImageBinding.ConsumedEquivalent(t,t with {BaseCombinedTextureIndex=4},false));
    }

    private static void Near(Vector3 expected,Vector3 actual)=>Assert.InRange(Vector3.Distance(expected,actual),0,4e-6f);
}
