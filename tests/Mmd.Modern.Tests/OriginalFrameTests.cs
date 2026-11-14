using System.Numerics;
using Mmd.Modern.Core;
using Xunit;
namespace Mmd.Modern.Tests;

public sealed class OriginalFrameTests
{
    [Fact] public void ProjectionDividesWFlipsYAndPreservesOutsideBranch()
    {
        var p=OriginalFrameMath.Project(new(1,-1,1.5f,2));
        Assert.True(p.Inside); Assert.Equal(new Vector2(.75f,.75f),p.Uv); Assert.Equal(.75f,p.Depth);
        Assert.False(OriginalFrameMath.Project(new(3,0,0,2)).Inside);
    }
    [Fact] public void ShadowNormalAndPartialFarUseDocumentedScaleAndOffset()
    {
        Assert.Equal(1,OriginalFrameMath.Visibility(.5f,.5f,.25f,false));
        Assert.InRange(OriginalFrameMath.Visibility(.5005f,.5f,.25f,false),.5499f,.5501f);
        Assert.InRange(OriginalFrameMath.Visibility(.5005f,.5f,.25f,true),.2998f,.3002f);
        Assert.Equal(1,OriginalFrameMath.Visibility(.9f,1,.9f,true));
    }
    [Fact] public void OutsideProjectionBypassesToonAndTranspButWhiteDepthStillUsesToon()
    {
        Vector4 Eval(bool inside,bool transp)=>OriginalFrameMath.EvaluateSurface(new(.8f,.4f,.2f,.2f),Vector4.Zero,new(.1f),Vector3.UnitY,
            Vector3.Zero,new(0,0,-5),Vector3.UnitX,Vector4.One,Vector4.One,OriginalSphereMode.None,false,new(inside,new(.5f),.7f),1,false,transp);
        Assert.Equal(new Vector4(.8f,.4f,.2f,.2f),Eval(false,true));
        Assert.True(Vector4.Distance(new(.08f,.04f,.02f,.5f),Eval(true,true))<1e-7f);
    }
    [Fact] public void GroundProjectsAfterWorldAtFixedHeightAndBlendUsesMaxAlpha()
    {
        Assert.Equal(new Vector3(2,.1f,4),OriginalFrameMath.ProjectGround(new(4,2,8),new(1,1,2)));
        Assert.True(Vector4.Distance(new(.35f,.3f,.45f,.8f),OriginalFrameMath.Blend(new(.8f,.4f,.6f,.25f),new(.2f,.26666668f,.4f,.8f),OriginalAlphaBlendMode.Normal))<1e-7f);
        Assert.True(Vector4.Distance(new(.4f,.36666667f,.55f,.8f),OriginalFrameMath.Blend(new(.8f,.4f,.6f,.25f),new(.2f,.26666668f,.4f,.8f),OriginalAlphaBlendMode.Additive))<1e-7f);
        Assert.Equal(new Vector3(2,3.1f,4),OriginalFrameMath.ProjectGround(new(4,2,8),new(1,1,2),Matrix4x4.CreateTranslation(0,3,0)));
    }
    [Fact] public void OptionsRejectUndefinedAndNonfiniteInputs()
    {
        Assert.Throws<ArgumentException>(()=>OriginalFrameMath.Validate(new((OriginalAlphaBlendMode)99,true,false)));
        Assert.Throws<ArgumentException>(()=>OriginalFrameMath.Validate(new(OriginalAlphaBlendMode.Normal,true,false,GroundShadow:new(Vector3.UnitX,Vector4.One,true,true))));
        Assert.Throws<ArgumentException>(()=>OriginalFrameMath.Validate(new(OriginalAlphaBlendMode.Normal,true,false,new(Matrix4x4.Identity,0,32,true,false,OriginalTextureSampling.LinearClamp,OriginalDepthComparison.LessEqual))));
    }
    [Fact] public void WhiteDisabledResourceStillComparesProjectedDepthAboveOne()
    {
        Assert.Equal(0,OriginalFrameMath.Visibility(1.01f,1,.5f,false));
    }
    [Fact] public void PartialFarPreservesBinary32MultiplyGrouping()
    {
        float delta=BitConverter.Int32BitsToSingle(0x3981d420),uvY=BitConverter.Int32BitsToSingle(0x3f251d3c);
        Assert.Equal(0x3cb68960,BitConverter.SingleToInt32Bits(OriginalFrameMath.Visibility(delta,0,uvY,true)));
    }
}
