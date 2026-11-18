using System.Runtime.InteropServices;
using System.Numerics;
using Mmd.Modern.Core;
using Vortice.Direct3D12;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Cumulative recording attempts, including aborted recordings. Copied bytes/calls are
/// CPU-to-GPU staging, already included in BufferUploadStatistics; there are no GPU seed copies.
/// FullPotentialBytes counts a complete payload for every changed upload. Retained/live are gauges.</summary>
internal readonly record struct D3D12SceneVertexBufferStatistics(long FullUploads,long PartialUploads,long UnchangedHits,
    long CopyCalls,long CopiedBytes,long FullPotentialBytes,long BufferCreations,int RetainedBuffers,long RetainedBytes,
    int LiveBuffers,long LiveBytes);

/// <summary>One mutable DEFAULT vertex buffer per fence slot and stream. Begin has already waited
/// that slot's fence. Each destination remembers its own actual revision, so omitted publications
/// and branches cannot drop changes. Only small revision metadata, never older CPU arrays, is held.</summary>
internal sealed class D3D12VertexBufferPool(D3D12GpuRuntime runtime,D3D12BufferUploadStaging staging,bool unorderedAccess=false):IDisposable
{
    private sealed class Buffer(ID3D12Resource resource,int bytes,Action released):IDisposable
    {
        internal ID3D12Resource Resource {get;}=resource;
        internal int Bytes {get;}=bytes;
        public void Dispose(){Resource.Dispose();released();}
    }
    private sealed class Entry(D3D12RetainedResource owner,object revision)
    {
        internal D3D12RetainedResource Owner {get;}=owner;
        internal Buffer Buffer=>(Buffer)Owner.Value;
        internal object Revision=revision;
        // A second different publication in one list must preserve previously returned readers.
        internal long RecordingFrame=-1;
    }
    private static readonly int vertexStride=Marshal.SizeOf<SceneVertex>();
    private const int vertexChunkSize=4096;
    private readonly Dictionary<string,Entry?[]> streams=[];
    private long fullUploads,partialUploads,unchangedHits,copyCalls,copiedBytes,fullPotentialBytes,bufferCreations,liveBytes;
    private int liveBuffers;
    private bool disposed;
    /// <summary>False only when the latest Get returned an existing buffer without any copy.</summary>
    internal bool LastGetWrote {get;private set;}
    internal int RetainedBufferCount=>streams.Values.Sum(slots=>slots.Count(entry=>entry is not null));
    internal D3D12SceneVertexBufferStatistics Statistics=>new(fullUploads,partialUploads,unchangedHits,copyCalls,copiedBytes,
        fullPotentialBytes,bufferCreations,RetainedBufferCount,streams.Values.Sum(slots=>slots.Sum(entry=>(long)(entry?.Buffer.Bytes??0))),
        liveBuffers,liveBytes);

    internal ID3D12Resource Get(string stream,SceneSnapshot scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if(scene.IsEmpty)throw new ArgumentException("A vertex buffer requires nonempty geometry.",nameof(scene));
        return GetPayload(stream,default,scene.VertexCount,vertexStride,scene.VertexRevision,scene);
    }
    internal ID3D12Resource GetExpanded(string stream,Vector4[] positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        if(positions.Length==0||!D3D12ExpandedPositionRevision.TryGet(positions,out var revision))
            throw new ArgumentException("An expanded buffer requires a nonempty producer-owned position stream.",nameof(positions));
        return GetPayload(stream,MemoryMarshal.AsBytes(positions.AsSpan()),positions.Length,16,revision!);
    }
    internal ID3D12Resource GetExpanded(string stream,D3D12ExpandedPositions positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        if(positions.Length==0)throw new ArgumentException("An expanded buffer requires nonempty geometry.",nameof(positions));
        return GetPayload(stream,default,positions.Length,16,positions.Revision,expanded:positions);
    }
    private ID3D12Resource GetPayload(string stream,ReadOnlySpan<byte> payload,int count,int stride,object revision,
        SceneSnapshot? scene=null,D3D12ExpandedPositions? expanded=null)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        if(!runtime.IsRecording)throw new InvalidOperationException("Vertex uploads require an open GPU frame.");
        if(!streams.TryGetValue(stream,out var slots))
        {slots=new Entry?[D3D12SsaaGiMemoryBudget.MaximumInFlightFrames];streams.Add(stream,slots);}
        int slot=runtime.SlotIndex,bytes=checked(count*stride);
        var entry=slots[slot];
        LastGetWrote=false;
        if(entry is not null&&ReferenceEquals(entry.Revision,revision))return Hit(entry);

        bool replacement=entry is null||entry.Buffer.Bytes!=bytes||entry.RecordingFrame==runtime.FrameId;
        IReadOnlyList<SceneVertexRange> ranges=replacement?[new(0,count)]:ChangedRanges(revision,entry!.Revision,count);
        if(ranges.Count==0){var unchanged=entry!;unchanged.Revision=revision;return Hit(unchanged);}
        bool full=ranges.Count==1&&ranges[0].Start==0&&ranges[0].Count==count;
        if(replacement)
        {
            var resource=runtime.Device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Buffer((ulong)bytes,unorderedAccess?ResourceFlags.AllowUnorderedAccess:ResourceFlags.None),ResourceStates.CopyDest);
            var owned=new Buffer(resource,bytes,()=>{liveBuffers--;liveBytes-=bytes;});
            bufferCreations++;liveBuffers++;liveBytes+=bytes;
            var previous=entry;entry=new(new(revision,owned),revision);slots[slot]=entry;previous?.Owner.Dispose();
        }
        var changed=entry!;KeepForFrame(changed);changed.RecordingFrame=runtime.FrameId;LastGetWrote=true;
        // Removing a touched entry handles both preexisting buffers and newly allocated buffers:
        // aborted barriers/copies never ran, so neither their final state nor revision is reusable.
        runtime.OnAbort(()=>
        {
            if(ReferenceEquals(slots[slot],changed)){slots[slot]=null;changed.Owner.Dispose();}
        });
        var gpu=changed.Buffer.Resource;
        if(!replacement)runtime.Commands.ResourceBarrierTransition(gpu,ResourceStates.VertexAndConstantBuffer,ResourceStates.CopyDest);
        foreach(var range in ranges)
        {
            if(scene is null&&expanded is null)
            {
                CopyRange(gpu,checked(range.Start*stride),payload.Slice(checked(range.Start*stride),checked(range.Count*stride)));
                continue;
            }
            // Composed snapshots own independent immutable chunks. Staging consumes those
            // spans directly, including full uploads, without asking for a flat CPU copy.
            int source=range.Start,remaining=range.Count;
            while(remaining>0)
            {
                int chunkIndex=source/vertexChunkSize,chunkOffset=source%vertexChunkSize;
                int length;
                if(expanded is not null)
                {
                    var chunk=expanded.GetChunk(chunkIndex);length=Math.Min(remaining,chunk.Length-chunkOffset);
                    CopyRange(gpu,checked(source*stride),MemoryMarshal.AsBytes(chunk.Slice(chunkOffset,length)));
                }
                else
                {
                    var chunk=scene!.GetVertexChunk(chunkIndex);length=Math.Min(remaining,chunk.Length-chunkOffset);
                    CopyRange(gpu,checked(source*stride),MemoryMarshal.AsBytes(chunk.Slice(chunkOffset,length)));
                }
                source+=length;remaining-=length;
            }
        }
        runtime.Commands.ResourceBarrierTransition(gpu,ResourceStates.CopyDest,ResourceStates.VertexAndConstantBuffer);
        changed.Revision=revision;fullPotentialBytes+=bytes;
        if(full)fullUploads++;else partialUploads++;
        runtime.BufferUploads++;return gpu;
    }
    private void CopyRange(ID3D12Resource destination,int offset,ReadOnlySpan<byte> bytes)
    {
        var upload=staging.Write(bytes);
        runtime.Commands.CopyBufferRegion(destination,(ulong)offset,upload.Resource,upload.Offset,(ulong)bytes.Length);
        copyCalls++;copiedBytes+=bytes.Length;
    }
    private static IReadOnlyList<SceneVertexRange> ChangedRanges(object current,object previous,int count)
        =>(current,previous) switch
        {
            (SceneVertexRevision next,SceneVertexRevision prior)=>next.GetChangedRanges(prior),
            (D3D12ExpandedPositionRevision next,D3D12ExpandedPositionRevision prior)=>next.GetChangedRanges(prior),
            _=>new[]{new SceneVertexRange(0,count)}
        };
    private ID3D12Resource Hit(Entry entry)
    {KeepForFrame(entry);entry.RecordingFrame=runtime.FrameId;unchangedHits++;return entry.Buffer.Resource;}
    private void KeepForFrame(Entry entry)
    {
        if(entry.Owner.LastFrameId==runtime.FrameId)return;
        runtime.Own(entry.Owner.Lease());entry.Owner.LastFrameId=runtime.FrameId;
    }
    internal void Remove(string stream)
    {
        if(!streams.Remove(stream,out var slots))return;
        for(int slot=0;slot<slots.Length;slot++){slots[slot]?.Owner.Dispose();slots[slot]=null;}
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        foreach(var slots in streams.Values)
            for(int slot=0;slot<slots.Length;slot++){slots[slot]?.Owner.Dispose();slots[slot]=null;}
        streams.Clear();
    }
}
