using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category","Gpu")]
public sealed class D3D12ExpandedPositionBufferTests
{
    private const int Chunk=4096;
    private const string Stream="test-expanded-positions";

    [Fact]
    public void HeldCopiesSurviveSlotReuseSkippedFramesAndBackwardBranches()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var root=Data();var a=Patch(root,2,1);var skipped=Patch(a,Chunk+2,2);
        var latest=Patch(skipped,2*Chunk+2,3);var branch=Patch(a,3*Chunk+2,4);
        var positionCache=new D3D12OutlinePositionCache();var options=OriginalOutlineTests.Options();
        var reads=new List<(ID3D12Resource Read,byte[] Expected)>();
        try
        {
            foreach(var data in new[]{a,a,a,latest,branch,skipped,a,latest,a})
            {
                var positions=positionCache.GetChunks(data,options);
                Assert.Equal(Bytes(OriginalOutlineMath.BuildPositions(data,options)),Bytes(positions));
                runtime.Begin();var source=cache.ExpandedPositionBuffer(Stream,positions);
                reads.Add((Copy(runtime,source,positions.Length*16),Bytes(positions)));runtime.Submit();
                Assert.False(positions.IsMaterialized);
                Assert.InRange(cache.ExpandedPositionBufferStatistics.RetainedBuffers,1,3);
            }
            runtime.WaitForIdle();foreach(var (read,expected) in reads)Assert.Equal(expected,Read(read,expected.Length));
            var stats=cache.ExpandedPositionBufferStatistics;
            Assert.Equal(3,stats.FullUploads);Assert.Equal(6,stats.PartialUploads);Assert.Equal(3,stats.BufferCreations);
            Assert.True(stats.CopiedBytes<stats.FullPotentialBytes);Assert.Equal(3,stats.RetainedBuffers);
        }
        finally{runtime.Abort();runtime.WaitForIdle();foreach(var (read,_) in reads)read.Dispose();}
    }

    [Fact]
    public void WidthAndModelBaseChangesRequireCompleteBytesInEveryActualSlot()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var data=Patch(Data(),1,1);var positionsCache=new D3D12OutlinePositionCache();var options=OriginalOutlineTests.Options();
        var original=positionsCache.GetChunks(data,options);
        for(int i=0;i<3;i++){runtime.Begin();cache.ExpandedPositionBuffer(Stream,original);runtime.Submit();}
        var widthData=Patch(data,Chunk+1,2,[new(.9f,Vector4.One)]);
        var width=positionsCache.GetChunks(widthData,options);
        var camera=options with {CameraPosition=options.CameraPosition+new Vector3(1,0,-3)};
        Assert.NotEqual(OriginalOutlineMath.ComputeModelBase(options),OriginalOutlineMath.ComputeModelBase(camera));
        var farther=positionsCache.GetChunks(widthData,camera);
        foreach(var positions in new[]{width,farther,width})
        {
            runtime.Begin();var source=cache.ExpandedPositionBuffer(Stream,positions);
            using var read=Copy(runtime,source,positions.Length*16);runtime.Wait(runtime.Submit());
            Assert.Equal(Bytes(positions),Read(read,positions.Length*16));
            Assert.False(positions.IsMaterialized);
        }
        var stats=cache.ExpandedPositionBufferStatistics;
        Assert.Equal(6,stats.FullUploads);Assert.Equal(0,stats.PartialUploads);Assert.Equal(3,stats.BufferCreations);
    }

    [Fact]
    public void UnknownAndTrackedSameListTransitionsRetainAllOldReaders()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var positionCache=new D3D12OutlinePositionCache();var data=Patch(Data(),3,1);
        var tracked=positionCache.Get(data,OriginalOutlineTests.Options());var unknown=tracked.ToArray();
        ID3D12Resource? unchanged=null;
        for(int i=0;i<5;i++)
        {runtime.Begin();var resource=cache.ExpandedPositionBuffer(Stream,unknown);unchanged??=resource;Assert.Same(unchanged,resource);runtime.Submit();}
        Assert.Equal(1,runtime.BufferUploads);Assert.Equal(0,cache.ExpandedPositionBufferStatistics.BufferCreations);
        runtime.Begin();var a=cache.ExpandedPositionBuffer(Stream,unknown);var b=cache.ExpandedPositionBuffer(Stream,tracked);
        var c=cache.ExpandedPositionBuffer(Stream,unknown);var d=cache.ExpandedPositionBuffer(Stream,tracked);
        Assert.NotSame(a,c);Assert.NotSame(b,d);
        using var ra=Copy(runtime,a,tracked.Length*16);using var rb=Copy(runtime,b,tracked.Length*16);
        using var rc=Copy(runtime,c,tracked.Length*16);using var rd=Copy(runtime,d,tracked.Length*16);
        runtime.Wait(runtime.Submit());foreach(var read in new[]{ra,rb,rc,rd})Assert.Equal(Bytes(tracked),Read(read,tracked.Length*16));
        Assert.Equal(1,cache.ExpandedPositionBufferStatistics.RetainedBuffers);
    }

    [Fact]
    public void ChunkedRootAndTrackedSameListTransitionsKeepAllReadersWithoutMaterialization()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var data=Data();var producer=new D3D12OutlinePositionCache();var options=OriginalOutlineTests.Options();
        var root=producer.GetChunks(data,options);var tracked=producer.GetChunks(Patch(data,Chunk+3,1),options);
        ID3D12Resource? rootBuffer=null;
        for(int i=0;i<4;i++)
        {runtime.Begin();var resource=cache.ExpandedPositionBuffer(Stream,root);rootBuffer??=resource;Assert.Same(rootBuffer,resource);runtime.Submit();}
        Assert.Equal(1,runtime.BufferUploads);Assert.Equal(0,cache.ExpandedPositionBufferStatistics.BufferCreations);
        runtime.Begin();var a=cache.ExpandedPositionBuffer(Stream,root);var b=cache.ExpandedPositionBuffer(Stream,tracked);
        var c=cache.ExpandedPositionBuffer(Stream,root);var d=cache.ExpandedPositionBuffer(Stream,tracked);
        Assert.NotSame(a,c);Assert.NotSame(b,d);
        using var ra=Copy(runtime,a,root.Length*16);using var rb=Copy(runtime,b,root.Length*16);
        using var rc=Copy(runtime,c,root.Length*16);using var rd=Copy(runtime,d,root.Length*16);
        runtime.Wait(runtime.Submit());
        Assert.Equal(Bytes(root),Read(ra,root.Length*16));Assert.Equal(Bytes(root),Read(rc,root.Length*16));
        Assert.Equal(Bytes(tracked),Read(rb,root.Length*16));Assert.Equal(Bytes(tracked),Read(rd,root.Length*16));
        Assert.False(root.IsMaterialized);Assert.False(tracked.IsMaterialized);
        Assert.Equal(1,cache.ExpandedPositionBufferStatistics.RetainedBuffers);
    }

    [Fact]
    public void ChunkedSameRecordingReplacementsLeaseEveryEarlierDestination()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var producer=new D3D12OutlinePositionCache();var data=Patch(Data(),3,1);var options=OriginalOutlineTests.Options();
        var first=producer.GetChunks(data,options);var next=producer.GetChunks(Patch(data,Chunk+3,1),options);
        for(int i=0;i<3;i++){runtime.Begin();cache.ExpandedPositionBuffer(Stream,first);runtime.Submit();}
        runtime.Begin();var a=cache.ExpandedPositionBuffer(Stream,first);var b=cache.ExpandedPositionBuffer(Stream,next);
        var c=cache.ExpandedPositionBuffer(Stream,first);Assert.NotSame(a,b);Assert.NotSame(a,c);Assert.NotSame(b,c);
        using var ra=Copy(runtime,a,first.Length*16);using var rb=Copy(runtime,b,first.Length*16);using var rc=Copy(runtime,c,first.Length*16);
        cache.Dispose();runtime.Wait(runtime.Submit());
        Assert.Equal(Bytes(first),Read(ra,first.Length*16));Assert.Equal(Bytes(next),Read(rb,next.Length*16));Assert.Equal(Bytes(first),Read(rc,first.Length*16));
        // Waiting for the last fence makes copies readable; retire all frame-context leases
        // before requiring that disposed owners have released every destination.
        runtime.WaitForIdle();
        Assert.False(first.IsMaterialized);Assert.False(next.IsMaterialized);Assert.Equal(0,cache.ExpandedPositionBufferStatistics.LiveBuffers);
    }

    [Fact]
    public void AbortedPartialCopyIsNotReusedAndDisposalKeepsQueuedReaders()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var positionCache=new D3D12OutlinePositionCache();var data=Patch(Data(),3,1);
        var a=positionCache.GetChunks(data,OriginalOutlineTests.Options());
        for(int i=0;i<3;i++){runtime.Begin();cache.ExpandedPositionBuffer(Stream,a);runtime.Submit();}
        var b=positionCache.GetChunks(Patch(data,Chunk+3,3),OriginalOutlineTests.Options());
        runtime.Begin();cache.ExpandedPositionBuffer(Stream,b);runtime.Abort();
        Assert.Equal(2,cache.ExpandedPositionBufferStatistics.RetainedBuffers);
        // Revisit the aborted slot without changing its payload or starting a hidden wait.
        for(int i=0;i<2;i++){runtime.Begin();runtime.Submit();}
        runtime.Begin();var source=cache.ExpandedPositionBuffer(Stream,b);using var read=Copy(runtime,source,b.Length*16);
        Assert.Equal(4,cache.ExpandedPositionBufferStatistics.FullUploads);Assert.Equal(1,cache.ExpandedPositionBufferStatistics.PartialUploads);
        cache.Dispose();Assert.Equal(0,cache.ExpandedPositionBufferStatistics.RetainedBuffers);
        runtime.Submit();runtime.WaitForIdle();Assert.Equal(Bytes(b),Read(read,b.Length*16));
        Assert.Equal(0,cache.ExpandedPositionBufferStatistics.LiveBuffers);Assert.Equal(0,cache.BufferUploadStatistics.LivePages);
        Assert.False(a.IsMaterialized);Assert.False(b.IsMaterialized);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ActualHullGuideAndCullingKeepHeldPixelsEqualToFullUploadOracle(int samples)
    {
        var box=D3D12OriginalOutlineTests.Box();var original=box.Surface.Snapshot;
        var vertices=new SceneVertex[4*Chunk+3];original.Vertices.CopyTo(vertices);
        for(int i=original.Vertices.Length;i<vertices.Length;i++)vertices[i]=original.Vertices[0];
        var scene=new SceneSnapshot(vertices,original.Indices,original.Materials,original.Textures,original.World,original.Camera,original.LightDirection);
        var edges=Enumerable.Range(0,vertices.Length).Select(i=>box.Outline!.Vertices[Math.Min(i,box.Outline.Vertices.Count-1)]).ToArray();
        var root=new OriginalFrameScene(new(scene,box.Surface.Materials,box.Surface.Profile),new(edges,box.Outline!.Materials),box.OutlineOptions,
            D3D12OriginalFrameTests.Options() with {EnableFrustumCulling=true,Antialiasing=FrameAntialiasing.Smaa1X,SpatialSamples=samples},box.Materials,
            editorOverlay:new([new(new(.05f,.1f),new(.95f,.1f),new(1,0,1,1),2,DeviceDepth:new(.99f))]));
        var current=root;using var session=new D3D12FrameSession();
        var held=new List<(OriginalFrameScene Frame,D3D12FrameOutput Output)>();
        try
        {
            for(int i=0;i<8;i++)
            {
                var nextScene=PatchScene(current.Surface.Snapshot,original.Vertices.Length,.04f);
                var nextEdges=PatchRange(current.Outline!,0,original.Vertices.Length,.04f);
                current=new(new(nextScene,current.Surface.Materials,current.Surface.Profile),nextEdges,current.OutlineOptions,current.Options,current.Materials,
                    editorOverlay:current.EditorOverlay);
                held.Add((current,session.Render(current,129,97)));
            }
            var recorder=(D3D12FrameRenderer)typeof(D3D12FrameSession).GetField("renderer",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(session)!;
            var cache=(D3D12GpuCache)typeof(D3D12FrameRenderer).GetField("cache",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(recorder)!;
            var originalRecorder=(D3D12OriginalStyleRenderer)typeof(D3D12FrameRenderer).GetField("originalRecorder",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(recorder)!;
            Assert.False(((D3D12OutlinePositionCache)typeof(D3D12OriginalStyleRenderer).GetField("edgePositionCache",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(originalRecorder)!).IsOutputMaterialized);
            Assert.Equal(3,cache.ExpandedPositionBufferStatistics.BufferCreations);
            Assert.Equal(5,cache.ExpandedPositionBufferStatistics.PartialUploads);
            Assert.True(cache.ExpandedPositionBufferStatistics.CopiedBytes<cache.ExpandedPositionBufferStatistics.FullPotentialBytes);
            byte[]? first=null,last=null;
            foreach(var (frame,output) in held)
            {
                var s=frame.Surface.Snapshot;
                var full=new OriginalFrameScene(new(s.WithDeformedVertices(s.Vertices.ToArray()),frame.Surface.Materials,frame.Surface.Profile),
                    new(frame.Outline!.Vertices,frame.Outline.Materials),frame.OutlineOptions,frame.Options,frame.Materials,editorOverlay:frame.EditorOverlay);
                using var independent=new D3D12FrameSession();using var expectedOutput=independent.Render(full,129,97);
                var expected=ReadOutput(independent.Runtime,expectedOutput);
                var actual=ReadOutput(session.Runtime,output);Assert.Equal(expected,actual);first??=actual;last=actual;
            }
            Assert.False(first!.AsSpan().SequenceEqual(last));
        }
        finally{session.WaitForIdle();foreach(var (_,output) in held)output.Dispose();}
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void PartialHullKeepsTemporalGiHistoryAndPixelsEqualToFullUploadSession(LightingMode mode)
    {
        var basis=D3D12RayGiTests.Bounce();var source=basis.Surface.Snapshot;
        var vertices=new SceneVertex[4*Chunk+3];source.Vertices.CopyTo(vertices);
        for(int i=source.Vertices.Length;i<vertices.Length;i++)vertices[i]=source.Vertices[0];
        var scene=new SceneSnapshot(vertices,source.Indices,source.Materials,source.Textures,source.World,source.Camera,source.LightDirection);
        var edges=new OriginalOutlineData(vertices.Select(v=>new OriginalOutlineVertex(v.Position,v.Normal,OriginalOutlineVertexMode.Expand,1,0)),
            source.Materials.Select(_=>new OriginalOutlineMaterial(.2f,new(0,0,0,1))));
        var bindings=basis.Surface.Materials.ToArray();bindings[0]=bindings[0] with {ToonShadowColor=new(.2f,.3f,.4f)};
        var options=D3D12OriginalFrameTests.Options() with {Antialiasing=FrameAntialiasing.Smaa1X,SpatialSamples=2,EnableFrustumCulling=true};
        var gi=new RayGiOptions(mode,Strength:.5f,SamplesPerPixel:2,Seed:31,MaxRayDistance:50,RayEpsilon:.001f,
            Composition:RayGiComposition.ToonPalette,Denoiser:RayGiDenoiser.TemporalSpatial);
        using var actual=new D3D12FrameSession();using var oracle=new D3D12FrameSession();
        for(int i=0;i<6;i++)
        {
            scene=PatchScene(scene,source.Vertices.Length,.015f);edges=PatchRange(edges,0,source.Vertices.Length,.015f);
            var frame=new OriginalFrameScene(new(scene,bindings,basis.Surface.Profile),edges,OriginalOutlineTests.Options(),options,basis.Materials);
            var full=new OriginalFrameScene(new(scene.WithDeformedVertices(scene.Vertices.ToArray()),bindings,basis.Surface.Profile),
                new(edges.Vertices,edges.Materials),frame.OutlineOptions,options,basis.Materials);
            Assert.Equal(oracle.Capture(full,47,35,gi).Rgba,actual.Capture(frame,47,35,gi).Rgba);
            Assert.Equal(mode,actual.RayGiStatistics!.Effective);
            Assert.Equal(oracle.RayGiStatistics!.TemporalHistoryFrames,actual.RayGiStatistics.TemporalHistoryFrames);
            Assert.Equal(oracle.RayGiStatistics.Seed,actual.RayGiStatistics.Seed);
        }
        var renderer=(D3D12FrameRenderer)typeof(D3D12FrameSession).GetField("renderer",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(actual)!;
        var cache=(D3D12GpuCache)typeof(D3D12FrameRenderer).GetField("cache",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(renderer)!;
        Assert.Equal(3,cache.ExpandedPositionBufferStatistics.PartialUploads);Assert.Equal(3,cache.ExpandedPositionBufferStatistics.BufferCreations);
    }

    private static OriginalOutlineData Data()=>new(Enumerable.Range(0,4*Chunk+3).Select(i=>new OriginalOutlineVertex(
        new(i*.0002f,i%7*.13f,-i*.001f),Vector3.UnitY,OriginalOutlineVertexMode.Expand,.8f,0)),[new(.2f,Vector4.One)]);
    private static OriginalOutlineData Patch(OriginalOutlineData source,int index,float offset,OriginalOutlineMaterial[]? materials=null)
        =>PatchRange(source,index,1,offset,materials);
    private static OriginalOutlineData PatchRange(OriginalOutlineData source,int index,int count,float offset,OriginalOutlineMaterial[]? materials=null)
    {
        var method=typeof(OriginalOutlineData).GetMethod("WithComposedVertices",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var callback=Delegate.CreateDelegate(method.GetParameters()[1].ParameterType,new Writer(offset),typeof(Writer).GetMethod(nameof(Writer.Write))!);
        return (OriginalOutlineData)method.Invoke(source,[new[]{(index,count)},callback,materials??source.Materials.ToArray()])!;
    }
    private static SceneSnapshot PatchScene(SceneSnapshot source,int count,float offset)
    {
        var method=typeof(SceneSnapshot).GetMethod("WithComposedVertices",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var callback=Delegate.CreateDelegate(method.GetParameters()[1].ParameterType,new SceneWriter(offset),typeof(SceneWriter).GetMethod(nameof(SceneWriter.Write))!);
        return (SceneSnapshot)method.Invoke(source,[new[]{(0,count)},callback])!;
    }
    private sealed class SceneWriter(float offset)
    {public void Write(int _,Span<SceneVertex> values){for(int i=0;i<values.Length;i++)values[i]=values[i] with {Position=values[i].Position+new Vector3(offset,0,0)};}}
    private sealed class Writer(float offset)
    {public void Write(int _,Span<OriginalOutlineVertex> values){for(int i=0;i<values.Length;i++)values[i]=values[i] with {Position=values[i].Position+new Vector3(offset,0,0)};}}
    private static byte[] Bytes(Vector4[] values)=>MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    private static byte[] Bytes(D3D12ExpandedPositions values)
    {var result=new Vector4[values.Length];values.CopyTo(result);return Bytes(result);}
    private static ID3D12Resource Copy(D3D12GpuRuntime runtime,ID3D12Resource source,int bytes)
    {
        var read=runtime.Device.CreateCommittedResource(HeapType.Readback,ResourceDescription.Buffer((ulong)bytes),ResourceStates.CopyDest);
        runtime.Commands.ResourceBarrierTransition(source,ResourceStates.VertexAndConstantBuffer,ResourceStates.CopySource);
        runtime.Commands.CopyBufferRegion(read,0,source,0,(ulong)bytes);
        runtime.Commands.ResourceBarrierTransition(source,ResourceStates.CopySource,ResourceStates.VertexAndConstantBuffer);return read;
    }
    private static byte[] Read(ID3D12Resource resource,int bytes)
    {var mapped=resource.Map<byte>(0,bytes);try{return mapped.ToArray();}finally{resource.Unmap(0);}}
    private static byte[] ReadOutput(D3D12GpuRuntime runtime,D3D12FrameOutput output)
    {
        var context=runtime.Begin();var layout=new PlacedSubresourceFootPrint[1];
        context.Device.GetCopyableFootprints(output.Resource.Description,0,1,0,layout,new int[1],new ulong[1],out ulong bytes);
        using var read=runtime.Device.CreateCommittedResource(HeapType.Readback,ResourceDescription.Buffer(bytes),ResourceStates.CopyDest);
        context.Commands.CopyTextureRegion(new TextureCopyLocation(read,layout[0]),0,0,0,new TextureCopyLocation(output.Resource,0));
        runtime.Wait(runtime.Submit());var mapped=read.Map<byte>(0,checked((int)bytes));var result=new byte[output.Width*output.Height*4];
        try{for(int y=0;y<output.Height;y++)mapped.Slice(y*layout[0].Footprint.RowPitch,output.Width*4).CopyTo(result.AsSpan(y*output.Width*4));}
        finally{read.Unmap(0);}return result;
    }
}
