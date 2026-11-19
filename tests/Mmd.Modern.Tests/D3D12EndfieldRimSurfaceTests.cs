using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;
using static Mmd.Modern.Tests.D3D12EndfieldRimBindingTests;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12EndfieldRimSurfaceTests
{
    private const int W=97,H=97;
    private static readonly Vector3 Normal=new(-.7f,.7f,-.14f);
    private static readonly EndfieldRimSpec Rim=new(new(.12f,.07f,.035f),new(.8f,.7f,.6f),1.25f,.18f,0);

    public static IEnumerable<object[]> Variants()=>T8dTestHelpers.SurfaceVariants();
    [Theory,MemberData(nameof(Variants))]
    public void ExactMaskCompilesForEveryActualSelectedPixelEntryAndOptionalRadialHair(string entry,bool native,bool gi,bool morph,bool sub)
    {
        foreach(bool hair in new[]{false,true})
        {
            string moduleId=T8dTestHelpers.ResolveEndfieldModuleId(hair,rim:true,matcap:false,palette:false,baseImage:false);
            byte[] bytes=T8dTestHelpers.CompileEndfieldPixel(moduleId,entry,native,gi,morph,sub);
            Assert.Equal(new byte[]{68,88,66,67},bytes[..4]);
            string source=T8dTestHelpers.AssembleEndfieldPixelSource(moduleId,entry,native,gi,morph,sub);
            Assert.Contains("bool mdaFront : SV_IsFrontFace",source);
            Assert.Contains("register(b0, space2)",source);
            Assert.Equal(1,source.Split("color.rgb += EndfieldRimDelta(v, mdaFront, camRight, camUp, camForward, camBackward, isOrtho);",StringSplitOptions.None).Length-1);
            Assert.DoesNotContain("pow(1-",source);
            if(!hair)Assert.DoesNotContain("MDA_STYLE_GEOM_RAW_RADIAL_SEEDS",source);
        }
    }

    [Fact]
    public void IndependentPhysicalAndSourceAlgebraAgreeIncludingBackFaceTotalInternalReflection()
    {
        Assert.InRange(Fresnel(1,true),1d/9-1e-7,1d/9+1e-7);
        Assert.Equal(1,Fresnel(0,true));Assert.Equal(1,Fresnel(.5,false));
        foreach(bool front in new[]{false,true})foreach(double cosine in new[]{0d,.1,.5,.86,.87,.99,1})
        {
            double eta=front?1.9999998807907104:1/1.9999998807907104;
            double g=eta*eta-1+cosine*cosine;
            double source;
            if(g<=0)source=1;
            else
            {
                g=Math.Sqrt(g);
                double a=(g-cosine)/(g+cosine);
                double b=(cosine*(g+cosine)-1)/(cosine*(g-cosine)+1);
                source=.5*a*a*(1+b*b);
            }
            Assert.InRange(Math.Abs(Fresnel(cosine,front)-source),0,1e-12);
        }
        var signed=Rim with{EdgeSize=4,EdgeStrength=-2,RangeStrength=-1,RadialStrength=.4f};
        float scalar=Mask(Normal,Vector3.UnitZ,true,Matrix4x4.Identity,signed);
        Assert.InRange(scalar,-2,-1.99f);
        Vector3 delta=Delta(signed,scalar);
        // A negative scalar clamps only the Overlay factor; signed brightness survives.
        Assert.InRange(Vector3.Distance(delta,signed.BaseColor*scalar),0,1e-6f);
    }

    [Theory,InlineData(false),InlineData(true)]
    [Trait("Category","Gpu")]
    public void ActualFrontFaceMirroredGeometryCameraRollAndIncomingMatchIndependentOracle(bool perspective)
    {
        using var session=new D3D12FrameSession();
        int changed=0;
        foreach(bool reverse in new[]{false,true})
        foreach(var world in new[]{Matrix4x4.Identity,Matrix4x4.CreateScale(-1.1f,.9f,1.2f)*Matrix4x4.CreateRotationY(.12f)})
        foreach(float roll in new[]{0f,.65f})
        {
            var camera=new SceneCamera(new(.2f,.1f,-5),Vector3.Zero,new(MathF.Sin(roll),MathF.Cos(roll),0),2.6f,.1f,30,perspective?30:null);
            var frame=Frame(world,camera,reverse:reverse);
            var baseline=session.Capture(frame,W,H,surfaceStyles:Map(null));
            var actual=session.Capture(frame,W,H,surfaceStyles:Map(Rim));
            Matrix4x4.Invert(world,out var inverse);
            Vector3 normal=Vector3.Normalize(Vector3.TransformNormal(Normal,Matrix4x4.Transpose(inverse)));
            bool front=Front(frame.Surface.Snapshot);
            Matrix4x4 view=camera.LeftHanded?Matrix4x4.CreateLookAtLeftHanded(camera.Eye,camera.Target,camera.Up)
                :Matrix4x4.CreateLookAt(camera.Eye,camera.Target,camera.Up);
            for(int y=H/2-8;y<=H/2+8;y+=8)for(int x=W/2-8;x<=W/2+8;x+=8)
            {
                Vector3 point=WorldAtPixel(frame.Surface.Snapshot,x,y);
                Vector3 incoming=perspective?camera.Eye-point:camera.Eye-camera.Target;
                Vector3 delta=Delta(Rim,Mask(normal,incoming,front,view,Rim));
                AssertPixel(baseline,actual,x,y,delta);
                if(delta.LengthSquared()>.000001f)changed++;
            }
            AssertAlpha(baseline,actual);
        }
        Assert.True(changed>10,"Oracle must exercise a visible nonzero branch, not only Off.");
    }

    [Theory,InlineData(false,false),InlineData(false,true),InlineData(true,false),InlineData(true,true)]
    [Trait("Category","Gpu")]
    public void ExplicitPaletteDeltaAddsOnceAfterNativeAndGiWithoutChangingStraightCoverage(bool native,bool gi)
    {
        using var session=new D3D12FrameSession();
        var frame=Frame(Matrix4x4.Identity,Camera,alpha:.43f,straight:true,native:native);
        var options=gi?new RayGiOptions(LightingMode.SoftwareRayTracing,SamplesPerPixel:4,Seed:13,Composition:RayGiComposition.ToonPalette):null;
        var baseline=session.Capture(frame,W,H,options,surfaceStyles:Map(null));
        var actual=session.Capture(frame,W,H,options,surfaceStyles:Map(Rim));
        var view=Matrix4x4.CreateLookAt(Camera.Eye,Camera.Target,Camera.Up);
        Vector3 delta=Delta(Rim,Mask(Normal,Camera.Eye-Camera.Target,Front(frame.Surface.Snapshot),view,Rim));
        AssertPixel(baseline,actual,W/2,H/2,delta,2.1);
        AssertAlpha(baseline,actual);
        Assert.InRange(actual.Rgba[(H/2*W+W/2)*4+3],108,111);
    }

    [Fact,Trait("Category","Gpu")]
    public void OffRemovalRestoresExactFrameAndWarmParameterChangesDoNotGrowPipelinesOrReadback()
    {
        using var session=new D3D12FrameSession();var frame=Frame(Matrix4x4.Identity,Camera);
        var original=session.Capture(frame,W,H,surfaceStyles:Map(null));
        Assert.Equal(original.Rgba,session.Capture(frame,W,H,surfaceStyles:Map(Rim with{EdgeStrength=0})).Rgba);
        using(var image=session.Render(frame,W,H,surfaceStyles:Map(Rim))){}
        var warm=session.Statistics!;long reads=session.Runtime.Readbacks;
        for(int i=0;i<8;i++)
        {
            using var image=session.Render(frame,W,H,surfaceStyles:Map(Rim with{EdgeSize=.4f+i*.2f,DirectionRadians=i*.3f}));
            Assert.Equal(warm.ShaderCompilations,session.Statistics!.ShaderCompilations);
            Assert.Equal(warm.PipelineCreations,session.Statistics.PipelineCreations);
            Assert.InRange(session.Statistics.CachedResources,0,warm.CachedResources+1);
        }
        Assert.Equal(reads,session.Runtime.Readbacks);
        Assert.Equal(original.Rgba,session.Capture(frame,W,H,surfaceStyles:Map(null)).Rgba);
    }

    [Fact,Trait("Category","Gpu")]
    public void RasterFrontConventionMatchesCullingForMirrorsAndReversedTriangles()
    {
        using var session=new D3D12FrameSession();int frontCount=0;
        foreach(bool reverse in new[]{false,true})foreach(float sx in new[]{1f,-1f})
        {
            var both=Frame(Matrix4x4.CreateScale(sx,1,1),Camera,reverse:reverse);
            var culled=new OriginalFrameScene(new(both.Surface.Snapshot,
                [both.Surface.Materials[0] with{DoubleSided=false}],both.Surface.Profile),options:both.Options);
            var a=session.Capture(both,W,H,surfaceStyles:Map(Rim));
            var b=session.Capture(culled,W,H,surfaceStyles:Map(Rim));
            int offset=(H/2*W+W/2)*4;
            if(Front(both.Surface.Snapshot))
            {
                Assert.Equal(a.Rgba.AsSpan(offset,4).ToArray(),b.Rgba.AsSpan(offset,4).ToArray());frontCount++;
            }
            else Assert.Equal(0,b.Rgba[offset+3]);
        }
        Assert.Equal(2,frontCount);
    }

    [Fact,Trait("Category","Gpu")]
    public void OptionalRadialHairStillUsesItsOwnSeedAndTextureWhileRimDeltaIsIndependent()
    {
        using var session=new D3D12FrameSession();
        var ordinary=Frame(Matrix4x4.Identity,Camera);var old=ordinary.Surface.Snapshot;
        var scene=new SceneSnapshot(old.Vertices.ToArray(),old.Indices.ToArray(),old.Materials,
            [new SceneTexture("explicit green",1,1,[0,173,0,255])],camera:Camera,lightDirection:old.LightDirection,
            rawRadialSeeds:new(Enumerable.Repeat(Vector3.UnitY,old.Vertices.Length).ToArray()));
        var frame=new OriginalFrameScene(new(scene,ordinary.Surface.Materials,ordinary.Surface.Profile),options:ordinary.Options);
        var hair=T8dTestHelpers.EndfieldHair(0,new(.02f,.015f,.01f));
        D3D12SurfaceStyleMap Styles(bool rim)=>T8dTestHelpers.CreateDirectSurfaceStyleMap(frame,["endfield"],
            [new(D3D12CharacterMaterialRole.Hair)],[rim?[hair,Rim.ToBinding()]:[hair]]);
        var baseline=session.Capture(frame,W,H,surfaceStyles:Styles(false));
        var actual=session.Capture(frame,W,H,surfaceStyles:Styles(true));
        var view=Matrix4x4.CreateLookAt(Camera.Eye,Camera.Target,Camera.Up);
        AssertPixel(baseline,actual,W/2,H/2,Delta(Rim,Mask(Normal,Camera.Eye-Camera.Target,Front(scene),view,Rim)));
        AssertAlpha(baseline,actual);
    }

    [Theory,InlineData(-.3f),InlineData(1.7f)]
    [Trait("Category","Gpu")]
    public void SignedHdrOverlayUsesBothDestinationsRatherThanPlainAdditive(float strength)
    {
        using var session=new D3D12FrameSession();
        var frame=Frame(Matrix4x4.Identity,Camera,straight:true);
        var binding=Rim with{BaseColor=new(.07f,.6f,.1f),RimColor=new(.9f,.4f,2),EdgeSize=4,
            EdgeStrength=strength,RangeStrength=.7f,RadialStrength=-.2f};
        var baseline=session.Capture(frame,W,H,surfaceStyles:Map(null));
        var actual=session.Capture(frame,W,H,surfaceStyles:Map(binding));
        Vector3 delta=Delta(binding,strength);
        Assert.True(Vector3.Distance(delta,binding.RimColor*strength)>.02f);
        AssertPixel(baseline,actual,W/2,H/2,delta,2.1);AssertAlpha(baseline,actual);
    }

    private static readonly SceneCamera Camera=new(new(0,0,-5),Vector3.Zero,Vector3.UnitY,2.6f,.1f,30);
    internal static OriginalFrameScene Frame(Matrix4x4 world,SceneCamera camera,bool reverse=false,float alpha=1,bool straight=false,bool native=false)
    {
        Vector3[] points=[new(-.8f,-.8f,0),new(.8f,-.8f,0),new(.8f,.8f,0),new(-.8f,.8f,0)];
        var scene=new SceneSnapshot(points.Select(p=>new SceneVertex(p,Normal,new(1.4f,-.2f))).ToArray(),
            reverse?[0,1,2,0,2,3]:[0,2,1,0,3,2],
            [new("explicit rim receiver",0,6,new(.16f,.14f,.12f,alpha),new(.01f),Vector3.Zero,4)],[],
            world:world,camera:camera,lightDirection:-Vector3.UnitZ);
        return new(new(scene,[new(new(.2f),DoubleSided:true)],new(Vector3.One,Vector3.One,Matrix4x4.Identity,OriginalTextureSampling.PointClamp)),
            options:new(OriginalAlphaBlendMode.Normal,true,false,BackgroundColor:Vector4.Zero,
                AlphaOutput:straight?FrameAlphaOutput.Straight:FrameAlphaOutput.LegacyMax),
            lighting:native?new([new NativeSceneLight(Guid.NewGuid(),"fill",SceneLightKind.Directional,Vector3.Zero,
                Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI),Vector3.One,2,30,CastShadows:false)]):null);
    }

    private static bool Front(SceneSnapshot scene)
    {
        var vp=scene.World*scene.Camera.ViewProjection(W,H);var vertices=scene.Vertices;var indices=scene.Indices;
        Vector2 Project(SceneVertex v){var p=Vector4.Transform(new Vector4(v.Position,1),vp);return new(p.X/p.W,-p.Y/p.W);}
        Vector2 a=Project(vertices[(int)indices[0]]),b=Project(vertices[(int)indices[1]]),c=Project(vertices[(int)indices[2]]);
        return (b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X)<0;
    }
    private static Vector3 WorldAtPixel(SceneSnapshot scene,int x,int y)
    {
        Matrix4x4.Invert(scene.Camera.ViewProjection(W,H),out var invVp);Matrix4x4.Invert(scene.World,out var invWorld);
        Vector3 Point(float z){var q=Vector4.Transform(new Vector4(2*(x+.5f)/W-1,1-2*(y+.5f)/H,z,1),invVp);return Vector3.Transform(new(q.X/q.W,q.Y/q.W,q.Z/q.W),invWorld);}
        var a=Point(0);var b=Point(1);return Vector3.Transform(a+(b-a)*(-a.Z/(b.Z-a.Z)),scene.World);
    }
    private static float Mask(Vector3 normal,Vector3 incoming,bool front,Matrix4x4 view,EndfieldRimSpec binding)
    {
        Vector3 n=Vector3.Normalize(normal)*(front?1:-1),v=Vector3.Normalize(incoming);
        Vector3 nc=Vector3.TransformNormal(n,view);nc.Z=-nc.Z;
        double cosine=Math.Cos(binding.DirectionRadians),sine=Math.Sin(binding.DirectionRadians);
        double x=cosine*.5*nc.X-sine*.5*nc.Y-.5,y=sine*.5*nc.X+cosine*.5*nc.Y-.5;
        double radial=Math.Max(0.9999989867210388-Math.Sqrt(x*x+y*y+nc.Z*nc.Z),0);
        double g=Math.Clamp(radial*radial,0,1)*5;
        double h=(Math.Clamp(g,0,1)+binding.EdgeSize-0.9999998807907104)*Fresnel(Math.Clamp(Math.Abs(Vector3.Dot(v,n)),0,1),front);
        double u=Math.Clamp(h*10-1,0,1);
        return(float)(u*u*(3-2*u)*binding.EdgeStrength);
    }
    private static double Fresnel(double cosine,bool front)
    {
        double eta=front?1.9999998807907104:1/1.9999998807907104;
        double transmitted=(1-cosine*cosine)/(eta*eta);if(transmitted>=1)return 1;
        double ct=Math.Sqrt(1-transmitted),rs=(cosine-eta*ct)/(cosine+eta*ct),rp=(eta*cosine-ct)/(eta*cosine+ct);
        return(rs*rs+rp*rp)*.5;
    }
    private static Vector3 Delta(EndfieldRimSpec b,float scalar)
    {
        float f=Math.Clamp(scalar,0,1);var c=Vector3.Zero;
        for(int i=0;i<3;i++)
            c[i]=b.BaseColor[i]<.5f?b.BaseColor[i]*((1-f)+2*f*b.RimColor[i]):1-((1-f)+2*f*(1-b.RimColor[i]))*(1-b.BaseColor[i]);
        return c*(b.RangeStrength+b.RadialStrength+scalar)-b.BaseColor*(b.RangeStrength+b.RadialStrength);
    }
    private static void AssertPixel(RenderedFrame baseline,RenderedFrame actual,int x,int y,Vector3 delta,double tolerance=1.6)
    {
        int offset=(y*W+x)*4;
        for(int c=0;c<3;c++)Assert.InRange(Math.Abs(actual.Rgba[offset+c]-Math.Clamp(baseline.Rgba[offset+c]+delta[c]*255,0,255)),0,tolerance);
    }
    private static void AssertAlpha(RenderedFrame a,RenderedFrame b){for(int i=3;i<a.Rgba.Length;i+=4)Assert.Equal(a.Rgba[i],b.Rgba[i]);}
}
