using Vortice.Direct3D12;

namespace Mmd.Modern.Rendering.D3D12;

// The callback must finish all writes before returning and must not retain the
// span or reenter the pool or its owning recording/cache lifecycle.
internal delegate void D3D12BufferUploadWriter(Span<byte> destination);

internal readonly record struct D3D12BufferUploadStatistics(long Copies,long CopiedBytes,long PageCreations,long PageMappings,
    int RetainedPages,long RetainedBytes,int LivePages,long LiveBytes,int LiveTransientPages)
{
    public int RetainedLargePages {get;init;}
    public long RetainedLargeBytes {get;init;}
    public int LiveLargePages {get;init;}
    public long LargePageCreations {get;init;}
}

/// <summary>CPU-only staging for buffer copies. Begin has already waited the selected slot's
/// fence before its cursor is reset. Every copy gets a distinct source range for the entire
/// recording, including same-list immutable-buffer replacements. DEFAULT owners are unchanged.</summary>
internal sealed class D3D12BufferUploadStaging(D3D12GpuRuntime runtime):IDisposable
{
    internal const int MinimumPageBytes=1024*1024;
    internal const int MaximumRetainedPageBytes=16*1024*1024;
    internal const long MaximumRetainedBytesPerSlot=32L*1024*1024;
    // One opt-in GI triangle upload per slot, in addition to the unchanged small-page pool.
    // This caps retained large mappings, not transient same-list demand or all live resources.
    internal const int MaximumLargeRetainedPageBytes=128*1024*1024;
    internal readonly record struct Allocation(ID3D12Resource Resource,ulong Offset);

    private sealed class Page:IDisposable
    {
        // Large writes share memory bandwidth. More workers did not help the PR01 workload.
        private static readonly ParallelOptions LargeWriteOptions=new()
        {
            MaxDegreeOfParallelism=Math.Min(4,Environment.ProcessorCount),
            TaskScheduler=TaskScheduler.Default
        };

        internal ID3D12Resource Resource {get;}
        internal int Capacity {get;}
        internal int Offset;
        private readonly nint address;
        private readonly Action released;
        private bool disposed;

        internal unsafe Page(ID3D12Device device,int capacity,Action released)
        {
            Capacity=capacity;this.released=released;
            Resource=device.CreateCommittedResource(HeapType.Upload,ResourceDescription.Buffer((ulong)capacity),ResourceStates.GenericRead);
            try
            {
                var mapped=Resource.Map<byte>(0,capacity);
                fixed(byte* pointer=mapped)address=(nint)pointer;
            }
            catch{Resource.Dispose();throw;}
        }
        internal unsafe void Write(int offset,ReadOnlySpan<byte> bytes)
            =>bytes.CopyTo(new Span<byte>((void*)(address+offset),bytes.Length));
        internal unsafe void WriteLarge(ReadOnlySpan<byte> bytes)
        {
            const int blockSize=4*1024*1024;
            int length=bytes.Length;
            if(LargeWriteOptions.MaxDegreeOfParallelism==1||length<8*blockSize)
            {
                Write(0,bytes);
                return;
            }
            fixed(byte* source=bytes)
            {
                nint sourceAddress=(nint)source;
                // The mapped page stays leased and the source stays pinned until every block is written.
                Parallel.For(0,(length+blockSize-1)/blockSize,LargeWriteOptions,block=>
                {
                    int offset=block*blockSize;
                    int count=Math.Min(blockSize,length-offset);
                    new ReadOnlySpan<byte>((void*)(sourceAddress+offset),count)
                        .CopyTo(new Span<byte>((void*)(address+offset),count));
                    // Publish this worker's write-combined stores before it joins the recorder.
                    Thread.MemoryBarrier();
                });
            }
        }
        internal unsafe void Write(int offset,int length,D3D12BufferUploadWriter writer)
            =>writer(new Span<byte>((void*)(address+offset),length));
        public void Dispose()
        {
            if(disposed)return;disposed=true;
            try{Resource.Unmap(0);}finally{Resource.Dispose();released();}
        }
    }
    private sealed class Slot
    {
        internal readonly List<D3D12RetainedResource> Pages=[];
        internal long FrameId=-1,RetainedBytes;
        internal D3D12RetainedResource? LargePage;
        internal bool LargeUsed;
    }

    private readonly Slot[] slots=Enumerable.Range(0,D3D12SsaaGiMemoryBudget.MaximumInFlightFrames).Select(_=>new Slot()).ToArray();
    private long copies,copiedBytes,pageCreations,pageMappings,liveBytes;
    private int livePages,liveTransientPages;
    private int liveLargePages;
    private long largePageCreations;
    private bool disposed;
    private int operationActive;
    private readonly record struct Reservation(Page Page,int Offset);
    internal D3D12BufferUploadStatistics Statistics=>new(copies,copiedBytes,pageCreations,pageMappings,
        slots.Sum(slot=>slot.Pages.Count+(slot.LargePage is null?0:1)),
        slots.Sum(slot=>slot.RetainedBytes+(slot.LargePage is null?0:((Page)slot.LargePage.Value).Capacity)),livePages,liveBytes,liveTransientPages)
        {RetainedLargePages=slots.Count(slot=>slot.LargePage is not null),
         RetainedLargeBytes=slots.Sum(slot=>slot.LargePage is null?0L:((Page)slot.LargePage.Value).Capacity),
         LiveLargePages=liveLargePages,LargePageCreations=largePageCreations};

    // Visit even cache-hit/empty recordings so a no-longer-changing scene cannot pin old big mappings.
    internal void BeginFrame()
    {
        Enter();try{CurrentSlot();}finally{Exit();}
    }
    private void Enter()
    {
        if(Interlocked.CompareExchange(ref operationActive,1,0)!=0)
            throw new InvalidOperationException("Buffer upload staging cannot be reentered.");
    }
    private void Exit()=>Volatile.Write(ref operationActive,0);
    private Slot CurrentSlot()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(!runtime.IsRecording)throw new InvalidOperationException("Buffer uploads require an open GPU frame.");
        var slot=slots[runtime.SlotIndex];
        if(slot.FrameId!=runtime.FrameId)
        {
            // Pages unused by the previous visit to this slot do not become permanent
            // history after a large frame. Its old fence and leases have already retired.
            for(int i=slot.Pages.Count-1;i>=0;i--)
            {
                var page=(Page)slot.Pages[i].Value;
                if(page.Offset==0)RemovePage(slot,i);
                else page.Offset=0;
            }
            if(!slot.LargeUsed)RemoveLargePage(slot);
            slot.LargeUsed=false;
            slot.FrameId=runtime.FrameId;
        }
        return slot;
    }
    internal Allocation Write(ReadOnlySpan<byte> bytes)
    {
        Enter();try{return WriteCore(bytes);}finally{Exit();}
    }
    private Allocation WriteCore(ReadOnlySpan<byte> bytes)
    {
        var slot=CurrentSlot();
        if(bytes.IsEmpty)throw new ArgumentException("A buffer upload must contain data.",nameof(bytes));
        var reserved=Reserve(slot,bytes.Length);
        reserved.Page.Write(reserved.Offset,bytes);
        copies++;copiedBytes+=bytes.Length;
        return new(reserved.Page.Resource,(ulong)reserved.Offset);
    }
    internal Allocation Write(int byteCount,D3D12BufferUploadWriter writer)
    {
        Enter();
        try
        {
            if(byteCount<=0)throw new ArgumentOutOfRangeException(nameof(byteCount));
            ArgumentNullException.ThrowIfNull(writer);
            var reserved=Reserve(CurrentSlot(),byteCount);
            // The reservation is consumed before the callback. A throwing writer
            // cannot cause the partially written region to be reused in this list.
            reserved.Page.Write(reserved.Offset,byteCount,writer);
            copies++;copiedBytes+=byteCount;
            return new(reserved.Page.Resource,(ulong)reserved.Offset);
        }
        finally{Exit();}
    }
    private Reservation Reserve(Slot slot,int length)
    {
        if(length>MaximumRetainedPageBytes)return ReserveTransient(length);
        int aligned=(length+3)&~3;
        D3D12RetainedResource? selected=null;Page? selectedPage=null;int bestRemaining=int.MaxValue;
        // A large request can skip a partially used page without consuming its tail.
        // Best fit also keeps small recordings on smaller pages so unused big pages trim.
        foreach(var retained in slot.Pages)
        {
            var page=(Page)retained.Value;int remaining=page.Capacity-page.Offset;
            if(remaining<aligned||remaining>=bestRemaining)continue;
            selected=retained;selectedPage=page;bestRemaining=remaining;
            if(remaining==aligned)break;
        }
        if(selected is not null)return ReservePage(selected,selectedPage!,aligned);
        // Only unused, too-small pages can be replaced in this recording. Partial
        // pages keep every queued source range and remain available to later writes.
        for(int i=slot.Pages.Count-1;i>=0;i--)
            if(((Page)slot.Pages[i].Value).Offset==0)RemovePage(slot,i);
        int capacity=MinimumPageBytes;
        while(capacity<length)capacity*=2;
        if(slot.RetainedBytes+capacity>MaximumRetainedBytesPerSlot)return ReserveTransient(length);
        var created=CreatePage(capacity,false);var owner=new D3D12RetainedResource(created,created);
        try{slot.Pages.Add(owner);}catch{owner.Dispose();throw;}
        slot.RetainedBytes+=capacity;
        return ReservePage(owner,created,aligned);
    }
    internal Allocation WriteLargeRetained(ReadOnlySpan<byte> bytes)
    {
        Enter();try{return WriteLargeRetainedCore(bytes);}finally{Exit();}
    }
    private Allocation WriteLargeRetainedCore(ReadOnlySpan<byte> bytes)
    {
        var slot=CurrentSlot();
        if(bytes.Length<=MaximumRetainedPageBytes||bytes.Length>MaximumLargeRetainedPageBytes)
        {
            ClearLargeRetainedPagesCore();
            return WriteCore(bytes);
        }
        // Changed input sizes must not leave three obsolete largest-ever pages cached.
        int length=bytes.Length;
        if(slots.Any(value=>value.LargePage is not null&&((Page)value.LargePage.Value).Capacity!=length))
            ClearLargeRetainedPagesCore();
        // Earlier CopyBufferRegion commands still read their source until Submit completes.
        if(slot.LargeUsed)return Transient(bytes);
        if(slot.LargePage is null)
        {
            var created=CreatePage(bytes.Length,false,true);
            slot.LargePage=new(created,created);
        }
        var retained=slot.LargePage;var page=(Page)retained.Value;
        runtime.Own(retained.Lease());retained.LastFrameId=runtime.FrameId;slot.LargeUsed=true;
        page.WriteLarge(bytes);copies++;copiedBytes+=bytes.Length;
        return new(page.Resource,0);
    }
    internal void ClearLargeRetainedPages()
    {
        Enter();try{ClearLargeRetainedPagesCore();}finally{Exit();}
    }
    private void ClearLargeRetainedPagesCore()
    {
        foreach(var slot in slots)RemoveLargePage(slot);
        // Do not clear LargeUsed: a source already recorded in this list remains in use.
    }
    private Reservation ReservePage(D3D12RetainedResource retained,Page page,int aligned)
    {
        // Cache disposal only releases its owner. The mapped resource remains alive
        // until every recorded copy in this slot has submitted/retired or aborted.
        if(retained.LastFrameId!=runtime.FrameId)
        {
            var lease=retained.Lease();
            try{runtime.Own(lease);}catch{lease.Dispose();throw;}
            retained.LastFrameId=runtime.FrameId;
        }
        int offset=page.Offset;page.Offset+=aligned;
        return new(page,offset);
    }
    private Reservation ReserveTransient(int length)
    {
        var page=CreatePage(length,true);
        try{runtime.Own(page);}catch{page.Dispose();throw;}
        return new(page,0);
    }
    private Allocation Transient(ReadOnlySpan<byte> bytes)
    {
        // Oversized uploads and excess same-frame demand stay frame-owned. They
        // cannot leave an unbounded largest-ever allocation in any retained slot.
        var reserved=ReserveTransient(bytes.Length);var page=reserved.Page;page.Write(0,bytes);
        copies++;copiedBytes+=bytes.Length;return new(page.Resource,0);
    }
    private Page CreatePage(int capacity,bool transient,bool large=false)
    {
        var page=new Page(runtime.Device,capacity,()=>
        {livePages--;liveBytes-=capacity;if(transient)liveTransientPages--;if(large)liveLargePages--;});
        pageCreations++;pageMappings++;livePages++;liveBytes+=capacity;
        if(transient)liveTransientPages++;
        if(large){largePageCreations++;liveLargePages++;}
        return page;
    }
    private static void RemovePage(Slot slot,int index)
    {
        var retained=slot.Pages[index];slot.RetainedBytes-=((Page)retained.Value).Capacity;
        slot.Pages.RemoveAt(index);retained.Dispose();
    }
    private static void RemoveLargePage(Slot slot)
    {var retained=slot.LargePage;slot.LargePage=null;retained?.Dispose();}
    public void Dispose()
    {
        Enter();
        try
        {
            if(disposed)return;disposed=true;
            foreach(var slot in slots)
            {foreach(var page in slot.Pages)page.Dispose();slot.Pages.Clear();slot.RetainedBytes=0;RemoveLargePage(slot);}
        }
        finally{Exit();}
    }
}
