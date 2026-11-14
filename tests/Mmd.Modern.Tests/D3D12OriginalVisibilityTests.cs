using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category","Gpu")]
public sealed class D3D12OriginalVisibilityTests
{
    [Fact]
    public void OffscreenMaterialIsNotDrawnAndRepeatedFramesReuseImmutableBounds()
    {
        using var session=new D3D12FrameSession();
        var visible=D3D12OriginalFrameTests.Alpha(false,0,0);
        var f=AppendOffscreenQuad(visible);
        var off=session.Capture(Toggle(f,false),257,191);var offStats=session.Statistics!;
        Assert.False(session.VisibilityStatistics!.Enabled);Assert.Equal(0,session.VisibilityStatistics.SurfaceTested);
        var on=session.Capture(f,257,191);var stats=session.VisibilityStatistics!;
        Assert.Equal(off.Rgba,on.Rgba);
        Assert.Equal(3,stats.SurfaceTested);Assert.Equal(1,stats.SurfaceCulled);Assert.Equal(2,stats.SurfaceDrawn);
        Assert.Equal(offStats.SceneDrawCalls-1,session.Statistics!.SceneDrawCalls);
        for(int i=0;i<3;i++) {using var output=session.Render(f,257,191);Assert.Equal(2,output.Statistics.SceneDrawCalls);}
        Assert.Equal(stats.SurfaceBoundsBuilds,session.VisibilityStatistics!.SurfaceBoundsBuilds);
        Assert.Equal(1,stats.SurfaceBoundsBuilds);
        Assert.Equal(0,session.Statistics!.ReadbackCount);
        Save("offscreen",off,on,session,offStats.SceneDrawCalls);
        session.Capture(new OriginalFrameScene(visible.Surface),257,191);
        Assert.Null(session.VisibilityStatistics);
    }

    [Fact]
    public void CameraOutsideCasterStillShadowsVisibleReceiver()
    {
        using var session=new D3D12FrameSession();
        var original=D3D12OriginalFrameTests.Shadow(true);var s=original.Surface.Snapshot;var vertices=s.Vertices.ToArray();
        for(int i=4;i<8;i++)vertices[i]=vertices[i] with {Position=vertices[i].Position+new Vector3(4,0,0)};
        var surface=new OriginalStyleScene(new(vertices,s.Indices,s.Materials,camera:s.Camera,lightDirection:s.LightDirection),original.Surface.Materials,original.Surface.Profile);
        var light=Matrix4x4.Identity;light.M11=.5f;light.M22=.5f;light.M31=-2;light.M33=-.1f;light.M43=.5f;
        var frame=new OriginalFrameScene(surface,options:original.Options! with {SelfShadow=original.Options.SelfShadow! with {LightViewProjection=light}},materials:original.Materials);
        var off=session.Capture(Toggle(frame,false),257,191);var offDraws=session.Statistics!.SceneDrawCalls;
        var on=session.Capture(frame,257,191);Assert.Equal(off.Rgba,on.Rgba);
        Assert.Equal(1,session.VisibilityStatistics!.SurfaceCulled);Assert.Equal(1,session.VisibilityStatistics.ShadowDrawn);
        Assert.Equal(offDraws-1,session.Statistics!.SceneDrawCalls);
        D3D12OriginalFrameTests.AssertPixel(on,128,95,new(.2f,.2f,.2f,1));
        Save("outside-caster",off,on,session,offDraws);
        var noCast=new OriginalFrameScene(surface,options:frame.Options,materials:[frame.Materials[0],frame.Materials[1] with {CastSelfShadow=false}]);
        var noShadow=session.Capture(noCast,257,191);D3D12OriginalFrameTests.AssertPixel(noShadow,128,95,Vector4.One);
        Assert.NotEqual(on.Rgba,noShadow.Rgba);
    }

    [Fact]
    public void CameraOutsideGroundCasterRetainsProjectionInsideImage()
    {
        using var session=new D3D12FrameSession();var f=D3D12OriginalFrameTests.Ground(true);var s=f.Surface.Snapshot;
        var vertices=s.Vertices.ToArray().Select(v=>v with {Position=v.Position+new Vector3(4,0,0)}).ToArray();
        var surface=new OriginalStyleScene(new(vertices,s.Indices,s.Materials,camera:s.Camera),f.Surface.Materials,f.Surface.Profile);
        var outline=new OriginalOutlineData(f.Outline!.Vertices.Select(v=>v with {Position=v.Position+new Vector3(4,0,0)}),f.Outline.Materials);
        var frame=new OriginalFrameScene(surface,outline,f.OutlineOptions,f.Options! with {GroundShadow=f.Options.GroundShadow! with {ProjectionDirection=new(-4,-1,0)}},
            f.Materials.Select(m=>m with {SurfaceEnabled=true}));
        var off=session.Capture(Toggle(frame,false),257,191);var offDraws=session.Statistics!.SceneDrawCalls;
        var on=session.Capture(frame,257,191);Assert.Equal(off.Rgba,on.Rgba);
        Assert.Equal(2,session.VisibilityStatistics!.SurfaceCulled);Assert.Equal(2,session.VisibilityStatistics.GroundDrawn);
        Assert.Equal(offDraws-2,session.Statistics!.SceneDrawCalls);
        Save("outside-ground-caster",off,on,session,offDraws);
        var noGround=session.Capture(new OriginalFrameScene(surface,outline,f.OutlineOptions,frame.Options! with {GroundShadow=null},frame.Materials),257,191);
        Assert.NotEqual(on.Rgba,noGround.Rgba);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ActualExpandedOrInwardOutlineCanEnterCameraWhileSurfaceIsOutside(bool inward)
    {
        using var session=new D3D12FrameSession();var box=D3D12OriginalOutlineTests.Box();var s=box.Surface.Snapshot;
        var vertices=s.Vertices.ToArray().Select(v=>v with {Position=v.Position+new Vector3(4,0,0)}).ToArray();
        var surface=new OriginalStyleScene(new(vertices,s.Indices,s.Materials,camera:s.Camera),box.Surface.Materials,box.Surface.Profile);
        var edge=new OriginalOutlineData(box.Outline!.Vertices.Select(v=>v with {Position=v.Position+new Vector3(4,0,0),
            Mode=inward?OriginalOutlineVertexMode.Inward:OriginalOutlineVertexMode.Expand,Normal=inward?new(1000,0,0):v.Normal}),box.Outline.Materials);
        var frame=new OriginalFrameScene(surface,edge,box.OutlineOptions! with {ReferencePoint=new(4,0,0,1),ModelWidth=200},D3D12OriginalFrameTests.Options());
        var off=session.Capture(Toggle(frame,false),257,191);var offDraws=session.Statistics!.SceneDrawCalls;
        var on=session.Capture(frame,257,191);Assert.Equal(off.Rgba,on.Rgba);
        Assert.Equal(1,session.VisibilityStatistics!.SurfaceCulled);Assert.Equal(0,session.VisibilityStatistics.OutlineCulled);
        Assert.Equal(1,session.VisibilityStatistics.OutlineDrawn);
        Assert.Equal(offDraws-1,session.Statistics!.SceneDrawCalls);
        Save(inward?"inward-enters":"expanded-enters",off,on,session,offDraws);
        var noEdge=session.Capture(new OriginalFrameScene(surface,options:frame.Options),257,191);Assert.NotEqual(on.Rgba,noEdge.Rgba);
        // Turning the camera away rejects both independent bounds without rebuilding either stream.
        var stats=session.VisibilityStatistics!;
        var camera=s.Camera with {Eye=s.Camera.Eye+new Vector3(100,0,0),Target=s.Camera.Target+new Vector3(100,0,0)};
        session.Capture(new OriginalFrameScene(surface,edge,frame.OutlineOptions,frame.Options! with {CameraOverride=camera}),257,191);
        Assert.Equal(1,session.VisibilityStatistics!.OutlineCulled);
        Assert.Equal(1,session.VisibilityStatistics.OutlineBoundsBuilds);
        Assert.Equal(stats.SurfaceBoundsBuilds,session.VisibilityStatistics.SurfaceBoundsBuilds);
    }

    [Fact]
    public void TransparentDoubleSidedSourceOrderAndNonidentityWorldAreUnchanged()
    {
        using var session=new D3D12FrameSession();var f=AppendOffscreenQuad(D3D12OriginalFrameTests.Alpha(false,0,0,false));var s=f.Surface.Snapshot;
        var indices=s.Indices.ToArray();for(int i=6;i<12;i+=3)(indices[i+1],indices[i+2])=(indices[i+2],indices[i+1]);
        var world=Matrix4x4.CreateScale(1.25f,.75f,1)*Matrix4x4.CreateTranslation(7,2,0);
        var camera=s.Camera with {Eye=s.Camera.Eye+new Vector3(7,2,0),Target=s.Camera.Target+new Vector3(7,2,0)};
        var surface=new OriginalStyleScene(new(s.Vertices,indices,s.Materials,world:world,camera:camera,lightDirection:s.LightDirection),
            f.Surface.Materials.Select(m=>m with {DoubleSided=true}),f.Surface.Profile);
        var frame=new OriginalFrameScene(surface,options:f.Options);
        var off=session.Capture(Toggle(frame,false),257,191);var offDraws=session.Statistics!.SceneDrawCalls;
        var on=session.Capture(frame,257,191);Assert.Equal(off.Rgba,on.Rgba);
        D3D12OriginalFrameTests.AssertPixel(on,128,95,new(.2696f,.02353f,.53137f,1));
        Assert.Equal(1,session.VisibilityStatistics!.SurfaceCulled);Save("alpha-double-sided-world",off,on,session,offDraws);
    }

    private static OriginalFrameScene Toggle(OriginalFrameScene frame,bool on)=>new(frame.Surface,frame.Outline,frame.OutlineOptions,frame.Options! with {EnableFrustumCulling=on},frame.Materials);
    private static OriginalFrameScene AppendOffscreenQuad(OriginalFrameScene frame)
    {
        var s=frame.Surface.Snapshot;int n=s.Vertices.Length;
        var vertices=s.Vertices.ToArray().Concat(s.Vertices[..4].ToArray().Select(v=>v with {Position=v.Position+new Vector3(20,0,0)})).ToArray();
        uint[] indices=[..s.Indices.ToArray(),(uint)n,(uint)n+2,(uint)n+1,(uint)n,(uint)n+3,(uint)n+2];
        SceneMaterial[] materials=[..s.Materials,s.Materials[0] with {Name="outside",StartIndex=s.Indices.Length}];
        var surface=new OriginalStyleScene(new(vertices,indices,materials,camera:s.Camera,lightDirection:s.LightDirection),[..frame.Surface.Materials,frame.Surface.Materials[0]],frame.Surface.Profile);
        return new(surface,options:frame.Options);
    }
    private static void Save(string name,RenderedFrame off,RenderedFrame on,D3D12FrameSession session,int offDraws)
    {
        string? path=Environment.GetEnvironmentVariable("MMD_VISIBILITY_ARTIFACTS");if(path is null)return;
        Directory.CreateDirectory(path);File.WriteAllBytes(Path.Combine(path,name+"-off.rgba"),off.Rgba);File.WriteAllBytes(Path.Combine(path,name+"-on.rgba"),on.Rgba);
        D3D12OriginalFrameTests.Save(name,on);
        using(var output=File.Create(Path.Combine(path,name+"-on.ppm")))
        {
            output.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{on.Width} {on.Height}\n255\n"));
            for(int i=0;i<on.Rgba.Length;i+=4)output.Write(on.Rgba,i,3);
        }
        File.WriteAllText(Path.Combine(path,name+".json"),JsonSerializer.Serialize(new {session.AdapterName,session.IsWarp,session.DebugLayerEnabled,session.DebugInfoQueueAvailable,
            offDraws,onDraws=session.Statistics!.SceneDrawCalls,visibility=session.VisibilityStatistics,offSha=Convert.ToHexString(SHA256.HashData(off.Rgba)),onSha=Convert.ToHexString(SHA256.HashData(on.Rgba))},new JsonSerializerOptions{WriteIndented=true}));
    }
}
