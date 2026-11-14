using System.Numerics;
using System.Threading;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Private immutable expanded geometry. Publications share unchanged chunk payloads,
/// never a parent output. Only explicit compatibility readers allocate the complete array.</summary>
internal sealed class D3D12ExpandedPositions
{
    internal const int ChunkSize=4096;
    private readonly Vector4[][] chunks;
    private Vector4[]? flat;
    private int[]? canonicalChunks;
    internal int Length {get;}
    internal int ChunkCount=>chunks.Length;
    internal bool IsMaterialized=>Volatile.Read(ref flat) is not null;
    internal D3D12ExpandedPositionRevision Revision {get;}

    // The producer transfers a completely initialized private table and fresh changed payloads.
    internal D3D12ExpandedPositions(int count,Vector4[][] ownedChunks,D3D12ExpandedPositionRevision revision)
    {Length=count;chunks=ownedChunks;Revision=revision;}

    internal Vector4 this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            if(index>=Length)throw new ArgumentOutOfRangeException(nameof(index));
            return chunks[index/ChunkSize][index%ChunkSize];
        }
    }
    internal ReadOnlySpan<Vector4> GetChunk(int index)=>chunks[index];
    // These chunks are privately owned and immutable. Large reductions can validate
    // each referenced payload once; short ranges only consume an existing proof.
    // A bad/unknown chunk still requires the original per-index validation, since a
    // particular material need not reference the bad vertex at all.
    internal bool IsCanonicalChunk(int index,bool validate)
    {
        var states=Volatile.Read(ref canonicalChunks);
        if(states is null)
        {
            if(!validate)return false;
            var created=new int[chunks.Length];
            states=Interlocked.CompareExchange(ref canonicalChunks,created,null)??created;
        }
        int state=Volatile.Read(ref states[index]);
        if(state!=0)return state>0;
        if(!validate)return false;
        bool canonical=true;
        foreach(var p in chunks[index])
            if(!float.IsFinite(p.X)||!float.IsFinite(p.Y)||!float.IsFinite(p.Z)||p.W!=1)
            {canonical=false;break;}
        Volatile.Write(ref states[index],canonical?1:-1);
        return canonical;
    }
    // Only the producer receives a new table; it clones a payload before every write.
    internal Vector4[][] CopyChunkTable()=>chunks.ToArray();
    internal bool SharesChunk(D3D12ExpandedPositions other,int index)=>ReferenceEquals(chunks[index],other.chunks[index]);
    internal void CopyTo(Span<Vector4> destination)
    {
        if(destination.Length!=Length)throw new ArgumentException("One destination per expanded vertex is required.",nameof(destination));
        int offset=0;
        foreach(var chunk in chunks){chunk.AsSpan().CopyTo(destination.Slice(offset,chunk.Length));offset+=chunk.Length;}
    }
    internal Vector4[] Materialize()
    {
        var found=Volatile.Read(ref flat);if(found is not null)return found;
        var values=new Vector4[Length];CopyTo(values);
        D3D12ExpandedPositionRevision.RegisterOwned(values,Revision);
        return Interlocked.CompareExchange(ref flat,values,null)??values;
    }
}
