using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class ChunkedSceneUploadTests
{
    private const int chunk=4096;
    private const string stream="chunked-scene-upload";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EqualCopiedIndexOwnersReuseTheMaterialMapAndChangedTopologyDoesNot(bool outline)
    {
        var scene=Scene();var cache=new D3D12VisibilityBoundsCache(1);
        var positions=Vertices(scene).Select(vertex=>new Vector4(vertex.Position,1)).ToArray();
        var first=Get(scene);var firstMap=Map(cache);
        Assert.False(IsMaterialized(scene));
        var equal=new SceneSnapshot(Vertices(scene),scene.Indices.ToArray(),scene.Materials,
            scene.Textures,scene.World,scene.Camera,scene.LightDirection);
        Assert.Same(first,Get(equal));Assert.False(cache.LastTopologyRebuilt);
        Assert.Same(firstMap.Offsets,Map(cache).Offsets);Assert.Same(firstMap.Materials,Map(cache).Materials);
        Assert.Equal(0,cache.LastRebuiltMaterials);Assert.Equal(scene.Materials.Count,cache.LastMaterialRangeComparisons);
        Assert.False(IsMaterialized(scene));

        // The cache advances to the accepted owner, so metadata wrappers take its
        // constant-time identity path on the next request.
        Assert.Same(first,Get(equal.WithWorld(Matrix4x4.CreateTranslation(1,0,0))));
        Assert.Equal(0,cache.LastMaterialRangeComparisons);Assert.False(cache.LastTopologyRebuilt);

        var changedIndices=equal.Indices.ToArray();changedIndices[2]=3;
        var changed=new SceneSnapshot(Vertices(equal),changedIndices,equal.Materials,
            equal.Textures,equal.World,equal.Camera,equal.LightDirection);
        Get(changed);Assert.True(cache.LastTopologyRebuilt);
        Assert.NotSame(firstMap.Offsets,Map(cache).Offsets);
        var changedMap=Map(cache);
        var repartitioned=changed.WithMaterials([
            changed.Materials[0] with {IndexCount=6},changed.Materials[1] with {StartIndex=6,IndexCount=0}]);
        Get(repartitioned);Assert.True(cache.LastTopologyRebuilt);
        Assert.NotSame(changedMap.Offsets,Map(cache).Offsets);

        OriginalVisibilityBounds[] Get(SceneSnapshot input)=>outline?cache.GetOutline(input,positions):cache.GetSurface(input);
    }

    [Fact]
    public void VertexIdentityAndBoundsReadComposedStorageWithoutAFlatCopy()
    {
        var scene=Scene();var wrapped=scene.WithWorld(Matrix4x4.CreateTranslation(2,3,4));
        var identity=new D3D12VertexStreamIdentity(scene);
        Assert.True(identity.Equals(new D3D12VertexStreamIdentity(wrapped)));
        Assert.Equal(identity.GetHashCode(),new D3D12VertexStreamIdentity(wrapped).GetHashCode());
        var cache=new D3D12VisibilityBoundsCache(1);cache.GetSurface(scene);
        var changed=Patch(scene,chunk+7,1,.5f);cache.GetSurface(changed);
        Assert.False(cache.LastTopologyRebuilt);
        Assert.InRange(cache.LastComparedVertices,1,chunk);
        Assert.False(IsMaterialized(scene));Assert.False(IsMaterialized(wrapped));Assert.False(IsMaterialized(changed));
    }

    [Fact]
    [Trait("Category","Gpu")]
    public void ChunkUploadsKeepHeldReadersExactAcrossReplacementAndAbortWithoutFlattening()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var baseline=Scene();var changed=Patch(baseline,chunk+7,1,.5f);
        var reads=new List<(ID3D12Resource Resource,SceneVertex[] Expected)>();
        try
        {
            for(int index=0;index<3;index++)
            {
                runtime.Begin();Hold(cache.SceneVertexBuffer(stream,baseline),baseline);runtime.Submit();
            }
            var warm=cache.SceneVertexBufferStatistics;
            Assert.Equal(3*baseline.VertexChunkCount,warm.CopyCalls);Assert.Equal(3,warm.FullUploads);
            Assert.Equal(3L*baseline.VertexCount*32,warm.CopiedBytes);

            runtime.Begin();var first=cache.SceneVertexBuffer(stream,baseline);
            var second=cache.SceneVertexBuffer(stream,changed);var again=cache.SceneVertexBuffer(stream,baseline);
            Assert.NotSame(first,second);Assert.NotSame(first,again);Assert.NotSame(second,again);
            // Readers are recorded only after every replacement has returned.
            Hold(first,baseline);Hold(second,changed);Hold(again,baseline);runtime.Submit();

            runtime.Begin();var before=cache.SceneVertexBufferStatistics;
            cache.SceneVertexBuffer(stream,changed);
            Assert.Equal(before.PartialUploads+1,cache.SceneVertexBufferStatistics.PartialUploads);
            Assert.Equal(before.CopiedBytes+chunk*32L,cache.SceneVertexBufferStatistics.CopiedBytes);
            runtime.Abort();
            for(int index=0;index<2;index++){runtime.Begin();runtime.Submit();}
            runtime.Begin();before=cache.SceneVertexBufferStatistics;
            Hold(cache.SceneVertexBuffer(stream,changed),changed);
            Assert.Equal(before.FullUploads+1,cache.SceneVertexBufferStatistics.FullUploads);
            Assert.Equal(before.CopyCalls+changed.VertexChunkCount,cache.SceneVertexBufferStatistics.CopyCalls);
            runtime.Submit();runtime.WaitForIdle();
            foreach(var read in reads)Assert.Equal(read.Expected,ReadVertices(read.Resource,read.Expected.Length));
            Assert.False(IsMaterialized(baseline));Assert.False(IsMaterialized(changed));
        }
        finally{runtime.Abort();runtime.WaitForIdle();foreach(var read in reads)read.Resource.Dispose();}

        void Hold(ID3D12Resource source,SceneSnapshot scene)
            =>reads.Add((Copy(runtime,source,scene.VertexCount),Vertices(scene)));
    }

    [Fact]
    [Trait("Category","Gpu")]
    public void HeldChunkedFramesMatchIndependentFullUploadsWithoutMaterializingTheirSurface()
    {
        var box=D3D12OriginalOutlineTests.Box();var source=box.Surface.Snapshot;
        var padded=new SceneVertex[3*chunk+7];source.CopyVertices(0,padded.AsSpan(0,source.VertexCount));
        for(int index=source.VertexCount;index<padded.Length;index++)padded[index]=source.GetVertex(0);
        var root=new SceneSnapshot(padded,source.Indices,source.Materials,source.Textures,
            source.World,source.Camera,source.LightDirection);
        var snapshot=Patch(root,padded.Length-1,1,0);
        using var session=new D3D12FrameSession();
        var held=new List<(D3D12FrameOutput Output,OriginalFrameScene Frame)>();
        try
        {
            for(int index=0;index<5;index++)
            {
                if(index>0)snapshot=Patch(Patch(snapshot,0,source.VertexCount,index%2==0?-.15f:.2f),2*chunk+1,1,.01f);
                var options=D3D12OriginalFrameTests.Options() with
                    {Antialiasing=FrameAntialiasing.Smaa1X,SpatialSamples=index<2?4:1,EnableFrustumCulling=true};
                var frame=new OriginalFrameScene(new(snapshot,box.Surface.Materials,box.Surface.Profile),options:options);
                held.Add((session.Render(frame,index<2?129:97,index<2?97:73),frame));
                Assert.False(IsMaterialized(snapshot));
            }
            session.WaitForIdle();
            foreach(var item in held)
            {
                var scene=item.Frame.Surface.Snapshot;
                var full=scene.WithDeformedVertices(Vertices(scene));
                var oracleFrame=new OriginalFrameScene(new(full,item.Frame.Surface.Materials,item.Frame.Surface.Profile),options:item.Frame.Options);
                using var oracle=new D3D12FrameSession();
                var expected=oracle.Capture(oracleFrame,item.Output.Width,item.Output.Height).Rgba;
                Assert.Equal(expected,ReadOutput(session.Runtime,item.Output));Assert.False(IsMaterialized(scene));
            }
        }
        finally{session.WaitForIdle();foreach(var item in held)item.Output.Dispose();}
    }

    private static SceneSnapshot Scene()
    {
        var vertices=Enumerable.Range(0,3*chunk+7).Select(index=>new SceneVertex(
            new(index*.001f,index%11,index%7),Vector3.UnitZ,new(.25f,.75f))).ToArray();
        var root=new SceneSnapshot(vertices,new uint[]{0,1,2,2,1,3},[
            new("first",0,3,Vector4.One,Vector3.Zero,Vector3.Zero,0),
            new("second",3,3,Vector4.One,Vector3.Zero,Vector3.Zero,0)]);
        return Patch(root,vertices.Length-1,1,0);
    }
    private static SceneSnapshot Patch(SceneSnapshot baseline,int start,int count,float offset)
    {
        var method=typeof(SceneSnapshot).GetMethod("WithComposedVertices",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var writer=new PatchWriter(offset);
        var callback=Delegate.CreateDelegate(method.GetParameters()[1].ParameterType,writer,
            typeof(PatchWriter).GetMethod(nameof(PatchWriter.Write))!);
        return (SceneSnapshot)method.Invoke(baseline,new object[]{new[]{(start,count)},callback})!;
    }
    private sealed class PatchWriter(float offset)
    {
        public void Write(int _,Span<SceneVertex> destination)
        {for(int index=0;index<destination.Length;index++)destination[index]=destination[index] with {Position=destination[index].Position+new Vector3(offset,0,0)};}
    }
    private static SceneVertex[] Vertices(SceneSnapshot scene)
    {var values=new SceneVertex[scene.VertexCount];scene.CopyVertices(0,values);return values;}
    private static bool IsMaterialized(SceneSnapshot scene)
    {
        var storage=typeof(SceneSnapshot).GetField("vertexStorage",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(scene)!;
        return (bool)storage.GetType().GetProperty("IsMaterialized",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.GetValue(storage)!;
    }
    private static (int[] Offsets,int[] Materials) Map(D3D12VisibilityBoundsCache cache)
    {
        var entries=(Array)typeof(D3D12VisibilityBoundsCache).GetField("entries",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(cache)!;
        var entry=entries.GetValue(0)!;
        return ((int[] Offsets,int[] Materials))entry.GetType().GetField("Map",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(entry)!;
    }
    private static ID3D12Resource Copy(D3D12GpuRuntime runtime,ID3D12Resource source,int count)
    {
        int bytes=checked(count*32);
        var read=runtime.Device.CreateCommittedResource(HeapType.Readback,ResourceDescription.Buffer((ulong)bytes),ResourceStates.CopyDest);
        runtime.Commands.ResourceBarrierTransition(source,ResourceStates.VertexAndConstantBuffer,ResourceStates.CopySource);
        runtime.Commands.CopyBufferRegion(read,0,source,0,(ulong)bytes);
        runtime.Commands.ResourceBarrierTransition(source,ResourceStates.CopySource,ResourceStates.VertexAndConstantBuffer);return read;
    }
    private static SceneVertex[] ReadVertices(ID3D12Resource resource,int count)
    {
        var bytes=resource.Map<byte>(0,checked(count*32));
        try{return MemoryMarshal.Cast<byte,SceneVertex>(bytes).ToArray();}finally{resource.Unmap(0);}
    }
    private static byte[] ReadOutput(D3D12GpuRuntime runtime,D3D12FrameOutput output)
    {
        var context=runtime.Begin();var footprint=new PlacedSubresourceFootPrint[1];
        context.Device.GetCopyableFootprints(output.Resource.Description,0,1,0,footprint,new int[1],new ulong[1],out ulong size);
        using var read=context.Device.CreateCommittedResource(HeapType.Readback,ResourceDescription.Buffer(size),ResourceStates.CopyDest);
        context.Commands.CopyTextureRegion(new TextureCopyLocation(read,footprint[0]),0,0,0,new TextureCopyLocation(output.Resource,0));
        runtime.Wait(runtime.Submit());var data=read.Map<byte>(0,checked((int)size));var result=new byte[output.Width*output.Height*4];
        try{for(int index=0;index<output.Height;index++)data.Slice(index*footprint[0].Footprint.RowPitch,output.Width*4).CopyTo(result.AsSpan(index*output.Width*4));}
        finally{read.Unmap(0);}return result;
    }
}
