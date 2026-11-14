using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12RayGiRasterTests
{
    private const int Width=257, Height=191;
    private static readonly RayGiOptions Gi=new(LightingMode.SoftwareRayTracing,Strength:5,SamplesPerPixel:16,Seed:17,
        MaxRayDistance:50,RayEpsilon:.001f,DirectionalIrradiance:new(3));

    [Fact]
    public void AsymmetricReceiverChangesOnlyItsRasterPixels()
    {
        using var session=new D3D12FrameSession();var frame=Fixture(center:1.2f);
        var off=session.Capture(frame,Width,Height);var on=session.Capture(frame,Width,Height,rayGiOptions:Gi);
        Assert.True(Changes(off,on)>100);Assert.Equal(0,BackgroundChanges(off,on));
        Assert.True(session.RayGiStatistics!.GBufferDrawCalls>0);
    }

    [Theory]
    [InlineData(-5.95f,30f)]
    [InlineData(0f,4f)]
    public void ClippedReceiversDoNotReceiveGi(float receiverZ,float far)
    {
        using var session=new D3D12FrameSession();PositiveControl(session);var frame=Fixture(receiverZ:receiverZ,far:far);
        var off=session.Capture(frame,Width,Height);var on=session.Capture(frame,Width,Height,rayGiOptions:Gi);
        Assert.Equal(off.Rgba,on.Rgba);
    }

    [Fact]
    public void TextureAndMaterialAlphaHaveEquivalentReceiverEligibility()
    {
        using var session=new D3D12FrameSession();
        PositiveControl(session);
        var texture=Fixture(textureAlpha:128);var material=Fixture(materialAlpha:128/255f);
        var textureOff=session.Capture(texture,Width,Height);var textureOn=session.Capture(texture,Width,Height,rayGiOptions:Gi);
        var materialOff=session.Capture(material,Width,Height);var materialOn=session.Capture(material,Width,Height,rayGiOptions:Gi);
        Assert.Equal(textureOff.Rgba,materialOff.Rgba);Assert.Equal(textureOff.Rgba,textureOn.Rgba);Assert.Equal(materialOff.Rgba,materialOn.Rgba);
    }

    [Fact]
    public void ForegroundOutlineCoversIndirectSurfaceColor()
    {
        using var session=new D3D12FrameSession();PositiveControl(session);var frame=Fixture(outline:true);
        var off=session.Capture(frame,Width,Height);var on=session.Capture(frame,Width,Height,rayGiOptions:Gi);
        int center=(Height/2*Width+Width/2)*4;
        Assert.Equal(new byte[]{0,0,0},off.Rgba.AsSpan(center,3).ToArray());
        Assert.Equal(off.Rgba.AsSpan(center,3).ToArray(),on.Rgba.AsSpan(center,3).ToArray());
    }

    [Fact]
    public void TransparentForegroundAttenuatesIndirectColorInOriginalSourceOrder()
    {
        using var session=new D3D12FrameSession();var baseFrame=Fixture();var overlay=Fixture(overlay:true);
        var b0=session.Capture(baseFrame,Width,Height);var b1=session.Capture(baseFrame,Width,Height,rayGiOptions:Gi);
        var o0=session.Capture(overlay,Width,Height);var o1=session.Capture(overlay,Width,Height,rayGiOptions:Gi);
        int center=(Height/2*Width+Width/2)*4;int originalDelta=b1.Rgba[center]-b0.Rgba[center],overlayDelta=o1.Rgba[center]-o0.Rgba[center];
        Assert.True(originalDelta>20);Assert.InRange(overlayDelta,originalDelta/2-2,originalDelta/2+2);
        Assert.Equal(0,BackgroundChanges(o0,o1));
    }

    [Fact]
    public void SampledSphereAlphaUsesTheSameReceiverEligibilityAsDiffuseAlpha()
    {
        using var session=new D3D12FrameSession();PositiveControl(session);
        var frame=Fixture(sphereAlpha:128);var off=session.Capture(frame,Width,Height);var on=session.Capture(frame,Width,Height,rayGiOptions:Gi);
        Assert.Equal(off.Rgba,on.Rgba);
    }

    [Fact]
    public void ZeroStrengthPreservesBaseAndD24ReceiversStillGetIndirectLight()
    {
        using var session=new D3D12FrameSession();
        var source=Fixture(outline:true);
        var frame=new OriginalFrameScene(source.Surface,source.Outline,source.OutlineOptions! with {Eligible=false},
            source.Options! with {GroundShadow=new(Vector3.UnitY,Vector4.Zero,false,true)},source.Materials);
        var off=session.Capture(frame,Width,Height);var zero=session.Capture(frame,Width,Height,rayGiOptions:Gi with {Strength=0});
        var on=session.Capture(frame,Width,Height,rayGiOptions:Gi);
        Assert.Equal(off.Rgba,zero.Rgba);Assert.True(Changes(off,on)>100);Assert.Equal(0,BackgroundChanges(off,on));
    }

    [Fact]
    public void FrameOptionsExecuteAndExplicitOffOverridesAndClearsStatistics()
    {
        using var session=new D3D12FrameSession();var baseline=Fixture();
        var selected=new OriginalFrameScene(baseline.Surface,options:baseline.Options! with {RayGi=Gi},materials:baseline.Materials);
        var off=session.Capture(baseline,Width,Height);var on=session.Capture(selected,Width,Height);
        Assert.True(Changes(off,on)>100);Assert.Equal(LightingMode.SoftwareRayTracing,session.RayGiStatistics!.Effective);
        Assert.Equal(off.Rgba,session.Capture(selected,Width,Height,rayGiOptions:new()).Rgba);
        Assert.Null(session.RayGiStatistics);Assert.Null(session.RayGiBackendIdentity);
    }

    [Fact]
    public void RecordedGiAbortRollsBackAndNextFrameMatchesFreshCapture()
    {
        var frame=Fixture();using var session=new D3D12FrameSession();
        Assert.Throws<InvalidOperationException>((Action)(()=>session.RenderForPresent(frame,Width,Height,Gi,(_,_)=>throw new InvalidOperationException("Abort after full GI recording."))));
        Assert.Null(session.RayGiStatistics);Assert.Null(session.RayGiBackendIdentity);
        var retry=session.Capture(frame,Width,Height,rayGiOptions:Gi);using var fresh=new D3D12FrameSession();
        Assert.Equal(fresh.Capture(frame,Width,Height,rayGiOptions:Gi).Rgba,retry.Rgba);
        Assert.Equal(1,session.Statistics!.SubmissionCount);
    }

    [Fact]
    public void StaticGiFramesAndCameraRotationKeepUploadsAndCachesBounded()
    {
        var frame=Fixture();using var session=new D3D12FrameSession();
        for(int i=0;i<6;i++){using var warm=session.Render(frame,127,95,rayGiOptions:Gi with {SamplesPerPixel=1});}
        var initial=session.Statistics!;
        for(int i=0;i<30;i++)
        {
            var camera=frame.Surface.Snapshot.Camera with {Eye=new(MathF.Sin(i*.1f),0,-6)};
            var changed=new OriginalFrameScene(frame.Surface,options:frame.Options! with {CameraOverride=camera,SphereRotationOverride=Matrix4x4.CreateRotationY(i*.1f)},materials:frame.Materials);
            using var output=session.Render(changed,127,95,rayGiOptions:Gi with {SamplesPerPixel=1});
            Assert.Equal(0,output.Statistics.ReadbackCount);Assert.Equal(1,output.Statistics.SubmissionCount);
            Assert.Equal(initial.BufferUploads,output.Statistics.BufferUploads);Assert.Equal(initial.CachedResources,output.Statistics.CachedResources);
            Assert.Equal(initial.ShaderCompilations,output.Statistics.ShaderCompilations);Assert.InRange(output.Statistics.PendingFrames,0,3);
        }
        session.WaitForIdle();
    }


    private static void PositiveControl(D3D12FrameSession session)
    {
        var frame=Fixture();var off=session.Capture(frame,Width,Height);var on=session.Capture(frame,Width,Height,rayGiOptions:Gi);
        Assert.True(Changes(off,on)>100,"Opaque control must receive real GI before a negative eligibility assertion.");
    }

    [Fact]
    public void PointWrapAndLinearClampUseTheirDeclaredFilterAndAddressModes()
    {
        using var session=new D3D12FrameSession();
        var wrap=Fixture(sampling:OriginalTextureSampling.PointWrap,textureColors:true);
        var clamp=Fixture(sampling:OriginalTextureSampling.PointClamp,textureColors:true);
        var linear=Fixture(sampling:OriginalTextureSampling.LinearClamp,textureColors:true);
        var w0=session.Capture(wrap,Width,Height);var w1=session.Capture(wrap,Width,Height,rayGiOptions:Gi);
        var c0=session.Capture(clamp,Width,Height);var c1=session.Capture(clamp,Width,Height,rayGiOptions:Gi);
        var l0=session.Capture(linear,Width,Height);var l1=session.Capture(linear,Width,Height,rayGiOptions:Gi);
        Assert.True(RedDelta(w0,w1)>RedDelta(c0,c1)+1000,"Wrapped wall must sample its red texel at u=1.25.");
        Assert.True(RedDelta(l0,l1)<RedDelta(w0,w1),"Clamped wall must sample its green edge texel.");
    }

    private static int Changes(RenderedFrame a,RenderedFrame b)=>Enumerable.Range(0,Width*Height).Count(p=>!a.Rgba.AsSpan(p*4,3).SequenceEqual(b.Rgba.AsSpan(p*4,3)));
    private static int BackgroundChanges(RenderedFrame a,RenderedFrame b)=>Enumerable.Range(0,Width*Height).Count(p=>a.Rgba.AsSpan(p*4,3).SequenceEqual(new byte[]{20,24,32})&&!a.Rgba.AsSpan(p*4,3).SequenceEqual(b.Rgba.AsSpan(p*4,3)));
    private static long RedDelta(RenderedFrame a,RenderedFrame b)=>Enumerable.Range(0,Width*Height).Sum(p=>(long)b.Rgba[p*4]-a.Rgba[p*4]);

    internal static OriginalFrameScene Fixture(float center=0,byte textureAlpha=255,float materialAlpha=1,bool overlay=false,bool outline=false,
        float receiverZ=0,float far=30,OriginalTextureSampling sampling=OriginalTextureSampling.PointClamp,bool textureColors=false,byte sphereAlpha=255)
    {
        static SceneVertex[] Quad(float z,float x0,float x1,float halfY,Vector3 n,Vector2 uv)=>[
            new(new(x0,-halfY,z),n,uv),new(new(x1,-halfY,z),n,uv),new(new(x1,halfY,z),n,uv),new(new(x0,halfY,z),n,uv)];
        var vertices=new List<SceneVertex>();var indices=new List<uint>();var materials=new List<SceneMaterial>();
        var bindings=new List<OriginalStyleMaterialBinding>();var flags=new List<OriginalFrameMaterialFlags>();
        void Add(string name,SceneVertex[] quad,Vector4 color,int texture=-1,bool surface=true,bool reverse=false,bool both=false)
        {
            uint offset=(uint)vertices.Count;int start=indices.Count;vertices.AddRange(quad);
            indices.AddRange((reverse?new uint[]{0,1,2,0,2,3}:new uint[]{0,2,1,0,3,2}).Select(i=>i+offset));
            materials.Add(new(name,start,6,color,Vector3.Zero,Vector3.Zero,1,texture));bindings.Add(new(Vector3.One,DoubleSided:both));flags.Add(new(false,false,false,SurfaceEnabled:surface));
        }
        Add("receiver",Quad(receiverZ,center-.65f,center+.65f,1,-Vector3.UnitZ,textureColors?new(.5f,.5f):Vector2.Zero),new(.35f,.35f,.35f,materialAlpha),textureAlpha==255&&!textureColors?-1:0);
        Add("red wall behind camera",Quad(-8,-15,15,15,Vector3.UnitZ,textureColors?new(1.25f,.5f):Vector2.Zero),new(1,.03f,.02f,1),textureColors?0:-1,both:true);
        if(overlay)Add("black transparent foreground",Quad(-1,-1,1,1.5f,-Vector3.UnitZ,Vector2.Zero),new(0,0,0,.5f));
        if(outline)Add("outline foreground",Quad(-1,-1,1,1.5f,-Vector3.UnitZ,Vector2.Zero),Vector4.One,surface:false,reverse:true);
        var camera=new SceneCamera(new(0,0,-6),Vector3.Zero,Vector3.UnitY,4.5f,.1f,far);
        SceneTexture[] textures=textureColors?[new("red-green",2,1,[255,0,0,255,0,255,0,255])]:[new("half alpha",1,1,[255,255,255,textureAlpha])];
        if(sphereAlpha<255){textures=[..textures,new("sphere alpha",1,1,[255,255,255,sphereAlpha])];bindings[0]=bindings[0] with {SphereTexture=1,SphereMode=OriginalSphereMode.Multiply};}
        var snapshot=new SceneSnapshot(vertices.ToArray(),indices.ToArray(),materials,textures,camera:camera,lightDirection:Vector3.Normalize(new(.5f,1,.5f)));
        var surface=new OriginalStyleScene(snapshot,bindings,new(new(.602f),Vector3.Zero,Matrix4x4.Identity,sampling));
        var options=new OriginalFrameOptions(OriginalAlphaBlendMode.Normal,true,false,null,null);
        if(!outline)return new(surface,options:options,materials:flags);
        var edgeVertices=vertices.Select((v,i)=>new OriginalOutlineVertex(v.Position,Vector3.Zero,OriginalOutlineVertexMode.Expand,1,i/4));
        var edgeMaterials=materials.Select((m,i)=>new OriginalOutlineMaterial(1,new(0,0,0,1),i==2));
        return new(surface,new(edgeVertices,edgeMaterials),new(new(0,0,0,1),Matrix4x4.Identity,camera.Eye,30,20),options,flags);
    }
}
