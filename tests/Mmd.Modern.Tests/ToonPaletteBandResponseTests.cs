using System.Numerics;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class ToonPaletteBandResponseTests
{
    private static Vector3 Bands(Vector3 d,Vector3 toon,float c,Vector3 incident,float strength=1,bool inside=true)
        =>RayGiToonPaletteMath.EvaluateDeltaWithResponse(d,toon,c,incident,strength,inside,RayGiToonResponse.PreservePaletteBands);

    [Fact]
    public void ResponseIsExplicitValidatedAndRoundTripsWithoutChangingDefaults()
    {
        Assert.Equal(RayGiToonResponse.Continuous,new RayGiOptions().ToonResponse);
        var options=new RayGiOptions(Composition:RayGiComposition.ToonPalette) {ToonResponse=RayGiToonResponse.PreservePaletteBands};
        options.Validate();Assert.Equal(options,JsonSerializer.Deserialize<RayGiOptions>(JsonSerializer.Serialize(options)));
        Assert.Throws<ArgumentOutOfRangeException>(()=>(options with {ToonResponse=(RayGiToonResponse)77}).Validate());
        Assert.Throws<ArgumentException>(()=>(options with {Composition=RayGiComposition.Additive}).Validate());
        Assert.NotEqual(options,options with {ToonResponse=RayGiToonResponse.Continuous});
    }

    [Fact]
    public void FiveFixedFillLevelsRetainAtLeastThreeQuartersOfOriginalContrast()
    {
        var values=new HashSet<float>();
        for(int i=0;i<=1000;i++)values.Add(Bands(Vector3.One,Vector3.Zero,0,new(i/1000f)).X);
        Assert.Equal(new[]{0f,.0625f,.125f,.1875f,.25f},values.Order().ToArray());
        Assert.Equal(Bands(Vector3.One,Vector3.Zero,0,new(.4f)),Bands(Vector3.One,Vector3.Zero,0,new(.6f)));
        Vector3 d=new(.8f,.5f,.2f),toon=new(.2f,.4f,.6f);
        Vector3 budget=d*(Vector3.One-toon),delta=Bands(d,toon,0,new(100),100);
        Assert.Equal(budget*.25f,delta);Assert.Equal(.25f,RayGiToonPaletteMath.MaximumPaletteBandFill);
    }

    [Fact]
    public void ColoredIncidentUsesOneScalarAlongOriginalPaletteDirectionAndKeepsWhiteNeutral()
    {
        Vector3 d=new(.8f,.5f,.2f),toon=new(.2f,.4f,.6f),budget=d*(Vector3.One-toon);
        Vector3 delta=Bands(d,toon,0,new(0,4,0));
        Assert.Equal(budget*.25f,delta);
        Vector3 white=Bands(Vector3.One,new(.3f),0,new(0,4,0));
        Assert.Equal(white.X,white.Y);Assert.Equal(white.Y,white.Z);
        Assert.Equal(Vector3.Zero,Bands(Vector3.Zero,toon,0,Vector3.One));
    }

    [Fact]
    public void EndpointsZeroInputAndDisabledResponseAreExact()
    {
        Vector3 d=new(.8f,.5f,.2f),toon=new(.2f,.4f,.6f),incident=new(.4f,.8f,.3f);
        Assert.Equal(Vector3.Zero,Bands(d,toon,0,Vector3.Zero));
        Assert.Equal(Vector3.Zero,Bands(d,toon,0,incident,0));
        Assert.Equal(Vector3.Zero,Bands(d,toon,1,incident));
        Assert.Equal(Vector3.Zero,Bands(d,Vector3.One,0,incident));
        Assert.Equal(Vector3.Zero,Bands(d,toon,0,incident,inside:false));
        Assert.Equal(RayGiToonPaletteMath.EvaluateDelta(d,toon,.2f,incident,.5f),
            RayGiToonPaletteMath.EvaluateDeltaWithResponse(d,toon,.2f,incident,.5f));
    }
}

[Trait("Category","Gpu")]
public sealed class D3D12ToonPaletteBandResponseTests
{
    private const int Size=65;
    private static RayGiOptions Options(LightingMode mode)=>new(mode,Strength:10,SamplesPerPixel:128,Seed:17,
        MaxRayDistance:50,RayEpsilon:.001f,DirectionalLightDirection:Vector3.UnitZ,DirectionalIrradiance:new(3),
        Composition:RayGiComposition.ToonPalette) {ToonResponse=RayGiToonResponse.PreservePaletteBands};
    private static byte[] Center(RenderedFrame frame)=>frame.Rgba.AsSpan((Size/2*Size+Size/2)*4,4).ToArray();

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void BothBackendsKeepRealGiWithinTheOriginalPaletteSegment(LightingMode mode)
    {
        using var session=new D3D12FrameSession();var frame=D3D12GiTextureFactorTests.Fixture();var options=Options(mode);
        if(mode==LightingMode.HardwareRayTracing)Assert.True(session.QueryRayGiCapabilities().HardwareAvailable);
        var off=session.Capture(frame,Size,Size);var bands=session.Capture(frame,Size,Size,rayGiOptions:options);
        Assert.Equal(RayGiToonResponse.PreservePaletteBands,session.RayGiStatistics!.ToonResponse);
        var continuous=session.Capture(frame,Size,Size,rayGiOptions:options with {ToonResponse=RayGiToonResponse.Continuous});
        byte[] a=Center(off),b=Center(bands),c=Center(continuous);
        Assert.True(b[0]>a[0]+3,"The band response must retain actual traced indirect light.");
        for(int channel=0;channel<3;channel++)
        {
            Assert.True(c[channel]>b[channel]);
            Assert.InRange(b[channel]-a[channel],0,(c[channel]-a[channel])*.25+2);
        }
        AssertCoverage(off,bands);
        Assert.Equal(continuous.Rgba,session.Capture(frame,Size,Size,rayGiOptions:options with {ToonResponse=RayGiToonResponse.Continuous}).Rgba);
        var bindings=frame.Surface.Materials.ToArray();bindings[0]=bindings[0] with {ToonShadowColor=new(.4f)};
        var neutral=new OriginalFrameScene(new(frame.Surface.Snapshot,bindings,frame.Surface.Profile),options:frame.Options,materials:frame.Materials);
        var n0=Center(session.Capture(neutral,Size,Size));var n1=Center(session.Capture(neutral,Size,Size,rayGiOptions:options));
        int red=n1[0]-n0[0],green=n1[1]-n0[1],blue=n1[2]-n0[2];
        Assert.True(red>3);Assert.InRange(Math.Abs(red-green),0,1);Assert.InRange(Math.Abs(red-blue),0,1);
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void LitEndpointZeroStrengthAndOutlinePixelsStayExact(LightingMode mode)
    {
        using var session=new D3D12FrameSession();var source=D3D12GiTextureFactorTests.Fixture();var options=Options(mode);
        var lit=new OriginalFrameScene(new(source.Surface.Snapshot.WithLighting(-Vector3.UnitZ),source.Surface.Materials,source.Surface.Profile),options:source.Options,materials:source.Materials);
        Assert.Equal(session.Capture(lit,Size,Size).Rgba,session.Capture(lit,Size,Size,rayGiOptions:options).Rgba);
        Assert.Equal(session.Capture(source,Size,Size).Rgba,session.Capture(source,Size,Size,rayGiOptions:options with {Strength=0}).Rgba);
        // A separate front-facing edge-only quad makes original edge coverage observable.
        var old=source.Surface.Snapshot;var vertices=old.Vertices.ToArray().ToList();
        vertices.AddRange([new(new(-.9f,-.9f,-.1f),-Vector3.UnitZ,Vector2.Zero),new(new(.9f,-.9f,-.1f),-Vector3.UnitZ,Vector2.Zero),
            new(new(.9f,.9f,-.1f),-Vector3.UnitZ,Vector2.Zero),new(new(-.9f,.9f,-.1f),-Vector3.UnitZ,Vector2.Zero)]);
        var indices=old.Indices.ToArray().Concat(new uint[]{8,9,10,8,10,11}).ToArray();
        var materials=old.Materials.Concat([new SceneMaterial("edge only",12,6,Vector4.One,Vector3.Zero,Vector3.Zero,1)]).ToArray();
        var mesh=new SceneSnapshot(vertices.ToArray(),indices,materials,old.Textures,old.World,old.Camera,old.LightDirection);
        var surface=new OriginalStyleScene(mesh,source.Surface.Materials.Concat([new OriginalStyleMaterialBinding(Vector3.One)]),source.Surface.Profile);
        var edge=vertices.Select((v,i)=>new OriginalOutlineVertex(v.Position,Vector3.Zero,OriginalOutlineVertexMode.Expand,1,i/4));
        var outlined=new OriginalFrameScene(surface,new(edge,[new(0,Vector4.Zero,false),new(0,Vector4.Zero,false),new(1,new(0,0,0,1))]),
            new(new(0,0,0,1),Matrix4x4.Identity,old.Camera.Eye,30,20),source.Options,source.Materials.Concat([new OriginalFrameMaterialFlags(false,false,false,SurfaceEnabled:false)]));
        var off=session.Capture(outlined,Size,Size);var on=session.Capture(outlined,Size,Size,rayGiOptions:options);AssertCoverage(off,on);
        int edgePixels=0;
        for(int i=0;i<off.Rgba.Length;i+=4)if(off.Rgba[i]==0&&off.Rgba[i+1]==0&&off.Rgba[i+2]==0&&off.Rgba[i+3]==255)
        {edgePixels++;Assert.Equal(off.Rgba.AsSpan(i,4).ToArray(),on.Rgba.AsSpan(i,4).ToArray());}
        Assert.True(edgePixels>0,"The fixture must contain visible original outline pixels.");
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void TextureMorphAndNativeDirectCombinationRetainBoundedIndirectFill(LightingMode mode)
    {
        using var session=new D3D12FrameSession();var source=D3D12GiTextureFactorTests.Fixture();
        var factors=OriginalMaterialTextureFactors.Identity with {Diffuse=new(new(.6f,.8f,1,1),Vector4.Zero)};
        var morphed=D3D12GiTextureFactorTests.WithFactors(source,0,factors);
        var light=new NativeSceneLight(Guid.NewGuid(),"palette direct",SceneLightKind.Directional,Vector3.Zero,
            Quaternion.Identity,new(.2f,.3f,.4f),.1f,CastShadows:false);
        var frame=new OriginalFrameScene(morphed.Surface,options:morphed.Options,materials:morphed.Materials,lighting:new([light]));
        var off=session.Capture(frame,Size,Size);var options=Options(mode);
        var bands=session.Capture(frame,Size,Size,rayGiOptions:options);
        Assert.Equal(1,session.RayGiStatistics!.SecondaryNativeLights);AssertCoverage(off,bands);
        var continuous=session.Capture(frame,Size,Size,rayGiOptions:options with {ToonResponse=RayGiToonResponse.Continuous});
        var a=Center(off);var b=Center(bands);var c=Center(continuous);
        Assert.True(b[0]>a[0]);Assert.True(c[0]>b[0]);Assert.True(b[1]>a[1]);Assert.True(b[2]>a[2]);
    }

    private static void AssertCoverage(RenderedFrame off,RenderedFrame on)
    {
        for(int i=0;i<off.Rgba.Length;i+=4)
        {
            Assert.Equal(off.Rgba[i+3],on.Rgba[i+3]);
            if(off.Rgba[i+3]==0)Assert.Equal(off.Rgba.AsSpan(i,4).ToArray(),on.Rgba.AsSpan(i,4).ToArray());
        }
    }
}
