using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12ImmutableGeometryGpuTests
{
    [Fact]
    public void SharedTopologyUploadsOnceAndReplacementKeepsBothCommandListReadersAlive()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var scene=D3D12ImmutableGeometryIdentityTests.Scene();var changed=D3D12ImmutableGeometryIdentityTests.Scene(reverse:true);
        runtime.Begin();
        var original=cache.IndexBuffer(scene);
        Assert.Same(original,cache.IndexBuffer(scene.WithDeformedVertices(scene.Vertices.ToArray())));
        Assert.Equal(1,runtime.BufferUploads);
        var replacement=cache.IndexBuffer(changed);Assert.NotSame(original,replacement);
        Assert.Equal(2,runtime.BufferUploads);Assert.Equal(1,cache.LiveResourceCount);
        using var firstReadback=Copy(runtime,original,12,ResourceStates.IndexBuffer);
        using var secondReadback=Copy(runtime,replacement,12,ResourceStates.IndexBuffer);
        runtime.Wait(runtime.Submit());
        Assert.Equal(new uint[]{0,1,2},Read<uint>(firstReadback,12));
        Assert.Equal(new uint[]{0,2,1},Read<uint>(secondReadback,12));
        for(int frame=0;frame<8;frame++)
        {
            runtime.Begin();Assert.Same(replacement,cache.IndexBuffer(changed.WithCamera(changed.Camera)));
            runtime.Submit();Assert.Equal(1,cache.LiveResourceCount);Assert.Equal(2,runtime.BufferUploads);
        }
        runtime.WaitForIdle();
    }

    [Fact]
    public void DeferredVertexFactoryChangesOnlyForNewPoseAndPreviousGpuPayloadSurvivesReplacement()
    {
        using var runtime=new D3D12GpuRuntime();using var cache=new D3D12GpuCache(runtime);
        var scene=D3D12ImmutableGeometryIdentityTests.Scene();var values=scene.Vertices.ToArray();values[0]=values[0] with{Position=new Vector3(9,8,7)};
        var pose=scene.WithDeformedVertices(values);int assembled=0;
        ID3D12Resource Buffer(SceneSnapshot source)=>cache.BufferByIdentity("test-vertices",ResourceStates.VertexAndConstantBuffer,
            new D3D12VertexStreamIdentity(source),()=>{assembled++;return source.Vertices.ToArray();});
        runtime.Begin();var first=Buffer(scene);
        Assert.Same(first,Buffer(scene.WithLighting(Vector3.UnitX)));Assert.Equal(1,assembled);
        var second=Buffer(pose);Assert.NotSame(first,second);Assert.Equal(2,assembled);Assert.Equal(1,cache.LiveResourceCount);
        using var oldReadback=Copy(runtime,first,96,ResourceStates.VertexAndConstantBuffer);
        using var newReadback=Copy(runtime,second,96,ResourceStates.VertexAndConstantBuffer);
        runtime.Wait(runtime.Submit());
        Assert.Equal(scene.Vertices.ToArray(),Read<SceneVertex>(oldReadback,96));
        Assert.Equal(pose.Vertices.ToArray(),Read<SceneVertex>(newReadback,96));
        runtime.Begin();Assert.Same(second,Buffer(pose.WithCamera(pose.Camera)));runtime.Wait(runtime.Submit());
        Assert.Equal(2,assembled);Assert.Equal(2,runtime.BufferUploads);Assert.Equal(1,cache.LiveResourceCount);
        runtime.WaitForIdle();
    }

    private static ID3D12Resource Copy(D3D12GpuRuntime runtime,ID3D12Resource source,int bytes,ResourceStates state)
    {
        var readback=runtime.Device.CreateCommittedResource(HeapType.Readback,ResourceDescription.Buffer((ulong)bytes),ResourceStates.CopyDest);
        runtime.Commands.ResourceBarrierTransition(source,state,ResourceStates.CopySource);
        runtime.Commands.CopyBufferRegion(readback,0,source,0,(ulong)bytes);
        runtime.Commands.ResourceBarrierTransition(source,ResourceStates.CopySource,state);return readback;
    }
    private static T[] Read<T>(ID3D12Resource resource,int bytes) where T:unmanaged
    {
        var values=MemoryMarshal.Cast<byte,T>(resource.Map<byte>(0,bytes)).ToArray();resource.Unmap(0);return values;
    }
}
