using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class RayGiCommonTests
{
    [Fact]
    public void GBufferRtvHandlesRemainDistinctDespiteMutableVorticeOffset()
    {
        Vortice.Direct3D12.CpuDescriptorHandle first=default;
        var handles=D3D12RayGiRenderer.GBufferHandles(first,32);
        Assert.Equal(first,handles.Position);Assert.NotEqual(handles.Position,handles.Normal);
        Assert.Equal(first.Offset(1,32),handles.Normal);
    }
    [Fact]
    public void OptionsDefaultOffAndRejectInvalidSampling()
    {
        var options=new RayGiOptions();
        Assert.Equal(LightingMode.Legacy,options.Mode);
        options.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(()=>(options with {SamplesPerPixel=0}).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(()=>(options with {DirectionalLightDirection=Vector3.Zero}).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(()=>(options with {Strength=float.NaN}).Validate());
    }

    [Fact]
    public void SharedGpuConstantsHaveFrozenLayout()
    {
        Assert.Equal(160,Marshal.SizeOf<RayGiConstants>());
        Assert.Equal(0,Marshal.OffsetOf<RayGiConstants>(nameof(RayGiConstants.Dispatch)).ToInt32());
        Assert.Equal(32,Marshal.OffsetOf<RayGiConstants>(nameof(RayGiConstants.LightDirectionDistance)).ToInt32());
        Assert.Equal(80,Marshal.OffsetOf<RayGiConstants>(nameof(RayGiConstants.Reserved)).ToInt32());
        Assert.Equal(96,Marshal.OffsetOf<RayGiConstants>(nameof(RayGiConstants.Camera)).ToInt32());
        var value = new RayGiConstants(
            new(257, 191, 8, 17),
            new(2, 2, 3, 0),
            new(1, 2, 3, 50),
            new(4, 5, 6, 8),
            new(.001f, 1, 0, 0),
            default,
            Matrix4x4.Identity);
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1));
        Assert.Equal(257u, BitConverter.ToUInt32(bytes[..4]));
        Assert.Equal(191u, BitConverter.ToUInt32(bytes.Slice(4, 4)));
        Assert.Equal(8u, BitConverter.ToUInt32(bytes.Slice(8, 4)));
        Assert.Equal(17u, BitConverter.ToUInt32(bytes.Slice(12, 4)));
        Assert.Equal(2u, BitConverter.ToUInt32(bytes.Slice(16, 4)));
    }

    [Fact]
    public void SharedIntegratorTracesGeometryAndSecondaryLightVisibility()
    {
        Assert.Contains("TraceClosest(ray,hit)",D3D12RayGiShaders.Integrator);
        Assert.Contains("TraceAny(shadow)",D3D12RayGiShaders.Integrator);
        Assert.Contains("AcceptCandidate",D3D12RayGiShaders.Shared);
        Assert.Contains("GiGBufferNormal.Load",D3D12RayGiShaders.Integrator);
    }

    [Theory]
    [InlineData(false,false)]
    [InlineData(true,true)]
    public void UnsupportedFrameStateFailsExplicitlyWhileOffRemainsAllowed(bool depth,bool transp)
    {
        var baseline=D3D12RayGiRasterTests.Fixture();
        var frame=new OriginalFrameScene(baseline.Surface,options:baseline.Options! with {WriteDepth=depth,Transp=transp});
        D3D12RayGiRenderer.Validate(frame,new());
        Assert.Throws<NotSupportedException>(()=>D3D12RayGiRenderer.Validate(frame,new(LightingMode.SoftwareRayTracing)));
    }
}
