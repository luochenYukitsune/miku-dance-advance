using System.Runtime.CompilerServices;

namespace Mmd.Modern.Core;

/// <summary>A conservative contiguous range of changed vertex payload, in vertex units.</summary>
public readonly record struct SceneVertexRange(int Start, int Count);

/// <summary>Immutable lineage for owned vertex bytes. Comparisons work between arbitrary retained
/// revisions, including skipped frames and branches. Indices, World and render state are independent.
/// This metadata retains no snapshot, vertex array, parent revision or earlier frame.</summary>
public sealed class SceneVertexRevision
{
    internal const int ChunkSize = 4096;
    private static readonly ConditionalWeakTable<object, SceneVertexRevision> revisions = new();
    private readonly object[] chunks;
    public int VertexCount { get; }
    /// <summary>True for validated Core range compositions. Publicly supplied complete payloads
    /// remain conservative roots; metadata wrappers preserve their source's status.</summary>
    public bool SupportsIncrementalUpdates { get; }

    private SceneVertexRevision(int vertexCount, object[] chunks, bool supportsIncrementalUpdates)
    { VertexCount = vertexCount; this.chunks = chunks; SupportsIncrementalUpdates = supportsIncrementalUpdates; }

    internal static SceneVertexRevision For(object storage, int vertexCount)
    {
        if (revisions.TryGetValue(storage, out var revision)) return revision;
        return revisions.GetValue(storage, _ => CreateRoot(vertexCount));
    }

    private static SceneVertexRevision CreateRoot(int vertexCount)
    {
        int count = vertexCount / ChunkSize + (vertexCount % ChunkSize == 0 ? 0 : 1);
        var chunks = new object[count];
        for (int index = 0; index < count; index++) chunks[index] = new object();
        return new(vertexCount, chunks, supportsIncrementalUpdates: false);
    }

    /// <summary>Called only for fresh Core-owned storage after all changed values validate.</summary>
    internal static void RegisterComposed(object baseline, object composed, int vertexCount,
        IReadOnlyList<(int Start, int Count)> ranges)
    {
        if (ReferenceEquals(baseline, composed)) return;
        var previous = For(baseline, vertexCount);
        var chunks = previous.chunks.ToArray();
        foreach (var range in ranges)
        {
            int first = range.Start / ChunkSize, last = (range.Start + range.Count - 1) / ChunkSize;
            for (int index = first; index <= last; index++) chunks[index] = new object();
        }
        revisions.Add(composed, new(vertexCount, chunks, supportsIncrementalUpdates: true));
    }

    /// <summary>Returns ordered, disjoint ranges covering every potentially changed vertex.
    /// Chunks touched by a composition are conservative changes even when their values compare equal.
    /// A different vertex count or unrelated root requires the full current payload.</summary>
    public IReadOnlyList<SceneVertexRange> GetChangedRanges(SceneVertexRevision previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (VertexCount == 0 || ReferenceEquals(this, previous)) return Array.Empty<SceneVertexRange>();
        if (VertexCount != previous.VertexCount)
            return Array.AsReadOnly(new[] { new SceneVertexRange(0, VertexCount) });
        var ranges = new List<SceneVertexRange>();
        int index = 0;
        while (index < chunks.Length)
        {
            if (ReferenceEquals(chunks[index], previous.chunks[index])) { index++; continue; }
            int start = index++ * ChunkSize;
            while (index < chunks.Length && !ReferenceEquals(chunks[index], previous.chunks[index])) index++;
            int end = (int)Math.Min((long)index * ChunkSize, VertexCount);
            ranges.Add(new(start, end - start));
        }
        return ranges.Count == 0 ? Array.Empty<SceneVertexRange>() : Array.AsReadOnly(ranges.ToArray());
    }
}
