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
    internal void RestoreState(ResourceStates state) => State = state;
}

/// <summary>A receiver workspace belongs to one frame fence (or one retired recording slot for single-sample GI).
/// A low-memory capture reuses it only after the preceding sample's integration, filtering and surface composition
/// in the same direct command list. SRV descriptors remain distinct per sample; temporal history banks never belong to this workspace.</summary>
internal sealed class D3D12RayGiWorkspace : IDisposable
{
    private readonly D3D12GpuRuntime runtime;
    private readonly int width, height;
    private readonly bool slotReusable;
    private readonly List<IDisposable>? ownedResources;
    private long frameId;
    private bool disposed;
    internal D3D12GiWorkspaceResource Position { get; } = null!;
    internal D3D12GiWorkspaceResource Normal { get; } = null!;
    internal D3D12GiWorkspaceResource Depth { get; } = null!;
    internal D3D12GiWorkspaceResource RawIndirect { get; } = null!;
    internal D3D12GiWorkspaceResource Diagnostics { get; } = null!;
    internal ID3D12Resource Zero { get; } = null!;
    internal CpuDescriptorHandle PositionRtv { get; }
    internal CpuDescriptorHandle NormalRtv { get; }
    internal CpuDescriptorHandle Dsv { get; }
    internal ulong AllocationBytes { get; private set; }
    internal bool IsSlotReusable => slotReusable;
    internal int Width => width;
    internal int Height => height;
    internal long LastUsedFrameId => frameId;

    internal sealed class HalfResWorkspace : IDisposable
    {
        internal readonly record struct StateSnapshot(ResourceStates Position, ResourceStates Normal, ResourceStates Indirect,
            bool HadSpatialOutput, ResourceStates SpatialOutput);

        private readonly D3D12RayGiWorkspace owner;
        private readonly List<IDisposable>? ownedResources;
        internal int Width { get; }
        internal int Height { get; }
        internal D3D12GiWorkspaceResource Position { get; } = null!;
        internal D3D12GiWorkspaceResource Normal { get; } = null!;
        internal D3D12GiWorkspaceResource Indirect { get; } = null!;
        internal ID3D12DescriptorHeap? DescriptorHeap { get; private set; }
        private D3D12GiWorkspaceResource? spatialOutput;
        private bool disposed;

        internal HalfResWorkspace(D3D12RayGiWorkspace owner, D3D12RecordingContext context, int width, int height)
        {
            this.owner = owner; Width = width; Height = height;
            if (owner.slotReusable) ownedResources = new List<IDisposable>(5);
            try
            {
                Position = new(owner.Allocate(context, HeapType.Default,
                    ResourceDescription.Texture2D(Format.R32G32B32A32_Float, (uint)width, (uint)height, mipLevels: 1, flags: ResourceFlags.AllowUnorderedAccess),
                    ResourceStates.UnorderedAccess, ownedResources), ResourceStates.UnorderedAccess);
                Normal = new(owner.Allocate(context, HeapType.Default,
                    ResourceDescription.Texture2D(Format.R32G32B32A32_Float, (uint)width, (uint)height, mipLevels: 1, flags: ResourceFlags.AllowUnorderedAccess),
                    ResourceStates.UnorderedAccess, ownedResources), ResourceStates.UnorderedAccess);
                Indirect = new(owner.Allocate(context, HeapType.Default,
                    ResourceDescription.Buffer(checked((ulong)width * (ulong)height * 16), ResourceFlags.AllowUnorderedAccess),
                    ResourceStates.UnorderedAccess, ownedResources), ResourceStates.UnorderedAccess);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal void AttachDescriptorHeap(ID3D12DescriptorHeap heap)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (ownedResources is null)
                throw new InvalidOperationException("Descriptor heap caching is supported only on slot-reusable half-res workspaces.");
            ownedResources.Add(heap);
            DescriptorHeap = heap;
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
                ResourceStates.UnorderedAccess, ownedResources), ResourceStates.UnorderedAccess);

        internal StateSnapshot CaptureStates()
            => new(Position.State, Normal.State, Indirect.State, spatialOutput is not null, spatialOutput?.State ?? default);

        internal void RestoreStates(StateSnapshot snapshot)
        {
            Position.RestoreState(snapshot.Position);
            Normal.RestoreState(snapshot.Normal);
            Indirect.RestoreState(snapshot.Indirect);
            if (!snapshot.HadSpatialOutput && spatialOutput is not null)
            {
                ownedResources?.Remove(spatialOutput.Resource);
                spatialOutput.Resource.Dispose();
                spatialOutput = null;
            }
            else if (snapshot.HadSpatialOutput && spatialOutput is not null)
            {
                spatialOutput.RestoreState(snapshot.SpatialOutput);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            DescriptorHeap = null;
            spatialOutput = null;
            if (ownedResources is null) return;
            for (int i = ownedResources.Count - 1; i >= 0; i--)
                ownedResources[i].Dispose();
            ownedResources.Clear();
        }
    }

    private D3D12GiWorkspaceResource? spatialOutput;
    private HalfResWorkspace? halfRes;

    internal D3D12RayGiWorkspace(D3D12RecordingContext context, int width, int height)
        : this(context, width, height, slotReusable: false)
    {
    }

    internal D3D12RayGiWorkspace(D3D12RecordingContext context, int width, int height, bool slotReusable)
    {
        runtime = context.Runtime; frameId = runtime.FrameId; this.width = width; this.height = height;
        this.slotReusable = slotReusable;
        if (slotReusable) ownedResources = new List<IDisposable>(10);
        try
        {
            var rtv = Own(context, runtime.Device.CreateDescriptorHeap(new(DescriptorHeapType.RenderTargetView, 2)), ownedResources);
            var dsv = Own(context, runtime.Device.CreateDescriptorHeap(new(DescriptorHeapType.DepthStencilView, 1)), ownedResources);
            D3D12GiWorkspaceResource Texture(Format format, ResourceFlags flags, ResourceStates state)
                => new(Allocate(context, HeapType.Default, ResourceDescription.Texture2D(format, (uint)width, (uint)height,
                    mipLevels: 1, flags: flags), state, ownedResources), state);
            Position = Texture(Format.R32G32B32A32_Float, ResourceFlags.AllowRenderTarget, ResourceStates.RenderTarget);
            Normal = Texture(Format.R32G32B32A32_Float, ResourceFlags.AllowRenderTarget, ResourceStates.RenderTarget);
            Depth = Texture(Format.D32_Float, ResourceFlags.AllowDepthStencil, ResourceStates.DepthWrite);
            RawIndirect = new(Allocate(context, HeapType.Default, ResourceDescription.Buffer(checked((ulong)width * (ulong)height * 16),
                ResourceFlags.AllowUnorderedAccess), ResourceStates.UnorderedAccess, ownedResources), ResourceStates.UnorderedAccess);
            Diagnostics = new(Allocate(context, HeapType.Default, ResourceDescription.Buffer(16, ResourceFlags.AllowUnorderedAccess),
                ResourceStates.CopyDest, ownedResources), ResourceStates.CopyDest);
            Zero = Allocate(context, HeapType.Upload, ResourceDescription.Buffer(16), ResourceStates.GenericRead, ownedResources);
            Zero.Map<byte>(0, 16).Clear(); Zero.Unmap(0);
            (PositionRtv, NormalRtv) = D3D12RayGiRenderer.GBufferHandles(rtv.GetCPUDescriptorHandleForHeapStart(),
                runtime.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView));
            Dsv = dsv.GetCPUDescriptorHandleForHeapStart();
            runtime.Device.CreateRenderTargetView(Position.Resource, null, PositionRtv);
            runtime.Device.CreateRenderTargetView(Normal.Resource, null, NormalRtv);
            runtime.Device.CreateDepthStencilView(Depth.Resource, null, Dsv);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static T Own<T>(D3D12RecordingContext context, T value, List<IDisposable>? targetOwned) where T : IDisposable
    {
        if (targetOwned is not null)
        {
            targetOwned.Add(value);
            return value;
        }
        return context.Own(value);
    }

    private ID3D12Resource Allocate(D3D12RecordingContext context, HeapType heap, ResourceDescription description, ResourceStates state, List<IDisposable>? targetOwned)
    {
        ulong bytes = runtime.Device.GetResourceAllocationInfo(0, [description]).SizeInBytes;
        if (bytes is 0 or ulong.MaxValue) throw new NotSupportedException("Device cannot measure GI receiver allocation.");
        var resource = runtime.Device.CreateCommittedResource(heap, description, state);
        try
        {
            Own(context, resource, targetOwned);
        }
        catch
        {
            resource.Dispose();
            throw;
        }
        AllocationBytes = checked(AllocationBytes + bytes);
        return resource;
    }

    internal void BeginSample(D3D12RecordingContext context, int targetWidth, int targetHeight)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!ReferenceEquals(runtime, context.Runtime) || frameId != runtime.FrameId || width != targetWidth || height != targetHeight)
            throw new InvalidOperationException("GI workspace reuse requires the same frame, device and render dimensions.");
        context.Commands.UnsetRenderTargets();
        Position.Transition(context, ResourceStates.RenderTarget);
        Normal.Transition(context, ResourceStates.RenderTarget);
        Depth.Transition(context, ResourceStates.DepthWrite);
        RawIndirect.Transition(context, ResourceStates.UnorderedAccess);
    }

    internal void BeginSlotSample(D3D12RecordingContext context, int targetWidth, int targetHeight)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!slotReusable || !ReferenceEquals(runtime, context.Runtime) || width != targetWidth || height != targetHeight)
            throw new InvalidOperationException("GI slot workspace reuse requires matching device and render dimensions.");

        long previousFrameId = frameId;
        ulong previousAllocationBytes = AllocationBytes;
        ResourceStates posState = Position.State;
        ResourceStates normState = Normal.State;
        ResourceStates depthState = Depth.State;
        ResourceStates rawState = RawIndirect.State;
        ResourceStates diagState = Diagnostics.State;
        bool hadSpatial = spatialOutput is not null;
        ResourceStates spatialState = spatialOutput?.State ?? default;
        bool hadHalf = halfRes is not null;
        HalfResWorkspace.StateSnapshot halfStates = halfRes?.CaptureStates() ?? default;

        frameId = runtime.FrameId;
        runtime.OnAbort(() =>
        {
            if (disposed) return;
            frameId = previousFrameId;
            AllocationBytes = previousAllocationBytes;
            Position.RestoreState(posState);
            Normal.RestoreState(normState);
            Depth.RestoreState(depthState);
            RawIndirect.RestoreState(rawState);
            Diagnostics.RestoreState(diagState);
            if (!hadSpatial && spatialOutput is not null)
            {
                ownedResources?.Remove(spatialOutput.Resource);
                spatialOutput.Resource.Dispose();
                spatialOutput = null;
            }
            else if (hadSpatial && spatialOutput is not null)
            {
                spatialOutput.RestoreState(spatialState);
            }
            if (!hadHalf && halfRes is not null)
            {
                halfRes.Dispose();
                halfRes = null;
            }
            else if (hadHalf && halfRes is not null)
            {
                halfRes.RestoreStates(halfStates);
            }
        });

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
            ResourceStates.UnorderedAccess, ownedResources), ResourceStates.UnorderedAccess);

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

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        halfRes?.Dispose();
        halfRes = null;
        spatialOutput = null;
        if (ownedResources is null) return;
        for (int i = ownedResources.Count - 1; i >= 0; i--)
            ownedResources[i].Dispose();
        ownedResources.Clear();
    }
}
