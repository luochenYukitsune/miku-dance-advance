using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class NativeAreaLightContractTests
{
    [Fact]
    public void RectangularSamplesFollowFullOrientationAndShareThePointLightAtlasBudget()
    {
        var area = D3D12NativeLightTests.Light(SceneLightKind.Area) with { AreaSize = new(4, 2),
            Rotation = Quaternion.CreateFromYawPitchRoll(.7f, .2f, .4f) };
        var samples = Enumerable.Range(0, 4).Select(i => NativeAreaLight.SamplePosition(area, i)).ToArray();
        Assert.True(Vector3.Distance(area.Position, samples.Aggregate(Vector3.Zero, (a,b) => a+b) / 4) < .000001f);
        foreach(var sample in samples) Assert.InRange(MathF.Abs(Vector3.Dot(sample-area.Position, area.Direction)), 0, .000001f);
        Assert.InRange(MathF.Abs(Vector3.Distance(samples[0],samples[1])-4/MathF.Sqrt(3)), 0, .000001f);
        Assert.Equal(24, NativeLightShadowProjection.Create(D3D12NativeLightTests.Fixture().Surface.Snapshot, area).Length);
        var lights = new SceneLightingSnapshot(Enumerable.Range(0,32).Select(_ => area with { Id=Guid.NewGuid() }));
        var layout = NativeLightShadowAtlasLayout.Create(lights);
        Assert.Equal(192, layout.FaceCount); Assert.Equal(7168, layout.Width); Assert.Equal(7168, layout.Height);
        var row = Assert.Single(NativeRayGiLights.From(D3D12NativeLightTests.WithLight(D3D12NativeLightTests.Fixture(),[area])).Values);
        Assert.Equal(new Vector4(2,1,1,0),row.ConeShadow);
        Assert.Equal(new Vector4(NativeAreaLight.Right(area),0),row.AreaRightTile);
    }
}

[Trait("Category","Gpu")]
[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12AreaLightTests
{
    private static NativeSceneLight Area => D3D12NativeLightTests.Light(SceneLightKind.Area);
    [Theory]
    [InlineData(D3D12SurfaceStyle.Mmd)]
    [InlineData(D3D12SurfaceStyle.GenshinPmx)]
    [InlineData(D3D12SurfaceStyle.StarRailPmx)]
    [InlineData(D3D12SurfaceStyle.WutheringPmx)]
    [InlineData(D3D12SurfaceStyle.EndfieldPmx)]
    public void RectangleIsOneSidedAndItsDimensionsAndRollChangeTheIllumination(D3D12SurfaceStyle style)
    {
        using var session = new D3D12FrameSession(); var f = D3D12NativeLightTests.Fixture();
        // Keep the receiver away from its toon ceiling, leaving room for additional light.
        var mesh=f.Surface.Snapshot; mesh=new(mesh.Vertices.ToArray(),mesh.Indices,mesh.Materials,mesh.Textures,mesh.World,mesh.Camera,Vector3.UnitZ);
        f=new(new(mesh,f.Surface.Materials,f.Surface.Profile),options:f.Options,materials:f.Materials);
        var map=new D3D12SurfaceStyleMap([style,D3D12SurfaceStyle.Mmd]);
        var baseline=session.Capture(f,129,97,surfaceStyles:map);
        // A sub-saturating fill leaves room in every style's deliberate palette budget;
        // clipping the full remaining budget would erase any emitter-shape differences.
        var area=Area with {CastShadows=false,AreaSize=new(3,.4f),Intensity=.5f};
        RenderedFrame Capture(NativeSceneLight light)=>session.Capture(D3D12NativeLightTests.WithLight(f,[light]),129,97,surfaceStyles:map);
        var lit=Capture(area); Assert.NotEqual(baseline.Rgba,lit.Rgba);
        Assert.Equal(baseline.Rgba,Capture(area with {Rotation=Quaternion.Identity}).Rgba);
        Assert.NotEqual(lit.Rgba,Capture(area with {AreaSize=new(.4f,.4f)}).Rgba);
        Assert.NotEqual(lit.Rgba,Capture(area with {Rotation=Quaternion.Normalize(area.Rotation*Quaternion.CreateFromAxisAngle(Vector3.UnitZ,MathF.PI/2))}).Rgba);
        Assert.Equal(lit.Rgba,Capture(area).Rgba);
    }

    [Fact]
    public void FourSampleVisibilityProducesSizeDependentPenumbraAndHonorsAlphaCutouts()
    {
        using var session=new D3D12FrameSession();
        var area=Area with {AreaSize=new(.05f,.05f)};
        RenderedFrame Capture(NativeSceneLight light,bool alpha=false)=>session.Capture(D3D12NativeLightTests.WithLight(D3D12NativeLightTests.Fixture(alpha),[light]),129,97);
        var hard=Capture(area); Assert.Equal(24,session.NativeLightStatistics!.ShadowFaces);
        area=area with {AreaSize=new(2,2)}; var soft=Capture(area);
        var lit=Capture(area with {CastShadows=false}); Assert.NotEqual(hard.Rgba,soft.Rgba); Assert.NotEqual(soft.Rgba,lit.Rgba);
        Assert.Equal(lit.Rgba,Capture(area,true).Rgba);
        Assert.Equal(soft.Rgba,Capture(area).Rgba);
        D3D12OriginalFrameTests.Save("area-small-shadow",hard); D3D12OriginalFrameTests.Save("area-large-shadow",soft);
    }

    [Fact]
    public void ThirtyTwoShadowedAreasAndMixedAtlasOffsetsKeepTheLastLightCorrect()
    {
        using var session=new D3D12FrameSession(); var area=Area;
        var f=D3D12NativeLightTests.Fixture();
        RenderedFrame Capture(IEnumerable<NativeSceneLight> lights)=>session.Capture(D3D12NativeLightTests.WithLight(f,lights),129,97);
        var single=Capture([area]);
        var lights=Enumerable.Range(0,32).Select(i=>area with {Id=Guid.NewGuid(),Intensity=i==31?area.Intensity:1e-30f}).ToArray();
        Assert.Equal(single.Rgba,Capture(lights).Rgba); Assert.Equal(768,session.NativeLightStatistics!.ShadowFaces);
        lights=lights.Select((l,i)=>i==31?l:l with {Kind=(SceneLightKind)(i%3)}).ToArray();
        Assert.Equal(single.Rgba,Capture(lights).Rgba);
        Assert.Equal(single.Rgba,Capture([area]).Rgba);
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void RectangleContributesToSecondaryGiAndTheBackFaceDoesNot(LightingMode mode)
    {
        using var session=new D3D12FrameSession(); var cap=session.QueryRayGiCapabilities();
        Assert.True(mode==LightingMode.SoftwareRayTracing?cap.SoftwareAvailable:cap.HardwareAvailable,cap.UnavailableReason);
        var area=NativeLightGiContractTests.Light(SceneLightKind.Point) with {Kind=SceneLightKind.Area,AreaSize=new(2,2)};
        RenderedFrame Capture(NativeSceneLight? light)=>session.Capture(NativeLightGiContractTests.Fixture(light is null?[]:[light]),79,59,rayGiOptions:NativeLightGiContractTests.Gi(mode));
        var unlit=Capture(null); var lit=Capture(area);
        Assert.NotEqual(unlit.Rgba,lit.Rgba); Assert.Equal(1,session.RayGiStatistics!.SecondaryNativeLights);
        Assert.Equal(unlit.Rgba,Capture(area with {Rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI)}).Rgba);
        Assert.Equal(lit.Rgba,Capture(area).Rgba);
    }
}
