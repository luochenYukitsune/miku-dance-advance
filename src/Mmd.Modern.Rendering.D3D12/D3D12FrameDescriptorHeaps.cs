using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>One descriptor set per fence-protected recording slot. Begin has retired the slot
/// before Get can overwrite its descriptors. Final color resources are never pooled here. Each set remembers the
/// resource behind every view it wrote, so unchanged views are not rewritten; a new layout or SRV heap forgets them.</summary>
internal sealed class D3D12FrameDescriptorHeaps : IDisposable
{
    internal sealed class Set : IDisposable
    {
        private readonly record struct SrvSlot(ID3D12Resource? Resource, Format ViewFormat, int HeapGeneration);
        internal ID3D12DescriptorHeap Rtv { get; }
        internal ID3D12DescriptorHeap Dsv { get; }
        internal ID3D12DescriptorHeap Srv { get; private set; }
        internal int SrvCapacity { get; private set; }
        internal int SrvGeneration { get; private set; } = 1;
        private SrvSlot[] srvViews = [];
        private object? layout, rtvView, dsvView;
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
            Srv.Dispose(); Srv = next; SrvCapacity = capacity; SrvGeneration++; Array.Clear(srvViews);
            return true;
        }
        /// <summary>Other recorders write their own fixed slots around the cached ranges; those ranges move only with the layout.</summary>
        internal void UseLayout(object key)
        {
            if (Equals(layout, key)) return;
            layout = key; Array.Clear(srvViews);
        }
        /// <summary>Writes a default SRV for <paramref name="resource"/> unless this slot already holds the same
        /// texture resource, view format and heap generation. Returns whether it wrote.</summary>
        internal bool WriteSrv(ID3D12Device device, int slot, ID3D12Resource resource, int stride, Format viewFormat = Format.Unknown)
        {
            if (srvViews.Length < SrvCapacity) Array.Resize(ref srvViews, SrvCapacity);
            Format effectiveFormat = viewFormat != Format.Unknown ? viewFormat : resource.Description.Format;
            var current = srvViews[slot];
            if (ReferenceEquals(current.Resource, resource) && current.ViewFormat == effectiveFormat && current.HeapGeneration == SrvGeneration)
                return false;
            var resourceDesc = resource.Description;
            ShaderResourceViewDescription? srv = resourceDesc.Dimension == ResourceDimension.Texture2D
                ? new ShaderResourceViewDescription
                {
                    Format = effectiveFormat,
                    ViewDimension = ShaderResourceViewDimension.Texture2D,
                    Shader4ComponentMapping = ShaderComponentMapping.Default,
                    Texture2D = new Texture2DShaderResourceView
                    {
                        MostDetailedMip = 0,
                        MipLevels = resourceDesc.MipLevels,
                    },
                }
                : null;
            device.CreateShaderResourceView(resource, srv, Srv.GetCPUDescriptorHandleForHeapStart().Offset(slot, stride));
            srvViews[slot] = new(resource, effectiveFormat, SrvGeneration);
            return true;
        }
        internal bool WriteTarget(ID3D12Device device, ID3D12Resource resource, bool depth)
        {
            if (ReferenceEquals(depth ? dsvView : rtvView, resource)) return false;
            if (depth) { device.CreateDepthStencilView(resource, null, Dsv.GetCPUDescriptorHandleForHeapStart()); dsvView = resource; }
            else { device.CreateRenderTargetView(resource, null, Rtv.GetCPUDescriptorHandleForHeapStart()); rtvView = resource; }
            return true;
        }
        private static ID3D12DescriptorHeap CreateSrv(ID3D12Device device, int capacity)
            => device.CreateDescriptorHeap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                capacity, DescriptorHeapFlags.ShaderVisible));
        public void Dispose() { Array.Clear(srvViews); layout = rtvView = dsvView = null; Srv.Dispose(); Dsv.Dispose(); Rtv.Dispose(); }
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
        if (runtime.IsRecording) throw new InvalidOperationException("Submit or abort the open GPU frame before waiting for idle.");
        try { runtime.WaitForIdle(); }
        finally
        {
            disposed = true;
            foreach (var set in slots) set?.Dispose();
            Array.Clear(slots);
        }
    }
}
