using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class GenshinHairAtlasSurfaceTests
{
    private const int Width=97,Height=73;
    internal static readonly Vector3 Dark=new(.2f,.25f,.3f);
    internal static D3D12GenshinHairShadowAtlasBinding Binding => new(0,1,
        D3D12GenshinAtlasColorEncoding.LinearRgb,1,.25f,.8f,.5f,1,1,0,0);

    public static IEnumerable<object[]> Variants()
    {
        foreach(var values in D3D12GenshinSurfaceTests.SurfaceVariants())
        foreach(bool rb in new[]{false,true}) yield return [..values,rb];
    }

    [Theory,MemberData(nameof(Variants))]
    public void ActualShaderVariantsCompileWithIndependentAtlasAndOptionalSingleRbLobe(string entry,bool native,bool gi,bool morph,bool sub,bool rb)
    {
        var bytes=OriginalFrameShaders.CompileGenshinHairAtlasSurface(entry,native,gi,morph,sub,rb);
        Assert.Equal(new byte[]{68,88,66,67},bytes[..4]);
        var source=OriginalFrameShaders.GenshinHairAtlasSurfaceSource(native,gi,morph,sub,rb);
        Assert.Contains("register(b9)",source); Assert.Contains("register(t13)",source);
        Assert.Contains("register(t14)",source); Assert.Contains("register(s6)",source);
        Assert.Contains("GenshinAtlasColor.Load(int3(pixel,0)).rgb",source);
        Assert.Contains("+highlight*originalC;",source);
        if(native) Assert.DoesNotContain("GenshinPaletteFactor(GenshinPrimaryBlend(v))",source);
        if(rb) Assert.Contains("float3 highlight=GenshinHairHighlight(v,n,h);",source);
        else Assert.Contains("float3 highlight=saturate(pow",source);
    }

    [Theory,InlineData(false),InlineData(true)]
    [Trait("Category","Gpu")]
    public void ExplicitRowsDayNightEncodingAndPointExtendMatchIndependentDiffuseOracle(bool srgb)
    {
        using var session=new D3D12FrameSession();
        byte[] atlas=Atlas();
        var frame=Frame(atlas);
        foreach(float row in new[]{1f,2f,3f,4f,5f,2.25f})
        foreach(float dayNight in new[]{0f,.25f,1f})
        {
            var binding=Binding with {ExplicitRowId=row,DayNight=dayNight,
                AtlasEncoding=srgb?D3D12GenshinAtlasColorEncoding.SrgbRgb:D3D12GenshinAtlasColorEncoding.LinearRgb};
            var coordinate=GenshinHairAtlasOracle.Coordinates(0,128/255f,row,binding.ShadowPosition,
                binding.ShadowRampWidth,binding.VertexWidth,binding.VertexAo,binding.UseLightmapAo,binding.UseVertexColorAo);
            var day=GenshinHairAtlasOracle.ClosestExtend(atlas,4,10,coordinate.Q,coordinate.DayY,srgb);
            var night=GenshinHairAtlasOracle.ClosestExtend(atlas,4,10,coordinate.Q,coordinate.NightY,srgb);
            var expected=GenshinHairAtlasOracle.Palette(day,night,dayNight,coordinate.Gate)*new Vector3(.6f,.5f,.4f);
            AssertPixel(session.Capture(frame,Width,Height,surfaceStyles:Map(binding)),expected);
        }
        var zero=Binding with {ShadowRampWidth=0};
        var edge=GenshinHairAtlasOracle.Coordinates(0,128/255f,1,.8f,0,1,1,0,0);
        Assert.Equal(1,edge.Q);
        AssertPixel(session.Capture(frame,Width,Height,surfaceStyles:Map(zero)),
            GenshinHairAtlasOracle.Palette(GenshinHairAtlasOracle.ClosestExtend(atlas,4,10,1,edge.DayY,false),
                GenshinHairAtlasOracle.ClosestExtend(atlas,4,10,1,edge.NightY,false),.25f,1)*new Vector3(.6f,.5f,.4f));
    }

    [Theory,InlineData(false,false),InlineData(true,false),InlineData(false,true),InlineData(true,true)]
    [Trait("Category","Gpu")]
    public void IndependentRbLobeAndCoverageSurviveNativeGiAndNullRestoration(bool native,bool gi)
    {
        using var session=new D3D12FrameSession();
        var frame=Frame(Atlas(),native:native,specular:new(.4f,.3f,.2f),light:-Vector3.UnitZ);
        var options=gi?new RayGiOptions(LightingMode.SoftwareRayTracing,SamplesPerPixel:2,Seed:43,Composition:RayGiComposition.ToonPalette):null;
        var atlas=Binding with {ShadowPosition=1.1f};
        var rb=new D3D12GenshinNonMetallicSpecularBinding(0,1,0,.1f);
        var withoutSpecular=session.Capture(Frame(Atlas(),native:native,light:-Vector3.UnitZ),Width,Height,options,surfaceStyles:Map(atlas,rb));
        var actual=session.Capture(frame,Width,Height,options,surfaceStyles:Map(atlas,rb));
        var a=Pixel(actual);var b=Pixel(withoutSpecular);
        var expected=new Vector3(.4f,.3f,.2f)*(128/255f*.1f*255);
        for(int c=0;c<3;c++) Assert.InRange(Math.Abs(a[c]-b[c]-expected[c]),0,1.1f);
        for(int i=3;i<actual.Rgba.Length;i+=4) Assert.Equal(withoutSpecular.Rgba[i],actual.Rgba[i]);
        var ordinary=session.Capture(frame,Width,Height,options,surfaceStyles:Map(null));
        session.Capture(frame,Width,Height,options,surfaceStyles:Map(atlas));
        Assert.Equal(ordinary.Rgba,session.Capture(frame,Width,Height,options,surfaceStyles:Map(null)).Rgba);
        var warm=session.Statistics!;
        long reads=session.Runtime.Readbacks;
        for(int i=0;i<3;i++)
        {
            using var output=session.Render(frame,Width,Height,options,surfaceStyles:Map(atlas with {DayNight=i*.25f}));
            Assert.Equal(0,output.Statistics.ReadbackCount);
            Assert.Equal(warm.ShaderCompilations,output.Statistics.ShaderCompilations);
            Assert.Equal(warm.PipelineCreations,output.Statistics.PipelineCreations);
        }
        Assert.Equal(reads,session.Runtime.Readbacks);
    }

    [Theory,InlineData(0f,false),InlineData(1.0004f,false),InlineData(1.001f,false),InlineData(0f,true)]
    [Trait("Category","Gpu")]
    public void AuthoredDarkEndpointRetainsRealShadowVisibilityAndOutsideMapUsesAtlas(float projectedDepth,bool outside)
    {
        using var session=new D3D12FrameSession();
        var atlas=Atlas();var frame=Frame(atlas,projectedDepth:projectedDepth,outside:outside);
        var coordinate=GenshinHairAtlasOracle.Coordinates(0,128/255f,1,.8f,.5f,1,1,0,0);
        var palette=GenshinHairAtlasOracle.Palette(
            GenshinHairAtlasOracle.ClosestExtend(atlas,4,10,coordinate.Q,coordinate.DayY,false),
            GenshinHairAtlasOracle.ClosestExtend(atlas,4,10,coordinate.Q,coordinate.NightY,false),.25f,1);
        float visibility=outside?1:OriginalFrameMath.Visibility(projectedDepth,1,.5f,false);
        AssertPixel(session.Capture(frame,Width,Height,surfaceStyles:Map(Binding)),
            GenshinHairAtlasOracle.MdaVisibility(palette,Dark,visibility)*new Vector3(.6f,.5f,.4f));
    }

    [Fact,Trait("Category","Gpu")]
    public void StraightAlphaAndSubTextureMorphKeepCoverageIndependentFromBothAtlasAndLightmapAlpha()
    {
        using var session=new D3D12FrameSession();
        var frame=Frame(Atlas(),sub:true,straight:true);
        var ordinary=session.Capture(frame,Width,Height,surfaceStyles:Map(null));
        var selected=session.Capture(frame,Width,Height,surfaceStyles:Map(Binding));
        for(int i=3;i<selected.Rgba.Length;i+=4) Assert.Equal(ordinary.Rgba[i],selected.Rgba[i]);
        var pixels=Atlas();for(int i=3;i<pixels.Length;i+=4)pixels[i]=255;
        Assert.Equal(selected.Rgba,session.Capture(Frame(pixels,sub:true,straight:true,unusedAlpha:255),Width,Height,surfaceStyles:Map(Binding)).Rgba);
        Assert.Equal(ordinary.Rgba,session.Capture(frame,Width,Height,surfaceStyles:Map(null)).Rgba);
    }

    [Fact,Trait("Category","Gpu")]
    public void StrongNativeLightsSpendAtlasRemainingBudgetWithoutClippingAuthoredSpecularOrMakingBlackAMissingSentinel()
    {
        using var session=new D3D12FrameSession();
        foreach(bool gi in new[]{false,true})
        {
            var options=gi?new RayGiOptions(LightingMode.SoftwareRayTracing,SamplesPerPixel:2,Seed:43,Composition:RayGiComposition.ToonPalette):null;
            var source=Frame(new byte[4*10*4],native:true,light:-Vector3.UnitZ);
            var light=source.Lighting!.Lights[0] with {Color=Vector3.One,Intensity=20};
            var frame=new OriginalFrameScene(source.Surface,options:source.Options,lighting:new([light]));
            var binding=Binding with {ShadowPosition=1.1f};
            // An authored black atlas under a lit normal is genuinely black before
            // added light. It is not a missing-texture fallback to the old palette.
            AssertPixel(session.Capture(Frame(new byte[4*10*4],light:-Vector3.UnitZ),Width,Height,
                options,surfaceStyles:Map(binding)),Vector3.Zero);
            AssertPixel(session.Capture(frame,Width,Height,options,surfaceStyles:Map(binding)),new(.6f,.5f,.4f));
        }
    }

    [Fact,Trait("Category","Gpu")]
    public void MixedAtlasAndOrdinaryMaterialsDoNotLeakDescriptorsOrAlterUnselectedPixels()
    {
        using var session=new D3D12FrameSession();
        var source=Frame(Atlas());var old=source.Surface.Snapshot;
        var vertices=new List<SceneVertex>();
        foreach(float y in new[]{.65f,0f,-.65f})
            vertices.AddRange(old.Vertices.ToArray().Select(v=>v with {Position=new(v.Position.X,v.Position.Y*.4f+y,0)}));
        var scene=new SceneSnapshot(vertices.ToArray(),[0,2,1,0,3,2,4,6,5,4,7,6,8,10,9,8,11,10],
            Enumerable.Range(0,3).Select(i=>old.Materials[0] with {StartIndex=i*6}).ToArray(),old.Textures,
            camera:old.Camera,lightDirection:old.LightDirection);
        var surface=new OriginalStyleScene(scene,Enumerable.Repeat(source.Surface.Materials[0],3).ToArray(),source.Surface.Profile);
        var frame=new OriginalFrameScene(surface,options:source.Options);
        var families=new[]{D3D12SurfaceStyle.GenshinPmx,D3D12SurfaceStyle.Mmd,D3D12SurfaceStyle.GenshinPmx};
        var off=new D3D12SurfaceStyleMap(families);
        var on=new D3D12SurfaceStyleMap(families,[new(D3D12CharacterMaterialRole.Hair,GenshinHairShadowAtlas:Binding),new(),new()]);
        var before=session.Capture(frame,Width,Height,surfaceStyles:off);
        var after=session.Capture(frame,Width,Height,surfaceStyles:on);
        int changed=0;
        for(int y=0;y<Height;y++)for(int x=0;x<Width;x++)
        {
            int at=(y*Width+x)*4;Assert.Equal(before.Rgba[at+3],after.Rgba[at+3]);
            if(y>=Height/2)Assert.Equal(before.Rgba.AsSpan(at,4).ToArray(),after.Rgba.AsSpan(at,4).ToArray());
            else if(!before.Rgba.AsSpan(at,3).SequenceEqual(after.Rgba.AsSpan(at,3)))changed++;
        }
        Assert.True(changed>10);
        Assert.Equal(before.Rgba,session.Capture(frame,Width,Height,surfaceStyles:off).Rgba);
    }

    internal static D3D12SurfaceStyleMap Map(D3D12GenshinHairShadowAtlasBinding? binding,D3D12GenshinNonMetallicSpecularBinding? rb=null)
        => new([D3D12SurfaceStyle.GenshinPmx],[new(D3D12CharacterMaterialRole.Hair,
            GenshinNonMetallicSpecular:rb,GenshinHairShadowAtlas:binding)]);

    internal static byte[] Atlas()
    {
        var pixels=new byte[4*10*4];
        for(int y=0;y<10;y++)for(int x=0;x<4;x++)
        {
            int at=(y*4+x)*4;pixels[at]=(byte)(20+x*40+y*3);
            pixels[at+1]=(byte)(30+y*17);pixels[at+2]=(byte)(180-x*20-y*4);
        }
        return pixels;
    }

    internal static OriginalFrameScene Frame(byte[] atlas,bool native=false,Vector3? specular=null,Vector3? light=null,
        float projectedDepth=0,bool outside=false,bool sub=false,bool straight=false,byte unusedAlpha=0,byte green=128)
    {
        Vector3 n=-Vector3.UnitZ;Vector2 uv=new(.25f,.25f);
        SceneVertex[] vertices=[new(new(-.8f,-.8f,0),n,uv),new(new(.8f,-.8f,0),n,uv),
            new(new(.8f,.8f,0),n,uv),new(new(-.8f,.8f,0),n,uv)];
        SceneTexture[] textures=[new("numerical G, independent R/B lobe",1,1,[128,green,255,unusedAlpha]),
            new("original synthetic atlas",4,10,atlas),new("coverage",1,1,[200,180,160,128])];
        var binding=new OriginalStyleMaterialBinding(Dark,sub?2:-1,sub?OriginalSphereMode.SubTexture:OriginalSphereMode.None,true);
        if(sub)binding=binding with {TextureFactors=OriginalMaterialTextureFactors.Identity with
            {Diffuse=new(new(.8f,.7f,.6f,1),Vector4.Zero),Toon=new(new(1,1,1,.2f),Vector4.Zero)}};
        var additional=sub?new SceneAdditionalUvs(1,Enumerable.Repeat(new SceneAdditionalUvVertex(new(.25f,.25f,0,0),default,default,default),4).ToArray()):null;
        var snapshot=new SceneSnapshot(vertices,[0,2,1,0,3,2],
            [new("explicit hair",0,6,new(.6f,.5f,.4f,straight?.5f:1),specular??Vector3.Zero,Vector3.Zero,4,sub?2:-1)],textures,
            camera:new(new(0,0,-5),Vector3.Zero,Vector3.UnitY,3,.1f,20),lightDirection:light??Vector3.UnitX,additionalUvs:additional);
        return new(new(snapshot,[binding],new(Vector3.One,Vector3.One,Matrix4x4.Identity,OriginalTextureSampling.PointClamp)),
            options:new(OriginalAlphaBlendMode.Normal,true,false,BackgroundColor:Vector4.Zero,
                SelfShadow:new(Matrix4x4.CreateScale(.2f)*Matrix4x4.CreateTranslation(outside?3:0,0,projectedDepth),32,32,false,false,
                    OriginalTextureSampling.PointClamp,OriginalDepthComparison.LessEqual),
                AlphaOutput:straight?FrameAlphaOutput.Straight:FrameAlphaOutput.LegacyMax),
            lighting:native?new([new NativeSceneLight(Guid.Parse("31bb2344-029e-418d-8ec3-e00000000001"),"controlled fill",SceneLightKind.Directional,
                Vector3.Zero,Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI),new(.2f),1,30,CastShadows:false)]):null);
    }

    private static byte[] Pixel(RenderedFrame frame)=>frame.Rgba.AsSpan(((Height/2)*Width+Width/2)*4,4).ToArray();
    private static void AssertPixel(RenderedFrame frame,Vector3 expected)
    {
        var p=Pixel(frame);
        for(int c=0;c<3;c++)Assert.InRange(Math.Abs(p[c]-expected[c]*255),0,1.25f);
        Assert.Equal(255,p[3]);
    }
}
