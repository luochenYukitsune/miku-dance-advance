using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;
namespace Mmd.Modern.Tests;

[Trait("Category","Gpu")]
public sealed class D3D12OriginalFrameTests
{
    internal static OriginalFrameOptions Options(bool depth=true,bool transp=false,OriginalSelfShadowOptions? shadow=null,OriginalGroundShadowOptions? ground=null)
        =>new(OriginalAlphaBlendMode.Normal,depth,transp,shadow,ground);
    [Fact] public void AlphaUsesSourceOrderDepthWriteAndSeparateMaxAlpha()
    {
        using var r=new D3D12OriginalStyleRenderer();
        var redBlue=Alpha(false,0,0); var a=r.Render(redBlue,257,191);
        AssertPixel(a,128,95,new(.2696f,.02353f,.53137f,1));
        var blueRed=r.Render(Alpha(true,0,0),257,191);
        Assert.NotEqual(a.Rgba,blueRed.Rgba);
        var depth=r.Render(Alpha(false,-.2f,.2f),257,191);
        AssertPixel(depth,128,95,new(.5392f,.04706f,.06275f,1));
        var noDepth=Alpha(false,-.2f,.2f,false);
        AssertPixel(r.Render(noDepth,257,191),128,95,new(.2696f,.02353f,.53137f,1));
        Assert.Equal(a.Rgba,r.Render(redBlue,257,191).Rgba);
        using var fresh=new D3D12OriginalStyleRenderer(); Assert.Equal(a.Rgba,fresh.Render(redBlue,257,191).Rgba);
        Save("alpha-source-order",a); Save("alpha-reverse-order",blueRed); Save("alpha-depth-write",depth);
    }
    [Fact] public void GeneratedShadowDarkensReceiverAndDisabledReceiverUsesWhiteDepth()
    {
        using var r=new D3D12OriginalStyleRenderer();
        var fixture=Shadow(true); var on=r.Render(fixture,257,191);
        var off=r.Render(Shadow(false),257,191);
        Save("shadow-generated",on); Save("shadow-white-receiver",off);
        Assert.True(on.Rgba[(95*257+128)*4]<off.Rgba[(95*257+128)*4]-100,$"shadow={on.Rgba[(95*257+128)*4]},white={off.Rgba[(95*257+128)*4]}");
        AssertPixel(on,128,95,new(.2f,.2f,.2f,1)); AssertPixel(off,128,95,Vector4.One);
        Assert.Equal(on.Rgba,r.Render(fixture,257,191).Rgba);
        using var fresh=new D3D12OriginalStyleRenderer(); Assert.Equal(on.Rgba,fresh.Render(fixture,257,191).Rgba);
        Save("shadow-generated",on); Save("shadow-white-receiver",off); SaveDevice(r);
    }
    [Fact] public void AdditiveModelModeAndCrossingAlphaPlanesPreserveSourceOrderDepth()
    {
        using var r=new D3D12OriginalStyleRenderer();var f=Alpha(false,0,0);
        var add=r.Render(new OriginalFrameScene(f.Surface,options:f.Options! with {BlendMode=OriginalAlphaBlendMode.Additive}),257,191);
        AssertPixel(add,128,95,new(.5f+20/255f,24/255f,.5f+32/255f,1));
        var s=f.Surface.Snapshot;var vertices=s.Vertices.ToArray();
        for(int i=0;i<4;i++)vertices[i]=vertices[i] with {Position=vertices[i].Position+new Vector3(0,0,vertices[i].Position.X*.5f)};
        var crossingSurface=new OriginalStyleScene(new(vertices,s.Indices,s.Materials,camera:s.Camera,lightDirection:s.LightDirection),f.Surface.Materials,f.Surface.Profile);
        var crossing=r.Render(new OriginalFrameScene(crossingSurface,options:f.Options),257,191);
        // Selected RH camera mirrors X: red is behind on screen-left, in front on screen-right.
        AssertPixel(crossing,100,95,new(.2696f,.02353f,.53137f,1));
        AssertPixel(crossing,156,95,new(.5392f,.04706f,.06275f,1));
        Save("alpha-additive",add);Save("alpha-crossing-depth",crossing);
    }
    [Fact] public void GroundStencilBlendsOverlappingEdgeGeometryOnlyOnceAndUsesProjectionBeforeWorld()
    {
        using var r=new D3D12OriginalStyleRenderer();
        var scene=Ground(true); var once=r.Render(scene,257,191); var twice=r.Render(Ground(false),257,191);
        AssertPixel(once,128,95,new(10/255f,12/255f,16/255f,1));
        AssertPixel(twice,128,95,new(5/255f,6/255f,8/255f,1));
        Assert.Equal(once.Rgba,r.Render(scene,257,191).Rgba);
        using var fresh=new D3D12OriginalStyleRenderer(); Assert.Equal(once.Rgba,fresh.Render(scene,257,191).Rgba);
        var moved=Ground(true,Matrix4x4.CreateTranslation(0,1,0),true);
        Assert.Equal(once.Rgba,r.Render(moved,257,191).Rgba);
        // Ground uses the edge stream even when outline drawing is disabled. Raw inward moves X.
        var edge=scene.Outline!;
        var expanded=new OriginalOutlineData(edge.Vertices.Select(v=>v with {Mode=OriginalOutlineVertexMode.Expand,Normal=new(10,0,0)}),[new(1,Vector4.One,false),new(1,Vector4.One,false)]);
        var wide=new OriginalFrameScene(scene.Surface,expanded,scene.OutlineOptions! with {ModelWidth=100,Eligible=false},scene.Options,scene.Materials);
        Assert.NotEqual(once.Rgba,r.Render(wide,257,191).Rgba);
        Save("ground-stencil-on",once); Save("ground-stencil-off-overlap",twice); Save("ground-edge-stream",r.Render(wide,257,191));
    }
    [Fact] public void PartialFarAndNonidentityWorldUseOneExplicitLightTransform()
    {
        using var r=new D3D12OriginalStyleRenderer();
        var fixture=Shadow(true);
        var light=Matrix4x4.Identity;light.M11=.5f;light.M22=.5f;light.M33=-.0005f;light.M43=.5f;
        var normal=new OriginalFrameScene(fixture.Surface,options:fixture.Options! with {SelfShadow=fixture.Options.SelfShadow! with {LightViewProjection=light}},materials:fixture.Materials);
        var partial=new OriginalFrameScene(normal.Surface,options:normal.Options! with {SelfShadow=normal.Options.SelfShadow! with {PartialFar=true}},materials:normal.Materials);
        var a=r.Render(normal,257,191);var b=r.Render(partial,257,191);
        Assert.InRange(a.Rgba[(95*257+128)*4],162,164); AssertPixel(b,128,95,new(.2f,.2f,.2f,1));
        var mesh=fixture.Surface.Snapshot;var world=Matrix4x4.CreateTranslation(10,0,0);
        var movedCamera=mesh.Camera with {Eye=mesh.Camera.Eye+new Vector3(10,0,0),Target=mesh.Camera.Target+new Vector3(10,0,0)};
        var movedSurface=new OriginalStyleScene(new(mesh.Vertices,mesh.Indices,mesh.Materials,mesh.Textures,world,movedCamera,mesh.LightDirection),fixture.Surface.Materials,fixture.Surface.Profile);
        var lvp=world*Matrix4x4.CreateTranslation(-10,0,0)*fixture.Options!.SelfShadow!.LightViewProjection;
        var moved=new OriginalFrameScene(movedSurface,options:fixture.Options with {SelfShadow=fixture.Options.SelfShadow with {LightViewProjection=lvp}},materials:fixture.Materials);
        Assert.Equal(r.Render(fixture,257,191).Rgba,r.Render(moved,257,191).Rgba);
        Save("shadow-normal-slope",a); Save("shadow-partial-far",b);Save("shadow-world-once",r.Render(moved,257,191));
    }
    [Fact] public void ShadowCasterIgnoresTextureAlphaButSkipsExactlyPoint98AndExplicitGate()
    {
        using var r=new D3D12OriginalStyleRenderer();
        var fixture=Shadow(true);var s=fixture.Surface.Snapshot;
        OriginalFrameScene Changed(float alpha,bool cast=true) {
            var mats=s.Materials.ToArray();mats[1]=mats[1] with {Diffuse=new(1,1,1,alpha),DiffuseTexture=0};
            var surface=new OriginalStyleScene(new(s.Vertices,s.Indices,mats,[new("transparent caster",1,1,[255,255,255,0])],s.World,s.Camera,s.LightDirection),fixture.Surface.Materials,fixture.Surface.Profile);
            return new(surface,options:fixture.Options,materials:[fixture.Materials[0],fixture.Materials[1] with {CastSelfShadow=cast}]);
        }
        var cast=r.Render(Changed(.5f),257,191);AssertPixel(cast,128,95,new(.2f,.2f,.2f,1));
        AssertPixel(r.Render(Changed(.98f),257,191),128,95,Vector4.One);
        AssertPixel(r.Render(Changed(.5f,false),257,191),128,95,Vector4.One);
        var adjacent=MathF.BitIncrement(.98f);Assert.Equal(cast.Rgba,r.Render(Changed(adjacent),257,191).Rgba);
        Save("shadow-transparent-caster",cast);Save("shadow-exact-point98-excluded",r.Render(Changed(.98f),257,191));
    }
    [Fact] public void OutsideUvSkipsToonAndTranspWhileDisabledWhiteStillRunsToon()
    {
        using var r=new D3D12OriginalStyleRenderer();
        var f=Alpha(false,0,1);var s=f.Surface.Snapshot;var vertices=s.Vertices.ToArray();
        for(int i=0;i<vertices.Length;i++)vertices[i]=vertices[i] with {Normal=Vector3.UnitY};
        var materials=s.Materials.ToArray();materials[0]=materials[0] with {Diffuse=new(.8f,.4f,.2f,.2f)};
        var surface=new OriginalStyleScene(new(vertices,s.Indices,materials,camera:s.Camera,lightDirection:Vector3.UnitX),[new(new(.1f)),new(new(.1f))],f.Surface.Profile);
        var outside=Matrix4x4.Identity;outside.M41=3;
        var opts=Options(transp:true,shadow:new(Matrix4x4.Identity,8,8,false,false,OriginalTextureSampling.LinearClamp,OriginalDepthComparison.LessEqual));
        var flags=new OriginalFrameMaterialFlags[]{new(false,false,false),new(false,false,false,SurfaceEnabled:false)};
        var inside=r.Render(new OriginalFrameScene(surface,options:opts,materials:flags),257,191);
        var outFrame=r.Render(new OriginalFrameScene(surface,options:opts with {SelfShadow=opts.SelfShadow! with {LightViewProjection=outside}},materials:flags),257,191);
        Vector3 bg=new(20/255f,24/255f,32/255f);
        AssertPixel(inside,128,95,new(new Vector3(.08f,.04f,.02f)*.5f+bg*.5f,1));
        AssertPixel(outFrame,128,95,new(new Vector3(.8f,.4f,.2f)*.2f+bg*.8f,1));
        var above=Matrix4x4.Identity;above.M43=1.1f;
        var litSurface=new OriginalStyleScene(new(vertices,s.Indices,materials,camera:s.Camera,lightDirection:Vector3.UnitY),surface.Materials,surface.Profile);
        var whiteCompare=r.Render(new OriginalFrameScene(litSurface,options:opts with {SelfShadow=opts.SelfShadow! with {LightViewProjection=above},Transp=false},materials:flags),257,191);
        AssertPixel(whiteCompare,128,95,new(new Vector3(.08f,.04f,.02f)*.2f+bg*.8f,1));
        Save("outside-shadow-uv",outFrame);Save("disabled-white-inrange-toon",inside);Save("disabled-white-depth-above-one",whiteCompare);
    }
    [Fact] public void FullFrameOutlineUsesAlphaBlendAndActualDepthFormatAndCameraOverride()
    {
        using var r=new D3D12OriginalStyleRenderer();var box=D3D12OriginalOutlineTests.Box();
        var data=new OriginalOutlineData(box.Outline!.Vertices,[box.Outline.Materials[0] with {Color=new(0,0,0,.5f)}]);
        var frame=new OriginalFrameScene(box.Surface,data,box.OutlineOptions,Options());
        var d32=r.Render(frame,257,191);AssertPixel(d32,78,95,new(10/255f,12/255f,16/255f,1));
        var d24=r.Render(new OriginalFrameScene(frame.Surface,data,frame.OutlineOptions,Options(ground:new(new(0,-1,0),Vector4.Zero,true,true))),257,191);
        Assert.Equal(d32.Rgba,d24.Rgba);
        var camera=box.Surface.Snapshot.Camera with {Eye=new(1,0,-5),Target=new(1,0,0)};
        var moved=new OriginalFrameScene(frame.Surface,data,frame.OutlineOptions! with {CameraPosition=camera.Eye},frame.Options! with {CameraOverride=camera});
        Assert.NotEqual(d32.Rgba,r.Render(moved,257,191).Rgba);
        Save("full-outline-d32",d32);Save("full-outline-d24s8",d24);Save("full-camera-override",r.Render(moved,257,191));
    }
    internal static OriginalFrameScene Ground(bool stencil,Matrix4x4? world=null,bool moveCamera=false)
    {
        SceneVertex[] quad=[new(new(-1,1,-1),Vector3.UnitY,Vector2.Zero),new(new(1,1,-1),Vector3.UnitY,Vector2.Zero),new(new(1,1,1),Vector3.UnitY,Vector2.Zero),new(new(-1,1,1),Vector3.UnitY,Vector2.Zero)];
        var vertices=quad.Concat(quad).ToArray();var shift=moveCamera?Vector3.UnitY:Vector3.Zero;
        var mesh=new SceneSnapshot(vertices,[0,1,2,0,2,3,4,5,6,4,6,7],[new("one",0,6,Vector4.One,Vector3.Zero,Vector3.Zero,1),new("two",6,6,Vector4.One,Vector3.Zero,Vector3.Zero,1)],world:world,camera:new(new Vector3(0,4,-5)+shift,new Vector3(0,.1f,0)+shift,Vector3.UnitY,4,.1f,20));
        var surface=new OriginalStyleScene(mesh,[new(Vector3.One),new(Vector3.One)]);
        var outline=new OriginalOutlineData(vertices.Select((v,i)=>new OriginalOutlineVertex(v.Position,v.Normal,OriginalOutlineVertexMode.Expand,1,i/4)),[new(0,Vector4.One,false),new(0,Vector4.One,false)]);
        return new(surface,outline,new(new(0,0,0,1),Matrix4x4.Identity,mesh.Camera.Eye,30,0,false),Options(false,ground:new(new(0,-1,0),new(0,0,0,.5f),true,stencil)),[new(false,false,true,SurfaceEnabled:false),new(false,false,true,SurfaceEnabled:false)]);
    }
    internal static OriginalFrameScene Alpha(bool reverse,float redZ,float blueZ,bool writeDepth=true)
    {
        SceneVertex[] Quad(float z)=>[new(new(-1,-1,z),-Vector3.UnitZ,Vector2.Zero),new(new(1,-1,z),-Vector3.UnitZ,Vector2.Zero),new(new(1,1,z),-Vector3.UnitZ,Vector2.Zero),new(new(-1,1,z),-Vector3.UnitZ,Vector2.Zero)];
        var vertices=Quad(reverse?blueZ:redZ).Concat(Quad(reverse?redZ:blueZ)).ToArray();
        var first=reverse?new Vector4(0,0,1,.5f):new Vector4(1,0,0,.5f); var second=reverse?new Vector4(1,0,0,.5f):new Vector4(0,0,1,.5f);
        var mesh=new SceneSnapshot(vertices,[0,2,1,0,3,2,4,6,5,4,7,6],[new("first",0,6,first,Vector3.Zero,Vector3.Zero,1),new("second",6,6,second,Vector3.Zero,Vector3.Zero,1)],camera:new(new(0,0,-5),Vector3.Zero,Vector3.UnitY,4,.1f,20),lightDirection:-Vector3.UnitZ);
        var s=new OriginalStyleScene(mesh,[new(Vector3.One),new(Vector3.One)],new(Vector3.One,Vector3.Zero,Matrix4x4.Identity,OriginalTextureSampling.PointClamp));
        return new(s,options:Options(writeDepth));
    }
    internal static OriginalFrameScene Shadow(bool receive)
    {
        // Receiver at z=0, caster at z=1, selected light projection sees from +Z; camera sees from -Z.
        var alpha=Alpha(false,0,1); var mesh=alpha.Surface.Snapshot;
        var materials=mesh.Materials.Select(m=>m with {Diffuse=Vector4.One}).ToArray();
        var s=new OriginalStyleScene(new(mesh.Vertices,mesh.Indices,materials,camera:mesh.Camera,lightDirection:-Vector3.UnitZ),[new(new(.2f)),new(new(.2f))],alpha.Surface.Profile);
        var light=Matrix4x4.CreateLookAt(new(0,0,5),Vector3.Zero,Vector3.UnitY)*Matrix4x4.CreateOrthographic(4,4,.1f,20);
        return new(s,options:Options(shadow:new(light,64,64,true,false,OriginalTextureSampling.LinearClamp,OriginalDepthComparison.LessEqual)),materials:[new(false,receive,false),new(true,false,false,ShadowDoubleSided:true)]);
    }
    internal static void AssertPixel(RenderedFrame f,int x,int y,Vector4 expected)
    { int p=(y*f.Width+x)*4; for(int c=0;c<4;c++) Assert.InRange(Math.Abs(f.Rgba[p+c]-Math.Clamp(expected[c],0,1)*255),0,1.2f); }
    internal static void Save(string name,RenderedFrame frame)
    {
        var path=Environment.GetEnvironmentVariable("MMD_FRAME_ARTIFACTS"); if(path is null) return;
        Directory.CreateDirectory(path); File.WriteAllBytes(Path.Combine(path,name+".rgba"),frame.Rgba);
        using var output=File.Create(Path.Combine(path,name+".ppm")); output.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{frame.Width} {frame.Height}\n255\n"));
        for(int i=0;i<frame.Rgba.Length;i+=4) output.Write(frame.Rgba,i,3);
    }
    internal static void SaveDevice(D3D12OriginalStyleRenderer renderer)
    {
        var path=Environment.GetEnvironmentVariable("MMD_FRAME_ARTIFACTS"); if(path is null) return;
        File.WriteAllText(Path.Combine(path,"device.json"),System.Text.Json.JsonSerializer.Serialize(new{renderer.AdapterName,renderer.FeatureLevel,renderer.IsWarp,renderer.DebugRequested,renderer.DebugLayerEnabled,renderer.DebugInfoQueueAvailable,renderer.LastDebugMessages}));
    }
}
