using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>One previous immutable input and its expanded positions. Reuses unchanged vertices across
/// fresh project snapshots without changing the original scalar binary32 expansion sequence.
/// Proven unchanged chunks bypass comparison only while model base and every width retain their bits.</summary>
internal sealed class D3D12OutlinePositionCache
{
    private const int ParallelVertexThreshold=32768;
    private readonly int maximumDegree;
    internal D3D12OutlinePositionCache():this(Environment.ProcessorCount){}
    internal D3D12OutlinePositionCache(int maximumDegree)
        =>this.maximumDegree=Math.Clamp(maximumDegree,1,Math.Clamp(Environment.ProcessorCount,1,16));
    private OriginalOutlineData? previousData;
    private D3D12ExpandedPositions? previousPositions;
    private int[]? previousWidths;
    private int previousModelBase;
    internal int LastExpandedVertices {get;private set;}
    internal int LastComparedVertices {get;private set;}
    internal int LastCopiedChunks {get;private set;}
    // Number of chunks dispatched by the parallel branch, not an OS thread count.
    internal int LastParallelChunkCount {get;private set;}
    internal bool IsOutputMaterialized=>previousPositions?.IsMaterialized??false;
    internal void Clear()
    {previousData=null;previousPositions=null;previousWidths=null;LastExpandedVertices=LastComparedVertices=LastCopiedChunks=LastParallelChunkCount=0;}

    internal Vector4[] Get(OriginalOutlineData data,OriginalOutlineOptions options,IReadOnlyList<OriginalFrameMaterialFlags>? flags=null)
        =>GetChunks(data,options,flags).Materialize();

    internal D3D12ExpandedPositions GetChunks(OriginalOutlineData data,OriginalOutlineOptions options,IReadOnlyList<OriginalFrameMaterialFlags>? flags=null)
    {
        ArgumentNullException.ThrowIfNull(data);
        if(flags is not null)
        {
            if(flags.Count!=data.Materials.Count||flags.Any(flag=>flag is null))
                throw new ArgumentException("One non-null frame flag set per outline material is required.",nameof(flags));
            foreach(var flag in flags)if(flag.CharacterOutlineStyle is {} style)CharacterOutlineMath.Validate(style);
        }
        float modelBase=OriginalOutlineMath.ComputeModelBase(options);
        int modelBaseBits=BitConverter.SingleToInt32Bits(modelBase);
        float Width(int material)=>flags?[material].CharacterOutlineStyle?.Width??data.Materials[material].Width;
        bool equalWidths=previousWidths?.Length==data.Materials.Count;
        if(equalWidths)
            for(int i=0;i<data.Materials.Count;i++)
                if(previousWidths![i]!=BitConverter.SingleToInt32Bits(Width(i))){equalWidths=false;break;}
        var widths=equalWidths?previousWidths!:Enumerable.Range(0,data.Materials.Count).Select(i=>BitConverter.SingleToInt32Bits(Width(i))).ToArray();
        ReadOnlySpan<float> selectedMaterialWidths=MemoryMarshal.Cast<int,float>(widths.AsSpan());
        bool reuse=previousPositions is not null&&previousData!.VertexCount==data.VertexCount&&previousModelBase==modelBaseBits;
        LastExpandedVertices=LastComparedVertices=LastCopiedChunks=LastParallelChunkCount=0;
        if(reuse&&ReferenceEquals(previousData,data)&&equalWidths)return previousPositions!;
        IReadOnlyList<SceneVertexRange>? ranges=reuse&&equalWidths
            ?data.VertexRevision.GetChangedRanges(previousData!.VertexRevision):null;
        int covered=ranges is null?data.VertexCount:ranges.Sum(range=>range.Count);
        if(maximumDegree>1&&covered>=ParallelVertexThreshold)
        {
            var work=new List<(int Start,int Count)>?[data.VertexChunkCount];
            int workRangeCount=ranges?.Count??1;
            for(int index=0;index<workRangeCount;index++)
            {
                int start=ranges?[index].Start??0;
                int end=start+(ranges?[index].Count??data.VertexCount);
                while(start<end)
                {
                    int chunk=start/D3D12ExpandedPositions.ChunkSize;
                    int offset=start%D3D12ExpandedPositions.ChunkSize;
                    int count=Math.Min(end-start,data.GetVertexChunk(chunk).Length-offset);
                    (work[chunk]??=[]).Add((offset,count));start+=count;
                }
            }
            int workChunks=work.Count(segments=>segments is not null);
            if(workChunks>1)
            {
                var oldData=previousData;var oldPositions=previousPositions;var oldWidths=previousWidths;
                var resultChunks=reuse?oldPositions!.CopyChunkTable():new Vector4[data.VertexChunkCount][];
                var compared=new int[work.Length];var expanded=new int[work.Length];var copiedChunks=new bool[work.Length];
                var changed=new bool[work.Length];
                Parallel.For(0,work.Length,new ParallelOptions{MaxDegreeOfParallelism=maximumDegree,TaskScheduler=TaskScheduler.Default},chunkIndex=>
                {
                    var segments=work[chunkIndex];if(segments is null)return;
                    var current=data.GetVertexChunk(chunkIndex);
                    var prior=reuse?oldData!.GetVertexChunk(chunkIndex):default;
                    Vector4[]? output=reuse?null:new Vector4[current.Length];
                    var selectedWidths=MemoryMarshal.Cast<int,float>(widths.AsSpan());
                    int localCompared=0,localExpanded=0;bool localCopied=false,localChanged=false;
                    foreach(var (offset,count) in segments)
                    {
                        int runStart=-1;
                        for(int local=offset;local<offset+count;local++)
                        {
                            localCompared++;
                            bool same=false;
                            if(reuse)
                            {
                                ref readonly var vertex=ref current[local];ref readonly var previous=ref prior[local];
                                same=widths[vertex.SelectedMaterialIndex]==oldWidths![previous.SelectedMaterialIndex]
                                    &&MemoryMarshal.AsBytes(current.Slice(local,1)).SequenceEqual(MemoryMarshal.AsBytes(prior.Slice(local,1)));
                            }
                            if(!same)
                            {
                                if(output is null){output=oldPositions!.GetChunk(chunkIndex).ToArray();localCopied=true;}
                                localChanged=true;
                                if(runStart<0)runStart=local;
                            }
                            else if(runStart>=0)
                            {
                                int length=local-runStart;
                                OriginalOutlineBatchMath.BuildPositions(current.Slice(runStart,length),modelBase,selectedWidths,
                                    output!.AsSpan(runStart,length));
                                localExpanded+=length;runStart=-1;
                            }
                        }
                        if(runStart>=0)
                        {
                            int length=offset+count-runStart;
                            OriginalOutlineBatchMath.BuildPositions(current.Slice(runStart,length),modelBase,selectedWidths,
                                output!.AsSpan(runStart,length));
                            localExpanded+=length;
                        }
                    }
                    if(output is not null)resultChunks[chunkIndex]=output;
                    compared[chunkIndex]=localCompared;expanded[chunkIndex]=localExpanded;
                    copiedChunks[chunkIndex]=localCopied;changed[chunkIndex]=localChanged;
                });
                int comparedTotal=0,expandedTotal=0,copiedTotal=0;bool anyChanged=false;
                for(int index=0;index<work.Length;index++)
                {comparedTotal+=compared[index];expandedTotal+=expanded[index];if(copiedChunks[index])copiedTotal++;anyChanged|=changed[index];}
                var parallelResult=!anyChanged?oldPositions!:new D3D12ExpandedPositions(data.VertexCount,resultChunks,
                    D3D12ExpandedPositionRevision.Create(data.VertexRevision,modelBaseBits,widths));
                previousData=data;previousModelBase=modelBaseBits;previousWidths=widths;
                LastComparedVertices=comparedTotal;LastExpandedVertices=expandedTotal;LastCopiedChunks=copiedTotal;
                LastParallelChunkCount=workChunks;
                return previousPositions=parallelResult;
            }
        }
        Vector4[][]? positions=reuse?null:new Vector4[data.VertexChunkCount][];
        bool[]? copied=null;
        int rangeCount=ranges?.Count??1;
        for(int rangeIndex=0;rangeIndex<rangeCount;rangeIndex++)
        {
            int start=ranges?[rangeIndex].Start??0;
            int end=start+(ranges?[rangeIndex].Count??data.VertexCount);
            while(start<end)
            {
                int chunkIndex=start/D3D12ExpandedPositions.ChunkSize,chunkOffset=start%D3D12ExpandedPositions.ChunkSize;
                var chunk=data.GetVertexChunk(chunkIndex);
                int count=Math.Min(chunk.Length-chunkOffset,end-start);
                var currentSpan=chunk.Slice(chunkOffset,count);
                var previousSpan=reuse?previousData!.GetVertexChunk(chunkIndex).Slice(chunkOffset,count):default;
                int runStart=-1;
                for(int local=0;local<count;local++)
                {
                    LastComparedVertices++;
                    bool same=false;
                    if(reuse)
                    {
                        ref readonly var vertex=ref currentSpan[local];ref readonly var previous=ref previousSpan[local];
                        // Exact bytes retain signed-zero and source-padding conservative misses.
                        same=widths[vertex.SelectedMaterialIndex]==previousWidths![previous.SelectedMaterialIndex]
                            &&MemoryMarshal.AsBytes(currentSpan.Slice(local,1)).SequenceEqual(MemoryMarshal.AsBytes(previousSpan.Slice(local,1)));
                    }
                    if(!same)
                    {
                        positions??=previousPositions!.CopyChunkTable();
                        copied??=new bool[data.VertexChunkCount];
                        if(!copied[chunkIndex])
                        {
                            positions[chunkIndex]=reuse?previousPositions!.GetChunk(chunkIndex).ToArray():new Vector4[chunk.Length];
                            copied[chunkIndex]=true;if(reuse)LastCopiedChunks++;
                        }
                        if(runStart<0)runStart=local;
                    }
                    else if(runStart>=0)
                    {
                        int length=local-runStart;
                        OriginalOutlineBatchMath.BuildPositions(currentSpan.Slice(runStart,length),modelBase,selectedMaterialWidths,
                            positions![chunkIndex].AsSpan(chunkOffset+runStart,length));
                        LastExpandedVertices+=length;runStart=-1;
                    }
                }
                if(runStart>=0)
                {
                    int length=count-runStart;
                    OriginalOutlineBatchMath.BuildPositions(currentSpan.Slice(runStart,length),modelBase,selectedMaterialWidths,
                        positions![chunkIndex].AsSpan(chunkOffset+runStart,length));
                    LastExpandedVertices+=length;
                }
                start+=count;
            }
        }
        var result=positions is null?previousPositions!:new D3D12ExpandedPositions(data.VertexCount,positions,
            D3D12ExpandedPositionRevision.Create(data.VertexRevision,modelBaseBits,widths));
        previousData=data;previousModelBase=modelBaseBits;previousWidths=widths;
        return previousPositions=result;
    }
}
