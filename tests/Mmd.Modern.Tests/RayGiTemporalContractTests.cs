using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class RayGiTemporalContractTests
{
    [Fact]
    public void TemporalIsOptInAndRequiresIncidentRadiance()
    {
        Assert.Equal(RayGiDenoiser.Off,new RayGiOptions().Denoiser);
        Assert.Equal(0,(int)RayGiDenoiser.Off);Assert.Equal(1,(int)RayGiDenoiser.Spatial);
        Assert.Throws<ArgumentException>(()=>new RayGiOptions(Denoiser:RayGiDenoiser.TemporalSpatial).Validate());
        var options=new RayGiOptions(LightingMode.SoftwareRayTracing,Composition:RayGiComposition.ToonPalette,Denoiser:RayGiDenoiser.TemporalSpatial);
        options.Validate();Assert.True(D3D12RayGiRenderer.IsTemporalEnabled(options));
        Assert.False(D3D12RayGiRenderer.IsTemporalEnabled(options with {Strength=0}));
        Assert.False(D3D12RayGiRenderer.IsTemporalEnabled(options with {Mode=LightingMode.Legacy}));
        Assert.False(D3D12RayGiRenderer.IsTemporalEnabled(null));
        Assert.True(D3D12RayGiDenoiser.IsEnabled(options));
    }

    [Fact]
    public void SoftwareCompatibleTemporalShaderCompilesWithoutGpuExecution()
    {
        var result=new DxcShaderCompiler().CompileCompute(D3D12RayGiTemporalPass.Source,"CSMain","cs_6_0");
        Assert.NotEmpty(result.Dxil);Assert.Equal("cs_6_0",result.Profile);
    }
}
