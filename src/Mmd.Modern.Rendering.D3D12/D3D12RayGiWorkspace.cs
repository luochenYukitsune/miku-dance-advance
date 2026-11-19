using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Tracks transitions for a frame-owned resource reused later in the same command list.</summary>
internal sealed class D3D12GiWorkspaceResource(ID3D12Resource resource, ResourceStates state)
{
    internal ID3D12Resource Resource { get; } = resource;
    internal ResourceStates State { get; private set; } = state;
    internal void Transition(D3D12RecordingContext context, ResourceStates next)
    {
        if (State == next) return;
        context.Commands.ResourceBarrierTransition(Resource, State, next);
        State = next;
    }
}

/// <summary>A receiver workspace belongs to one frame fence. A low-memory capture reuses it only after
/// the preceding sample's integration, filtering and surface composition in the same direct command list.
/// SRV descriptors remain distinct per sample; temporal history banks never belong to this workspace.</summary>
internal sealed class D3D12RayGiWorkspace
{
    private readonly D3D12GpuRuntime runtime;
    private readonly long frameId;
    private readonly int width, height;
    internal D3D12GiWorkspaceResource Position { get; }
    internal D3D12GiWorkspaceResource Normal { get; }
    internal D3D12GiWorkspaceResource Depth { get; }
    internal D3D12GiWorkspaceResource RawIndirect { get; }
    internal D3D12GiWorkspaceResource Diagnostics { get; }
    internal ID3D12Resource Zero { get; }
    internal CpuDescriptorHandle PositionRtv { get; }
    internal CpuDescriptorHandle NormalRtv { get; }
    internal CpuDescriptorHandle Dsv { get; }
    internal ulong AllocationBytes { get; private set; }
    internal sealed class HalfResWorkspace
    {
        private readonly D3D12RayGiWorkspace owner;
        internal int Width { get; }
        internal int Height { get; }
        internal D3D12GiWorkspaceResource Position { get; }
        internal D3D12GiWorkspaceResource Normal { get; }
        internal D3D12GiWorkspaceResource Indirect { get; }
        private D3D12GiWorkspaceResource? spatialOutput;

        internal HalfResWorkspace(D3D12RayGiWorkspace owner, D3D12RecordingContext context, int width, int height)
        {
            this.owner = owner; Width = width; Height = height;
            Position = new(owner.Allocate(context, HeapType.Default,
                ResourceDescription.Texture2D(Format.R32G32B32A32_Float, (uint)width, (uint)height, mipLevels: 1, flags: ResourceFlags.AllowUnorderedAccess),
                ResourceStates.UnorderedAccess), ResourceStates.UnorderedAccess);
            Normal = new(owner.Allocate(context, HeapType.Default,
                ResourceDescription.Texture2D(Format.R32G32B32A32_Float, (uint)width, (uint)height, mipLevels: 1, flags: ResourceFlags.AllowUnorderedAccess),
                ResourceStates.UnorderedAccess), ResourceStates.UnorderedAccess);
            Indirect = new(owner.Allocate(context, HeapType.Default,
                ResourceDescription.Buffer(checked((ulong)width * (ulong)height * 16), ResourceFlags.AllowUnorderedAccess),
                ResourceStates.UnorderedAccess), ResourceStates.UnorderedAccess);
        }

        internal void BeginSample(D3D12RecordingContext context)
        {
            Position.Transition(context, ResourceStates.UnorderedAccess);
            Normal.Transition(context, ResourceStates.UnorderedAccess);
            Indirect.Transition(context, ResourceStates.UnorderedAccess);
        }

        internal D3D12GiWorkspaceResource SpatialOutput(D3D12RecordingContext context)
            => spatialOutput ??= new(owner.Allocate(context, HeapType.Default,
                ResourceDescription.Buffer(checked((ulong)Width * (ulong)Height * 16), ResourceFlags.AllowUnorderedAccess),
                ResourceStates.UnorderedAccess), ResourceStates.UnorderedAccess);
    }

    private D3D12GiWorkspaceResource? spatialOutput;
    private HalfResWorkspace? halfRes;

    internal D3D12RayGiWorkspace(D3D12RecordingContext context, int width, int height)
    {
        runtime = context.Runtime; frameId = runtime.FrameId; this.width = width; this.height = height;
        var rtv = context.Own(runtime.Device.CreateDescriptorHeap(new(DescriptorHeapType.RenderTargetView, 2)));
        var dsv = context.Own(runtime.Device.CreateDescriptorHeap(new(DescriptorHeapType.DepthStencilView, 1)));
        D3D12GiWorkspaceResource Texture(Format format, ResourceFlags flags, ResourceStates state)
            => new(Allocate(context, HeapType.Default, ResourceDescription.Texture2D(format, (uint)width, (uint)height,
                mipLevels: 1, flags: flags), state), state);
        Position = Texture(Format.R32G32B32A32_Float, ResourceFlags.AllowRenderTarget, ResourceStates.RenderTarget);
        Normal = Texture(Format.R32G32B32A32_Float, ResourceFlags.AllowRenderTarget, ResourceStates.RenderTarget);
        Depth = Texture(Format.D32_Float, ResourceFlags.AllowDepthStencil, ResourceStates.DepthWrite);
        RawIndirect = new(Allocate(context, HeapType.Default, ResourceDescription.Buffer(checked((ulong)width * (ulong)height * 16),
            ResourceFlags.AllowUnorderedAccess), ResourceStates.UnorderedAccess), ResourceStates.UnorderedAccess);
        Diagnostics = new(Allocate(context, HeapType.Default, ResourceDescription.Buffer(16, ResourceFlags.AllowUnorderedAccess),
            ResourceStates.CopyDest), ResourceStates.CopyDest);
        Zero = Allocate(context, HeapType.Upload, ResourceDescription.Buffer(16), ResourceStates.GenericRead);
        Zero.Map<byte>(0, 16).Clear(); Zero.Unmap(0);
        (PositionRtv, NormalRtv) = D3D12RayGiRenderer.GBufferHandles(rtv.GetCPUDescriptorHandleForHeapStart(),
            runtime.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView));
        Dsv = dsv.GetCPUDescriptorHandleForHeapStart();
        runtime.Device.CreateRenderTargetView(Position.Resource, null, PositionRtv);
        runtime.Device.CreateRenderTargetView(Normal.Resource, null, NormalRtv);
        runtime.Device.CreateDepthStencilView(Depth.Resource, null, Dsv);
    }

    private ID3D12Resource Allocate(D3D12RecordingContext context, HeapType heap, ResourceDescription description, ResourceStates state)
    {
        ulong bytes = runtime.Device.GetResourceAllocationInfo(0, [description]).SizeInBytes;
        if (bytes is 0 or ulong.MaxValue) throw new NotSupportedException("Device cannot measure GI receiver allocation.");
        var resource = context.Own(runtime.Device.CreateCommittedResource(heap, description, state));
        AllocationBytes = checked(AllocationBytes + bytes);
        return resource;
    }

    internal void BeginSample(D3D12RecordingContext context, int targetWidth, int targetHeight)
    {
        if (!ReferenceEquals(runtime, context.Runtime) || frameId != runtime.FrameId || width != targetWidth || height != targetHeight)
            throw new InvalidOperationException("GI workspace reuse requires the same frame, device and render dimensions.");
        context.Commands.UnsetRenderTargets();
        Position.Transition(context, ResourceStates.RenderTarget);
        Normal.Transition(context, ResourceStates.RenderTarget);
        Depth.Transition(context, ResourceStates.DepthWrite);
        RawIndirect.Transition(context, ResourceStates.UnorderedAccess);
    }

    internal void ResetDiagnostics(D3D12RecordingContext context)
    {
        Diagnostics.Transition(context, ResourceStates.CopyDest);
        context.Commands.CopyBufferRegion(Diagnostics.Resource, 0, Zero, 0, 16);
        Diagnostics.Transition(context, ResourceStates.UnorderedAccess);
    }

    internal D3D12GiWorkspaceResource SpatialOutput(D3D12RecordingContext context)
        => spatialOutput ??= new(Allocate(context, HeapType.Default,
            ResourceDescription.Buffer(checked((ulong)width * (ulong)height * 16), ResourceFlags.AllowUnorderedAccess),
            ResourceStates.UnorderedAccess), ResourceStates.UnorderedAccess);

    internal HalfResWorkspace AcquireHalfRes(D3D12RecordingContext context, int halfWidth, int halfHeight)
    {
        if (halfRes is null) halfRes = new(this, context, halfWidth, halfHeight);
        else
        {
            if (halfRes.Width != halfWidth || halfRes.Height != halfHeight)
                throw new InvalidOperationException("Half-res GI workspace reuse requires matching half dimensions.");
            halfRes.BeginSample(context);
        }
        return halfRes;
    }
}
