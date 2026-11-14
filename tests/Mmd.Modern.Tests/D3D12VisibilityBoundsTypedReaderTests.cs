using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12VisibilityBoundsTypedReaderTests
{
    private const int Chunk=D3D12ExpandedPositions.ChunkSize;
    private const int Count=3*Chunk+7;
    private const int LargeCount=6*D3D12VisibilityBoundsCache.IndexLeafSize+3;
    private static readonly float NegativeZero=BitConverter.Int32BitsToSingle(int.MinValue);

    [Theory]
    [InlineData(0L,false,false)]
    [InlineData(0L,false,true)]
    [InlineData(0L,true,false)]
    [InlineData(0L,true,true)]
    [InlineData(D3D12VisibilityBoundsCache.MaximumLeafCacheBytes,false,false)]
    [InlineData(D3D12VisibilityBoundsCache.MaximumLeafCacheBytes,false,true)]
    [InlineData(D3D12VisibilityBoundsCache.MaximumLeafCacheBytes,true,false)]
    [InlineData(D3D12VisibilityBoundsCache.MaximumLeafCacheBytes,true,true)]
    public void OriginalGatherOrderMatchesFlatOracleForBothReadersAndCacheBudgets(long budget,bool owned,bool alternateChunks)
    {
        var scene=Scene(alternateChunks);var positions=Positions();var cache=new D3D12VisibilityBoundsCache(1,budget);
        var first=Get(cache,scene,positions,owned);var saved=Bytes(first);
        EqualOracle(scene,positions,first);

        // Every input is a fresh immutable stream. Exercise dense, sparse, reverse and
        // mixed array/chunk transitions against this entry's actual previous output.
        var dense=positions.Select(p=>p+new Vector4(2,3,4,0)).ToArray();
        var sparse=dense.ToArray();sparse[Chunk]=new(-100,NegativeZero,0,1);
        var later=sparse.ToArray();later[2*Chunk]=new(-200,0,NegativeZero,1);
        foreach(var input in new[]{dense,sparse,later,positions,later,dense,positions})
            EqualOracle(scene,input,Get(cache,scene,input,owned));
        EqualOracle(scene,positions,Get(cache,scene,positions,!owned));
        Assert.Equal(saved,Bytes(first));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void InvalidComponentsAtChunkBoundariesAndShortTailKeepUnknownBitsAndHeldResults(int component)
    {
        var scene=Scene(true);var positions=Positions();
        foreach(long budget in new[]{0L,D3D12VisibilityBoundsCache.MaximumLeafCacheBytes})
        foreach(bool owned in new[]{false,true})
        {
            var cache=new D3D12VisibilityBoundsCache(1,budget);
            var held=Get(cache,scene,positions,owned);var heldBytes=Bytes(held);
            foreach(int vertex in new[]{Chunk-1,Chunk,2*Chunk,Count-1})
            foreach(float value in new[]{BitConverter.Int32BitsToSingle(unchecked((int)0x7fc00031)),
                float.NegativeInfinity,float.PositiveInfinity,NegativeZero,0f,2f})
            {
                var changed=positions.ToArray();var p=changed[vertex];
                changed[vertex]=component switch
                {0=>p with {X=value},1=>p with {Y=value},2=>p with {Z=value},_=>p with {W=value}};
                var actual=Get(cache,scene,changed,owned);EqualOracle(scene,changed,actual);
                var saved=Bytes(actual);
                EqualOracle(scene,positions,Get(cache,scene,positions,owned));
                Assert.Equal(saved,Bytes(actual));
            }
            Assert.Equal(heldBytes,Bytes(held));
        }
    }

    [Fact]
    public void OwnedDenseThenSparseRebuildsStaleLeavesWithoutMaterializingPositions()
    {
        var scene=Scene(false);var positions=Positions();var cache=new D3D12VisibilityBoundsCache(1);
        var root=Owned(scene,positions);var held=cache.GetOutline(scene,root);var heldBytes=Bytes(held);
        var dense=positions.Select(p=>p+new Vector4(1,2,3,0)).ToArray();
        var denseOutput=Owned(scene,dense);EqualOracle(scene,dense,cache.GetOutline(scene,denseOutput));
        Assert.Equal(1,cache.LastFullScanMaterials);Assert.Equal(0,cache.LastMarkedLeafIncidences);
        var sparse=dense.ToArray();sparse[0]=new(-100,0,0,1);
        var sparseOutput=Owned(scene,sparse);EqualOracle(scene,sparse,cache.GetOutline(scene,sparseOutput));
        Assert.Equal(0,cache.LastFullScanMaterials);
        Assert.Equal(LargeCount,cache.LastScannedIndices);
        Assert.Equal((LargeCount+D3D12VisibilityBoundsCache.IndexLeafSize-1)/D3D12VisibilityBoundsCache.IndexLeafSize,
            cache.LastRebuiltLeaves);
        EqualOracle(scene,positions,cache.GetOutline(scene,root));
        Assert.False(root.IsMaterialized);Assert.False(denseOutput.IsMaterialized);Assert.False(sparseOutput.IsMaterialized);
        Assert.Equal(heldBytes,Bytes(held));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(D3D12VisibilityBoundsCache.MaximumLeafCacheBytes)]
    public void ZeroOnlyBoundsPreserveBothSignsThroughChunkAndLeafTransitions(long budget)
    {
        var scene=Scene(true);var cache=new D3D12VisibilityBoundsCache(1,budget);
        var zero=Enumerable.Range(0,Count).Select(i=>new Vector4(i%2==0?0:NegativeZero,
            i%3==0?NegativeZero:0,i%5==0?NegativeZero:0,1)).ToArray();
        var held=Get(cache,scene,zero,true);var saved=Bytes(held);
        var changed=zero.Select(p=>new Vector4(-p.X,-p.Y,-p.Z,1)).ToArray();
        foreach(var positions in new[]{changed,zero})
        {
            EqualOracle(scene,positions,Get(cache,scene,positions,true));
            EqualOracle(scene,positions,Get(cache,scene,positions,false));
        }
        Assert.Equal(saved,Bytes(held));
    }

    [Fact]
    public void EmptyOwnedStreamAndEmptyMaterialsKeepTheirExistingBoundsIdentity()
    {
        var scene=SceneSnapshot.CreateEmpty();var positions=Owned(scene,[]);var cache=new D3D12VisibilityBoundsCache(1);
        var first=cache.GetOutline(scene,positions);
        Assert.Empty(first);Assert.Same(first,cache.GetOutline(scene,positions));Assert.False(positions.IsMaterialized);
        Assert.Empty(D3D12VisibilityBoundsCache.PositionMaterialBounds(scene,positions));
    }

    private static OriginalVisibilityBounds[] Get(D3D12VisibilityBoundsCache cache,SceneSnapshot scene,Vector4[] positions,bool owned)
    {
        if(!owned)return cache.GetOutline(scene,positions);
        var stream=Owned(scene,positions);var result=cache.GetOutline(scene,stream);
        EqualOracle(scene,positions,D3D12VisibilityBoundsCache.PositionMaterialBounds(scene,stream));
        Assert.False(stream.IsMaterialized);return result;
    }

    private static D3D12ExpandedPositions Owned(SceneSnapshot scene,Vector4[] positions)
    {
        var chunks=new Vector4[(positions.Length+Chunk-1)/Chunk][];
        for(int i=0;i<chunks.Length;i++)chunks[i]=positions.AsSpan(i*Chunk,Math.Min(Chunk,positions.Length-i*Chunk)).ToArray();
        // Independent conservative roots cover every potentially changed position. Do not
        // give arbitrary test streams a shared source proof that would hide changes.
        var source=scene.VertexCount==0?SceneSnapshot.CreateEmpty()
            :new SceneSnapshot(scene.Vertices.ToArray(),scene.Indices,scene.Materials);
        var revision=D3D12ExpandedPositionRevision.Create(source.VertexRevision,0,ReadOnlySpan<int>.Empty);
        return new(positions.Length,chunks,revision);
    }

    private static SceneSnapshot Scene(bool alternateChunks)
    {
        var vertices=Enumerable.Range(0,Count).Select(_=>new SceneVertex(Vector3.Zero,Vector3.UnitY,Vector2.Zero)).ToArray();
        var indices=new uint[LargeCount+9];
        for(int i=0;i<LargeCount;i++)
            indices[i]=alternateChunks?(uint)((i%3)*Chunk+(i/3)%128):(uint)i;
        // Repeated references straddle both index leaves and immutable position chunks.
        indices[255]=Chunk-1;indices[256]=Chunk;indices[257]=Chunk;
        indices[511]=2*Chunk;indices[512]=0;indices[LargeCount-1]=Count-1;
        indices[LargeCount]=Chunk-1;indices[LargeCount+1]=Chunk;indices[LargeCount+2]=2*Chunk;
        for(int i=0;i<6;i++)indices[LargeCount+3+i]=(uint)(Count-6+i);
        return new(vertices,indices,[Material("large",0,LargeCount),Material("shared",LargeCount,3),
            Material("tail",LargeCount+3,6),Material("empty",LargeCount+9,0)]);
    }

    private static Vector4[] Positions()
    {
        float[] values=[0,NegativeZero,float.Epsilon,-float.Epsilon,float.MaxValue,-float.MaxValue];
        return Enumerable.Range(0,Count).Select(i=>new Vector4(values[i%values.Length],
            values[(i+1)%values.Length],values[(i+2)%values.Length],1)).ToArray();
    }
    private static SceneMaterial Material(string name,int start,int count)=>new(name,start,count,Vector4.One,Vector3.Zero,Vector3.Zero,1);
    private static byte[] Bytes(OriginalVisibilityBounds[] bounds)=>MemoryMarshal.AsBytes(bounds.AsSpan()).ToArray();
    private static void EqualOracle(SceneSnapshot scene,Vector4[] positions,OriginalVisibilityBounds[] actual)
        =>Assert.Equal(Bytes(OriginalFrustumVisibility.PositionMaterialBounds(scene,positions)),Bytes(actual));
}
