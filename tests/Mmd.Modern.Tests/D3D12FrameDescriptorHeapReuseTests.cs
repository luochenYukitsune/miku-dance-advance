using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12FrameDescriptorHeapReuseTests
{
    [Fact]
    public void FenceRetiredSlotsReuseAllThreeHeapsWithoutAdditionalCreations()
    {
        using var runtime = new D3D12GpuRuntime();
        using var pool = new D3D12FrameDescriptorHeaps(runtime);
        var prior = new D3D12FrameDescriptorHeaps.Set?[3];
        for (int frame = 0; frame < 12; frame++)
        {
            runtime.Begin();
            var heaps = pool.Get(17);
            if (frame < 3) prior[runtime.SlotIndex] = heaps;
            else Assert.Same(prior[runtime.SlotIndex], heaps);
            Assert.Equal(17, heaps.SrvCapacity);
            runtime.Submit();
            Assert.Equal(Math.Min(frame + 1, 3) * 3, pool.LiveHeapCount);
            Assert.Equal(Math.Min(frame + 1, 3) * 3, pool.HeapCreations);
        }
        Assert.Equal(3, prior.Select(set => set!.Srv.NativePointer).Distinct().Count());
    }

    [Fact]
    public void CapacityGrowthReplacesOnlyRetiredSlotSrvAndSmallerRequestsKeepCapacity()
    {
        using var runtime = new D3D12GpuRuntime();
        using var pool = new D3D12FrameDescriptorHeaps(runtime);
        var prior = new (nint Rtv, nint Dsv, nint Srv)[3];
        for (int frame = 0; frame < 9; frame++)
        {
            runtime.Begin();
            int slot = runtime.SlotIndex;
            var set = pool.Get(frame < 3 ? 2 : frame < 6 ? 129 : 1);
            if (frame < 3) prior[slot] = (set.Rtv.NativePointer, set.Dsv.NativePointer, set.Srv.NativePointer);
            else
            {
                Assert.Equal(prior[slot].Rtv, set.Rtv.NativePointer);
                Assert.Equal(prior[slot].Dsv, set.Dsv.NativePointer);
                if (frame < 6)
                {
                    Assert.NotEqual(prior[slot].Srv, set.Srv.NativePointer);
                    prior[slot].Srv = set.Srv.NativePointer;
                }
                else Assert.Equal(prior[slot].Srv, set.Srv.NativePointer);
                Assert.Equal(129, set.SrvCapacity);
            }
            runtime.Submit();
        }
        Assert.Equal(12, pool.HeapCreations);
        Assert.Equal(9, pool.LiveHeapCount);
    }

    [Fact]
    public void AcquisitionRequiresOneOpenRecordingAndAbortDoesNotPreventReuse()
    {
        using var runtime = new D3D12GpuRuntime();
        using var pool = new D3D12FrameDescriptorHeaps(runtime);
        Assert.Throws<InvalidOperationException>(() => pool.Get(1));
        for (int frame = 0; frame < 12; frame++)
        {
            runtime.Begin();
            Assert.Throws<ArgumentOutOfRangeException>(() => pool.Get(-1));
            pool.Get(frame < 3 ? 0 : 8);
            Assert.Throws<InvalidOperationException>(() => pool.Get(8));
            if (frame % 2 == 0) runtime.Abort(); else runtime.Submit();
        }
        Assert.Equal(12, pool.HeapCreations);
        pool.Dispose();
        Assert.Equal(0, pool.LiveHeapCount);
        Assert.Throws<ObjectDisposedException>(() => pool.Get(1));
    }
}
