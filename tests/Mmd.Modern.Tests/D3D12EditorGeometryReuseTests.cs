using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12EditorGeometryReuseTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SharedSurfaceAndOutlineStreamsMatchPackedVisibilityBitForBit(bool modern, bool ground)
    {
        var source=D3D12OriginalOutlineTests.Box();
        var options=D3D12OriginalFrameTests.Options() with { EnableFrustumCulling=true,
            GroundShadow=ground?new(new(.3f,-1,.2f),new(0,0,0,.5f),false,false):null };
        var frame=new OriginalFrameScene(source.Surface,source.Outline,source.OutlineOptions,options,
            source.Materials.Select(f=>modern?f with {CharacterOutlineStyle=new(2,new(1,0,0,.5f))}:f));
        Compare(frame);
    }

    [Theory]
    [InlineData(.25f)]
    [InlineData(.75f)]
    public void SharedUv1SlotPreservesSubtextureZeroAlphaCoverage(float u)
    {
        var source=D3D12SubTextureTemporalCoverageTests.Fixture(new(u,.5f));
        var s=source.Surface.Snapshot;var textures=s.Textures.ToArray();
        textures[1]=new("UV1 alpha",2,1,[255,255,255,0,255,255,255,255]);
        var scene=new SceneSnapshot(s.Vertices,s.Indices,s.Materials,textures,s.World,s.Camera,s.LightDirection,additionalUvs:s.AdditionalUvs);
        Compare(D3D12SubTextureTemporalCoverageTests.Copy(source,new(scene,source.Surface.Materials,source.Surface.Profile)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedGeometrySurvivesThreeFlightSlotsAndHeldSmaaOutputs(bool modern)
    {
        var box=D3D12OriginalOutlineTests.Box();
        var frame=new OriginalFrameScene(box.Surface,box.Outline,box.OutlineOptions,
            D3D12OriginalFrameTests.Options() with {Antialiasing=FrameAntialiasing.Smaa1X,SpatialSamples=4,EnableFrustumCulling=true},
            box.Materials.Select(f=>modern?f with {CharacterOutlineStyle=new(2,new(1,0,0,.5f))}:f),
            editorOverlay:new([new(new(.1f,.5f),new(.9f,.5f),new(1,0,1,1),2,DeviceDepth:new(.99f))]));
        byte[] expected;
        using(var independent=new D3D12FrameSession())
        using(var output=independent.Render(frame,128,96))expected=ReadOutput(independent.Runtime,output);
        using var session=new D3D12FrameSession();
        using var held=session.Render(frame,128,96);
        var scene=frame.Surface.Snapshot;
        for(int i=1;i<=7;i++)
        {
            var delta=new Vector3(i*.1f,0,0);
            var movedScene=new SceneSnapshot(scene.Vertices.ToArray().Select(v=>v with {Position=v.Position+delta}).ToArray(),
                scene.Indices,scene.Materials,scene.Textures,scene.World,scene.Camera,scene.LightDirection);
            var moved=new OriginalFrameScene(new(movedScene,frame.Surface.Materials,frame.Surface.Profile),
                new(frame.Outline!.Vertices.Select(v=>v with {Position=v.Position+delta}),frame.Outline.Materials),
                frame.OutlineOptions,frame.Options,frame.Materials,frame.Lighting,frame.EditorOverlay);
            using var output=session.Render(moved,128,96);
            Assert.Equal(0,output.Statistics.ReadbackCount);
            Assert.InRange(output.Statistics.PendingFrames,0,3);
        }
        Assert.Equal(expected,ReadOutput(session.Runtime,held));
    }

    [Fact]
    public void AbortedSharedStreamsAndSameFrameReplacementsKeepEarlierReadersValid()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        using var renderer=new D3D12OriginalStyleRenderer(runtime);using var pass=new D3D12TemporalInputPass(runtime);
        var source=D3D12OriginalOutlineTests.Box();
        var frame=new OriginalFrameScene(source.Surface,source.Outline,source.OutlineOptions,D3D12OriginalFrameTests.Options());
        var s=frame.Surface.Snapshot;
        var movedScene=new SceneSnapshot(s.Vertices.ToArray().Select(v=>v with {Position=v.Position+new Vector3(.3f,0,0)}).ToArray(),
            s.Indices,s.Materials,s.Textures,s.World,s.Camera,s.LightDirection);
        var moved=new OriginalFrameScene(new(movedScene,frame.Surface.Materials,frame.Surface.Profile),
            new(frame.Outline!.Vertices.Select(v=>v with {Position=v.Position+new Vector3(.3f,0,0)}),frame.Outline.Materials),
            frame.OutlineOptions,frame.Options);
        var context=runtime.Begin();
        var aborted=Borrow(context,frame);
        pass.RecordEditorVisibility(context,frame,64,64,sharedGeometry:aborted);
        runtime.Abort();
        context=runtime.Begin();
        var original=Borrow(context,frame);var first=Copy(context,pass.RecordEditorVisibility(context,frame,64,64,sharedGeometry:original));
        var movedGeometry=Borrow(context,moved);var second=Copy(context,pass.RecordEditorVisibility(context,moved,64,64,sharedGeometry:movedGeometry));
        runtime.Wait(runtime.Submit());
        Assert.NotEqual(Read(first),Read(second));
        Compare(frame);Compare(moved);

        D3D12EditorGeometry Borrow(D3D12RecordingContext recording,OriginalFrameScene input)
        {
            var scene=input.Surface.Snapshot;
            var vb=cache.BufferByIdentity("test-surface",MemoryMarshal.AsBytes(scene.Vertices),ResourceStates.VertexAndConstantBuffer,new D3D12VertexStreamIdentity(scene));
            var ib=cache.IndexBuffer(scene);
            return renderer.BorrowEditorGeometry(recording,input,vb,ib,cache);
        }
    }

    private static void Compare(OriginalFrameScene frame)
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        using var renderer=new D3D12OriginalStyleRenderer(runtime);
        using var packed=new D3D12TemporalInputPass(runtime);using var shared=new D3D12TemporalInputPass(runtime);
        var context=runtime.Begin();
        try
        {
            var scene=frame.Surface.Snapshot;
            var vb=cache.BufferByIdentity("test-surface",MemoryMarshal.AsBytes(scene.Vertices),ResourceStates.VertexAndConstantBuffer,new D3D12VertexStreamIdentity(scene));
            var ib=cache.IndexBuffer(scene);
            var geometry=renderer.BorrowEditorGeometry(context,frame,vb,ib,cache);
            long before=runtime.BufferUploads;
            var optimized=shared.RecordEditorVisibility(context,frame,64,64,sharedGeometry:geometry);
            Assert.Equal(before,runtime.BufferUploads);
            var optimizedRead=Copy(context,optimized);
            var baselineRead=Copy(context,packed.RecordEditorVisibility(context,frame,64,64));
            runtime.Wait(runtime.Submit());
            Assert.Equal(Read(baselineRead),Read(optimizedRead));
        }
        finally{runtime.Abort();}
    }

    private static (ID3D12Resource Resource,PlacedSubresourceFootPrint Layout,int Bytes) Copy(D3D12RecordingContext context,D3D12GpuTarget target)
    {
        var layout=new PlacedSubresourceFootPrint[1];
        context.Device.GetCopyableFootprints(target.Resource.Description,0,1,0,layout,new int[1],new ulong[1],out ulong bytes);
        var buffer=context.Own(context.Device.CreateCommittedResource(HeapType.Readback,ResourceDescription.Buffer(bytes),ResourceStates.CopyDest));
        context.Commands.ResourceBarrierTransition(target.Resource,ResourceStates.DepthWrite,ResourceStates.CopySource);
        context.Commands.CopyTextureRegion(new TextureCopyLocation(buffer,layout[0]),0,0,0,new TextureCopyLocation(target.Resource,0));
        return(buffer,layout[0],checked((int)bytes));
    }
    private static uint[] Read((ID3D12Resource Resource,PlacedSubresourceFootPrint Layout,int Bytes) source)
    {
        var result=new uint[64*64];var data=source.Resource.Map<byte>(0,source.Bytes);
        try{for(int y=0;y<64;y++)MemoryMarshal.Cast<byte,uint>(data.Slice(y*source.Layout.Footprint.RowPitch,64*4)).CopyTo(result.AsSpan(y*64,64));}
        finally{source.Resource.Unmap(0);}return result;
    }

    private static byte[] ReadOutput(D3D12GpuRuntime runtime,D3D12FrameOutput output)
    {
        var context=runtime.Begin();var layout=new PlacedSubresourceFootPrint[1];
        try
        {
            context.Device.GetCopyableFootprints(output.Resource.Description,0,1,0,layout,new int[1],new ulong[1],out ulong bytes);
            var buffer=context.Own(context.Device.CreateCommittedResource(HeapType.Readback,ResourceDescription.Buffer(bytes),ResourceStates.CopyDest));
            context.Commands.CopyTextureRegion(new TextureCopyLocation(buffer,layout[0]),0,0,0,new TextureCopyLocation(output.Resource,0));
            runtime.Wait(runtime.Submit());
            var rgba=new byte[output.Width*output.Height*4];var data=buffer.Map<byte>(0,checked((int)bytes));
            try{for(int y=0;y<output.Height;y++)data.Slice(y*layout[0].Footprint.RowPitch,output.Width*4).CopyTo(rgba.AsSpan(y*output.Width*4,output.Width*4));}
            finally{buffer.Unmap(0);}return rgba;
        }
        finally{runtime.Abort();}
    }
}
