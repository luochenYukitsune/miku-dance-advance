using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12VisibilityBoundsSurfaceReaderTests
{
    private const int Chunk=4096;
    private const int Count=3*Chunk+7;
    private const int DenseCount=3*D3D12VisibilityBoundsCache.IndexLeafSize;
    private const int SparseCount=6*D3D12VisibilityBoundsCache.IndexLeafSize+3;
    private static readonly float NegativeZero=BitConverter.Int32BitsToSingle(int.MinValue);

    [Theory]
    [InlineData(0L,false,false)]
    [InlineData(0L,true,false)]
    [InlineData(D3D12VisibilityBoundsCache.MaximumLeafCacheBytes,false,false)]
    [InlineData(D3D12VisibilityBoundsCache.MaximumLeafCacheBytes,true,false)]
    [InlineData(0L,true,true)]
    [InlineData(D3D12VisibilityBoundsCache.MaximumLeafCacheBytes,true,true)]
    public void SharedVertexStillInvalidatesSparseMaterialAfterOtherMaterialSaturates(long budget,bool outline,bool owned)
    {
        var source=SharedScene();var cache=new D3D12VisibilityBoundsCache(1,budget);
        var rootPositions=Positions(source);var rootStream=Stream(source,rootPositions,owned);
        var held=Get(cache,source,rootStream,outline);var saved=Bytes(held);
        EqualOracle(source,rootPositions,outline,held);
        var vertices=Vertices(source);
        for(int index=0;index<4;index++)vertices[index]=vertices[index] with {Position=new(-10-index,2,3)};
        // The early material is already saturated before this shared vertex is reached.
        // Its second material must still receive the changed-position invalidation.
        vertices[Chunk]=vertices[Chunk] with {Position=new(-100,NegativeZero,4)};
        var changed=source.WithDeformedVertices(vertices);var changedPositions=Positions(changed);
        var changedStream=Stream(changed,changedPositions,owned);
        var bounds=Get(cache,changed,changedStream,outline);EqualOracle(changed,changedPositions,outline,bounds);
        Assert.Equal(2,cache.LastRebuiltMaterials);
        Assert.Equal(Count,cache.LastComparedVertices);
        Assert.Equal(budget==0?2:4+1+SparseCount-1,cache.LastPositionComparisons);
        if(budget!=0)
        {
            Assert.Equal(1,cache.LastFullScanMaterials);Assert.Equal(1,cache.LastRebuiltLeaves);
            Assert.Equal(DenseCount+D3D12VisibilityBoundsCache.IndexLeafSize,cache.LastScannedIndices);
        }

        // Advance through a fresh stream whose used positions did not change. Both the
        // unused position and surface-only shading bytes must leave bounds untouched.
        vertices=Vertices(changed);
        vertices[0]=vertices[0] with {Normal=Vector3.UnitX,Uv=Vector2.One};
        vertices[^1]=vertices[^1] with {Position=new(-500)};
        var shading=changed.WithDeformedVertices(vertices);var shadingPositions=Positions(shading);
        var shadingStream=Stream(shading,shadingPositions,owned);
        Assert.Same(bounds,Get(cache,shading,shadingStream,outline));Assert.Equal(0,cache.LastRebuiltMaterials);
        OriginalVisibilityBounds[]? current;
        bool isCurrent=shadingStream is D3D12ExpandedPositions ownedStream
            ?cache.TryGetCurrentExpanded(shading,ownedStream,out current)
            :cache.TryGetCurrent(shading,outline?shadingPositions:null,out current);
        Assert.True(isCurrent);Assert.Same(bounds,current);
        Assert.Equal(0,cache.LastComparedVertices);Assert.Equal(0,cache.LastPositionComparisons);

        // Dense reduction left the early material's leaf cache stale. After the
        // no-bounds-change baseline advance, its first sparse update must rebuild it.
        var sparse=Compose(shading,700,vertex=>vertex with {Position=new(-200,0,1)});
        var sparsePositions=Positions(sparse);var sparseStream=Stream(sparse,sparsePositions,owned);
        EqualOracle(sparse,sparsePositions,outline,Get(cache,sparse,sparseStream,outline));
        Assert.Equal(1,cache.LastRebuiltMaterials);
        if(budget!=0)
        {Assert.Equal(0,cache.LastFullScanMaterials);Assert.Equal(3,cache.LastRebuiltLeaves);Assert.Equal(DenseCount,cache.LastScannedIndices);}
        foreach(var scene in new[]{source,changed,shading,sparse,source})
        {
            var positions=Positions(scene);var stream=Stream(scene,positions,owned);
            EqualOracle(scene,positions,outline,Get(cache,scene,stream,outline));
            Assert.False(IsMaterialized(scene));
        }
        if(owned)
            foreach(var stream in new[]{rootStream,changedStream,shadingStream,sparseStream})
                Assert.False(((D3D12ExpandedPositions)stream).IsMaterialized);
        Assert.Equal(saved,Bytes(held));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(D3D12VisibilityBoundsCache.MaximumLeafCacheBytes)]
    public void SurfaceChunkBoundariesShortTailSignedZeroAndSkippedBranchesKeepCoreOracle(long budget)
    {
        var source=BoundaryScene();var cache=new D3D12VisibilityBoundsCache(1,budget);
        var held=cache.GetSurface(source);var saved=Bytes(held);EqualSurface(source,held);
        var shading=Compose(source,Chunk,vertex=>vertex with {Normal=Vector3.UnitX,Uv=Vector2.One});
        Assert.Same(held,cache.GetSurface(shading));Assert.Equal(0,cache.LastRebuiltMaterials);
        var unused=Compose(shading,1000,vertex=>vertex with {Position=new(-50)});
        Assert.Same(held,cache.GetSurface(unused));Assert.Equal(0,cache.LastRebuiltMaterials);
        Assert.True(cache.TryGetCurrent(unused,null,out var unchanged));Assert.Same(held,unchanged);
        var first=Compose(unused,Chunk-1,vertex=>vertex with {Position=new(NegativeZero,0,NegativeZero)});
        var skipped=Compose(first,Chunk,vertex=>vertex with {Position=new(0,NegativeZero,0)});
        var latest=Compose(skipped,2*Chunk,vertex=>vertex with {Position=new(float.Epsilon,-float.Epsilon,float.MaxValue)});
        latest=Compose(latest,Count-1,vertex=>vertex with {Position=new(-float.MaxValue,NegativeZero,float.Epsilon)});
        var branch=Compose(source,0,vertex=>vertex with {Position=new(NegativeZero,NegativeZero,NegativeZero)});
        foreach(var scene in new[]{latest,source,first,branch,skipped,latest,unused})
        {
            EqualSurface(scene,cache.GetSurface(scene));Assert.False(cache.LastTopologyRebuilt);
            Assert.False(IsMaterialized(scene));
        }
        Assert.Equal(saved,Bytes(held));
    }

    private static SceneSnapshot SharedScene()
    {
        var indices=Enumerable.Range(0,DenseCount).Select(index=>(uint)index).ToList();indices[^1]=Chunk;
        var sparse=Enumerable.Range(0,SparseCount).Select(index=>(uint)(2*Chunk+index)).ToArray();
        sparse[2*D3D12VisibilityBoundsCache.IndexLeafSize]=Chunk;indices.AddRange(sparse);
        return new(Enumerable.Range(0,Count).Select(index=>new SceneVertex(new(index*.125f,0,.5f),Vector3.UnitY,Vector2.Zero)).ToArray(),
            indices.ToArray(),[Material("dense",0,DenseCount),Material("sparse",DenseCount,SparseCount),Material("empty",DenseCount+SparseCount,0)]);
    }
    private static SceneSnapshot BoundaryScene()=>new(
        Enumerable.Range(0,Count).Select(_=>new SceneVertex(Vector3.Zero,Vector3.UnitY,Vector2.Zero)).ToArray(),
        [Chunk-1,Chunk,2*Chunk,Count-1,0,Chunk-1],
        [Material("first",0,3),Material("second",3,3),Material("empty",6,0)]);
    private static SceneMaterial Material(string name,int start,int count)=>new(name,start,count,Vector4.One,Vector3.Zero,Vector3.Zero,1);
    private static SceneVertex[] Vertices(SceneSnapshot scene)
    {var vertices=new SceneVertex[scene.VertexCount];scene.CopyVertices(0,vertices);return vertices;}
    private static Vector4[] Positions(SceneSnapshot scene)=>Vertices(scene).Select(vertex=>new Vector4(vertex.Position,1)).ToArray();
    private static object Stream(SceneSnapshot scene,Vector4[] positions,bool owned)
    {
        if(!owned)return positions;
        var chunks=new Vector4[(positions.Length+Chunk-1)/Chunk][];
        for(int index=0;index<chunks.Length;index++)
            chunks[index]=positions.AsSpan(index*Chunk,Math.Min(Chunk,positions.Length-index*Chunk)).ToArray();
        // Each independently supplied payload needs an independent conservative proof.
        // Reusing the scene's lineage here could hide changes to the owned output fixture.
        var source=new SceneSnapshot(Vertices(scene),scene.Indices,scene.Materials);
        return new D3D12ExpandedPositions(positions.Length,chunks,
            D3D12ExpandedPositionRevision.Create(source.VertexRevision,0,ReadOnlySpan<int>.Empty));
    }
    private static OriginalVisibilityBounds[] Get(D3D12VisibilityBoundsCache cache,SceneSnapshot scene,object positions,bool outline)
    {
        if(!outline)return cache.GetSurface(scene);
        if(positions is not D3D12ExpandedPositions owned)return cache.GetOutline(scene,(Vector4[])positions);
        var bounds=cache.GetOutline(scene,owned);Assert.False(owned.IsMaterialized);return bounds;
    }
    private static byte[] Bytes(OriginalVisibilityBounds[] bounds)=>MemoryMarshal.AsBytes(bounds.AsSpan()).ToArray();
    private static void EqualOracle(SceneSnapshot scene,Vector4[] positions,bool outline,OriginalVisibilityBounds[] bounds)
        =>Assert.Equal(Bytes(outline?OriginalFrustumVisibility.PositionMaterialBounds(scene,positions):OriginalFrustumVisibility.SurfaceMaterialBounds(scene)),Bytes(bounds));
    private static void EqualSurface(SceneSnapshot scene,OriginalVisibilityBounds[] bounds)
        =>Assert.Equal(Bytes(OriginalFrustumVisibility.SurfaceMaterialBounds(scene)),Bytes(bounds));
    private static bool IsMaterialized(SceneSnapshot scene)
    {
        var storage=typeof(SceneSnapshot).GetField("vertexStorage",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(scene)!;
        return (bool)storage.GetType().GetProperty("IsMaterialized",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.GetValue(storage)!;
    }
    private sealed class Writer(Func<SceneVertex,SceneVertex> edit)
    {public void Write(int _,Span<SceneVertex> destination){for(int index=0;index<destination.Length;index++)destination[index]=edit(destination[index]);}}
    private static SceneSnapshot Compose(SceneSnapshot scene,int index,Func<SceneVertex,SceneVertex> edit)
    {
        var method=typeof(SceneSnapshot).GetMethod("WithComposedVertices",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var writer=Delegate.CreateDelegate(method.GetParameters()[1].ParameterType,new Writer(edit),typeof(Writer).GetMethod(nameof(Writer.Write))!);
        try{return (SceneSnapshot)method.Invoke(scene,[new[]{(index,1)},writer])!;}
        catch(TargetInvocationException error) when(error.InnerException is not null)
        {ExceptionDispatchInfo.Capture(error.InnerException).Throw();throw;}
    }
}
