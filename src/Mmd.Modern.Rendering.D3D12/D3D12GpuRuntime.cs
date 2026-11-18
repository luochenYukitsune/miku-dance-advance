using Mmd.Modern.Core;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;
namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Elapsed wall time within the recording thread, including driver blocking or preemption,
/// and allocations on that thread. This is not strict on-CPU time or CPU utilization.
/// Stages can be nested and must not be summed.</summary>
public readonly record struct D3D12CpuStageStatistics(double CpuMilliseconds,long AllocatedBytes,int Calls);

/// <summary>Bounded per-GPU-frame diagnostics for Scene recordings, including SSAA/effect redraws.
/// Editor visibility and later borrowed-input preparation are outside this scope.</summary>
public sealed record D3D12SceneRecordCpuStatistics
{
    public int SceneInvocations {get;init;}
    public D3D12CpuStageStatistics OriginalRecorder {get;init;}
    public D3D12CpuStageStatistics Preflight {get;init;}
    public D3D12CpuStageStatistics SurfaceStyleValidation {get;init;}
    public D3D12CpuStageStatistics Pipelines {get;init;}
    public D3D12CpuStageStatistics PrimaryShadow {get;init;}
    public D3D12CpuStageStatistics NativeLights {get;init;}
    public D3D12CpuStageStatistics Ground {get;init;}
    public D3D12CpuStageStatistics SurfaceLoop {get;init;}
    public D3D12CpuStageStatistics CombinedOutline {get;init;}
    public D3D12CpuStageStatistics OriginalOutline {get;init;}
    public D3D12CpuStageStatistics ModernOutline {get;init;}
    public D3D12CpuStageStatistics OriginalPositions {get;init;}
    public D3D12CpuStageStatistics ModernPositions {get;init;}
    public D3D12CpuStageStatistics ModernPacking {get;init;}
    public D3D12CpuStageStatistics OriginalOutlineUpload {get;init;}
    public D3D12CpuStageStatistics ModernOutlineUpload {get;init;}
    public D3D12CpuStageStatistics SurfaceBoundsLookup {get;init;}
    public D3D12CpuStageStatistics SurfaceBoundsBuild {get;init;}
    public D3D12CpuStageStatistics OutlineBoundsLookup {get;init;}
    public D3D12CpuStageStatistics OutlineBoundsBuild {get;init;}
    // Opt-in cache phases are wall time only (AllocatedBytes is zero). Reduction includes
    // the existing parallel join; invalidation includes input comparison and CSR candidates.
    public D3D12CpuStageStatistics SurfaceBoundsTopology {get;init;}
    public D3D12CpuStageStatistics SurfaceBoundsNewEntry {get;init;}
    public D3D12CpuStageStatistics SurfaceBoundsInvalidation {get;init;}
    public D3D12CpuStageStatistics SurfaceBoundsPreparation {get;init;}
    public D3D12CpuStageStatistics SurfaceBoundsReduction {get;init;}
    public D3D12CpuStageStatistics OutlineBoundsTopology {get;init;}
    public D3D12CpuStageStatistics OutlineBoundsNewEntry {get;init;}
    public D3D12CpuStageStatistics OutlineBoundsInvalidation {get;init;}
    public D3D12CpuStageStatistics OutlineBoundsPreparation {get;init;}
    public D3D12CpuStageStatistics OutlineBoundsReduction {get;init;}
    public long OriginalExpandedVertices {get;init;}
    public long ModernExpandedVertices {get;init;}
    public long BoundsMaterialRangeComparisons {get;init;}
    public long BoundsPositionComparisons {get;init;}
    public long BoundsScannedIndices {get;init;}
    public long BoundsRebuiltMaterials {get;init;}
    // Full and sparse describe the chosen reduction path; a sparse scan may include
    // a nonfinite-input fallback to the original complete ordered loop.
    public long BoundsFullScanMaterials {get;init;}
    public long BoundsSparseScanMaterials {get;init;}
    public long BoundsParallelMaterials {get;init;}
    public long BoundsMarkedLeafIncidences {get;init;}
}

public sealed record D3D12FrameStatistics(long FrameId,int SubmissionCount,int ReadbackCount,int SceneDrawCalls,int EffectPassDrawCalls,
    long TotalSubmissions,long TotalReadbacks,long BufferUploads,long TextureUploads,long ShaderCompilations,long PipelineCreations,
    ulong SubmittedFence,ulong CompletedFence,int PendingFrames,int DescriptorHighWater,string DeviceIdentity,string CommandListIdentity)
{
    public int LiveFrameResources {get;init;}
    public int CachedResources {get;init;}
    public double LastSlotWaitMilliseconds {get;init;}
    public double RecordCpuMilliseconds {get;init;}
    public double GeometryCacheMilliseconds {get;init;}
    public double TextureBindingMilliseconds {get;init;}
    public double SceneRecordMilliseconds {get;init;}
    public D3D12SceneRecordCpuStatistics? SceneRecordCpu {get;init;}
    public double BeginFrameCpuMilliseconds {get;init;}
    public double FrameTargetMilliseconds {get;init;}
    public double AntialiasingCpuMilliseconds {get;init;}
    public double EditorVisibilityCpuMilliseconds {get;init;}
    public double EditorOverlayCpuMilliseconds {get;init;}
    public double AppendPresentCpuMilliseconds {get;init;}
    // Current recording thread only; these counters never collect or change GC policy.
    public long RecordAllocatedBytes {get;init;}
    public long SceneRecordAllocatedBytes {get;init;}
    public long EditorVisibilityAllocatedBytes {get;init;}
    public double SubmitCpuMilliseconds {get;init;}
    public int ConstantAllocations {get;init;}
    public long ConstantBytes {get;init;}
    public int FrameOwnedAllocations {get;init;}
    // Fence-protected transient pool: resources and heaps created or reused by this frame, and all live entries.
    public int TransientCreations {get;init;}
    public int TransientResourceCreations {get;init;}
    public int TransientReuses {get;init;}
    public int TransientLiveResources {get;init;}
    // Shader-resource and target view descriptors written by the frame renderer's texture binding and targets.
    public int TextureDescriptorWrites {get;init;}
    public int TextureDescriptorSkips {get;init;}
    public int TemporalGiInFlightFrames {get;init;}
    public int TemporalGiHistoryGenerations {get;init;}
    public bool TemporalGiBoundedFlight=>TemporalGiInFlightFrames==1;
    public ulong TemporalGiReservationBytes {get;init;}
    public ulong TemporalGiAvailableBytes {get;init;}
    public double TemporalGiRetirementMilliseconds {get;init;}
    public bool CaptureLowMemory {get;init;}
    public int CaptureInFlightFrames {get;init;}
    public double CaptureRetirementMilliseconds {get;init;}
    public FrameAntialiasing Antialiasing {get;init;}
    public int AntialiasingPassDrawCalls {get;init;}
    public SmaaQuality SmaaQuality {get;init;}=SmaaQuality.High;
    public int SpatialSamples {get;init;}=1;
    public int SpatialAccumulationDispatches {get;init;}
    public int SpatialResolveDrawCalls {get;init;}
    public D3D12SpatialUpscalingMode SpatialUpscaling {get;init;}
    public int UpscalingInputWidth {get;init;}
    public int UpscalingInputHeight {get;init;}
    public int UpscalingOutputWidth {get;init;}
    public int UpscalingOutputHeight {get;init;}
    public int UpscalingPassDrawCalls {get;init;}
    public D3D12TemporalUpscalingStatistics? TemporalUpscaling {get;init;}
}

/// <summary>The sole command-list submission and retirement owner. Each slot has its own allocator/list and frame resources.</summary>
internal sealed class D3D12GpuRuntime:IDisposable
{
    // D3D12 UPLOAD buffers support persistent mapping. Each page belongs to one
    // fence-protected recording slot; no CPU reads or writes overlap GPU use.
    private sealed class ConstantUploadPage : IDisposable
    {
        internal const int Size=1024*1024;
        private readonly ID3D12Resource resource;
        private readonly nint address;
        internal readonly ulong GpuAddress;
        internal unsafe ConstantUploadPage(ID3D12Device device)
        {
            resource=device.CreateCommittedResource(HeapType.Upload,ResourceDescription.Buffer(Size),ResourceStates.GenericRead);
            try
            {
                var mapped=resource.Map<byte>(0,Size);
                fixed(byte* pointer=mapped)address=(nint)pointer;
                GpuAddress=resource.GPUVirtualAddress;
            }
            catch {resource.Dispose();throw;}
        }
        internal unsafe void Write(int offset,ReadOnlySpan<byte> bytes)
            =>bytes.CopyTo(new Span<byte>((void*)(address+offset),bytes.Length));
        public void Dispose(){resource.Unmap(0);resource.Dispose();}
    }
    private sealed class Slot
    {
        internal readonly ID3D12CommandAllocator Allocator;
        internal readonly ID3D12GraphicsCommandList Commands;
        internal readonly List<IDisposable> Resources=[];
        internal readonly List<Action> AbortActions=[];
        internal readonly List<ConstantUploadPage> ConstantPages=[];
        internal int ConstantPage,ConstantOffset;
        internal ulong Fence;
        internal Slot(ID3D12Device device)
        {
            Allocator=device.CreateCommandAllocator(CommandListType.Direct);
            Commands=device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct,Allocator,null); Commands.Close();
        }
    }
    internal ID3D12Device Device {get;}
    internal ID3D12CommandQueue Queue {get;}
    internal ID3D12GraphicsCommandList Commands=>slots[slotIndex].Commands;
    internal ID3D12InfoQueue? DebugQueue {get;}
    internal string AdapterName {get;}
    internal bool IsWarp {get;}
    internal FeatureLevel FeatureLevel {get;}
    internal bool DebugRequested {get;}
    internal bool DebugLayerEnabled {get;}
    internal long FrameId {get;private set;}
    internal int SlotIndex=>slotIndex;
    internal string DeviceIdentity {get;}="D3D12-"+Guid.NewGuid().ToString("N");
    internal string CommandListIdentity=>$"{DeviceIdentity}/list-{slotIndex}";
    internal long Submissions=0,Readbacks=0,BufferUploads=0,TextureUploads=0,ShaderCompilations=0,PipelineCreations=0;
    internal int DescriptorHighWater=0;
    internal double LastSlotWaitMilliseconds;
    internal int ConstantAllocations;
    internal long ConstantPageMappings {get;private set;}
    internal long ConstantBytes;
    internal int FrameOwnedAllocations;
    internal int LiveFrameResources=>slots.Sum(s=>s.Resources.Count+s.ConstantPages.Count);
    internal ulong SubmittedFence=>fenceValue;
    internal ulong CompletedFence=>fence.CompletedValue;
    internal int PendingFrames=>slots.Count(s=>s.Fence>CompletedFence);
    internal bool IsRecording {get;private set;}
    internal D3D12TransientResources Transients {get;}
    private readonly Slot[] slots;
    private readonly ID3D12Fence fence;
    private readonly D3D12FenceWaiter fenceWaiter;
    private int slotIndex=-1;
    private ulong fenceValue;
    private bool disposed;
    internal D3D12GpuRuntime()
    {
        DebugRequested=Environment.GetEnvironmentVariable("MMD_D3D12_DEBUG")=="1";
        DebugLayerEnabled=D3D12DebugLayerBootstrap.BeforeDeviceCreation(DebugRequested);
        (Device,AdapterName,IsWarp)=D3D12AdapterSelection.CreateDevice(FeatureLevel.Level_11_0);
        FeatureLevel=Device.CheckMaxSupportedFeatureLevel();
        if(DebugRequested) DebugQueue=Device.QueryInterfaceOrNull<ID3D12InfoQueue>();
        Queue=Device.CreateCommandQueue(CommandListType.Direct);fence=Device.CreateFence(0,FenceFlags.None);fenceWaiter=new(fence);
        slots=Enumerable.Range(0,3).Select(_=>new Slot(Device)).ToArray();
        Transients=new(this);
    }
    internal D3D12RecordingContext Begin()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(IsRecording) throw new InvalidOperationException("Concurrent or nested GPU recording is unsupported.");
        slotIndex=(slotIndex+1)%slots.Length;var slot=slots[slotIndex];
        var waitTimer=System.Diagnostics.Stopwatch.StartNew();Wait(slot.Fence);LastSlotWaitMilliseconds=waitTimer.Elapsed.TotalMilliseconds;Retire(slot);
        ConstantAllocations=0;ConstantBytes=0;FrameOwnedAllocations=0;
        slot.Allocator.Reset();slot.Commands.Reset(slot.Allocator,null);slot.ConstantPage=slot.ConstantOffset=0;IsRecording=true;FrameId++;
        DebugQueue?.ClearStoredMessages();return new(this);
    }
    internal T Own<T>(T value) where T:IDisposable {if(!IsRecording)throw new InvalidOperationException("No open GPU frame.");slots[slotIndex].Resources.Add(value);FrameOwnedAllocations++;return value;}
    internal void OnAbort(Action action)=>slots[slotIndex].AbortActions.Add(action);
    internal ulong UploadConstants(ReadOnlySpan<byte> bytes)
    {
        const int pageSize=ConstantUploadPage.Size;
        if(!IsRecording)throw new InvalidOperationException("No open GPU frame.");
        int aligned=(bytes.Length+255)&~255;if(aligned>pageSize)throw new ArgumentException("A constant allocation exceeds one upload page.");
        var slot=slots[slotIndex];
        ConstantAllocations++;ConstantBytes+=bytes.Length;
        if(slot.ConstantOffset+aligned>pageSize){slot.ConstantPage++;slot.ConstantOffset=0;}
        if(slot.ConstantPages.Count<=slot.ConstantPage)
        {slot.ConstantPages.Add(new(Device));ConstantPageMappings++;}
        var page=slot.ConstantPages[slot.ConstantPage];int offset=slot.ConstantOffset;slot.ConstantOffset+=aligned;
        page.Write(offset,bytes);
        return page.GpuAddress+(ulong)offset;
    }
    internal ulong Submit()
    {
        if(!IsRecording)throw new InvalidOperationException("No open GPU frame.");
        var slot=slots[slotIndex];slot.Commands.Close();IsRecording=false;
        Queue.ExecuteCommandList(slot.Commands);Queue.Signal(fence,++fenceValue).CheckError();slot.Fence=fenceValue;slot.AbortActions.Clear();Submissions++;return fenceValue;
    }
    internal void Abort()
    {
        if(!IsRecording)return;
        try{Commands.Close();}finally{IsRecording=false;var slot=slots[slotIndex];foreach(var action in slot.AbortActions)action();slot.AbortActions.Clear();Retire(slot);}
    }
    internal void Wait(ulong value)=>WaitForFence(value,CancellationToken.None);

    internal void WaitForFence(ulong value,CancellationToken cancellationToken)
    {
        fenceWaiter.Wait(value,cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if(fence.CompletedValue==ulong.MaxValue)throw new InvalidOperationException("D3D12 device removed.");
    }
    internal void WaitForIdle()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(IsRecording)throw new InvalidOperationException("Submit or abort the open GPU frame before waiting for idle.");
        Wait(fenceValue);foreach(var slot in slots)Retire(slot);
        Transients.ReleaseIdle();
    }
    private static void Retire(Slot slot){for(int i=slot.Resources.Count-1;i>=0;i--)slot.Resources[i].Dispose();slot.Resources.Clear();}
    internal D3D12FrameStatistics Statistics(int scenes,int passes,int readbacks,int submissions)=>new(FrameId,submissions,readbacks,scenes,passes,
        Submissions,Readbacks,BufferUploads,TextureUploads,ShaderCompilations,PipelineCreations,fenceValue,CompletedFence,PendingFrames,DescriptorHighWater,
        DeviceIdentity,CommandListIdentity){LiveFrameResources=LiveFrameResources,LastSlotWaitMilliseconds=LastSlotWaitMilliseconds,ConstantAllocations=ConstantAllocations,ConstantBytes=ConstantBytes,FrameOwnedAllocations=FrameOwnedAllocations,
        TransientCreations=Transients.FrameCreations,TransientResourceCreations=Transients.FrameResourceCreations,TransientReuses=Transients.FrameReuses,TransientLiveResources=Transients.LiveCount};
    public void Dispose()
    {
        if(disposed)return;
        try{Abort();WaitForIdle();}
        finally
        {
            disposed=true;Transients.Dispose();
            foreach(var slot in slots){foreach(var page in slot.ConstantPages)page.Dispose();slot.Commands.Dispose();slot.Allocator.Dispose();}
            DebugQueue?.Dispose();fence.Dispose();Queue.Dispose();Device.Dispose();fenceWaiter.Dispose();
        }
    }
}

internal sealed class D3D12RecordingContext(D3D12GpuRuntime runtime)
{
    internal D3D12GpuRuntime Runtime=>runtime;
    internal ID3D12Device Device=>runtime.Device;
    internal ID3D12GraphicsCommandList Commands=>runtime.Commands;
    internal T Own<T>(T value) where T:IDisposable=>runtime.Own(value);
    internal ulong UploadConstants(ReadOnlySpan<byte> bytes)=>runtime.UploadConstants(bytes);
    internal ulong UploadConstants<T>(T value) where T:unmanaged=>runtime.UploadConstants(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value,1)));
    internal void Transition(D3D12GpuTarget target,ResourceStates state)
    {if(target.State!=state){Commands.ResourceBarrierTransition(target.Resource,target.State,state);target.State=state;}}
}

internal sealed class D3D12GpuTarget(string name,ID3D12Resource resource,CpuDescriptorHandle handle,int width,int height,Format format,bool depth)
{
    internal string Name=name;
    internal ID3D12Resource Resource=resource;
    internal CpuDescriptorHandle Handle=handle;
    internal int Width=width,Height=height;
    internal Format Format=format;
    internal bool Depth=depth,Initialized=false;
    internal bool StencilInitialized=false;
    internal ResourceStates State=depth?ResourceStates.DepthWrite:ResourceStates.RenderTarget;
}
