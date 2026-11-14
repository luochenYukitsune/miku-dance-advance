using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12OutlinePositionCacheTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshSnapshotsReuseUnchangedVerticesAndMatchFullScalarExpansionBitwise(bool modern)
    {
        var cache=new D3D12OutlinePositionCache();var data=Data();var options=OriginalOutlineTests.Options();
        var flags=Flags();
        var first=cache.Get(data,options,modern?flags:null);EqualFull(first,data,options,modern?flags:null);
        Assert.Equal(data.Vertices.Count,cache.LastExpandedVertices);
        var copy=new OriginalOutlineData(data.Vertices,data.Materials);
        Assert.Same(first,cache.Get(copy,options,modern?Flags():null));Assert.Equal(0,cache.LastExpandedVertices);
        var vertices=copy.Vertices.ToArray();vertices[7]=vertices[7] with {Position=vertices[7].Position+new Vector3(.2f,-.1f,.3f)};
        var changed=new OriginalOutlineData(vertices,copy.Materials);
        var next=cache.Get(changed,options,modern?Flags():null);Assert.NotSame(first,next);Assert.Equal(1,cache.LastExpandedVertices);
        EqualFull(next,changed,options,modern?flags:null);
        Assert.Same(next,cache.Get(new OriginalOutlineData(changed.Vertices,changed.Materials),options,modern?Flags():null));
        Assert.Equal(0,cache.LastExpandedVertices);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModelBaseSelectionWidthSizeOrderAndStyleChangesAlwaysMatchOriginalBuild(bool modern)
    {
        var cache=new D3D12OutlinePositionCache();var data=Data();var options=OriginalOutlineTests.Options();var flags=Flags();
        for(int step=0;step<24;step++)
        {
            var vertices=data.Vertices.ToArray();var materials=data.Materials.ToArray();
            switch(step%8)
            {
                case 0: options=options with {CameraPosition=options.CameraPosition+new Vector3(.1f,.2f,-.3f)};break;
                case 1: vertices[5]=vertices[5] with {SelectedMaterialIndex=(vertices[5].SelectedMaterialIndex+1)%materials.Length};break;
                case 2: materials[1]=materials[1] with {Width=materials[1].Width+.125f};break;
                case 3: Array.Reverse(vertices);break;
                case 4: vertices=vertices.Concat([vertices[0]]).ToArray();break;
                case 5: flags[0]=flags[0] with {CharacterOutlineStyle=new(.27f,new(.2f,.3f,.4f,1),Enabled:false)};break;
                case 6: vertices[2]=vertices[2] with {Mode=vertices[2].Mode==OriginalOutlineVertexMode.Expand?OriginalOutlineVertexMode.Inward:OriginalOutlineVertexMode.Expand,Normal=new(.2f,-3,.4f)};break;
                case 7: options=options with {ReferencePoint=options.ReferencePoint+new Vector4(.1f,.2f,-.3f,0),DistanceTransform=Matrix4x4.CreateTranslation(step*.01f,0,0),FovDegrees=step+15,ModelWidth=.4f};break;
            }
            data=new OriginalOutlineData(vertices,materials);
            EqualFull(cache.Get(data,options,modern?flags:null),data,options,modern?flags:null);
        }
    }

    [Fact]
    public void SignedZeroVertexAndWidthBitsCannotReuseAValueComparedOnlyNumerically()
    {
        var cache=new D3D12OutlinePositionCache();float negativeZero=BitConverter.Int32BitsToSingle(int.MinValue);
        var vertex=new OriginalOutlineVertex(new(-0f,0,0),new(negativeZero,0,0),OriginalOutlineVertexMode.Expand,1,0);
        var options=OriginalOutlineTests.Options();var data=new OriginalOutlineData([vertex],[new(0,Vector4.One)]);
        EqualFull(cache.Get(data,options),data,options,null);
        var negativeWidth=new OriginalOutlineData([vertex],[new(negativeZero,Vector4.One)]);
        EqualFull(cache.Get(negativeWidth,options),negativeWidth,options,null);Assert.Equal(1,cache.LastExpandedVertices);
        var changedVertex=new OriginalOutlineData([vertex with {Normal=Vector3.Zero}],negativeWidth.Materials);
        EqualFull(cache.Get(changedVertex,options),changedVertex,options,null);Assert.Equal(1,cache.LastExpandedVertices);
    }

    [Fact]
    public void ColorAndVisibilityChangesReuseGeometryWhileStyleWidthChangesRebuildOnlyItsVertices()
    {
        var cache=new D3D12OutlinePositionCache();var data=Data();var options=OriginalOutlineTests.Options();var flags=Flags();
        var first=cache.Get(data,options,flags);
        flags[0]=flags[0] with {CharacterOutlineStyle=flags[0].CharacterOutlineStyle! with {Color=new(.2f,.3f,.4f,.5f),Enabled=false}};
        Assert.Same(first,cache.Get(data,options,flags));Assert.Equal(0,cache.LastExpandedVertices);
        flags[0]=flags[0] with {CharacterOutlineStyle=flags[0].CharacterOutlineStyle! with {Width=.5f}};
        var changed=cache.Get(data,options,flags);EqualFull(changed,data,options,flags);
        Assert.Equal(data.Vertices.Count(v=>v.SelectedMaterialIndex==0),cache.LastExpandedVertices);
    }

    private static OriginalOutlineData Data()=>new(Enumerable.Range(0,96).Select(i=>new OriginalOutlineVertex(
        new(i*.037f,i%7*.13f,-i*.011f),new(i%3*.3f,-.2f,i%5*.4f),
        i%9==0?OriginalOutlineVertexMode.Inward:OriginalOutlineVertexMode.Expand,.3f+i%5*.1f,i%3)),
        [new(.2f,Vector4.One),new(.4f,Vector4.Zero),new(.1f,new(.1f,.2f,.3f,1))]);
    private static OriginalFrameMaterialFlags[] Flags()=>[new(false,false,false){CharacterOutlineStyle=new(.18f)},new(false,false,false),new(false,false,false){CharacterOutlineStyle=new(.23f)}];
    private static void EqualFull(Vector4[] actual,OriginalOutlineData data,OriginalOutlineOptions options,IReadOnlyList<OriginalFrameMaterialFlags>? flags)
    {
        var expected=flags is null?OriginalOutlineMath.BuildPositions(data,options):CharacterOutlineMath.BuildPositions(data,options,flags);
        Assert.Equal(MemoryMarshal.AsBytes(expected.AsSpan()).ToArray(),MemoryMarshal.AsBytes(actual.AsSpan()).ToArray());
    }
}
