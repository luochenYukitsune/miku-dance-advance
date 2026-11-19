using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category","Gpu")]
public sealed class D3D12SubTextureNativeLightsTests
{
    private const int Width=97,Height=73;
    private static readonly Vector3[] Quadrants=[new(160/255f,32/255f,16/255f),new(16/255f,192/255f,32/255f),
        new(32/255f,16/255f,224/255f),new(128/255f,160/255f,192/255f)];

    [Theory,InlineData(.25f,.25f,0),InlineData(.75f,.25f,1),InlineData(.25f,.75f,2),InlineData(.75f,.75f,3)]
    public void NativeDirectionalColourUsesExplicitUv1RatherThanBaseOrNormalProjection(float u,float v,int quadrant)
    {
        using var session=new D3D12FrameSession();
        var frame=Fixture(new(u,v));
        var light=Light(SceneLightKind.Directional) with {Color=Vector3.One,Intensity=.25f,CastShadows=false};
        Pixel(session.Capture(WithLights(frame,[light]),Width,Height),new(Quadrants[quadrant]*.75f,1));
        Assert.Equal(1,session.NativeLightStatistics!.EnabledLights);
        Assert.False(session.NativeLightStatistics.NativeLightsContributeToGi);
    }

    [Theory,InlineData(SceneLightKind.Directional),InlineData(SceneLightKind.Point),InlineData(SceneLightKind.Spot)]
    public void EachNativeLightAddsPositiveRadianceWithoutChangingSubTextureAlpha(SceneLightKind kind)
    {
        using var session=new D3D12FrameSession();
        var frame=Fixture(new(.25f,.25f),alpha:200/255f);
        var dark=session.Capture(frame,Width,Height);
        var bright=session.Capture(WithLights(frame,[Light(kind) with {CastShadows=false}]),Width,Height);
        int center=(Height/2*Width+Width/2)*4;
        Assert.True(bright.Rgba[center]>dark.Rgba[center]+8,$"{kind}: lit={bright.Rgba[center]}, baseline={dark.Rgba[center]}");
        Assert.Equal(dark.Rgba[center+3],bright.Rgba[center+3]);
        Assert.Equal(0,session.NativeLightStatistics!.ShadowFaces);
    }

    [Theory,InlineData(false,false),InlineData(true,false),InlineData(false,true),InlineData(true,true)]
    public void NativeTextureFactorsRespectMultiplyAddAndNoDiffuseTechnique(bool additive,bool noDiffuse)
    {
        using var session=new D3D12FrameSession();
        var frame=Fixture(new(.75f,.25f),noDiffuse:noDiffuse,additive:additive);
        var factors=new OriginalMaterialTextureFactors(new(new(.5f,.25f,.75f,.5f),new(.1f,.2f,.3f,.25f)),
            new(new(.4f,.6f,.8f,.5f),new(.2f,.1f,0,.25f)),OriginalTextureFactor.Identity);
        frame=new(new(frame.Surface.Snapshot,[frame.Surface.Materials[0] with {TextureFactors=factors}],frame.Surface.Profile),options:frame.Options);
        var light=Light(SceneLightKind.Directional) with {Color=new(.3f,.2f,.1f),Intensity=.25f,CastShadows=false};
        Vector3 diffuse=noDiffuse?Vector3.One:(new Vector3(.5f,.25f,.75f)+new Vector3(.1f,.2f,.3f))*.75f+new Vector3(.25f);
        Vector3 sphere=(Quadrants[1]*new Vector3(.4f,.6f,.8f)+new Vector3(.2f,.1f,0))*.75f;
        bool multiply=!additive||noDiffuse;
        Vector3 original=multiply?.5f*diffuse*(sphere+new Vector3(.25f)):.5f*diffuse+sphere;
        Vector3 direct=diffuse*light.Color*light.Intensity*(multiply?sphere+new Vector3(.25f):Vector3.One);
        Pixel(session.Capture(WithLights(frame,[light]),Width,Height),new(original+direct,1));
    }

    [Theory,InlineData(0f,false,false),InlineData(.5f,false,false),InlineData(1f,false,false),
        InlineData(0f,true,false),InlineData(.5f,true,false),InlineData(1f,true,false),
        InlineData(0f,false,true),InlineData(.5f,false,true)]
    public void NativeDirectPreservesRawToonWeightTranspAndOutsideProjectionAlpha(float c,bool noDiffuse,bool transp)
    {
        using var session=new D3D12FrameSession();
        var source=Fixture(new(.25f,.25f),c,128/255f,.2f,noDiffuse,transp);
        var light=Light(SceneLightKind.Directional) with {CastShadows=false,Intensity=.1f};
        // Side light contributes even when the original incoming light has c=0.
        light=light with {Rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI/2)};
        var frame=WithLights(source,[light]);
        float raw=(noDiffuse?1:128/255f)*(128/255f);
        float alpha=transp?.5f:raw*(noDiffuse||c>=1?1:c+.2f*(1-c));
        Alpha(session.Capture(frame,Width,Height),alpha);
        var outside=Matrix4x4.Identity;outside.M41=10;
        frame=Options(frame,frame.Options! with {SelfShadow=new(outside,8,8,false,false,OriginalTextureSampling.PointClamp,OriginalDepthComparison.LessEqual)});
        Alpha(session.Capture(frame,Width,Height),raw);
    }

    [Theory,InlineData(SceneLightKind.Directional,false,1),InlineData(SceneLightKind.Point,false,6),InlineData(SceneLightKind.Spot,false,1),
        InlineData(SceneLightKind.Directional,true,1),InlineData(SceneLightKind.Point,true,6),InlineData(SceneLightKind.Spot,true,1)]
    public void NativeShadowConsumesUv1RawAlphaAndKeepsCutoutPolicyIndependentOfToonAndMorphAlpha(SceneLightKind kind,bool additive,int faces)
    {
        using var session=new D3D12FrameSession();
        var light=Light(kind);
        var opaque=WithLights(ShadowFixture(new(.75f,.25f),additive),[light]);
        var blocked=session.Capture(opaque,Width,Height);
        var stats=session.NativeLightStatistics!;
        Assert.Equal(faces,stats.ShadowFaces);
        Assert.Equal(faces,stats.ShadowCasterDraws);
        Assert.Equal(kind==SceneLightKind.Point?1:faces,stats.ShadowDrawCalls);
        Assert.Equal(faces,stats.ShadowDrawCalls+stats.ShadowCulledDraws);
        // UV1 first quadrant is clear although base UV and normal projection both sample opaque texels.
        var hole=session.Capture(WithLights(ShadowFixture(new(.25f,.25f),additive),[light]),Width,Height);
        var noShadow=session.Capture(WithLights(opaque,[light with {CastShadows=false}]),Width,Height);
        int center=(Height/2*Width+Width/2)*4;
        Assert.True(noShadow.Rgba[center]>blocked.Rgba[center]+40,$"{kind}: lit={noShadow.Rgba[center]}, shadow={blocked.Rgba[center]}");
        Assert.Equal(noShadow.Rgba,hole.Rgba);
    }

    [Theory,InlineData(1,false,false),InlineData(2,false,true),InlineData(2,true,true)]
    public void NativeSubTextureCanFeedSsaaSmaaAndStraightOutput(int samples,bool smaa,bool straight)
    {
        using var session=new D3D12FrameSession();
        var frame=WithLights(Fixture(new(.25f,.75f),alpha:128/255f),[Light(SceneLightKind.Directional) with {CastShadows=false,Color=Vector3.One,Intensity=.25f}]);
        frame=Options(frame,frame.Options! with {SpatialSamples=samples,Antialiasing=smaa?FrameAntialiasing.Smaa1X:FrameAntialiasing.Off,
            AlphaOutput=straight?FrameAlphaOutput.Straight:FrameAlphaOutput.LegacyMax});
        float alpha=128/255f*(128/255f);
        Pixel(session.Capture(frame,Width,Height),new(Quadrants[2]*.75f*(straight?1:alpha),alpha));
        Assert.Equal(samples,session.Statistics!.SpatialSamples);
        Assert.Equal(smaa?3:0,session.Statistics.AntialiasingPassDrawCalls);
    }

    [Fact]
    public void DisabledNativeLightsReturnExactBaselineAcrossVariantSwitches()
    {
        using var session=new D3D12FrameSession();var source=Fixture(new(.25f,.25f));
        var baseline=session.Capture(source,Width,Height);var light=Light(SceneLightKind.Directional);
        foreach(var inactive in new[]{light with {Enabled=false},light with {Intensity=0},light with {Color=Vector3.Zero}})
        {
            session.Capture(WithLights(source,[light]),Width,Height);
            Assert.Equal(baseline.Rgba,session.Capture(WithLights(source,[inactive]),Width,Height).Rgba);
            Assert.Null(session.NativeLightStatistics);
        }
        var ordinary=new OriginalFrameScene(new(source.Surface.Snapshot,[source.Surface.Materials[0] with {SphereMode=OriginalSphereMode.Multiply}],source.Surface.Profile),options:source.Options);
        var original=session.Capture(ordinary,Width,Height);
        session.Capture(WithLights(source,[light]),Width,Height);
        Assert.Equal(original.Rgba,session.Capture(ordinary,Width,Height).Rgba);
        Assert.Equal(baseline.Rgba,session.Capture(source,Width,Height).Rgba);
    }

    [Fact]
    public void AnimatedUvNativeShadowBuffersRemainBoundedWarmAndAbortSafeAcrossResize()
    {
        using var session=new D3D12FrameSession();var light=Light(SceneLightKind.Directional);
        var source=WithLights(ShadowFixture(new(.75f,.25f),false),[light]);
        var baseline=session.Capture(source,Width,Height);
        session.Capture(source,Width+2,Height);
        int resources=session.Statistics!.CachedResources;long shaders=session.Statistics.ShaderCompilations,pipelines=session.Statistics.PipelineCreations;
        for(int i=0;i<24;i++)
        {
            var updated=WithLights(ShadowFixture(new(i%2==0?.25f:.75f,.25f),i%2==0),[light]);
            using var output=session.Render(updated,Width+(i%2)*2,Height);
            Assert.Equal(0,output.Statistics.ReadbackCount);
            Assert.Equal(shaders,output.Statistics.ShaderCompilations);
            Assert.Equal(pipelines,output.Statistics.PipelineCreations);
            Assert.InRange(output.Statistics.CachedResources,0,resources+2);
        }
        Assert.Throws<OperationCanceledException>(()=>session.RenderForPresent(source,Width,Height,null,(_,_)=>throw new OperationCanceledException()));
        Assert.Equal(baseline.Rgba,session.Capture(source,Width,Height).Rgba);
        session.WaitForIdle();
        using var final=session.Render(source,Width,Height);
        Assert.Equal(0,final.Statistics.ReadbackCount);
        Assert.InRange(final.Statistics.CachedResources,0,resources+2);
    }

    [Theory,InlineData(LightingMode.SoftwareRayTracing),InlineData(LightingMode.HardwareRayTracing)]
    public void SubTextureTemporalGiSsaaAndRejectedAaDoNotContaminateNativeState(LightingMode mode)
    {
        using var session=new D3D12FrameSession();var frame=WithLights(Fixture(new(.25f,.25f)),[Light(SceneLightKind.Directional)]);
        var baseline=session.Capture(frame,Width,Height);
        var ssaa=Options(frame,frame.Options! with {SpatialSamples=2});
        using(var combined=session.Render(ssaa,Width,Height,rayGiOptions:new(mode,Composition:RayGiComposition.ToonPalette,Denoiser:RayGiDenoiser.TemporalSpatial)))
        {
            Assert.Equal(1,combined.Statistics.SubmissionCount);Assert.Equal(0,combined.Statistics.ReadbackCount);
            Assert.Equal(2,session.RayGiStatistics!.DispatchCount);Assert.Equal(4,session.RayGiStatistics.DenoiseDispatchCount);
            Assert.Equal(1UL,session.RayGiStatistics.TemporalHistoryFrames);
            Assert.True(session.NativeLightStatistics!.NativeLightsContributeToGi);
        }
        Assert.Throws<NotSupportedException>(()=>session.Render(frame,Width,Height,temporalUpscaling:new(){Mode=D3D12TemporalUpscalingMode.Fsr}));
        Assert.Throws<NotSupportedException>(()=>session.Render(frame,Width,Height,temporalUpscaling:new(){Mode=D3D12TemporalUpscalingMode.XeSs}));
        Assert.Throws<NotSupportedException>(()=>session.Render(frame,Width,Height,temporalUpscaling:new(){Mode=D3D12TemporalUpscalingMode.Dlss}));
        Assert.False(RayGiScene.FromOriginal(frame).SubTextureUvs.IsEmpty);
        Assert.Equal(baseline.Rgba,session.Capture(frame,Width,Height).Rgba);
        Assert.False(session.NativeLightStatistics!.NativeLightsContributeToGi);
    }

    private static NativeSceneLight Light(SceneLightKind kind)=>new(Guid.NewGuid(),"subtexture "+kind,kind,new(0,0,-2),
        Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI),new(1,.3f,.1f),kind==SceneLightKind.Directional?1:4,8);

    private static OriginalFrameScene WithLights(OriginalFrameScene frame,IEnumerable<NativeSceneLight> lights)
        =>new(frame.Surface,options:frame.Options,materials:frame.Materials,lighting:new(lights));
    private static OriginalFrameScene Options(OriginalFrameScene frame,OriginalFrameOptions options)
        =>new(frame.Surface,options:options,materials:frame.Materials,lighting:frame.Lighting);

    private static OriginalFrameScene Fixture(Vector2 uv,float c=1,float alpha=1,float toonWeight=1,bool noDiffuse=false,bool transp=false,bool additive=false)
    {
        var mesh=D3D12MeshTests.Quad();var vertices=mesh.Vertices.ToArray();float d=c/3;
        for(int i=0;i<vertices.Length;i++)vertices[i]=vertices[i] with {Normal=new(MathF.Sqrt(1-d*d),0,-d),Uv=new(.9f,.9f)};
        byte a=(byte)MathF.Round(alpha*255);
        SceneTexture[] textures=[new("white diffuse",1,1,[255,255,255,a]),
            new("UV1 quadrants",2,2,[160,32,16,a,16,192,32,a,32,16,224,a,128,160,192,a])];
        var stream=new SceneAdditionalUvs(4,Enumerable.Repeat(new SceneAdditionalUvVertex(new(uv,31,37),new(41),new(43),new(47)),4).ToArray());
        var material=new SceneMaterial("UV1",0,6,Vector4.One,Vector3.Zero,Vector3.Zero,1,noDiffuse?-1:0);
        var scene=new SceneSnapshot(vertices,mesh.Indices,[material],textures,camera:mesh.Camera,lightDirection:-Vector3.UnitZ,additionalUvs:stream);
        var binding=new OriginalStyleMaterialBinding(new(.4f),1,OriginalSphereMode.SubTexture,true){SubTextureAdditive=additive,
            TextureFactors=OriginalMaterialTextureFactors.Identity with {Toon=new(new(1,1,1,toonWeight),Vector4.Zero)}};
        return new(new(scene,[binding],new(new(.5f),Vector3.Zero,Matrix4x4.Identity,OriginalTextureSampling.PointClamp)),
            options:new(OriginalAlphaBlendMode.Normal,true,transp,BackgroundColor:Vector4.Zero));
    }

    private static OriginalFrameScene ShadowFixture(Vector2 casterUv,bool additive)
    {
        SceneVertex[] Quad(float z,float half)=>[new(new(-half,-half,z),-Vector3.UnitZ,new(.9f,.9f)),
            new(new(half,-half,z),-Vector3.UnitZ,new(.9f,.9f)),new(new(half,half,z),-Vector3.UnitZ,new(.9f,.9f)),new(new(-half,half,z),-Vector3.UnitZ,new(.9f,.9f))];
        var uvs=Enumerable.Range(0,8).Select(i=>new SceneAdditionalUvVertex(new(i<4?new Vector2(.75f,.25f):casterUv,17,19),new(23),new(29),new(31))).ToArray();
        var scene=new SceneSnapshot(Quad(0,1.4f).Concat(Quad(-1,.35f)).ToArray(),[0u,2u,1u,0u,3u,2u,4u,6u,5u,4u,7u,6u],
            [new("receiver",0,6,new(.6f,.6f,.6f,1),Vector3.Zero,Vector3.Zero,1),new("caster",6,6,Vector4.One,Vector3.Zero,Vector3.Zero,1,DiffuseTexture:0)],
            [new("opaque diffuse",1,1,[255,255,255,255]),new("UV1 raw alpha",2,2,[255,255,255,0,255,255,255,255,255,255,255,255,255,255,255,255])],
            camera:new(new(0,0,-5),Vector3.Zero,Vector3.UnitY,4,.1f,20),lightDirection:-Vector3.UnitZ,additionalUvs:new(4,uvs));
        // Zero colour-factor alpha and toon W deliberately must not change raw cutout shadow coverage.
        var binding=new OriginalStyleMaterialBinding(new(.2f),1,OriginalSphereMode.SubTexture,true){SubTextureAdditive=additive,
            TextureFactors=OriginalMaterialTextureFactors.Identity with {Sphere=new(new(1,1,1,0),Vector4.Zero),Toon=new(new(1,1,1,0),Vector4.Zero)}};
        return new(new(scene,[new(new(.2f)),binding],new(new(.1f),Vector3.Zero,Matrix4x4.Identity,OriginalTextureSampling.PointClamp)),
            options:new(OriginalAlphaBlendMode.Normal,true,true),materials:[new(false,true,false),new(true,false,false,SurfaceEnabled:false,ShadowDoubleSided:true)]);
    }

    private static void Alpha(RenderedFrame frame,float expected)
    {
        int offset=(Height/2*Width+Width/2)*4+3,value=(int)MathF.Round(expected*255);
        Assert.InRange((int)frame.Rgba[offset],Math.Max(0,value-2),Math.Min(255,value+2));
    }
    private static void Pixel(RenderedFrame frame,Vector4 expected)
    {
        int offset=(Height/2*Width+Width/2)*4;
        for(int i=0;i<4;i++)
        {
            int value=(int)MathF.Round(Math.Clamp(expected[i],0,1)*255);
            Assert.InRange((int)frame.Rgba[offset+i],Math.Max(0,value-2),Math.Min(255,value+2));
        }
    }
}

