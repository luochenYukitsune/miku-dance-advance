using Vortice;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Mutable shadow attachments belong to one fence-protected recording slot.
/// Their submitted boundary states are always PSR/DepthWrite, including after an aborted reuse.</summary>
internal sealed class D3D12ShadowTargets:IDisposable
{
    internal ID3D12Resource Color {get;}
    internal ID3D12Resource Depth {get;}
    internal CpuDescriptorHandle Rtv {get;}
    internal CpuDescriptorHandle Dsv {get;}
    private readonly ID3D12DescriptorHeap rtvHeap,dsvHeap;

    private D3D12ShadowTargets(ID3D12Device device,int width,int height,Format depthFormat)
    {
        ID3D12Resource? color=null,depth=null;ID3D12DescriptorHeap? rtv=null,dsv=null;
        try
        {
            color=device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Texture2D(Format.R32_Float,(uint)width,(uint)height,mipLevels:1,flags:ResourceFlags.AllowRenderTarget),ResourceStates.PixelShaderResource);
            depth=device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Texture2D(depthFormat,(uint)width,(uint)height,mipLevels:1,flags:ResourceFlags.AllowDepthStencil),ResourceStates.DepthWrite);
            rtv=device.CreateDescriptorHeap(new(DescriptorHeapType.RenderTargetView,1));
            dsv=device.CreateDescriptorHeap(new(DescriptorHeapType.DepthStencilView,1));
            Rtv=rtv.GetCPUDescriptorHandleForHeapStart();Dsv=dsv.GetCPUDescriptorHandleForHeapStart();
            device.CreateRenderTargetView(color,null,Rtv);device.CreateDepthStencilView(depth,null,Dsv);
            Color=color;Depth=depth;rtvHeap=rtv;dsvHeap=dsv;
        }
        catch{dsv?.Dispose();rtv?.Dispose();depth?.Dispose();color?.Dispose();throw;}
    }

    internal static D3D12ShadowTargets Get(D3D12RecordingContext context,D3D12GpuCache cache,string stream,int width,int height,Format depthFormat)
    {
        // Begin waits this slot's fence before any recording. Fixed stream names keep resize history bounded;
        // GetReplaceable leases both replaced and current attachments through their actual reader's fence.
        object identity=(width,height,depthFormat);
        // Other slots may still own larger images after a shrink. Release those owners immediately;
        // their submitted work remains protected by its own leases, even if no further frame arrives.
        ReleaseSlots(cache,stream,identity);
        return cache.GetReplaceable(stream+":"+context.Runtime.SlotIndex,identity,()=>new D3D12ShadowTargets(context.Device,width,height,depthFormat));
    }

    internal static void ReleaseInactive(D3D12GpuCache cache,Mmd.Modern.Core.OriginalFrameScene frame)
    {
        bool scene=!frame.Surface.Snapshot.IsEmpty&&frame.Options is not null;
        if(!scene||frame.Options?.SelfShadow is not {Generate:true})ReleaseSlots(cache,"original-primary-shadow");
        if(!scene||frame.Lighting?.Lights.Any(light=>light.Enabled&&light.Intensity>0
            &&light.Color!=System.Numerics.Vector3.Zero&&light.CastShadows)!=true)ReleaseSlots(cache,"original-native-shadow");
    }

    internal static void ReleaseSlots(D3D12GpuCache cache,string stream,object? keepIdentity=null)
    {
        for(int slot=0;slot<D3D12SsaaGiMemoryBudget.MaximumInFlightFrames;slot++)
            cache.RemoveReplaceable(stream+":"+slot,keepIdentity);
    }

    internal void Begin(D3D12RecordingContext context)
    {
        context.Commands.ResourceBarrierTransition(Color,ResourceStates.PixelShaderResource,ResourceStates.RenderTarget);
        context.Commands.OMSetRenderTargets(Rtv,Dsv);
        context.Commands.ClearRenderTargetView(Rtv,new Color4(1f,1f,1f,1f));
        context.Commands.ClearDepthStencilView(Dsv,ClearFlags.Depth,1,0);
    }
    internal void End(D3D12RecordingContext context)
    {
        context.Commands.UnsetRenderTargets();
        context.Commands.ResourceBarrierTransition(Color,ResourceStates.RenderTarget,ResourceStates.PixelShaderResource);
    }

    // Shared immutable fallback is initialized once. Cache.Get removes a newly recorded initialization
    // on abort; a reused white texture receives no GPU writes and can be read by all three slots.
    internal static ID3D12Resource White(D3D12RecordingContext context,D3D12GpuCache cache)
        =>cache.Get("original-shadow-white",()=>
        {
            var white=context.Device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Texture2D(Format.R32_Float,1,1,mipLevels:1,flags:ResourceFlags.AllowRenderTarget),ResourceStates.PixelShaderResource);
            try
            {
                var heap=context.Own(context.Device.CreateDescriptorHeap(new(DescriptorHeapType.RenderTargetView,1)));
                var rtv=heap.GetCPUDescriptorHandleForHeapStart();context.Device.CreateRenderTargetView(white,null,rtv);
                context.Commands.ResourceBarrierTransition(white,ResourceStates.PixelShaderResource,ResourceStates.RenderTarget);
                context.Commands.ClearRenderTargetView(rtv,new Color4(1f,1f,1f,1f));
                context.Commands.ResourceBarrierTransition(white,ResourceStates.RenderTarget,ResourceStates.PixelShaderResource);return white;
            }
            catch{white.Dispose();throw;}
        });

    public void Dispose(){dsvHeap.Dispose();rtvHeap.Dispose();Depth.Dispose();Color.Dispose();}
}
