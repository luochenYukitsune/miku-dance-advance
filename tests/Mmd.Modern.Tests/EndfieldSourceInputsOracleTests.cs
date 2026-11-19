using System.Numerics;
using Mmd.Modern.Plugins.Contracts;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EndfieldSourceInputsOracleTests
{
    private static EndfieldBaseImageSpec Neutral=>new(new(.2f,.5f,.8f),new(.5f),Vector3.One,Vector3.One,ShadowStrength:0);

    [Theory,InlineData(0),InlineData(.5f),InlineData(1)]
    public void FacingPreservesEndpointAndDefinedZeroPowerBehavior(float blend)
    {
        Assert.Equal(0,EndfieldSourceInputsOracle.Facing(Vector3.UnitZ,Vector3.UnitZ,blend));
        Assert.Equal(blend==0?0:1,EndfieldSourceInputsOracle.Facing(Vector3.UnitZ,Vector3.UnitX,blend));
        Assert.Equal(EndfieldSourceInputsOracle.Facing(Vector3.UnitZ,Vector3.Normalize(new(1,0,1)),blend),
            EndfieldSourceInputsOracle.Facing(-Vector3.UnitZ,Vector3.Normalize(new(1,0,1)),blend));
        Assert.Equal(0,EndfieldSourceInputsOracle.Facing(Vector3.Zero,Vector3.Zero,0));
    }

    [Fact]
    public void FacingIndependentStrengthOneAndSignedHdrMixPrecedeBackfaceSquare()
    {
        Vector3 geometric=Vector3.UnitZ,perturbed=Vector3.UnitX,m=new(-.5f,.25f,2);
        Near(m*m,EndfieldSourceInputsOracle.Base(m,geometric,perturbed,Vector3.UnitZ,.5f));
        Near(m,EndfieldSourceInputsOracle.Base(m,geometric,perturbed,Vector3.UnitZ,0));
        var b=Neutral with {BaseImageLinearRgb=m,FacingBase=new(.5f),Saturation=0};
        // Facing gives M squared, then the accepted backface stage squares that value again.
        Near(new(16),EndfieldSourceInputsOracle.Evaluate(b,false,geometric,perturbed,Vector3.UnitZ));
        Assert.NotEqual(EndfieldSourceInputsOracle.Normal(geometric,perturbed,1),EndfieldSourceInputsOracle.Normal(geometric,perturbed,2));
    }

    [Theory,InlineData(.3f),InlineData(-2),InlineData(2),InlineData(1),InlineData(1.5f)]
    public void DirectionalMapRangeHandlesOrdinaryReversedAndCollapsedEndpoints(float width)
    {
        var b=new EndfieldDirectionalShadowSpec(Vector3.UnitZ,width);
        foreach(float z in new[]{-1f,-.5f,0,.5f,1})
        {
            float max=1-(width-.5f),min=max*width,u=z*.5f+.5f;
            float expected=max==min?0:Math.Clamp((u-min)/(max-min)*1.5f,0,1.5f);
            Assert.Equal(expected,EndfieldSourceInputsOracle.DirectionalQ(new(0,0,z),b));
        }
    }

    [Fact]
    public void QAboveOneSurvivesAndDoesNotReuseOldMatcapMapRange()
    {
        var b=new EndfieldDirectionalShadowSpec(Vector3.UnitZ);
        float q=EndfieldSourceInputsOracle.DirectionalQ(Vector3.UnitZ,b);
        Assert.InRange(q,1.14f,1.15f);
        float oldA=1-(1-b.Width),oldMinimum=oldA*b.Width;
        float old=1.5f*Math.Clamp((1-oldMinimum)/(oldA-oldMinimum),0,1);
        Assert.Equal(1.5f,old); Assert.NotEqual(old,q);
        Assert.Equal(0,EndfieldSourceInputsOracle.DirectionalQ(-Vector3.UnitZ,b));
        Assert.Equal(1.5f,EndfieldSourceInputsOracle.DirectionalQ(Vector3.UnitZ,b with {Width=.8f}));
    }

    [Fact]
    public void IndependentNormalStrengthAndZeroCancellationHaveExplicitPolicy()
    {
        Vector3 g=Vector3.UnitZ,p=Vector3.UnitX;
        Near(g,EndfieldSourceInputsOracle.Normal(g,p,0));
        Near(p,EndfieldSourceInputsOracle.Normal(g,p,1));
        Near(Vector3.Normalize(new Vector3(2,0,-1)),EndfieldSourceInputsOracle.Normal(g,p,2));
        Near(Vector3.Zero,EndfieldSourceInputsOracle.Normal(g,g*.5f,2));
        var b=new EndfieldDirectionalShadowSpec(Vector3.UnitZ);
        Assert.NotEqual(EndfieldSourceInputsOracle.DirectionalQ(g,b),EndfieldSourceInputsOracle.DirectionalQ(p,b));
    }

    [Fact]
    public void DisabledChildrenKeepAcceptedNumericalPathAndDirectionalOverridesDormantQ()
    {
        var b=Neutral with {ShadowStrength=1};
        Near(EndfieldBaseImageOracle.Evaluate(b,true),EndfieldSourceInputsOracle.Evaluate(b,true,Vector3.UnitZ,Vector3.UnitX,Vector3.UnitZ));
        var directional=b with {DirectionalShadow=new(Vector3.UnitZ)};
        Near(EndfieldSourceInputsOracle.Evaluate(directional,true,Vector3.UnitZ,Vector3.UnitX,Vector3.UnitZ),
            EndfieldSourceInputsOracle.Evaluate(directional with {ShadowFactor=-23},true,Vector3.UnitZ,Vector3.UnitX,Vector3.UnitZ));
    }

    [Fact]
    public void ValidationRejectsAmbiguityAndNonfiniteOrNonNormalizedSourceInputs()
    {
        var matcap=T8dTestHelpers.EndfieldMatcap(coupleEndfieldPalette:true);
        var palette=new EndfieldPaletteSpec(Vector3.Zero).ToBinding();
        Assert.True(T8dTestHelpers.ValidateEndfield([matcap,palette,(Neutral with {FacingBase=new(1),DirectionalShadow=new(Vector3.UnitZ,-4,16)}).ToBinding()]).IsValid);
        foreach(float bad in new[]{-.01f,1.01f,float.NaN,float.PositiveInfinity})
            Assert.False(T8dTestHelpers.ValidateEndfield([matcap,palette,(Neutral with {FacingBase=new(bad)}).ToBinding()]).IsValid);
        foreach(Vector3 bad in new[]{Vector3.Zero,new Vector3(0,0,2),new Vector3(float.NaN,0,1),new Vector3(float.PositiveInfinity,0,1)})
            Assert.False(T8dTestHelpers.ValidateEndfield([matcap,palette,(Neutral with {DirectionalShadow=new(bad)}).ToBinding()]).IsValid);
        Assert.False(T8dTestHelpers.ValidateEndfield([matcap,palette,(Neutral with {DirectionalShadow=new(Vector3.UnitZ,4.1f)}).ToBinding()]).IsValid);
        Assert.False(T8dTestHelpers.ValidateEndfield([matcap,palette,(Neutral with {DirectionalShadow=new(Vector3.UnitZ,NormalStrength:-1)}).ToBinding()]).IsValid);
        Assert.False(T8dTestHelpers.ValidateEndfield([matcap,palette,(Neutral with {DirectionalShadow=new(Vector3.UnitZ,NormalStrength:float.NaN)}).ToBinding()]).IsValid);
        Assert.False(T8dTestHelpers.ValidateEndfield([matcap,palette,(Neutral with {DirectionalShadow=new(Vector3.UnitZ),ShadowInputMode=T8dTestHelpers.ModeTexture,ShadowSlot=0}).ToBinding()]).IsValid);
    }

    [Fact]
    public void ConsumedEqualityIgnoresInactiveSourceAndExplicitShadowFactor()
    {
        var matcap=T8dTestHelpers.EndfieldMatcap(coupleEndfieldPalette:true);
        var palette=new EndfieldPaletteSpec(Vector3.Zero).ToBinding();
        StyleMaterialBindingData[] Pack(EndfieldBaseImageSpec b)=>[matcap,palette,b.ToBinding()];
        byte[] Cbv(EndfieldBaseImageSpec b)=>T8dTestHelpers.BuildEndfieldDrawData(Pack(b))!.ConstantBufferData.ToArray();

        Assert.False(Cbv(Neutral).AsSpan().SequenceEqual(Cbv(Neutral with {FacingBase=new(.5f)})));
        var b=Neutral with {ShadowStrength=1,DirectionalShadow=new(Vector3.UnitZ)};
        Assert.False(Cbv(b).AsSpan().SequenceEqual(Cbv(b with {DirectionalShadow=new(Vector3.UnitX)})));
        Assert.False(Cbv(b).AsSpan().SequenceEqual(Cbv(b with {DirectionalShadow=new(Vector3.UnitZ,NormalStrength:1)})));
        Assert.True(Cbv(b).AsSpan().SequenceEqual(Cbv(b with {DirectionalShadow=new(Vector3.UnitZ,NormalStrength:2)})));
    }

    private static void Near(Vector3 expected,Vector3 actual)=>Assert.InRange(Vector3.Distance(expected,actual),0,4e-6f);
}
