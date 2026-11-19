using System.Numerics;
using System.Security.Cryptography;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>Declared three-knot ordinary-PMX adaptation, not a numerical recreation of Endfield assets.</summary>
[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12EndfieldSurfaceTests
{
    private const int Width=129,Height=97;

    public static IEnumerable<object[]> SurfaceVariants()
    {
        foreach(bool native in new[]{false,true})
        foreach(bool gi in new[]{false,true})
        foreach(var variant in new[]{(Morph:false,Sub:false),(Morph:true,Sub:false),(Morph:true,Sub:true)})
            yield return [native?(gi?"PSSurfaceGiNative":"PSSurfaceNative"):(gi?"PSSurfaceGi":"PSSurface"),native,gi,variant.Morph,variant.Sub];
    }

    [Theory,MemberData(nameof(SurfaceVariants))]
    public void EveryActualSurfaceVariantCompilesWithOneCanonicalThreeKnotResponse(string entry,bool native,bool gi,bool morph,bool sub)
    {
        var shader=T8dTestHelpers.CompileEndfieldPixel("default",entry,native,gi,morph,sub);
        var mmd=OriginalFrameShaders.CompileCharacterToon(entry,native,gi,morph,sub);
        var genshinModule=TestStylePlugins.AllViewCatalog.FindRegistration("genshin")!.ShaderModules!["default"];
        var genshin=D3D12StyleShaderAssembler.CompilePixel("genshin",entry,genshinModule,
            D3D12StyleShaderAssembler.ResolveEffectiveHooks(genshinModule,null),
            D3D12StyleShaderAssembler.ResolveEffectiveGeometry(genshinModule,sub),native,gi,morph,sub,false,false);
        Assert.Equal(new byte[]{(byte)'D',(byte)'X',(byte)'B',(byte)'C'},shader[..4]);
        Assert.NotEqual(Convert.ToHexString(SHA256.HashData(mmd)),Convert.ToHexString(SHA256.HashData(shader)));
        Assert.NotEqual(Convert.ToHexString(SHA256.HashData(genshin)),Convert.ToHexString(SHA256.HashData(shader)));
        var source=T8dTestHelpers.AssembleEndfieldPixelSource("default",entry,native,gi,morph,sub);
        Assert.Contains("EndfieldLightResponseCore(dot(n, l), visibility)",source);
        Assert.Contains("float halfLambert = saturate(nDotL * .5 + .5);",source);
        Assert.Contains("if (IncomingLight.w <= 0) band = halfLambert >= ShadowOptions.w ? 1 : 0;",source);
        Assert.Contains("float lowerThreshold = ShadowOptions.w - IncomingLight.w;",source);
        Assert.Contains("float upperThreshold = ShadowOptions.w + IncomingLight.w;",source);
        Assert.Contains("float halfTransition = IncomingLight.w * .25;",source);
        Assert.Contains("band = .5 * lowerBand + .5 * upperBand;",source);
        Assert.Contains("+ highlight * originalC;",source);
        Assert.Contains("float3 highlight = saturate(pow(max(0, dot(n, h)), SpecularPower.w) * SpecularPower.rgb);",source);
        Assert.DoesNotContain("CharacterMainToon(",source);
        Assert.DoesNotContain("GenshinLightResponse",source);
        Assert.DoesNotContain("highlightBand",source);
        if(gi||native)Assert.Contains("MdaStyleLightResponse(v, n, l, visibility)",source);
        if(native)
        {
            // Additional lights take the family band and the shared highlight compression, not a palette budget.
            Assert.Contains("NATIVE_LIGHT_RESPONSE(v, n, l)",source);
            Assert.Contains("MdaStyleLightResponse(v, n, l, 1)",source);
            Assert.DoesNotContain("remainingPalette",source);
        }
        if(sub)Assert.Contains("originalC < 1) alpha *= originalC + ToonShadowColor.w * (1 - originalC)",source);
    }

    [Fact,Trait("Category","Gpu")]
    public void FourFamiliesShareOneFrameAndKeepUnselectedPixelsAndCoverageExactWithBoundedCaches()
    {
        using var session=new D3D12FrameSession();
        var frame=Rows();
        var previous=new D3D12SurfaceStyleMap(["genshin","genshin","star-rail","mmd"],registeredStyles:TestStylePlugins.AllPresetCatalog.Plugins.Values);
        var map=new D3D12SurfaceStyleMap(["endfield","genshin","star-rail","mmd"],registeredStyles:TestStylePlugins.AllPresetCatalog.Plugins.Values);
        var baseline=session.Capture(frame,Width,Height);
        var existing=session.Capture(frame,Width,Height,surfaceStyles:previous);
        var mixed=session.Capture(frame,Width,Height,surfaceStyles:map);
        var centers=new[]{1.2f,.4f,-.4f,-1.2f}.Select(y=>Project(frame,new(0,y,0))).ToArray();
        var selected=centers[0];
        Assert.NotEqual(Pixel(existing,selected.X,selected.Y),Pixel(mixed,selected.X,selected.Y));
        for(int i=1;i<centers.Length;i++)
            Assert.Equal(Pixel(existing,centers[i].X,centers[i].Y),Pixel(mixed,centers[i].X,centers[i].Y));
        Assert.Equal(Pixel(baseline,centers[3].X,centers[3].Y),Pixel(mixed,centers[3].X,centers[3].Y));
        Assert.Equal(4,centers.Select(p=>Convert.ToHexString(Pixel(mixed,p.X,p.Y))).Distinct().Count());
        int edge1=Project(frame,new(0,.95f,0)).Y,edge2=Project(frame,new(0,1.45f,0)).Y;
        int low=Math.Min(edge1,edge2)-1,high=Math.Max(edge1,edge2)+1,changed=0,untouched=0;
        for(int y=0;y<Height;y++)for(int x=0;x<Width;x++)
        {
            int offset=(y*Width+x)*4;
            Assert.Equal(existing.Rgba[offset+3],mixed.Rgba[offset+3]);
            if(y<low||y>high)
            {
                Assert.Equal(Pixel(existing,x,y),Pixel(mixed,x,y));
                if(mixed.Rgba[offset+3]!=0)untouched++;
            }
            else if(!Pixel(existing,x,y).SequenceEqual(Pixel(mixed,x,y)))changed++;
        }
        Assert.True(changed>100);Assert.True(untouched>300);
        Assert.Single(session.LastRecordings.Select(record=>record.DeviceIdentity).Distinct());
        Assert.Single(session.LastRecordings.Select(record=>record.CommandListIdentity).Distinct());
        Assert.Equal(existing.Rgba,session.Capture(frame,Width,Height,surfaceStyles:previous).Rgba);
        Assert.Equal(baseline.Rgba,session.Capture(frame,Width,Height).Rgba);
        var warm=session.Statistics!;long readbacks=session.Runtime.Readbacks;
        for(int i=0;i<12;i++)
        {
            using var output=session.Render(frame,Width,Height,surfaceStyles:i%3==0?map:i%3==1?previous:null);
            Assert.Equal(0,output.Statistics.ReadbackCount);
            Assert.Equal(warm.ShaderCompilations,output.Statistics.ShaderCompilations);
            Assert.Equal(warm.PipelineCreations,output.Statistics.PipelineCreations);
            Assert.InRange(output.Statistics.CachedResources,0,warm.CachedResources+2);
        }
        Assert.Equal(readbacks,session.Runtime.Readbacks);
    }

    [Theory,InlineData(false),InlineData(true)]
    [Trait("Category","Gpu")]
    public void NumericGpuSamplesKeepDarkMidLitPlateausAndExplicitFaceLiftInsideAndOutsideProjection(bool outside)
    {
        using var session=new D3D12FrameSession();
        var body=new D3D12SurfaceStyleMap(["endfield"],registeredStyles:TestStylePlugins.AllPresetCatalog.Plugins.Values);
        var face=new D3D12SurfaceStyleMap(["endfield"],
            [new(D3D12CharacterMaterialRole.Face,.35f,.35f)],TestStylePlugins.AllPresetCatalog);
        // Default threshold .5, width .1: transitions cover half-Lambert
        // [.375,.425] and [.575,.625]. Several interior samples share exactly
        // the same .5 plateau; the two separate shoulders reach .25 and .75.
        foreach(var sample in new[]{(Dot:-1f,Blend:0f),(Dot:-.4f,Blend:0f),(Dot:-.25f,Blend:0f),
            (Dot:-.2f,Blend:.25f),(Dot:-.15f,Blend:.5f),(Dot:-.1f,Blend:.5f),(Dot:0f,Blend:.5f),
            (Dot:.1f,Blend:.5f),(Dot:.15f,Blend:.5f),(Dot:.2f,Blend:.75f),(Dot:.25f,Blend:1f),
            (Dot:.4f,Blend:1f),(Dot:1f,Blend:1f)})
        {
            var normal=new Vector3(sample.Dot,0,-MathF.Sqrt(MathF.Max(0,1-sample.Dot*sample.Dot)));
            var material=new SceneMaterial("explicit three-knot signal",0,6,new(.6f,.4f,.2f,1),Vector3.Zero,Vector3.Zero,1);
            var scene=new SceneSnapshot(Quad(0,normal),[0,2,1,0,3,2],[material],
                camera:new(new(0,0,-5),Vector3.Zero,Vector3.UnitY,3,.1f,20),lightDirection:Vector3.UnitX);
            var shadow=new OriginalSelfShadowOptions(outside?Matrix4x4.CreateTranslation(3,0,0):Matrix4x4.CreateScale(.2f),
                32,32,false,false,OriginalTextureSampling.PointClamp,OriginalDepthComparison.LessEqual);
            var frame=new OriginalFrameScene(new(scene,[new(new(.2f))],
                new(Vector3.One,Vector3.Zero,Matrix4x4.Identity,OriginalTextureSampling.PointClamp)),
                options:Options() with {SelfShadow=shadow},materials:[new(false,true,false)]);
            var rendered=session.Capture(frame,Width,Height,surfaceStyles:body);
            var selected=session.Capture(frame,Width,Height,surfaceStyles:face);
            var color=Pixel(rendered,Width/2,Height/2);var lifted=Pixel(selected,Width/2,Height/2);
            var diffuse=new Vector3(.6f,.4f,.2f);
            float factor=.2f+.8f*sample.Blend,faceFactor=.2f+.8f*(.65f+.35f*sample.Blend);
            for(int channel=0;channel<3;channel++)
            {
                Assert.InRange(Math.Abs(color[channel]-diffuse[channel]*factor*255),0,1.1f);
                Assert.InRange(Math.Abs(lifted[channel]-diffuse[channel]*faceFactor*255),0,1.1f);
            }
            Assert.Equal((byte)255,color[3]);Assert.Equal(color[3],lifted[3]);
        }
    }

    [Fact,Trait("Category","Gpu")]
    public void ExplicitFaceKeepsTheRealCastShadowShapeAndReducesItsContrastWithoutChangingTheBody()
    {
        using var session=new D3D12FrameSession();
        SceneVertex[] Box(float left,float right,float z)=>
            [new(new(left,-.8f,z),-Vector3.UnitZ,Vector2.Zero),new(new(right,-.8f,z),-Vector3.UnitZ,Vector2.Zero),
                new(new(right,.8f,z),-Vector3.UnitZ,Vector2.Zero),new(new(left,.8f,z),-Vector3.UnitZ,Vector2.Zero)];
        // A narrower real occluder leaves both shadowed and sunlit face regions,
        // so lifting a flat whole material cannot satisfy the footprint checks.
        var vertices=Box(-1.8f,-.2f,0).Concat(Box(.2f,1.8f,0)).Concat(Box(-1.8f,-.6f,1)).ToArray();
        var scene=new SceneSnapshot(vertices,[0,2,1,0,3,2,4,6,5,4,7,6,8,10,9,8,11,10],
            Enumerable.Range(0,3).Select(i=>new SceneMaterial("explicit receiver "+i,i*6,6,Vector4.One,Vector3.Zero,Vector3.Zero,1)).ToArray(),
            camera:new(new(0,0,-5),Vector3.Zero,Vector3.UnitY,3.6f,.1f,20),lightDirection:-Vector3.UnitZ);
        var projection=Matrix4x4.CreateLookAt(new(0,0,5),Vector3.Zero,Vector3.UnitY)*Matrix4x4.CreateOrthographic(4,4,.1f,20);
        var frame=new OriginalFrameScene(new(scene,[new(new(.2f)),new(new(.2f)),new(new(.2f))],
                new(Vector3.One,Vector3.Zero,Matrix4x4.Identity,OriginalTextureSampling.PointClamp)),
            options:Options() with {SelfShadow=new(projection,64,64,true,false,OriginalTextureSampling.PointClamp,OriginalDepthComparison.LessEqual)},
            materials:[new(false,true,false),new(false,true,false),new(true,false,false,SurfaceEnabled:false,ShadowDoubleSided:true)]);
        string[] families=["endfield","endfield","mmd"];
        var plain=new D3D12SurfaceStyleMap(families,registeredStyles:TestStylePlugins.AllPresetCatalog.Plugins.Values);
        var selected=new D3D12SurfaceStyleMap(families,[new(D3D12CharacterMaterialRole.Face,.35f,.35f),new(),new()],TestStylePlugins.AllPresetCatalog);
        var before=session.Capture(frame,Width,Height,surfaceStyles:plain);
        var after=session.Capture(frame,Width,Height,surfaceStyles:selected);
        var face=Project(frame,new(-.9f,0,0));var sunlitFace=Project(frame,new(-.35f,0,0));
        var body=Project(frame,new(.9f,0,0));
        D3D12OriginalFrameTests.AssertPixel(before,face.X,face.Y,new(.2f,.2f,.2f,1));
        D3D12OriginalFrameTests.AssertPixel(after,face.X,face.Y,new(.72f,.72f,.72f,1));
        D3D12OriginalFrameTests.AssertPixel(before,sunlitFace.X,sunlitFace.Y,Vector4.One);
        Assert.Equal(Pixel(before,sunlitFace.X,sunlitFace.Y),Pixel(after,sunlitFace.X,sunlitFace.Y));
        Assert.Equal(Pixel(before,body.X,body.Y),Pixel(after,body.X,body.Y));
        int changedShadow=0;
        for(int offset=0;offset<before.Rgba.Length;offset+=4)
        {
            if(before.Rgba[offset]==after.Rgba[offset])continue;
            Assert.True(before.Rgba[offset]<254,"Originally fully sunlit fragments must stay outside the changed shadow footprint.");
            Assert.True(after.Rgba[offset]>before.Rgba[offset]);
            changedShadow++;
        }
        Assert.True(changedShadow>40);
        for(int offset=3;offset<before.Rgba.Length;offset+=4)Assert.Equal(before.Rgba[offset],after.Rgba[offset]);
        Assert.Equal(before.Rgba,session.Capture(frame,Width,Height,surfaceStyles:plain).Rgba);
    }

    public static IEnumerable<object[]> CoverageVariants()
    {
        foreach(bool straight in new[]{false,true})
        foreach(bool both in new[]{false,true})
        foreach(var variant in new[]{(Sphere:false,Sub:false),(Sphere:true,Sub:false),(Sphere:true,Sub:true)})
            yield return [straight,both,variant.Sphere,variant.Sub];
    }

    [Theory,MemberData(nameof(CoverageVariants)),Trait("Category","Gpu")]
    public void BothCullVariantsKeepRawAlphaTransparentSourceOrderMorphSphereAndSubTexture(bool straight,bool both,bool sphere,bool sub)
    {
        using var session=new D3D12FrameSession();
        var frame=Layers(straight,both,sphere,sub);
        var baseline=session.Capture(frame,Width,Height);
        var map=new D3D12SurfaceStyleMap(["endfield","mmd"],registeredStyles:TestStylePlugins.AllPresetCatalog.Plugins.Values);
        var mixed=session.Capture(frame,Width,Height,surfaceStyles:map);
        for(int offset=3;offset<baseline.Rgba.Length;offset+=4)Assert.Equal(baseline.Rgba[offset],mixed.Rgba[offset]);
        // Original raw c=.15 continues to control SubTexture coverage. It must not
        // be replaced by Endfield's independently derived midpoint blend=.5.
        const float c=.15f,blend=.5f;
        float rawAlpha=.8f*(192/255f)*(sphere?192/255f:1);
        if(sub)rawAlpha*=c+.2f*(1-c);
        var first=new Vector3(.6f,0,0)*new Vector3(.8f,.7f,.6f)*(.2f+.8f*blend);
        var second=new Vector3(0,.6f,0)*new Vector3(.8f,.7f,.6f)*(.2f+.8f*c);
        if(sphere){first*=192/255f;second*=192/255f;}
        var expected=first*(rawAlpha*(1-rawAlpha))+second*rawAlpha;
        float composedAlpha=straight?rawAlpha*(2-rawAlpha):rawAlpha;
        if(straight)expected/=composedAlpha;
        // Prove before comparing readback that the selected red layer's change
        // survives both source-over layers and final 8-bit quantization. In the
        // weakest legacy SubTexture case it is about 3.2 byte values, not the
        // sub-byte difference of the former low-alpha, nearly equal-band fixture.
        float redResponseDelta=.6f*.8f*(sphere?192/255f:1)*.8f*(blend-c);
        float expectedByteDifference=redResponseDelta*rawAlpha*(1-rawAlpha)*255/(straight?composedAlpha:1);
        Assert.True(expectedByteDifference>1.5f,"The CPU reference must establish an observable selected-style difference.");
        var pixel=Pixel(mixed,Width/2,Height/2);
        for(int channel=0;channel<3;channel++)Assert.InRange(Math.Abs(pixel[channel]-expected[channel]*255),0,2f);
        Assert.InRange(Math.Abs(pixel[3]-composedAlpha*255),0,2f);
        Assert.NotEqual(Pixel(baseline,Width/2,Height/2),pixel);
        Assert.Equal(baseline.Rgba,session.Capture(frame,Width,Height).Rgba);
    }

    [Theory,InlineData(false),InlineData(true),Trait("Category","Gpu")]
    public void GiSpendsItsThreeKnotPaletteBudgetAndNativeDirectAddsItsFullBrightness(bool native)
    {
        using var session=new D3D12FrameSession();
        var frame=GiReceiver(native);
        var map=new D3D12SurfaceStyleMap(["endfield","mmd"],registeredStyles:TestStylePlugins.AllPresetCatalog.Plugins.Values);
        var options=new RayGiOptions(LightingMode.SoftwareRayTracing,Strength:20,SamplesPerPixel:32,Seed:17,
            MaxRayDistance:50,RayEpsilon:.001f,DirectionalLightDirection:Vector3.UnitZ,DirectionalIrradiance:new(3),
            Composition:RayGiComposition.ToonPalette);
        var off=session.Capture(frame,Width,Height,surfaceStyles:map);
        var on=session.Capture(frame,Width,Height,options,surfaceStyles:map);
        var before=Pixel(off,Width/2,Height/2);var after=Pixel(on,Width/2,Height/2);
        // NdotL=-.05 lies on the neutral midpoint. Traced neutral illumination spends
        // the remaining half of the original palette; additional direct light adds in full.
        const float blend=.5f,diffuse=.6f;
        float primaryFactor=.2f+.8f*blend;
        float expected=diffuse*(primaryFactor+(native?.05f:0))*255; // A fully lit native light adds its whole brightness below the shared shoulder.
        float budget=diffuse*.8f*(1-blend)*.25f*255;
        for(int channel=0;channel<3;channel++)Assert.InRange(Math.Abs(before[channel]-expected),0,1.1f);
        for(int channel=0;channel<3;channel++)Assert.InRange(after[channel]-before[channel],0,budget+1.1f);
        Assert.True(after[0]>before[0],"The receiver must contain actual traced neutral fill.");
        Assert.InRange(Math.Abs((after[0]-before[0])-(after[1]-before[1])),0,1);
        Assert.InRange(Math.Abs((after[1]-before[1])-(after[2]-before[2])),0,1);
        Assert.Equal(before[3],after[3]);
        if(native)Assert.True(session.NativeLightStatistics!.NativeLightsContributeToGi);
        using var rendered=session.Render(frame,Width,Height,options,surfaceStyles:map);
        Assert.Equal(0,rendered.Statistics.ReadbackCount);
    }

    [Theory,InlineData(false,4f),InlineData(true,4f),InlineData(false,0f),InlineData(true,0f),Trait("Category","Gpu")]
    public void IndependentAuthoredBlinnStaysExactInsideAndOutsideAndWithTheHostZeroExponentRule(bool outside,float shininess)
    {
        using var session=new D3D12FrameSession();
        const float d=.21f;
        var normal=new Vector3(d,0,-MathF.Sqrt(1-d*d));
        var authoredSpecular=new Vector3(.4f,.25f,.125f);
        OriginalFrameScene Fixture(Vector3 specular)
        {
            var material=new SceneMaterial("separated authored PMX highlight",0,6,new(.25f,.125f,.0625f,1),specular,Vector3.Zero,shininess);
            var scene=new SceneSnapshot(Quad(0,normal),[0,2,1,0,3,2],[material],
                camera:new(new(0,0,-5),Vector3.Zero,Vector3.UnitY,3,.1f,20),lightDirection:Vector3.UnitX);
            // Matching palette endpoints isolate the independent authored specular
            // from the new three-knot diffuse transfer.
            return new(new(scene,[new(Vector3.One)],new(Vector3.One,Vector3.One,Matrix4x4.Identity,OriginalTextureSampling.PointClamp)),
                options:Options() with {SelfShadow=new(outside?Matrix4x4.CreateTranslation(3,0,0):Matrix4x4.CreateScale(.2f),
                    32,32,false,false,OriginalTextureSampling.PointClamp,OriginalDepthComparison.LessEqual)},
                materials:[new(false,true,false)]);
        }
        var withoutSpecular=Fixture(Vector3.Zero);var withSpecular=Fixture(authoredSpecular);
        var map=new D3D12SurfaceStyleMap(["endfield"],registeredStyles:TestStylePlugins.AllPresetCatalog.Plugins.Values);
        var mmdBase=session.Capture(withoutSpecular,Width,Height);
        var mmdLit=session.Capture(withSpecular,Width,Height);
        var newBase=session.Capture(withoutSpecular,Width,Height,surfaceStyles:map);
        var newLit=session.Capture(withSpecular,Width,Height,surfaceStyles:map);
        Assert.Equal(mmdBase.Rgba,newBase.Rgba);
        Assert.Equal(mmdLit.Rgba,newLit.Rgba);
        var zeroPixel=Pixel(newBase,Width/2,Height/2);var litPixel=Pixel(newLit,Width/2,Height/2);
        var host=OriginalStyleMath.ResolveHostColors(withSpecular.Surface.Snapshot.Materials[0],withSpecular.Surface.Profile);
        Assert.Equal(shininess==0?.1f:shininess,host.SpecularPower.W);
        var halfVector=Vector3.Normalize(Vector3.UnitX-Vector3.UnitZ);
        float noH=MathF.Max(0,Vector3.Dot(normal,halfVector));
        Assert.InRange(noH,.8f,.9f);
        float lobe=MathF.Pow(noH,host.SpecularPower.W);
        var expectedIncrement=authoredSpecular*lobe*((outside?1:3*d)*255);
        for(int channel=0;channel<3;channel++)
        {
            int increment=litPixel[channel]-zeroPixel[channel];
            Assert.True(increment>2,"Nonzero authored specular must retain a measurable GPU highlight.");
            Assert.InRange(Math.Abs(increment-expectedIncrement[channel]),0,1.1f);
        }
        Assert.Equal((byte)255,zeroPixel[3]);Assert.Equal(zeroPixel[3],litPixel[3]);
    }

    private static OriginalFrameScene Rows()
    {
        var vertices=new List<SceneVertex>();
        const float d=-.05f;var normal=new Vector3(d,0,-MathF.Sqrt(1-d*d));
        foreach(float y in new[]{1.2f,.4f,-.4f,-1.2f})vertices.AddRange(Quad(y,normal,.25f));
        uint[] indices=Enumerable.Range(0,4).SelectMany(i=>new[]{i*4,i*4+2,i*4+1,i*4,i*4+3,i*4+2})
            .Select(index=>(uint)index).ToArray();
        var scene=new SceneSnapshot(vertices.ToArray(),indices,
            Enumerable.Range(0,4).Select(i=>new SceneMaterial("explicit family "+i,i*6,6,new(.6f,.4f,.2f,1),Vector3.Zero,Vector3.Zero,1)).ToArray(),
            camera:new(new(0,0,-5),Vector3.Zero,Vector3.UnitY,3.6f,.1f,20),lightDirection:Vector3.UnitX);
        return new(new(scene,Enumerable.Repeat(new OriginalStyleMaterialBinding(new(.2f)),4),
            new(Vector3.One,Vector3.Zero,Matrix4x4.Identity,OriginalTextureSampling.PointClamp)),options:Options());
    }

    private static OriginalFrameScene Layers(bool straight,bool both,bool sphere,bool sub)
    {
        const float d=.05f;var normal=new Vector3(d,0,-MathF.Sqrt(1-d*d));
        var vertices=Quad(0,normal).Concat(Quad(0,normal)).ToArray();
        var textureFactors=OriginalMaterialTextureFactors.Identity with
        {Diffuse=new(new(.8f,.7f,.6f,1),Vector4.Zero),Toon=new(new(1,1,1,.2f),Vector4.Zero)};
        var binding=new OriginalStyleMaterialBinding(new(.2f),sphere?1:-1,
            sub?OriginalSphereMode.SubTexture:sphere?OriginalSphereMode.Multiply:OriginalSphereMode.None,both){TextureFactors=textureFactors};
        var additional=sub?new SceneAdditionalUvs(1,Enumerable.Repeat(new SceneAdditionalUvVertex(new(.25f,.25f,0,0),default,default,default),8).ToArray()):null;
        var scene=new SceneSnapshot(vertices,[0,2,1,0,3,2,4,6,5,4,7,6],
            [new("red first",0,6,new(.6f,0,0,.8f),Vector3.Zero,Vector3.Zero,1,0),new("green second",6,6,new(0,.6f,0,.8f),Vector3.Zero,Vector3.Zero,1,0)],
            [new("raw diffuse alpha",1,1,[255,255,255,192]),new("sphere",1,1,[192,192,192,192])],
            camera:new(new(0,0,-5),Vector3.Zero,Vector3.UnitY,3,.1f,20),lightDirection:Vector3.UnitX,additionalUvs:additional);
        return new(new(scene,[binding,binding],new(Vector3.One,Vector3.Zero,Matrix4x4.Identity,OriginalTextureSampling.PointClamp)),
            options:Options() with {WriteDepth=false,AlphaOutput=straight?FrameAlphaOutput.Straight:FrameAlphaOutput.LegacyMax});
    }

    private static OriginalFrameScene GiReceiver(bool native)
    {
        var old=D3D12RayGiTests.Bounce();var source=old.Surface.Snapshot;var vertices=source.Vertices.ToArray();
        const float d=-.05f;var normal=new Vector3(d,0,-MathF.Sqrt(1-d*d));
        for(int i=0;i<4;i++)vertices[i]=vertices[i] with {Normal=normal};
        var materials=source.Materials.ToArray();materials[0]=materials[0] with {Diffuse=new(.6f,.6f,.6f,1)};
        var scene=new SceneSnapshot(vertices,source.Indices,materials,source.Textures,source.World,source.Camera,Vector3.UnitX);
        var flags=old.Materials.ToArray();flags[0]=flags[0] with {GiToonResponseOverride=RayGiToonResponse.PreservePaletteBands};
        return new(new(scene,[new(new(.2f)),old.Surface.Materials[1]],new(Vector3.One,Vector3.Zero,Matrix4x4.Identity,OriginalTextureSampling.PointClamp)),
            options:Options(),materials:flags,lighting:native?new([new NativeSceneLight(Guid.NewGuid(),"neutral fill",SceneLightKind.Directional,
                Vector3.Zero,Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI),new(.1f),.5f,30,CastShadows:false)]):null);
    }

    private static SceneVertex[] Quad(float y,Vector3 normal,float halfHeight=.4f)=>[new(new(-.8f,y-halfHeight,0),normal,Vector2.Zero),new(new(.8f,y-halfHeight,0),normal,Vector2.Zero),
        new(new(.8f,y+halfHeight,0),normal,Vector2.Zero),new(new(-.8f,y+halfHeight,0),normal,Vector2.Zero)];
    private static OriginalFrameOptions Options()=>new(OriginalAlphaBlendMode.Normal,true,false,BackgroundColor:Vector4.Zero);
    private static (int X,int Y) Project(OriginalFrameScene frame,Vector3 point)
    {
        var clip=Vector4.Transform(new Vector4(point,1),frame.Surface.Snapshot.Camera.ViewProjection(Width,Height));
        return ((int)((clip.X/clip.W*.5f+.5f)*Width),(int)((.5f-clip.Y/clip.W*.5f)*Height));
    }
    private static byte[] Pixel(RenderedFrame frame,int x,int y)=>frame.Rgba.AsSpan((y*Width+x)*4,4).ToArray();
}
