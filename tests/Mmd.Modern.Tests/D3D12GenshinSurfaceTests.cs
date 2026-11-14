using System.Numerics;
using System.Security.Cryptography;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>Ordinary-PMX body response only. Dedicated face, hair and LightMap inputs are separate work.</summary>
[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12GenshinSurfaceTests
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
    public void EveryActualSurfaceVariantCompilesAndDiffersFromTheMmdShader(string entry,bool native,bool gi,bool morph,bool sub)
    {
        var shader=OriginalFrameShaders.CompileGenshinSurface(entry,native,gi,morph,sub);
        var mmd=OriginalFrameShaders.CompileCharacterToon(entry,native,gi,morph,sub);
        Assert.Equal(new byte[]{(byte)'D',(byte)'X',(byte)'B',(byte)'C'},shader[..4]);
        Assert.NotEqual(Convert.ToHexString(SHA256.HashData(mmd)),Convert.ToHexString(SHA256.HashData(shader)));
        var source=OriginalFrameShaders.GenshinSurfaceSource(native,gi,morph,sub);
        Assert.Contains("GenshinLightResponse(dot(n,l),visibility)",source);
        Assert.Contains("halfLambert=saturate(nDotL*.5+.5)",source);
        Assert.Contains("+highlight*originalC;",source);
        Assert.DoesNotContain("CharacterMainToon(",source);
        if(gi||native)Assert.Contains("GenshinLightResponse(dot(normalize(v.Normal),IncomingLight.xyz),visibility)",source);
        if(native)Assert.Contains("diffuse*remainingPalette*light.ColorIntensity.rgb",source);
        if(sub)Assert.Contains("originalC<1) alpha*=originalC+ToonShadowColor.w*(1-originalC)",source);
    }

    [Fact,Trait("Category","Gpu")]
    public void MixedMaterialDrawsUseDifferentAlgorithmsAndKeepTheMmdRegionAndCoverageExact()
    {
        using var session=new D3D12FrameSession();
        var frame=Rows();var map=new D3D12SurfaceStyleMap([D3D12SurfaceStyle.GenshinPmx,D3D12SurfaceStyle.Mmd]);
        var baseline=session.Capture(frame,Width,Height);
        var mixed=session.Capture(frame,Width,Height,surfaceStyles:map);
        int selectedY=Project(frame,new(0,.6f,0)).Y,unchangedY=Project(frame,new(0,-.6f,0)).Y;
        Assert.NotEqual(Pixel(baseline,Width/2,selectedY),Pixel(mixed,Width/2,selectedY));
        Assert.Equal(Pixel(baseline,Width/2,unchangedY),Pixel(mixed,Width/2,unchangedY));
        int unchangedOpaque=0,changed=0;
        for(int y=0;y<Height;y++)for(int x=0;x<Width;x++)
        {
            int offset=(y*Width+x)*4;
            Assert.Equal(baseline.Rgba[offset+3],mixed.Rgba[offset+3]);
            bool inactive=unchangedY<Height/2?y<Height/2:y>=Height/2;
            if(inactive)
            {
                Assert.Equal(Pixel(baseline,x,y),Pixel(mixed,x,y));
                if(baseline.Rgba[offset+3]!=0)unchangedOpaque++;
            }
            else if(!Pixel(baseline,x,y).SequenceEqual(Pixel(mixed,x,y)))changed++;
        }
        Assert.True(unchangedOpaque>200);Assert.True(changed>200);
        Assert.Single(session.LastRecordings.Select(record=>record.DeviceIdentity).Distinct());
        Assert.Single(session.LastRecordings.Select(record=>record.CommandListIdentity).Distinct());
        Assert.Equal(baseline.Rgba,session.Capture(frame,Width,Height).Rgba);
        Assert.Equal(baseline.Rgba,session.Capture(frame,Width,Height,surfaceStyles:new([D3D12SurfaceStyle.Mmd,D3D12SurfaceStyle.Mmd])).Rgba);
        var warm=session.Statistics!;long readbacks=session.Runtime.Readbacks;
        for(int i=0;i<18;i++)
        {
            using var output=session.Render(frame,Width,Height,surfaceStyles:i%2==0?map:null);
            Assert.Equal(0,output.Statistics.ReadbackCount);
            Assert.Equal(warm.ShaderCompilations,output.Statistics.ShaderCompilations);
            Assert.Equal(warm.PipelineCreations,output.Statistics.PipelineCreations);
            Assert.InRange(output.Statistics.CachedResources,0,warm.CachedResources+2);
        }
        Assert.Equal(readbacks,session.Runtime.Readbacks);
    }

    [Theory]
    [InlineData(false,false,false)]
    [InlineData(true,false,false)]
    [InlineData(false,true,false)]
    [InlineData(true,true,false)]
    [InlineData(false,true,true)]
    [InlineData(true,true,true)]
    [Trait("Category","Gpu")]
    public void RawAlphaAndTransparentSourceOrderSurviveMorphSphereAndSubTexture(bool straight,bool sphere,bool sub)
    {
        using var session=new D3D12FrameSession();
        var frame=Layers(straight,sphere,sub);
        var baseline=session.Capture(frame,Width,Height);
        var map=new D3D12SurfaceStyleMap([D3D12SurfaceStyle.GenshinPmx,D3D12SurfaceStyle.Mmd]);
        var mixed=session.Capture(frame,Width,Height,surfaceStyles:map);
        for(int offset=3;offset<baseline.Rgba.Length;offset+=4)Assert.Equal(baseline.Rgba[offset],mixed.Rgba[offset]);
        // The green second draw is drawn over the selected red draw. Reordering the
        // families would change both its green contribution and the red attenuation.
        const float c=.525f;
        float rawAlpha=.5f*(128/255f)*(sphere?128/255f:1);
        if(sub)rawAlpha*=c+.2f*(1-c);
        var first=new Vector3(.6f,0,0)*new Vector3(.8f,.7f,.6f);
        var second=new Vector3(0,.6f,0)*new Vector3(.8f,.7f,.6f)*(.2f+.8f*c);
        if(sphere){first*=128/255f;second*=128/255f;}
        var expected=first*(rawAlpha*(1-rawAlpha))+second*rawAlpha;
        float composedAlpha=straight?rawAlpha*(2-rawAlpha):rawAlpha;
        if(straight)expected/=composedAlpha;
        var pixel=Pixel(mixed,Width/2,Height/2);
        for(int channel=0;channel<3;channel++)Assert.InRange(Math.Abs(pixel[channel]-expected[channel]*255),0,2f);
        Assert.InRange(Math.Abs(pixel[3]-composedAlpha*255),0,2f);
        Assert.NotEqual(Pixel(baseline,Width/2,Height/2),pixel);
        Assert.Equal(baseline.Rgba,session.Capture(frame,Width,Height).Rgba);
    }

    [Theory,InlineData(false),InlineData(true)]
    [Trait("Category","Gpu")]
    public void GiAndNativeDirectUseTheSameRemainingPaletteBudget(bool native)
    {
        using var session=new D3D12FrameSession();
        var frame=GiReceiver(native);
        var map=new D3D12SurfaceStyleMap([D3D12SurfaceStyle.GenshinPmx,D3D12SurfaceStyle.Mmd]);
        var options=new RayGiOptions(LightingMode.SoftwareRayTracing,Strength:20,SamplesPerPixel:32,Seed:17,
            MaxRayDistance:50,RayEpsilon:.001f,DirectionalLightDirection:Vector3.UnitZ,DirectionalIrradiance:new(3),
            Composition:RayGiComposition.ToonPalette);
        var off=session.Capture(frame,Width,Height,surfaceStyles:map);
        var on=session.Capture(frame,Width,Height,options,surfaceStyles:map);
        var before=Pixel(off,Width/2,Height/2);var after=Pixel(on,Width/2,Height/2);
        // NdotL=-.05 has old clamped c=0 but half-Lambert=.475, so the new
        // smooth band has blend .15625. Neutral GI spends at most 25% of its contrast.
        const float blend=.15625f,diffuse=.6f;
        float primaryFactor=.2f+.8f*blend;
        float expected=diffuse*(primaryFactor+(native?(1-primaryFactor)*.05f:0))*255;
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

    [Theory,InlineData(false),InlineData(true)]
    [Trait("Category","Gpu")]
    public void AuthoredNonzeroBlinnHighlightKeepsItsExactMmdIncrementInsideAndOutsideTheShadowProjection(bool outside)
    {
        using var session=new D3D12FrameSession();
        const float d=.175f;
        var normal=new Vector3(d,0,-MathF.Sqrt(1-d*d));
        var authoredSpecular=new Vector3(.4f,.25f,.125f);
        OriginalFrameScene Fixture(Vector3 specular)
        {
            var material=new SceneMaterial("authored PMX highlight",0,6,new(.25f,.125f,.0625f,1),specular,Vector3.Zero,4);
            var scene=new SceneSnapshot(Quad(0,normal),[0,2,1,0,3,2],[material],
                camera:new(new(0,0,-5),Vector3.Zero,Vector3.UnitY,3,.1f,20),lightDirection:Vector3.UnitX);
            // Equal authored palette endpoints isolate the specular increment while
            // retaining nonzero diffuse. A changed highlight attenuation is observable.
            return new(new(scene,[new(Vector3.One)],new(Vector3.One,Vector3.One,Matrix4x4.Identity,OriginalTextureSampling.PointClamp)),
                options:Options() with {SelfShadow=new(outside?Matrix4x4.CreateTranslation(3,0,0):Matrix4x4.CreateScale(.2f),
                    32,32,false,false,OriginalTextureSampling.PointClamp,OriginalDepthComparison.LessEqual)},
                materials:[new(false,true,false)]);
        }
        var withoutSpecular=Fixture(Vector3.Zero);var withSpecular=Fixture(authoredSpecular);
        var map=new D3D12SurfaceStyleMap([D3D12SurfaceStyle.GenshinPmx]);
        var mmdBase=session.Capture(withoutSpecular,Width,Height);
        var mmdLit=session.Capture(withSpecular,Width,Height);
        var genshinBase=session.Capture(withoutSpecular,Width,Height,surfaceStyles:map);
        var genshinLit=session.Capture(withSpecular,Width,Height,surfaceStyles:map);
        Assert.Equal(mmdBase.Rgba,genshinBase.Rgba);
        Assert.Equal(mmdLit.Rgba,genshinLit.Rgba);
        var oldBase=Pixel(mmdBase,Width/2,Height/2);var oldLit=Pixel(mmdLit,Width/2,Height/2);
        var newBase=Pixel(genshinBase,Width/2,Height/2);var newLit=Pixel(genshinLit,Width/2,Height/2);
        var halfVector=Vector3.Normalize(Vector3.UnitX-Vector3.UnitZ);
        float lobe=MathF.Pow(MathF.Max(0,Vector3.Dot(normal,halfVector)),4);
        var expectedIncrement=authoredSpecular*lobe*(outside?1:3*d)*255;
        for(int channel=0;channel<3;channel++)
        {
            int oldIncrement=oldLit[channel]-oldBase[channel],newIncrement=newLit[channel]-newBase[channel];
            Assert.Equal(oldIncrement,newIncrement);
            Assert.True(newIncrement>2,"Each authored specular channel must contribute a real nonzero GPU highlight.");
            Assert.InRange(Math.Abs(newIncrement-expectedIncrement[channel]),0,1.1f);
        }
        Assert.Equal((byte)255,newBase[3]);Assert.Equal(newBase[3],newLit[3]);
    }

    private static OriginalFrameScene Rows()
    {
        var vertices=new List<SceneVertex>();
        const float d=.175f;var normal=new Vector3(d,0,-MathF.Sqrt(1-d*d));
        foreach(float y in new[]{.6f,-.6f})vertices.AddRange(Quad(y,normal));
        var scene=new SceneSnapshot(vertices.ToArray(),[0,2,1,0,3,2,4,6,5,4,7,6],
            [new("selected",0,6,new(.6f,.4f,.2f,1),Vector3.Zero,Vector3.Zero,1),new("MMD",6,6,new(.6f,.4f,.2f,1),Vector3.Zero,Vector3.Zero,1)],
            camera:new(new(0,0,-5),Vector3.Zero,Vector3.UnitY,3,.1f,20),lightDirection:Vector3.UnitX);
        return new(new(scene,[new(new(.2f)),new(new(.2f))],new(Vector3.One,Vector3.Zero,Matrix4x4.Identity,OriginalTextureSampling.PointClamp)),options:Options());
    }

    private static OriginalFrameScene Layers(bool straight,bool sphere,bool sub)
    {
        const float d=.175f;var normal=new Vector3(d,0,-MathF.Sqrt(1-d*d));
        var vertices=Quad(0,normal).Concat(Quad(0,normal)).ToArray();
        var textureFactors=OriginalMaterialTextureFactors.Identity with
        {Diffuse=new(new(.8f,.7f,.6f,1),Vector4.Zero),Toon=new(new(1,1,1,.2f),Vector4.Zero)};
        var binding=new OriginalStyleMaterialBinding(new(.2f),sphere?1:-1,
            sub?OriginalSphereMode.SubTexture:sphere?OriginalSphereMode.Multiply:OriginalSphereMode.None,true){TextureFactors=textureFactors};
        var additional=sub?new SceneAdditionalUvs(1,Enumerable.Repeat(new SceneAdditionalUvVertex(new(.25f,.25f,0,0),default,default,default),8).ToArray()):null;
        var scene=new SceneSnapshot(vertices,[0,2,1,0,3,2,4,6,5,4,7,6],
            [new("red first",0,6,new(.6f,0,0,.5f),Vector3.Zero,Vector3.Zero,1,0),new("green second",6,6,new(0,.6f,0,.5f),Vector3.Zero,Vector3.Zero,1,0)],
            [new("raw diffuse alpha",1,1,[255,255,255,128]),new("sphere",1,1,[128,128,128,128])],
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

    private static SceneVertex[] Quad(float y,Vector3 normal)=>[new(new(-.8f,y-.4f,0),normal,Vector2.Zero),new(new(.8f,y-.4f,0),normal,Vector2.Zero),
        new(new(.8f,y+.4f,0),normal,Vector2.Zero),new(new(-.8f,y+.4f,0),normal,Vector2.Zero)];
    private static OriginalFrameOptions Options()=>new(OriginalAlphaBlendMode.Normal,true,false,BackgroundColor:Vector4.Zero);
    private static (int X,int Y) Project(OriginalFrameScene frame,Vector3 point)
    {
        var clip=Vector4.Transform(new Vector4(point,1),frame.Surface.Snapshot.Camera.ViewProjection(Width,Height));
        return ((int)((clip.X/clip.W*.5f+.5f)*Width),(int)((.5f-clip.Y/clip.W*.5f)*Height));
    }
    private static byte[] Pixel(RenderedFrame frame,int x,int y)=>frame.Rgba.AsSpan((y*Width+x)*4,4).ToArray();
}
