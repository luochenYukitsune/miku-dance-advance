using System.Numerics;
using System.Text.Json;
using Mmd.Modern.App;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using System.Runtime.InteropServices;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class RayGiNearOcclusionTests
{
    private static RayGiOptions Options(string json)=>JsonSerializer.Deserialize<RayGiOptions>(json)!;
    private static Vector3 Share(SceneMaterial material,OriginalStyleProfile profile)=>OriginalStyleMath.ResolveAmbientShare(material,profile);
    private static SceneMaterial Material(Vector3 ambient,Vector3 diffuse)=>new("receiver",0,3,new(diffuse,1),Vector3.Zero,ambient,1);

    [Fact]
    public void CoreDefaultLeavesExistingGiUnoccludedAndAllowsShortTransportDistances()
    {
        var options=new RayGiOptions();
        Assert.Equal(0,options.OcclusionStrength);
        Assert.Equal(1,options.OcclusionRadius);
        (options with {MaxRayDistance=.01f,RayEpsilon=.001f}).Validate();
        Assert.Equal(.01f,(options with {MaxRayDistance=.01f,RayEpsilon=.001f}).EffectiveOcclusionRadius);
        (options with {MaxRayDistance=100,RayEpsilon=2}).Validate();
        Assert.Throws<ArgumentOutOfRangeException>(()=>(options with {MaxRayDistance=100,RayEpsilon=2,OcclusionStrength=1}).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(()=>(options with {OcclusionStrength=float.NaN}).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(()=>(options with {OcclusionRadius=float.PositiveInfinity}).Validate());
    }

    [Theory]
    [InlineData("{\"OcclusionStrength\":-0.01}")]
    [InlineData("{\"OcclusionStrength\":1.01}")]
    [InlineData("{\"OcclusionRadius\":0}")]
    [InlineData("{\"OcclusionRadius\":0.001,\"OcclusionStrength\":1}")]
    public void InvalidNearOcclusionOptionsAreRejected(string json)
        =>Assert.Throws<ArgumentOutOfRangeException>(()=>Options(json).Validate());

    [Fact]
    public void AmbientShareUsesUnsaturatedHostInputsThenAllocatesClampedEg()
    {
        var material=Material(new(.6f,.2f,0),new(1,.5f,0));
        var profile=new OriginalStyleProfile(new(.602f),Vector3.One,Matrix4x4.Identity);
        var share=Share(material,profile);
        Assert.Equal(.6f/1.202f,share.X,6);
        Assert.Equal(.2f/.501f,share.Y,6);
        Assert.Equal(0,share.Z);
        var eg=OriginalStyleMath.ResolveHostColors(material,profile).EgColor;
        Assert.Equal(.6f/1.202f,eg.X*share.X,6);
        Assert.Equal(.2f,eg.Y*share.Y,6);
        Assert.Equal(Vector3.Zero,Share(Material(Vector3.Zero,Vector3.Zero),profile));
        Assert.Equal(Vector3.One,Share(Material(new(.6f),Vector3.Zero),profile));
        Assert.Equal(Vector3.Zero,Share(Material(Vector3.Zero,Vector3.One),profile));
    }

    [Fact]
    public void PreviewStartsOffAndNearOcclusionRequiresAnExplicitChoice()
    {
        using var vm=new FrameViewportViewModel(new());
        Assert.False(vm.GiEnabled);
        Assert.Equal(.5f,vm.RayGiOptions.Strength);
        Assert.Equal(16,vm.RayGiOptions.SamplesPerPixel);
        Assert.Equal(0,vm.RayGiOptions.OcclusionStrength);
        Assert.False(vm.NearOcclusionEnabled);
        vm.NearOcclusionEnabled=true;
        Assert.Equal(1,vm.RayGiOptions.OcclusionStrength);
        vm.NearOcclusionEnabled=false;
        Assert.Equal(0,vm.RayGiOptions.OcclusionStrength);
        Assert.Equal(0,FrameViewportOptions.Parse(["--frame-viewer"]).RayGi!.OcclusionStrength);
        Assert.Equal(0,FrameViewportOptions.Parse(["--frame-viewer","--gi-occlusion","off"]).RayGi!.OcclusionStrength);
        Assert.Equal(1,FrameViewportOptions.Parse(["--frame-viewer","--gi-occlusion","on"]).RayGi!.OcclusionStrength);
        Assert.Throws<ArgumentException>(()=>FrameViewportOptions.Parse(["--frame-viewer","--gi-occlusion","invalid"]));
    }

    [Fact]
    public void SurfaceConstantsUseTwoAlignedRegisters()
    {
        Assert.Equal(32,Marshal.SizeOf<D3D12OriginalStyleRenderer.GiSurfaceConstants>());
        var value=new D3D12OriginalStyleRenderer.GiSurfaceConstants(1,129,.5f,.75f,new(.2f,.3f,.4f,0));
        var bytes=MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value,1));
        Assert.Equal(.75f,BitConverter.ToSingle(bytes.Slice(12,4)));
        Assert.Equal(.2f,BitConverter.ToSingle(bytes.Slice(16,4)));
        Assert.Equal(.4f,BitConverter.ToSingle(bytes.Slice(24,4)));
    }

    [Fact]
    public void AmbientAllocationIsBoundedForNegativeZeroAndExtremeFiniteArtistInputs()
    {
        var profile=new OriginalStyleProfile(new(.602f),Vector3.One,Matrix4x4.Identity);
        Assert.Equal(new Vector3(1,0,0),Share(Material(new(.5f,-.5f,0),new(-.5f,1,0)),profile));
        var maximum=new OriginalStyleProfile(new(float.MaxValue),Vector3.One,Matrix4x4.Identity);
        var extreme=Share(Material(new(float.MaxValue),new(float.MaxValue)),maximum);
        Assert.All(new[]{extreme.X,extreme.Y,extreme.Z},value=>Assert.True(float.IsFinite(value)&&value>=0&&value<=1));
        Assert.True(extreme.X>0);
        Assert.Equal(Vector3.One,Share(Material(new(float.MaxValue),Vector3.Zero),maximum));
    }
}
