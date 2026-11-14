using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12EndfieldHairSurfaceTests
{
    private const int W=97,H=97;
    private static readonly Vector3 Palette=new(.12f,.07f,.035f);
    private static readonly Vector2 Uv=new(1.23f,-.58f);
    private static readonly byte[] Mask=[255,18,0,0, 0,239,255,255, 37,91,43,13, 127,178,91,213];

    public static IEnumerable<object[]> Variants()
    {
        foreach(var variant in D3D12GenshinSurfaceTests.SurfaceVariants())yield return variant;
    }
    [Theory,MemberData(nameof(Variants))]
    public void AllPixelAndVertexVariantsCompileWithOptionalIndependentSideStream(string entry,bool native,bool gi,bool morph,bool sub)
    {
        foreach(bool vertex in new[]{false,true})
        {
            var bytes=OriginalFrameShaders.CompileEndfieldHairSurface(vertex?"VSSurface":entry,vertex,native,gi,morph,sub);
            Assert.Equal(new byte[]{68,88,66,67},bytes[..4]);
        }
        string source=OriginalFrameShaders.EndfieldHairSurfaceSource(native,gi,morph,sub);
        Assert.Contains("float3 RawSeed:RAW_RADIAL",source);
        Assert.Contains("o.RawSeed=mul(float4(v.RawSeed,0),World).xyz",source);
        Assert.Contains("register(b7)",source);Assert.Contains("register(t12)",source);Assert.Contains("register(s5)",source);
        Assert.Contains("SampleLevel(EndfieldHairSampler,v.Uv,0).g",source);
        Assert.Contains("color.rgb+=EndfieldRadialIncrement(v);",source);
        Assert.Equal(1,source.Split("color.rgb+=EndfieldRadialIncrement(v);",StringSplitOptions.None).Length-1);
        Assert.DoesNotContain("SpecularPower.rgb*p",source);
        Assert.Equal(32,Marshal.SizeOf<SceneVertex>());
    }

    [Theory,InlineData(false),InlineData(true)]
    [Trait("Category","Gpu")]
    public void InterpolatedRestSeedCurrentNormalAndIncomingMatchIndependentRayPlaneOracle(bool perspective)
    {
        using var session=new D3D12FrameSession();
        foreach(var world in new[]{Matrix4x4.Identity,
            Matrix4x4.CreateScale(1.2f,.8f,1.4f)*Matrix4x4.CreateRotationY(.22f)*Matrix4x4.CreateTranslation(.1f,-.08f,.2f),
            Matrix4x4.CreateScale(-1.1f,1.2f,.9f)*Matrix4x4.CreateRotationX(-.18f)})
        {
            var frame=Frame(world,perspective,vary:true);
            var zero=session.Capture(frame,W,H,surfaceStyles:Map(new(0,Vector3.Zero,.4f)));
            var actual=session.Capture(frame,W,H,surfaceStyles:Map(new(0,Palette,.4f)));
            int tested=0;float min=float.MaxValue,max=0;
            for(int y=H/2-12;y<=H/2+12;y+=8)for(int x=W/2-12;x<=W/2+12;x+=8)
            {
                var local=LocalAtPixel(frame.Surface.Snapshot,x,y);
                Assert.InRange(local.X,-.75f,.75f);Assert.InRange(local.Y,-.75f,.75f);
                var n=Normal(local,true);var seed=Seed(local,true);
                float scalar=Oracle(frame.Surface.Snapshot,local,n,seed,.4f)*LinearGreen(Mask,Uv);
                AssertPixel(zero,actual,x,y,Palette*scalar);tested++;min=Math.Min(min,scalar);max=Math.Max(max,scalar);
            }
            Assert.Equal(16,tested);
            if(world==Matrix4x4.Identity)Assert.True(max-min>.002f,"Varying attributes must exercise a nonconstant radial response.");
            AssertAlpha(zero,actual);
        }
    }

    [Theory,InlineData(-3f),InlineData(0f),InlineData(7f)]
    [Trait("Category","Gpu")]
    public void GreenNumericMaskSignedBrightnessAndHdrPaletteAreIndependentOfSpecularAndCoverage(float brightness)
    {
        using var session=new D3D12FrameSession();
        foreach(byte green in new byte[]{0,113,255})
        {
            // Isolate HDR accumulation from the UNorm target's source/blend
            // clamp boundary. Signed/ordinary cases separately retain alpha .6.
            float alpha=brightness==7?1:.6f;
            var frame=Frame(Matrix4x4.Identity,false,mask:new byte[]{255,green,71,0},alpha:alpha);
            var palette=brightness==7?new Vector3(2,.15f,.05f):Palette;
            var zero=session.Capture(frame,W,H,surfaceStyles:Map(new(0,Vector3.Zero,brightness)));
            var actual=session.Capture(frame,W,H,surfaceStyles:Map(new(0,palette,brightness)));
            var p=LocalAtPixel(frame.Surface.Snapshot,W/2,H/2);
            float scalar=Oracle(frame.Surface.Snapshot,p,Normal(p,false),Seed(p,false),brightness)*(green/255f);
            AssertPixel(zero,actual,W/2,H/2,palette*scalar*alpha);
            AssertAlpha(zero,actual);
            if(green==0)Assert.Equal(zero.Rgba,actual.Rgba);
        }
    }

    [Fact,Trait("Category","Gpu")]
    public void DegenerateSeedContributesZeroAndRemovalRestoresOriginalWithBoundedWarmCache()
    {
        using var session=new D3D12FrameSession();
        var empty=Frame(Matrix4x4.Identity,false,zeroSeed:true);
        Assert.Equal(session.Capture(empty,W,H,surfaceStyles:Map(null)).Rgba,
            session.Capture(empty,W,H,surfaceStyles:Map(new(0,Palette))).Rgba);
        var frame=Frame(Matrix4x4.Identity,false);
        var ordinary=session.Capture(frame,W,H,surfaceStyles:Map(null));
        session.Render(frame,W,H,surfaceStyles:Map(new(0,Palette)));
        var warm=session.Statistics!;long reads=session.Runtime.Readbacks;
        for(int i=0;i<8;i++)
        {
            session.Render(frame,W,H,surfaceStyles:Map(new(0,Palette,.1f*i)));
            Assert.Equal(warm.PipelineCreations,session.Statistics!.PipelineCreations);
            Assert.Equal(warm.ShaderCompilations,session.Statistics.ShaderCompilations);
            Assert.InRange(session.Statistics.CachedResources,0,warm.CachedResources+2);
        }
        Assert.Equal(reads,session.Runtime.Readbacks);
        Assert.Equal(ordinary.Rgba,session.Capture(frame,W,H,surfaceStyles:Map(null)).Rgba);
    }

    [Theory,InlineData(false,false),InlineData(true,false),InlineData(false,true),InlineData(true,true)]
    [Trait("Category","Gpu")]
    public void RadialPaletteAddsOnceOutsideDiffuseBudgetAcrossNativeAndGi(bool native,bool gi)
    {
        using var session=new D3D12FrameSession();
        var frame=Frame(Matrix4x4.Identity,false,native:native);
        var options=gi?new RayGiOptions(LightingMode.SoftwareRayTracing,SamplesPerPixel:4,Seed:19,Composition:RayGiComposition.ToonPalette):null;
        var zero=session.Capture(frame,W,H,options,surfaceStyles:Map(new(0,Vector3.Zero)));
        var actual=session.Capture(frame,W,H,options,surfaceStyles:Map(new(0,Palette)));
        var p=LocalAtPixel(frame.Surface.Snapshot,W/2,H/2);
        AssertPixel(zero,actual,W/2,H/2,Palette*(Oracle(frame.Surface.Snapshot,p,Normal(p,false),Seed(p,false),0)*LinearGreen(Mask,Uv)));
        AssertAlpha(zero,actual);
    }

    internal static D3D12SurfaceStyleMap Map(D3D12EndfieldHairBinding? binding)
        =>new([D3D12SurfaceStyle.EndfieldPmx],[new(D3D12CharacterMaterialRole.Hair,EndfieldHair:binding)]);
    private static Vector3 Normal(Vector3 p,bool vary)=>new(.15f+(vary?.11f*p.X:0),.045f+(vary?.02f*p.Y:0),-1);
    private static Vector3 Seed(Vector3 p,bool vary)=>new(vary?.12f*p.Y:0,1+(vary?.15f*p.X:0),vary?.07f*p.X:0);
    private static OriginalFrameScene Frame(Matrix4x4 world,bool perspective,bool vary=false,byte[]? mask=null,float alpha=1,bool zeroSeed=false,bool native=false)
    {
        Vector3[] positions=[new(-.8f,-.8f,0),new(.8f,-.8f,0),new(.8f,.8f,0),new(-.8f,.8f,0)];
        var vertices=positions.Select(p=>new SceneVertex(p,Normal(p,vary),Uv)).ToArray();
        var center=Vector3.Transform(Vector3.Zero,world);
        var scene=new SceneSnapshot(vertices,[0,2,1,0,3,2],
            [new("explicit hair",0,6,new(.16f,.14f,.12f,alpha),Vector3.Zero,Vector3.Zero,4)],
            [new SceneTexture("numeric green",mask is null?2:1,mask is null?2:1,mask??Mask)],world:world,
            camera:new(center+new Vector3(.14f,.04f,-5),center,Vector3.UnitY,2.6f,.1f,30,perspective?30:null),lightDirection:-Vector3.UnitZ,
            rawRadialSeeds:new(positions.Select(p=>zeroSeed?Vector3.Zero:Seed(p,vary)).ToArray()));
        return new(new(scene,[new(new(.2f),DoubleSided:true)],new(Vector3.One,Vector3.One,Matrix4x4.Identity,OriginalTextureSampling.PointClamp)),
            options:new(OriginalAlphaBlendMode.Normal,true,false,BackgroundColor:Vector4.Zero),
            lighting:native?new([new NativeSceneLight(Guid.NewGuid(),"fill",SceneLightKind.Directional,Vector3.Zero,
                Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI),Vector3.One,2,30,CastShadows:false)]):null);
    }
    private static Vector3 LocalAtPixel(SceneSnapshot scene,int x,int y)
    {
        Matrix4x4.Invert(scene.Camera.ViewProjection(W,H),out var invVp);Matrix4x4.Invert(scene.World,out var invWorld);
        Vector3 Point(float z){var q=Vector4.Transform(new Vector4(2*(x+.5f)/W-1,1-2*(y+.5f)/H,z,1),invVp);return Vector3.Transform(new(q.X/q.W,q.Y/q.W,q.Z/q.W),invWorld);}
        var a=Point(0);var b=Point(1);return a+(b-a)*(-a.Z/(b.Z-a.Z));
    }
    private static float Oracle(SceneSnapshot scene,Vector3 local,Vector3 normal,Vector3 seed,float brightness)
    {
        Matrix4x4.Invert(scene.World,out var inverse);
        var n=Vector3.Normalize(Vector3.TransformNormal(normal,Matrix4x4.Transpose(inverse)));
        var raw=Vector3.TransformNormal(seed,scene.World);
        var cross=Vector3.Cross(raw,n);if(cross.LengthSquared()<1e-20)return 0;
        var tangent=Vector3.Cross(n,Vector3.Normalize(cross));
        var b=Vector3.Normalize(Vector3.Cross(tangent,n));
        var incoming=scene.Camera.VerticalFovDegrees is null?scene.Camera.Eye-scene.Camera.Target:scene.Camera.Eye-Vector3.Transform(local,scene.World);
        var u=Vector3.Normalize(Vector3.Normalize(incoming)+new Vector3(0,0,.5000999569892883f));
        double q=Math.Clamp((Math.Abs(Vector3.Dot(u,b))-.10000000149011612)/(0-.10000000149011612),0,1);
        return (float)(Math.Min(q,.75)*(brightness+1.5001001358032227));
    }
    private static float LinearGreen(byte[] pixels,Vector2 uv)
    {
        double x=uv.X*2-.5,y=uv.Y*2-.5;int ix=(int)Math.Floor(x),iy=(int)Math.Floor(y);double fx=x-ix,fy=y-iy,sum=0;
        for(int j=0;j<2;j++)for(int i=0;i<2;i++)sum+=pixels[((((iy+j)%2+2)%2)*2+((ix+i)%2+2)%2)*4+1]/255d*(i==0?1-fx:fx)*(j==0?1-fy:fy);
        return(float)sum;
    }
    private static void AssertPixel(RenderedFrame baseline,RenderedFrame actual,int x,int y,Vector3 increment)
    {
        int offset=(y*W+x)*4;
        for(int c=0;c<3;c++)
        {
            double expected=Math.Clamp(baseline.Rgba[offset+c]+increment[c]*255,0,255);
            Assert.InRange(Math.Abs(actual.Rgba[offset+c]-expected),0,1.6);
        }
    }
    private static void AssertAlpha(RenderedFrame a,RenderedFrame b){for(int i=3;i<a.Rgba.Length;i+=4)Assert.Equal(a.Rgba[i],b.Rgba[i]);}
}
