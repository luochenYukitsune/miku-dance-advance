using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12VisibilityBoundsLeafTests
{
    private const int Leaf=D3D12VisibilityBoundsCache.IndexLeafSize;
    private const int LargeCount=3*Leaf+9;
    private const int VertexCount=2*4096+7;
    private static readonly float NegativeZero=BitConverter.Int32BitsToSingle(int.MinValue);

    [Fact]
    public void ChangedVertexOnlyRescansItsIndexLeavesIncludingRepeatedAndCrossLeafReferences()
    {
        var source=Scene();var cache=new D3D12VisibilityBoundsCache(1);
        var original=cache.GetSurface(source);var saved=Bytes(original);
        var changed=Compose(source,7,v=>v with {Position=new(-10,3,4)});
        EqualSurface(changed,cache.GetSurface(changed));
        Assert.Equal(1,cache.LastRebuiltMaterials);Assert.Equal(1,cache.LastRebuiltLeaves);
        Assert.Equal(Leaf,cache.LastScannedIndices);Assert.Equal(4096,cache.LastComparedVertices);

        // Vertex Leaf occurs in BOTH adjacent large-material leaves and twice in the
        // shared material. Each vertex/leaf pair must invalidate exactly once.
        var repeated=Compose(changed,Leaf,v=>v with {Position=new(-20,8,9)});
        EqualSurface(repeated,cache.GetSurface(repeated));
        Assert.Equal(2,cache.LastRebuiltMaterials);Assert.Equal(3,cache.LastRebuiltLeaves);
        Assert.Equal(2*Leaf+6,cache.LastScannedIndices);
        Assert.Equal(saved,Bytes(original));Assert.False(IsMaterialized(changed));Assert.False(IsMaterialized(repeated));
    }

    [Fact]
    public void FiniteExtremesSubnormalsAndBothZeroSignsKeepTheIndependentOrderedOracleBits()
    {
        var source=Scene();var vertices=source.Vertices.ToArray();
        float[] values=[0,NegativeZero,float.Epsilon,-float.Epsilon,float.MaxValue,-float.MaxValue];
        for(int i=0;i<vertices.Length;i++)vertices[i]=vertices[i] with
        {Position=new(values[i%values.Length],values[(i+1)%values.Length],values[(i+2)%values.Length])};
        var scene=source.WithDeformedVertices(vertices);var cache=new D3D12VisibilityBoundsCache(1);
        var held=cache.GetSurface(scene);var heldBytes=Bytes(held);EqualSurface(scene,held);
        for(int step=0;step<18;step++)
        {
            int index=step%3==0?Leaf:step%3==1?2*Leaf:LargeCount-1;
            float value=values[step%values.Length];
            scene=Compose(scene,index,v=>v with {Position=new(value,value,value)});
            EqualSurface(scene,cache.GetSurface(scene));Assert.Equal(0,cache.LastFallbackMaterials);
        }
        Assert.Equal(heldBytes,Bytes(held));
        var zeroVertices=source.Vertices.ToArray();
        for(int i=0;i<zeroVertices.Length;i++)zeroVertices[i]=zeroVertices[i] with {Position=Vector3.Zero};
        var zeros=source.WithDeformedVertices(zeroVertices);cache.GetSurface(zeros);
        var negative=Compose(zeros,Leaf,v=>v with {Position=new(NegativeZero,NegativeZero,NegativeZero)});
        var bounds=cache.GetSurface(negative);EqualSurface(negative,bounds);
        Assert.Equal(int.MinValue,BitConverter.SingleToInt32Bits(bounds[0].Minimum.X));
        Assert.Equal(0,BitConverter.SingleToInt32Bits(bounds[0].Maximum.X));
    }

    [Fact]
    public void DefensiveSurfaceNonfiniteFallbackKeepsNaNPayloadOrderAndRestoresFiniteLeaves()
    {
        var source=Scene();var cache=new D3D12VisibilityBoundsCache(1);cache.GetSurface(source);
        var unusual=source.WithDeformedVertices(source.Vertices.ToArray());
        // Public snapshot APIs reject nonfinite positions. Corrupt this fresh test fixture
        // before the cache first sees it to verify the defensive exact fallback as well.
        CorruptBeforeCaching(unusual,Leaf+4,new(BitConverter.Int32BitsToSingle(0x7fc00001),2,float.PositiveInfinity));
        CorruptBeforeCaching(unusual,2*Leaf+4,new(BitConverter.Int32BitsToSingle(0x7fc00002),float.NegativeInfinity,3));
        var unknown=cache.GetSurface(unusual);var held=Bytes(unknown);EqualSurface(unusual,unknown);
        Assert.Equal(1,cache.LastFallbackMaterials);Assert.Equal(2,cache.LastRebuiltLeaves);
        Assert.Equal(2*Leaf+LargeCount,cache.LastScannedIndices);
        Assert.True(OriginalFrustumVisibility.IsPotentiallyVisible(unknown[0],Matrix4x4.Identity));
        EqualSurface(source,cache.GetSurface(source));Assert.Equal(0,cache.LastFallbackMaterials);
        Assert.Equal(2,cache.LastRebuiltLeaves);Assert.Equal(2*Leaf,cache.LastScannedIndices);
        Assert.Equal(held,Bytes(unknown));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ExpandedInvalidComponentsStayUnknownAndRestoreAcrossLeaves(int component)
    {
        var scene=Scene();var root=Positions(scene);var cache=new D3D12VisibilityBoundsCache(1);
        var held=cache.GetOutline(scene,root);var heldBytes=Bytes(held);
        foreach(float value in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity,0f})
        {
            var positions=root.ToArray();var p=positions[Leaf];
            positions[Leaf]=component switch
            {0=>p with {X=value},1=>p with {Y=value},2=>p with {Z=value},_=>p with {W=value}};
            var actual=cache.GetOutline(scene,positions);EqualOutline(scene,positions,actual);
            Assert.Equal(3,cache.LastRebuiltLeaves);Assert.Equal(2,cache.LastRebuiltMaterials);
            if(!float.IsFinite(value)||component==3)
            {
                Assert.True(float.IsNaN(actual[0].Minimum.X));Assert.True(float.IsNaN(actual[1].Maximum.Z));
                Assert.True(OriginalFrustumVisibility.IsPotentiallyVisible(actual[0],Matrix4x4.Identity));
            }
            EqualOutline(scene,root,cache.GetOutline(scene,root));Assert.Equal(3,cache.LastRebuiltLeaves);
        }
        Assert.Equal(heldBytes,Bytes(held));
    }

    [Fact]
    public void ExpandedChunkOutputsUseLeafBoundsWithoutMaterializingAndKeepHeldResults()
    {
        var scene=Scene();var producer=new D3D12OutlinePositionCache();var cache=new D3D12VisibilityBoundsCache(1);
        var options=OriginalOutlineTests.Options();var rootData=Data(scene);var root=producer.GetChunks(rootData,options);
        var held=cache.GetOutline(scene,root);var saved=Bytes(held);
        var changed=Compose(scene,Leaf,v=>v with {Position=v.Position+new Vector3(2,3,4)});
        var data=Data(changed);var output=producer.GetChunks(data,options);
        var actual=cache.GetOutline(changed,output);
        EqualOutline(changed,OriginalOutlineMath.BuildPositions(data,options),actual);
        Assert.Equal(3,cache.LastRebuiltLeaves);Assert.Equal(2*Leaf+6,cache.LastScannedIndices);
        Assert.False(root.IsMaterialized);Assert.False(output.IsMaterialized);Assert.False(producer.IsOutputMaterialized);
        Assert.Equal(saved,Bytes(held));
        EqualOutline(scene,OriginalOutlineMath.BuildPositions(rootData,options),cache.GetOutline(scene,root));
    }

    [Fact]
    public void ShadingUnusedAndMetadataUpdatesDoNotRescanLeavesAndBranchesUseActualBaseline()
    {
        var source=Scene();var cache=new D3D12VisibilityBoundsCache(2);var held=cache.GetSurface(source);
        var shading=Compose(source,7,v=>v with {Normal=Vector3.UnitX,Uv=Vector2.One});
        Assert.Same(held,cache.GetSurface(shading));Assert.Equal(0,cache.LastRebuiltLeaves);
        var unused=Compose(shading,VertexCount-1,v=>v with {Position=new(-50)});
        Assert.Same(held,cache.GetSurface(unused));Assert.Equal(0,cache.LastScannedIndices);
        var first=Compose(unused,7,v=>v with {Position=new(-2)});
        var skipped=Compose(first,Leaf,v=>v with {Position=new(-3)});
        var latest=Compose(skipped,2*4096,v=>v with {Position=new(-4)});
        var branch=Compose(source,2*Leaf+4,v=>v with {Position=new(-5)});
        foreach(var scene in new[]{latest,source,first,branch,latest})
        {
            EqualSurface(scene,cache.GetSurface(scene));Assert.False(cache.LastTopologyRebuilt);
            Assert.False(IsMaterialized(scene));Assert.Equal(1,cache.EntryCount);
        }
        var wrapped=latest.WithWorld(Matrix4x4.CreateTranslation(1,2,3)).WithMaterials(latest.Materials.Select(m=>m with {Diffuse=new(.5f)}));
        var bounds=cache.GetSurface(wrapped);Assert.Same(bounds,cache.GetSurface(latest));
        Assert.Equal(0,cache.LastScannedIndices);Assert.Equal(0,cache.LastRebuiltLeaves);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TopologyReplacementEvictsLeafCachesAtTheExistingCapacity(int capacity)
    {
        var source=Scene();var cache=new D3D12VisibilityBoundsCache(capacity);
        var held=cache.GetSurface(source);var saved=Bytes(held);
        var second=Ranged(source,3);var third=Ranged(source,6);
        foreach(var scene in new[]{second,third,source,second})
        {
            EqualSurface(scene,cache.GetSurface(scene));Assert.True(cache.LastTopologyRebuilt);
            Assert.True(cache.EntryCount<=capacity);
        }
        var replaced=new SceneSnapshot(source.Vertices.ToArray(),source.Indices.ToArray().Reverse().ToArray(),source.Materials);
        EqualSurface(replaced,cache.GetSurface(replaced));Assert.True(cache.LastTopologyRebuilt);
        Assert.Equal(saved,Bytes(held));
    }

    [Fact]
    public void LeafBudgetFallsBackToExistingMaterialLoopsAndEmptyScenesKeepTheirIdentity()
    {
        var source=Scene();var cache=new D3D12VisibilityBoundsCache(1,leafCacheByteLimit:0);
        EqualSurface(source,cache.GetSurface(source));Assert.Equal(0,cache.LastRebuiltLeaves);
        var changed=Compose(source,7,v=>v with {Position=new(-4)});
        EqualSurface(changed,cache.GetSurface(changed));Assert.Equal(LargeCount,cache.LastScannedIndices);
        var positions=Positions(source);var outline=new D3D12VisibilityBoundsCache(1,leafCacheByteLimit:0);
        EqualOutline(source,positions,outline.GetOutline(source,positions));Assert.Equal(0,outline.LastRebuiltLeaves);
        var empty=SceneSnapshot.CreateEmpty();var emptyCache=new D3D12VisibilityBoundsCache(1);
        var bounds=emptyCache.GetSurface(empty);Assert.Empty(bounds);Assert.Same(bounds,emptyCache.GetSurface(empty));
        Assert.Equal(0,emptyCache.LastScannedIndices);
    }

    [Fact]
    public void DenseSurfaceUpdatesSkipLeafIncidencesAndSparseTransitionsRebuildInvalidLeaves()
    {
        var source=Scene();var cache=new D3D12VisibilityBoundsCache(1);
        var held=cache.GetSurface(source);var heldBytes=Bytes(held);
        var dense=source.WithDeformedVertices(source.Vertices.ToArray().Select(v=>v with {Position=v.Position+new Vector3(2,3,4)}).ToArray());
        EqualSurface(dense,cache.GetSurface(dense));
        Assert.Equal(1,cache.LastFullScanMaterials);Assert.Equal(0,cache.LastMarkedLeafIncidences);
        Assert.Equal(2,cache.LastRebuiltLeaves);Assert.Equal(LargeCount+9,cache.LastScannedIndices);

        // The dense loop did not update leaf contents. A first sparse result must
        // rebuild every large-material leaf before later partial reuse is allowed.
        var sparse=Compose(dense,7,v=>v with {Position=new(-10,3,4)});
        EqualSurface(sparse,cache.GetSurface(sparse));Assert.Equal(0,cache.LastFullScanMaterials);
        Assert.Equal(4,cache.LastRebuiltLeaves);Assert.Equal(LargeCount,cache.LastScannedIndices);
        var later=Compose(sparse,8,v=>v with {Position=new(-20,3,4)});
        EqualSurface(later,cache.GetSurface(later));Assert.Equal(1,cache.LastRebuiltLeaves);
        Assert.Equal(Leaf,cache.LastScannedIndices);Assert.True(cache.LastMarkedLeafIncidences>0);

        foreach(var scene in new[]{dense,source,sparse,later,source})
            EqualSurface(scene,cache.GetSurface(scene));
        Assert.Equal(heldBytes,Bytes(held));
    }

    [Fact]
    public void DenseExpandedUpdatesKeepInvalidInputAndHeldOutputBitsAcrossSparseAndBackwardResults()
    {
        var scene=Scene();var root=Positions(scene);var cache=new D3D12VisibilityBoundsCache(1);
        var held=cache.GetOutline(scene,root);var heldBytes=Bytes(held);
        var dense=root.Select(p=>p+new Vector4(2,3,4,0)).ToArray();
        dense[Leaf]=dense[Leaf] with {W=0};
        var invalid=cache.GetOutline(scene,dense);var invalidBytes=Bytes(invalid);
        EqualOutline(scene,dense,invalid);Assert.Equal(1,cache.LastFullScanMaterials);
        Assert.Equal(0,cache.LastMarkedLeafIncidences);Assert.True(float.IsNaN(invalid[0].Minimum.X));

        var sparse=dense.ToArray();sparse[Leaf]=sparse[Leaf] with {W=1};
        EqualOutline(scene,sparse,cache.GetOutline(scene,sparse));
        Assert.Equal(0,cache.LastFullScanMaterials);Assert.Equal(5,cache.LastRebuiltLeaves);
        Assert.Equal(LargeCount+6,cache.LastScannedIndices);
        var later=sparse.ToArray();later[7]=new(-30,NegativeZero,0,1);
        EqualOutline(scene,later,cache.GetOutline(scene,later));Assert.Equal(1,cache.LastRebuiltLeaves);

        foreach(var positions in new[]{root,dense,sparse,later,dense,root})
            EqualOutline(scene,positions,cache.GetOutline(scene,positions));
        Assert.Equal(heldBytes,Bytes(held));Assert.Equal(invalidBytes,Bytes(invalid));
    }

    [Fact]
    public void ASharedVertexTouchingEveryLeafUsesTheOrderedFullScan()
    {
        var source=Scene();var indices=source.Indices.ToArray();
        for(int index=0;index<LargeCount;index+=Leaf)indices[index]=7;
        var scene=new SceneSnapshot(source.Vertices.ToArray(),indices,source.Materials);
        var cache=new D3D12VisibilityBoundsCache(1);cache.GetSurface(scene);
        var changed=Compose(scene,7,v=>v with {Position=new(-10)});
        EqualSurface(changed,cache.GetSurface(changed));
        Assert.Equal(1,cache.LastFullScanMaterials);Assert.Equal(0,cache.LastRebuiltLeaves);
        Assert.Equal(LargeCount,cache.LastScannedIndices);
        EqualSurface(scene,cache.GetSurface(scene));Assert.Equal(4,cache.LastRebuiltLeaves);
    }

    private static SceneSnapshot Scene()
    {
        var vertices=Enumerable.Range(0,VertexCount).Select(i=>new SceneVertex(new(100+i*.25f,i%7*.125f,.5f),Vector3.UnitY,Vector2.Zero)).ToArray();
        var indices=Enumerable.Range(0,LargeCount).Select(i=>(uint)i).ToList();indices[Leaf-1]=Leaf;
        indices.AddRange(new uint[]{Leaf-1,Leaf,Leaf,2*Leaf,2*Leaf+1,0,2*4096,2*4096+1,2*4096+2});
        return new(vertices,indices.ToArray(),[Material("large",0,LargeCount),Material("shared",LargeCount,6),
            Material("tail",LargeCount+6,3),Material("empty",LargeCount+9,0)]);
    }
    private static SceneSnapshot Ranged(SceneSnapshot scene,int delta)=>scene.WithMaterials(
        [Material("large",0,LargeCount-delta),Material("shared",LargeCount-delta,6+delta),
            Material("tail",LargeCount+6,3),Material("empty",LargeCount+9,0)]);
    private static SceneMaterial Material(string name,int start,int count)=>new(name,start,count,Vector4.One,Vector3.Zero,Vector3.Zero,1);
    private static Vector4[] Positions(SceneSnapshot scene)=>scene.Vertices.ToArray().Select(v=>new Vector4(v.Position,1)).ToArray();
    private static OriginalOutlineData Data(SceneSnapshot scene)=>new(scene.Vertices.ToArray().Select(v=>
        new OriginalOutlineVertex(v.Position,v.Normal,OriginalOutlineVertexMode.Expand,1,0)),
        scene.Materials.Select(_=>new OriginalOutlineMaterial(.2f,Vector4.One)));
    private static byte[] Bytes(OriginalVisibilityBounds[] bounds)=>MemoryMarshal.AsBytes(bounds.AsSpan()).ToArray();
    private static void EqualSurface(SceneSnapshot scene,OriginalVisibilityBounds[] actual)
        =>Assert.Equal(Bytes(OriginalFrustumVisibility.SurfaceMaterialBounds(scene)),Bytes(actual));
    private static void EqualOutline(SceneSnapshot scene,Vector4[] positions,OriginalVisibilityBounds[] actual)
        =>Assert.Equal(Bytes(OriginalFrustumVisibility.PositionMaterialBounds(scene,positions)),Bytes(actual));
    private static void CorruptBeforeCaching(SceneSnapshot scene,int index,Vector3 position)
    {
        var span=scene.GetVertexChunk(index/4096);
        var writable=MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(span),span.Length);
        writable[index%4096]=writable[index%4096] with {Position=position};
    }
    private static bool IsMaterialized(SceneSnapshot scene)
    {
        var storage=typeof(SceneSnapshot).GetField("vertexStorage",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(scene)!;
        return (bool)storage.GetType().GetProperty("IsMaterialized",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.GetValue(storage)!;
    }
    private sealed class Writer(Func<SceneVertex,SceneVertex> edit)
    {public void Write(int _,Span<SceneVertex> destination){for(int i=0;i<destination.Length;i++)destination[i]=edit(destination[i]);}}
    private static SceneSnapshot Compose(SceneSnapshot scene,int index,Func<SceneVertex,SceneVertex> edit)
    {
        var method=typeof(SceneSnapshot).GetMethod("WithComposedVertices",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var writer=Delegate.CreateDelegate(method.GetParameters()[1].ParameterType,new Writer(edit),typeof(Writer).GetMethod(nameof(Writer.Write))!);
        try{return (SceneSnapshot)method.Invoke(scene,[new[]{(index,1)},writer])!;}
        catch(TargetInvocationException error) when(error.InnerException is not null)
        {ExceptionDispatchInfo.Capture(error.InnerException).Throw();throw;}
    }
}
