using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category","Gpu")]
public sealed class D3D12GiTextureFactorTests
{
    private const int Width=97,Height=73;
    internal static RayGiOptions Options(LightingMode mode=LightingMode.SoftwareRayTracing,bool denoise=false)
        =>new(mode,Strength:.5f,SamplesPerPixel:64,Seed:17,MaxRayDistance:50,RayEpsilon:.001f,
            DirectionalLightDirection:Vector3.UnitZ,DirectionalIrradiance:new(3),
            Composition:RayGiComposition.ToonPalette,Denoiser:denoise?RayGiDenoiser.Spatial:RayGiDenoiser.Off);

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing,false)]
    [InlineData(LightingMode.HardwareRayTracing,false)]
    [InlineData(LightingMode.SoftwareRayTracing,true)]
    [InlineData(LightingMode.HardwareRayTracing,true)]
    public void SecondaryDiffuseMorphEqualsIndependentAdjustedTexelAndChangesBounce(LightingMode mode,bool denoise)
    {
        using var session=new D3D12FrameSession();Require(session,mode);
        var original=Fixture();var options=Options(mode,denoise);
        var factor=new OriginalTextureFactor(new(.5f,.5f,.5f,1),Vector4.Zero);
        var morphed=WithFactors(original,1,OriginalMaterialTextureFactors.Identity with {Diffuse=factor});
        var adjusted=WithTexture(original,1,[32,64,96,255]);
        // A second material selects the variant globally; the unchanged receiver still
        // uses the accepted colour/alpha when no indirect contribution is composed.
        Assert.Equal(session.Capture(original,Width,Height).Rgba,session.Capture(morphed,Width,Height).Rgba);
        var before=session.Capture(original,Width,Height,rayGiOptions:options);
        var actual=session.Capture(morphed,Width,Height,rayGiOptions:options);
        var expected=session.Capture(adjusted,Width,Height,rayGiOptions:options);
        AssertPixels(expected,actual);
        Assert.NotEqual(Center(before),Center(actual));
        Assert.Equal(mode,session.RayGiStatistics!.Effective);
        Assert.Equal(original.Surface.Snapshot.Indices.Length/3,RayGiScene.FromOriginal(morphed).Triangles.Length);
        Assert.Equal(original.Surface.Snapshot.Materials[1].Diffuse.W,morphed.Surface.Snapshot.Materials[1].Diffuse.W);
        Assert.Equal(before.Rgba,session.Capture(original,Width,Height,rayGiOptions:options).Rgba);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ReceiverDiffuseSphereAndHostToonUseOneEffectivePaletteBudget(int channel)
    {
        using var session=new D3D12FrameSession();
        var source=Fixture(sphere:channel==1?OriginalSphereMode.Multiply:OriginalSphereMode.None);
        var half=new OriginalTextureFactor(new(.5f,.5f,.5f,1),Vector4.Zero);
        var factors=channel switch {
            0=>OriginalMaterialTextureFactors.Identity with {Diffuse=half},
            1=>OriginalMaterialTextureFactors.Identity with {Sphere=half},
            _=>OriginalMaterialTextureFactors.Identity with {Toon=half with {Multiply=new(.5f,.5f,.5f,0)}} };
        var morphed=WithFactors(source,0,factors);
        OriginalFrameScene expected;
        if(channel==0)expected=WithTexture(source,0,[96,96,96,255]);
        else if(channel==1)expected=WithTexture(source,2,[64,64,64,255]);
        else
        {
            var bindings=source.Surface.Materials.ToArray();bindings[0]=bindings[0] with {ToonShadowColor=bindings[0].ToonShadowColor*.5f};
            expected=Copy(source,new(source.Surface.Snapshot,bindings,source.Surface.Profile));
        }
        var options=Options();
        AssertPixels(session.Capture(expected,Width,Height,rayGiOptions:options),session.Capture(morphed,Width,Height,rayGiOptions:options));
        Assert.Equal(session.Capture(morphed,Width,Height).Rgba,
            session.Capture(morphed,Width,Height,rayGiOptions:options with {Strength=0}).Rgba);
    }

    [Theory]
    [InlineData(RayGiComposition.Additive)]
    [InlineData(RayGiComposition.ToonPalette)]
    public void ZeroWeightDiffuseStillKeepsRawAlphaAndMissingMapIsIgnored(RayGiComposition composition)
    {
        using var session=new D3D12FrameSession();
        var source=Fixture();
        var zero=new OriginalTextureFactor(new(4,-3,2,-.5f),new(.4f,.2f,-.1f,.5f));
        var factors=OriginalMaterialTextureFactors.Identity with {Diffuse=zero};
        var morph=WithFactors(source,1,factors);
        var white=WithTexture(source,1,[255,255,255,255]);
        var options=Options() with {Composition=composition};
        AssertPixels(session.Capture(white,Width,Height,rayGiOptions:options),session.Capture(morph,Width,Height,rayGiOptions:options));
        var old=source.Surface.Snapshot;
        var materials=old.Materials.ToArray();materials[1]=materials[1] with {DiffuseTexture=-1};
        var noMap=Copy(source,new(old.WithMaterials(materials),source.Surface.Materials,source.Surface.Profile));
        Assert.Equal(session.Capture(noMap,Width,Height,rayGiOptions:options).Rgba,
            session.Capture(WithFactors(noMap,1,factors),Width,Height,rayGiOptions:options).Rgba);
        var cutout=WithTexture(source,1,[64,128,192,0]);
        var cutoutMorph=WithFactors(cutout,1,factors);
        // No colour factor turns an invisible raw-alpha wall into a GI emitter/occluder.
        Assert.Equal(session.Capture(cutout,Width,Height,rayGiOptions:options).Rgba,
            session.Capture(cutoutMorph,Width,Height,rayGiOptions:options).Rgba);
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void NativeLightSsaaAbortAndRepeatedFactorPosesKeepBoundedLeases(LightingMode mode)
    {
        using var session=new D3D12FrameSession();Require(session,mode);
        var source=Fixture();
        var light=new NativeSceneLight(Guid.NewGuid(),"factor fill",SceneLightKind.Directional,Vector3.Zero,
            Quaternion.Identity,new(.2f,.3f,.4f),.1f,CastShadows:false);
        source=new(source.Surface,options:source.Options! with {SpatialSamples=2},materials:source.Materials,lighting:new([light]));
        var options=Options(mode,true) with {SamplesPerPixel=4};
        OriginalFrameScene Pose(float value)=>WithFactors(source,0,OriginalMaterialTextureFactors.Identity with {
            Diffuse=new(new(value,value,value,1),Vector4.Zero)});
        var selected=Pose(.5f);
        Assert.Throws<OperationCanceledException>(()=>session.RenderForPresent(selected,33,25,options,
            (_,_)=>throw new OperationCanceledException("Abort morphed GI after recording")));
        Assert.False(session.Runtime.IsRecording);
        var recovered=session.Capture(selected,33,25,rayGiOptions:options);
        using(var fresh=new D3D12FrameSession())Assert.Equal(fresh.Capture(selected,33,25,rayGiOptions:options).Rgba,recovered.Rgba);
        int cached=session.Statistics!.CachedResources;
        for(int i=0;i<8;i++)
        {
            using var output=session.Render(Pose(.5f+i*.025f),33,25,rayGiOptions:options);
            Assert.Equal(0,output.Statistics.ReadbackCount);
            Assert.Equal(2,session.RayGiStatistics!.DispatchCount);
            Assert.Equal(cached,output.Statistics.CachedResources);
        }
        Assert.Equal(recovered.Rgba,session.Capture(selected,33,25,rayGiOptions:options).Rgba);
        session.WaitForIdle();Assert.InRange(session.Runtime.LiveFrameResources,0,3);
    }

    [Fact]
    public void InvalidEffectiveDomainsFailBeforeBeginAndPreserveNextOrdinaryFrame()
    {
        using var session=new D3D12FrameSession();var source=Fixture();
        var baseline=session.Capture(source,33,25);long frame=session.Runtime.FrameId,submissions=session.Runtime.Submissions;
        foreach(var factor in new[] {
            OriginalMaterialTextureFactors.Identity with {Diffuse=new(new(1,1,1,float.MaxValue),new(0,0,0,float.MaxValue))},
            OriginalMaterialTextureFactors.Identity with {Diffuse=new(Vector4.One,new(-2,-2,-2,0))},
            OriginalMaterialTextureFactors.Identity with {Toon=new(new(8,8,8,0),Vector4.Zero)} })
        {
            Assert.Throws<NotSupportedException>(()=>session.Capture(WithFactors(source,0,factor),33,25,rayGiOptions:Options()));
            Assert.Equal(frame,session.Runtime.FrameId);Assert.Equal(submissions,session.Runtime.Submissions);
        }
        Assert.Equal(baseline.Rgba,session.Capture(source,33,25).Rgba);
    }

    [Fact]
    public void EffectiveDiffuseDoesNotAddGiToLitOrWhiteToonOrNoHitControls()
    {
        using var session=new D3D12FrameSession();var source=Fixture();
        var factor=OriginalMaterialTextureFactors.Identity with {Diffuse=new(new(.5f,.5f,.5f,1),Vector4.Zero)};
        var lit=Copy(source,new(source.Surface.Snapshot.WithLighting(-Vector3.UnitZ),source.Surface.Materials,source.Surface.Profile));
        var bindings=source.Surface.Materials.ToArray();bindings[0]=bindings[0] with {ToonShadowColor=Vector3.One};
        var whiteToon=Copy(source,new(source.Surface.Snapshot,bindings,source.Surface.Profile));
        var flags=source.Materials.ToArray();flags[1]=flags[1] with {SurfaceEnabled=false};
        var noHit=new OriginalFrameScene(source.Surface,options:source.Options,materials:flags);
        foreach(var control in new[]{lit,whiteToon,noHit})
        {
            var frame=WithFactors(control,0,factor);
            Assert.Equal(Center(session.Capture(frame,Width,Height)),Center(session.Capture(frame,Width,Height,rayGiOptions:Options())));
        }
    }

    internal static OriginalFrameScene Fixture(OriginalSphereMode sphere=OriginalSphereMode.None)
    {
        SceneVertex[] Quad(float z,float half,Vector3 normal)=>[
            new(new(-half,-half,z),normal,Vector2.Zero),new(new(half,-half,z),normal,Vector2.Zero),
            new(new(half,half,z),normal,Vector2.Zero),new(new(-half,half,z),normal,Vector2.Zero)];
        var vertices=Quad(0,.8f,-Vector3.UnitZ).Concat(Quad(-8,15,Vector3.UnitZ)).ToArray();
        SceneTexture[] textures=[new("receiver",1,1,[192,192,192,255]),new("wall",1,1,[64,128,192,255]),new("sphere",1,1,[128,128,128,255])];
        SceneMaterial[] materials=[new("receiver",0,6,new(.6f,.6f,.6f,1),Vector3.Zero,Vector3.Zero,1,0),
            new("bounce",6,6,Vector4.One,Vector3.Zero,Vector3.Zero,1,1)];
        var snapshot=new SceneSnapshot(vertices,[0,2,1,0,3,2,4,5,6,4,6,7],materials,textures,
            camera:new(new(0,0,-6),Vector3.Zero,Vector3.UnitY,4.5f,.1f,30),lightDirection:Vector3.UnitX);
        var surface=new OriginalStyleScene(snapshot,[new(new(.25f,.4f,.5f),sphere==OriginalSphereMode.None?-1:2,sphere),new(Vector3.One,DoubleSided:true)],
            new(new(.8f),Vector3.One,Matrix4x4.Identity,OriginalTextureSampling.PointClamp));
        return new(surface,options:new(OriginalAlphaBlendMode.Normal,true,false),materials:[new(false,false,false),new(false,false,false)]);
    }
    internal static OriginalFrameScene WithFactors(OriginalFrameScene frame,int material,OriginalMaterialTextureFactors factors)
    {
        var bindings=frame.Surface.Materials.ToArray();bindings[material]=bindings[material] with {TextureFactors=factors};
        return Copy(frame,new(frame.Surface.Snapshot,bindings,frame.Surface.Profile));
    }
    private static OriginalFrameScene WithTexture(OriginalFrameScene frame,int index,byte[] bytes)
    {
        var old=frame.Surface.Snapshot;var textures=old.Textures.ToArray();textures[index]=new(textures[index].Name,1,1,bytes);
        var mesh=new SceneSnapshot(old.Vertices,old.Indices,old.Materials,textures,old.World,old.Camera,old.LightDirection);
        return Copy(frame,new(mesh,frame.Surface.Materials,frame.Surface.Profile));
    }
    private static OriginalFrameScene Copy(OriginalFrameScene frame,OriginalStyleScene surface)
        =>new(surface,frame.Outline,frame.OutlineOptions,frame.Options,frame.Materials,frame.Lighting,frame.EditorOverlay);
    private static byte[] Center(RenderedFrame frame)=>frame.Rgba.AsSpan((Height/2*Width+Width/2)*4,4).ToArray();
    private static void AssertPixels(RenderedFrame expected,RenderedFrame actual)
    {
        Assert.Equal(expected.Rgba.Length,actual.Rgba.Length);
        for(int i=0;i<actual.Rgba.Length;i++)Assert.InRange(Math.Abs(expected.Rgba[i]-actual.Rgba[i]),0,1);
    }
    private static void Require(D3D12FrameSession session,LightingMode mode)
    {
        var support=session.QueryRayGiCapabilities();
        Assert.True(mode==LightingMode.HardwareRayTracing?support.HardwareAvailable:support.SoftwareAvailable,support.UnavailableReason);
        Assert.False(session.IsWarp);
    }
}
