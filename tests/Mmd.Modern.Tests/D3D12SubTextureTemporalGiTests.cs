using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category","Gpu")]
public sealed class D3D12SubTextureTemporalGiTests
{
    private const int Width=65,Height=49;

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void ConsumedUv1ValuesResetHistoryWhileEqualFreshStreamsAndUnusedChannelsRetainIt(LightingMode mode)
    {
        using var session=new D3D12FrameSession();Require(session,mode);
        var options=Options(mode);var source=Fixture();
        var first=WithUvs(source,new(0,.5f));
        Render(session,first,options,1,"FirstFrame");
        var equal=WithUvs(source,new(0,.5f));
        Assert.NotSame(first.Surface.Snapshot.AdditionalUvs,equal.Surface.Snapshot.AdditionalUvs);
        Assert.NotEqual(RayGiScene.FromOriginal(first).SubTextureUvIdentity,RayGiScene.FromOriginal(equal).SubTextureUvIdentity);
        Render(session,equal,options,2,"None");
        // Numeric equality includes signed zero. Neither author channel count nor any
        // values outside the consumed UV1.xy triangle table affect this GI history.
        Render(session,WithUvs(source,new(-0f,.5f),extras:900,channels:1),options,3,"None");
        var changedX=WithUvs(source,new(.75f,.5f));
        AssertFreshReset(session,changedX,options,"TextureCoordinatesChanged");
        Render(session,WithUvs(source,new(.75f,.5f),extras:700),options,2,"None");
        AssertFreshReset(session,WithUvs(source,new(.75f,.25f)),options,"TextureCoordinatesChanged");
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing,false)]
    [InlineData(LightingMode.SoftwareRayTracing,true)]
    [InlineData(LightingMode.HardwareRayTracing,false)]
    [InlineData(LightingMode.HardwareRayTracing,true)]
    public void OnlyEffectiveTwoMapToonWeightAndInheritedCompositionResetHistory(LightingMode mode,bool noDiffuse)
    {
        using var session=new D3D12FrameSession();Require(session,mode);
        var options=Options(mode);var source=Fixture(noDiffuse);
        Render(session,WithState(source,.5f,0,false),options,1,"FirstFrame");
        // The shader consumes the sum, so a different factor decomposition is equal.
        Render(session,WithState(source,.25f,.25f,false),options,2,"None");
        var changedWeight=WithState(source,1,0,false);
        if(noDiffuse)Render(session,changedWeight,options,3,"None");
        else AssertFreshReset(session,changedWeight,options,"MaterialChanged");
        var inheritedAdd=WithState(source,1,0,true);
        if(noDiffuse)Render(session,inheritedAdd,options,4,"None");
        else AssertFreshReset(session,inheritedAdd,options,"MaterialChanged");
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing,false)]
    [InlineData(LightingMode.SoftwareRayTracing,true)]
    [InlineData(LightingMode.HardwareRayTracing,false)]
    [InlineData(LightingMode.HardwareRayTracing,true)]
    public void TemporalFramesKeepCurrentGeneratedShadowAlphaWhenToonWeightChanges(LightingMode mode,bool noDiffuse)
    {
        using var session=new D3D12FrameSession();Require(session,mode);
        var options=Options(mode);var source=ShadowFixture(noDiffuse);
        var shadowed=session.Capture(source,Width,Height,options);
        AssertHistory(session,options,1,"FirstFrame");
        Assert.Equal(1,session.VisibilityStatistics!.ShadowDrawn);
        int alpha=(Height/2*Width+Width/2)*4+3;
        Assert.InRange(shadowed.Rgba[alpha],noDiffuse?254:50,noDiffuse?255:52);
        var changed=WithState(source,1,0,false);
        var current=session.Capture(changed,Width,Height,options);
        AssertHistory(session,options,noDiffuse?2UL:1UL,noDiffuse?"None":"MaterialChanged");
        Assert.Equal(255,current.Rgba[alpha]);
        Assert.Equal(1,session.VisibilityStatistics!.ShadowDrawn);
        using var fresh=new D3D12FrameSession();
        // The centre's actual alpha is current surface coverage, never history alpha.
        Assert.Equal(fresh.Capture(changed,Width,Height,options).Rgba[alpha],current.Rgba[alpha]);
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void RejectionAndAbortedUvResetPreserveHistoryAndAnimatedResourcesStayBounded(LightingMode mode)
    {
        using var session=new D3D12FrameSession();using var control=new D3D12FrameSession();Require(session,mode);
        var options=Options(mode);var source=Fixture();
        Assert.Equal(control.Capture(source,Width,Height,options).Rgba,session.Capture(source,Width,Height,options).Rgba);
        long frameId=session.Runtime.FrameId,submissions=session.Runtime.Submissions;
        // Mode 3 now has shared temporal coverage. Retain this rejection/rollback check
        // against the still unsupported transparent-background combination, before SDK lookup.
        var transparentBackground=Copy(source,options:source.Options! with {BackgroundColor=new(0,0,0,.5f)});
        foreach(var vendor in new[]{D3D12TemporalUpscalingMode.Fsr,D3D12TemporalUpscalingMode.XeSs,D3D12TemporalUpscalingMode.Dlss})
            Assert.Contains("opaque composited background",Assert.Throws<NotSupportedException>(()=>session.Render(transparentBackground,Width,Height,options,
                temporalUpscaling:new(){Mode=vendor,LoaderPath=Path.Combine(Path.GetTempPath(),"missing-subtexture-runtime.dll")})).Message);
        foreach(var invalid in new[]{Copy(source,options:source.Options! with {Transp=true}),Copy(source,options:source.Options! with {WriteDepth=false})})
            Assert.Throws<NotSupportedException>(()=>session.Render(invalid,Width,Height,options));
        Assert.Equal(frameId,session.Runtime.FrameId);Assert.Equal(submissions,session.Runtime.Submissions);
        Assert.False(session.Runtime.IsRecording);
        Assert.Throws<OperationCanceledException>(()=>session.RenderForPresent(WithUvs(source,new(.75f,.5f)),Width,Height,options,
            (_,_)=>throw new OperationCanceledException("Abort an unsubmitted UV1 history reset.")));
        Assert.False(session.Runtime.IsRecording);Assert.Equal(submissions,session.Runtime.Submissions);
        Assert.Equal(control.Capture(source,Width,Height,options).Rgba,session.Capture(source,Width,Height,options).Rgba);
        AssertHistory(session,options,2,"None");
        Assert.Equal(control.RayGiStatistics!.Seed,session.RayGiStatistics!.Seed);

        using(session.Render(source,Width+2,Height,options)){}
        AssertHistory(session,options,1,"Resized",Width+2);
        int cached=session.Statistics!.CachedResources,low=int.MaxValue,high=0;
        long shaders=session.Runtime.ShaderCompilations,pipelines=session.Runtime.PipelineCreations;
        for(int i=0;i<24;i++)
        {
            using var gpu=session.Render(WithUvs(source,new(.2f+i*.01f,.5f)),Width+2,Height,options);
            Assert.Equal(0,gpu.Statistics.ReadbackCount);Assert.InRange(gpu.Statistics.PendingFrames,0,3);
            Assert.InRange(gpu.Statistics.CachedResources,0,cached+2);
            Assert.Equal(shaders,gpu.Statistics.ShaderCompilations);Assert.Equal(pipelines,gpu.Statistics.PipelineCreations);
            AssertHistory(session,options,1,"TextureCoordinatesChanged",Width+2);
            if(i>=10){low=Math.Min(low,gpu.Statistics.LiveFrameResources);high=Math.Max(high,gpu.Statistics.LiveFrameResources);}
        }
        Assert.InRange(high-low,0,8);
        var ordinaryBindings=source.Surface.Materials.ToArray();ordinaryBindings[0]=ordinaryBindings[0] with {SphereMode=OriginalSphereMode.Multiply};
        var ordinary=Copy(source,new(source.Surface.Snapshot,ordinaryBindings,source.Surface.Profile));
        using(var fresh=new D3D12FrameSession())
            Assert.Equal(fresh.Capture(ordinary,Width,Height,options).Rgba,session.Capture(ordinary,Width,Height,options).Rgba);
        AssertHistory(session,options,1,"Resized");
        var ssaa=Copy(source,options:source.Options! with {SpatialSamples=2});
        using(var combined=session.Render(ssaa,Width,Height,options))
        {
            Assert.Equal(1,combined.Statistics.SubmissionCount);Assert.Equal(0,combined.Statistics.ReadbackCount);
            Assert.Equal(2,session.RayGiStatistics!.DispatchCount);Assert.Equal(4,session.RayGiStatistics.DenoiseDispatchCount);
            Assert.Equal(1UL,session.RayGiStatistics.TemporalHistoryFrames);
        }
        using(session.Render(ssaa,Width,Height,options)){}
        Assert.Equal(2UL,session.RayGiStatistics!.TemporalHistoryFrames);
        session.WaitForIdle();Assert.InRange(session.Runtime.LiveFrameResources,0,3);
    }

    private static RayGiOptions Options(LightingMode mode)=>D3D12GiTextureFactorTests.Options(mode,true) with
        {SamplesPerPixel=2,Denoiser=RayGiDenoiser.TemporalSpatial};

    private static void Render(D3D12FrameSession session,OriginalFrameScene frame,RayGiOptions options,ulong frames,string reason)
    {
        using var gpu=session.Render(frame,Width,Height,options);
        Assert.Equal(0,gpu.Statistics.ReadbackCount);Assert.Equal(1,gpu.Statistics.SubmissionCount);
        AssertHistory(session,options,frames,reason);
    }

    private static void AssertFreshReset(D3D12FrameSession session,OriginalFrameScene frame,RayGiOptions options,string reason)
    {
        var actual=session.Capture(frame,Width,Height,options);
        AssertHistory(session,options,1,reason);
        using var fresh=new D3D12FrameSession();Assert.Equal(fresh.Capture(frame,Width,Height,options).Rgba,actual.Rgba);
    }

    private static void AssertHistory(D3D12FrameSession session,RayGiOptions options,ulong frames,string reason,int width=Width)
    {
        var stats=Assert.IsType<D3D12RayGiStatistics>(session.RayGiStatistics);
        Assert.Equal(options.Mode,stats.Requested);Assert.Equal(options.Mode,stats.Effective);
        Assert.Equal(RayGiDenoiser.TemporalSpatial,stats.Denoiser);
        Assert.Equal(1,stats.DispatchCount);Assert.Equal(2,stats.DenoiseDispatchCount);
        Assert.Equal(frames,stats.TemporalHistoryFrames);Assert.Equal(reason!="None",stats.TemporalResetHistory);
        Assert.Equal(reason,stats.TemporalResetReason);
        Assert.Equal(unchecked(options.Seed+(uint)(frames-1)*0x9e3779b9u),stats.Seed);
        Assert.Equal(width*Height*64L,stats.TemporalHistoryBytes);
        if(options.Mode==LightingMode.HardwareRayTracing)Assert.StartsWith("DXR-raw-",session.RayGiBackendIdentity);
        else Assert.Equal("GPU-BVH",session.RayGiBackendIdentity);
    }

    private static OriginalFrameScene Fixture(bool noDiffuse=false)
    {
        var source=D3D12GiTextureFactorTests.Fixture();var mesh=source.Surface.Snapshot;
        var materials=mesh.Materials.ToArray();if(noDiffuse)materials[0]=materials[0] with {DiffuseTexture=-1};
        var textures=mesh.Textures.ToArray();textures[2]=new("UV1 opaque and cutout",2,1,[128,128,128,255,128,128,128,0]);
        var stream=new SceneAdditionalUvs(4,Enumerable.Repeat(new SceneAdditionalUvVertex(new(.25f,.5f,19,23),new(29),new(31),new(37)),mesh.Vertices.Length).ToArray());
        var snapshot=new SceneSnapshot(mesh.Vertices,mesh.Indices,materials,textures,mesh.World,mesh.Camera,mesh.LightDirection,additionalUvs:stream);
        var bindings=source.Surface.Materials.ToArray();bindings[0]=bindings[0] with {SphereTexture=2,SphereMode=OriginalSphereMode.SubTexture};
        return Copy(source,new(snapshot,bindings,source.Surface.Profile));
    }

    private static OriginalFrameScene ShadowFixture(bool noDiffuse)
    {
        var source=D3D12OriginalFrameTests.Shadow(true);var mesh=source.Surface.Snapshot;
        var materials=mesh.Materials.ToArray();materials[0]=materials[0] with {DiffuseTexture=noDiffuse?-1:0};
        var stream=new SceneAdditionalUvs(1,Enumerable.Repeat(new SceneAdditionalUvVertex(new(.25f,.75f,19,23),default,default,default),mesh.Vertices.Length).ToArray());
        var snapshot=new SceneSnapshot(mesh.Vertices,mesh.Indices,materials,[new("white",1,1,[255,255,255,255])],
            mesh.World,mesh.Camera,mesh.LightDirection,additionalUvs:stream);
        var bindings=source.Surface.Materials.ToArray();bindings[0]=bindings[0] with {SphereTexture=0,SphereMode=OriginalSphereMode.SubTexture};
        var flags=source.Materials.ToArray();flags[1]=flags[1] with {SurfaceEnabled=false};
        var frame=new OriginalFrameScene(new(snapshot,bindings,source.Surface.Profile),options:source.Options! with {BackgroundColor=Vector4.Zero},materials:flags);
        return WithState(frame,.2f,0,false);
    }

    private static OriginalFrameScene WithUvs(OriginalFrameScene frame,Vector2 xy,float extras=100,int channels=4)
    {
        var mesh=frame.Surface.Snapshot;
        var stream=new SceneAdditionalUvs(channels,Enumerable.Range(0,mesh.Vertices.Length).Select(i=>
            new SceneAdditionalUvVertex(new(xy,extras+i,-extras-i),new(extras+7),new(extras+11),new(extras+13))).ToArray());
        return Copy(frame,new(mesh.WithAdditionalUvs(stream),frame.Surface.Materials,frame.Surface.Profile));
    }

    private static OriginalFrameScene WithState(OriginalFrameScene frame,float multiplyW,float addW,bool inheritedAdd)
    {
        var bindings=frame.Surface.Materials.ToArray();
        bindings[0]=bindings[0] with {SubTextureAdditive=inheritedAdd,
            TextureFactors=bindings[0].TextureFactors with {Toon=new(new(1,1,1,multiplyW),new(0,0,0,addW))}};
        return Copy(frame,new(frame.Surface.Snapshot,bindings,frame.Surface.Profile));
    }

    private static OriginalFrameScene Copy(OriginalFrameScene frame,OriginalStyleScene? surface=null,OriginalFrameOptions? options=null)
        =>new(surface??frame.Surface,frame.Outline,frame.OutlineOptions,options??frame.Options,frame.Materials,frame.Lighting,frame.EditorOverlay);

    private static void Require(D3D12FrameSession session,LightingMode mode)
    {
        var support=session.QueryRayGiCapabilities();
        Assert.True(mode==LightingMode.HardwareRayTracing?support.HardwareAvailable:support.SoftwareAvailable,support.UnavailableReason);
        Assert.False(session.IsWarp);
    }
}
