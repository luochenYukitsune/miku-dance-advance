using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
[Trait("Category","Gpu")]
public sealed class D3D12RayGiTemporalTests
{
    private const int Width=129,Height=97;
    private static readonly RayGiOptions Gi=new(LightingMode.SoftwareRayTracing,Strength:.5f,SamplesPerPixel:2,Seed:31,
        MaxRayDistance:50,RayEpsilon:.001f,Composition:RayGiComposition.ToonPalette,Denoiser:RayGiDenoiser.TemporalSpatial);

    [Fact]
    public void IndependentLowSampleFramesConvergeToHighSampleReferenceWithoutColourDrift()
    {
        var frame=D3D12RayGiTests.Bounce();using var session=new D3D12FrameSession();
        using var referenceSession=new D3D12FrameSession();
        var reference=Capture(referenceSession,frame,Gi with {SamplesPerPixel=1024,Denoiser=RayGiDenoiser.Off,Seed=7141});
        var first=Capture(session,frame,Gi);
        for(int i=1;i<39;i++)using(session.Render(frame,Width,Height,Gi)){}
        var last=Capture(session,frame,Gi);
        Assert.False(session.RayGiStatistics!.TemporalResetHistory);
        Assert.Equal(40UL,session.RayGiStatistics.TemporalHistoryFrames);
        Assert.Equal(2,session.RayGiStatistics.DenoiseDispatchCount);
        double rawError=0,temporalError=0,spatialError=0,firstError=0;int count=0,accumulated=0;
        Vector3 filteredMean=default,referenceMean=default;
        for(int y=20;y<77;y++)for(int x=22;x<107;x++)
        {
            int i=y*Width+x;if(last.Normal[i].W<1||last.Normal[i].W>2)continue;
            Assert.Equal(last.Raw[i].W,last.Temporal[i].W);Assert.Equal(last.Raw[i].W,last.Filtered[i].W);
            rawError+=Vector3.DistanceSquared(Rgb(last.Raw[i]),Rgb(reference.Filtered[i]));
            temporalError+=Vector3.DistanceSquared(Rgb(last.Temporal[i]),Rgb(reference.Filtered[i]));
            spatialError+=Vector3.DistanceSquared(Rgb(last.Filtered[i]),Rgb(reference.Filtered[i]));
            firstError+=Vector3.DistanceSquared(Rgb(first.Filtered[i]),Rgb(reference.Filtered[i]));
            filteredMean+=Rgb(last.Filtered[i]);referenceMean+=Rgb(reference.Filtered[i]);
            if(last.Moments[i].Z==32)accumulated++;count++;
            Assert.InRange(last.Moments[i].Z,1,32);Assert.InRange(last.Moments[i].X,0,1);
            Assert.InRange(last.Moments[i].Y,0,1);Assert.True(float.IsFinite(last.Temporal[i].X));
        }
        Assert.True(count>1000);Assert.True(accumulated>count*.7,$"Only {accumulated}/{count} receiver pixels accumulated history.");
        Assert.True(temporalError<rawError*.5,$"Pre-spatial MSE {rawError/count} -> {temporalError/count}");
        Assert.True(spatialError<firstError*.8,$"Spatial-only first frame {firstError/count} -> temporal+spatial {spatialError/count}");
        for(int channel=0;channel<3;channel++)
            Assert.InRange(MathF.Abs(filteredMean[channel]-referenceMean[channel])/count,0,MathF.Max(.006f,referenceMean[channel]/count*.15f));
        Save("convergence",new{count,accumulated,rawMse=rawError/count,temporalMse=temporalError/count,
            filteredMse=spatialError/count,firstSpatialMse=firstError/count,filteredMean=filteredMean/count,referenceMean=referenceMean/count});
    }

    [Fact]
    public void SubmitCommitsCaptureAccumulatesAbortDoesNotAdvanceAndResetMatchesFresh()
    {
        using var session=new D3D12FrameSession();using var control=new D3D12FrameSession();
        var frame=D3D12RayGiTests.Bounce();
        Assert.Equal(control.Capture(frame,Width,Height,Gi).Rgba,session.Capture(frame,Width,Height,Gi).Rgba);
        long controlUploads=control.Runtime.BufferUploads;
        uint firstSeed=session.RayGiStatistics!.Seed;
        Assert.Equal(1UL,session.RayGiStatistics.TemporalHistoryFrames);
        // Replace the cached current triangle buffer in a recording that never submits.
        var mesh=frame.Surface.Snapshot;var vertices=mesh.Vertices.ToArray();
        vertices[0]=vertices[0] with {Position=vertices[0].Position+new Vector3(1,0,0)};
        var abandoned=new OriginalFrameScene(new OriginalStyleScene(mesh.WithDeformedVertices(vertices),frame.Surface.Materials,frame.Surface.Profile),
            options:frame.Options,materials:frame.Materials);
        Assert.Throws<OperationCanceledException>(()=>session.RenderForPresent(abandoned,Width,Height,Gi,(_,_)=>throw new OperationCanceledException("after temporal GI")));
        Assert.Null(session.RayGiStatistics);
        Assert.Equal(control.Capture(frame,Width,Height,Gi).Rgba,session.Capture(frame,Width,Height,Gi).Rgba);
        Assert.Equal(controlUploads,control.Runtime.BufferUploads);
        Assert.Equal(2UL,session.RayGiStatistics!.TemporalHistoryFrames);Assert.NotEqual(firstSeed,session.RayGiStatistics.Seed);
        var reset=Gi with {TemporalHistoryGeneration=11,TemporalCameraCutKey=7};
        var actual=session.Capture(frame,Width,Height,reset);using var fresh=new D3D12FrameSession();
        Assert.Equal(fresh.Capture(frame,Width,Height,reset).Rgba,actual.Rgba);
        Assert.True(session.RayGiStatistics.TemporalResetHistory);Assert.Equal("GenerationChanged",session.RayGiStatistics.TemporalResetReason);
        Assert.Equal(firstSeed,session.RayGiStatistics.Seed);
        using var output=session.Render(frame,Width,Height,reset);
        Assert.Equal(0,output.Statistics.ReadbackCount);Assert.Equal(1,output.Statistics.SubmissionCount);
        Assert.Equal(2UL,session.RayGiStatistics.TemporalHistoryFrames);
    }

    [Fact]
    public void DeformationCameraMotionAndDisocclusionUsePreviousSurfaceAndRejectNewlyExposedPixels()
    {
        using var session=new D3D12FrameSession();var frame=D3D12RayGiTests.Bounce();
        // Bounce's broad red transport wall is behind the camera. Place that same material/quad
        // in front of the receiver so this fixture really produces visible occlusion/disocclusion.
        var sourceMesh=frame.Surface.Snapshot;var initialVertices=sourceMesh.Vertices.ToArray();
        for(int i=4;i<initialVertices.Length;i++)initialVertices[i]=initialVertices[i] with
            {Position=new(initialVertices[i].Position.X*(.75f/15),initialVertices[i].Position.Y*(.75f/15),-2)};
        frame=new(new OriginalStyleScene(sourceMesh.WithDeformedVertices(initialVertices),frame.Surface.Materials,frame.Surface.Profile),
            options:frame.Options,materials:frame.Materials);
        for(int i=0;i<5;i++)using(session.Render(frame,Width,Height,Gi)){}
        var before=Capture(session,frame,Gi);
        int initialBlockerPixels=before.Normal.Count(normal=>normal.W>2);
        Assert.True(initialBlockerPixels>100,$"The front red blocker must visibly cover receiver pixels; actual {initialBlockerPixels}.");
        var mesh=frame.Surface.Snapshot;var vertices=mesh.Vertices.ToArray();
        // Move the blocker/secondary surface laterally and towards the camera; stable vertex correspondence.
        for(int i=4;i<vertices.Length;i++)vertices[i]=vertices[i] with {Position=vertices[i].Position+new Vector3(.7f,0,-.5f)};
        var changed=new OriginalFrameScene(new OriginalStyleScene(mesh.WithDeformedVertices(vertices),frame.Surface.Materials,frame.Surface.Profile),
            options:frame.Options,materials:frame.Materials);
        var after=Capture(session,changed,Gi);
        Assert.False(session.RayGiStatistics!.TemporalResetHistory);
        int reused=0,exposed=0;
        for(int y=1;y<Height-1;y++)for(int x=1;x<Width-1;x++)
        {
            int i=y*Width+x;
            // Independent visibility oracle: same camera, currently receiver, previously interior
            // to the blocker. Erode one pixel so all previous bilinear taps belong to the blocker.
            bool wasBlockerInterior=true;
            for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                wasBlockerInterior&=before.Normal[(y+dy)*Width+x+dx].W>2;
            if(after.Normal[i].W is not (>0 and <=2)||!wasBlockerInterior)continue;
            exposed++;Assert.Equal(1f,after.Moments[i].Z);Assert.Equal(after.Raw[i],after.Temporal[i]);
        }
        Assert.True(exposed>0,"The moving blocker must expose receiver pixels that reject incompatible history.");
        // Test camera movement separately: a screen-coordinate visibility mask is not a valid
        // disocclusion oracle after projection changes, but stable receivers must still reproject.
        var camera=(changed.Options?.CameraOverride??mesh.Camera);
        var movingCamera=new OriginalFrameScene(changed.Surface,options:changed.Options! with
            {CameraOverride=camera with {Eye=camera.Eye+new Vector3(.07f,0,-.2f),Target=camera.Target+new Vector3(.07f,0,0)}},materials:changed.Materials);
        var cameraAfter=Capture(session,movingCamera,Gi);
        Assert.False(session.RayGiStatistics!.TemporalResetHistory);
        for(int i=0;i<cameraAfter.Moments.Length;i++)
            if(cameraAfter.Normal[i].W is >0 and <=2&&cameraAfter.Moments[i].Z>1)reused++;
        Assert.True(reused>1000,$"Only {reused} moving receiver pixels retained history.");
        Save("deformation",new{initialBlockerPixels,reused,exposed});
    }

    [Fact]
    public void NormalOnlyDeformationRejectsChangedHemisphereAndSmallRotationKeepsHistory()
    {
        using var session=new D3D12FrameSession();var frame=D3D12RayGiTests.Bounce();
        var options=Gi with {SamplesPerPixel=64};
        for(int i=0;i<5;i++)using(session.Render(frame,Width,Height,options)){}
        var before=Capture(session,frame,options);
        OriginalFrameScene RotateNormals(float degrees)
        {
            var mesh=frame.Surface.Snapshot;var vertices=mesh.Vertices.ToArray();
            var rotation=Matrix4x4.CreateRotationY(degrees*MathF.PI/180);
            for(int i=0;i<4;i++)vertices[i]=vertices[i] with {Normal=Vector3.TransformNormal(vertices[i].Normal,rotation)};
            return new(new OriginalStyleScene(mesh.WithDeformedVertices(vertices),frame.Surface.Materials,frame.Surface.Profile),
                options:frame.Options,materials:frame.Materials);
        }
        var small=Capture(session,RotateNormals(5),options);
        Assert.False(session.RayGiStatistics!.TemporalResetHistory);
        var large=Capture(session,RotateNormals(80),options);
        Assert.False(session.RayGiStatistics!.TemporalResetHistory);
        int receivers=0,reused=0;double beforeRed=0,afterRed=0;
        for(int i=0;i<large.Moments.Length;i++)
        {
            if(large.Normal[i].W is not (>0 and <=2))continue;
            receivers++;if(small.Moments[i].Z>1)reused++;
            Assert.Equal(1f,large.Moments[i].Z);Assert.Equal(large.Raw[i],large.Temporal[i]);
            beforeRed+=before.Raw[i].X;afterRed+=large.Raw[i].X;
        }
        Assert.True(receivers>1000);Assert.True(reused>receivers*.9,$"Only {reused}/{receivers} mildly rotated pixels retained history.");
        Assert.True(Math.Abs(afterRed-beforeRed)>beforeRed*.1,
            $"A stationary receiver's incident lighting must change with its hemisphere: {beforeRed/receivers} -> {afterRed/receivers}.");
        Save("normal-deformation",new{receivers,reused,beforeMeanRed=beforeRed/receivers,afterMeanRed=afterRed/receivers});
    }

    [Fact]
    public void OffRemainsExactResizeAndModeChangeResetAndLongQueueResourcesPlateau()
    {
        using var session=new D3D12FrameSession();using var fresh=new D3D12FrameSession();var frame=D3D12RayGiTests.Bounce();
        var off=Gi with {Denoiser=RayGiDenoiser.Off};var reference=fresh.Capture(frame,Width,Height,off);
        for(int i=0;i<18;i++)using(session.Render(frame,Width,Height,Gi)){}
        Assert.Equal(reference.Rgba,session.Capture(frame,Width,Height,off).Rgba);
        Assert.Equal(RayGiDenoiser.Off,session.RayGiStatistics!.Denoiser);Assert.Equal(off.Seed,session.RayGiStatistics.Seed);
        var restarted=session.Capture(frame,Width,Height,Gi);using var clean=new D3D12FrameSession();
        Assert.Equal(clean.Capture(frame,Width,Height,Gi).Rgba,restarted.Rgba);Assert.True(session.RayGiStatistics!.TemporalResetHistory);
        using(session.Render(frame,91,73,Gi)){}Assert.Equal("Resized",session.RayGiStatistics.TemporalResetReason);
        int high=0,low=int.MaxValue;
        for(int i=0;i<30;i++)using(var output=session.Render(frame,91,73,Gi))
        {
            Assert.Equal(0,output.Statistics.ReadbackCount);Assert.InRange(output.Statistics.PendingFrames,0,3);
            if(i>=10){high=Math.Max(high,output.Statistics.LiveFrameResources);low=Math.Min(low,output.Statistics.LiveFrameResources);}
            Assert.Equal(91L*73*64,session.RayGiStatistics.TemporalHistoryBytes);
        }
        Assert.InRange(high-low,0,8);session.WaitForIdle();Assert.InRange(session.Runtime.LiveFrameResources,0,3);
        Save("resources",new{high,low,session.RayGiStatistics.TemporalHistoryBytes});
    }

    private sealed record Signals(Vector4[] Raw,Vector4[] Temporal,Vector4[] Filtered,Vector4[] Moments,Vector4[] Normal);
    private static Signals Capture(D3D12FrameSession session,OriginalFrameScene frame,RayGiOptions options)
    {
        string directory=Path.Combine(Environment.GetEnvironmentVariable("MMD_GI_TEMPORAL_ARTIFACTS")??Path.Combine(AppContext.BaseDirectory,"gi-temporal-test-artifacts"),Guid.NewGuid().ToString("N"));
        string? previous=Environment.GetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS");
        try
        {
            Environment.SetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS",directory);session.Capture(frame,Width,Height,options);
            Vector4[] Read(string prefix)=>MemoryMarshal.Cast<byte,Vector4>(File.ReadAllBytes(Directory.GetFiles(directory,"*.bin",SearchOption.AllDirectories)
                .Single(path=>Path.GetFileName(path).StartsWith(prefix,StringComparison.Ordinal)))).ToArray();
            var output=Read("indirect-");bool filtered=options.Denoiser!=RayGiDenoiser.Off;
            return new(filtered?Read("raw-indirect-"):output,options.Denoiser==RayGiDenoiser.TemporalSpatial?Read("temporal-indirect-"):output,
                output,options.Denoiser==RayGiDenoiser.TemporalSpatial?Read("temporal-moments-"):new Vector4[Width*Height],Read("normal-id-"));
        }
        finally{Environment.SetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS",previous);}
    }
    private static Vector3 Rgb(Vector4 value)=>new(value.X,value.Y,value.Z);
    private static void Save(string name,object value)
    {
        string directory=Environment.GetEnvironmentVariable("MMD_GI_TEMPORAL_ARTIFACTS")??Path.Combine(AppContext.BaseDirectory,"gi-temporal-test-artifacts");
        Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,name+".json"),JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true,IncludeFields=true}));
    }
}
