using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12VisibilityBoundsCacheTests
{
    [Fact]
    public void SurfaceOnlyRebuildsMaterialsReferencingChangedPositionsAndKeepsOldResultsImmutable()
    {
        var cache=new D3D12VisibilityBoundsCache(2);var scene=Scene();
        var first=cache.GetSurface(scene);EqualSurface(scene,first);
        Assert.True(cache.LastTopologyRebuilt);Assert.Equal(4,cache.LastRebuiltMaterials);
        var old=Bytes(first);
        var vertices=scene.Vertices.ToArray();vertices[1]=vertices[1] with {Position=new(-4,5,.25f)};
        var changed=scene.WithDeformedVertices(vertices);var next=cache.GetSurface(changed);
        EqualSurface(changed,next);Assert.Equal(1,cache.LastRebuiltMaterials);
        Assert.Equal(vertices.Length,cache.LastComparedVertices);Assert.False(cache.LastTopologyRebuilt);
        Assert.Equal(old,Bytes(first));Assert.NotSame(first,next);
        // Vertex 0 belongs to both nonempty materials 0 and 2.
        vertices[0]=vertices[0] with {Position=new(10,-6,.75f)};
        changed=scene.WithDeformedVertices(vertices);EqualSurface(changed,cache.GetSurface(changed));
        Assert.Equal(2,cache.LastRebuiltMaterials);
    }

    [Fact]
    public void SurfaceIgnoresNormalUvMaterialColourAndUnusedPositionChanges()
    {
        var cache=new D3D12VisibilityBoundsCache(2);var scene=Scene();var first=cache.GetSurface(scene);
        var vertices=scene.Vertices.ToArray();
        vertices[0]=vertices[0] with {Normal=new(.2f,-.3f,7),Uv=new(.25f,.75f)};
        vertices[^1]=vertices[^1] with {Position=new(123,456,789)};
        var changed=scene.WithDeformedVertices(vertices).WithMaterials(scene.Materials.Select(m=>m with {Diffuse=new(.3f,.4f,.5f,.6f)}));
        Assert.Same(first,cache.GetSurface(changed));Assert.Equal(0,cache.LastRebuiltMaterials);
        EqualSurface(changed,first);
        Assert.Same(first,cache.GetSurface(changed.WithLighting(Vector3.UnitX)));
        Assert.Equal(0,cache.LastComparedVertices);
        Assert.True(float.IsPositiveInfinity(first[1].Minimum.X));
        Assert.True(float.IsNegativeInfinity(first[1].Maximum.X));
    }

    [Fact]
    public void SurfaceDetectsSignedZeroAndSameSizePositionReplacementBitwise()
    {
        var cache=new D3D12VisibilityBoundsCache(1);var scene=Scene();cache.GetSurface(scene);
        var vertices=scene.Vertices.ToArray();
        vertices[0]=vertices[0] with {Position=new(BitConverter.Int32BitsToSingle(int.MinValue),0,.5f)};
        var changed=scene.WithDeformedVertices(vertices);EqualSurface(changed,cache.GetSurface(changed));
        Assert.Equal(2,cache.LastRebuiltMaterials);
        vertices[0]=vertices[0] with {Position=scene.Vertices[0].Position};
        changed=scene.WithDeformedVertices(vertices);EqualSurface(changed,cache.GetSurface(changed));
        Assert.Equal(2,cache.LastRebuiltMaterials);
    }

    [Fact]
    public void EqualIndexValuesReuseTopologyButDifferentIndicesVertexCountsAndRangesRebuild()
    {
        var cache=new D3D12VisibilityBoundsCache(1);var scene=Scene();cache.GetSurface(scene);
        var replacement=new SceneSnapshot(scene.Vertices,scene.Indices,scene.Materials);
        EqualSurface(replacement,cache.GetSurface(replacement));Assert.False(cache.LastTopologyRebuilt);
        uint[] reordered=[3,2,0,0,1,2,0,2,3];
        var topology=new SceneSnapshot(scene.Vertices,reordered,scene.Materials);
        EqualSurface(topology,cache.GetSurface(topology));Assert.True(cache.LastTopologyRebuilt);
        var ranges=topology.WithMaterials([Material("a",0,6),Material("empty",6,0),Material("b",6,0),Material("c",6,3)]);
        EqualSurface(ranges,cache.GetSurface(ranges));Assert.True(cache.LastTopologyRebuilt);
        var longer=new SceneSnapshot([..scene.Vertices.ToArray(),new(Vector3.One,Vector3.UnitX,Vector2.Zero)],scene.Indices,scene.Materials);
        EqualSurface(longer,cache.GetSurface(longer));Assert.True(cache.LastTopologyRebuilt);
    }

    [Fact]
    public void SurfaceCacheRetainsAtMostTwoCurrentTopologyEntries()
    {
        var cache=new D3D12VisibilityBoundsCache(2);var one=Scene();
        var twoIndices=one.Indices.ToArray();(twoIndices[0],twoIndices[1])=(twoIndices[1],twoIndices[0]);
        var threeIndices=one.Indices.ToArray();(threeIndices[1],threeIndices[2])=(threeIndices[2],threeIndices[1]);
        var two=new SceneSnapshot(one.Vertices,twoIndices,one.Materials);
        var three=new SceneSnapshot(one.Vertices,threeIndices,one.Materials);
        var first=cache.GetSurface(one);var second=cache.GetSurface(two);
        Assert.Same(first,cache.GetSurface(one));Assert.Same(second,cache.GetSurface(two));
        cache.GetSurface(three);Assert.Equal(2,cache.EntryCount);
        EqualSurface(one,cache.GetSurface(one));Assert.True(cache.LastTopologyRebuilt);
        for(int i=0;i<12;i++)
        {
            var vertices=one.Vertices.ToArray();vertices[0]=vertices[0] with {Position=new(i,0,.5f)};
            one=one.WithDeformedVertices(vertices);EqualSurface(one,cache.GetSurface(one));
            Assert.Equal(2,cache.EntryCount);
        }
    }

    [Fact]
    public void OutlineUsesActualFourComponentBitsAndRebuildsSharedMaterialsOnly()
    {
        var cache=new D3D12VisibilityBoundsCache(1);var scene=Scene();var positions=Positions(scene);
        var first=cache.GetOutline(scene,positions);EqualOutline(scene,positions,first);var saved=Bytes(first);
        Assert.Same(first,cache.GetOutline(scene,positions.ToArray()));Assert.Equal(0,cache.LastRebuiltMaterials);
        var changed=positions.ToArray();changed[1]=new(-7,8,.5f,1);
        EqualOutline(scene,changed,cache.GetOutline(scene,changed));Assert.Equal(1,cache.LastRebuiltMaterials);
        Assert.Equal(saved,Bytes(first));
        changed=changed.ToArray();changed[0]=new(BitConverter.Int32BitsToSingle(int.MinValue),0,.5f,1);
        EqualOutline(scene,changed,cache.GetOutline(scene,changed));Assert.Equal(2,cache.LastRebuiltMaterials);
        changed=changed.ToArray();changed[0]=new(0,0,.5f,2);
        var invalid=cache.GetOutline(scene,changed);EqualOutline(scene,changed,invalid);Assert.Equal(2,cache.LastRebuiltMaterials);
        Assert.True(float.IsNaN(invalid[0].Minimum.X));Assert.True(float.IsNaN(invalid[2].Maximum.X));
        Assert.True(OriginalFrustumVisibility.IsPotentiallyVisible(invalid[0],Matrix4x4.CreateTranslation(100,0,0)));
        changed=changed.ToArray();changed[0]=positions[0];
        EqualOutline(scene,changed,cache.GetOutline(scene,changed));Assert.Equal(2,cache.LastRebuiltMaterials);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void OutlineNonfiniteComponentsAndInvalidWMatchUnknownOracle(int component)
    {
        var cache=new D3D12VisibilityBoundsCache(1);var scene=Scene();var positions=Positions(scene);cache.GetOutline(scene,positions);
        foreach(float value in new[]{float.NaN,float.NegativeInfinity,float.PositiveInfinity})
        {
            positions=positions.ToArray();var p=positions[0];
            positions[0]=component switch {0=>p with {X=value},1=>p with {Y=value},2=>p with {Z=value},_=>p with {W=value}};
            EqualOutline(scene,positions,cache.GetOutline(scene,positions));Assert.Equal(2,cache.LastRebuiltMaterials);
        }
        positions=Positions(scene);positions[^1]=new(float.NaN);var bounds=cache.GetOutline(scene,positions);
        EqualOutline(scene,positions,bounds);Assert.False(float.IsNaN(bounds[0].Minimum.X));
        Assert.Throws<ArgumentException>(()=>cache.GetOutline(scene,positions[..^1]));
    }

    [Fact]
    public void CameraWorldChangesReuseLocalBoundsAndStillTransformEachVisibilityTest()
    {
        var cache=new D3D12VisibilityBoundsCache(1);var scene=Scene();var first=cache.GetSurface(scene);
        var moved=scene.WithWorld(Matrix4x4.CreateTranslation(100,0,0)).WithCamera(scene.Camera with {Eye=scene.Camera.Eye+Vector3.UnitX});
        Assert.Same(first,cache.GetSurface(moved));Assert.Equal(0,cache.LastRebuiltMaterials);
        Assert.True(OriginalFrustumVisibility.IsPotentiallyVisible(first[0],scene.World,Matrix4x4.Identity));
        Assert.False(OriginalFrustumVisibility.IsPotentiallyVisible(first[0],moved.World,Matrix4x4.Identity));
        var positions=Positions(scene);var outlineCache=new D3D12VisibilityBoundsCache(1);var outline=outlineCache.GetOutline(scene,positions);
        Assert.Same(outline,outlineCache.GetOutline(moved,positions));
        Assert.False(OriginalFrustumVisibility.IsPotentiallyVisible(outline[0],moved.World));
    }

    [Fact]
    public void IndependentOriginalAndModernOutlineCachesKeepOneEntryAndPreviousBounds()
    {
        var original=new D3D12VisibilityBoundsCache(1);var modern=new D3D12VisibilityBoundsCache(1);
        var scene=Scene();var first=Positions(scene);var second=first.Select(p=>p+new Vector4(.25f,0,0,0)).ToArray();
        var old=original.GetOutline(scene,first);var newer=modern.GetOutline(scene,second);
        Assert.Same(old,original.GetOutline(scene,first));Assert.Same(newer,modern.GetOutline(scene,second));
        EqualOutline(scene,first,old);EqualOutline(scene,second,newer);
        Assert.Equal(1,original.EntryCount);Assert.Equal(1,modern.EntryCount);
    }

    [Fact]
    public void OutlineTopologyAndRangeChangesInvalidateEvenWithTheSamePositionArray()
    {
        var cache=new D3D12VisibilityBoundsCache(1);var scene=Scene();var positions=Positions(scene);
        cache.GetOutline(scene,positions);
        var ranged=scene.WithMaterials([Material("a",0,6),Material("empty",6,0),Material("b",6,0),Material("c",6,3)]);
        EqualOutline(ranged,positions,cache.GetOutline(ranged,positions));Assert.True(cache.LastTopologyRebuilt);
        var replaced=new SceneSnapshot(ranged.Vertices,[3,3,3,3,2,0,2,1,0],ranged.Materials);
        EqualOutline(replaced,positions,cache.GetOutline(replaced,positions));Assert.True(cache.LastTopologyRebuilt);
        Assert.Equal(1,cache.EntryCount);
    }

    [Fact]
    public void SuccessivePartialUpdatesMatchIndependentFullBoundsByteForByte()
    {
        var surface=new D3D12VisibilityBoundsCache(2);var outline=new D3D12VisibilityBoundsCache(1);
        var scene=Scene();var positions=Positions(scene);var random=new Random(83021);
        for(int step=0;step<96;step++)
        {
            var vertices=scene.Vertices.ToArray();int changed=random.Next(vertices.Length);
            vertices[changed]=vertices[changed] with {Position=new(random.Next(-100,100)*.125f,random.Next(-100,100)*.0625f,random.Next(-10,10)*.25f)};
            scene=scene.WithDeformedVertices(vertices);positions=positions.ToArray();
            positions[changed]=new(vertices[changed].Position,1);
            if(step%7==0)positions[changed]=positions[changed] with {W=0};
            if(step%11==0)positions[changed]=positions[changed] with {X=BitConverter.Int32BitsToSingle(unchecked((int)0x7fc00001))};
            EqualSurface(scene,surface.GetSurface(scene));EqualOutline(scene,positions,outline.GetOutline(scene,positions));
            Assert.True(surface.TryGetCurrent(scene,null,out var sameSurface));
            EqualSurface(scene,sameSurface!);Assert.True(outline.TryGetCurrent(scene,positions,out var sameOutline));
            EqualOutline(scene,positions,sameOutline!);
        }
    }

    private static SceneSnapshot Scene()=>new(
        [new(new(0,0,.5f),Vector3.UnitX,Vector2.Zero),new(new(1,0,.5f),Vector3.UnitX,Vector2.Zero),
         new(new(0,1,.5f),Vector3.UnitX,Vector2.Zero),new(new(2,2,.5f),Vector3.UnitX,Vector2.Zero),
         new(new(20,20,.5f),Vector3.UnitX,Vector2.Zero)],
        [0,1,2,0,2,3,3,3,3],
        [Material("a",0,3),Material("empty",3,0),Material("b",3,3),Material("c",6,3)]);
    private static SceneMaterial Material(string name,int start,int count)=>new(name,start,count,Vector4.One,Vector3.Zero,Vector3.Zero,1);
    private static Vector4[] Positions(SceneSnapshot scene)=>scene.Vertices.ToArray().Select(v=>new Vector4(v.Position,1)).ToArray();
    private static byte[] Bytes(OriginalVisibilityBounds[] values)=>MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    private static void EqualSurface(SceneSnapshot scene,OriginalVisibilityBounds[] actual)=>Assert.Equal(Bytes(OriginalFrustumVisibility.SurfaceMaterialBounds(scene)),Bytes(actual));
    private static void EqualOutline(SceneSnapshot scene,Vector4[] positions,OriginalVisibilityBounds[] actual)=>Assert.Equal(Bytes(OriginalFrustumVisibility.PositionMaterialBounds(scene,positions)),Bytes(actual));
}
