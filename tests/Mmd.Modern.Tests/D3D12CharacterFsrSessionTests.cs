using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12TemporalUpscalingContractTests
{
    [Theory]
    [InlineData(D3D12FsrQualityPreset.NativeAa, 1920, 1080)]
    [InlineData(D3D12FsrQualityPreset.Quality, 1280, 720)]
    [InlineData(D3D12FsrQualityPreset.Balanced, 1129, 635)]
    [InlineData(D3D12FsrQualityPreset.Performance, 960, 540)]
    [InlineData(D3D12FsrQualityPreset.UltraPerformance, 640, 360)]
    public void PresetsMatchPinnedVendorRatios(D3D12FsrQualityPreset quality, int width, int height)
    {
        var value = new D3D12TemporalUpscalingOptions {Mode=D3D12TemporalUpscalingMode.Fsr,QualityPreset=quality};
        var result = value.GetDimensions(1920,1080);
        Assert.Equal(width,result.RenderWidth);Assert.Equal(height,result.RenderHeight);
        Assert.Equal(1920,result.DisplayWidth);Assert.Equal(1080,result.DisplayHeight);
        Assert.Equal(new D3D12SpatialUpscalingDimensions(1920,1080,1920,1080),D3D12TemporalUpscalingOptions.Off.GetDimensions(1920,1080));
    }

    [Fact]
    public void HistoryRequiresSubmissionAndResetsDiscontinuitiesWithoutResettingDeformation()
    {
        var frame=D3D12CharacterFsrSessionTests.Character();var history=new D3D12TemporalHistory();
        var options=new D3D12TemporalUpscalingOptions();
        Assert.True(history.ShouldReset(frame,options));Assert.Null(history.Previous);Assert.Equal(0,history.SubmittedFrames);
        history.Commit(frame,options,0,18);
        Assert.False(history.ShouldReset(frame,options));Assert.Equal(1,history.Phase);
        var snapshot=frame.Surface.Snapshot;
        var moved=new OriginalFrameScene(new OriginalStyleScene(snapshot.WithDeformedVertices(snapshot.Vertices.ToArray()
            .Select(v=>v with {Position=v.Position+new Vector3(.1f,0,0)}).ToArray()),frame.Surface.Materials,frame.Surface.Profile),
            frame.Outline,frame.OutlineOptions,frame.Options,frame.Materials);
        Assert.False(history.ShouldReset(moved,options));
        Assert.Equal("SeekOrRestart",history.ResetReason(frame,options with {HistoryGeneration=1}));
        Assert.Equal("CameraCut",history.ResetReason(frame,options with {CameraCutKey=1}));
        Assert.Equal("ExplicitRequest",history.ResetReason(frame,options with {ResetHistory=true}));
        history.Invalidate("AbortedFrame");Assert.Null(history.Previous);Assert.Equal(0,history.Phase);
        Assert.Equal("AbortedFrame",history.ResetReason(frame,options));Assert.Equal(1,history.SubmittedFrames);
    }
}

[Trait("Category","Gpu")]
public sealed class D3D12CharacterFsrSessionTests(ITestOutputHelper output)
{
    private static D3D12TemporalUpscalingOptions Options => new()
    {
        Mode=D3D12TemporalUpscalingMode.Fsr,
        QualityPreset=D3D12FsrQualityPreset.Quality,
        LoaderPath=Environment.GetEnvironmentVariable("MMD_FSR_LOADER_PATH")
            ?? throw new InvalidOperationException("Set MMD_FSR_LOADER_PATH for the explicit SDK GPU tests.")
    };

    [Fact]
    public void CompleteToonOutlineAndTransparentCharacterCapturesAndOffRetainsEveryByte()
    {
        var frame=Character();using var session=new D3D12FrameSession();
        using var reference=new D3D12OriginalStyleRenderer();var baseline=reference.Render(frame,96,72);
        Assert.Equal(baseline.Rgba,session.Capture(frame,96,72,temporalUpscaling:D3D12TemporalUpscalingOptions.Off).Rgba);
        var options=Options;
        for(int i=0;i<8;i++)
        {
            using var gpu=session.Render(frame,96,72,temporalUpscaling:options);
            Assert.Equal(0,gpu.Statistics.ReadbackCount);Assert.Equal(1,gpu.Statistics.SubmissionCount);
            Assert.Equal(i==0,gpu.Statistics.TemporalUpscaling!.ResetHistory);
        }
        var captured=session.Capture(frame,96,72,temporalUpscaling:options);
        Assert.Equal(96,captured.Width);Assert.Equal(72,captured.Height);Assert.Equal(96*72*4,captured.Rgba.Length);
        var stats=session.Statistics!;Assert.Equal(1,stats.ReadbackCount);Assert.Equal(64,stats.UpscalingInputWidth);Assert.Equal(48,stats.UpscalingInputHeight);
        Assert.NotEqual(0ul,stats.TemporalUpscaling!.ProviderId);Assert.False(string.IsNullOrWhiteSpace(stats.TemporalUpscaling.ProviderName));
        Assert.Contains(session.LastRecordings,r=>r.Channel=="original-frame");
        Assert.Contains(session.LastRecordings,r=>r.Channel=="fsr:temporal");
        Assert.Contains(session.LastTargets,t=>t.Format=="R16G16B16A16_Float");
        // Independent stable regions: opaque composited background and the solid body survive the color bridge.
        for(int channel=0;channel<3;channel++) Assert.InRange(Math.Abs(captured.Rgba[channel]-baseline.Rgba[channel]),0,2);
        // This point is inside both the translucent textured triangle and the solid body, away from their edges.
        // Reconstruction must retain the accepted display-domain alpha composite after the linear FSR bridge.
        int transparentPixel=(40*96+36)*4;
        for(int channel=0;channel<3;channel++)
            Assert.InRange(Math.Abs(captured.Rgba[transparentPixel+channel]-baseline.Rgba[transparentPixel+channel]),0,8);
        Assert.True(captured.Rgba.Where((_,i)=>i%4==0).Count(v=>v>60)>100);
        Assert.True(captured.Rgba.Where((_,i)=>i%4==1).Count(v=>v>60)>100);
        Assert.Equal(baseline.Rgba,session.Capture(frame,96,72).Rgba);
        Assert.Null(session.Statistics!.TemporalUpscaling);
        using(var reenabled=session.Render(frame,96,72,temporalUpscaling:options))
        {Assert.True(reenabled.Statistics.TemporalUpscaling!.ResetHistory);Assert.Equal("ModeChanged",reenabled.Statistics.TemporalUpscaling.ResetReason);}
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(stats));
        D3D12OriginalFrameTests.Save("character-fsr-temporal",captured);
    }

    [Fact]
    public void SessionResetsSeekCameraCutResizeMaterialAndAbortThenKeepsResourceUseBounded()
    {
        using var session=new D3D12FrameSession();var frame=Character();var options=Options;
        D3D12TemporalUpscalingStatistics Render(OriginalFrameScene f,D3D12TemporalUpscalingOptions o,int width=96,int height=72)
        {using var result=session.Render(f,width,height,temporalUpscaling:o);return result.Statistics.TemporalUpscaling!;}
        Assert.True(Render(frame,options).ResetHistory);Assert.False(Render(frame,options).ResetHistory);
        options=options with {HistoryGeneration=1};Assert.Equal("SeekOrRestart",Render(frame,options).ResetReason);
        options=options with {CameraCutKey=1};Assert.Equal("CameraCut",Render(frame,options).ResetReason);
        Assert.Equal("ExplicitRequest",Render(frame,options with {ResetHistory=true}).ResetReason);
        Assert.False(Render(frame,options).ResetHistory);
        var changed=Character(.35f);Assert.Equal("SceneStructureChanged",Render(changed,options).ResetReason);
        Assert.Equal("ConfigurationChanged",Render(changed,options,120,90).ResetReason);
        Assert.False(Render(changed,options,120,90).ResetHistory);
        long before=session.Statistics!.TemporalUpscaling!.SubmittedFrames;
        Assert.Throws<InvalidOperationException>(()=>session.RenderForPresent(changed,120,90,null,
            (_,_)=>throw new InvalidOperationException("injected after vendor recording"),temporalUpscaling:options));
        Assert.False(session.Runtime.IsRecording);
        var recovered=Render(changed,options,120,90);Assert.Equal("AbortedFrame",recovered.ResetReason);Assert.Equal(before+1,recovered.SubmittedFrames);
        for(int i=0;i<5;i++)Render(changed,options,120,90);
        var warm=session.Statistics!;
        for(int i=0;i<36;i++)Render(changed,options,120,90);
        var last=session.Statistics!;
        Assert.Equal(warm.CachedResources,last.CachedResources);Assert.Equal(warm.LiveFrameResources,last.LiveFrameResources);
        Assert.Equal(warm.ShaderCompilations,last.ShaderCompilations);Assert.Equal(warm.PipelineCreations,last.PipelineCreations);
        Assert.Equal(warm.TotalReadbacks,last.TotalReadbacks);Assert.InRange(last.PendingFrames,0,3);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{warm,last}));
    }

    [Fact]
    public void StackedAaAndUnsupportedCameraRejectBeforeRecordingWithoutSilentOverrides()
    {
        using var session=new D3D12FrameSession();var frame=Character();var options=Options;
        var smaa=new OriginalFrameScene(frame.Surface,frame.Outline,frame.OutlineOptions,frame.Options! with {Antialiasing=FrameAntialiasing.Smaa1X},frame.Materials);
        Assert.Throws<NotSupportedException>(()=>session.Capture(smaa,96,72,temporalUpscaling:options));
        var spatial=new D3D12SpatialUpscalingOptions {Mode=D3D12SpatialUpscalingMode.Fsr1};
        Assert.Throws<NotSupportedException>(()=>session.Capture(frame,96,72,upscaling:spatial,temporalUpscaling:options));
        var ortho=new OriginalFrameScene(frame.Surface,frame.Outline,frame.OutlineOptions,frame.Options! with {CameraOverride=frame.Surface.Snapshot.Camera with {VerticalFovDegrees=null}},frame.Materials);
        Assert.Throws<NotSupportedException>(()=>session.Capture(ortho,96,72,temporalUpscaling:options));
        Assert.False(session.Runtime.IsRecording);Assert.Equal(0,session.Runtime.Submissions);
        using var good=session.Render(frame,96,72,temporalUpscaling:options);Assert.True(good.Statistics.TemporalUpscaling!.ResetHistory);
    }

    [Fact]
    public void ActualSwapchainPresentsFullCharacterAndResizeWithoutCpuReadback()
    {
        nint window=CreateWindowExW(0,"STATIC","MMD temporal test",0x80000000,0,0,120,90,0,0,0,0);
        Assert.NotEqual(0,window);
        try
        {
            using var presenter=new D3D12SwapchainPresenter(window,96,72);var frame=Character();var options=Options;
            for(int i=0;i<4;i++)Assert.True(presenter.Present(frame,96,72,temporalUpscaling:options));
            Assert.Equal(0,presenter.Statistics.Frame!.TotalReadbacks);Assert.Equal(4,presenter.Statistics.PresentCount);
            Assert.False(presenter.TemporalUpscalingStatistics!.ResetHistory);
            Assert.True(presenter.Present(frame,120,90,temporalUpscaling:options));
            Assert.True(presenter.TemporalUpscalingStatistics!.ResetHistory);Assert.Equal(1,presenter.Statistics.ResizeCount);
            Assert.Equal(0,presenter.Statistics.Frame!.TotalReadbacks);Assert.Equal(120,presenter.Capture(frame,temporalUpscaling:options).Width);
            Assert.Equal(1,presenter.Statistics.ExplicitCaptureCount);
        }
        finally{DestroyWindow(window);}
    }

    [Fact]
    public void EditorOverlayDrawsAfterUpscalingAndNeverEntersCaptureOrHistory()
    {
        using var session=new D3D12FrameSession();var frame=Character();var options=Options;
        var overlay=new ViewportOverlaySnapshot([new(new(.1f,.15f),new(.9f,.15f),new(1,0,0,1))]);
        var editing=new OriginalFrameScene(frame.Surface,frame.Outline,frame.OutlineOptions,frame.Options,frame.Materials,frame.Lighting,overlay);
        using(var initial=session.Render(frame,96,72,temporalUpscaling:options)){}
        byte[]? pixels=null;
        using(var shown=session.RenderForPresent(editing,96,72,null,(context,target)=>
        {
            Assert.Equal(96,target.Width);Assert.Equal(72,target.Height);
            Assert.Equal(1,session.EditorOverlayDrawCalls);
            Assert.Equal("$temporal-color-display",target.Name);
        },temporalUpscaling:options))
        {Assert.Equal(0,shown.Statistics.ReadbackCount);Assert.False(shown.Statistics.TemporalUpscaling!.ResetHistory);}
        var captured=session.Capture(editing,96,72,temporalUpscaling:options);pixels=captured.Rgba;
        Assert.Equal(0,session.EditorOverlayDrawCalls);Assert.False(session.TemporalUpscalingStatistics!.ResetHistory);
        // A horizontal opaque red editor line would replace the background here if it entered capture/history.
        int location=(10*96+12)*4;Assert.InRange(pixels[location],18,22);Assert.InRange(pixels[location+1],22,26);
    }

    [Fact]
    public void NullFrameOptionsRetainTheOriginalSurfaceTechniqueWithTemporalJitter()
    {
        var source=D3D12OriginalOutlineTests.Box();
        var snapshot=source.Surface.Snapshot.WithCamera(source.Surface.Snapshot.Camera with {VerticalFovDegrees=45});
        var frame=new OriginalFrameScene(new OriginalStyleScene(snapshot,source.Surface.Materials,source.Surface.Profile),source.Outline,source.OutlineOptions);
        using var reference=new D3D12OriginalStyleRenderer();var baseline=reference.Render(frame,96,72);
        using var session=new D3D12FrameSession();var options=Options with {QualityPreset=D3D12FsrQualityPreset.NativeAa};
        for(int i=0;i<3;i++){using var gpu=session.Render(frame,96,72,temporalUpscaling:options);Assert.Equal(i==0,gpu.Statistics.TemporalUpscaling!.ResetHistory);}
        var captured=session.Capture(frame,96,72,temporalUpscaling:options);int center=(36*96+48)*4;
        for(int channel=0;channel<3;channel++)Assert.InRange(Math.Abs(captured.Rgba[center+channel]-baseline.Rgba[center+channel]),0,3);
    }

    internal static OriginalFrameScene Character(float alpha=.55f)
    {
        var box=D3D12OriginalOutlineTests.Box();var mesh=box.Surface.Snapshot;
        SceneVertex[] glass=[new(new(-.1f,-.75f,-.2f),-Vector3.UnitZ,new(0,1)),new(new(1.5f,-.75f,-.2f),-Vector3.UnitZ,new(1,1)),new(new(.7f,.9f,-.2f),-Vector3.UnitZ,new(.5f,0))];
        var vertices=mesh.Vertices.ToArray().Concat(glass).ToArray();
        var scene=new SceneSnapshot(vertices,[..mesh.Indices.ToArray(),8,10,9],
            [mesh.Materials[0] with {Diffuse=new(.7f,.8f,.9f,1)},new("transparent",36,3,new(.9f,.25f,.1f,alpha),Vector3.Zero,Vector3.Zero,1,DiffuseTexture:0)],
            [new("alpha",1,1,new byte[]{255,255,255,160})],camera:mesh.Camera with {VerticalFovDegrees=45});
        var surface=new OriginalStyleScene(scene,[new(new(.25f,.4f,.65f),DoubleSided:true),new(Vector3.One,DoubleSided:true)],box.Surface.Profile);
        var outline=new OriginalOutlineData(box.Outline!.Vertices.Concat(glass.Select(v=>new OriginalOutlineVertex(v.Position,v.Normal,OriginalOutlineVertexMode.Expand,1,1))),
            [box.Outline.Materials[0],new(1,new(0,0,0,1),false)]);
        return new(surface,outline,box.OutlineOptions,new(OriginalAlphaBlendMode.Normal,true,false,EnableFrustumCulling:false));
    }

    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern nint CreateWindowExW(uint exStyle,string className,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint parameter);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
}
