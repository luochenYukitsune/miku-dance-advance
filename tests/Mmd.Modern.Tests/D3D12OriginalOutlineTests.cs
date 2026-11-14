using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category","Gpu")]
public sealed class D3D12OriginalOutlineTests
{
    [Fact]
    public void NoOutlineFramePreservesAllExistingSurfaceFixtureBytes()
    {
        using var renderer=new D3D12OriginalStyleRenderer();
        foreach(var mode in new[]{OriginalSphereMode.None,OriginalSphereMode.Multiply,OriginalSphereMode.Add})
        foreach(float facing in new[]{-.25f,0,1/6f,1/3f,1})
        {
            var surface=D3D12OriginalStyleTests.Fixture(mode,facing);
            var direct=renderer.Render(surface,257,191);
            var wrapped=renderer.Render(new OriginalFrameScene(surface),257,191);
            Assert.Equal(direct.Rgba,wrapped.Rgba);
            Save($"surface-{mode}-{BitConverter.SingleToInt32Bits(facing):x8}",direct);
        }
    }

    [Fact]
    public void BoxOutlineChangesOnlyEdgePixelsAndWidthZeroOneTwoGrowOutward()
    {
        using var renderer=new D3D12OriginalStyleRenderer();
        var scene=Box();
        var off=renderer.Render(scene.Surface,257,191);
        Assert.Equal(off.Rgba,renderer.Render(new OriginalFrameScene(scene.Surface),257,191).Rgba);
        var zero=renderer.Render(WithWidth(scene,0),257,191);
        var one=renderer.Render(scene,257,191);
        var two=renderer.Render(WithWidth(scene,2),257,191);
        Assert.Equal(off.Rgba,zero.Rgba);
        Assert.Equal(Pixel(off,128,95),Pixel(one,128,95));
        Assert.Equal(Pixel(off,128,95),Pixel(two,128,95));
        Assert.Equal(Background,Pixel(off,78,95));
        Assert.Equal(Edge,Pixel(one,78,95));
        Assert.Equal(Background,Pixel(one,74,95));
        Assert.Equal(Edge,Pixel(two,74,95));
        int changed=0;
        for(int y=0;y<191;y++) for(int x=0;x<257;x++)
            if(Pixel(off,x,y)!=Pixel(one,x,y)) { changed++; Assert.Equal(Background,Pixel(off,x,y)); Assert.Equal(Edge,Pixel(one,x,y)); }
        Assert.True(changed>100);
        Save("box-off",off); Save("box-width0",zero); Save("box-width1",one); Save("box-width2",two);
        Assert.Equal(one.Rgba,renderer.Render(scene,257,191).Rgba);
        using var fresh=new D3D12OriginalStyleRenderer();
        Assert.Equal(one.Rgba,fresh.Render(scene,257,191).Rgba);
        Assert.Empty(renderer.LastDebugMessages);
        SaveDevice(renderer);
    }

    [Fact]
    public void EligibilityAndDisabledMaterialSkipEdgeWhileZeroAlphaIsStillWritten()
    {
        using var renderer=new D3D12OriginalStyleRenderer();
        var scene=Box(); var off=renderer.Render(scene.Surface,257,191);
        var disabled=new OriginalOutlineData(scene.Outline!.Vertices,[scene.Outline.Materials[0] with {DrawEnabled=false}]);
        Assert.Equal(off.Rgba,renderer.Render(new OriginalFrameScene(scene.Surface,disabled,scene.OutlineOptions),257,191).Rgba);
        Assert.Equal(off.Rgba,renderer.Render(new OriginalFrameScene(scene.Surface,scene.Outline,scene.OutlineOptions! with {Eligible=false}),257,191).Rgba);
        Assert.Equal(off.Rgba,renderer.Render(new OriginalFrameScene(scene.Surface,scene.Outline,scene.OutlineOptions! with {ModelWidth=0}),257,191).Rgba);
        var transparent=new OriginalOutlineData(scene.Outline.Vertices,[scene.Outline.Materials[0] with {Color=new(1,0,0,0)}]);
        var alpha=renderer.Render(new OriginalFrameScene(scene.Surface,transparent,scene.OutlineOptions),257,191);
        Assert.Equal((255,0,0,0),Pixel(alpha,78,95)); Save("box-alpha-preserved",alpha);
    }

    [Fact]
    public void FrontCullingIsReversedForRhCameraAndThinSheetUsesBackFaceOnly()
    {
        using var renderer=new D3D12OriginalStyleRenderer();
        OriginalFrameScene Sheet(bool reverse)
        {
            var b=Box(); var s=b.Surface.Snapshot;
            var vertices=s.Vertices[..4].ToArray();
            uint[] indices=reverse?[0,1,2,0,2,3]:[0,2,1,0,3,2];
            var mesh=new SceneSnapshot(vertices,indices,[s.Materials[0] with {IndexCount=6}],camera:s.Camera);
            var surface=new OriginalStyleScene(mesh,[new(Vector3.One,DoubleSided:false)],b.Surface.Profile);
            var data=new OriginalOutlineData(b.Outline!.Vertices.Take(4),b.Outline.Materials);
            return new(surface,data,b.OutlineOptions);
        }
        var front=Sheet(false); var back=Sheet(true);
        var frontFrame=renderer.Render(front,257,191); var backFrame=renderer.Render(back,257,191);
        Assert.Equal(renderer.Render(front.Surface,257,191).Rgba,frontFrame.Rgba);
        Assert.NotEqual(Background,Pixel(frontFrame,128,95));
        Assert.Equal(Background,Pixel(renderer.Render(back.Surface,257,191),128,95));
        Assert.Equal(Edge,Pixel(backFrame,128,95));
        Save("sheet-front",frontFrame); Save("sheet-back",backFrame);
    }

    [Fact]
    public void OccluderDepthProtectsInteriorAndDisabledFirstMaterialStillAdvancesIndexRange()
    {
        using var renderer=new D3D12OriginalStyleRenderer();
        var b=Box(); var s=b.Surface.Snapshot;
        // A foreground quad covers the complete left half of the hull and its outline.
        SceneVertex[] cover=[new(new(0,-2,-1),-Vector3.UnitZ,Vector2.Zero),new(new(2,-2,-1),-Vector3.UnitZ,Vector2.Zero),new(new(2,2,-1),-Vector3.UnitZ,Vector2.Zero),new(new(0,2,-1),-Vector3.UnitZ,Vector2.Zero)];
        var vertices=cover.Concat(s.Vertices.ToArray()).ToArray();
        uint[] indices=[0,2,1,0,3,2,..s.Indices.ToArray().Select(i=>i+4)];
        SceneMaterial[] materials=[new("occluder",0,6,new(0,1,0,1),Vector3.Zero,Vector3.Zero,1),s.Materials[0] with {StartIndex=6}];
        var mesh=new SceneSnapshot(vertices,indices,materials,camera:s.Camera);
        var surface=new OriginalStyleScene(mesh,[new(Vector3.One),new(Vector3.One)],b.Surface.Profile);
        var outline=new OriginalOutlineData(cover.Select(v=>new OriginalOutlineVertex(v.Position,v.Normal,OriginalOutlineVertexMode.Expand,1,0)).Concat(b.Outline!.Vertices.Select(v=>v with {SelectedMaterialIndex=1})),[new(1,Vector4.One,false),b.Outline.Materials[0]]);
        var result=renderer.Render(new OriginalFrameScene(surface,outline,b.OutlineOptions),257,191);
        var off=renderer.Render(surface,257,191);
        Assert.Equal(Pixel(off,78,95),Pixel(result,78,95));
        Assert.Equal((0,255,0,255),Pixel(result,78,95));
        Assert.Equal(Edge,Pixel(result,178,95));
        Assert.Equal(Pixel(off,150,95),Pixel(result,150,95));
        Save("box-occluded",result);
    }

    public static OriginalFrameScene Box()
    {
        Vector3[] p=[new(-1,-1,0),new(1,-1,0),new(1,1,0),new(-1,1,0),new(-1,-1,.12f),new(1,-1,.12f),new(1,1,.12f),new(-1,1,.12f)];
        var vertices=p.Select((v,i)=>new SceneVertex(v,new(v.X,v.Y,i<4?-.2f:.2f),Vector2.Zero)).ToArray();
        uint[] indices=[0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,1,2,6,1,6,5,2,3,7,2,7,6,3,0,4,3,4,7];
        var mesh=new SceneSnapshot(vertices,indices,[new("box",0,36,Vector4.One,Vector3.Zero,Vector3.Zero,1)],camera:new(new(0,0,-5),Vector3.Zero,Vector3.UnitY,4,.1f,20));
        var surface=new OriginalStyleScene(mesh,[new(Vector3.One)],new(Vector3.One,Vector3.Zero,Matrix4x4.Identity,OriginalTextureSampling.PointClamp));
        var outline=new OriginalOutlineData(vertices.Select(v=>new OriginalOutlineVertex(v.Position,v.Normal,OriginalOutlineVertexMode.Expand,1,0)),[new(1,new(0,0,0,1))]);
        return new(surface,outline,OriginalOutlineTests.Options(20));
    }

    private static OriginalFrameScene WithWidth(OriginalFrameScene frame,float width)=>new(frame.Surface,new OriginalOutlineData(frame.Outline!.Vertices,[frame.Outline.Materials[0] with {Width=width}]),frame.OutlineOptions);
    private static readonly (int,int,int,int) Background=(20,24,32,255),Edge=(0,0,0,255);
    private static (int,int,int,int) Pixel(RenderedFrame frame,int x,int y) { int i=(y*frame.Width+x)*4; return(frame.Rgba[i],frame.Rgba[i+1],frame.Rgba[i+2],frame.Rgba[i+3]); }
    private static void Save(string name,RenderedFrame frame)
    {
        string? path=Environment.GetEnvironmentVariable("MMD_OUTLINE_ARTIFACTS"); if(path is null) return;
        Directory.CreateDirectory(path); File.WriteAllBytes(Path.Combine(path,name+".rgba"),frame.Rgba);
        using var output=File.Create(Path.Combine(path,name+".ppm"));
        var header=System.Text.Encoding.ASCII.GetBytes($"P6\n{frame.Width} {frame.Height}\n255\n"); output.Write(header);
        for(int i=0;i<frame.Rgba.Length;i+=4) output.Write(frame.Rgba,i,3);
    }
    private static void SaveDevice(D3D12OriginalStyleRenderer renderer)
    {
        string? path=Environment.GetEnvironmentVariable("MMD_OUTLINE_ARTIFACTS"); if(path is null) return;
        File.WriteAllText(Path.Combine(path,"device.json"),System.Text.Json.JsonSerializer.Serialize(new{renderer.AdapterName,renderer.FeatureLevel,renderer.IsWarp,renderer.DebugRequested,renderer.DebugLayerEnabled,renderer.DebugInfoQueueAvailable,renderer.LastDebugMessages,OS=Environment.OSVersion.ToString(),Runtime=Environment.Version.ToString(),Width=257,Height=191,Sampling=1}));
    }
}
