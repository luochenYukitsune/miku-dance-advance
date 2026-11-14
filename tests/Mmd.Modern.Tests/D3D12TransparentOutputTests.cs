using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Mmd.Modern.ViewOutput.Images;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category","Gpu")]
public sealed class D3D12TransparentOutputTests
{
    private const int Width=97,Height=73;
    private static readonly Vector4 Red=new(.8f,.2f,.1f,.5f),Blue=new(.1f,.3f,.9f,.5f);

    [Theory,InlineData(false),InlineData(true)]
    public void TwoLayersMatchIndependentOverAndClearIsPremultiplied(bool partialBackground)
    {
        using var session=new D3D12FrameSession();
        Vector4 background=partialBackground?new(.2f,.6f,.4f,.25f):new(.9f,.7f,.5f,0);
        var frame=Fixture([Red,Blue],background);
        var image=session.Capture(frame,Width,Height);
        Pixel(image,Width/2,Height/2,Over([Red,Blue],background));
        Pixel(image,0,0,background.W==0?Vector4.Zero:background);
        Assert.Contains(session.LastRecordings,r=>r.Channel=="alpha:straight");
        Assert.Contains(session.LastTargets,r=>r.Name=="$output"&&r.Format=="R16G16B16A16_Float");
        Assert.Equal(1,session.Statistics!.ReadbackCount);
    }

    [Theory,InlineData(1f/255),InlineData(.01f),InlineData(.5f)]
    public void Fp16IntermediatePreservesColorAtVeryLowOpacity(float alpha)
    {
        using var session=new D3D12FrameSession();
        var color=new Vector4(.2f,.45f,.7f,alpha);
        var image=session.Capture(Fixture([color],Vector4.Zero),Width,Height);
        Pixel(image,Width/2,Height/2,color,1);
    }

    [Theory,InlineData(1,false),InlineData(4,false),InlineData(1,true),InlineData(4,true)]
    public void FilteringKeepsPremultipliedColorUntilFinalResolve(int samples,bool smaa)
    {
        using var session=new D3D12FrameSession();
        var color=new Vector4(.2f,.45f,.7f,.5f);
        var frame=Fixture([color],Vector4.Zero,diagonal:true);
        frame=Options(frame,frame.Options! with {SpatialSamples=samples,Antialiasing=smaa?FrameAntialiasing.Smaa1X:FrameAntialiasing.Off});
        var image=session.Capture(frame,Width,Height);
        int covered=0,partial=0;
        for(int p=0;p<image.Rgba.Length;p+=4)
        {
            byte alpha=image.Rgba[p+3];
            if(alpha==0){Assert.Equal(new byte[3],image.Rgba.AsSpan(p,3).ToArray());continue;}
            covered++;
            for(int c=0;c<3;c++)Assert.InRange((int)image.Rgba[p+c],(int)MathF.Round(color[c]*255)-1,(int)MathF.Round(color[c]*255)+1);
            if(alpha<125)partial++;
        }
        Assert.True(covered>100);
        if(samples>1||smaa)Assert.True(partial>0,"A diagonal coverage edge must actually be filtered.");
        Assert.Equal("alpha:straight",session.LastRecordings.Last().Channel);
        if(smaa)Assert.Equal(3,session.Statistics!.AntialiasingPassDrawCalls);
    }

    [Fact]
    public void SmaaFindsBlackAlphaOnlySilhouetteAndLegacyDefaultIsUnchanged()
    {
        using var session=new D3D12FrameSession();
        var black=Fixture([new(0,0,0,1)],Vector4.Zero,diagonal:true);
        var baseline=session.Capture(black,Width,Height);
        var filtered=session.Capture(Options(black,black.Options! with {Antialiasing=FrameAntialiasing.Smaa1X}),Width,Height);
        Assert.NotEqual(baseline.Rgba,filtered.Rgba);
        Assert.Contains(Enumerable.Range(0,Width*Height),p=>filtered.Rgba[p*4+3] is >0 and <255);
        Assert.All(Enumerable.Range(0,Width*Height),p=>Assert.Equal(new byte[3],filtered.Rgba.AsSpan(p*4,3).ToArray()));
        var legacy=Options(black,black.Options! with {AlphaOutput=FrameAlphaOutput.LegacyMax,BackgroundColor=Vector4.One});
        var old=session.Capture(legacy,Width,Height);
        session.Capture(black,Width,Height);
        Assert.Equal(old.Rgba,session.Capture(legacy,Width,Height).Rgba);
        Assert.DoesNotContain(session.LastRecordings,r=>r.Channel=="alpha:straight");
    }

    [Fact]
    public void SubTextureTechniqueAlphaIsResolvedWithoutPremultiplicationTwice()
    {
        using var session=new D3D12FrameSession();var source=Fixture([Red],Vector4.Zero);
        var old=source.Surface.Snapshot;
        var stream=new SceneAdditionalUvs(1,Enumerable.Repeat(new SceneAdditionalUvVertex(new(.5f),default,default,default),old.Vertices.Length).ToArray());
        var mesh=new SceneSnapshot(old.Vertices,old.Indices,[old.Materials[0] with {DiffuseTexture=0}],
            [new("diffuse",1,1,[255,255,255,255]),new("sub texture",1,1,[51,153,102,128])],camera:old.Camera,lightDirection:Vector3.UnitX,additionalUvs:stream);
        var binding=new OriginalStyleMaterialBinding(Vector3.One,1,OriginalSphereMode.SubTexture,true) {
            TextureFactors=OriginalMaterialTextureFactors.Identity with {Toon=new(new(1,1,1,.2f),Vector4.Zero)}};
        var frame=new OriginalFrameScene(new(mesh,[binding],source.Surface.Profile),options:source.Options);
        Pixel(session.Capture(frame,Width,Height),Width/2,Height/2,new(.8f*.2f,.2f*.6f,.1f*.4f,.5f*(128/255f)*.2f));
    }

    [Theory,InlineData(LightingMode.SoftwareRayTracing),InlineData(LightingMode.HardwareRayTracing)]
    public void GiAndOutlineCanFeedStraightOutputWithSsaaAndSmaa(LightingMode mode)
    {
        using var session=new D3D12FrameSession();
        var bounce=D3D12RayGiTests.Bounce();
        var frame=Options(bounce,bounce.Options! with {AlphaOutput=FrameAlphaOutput.Straight,BackgroundColor=Vector4.Zero,
            SpatialSamples=2,Antialiasing=FrameAntialiasing.Smaa1X});
        var gi=new RayGiOptions(mode,Strength:4,SamplesPerPixel:16,Seed:17,MaxRayDistance:50,RayEpsilon:.001f,DirectionalIrradiance:new(3));
        var off=session.Capture(frame,Width,Height);
        var on=session.Capture(frame,Width,Height,rayGiOptions:gi);
        Assert.NotEqual(off.Rgba,on.Rgba);Assert.Equal(mode,session.RayGiStatistics!.Effective);
        using(var output=session.Render(frame,Width,Height,rayGiOptions:gi))Assert.Equal(0,output.Statistics.ReadbackCount);
        var box=D3D12OriginalOutlineTests.Box();
        var outline=Options(box,new(OriginalAlphaBlendMode.Normal,true,false,BackgroundColor:Vector4.Zero,AlphaOutput:FrameAlphaOutput.Straight));
        var edges=session.Capture(outline,257,191);
        Pixel(edges,78,95,new(0,0,0,1));Pixel(edges,0,0,Vector4.Zero);
    }

    [Fact]
    public void AbortResizeAndUnsupportedCombinationsPreserveReusableSessionAndZeroRenderReadback()
    {
        using var session=new D3D12FrameSession();var frame=Fixture([Red,Blue],Vector4.Zero);
        var expected=session.Capture(frame,Width,Height);int? cacheCount=null;
        for(int i=0;i<8;i++)
        {
            using var output=session.Render(frame,Width+i%2,Height+i%3);
            Assert.Equal(0,output.Statistics.ReadbackCount);
            cacheCount??=output.Statistics.CachedResources;
            Assert.Equal(cacheCount.Value,output.Statistics.CachedResources);
        }
        Assert.Throws<OperationCanceledException>(()=>session.RenderForPresent(frame,Width,Height,null,(_,_)=>throw new OperationCanceledException()));
        Assert.Equal(expected.Rgba,session.Capture(frame,Width,Height).Rgba);
        Assert.Throws<NotSupportedException>(()=>session.Render(frame,Width,Height,temporalUpscaling:new(){Mode=D3D12TemporalUpscalingMode.Fsr}));
        Assert.Throws<NotSupportedException>(()=>session.Render(frame,Width,Height,upscaling:new(){Mode=D3D12SpatialUpscalingMode.Fsr1}));
        Assert.Throws<NotSupportedException>(()=>Options(frame,frame.Options! with {BlendMode=OriginalAlphaBlendMode.Additive}));
        var overlay=new ViewportOverlaySnapshot([new(Vector2.Zero,Vector2.One,Vector4.One)]);
        var withOverlay=new OriginalFrameScene(frame.Surface,options:frame.Options,editorOverlay:overlay);
        Assert.Throws<NotSupportedException>(()=>session.Render(withOverlay,Width,Height));
        Assert.Equal(expected.Rgba,session.Capture(withOverlay,Width,Height).Rgba); // Capture excludes editor overlays.
        Assert.Equal(expected.Rgba,session.Capture(frame,Width,Height).Rgba);
    }

    [Fact]
    public async Task PublicViewCaptureAutomaticallySelectsStraightAndPngRetainsItsActualRgba()
    {
        var frame=Fixture([Red,Blue],Vector4.Zero);
        frame=Options(frame,frame.Options! with {AlphaOutput=FrameAlphaOutput.LegacyMax});
        var plan=new OfflineFramePlan(Width,Height,new(30),0,0);var sample=plan.GetSample(0);
        await using var renderer=new D3D12ViewRenderer(new(Width,Height));
        using var pixels=await renderer.CaptureAsync(new(sample,Width,Height,frame,null,null,null),TestContext.Current.CancellationToken);
        Assert.Equal(FrameAlphaMode.Straight,pixels.Alpha);
        Pixel(new(Width,Height,pixels.Pixels.ToArray(),"view","GPU",0),Width/2,Height/2,Over([Red,Blue],Vector4.Zero));
        string directory=Path.Combine(Path.GetTempPath(),"mmd-transparent-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);string target=Path.Combine(directory,"transparent.png");
        await using(var sink=new PngFrameSink(target))
        {
            await sink.BeginAsync(new(plan),TestContext.Current.CancellationToken);
            await sink.WriteAsync(sample,pixels,TestContext.Current.CancellationToken);
            await sink.CompleteAsync(TestContext.Current.CancellationToken);
        }
        Assert.Equal(pixels.Pixels.ToArray(),ReadPng(File.ReadAllBytes(target)));
        Assert.Equal(1,renderer.Statistics!.ReadbackCount);
    }

    private static OriginalFrameScene Fixture(Vector4[] colors,Vector4 background,bool diagonal=false)
    {
        var quad=D3D12MeshTests.Quad();var vertices=quad.Vertices.ToArray();
        uint[] part=diagonal?[0,1,3]:quad.Indices.ToArray();
        var indices=Enumerable.Range(0,colors.Length).SelectMany(_=>part).ToArray();
        var materials=colors.Select((c,i)=>new SceneMaterial("layer"+i,i*part.Length,part.Length,c,Vector3.Zero,Vector3.Zero,1)).ToArray();
        var mesh=new SceneSnapshot(vertices,indices,materials,camera:quad.Camera,lightDirection:-Vector3.UnitZ);
        var surface=new OriginalStyleScene(mesh,colors.Select(_=>new OriginalStyleMaterialBinding(Vector3.One,DoubleSided:true)),
            new(Vector3.One,Vector3.Zero,Matrix4x4.Identity,OriginalTextureSampling.PointClamp));
        return new(surface,options:new(OriginalAlphaBlendMode.Normal,false,false,BackgroundColor:background,AlphaOutput:FrameAlphaOutput.Straight));
    }
    private static OriginalFrameScene Options(OriginalFrameScene frame,OriginalFrameOptions options)
        =>new(frame.Surface,frame.Outline,frame.OutlineOptions,options,frame.Materials,frame.Lighting,frame.EditorOverlay);
    private static Vector4 Over(Vector4[] layers,Vector4 background)
    {
        var rgb=new Vector3(background.X,background.Y,background.Z)*background.W;float alpha=background.W;
        foreach(var source in layers){rgb=new Vector3(source.X,source.Y,source.Z)*source.W+rgb*(1-source.W);alpha=source.W+alpha*(1-source.W);}
        return alpha>0?new(rgb/alpha,alpha):Vector4.Zero;
    }
    private static void Pixel(RenderedFrame frame,int x,int y,Vector4 expected,int tolerance=1)
    {
        int p=(y*frame.Width+x)*4;
        for(int c=0;c<4;c++)Assert.InRange((int)frame.Rgba[p+c],Math.Max(0,(int)MathF.Round(expected[c]*255)-tolerance),Math.Min(255,(int)MathF.Round(expected[c]*255)+tolerance));
    }
    private static byte[] ReadPng(byte[] png)
    {
        int width=0,height=0;using var compressed=new MemoryStream();
        for(int p=8;p<png.Length;)
        {
            int count=BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p));var type=System.Text.Encoding.ASCII.GetString(png,p+4,4);
            if(type=="IHDR"){width=BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p+8));height=BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p+12));}
            if(type=="IDAT")compressed.Write(png.AsSpan(p+8,count));p+=count+12;
        }
        compressed.Position=0;using var zlib=new ZLibStream(compressed,CompressionMode.Decompress);using var raw=new MemoryStream();zlib.CopyTo(raw);
        var scanlines=raw.ToArray();var result=new byte[width*height*4];
        for(int y=0;y<height;y++){Assert.Equal(0,scanlines[y*(width*4+1)]);scanlines.AsSpan(y*(width*4+1)+1,width*4).CopyTo(result.AsSpan(y*width*4));}
        return result;
    }
}
