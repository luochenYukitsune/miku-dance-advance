using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category","Gpu")]
public sealed class D3D12BufferUploadStagingTests
{
    [Fact]
    public void ChangedBufferPayloadsReuseThreeMappedPagesAndKeepEveryQueuedCopyExact()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var reads=new List<(ID3D12Resource Resource,byte[] Expected)>();
        try
        {
            for(int frame=0;frame<12;frame++)
            {
                var context=runtime.Begin();var bytes=Payload(4097,frame);
                var source=cache.FrameBuffer(bytes,ResourceStates.CopySource);
                reads.Add((Copy(context,source,bytes.Length),bytes));runtime.Submit();
                if(frame>=2)
                {
                    Assert.Equal(3,cache.BufferUploadStatistics.PageCreations);
                    Assert.Equal(3,cache.BufferUploadStatistics.PageMappings);
                }
            }
            runtime.WaitForIdle();
            foreach(var read in reads)Assert.Equal(read.Expected,Read(read.Resource,read.Expected.Length));
            Assert.Equal(12,cache.BufferUploadStatistics.Copies);
            Assert.Equal(3,cache.BufferUploadStatistics.RetainedPages);
            Assert.Equal(3L*D3D12BufferUploadStaging.MinimumPageBytes,cache.BufferUploadStatistics.RetainedBytes);
        }
        finally{runtime.Abort();runtime.WaitForIdle();foreach(var read in reads)read.Resource.Dispose();}
    }

    [Fact]
    public void SameListIdentityReplacementAndContentCacheKeepDistinctSourceRanges()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var context=runtime.Begin();var a=Payload(513,1);var b=Payload(997,2);var c=Payload(71,3);
        var first=cache.BufferByIdentity("test-upload-replacement",a,ResourceStates.CopySource,new object());
        var second=cache.BufferByIdentity("test-upload-replacement",b,ResourceStates.CopySource,new object());
        var third=cache.Buffer(c,ResourceStates.CopySource);
        Assert.NotSame(first,second);Assert.Same(third,cache.Buffer(c,ResourceStates.CopySource));
        using var firstRead=Copy(context,first,a.Length);using var secondRead=Copy(context,second,b.Length);
        using var thirdRead=Copy(context,third,c.Length);
        runtime.Wait(runtime.Submit());
        Assert.Equal(a,Read(firstRead,a.Length));Assert.Equal(b,Read(secondRead,b.Length));Assert.Equal(c,Read(thirdRead,c.Length));
        Assert.Equal(3,cache.BufferUploadStatistics.Copies);
        Assert.Equal(1,cache.BufferUploadStatistics.PageCreations);
        Assert.Equal(1,cache.BufferUploadStatistics.PageMappings);
    }

    [Fact]
    public void AbortedFirstAndReusedSlotUploadsResetWithoutRecreatingTheMappedPage()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        runtime.Begin();cache.FrameBuffer(Payload(521,1),ResourceStates.CopySource);runtime.Abort();
        Assert.Equal(0,runtime.Submissions);Assert.Equal(1,cache.BufferUploadStatistics.LivePages);
        AdvanceEmptySlots();
        var context=runtime.Begin();var expected=Payload(521,2);
        var source=cache.FrameBuffer(expected,ResourceStates.CopySource);using var firstRead=Copy(context,source,expected.Length);
        runtime.Wait(runtime.Submit());Assert.Equal(expected,Read(firstRead,expected.Length));
        AdvanceEmptySlots();runtime.Begin();cache.FrameBuffer(Payload(1733,3),ResourceStates.CopySource);runtime.Abort();
        AdvanceEmptySlots();context=runtime.Begin();expected=Payload(67,4);
        source=cache.FrameBuffer(expected,ResourceStates.CopySource);using var recovered=Copy(context,source,expected.Length);
        runtime.Wait(runtime.Submit());Assert.Equal(expected,Read(recovered,expected.Length));
        Assert.Equal(1,cache.BufferUploadStatistics.PageCreations);Assert.Equal(1,cache.BufferUploadStatistics.PageMappings);

        void AdvanceEmptySlots(){for(int i=0;i<2;i++){runtime.Begin();runtime.Submit();}}
    }

    [Fact]
    public void OversizeAndExcessFramePagesRetireWithoutGrowingTheRetainedPool()
    {
        using var runtime=new D3D12GpuRuntime();using var staging=new D3D12BufferUploadStaging(runtime);
        runtime.Begin();staging.Write(new byte[D3D12BufferUploadStaging.MaximumRetainedPageBytes+1]);
        Assert.Equal(0,staging.Statistics.RetainedBytes);Assert.Equal(1,staging.Statistics.LiveTransientPages);
        runtime.Abort();Assert.Equal(0,staging.Statistics.LivePages);
        runtime.Begin();var large=new byte[D3D12BufferUploadStaging.MaximumRetainedPageBytes];
        staging.Write(large);staging.Write(large);staging.Write(large);
        Assert.Equal(D3D12BufferUploadStaging.MaximumRetainedBytesPerSlot,staging.Statistics.RetainedBytes);
        Assert.Equal(1,staging.Statistics.LiveTransientPages);
        runtime.Abort();Assert.Equal(0,staging.Statistics.LiveTransientPages);Assert.Equal(2,staging.Statistics.LivePages);
        staging.Dispose();Assert.Equal(0,staging.Statistics.LivePages);Assert.Equal(0,staging.Statistics.RetainedBytes);
    }

    [Fact]
    public void UnusedExtraPagesAreTrimmedAfterTheSlotReturnsToSmallerUploads()
    {
        using var runtime=new D3D12GpuRuntime();using var staging=new D3D12BufferUploadStaging(runtime);
        var large=new byte[8*1024*1024];
        runtime.Begin();staging.Write(large);staging.Write(large);runtime.Submit();
        Assert.Equal(2,staging.Statistics.RetainedPages);
        AdvanceEmptySlots();runtime.Begin();staging.Write(Payload(513,1));runtime.Submit();
        AdvanceEmptySlots();runtime.Begin();staging.Write(Payload(67,2));
        Assert.Equal(1,staging.Statistics.RetainedPages);Assert.Equal(1,staging.Statistics.LivePages);
        Assert.Equal(2,staging.Statistics.PageMappings);runtime.Abort();

        void AdvanceEmptySlots(){for(int i=0;i<2;i++){runtime.Begin();runtime.Submit();}}
    }

    [Fact]
    public void CacheDisposalKeepsUsedMappedPagesUntilTheirActualFrameRetires()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var context=runtime.Begin();var expected=Payload(1301,7);
        var source=cache.FrameBuffer(expected,ResourceStates.CopySource);using var read=Copy(context,source,expected.Length);
        cache.Dispose();Assert.Equal(0,cache.BufferUploadStatistics.RetainedPages);
        Assert.Equal(1,cache.BufferUploadStatistics.LivePages);
        runtime.Submit();runtime.WaitForIdle();Assert.Equal(0,cache.BufferUploadStatistics.LivePages);
        Assert.Equal(expected,Read(read,expected.Length));
    }

    [Fact]
    public void HeldOutputSurvivesChangedGeometryResizeSmaaAndUploadSlotReuse()
    {
        var initial=Frame(0);using var oracle=new D3D12FrameSession();var expected=oracle.Capture(initial,97,73).Rgba;
        using var session=new D3D12FrameSession();using var held=session.Render(initial,97,73);
        for(int i=0;i<12;i++)
        {
            using var changed=session.Render(Frame(i%2==0?.35f:-.35f),i%2==0?129:65,i%2==0?91:49);
            Assert.Equal(0,changed.Statistics.ReadbackCount);
        }
        session.WaitForIdle();Assert.Equal(expected,ReadOutput(session.Runtime,held));
    }

    private static OriginalFrameScene Frame(float offset)
    {
        var box=D3D12OriginalOutlineTests.Box();var source=box.Surface.Snapshot;
        var vertices=source.Vertices.ToArray();
        for(int i=0;i<vertices.Length;i++)vertices[i]=vertices[i] with {Position=vertices[i].Position+new Vector3(offset,0,0)};
        var scene=source.WithDeformedVertices(vertices);
        return new(new(scene,box.Surface.Materials,box.Surface.Profile),options:D3D12OriginalFrameTests.Options() with
            {Antialiasing=FrameAntialiasing.Smaa1X,SpatialSamples=4});
    }

    private static byte[] Payload(int length,int seed)
        =>Enumerable.Range(0,length).Select(i=>(byte)((i*37+seed*53)%251)).ToArray();
    private static ID3D12Resource Copy(D3D12RecordingContext context,ID3D12Resource source,int length)
    {
        var read=context.Device.CreateCommittedResource(HeapType.Readback,ResourceDescription.Buffer((ulong)length),ResourceStates.CopyDest);
        context.Commands.CopyBufferRegion(read,0,source,0,(ulong)length);return read;
    }
    private static byte[] Read(ID3D12Resource resource,int length)
    {var data=resource.Map<byte>(0,length);try{return data.ToArray();}finally{resource.Unmap(0);}}
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
