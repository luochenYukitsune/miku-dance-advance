using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

// Synthetic fixtures and independent ray/normal/source-equation reference.
// No graph/assets, dynamic attribute parity or complete HA/HB claim.
[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class EndfieldSourceInputsGpuTests
{
    private const int W=97,H=97;
    private static readonly SceneCamera Camera=new(new(0,0,-5),Vector3.Zero,Vector3.UnitY,2.6f,.1f,30);
    private static readonly Vector3 OldPre=new(.22f,.16f,.1f);
    private static readonly D3D12EndfieldMatcapBinding Matcap=new(0,1,2,D3D12MatcapColorEncoding.LinearRgb,
        0,OldPre,Vector3.One,NormalStrength:1.7f,HighlightCoefficient:1,BaselineStrength:.8f,CoupleEndfieldPalette:true);
    private static readonly D3D12EndfieldRimBinding Rim=new(OldPre,new(.8f,.7f,.6f),EdgeSize:4,EdgeStrength:.16f,RangeStrength:.38f);
    private static readonly D3D12EndfieldMatcapBinding CoupledMatcap=Matcap with {BaselineStrength=Rim.RangeStrength+Rim.RadialStrength};
    private static readonly D3D12EndfieldHairBinding Hair=new(3,OldPre,-.15f);
    private static readonly D3D12EndfieldPaletteBinding Palette=new(Vector3.Zero,
        EmissionStrength:.4f,UniformEmissionBlue:.7f,LightInfluence:0,Density:0);
    private static readonly D3D12EndfieldBaseImageBinding Base=new(new(.62f,.48f,.35f),new(.25f,.4f,.6f),
        Vector3.One,Vector3.One,ShadowFactor:.27f,WarmTint:0,DetailBlue:1,ShadowStrength:.85f);

    [Fact,Trait("Category","Gpu")]
    public void FacingUsesFragmentPerspectiveViewAndConstantOrthographicView()
    {
        using var session=new D3D12FrameSession();
        var input=Base with { FacingBase=new(.72f),ShadowStrength=0 };
        foreach(var camera in new[] {Camera,Camera with {Eye=new(0,0,-2),VerticalFovDegrees=60},
            Camera with {Eye=new(1.3f,.4f,-3),VerticalFovDegrees=40}})
        {
            var frame=Frame(camera:camera);
            var plain=session.Capture(frame,W,H,surfaceStyles:Plain());
            var actual=session.Capture(frame,W,H,surfaceStyles:Map(input));
            foreach(int x in new[]{36,48,60}) AssertPixel(plain,actual,x,44,Delta(frame.Surface.Snapshot,input,Matcap,x,44));
            AssertCoverage(plain,actual);
            // An options camera overrides the immutable snapshot camera for both
            // geometry projection and facing; neither may retain snapshot pose/FOV.
            var original=Frame();
            var overridden=new OriginalFrameScene(original.Surface,options:original.Options! with {CameraOverride=camera});
            Assert.Equal(actual.Rgba,session.Capture(overridden,W,H,surfaceStyles:Map(input)).Rgba);
        }
        var perspective=Frame(camera:Camera with {Eye=new(0,0,-2),VerticalFovDegrees=60});
        var left=Delta(perspective.Surface.Snapshot,input,Matcap,32,48);
        var right=Delta(perspective.Surface.Snapshot,input,Matcap,64,48);
        Assert.True(Vector3.Distance(left,right)*255>1,"Off-axis fixture must distinguish per-fragment view from a camera-wide vector.");
    }

    [Fact,Trait("Category","Gpu")]
    public void FacingBlendZeroRestoresOldInputIncludingExactGrazingZeroPower()
    {
        using var session=new D3D12FrameSession();
        var textures=Textures(); textures[0]=new("exact tangent packed normal",1,1,[255,128,0,0]);
        var frame=Frame(textures:textures);
        var original=Base with {ShadowStrength=0};
        var old=session.Capture(frame,W,H,surfaceStyles:Map(original));
        var zero=session.Capture(frame,W,H,surfaceStyles:Map(original with {FacingBase=new(0)}));
        Assert.Equal(old.Rgba,zero.Rgba);
        var changed=session.Capture(frame,W,H,surfaceStyles:Map(original with {FacingBase=new(.5f)}));
        AssertPixel(session.Capture(frame,W,H,surfaceStyles:Plain()),changed,48,48,
            Delta(frame.Surface.Snapshot,original with {FacingBase=new(.5f)},Matcap,48,48));
        Assert.NotEqual(old.Rgba,changed.Rgba);
    }

    [Fact,Trait("Category","Gpu")]
    public void DirectionalShadowUsesIndependentNormalStrengthAndSourceMapRange()
    {
        using var session=new D3D12FrameSession();
        var frame=Frame(); var plain=session.Capture(frame,W,H,surfaceStyles:Plain());
        var outputs=new List<byte[]>();
        foreach(float strength in new[]{0f,1f,2f})
        {
            var input=Base with {DirectionalShadow=new(Vector3.Normalize(new(.8f,.2f,-.4f)),.3f,strength)};
            var actual=session.Capture(frame,W,H,surfaceStyles:Map(input));
            foreach(int x in new[]{36,48,60}) AssertPixel(plain,actual,x,48,Delta(frame.Surface.Snapshot,input,Matcap,x,48));
            outputs.Add(actual.Rgba);
            // MatCap normal/gate has different semantics; white highlight isolates that
            // downstream stage, so changing its normal strength must not change source q.
            Assert.Equal(actual.Rgba,session.Capture(frame,W,H,surfaceStyles:Map(input,matcap:Matcap with {NormalStrength=0})).Rgba);
        }
        Assert.NotEqual(outputs[0],outputs[2]);
        foreach(float width in new[]{-1f,1f,2f})
        {
            var input=Base with {DirectionalShadow=new(Vector3.UnitX,width,1)};
            AssertPixel(plain,session.Capture(frame,W,H,surfaceStyles:Map(input)),48,48,
                Delta(frame.Surface.Snapshot,input,Matcap,48,48));
        }
    }

    [Fact,Trait("Category","Gpu")]
    public void SelectedCurrentUv1AndDeformedGeometryDriveBothIndependentNormals()
    {
        using var session=new D3D12FrameSession();
        var binding=Matcap with {UvChannel=1};
        var input=Base with {FacingBase=new(.7f),DirectionalShadow=new(Vector3.Normalize(new(.9f,.2f,-.2f)),.3f,2),
            BaseInputMode=D3D12EndfieldPaletteInputMode.Texture,BaseCombinedTextureIndex=4,
            BaseEncoding=D3D12MatcapColorEncoding.SrgbRgb};
        byte[]? previous=null;
        foreach(float shift in new[]{0f,.28f})
        {
            var source=Frame(uvShift:shift);
            var deformed=source.Surface.Snapshot.WithDeformedVertices(source.Surface.Snapshot.Vertices.ToArray()
                .Select(v=>v with {Position=v.Position+new Vector3(shift*.2f,0,0),Uv=v.Uv+new Vector2(.19f,-.11f)}).ToArray());
            var frame=Replace(source,deformed);
            Assert.True(deformed.HasDynamicVertices);
            var plain=session.Capture(frame,W,H,surfaceStyles:Plain());
            var actual=session.Capture(frame,W,H,surfaceStyles:Map(input,matcap:binding));
            foreach(int x in new[]{36,48,60}) AssertPixel(plain,actual,x,44,Delta(deformed,input,binding,x,44));
            AssertCoverage(plain,actual);
            if(previous is not null) Assert.NotEqual(previous,actual.Rgba);
            previous=actual.Rgba;
        }
    }

    [Theory,InlineData(false,false),InlineData(false,true),InlineData(true,false),InlineData(true,true)]
    [Trait("Category","Gpu")]
    public void SourceInputsFeedActualCoupledRimRadialAndNativeGiExactlyOnce(bool native,bool gi)
    {
        using var session=new D3D12FrameSession();
        var frame=Frame(native:native,alpha:.61f);
        var input=Base with {FacingBase=new(.7f),DirectionalShadow=new(Vector3.Normalize(new(.8f,.1f,-.3f)),.3f,2)};
        var options=gi?new RayGiOptions(LightingMode.SoftwareRayTracing,SamplesPerPixel:4,Seed:13,Composition:RayGiComposition.ToonPalette):null;
        var plain=session.Capture(frame,W,H,options,surfaceStyles:Plain());
        var old=session.Capture(frame,W,H,options,surfaceStyles:Map(Base,true));
        var actual=session.Capture(frame,W,H,options,surfaceStyles:Map(input,true));
        foreach(int x in new[]{40,48,56}) AssertPixel(plain,actual,x,48,Delta(frame.Surface.Snapshot,input,CoupledMatcap,x,48,true),2.1);
        AssertCoverage(plain,actual);
        Assert.Equal(old.Rgba,session.Capture(frame,W,H,options,surfaceStyles:Map(Base with {FacingBase=null,DirectionalShadow=null},true)).Rgba);
    }

    [Fact,Trait("Category","Gpu")]
    public void NonzeroDrawOffsetLeavesDefaultMmdUntouchedAndUsesCorrectTypedImage()
    {
        using var session=new D3D12FrameSession();
        var frame=Frame(split:true);
        var input=Base with {FacingBase=new(.7f),DirectionalShadow=new(Vector3.Normalize(new(.8f,.1f,-.3f))),
            BaseInputMode=D3D12EndfieldPaletteInputMode.Texture,BaseCombinedTextureIndex=4,BaseEncoding=D3D12MatcapColorEncoding.SrgbRgb};
        D3D12SurfaceStyleMap Styles(bool enabled)=>new([D3D12SurfaceStyle.Mmd,D3D12SurfaceStyle.EndfieldPmx],
            [new(),new(EndfieldMatcap:enabled?Matcap with {Palette=Palette with {BaseImage=input}}:null)]);
        var plain=session.Capture(frame,W,H,surfaceStyles:Styles(false));
        var actual=session.Capture(frame,W,H,surfaceStyles:Styles(true));
        Assert.Equal(6,frame.Surface.Snapshot.Materials[1].StartIndex);
        var first=Project(frame.Surface.Snapshot,new(-.45f,0,0));var second=Project(frame.Surface.Snapshot,new(.45f,0,0));
        foreach(int dy in new[]{-4,4}) foreach(int dx in new[]{-4,4})
        {
            AssertPixel(plain,actual,first.X+dx,first.Y+dy,Vector3.Zero,0);
            AssertPixel(plain,actual,second.X+dx,second.Y+dy,Delta(frame.Surface.Snapshot,input,Matcap,second.X+dx,second.Y+dy,material:1));
        }
        AssertCoverage(plain,actual);
    }

    [Fact,Trait("Category","Gpu")]
    public void SourceEditsResetHistoriesOnSubmitAndAbortRetainsWarmResources()
    {
        using var session=new D3D12FrameSession();
        var source=Frame(camera:Camera with {VerticalFovDegrees=30});
        var frame=new OriginalFrameScene(source.Surface,options:source.Options! with {BackgroundColor=Vector4.One,AlphaOutput=FrameAlphaOutput.LegacyMax});
        string loader=Environment.GetEnvironmentVariable("MMD_FSR_LOADER_PATH")??throw new InvalidOperationException("Real FSR loader required.");
        var temporal=new D3D12TemporalUpscalingOptions {Mode=D3D12TemporalUpscalingMode.Fsr,QualityPreset=D3D12FsrQualityPreset.NativeAa,LoaderPath=loader};
        var gi=new RayGiOptions(LightingMode.SoftwareRayTracing,SamplesPerPixel:4,Seed:31,Composition:RayGiComposition.ToonPalette,Denoiser:RayGiDenoiser.TemporalSpatial);
        var input=Base with {FacingBase=new(.7f),DirectionalShadow=new(Vector3.Normalize(new(.8f,.1f,-.3f)),.3f,2)};
        var original=Map(input);var edited=Map(input with {FacingBase=new(.6f),DirectionalShadow=new(Vector3.UnitX,.4f,1)});
        long reads=session.Runtime.Readbacks;
        Render(original,1,"FirstFrame","FirstFrame");Render(Map(input with {}),2,"None","None");var warm=session.Statistics!;
        Assert.Throws<OperationCanceledException>(()=>session.RenderForPresent(frame,96,72,gi,(_,_)=>throw new OperationCanceledException("Abort source-input edit."),temporalUpscaling:temporal,surfaceStyles:edited));
        Assert.False(session.Runtime.IsRecording);
        Render(original,3,"None","AbortedFrame");Render(edited,1,"ExplicitReset","ExplicitRequest");Render(edited,2,"None","None");
        Assert.Equal(warm.ShaderCompilations,session.Statistics!.ShaderCompilations);Assert.Equal(warm.PipelineCreations,session.Statistics.PipelineCreations);
        Assert.InRange(session.Statistics.CachedResources,0,warm.CachedResources+1);Assert.Equal(reads,session.Runtime.Readbacks);
        void Render(D3D12SurfaceStyleMap styles,ulong count,string giReason,string aaReason)
        {using var image=session.Render(frame,96,72,gi,temporalUpscaling:temporal,surfaceStyles:styles);Assert.Equal(count,session.RayGiStatistics!.TemporalHistoryFrames);Assert.Equal(giReason,session.RayGiStatistics.TemporalResetReason);Assert.Equal(aaReason,image.Statistics.TemporalUpscaling!.ResetReason);}
    }

    private static D3D12SurfaceStyleMap Plain()=>new([D3D12SurfaceStyle.EndfieldPmx],[new()]);
    private static D3D12SurfaceStyleMap Map(D3D12EndfieldBaseImageBinding input,bool coupled=false,D3D12EndfieldMatcapBinding? matcap=null)
        =>new([D3D12SurfaceStyle.EndfieldPmx],[new(coupled?D3D12CharacterMaterialRole.Hair:D3D12CharacterMaterialRole.Body,
            EndfieldMatcap:(matcap??(coupled?CoupledMatcap:Matcap)) with {Palette=Palette with {BaseImage=input}},EndfieldRim:coupled?Rim:null,EndfieldHair:coupled?Hair:null)]);
    private static SceneTexture[] Textures()=>[new("packed RG numerical",2,2,[150,110,0,0,205,145,255,255,165,165,0,255,215,100,255,0]),
        new("P numerical",1,1,[40,0,0,24]),new("constant MatCap",1,1,[40,180,70,180]),new("radial numerical Green",1,1,[0,64,0,0]),
        new("authored sRGB base",2,2,[45,100,210,0,210,160,80,255,90,180,70,120,170,65,130,24])];
    private static OriginalFrameScene Frame(SceneCamera? camera=null,SceneTexture[]? textures=null,float uvShift=0,bool native=false,float alpha=1,bool split=false)
    {
        var vertices=new List<SceneVertex>();var indices=new List<uint>();var materials=new List<SceneMaterial>();
        foreach(float center in split?new[]{-.45f,.45f}:new[]{0f})
        {float half=split?.34f:.8f;uint start=(uint)vertices.Count;
            foreach(var p in new[]{new Vector2(-half,-half),new(half,-half),new(half,half),new(-half,half)})
                vertices.Add(new(new(center+p.X,p.Y,0),Vector3.UnitZ,new((p.X/half+1)*.5f,(p.Y/half+1)*.5f)));
            int first=indices.Count;indices.AddRange([start,start+2,start+1,start,start+3,start+2]);
            materials.Add(new("explicit receiver",first,6,new(.24f,.2f,.16f,alpha),new(.035f,.025f,.015f),new(.02f),4));}
        var additional=new SceneAdditionalUvs(1,vertices.Select(v=>new SceneAdditionalUvVertex(new(v.Uv.X*.8f-.17f+uvShift,1-v.Uv.Y*.65f+uvShift,0,0),default,default,default)).ToArray());
        var scene=new SceneSnapshot(vertices.ToArray(),indices.ToArray(),materials,textures??Textures(),camera:camera??Camera,lightDirection:-Vector3.UnitZ,
            additionalUvs:additional,rawRadialSeeds:new(Enumerable.Repeat(Vector3.UnitY,vertices.Count).ToArray()));
        return new(new(scene,materials.Select(_=>new OriginalStyleMaterialBinding(new(.2f),DoubleSided:true)).ToArray(),new(Vector3.One,Vector3.One,Matrix4x4.Identity,OriginalTextureSampling.PointClamp)),
            options:new(OriginalAlphaBlendMode.Normal,true,false,BackgroundColor:Vector4.Zero,AlphaOutput:FrameAlphaOutput.Straight),
            lighting:native?new([new NativeSceneLight(Guid.NewGuid(),"fill",SceneLightKind.Directional,Vector3.Zero,Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI),Vector3.One,1.2f,30,CastShadows:false)]):null);
    }
    private static OriginalFrameScene Replace(OriginalFrameScene f,SceneSnapshot s)=>new(new(s,f.Surface.Materials,f.Surface.Profile),options:f.Options,lighting:f.Lighting);

    private static Vector3 Delta(SceneSnapshot s,D3D12EndfieldBaseImageBinding input,D3D12EndfieldMatcapBinding mat,int x,int y,bool coupled=false,int material=0)
    {
        Vector3 p=Point(s,x,y);int first=(int)s.Indices[s.Materials[material].StartIndex];
        Vector3 a=s.Vertices[first].Position,b=s.Vertices[first+2].Position;
        var f=new Vector2((p.X-a.X)/(b.X-a.X),(p.Y-a.Y)/(b.Y-a.Y));
        Vector2 Uv(int i)=>mat.UvChannel==0?s.Vertices[i].Uv:new(s.AdditionalUvs!.Vertices[i].Uv1.X,s.AdditionalUvs.Vertices[i].Uv1.Y);
        Vector2 uv=Uv(first)+f*(Uv(first+2)-Uv(first));
        Vector4 Sample(int index,bool srgb=false){var t=s.Textures[index];return MatcapOracle.BilinearRepeat(t.Rgba,t.Width,t.Height,uv,srgb);}
        var packed=Sample(mat.NormalCombinedTextureIndex);
        Vector3 Normal(float strength)=>MatcapOracle.PackedNormal(new(packed.X,packed.Y),Vector3.UnitZ,new(Vector3.UnitX,mat.UvChannel==1?1:-1),1,true,strength);
        Vector3 baseRgb=input.BaseImageLinearRgb;
        if(input.BaseInputMode==D3D12EndfieldPaletteInputMode.Texture){var sample=Sample(input.BaseCombinedTextureIndex!.Value,input.BaseEncoding==D3D12MatcapColorEncoding.SrgbRgb);baseRgb=new(sample.X,sample.Y,sample.Z);}
        if(input.FacingBase is {} facing)
        {Vector3 view=s.Camera.VerticalFovDegrees is null?s.Camera.Eye-s.Camera.Target:s.Camera.Eye-p;float d=Math.Clamp(MathF.Abs(Vector3.Dot(Normal(1),Vector3.Normalize(view))),0,1);
            float blend=Math.Clamp(facing.Blend,0,.99999f),power=blend==.5f?1:blend<.5f?2*blend:.5f/(1-blend);
            float amount=1-(power==0?1:MathF.Pow(d,power));baseRgb=baseRgb*(1-amount)+baseRgb*baseRgb*amount;}
        float q=input.ShadowFactor;
        if(input.DirectionalShadow is {} dir)
        {float u=Vector3.Dot(Normal(dir.NormalStrength),Vector3.Normalize(dir.WorldDirection))*.5f+.5f;
            float max=1-(dir.Width-.5f),min=max*dir.Width;q=max==min?0:Math.Clamp((u-min)/(max-min)*1.5f,0,1.5f);}
        Vector3 pre=EndfieldBaseImageOracle.Evaluate(input,true,baseRgb,sampledShadowFactor:q);
        float radial=.75f*(64f/255)*(Hair.Brightness+1.5001001358032227f);
        var result=EndfieldPaletteOracle.Evaluate(pre,pre,Rim.RimColor,mat.BaselineStrength,coupled?Rim.EdgeStrength:0,coupled?radial:0,
            Palette.UniformEmissionBlue,Palette.EmissionStrength,Palette.LightInfluence,Palette.ReceivedLightLinearRgb,Palette.Density,uv,hasHair:coupled,hasRim:coupled);
        return result.Emission-pre*mat.BaselineStrength;
    }
    private static Vector3 Point(SceneSnapshot s,int x,int y)
    {Matrix4x4.Invert(s.Camera.ViewProjection(W,H),out var inverse);Vector3 Unproject(float z){var p=Vector4.Transform(new Vector4(2*(x+.5f)/W-1,1-2*(y+.5f)/H,z,1),inverse);return new(p.X/p.W,p.Y/p.W,p.Z/p.W);}var a=Unproject(0);var d=Unproject(1)-a;return a-d*(a.Z/d.Z);}
    private static (int X,int Y) Project(SceneSnapshot s,Vector3 p)
    {var q=Vector4.Transform(new Vector4(p,1),s.Camera.ViewProjection(W,H));return ((int)((q.X/q.W+1)*W/2),(int)((1-q.Y/q.W)*H/2));}
    private static void AssertPixel(RenderedFrame plain,RenderedFrame actual,int x,int y,Vector3 delta,double tolerance=1.6)
    {int at=(y*W+x)*4;Assert.True(plain.Rgba[at+3]>0,"Independent sample must lie inside receiver.");for(int c=0;c<3;c++)Assert.InRange(Math.Abs(actual.Rgba[at+c]-Math.Clamp(plain.Rgba[at+c]+255*delta[c],0,255)),0,tolerance);}
    private static void AssertCoverage(RenderedFrame a,RenderedFrame b){for(int at=3;at<a.Rgba.Length;at+=4)Assert.Equal(a.Rgba[at],b.Rgba[at]);}
}
