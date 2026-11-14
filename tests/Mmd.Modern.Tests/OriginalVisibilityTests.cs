using System.Numerics;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class OriginalVisibilityTests
{
    [Theory]
    [InlineData(-3,0,.5f)] [InlineData(3,0,.5f)]
    [InlineData(0,-3,.5f)] [InlineData(0,3,.5f)]
    [InlineData(0,0,-2)] [InlineData(0,0,3)]
    public void StrictlyOutsideEachHomogeneousClipPlaneIsCulled(float x,float y,float z)
    {
        var center=new Vector3(x,y,z);
        Assert.False(OriginalFrustumVisibility.IsPotentiallyVisible(new(center-new Vector3(.1f),center+new Vector3(.1f)),Matrix4x4.Identity));
    }

    [Fact]
    public void CrossingPlanesAndBoxesWithNoCornerInsideRemainVisible()
    {
        OriginalVisibilityBounds[] bounds=[new(new(-2,-.2f,.4f),new(0,.2f,.6f)),new(new(-2,-2,.4f),new(2,2,.6f)),
            new(new(-.1f,-.1f,-.1f),new(.1f,.1f,.1f)),new(new(-.1f,-.1f,.9f),new(.1f,.1f,1.1f))];
        foreach(var bound in bounds) Assert.True(OriginalFrustumVisibility.IsPotentiallyVisible(bound,Matrix4x4.Identity));
    }

    [Fact]
    public void SurfaceAppliesNonidentityWorldBeforeViewProjectionAndOutlineUsesCombinedMatrix()
    {
        var bounds=new OriginalVisibilityBounds(new(9,-.2f,.2f),new(10,.2f,.8f));
        var world=Matrix4x4.CreateScale(2,1,1)*Matrix4x4.CreateTranslation(-19,0,0);
        Assert.False(OriginalFrustumVisibility.IsPotentiallyVisible(bounds,Matrix4x4.Identity));
        Assert.True(OriginalFrustumVisibility.IsPotentiallyVisible(bounds,world,Matrix4x4.Identity));
        Assert.True(OriginalFrustumVisibility.IsPotentiallyVisible(bounds,world*Matrix4x4.Identity));
        Assert.False(OriginalFrustumVisibility.IsPotentiallyVisible(bounds,world,Matrix4x4.CreateTranslation(5,0,0)));
    }

    [Fact]
    public void ClipBoundaryRoundoffNonfiniteAndUncertainInputsAreRetained()
    {
        Assert.True(OriginalFrustumVisibility.IsPotentiallyVisible(new(new(1.0000001f,0,.5f),new(1.0000001f,0,.5f)),Matrix4x4.Identity));
        Assert.True(OriginalFrustumVisibility.IsPotentiallyVisible(new(new(float.NaN,0,0),Vector3.One),Matrix4x4.Identity));
        Assert.True(OriginalFrustumVisibility.IsPotentiallyVisible(new(Vector3.One,Vector3.Zero),Matrix4x4.Identity));
        var invalid=Matrix4x4.Identity;invalid.M11=float.PositiveInfinity;
        Assert.True(OriginalFrustumVisibility.IsPotentiallyVisible(new(Vector3.Zero,Vector3.One),invalid));
        // Large cancellation: a small exact-math negative plane cannot prove shader binary32 is outside.
        var cancellation=Matrix4x4.Identity;cancellation.M11=1e10f;cancellation.M41=-1e10f-1024;
        Assert.True(OriginalFrustumVisibility.IsPotentiallyVisible(new(new(1,0,.5f),new(1,0,.5f)),cancellation));
    }

    [Fact]
    public void PerMaterialBoundsUseOnlyReferencedGeometryAndActualExpandedOrInwardPositions()
    {
        SceneVertex[] vertices=[new(new(0,0,.5f),Vector3.UnitX,Vector2.Zero),new(new(1,0,.5f),Vector3.UnitX,Vector2.Zero),
            new(new(0,1,.5f),Vector3.UnitX,Vector2.Zero),new(new(100,100,100),Vector3.UnitX,Vector2.Zero)];
        var scene=new SceneSnapshot(vertices,[0,1,2],[new("one",0,3,Vector4.One,Vector3.Zero,Vector3.Zero,1)]);
        Assert.Equal(new OriginalVisibilityBounds(new(0,0,.5f),new(1,1,.5f)),OriginalFrustumVisibility.SurfaceMaterialBounds(scene)[0]);
        var data=new OriginalOutlineData(vertices.Select((v,i)=>new OriginalOutlineVertex(v.Position,new(-100,0,0),
            i==0?OriginalOutlineVertexMode.Inward:OriginalOutlineVertexMode.Expand,1,0)),[new(1,Vector4.One)]);
        var actual=OriginalOutlineMath.BuildPositions(data,OriginalOutlineTests.Options(100));
        var edge=OriginalFrustumVisibility.PositionMaterialBounds(scene,actual)[0];
        Assert.Equal(MathF.Min(actual[0].X,MathF.Min(actual[1].X,actual[2].X)),edge.Minimum.X);
        Assert.Equal(MathF.Max(actual[0].X,MathF.Max(actual[1].X,actual[2].X)),edge.Maximum.X);
        Assert.Equal(.5f,edge.Minimum.Z);
        Assert.True(edge.Minimum.X<0);
        actual[0]=new(float.NaN,0,0,1);
        Assert.True(OriginalFrustumVisibility.IsPotentiallyVisible(OriginalFrustumVisibility.PositionMaterialBounds(scene,actual)[0],Matrix4x4.Identity));
    }
}
