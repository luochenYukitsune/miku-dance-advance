using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

// Supplemental actual-draw fixtures; root copies into tests only after the active run.
[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12EndfieldHairMixedSurfaceTests
{
    private const int W=129,H=161;
    [Fact,Trait("Category","Gpu")]
    public void EndfieldResourcesAreIsolatedFromMmdFaceEyeAndOtherDedicatedHairInOneFrame()
    {
        const int count=8;
        var vertices=new List<SceneVertex>();var indices=new List<int>();var materials=new List<SceneMaterial>();
        for(int row=0;row<count;row++)
        {
            float y=.7f-row*.2f;int start=vertices.Count;
            vertices.AddRange([new(new(-.7f,y-.075f,0),-Vector3.UnitZ,new(.25f)),new(new(.7f,y-.075f,0),-Vector3.UnitZ,new(.25f)),
                new(new(.7f,y+.075f,0),-Vector3.UnitZ,new(.25f)),new(new(-.7f,y+.075f,0),-Vector3.UnitZ,new(.25f))]);
            indices.AddRange([start,start+2,start+1,start,start+3,start+2]);
            materials.Add(new("explicit row",row*6,6,new(.08f,.06f,.04f,1),new(.12f),Vector3.Zero,4));
        }
        var scene=new SceneSnapshot(vertices.ToArray(),indices.Select(index=>checked((uint)index)).ToArray(),materials,
            [new SceneTexture("green packed",1,1,[128,255,255,64]),new SceneTexture("shadow red",1,1,[255,0,0,0]),
                new SceneTexture("face RGB",1,1,[255,255,255,0]),new SceneTexture("face GA",1,1,[0,255,0,255])],
            camera:new(new(0,0,-5),Vector3.Zero,Vector3.UnitY,2,.1f,20),lightDirection:-Vector3.UnitZ,
            rawRadialSeeds:new(Enumerable.Repeat(Vector3.UnitY,vertices.Count).ToArray()));
        var frame=Create(scene,Enumerable.Repeat(new OriginalStyleMaterialBinding(new(.2f),DoubleSided:true),count).ToArray());
        D3D12SurfaceStyle[] families=[D3D12SurfaceStyle.EndfieldPmx,D3D12SurfaceStyle.Mmd,D3D12SurfaceStyle.GenshinPmx,
            D3D12SurfaceStyle.EndfieldPmx,D3D12SurfaceStyle.GenshinPmx,D3D12SurfaceStyle.StarRailPmx,
            D3D12SurfaceStyle.WutheringPmx,D3D12SurfaceStyle.EndfieldPmx];
        D3D12CharacterMaterialStyle[] before=[new(D3D12CharacterMaterialRole.Hair),new(),
            new(D3D12CharacterMaterialRole.Face,FaceSdf:D3D12FaceSdfBinding.GenshinSoft(2,3,-Vector3.UnitZ,Vector3.UnitX)),
            new(D3D12CharacterMaterialRole.Eye),new(D3D12CharacterMaterialRole.Hair,GenshinNonMetallicSpecular:new(0)),
            new(D3D12CharacterMaterialRole.Hair,StarMcHair:new(0,1)),new(D3D12CharacterMaterialRole.Hair,StarMcHair:new(0,1)),new()];
        var after=before.ToArray();after[0]=new(D3D12CharacterMaterialRole.Hair,EndfieldHair:new(0,new(.1f,.07f,.04f)));
        using var session=new D3D12FrameSession();
        var baseline=session.Capture(frame,W,H,surfaceStyles:new(families,before));
        var selected=session.Capture(frame,W,H,surfaceStyles:new(families,after));
        for(int row=0;row<count;row++)
        {
            var clip=Vector4.Transform(new Vector4(0,.7f-row*.2f,0,1),scene.Camera.ViewProjection(W,H));
            int y=(int)((.5f-.5f*clip.Y/clip.W)*H);
            var a=Pixel(baseline,W/2,y);var b=Pixel(selected,W/2,y);
            if(row==0)Assert.NotEqual(a,b);else Assert.Equal(a,b);
        }
        for(int i=3;i<baseline.Rgba.Length;i+=4)Assert.Equal(baseline.Rgba[i],selected.Rgba[i]);
        Assert.Equal(baseline.Rgba,session.Capture(frame,W,H,surfaceStyles:new(families,before)).Rgba);
    }

    [Fact,Trait("Category","Gpu")]
    public void Uv1SubTextureAndTextureMorphRemainIndependentFromRawSeedSlotTwoAndUv0Green()
    {
        using var session=new D3D12FrameSession();
        var normal=-Vector3.UnitZ;Vector3[] points=[new(-.7f,-.7f,0),new(.7f,-.7f,0),new(.7f,.7f,0),new(-.7f,.7f,0)];
        var vertices=points.Select(p=>new SceneVertex(p,normal,new(.25f))).ToArray();
        var palette=new Vector3(.06f,.035f,.02f);byte[]? previous=null;
        foreach(float uv1 in new[]{.25f,.75f})
        {
            var uv=new SceneAdditionalUvs(1,Enumerable.Repeat(new SceneAdditionalUvVertex(new(uv1,.5f,0,0),default,default,default),4).ToArray());
            var scene=new SceneSnapshot(vertices,[0,2,1,0,3,2],[new("opaque subtexture hair",0,6,new(.24f,.2f,.16f,1),Vector3.Zero,Vector3.Zero,4)],
                [new SceneTexture("numerical G only",1,1,[17,255,81,0]),new SceneTexture("independent sphere UV1",2,1,[60,180,240,255,240,80,40,255])],
                camera:new(new(0,0,-5),Vector3.Zero,Vector3.UnitY,2,.1f,20),lightDirection:normal,
                additionalUvs:uv,rawRadialSeeds:new(Enumerable.Repeat(Vector3.UnitY,4).ToArray()));
            var material=new OriginalStyleMaterialBinding(new(.2f),1,OriginalSphereMode.SubTexture,true)
            {TextureFactors=OriginalMaterialTextureFactors.Identity with {Sphere=new(new(.8f,.6f,.4f,1),new(.03f,.02f,.01f,0))}};
            var frame=Create(scene,[material]);
            var zero=session.Capture(frame,W,H,surfaceStyles:D3D12EndfieldHairSurfaceTests.Map(new(0,Vector3.Zero)));
            var actual=session.Capture(frame,W,H,surfaceStyles:D3D12EndfieldHairSurfaceTests.Map(new(0,palette)));
            var a=Pixel(zero,W/2,H/2);var b=Pixel(actual,W/2,H/2);
            if(previous is not null)Assert.NotEqual(previous,a);previous=a;
            for(int c=0;c<3;c++)
            {
                double expected=Math.Clamp(a[c]+palette[c]*(.75*1.5001001358032227)*255,0,255);
                Assert.InRange(Math.Abs(b[c]-expected),0,1.6);
            }
            for(int i=3;i<zero.Rgba.Length;i+=4)Assert.Equal(zero.Rgba[i],actual.Rgba[i]);
            var noMorph=Create(scene,[material with {TextureFactors=OriginalMaterialTextureFactors.Identity}]);
            var unmodified=session.Capture(noMorph,W,H,surfaceStyles:D3D12EndfieldHairSurfaceTests.Map(new(0,Vector3.Zero)));
            Assert.NotEqual(a,Pixel(unmodified,W/2,H/2));
        }
    }
    private static byte[] Pixel(RenderedFrame frame,int x,int y)=>frame.Rgba.AsSpan((y*W+x)*4,4).ToArray();
    private static OriginalFrameScene Create(SceneSnapshot scene,OriginalStyleMaterialBinding[] materials)
        =>new(new(scene,materials,new(Vector3.One,Vector3.One,Matrix4x4.Identity,OriginalTextureSampling.PointClamp)),
            options:new(OriginalAlphaBlendMode.Normal,true,false,BackgroundColor:Vector4.Zero));
}
