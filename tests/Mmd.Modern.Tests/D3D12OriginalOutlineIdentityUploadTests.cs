using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category","Gpu")]
public sealed class D3D12OriginalOutlineIdentityUploadTests
{
    [Theory]
    [InlineData("pose")]
    [InlineData("width")]
    [InlineData("camera")]
    public void ChangedImmutableHullMatchesFreshFrameExactly(string change)
    {
        var frame=Frame();var changed=Change(frame,change);
        using var session=new D3D12FrameSession();
        var before=session.Capture(frame,128,96).Rgba;
        long uploads=session.Statistics!.BufferUploads;
        var actual=session.Capture(changed,128,96).Rgba;
        using var independent=new D3D12FrameSession();
        Assert.Equal(independent.Capture(changed,128,96).Rgba,actual);
        Assert.NotEqual(before,actual);
        Assert.True(session.Statistics!.BufferUploads>uploads);
        uploads=session.Statistics.BufferUploads;
        for(int i=0;i<5;i++)
        {
            Assert.Equal(actual,session.Capture(changed,128,96).Rgba);
            Assert.Equal(uploads,session.Statistics!.BufferUploads);
        }
    }

    [Fact]
    public void OriginalHullAndGuideBorrowUploadOnceAcrossThreeSlotsAndKeepHeldOutput()
    {
        var source=Frame();
        var frame=new OriginalFrameScene(source.Surface,source.Outline,source.OutlineOptions,source.Options,source.Materials,
            editorOverlay:new([new(new(.05f,.1f),new(.95f,.1f),new(1,0,1,1),2,DeviceDepth:new(.99f))]));
        byte[] expected;
        using(var independent=new D3D12FrameSession())
        using(var output=independent.Render(frame,128,96))expected=ReadOutput(independent.Runtime,output);
        using var session=new D3D12FrameSession();using var held=session.Render(frame,128,96);
        // One surface, one topology and one immutable original position stream.
        // The separate guide replay must borrow the same original GPU resource.
        Assert.Equal(3,held.Statistics.BufferUploads);
        for(int i=0;i<9;i++)
        {
            using var output=session.Render(frame,128,96);
            Assert.Equal(3,output.Statistics.BufferUploads);
            Assert.Equal(0,output.Statistics.ReadbackCount);
            Assert.InRange(output.Statistics.PendingFrames,0,3);
        }
        Assert.Equal(expected,ReadOutput(session.Runtime,held));
    }

    [Fact]
    public void AbortedFirstCreationAndReplacementRecoverWithoutInvalidatingHeldReaders()
    {
        var first=Frame();var changed=Change(first,"pose");
        byte[] firstExpected,changedExpected;
        using(var independent=new D3D12FrameSession())
        {
            firstExpected=independent.Capture(first,128,96).Rgba;
            changedExpected=independent.Capture(changed,128,96).Rgba;
        }
        using var session=new D3D12FrameSession();
        Abort(first);
        using var held=session.Render(first,128,96);
        Abort(changed);
        for(int i=0;i<7;i++)
        {
            using var output=session.Render(changed,128,96);
            Assert.Equal(changedExpected,ReadOutput(session.Runtime,output));
        }
        Assert.Equal(firstExpected,ReadOutput(session.Runtime,held));

        void Abort(OriginalFrameScene input)
            =>Assert.Throws<InvalidOperationException>(()=>
            {
                using var unused=session.RenderForPresent(input,128,96,null,(_,_)=>throw new InvalidOperationException("abort original hull"));
            });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void AlternatingOriginalAndModernRunsWithGroundAndSmaaMatchFreshSsaaOracle(int samples)
    {
        var frame=Mixed(samples);
        using var session=new D3D12FrameSession();using var held=session.Render(frame,128,96);
        var recorded=new List<(OriginalFrameScene Frame,D3D12FrameOutput Output)>();
        try
        {
            // Queue replacements through all flight slots before any diagnostic wait.
            for(int i=1;i<=7;i++)
            {
                var changed=Move(frame,new(i*.07f,-i*.02f,0));
                var output=session.Render(changed,128,96);recorded.Add((changed,output));
                Assert.InRange(output.Statistics.PendingFrames,0,3);
                Assert.Equal(0,output.Statistics.ReadbackCount);
            }
            foreach(var item in recorded)
            {
                using var independent=new D3D12FrameSession();
                Assert.Equal(independent.Capture(item.Frame,128,96).Rgba,ReadOutput(session.Runtime,item.Output));
            }
        }
        finally{foreach(var item in recorded)item.Output.Dispose();}
        using var originalOracle=new D3D12FrameSession();
        Assert.Equal(originalOracle.Capture(frame,128,96).Rgba,ReadOutput(session.Runtime,held));
    }

    [Fact]
    public void BorrowedOriginalGpuBytesEqualFullScalarExpansionAndSurviveSameListReplacement()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        using var renderer=new D3D12OriginalStyleRenderer(runtime);
        try
        {
        var frame=Frame();var changed=Change(frame,"pose");var context=runtime.Begin();
        var first=Borrow(frame);long uploads=runtime.BufferUploads;
        var firstPositions=Assert.IsType<D3D12ExpandedPositions>(first.OriginalPositions);
        Assert.Same(first.OriginalOutline,cache.ExpandedPositionBuffer("original-edge",firstPositions));
        Assert.False(firstPositions.IsMaterialized);
        Assert.Equal(uploads,runtime.BufferUploads);
        using var oldRead=Copy(first.OriginalOutline!,firstPositions.Length*16);
        var second=Borrow(changed);
        var secondPositions=Assert.IsType<D3D12ExpandedPositions>(second.OriginalPositions);
        Assert.False(secondPositions.IsMaterialized);
        Assert.NotSame(first.OriginalOutline,second.OriginalOutline);
        using var newRead=Copy(second.OriginalOutline!,secondPositions.Length*16);
        runtime.Wait(runtime.Submit());
        Assert.Equal(ScalarBytes(frame),ReadBuffer(oldRead,firstPositions.Length*16));
        Assert.Equal(ScalarBytes(changed),ReadBuffer(newRead,secondPositions.Length*16));

        D3D12EditorGeometry Borrow(OriginalFrameScene input)
        {
            var scene=input.Surface.Snapshot;
            var surface=cache.BufferByIdentity("test-surface",MemoryMarshal.AsBytes(scene.Vertices),ResourceStates.VertexAndConstantBuffer,new D3D12VertexStreamIdentity(scene));
            return renderer.BorrowEditorGeometry(context,input,surface,cache.IndexBuffer(scene),cache);
        }
        ID3D12Resource Copy(ID3D12Resource source,int bytes)
        {
            var readback=runtime.Device.CreateCommittedResource(HeapType.Readback,ResourceDescription.Buffer((ulong)bytes),ResourceStates.CopyDest);
            context.Commands.ResourceBarrierTransition(source,ResourceStates.VertexAndConstantBuffer,ResourceStates.CopySource);
            context.Commands.CopyBufferRegion(readback,0,source,0,(ulong)bytes);
            context.Commands.ResourceBarrierTransition(source,ResourceStates.CopySource,ResourceStates.VertexAndConstantBuffer);
            return readback;
        }
        }
        finally {runtime.Abort();}
    }

    private static byte[] ScalarBytes(OriginalFrameScene frame)
        =>MemoryMarshal.AsBytes(OriginalOutlineMath.BuildPositions(frame.Outline!,frame.OutlineOptions!).AsSpan()).ToArray();
    private static byte[] ReadBuffer(ID3D12Resource resource,int bytes)
    {
        var data=resource.Map<byte>(0,bytes);
        try{return data.ToArray();}finally{resource.Unmap(0);}
    }
    private static OriginalFrameScene Frame()
    {
        var box=D3D12OriginalOutlineTests.Box();
        return new(box.Surface,box.Outline,box.OutlineOptions,D3D12OriginalFrameTests.Options() with {EnableFrustumCulling=true},box.Materials);
    }
    private static OriginalFrameScene Change(OriginalFrameScene frame,string change)
    {
        if(change=="pose")return Move(frame,new(.25f,.1f,0));
        if(change=="width")return new(frame.Surface,new(frame.Outline!.Vertices,
            frame.Outline.Materials.Select(m=>m with {Width=m.Width+1.5f})),frame.OutlineOptions,frame.Options,frame.Materials);
        var camera=frame.Surface.Snapshot.Camera with {Eye=new(1,0,-7),Target=new(1,0,0)};
        return new(frame.Surface,frame.Outline,frame.OutlineOptions! with {CameraPosition=camera.Eye},
            frame.Options! with {CameraOverride=camera},frame.Materials);
    }
    private static OriginalFrameScene Move(OriginalFrameScene frame,Vector3 delta)
    {
        var scene=frame.Surface.Snapshot;
        return new(new(scene.WithDeformedVertices(scene.Vertices.ToArray().Select(v=>v with {Position=v.Position+delta}).ToArray()),frame.Surface.Materials,frame.Surface.Profile),
            new(frame.Outline!.Vertices.Select(v=>v with {Position=v.Position+delta}),frame.Outline.Materials),
            frame.OutlineOptions,frame.Options,frame.Materials,frame.Lighting,frame.EditorOverlay);
    }
    private static OriginalFrameScene Mixed(int samples)
    {
        var box=Frame();var source=box.Surface.Snapshot;
        var vertices=new List<SceneVertex>();var indices=new List<uint>();var materials=new List<SceneMaterial>();
        var edges=new List<OriginalOutlineVertex>();var edgeMaterials=new List<OriginalOutlineMaterial>();
        for(int i=0;i<3;i++)
        {
            int start=vertices.Count;var delta=new Vector3((i-1)*.4f,0,i*.08f);
            vertices.AddRange(source.Vertices.ToArray().Select(v=>v with {Position=v.Position+delta}));
            indices.AddRange(source.Indices.ToArray().Select(index=>index+(uint)start));
            materials.Add(source.Materials[0] with {Name=$"run-{i}",StartIndex=i*36});
            edges.AddRange(box.Outline!.Vertices.Select(v=>v with {Position=v.Position+delta,SelectedMaterialIndex=i}));
            edgeMaterials.Add(box.Outline.Materials[0] with {Color=i==0?new(1,0,0,.5f):new(0,0,1,.5f)});
        }
        var scene=new SceneSnapshot(vertices.ToArray(),indices.ToArray(),materials,camera:source.Camera);
        var options=box.Options! with {Antialiasing=FrameAntialiasing.Smaa1X,SpatialSamples=samples,
            GroundShadow=new(new(.3f,-1,.2f),new(0,0,0,.5f),false,false)};
        return new(new(scene,Enumerable.Repeat(box.Surface.Materials[0],3),box.Surface.Profile),new(edges,edgeMaterials),box.OutlineOptions,options,
            [box.Materials[0],box.Materials[0] with {CharacterOutlineStyle=new(2,new(0,1,0,.5f))},box.Materials[0]]);
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
