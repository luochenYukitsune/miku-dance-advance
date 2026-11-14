using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;
namespace Mmd.Modern.Tests;

[Trait("Category","Gpu")]
public sealed class D3D12Fsr1Tests
{
    private static readonly D3D12SpatialUpscalingOptions Fsr=new() {Mode=D3D12SpatialUpscalingMode.Fsr1};
    [Theory]
    [InlineData(97,73)]
    [InlineData(1,1)]
    [InlineData(1,73)]
    public void ActualGpuConstantColorSurvivesEasuAndRcasAtOddAndOnePixelDimensions(int width,int height)
    {
        using var session=new D3D12FrameSession();var frame=Empty(new(41/255f,88/255f,177/255f,1));
        var image=session.Capture(frame,width,height,upscaling:Fsr);
        Assert.Equal(width,image.Width);Assert.Equal(height,image.Height);
        for(int i=0;i<image.Rgba.Length;i+=4)
        {Assert.InRange(image.Rgba[i],40,42);Assert.InRange(image.Rgba[i+1],87,89);Assert.InRange(image.Rgba[i+2],176,178);Assert.Equal(255,image.Rgba[i+3]);}
        Assert.Equal(2,session.Statistics!.UpscalingPassDrawCalls);
        Assert.Equal(new[]{"fsr1:easu","fsr1:rcas"},session.LastRecordings.Select(r=>r.Channel));
    }
    [Fact]
    public void OffIsExactAndToggleResizeRetainedOutputNeverReadsBack()
    {
        using var session=new D3D12FrameSession();var frame=Empty(Vector4.One);
        var original=session.Capture(frame,97,73);
        Assert.Equal(original.Rgba,session.Capture(frame,97,73,upscaling:D3D12SpatialUpscalingOptions.Off).Rgba);
        foreach(var size in new[]{(97,73),(131,81),(1,1),(97,73)})
        {
            using var output=session.Render(frame,size.Item1,size.Item2,upscaling:Fsr);
            Assert.Equal(size.Item1,output.Width);Assert.Equal(size.Item2,output.Height);
            Assert.Equal(0,output.Statistics.ReadbackCount);Assert.Equal(1,output.Statistics.SubmissionCount);
            Assert.Equal(Fsr.GetDimensions(size.Item1,size.Item2).RenderWidth,output.Statistics.UpscalingInputWidth);
            session.WaitForIdle();Assert.Equal((ulong)size.Item1,output.Resource.Description.Width);
        }
        Assert.Equal(original.Rgba,session.Capture(frame,97,73).Rgba);
    }
    [Fact]
    public void SmaaPrecedesEasuAndUnsupportedTransparencyDoesNotPoisonSession()
    {
        using var session=new D3D12FrameSession();
        Assert.Throws<NotSupportedException>(()=>session.Capture(Empty(new(1,1,1,.5f)),97,73,upscaling:Fsr));
        var frame=Empty(Vector4.One);frame=new(frame.Surface,options:frame.Options! with {Antialiasing=FrameAntialiasing.Smaa1X});
        session.Capture(frame,97,73,upscaling:Fsr with {EnableRcas=false});
        Assert.Equal(new[]{"smaa:edges","smaa:weights","smaa:neighborhood","fsr1:easu"},session.LastRecordings.Select(r=>r.Channel));
        Assert.Equal(3,session.Statistics!.AntialiasingPassDrawCalls);Assert.Equal(1,session.Statistics.UpscalingPassDrawCalls);
        Assert.Equal(64,session.Statistics.UpscalingInputWidth);Assert.Equal(48,session.Statistics.UpscalingInputHeight);
    }

    [Fact]
    public void ActualGpuEdgeAdaptiveReconstructionDiffersFromBilinearAndRcasChangesTexture()
    {
        const int inputWidth=43,inputHeight=31,width=79,height=57;
        byte[] input=new byte[inputWidth*inputHeight*4];
        for(int y=0;y<inputHeight;y++)for(int x=0;x<inputWidth;x++)
        {
            int i=(y*inputWidth+x)*4;
            byte color=(byte)(x>5&&x<38&&y>4&&y<4+.61*x?35:215);
            input[i]=color;input[i+1]=(byte)(30+(x*17+y*11)%190);input[i+2]=(byte)(40+(x*9+y*19)%170);input[i+3]=255;
        }
        byte[] easu=Filter(input,inputWidth,inputHeight,width,height,Fsr with {EnableRcas=false});
        byte[] sharpened=Filter(input,inputWidth,inputHeight,width,height,Fsr);
        byte[] weak=Filter(input,inputWidth,inputHeight,width,height,Fsr with {RcasSharpness=2});
        int nonBilinear=0,rcasChanges=0,sharpnessChanges=0;
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            int i=(y*width+x)*4;
            // Independent pixel-center bilinear reference. An ordinary resize cannot pass this assertion.
            double px=(x+.5)*inputWidth/width-.5,py=(y+.5)*inputHeight/height-.5;
            int x0=(int)Math.Floor(px),y0=(int)Math.Floor(py);double tx=px-x0,ty=py-y0;
            double Sample(int xx,int yy)=>input[(Math.Clamp(yy,0,inputHeight-1)*inputWidth+Math.Clamp(xx,0,inputWidth-1))*4];
            double bilinear=(Sample(x0,y0)*(1-tx)+Sample(x0+1,y0)*tx)*(1-ty)+(Sample(x0,y0+1)*(1-tx)+Sample(x0+1,y0+1)*tx)*ty;
            if(Math.Abs(easu[i]-bilinear)>3)nonBilinear++;
            if(easu[i]!=sharpened[i]||easu[i+1]!=sharpened[i+1])rcasChanges++;
            if(weak[i]!=sharpened[i]||weak[i+1]!=sharpened[i+1])sharpnessChanges++;
            Assert.Equal(255,easu[i+3]);Assert.Equal(255,sharpened[i+3]);
        }
        Assert.True(nonBilinear>100,$"Only {nonBilinear} pixels differed from bilinear.");
        Assert.True(rcasChanges>100,$"Only {rcasChanges} pixels changed with RCAS.");
        Assert.True(sharpnessChanges>100,$"Only {sharpnessChanges} pixels changed with RCAS sharpness.");
    }

    [Fact]
    public void SsaaThenSmaaAtInputExtentMatchesIndependentLowResolutionCaptureThenEasu()
    {
        using var session=new D3D12FrameSession();var original=Diagonal();
        var frame=new OriginalFrameScene(original.Surface,options:original.Options! with {SpatialSamples=4,Antialiasing=FrameAntialiasing.Smaa1X});
        var options=Fsr with {EnableRcas=false};var dimensions=options.GetDimensions(97,73);
        var low=session.Capture(frame,dimensions.RenderWidth,dimensions.RenderHeight);
        byte[] reference=Filter(low.Rgba,low.Width,low.Height,97,73,options);
        var actual=session.Capture(frame,97,73,upscaling:options);
        Assert.Equal(reference,actual.Rgba);
        Assert.Equal(4,session.Statistics!.SpatialAccumulationDispatches);
        Assert.Equal(1,session.Statistics.SpatialResolveDrawCalls);
        Assert.Equal(3,session.Statistics.AntialiasingPassDrawCalls);
        Assert.Equal(new[]{"smaa:edges","smaa:weights","smaa:neighborhood","fsr1:easu"},session.LastRecordings.TakeLast(4).Select(r=>r.Channel));
        Assert.All(session.LastTargets.Where(t=>!t.Name.StartsWith("$fsr1",StringComparison.Ordinal)),t=>
        {Assert.Equal(dimensions.RenderWidth,t.Width);Assert.Equal(dimensions.RenderHeight,t.Height);});
    }

    [Fact]
    public void RetainedFramesBoundAllocationsCacheShadersAndRecoverFromPresentCancellation()
    {
        using var session=new D3D12FrameSession();var frame=Diagonal();
        var baseline=session.Capture(frame,97,73,upscaling:Fsr);
        // Warm both output format variants once, then resizing must never create more pipelines.
        using(session.Render(frame,97,73,upscaling:Fsr with {EnableRcas=false})){}
        var warm=session.Statistics!;int maximumResources=0;
        for(int i=0;i<24;i++)
        {
            using var output=session.Render(frame,97+i%3*2,73+i%4*2,upscaling:Fsr with {EnableRcas=i%2==0});
            Assert.Equal(0,output.Statistics.ReadbackCount);Assert.Equal(1,output.Statistics.SubmissionCount);
            Assert.Equal(warm.ShaderCompilations,output.Statistics.ShaderCompilations);
            Assert.Equal(warm.PipelineCreations,output.Statistics.PipelineCreations);
            Assert.Equal(warm.CachedResources,output.Statistics.CachedResources);
            Assert.InRange(output.Statistics.PendingFrames,0,3);
            maximumResources=Math.Max(maximumResources,output.Statistics.LiveFrameResources);
        }
        Assert.InRange(maximumResources,1,200);
        Assert.Throws<OperationCanceledException>(()=>session.RenderForPresent(frame,97,73,null,
            (_,target)=>{Assert.Equal(97,target.Width);Assert.Equal(73,target.Height);Assert.Equal(ResourceStates.CopySource,target.State);throw new OperationCanceledException();},Fsr));
        Assert.Equal(baseline.Rgba,session.Capture(frame,97,73,upscaling:Fsr).Rgba);
        using var fresh=new D3D12FrameSession();
        Assert.Equal(baseline.Rgba,fresh.Capture(frame,97,73,upscaling:Fsr).Rgba);
    }

    private static byte[] Filter(byte[] pixels,int inputWidth,int inputHeight,int width,int height,D3D12SpatialUpscalingOptions options)
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var context=runtime.Begin();
        try
        {
            var input=new D3D12GpuTarget("synthetic-input",cache.Texture(new("synthetic",inputWidth,inputHeight,pixels)),default,
                inputWidth,inputHeight,Format.R8G8B8A8_UNorm,false){State=ResourceStates.PixelShaderResource,Initialized=true};
            var result=new D3D12Fsr1Pass(runtime,cache).Record(context,input,width,height,options).Output;
            context.Transition(result,ResourceStates.CopySource);
            var footprint=new PlacedSubresourceFootPrint[1];
            runtime.Device.GetCopyableFootprints(result.Resource.Description,0,1,0,footprint,new int[1],new ulong[1],out ulong size);
            var readback=context.Own(runtime.Device.CreateCommittedResource(HeapType.Readback,ResourceDescription.Buffer(size),ResourceStates.CopyDest));
            context.Commands.CopyTextureRegion(new TextureCopyLocation(readback,footprint[0]),0,0,0,new TextureCopyLocation(result.Resource,0));
            runtime.Wait(runtime.Submit());var data=readback.Map<byte>(0,checked((int)size));var output=new byte[width*height*4];
            try{for(int y=0;y<height;y++)data.Slice(y*footprint[0].Footprint.RowPitch,width*4).CopyTo(output.AsSpan(y*width*4,width*4));}
            finally{readback.Unmap(0);}
            return output;
        }
        finally{runtime.Abort();}
    }
    private static OriginalFrameScene Diagonal()
    {
        SceneVertex[] vertices=[new(new(-.8f,-.7f,0),-Vector3.UnitZ,Vector2.Zero),
            new(new(.8f,-.4f,0),-Vector3.UnitZ,Vector2.Zero),new(new(-.5f,.9f,0),-Vector3.UnitZ,Vector2.Zero)];
        var scene=new SceneSnapshot(vertices,new uint[]{0,1,2},[new("white",0,3,Vector4.One,Vector3.Zero,Vector3.One,1)],
            camera:new(new(0,0,-5),Vector3.Zero,Vector3.UnitY,2,.1f,20),lightDirection:-Vector3.UnitZ);
        return new(new(scene,[new(Vector3.One,DoubleSided:true)]),options:new(OriginalAlphaBlendMode.Normal,true,false));
    }
    private static OriginalFrameScene Empty(Vector4 background)
    {
        var scene=SceneSnapshot.CreateEmpty();
        return new(new(scene,[]),options:new(OriginalAlphaBlendMode.Normal,true,false) {BackgroundColor=background});
    }
}
