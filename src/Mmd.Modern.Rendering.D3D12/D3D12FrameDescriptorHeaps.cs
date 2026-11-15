using Vortice.Direct3D12;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>One descriptor set per fence-protected recording slot. Begin has retired the slot
/// before Get can overwrite its descriptors. Final color resources are never pooled here.</summary>
internal sealed class D3D12FrameDescriptorHeaps : IDisposable
{
    internal sealed class Set : IDisposable
    {
        internal ID3D12DescriptorHeap Rtv { get; }
        internal ID3D12DescriptorHeap Dsv { get; }
        internal ID3D12DescriptorHeap Srv { get; private set; }
        internal int SrvCapacity { get; private set; }
        internal Set(ID3D12Device device, int capacity)
        {
            Rtv = device.CreateDescriptorHeap(new(DescriptorHeapType.RenderTargetView, 1));
            try
            {
                Dsv = device.CreateDescriptorHeap(new(DescriptorHeapType.DepthStencilView, 1));
                try
                {
                    Srv = CreateSrv(device, capacity); SrvCapacity = capacity;
                }
                catch { Dsv.Dispose(); throw; }
            }
            catch { Rtv.Dispose(); throw; }
        }
        internal bool EnsureCapacity(ID3D12Device device, int capacity)
        {
            if (SrvCapacity >= capacity) return false;
            var next = CreateSrv(device, capacity);
            Srv.Dispose(); Srv = next; SrvCapacity = capacity;
            return true;
        }
        private static ID3D12DescriptorHeap CreateSrv(ID3D12Device device, int capacity)
            => device.CreateDescriptorHeap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                capacity, DescriptorHeapFlags.ShaderVisible));
        public void Dispose() { Srv.Dispose(); Dsv.Dispose(); Rtv.Dispose(); }
    }

    private readonly D3D12GpuRuntime runtime;
    private readonly Set?[] slots = new Set?[3];
    private long lastFrameId = -1;
    private bool disposed;
    internal long HeapCreations { get; private set; }
    internal int LiveHeapCount => slots.Count(value => value is not null) * 3;
    internal D3D12FrameDescriptorHeaps(D3D12GpuRuntime runtime) => this.runtime = runtime;
    internal Set Get(int descriptorCount)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!runtime.IsRecording) throw new InvalidOperationException("Descriptor reuse requires an open GPU frame.");
        if (lastFrameId == runtime.FrameId)
            throw new InvalidOperationException("A frame descriptor set can be acquired only once per recording.");
        if (descriptorCount < 0) throw new ArgumentOutOfRangeException(nameof(descriptorCount));
        int capacity = Math.Max(1, descriptorCount);
        var set = slots[runtime.SlotIndex];
        if (set is null)
        {
            set = new(runtime.Device, capacity); slots[runtime.SlotIndex] = set; HeapCreations += 3;
        }
        else if (set.EnsureCapacity(runtime.Device, capacity)) HeapCreations++;
        lastFrameId = runtime.FrameId;
        return set;
    }
    public void Dispose()
    {
        if (disposed) return;
        // An open recording could still refer to these descriptor tables. Its owner must
        // submit or abort it, then retire every GPU reader before releasing the pool.
        runtime.WaitForIdleForTeardown();
        disposed = true;
        foreach (var set in slots) set?.Dispose();
        Array.Clear(slots);
    }
}
