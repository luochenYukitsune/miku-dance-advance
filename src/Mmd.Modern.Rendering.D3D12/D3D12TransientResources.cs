using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Fence-protected reuse of per-frame textures, buffers and descriptor heaps, matched by heap type and
/// the complete description. An entry is handed out again only after the fence of its last recording completed
/// and no output lease holds it. Aborted recordings restore the previous fence and state. Entries unused for one
/// full recording-slot cycle, and every idle entry after WaitForIdle, are released.</summary>
internal sealed class D3D12TransientResources(D3D12GpuRuntime runtime) : IDisposable
{
    private readonly record struct ResourceKey(HeapType Heap, ResourceDimension Dimension, ulong Alignment, ulong Width, int Height,
        ushort DepthOrArraySize, ushort MipLevels, Format Format, int SampleCount, int SampleQuality, TextureLayout Layout, ResourceFlags Flags);
    private readonly record struct HeapKey(DescriptorHeapType Type, int Count, DescriptorHeapFlags Flags);
    private sealed class Entry(object key, IDisposable value, ResourceStates state, int lastSlot)
    {
        internal readonly object Key = key;
        internal readonly IDisposable Value = value;
        internal int LastSlot = lastSlot;
        internal ulong Fence;
        internal long LastFrame = -1;
        internal long LastSubmission = -1;
        internal bool Detached;
        // State after the last submitted use. End resolves the state of the open or last submitted use.
        internal ResourceStates State = state;
        internal Func<ResourceStates>? End;
    }
    private readonly object gate = new();
    private readonly List<Entry> entries = [];
    private readonly List<Entry> recorded = [];
    private long recordedFrame = -1, maintainedFrame = -1;
    private int creations, resourceCreations, reuses, detached;
    private bool disposed;
    internal long TotalCreations { get { lock (gate) return totalCreations; } }
    private long totalCreations;
    internal int FrameCreations { get { lock (gate) return recordedFrame == runtime.FrameId ? creations : 0; } }
    internal int FrameResourceCreations { get { lock (gate) return recordedFrame == runtime.FrameId ? resourceCreations : 0; } }
    internal int FrameReuses { get { lock (gate) return recordedFrame == runtime.FrameId ? reuses : 0; } }
    internal int LiveCount { get { lock (gate) return disposed ? detached : entries.Count; } }
    internal IReadOnlyList<(ulong Width, int Height)> LiveTextureDimensions()
    {
        lock (gate)
        {
            var result = new List<(ulong, int)>();
            foreach (var entry in entries)
                if (entry.Key is ResourceKey { Dimension: ResourceDimension.Texture2D } key)
                    result.Add((key.Width, key.Height));
            return result;
        }
    }

    /// <summary>Returns a resource in <paramref name="state"/> at this point of the open recording. Its previous
    /// contents are undefined; callers clear or completely overwrite it before reading.</summary>
    internal ID3D12Resource Resource(D3D12RecordingContext context, HeapType heap, ResourceDescription description, ResourceStates state)
    {
        var key = new ResourceKey(heap, description.Dimension, description.Alignment, description.Width, description.Height,
            description.DepthOrArraySize, description.MipLevels, description.Format, description.SampleDescription.Count,
            description.SampleDescription.Quality, description.Layout, description.Flags);
        lock (gate)
        {
            var entry = Acquire(key, () => runtime.Device.CreateCommittedResource(heap, description, state), state, isResource: true, shareAcrossSlots: heap == HeapType.Readback, out bool reused);
            var resource = (ID3D12Resource)entry.Value;
            // Upload and readback resources keep their creation state; default-heap buffers decay to COMMON after use.
            bool fixedState = heap != HeapType.Default;
            if (reused && !fixedState && entry.State != state) context.Commands.ResourceBarrierTransition(resource, entry.State, state);
            entry.End = !fixedState && description.Dimension == ResourceDimension.Buffer ? static () => ResourceStates.Common : () => state;
            return resource;
        }
    }

    internal ID3D12DescriptorHeap Heap(DescriptorHeapDescription description)
    {
        lock (gate)
            return (ID3D12DescriptorHeap)Acquire(new HeapKey(description.Type, description.DescriptorCount, description.Flags),
                () => runtime.Device.CreateDescriptorHeap(description), ResourceStates.Common, isResource: false, shareAcrossSlots: false, out _).Value;
    }

    /// <summary>The pooled texture of <paramref name="target"/> ends this recording in the target's tracked state.</summary>
    internal D3D12GpuTarget Track(D3D12GpuTarget target)
    {
        lock (gate)
        {
            var entry = Recorded(target.Resource) ?? throw new InvalidOperationException("The target does not belong to this recording's transient pool.");
            if (entry.Key is not ResourceKey { Heap: HeapType.Default } key || key.Dimension == ResourceDimension.Buffer)
                throw new InvalidOperationException("Only default-heap textures are frame targets.");
            entry.End = () => target.State;
            return target;
        }
    }

    /// <summary>Removes a pooled resource of the last recording from reuse while an external consumer holds it.
    /// The returned release queues it again behind the latest submitted (or open) recording. Unpooled resources return null.</summary>
    internal Action? Detach(ID3D12Resource resource)
    {
        lock (gate)
        {
            if (Recorded(resource) is not { } entry || entry.Detached) return null;
            entry.Detached = true;
            detached++;
            int released = 0;
            return () =>
            {
                if (Interlocked.Exchange(ref released, 1) != 0) return;
                lock (gate)
                {
                    detached--;
                    entry.Detached = false;
                    if (disposed) { entry.Value.Dispose(); return; }
                    entry.Fence = Math.Max(entry.Fence, runtime.IsRecording ? runtime.SubmittedFence + 1 : runtime.SubmittedFence);
                    entry.LastSubmission = runtime.Submissions;
                }
            };
        }
    }

    /// <summary>Releases every entry whose last recording completed. Leased entries stay with their consumers.</summary>
    internal void ReleaseIdle()
    {
        lock (gate)
        {
            ulong completed = runtime.CompletedFence;
            for (int i = entries.Count - 1; i >= 0; i--)
                if (!entries[i].Detached && entries[i].Fence <= completed) { entries[i].Value.Dispose(); entries.RemoveAt(i); }
            recorded.RemoveAll(entry => !entries.Contains(entry));
        }
    }

    private Entry? Recorded(object value)
    {
        if (recordedFrame != runtime.FrameId) return null;
        foreach (var entry in recorded) if (ReferenceEquals(entry.Value, value)) return entry;
        return null;
    }

    private Entry Acquire(object key, Func<IDisposable> create, ResourceStates state, bool isResource, bool shareAcrossSlots, out bool reused)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!runtime.IsRecording) throw new InvalidOperationException("Transient resources require an open GPU frame.");
        long frame = runtime.FrameId;
        long submission = runtime.Submissions;
        if (recordedFrame != frame) { recorded.Clear(); recordedFrame = frame; creations = resourceCreations = reuses = 0; }
        ulong completed = runtime.CompletedFence;
        if (completed == ulong.MaxValue)
        {
            for (int i = entries.Count - 1; i >= 0; i--)
                if (!entries[i].Detached) { entries[i].Value.Dispose(); entries.RemoveAt(i); }
            recorded.Clear();
            throw new InvalidOperationException("D3D12 device removed.");
        }
        if (maintainedFrame != frame)
        {
            // Resize generations and disabled passes stop matching; release them after one slot cycle.
            maintainedFrame = frame;
            for (int i = entries.Count - 1; i >= 0; i--)
                if (!entries[i].Detached && entries[i].Fence <= completed && entries[i].LastSubmission < submission - D3D12SsaaGiMemoryBudget.MaximumInFlightFrames)
                { entries[i].Value.Dispose(); entries.RemoveAt(i); }
        }
        Entry? found = null;
        int slot = runtime.SlotIndex;
        foreach (var entry in entries)
        {
            if (entry.Detached || entry.LastFrame == frame || entry.Fence > completed || !entry.Key.Equals(key)) continue;
            if (entry.LastSlot == slot) { found = entry; break; }
            if (shareAcrossSlots) found ??= entry;
        }
        reused = found is not null;
        if (found is null)
        {
            found = new(key, create(), state, slot) { LastSubmission = submission };
            entries.Add(found); creations++; totalCreations++;
            if (isResource) resourceCreations++;
        }
        else
        {
            reuses++;
            if (found.End is { } end) found.State = end(); // That recording was submitted and has completed.
        }
        var entry0 = found; var previousState = found.State; ulong previousFence = found.Fence; long previousFrame = found.LastFrame; long previousSubmission = found.LastSubmission; int previousSlot = found.LastSlot;
        found.Fence = runtime.SubmittedFence + 1; found.LastFrame = frame; found.LastSubmission = submission; found.LastSlot = slot; found.End = null;
        recorded.Add(found);
        // Commands of an aborted recording never execute: restore the fence and state from before it.
        runtime.OnAbort(() =>
        {
            lock (gate) { entry0.Fence = previousFence; entry0.LastFrame = previousFrame; entry0.LastSubmission = previousSubmission; entry0.LastSlot = previousSlot; entry0.State = previousState; entry0.End = null; recorded.Remove(entry0); }
        });
        return found;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var entry in entries) if (!entry.Detached) entry.Value.Dispose();
            entries.Clear(); recorded.Clear();
        }
    }
}
