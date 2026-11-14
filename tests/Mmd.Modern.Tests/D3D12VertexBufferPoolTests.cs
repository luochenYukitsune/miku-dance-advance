using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category","Gpu")]
public sealed class D3D12VertexBufferPoolTests
{
    private const int chunk=4096;
    private const string stream="test-scene-vertices";

    [Fact]
    public void EachSlotComparesItsOwnActualRevisionAcrossSkippedAndBackwardPublications()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var baseline=Scene();var first=Patch(baseline,17,1,1);
        var omitted=Patch(first,chunk+5,1,2);var latest=Patch(omitted,2*chunk+9,1,3);
        var branch=Patch(baseline,3*chunk+4,1,4);
        var inputs=new[]{baseline,baseline,baseline,latest,branch,first,first,latest,baseline};
        var reads=new List<(ID3D12Resource Resource,SceneVertex[] Expected)>();
        try
        {
            foreach(var input in inputs)
            {
                runtime.Begin();var source=cache.SceneVertexBuffer(stream,input);
                reads.Add((Copy(runtime,source,input.Vertices.Length),input.Vertices.ToArray()));runtime.Submit();
                Assert.InRange(cache.SceneVertexBufferStatistics.RetainedBuffers,1,3);
            }
            runtime.WaitForIdle();
            foreach(var read in reads)Assert.Equal(read.Expected,Read(read.Resource,read.Expected.Length));
            var statistics=cache.SceneVertexBufferStatistics;
            Assert.Equal(3,statistics.FullUploads);Assert.Equal(6,statistics.PartialUploads);
            Assert.Equal(3,statistics.BufferCreations);Assert.Equal(3,statistics.LiveBuffers);
            Assert.True(statistics.CopiedBytes<statistics.FullPotentialBytes);
        }
        finally{runtime.Abort();runtime.WaitForIdle();foreach(var read in reads)read.Resource.Dispose();}
    }

    [Fact]
    public void DisjointChangedChunksUseExactSeparateCopiesAndIncludeTheFinalShortChunk()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var baseline=Scene();Warm(runtime,cache,baseline);
        var changed=Patch(Patch(baseline,2,1,1),baseline.Vertices.Length-1,1,2);
        runtime.Begin();var before=cache.SceneVertexBufferStatistics;
        var source=cache.SceneVertexBuffer(stream,changed);using var read=Copy(runtime,source,changed.Vertices.Length);
        runtime.Wait(runtime.Submit());Assert.Equal(changed.Vertices.ToArray(),Read(read,changed.Vertices.Length));
        var after=cache.SceneVertexBufferStatistics;
        Assert.Equal(before.CopyCalls+2,after.CopyCalls);
        Assert.Equal(before.CopiedBytes+(chunk+3)*32L,after.CopiedBytes);
        Assert.Equal(before.PartialUploads+1,after.PartialUploads);Assert.Equal(before.BufferCreations,after.BufferCreations);
    }

    [Fact]
    public void MetadataWrappersAndRepeatedIdentityReuseEveryWarmSlotWithoutCopies()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var scene=Scene();var resources=new ID3D12Resource[3];
        for(int i=0;i<3;i++){runtime.Begin();resources[i]=cache.SceneVertexBuffer(stream,scene);runtime.Submit();}
        var warm=cache.SceneVertexBufferStatistics;long uploads=runtime.BufferUploads;
        for(int i=0;i<9;i++)
        {
            runtime.Begin();var wrapped=scene.WithCamera(scene.Camera).WithLighting(Vector3.UnitY)
                .WithWorld(Matrix4x4.CreateTranslation(i,0,0));
            var source=cache.SceneVertexBuffer(stream,wrapped);
            Assert.Same(resources[i%3],source);Assert.Same(source,cache.SceneVertexBuffer(stream,wrapped));runtime.Submit();
        }
        runtime.WaitForIdle();var after=cache.SceneVertexBufferStatistics;
        Assert.Equal(warm.CopiedBytes,after.CopiedBytes);Assert.Equal(warm.CopyCalls,after.CopyCalls);
        Assert.Equal(18,after.UnchangedHits);Assert.Equal(uploads,runtime.BufferUploads);
        Assert.Equal(3,after.RetainedBuffers);Assert.Equal(scene.Vertices.Length*32L*3,after.RetainedBytes);
    }

    [Fact]
    public void SameListAThenBThenARetainsAllPreviouslyReturnedReaders()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var first=Scene();var second=Patch(first,chunk+7,1,5);
        runtime.Begin();var a=cache.SceneVertexBuffer(stream,first);
        Assert.Same(a,cache.SceneVertexBuffer(stream,first));
        var b=cache.SceneVertexBuffer(stream,second);var aAgain=cache.SceneVertexBuffer(stream,first);
        Assert.NotSame(a,b);Assert.NotSame(a,aAgain);Assert.NotSame(b,aAgain);
        // Deliberately record the readers after every replacement has been returned.
        using var aRead=Copy(runtime,a,first.Vertices.Length);using var bRead=Copy(runtime,b,second.Vertices.Length);
        using var againRead=Copy(runtime,aAgain,first.Vertices.Length);
        Assert.Equal(3,cache.SceneVertexBufferStatistics.LiveBuffers);
        runtime.Submit();runtime.WaitForIdle();
        Assert.Equal(first.Vertices.ToArray(),Read(aRead,first.Vertices.Length));
        Assert.Equal(second.Vertices.ToArray(),Read(bRead,second.Vertices.Length));
        Assert.Equal(first.Vertices.ToArray(),Read(againRead,first.Vertices.Length));
        Assert.Equal(3,cache.SceneVertexBufferStatistics.FullUploads);
        Assert.Equal(0,cache.SceneVertexBufferStatistics.PartialUploads);
        Assert.Equal(1,cache.SceneVertexBufferStatistics.LiveBuffers);
    }

    [Fact]
    public void AbortedNewAndPartiallyUpdatedSlotsRequireFreshFullPayloads()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var baseline=Scene();var changed=Patch(baseline,chunk+3,1,7);
        runtime.Begin();cache.SceneVertexBuffer(stream,baseline);runtime.Abort();
        Assert.Equal(0,cache.SceneVertexBufferStatistics.RetainedBuffers);Assert.Equal(0,cache.SceneVertexBufferStatistics.LiveBuffers);
        Advance();runtime.Begin();cache.SceneVertexBuffer(stream,baseline);runtime.Submit();
        Advance();runtime.Begin();cache.SceneVertexBuffer(stream,changed);runtime.Abort();
        Assert.Equal(0,cache.SceneVertexBufferStatistics.RetainedBuffers);Assert.Equal(0,cache.SceneVertexBufferStatistics.LiveBuffers);
        Advance();runtime.Begin();var source=cache.SceneVertexBuffer(stream,changed);
        using var read=Copy(runtime,source,changed.Vertices.Length);runtime.Wait(runtime.Submit());
        Assert.Equal(changed.Vertices.ToArray(),Read(read,changed.Vertices.Length));
        Assert.Equal(3,cache.SceneVertexBufferStatistics.FullUploads);Assert.Equal(1,cache.SceneVertexBufferStatistics.PartialUploads);
        Assert.Equal(3,cache.SceneVertexBufferStatistics.BufferCreations);

        void Advance(){for(int i=0;i<2;i++){runtime.Begin();runtime.Submit();}}
    }

    [Fact]
    public void SizeChangesReallocateAndUnrelatedSameSizeRootsUseFullCopy()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var baseline=Scene();Warm(runtime,cache,baseline);
        var resized=Scene(baseline.Vertices.Length+9);var unrelated=Scene();
        runtime.Begin();var changed=cache.SceneVertexBuffer(stream,resized);
        using var resizedRead=Copy(runtime,changed,resized.Vertices.Length);runtime.Submit();
        runtime.Begin();changed=cache.SceneVertexBuffer(stream,unrelated);
        using var rootRead=Copy(runtime,changed,unrelated.Vertices.Length);runtime.Submit();runtime.WaitForIdle();
        Assert.Equal(resized.Vertices.ToArray(),Read(resizedRead,resized.Vertices.Length));
        Assert.Equal(unrelated.Vertices.ToArray(),Read(rootRead,unrelated.Vertices.Length));
        var statistics=cache.SceneVertexBufferStatistics;
        Assert.Equal(5,statistics.FullUploads);Assert.Equal(0,statistics.PartialUploads);
        Assert.Equal(4,statistics.BufferCreations);Assert.Equal(3,statistics.RetainedBuffers);Assert.Equal(3,statistics.LiveBuffers);
    }

    [Fact]
    public void CacheDisposalKeepsQueuedDefaultAndUploadResourcesUntilFenceRetirement()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var scene=Scene();runtime.Begin();var source=cache.SceneVertexBuffer(stream,scene);
        using var read=Copy(runtime,source,scene.Vertices.Length);cache.Dispose();
        Assert.Equal(0,cache.SceneVertexBufferStatistics.RetainedBuffers);Assert.Equal(1,cache.SceneVertexBufferStatistics.LiveBuffers);
        Assert.Equal(1,cache.BufferUploadStatistics.LivePages);
        runtime.Submit();runtime.WaitForIdle();Assert.Equal(0,cache.SceneVertexBufferStatistics.LiveBuffers);
        Assert.Equal(0,cache.BufferUploadStatistics.LivePages);Assert.Equal(scene.Vertices.ToArray(),Read(read,scene.Vertices.Length));
    }

    [Fact]
    public void UntrackedSnapshotsKeepGlobalNoChangeReuseAndMixedSameListRoutesKeepOldReadersAlive()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var untracked=Scene(incremental:false);ID3D12Resource? first=null;
        for(int i=0;i<5;i++)
        {
            runtime.Begin();var source=cache.SceneVertexBuffer(stream,untracked.WithCamera(untracked.Camera));
            first??=source;Assert.Same(first,source);runtime.Submit();
        }
        Assert.Equal(1,runtime.BufferUploads);Assert.Equal(0,cache.SceneVertexBufferStatistics.BufferCreations);
        var tracked=Patch(untracked,chunk+3,1,7);var untrackedNext=untracked.WithDeformedVertices(tracked.Vertices.ToArray());
        runtime.Begin();var a=cache.SceneVertexBuffer(stream,untracked);
        var b=cache.SceneVertexBuffer(stream,tracked);var c=cache.SceneVertexBuffer(stream,untrackedNext);
        Assert.Equal(0,cache.SceneVertexBufferStatistics.RetainedBuffers);
        var d=cache.SceneVertexBuffer(stream,tracked);Assert.NotSame(b,d);
        using var aRead=Copy(runtime,a,untracked.Vertices.Length);using var bRead=Copy(runtime,b,tracked.Vertices.Length);
        using var cRead=Copy(runtime,c,untrackedNext.Vertices.Length);using var dRead=Copy(runtime,d,tracked.Vertices.Length);
        runtime.Submit();runtime.WaitForIdle();
        Assert.Equal(untracked.Vertices.ToArray(),Read(aRead,untracked.Vertices.Length));
        Assert.Equal(tracked.Vertices.ToArray(),Read(bRead,tracked.Vertices.Length));
        Assert.Equal(untrackedNext.Vertices.ToArray(),Read(cRead,untrackedNext.Vertices.Length));
        Assert.Equal(tracked.Vertices.ToArray(),Read(dRead,tracked.Vertices.Length));
        Assert.Equal(1,cache.LiveResourceCount);Assert.Equal(1,cache.SceneVertexBufferStatistics.RetainedBuffers);
        Assert.Equal(1,cache.SceneVertexBufferStatistics.LiveBuffers);
    }

    [Fact]
    public void TrackedPartialFramesKeepEveryHeldImageExactAcrossResizeSmaaAndSsaaSlotReuse()
    {
        var box=D3D12OriginalOutlineTests.Box();var source=box.Surface.Snapshot;
        var padded=new SceneVertex[4*chunk+3];source.Vertices.CopyTo(padded);
        for(int i=source.Vertices.Length;i<padded.Length;i++)padded[i]=source.Vertices[0];
        var root=new SceneSnapshot(padded,source.Indices,source.Materials,source.Textures,
            source.World,source.Camera,source.LightDirection);
        var snapshot=Patch(root,padded.Length-1,1,0);
        using var session=new D3D12FrameSession();
        var held=new List<(D3D12FrameOutput Output,OriginalFrameScene Frame)>();
        try
        {
            // Queue every output before any readback/oracle. Changes touch the visible
            // cube and a nonadjacent unused chunk, leaving the remaining chunks intact.
            for(int i=0;i<10;i++)
            {
                if(i>0)snapshot=Patch(Patch(snapshot,0,source.Vertices.Length,i%2==0?-.22f:.28f),2*chunk+13,1,.01f*i);
                Assert.True(snapshot.VertexRevision.SupportsIncrementalUpdates);
                var options=D3D12OriginalFrameTests.Options() with
                    {Antialiasing=FrameAntialiasing.Smaa1X,SpatialSamples=i<2||i%3==0?4:1,EnableFrustumCulling=true};
                var frame=new OriginalFrameScene(new(snapshot,box.Surface.Materials,box.Surface.Profile),options:options);
                int width=i<2?129:i%2==0?97:145,height=i<2?97:i%2==0?73:105;
                var output=session.Render(frame,width,height);held.Add((output,frame));
                Assert.Equal(0,output.Statistics.ReadbackCount);
            }
            var renderer=(D3D12FrameRenderer)typeof(D3D12FrameSession).GetField("renderer",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(session)!;
            var cache=(D3D12GpuCache)typeof(D3D12FrameRenderer).GetField("cache",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(renderer)!;
            var uploads=cache.SceneVertexBufferStatistics;
            Assert.Equal(3,uploads.FullUploads);Assert.Equal(7,uploads.PartialUploads);
            Assert.Equal(3,uploads.BufferCreations);Assert.Equal(3,uploads.RetainedBuffers);
            Assert.True(uploads.CopiedBytes<uploads.FullPotentialBytes);
            session.WaitForIdle();byte[]? firstPixels=null,changedPixels=null;
            for(int i=0;i<held.Count;i++)
            {
                var item=held[i];var scene=item.Frame.Surface.Snapshot;
                // An independent public deformation deliberately forces the complete
                // immutable upload route, so the pixel oracle does not share partial state.
                var oracleScene=scene.WithDeformedVertices(scene.Vertices.ToArray());
                var oracleFrame=new OriginalFrameScene(new(oracleScene,item.Frame.Surface.Materials,item.Frame.Surface.Profile),options:item.Frame.Options);
                using var oracle=new D3D12FrameSession();
                var expected=oracle.Capture(oracleFrame,item.Output.Width,item.Output.Height).Rgba;
                var actual=ReadOutput(session.Runtime,item.Output);Assert.Equal(expected,actual);
                if(i==0)firstPixels=actual;if(i==1)changedPixels=actual;
            }
            // These two frames use identical dimensions and AA; the visible cube moved.
            Assert.False(firstPixels!.AsSpan().SequenceEqual(changedPixels!));
            Assert.NotEqual(new byte[]{20,24,32,255},firstPixels.AsSpan((48*129+64)*4,4).ToArray());
        }
        finally{session.WaitForIdle();foreach(var item in held)item.Output.Dispose();}
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing,false)]
    [InlineData(LightingMode.HardwareRayTracing,false)]
    [InlineData(LightingMode.SoftwareRayTracing,true)]
    [InlineData(LightingMode.HardwareRayTracing,true)]
    public void TrackedPartialGeometryMatchesFullUploadsWithGiSsaaAndTemporalHistory(LightingMode mode,bool temporal)
    {
        var original=D3D12RayGiTests.Bounce();var source=original.Surface.Snapshot;
        var bindings=original.Surface.Materials.ToArray();
        bindings[0]=bindings[0] with {ToonShadowColor=new(.2f,.3f,.4f)};
        var padded=new SceneVertex[4*chunk+3];source.Vertices.CopyTo(padded);
        for(int i=source.Vertices.Length;i<padded.Length;i++)padded[i]=source.Vertices[0];
        var root=new SceneSnapshot(padded,source.Indices,source.Materials,source.Textures,
            source.World,source.Camera,source.LightDirection);
        var snapshot=Patch(root,padded.Length-1,1,0);
        using var actualSession=new D3D12FrameSession();using var oracle=new D3D12FrameSession();
        var capabilities=actualSession.QueryRayGiCapabilities();
        Assert.True(mode==LightingMode.HardwareRayTracing?capabilities.HardwareAvailable:capabilities.SoftwareAvailable);
        var gi=new RayGiOptions(mode,Strength:.5f,SamplesPerPixel:2,Seed:31,MaxRayDistance:50,RayEpsilon:.001f,
            Composition:temporal?RayGiComposition.ToonPalette:RayGiComposition.Additive,
            Denoiser:temporal?RayGiDenoiser.TemporalSpatial:RayGiDenoiser.Off);
        var options=D3D12OriginalFrameTests.Options() with
            {Antialiasing=FrameAntialiasing.Smaa1X,SpatialSamples=2,EnableFrustumCulling=true};
        for(int i=0;i<6;i++)
        {
            if(i>0)snapshot=Patch(Patch(snapshot,0,source.Vertices.Length,.015f),2*chunk+5,1,.01f);
            var actualFrame=new OriginalFrameScene(new(snapshot,bindings,original.Surface.Profile),
                options:options,materials:original.Materials);
            var full=snapshot.WithDeformedVertices(snapshot.Vertices.ToArray());
            var oracleFrame=new OriginalFrameScene(new(full,bindings,original.Surface.Profile),
                options:options,materials:original.Materials);
            var actual=actualSession.Capture(actualFrame,47,35,gi);
            Assert.Equal(oracle.Capture(oracleFrame,47,35,gi).Rgba,actual.Rgba);
            Assert.Equal(mode,actualSession.RayGiStatistics!.Effective);
            if(temporal)
            {
                Assert.Equal(i==0,actualSession.RayGiStatistics.TemporalResetHistory);
                Assert.Equal(oracle.RayGiStatistics!.TemporalHistoryFrames,actualSession.RayGiStatistics.TemporalHistoryFrames);
                Assert.Equal(oracle.RayGiStatistics.Seed,actualSession.RayGiStatistics.Seed);
            }
            if(i==0)
            {
                using var noGi=new D3D12FrameSession();
                Assert.NotEqual(noGi.Capture(actualFrame,47,35).Rgba,actual.Rgba);
            }
        }
        var renderer=(D3D12FrameRenderer)typeof(D3D12FrameSession).GetField("renderer",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(actualSession)!;
        var cache=(D3D12GpuCache)typeof(D3D12FrameRenderer).GetField("cache",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(renderer)!;
        Assert.Equal(3,cache.SceneVertexBufferStatistics.PartialUploads);
        Assert.Equal(3,cache.SceneVertexBufferStatistics.BufferCreations);
    }

    private static SceneSnapshot Scene(int count=4*chunk+3,bool incremental=true)
    {
        var vertices=Enumerable.Range(0,count).Select(index=>new SceneVertex(new(index*.001f,index%11,index%7),Vector3.UnitZ,new(.25f,.75f))).ToArray();
        var root=new SceneSnapshot(vertices,new uint[]{0,1,2},new[]{new SceneMaterial("vertices",0,3,Vector4.One,Vector3.Zero,Vector3.Zero,0)});
        return incremental?Patch(root,count-1,1,0):root.WithDeformedVertices(vertices);
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
        {for(int i=0;i<destination.Length;i++)destination[i]=destination[i] with {Position=destination[i].Position+new Vector3(offset,0,0)};}
    }
    private static void Warm(D3D12GpuRuntime runtime,D3D12GpuCache cache,SceneSnapshot scene)
    {for(int i=0;i<3;i++){runtime.Begin();cache.SceneVertexBuffer(stream,scene);runtime.Submit();}}
    private static ID3D12Resource Copy(D3D12GpuRuntime runtime,ID3D12Resource source,int count)
    {
        int bytes=checked(count*32);
        var read=runtime.Device.CreateCommittedResource(HeapType.Readback,ResourceDescription.Buffer((ulong)bytes),ResourceStates.CopyDest);
        runtime.Commands.ResourceBarrierTransition(source,ResourceStates.VertexAndConstantBuffer,ResourceStates.CopySource);
        runtime.Commands.CopyBufferRegion(read,0,source,0,(ulong)bytes);
        runtime.Commands.ResourceBarrierTransition(source,ResourceStates.CopySource,ResourceStates.VertexAndConstantBuffer);return read;
    }
    private static SceneVertex[] Read(ID3D12Resource resource,int count)
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
        try{for(int y=0;y<output.Height;y++)data.Slice(y*footprint[0].Footprint.RowPitch,output.Width*4).CopyTo(result.AsSpan(y*output.Width*4));}
        finally{read.Unmap(0);}return result;
    }
}
