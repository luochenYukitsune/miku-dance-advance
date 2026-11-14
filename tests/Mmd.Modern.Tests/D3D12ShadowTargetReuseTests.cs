using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category","Gpu")]
public sealed class D3D12ShadowTargetReuseTests
{
    [Theory]
    [InlineData(Format.D24_UNorm_S8_UInt)]
    [InlineData(Format.D32_Float)]
    public void ThreeFlightSlotsReuseTheirTargetsAndPreserveDistinctGpuWrites(Format depthFormat)
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var slots=new D3D12ShadowTargets?[3];
        var reads=new List<(ID3D12Resource Resource,int Pitch,float Expected)>();
        try
        {
            for(int frame=0;frame<12;frame++)
            {
                var context=runtime.Begin();
                var target=D3D12ShadowTargets.Get(context,cache,"test-shadow",8,4,depthFormat);
                if(frame<3)slots[runtime.SlotIndex]=target;
                else Assert.Same(slots[runtime.SlotIndex],target);
                Assert.Equal(depthFormat,target.Depth.Description.Format);
                float value=(frame+1)/16f;
                target.Begin(context);context.Commands.ClearRenderTargetView(target.Rtv,new Color4(value,value,value,value));target.End(context);
                var read=Copy(context,target.Color);reads.Add((read.Resource,read.Pitch,value));
                runtime.Submit();
                Assert.Equal(Math.Min(frame+1,3),cache.LiveResourceCount);
                Assert.InRange(runtime.PendingFrames,0,3);
            }
            runtime.WaitForIdle();
            Assert.Equal(3,slots.Distinct().Count());
            foreach(var read in reads)AssertPixels(read.Resource,read.Pitch,8,4,read.Expected);
        }
        finally{runtime.Abort();runtime.WaitForIdle();foreach(var read in reads)read.Resource.Dispose();}
    }

    [Fact]
    public void SameFrameResizeKeepsEarlierGpuReadersAndBoundsCacheEntries()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var context=runtime.Begin();
        var old=D3D12ShadowTargets.Get(context,cache,"test-resize",8,4,Format.D24_UNorm_S8_UInt);
        old.Begin(context);context.Commands.ClearRenderTargetView(old.Rtv,new Color4(.25f,.25f,.25f,.25f));old.End(context);
        var replacement=D3D12ShadowTargets.Get(context,cache,"test-resize",16,8,Format.D24_UNorm_S8_UInt);
        Assert.NotSame(old,replacement);Assert.Equal(1,cache.LiveResourceCount);
        replacement.Begin(context);context.Commands.ClearRenderTargetView(replacement.Rtv,new Color4(.75f,.75f,.75f,.75f));replacement.End(context);
        var first=Copy(context,old.Color);using var firstRead=first.Resource;
        var second=Copy(context,replacement.Color);using var secondRead=second.Resource;
        runtime.Wait(runtime.Submit());
        AssertPixels(firstRead,first.Pitch,8,4,.25f);AssertPixels(secondRead,second.Pitch,16,8,.75f);
    }

    [Fact]
    public void DisableAndShrinkRetireEverySlotOwnerWhileSubmittedCopiesRemainValid()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var reads=new List<(ID3D12Resource Resource,int Pitch)>();
        try
        {
            for(int i=0;i<3;i++)
            {
                var context=runtime.Begin();
                foreach(string stream in new[]{"original-primary-shadow","original-native-shadow"})
                {
                    var target=D3D12ShadowTargets.Get(context,cache,stream,32,16,Format.D32_Float);
                    target.Begin(context);target.End(context);reads.Add(Copy(context,target.Color));
                }
                runtime.Submit();
            }
            Assert.Equal(6,cache.LiveResourceCount);
            var shrinking=runtime.Begin();
            var smaller=D3D12ShadowTargets.Get(shrinking,cache,"original-primary-shadow",8,4,Format.D24_UNorm_S8_UInt);
            Assert.Equal(4,cache.LiveResourceCount); // Three stale owners retired, one smaller owner installed.
            smaller.Begin(shrinking);smaller.End(shrinking);
            var empty=new OriginalFrameScene(new OriginalStyleScene(SceneSnapshot.CreateEmpty(),[]));
            D3D12ShadowTargets.ReleaseInactive(cache,empty);Assert.Equal(0,cache.LiveResourceCount);
            runtime.Wait(runtime.Submit());runtime.WaitForIdle();
            foreach(var read in reads)AssertPixels(read.Resource,read.Pitch,32,16,1);
        }
        finally{runtime.Abort();runtime.WaitForIdle();foreach(var read in reads)read.Resource.Dispose();}
    }

    [Fact]
    public void AbortOfNewAndReusedTargetsPreservesCommittedStatesAndWhiteInitialization()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var context=runtime.Begin();
        var initial=D3D12ShadowTargets.Get(context,cache,"test-abort",8,4,Format.D24_UNorm_S8_UInt);
        var abortedWhite=D3D12ShadowTargets.White(context,cache);
        initial.Begin(context);runtime.Abort();Assert.Equal(0,cache.LiveResourceCount);
        D3D12ShadowTargets? committed=null;ID3D12Resource? white=null;
        for(int frame=0;frame<3;frame++)
        {
            context=runtime.Begin();
            var target=D3D12ShadowTargets.Get(context,cache,"test-abort",8,4,Format.D24_UNorm_S8_UInt);
            white=D3D12ShadowTargets.White(context,cache);Assert.NotSame(abortedWhite,white);
            target.Begin(context);context.Commands.ClearRenderTargetView(target.Rtv,new Color4(.25f,.25f,.25f,.25f));target.End(context);
            if(frame==0)committed=target;
            runtime.Submit();
        }
        context=runtime.Begin();
        var reused=D3D12ShadowTargets.Get(context,cache,"test-abort",8,4,Format.D24_UNorm_S8_UInt);
        Assert.Same(committed,reused);reused.Begin(context);
        context.Commands.ClearRenderTargetView(reused.Rtv,new Color4(.875f,.875f,.875f,.875f));runtime.Abort();
        for(int i=0;i<2;i++){runtime.Begin();runtime.Submit();}
        context=runtime.Begin();
        Assert.Same(reused,D3D12ShadowTargets.Get(context,cache,"test-abort",8,4,Format.D24_UNorm_S8_UInt));
        Assert.Same(white,D3D12ShadowTargets.White(context,cache));
        var before=Copy(context,reused.Color);using var beforeRead=before.Resource;
        var whiteCopy=Copy(context,white!);using var whiteRead=whiteCopy.Resource;
        reused.Begin(context);context.Commands.ClearRenderTargetView(reused.Rtv,new Color4(.5f,.5f,.5f,.5f));reused.End(context);
        var after=Copy(context,reused.Color);using var afterRead=after.Resource;
        runtime.Wait(runtime.Submit());
        AssertPixels(beforeRead,before.Pitch,8,4,.25f);AssertPixels(afterRead,after.Pitch,8,4,.5f);
        AssertPixels(whiteRead,whiteCopy.Pitch,1,1,1);Assert.Equal(4,cache.LiveResourceCount);
    }

    [Fact]
    public void PrimaryAndNativeShadowResizeAndSsaaPreserveCapturesAndHeldOutputLeases()
    {
        var source=D3D12OriginalFrameTests.Shadow(true);
        var light=new NativeSceneLight(Guid.NewGuid(),"reuse point",SceneLightKind.Point,new(0,0,-2),
            Quaternion.Identity,Vector3.One,2,8);
        var frames=Enumerable.Range(0,3).Select(i=>new OriginalFrameScene(source.Surface,options:source.Options! with {
            SelfShadow=source.Options!.SelfShadow! with {Width=64+i*16,Height=64+i*8,Generate=i!=0},SpatialSamples=i==2?4:1},
            materials:source.Materials,lighting:i==0?null:new SceneLightingSnapshot(i==1?[light]:[light,light with {Id=Guid.NewGuid(),CastShadows=false}]))).ToArray();
        var expected=new byte[3][];
        for(int i=0;i<frames.Length;i++)
        {using var fresh=new D3D12FrameSession();expected[i]=fresh.Capture(frames[i],97+i*8,73+i*8).Rgba;}
        using var session=new D3D12FrameSession();
        var held=new List<(D3D12FrameOutput Output,int Variant)>();
        try
        {
            for(int i=0;i<12;i++)
            {
                int variant=(i/3+i)%3; // Each flight slot changes shadow extent/light count after the first cycle.
                var output=session.Render(frames[variant],97+variant*8,73+variant*8);held.Add((output,variant));
                Assert.Equal(0,output.Statistics.ReadbackCount);Assert.Equal(1,output.Statistics.SubmissionCount);
            }
            for(int i=0;i<frames.Length;i++)Assert.Equal(expected[i],session.Capture(frames[i],97+i*8,73+i*8).Rgba);
            foreach(var item in held)Assert.Equal(expected[item.Variant],ReadOutput(session.Runtime,item.Output));
        }
        finally{session.WaitForIdle();foreach(var item in held)item.Output.Dispose();}
    }

    private static byte[] ReadOutput(D3D12GpuRuntime runtime,D3D12FrameOutput output)
    {
        var context=runtime.Begin();var layouts=new PlacedSubresourceFootPrint[1];var rows=new int[1];var rowBytes=new ulong[1];
        context.Device.GetCopyableFootprints(output.Resource.Description,0,1,0,layouts,rows,rowBytes,out ulong size);
        using var readback=context.Device.CreateCommittedResource(HeapType.Readback,ResourceDescription.Buffer(size),ResourceStates.CopyDest);
        context.Commands.CopyTextureRegion(new TextureCopyLocation(readback,layouts[0]),0,0,0,new TextureCopyLocation(output.Resource,0));
        runtime.Wait(runtime.Submit());var pixels=new byte[output.Width*output.Height*4];var data=readback.Map<byte>(0,checked((int)size));
        try{for(int y=0;y<output.Height;y++)data.Slice(y*layouts[0].Footprint.RowPitch,output.Width*4).CopyTo(pixels.AsSpan(y*output.Width*4));}
        finally{readback.Unmap(0);}return pixels;
    }

    private static (ID3D12Resource Resource,int Pitch) Copy(D3D12RecordingContext context,ID3D12Resource source)
    {
        var layouts=new PlacedSubresourceFootPrint[1];var rows=new int[1];var rowBytes=new ulong[1];
        context.Device.GetCopyableFootprints(source.Description,0,1,0,layouts,rows,rowBytes,out ulong bytes);
        var readback=context.Device.CreateCommittedResource(HeapType.Readback,ResourceDescription.Buffer(Math.Max(bytes,(ulong)layouts[0].Footprint.RowPitch*(ulong)rows[0])),ResourceStates.CopyDest);
        context.Commands.ResourceBarrierTransition(source,ResourceStates.PixelShaderResource,ResourceStates.CopySource);
        context.Commands.CopyTextureRegion(new TextureCopyLocation(readback,layouts[0]),0,0,0,new TextureCopyLocation(source,0));
        context.Commands.ResourceBarrierTransition(source,ResourceStates.CopySource,ResourceStates.PixelShaderResource);
        return(readback,layouts[0].Footprint.RowPitch);
    }
    private static void AssertPixels(ID3D12Resource resource,int pitch,int width,int height,float expected)
    {
        var bytes=resource.Map<byte>(0,pitch*height);
        try{for(int y=0;y<height;y++)for(int x=0;x<width;x++)Assert.Equal(expected,BitConverter.ToSingle(bytes.Slice(y*pitch+x*4,4)));}
        finally{resource.Unmap(0);}
    }
}
