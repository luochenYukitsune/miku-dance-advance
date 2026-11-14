using System.Numerics;
using System.Runtime.CompilerServices;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Proof for an actual private immutable expanded-position output. The weak key owns
/// neither this metadata nor a prior frame through a strong history chain; metadata contains only
/// opaque source chunks and copied numeric expansion inputs, never an array, frame or parent.</summary>
internal sealed class D3D12ExpandedPositionRevision
{
    private static readonly ConditionalWeakTable<Vector4[], D3D12ExpandedPositionRevision> revisions = new();
    private readonly SceneVertexRevision source;
    private readonly int modelBaseBits;
    private readonly int[] widths;
    internal int VertexCount => source.VertexCount;
    internal bool SupportsIncrementalUpdates => source.SupportsIncrementalUpdates;

    private D3D12ExpandedPositionRevision(SceneVertexRevision source, int modelBaseBits, ReadOnlySpan<int> widths)
    { this.source = source; this.modelBaseBits = modelBaseBits; this.widths = widths.ToArray(); }

    /// <summary>Unknown and caller-created arrays never acquire a conservative root implicitly.</summary>
    internal static bool TryGet(Vector4[] positions, out D3D12ExpandedPositionRevision? revision)
        => revisions.TryGetValue(positions, out revision);

    internal static D3D12ExpandedPositionRevision Create(SceneVertexRevision source,int modelBaseBits,ReadOnlySpan<int> widths)
        =>new(source,modelBaseBits,widths);

    internal static void RegisterOwned(Vector4[] positions,D3D12ExpandedPositionRevision revision)
    {
        if(positions.Length!=revision.VertexCount)throw new ArgumentException("Expanded output must preserve source vertex count.",nameof(positions));
        revisions.Add(positions,revision);
    }

    /// <summary>Only the outline producer calls this after it has completely initialized a fresh
    /// private output. Reused equal output retains its older, still-conservative source proof.</summary>
    internal static void RegisterOwned(Vector4[] positions, SceneVertexRevision source, int modelBaseBits, ReadOnlySpan<int> widths)
    {
        if (positions.Length != source.VertexCount) throw new ArgumentException("Expanded output must preserve source vertex count.", nameof(positions));
        revisions.Add(positions, new(source, modelBaseBits, widths));
    }

    /// <summary>Compare the actual prior output, allowing skipped frames, reverse traversal and
    /// branches. Different numeric expansion inputs require the complete current stream.</summary>
    internal IReadOnlyList<SceneVertexRange> GetChangedRanges(D3D12ExpandedPositionRevision previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (ReferenceEquals(this, previous) || VertexCount == 0) return Array.Empty<SceneVertexRange>();
        if (VertexCount != previous.VertexCount || modelBaseBits != previous.modelBaseBits
            || !widths.AsSpan().SequenceEqual(previous.widths))
            return Array.AsReadOnly(new[] { new SceneVertexRange(0, VertexCount) });
        return source.GetChangedRanges(previous.source);
    }
}
