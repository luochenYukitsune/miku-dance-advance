using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

// Independently calculated synthetic signals. Tests the MDA live-light adapter,
// not Blender's renderer or source-game assets.
[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class EndfieldLiveMaterialGpuTests
{
    private const int W=97,H=97;
    private static readonly Vector3 C=new(.20f,.14f,.10f);
    private static readonly D3D12EndfieldPaletteBinding Live=new(Vector3.Zero,LightInfluence:.8f,
        ReceivedLightInputMode:D3D12EndfieldPaletteInputMode.RendererDiffuse);
    private static readonly D3D12EndfieldMatcapBinding Matcap=new(0,1,2,D3D12MatcapColorEncoding.LinearRgb,0,C,Vector3.One,
        NormalStrength:0,HighlightCoefficient:0,BaselineStrength:1,CoupleEndfieldPalette:true);

    [Fact,Trait("Category","Gpu")]
    public void PrimaryDirectionCurrentPackedNormalAndRadianceProduceNumericalLiveDiffuse()
    {
        using var session=new D3D12FrameSession();
        foreach(byte red in new byte[]{160,217}) foreach(Vector3 direction in new[]{Vector3.UnitX,-Vector3.UnitX})
        {
            var frame=Frame(direction,red:red);
            var old=session.Capture(frame,W,H,surfaceStyles:Map(Live with {LightInfluence=0}));
            var actual=session.Capture(frame,W,H,surfaceStyles:Map(Live));
            Vector3 n=Normal(frame.Surface.Snapshot,red);
            Vector3 delta=C*(Live.LightInfluence*Math.Max(Vector3.Dot(n,direction),0));
            AssertDelta(old,actual,delta);
            Assert.Equal(actual.Rgba,session.Capture(frame,W,H,surfaceStyles:Map(Live,Matcap with {NormalStrength=2.4f})).Rgba);
            var changedRadiance=new OriginalFrameScene(new(frame.Surface.Snapshot,frame.Surface.Materials,
                frame.Surface.Profile with {LightAmbient=new(.5f,.8f,.3f)}),options:frame.Options);
            AssertDelta(session.Capture(changedRadiance,W,H,surfaceStyles:Map(Live with {LightInfluence=0})),
                session.Capture(changedRadiance,W,H,surfaceStyles:Map(Live)),delta*new Vector3(.5f,.8f,.3f));
            AssertCoverage(old,actual);
        }
    }

    [Fact,Trait("Category","Gpu")]
    public void CurrentDeformedNormalsFeedLiveLightAndZeroInfluenceRestoresExactOldPixels()
    {
        using var session=new D3D12FrameSession();
        var source=Frame(Vector3.UnitX);
        var deformed=source.Surface.Snapshot.WithDeformedVertices(source.Surface.Snapshot.Vertices.ToArray()
            .Select(v=>v with {Position=v.Position+new Vector3(.08f,0,0),Normal=Vector3.Normalize(new(-.2f,0,1))}).ToArray());
        var frame=new OriginalFrameScene(new(deformed,source.Surface.Materials,source.Surface.Profile),options:source.Options);
        var old=session.Capture(frame,W,H,surfaceStyles:Map(Live with {LightInfluence=0}));
        var live=session.Capture(frame,W,H,surfaceStyles:Map(Live));
        AssertDelta(old,live,C*(Live.LightInfluence*Math.Max(Vector3.Dot(Normal(deformed,217),Vector3.UnitX),0)));
        Assert.NotEqual(session.Capture(source,W,H,surfaceStyles:Map(Live)).Rgba,live.Rgba);
        Assert.Equal(old.Rgba,session.Capture(frame,W,H,surfaceStyles:Map(new(Vector3.Zero))).Rgba);
    }

    [Theory,InlineData(SceneLightKind.Directional,false),InlineData(SceneLightKind.Directional,true),
        InlineData(SceneLightKind.Point,false),InlineData(SceneLightKind.Point,true),
        InlineData(SceneLightKind.Spot,false),InlineData(SceneLightKind.Spot,true)]
    [Trait("Category","Gpu")]
    public void NativeLiveSignalIsNumericalAndSharesRemainingEndpointWithGi(SceneLightKind kind,bool gi)
    {
        using var session=new D3D12FrameSession();
        var input=Frame(-Vector3.UnitZ);
        var light=new NativeSceneLight(Guid.NewGuid(),"explicit live light",kind,new(2,0,0),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI/2),new(.8f,.6f,.4f),kind==SceneLightKind.Directional?.1f:.4f,10,CastShadows:false);
        var frame=new OriginalFrameScene(input.Surface,options:input.Options,lighting:new([light]));
        var options=gi?new RayGiOptions(LightingMode.SoftwareRayTracing,SamplesPerPixel:4,Seed:17,Composition:RayGiComposition.ToonPalette):null;
        var old=session.Capture(frame,W,H,options,surfaceStyles:Map(Live with {LightInfluence=0}));
        var actual=session.Capture(frame,W,H,options,surfaceStyles:Map(Live));
        var incoming=kind==SceneLightKind.Directional?-light.Direction:Vector3.UnitX;
        float attenuation=kind==SceneLightKind.Directional?1:(1-MathF.Pow(4f/(light.Range*light.Range),2))/4;
        var delta=C*light.Color*(Live.LightInfluence*light.Intensity*attenuation*Math.Max(Vector3.Dot(Normal(frame.Surface.Snapshot,217),incoming),0));
        AssertDelta(old,actual,delta);
        var strong=light with {Intensity=100};
        var bright=new OriginalFrameScene(input.Surface,options:input.Options,lighting:new([strong]));
        // Native light has already consumed every available PMX contrast channel.
        var brightOld=session.Capture(bright,W,H,options,surfaceStyles:Map(Live with {LightInfluence=0}));
        var brightLive=session.Capture(bright,W,H,options,surfaceStyles:Map(Live));
        AssertDelta(brightOld,brightLive,Vector3.Zero);
        AssertCoverage(old,actual);
    }

    [Fact,Trait("Category","Gpu")]
    public void NativeShadowAtlasOccludesLiveDiffuseWithoutChangingCoverage()
    {
        using var session=new D3D12FrameSession();
        var input=Frame(Vector3.UnitZ,normal:-Vector3.UnitZ,red:128,casterPosition:new(0,0,-1));
        var light=new NativeSceneLight(Guid.NewGuid(),"shadowed live",SceneLightKind.Directional,Vector3.Zero,
            Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI),Vector3.One,.2f,10);
        var frame=new OriginalFrameScene(input.Surface,options:input.Options,materials:input.Materials,lighting:new([light]));
        var old=session.Capture(frame,W,H,surfaceStyles:Map(Live with {LightInfluence=0},count:2));
        var actual=session.Capture(frame,W,H,surfaceStyles:Map(Live,count:2));
        AssertDelta(old,actual,Vector3.Zero);
        var lit=new OriginalFrameScene(input.Surface,options:input.Options,materials:input.Materials,
            lighting:new([light with {CastShadows=false}]));
        var litOld=session.Capture(lit,W,H,surfaceStyles:Map(Live with {LightInfluence=0},count:2));
        var litLive=session.Capture(lit,W,H,surfaceStyles:Map(Live,count:2));
        AssertDelta(litOld,litLive,C*(.8f*.2f*Math.Max(Vector3.Dot(Normal(input.Surface.Snapshot,128),-light.Direction),0)));
        Assert.True(litLive.Rgba[At]-litOld.Rgba[At]>4,"Fixture must receive visible native live light when the caster is disabled.");
        AssertCoverage(old,actual);
    }

    [Fact,Trait("Category","Gpu")]
    public void PrimaryGeometryShadowOccludesLiveMappedNormalSignal()
    {
        using var session=new D3D12FrameSession();
        var incoming=Vector3.Normalize(new Vector3(1,0,-.2f));
        var input=Frame(incoming,casterPosition:incoming*2);
        var projection=ModernDirectionalShadowProjection.Fit(input.Surface.Snapshot);
        var options=input.Options! with {SelfShadow=new(projection,256,256,true,false,OriginalTextureSampling.LinearClamp,OriginalDepthComparison.LessEqual)};
        var frame=new OriginalFrameScene(input.Surface,options:options,materials:input.Materials);
        var old=session.Capture(frame,W,H,surfaceStyles:Map(Live with {LightInfluence=0},count:2));
        var actual=session.Capture(frame,W,H,surfaceStyles:Map(Live,count:2));
        AssertDelta(old,actual,Vector3.Zero);
        var noCaster=new OriginalFrameScene(input.Surface,options:options,
            materials:[input.Materials[0],input.Materials[1] with {CastSelfShadow=false}]);
        var litOld=session.Capture(noCaster,W,H,surfaceStyles:Map(Live with {LightInfluence=0},count:2));
        var litLive=session.Capture(noCaster,W,H,surfaceStyles:Map(Live,count:2));
        AssertDelta(litOld,litLive,C*(.8f*Math.Max(Vector3.Dot(Normal(input.Surface.Snapshot,217),incoming),0)));
        Assert.True(litLive.Rgba[At]-litOld.Rgba[At]>4);
    }

    [Theory,InlineData(false),InlineData(true)]
    [Trait("Category","Gpu")]
    public void FragmentRangeUsesNumericalRedAlphaAndDirectionalQ(bool coupled)
    {
        using var session=new D3D12FrameSession();
        var frame=Frame(Vector3.UnitX);
        var baseline=Matcap with {CoupleEndfieldPalette=coupled,BaselineStrength=.4f};
        var palette=coupled?new D3D12EndfieldPaletteBinding(Vector3.Zero):null;
        var old=session.Capture(frame,W,H,surfaceStyles:Map(palette,baseline));
        foreach(float coefficient in new[]{-.1f,.15f}) foreach(Vector3 direction in new[]{Vector3.UnitX,-Vector3.UnitX})
        {
            var range=new D3D12EndfieldHighlightRangeBinding(.3f,.7f,new(direction,.4f,1));
            var binding=baseline with {HighlightCoefficient=coefficient,HighlightRange=range};
            var actual=session.Capture(frame,W,H,surfaceStyles:Map(palette,binding));
            float k=Math.Clamp(coefficient+Math.Clamp((64+32)/255f,0,1),0,1);
            float q=Q(Normal(frame.Surface.Snapshot,217),direction,.4f);
            float strength=.3f+(.7f*k)*Math.Clamp(q*k,0,1);
            AssertDelta(old,actual,C*(strength-.4f));
            AssertCoverage(old,actual);
        }
        Assert.Equal(old.Rgba,session.Capture(frame,W,H,surfaceStyles:Map(palette,baseline with {HighlightRange=null})).Rgba);
    }

    [Fact,Trait("Category","Gpu")]
    public void FragmentRangeReplacesOnlyBaselineBeforeExistingRimOverlayAndRange()
    {
        using var session=new D3D12FrameSession();
        var frame=Frame(Vector3.UnitX);
        var rim=new D3D12EndfieldRimBinding(C,new(.8f,.7f,.6f),EdgeSize:4,EdgeStrength:.16f,RangeStrength:.4f);
        var range=new D3D12EndfieldHighlightRangeBinding(.3f,.7f,new(Vector3.UnitX,.4f,1));
        D3D12SurfaceStyleMap Styles(bool enabled)=>new([D3D12SurfaceStyle.EndfieldPmx],
            [new(EndfieldMatcap:Matcap with {BaselineStrength=.4f,Palette=new(Vector3.Zero),HighlightRange=enabled?range:null},EndfieldRim:rim)]);
        var old=session.Capture(frame,W,H,surfaceStyles:Styles(false));
        var actual=session.Capture(frame,W,H,surfaceStyles:Styles(true));
        float k=(64+32)/255f,q=Q(Normal(frame.Surface.Snapshot,217),Vector3.UnitX,.4f);
        float strength=.3f+(.7f*k)*Math.Clamp(q*k,0,1);
        AssertDelta(old,actual,MatcapOracle.Overlay(C,rim.RimColor,.16f)*(strength-.4f));
        AssertCoverage(old,actual);
    }

    private const int At=(48*W+48)*4;
    private static float Q(Vector3 n,Vector3 direction,float width)
    {
        float maximum=1-(width-.5f),minimum=maximum*width;
        return Math.Clamp(((Vector3.Dot(n,direction)*.5f+.5f-minimum)/(maximum-minimum))*1.5f,0,1.5f);
    }
    private static Vector3 Normal(SceneSnapshot scene,byte red)
    {
        var corner=D3D12EndfieldMatcapGeometry.Build(scene,0,0).Corners[0];
        return MatcapOracle.PackedNormal(new(red/255f,128/255f),new(corner.WorldNormal.X,corner.WorldNormal.Y,corner.WorldNormal.Z),
            corner.Tangent,corner.UvParity.Z,true,1);
    }
    private static D3D12SurfaceStyleMap Map(D3D12EndfieldPaletteBinding? palette,D3D12EndfieldMatcapBinding? binding=null,int count=1)
        =>new(Enumerable.Repeat(D3D12SurfaceStyle.EndfieldPmx,count),Enumerable.Range(0,count)
            .Select(i=>new D3D12CharacterMaterialStyle(EndfieldMatcap:i==0?(binding??Matcap) with {Palette=palette}:null)));

    private static OriginalFrameScene Frame(Vector3 incoming,byte red=217,Vector3? normal=null,Vector3? casterPosition=null)
    {
        var vertices=new List<SceneVertex>(); var indices=new List<uint>();
        void Quad(Vector3 offset,float half) {
            uint start=(uint)vertices.Count;
            foreach(var p in new[]{new Vector2(-half,-half),new(half,-half),new(half,half),new(-half,half)})
                vertices.Add(new(offset+new Vector3(p,0),normal??Vector3.UnitZ,new((p.X/half+1)*.5f,(p.Y/half+1)*.5f)));
            indices.AddRange([start,start+2,start+1,start,start+3,start+2]);
        }
        Quad(Vector3.Zero,.8f);
        if(casterPosition is {} caster) Quad(caster,.6f);
        var materials=new List<SceneMaterial>{new("explicit receiver",0,6,new(.6f,.6f,.6f,1),Vector3.Zero,Vector3.Zero,1)};
        if(casterPosition is not null) materials.Add(new("invisible caster",6,6,Vector4.One,Vector3.Zero,Vector3.Zero,1));
        var scene=new SceneSnapshot(vertices.ToArray(),indices.ToArray(),materials,
            [new("numerical packed normal",1,1,[red,128,0,0]),new("numerical P",1,1,[64,0,0,32]),new("constant white MatCap",1,1,[255,255,255,255])],
            camera:new(new(0,0,-5),Vector3.Zero,Vector3.UnitY,2.6f,.1f,30),lightDirection:incoming);
        return new(new(scene,materials.Select(_=>new OriginalStyleMaterialBinding(new(.2f),DoubleSided:true)),
            new(Vector3.One,Vector3.Zero,Matrix4x4.Identity)),options:new(OriginalAlphaBlendMode.Normal,true,false,
                BackgroundColor:Vector4.Zero,AlphaOutput:FrameAlphaOutput.Straight),
            materials:materials.Select((_,i)=>new OriginalFrameMaterialFlags(i>0,i==0,false,SurfaceEnabled:i==0,ShadowDoubleSided:true)));
    }
    private static void AssertDelta(RenderedFrame old,RenderedFrame actual,Vector3 delta)
    {
        for(int c=0;c<3;c++) Assert.InRange(Math.Abs(actual.Rgba[At+c]-old.Rgba[At+c]-delta[c]*255),0,1.8f);
    }
    private static void AssertCoverage(RenderedFrame old,RenderedFrame actual)
    {
        for(int i=3;i<old.Rgba.Length;i+=4) Assert.Equal(old.Rgba[i],actual.Rgba[i]);
    }
}
