using System.Numerics;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class RayGiDenoiserContractTests
{
    private static RayGiOptions Read(string json)=>JsonSerializer.Deserialize<RayGiOptions>(json)!;

    [Fact]
    public void DenoiserIsExplicitAndExistingDefaultsRemainOff()
    {
        var property=typeof(RayGiOptions).GetProperty("Denoiser");
        Assert.NotNull(property);
        Assert.Equal("Off",property.GetValue(new RayGiOptions())!.ToString());
        Assert.Equal("Spatial",property.GetValue(Read("{\"Composition\":1,\"Denoiser\":1}"))!.ToString());
        Read("{\"Composition\":1,\"Denoiser\":1}").Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void UnknownDenoiserIsRejectedInEveryMode(int mode)
        =>Assert.Throws<ArgumentOutOfRangeException>(()=>Read($$"""{"Mode":{{mode}},"Denoiser":77}""").Validate());

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SpatialRequiresToonPaletteEvenWhenGiIsOff(int mode)
        =>Assert.Throws<ArgumentException>(()=>Read($$"""{"Mode":{{mode}},"Denoiser":1}""").Validate());

    [Fact]
    public void ExistingStatisticsMeanOneGiDispatchAndNoDenoising()
    {
        var stats=new D3D12RayGiStatistics(LightingMode.SoftwareRayTracing,LightingMode.SoftwareRayTracing,1,1,0,0,4,17,.5f,129,129,4);
        var denoiser=typeof(D3D12RayGiStatistics).GetProperty("Denoiser");
        var count=typeof(D3D12RayGiStatistics).GetProperty("DenoiseDispatchCount");
        Assert.NotNull(denoiser);Assert.NotNull(count);
        Assert.Equal("Off",denoiser.GetValue(stats)!.ToString());
        Assert.Equal(0,count.GetValue(stats));Assert.Equal(1,stats.DispatchCount);
    }

    [Theory]
    [InlineData(LightingMode.Legacy,1,RayGiDenoiser.Spatial,false)]
    [InlineData(LightingMode.SoftwareRayTracing,0,RayGiDenoiser.Spatial,false)]
    [InlineData(LightingMode.HardwareRayTracing,0,RayGiDenoiser.Spatial,false)]
    [InlineData(LightingMode.SoftwareRayTracing,1,RayGiDenoiser.Off,false)]
    [InlineData(LightingMode.HardwareRayTracing,1,RayGiDenoiser.Off,false)]
    [InlineData(LightingMode.SoftwareRayTracing,1,RayGiDenoiser.Spatial,true)]
    [InlineData(LightingMode.HardwareRayTracing,1,RayGiDenoiser.Spatial,true)]
    public void FilteringRequiresEnabledGiPositiveStrengthAndSpatial(LightingMode mode,float strength,RayGiDenoiser filter,bool expected)
    {
        var options=new RayGiOptions(mode,Strength:strength,Composition:RayGiComposition.ToonPalette,Denoiser:filter);
        options.Validate();Assert.Equal(expected,D3D12RayGiDenoiser.IsEnabled(options));
    }

    [Fact]
    public void FootprintComesFromTheSelectedOrthographicCameraAndOutputHeight()
    {
        var camera=new SceneCamera(new(3,4,-5),new(3,4,9),Vector3.UnitY,6,.1f,100);
        Assert.Equal(new Vector4(0,0,1,.02f),D3D12RayGiDenoiser.CameraGuide(camera,300));
        Assert.Equal(.01f,D3D12RayGiDenoiser.CameraGuide(camera,600).W);
        Assert.Equal(.04f,D3D12RayGiDenoiser.CameraGuide(camera with {OrthographicHeight=12},300).W);
        Assert.Equal(new Vector4(0,0,-1,.02f),D3D12RayGiDenoiser.CameraGuide(camera with {Eye=camera.Target,Target=camera.Eye},300));
        var huge=camera with {Eye=new(0,0,-float.MaxValue),Target=new(0,0,float.MaxValue)};
        Assert.Equal(new Vector4(0,0,1,.02f),D3D12RayGiDenoiser.CameraGuide(huge,300));
        Assert.Equal(default,D3D12RayGiDenoiser.CameraGuide(camera,0));
        Assert.Equal(default,D3D12RayGiDenoiser.CameraGuide(camera with {Target=camera.Eye},300));
        Assert.Equal(default,D3D12RayGiDenoiser.CameraGuide(camera with {OrthographicHeight=float.NaN},300));
    }

    [Fact]
    public void SpatialShaderCompilesAsSoftwareCompatibleSm6WithoutGpuExecution()
    {
        string output=Environment.GetEnvironmentVariable("MMD_DENOISE_CPU_ARTIFACTS")??Path.Combine(AppContext.BaseDirectory,"denoise-cpu");
        var compiler=new DxcShaderCompiler(cacheDirectory:Environment.GetEnvironmentVariable("MMD_DXC_CACHE")??Path.Combine(AppContext.BaseDirectory,"denoise-dxc"));
        var result=compiler.CompileCompute(D3D12RayGiDenoiser.Source,"CSMain","cs_6_0");
        Assert.NotEmpty(result.Dxil);Assert.Equal("cs_6_0",result.Profile);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output,"spatial-compile.json"),JsonSerializer.Serialize(new
        {result.SourceHash,result.OutputHash,result.CompilerHash,result.CompilerVersion,result.Diagnostics,result.Profile},new JsonSerializerOptions{WriteIndented=true}));
    }
}
