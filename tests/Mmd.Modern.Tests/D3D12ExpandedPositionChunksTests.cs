using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12ExpandedPositionChunksTests
{
    private const int Chunk=4096;
    private const int Count=4*Chunk+7;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyChangedChunksAreCopiedAndEveryRetainedOutputMatchesTheScalarOracle(bool modern)
    {
        var root=Data();var cache=new D3D12OutlinePositionCache();var options=OriginalOutlineTests.Options();
        var flags=modern?Flags():null;var first=cache.GetChunks(root,options,flags);var saved=Bytes(first);
        Assert.False(first.IsMaterialized);Assert.Equal(Count,cache.LastExpandedVertices);
        var boundary=Patch(root,[(Chunk-1,2)]);var next=cache.GetChunks(boundary,options,flags);
        Assert.Equal(2,cache.LastCopiedChunks);Assert.Equal(2,cache.LastExpandedVertices);
        Assert.Equal(2*Chunk,cache.LastComparedVertices);
        for(int index=0;index<first.ChunkCount;index++)Assert.Equal(index>1,first.SharesChunk(next,index));
        Assert.Equal(saved,Bytes(first));EqualFull(next,boundary,options,flags);
        Assert.False(cache.IsOutputMaterialized);Assert.False(first.IsMaterialized);

        var inside=Patch(boundary,[(2*Chunk+1,1),(3*Chunk-1,1)]);var insideOutput=cache.GetChunks(inside,options,flags);
        Assert.Equal(1,cache.LastCopiedChunks);Assert.Equal(2,cache.LastExpandedVertices);
        Assert.False(next.SharesChunk(insideOutput,2));Assert.True(next.SharesChunk(insideOutput,3));
        EqualFull(insideOutput,inside,options,flags);Assert.False(insideOutput.IsMaterialized);

        var tail=Patch(inside,[(Count-1,1)]);var tailOutput=cache.GetChunks(tail,options,flags);
        Assert.Equal(7,cache.LastComparedVertices);Assert.Equal(1,cache.LastCopiedChunks);
        Assert.Equal(7,tailOutput.GetChunk(4).Length);EqualFull(tailOutput,tail,options,flags);
        var branch=Patch(root,[(3*Chunk,1)]);
        foreach(var data in new[]{root,tail,boundary,branch,inside,root})
        {var output=cache.GetChunks(data,options,flags);EqualFull(output,data,options,flags);Assert.False(output.IsMaterialized);}
    }

    [Fact]
    public void SmallEditAllocationIsBoundedByChangedChunksRatherThanTheCompletePayload()
    {
        var root=Data(64*Chunk+7);var cache=new D3D12OutlinePositionCache();var options=OriginalOutlineTests.Options();
        var first=cache.GetChunks(root,options);var changed=Patch(root,[(Chunk+7,1)]);
        long allocated=GC.GetAllocatedBytesForCurrentThread();var next=cache.GetChunks(changed,options);
        allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
        Assert.InRange(allocated,Chunk*16L,Chunk*16L*4);
        Assert.Equal(1,cache.LastCopiedChunks);Assert.True(first.SharesChunk(next,0));Assert.False(first.SharesChunk(next,1));
        Assert.False(first.IsMaterialized);Assert.False(next.IsMaterialized);EqualFull(next,changed,options,null);
    }

    [Fact]
    public void BoundsAndConcurrentCompatibilityReadsDoNotChangeChunkOwnershipOrRequireFlatProduction()
    {
        var data=Data();var options=OriginalOutlineTests.Options();var cache=new D3D12OutlinePositionCache();
        var first=cache.GetChunks(data,options);var expected=Bytes(first);
        var scene=new SceneSnapshot(data.Vertices.Select(vertex=>new SceneVertex(vertex.Position,vertex.Normal,Vector2.Zero)).ToArray(),
            [0,1,2,Chunk-1,Chunk,Chunk+1,Count-3,Count-2,Count-1],
            [new("all",0,9,Vector4.One,Vector3.Zero,Vector3.Zero,0)]);
        var bounds=new D3D12VisibilityBoundsCache(1);var original=bounds.GetOutline(scene,first);
        Assert.False(first.IsMaterialized);
        var changed=Patch(data,[(Chunk,1)]);var next=cache.GetChunks(changed,options);
        var actual=bounds.GetOutline(scene,next);Assert.Equal(Chunk,bounds.LastComparedVertices);
        Assert.Equal(BoundsBytes(OriginalFrustumVisibility.PositionMaterialBounds(scene,Values(next))),BoundsBytes(actual));
        Assert.False(next.IsMaterialized);Assert.False(first.IsMaterialized);
        Assert.True(bounds.TryGetCurrentExpanded(scene,next,out var same));Assert.Same(actual,same);
        Assert.False(next.IsMaterialized);
        var readers=new Vector4[16][];
        Parallel.For(0,readers.Length,index=>readers[index]=first.Materialize());
        foreach(var reader in readers){Assert.Same(readers[0],reader);Assert.Equal(expected,Bytes(reader));}
        Assert.True(first.IsMaterialized);Assert.False(next.IsMaterialized);
        Assert.Equal(expected,Bytes(cache.Get(data,options)));Assert.Equal(expected,Bytes(first));
        Assert.True(D3D12ExpandedPositionRevision.TryGet(readers[0],out var proof));Assert.Same(first.Revision,proof);
        GC.KeepAlive(original);
    }

    [Fact]
    public void KeepingOnlyRevisionMetadataRetainsNoOutputChunksSourceOrProducer()
    {
        var (proof,output,chunk,source,producer)=WeakOutput();
        for(int attempt=0;attempt<3&&(output.IsAlive||chunk.IsAlive||source.IsAlive||producer.IsAlive);attempt++)
        {GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}
        Assert.False(output.IsAlive);Assert.False(chunk.IsAlive);Assert.False(source.IsAlive);Assert.False(producer.IsAlive);
        Assert.Equal(Count,proof.VertexCount);GC.KeepAlive(proof);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (D3D12ExpandedPositionRevision Proof,WeakReference Output,WeakReference Chunk,WeakReference Source,WeakReference Producer) WeakOutput()
    {
        var source=Data();var cache=new D3D12OutlinePositionCache();var output=cache.GetChunks(source,OriginalOutlineTests.Options());
        var chunk=((Vector4[][])typeof(D3D12ExpandedPositions).GetField("chunks",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(output)!)[0];
        return(output.Revision,new(output),new(chunk),new(source),new(cache));
    }
    private sealed class Writer
    {public void Write(int _,Span<OriginalOutlineVertex> values){for(int i=0;i<values.Length;i++)values[i]=values[i] with {Position=values[i].Position+new Vector3(.125f,-.25f,.5f)};}}
    private static OriginalOutlineData Patch(OriginalOutlineData source,(int Start,int Count)[] ranges)
    {
        var method=typeof(OriginalOutlineData).GetMethod("WithComposedVertices",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var callback=Delegate.CreateDelegate(method.GetParameters()[1].ParameterType,new Writer(),typeof(Writer).GetMethod(nameof(Writer.Write))!);
        return (OriginalOutlineData)method.Invoke(source,[ranges,callback,source.Materials.ToArray()])!;
    }
    private static OriginalOutlineData Data(int count=Count)=>new(Enumerable.Range(0,count).Select(i=>new OriginalOutlineVertex(
        new(i*.00037f,i%7*.13f,-i*.0011f),new(i%3*.3f,-.2f,i%5*.4f),
        i%9==0?OriginalOutlineVertexMode.Inward:OriginalOutlineVertexMode.Expand,.3f+i%5*.1f,i%2)),[new(.2f,Vector4.One),new(.4f,Vector4.Zero)]);
    private static OriginalFrameMaterialFlags[] Flags()=>[new(false,false,false){CharacterOutlineStyle=new(.2f)},new(false,false,false)];
    private static Vector4[] Values(D3D12ExpandedPositions values){var result=new Vector4[values.Length];values.CopyTo(result);return result;}
    private static byte[] Bytes(D3D12ExpandedPositions values)=>Bytes(Values(values));
    private static byte[] Bytes(Vector4[] values)=>MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    private static byte[] BoundsBytes(OriginalVisibilityBounds[] values)=>MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    private static void EqualFull(D3D12ExpandedPositions actual,OriginalOutlineData data,OriginalOutlineOptions options,IReadOnlyList<OriginalFrameMaterialFlags>? flags)
        =>Assert.Equal(Bytes(flags is null?OriginalOutlineMath.BuildPositions(data,options):CharacterOutlineMath.BuildPositions(data,options,flags)),Bytes(actual));
}
