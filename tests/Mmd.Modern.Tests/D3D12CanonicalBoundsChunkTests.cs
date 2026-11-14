using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12CanonicalBoundsChunkTests
{
    private const int Chunk=4096,Count=2*Chunk+5,Dense=3*Chunk;
    private static readonly float NegativeZero=BitConverter.Int32BitsToSingle(int.MinValue);

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    public void SharedDenseChunksAndSmallTailKeepOrderedBoundsAndOldSnapshots(int degree)
    {
        var scene=Scene();var positions=Positions();var owned=Owned(scene,positions);
        var cache=new D3D12VisibilityBoundsCache(1,0,degree);
        var held=cache.GetOutline(scene,owned);var saved=Bytes(held);
        EqualOracle(scene,positions,held);Assert.False(owned.IsMaterialized);
        var changed=positions.Select(p=>p+new Vector4(0,2,-3,0)).ToArray();
        var next=Owned(scene,changed);
        EqualOracle(scene,changed,cache.GetOutline(scene,next));
        EqualOracle(scene,positions,cache.GetOutline(scene,owned));
        Assert.Equal(saved,Bytes(held));
        Assert.False(owned.IsMaterialized);Assert.False(next.IsMaterialized);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void BadVertexOnlyInvalidatesMaterialsThatActuallyReferenceIt(int kind)
    {
        var scene=Scene();var positions=Positions();
        positions[Chunk+5]=kind switch
        {
            0=>new(float.NaN,0,0,1),1=>new(0,float.PositiveInfinity,0,1),
            2=>new(0,0,float.NegativeInfinity,1),3=>new(0,0,0,0),_=>new(0,0,0,float.NaN)
        };
        var owned=Owned(scene,positions);var cache=new D3D12VisibilityBoundsCache(1,0,16);
        var bounds=cache.GetOutline(scene,owned);EqualOracle(scene,positions,bounds);
        Assert.True(float.IsNaN(bounds[0].Minimum.X));
        Assert.True(float.IsFinite(bounds[^2].Minimum.X));
        Assert.False(owned.IsMaterialized);

        var excluded=Scene(excludeLast:true);var unreferenced=Positions();
        unreferenced[^1]=positions[Chunk+5];
        var excludedOwned=Owned(excluded,unreferenced);
        var untouched=cache.GetOutline(excluded,excludedOwned);EqualOracle(excluded,unreferenced,untouched);
        Assert.All(untouched.Take(untouched.Length-1),b=>Assert.True(float.IsFinite(b.Minimum.X)));
        Assert.False(excludedOwned.IsMaterialized);
    }

    [Theory]
    [InlineData(4095,false,4095)]
    [InlineData(4096,false,4096)]
    [InlineData(4098,false,4098)]
    [InlineData(12288,true,4099)]
    public void ThresholdAndFirstInvalidOrdinalKeepTheOriginalReaderContract(int count,bool special,int visited)
    {
        var scene=Scene();var positions=Positions();positions[Chunk+2]=new(0,0,0,2);
        var owned=Owned(scene,positions);
        var method=typeof(D3D12VisibilityBoundsCache).GetMethod("IndexRange",BindingFlags.NonPublic|BindingFlags.Static)!;
        var read=method.CreateDelegate<IndexReader>();
        var result=read(scene,owned,0,count,out bool actualSpecial,out int actualVisited);
        Assert.Equal(special,actualSpecial);Assert.Equal(visited,actualVisited);
        if(special)Assert.True(float.IsNaN(result.Minimum.X));
        else Assert.True(float.IsFinite(result.Minimum.X));
        Assert.False(owned.IsMaterialized);
    }

    private delegate OriginalVisibilityBounds IndexReader(SceneSnapshot scene,object? positions,int start,int count,
        out bool special,out int visited);

    private static SceneSnapshot Scene(bool excludeLast=false)
    {
        int referenced=excludeLast?Count-1:Count;
        var indices=Enumerable.Range(0,4*Dense).Select(i=>(uint)(i%Dense%referenced)).ToList();
        indices.AddRange(new uint[]{0,Chunk,(uint)(excludeLast?Count-2:Count-1)});
        var materials=Enumerable.Range(0,4).Select(i=>new SceneMaterial($"dense{i}",i*Dense,Dense,Vector4.One,
            Vector3.Zero,Vector3.Zero,1)).ToList();
        materials.Add(new("small",4*Dense,3,Vector4.One,Vector3.Zero,Vector3.Zero,1));
        materials.Add(new("empty",4*Dense+3,0,Vector4.One,Vector3.Zero,Vector3.Zero,1));
        return new(Positions().Select(p=>new SceneVertex(new(p.X,p.Y,p.Z),Vector3.UnitY,Vector2.Zero)).ToArray(),
            indices.ToArray(),materials);
    }
    private static Vector4[] Positions()=>Enumerable.Range(0,Count).Select(i=>new Vector4(i%2==0?0:NegativeZero,
        i%7*.125f,i%3*.25f,1)).ToArray();
    private static D3D12ExpandedPositions Owned(SceneSnapshot scene,Vector4[] positions)
    {
        var chunks=new Vector4[(Count+Chunk-1)/Chunk][];
        for(int i=0;i<chunks.Length;i++)chunks[i]=positions.AsSpan(i*Chunk,Math.Min(Chunk,Count-i*Chunk)).ToArray();
        // Fresh conservative source prevents a fixture lineage from hiding arbitrary output changes.
        var source=new SceneSnapshot(scene.Vertices.ToArray(),scene.Indices,scene.Materials);
        return new(Count,chunks,D3D12ExpandedPositionRevision.Create(source.VertexRevision,0,[]));
    }
    private static byte[] Bytes(OriginalVisibilityBounds[] bounds)=>MemoryMarshal.AsBytes(bounds.AsSpan()).ToArray();
    private static void EqualOracle(SceneSnapshot scene,Vector4[] positions,OriginalVisibilityBounds[] actual)
        =>Assert.Equal(Bytes(OriginalFrustumVisibility.PositionMaterialBounds(scene,positions)),Bytes(actual));
}
