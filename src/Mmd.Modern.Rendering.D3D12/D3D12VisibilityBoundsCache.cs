using System.Diagnostics;
using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Bounded current local bounds. A topology owns a vertex-to-material map; fresh immutable
/// position streams are compared once. Sparse changes invalidate bounded original-index leaves;
/// dense changes and nonfinite surface inputs retain the original complete material reduction.</summary>
internal sealed class D3D12VisibilityBoundsCache
{
    internal const int IndexLeafSize=256;
    internal const long MaximumLeafCacheBytes=32L*1024*1024;
    internal const long MaximumInvalidationBlockMapBytes=4L*1024*1024;
    private const int SurfaceChunkSize=4096;
    private const int InvalidationBlockSize=256;

    private readonly record struct MaterialMap(int[] Offsets,int[] Materials,int[]? BlockOffsets,int[]? BlockMaterials);

    private sealed class Entry(SceneSnapshot scene,object? positions,long leafCacheByteLimit,long blockMapByteLimit)
    {
        internal SceneSnapshot Scene=scene;
        // These expanded streams are private renderer-owned immutable results, like SceneSnapshot's arrays.
        internal object? Positions=positions;
        internal OriginalVisibilityBounds[] Bounds=new OriginalVisibilityBounds[scene.Materials.Count];
        // 0: clean; 1: collecting sparse candidates; 2: a complete ordered scan is
        // already required. Dense incidences then need no further candidate calls/writes.
        internal readonly byte[] Dirty=new byte[scene.Materials.Count];
        internal readonly MaterialMap Map=BuildMap(scene,blockMapByteLimit);
        internal readonly IndexLeaves? Leaves=IndexLeaves.TryCreate(scene,leafCacheByteLimit);
    }

    // Every leaf is a consecutive slice of ONE material's original index sequence. No vertex
    // payload, parent output, or previous-frame chain is retained by this mutable private cache.
    private sealed class IndexLeaves
    {
        private readonly (int Start,int Count)[] ranges;
        private readonly int[] materialOffsets,vertexOffsets,vertexLeaves;
        private readonly int[] candidateOffsets,candidateCounts,candidateVertices;
        private readonly OriginalVisibilityBounds[] bounds;
        private readonly bool[] dirty,special,materialCurrent;

        private IndexLeaves(SceneSnapshot scene,int leafCount,int candidateCount)
        {
            ranges=new (int,int)[leafCount];materialOffsets=new int[scene.Materials.Count+1];
            bounds=new OriginalVisibilityBounds[leafCount];dirty=new bool[leafCount];special=new bool[leafCount];
            candidateOffsets=new int[scene.Materials.Count+1];candidateCounts=new int[scene.Materials.Count];
            candidateVertices=new int[candidateCount];materialCurrent=new bool[scene.Materials.Count];
            Array.Fill(dirty,true);
            int leaf=0;
            for(int material=0;material<scene.Materials.Count;material++)
            {
                materialOffsets[material]=leaf;var range=scene.Materials[material];
                candidateOffsets[material+1]=candidateOffsets[material]
                    +(range.IndexCount>IndexLeafSize?range.IndexCount/IndexLeafSize:0);
                if(range.IndexCount<=IndexLeafSize)candidateCounts[material]=-2;
                for(int offset=0;offset<range.IndexCount;offset+=IndexLeafSize)
                    ranges[leaf++]=(range.StartIndex+offset,Math.Min(IndexLeafSize,range.IndexCount-offset));
            }
            materialOffsets[^1]=leaf;
            var counts=new int[scene.VertexCount];var seen=new int[counts.Length];Array.Fill(seen,-1);
            for(int i=0;i<ranges.Length;i++)
            {
                var range=ranges[i];
                for(int j=range.Start;j<range.Start+range.Count;j++)
                {
                    int vertex=(int)scene.Indices[j];if(seen[vertex]==i)continue;
                    seen[vertex]=i;counts[vertex]++;
                }
            }
            vertexOffsets=new int[counts.Length+1];
            for(int i=0;i<counts.Length;i++)vertexOffsets[i+1]=checked(vertexOffsets[i]+counts[i]);
            vertexLeaves=new int[vertexOffsets[^1]];Array.Clear(counts);Array.Fill(seen,-1);
            for(int i=0;i<ranges.Length;i++)
            {
                var range=ranges[i];
                for(int j=range.Start;j<range.Start+range.Count;j++)
                {
                    int vertex=(int)scene.Indices[j];if(seen[vertex]==i)continue;
                    seen[vertex]=i;vertexLeaves[vertexOffsets[vertex]+counts[vertex]++]=i;
                }
            }
        }

        internal static IndexLeaves? TryCreate(SceneSnapshot scene,long byteLimit)
        {
            long references=0,leaves=0,candidates=0;bool hasSplitRange=false;
            foreach(var range in scene.Materials)
            {
                references+=range.IndexCount;leaves+=((long)range.IndexCount+IndexLeafSize-1)/IndexLeafSize;
                hasSplitRange|=range.IndexCount>IndexLeafSize;
                if(range.IndexCount>IndexLeafSize)candidates+=range.IndexCount/IndexLeafSize;
            }
            if(!hasSplitRange)return null;
            // Bound the extra resident arrays before allocating. U (deduplicated vertex/leaf
            // incidences) is at most references. Reserve array headers/alignment as well.
            long bytes=4L*(scene.VertexCount+1L)+4L*references+34L*leaves
                +13L*scene.Materials.Count+8L+4L*candidates+512;
            if(leaves>int.MaxValue||references>int.MaxValue||bytes>byteLimit)return null;
            return new(scene,(int)leaves,(int)candidates);
        }

        internal bool RecordChangedVertex(int material,int vertex)
        {
            int count=candidateCounts[material];
            // Saturated dense ranges and single-leaf ranges need only this read.
            if(count<0)return true;
            int start=candidateOffsets[material],capacity=candidateOffsets[material+1]-start;
            // One-leaf materials already require their complete ordered loop. Split
            // ranges retain only enough candidates to make a sparse scan worthwhile:
            // one full leaf per changed vertex must cost no more than the full range.
            if(count==capacity){candidateCounts[material]=-1;return true;}
            candidateVertices[start+count]=vertex;
            candidateCounts[material]=count+1;
            return false;
        }

        internal bool PrepareMaterial(int material,int indexCount,out int markedIncidences)
        {
            markedIncidences=0;
            int first=materialOffsets[material],end=materialOffsets[material+1];
            if(end-first<=1)
            {Array.Fill(dirty,true,first,end-first);return true;}
            int count=candidateCounts[material];candidateCounts[material]=0;
            if(count<0)
            {
                // A dense update never visits the vertex/leaf CSR. Its ordered full
                // loop leaves the old leaf contents unusable until a sparse rebuild.
                materialCurrent[material]=false;return false;
            }
            if(!materialCurrent[material])
            {
                Array.Fill(dirty,true,first,end-first);return true;
            }
            int scanned=0;
            for(int i=0;i<count;i++)
            {
                int vertex=candidateVertices[candidateOffsets[material]+i];
                // CSR lists are sorted by leaf, hence by material. Skip other
                // materials without repeatedly traversing shared vertices' lists.
                int start=vertexOffsets[vertex],stop=vertexOffsets[vertex+1];
                int low=start,high=stop;
                while(low<high)
                {int middle=low+(high-low)/2;if(vertexLeaves[middle]<first)low=middle+1;else high=middle;}
                for(int j=low;j<stop&&vertexLeaves[j]<end;j++)
                {
                    int leaf=vertexLeaves[j];markedIncidences++;
                    if(dirty[leaf])continue;
                    dirty[leaf]=true;scanned+=ranges[leaf].Count;
                }
                // Even one highly shared vertex can touch the entire material.
                if(scanned>=indexCount)
                {materialCurrent[material]=false;return false;}
            }
            return true;
        }

        internal OriginalVisibilityBounds MaterialBounds(SceneSnapshot scene,object? positions,int material,
            out int rebuilt,out int scanned,out bool fallback)
        {
            rebuilt=scanned=0;fallback=false;bool hasSpecial=false;
            var min=new Vector3(float.PositiveInfinity);var max=new Vector3(float.NegativeInfinity);
            for(int i=materialOffsets[material];i<materialOffsets[material+1];i++)
            {
                if(dirty[i])
                {
                    var range=ranges[i];
                    bounds[i]=IndexRange(scene,positions,range.Start,range.Count,out special[i],out int visited);
                    scanned+=visited;rebuilt++;dirty[i]=false;
                }
                hasSpecial|=special[i];
                // Preserve the index order, including ties between positive and negative zero.
                min=Vector3.Min(min,bounds[i].Minimum);max=Vector3.Max(max,bounds[i].Maximum);
            }
            materialCurrent[material]=true;
            if(!hasSpecial)return new(min,max);
            if(positions is not null)return new(new(float.NaN),new(float.NaN));
            // Surface Min/Max has no explicit invalid-input contract. Re-run the exact old
            // ordered loop for NaN payloads/infinities rather than assuming associativity.
            fallback=true;scanned+=scene.Materials[material].IndexCount;
            return SurfaceRange(scene,material);
        }
    }

    private readonly Entry?[] entries;
    private readonly long leafCacheByteLimit;
    private readonly long invalidationBlockMapByteLimit;
    private readonly int parallelDegree;
    internal int EntryCount {get;private set;}
    // Compatibility counter: candidate range coverage, including guard-skipped vertices.
    internal int LastComparedVertices {get;private set;}
    // Actual SameBits position comparisons; unused/already-full rows do not load positions.
    internal int LastPositionComparisons {get;private set;}
    internal int LastRebuiltMaterials {get;private set;}
    internal int LastMaterialRangeComparisons {get;private set;}
    internal int LastRebuiltLeaves {get;private set;}
    internal int LastScannedIndices {get;private set;}
    internal int LastFallbackMaterials {get;private set;}
    internal int LastFullScanMaterials {get;private set;}
    internal int LastMarkedLeafIncidences {get;private set;}
    internal bool LastTopologyRebuilt {get;private set;}
    // Number of material reductions scheduled through Parallel.For, not worker threads.
    internal int LastParallelMaterialCount {get;private set;}
    internal int LastSparseScanMaterials {get;private set;}
    // Opt-in wall-clock phases on the caller thread. Direct cache users never read the clock.
    internal bool MeasureCpu {get;set;}
    internal long LastTopologyTicks {get;private set;}
    internal long LastNewEntryTicks {get;private set;}
    internal long LastInvalidationTicks {get;private set;}
    internal long LastPreparationTicks {get;private set;}
    internal long LastReductionTicks {get;private set;}

    internal D3D12VisibilityBoundsCache(int capacity,long leafCacheByteLimit=MaximumLeafCacheBytes)
        :this(capacity,leafCacheByteLimit,16) {}

    internal D3D12VisibilityBoundsCache(int capacity,long leafCacheByteLimit,int degree)
        :this(capacity,leafCacheByteLimit,degree,MaximumInvalidationBlockMapBytes) {}

    internal D3D12VisibilityBoundsCache(int capacity,long leafCacheByteLimit,int degree,long invalidationBlockMapByteLimit)
    {
        if(capacity is <1 or >2)throw new ArgumentOutOfRangeException(nameof(capacity));
        if(leafCacheByteLimit is <0 or >MaximumLeafCacheBytes)throw new ArgumentOutOfRangeException(nameof(leafCacheByteLimit));
        if(degree is <1 or >16)throw new ArgumentOutOfRangeException(nameof(degree));
        if(invalidationBlockMapByteLimit is <0 or >MaximumInvalidationBlockMapBytes)
            throw new ArgumentOutOfRangeException(nameof(invalidationBlockMapByteLimit));
        entries=new Entry?[capacity];
        this.leafCacheByteLimit=leafCacheByteLimit;
        this.invalidationBlockMapByteLimit=invalidationBlockMapByteLimit;
        parallelDegree=Math.Min(degree,Environment.ProcessorCount);
    }

    internal OriginalVisibilityBounds[] GetSurface(SceneSnapshot scene)=>Get(scene,null);
    internal bool TryGetCurrent(SceneSnapshot scene,Vector4[]? positions,out OriginalVisibilityBounds[]? bounds)
        =>TryGetCurrentCore(scene,positions,out bounds);
    internal bool TryGetCurrentExpanded(SceneSnapshot scene,D3D12ExpandedPositions positions,out OriginalVisibilityBounds[]? bounds)
        =>TryGetCurrentCore(scene,positions,out bounds);
    private bool TryGetCurrentCore(SceneSnapshot scene,object? positions,out OriginalVisibilityBounds[]? bounds)
    {
        LastComparedVertices=LastPositionComparisons=LastRebuiltMaterials=LastMaterialRangeComparisons=0;LastTopologyRebuilt=false;
        LastRebuiltLeaves=LastScannedIndices=LastFallbackMaterials=0;
        LastFullScanMaterials=LastMarkedLeafIncidences=LastParallelMaterialCount=0;
        LastSparseScanMaterials=0;
        LastTopologyTicks=LastNewEntryTicks=LastInvalidationTicks=LastPreparationTicks=LastReductionTicks=0;
        for(int i=0;i<EntryCount;i++)
        {
            var entry=entries[i]!;
            bool sameStream=positions is null?entry.Positions is null&&ReferenceEquals(entry.Scene.VertexRevision,scene.VertexRevision)
                :ReferenceEquals(entry.Positions,positions);
            if(!sameStream||!SameTopology(entry.Scene,scene))continue;
            entry.Scene=scene;
            for(int j=i;j>0;j--)entries[j]=entries[j-1];
            entries[0]=entry;bounds=entry.Bounds;return true;
        }
        bounds=null;return false;
    }
    internal OriginalVisibilityBounds[] GetOutline(SceneSnapshot scene,Vector4[] positions)
    {
        ArgumentNullException.ThrowIfNull(scene);ArgumentNullException.ThrowIfNull(positions);
        if(positions.Length!=scene.VertexCount)
            throw new ArgumentException("One actual position per scene vertex is required.",nameof(positions));
        return Get(scene,positions);
    }
    internal OriginalVisibilityBounds[] GetOutline(SceneSnapshot scene,D3D12ExpandedPositions positions)
    {
        ArgumentNullException.ThrowIfNull(scene);ArgumentNullException.ThrowIfNull(positions);
        if(positions.Length!=scene.VertexCount)
            throw new ArgumentException("One actual position per scene vertex is required.",nameof(positions));
        return Get(scene,positions);
    }

    private OriginalVisibilityBounds[] Get(SceneSnapshot scene,object? positions)
    {
        ArgumentNullException.ThrowIfNull(scene);
        LastComparedVertices=LastPositionComparisons=LastRebuiltMaterials=LastMaterialRangeComparisons=0;LastTopologyRebuilt=false;
        LastRebuiltLeaves=LastScannedIndices=LastFallbackMaterials=0;
        LastFullScanMaterials=LastMarkedLeafIncidences=LastParallelMaterialCount=0;
        LastSparseScanMaterials=0;
        LastTopologyTicks=LastNewEntryTicks=LastInvalidationTicks=LastPreparationTicks=LastReductionTicks=0;
        Entry? found=null;int foundIndex=-1;
        long topologyStarted=MeasureCpu?Stopwatch.GetTimestamp():0;
        for(int i=0;i<EntryCount;i++)
        {
            var candidate=entries[i]!;
            if((candidate.Positions is null)!=(positions is null)||!SameTopology(candidate.Scene,scene))continue;
            found=candidate;foundIndex=i;break;
        }
        if(MeasureCpu)LastTopologyTicks=Stopwatch.GetTimestamp()-topologyStarted;
        try
        {
            if(found is null)
            {
                long newEntryStarted=MeasureCpu?Stopwatch.GetTimestamp():0;
                found=new(scene,positions,leafCacheByteLimit,invalidationBlockMapByteLimit);
                if(MeasureCpu)LastNewEntryTicks=Stopwatch.GetTimestamp()-newEntryStarted;
                RebuildMaterials(found,scene,positions,initial:true);
                LastTopologyRebuilt=true;
                foundIndex=Math.Min(EntryCount,entries.Length-1);EntryCount=Math.Min(EntryCount+1,entries.Length);
            }
            else if(positions is null?!ReferenceEquals(found.Scene.VertexRevision,scene.VertexRevision)
                :!ReferenceEquals(found.Positions,positions))
            {
                long invalidationStarted=MeasureCpu?Stopwatch.GetTimestamp():0;
                if(positions is null)
                {
                    // Compare with this topology entry's actual last input, including skipped or backward frames.
                    foreach(var range in scene.VertexRevision.GetChangedRanges(found.Scene.VertexRevision))
                    {
                        LastComparedVertices+=range.Count;
                        int start=range.Start,end=start+range.Count;
                        while(start<end)
                        {
                            int count=InvalidationSegmentLength(found,start,end);
                            if(!RequiresBlockPositionComparison(found,start)) {start+=count;continue;}
                            int chunkIndex=start/SurfaceChunkSize,chunkOffset=start%SurfaceChunkSize;
                            var nextChunk=scene.GetVertexChunk(chunkIndex);
                            count=Math.Min(count,nextChunk.Length-chunkOffset);
                            var previousSpan=found.Scene.GetVertexChunk(chunkIndex).Slice(chunkOffset,count);
                            var nextSpan=nextChunk.Slice(chunkOffset,count);
                            for(int local=0;local<count;local++)
                            {
                                int i=start+local;
                                if(!RequiresPositionComparison(found,i))continue;
                                LastPositionComparisons++;
                                ref readonly var previous=ref previousSpan[local];
                                ref readonly var next=ref nextSpan[local];
                                if(SameBits(previous.Position,next.Position))continue;
                                for(int j=found.Map.Offsets[i];j<found.Map.Offsets[i+1];j++)
                                {
                                    int material=found.Map.Materials[j];
                                    if(found.Dirty[material]==2)continue;
                                    found.Dirty[material]=found.Leaves is null||found.Leaves.RecordChangedVertex(material,i)?(byte)2:(byte)1;
                                }
                            }
                            start+=count;
                        }
                    }
                }
                else
                {
                    // Only producer-owned outputs have proof. Compare against this entry's actual
                    // previous output, never the surface revision or the producer's latest frame.
                    IReadOnlyList<SceneVertexRange>? ranges=null;
                    if(TryRevision(positions,out var nextRevision)&&TryRevision(found.Positions!,out var priorRevision))
                        ranges=nextRevision!.GetChangedRanges(priorRevision!);
                    int rangeCount=ranges?.Count??1;
                    for(int rangeIndex=0;rangeIndex<rangeCount;rangeIndex++)
                    {
                        int start=ranges?[rangeIndex].Start??0;
                        int end=start+(ranges?[rangeIndex].Count??scene.VertexCount);
                        LastComparedVertices+=end-start;
                        while(start<end)
                        {
                            int count=InvalidationSegmentLength(found,start,end);
                            if(!RequiresBlockPositionComparison(found,start)) {start+=count;continue;}
                            var previousSpan=PositionSpan(found.Positions!,start,count);var nextSpan=PositionSpan(positions,start,count);
                            for(int local=0;local<count;local++)
                            {
                                int i=start+local;
                                if(!RequiresPositionComparison(found,i))continue;
                                LastPositionComparisons++;
                                if(SameBits(previousSpan[local],nextSpan[local]))continue;
                                for(int j=found.Map.Offsets[i];j<found.Map.Offsets[i+1];j++)
                                {
                                    int material=found.Map.Materials[j];
                                    if(found.Dirty[material]==2)continue;
                                    found.Dirty[material]=found.Leaves is null||found.Leaves.RecordChangedVertex(material,i)?(byte)2:(byte)1;
                                }
                            }
                            start+=count;
                        }
                    }
                }
                if(MeasureCpu)LastInvalidationTicks=Stopwatch.GetTimestamp()-invalidationStarted;
                RebuildMaterials(found,scene,positions,initial:false);
            }
            // Advance the comparison baseline even when only unused vertices, normals or UVs changed.
            found.Scene=scene;found.Positions=positions;
            for(int i=foundIndex;i>0;i--)entries[i]=entries[i-1];
            entries[0]=found;return found.Bounds;
        }
        catch
        {
            // Leaf and candidate state may already have changed. Never retain a partially
            // updated entry, but leave previously returned arrays and other entries alone.
            if(foundIndex>=0)
            {
                for(int i=foundIndex;i<EntryCount-1;i++)entries[i]=entries[i+1];
                entries[--EntryCount]=null;
            }
            throw;
        }
    }

    private static int InvalidationSegmentLength(Entry entry,int start,int end)=>Math.Min(end-start,
        entry.Map.BlockOffsets is null?SurfaceChunkSize-start%SurfaceChunkSize:InvalidationBlockSize-start%InvalidationBlockSize);

    private static bool RequiresBlockPositionComparison(Entry entry,int vertex)
    {
        if(entry.Map.BlockOffsets is not {} offsets)return true;
        int block=vertex/InvalidationBlockSize;
        // The union includes every referenced vertex in the complete block, even when
        // this changed range covers only part of it. Never retain this dirty-state proof.
        for(int index=offsets[block];index<offsets[block+1];index++)
            if(entry.Dirty[entry.Map.BlockMaterials![index]]!=2)return true;
        return false;
    }

    private static bool RequiresPositionComparison(Entry entry,int vertex)
    {
        // Unused vertices cannot affect any bounds. Once every referenced material
        // requires its complete ordered scan, later positions cannot add invalidation.
        // A shared vertex still needs comparison while ANY material remains clean/sparse.
        for(int index=entry.Map.Offsets[vertex];index<entry.Map.Offsets[vertex+1];index++)
            if(entry.Dirty[entry.Map.Materials[index]]!=2)return true;
        return false;
    }

    private readonly record struct MaterialResult(OriginalVisibilityBounds Bounds,int Marked,int Full,
        int Leaves,int Scanned,int Fallback);

    private void RebuildMaterials(Entry entry,SceneSnapshot scene,object? positions,bool initial)
    {
        int count=entry.Dirty.Length;
        var eligible=new List<int>();long eligibleIndices=0;bool any=initial;
        for(int i=0;i<count;i++)
        {
            any|=entry.Dirty[i]!=0;
            // Gate on the complete batch so small dense ranges can share its workers.
            if((initial||entry.Dirty[i]==2)&&scene.Materials[i].IndexCount>0)
            {eligible.Add(i);eligibleIndices+=scene.Materials[i].IndexCount;}
        }
        if(!any)return;
        bool parallel=parallelDegree>1&&eligible.Count>=2&&eligibleIndices>=32768;
        var results=new MaterialResult[count];
        var work=new bool[count];
        var sparse=new bool[count];
        var marked=new int[count];
        // Candidate/CSR marking remains on the caller thread. Each later reduction owns
        // only its material's leaf, candidate and material-current slices.
        long preparationStarted=MeasureCpu?Stopwatch.GetTimestamp():0;
        for(int i=0;i<count;i++)
        {
            if(!initial&&entry.Dirty[i]==0)continue;
            work[i]=true;
            if(entry.Leaves is {} leaves)
                sparse[i]=leaves.PrepareMaterial(i,scene.Materials[i].IndexCount,out marked[i]);
        }
        if(MeasureCpu)LastPreparationTicks=Stopwatch.GetTimestamp()-preparationStarted;
        long reductionStarted=MeasureCpu?Stopwatch.GetTimestamp():0;
        if(parallel)
        {
            LastParallelMaterialCount=eligible.Count;
            Parallel.For(0,eligible.Count,new ParallelOptions
            {MaxDegreeOfParallelism=parallelDegree,TaskScheduler=TaskScheduler.Default},index=>
            {
                int material=eligible[index];
                results[material]=RebuildMaterial(entry,scene,positions,material,sparse[material],marked[material]);
            });
        }
        for(int i=0;i<count;i++)
        {
            if(!work[i]||(parallel&&(initial||entry.Dirty[i]==2)&&scene.Materials[i].IndexCount>0))continue;
            results[i]=RebuildMaterial(entry,scene,positions,i,sparse[i],marked[i]);
        }
        if(MeasureCpu)LastReductionTicks=Stopwatch.GetTimestamp()-reductionStarted;
        OriginalVisibilityBounds[]? updated=initial?entry.Bounds:null;
        for(int i=0;i<count;i++)
        {
            if(!work[i])continue;
            updated??=entry.Bounds.ToArray();
            var result=results[i];updated[i]=result.Bounds;
            LastMarkedLeafIncidences+=result.Marked;LastFullScanMaterials+=result.Full;
            LastRebuiltLeaves+=result.Leaves;LastScannedIndices+=result.Scanned;
            LastFallbackMaterials+=result.Fallback;LastRebuiltMaterials++;
            if(entry.Leaves is not null&&sparse[i])LastSparseScanMaterials++;
        }
        // Publishing and clearing dirty bits happen only after every reduction succeeds.
        if(updated is not null)entry.Bounds=updated;
        Array.Clear(entry.Dirty);
    }

    private static MaterialResult RebuildMaterial(Entry entry,SceneSnapshot scene,object? positions,
        int material,bool sparse,int marked)
    {
        if(entry.Leaves is not {} leaves)
            return new(positions is null?SurfaceRange(scene,material):PositionRange(scene,positions,material),
                0,0,0,scene.Materials[material].IndexCount,0);
        if(!sparse)
            return new(positions is null?SurfaceRange(scene,material):PositionRange(scene,positions,material),
                marked,1,0,scene.Materials[material].IndexCount,0);
        var result=leaves.MaterialBounds(scene,positions,material,out int rebuilt,out int scanned,out bool fallback);
        return new(result,marked,0,rebuilt,scanned,fallback?1:0);
    }

    private bool SameTopology(SceneSnapshot first,SceneSnapshot second)
    {
        if(first.VertexCount!=second.VertexCount||first.Materials.Count!=second.Materials.Count)return false;
        // A fresh assembler owner may hold the exact same immutable topology. Check its
        // complete values once on that transition before discarding the vertex/material map.
        if(!D3D12ImmutableGeometry.SameStorage(first.Indices,second.Indices)
            &&!first.Indices.SequenceEqual(second.Indices))return false;
        if(ReferenceEquals(first.Materials,second.Materials))return true;
        for(int i=0;i<first.Materials.Count;i++)
        {
            LastMaterialRangeComparisons++;
            if(first.Materials[i].StartIndex!=second.Materials[i].StartIndex||first.Materials[i].IndexCount!=second.Materials[i].IndexCount)
                return false;
        }
        return true;
    }

    private static MaterialMap BuildMap(SceneSnapshot scene,long blockMapByteLimit)
    {
        var counts=new int[scene.VertexCount];var seen=new int[counts.Length];Array.Fill(seen,-1);
        for(int material=0;material<scene.Materials.Count;material++)
        {
            var range=scene.Materials[material];
            for(int j=range.StartIndex;j<range.StartIndex+range.IndexCount;j++)
            {
                int vertex=(int)scene.Indices[j];if(seen[vertex]==material)continue;
                seen[vertex]=material;counts[vertex]++;
            }
        }
        var offsets=new int[counts.Length+1];
        for(int i=0;i<counts.Length;i++)offsets[i+1]=checked(offsets[i]+counts[i]);
        var materials=new int[offsets[^1]];Array.Clear(counts);Array.Fill(seen,-1);
        for(int material=0;material<scene.Materials.Count;material++)
        {
            var range=scene.Materials[material];
            for(int j=range.StartIndex;j<range.StartIndex+range.IndexCount;j++)
            {
                int vertex=(int)scene.Indices[j];if(seen[vertex]==material)continue;
                seen[vertex]=material;materials[offsets[vertex]+counts[vertex]++]=material;
            }
        }
        int blockCount=scene.VertexCount/InvalidationBlockSize+(scene.VertexCount%InvalidationBlockSize==0?0:1);
        long baseBytes=4L*(blockCount+1L)+4L*scene.Materials.Count+512;
        if(baseBytes>blockMapByteLimit)return new(offsets,materials,null,null);
        var blockOffsets=new int[blockCount+1];var blockMaterials=new List<int>();
        var stamps=new int[scene.Materials.Count];Array.Fill(stamps,-1);
        for(int block=0;block<blockCount;block++)
        {
            int first=block*InvalidationBlockSize,end=(int)Math.Min(scene.VertexCount,(long)first+InvalidationBlockSize);
            for(int vertex=first;vertex<end;vertex++)
                for(int index=offsets[vertex];index<offsets[vertex+1];index++)
                {
                    int material=materials[index];if(stamps[material]==block)continue;
                    // Reserve list growth (<2N) plus the final N-element array before
                    // adding an incidence. A capped map falls back to vertex guards.
                    if(baseBytes+12L*(blockMaterials.Count+1L)>blockMapByteLimit)
                        return new(offsets,materials,null,null);
                    stamps[material]=block;blockMaterials.Add(material);
                }
            blockOffsets[block+1]=blockMaterials.Count;
        }
        return new(offsets,materials,blockOffsets,blockMaterials.ToArray());
    }

    // Preserve the Core oracle's exact index/min/max order, including signed zero and empty ranges.
    private static OriginalVisibilityBounds SurfaceRange(SceneSnapshot scene,int material)
    {
        var range=scene.Materials[material];var min=new Vector3(float.PositiveInfinity);var max=new Vector3(float.NegativeInfinity);
        for(int j=range.StartIndex;j<range.StartIndex+range.IndexCount;j++)
        {
            var p=scene.GetVertex((int)scene.Indices[j]).Position;min=Vector3.Min(min,p);max=Vector3.Max(max,p);
        }
        return new(min,max);
    }
    private static OriginalVisibilityBounds IndexRange(SceneSnapshot scene,object? positions,int start,int count,
        out bool special,out int visited)
    {
        var indices=scene.Indices.Slice(start,count);
        // Select the stream once, outside the original-index reduction. Leaf and
        // dense paths use the same ordered typed readers and invalid-input contract.
        if(positions is D3D12ExpandedPositions owned)return PositionIndexRange(indices,owned,out special,out visited);
        if(positions is Vector4[] flat)return PositionIndexRange(indices,flat,out special,out visited);
        special=false;visited=0;var min=new Vector3(float.PositiveInfinity);var max=new Vector3(float.NegativeInfinity);
        foreach(uint vertex in indices)
        {
            visited++;var xyz=scene.GetVertex((int)vertex).Position;
            special|=!float.IsFinite(xyz.X)||!float.IsFinite(xyz.Y)||!float.IsFinite(xyz.Z);
            min=Vector3.Min(min,xyz);max=Vector3.Max(max,xyz);
        }
        return new(min,max);
    }
    internal static OriginalVisibilityBounds[] PositionMaterialBounds(SceneSnapshot scene,D3D12ExpandedPositions positions)
        =>PositionMaterialBounds(scene,(object)positions);
    private static OriginalVisibilityBounds[] PositionMaterialBounds(SceneSnapshot scene,object positions)
    {
        var bounds=new OriginalVisibilityBounds[scene.Materials.Count];
        for(int material=0;material<bounds.Length;material++)bounds[material]=PositionRange(scene,positions,material);
        return bounds;
    }
    private static bool TryRevision(object positions,out D3D12ExpandedPositionRevision? revision)
    {
        if(positions is D3D12ExpandedPositions owned){revision=owned.Revision;return true;}
        return D3D12ExpandedPositionRevision.TryGet((Vector4[])positions,out revision);
    }
    private static ReadOnlySpan<Vector4> PositionSpan(object positions,int start,int count)
        =>positions is D3D12ExpandedPositions owned?owned.GetChunk(start/D3D12ExpandedPositions.ChunkSize).Slice(start%D3D12ExpandedPositions.ChunkSize,count)
            :((Vector4[])positions).AsSpan(start,count);
    private static OriginalVisibilityBounds PositionRange(SceneSnapshot scene,object positions,int material)
    {
        var range=scene.Materials[material];var indices=scene.Indices.Slice(range.StartIndex,range.IndexCount);
        return positions is D3D12ExpandedPositions owned?PositionIndexRange(indices,owned,out _,out _)
            :PositionIndexRange(indices,(Vector4[])positions,out _,out _);
    }
    private static OriginalVisibilityBounds PositionIndexRange(ReadOnlySpan<uint> indices,ReadOnlySpan<Vector4> positions,
        out bool special,out int visited)
    {
        special=false;visited=indices.Length;var min=new Vector3(float.PositiveInfinity);var max=new Vector3(float.NegativeInfinity);
        for(int index=0;index<indices.Length;index++)
        {
            var p=positions[(int)indices[index]];
            if(!float.IsFinite(p.X)||!float.IsFinite(p.Y)||!float.IsFinite(p.Z)||p.W!=1)
            {special=true;visited=index+1;return new(new(float.NaN),new(float.NaN));}
            var xyz=new Vector3(p.X,p.Y,p.Z);min=Vector3.Min(min,xyz);max=Vector3.Max(max,xyz);
        }
        return new(min,max);
    }
    private static OriginalVisibilityBounds PositionIndexRange(ReadOnlySpan<uint> indices,D3D12ExpandedPositions positions,
        out bool special,out int visited)
    {
        special=false;visited=indices.Length;var min=new Vector3(float.PositiveInfinity);var max=new Vector3(float.NegativeInfinity);
        int currentChunk=-1;ReadOnlySpan<Vector4> chunk=default;bool canonical=false;
        bool validateChunks=indices.Length>=D3D12ExpandedPositions.ChunkSize;
        for(int index=0;index<indices.Length;index++)
        {
            int vertex=(int)indices[index],nextChunk=vertex/D3D12ExpandedPositions.ChunkSize;
            // Keep the last chunk payload for nearby/repeated references without
            // changing their order or materializing a whole-scene compatibility copy.
            if(nextChunk!=currentChunk)
            {
                chunk=positions.GetChunk(nextChunk);currentChunk=nextChunk;
                canonical=positions.IsCanonicalChunk(nextChunk,validateChunks);
            }
            var p=chunk[vertex%D3D12ExpandedPositions.ChunkSize];
            if(!canonical&&(!float.IsFinite(p.X)||!float.IsFinite(p.Y)||!float.IsFinite(p.Z)||p.W!=1))
            {special=true;visited=index+1;return new(new(float.NaN),new(float.NaN));}
            var xyz=new Vector3(p.X,p.Y,p.Z);min=Vector3.Min(min,xyz);max=Vector3.Max(max,xyz);
        }
        return new(min,max);
    }
    private static bool SameBits(Vector3 a,Vector3 b)=>SameBits(a.X,b.X)&&SameBits(a.Y,b.Y)&&SameBits(a.Z,b.Z);
    private static bool SameBits(Vector4 a,Vector4 b)=>SameBits(a.X,b.X)&&SameBits(a.Y,b.Y)&&SameBits(a.Z,b.Z)&&SameBits(a.W,b.W);
    private static bool SameBits(float a,float b)=>BitConverter.SingleToInt32Bits(a)==BitConverter.SingleToInt32Bits(b);
}
