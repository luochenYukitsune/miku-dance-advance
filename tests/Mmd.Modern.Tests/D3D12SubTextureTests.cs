using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12SubTextureTests
{
    private const int Width=97,Height=73;

    [Theory,InlineData(.25f,.25f,0),InlineData(.75f,.25f,1),InlineData(.25f,.75f,2),InlineData(.75f,.75f,3),Trait("Category","Gpu")]
    public void Uv1CoordinatesSelectQuadrantsWithoutBaseUvNormalProjectionOrYFlip(float u,float v,int quadrant)
    {
        using var session=new D3D12FrameSession();
        var frame=Fixture(new(u,v),1,1,1);
        byte[][] colors=[[160,32,16,255],[16,192,32,255],[32,16,224,255],[128,160,192,255]];
        var expected=new Vector4(new Vector3(colors[quadrant][0],colors[quadrant][1],colors[quadrant][2])/255*.5f,1);
        Pixel(session.Capture(frame,Width,Height),expected);
        // Null Options selects the distinct unblended original-style slice.
        Pixel(session.Capture(new(frame.Surface),Width,Height),expected);
    }

    [Theory,InlineData(0f,false,false),InlineData(.5f,false,false),InlineData(1f,false,false),
        InlineData(0f,true,false),InlineData(.5f,true,false),InlineData(1f,true,false),
        InlineData(0f,false,true),InlineData(.5f,false,true),Trait("Category","Gpu")]
    public void TwoMapAlphaUsesToonWeightOnlyInsideShadowedBranch(float c,bool noDiffuse,bool transp)
    {
        using var session=new D3D12FrameSession();
        var frame=Fixture(new(.25f,.25f),c,128/255f,.2f,noDiffuse:noDiffuse,transp:transp);
        float raw=(noDiffuse?1:128/255f)*(128/255f);
        float alpha=transp?.5f:raw*(noDiffuse||c>=1?1:c+.2f*(1-c));
        Vector3 map=new(160/255f,32/255f,16/255f);
        Vector3 lit=.5f*map*(c+.4f*(1-c));
        Pixel(session.Capture(frame,Width,Height),new(lit*alpha,alpha));
        var nullFrame=new OriginalFrameScene(frame.Surface);
        float nullAlpha=raw*(noDiffuse||c>=1?1:c+.2f*(1-c));
        Pixel(session.Capture(nullFrame,Width,Height),new(lit,nullAlpha));
        var outside=Matrix4x4.Identity;outside.M41=10;
        var options=frame.Options! with {SelfShadow=new(outside,8,8,false,false,OriginalTextureSampling.PointClamp,OriginalDepthComparison.LessEqual)};
        var outFrame=new OriginalFrameScene(frame.Surface,options:options,materials:frame.Materials);
        Pixel(session.Capture(outFrame,Width,Height),new(.5f*map*raw,raw));
    }

    [Theory,InlineData(false,false),InlineData(true,false),InlineData(false,true),InlineData(true,true),Trait("Category","Gpu")]
    public void ExplicitAdditiveStateAffectsTwoMapsButNoDiffuseAlwaysMultiplies(bool additive,bool noDiffuse)
    {
        using var session=new D3D12FrameSession();
        var frame=Fixture(new(.75f,.25f),1,1,1,noDiffuse:noDiffuse,additive:additive);
        var original=frame.Surface.Materials[0];
        var factors=new OriginalMaterialTextureFactors(new(new(.5f,.25f,.75f,.5f),new(.1f,.2f,.3f,.25f)),
            new(new(.4f,.6f,.8f,.5f),new(.2f,.1f,0,.25f)),OriginalTextureFactor.Identity);
        frame=new(new(frame.Surface.Snapshot,[original with {TextureFactors=factors}],frame.Surface.Profile),options:frame.Options);
        Vector3 diffuse=noDiffuse?Vector3.One:(Vector3.One*new Vector3(.5f,.25f,.75f)+new Vector3(.1f,.2f,.3f))*.75f+new Vector3(.25f);
        var sampled=new Vector3(16/255f,192/255f,32/255f);
        var sphere=(sampled*new Vector3(.4f,.6f,.8f)+new Vector3(.2f,.1f,0))*.75f;
        var expected=additive&&!noDiffuse?.5f*diffuse+sphere:.5f*diffuse*(sphere+new Vector3(.25f));
        Pixel(session.Capture(frame,Width,Height),new(expected,1));
    }

    [Fact,Trait("Category","Gpu")]
    public void AnimatedUvStreamIsBoundedAbortSafeAndOrdinaryFrameRestoresExactly()
    {
        using var session=new D3D12FrameSession();
        var ordinary=new OriginalFrameScene(D3D12OriginalStyleTests.Fixture(OriginalSphereMode.Multiply,1),options:new(OriginalAlphaBlendMode.Normal,true,false));
        var baseline=session.Capture(ordinary,Width,Height);
        var selected=Fixture(new(.25f,.25f),1,1,1);
        var expected=session.Capture(selected,Width,Height);
        int resources=0;
        for(int i=0;i<32;i++)
        {
            var stream=new SceneAdditionalUvs(1,Enumerable.Repeat(new SceneAdditionalUvVertex(new(i/32f,.25f,19,23),default,default,default),4).ToArray());
            var scene=new OriginalFrameScene(new(selected.Surface.Snapshot.WithAdditionalUvs(stream),selected.Surface.Materials,selected.Surface.Profile),options:selected.Options);
            using var image=session.Render(scene,Width+(i%2)*2,Height);
            if(i==0)resources=image.Statistics.CachedResources;
            Assert.Equal(0,image.Statistics.ReadbackCount);
            Assert.InRange(image.Statistics.CachedResources,0,resources+2);
        }
        Assert.Throws<OperationCanceledException>(()=>session.RenderForPresent(selected,Width,Height,null,(_,_)=>throw new OperationCanceledException()));
        Assert.Equal(expected.Rgba,session.Capture(selected,Width,Height).Rgba);
        Assert.Equal(baseline.Rgba,session.Capture(ordinary,Width,Height).Rgba);
        session.WaitForIdle();
    }

    [Fact,Trait("Category","Gpu")]
    public void TemporalGiSsaaRunsWhileUnsupportedTransparentReconstructionStillRejects()
    {
        using var session=new D3D12FrameSession();var frame=Fixture(new(.25f,.25f),1,1,1);
        var ssaa=new OriginalFrameScene(frame.Surface,options:frame.Options! with {SpatialSamples=2},materials:frame.Materials);
        using(var combined=session.Render(ssaa,Width,Height,rayGiOptions:new(LightingMode.SoftwareRayTracing,Composition:RayGiComposition.ToonPalette,Denoiser:RayGiDenoiser.TemporalSpatial)))
        {
            Assert.Equal(1,combined.Statistics.SubmissionCount);Assert.Equal(0,combined.Statistics.ReadbackCount);
            Assert.Equal(2,session.RayGiStatistics!.DispatchCount);Assert.Equal(4,session.RayGiStatistics.DenoiseDispatchCount);
            Assert.Equal(1UL,session.RayGiStatistics.TemporalHistoryFrames);
        }
        // This fixture has transparent output. Opening UV1 coverage must not open transparent reconstruction.
        foreach(var vendor in new[]{D3D12TemporalUpscalingMode.Fsr,D3D12TemporalUpscalingMode.XeSs,D3D12TemporalUpscalingMode.Dlss})
            Assert.Throws<NotSupportedException>(()=>session.Render(frame,Width,Height,temporalUpscaling:new(){Mode=vendor}));
        Assert.False(RayGiScene.FromOriginal(frame).SubTextureUvs.IsEmpty);
        using var image=session.Render(frame,Width,Height);Assert.Equal(1,image.Statistics.SubmissionCount);
    }

    internal static OriginalFrameScene Fixture(Vector2 uv,float c,float textureAlpha,float toonWeight,bool noDiffuse=false,bool transp=false,bool additive=false)
    {
        var mesh=D3D12MeshTests.Quad();
        var vertices=mesh.Vertices.ToArray();
        float d=c/3;
        for(int i=0;i<vertices.Length;i++)vertices[i]=vertices[i] with {Normal=new(MathF.Sqrt(1-d*d),0,-d),Uv=new(.9f,.9f)};
        byte a=(byte)MathF.Round(textureAlpha*255);
        var textures=new SceneTexture[]{new("white diffuse",1,1,[255,255,255,a]),
            new("UV quadrants",2,2,[160,32,16,a,16,192,32,a,32,16,224,a,128,160,192,a])};
        var stream=new SceneAdditionalUvs(4,Enumerable.Repeat(new SceneAdditionalUvVertex(new(uv,31,37),new(41),new(43),new(47)),4).ToArray());
        var material=new SceneMaterial("uv1",0,6,Vector4.One,Vector3.Zero,Vector3.Zero,1,noDiffuse?-1:0);
        var snapshot=new SceneSnapshot(vertices,mesh.Indices,[material],textures,camera:mesh.Camera,lightDirection:-Vector3.UnitZ,additionalUvs:stream);
        var binding=new OriginalStyleMaterialBinding(new(.4f),1,OriginalSphereMode.SubTexture,true) {SubTextureAdditive=additive,
            TextureFactors=OriginalMaterialTextureFactors.Identity with {Toon=new(new(1,1,1,toonWeight),Vector4.Zero)}};
        var surface=new OriginalStyleScene(snapshot,[binding],new(new(.5f),Vector3.Zero,Matrix4x4.Identity,OriginalTextureSampling.PointClamp));
        return new(surface,options:new(OriginalAlphaBlendMode.Normal,true,transp,BackgroundColor:Vector4.Zero));
    }

    private static void Pixel(RenderedFrame frame,Vector4 expected)
    {
        float[] channels=[expected.X,expected.Y,expected.Z,expected.W];int offset=((Height/2)*Width+Width/2)*4;
        for(int i=0;i<4;i++) {
            int value=(int)MathF.Round(Math.Clamp(channels[i],0,1)*255);
            Assert.InRange((int)frame.Rgba[offset+i],Math.Max(0,value-2),Math.Min(255,value+2));
        }
    }
}
