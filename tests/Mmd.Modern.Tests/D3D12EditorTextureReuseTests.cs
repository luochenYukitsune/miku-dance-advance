using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category","Gpu")]
public sealed class D3D12EditorTextureReuseTests
{
    [Theory]
    [InlineData(0,false)]
    [InlineData(128,false)]
    [InlineData(255,false)]
    [InlineData(0,true)]
    [InlineData(128,true)]
    [InlineData(255,true)]
    public void BorrowedNumericalTexturesKeepDiffuseAndUv1AlphaAndSrvSlotsExact(int alpha,bool subTexture)
    {
        var frame=Fixture(alpha,subTexture);
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        using var renderer=new D3D12OriginalStyleRenderer(runtime);
        using var packed=new D3D12TemporalInputPass(runtime);using var shared=new D3D12TemporalInputPass(runtime);
        var context=runtime.Begin();
        try
        {
            var geometry=Borrow(context,renderer,cache,frame);
            Assert.Same(cache,geometry.Cache);
            long before=runtime.TextureUploads;
            using var optimized=Copy(context,shared.RecordEditorVisibility(context,frame,64,64,sharedGeometry:geometry));
            Assert.Equal(before,runtime.TextureUploads);
            using var original=Copy(context,packed.RecordEditorVisibility(context,frame,64,64));
            runtime.Wait(runtime.Submit());
            var a=Read(original);var b=Read(optimized);
            Assert.Equal(a,b);
            Assert.Equal(alpha==0,b.All(v=>v==0x3f800000u));
        }
        finally{runtime.Abort();}
    }

    [Fact]
    public void SharedTextureOwnersSurviveAbortReplacementAndThreeFlightSlots()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        using var renderer=new D3D12OriginalStyleRenderer(runtime);using var pass=new D3D12TemporalInputPass(runtime);
        var reads=new List<(DepthReadback Read,bool Empty)>();
        try
        {
            var aborted=runtime.Begin();var abortedFrame=Fixture(0,true);
            var abortedGeometry=Borrow(aborted,renderer,cache,abortedFrame);
            pass.RecordEditorVisibility(aborted,abortedFrame,64,64,sharedGeometry:abortedGeometry);
            runtime.Abort();
            for(int frame=0;frame<9;frame++)
            {
                bool empty=frame%2==0;var input=Fixture(empty?0:255,true);
                var context=runtime.Begin();var geometry=Borrow(context,renderer,cache,input);
                Assert.Same(cache,geometry.Cache);
                long before=runtime.TextureUploads;
                var visibility=pass.RecordEditorVisibility(context,input,64,64,sharedGeometry:geometry);
                Assert.Equal(before,runtime.TextureUploads);
                reads.Add((Copy(context,visibility),empty));
                runtime.Submit();Assert.InRange(runtime.PendingFrames,0,3);
            }
            runtime.WaitForIdle();
            foreach(var item in reads)Assert.Equal(item.Empty,Read(item.Read).All(v=>v==0x3f800000u));
        }
        finally{runtime.Abort();runtime.WaitForIdle();foreach(var item in reads)item.Read.Dispose();}
    }

    [Fact]
    public void EnablingWorldGuidesAddsNoSecondTextureUploadsToPrimaryFrameOwner()
    {
        var frame=Fixture(255,true);
        using var session=new D3D12FrameSession();
        using var initial=session.Render(frame,128,96);
        long uploads=initial.Statistics.TextureUploads;
        var overlay=new ViewportOverlaySnapshot([new(new(.1f,.5f),new(.9f,.5f),new(1,0,1,1),2,DeviceDepth:new(.5f))]);
        var editing=new OriginalFrameScene(frame.Surface,frame.Outline,frame.OutlineOptions,frame.Options,frame.Materials,frame.Lighting,overlay);
        using var shown=session.Render(editing,128,96);
        Assert.Equal(uploads,shown.Statistics.TextureUploads);
        Assert.Contains(session.LastRecordings,r=>r.Channel=="editor:world-guide-depth");
    }

    private static OriginalFrameScene Fixture(int alpha,bool subTexture)
    {
        var source=D3D12SubTextureTemporalCoverageTests.Fixture(new(.75f,.25f));
        var scene=source.Surface.Snapshot;
        var textures=scene.Textures.ToArray();
        textures[0]=new("different diffuse slot",1,1,[32,192,224,(byte)(subTexture?255:alpha)]);
        textures[1]=new("different sphere UV1 slot",2,1,[224,32,96,255,16,224,32,(byte)alpha]);
        var mesh=new SceneSnapshot(scene.Vertices,scene.Indices,scene.Materials,textures,scene.World,scene.Camera,scene.LightDirection,additionalUvs:scene.AdditionalUvs);
        var binding=source.Surface.Materials[0] with {SphereMode=subTexture?OriginalSphereMode.SubTexture:OriginalSphereMode.None,
            SphereTexture=subTexture?1:-1};
        return D3D12SubTextureTemporalCoverageTests.Copy(source,new(mesh,[binding],source.Surface.Profile));
    }

    private static D3D12EditorGeometry Borrow(D3D12RecordingContext context,D3D12OriginalStyleRenderer renderer,
        D3D12GpuCache cache,OriginalFrameScene frame)
    {
        var scene=frame.Surface.Snapshot;
        cache.Texture(new("primary-white",1,1,[255,255,255,255]));
        foreach(var texture in scene.Textures)cache.Texture(texture);
        var vb=cache.BufferByIdentity("test-source",MemoryMarshal.AsBytes(scene.Vertices),ResourceStates.VertexAndConstantBuffer,new D3D12VertexStreamIdentity(scene));
        return renderer.BorrowEditorGeometry(context,frame,vb,cache.IndexBuffer(scene),cache);
    }

    private sealed record DepthReadback(ID3D12Resource Buffer,PlacedSubresourceFootPrint Layout,int Bytes):IDisposable
    {public void Dispose()=>Buffer.Dispose();}

    private static DepthReadback Copy(D3D12RecordingContext context,D3D12GpuTarget target)
    {
        var layout=new PlacedSubresourceFootPrint[1];
        context.Device.GetCopyableFootprints(target.Resource.Description,0,1,0,layout,new int[1],new ulong[1],out ulong bytes);
        var buffer=context.Device.CreateCommittedResource(HeapType.Readback,ResourceDescription.Buffer(bytes),ResourceStates.CopyDest);
        context.Commands.ResourceBarrierTransition(target.Resource,ResourceStates.DepthWrite,ResourceStates.CopySource);
        context.Commands.CopyTextureRegion(new TextureCopyLocation(buffer,layout[0]),0,0,0,new TextureCopyLocation(target.Resource,0));
        return new(buffer,layout[0],checked((int)bytes));
    }

    private static uint[] Read(DepthReadback source)
    {
        var output=new uint[64*64];var data=source.Buffer.Map<byte>(0,source.Bytes);
        try{for(int y=0;y<64;y++)MemoryMarshal.Cast<byte,uint>(data.Slice(y*source.Layout.Footprint.RowPitch,64*4)).CopyTo(output.AsSpan(y*64,64));}
        finally{source.Buffer.Unmap(0);}return output;
    }
}
