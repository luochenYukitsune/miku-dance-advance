using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category","Gpu")]
[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12GlobalLightingTests
{
    internal static OriginalFrameScene WithSettings(OriginalFrameScene frame,SceneLightingSettings settings) => new(frame.Surface,frame.Outline,
        frame.OutlineOptions,frame.Options,frame.Materials,(frame.Lighting??SceneLightingSnapshot.Empty).WithSettings(settings),frame.EditorOverlay);

    [Theory]
    [InlineData(D3D12SurfaceStyle.Mmd)]
    [InlineData(D3D12SurfaceStyle.GenshinPmx)]
    [InlineData(D3D12SurfaceStyle.StarRailPmx)]
    [InlineData(D3D12SurfaceStyle.WutheringPmx)]
    [InlineData(D3D12SurfaceStyle.EndfieldPmx)]
    public void EnvironmentWorksWithoutAnyLampAndNeutralSettingsRestoreExactPixels(D3D12SurfaceStyle style)
    {
        using var session=new D3D12FrameSession();var original=D3D12NativeLightTests.Fixture();
        var source=new OriginalFrameScene(new(original.Surface.Snapshot.WithLighting(Vector3.UnitZ),original.Surface.Materials,original.Surface.Profile),
            options:original.Options,materials:original.Materials);
        var map=new D3D12SurfaceStyleMap([style,D3D12SurfaceStyle.Mmd]);
        var baseline=session.Capture(source,129,97,surfaceStyles:map);
        Assert.Equal(baseline.Rgba,session.Capture(WithSettings(source,new()),129,97,surfaceStyles:map).Rgba);
        var fill=session.Capture(WithSettings(source,new(.2f)),129,97,surfaceStyles:map);
        Assert.NotEqual(baseline.Rgba,fill.Rgba);Assert.Equal(0,session.NativeLightStatistics!.EnabledLights);Assert.Equal(0,session.NativeLightStatistics.ShadowFaces);
        Assert.Equal(fill.Rgba,session.Capture(WithSettings(source,new(.2f)),129,97,surfaceStyles:map).Rgba);
        Assert.Equal(baseline.Rgba,session.Capture(source,129,97,surfaceStyles:map).Rgba);
    }

    [Fact]
    public void ExposureMatchesLinearSrgbOracleAndKeepsStraightAlphaExactly()
    {
        using var session=new D3D12FrameSession();var source=D3D12NativeLightTests.Fixture();
        foreach(bool straight in new[]{false,true})
        {
            var frame=new OriginalFrameScene(source.Surface,options:source.Options! with {AlphaOutput=straight?FrameAlphaOutput.Straight:FrameAlphaOutput.LegacyMax,
                BackgroundColor=straight?Vector4.Zero:new(.17f,.23f,.31f,1)},materials:source.Materials);
            var baseline=session.Capture(frame,129,97);
            foreach(float ev in new[]{-2f,1f})
            {
                var adjusted=session.Capture(WithSettings(frame,new(0,ev)),129,97);
                Assert.NotEqual(baseline.Rgba,adjusted.Rgba);
                for(int i=0;i<adjusted.Rgba.Length;i++)
                    if(i%4==3)Assert.Equal(baseline.Rgba[i],adjusted.Rgba[i]);
                    else Assert.InRange(Math.Abs(Oracle(baseline.Rgba[i],ev)-adjusted.Rgba[i]),0,1);
            }
            Assert.Equal(baseline.Rgba,session.Capture(WithSettings(frame,new()),129,97).Rgba);
        }
        static int Oracle(byte value,float ev)
        {
            double c=value/255.0;double linear=c<=.04045?c/12.92:Math.Pow((c+.055)/1.055,2.4);
            linear*=Math.Pow(2,ev);double display=linear<=.0031308?linear*12.92:1.055*Math.Pow(linear,1/2.4)-.055;
            return (int)Math.Round(Math.Clamp(display,0,1)*255,MidpointRounding.ToEven);
        }
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void EnvironmentIlluminatesSecondaryHitsAndChangingItResetsGiHistory(LightingMode mode)
    {
        using var session=new D3D12FrameSession();var cap=session.QueryRayGiCapabilities();
        Assert.True(mode==LightingMode.HardwareRayTracing?cap.HardwareAvailable:cap.SoftwareAvailable,cap.UnavailableReason);
        var source=NativeLightGiContractTests.Fixture([]);
        var gi=NativeLightGiContractTests.Gi(mode) with {Denoiser=RayGiDenoiser.TemporalSpatial};
        var dark=WithSettings(source,new());var filled=WithSettings(source,new(.2f));
        var first=session.Capture(dark,79,59,rayGiOptions:gi);session.Capture(dark,79,59,rayGiOptions:gi);
        var changed=session.Capture(filled,79,59,rayGiOptions:gi);
        Assert.Equal("LightingChanged",session.RayGiStatistics!.TemporalResetReason);Assert.NotEqual(first.Rgba,changed.Rgba);
        using var fresh=new D3D12FrameSession();Assert.Equal(changed.Rgba,fresh.Capture(filled,79,59,rayGiOptions:gi).Rgba);
    }
}
