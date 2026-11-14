using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12BoundsInvalidationBlockTests
{
    private const int Count=2*4096+7,Dense=768,Sparse=1539,Shared=640;
    private static readonly float NegativeZero=BitConverter.Int32BitsToSingle(int.MinValue);

    [Theory]
    [InlineData(0L,0)]
    [InlineData(0L,1)]
    [InlineData(0L,2)]
    [InlineData(D3D12VisibilityBoundsCache.MaximumLeafCacheBytes,0)]
    [InlineData(D3D12VisibilityBoundsCache.MaximumLeafCacheBytes,1)]
    [InlineData(D3D12VisibilityBoundsCache.MaximumLeafCacheBytes,2)]
    public void SaturatedBlocksPreserveSharedSparseChannelsAndEveryDiagnosticCounter(long leafBudget,int stream)
    {
        var source=Scene();var enabled=new D3D12VisibilityBoundsCache(1,leafBudget,16);
        var disabled=new D3D12VisibilityBoundsCache(1,leafBudget,16,0);
        var held=Read(enabled,source,stream);var saved=Bytes(held);
        EqualOracle(source,stream,held);Assert.Equal(saved,Bytes(Read(disabled,source,stream)));
        var first=Vertices(source);
        // Material zero saturates before block256. The later block512 still contains
        // material one's shared vertex640, so a block proof must include both owners.
        for(int i=0;i<4;i++)first[i]=first[i] with {Position=new(-10-i,0,1)};
        first[Shared]=first[Shared] with {Position=new(-100,NegativeZero,2)};
        var dense=source.WithDeformedVertices(first);
        Compare(dense);Assert.Equal(2,enabled.LastRebuiltMaterials);

        var later=Vertices(dense);later[700]=later[700] with {Position=new(-200,0,1)};
        var sparse=dense.WithDeformedVertices(later);
        Compare(sparse);Assert.Equal(1,enabled.LastRebuiltMaterials);
        // Every new invalidation must consult current Dirty flags; never reuse the
        // previous frame's saturated-block answer across sparse/backward snapshots.
        foreach(var frame in new[]{source,dense,sparse,source,sparse})Compare(frame);

        var shading=Vertices(sparse);
        shading[^1]=shading[^1] with {Position=new(-500,0,0)};
        shading[10]=shading[10] with {Normal=Vector3.UnitX,Uv=Vector2.One};
        Compare(sparse.WithDeformedVertices(shading));Assert.Equal(0,enabled.LastRebuiltMaterials);
        var shifted=source.WithMaterials([Material("dense",0,Dense-3),
            Material("sparse",Dense-3,Sparse+3),Material("empty",Dense+Sparse,0)]);
        Compare(shifted);Assert.True(enabled.LastTopologyRebuilt);
        Assert.Equal(saved,Bytes(held));

        void Compare(SceneSnapshot scene)
        {
            var actual=Read(enabled,scene,stream);var fallback=Read(disabled,scene,stream);
            EqualOracle(scene,stream,actual);Assert.Equal(Bytes(fallback),Bytes(actual));
            Assert.Equal(Counters(disabled),Counters(enabled));
        }
    }

    [Theory]
    [InlineData(513L)]
    [InlineData(700L)]
    public void PartialBlockMapBudgetFallsBackWithoutChangingOutputs(long blockBudget)
    {
        var source=Scene();var bounded=new D3D12VisibilityBoundsCache(1,0,1,blockBudget);
        var fallback=new D3D12VisibilityBoundsCache(1,0,1,0);
        foreach(var scene in new[]{source,source.WithDeformedVertices(Vertices(source).Select((v,i)=>
            i<4||i==Shared?v with {Position=new(-i-1,0,1)}:v).ToArray()),source})
        {
            var actual=bounded.GetSurface(scene);Assert.Equal(Bytes(fallback.GetSurface(scene)),Bytes(actual));
            EqualOracle(scene,0,actual);Assert.Equal(Counters(fallback),Counters(bounded));
        }
    }

    private static int[] Counters(D3D12VisibilityBoundsCache cache)=>[cache.LastComparedVertices,
        cache.LastPositionComparisons,cache.LastRebuiltMaterials,cache.LastMaterialRangeComparisons,
        cache.LastRebuiltLeaves,cache.LastScannedIndices,cache.LastFallbackMaterials,cache.LastFullScanMaterials,
        cache.LastMarkedLeafIncidences,cache.LastParallelMaterialCount,cache.LastSparseScanMaterials];
    private static SceneSnapshot Scene()
    {
        var indices=Enumerable.Range(0,Dense).Select(i=>(uint)i).ToList();
        var sparse=Enumerable.Range(0,Sparse).Select(i=>(uint)(1024+i)).ToArray();sparse[512]=Shared;indices.AddRange(sparse);
        return new(Enumerable.Range(0,Count).Select(i=>new SceneVertex(new(i*.125f,0,.5f),Vector3.UnitY,Vector2.Zero)).ToArray(),
            indices.ToArray(),[Material("dense",0,Dense),Material("sparse",Dense,Sparse),Material("empty",Dense+Sparse,0)]);
    }
    private static SceneMaterial Material(string name,int start,int count)=>new(name,start,count,Vector4.One,Vector3.Zero,Vector3.Zero,1);
    private static SceneVertex[] Vertices(SceneSnapshot scene)
    {var vertices=new SceneVertex[scene.VertexCount];scene.CopyVertices(0,vertices);return vertices;}
    private static Vector4[] Positions(SceneSnapshot scene)=>Vertices(scene).Select(v=>new Vector4(v.Position,1)).ToArray();
    private static OriginalVisibilityBounds[] Read(D3D12VisibilityBoundsCache cache,SceneSnapshot scene,int stream)
    {
        if(stream==0)return cache.GetSurface(scene);
        var positions=Positions(scene);if(stream==1)return cache.GetOutline(scene,positions);
        var chunks=new Vector4[(Count+4095)/4096][];
        for(int i=0;i<chunks.Length;i++)chunks[i]=positions.AsSpan(i*4096,Math.Min(4096,Count-i*4096)).ToArray();
        var owned=new D3D12ExpandedPositions(Count,chunks,D3D12ExpandedPositionRevision.Create(
            new SceneSnapshot(Vertices(scene),scene.Indices,scene.Materials).VertexRevision,0,[]));
        var result=cache.GetOutline(scene,owned);Assert.False(owned.IsMaterialized);return result;
    }
    private static byte[] Bytes(OriginalVisibilityBounds[] bounds)=>MemoryMarshal.AsBytes(bounds.AsSpan()).ToArray();
    private static void EqualOracle(SceneSnapshot scene,int stream,OriginalVisibilityBounds[] actual)
        =>Assert.Equal(Bytes(stream==0?OriginalFrustumVisibility.SurfaceMaterialBounds(scene)
            :OriginalFrustumVisibility.PositionMaterialBounds(scene,Positions(scene))),Bytes(actual));
}
