using System.Numerics;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class OriginalOutlineTests
{
    [Theory]
    [InlineData(0,1)] [InlineData(30,19)] [InlineData(60,37)]
    public void ModelBaseUsesTransformedReferenceDistanceAndDegrees(float fov,float factor)
    {
        var o=new OriginalOutlineOptions(new(1,2,3,2),Matrix4x4.CreateTranslation(4,5,6),new(9,12,60),fov,2);
        // Reference transforms to (9,12,15,2), without perspective division.
        float expected=45f*4.5e-5f; expected*=factor; expected*=2;
        Assert.Equal(BitConverter.SingleToInt32Bits(expected),BitConverter.SingleToInt32Bits(OriginalOutlineMath.ComputeModelBase(o)));
        Assert.Equal(expected*2,OriginalOutlineMath.ComputeModelBase(o with {CameraPosition=new(9,12,105)}));
    }

    [Fact]
    public void RawNonUnitAndZeroNormalsAndDisabledInwardPathStayDistinct()
    {
        Vector3 p=new(1,2,3);
        var v=new OriginalOutlineVertex(p,new(0,2,0),OriginalOutlineVertexMode.Expand,.5f,0);
        Assert.Equal(new Vector3(1,2.6f,3),OriginalOutlineMath.ExpandPosition(v,.2f,3));
        Assert.Equal(p,OriginalOutlineMath.ExpandPosition(v with {Normal=Vector3.Zero},.2f,3));
        Assert.Equal(p,OriginalOutlineMath.ExpandPosition(v,.2f,0));
        Assert.Equal(new Vector3(1,1.99f,3),OriginalOutlineMath.ExpandPosition(v with {Mode=OriginalOutlineVertexMode.Inward},.2f,0));
        Assert.NotEqual(OriginalOutlineMath.ExpandPosition(v,.2f,3),OriginalOutlineMath.ExpandPosition(v with {Normal=Vector3.UnitY},.2f,3));
    }

    [Fact]
    public void SharedVerticesSelectStrictMinimumAtLoadTiesKeepFirstAndMorphDoesNotReselect()
    {
        uint[] indices=[0,1,2,0,2,3,0,3,1];
        var selected=OriginalOutlineMath.SelectVertexMaterialsAtLoad(5,indices,[3,3,3],[2,1,1]);
        Assert.Equal(new[]{1,2,1,1,0},selected);
        var vertices=Enumerable.Range(0,5).Select(i=>new OriginalOutlineVertex(Vector3.Zero,Vector3.UnitX,OriginalOutlineVertexMode.Expand,1,selected[i]));
        var data=new OriginalOutlineData(vertices,[new(.2f,Vector4.One),new(4,Vector4.One),new(.1f,Vector4.One)]);
        Assert.Equal(new Vector4(4,0,0,1),OriginalOutlineMath.BuildPositions(data,new(new(0,0,0,1),Matrix4x4.Identity,new(0,0,-1),0,1/4.5e-5f))[0]);
    }

    [Fact]
    public void SelectionRetainsObservedSentinelAndRejectsBrokenRanges()
    {
        Assert.Equal(new[]{0,0,0},OriginalOutlineMath.SelectVertexMaterialsAtLoad(3,[0,1,2],[0,3],[1,1000]));
        Assert.Throws<ArgumentException>(()=>OriginalOutlineMath.SelectVertexMaterialsAtLoad(3,[0,1,2],[0,0],[1,2]));
        Assert.Throws<ArgumentException>(()=>OriginalOutlineMath.SelectVertexMaterialsAtLoad(3,[0,1,9],[3],[1]));
    }

    [Fact]
    public void FrameAndDataAreImmutableAndValidateCorrespondence()
    {
        var surface=D3D12OriginalStyleTests.Fixture(OriginalSphereMode.None,1);
        var vertices=surface.Snapshot.Vertices.ToArray().Select(v=>new OriginalOutlineVertex(v.Position,v.Normal,OriginalOutlineVertexMode.Expand,1,0)).ToArray();
        OriginalOutlineMaterial[] materials=[new(1,new(0,0,0,.3f))];
        var data=new OriginalOutlineData(vertices,materials);
        vertices[0]=vertices[0] with {VertexScale=99}; materials[0]=new(9,Vector4.One);
        Assert.Equal(1,data.Vertices[0].VertexScale); Assert.Equal(.3f,data.Materials[0].Color.W);
        Assert.Null(new OriginalFrameScene(surface).Outline);
        Assert.Throws<ArgumentException>(()=>new OriginalFrameScene(surface,data));
        Assert.Throws<ArgumentException>(()=>new OriginalFrameScene(surface,new OriginalOutlineData([data.Vertices[0]],data.Materials),Options()));
        Assert.Throws<ArgumentException>(()=>new OriginalOutlineData([data.Vertices[0] with {SelectedMaterialIndex=2}],data.Materials));
    }

    public static OriginalOutlineOptions Options(float width=1)=>new(new(0,0,0,1),Matrix4x4.Identity,new(0,0,-5),30,width);
}
