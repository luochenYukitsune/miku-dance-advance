using System.Numerics;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category","Gpu")]
public sealed class D3D12RayGiNearOcclusionTests
{
    private const int Size=129;
    // Deserialization intentionally permits running these behavioral assertions against the frozen
    // previous renderer/Core binaries for Red; old Core ignores the then-unknown occlusion fields.
    private static RayGiOptions Gi(bool occlusion=true,float radius=1,float strength=0,int spp=256)=>
        JsonSerializer.Deserialize<RayGiOptions>(FormattableString.Invariant($$"""
        {"Strength":{{strength}},"SamplesPerPixel":{{spp}},"Seed":17,"MaxRayDistance":50,"RayEpsilon":0.001,
         "OcclusionStrength":{{(occlusion?1:0)}},"OcclusionRadius":{{radius}},"DirectScale":0}
        """))! with {Mode=LightingMode.SoftwareRayTracing};

    [Fact]
    public void ClosedNearHemisphereDarkensAmbientAndOpenOrFarHemisphereDoesNot()
    {
        using var session=new D3D12FrameSession();
        var closed=Fixture();var open=Fixture(blocker:false);var far=Fixture(distance:2);
        var off=session.Capture(closed,Size,Size);
        var dark=session.Capture(closed,Size,Size,rayGiOptions:Gi());
        var clear=session.Capture(open,Size,Size,rayGiOptions:Gi());
        var farOff=session.Capture(far,Size,Size);
        var farOn=session.Capture(far,Size,Size,rayGiOptions:Gi());
        var tiny=session.Capture(closed,Size,Size,rayGiOptions:Gi(radius:.05f));
        Save("closed-off",off);Save("closed-on",dark);Save("open-on",clear);Save("far-on",farOn);
        Assert.True(Red(off)-Red(dark)>50,$"closed ambient center must darken: {Red(off)} -> {Red(dark)}");
        Assert.Equal(Red(off),Red(clear));Assert.Equal(farOff.Rgba,farOn.Rgba);Assert.Equal(off.Rgba,tiny.Rgba);
        Assert.Equal(0,BackgroundChanges(off,dark));AssertAlphaEqual(off,dark);
    }

    [Theory]
    [InlineData(false,false)]
    [InlineData(true,false)]
    [InlineData(false,true)]
    [InlineData(true,true)]
    public void ZeroAmbientPreservesDiffuseLightingSpecularAndAdditiveSphere(bool specular,bool addSphere)
    {
        using var session=new D3D12FrameSession();
        var frame=Fixture(ambient:0,specular:specular,sphere:addSphere?OriginalSphereMode.Add:OriginalSphereMode.None);
        var off=session.Capture(frame,Size,Size);var on=session.Capture(frame,Size,Size,rayGiOptions:Gi());
        Assert.True(Red(off)>20);Assert.Equal(off.Rgba,on.Rgba);
    }

    [Fact]
    public void AmbientRemovalKeepsSpecularAndAdditiveSphereAndScalesOnlyMultiplySphere()
    {
        using var session=new D3D12FrameSession();
        int Delta(bool specular=false,OriginalSphereMode sphere=OriginalSphereMode.None)
        {
            var frame=Fixture(specular:specular,sphere:sphere);
            return Red(session.Capture(frame,Size,Size))-Red(session.Capture(frame,Size,Size,rayGiOptions:Gi()));
        }
        int plain=Delta(),spec=Delta(specular:true),add=Delta(sphere:OriginalSphereMode.Add),multiply=Delta(sphere:OriginalSphereMode.Multiply);
        Assert.True(plain>50);Assert.InRange(spec,plain-1,plain+1);Assert.InRange(add,plain-1,plain+1);
        Assert.InRange(multiply,plain/2-2,plain/2+2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OccludableFillFollowsToonAndOutsideShadowUvUsesFactorOne(bool outside)
    {
        using var session=new D3D12FrameSession();var frame=Fixture(toon:true,outside:outside);
        var off=session.Capture(frame,Size,Size);var on=session.Capture(frame,Size,Size,rayGiOptions:Gi());
        int loss=Red(off)-Red(on);
        Assert.True(loss>25);
        var other=Fixture(toon:true,outside:!outside);
        int otherLoss=Red(session.Capture(other,Size,Size))-Red(session.Capture(other,Size,Size,rayGiOptions:Gi()));
        if(outside)Assert.InRange(loss,otherLoss*2-1,otherLoss*2+1);
        else Assert.InRange(otherLoss,loss*2-1,loss*2+1);
        Save(outside?"outside-shadow-on":"toon-on",on);
    }

    [Theory]
    [InlineData(0.5f,255,false)]
    [InlineData(1f,128,false)]
    [InlineData(1f,255,true)]
    public void TranslucentReceiversAndForegroundOutlineKeepTheirAcceptedPixels(float alpha,byte textureAlpha,bool outline)
    {
        using var session=new D3D12FrameSession();var frame=Fixture(alpha:alpha,textureAlpha:textureAlpha,outline:outline);
        var off=session.Capture(frame,Size,Size);var on=session.Capture(frame,Size,Size,rayGiOptions:Gi());
        Assert.Equal(Center(off),Center(on));AssertAlphaEqual(off,on);Assert.Equal(0,BackgroundChanges(off,on));
        if(outline)Assert.Equal(new byte[]{0,0,0,255},Center(on));
    }

    [Fact]
    public void OffAndOcclusionZeroKeepFrozenPreviousFramesAndSoftwareHardwareAgree()
    {
        using var session=new D3D12FrameSession();var frame=Fixture(specular:true,sphere:OriginalSphereMode.Add);
        var off=session.Capture(frame,Size,Size);
        var legacy=session.Capture(frame,Size,Size,rayGiOptions:Gi() with {Mode=LightingMode.Legacy});
        var oldGi=session.Capture(frame,Size,Size,rayGiOptions:Gi(occlusion:false,strength:.5f));
        var software=session.Capture(frame,Size,Size,rayGiOptions:Gi(strength:.5f));
        var bounce=Fixture(blocker:false,bounce:true);
        var transport=Gi(occlusion:false,strength:2) with {DirectScale=1,DirectionalLightDirection=Vector3.Normalize(new(.5f,1,.5f))};
        var bounceOff=session.Capture(bounce,Size,Size);var bounceGi=session.Capture(bounce,Size,Size,rayGiOptions:transport);
        Assert.Equal(off.Rgba,legacy.Rgba);Save("compat-off",off);Save("compat-ao0",oldGi);
        Save("compat-bounce-ao0",bounceGi);
        string? baseline=Environment.GetEnvironmentVariable("MMD_NEAR_OCCLUSION_BASELINE");
        if(baseline is not null)
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(baseline,"compat-off.rgba")),off.Rgba);
            Assert.Equal(File.ReadAllBytes(Path.Combine(baseline,"compat-ao0.rgba")),oldGi.Rgba);
            Assert.Equal(File.ReadAllBytes(Path.Combine(baseline,"compat-bounce-ao0.rgba")),bounceGi.Rgba);
        }
        Assert.True(Red(bounceGi)>Red(bounceOff),$"AO0 control must still have real secondary diffuse radiance: {Red(bounceOff)} -> {Red(bounceGi)}.");
        Assert.True(Red(oldGi)-Red(software)>50);
        Assert.True(session.QueryRayGiCapabilities().HardwareAvailable);
        var hardware=session.Capture(frame,Size,Size,rayGiOptions:Gi(strength:.5f) with {Mode=LightingMode.HardwareRayTracing});
        int max=software.Rgba.Zip(hardware.Rgba,(a,b)=>Math.Abs(a-b)).Max();
        Assert.InRange(max,0,1);Save("software-on",software);Save("hardware-on",hardware);
        var bounceHardware=session.Capture(bounce,Size,Size,rayGiOptions:transport with {Mode=LightingMode.HardwareRayTracing});
        Assert.InRange(bounceGi.Rgba.Zip(bounceHardware.Rgba,(a,b)=>Math.Abs(a-b)).Max(),0,1);
        using var present=session.Render(frame,Size,Size,rayGiOptions:Gi(spp:4));
        Assert.Equal(0,present.Statistics.ReadbackCount);Assert.Equal(1,present.Statistics.SubmissionCount);
    }

    private static byte Red(RenderedFrame frame)=>frame.Rgba[(Size/2*Size+Size/2)*4];
    private static byte[] Center(RenderedFrame frame)=>frame.Rgba.AsSpan((Size/2*Size+Size/2)*4,4).ToArray();
    private static void AssertAlphaEqual(RenderedFrame a,RenderedFrame b)
        =>Assert.Equal(Enumerable.Range(0,Size*Size).Select(i=>a.Rgba[i*4+3]),Enumerable.Range(0,Size*Size).Select(i=>b.Rgba[i*4+3]));
    private static int BackgroundChanges(RenderedFrame a,RenderedFrame b)=>Enumerable.Range(0,Size*Size).Count(p=>
        a.Rgba.AsSpan(p*4,3).SequenceEqual(new byte[]{20,24,32})&&!a.Rgba.AsSpan(p*4,3).SequenceEqual(b.Rgba.AsSpan(p*4,3)));
    private static void Save(string name,RenderedFrame frame)
    {
        string? output=Environment.GetEnvironmentVariable("MMD_NEAR_OCCLUSION_ARTIFACTS");if(output is null)return;
        Directory.CreateDirectory(output);File.WriteAllBytes(Path.Combine(output,name+".rgba"),frame.Rgba);
        File.WriteAllText(Path.Combine(output,name+".json"),JsonSerializer.Serialize(new {width=Size,height=Size,center=Center(frame).Select(v=>(int)v).ToArray()}));
    }

    private static OriginalFrameScene Fixture(bool blocker=true,float distance=.2f,float ambient=.32f,bool specular=false,
        OriginalSphereMode sphere=OriginalSphereMode.None,bool toon=false,bool outside=false,float alpha=1,byte textureAlpha=255,bool outline=false,bool bounce=false)
    {
        var vertices=new List<SceneVertex>();var indices=new List<uint>();var materials=new List<SceneMaterial>();
        var bindings=new List<OriginalStyleMaterialBinding>();var flags=new List<OriginalFrameMaterialFlags>();
        void Quad(string name,float x0,float x1,float y0,float y1,float z,bool receiver=false,bool edge=false)
        {
            uint start=(uint)vertices.Count;int offset=indices.Count;
            vertices.AddRange([new(new(x0,y0,z),-Vector3.UnitZ,Vector2.Zero),new(new(x1,y0,z),-Vector3.UnitZ,Vector2.Zero),
                new(new(x1,y1,z),-Vector3.UnitZ,Vector2.Zero),new(new(x0,y1,z),-Vector3.UnitZ,Vector2.Zero)]);
            indices.AddRange((edge?new uint[]{0,1,2,0,2,3}:new uint[]{0,2,1,0,3,2}).Select(i=>i+start));
            materials.Add(new(name,offset,6,receiver?new(.25f,.25f,.25f,alpha):new(.1f,.1f,.1f,1),receiver&&specular?new(.18f):Vector3.Zero,
                receiver?new(ambient):Vector3.Zero,1,receiver&&textureAlpha<255?0:-1));
            bindings.Add(new(toon?new(.5f,.7f,.9f):Vector3.One,receiver&&sphere!=OriginalSphereMode.None?1:-1,
                receiver?sphere:OriginalSphereMode.None,DoubleSided:!receiver));
            flags.Add(new(false,false,false,SurfaceEnabled:!edge));
        }
        Quad("receiver",-.7f,.7f,-.7f,.7f,0,true);
        if(blocker)
        {
            const float outer=1.7f,hole=.05f;
            Quad("near left",-outer,-hole,-outer,outer,-distance);
            Quad("near right",hole,outer,-outer,outer,-distance);
            Quad("near lower",-hole,hole,-outer,-hole,-distance);
            Quad("near upper",-hole,hole,hole,outer,-distance);
        }
        if(bounce)Quad("distant bounce",-15,15,-15,15,-8);
        if(outline)Quad("outline",-.8f,.8f,-.8f,.8f,-.1f,edge:true);
        var camera=new SceneCamera(new(0,0,-6),Vector3.Zero,Vector3.UnitY,4.5f,.1f,30);
        SceneTexture[] textures=[new("alpha",1,1,[255,255,255,textureAlpha]),new("sphere",1,1,[128,128,128,255])];
        // Add sphere uses a lower radiance so no channel saturates with the highlight.
        if(sphere==OriginalSphereMode.Add)textures[1]=new("sphere add",1,1,[46,46,46,255]);
        var snapshot=new SceneSnapshot(vertices.ToArray(),indices.ToArray(),materials,textures,camera:camera,
            lightDirection:toon?Vector3.UnitY:-Vector3.UnitZ);
        var surface=new OriginalStyleScene(snapshot,bindings,new(new(.602f),Vector3.One,Matrix4x4.Identity,OriginalTextureSampling.PointClamp));
        var shadow=outside?new OriginalSelfShadowOptions(Matrix4x4.CreateTranslation(0,4,0),16,16,false,false,OriginalTextureSampling.PointClamp,OriginalDepthComparison.LessEqual):null;
        var options=new OriginalFrameOptions(OriginalAlphaBlendMode.Normal,true,false,shadow,null);
        if(!outline)return new(surface,options:options,materials:flags);
        var edgeVertices=vertices.Select((v,i)=>new OriginalOutlineVertex(v.Position,Vector3.Zero,OriginalOutlineVertexMode.Expand,1,i/4));
        var edgeMaterials=materials.Select((_,i)=>new OriginalOutlineMaterial(1,new(0,0,0,1),i==materials.Count-1));
        return new(surface,new(edgeVertices,edgeMaterials),new(new(0,0,0,1),Matrix4x4.Identity,camera.Eye,30,20),options,flags);
    }
}
