using System.Numerics;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

// Root runs these tests serially with all other real-GPU evidence. Author CPU compilation alone
// does not prove execution, original-image matching, visual style acceptance, or RT-core occupancy.
[Trait("Category","Gpu")]
public sealed class D3D12RayGiToonPaletteTests
{
    private const int Size=129;
    private static readonly RayGiOptions Palette=new(LightingMode.SoftwareRayTracing,Strength:.5f,SamplesPerPixel:256,Seed:17,
        MaxRayDistance:50,RayEpsilon:.001f,DirectionalLightDirection:Vector3.UnitZ,DirectionalIrradiance:new(3),Composition:RayGiComposition.ToonPalette);

    [Fact]
    public void LitEndpointOutsideProjectionZeroStrengthAndNoIncidentKeepTheOriginalPixels()
    {
        using var session=new D3D12FrameSession();
        foreach(var frame in new[]{Fixture(control:1),Fixture(outside:true),Fixture(bounce:false),Fixture(toon:Vector3.One)})
        {
            var off=session.Capture(frame,Size,Size);var palette=session.Capture(frame,Size,Size,rayGiOptions:Palette);
            Assert.Equal(off.Rgba,palette.Rgba);
        }
        var shaded=Fixture();var original=session.Capture(shaded,Size,Size);
        Assert.Equal(original.Rgba,session.Capture(shaded,Size,Size,rayGiOptions:Palette with {Strength=0}).Rgba);
        Assert.Equal(original.Rgba,session.Capture(shaded,Size,Size,rayGiOptions:Palette with {Mode=LightingMode.Legacy}).Rgba);
        var positive=session.Capture(shaded,Size,Size,rayGiOptions:Palette);
        Assert.True(Red(positive)-Red(original)>10,"Negative controls require real incident radiance in the shaded control.");
        Save("shaded-off",original,session);Save("shaded-palette",positive,session);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncidentHasExactlyOneReceiverDiffuseFactorAndNoDivisionByDarkChannels(bool texture)
    {
        using var session=new D3D12FrameSession();
        int Delta(float diffuse)
        {
            var frame=Fixture(diffuse:diffuse,texture:texture);
            var off=session.Capture(frame,Size,Size);var on=session.Capture(frame,Size,Size,rayGiOptions:Palette);
            return Red(on)-Red(off);
        }
        int half=Delta(.5f),full=Delta(1);
        // Host Eg=.8*diffuse+.1: material .5 -> .5, material 1 -> .9.
        // The texture case instead scales a fixed Eg=.9 by the 128/255 texel once.
        float ratio=texture?255f/128:.9f/.5f;
        Assert.True(half>10);Assert.InRange(full,ratio*half-2,ratio*half+2);
        var black=Fixture(diffuse:0,texture:texture,ambient:0);
        Assert.Equal(session.Capture(black,Size,Size).Rgba,session.Capture(black,Size,Size,rayGiOptions:Palette).Rgba);
    }

    [Fact]
    public void AddSphereAndSpecularDoNotIncreaseTheDiffuseBudgetWhileMultiplySphereScalesIt()
    {
        using var session=new D3D12FrameSession();
        int Delta(OriginalSphereMode sphere=OriginalSphereMode.None,bool specular=false)
        {
            var frame=Fixture(control:.25f,sphere:sphere,specular:specular);
            return Red(session.Capture(frame,Size,Size,rayGiOptions:Palette))-Red(session.Capture(frame,Size,Size));
        }
        int plain=Delta(),add=Delta(OriginalSphereMode.Add),specular=Delta(specular:true),multiply=Delta(OriginalSphereMode.Multiply);
        Assert.True(plain>10);Assert.InRange(add,plain-1,plain+1);Assert.InRange(specular,plain-1,plain+1);
        Assert.InRange(multiply,plain/2-2,plain/2+2);
    }

    [Fact]
    public void OpaqueReceiverEligibilityAndTransparentForegroundOrderingRemainUnchanged()
    {
        using var session=new D3D12FrameSession();
        foreach(var frame in new[]{Fixture(alpha:.5f),Fixture(textureAlpha:128),Fixture(sphere:OriginalSphereMode.Multiply,sphereAlpha:128),Fixture(outline:true)})
        {
            var off=session.Capture(frame,Size,Size);var on=session.Capture(frame,Size,Size,rayGiOptions:Palette);
            Assert.Equal(Center(off),Center(on));AssertAlphaEqual(off,on);Assert.Equal(0,BackgroundChanges(off,on));
        }
        int Delta(bool overlay)
        {
            var frame=Fixture(overlay:overlay);var off=session.Capture(frame,Size,Size);var on=session.Capture(frame,Size,Size,rayGiOptions:Palette);
            AssertAlphaEqual(off,on);Assert.Equal(0,BackgroundChanges(off,on));return Red(on)-Red(off);
        }
        int original=Delta(false),covered=Delta(true);Assert.True(original>10);Assert.InRange(covered,original/2-2,original/2+2);
    }

    [Fact]
    public void GeometryAndColoredSecondaryRadianceAffectOnlyTheAvailableToonBudget()
    {
        using var session=new D3D12FrameSession();
        var red=Fixture(bounceColor:new(1,.04f,.04f));var cyan=Fixture(bounceColor:new(.04f,1,1));
        byte[] r0=Center(session.Capture(red,Size,Size)),r1=Center(session.Capture(red,Size,Size,rayGiOptions:Palette));
        byte[] c0=Center(session.Capture(cyan,Size,Size)),c1=Center(session.Capture(cyan,Size,Size,rayGiOptions:Palette));
        Assert.True(r1[0]-r0[0]>r1[1]-r0[1]+10);Assert.True(c1[1]-c0[1]>c1[0]-c0[0]+10);
        // The diffuse lit endpoint is .5, so no component may exceed its UNorm conversion.
        Assert.All(r1.Take(3),v=>Assert.InRange(v,(byte)0,(byte)128));
        Assert.All(c1.Take(3),v=>Assert.InRange(v,(byte)0,(byte)128));
    }

    [Fact]
    public void CompositionSwitchesHardwareAbortAndResizeCarryTheirOwnSemanticsWithoutNewReadbacks()
    {
        using var session=new D3D12FrameSession();var frame=Fixture();
        var off=session.Capture(frame,Size,Size);
        var additive=session.Capture(frame,Size,Size,rayGiOptions:Palette with {Composition=RayGiComposition.Additive});
        Assert.Equal(RayGiComposition.Additive,session.RayGiStatistics!.Composition);
        var software=session.Capture(frame,Size,Size,rayGiOptions:Palette);
        Assert.Equal(RayGiComposition.ToonPalette,session.RayGiStatistics!.Composition);
        Assert.Equal("MeanIncidentRadiance",session.RayGiStatistics.RgbSemantic);Assert.Equal(1,session.RayGiStatistics.ReferenceRadiance);
        Assert.True(Red(software)>Red(off));Assert.True(Red(additive)>Red(software));
        Assert.True(session.QueryRayGiCapabilities().HardwareAvailable);
        var hardware=session.Capture(frame,Size,Size,rayGiOptions:Palette with {Mode=LightingMode.HardwareRayTracing});
        Assert.InRange(software.Rgba.Zip(hardware.Rgba,(a,b)=>Math.Abs(a-b)).Max(),0,1);
        Assert.StartsWith("DXR-raw-",session.RayGiBackendIdentity);Assert.False(session.IsWarp);
        Assert.Throws<InvalidOperationException>((Action)(()=>session.RenderForPresent(frame,Size,Size,Palette,(_,_)=>throw new InvalidOperationException("Abort after ToonPalette recording"))));
        Assert.Null(session.RayGiStatistics);
        Assert.Equal(additive.Rgba,session.Capture(frame,Size,Size,rayGiOptions:Palette with {Composition=RayGiComposition.Additive}).Rgba);
        Assert.Equal(software.Rgba,session.Capture(frame,Size,Size,rayGiOptions:Palette).Rgba);
        using(var output=session.Render(frame,101,87,rayGiOptions:Palette with {Mode=LightingMode.HardwareRayTracing,SamplesPerPixel=4}))
        {Assert.Equal(0,output.Statistics.ReadbackCount);Assert.Equal(1,output.Statistics.SubmissionCount);}
        Assert.Equal(off.Rgba,session.Capture(frame,Size,Size).Rgba);Assert.Null(session.RayGiStatistics);
        Save("software-palette",software,session);Save("hardware-palette",hardware,session);
    }

    [Fact]
    public void ExplicitExperimentalAoRemainsSeparateAndNeverTurnsOnWithThePaletteChoice()
    {
        using var session=new D3D12FrameSession();
        var frame=Fixture(bounceDistance:.2f,bounceHalf:8,bounceHole:.05f,control:1);
        var off=session.Capture(frame,Size,Size);var palette=session.Capture(frame,Size,Size,rayGiOptions:Palette);
        var experimental=session.Capture(frame,Size,Size,rayGiOptions:Palette with {OcclusionStrength=1,OcclusionRadius=1});
        Assert.Equal(off.Rgba,palette.Rgba);Assert.True(Red(experimental)<Red(off)-20);
        // This orthogonal old AO subtraction is deliberately outside the no-darkening/style claim.
        Assert.Equal(0,Palette.OcclusionStrength);
    }

    private static byte Red(RenderedFrame frame)=>frame.Rgba[(Size/2*Size+Size/2)*4];
    private static byte[] Center(RenderedFrame frame)=>frame.Rgba.AsSpan((Size/2*Size+Size/2)*4,4).ToArray();
    private static void AssertAlphaEqual(RenderedFrame a,RenderedFrame b)
        =>Assert.Equal(Enumerable.Range(0,Size*Size).Select(i=>a.Rgba[i*4+3]),Enumerable.Range(0,Size*Size).Select(i=>b.Rgba[i*4+3]));
    private static int BackgroundChanges(RenderedFrame a,RenderedFrame b)=>Enumerable.Range(0,Size*Size).Count(p=>
        a.Rgba.AsSpan(p*4,3).SequenceEqual(new byte[]{20,24,32})&&!a.Rgba.AsSpan(p*4,3).SequenceEqual(b.Rgba.AsSpan(p*4,3)));
    private static void Save(string name,RenderedFrame frame,D3D12FrameSession session)
    {
        string? output=Environment.GetEnvironmentVariable("MMD_TOON_PALETTE_GPU_ARTIFACTS");if(output is null)return;
        Directory.CreateDirectory(output);File.WriteAllBytes(Path.Combine(output,name+".rgba"),frame.Rgba);
        File.WriteAllText(Path.Combine(output,name+".json"),JsonSerializer.Serialize(new {width=Size,height=Size,
            center=Center(frame),session.AdapterName,session.IsWarp,
            note="Image pixels only; current session state may have advanced beyond this capture. Per-dispatch semantics are asserted by the tests and recorded by the native preview harness."},new JsonSerializerOptions{WriteIndented=true}));
    }

    private static OriginalFrameScene Fixture(float diffuse=.5f,bool texture=false,Vector3? toon=null,float control=0,
        bool bounce=true,float bounceDistance=8,float bounceHalf=15,Vector3? bounceColor=null,bool outside=false,
        OriginalSphereMode sphere=OriginalSphereMode.None,bool specular=false,float alpha=1,byte textureAlpha=255,byte sphereAlpha=255,bool outline=false,bool overlay=false,float ambient=.1f,float bounceHole=0)
    {
        var vertices=new List<SceneVertex>();var indices=new List<uint>();var materials=new List<SceneMaterial>();
        var bindings=new List<OriginalStyleMaterialBinding>();var flags=new List<OriginalFrameMaterialFlags>();
        void Quad(string name,float half,float z,Vector3 normal,Vector4 color,bool receiver=false,bool edge=false)
        {
            uint start=(uint)vertices.Count;int offset=indices.Count;
            vertices.AddRange([new(new(-half,-half,z),normal,Vector2.Zero),new(new(half,-half,z),normal,Vector2.Zero),new(new(half,half,z),normal,Vector2.Zero),new(new(-half,half,z),normal,Vector2.Zero)]);
            indices.AddRange((edge||normal==Vector3.UnitZ?new uint[]{0,1,2,0,2,3}:new uint[]{0,2,1,0,3,2}).Select(i=>i+start));
            materials.Add(new(name,offset,6,color,receiver&&specular?new(.18f):Vector3.Zero,receiver?new(ambient):Vector3.Zero,1,
                receiver&&(texture||textureAlpha<255)?0:-1));
            bindings.Add(new(receiver?toon??new(.25f,.5f,.75f):Vector3.One,receiver&&sphere!=OriginalSphereMode.None?1:-1,
                receiver?sphere:OriginalSphereMode.None,DoubleSided:!receiver));
            flags.Add(new(false,false,false,SurfaceEnabled:!edge));
        }
        // Ambient .1 is separately allocated only if the caller explicitly enables old AO.
        // Profile diffuse light .8 means receiver .5 gives Eg .5, and 1 gives Eg .9.
        Quad("receiver",.7f,0,-Vector3.UnitZ,new(new Vector3(texture?1:diffuse),alpha),true);
        if(bounce && bounceHole==0)Quad("colored bounce",bounceHalf,-bounceDistance,Vector3.UnitZ,new(bounceColor??Vector3.One,1));
        else if(bounce)
        {
            // A camera ray must see the receiver through the hole while hemisphere rays hit
            // nearby geometry. A solid foreground plane would test the blocker, not receiver AO.
            void Ring(float x0,float x1,float y0,float y1)
            {
                Quad("near ring",1,-bounceDistance,Vector3.UnitZ,new(bounceColor??Vector3.One,1));
                Vector3[] positions=[new(x0,y0,-bounceDistance),new(x1,y0,-bounceDistance),new(x1,y1,-bounceDistance),new(x0,y1,-bounceDistance)];
                for(int i=0;i<4;i++)vertices[vertices.Count-4+i]=vertices[vertices.Count-4+i] with {Position=positions[i]};
            }
            Ring(-bounceHalf,-bounceHole,-bounceHalf,bounceHalf);Ring(bounceHole,bounceHalf,-bounceHalf,bounceHalf);
            Ring(-bounceHole,bounceHole,-bounceHalf,-bounceHole);Ring(-bounceHole,bounceHole,bounceHole,bounceHalf);
        }
        // Cover the center raster pixel but a negligible hemisphere solid angle. A large nearby
        // alpha plane is a binary transport blocker under the existing contract, so it cannot
        // independently test surface blending of the same incident light.
        if(overlay)Quad("transparent foreground",.02f,-3,-Vector3.UnitZ,new(.1f,.2f,.3f,.5f));
        if(outline)Quad("foreground outline",.8f,-.1f,-Vector3.UnitZ,Vector4.One,edge:true);
        byte texel=texture?(byte)MathF.Round(diffuse*255):byte.MaxValue;
        SceneTexture[] textures=[new("diffuse",1,1,[texel,texel,texel,textureAlpha]),new("sphere",1,1,[sphere==OriginalSphereMode.Add?(byte)46:(byte)128,sphere==OriginalSphereMode.Add?(byte)46:(byte)128,sphere==OriginalSphereMode.Add?(byte)46:(byte)128,sphereAlpha])];
        float facing=control/3;Vector3 incoming=new(MathF.Sqrt(1-facing*facing),0,-facing);
        var camera=new SceneCamera(new(0,0,-6),Vector3.Zero,Vector3.UnitY,4.5f,.1f,30);
        var snapshot=new SceneSnapshot(vertices.ToArray(),indices.ToArray(),materials,textures,camera:camera,lightDirection:incoming);
        var profile=new OriginalStyleProfile(new(.8f),Vector3.One,Matrix4x4.Identity,OriginalTextureSampling.PointClamp);
        var surface=new OriginalStyleScene(snapshot,bindings,profile);
        var shadow=outside?new OriginalSelfShadowOptions(Matrix4x4.CreateTranslation(0,4,0),16,16,false,false,OriginalTextureSampling.PointClamp,OriginalDepthComparison.LessEqual):null;
        var options=new OriginalFrameOptions(OriginalAlphaBlendMode.Normal,true,false,shadow,null);
        if(!outline)return new(surface,options:options,materials:flags);
        var edgeVertices=vertices.Select((v,i)=>new OriginalOutlineVertex(v.Position,Vector3.Zero,OriginalOutlineVertexMode.Expand,1,i/4));
        var edgeMaterials=materials.Select((_,i)=>new OriginalOutlineMaterial(1,new(0,0,0,1),i==materials.Count-1));
        return new(surface,new(edgeVertices,edgeMaterials),new(new(0,0,0,1),Matrix4x4.Identity,camera.Eye,30,20),options,flags);
    }
}
