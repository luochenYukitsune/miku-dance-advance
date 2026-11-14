using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12OriginalStyleTests
{
    [Fact]
    public void ActualRendererMatchesCpuSurfaceAndRepeatsOnSameAndFreshDevices()
    {
        using var renderer = new D3D12OriginalStyleRenderer();
        foreach (var mode in new[] { OriginalSphereMode.None, OriginalSphereMode.Multiply, OriginalSphereMode.Add })
        foreach (float d in new[] { -.25f, 0, 1/6f, 1/3f, 1 })
        {
            var scene = Fixture(mode, d);
            var frame = renderer.Render(scene,257,191);
            var m = scene.Snapshot.Materials[0];
            var host = OriginalStyleMath.ResolveHostColors(m,scene.Profile);
            Vector4 expected = OriginalStyleMath.EvaluateSurface(host.EgColor,host.SpecularPower,scene.Materials[0].ToonShadowColor,
                scene.Snapshot.Vertices[0].Normal,Vector3.Zero,scene.Snapshot.Camera.Eye,scene.Snapshot.LightDirection,
                new(128/255f,192/255f,64/255f,160/255f),new(64/255f,128/255f,192/255f,208/255f),mode);
            for(int c=0;c<4;c++) Assert.InRange(Math.Abs(frame.Rgba[(95*257+128)*4+c]-Math.Clamp(expected[c],0,1)*255),0,1.1f);
            Assert.Equal(frame.Rgba,renderer.Render(scene,257,191).Rgba);
            using var fresh = new D3D12OriginalStyleRenderer();
            Assert.Equal(frame.Rgba,fresh.Render(scene,257,191).Rgba);
            Assert.Empty(renderer.LastDebugMessages);
        }
    }

    [Fact]
    public void SphereProjectionUsesVertexRawNormalAndExplicitAsymmetricRotation()
    {
        using var renderer = new D3D12OriginalStyleRenderer();
        var scene = Fixture(OriginalSphereMode.Add,1);
        Vector3 raw = new(.2f,.9f,-.7f);
        var rotation = Matrix4x4.CreateRotationZ(.7f)*Matrix4x4.CreateRotationY(.3f);
        // 2x2 makes both projection orientation and the point sampler observable.
        var image = new SceneTexture("sphere quadrants",2,2,[255,0,0,255, 0,255,0,255, 0,0,255,255, 255,255,0,255]);
        var mesh = scene.Snapshot;
        var vertices = mesh.Vertices.ToArray();
        for(int i=0;i<vertices.Length;i++) vertices[i]=vertices[i] with {Normal=raw};
        var m = mesh.Materials[0] with {Diffuse=new(0,0,0,1),Ambient=Vector3.Zero,Specular=Vector3.Zero};
        mesh = new(vertices,mesh.Indices,[m],[mesh.Textures[0],image],camera:mesh.Camera,lightDirection:raw);
        scene=new(mesh,scene.Materials,scene.Profile with {SphereRotation=rotation});
        var frame=renderer.Render(scene,257,191);
        var uv=OriginalStyleMath.SphereUv(raw,rotation);
        int x=Math.Clamp((int)(uv.X*2),0,1),y=Math.Clamp((int)(uv.Y*2),0,1),offset=(y*2+x)*4;
        for(int c=0;c<3;c++) Assert.Equal(image.Rgba[offset+c],frame.Rgba[(95*257+128)*4+c]);
        Assert.NotEqual(frame.Rgba,renderer.Render(new OriginalStyleScene(mesh,scene.Materials,scene.Profile with {SphereRotation=Matrix4x4.Identity}),257,191).Rgba);
    }

    [Fact]
    public void PmxWindingFacesCameraAndBackFacesCullWhileBothFacesRemainVisible()
    {
        using var renderer=new D3D12OriginalStyleRenderer();
        var source=Fixture(OriginalSphereMode.None,1);
        var old=source.Snapshot;
        // PMX face order has geometric cross-product -Z, agreeing with the stored -Z normal.
        // The selected neutral RH camera looks from -Z towards the origin.
        uint[] front=[0,2,1,0,3,2],back=[0,1,2,0,2,3];
        OriginalStyleScene Scene(uint[] indices,bool bothFaces)=>new(
            new(old.Vertices,indices,old.Materials,old.Textures,camera:old.Camera,lightDirection:old.LightDirection),
            [source.Materials[0] with {DoubleSided=bothFaces}],source.Profile);
        int center=(95*257+128)*4;
        byte[] background=[20,24,32,255];
        var visible=renderer.Render(Scene(front,false),257,191);
        Assert.NotEqual(background,visible.Rgba.AsSpan(center,4).ToArray());
        Assert.Equal(background,renderer.Render(Scene(back,false),257,191).Rgba.AsSpan(center,4).ToArray());
        Assert.Equal(visible.Rgba,renderer.Render(Scene(front,true),257,191).Rgba);
        Assert.Equal(visible.Rgba,renderer.Render(Scene(back,true),257,191).Rgba);
    }

    [Fact]
    public void ActualBlinnUsesEyePositionSpecularColourAndZeroPowerFallback()
    {
        using var renderer=new D3D12OriginalStyleRenderer();
        var reference=Fixture(OriginalSphereMode.None,MathF.Sqrt(.75f));
        var mesh=reference.Snapshot;
        var material=mesh.Materials[0] with {Diffuse=new(0,0,0,1),Specular=new(.4f,.2f,.1f),Shininess=0,DiffuseTexture=-1};
        mesh=new(mesh.Vertices,mesh.Indices,[material],camera:mesh.Camera,lightDirection:mesh.LightDirection);
        var scene=new OriginalStyleScene(mesh,[new(Vector3.One,-1,OriginalSphereMode.None,true)],reference.Profile);
        var host=OriginalStyleMath.ResolveHostColors(material,scene.Profile);
        var expected=OriginalStyleMath.EvaluateSurface(host.EgColor,host.SpecularPower,Vector3.One,mesh.Vertices[0].Normal,
            Vector3.Zero,mesh.Camera.Eye,mesh.LightDirection,Vector4.One,Vector4.One,OriginalSphereMode.None,false);
        var frame=renderer.Render(scene,257,191);
        for(int c=0;c<4;c++) Assert.InRange(Math.Abs(frame.Rgba[(95*257+128)*4+c]-Math.Clamp(expected[c],0,1)*255),0,1.1f);
        var highPower=new OriginalStyleScene(new(mesh.Vertices,mesh.Indices,[material with {Shininess=32}],camera:mesh.Camera,lightDirection:mesh.LightDirection),scene.Materials,scene.Profile);
        Assert.NotEqual(frame.Rgba,renderer.Render(highPower,257,191).Rgba);
        var movedEye=mesh.Camera with {Eye=new(3,0,-5)};
        var changedView=new OriginalStyleScene(new(mesh.Vertices,mesh.Indices,mesh.Materials,camera:movedEye,lightDirection:mesh.LightDirection),scene.Materials,scene.Profile);
        Assert.NotEqual(frame.Rgba,renderer.Render(changedView,257,191).Rgba);
    }

    [Fact]
    public void CutoutDiscardsRawCombinedAlpha()
    {
        using var renderer=new D3D12OriginalStyleRenderer();
        var scene=Fixture(OriginalSphereMode.Multiply,1);
        var s=scene.Snapshot;
        var texture=new SceneTexture("alpha zero",1,1,[255,255,255,0]);
        scene=new(new(s.Vertices,s.Indices,s.Materials,[texture,s.Textures[1]],camera:s.Camera,lightDirection:s.LightDirection),scene.Materials,scene.Profile);
        Assert.Equal(new byte[]{20,24,32,255},renderer.Render(scene,257,191).Rgba.AsSpan((95*257+128)*4,4).ToArray());
    }

    public static OriginalStyleScene Fixture(OriginalSphereMode mode,float d)
    {
        var quad=D3D12MeshTests.Quad();
        var vertices=quad.Vertices.ToArray();
        for(int i=0;i<vertices.Length;i++) vertices[i]=vertices[i] with {Normal=new(MathF.Sqrt(1-d*d),0,-d)};
        var m=new SceneMaterial("surface",0,6,new(.6f,.3f,.2f,.8f),Vector3.Zero,Vector3.Zero,8,0);
        var mesh=new SceneSnapshot(vertices,quad.Indices,[m],[new("diffuse",1,1,[128,192,64,160]),new("sphere",1,1,[64,128,192,208])],camera:quad.Camera,lightDirection:-Vector3.UnitZ);
        return new(mesh,[new(new(.8f,.6f,.4f),mode==OriginalSphereMode.None?-1:1,mode,true)],new(Vector3.One,Vector3.One,Matrix4x4.Identity,OriginalTextureSampling.PointClamp));
    }
}
