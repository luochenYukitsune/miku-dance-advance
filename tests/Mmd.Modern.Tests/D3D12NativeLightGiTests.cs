using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class NativeLightGiContractTests
{
    [Fact]
    public void OptionalLightTableKeepsExistingAbisAndFiltersInactiveInputs()
    {
        Assert.Equal(80,Marshal.SizeOf<NativeRayGiLight>());
        Assert.Equal(96,Marshal.SizeOf<RayGiMaterialGpu>());
        var light=Light(SceneLightKind.Point);
        var frame=Fixture([light,light with {Id=Guid.NewGuid(),Enabled=false}]);
        var table=NativeRayGiLights.From(frame);
        Assert.Single(table.Values);
        Assert.Same(table,NativeRayGiLights.From(frame));
        Assert.Equal(new Vector4(light.Position,1),table.Values[0].PositionKind);
        Assert.Equal(new Vector4(-Vector3.UnitZ,light.Range),table.Values[0].DirectionRange);
        Assert.Equal(0,table.Values[0].ConeShadow.Z);
        Assert.Empty(NativeRayGiLights.From(Fixture([light with {Color=Vector3.Zero}])).Values);
        foreach(bool hardware in new[]{false,true})
        foreach(bool morph in new[]{false,true})
        {
            Assert.Equal(D3D12RayGiShaders.Source(hardware,morph),D3D12RayGiShaders.Source(hardware,morph,false));
            Assert.DoesNotContain("GiNativeLights",D3D12RayGiShaders.Source(hardware,morph));
            Assert.Contains("register(t9)",D3D12RayGiShaders.Source(hardware,morph,true));
            Assert.Contains("outgoing+=GiNativeOutgoing",D3D12RayGiShaders.Source(hardware,morph,true));
        }
    }

    [Fact]
    public void NewNativeRadianceRejectsFloatOverflowBeforeGpuSubmission()
    {
        var frame=Fixture([Light(SceneLightKind.Point) with {Intensity=float.MaxValue}]);
        Assert.Throws<NotSupportedException>(()=>D3D12RayGiRenderer.Validate(frame,Gi(LightingMode.SoftwareRayTracing)));
        D3D12RayGiRenderer.Validate(frame,RayGiOptions.Off);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1e-30f)]
    public void DirectScaleCannotHideUnscaledColorOrNearLightOverflow(float directScale)
    {
        var options=Gi(LightingMode.SoftwareRayTracing) with {DirectScale=directScale};
        // The colour-first shader operation itself overflows, even before attenuation.
        var product=Fixture([Light(SceneLightKind.Point) with {Color=new(2),Intensity=float.MaxValue}]);
        Assert.Throws<NotSupportedException>(()=>D3D12RayGiRenderer.Validate(product,options));
        // A finite authored intensity can still overflow once it is normalized at an aim near the range end.
        var attenuation=Fixture([Light(SceneLightKind.Point) with {Color=Vector3.One,Intensity=float.MaxValue/2,AimDistance=30}]);
        Assert.Throws<NotSupportedException>(()=>D3D12RayGiRenderer.Validate(attenuation,options));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1e-30f)]
    public void DirectScaleCannotHideUnscaledMultiLightAccumulation(float directScale)
    {
        var lights=Enumerable.Range(0,SceneLightingSnapshot.MaxActiveLights).Select(_=>
            Light(SceneLightKind.Directional) with {Color=Vector3.One,Intensity=float.MaxValue*.9f}).ToArray();
        var single=Fixture([lights[0]]);
        var options=Gi(LightingMode.SoftwareRayTracing) with {DirectScale=directScale};
        Assert.True(NativeRayGiLights.IrradianceBound(single)<float.MaxValue);
        D3D12RayGiRenderer.Validate(single,options);
        var combined=Fixture(lights);
        // Multiple aligned lights can overflow even after the shader's Lambert 1/pi factor.
        Assert.True((double)lights[0].Intensity*lights.Length/Math.PI>float.MaxValue);
        Assert.True(NativeRayGiLights.IrradianceBound(combined)>float.MaxValue);
        Assert.Throws<NotSupportedException>(()=>D3D12RayGiRenderer.Validate(combined,options));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1e-30f)]
    public void FiniteTinyColorLargeIntensityRemainsAcceptedAtSmallDirectScales(float directScale)
    {
        var frame=Fixture([Light(SceneLightKind.Point) with {Color=new(1e-20f),Intensity=1e38f}]);
        Assert.True(NativeRayGiLights.IrradianceBound(frame)<float.MaxValue);
        D3D12RayGiRenderer.Validate(frame,Gi(LightingMode.SoftwareRayTracing) with {DirectScale=directScale});
    }

    [Fact]
    public void TinyColorLargeIntensityUsesFiniteColorFirstGrouping()
    {
        var frame=Fixture([Light(SceneLightKind.Point) with {Color=new(1e-20f),Intensity=1e38f}]);
        Assert.True(NativeRayGiLights.IrradianceBound(frame)<float.MaxValue);
        D3D12RayGiRenderer.Validate(frame,Gi(LightingMode.SoftwareRayTracing));
        Assert.Contains("precise float3 irradiance=light.ColorIntensity.rgb*light.ColorIntensity.w;",D3D12RayGiShaders.Source(false,false,true));
        Assert.Contains("incident+=irradiance*angularRadiance;",D3D12RayGiShaders.Source(true,false,true));
    }

    // Authored in the original I / d^2 units and converted, keeping the accepted secondary energy scale.
    internal static NativeSceneLight Light(SceneLightKind kind)=>SceneLightFalloff.FromLegacy(new(Guid.NewGuid(),"secondary red",kind,
        new(0,0,2),Quaternion.Identity,new(1,0,0),kind==SceneLightKind.Directional?2:150,30,
        InnerConeRadians:.55f,OuterConeRadians:.7f,CastShadows:false));
    internal static OriginalFrameScene Fixture(IEnumerable<NativeSceneLight> lights)
    {
        var original=D3D12RayGiTests.Bounce();var old=original.Surface.Snapshot;
        var materials=old.Materials.ToArray();
        materials[0]=materials[0] with {Diffuse=new(.5f,.5f,.5f,1)};
        materials[1]=materials[1] with {Diffuse=Vector4.One};
        var mesh=old.WithMaterials(materials);
        var bindings=original.Surface.Materials.ToArray();bindings[0]=bindings[0] with {ToonShadowColor=new(.2f)};
        return new(new(mesh,bindings,original.Surface.Profile),options:original.Options,materials:original.Materials,lighting:new(lights));
    }
    internal static RayGiOptions Gi(LightingMode mode)=>new(mode,Strength:1,SamplesPerPixel:64,Seed:17,
        MaxRayDistance:50,RayEpsilon:.001f,DirectionalIrradiance:Vector3.Zero,Composition:RayGiComposition.ToonPalette);
}

[Trait("Category","Gpu")]
public sealed class D3D12NativeLightGiTests
{
    private const int Width=79,Height=59;
    private static RayGiOptions Options(LightingMode mode)=>NativeLightGiContractTests.Gi(mode);
    private static NativeSceneLight Light(SceneLightKind kind)=>NativeLightGiContractTests.Light(kind);
    private static OriginalFrameScene Frame(params NativeSceneLight[] lights)=>NativeLightGiContractTests.Fixture(lights);

    [Theory]
    [InlineData(SceneLightKind.Directional,LightingMode.SoftwareRayTracing)]
    [InlineData(SceneLightKind.Directional,LightingMode.HardwareRayTracing)]
    [InlineData(SceneLightKind.Point,LightingMode.SoftwareRayTracing)]
    [InlineData(SceneLightKind.Point,LightingMode.HardwareRayTracing)]
    [InlineData(SceneLightKind.Spot,LightingMode.SoftwareRayTracing)]
    [InlineData(SceneLightKind.Spot,LightingMode.HardwareRayTracing)]
    public void NativeLightsIlluminateSecondaryWallWhenReceiverCannotReceiveTheirDirectLight(SceneLightKind kind,LightingMode mode)
    {
        using var session=new D3D12FrameSession();Require(session,mode);
        var light=Light(kind);var frame=Frame(light);var gi=Options(mode);
        var direct=session.Capture(frame,Width,Height);
        var unlit=session.Capture(Frame(),Width,Height,rayGiOptions:gi);
        // Receiver normal points -Z, while all three lights arrive from +Z: no primary direct term.
        Assert.Equal(Center(direct),Center(unlit));
        var bounced=session.Capture(frame,Width,Height,rayGiOptions:gi);
        Assert.Equal(1,session.RayGiStatistics!.SecondaryNativeLights);
        Assert.True(session.NativeLightStatistics!.NativeLightsContributeToGi);
        Assert.True(Center(bounced)[0]>Center(direct)[0]+5,"Only secondary-hit native lighting can produce this red bounce.");
        Assert.InRange(Math.Abs(Center(bounced)[1]-Center(direct)[1]),0,1);
        Assert.InRange(Math.Abs(Center(bounced)[2]-Center(direct)[2]),0,1);
        var disabled=session.Capture(Frame(light with {Enabled=false}),Width,Height,rayGiOptions:gi);
        Assert.Equal(unlit.Rgba,disabled.Rgba);
        Assert.Equal(0,session.RayGiStatistics!.SecondaryNativeLights);
        Assert.Equal(bounced.Rgba,session.Capture(frame,Width,Height,rayGiOptions:gi).Rgba);
        Assert.Equal(direct.Rgba,session.Capture(frame,Width,Height).Rgba);
        Assert.False(session.NativeLightStatistics!.NativeLightsContributeToGi);
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void PointOcclusionRangeAndSpotDirectionChangeActualSecondaryEnergy(LightingMode mode)
    {
        using var session=new D3D12FrameSession();Require(session,mode);var gi=Options(mode);
        var point=Light(SceneLightKind.Point);var lit=session.Capture(Frame(point),Width,Height,rayGiOptions:gi);
        // The receiver really occludes rays from parts of the bounce wall to the point light.
        var shadow=session.Capture(Frame(point with {CastShadows=true}),Width,Height,rayGiOptions:gi);
        Assert.True(Center(shadow)[0]<Center(lit)[0]);
        var noEnergy=session.Capture(Frame(),Width,Height,rayGiOptions:gi);
        var distant=session.Capture(Frame(point with {Position=new(0,0,100)}),Width,Height,rayGiOptions:gi);
        Assert.Equal(Center(noEnergy),Center(distant));
        var spot=Light(SceneLightKind.Spot);
        var inside=session.Capture(Frame(spot),Width,Height,rayGiOptions:gi);
        var outside=session.Capture(Frame(spot with {Rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI)}),Width,Height,rayGiOptions:gi);
        Assert.True(Center(inside)[0]>Center(outside)[0]+5);
        Assert.Equal(Center(noEnergy),Center(outside));
    }

    [Fact]
    public void SoftwareAndDxrAgreeAndRepeatedLightEditsRetireThroughFrameFences()
    {
        using var session=new D3D12FrameSession();Require(session,LightingMode.HardwareRayTracing);
        var light=Light(SceneLightKind.Point);var frame=Frame(light);
        var soft=session.Capture(frame,Width,Height,rayGiOptions:Options(LightingMode.SoftwareRayTracing));
        var hard=session.Capture(frame,Width,Height,rayGiOptions:Options(LightingMode.HardwareRayTracing));
        Assert.InRange(soft.Rgba.Zip(hard.Rgba,(a,b)=>Math.Abs(a-b)).Max(),0,1);
        int resources=0;
        for(int i=0;i<24;i++)
        {
            var edited=Frame(light with {Color=i%2==0?new(1,0,0):new(0,0,1),Intensity=120+i});
            using var output=session.Render(edited,Width+(i%2)*2,Height,rayGiOptions:Options(LightingMode.HardwareRayTracing));
            if(i==0)resources=output.Statistics.CachedResources;
            Assert.Equal(0,output.Statistics.ReadbackCount);
            Assert.InRange(output.Statistics.CachedResources,0,resources+2);
            Assert.InRange(output.Statistics.PendingFrames,0,3);
        }
        Assert.Throws<OperationCanceledException>(()=>session.RenderForPresent(frame,Width,Height,
            Options(LightingMode.HardwareRayTracing),(_,_)=>throw new OperationCanceledException()));
        Assert.Equal(hard.Rgba,session.Capture(frame,Width,Height,rayGiOptions:Options(LightingMode.HardwareRayTracing)).Rgba);
        var blue=session.Capture(Frame(light with {Color=new(0,0,1)}),Width,Height,rayGiOptions:Options(LightingMode.HardwareRayTracing));
        Assert.True(Center(hard)[0]>Center(blue)[0]+5);
        Assert.True(Center(blue)[2]>Center(hard)[2]+5);
        session.WaitForIdle();
    }

    private static byte[] Center(RenderedFrame frame)=>frame.Rgba.AsSpan(((frame.Height/2)*frame.Width+frame.Width/2)*4,4).ToArray();
    private static void Require(D3D12FrameSession session,LightingMode mode)
    {
        var support=session.QueryRayGiCapabilities();
        Assert.True(mode==LightingMode.HardwareRayTracing?support.HardwareAvailable:support.SoftwareAvailable,support.UnavailableReason??"Required GPU GI provider unavailable.");
    }
}
