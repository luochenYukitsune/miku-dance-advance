using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Conservative per-material input lineage for native shadow face reuse. A material receives a new
/// version whenever a vertex its indices reference may differ, or the index/material topology changes.
/// Only the latest scene is retained; its unchanged chunks are shared with the next frame.</summary>
internal sealed class D3D12NativeShadowCasterVersions
{
    private SceneSnapshot? previous;
    private (int First,int Last)[] spans=[];
    private long[] versions=[];
    private long next;

    internal long[] Update(SceneSnapshot scene)
    {
        if(previous is null||!SameTopology(previous,scene))
        {
            spans=Spans(scene);versions=new long[spans.Length];
            for(int i=0;i<versions.Length;i++)versions[i]=++next;
        }
        else
        {
            var prevStatic=previous.StaticVertexView;
            var currStatic=scene.StaticVertexView;
            if(!ReferenceEquals(prevStatic.VertexRevision,currStatic.VertexRevision))
            {
                // Unrelated roots report every chunk; only Core compositions are worth an exact comparison.
                bool compare=currStatic.VertexRevision.SupportsIncrementalUpdates;
                foreach(var range in currStatic.VertexRevision.GetChangedRanges(prevStatic.VertexRevision))
                {
                    if(!compare){Invalidate(range.Start,range.Start+range.Count-1);continue;}
                    int end=range.Start+range.Count;
                    for(int start=range.Start;start<end;)
                    {
                        int chunk=start/SceneVertexRevision.ChunkSize,offset=start%SceneVertexRevision.ChunkSize;
                        var before=prevStatic.GetVertexChunk(chunk);var after=currStatic.GetVertexChunk(chunk);
                        int count=Math.Min(end-start,after.Length-offset);
                        before=before.Slice(offset,count);after=after.Slice(offset,count);
                        // Exact payload bytes, including signed zero, normal and UV, decide the change.
                        int first=MemoryMarshal.AsBytes(before).CommonPrefixLength(MemoryMarshal.AsBytes(after))/Unsafe.SizeOf<SceneVertex>();
                        if(first<count)
                        {
                            int last=count-1;
                            while(last>first&&MemoryMarshal.AsBytes(before.Slice(last,1)).SequenceEqual(MemoryMarshal.AsBytes(after.Slice(last,1))))last--;
                            Invalidate(start+first,start+last);
                        }
                        start+=count;
                    }
                }
            }
            var prevRanges=previous.DeformationRanges;
            var currRanges=scene.DeformationRanges;
            if(prevRanges.Count>0||currRanges.Count>0)
            {
                for(int i=0;i<prevRanges.Count;i++)
                {
                    var p=prevRanges[i];
                    if(p.Count>0&&FindRange(currRanges,p.Start,p.Count) is null)
                        Invalidate(p.Start,p.Start+p.Count-1);
                }
                for(int i=0;i<currRanges.Count;i++)
                {
                    var nextRange=currRanges[i];
                    if(nextRange.Count<=0)continue;
                    var prevRange=FindRange(prevRanges,nextRange.Start,nextRange.Count);
                    if(prevRange is null||!SameMatrixBits(prevRange.World,nextRange.World)||!prevRange.Deformation.HasSameInputs(nextRange.Deformation))
                        Invalidate(nextRange.Start,nextRange.Start+nextRange.Count-1);
                }
            }
        }
        previous=scene;
        return versions;
    }

    private static SceneDeformationRange? FindRange(IReadOnlyList<SceneDeformationRange> ranges,int start,int count)
    {
        for(int i=0;i<ranges.Count;i++)
            if(ranges[i].Start==start&&ranges[i].Count==count)return ranges[i];
        return null;
    }

    private static bool SameMatrixBits(Matrix4x4 a,Matrix4x4 b)
        =>MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref a,1)).SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref b,1)));

    internal void Reset(){previous=null;spans=[];versions=[];}

    private void Invalidate(int first,int last)
    {
        for(int i=0;i<spans.Length;i++)
            if(spans[i].First<=last&&first<=spans[i].Last)versions[i]=++next;
    }

    private static (int First,int Last)[] Spans(SceneSnapshot scene)
    {
        var result=new (int First,int Last)[scene.Materials.Count];var indices=scene.Indices;
        for(int m=0;m<result.Length;m++)
        {
            var material=scene.Materials[m];int first=int.MaxValue,last=-1;
            for(int i=material.StartIndex;i<material.StartIndex+material.IndexCount;i++)
            {int vertex=(int)indices[i];if(vertex<first)first=vertex;if(vertex>last)last=vertex;}
            result[m]=(first,last);
        }
        return result;
    }

    private static bool SameTopology(SceneSnapshot first,SceneSnapshot second)
    {
        if(first.VertexCount!=second.VertexCount||first.Materials.Count!=second.Materials.Count)return false;
        if(!D3D12ImmutableGeometry.SameStorage(first.Indices,second.Indices)&&!first.Indices.SequenceEqual(second.Indices))return false;
        if(ReferenceEquals(first.Materials,second.Materials))return true;
        for(int i=0;i<first.Materials.Count;i++)
            if(first.Materials[i].StartIndex!=second.Materials[i].StartIndex||first.Materials[i].IndexCount!=second.Materials[i].IndexCount)return false;
        return true;
    }
}

/// <summary>The exact recorded inputs of each face rectangle of one shadow atlas instance since its last full clear.
/// Pixels outside tracked faces with draws hold the clear value. Any abort or replacement forces a full clear.</summary>
internal sealed class D3D12NativeShadowFaceCache
{
    internal readonly record struct Rect(int X,int Y,int Size);

    // Unmanaged and padding-free: equality is exact bytes, never float equality (signed zero, NaN payloads).
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Draw(int Material,int StartIndex,int IndexCount,int DoubleSided,long Version,
        Matrix4x4 SphereRotation,Vector4 EgColor,Vector4 Options);

    internal sealed class Face(Matrix4x4 projection,Matrix4x4 world,object? additionalUvs,Draw[] draws,object?[] references)
    {
        internal readonly Matrix4x4 Projection=projection,World=world;
        internal readonly object? AdditionalUvs=additionalUvs;
        // Per draw: pipeline state, diffuse texture and (only when sampled) sphere texture.
        internal readonly Draw[] Draws=draws;
        internal readonly object?[] References=references;

        internal bool Matches(Matrix4x4 projection,Matrix4x4 world,object? additionalUvs,ReadOnlySpan<Draw> draws,ReadOnlySpan<object?> references)
        {
            if(!ReferenceEquals(AdditionalUvs,additionalUvs)||Draws.Length!=draws.Length||!Same(Projection,projection)||!Same(World,world))return false;
            if(!MemoryMarshal.AsBytes(Draws.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(draws)))return false;
            for(int i=0;i<References.Length;i++)if(!ReferenceEquals(References[i],references[i]))return false;
            return true;
        }
        private static bool Same(Matrix4x4 first,Matrix4x4 second)
            =>MemoryMarshal.AsBytes(new ReadOnlySpan<Matrix4x4>(in first)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<Matrix4x4>(in second)));
    }

    internal Dictionary<Rect,Face> Faces=[];
    // False until a full clear of this exact resource is recorded, and again after any aborted recording.
    internal bool Valid;

    private static readonly ConditionalWeakTable<D3D12ShadowTargets,D3D12NativeShadowFaceCache> caches=new();
    internal static D3D12NativeShadowFaceCache For(D3D12ShadowTargets targets)=>caches.GetValue(targets,_=>new());
}
