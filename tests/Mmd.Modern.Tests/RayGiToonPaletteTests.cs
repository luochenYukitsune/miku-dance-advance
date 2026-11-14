using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class RayGiToonPaletteTests
{
    private delegate Vector3 Delta(Vector3 diffuseLit,Vector3 toon,float control,Vector3 incident,float strength,bool inside);
    private static Delta Evaluate=>typeof(RayGiOptions).Assembly.GetType("Mmd.Modern.Core.RayGiToonPaletteMath",throwOnError:true)!
        .GetMethod("EvaluateDelta")!.CreateDelegate<Delta>();
    private static RayGiOptions Read(string json)=>JsonSerializer.Deserialize<RayGiOptions>(json)!;

    [Fact]
    public void CompositionIsExplicitAndOldDefaultsRemainAdditive()
    {
        var property=typeof(RayGiOptions).GetProperty("Composition");
        Assert.NotNull(property);Assert.Equal("Additive",property.GetValue(new RayGiOptions())!.ToString());
        Assert.Equal("ToonPalette",property.GetValue(Read("{\"Composition\":1}"))!.ToString());
        Read("{\"Composition\":1}").Validate();
        Assert.Equal(0,new RayGiOptions().OcclusionStrength);
    }

    [Fact]
    public void InvalidCompositionIsRejectedEvenIfGiIsOff()
        =>Assert.Throws<ArgumentOutOfRangeException>(()=>Read("{\"Composition\":77}").Validate());

    [Fact]
    public void SaturatedLitEndpointAndNoIncidentCannotGainBrightness()
    {
        var evaluate=Evaluate;Vector3 d=new(1,.8f,.3f),toon=new(.4f,.6f,.8f),incident=new(.4f,.3f,.2f);
        Assert.Equal(Vector3.Zero,evaluate(d,toon,1,incident,.5f,true));
        Assert.Equal(Vector3.Zero,evaluate(d,Vector3.One,.3f,incident,.5f,true));
        Assert.Equal(Vector3.Zero,evaluate(d,toon,0,incident,0,true));
        Assert.Equal(Vector3.Zero,evaluate(d,toon,0,Vector3.Zero,100,true));
        Assert.Equal(Vector3.Zero,evaluate(d,toon,0,incident,.5f,false));
    }

    [Fact]
    public void UnitIncidentUsesOneDiffuseFactorAndNoAlbedoDivision()
    {
        var evaluate=Evaluate;Vector3 toon=new(.2f,.4f,.6f),incident=Vector3.One;
        Vector3 half=evaluate(new(.5f),toon,0,incident,.5f,true),full=evaluate(Vector3.One,toon,0,incident,.5f,true);
        Assert.Equal(full,half*2);
        Assert.Equal(.4f,full.X,6);Assert.Equal(.3f,full.Y,6);Assert.Equal(.2f,full.Z,6);
        Assert.Equal(Vector3.Zero,evaluate(Vector3.Zero,toon,0,new(float.MaxValue),100,true));
        Assert.Equal(new Vector3(0,.3f,0),evaluate(new(0,1,0),toon,0,incident,.5f,true));
    }

    [Fact]
    public void ValidRandomBudgetsNeverDarkenOrExceedTheirDiffuseLitEndpoint()
    {
        var evaluate=Evaluate;var random=new Random(24331);
        Vector3 Next()=>new(random.NextSingle(),random.NextSingle(),random.NextSingle());
        for(int i=0;i<2000;i++)
        {
            Vector3 d=Next(),toon=Next(),incident=Next()*4;float c=random.NextSingle();
            Vector3 delta=evaluate(d,toon,c,incident,random.NextSingle()*100,true);
            Vector3 remaining=d*(Vector3.One-(new Vector3(c)+toon*(1-c)));
            foreach(var value in new[]{delta.X,delta.Y,delta.Z})Assert.True(float.IsFinite(value)&&value>=0);
            Assert.True(delta.X<=remaining.X+1e-7f&&delta.Y<=remaining.Y+1e-7f&&delta.Z<=remaining.Z+1e-7f);
        }
    }

    [Fact]
    public void UnsupportedArtistDomainsAndNonFiniteIncidentFailExplicitly()
    {
        var evaluate=Evaluate;
        Assert.Throws<ArgumentOutOfRangeException>(()=>evaluate(new(-.01f,0,0),Vector3.Zero,0,Vector3.One,1,true));
        Assert.Throws<ArgumentOutOfRangeException>(()=>evaluate(Vector3.One,new(1.01f),0,Vector3.One,1,true));
        Assert.Throws<ArgumentOutOfRangeException>(()=>evaluate(Vector3.One,new(float.NaN),0,Vector3.One,1,true));
        Assert.Throws<ArgumentOutOfRangeException>(()=>evaluate(Vector3.One,Vector3.Zero,-.01f,Vector3.One,1,true));
        Assert.Throws<ArgumentOutOfRangeException>(()=>evaluate(Vector3.One,Vector3.Zero,0,new(float.PositiveInfinity),1,true));
        Assert.Throws<ArgumentOutOfRangeException>(()=>evaluate(Vector3.One,Vector3.Zero,0,new(-.1f),1,true));
    }

    [Fact]
    public void RendererRejectsUnsupportedToonDomainsWithoutRestrictingOldAdditive()
    {
        static OriginalFrameScene Frame(Vector3 toon,Vector3 ambient)
        {
            SceneVertex[] vertices=[new(new(-1,-1,0),-Vector3.UnitZ,Vector2.Zero),new(new(1,-1,0),-Vector3.UnitZ,Vector2.Zero),new(new(0,1,0),-Vector3.UnitZ,Vector2.Zero)];
            var snapshot=new SceneSnapshot(vertices,[0u,2u,1u],[new SceneMaterial("receiver",0,3,Vector4.One,Vector3.Zero,ambient,1)]);
            return new(new OriginalStyleScene(snapshot,[new(toon)]),options:new(OriginalAlphaBlendMode.Normal,true,false));
        }
        var additive=new RayGiOptions(LightingMode.SoftwareRayTracing);
        var palette=additive with {Composition=RayGiComposition.ToonPalette};
        foreach(var frame in new[]{Frame(new(1.01f),Vector3.Zero),Frame(Vector3.Zero,new(-2f))})
        {
            D3D12RayGiRenderer.Validate(frame,additive);
            Assert.Throws<NotSupportedException>(()=>D3D12RayGiRenderer.Validate(frame,palette));
        }
        var valid=Frame(new(.5f),Vector3.Zero);
        D3D12RayGiRenderer.Validate(valid,palette);
        Assert.Throws<NotSupportedException>(()=>D3D12RayGiRenderer.Validate(valid,palette with {DirectionalIrradiance=new(float.MaxValue)}));
        Assert.Throws<NotSupportedException>(()=>D3D12RayGiRenderer.Validate(valid,palette with {DirectionalLightDirection=new(float.MaxValue)}));
        D3D12RayGiRenderer.Validate(valid,additive with {DirectionalIrradiance=new(float.MaxValue),DirectionalLightDirection=new(float.MaxValue)});
    }

    [Fact]
    public void DiagnosticSemanticsFollowCompositionAndKeepTheFixedReferenceScale()
    {
        var statistics=new D3D12RayGiStatistics(LightingMode.SoftwareRayTracing,LightingMode.SoftwareRayTracing,1,1,0,0,4,17,.5f,129,129,4);
        Assert.Equal("ReceiverAlbedoTimesMeanIncidentRadiance",statistics.RgbSemantic);
        var palette=statistics with {Composition=RayGiComposition.ToonPalette};
        Assert.Equal("MeanIncidentRadiance",palette.RgbSemantic);Assert.Equal(1,palette.ReferenceRadiance);
        Assert.Contains("MeanIncidentRadiance",JsonSerializer.Serialize(palette));
    }

    [Fact]
    public void OffShaderSourceIsTheAcceptedUnmodifiedContract()
    {
        var source=(string)typeof(OriginalFrameShaders).GetField("Source",BindingFlags.Static|BindingFlags.NonPublic)!.GetRawConstantValue()!;
        // Runtime C# raw string has its indentation removed. The source-file literal, including
        // its outer newline/indentation, separately retains historical hash 5027460e... .
        Assert.Equal("afd46ca86380b2e4033737ec0f91cad29ed61eed7da58994a178a07f6b45dd37",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToonIncidentShaderHasDistinctMeaningAndAdditiveKeepsFrozenCompilerBytes(bool hardware)
    {
        string output=Environment.GetEnvironmentVariable("MMD_TOON_PALETTE_CPU_ARTIFACTS")
            ??Path.Combine(AppContext.BaseDirectory,"toon-palette-cpu-evidence");
        Directory.CreateDirectory(output);
        var compiler=new DxcShaderCompiler(cacheDirectory:Path.Combine(output,"dxc"));
        var defines=new Dictionary<string,string?>();if(hardware)defines.Add("GI_HARDWARE","1");
        var additive=compiler.CompileCompute(D3D12RayGiShaders.Source(hardware),"CSMain",hardware?"cs_6_5":"cs_6_0",defines);
        string stem=hardware?"hardware":"software";
        File.WriteAllBytes(Path.Combine(output,stem+"-additive.dxil"),additive.Dxil);
        string? baseline=Environment.GetEnvironmentVariable("MMD_TOON_PALETTE_CPU_BASELINE");
        if(baseline is not null)Assert.Equal(File.ReadAllBytes(Path.Combine(baseline,stem+"-additive.dxil")),additive.Dxil);
        defines.Add("GI_TOON","1");
        var palette=compiler.CompileCompute(D3D12RayGiShaders.Source(hardware),"CSMain",hardware?"cs_6_5":"cs_6_0",defines);
        File.WriteAllBytes(Path.Combine(output,stem+"-incident.dxil"),palette.Dxil);
        Assert.False(additive.Dxil.AsSpan().SequenceEqual(palette.Dxil),"Toon mode must compile a separate incident-radiance signal.");
        Assert.NotEmpty(OriginalFrameShaders.CompileGi("PSSurfaceGi",false));
    }
}
