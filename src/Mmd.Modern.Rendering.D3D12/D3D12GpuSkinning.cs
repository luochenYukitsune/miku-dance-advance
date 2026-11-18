using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace Mmd.Modern.Rendering.D3D12;

public sealed record D3D12GpuSkinningStatistics(
    long SkinDispatches,
    long SkippedSkinRanges,
    long OutlineDispatches,
    long RayGiTriangleDispatches,
    long DynamicUploadBytes,
    long StaticUploadBytes);

/// <summary>
/// GPU compute skinning, outline expansion, temporal vertex packing, and Ray GI triangle assembly.
/// All slots track their own last-computed ranges/options and restore cleanly on recording abort.
/// </summary>
internal sealed class D3D12GpuSkinning : IDisposable
{
    private const int ThreadsPerGroup = 256;

    [StructLayout(LayoutKind.Sequential)]
    private struct SkinConstants
    {
        public uint RangeStart;
        public uint VertexCount;
        public uint MorphCount;
        public uint HasSdef;
        public uint HasQdef;
        public uint HasWorld;
        public uint Pad0;
        public uint Pad1;
        public Matrix4x4 World;
        public Matrix4x4 NormalWorld;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OutlineExpandConstants
    {
        public uint VertexCount;
        public uint RangeCount;
        public float ModelBase;
        public uint WriteModern;
        public uint WriteWidthSource;
        public uint HasAdditionalUv;
        public uint Pad0;
        public uint Pad1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TemporalPackConstants
    {
        public uint VertexCount;
        public uint Mode;
        public uint Pad0;
        public uint Pad1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RayGiBuildConstants
    {
        public uint TriangleCount;
        public uint HasWorld;
        public uint Pad0;
        public uint Pad1;
        public Matrix4x4 World;
        public Matrix4x4 NormalWorld;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GpuRangeBounds(uint Start, uint End);

    private sealed class StaticModelGpu(ID3D12Resource restVertices, ID3D12Resource bindings, ID3D12Resource? sdef) : IDisposable
    {
        internal ID3D12Resource RestVertices { get; } = restVertices;
        internal ID3D12Resource Bindings { get; } = bindings;
        internal ID3D12Resource? Sdef { get; } = sdef;
        public void Dispose()
        {
            Sdef?.Dispose();
            Bindings.Dispose();
            RestVertices.Dispose();
        }
    }

    private sealed class TopologyMaterialTableGpu(
        D3D12IndexStreamIdentity indexIdentity,
        (int StartIndex, int IndexCount)[] materialRanges,
        ID3D12Resource triangleMaterials) : IDisposable
    {
        internal D3D12IndexStreamIdentity IndexIdentity { get; } = indexIdentity;
        internal (int StartIndex, int IndexCount)[] MaterialRanges { get; } = materialRanges;
        internal ID3D12Resource TriangleMaterials { get; } = triangleMaterials;
        public void Dispose() => TriangleMaterials.Dispose();
    }

    private sealed class SkinSlotRecord(ID3D12Resource buffer, (int Start, int Count, SceneDeformation Deformation, Matrix4x4 World)[] ranges)
    {
        internal ID3D12Resource Buffer { get; } = buffer;
        internal (int Start, int Count, SceneDeformation Deformation, Matrix4x4 World)[] Ranges { get; } = ranges;
    }

    private sealed class OutlineSlotState
    {
        internal ID3D12Resource? SurfaceBuffer;
        internal SceneVertexRevision? StaticOutlineRevision;
        internal (int Start, int Count)[]? Ranges;
        internal int ModelBaseBits;
        internal int[]? WidthBits;
        internal bool HasAdditionalUv;
        internal SceneAdditionalUvs? AdditionalUvs;
        internal ID3D12Resource? Expanded;
        internal ID3D12Resource? ModernOutline;
        internal ID3D12Resource? WidthSource;
        internal bool HasModern;
        internal bool HasWidthSource;
    }

    private sealed class TemporalHistoryRecord
    {
        internal SceneSnapshot? Snapshot;
        internal ID3D12Resource? SurfaceVertices;
        internal OriginalOutlineData? Outline;
        internal OriginalOutlineOptions? OutlineOptions;
        internal IReadOnlyList<OriginalFrameMaterialFlags>? Materials;
        internal ID3D12Resource? OriginalExpanded;
        internal ID3D12Resource? ModernExpanded;
        internal ID3D12Resource? WidthSource;
    }

    private sealed record ComputePipeline(ID3D12RootSignature Root, ID3D12PipelineState State) : IDisposable
    {
        public void Dispose()
        {
            State.Dispose();
            Root.Dispose();
        }
    }

    private readonly D3D12GpuRuntime runtime;
    private readonly D3D12GpuCache cache;
    private readonly ConditionalWeakTable<SceneSkinningModel, StaticModelGpu> modelCache = new();
    private readonly SkinSlotRecord?[] skinSlots = new SkinSlotRecord?[D3D12SsaaGiMemoryBudget.MaximumInFlightFrames];
    private readonly OutlineSlotState?[] originalOutlineSlots = new OutlineSlotState?[D3D12SsaaGiMemoryBudget.MaximumInFlightFrames];
    private readonly OutlineSlotState?[] modernOutlineSlots = new OutlineSlotState?[D3D12SsaaGiMemoryBudget.MaximumInFlightFrames];
    private readonly TemporalHistoryRecord?[] retainedTemporalSlots = new TemporalHistoryRecord?[2];
    private int retainedTemporalCursor;

    private DxcShaderCompiler? compiler;
    private ComputePipeline? skinPipeline;
    private ComputePipeline? outlinePipeline;
    private ComputePipeline? temporalPipeline;
    private ComputePipeline? rayGiBuildPipeline;
    private ID3D12Resource? dummyBuffer;
    private TopologyMaterialTableGpu? cachedTriangleMaterials;

    private long skinDispatches, skippedSkinRanges, outlineDispatches, rayGiTriangleDispatches;
    private long dynamicUploadBytes, staticUploadBytes;
    private bool disposed;

    internal D3D12GpuSkinning(D3D12GpuRuntime runtime, D3D12GpuCache cache)
    {
        this.runtime = runtime;
        this.cache = cache;
    }

    internal D3D12GpuSkinningStatistics Statistics => new(
        skinDispatches, skippedSkinRanges, outlineDispatches, rayGiTriangleDispatches, dynamicUploadBytes, staticUploadBytes);

    internal static Vector2 EffectiveUv(SceneSnapshot scene, int vertex)
    {
        var baseUv = scene.StaticVertexView.GetVertex(vertex).Uv;
        if (!scene.HasDeferredDeformation) return baseUv;
        var ranges = scene.DeformationRanges;
        for (int r = 0; r < ranges.Count; r++)
        {
            var range = ranges[r];
            if (vertex < range.Start || vertex >= range.Start + range.Count) continue;
            if (range.Deformation.Morphs is { } morphs)
            {
                int local = vertex - range.Start;
                int idx = morphs.Indices.BinarySearch(local);
                if (idx >= 0) return baseUv + morphs.Uvs[idx];
            }
            break;
        }
        return baseUv;
    }

    internal static bool HaveSameBaseUvs(SceneSnapshot a, SceneSnapshot b)
    {
        if (a.VertexCount != b.VertexCount) return false;
        if (!a.HasDeferredDeformation && !b.HasDeferredDeformation)
        {
            for (int chunk = 0; chunk < a.VertexChunkCount; chunk++)
            {
                var ca = a.GetVertexChunk(chunk);
                var cb = b.GetVertexChunk(chunk);
                if (D3D12ImmutableGeometry.SameStorage(ca, cb)) continue;
                for (int i = 0; i < ca.Length; i++)
                    if (ca[i].Uv != cb[i].Uv) return false;
            }
            return true;
        }
        var sa = a.StaticVertexView;
        var sb = b.StaticVertexView;
        if (ReferenceEquals(sa.VertexRevision, sb.VertexRevision))
        {
            var ra = a.DeformationRanges;
            var rb = b.DeformationRanges;
            if (ra.Count == rb.Count)
            {
                bool sameTopology = true;
                for (int i = 0; i < ra.Count; i++)
                {
                    if (ra[i].Start != rb[i].Start || ra[i].Count != rb[i].Count) { sameTopology = false; break; }
                }
                if (sameTopology)
                {
                    for (int i = 0; i < ra.Count; i++)
                    {
                        var ma = ra[i].Deformation.Morphs;
                        var mb = rb[i].Deformation.Morphs;
                        if (ReferenceEquals(ma, mb)) continue;
                        if (!SameMorphUvs(ma, mb)) return false;
                    }
                    return true;
                }
            }
        }
        for (int chunk = 0; chunk < sa.VertexChunkCount; chunk++)
        {
            var ca = sa.GetVertexChunk(chunk);
            var cb = sb.GetVertexChunk(chunk);
            int chunkStart = chunk * OwnedVertexChunks<SceneVertex>.ChunkSize;
            int chunkEnd = chunkStart + ca.Length;
            if (D3D12ImmutableGeometry.SameStorage(ca, cb)
                && !ChunkOverlapsMorphUvs(a.DeformationRanges, chunkStart, chunkEnd)
                && !ChunkOverlapsMorphUvs(b.DeformationRanges, chunkStart, chunkEnd))
                continue;
            for (int i = 0; i < ca.Length; i++)
            {
                int v = chunkStart + i;
                if (EffectiveUv(a, v) != EffectiveUv(b, v)) return false;
            }
        }
        return true;
    }

    private static bool SameMorphUvs(SceneMorphDeltas? a, SceneMorphDeltas? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null) return AllZeroUvs(b!);
        if (b is null) return AllZeroUvs(a);
        var ia = a.Indices; var ib = b.Indices;
        var ua = a.Uvs; var ub = b.Uvs;
        int pa = 0, pb = 0;
        while (pa < ia.Length && pb < ib.Length)
        {
            int va = ia[pa], vb = ib[pb];
            if (va == vb) { if (ua[pa] != ub[pb]) return false; pa++; pb++; }
            else if (va < vb) { if (ua[pa] != Vector2.Zero) return false; pa++; }
            else { if (ub[pb] != Vector2.Zero) return false; pb++; }
        }
        while (pa < ia.Length) { if (ua[pa++] != Vector2.Zero) return false; }
        while (pb < ib.Length) { if (ub[pb++] != Vector2.Zero) return false; }
        return true;
    }

    private static bool AllZeroUvs(SceneMorphDeltas m)
    {
        foreach (var uv in m.Uvs) if (uv != Vector2.Zero) return false;
        return true;
    }

    private static bool ChunkOverlapsMorphUvs(IReadOnlyList<SceneDeformationRange> ranges, int start, int end)
    {
        for (int i = 0; i < ranges.Count; i++)
        {
            var r = ranges[i];
            if (r.Start + r.Count <= start || r.Start >= end) continue;
            if (r.Deformation.Morphs is not null) return true;
        }
        return false;
    }

    internal void Skin(ID3D12Resource vb, SceneSnapshot scene, bool staticCopiedOrReplaced)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!runtime.IsRecording) throw new InvalidOperationException("GPU skinning requires an open GPU recording.");
        var ranges = scene.DeformationRanges;
        if (ranges.Count == 0) return;

        int slot = runtime.SlotIndex;
        var prior = skinSlots[slot];
        bool canComparePrior = !staticCopiedOrReplaced && prior is not null && ReferenceEquals(prior.Buffer, vb)
            && prior.Ranges.Length == ranges.Count;

        var pending = new bool[ranges.Count];
        int dispatchCount = 0;
        for (int i = 0; i < ranges.Count; i++)
        {
            var r = ranges[i];
            bool same = canComparePrior
                && prior!.Ranges[i].Start == r.Start
                && prior.Ranges[i].Count == r.Count
                && prior.Ranges[i].World == r.World
                && prior.Ranges[i].Deformation.HasSameInputs(r.Deformation);
            pending[i] = !same;
            if (same) skippedSkinRanges++;
            else dispatchCount++;
        }

        if (dispatchCount == 0) return;

        EnsureSkinPipeline();
        var dummyAddr = EnsureDummyBuffer().GPUVirtualAddress;
        var commands = runtime.Commands;
        commands.ResourceBarrierTransition(vb, ResourceStates.VertexAndConstantBuffer, ResourceStates.UnorderedAccess);
        commands.SetComputeRootSignature(skinPipeline!.Root);
        commands.SetPipelineState(skinPipeline.State);
        commands.SetComputeRootUnorderedAccessView(11, vb.GPUVirtualAddress);

        for (int i = 0; i < ranges.Count; i++)
        {
            if (!pending[i]) continue;
            var range = ranges[i];
            var def = range.Deformation;
            var model = def.Model;
            var modelGpu = GetOrUploadModel(model);

            var skinAlloc = StageBytes(MemoryMarshal.AsBytes(def.SkinMatrices));
            ulong sdefRotAddr = model.HasSdef ? StageBytes(MemoryMarshal.AsBytes(def.SdefRotations)).GpuAddress : dummyAddr;
            ulong qdefRealAddr = model.HasQdef ? StageBytes(MemoryMarshal.AsBytes(def.QdefReal)).GpuAddress : dummyAddr;
            ulong qdefDualAddr = model.HasQdef ? StageBytes(MemoryMarshal.AsBytes(def.QdefDual)).GpuAddress : dummyAddr;

            var morphs = def.Morphs;
            uint morphCount = morphs is not null ? (uint)morphs.Indices.Length : 0u;
            ulong morphIdxAddr = dummyAddr, morphPosAddr = dummyAddr, morphUvAddr = dummyAddr;
            if (morphCount > 0)
            {
                morphIdxAddr = StageBytes(MemoryMarshal.AsBytes(morphs!.Indices)).GpuAddress;
                morphPosAddr = StageBytes(MemoryMarshal.AsBytes(morphs.Positions)).GpuAddress;
                morphUvAddr = StageBytes(MemoryMarshal.AsBytes(morphs.Uvs)).GpuAddress;
            }

            bool hasWorld = range.World != Matrix4x4.Identity;
            Matrix4x4 normalWorld = Matrix4x4.Identity;
            if (hasWorld)
            {
                if (!Matrix4x4.Invert(range.World, out var inverse))
                    throw new ArgumentException("Deformation World must be invertible.");
                normalWorld = Matrix4x4.Transpose(inverse);
            }

            var constants = new SkinConstants
            {
                RangeStart = (uint)range.Start,
                VertexCount = (uint)range.Count,
                MorphCount = morphCount,
                HasSdef = model.HasSdef ? 1u : 0u,
                HasQdef = model.HasQdef ? 1u : 0u,
                HasWorld = hasWorld ? 1u : 0u,
                World = range.World,
                NormalWorld = normalWorld,
            };
            var cbAlloc = StageStruct(in constants);

            commands.SetComputeRootConstantBufferView(0, cbAlloc);
            commands.SetComputeRootShaderResourceView(1, modelGpu.RestVertices.GPUVirtualAddress);
            commands.SetComputeRootShaderResourceView(2, modelGpu.Bindings.GPUVirtualAddress);
            commands.SetComputeRootShaderResourceView(3, modelGpu.Sdef?.GPUVirtualAddress ?? dummyAddr);
            commands.SetComputeRootShaderResourceView(4, skinAlloc.GpuAddress);
            commands.SetComputeRootShaderResourceView(5, sdefRotAddr);
            commands.SetComputeRootShaderResourceView(6, qdefRealAddr);
            commands.SetComputeRootShaderResourceView(7, qdefDualAddr);
            commands.SetComputeRootShaderResourceView(8, morphIdxAddr);
            commands.SetComputeRootShaderResourceView(9, morphPosAddr);
            commands.SetComputeRootShaderResourceView(10, morphUvAddr);

            int groups = (range.Count + ThreadsPerGroup - 1) / ThreadsPerGroup;
            commands.Dispatch(groups, 1, 1);
            skinDispatches++;
        }

        commands.ResourceBarrierUnorderedAccessView(vb);
        commands.ResourceBarrierTransition(vb, ResourceStates.UnorderedAccess, ResourceStates.VertexAndConstantBuffer);

        var nextRecords = new (int Start, int Count, SceneDeformation Deformation, Matrix4x4 World)[ranges.Count];
        for (int i = 0; i < ranges.Count; i++)
            nextRecords[i] = (ranges[i].Start, ranges[i].Count, ranges[i].Deformation, ranges[i].World);

        var nextSlot = new SkinSlotRecord(vb, nextRecords);
        skinSlots[slot] = nextSlot;
        runtime.OnAbort(() =>
        {
            if (ReferenceEquals(skinSlots[slot], nextSlot)) skinSlots[slot] = prior;
        });
    }

    internal (ID3D12Resource Stream, ID3D12Resource? WidthSource, ID3D12Resource ExpandedRaw) GetOutlineStreams(
        OriginalFrameScene frame,
        ID3D12Resource vb,
        bool modern,
        bool needWidthSource,
        string? streamPrefix = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!runtime.IsRecording) throw new InvalidOperationException("GPU outline expansion requires an open GPU recording.");
        var data = frame.Outline ?? throw new ArgumentException("Outline data is required.");
        var options = frame.OutlineOptions ?? throw new ArgumentException("Outline options are required.");
        var scene = frame.Surface.Snapshot;

        float modelBase = OriginalOutlineMath.ComputeModelBase(options);
        int modelBaseBits = BitConverter.SingleToInt32Bits(modelBase);
        var widths = new float[data.Materials.Count];
        var widthBits = new int[widths.Length];
        for (int i = 0; i < widths.Length; i++)
        {
            if (modern && frame.Materials[i].CharacterOutlineStyle is { } style)
                CharacterOutlineMath.Validate(style);
            float w = modern ? (frame.Materials[i].CharacterOutlineStyle?.Width ?? data.Materials[i].Width) : data.Materials[i].Width;
            widths[i] = w;
            widthBits[i] = BitConverter.SingleToInt32Bits(w);
        }

        var staticOutline = data.StaticView;
        var staticRevision = staticOutline.VertexRevision;
        bool hasAdditionalUv = modern && D3D12OriginalStyleRenderer.HasSubTexture(frame);
        var additionalUvs = hasAdditionalUv ? scene.AdditionalUvs ?? throw new ArgumentException("SubTexture outline requires UV1.") : null;

        var slots = modern ? modernOutlineSlots : originalOutlineSlots;
        int slot = runtime.SlotIndex;
        var prior = streamPrefix is null ? slots[slot] : null;

        if (prior is not null
            && ReferenceEquals(prior.SurfaceBuffer, vb)
            && ReferenceEquals(prior.StaticOutlineRevision, staticRevision)
            && prior.ModelBaseBits == modelBaseBits
            && SameRanges(prior.Ranges, scene.DeformationRanges)
            && prior.WidthBits is not null && prior.WidthBits.AsSpan().SequenceEqual(widthBits)
            && prior.HasModern == modern
            && (!needWidthSource || prior.HasWidthSource)
            && (!modern || (prior.HasAdditionalUv == hasAdditionalUv && ReferenceEquals(prior.AdditionalUvs, additionalUvs)))
            && prior.Expanded is not null
            && (!modern || prior.ModernOutline is not null)
            && (!needWidthSource || prior.WidthSource is not null))
        {
            return (modern ? prior.ModernOutline! : prior.Expanded!, needWidthSource ? prior.WidthSource : null, prior.Expanded!);
        }

        EnsureOutlinePipeline();
        string prefix = streamPrefix ?? (modern ? "gpu-modern-outline" : "gpu-original-outline");
        var staticOutlineGpu = cache.StaticOutlineBuffer(prefix + ":static", staticOutline, out _);

        int vertexCount = data.VertexCount;
        var expandedBuf = cache.WritableGpuBuffer(prefix + ":expanded", vertexCount, 16, out var expandedState, out _);
        ResourceStates widthSrcState = ResourceStates.Common, modernState = ResourceStates.Common;
        ID3D12Resource? widthSourceBuf = needWidthSource
            ? cache.WritableGpuBuffer(prefix + ":width-source", vertexCount, 16, out widthSrcState, out _)
            : null;
        ID3D12Resource? modernBuf = modern
            ? cache.WritableGpuBuffer(prefix + ":modern-stream", vertexCount, 52, out modernState, out _)
            : null;

        var commands = runtime.Commands;
        if (expandedState != ResourceStates.UnorderedAccess)
            commands.ResourceBarrierTransition(expandedBuf, expandedState, ResourceStates.UnorderedAccess);
        if (needWidthSource && widthSrcState != ResourceStates.UnorderedAccess)
            commands.ResourceBarrierTransition(widthSourceBuf!, widthSrcState, ResourceStates.UnorderedAccess);
        if (modern && modernState != ResourceStates.UnorderedAccess)
            commands.ResourceBarrierTransition(modernBuf!, modernState, ResourceStates.UnorderedAccess);

        // Transition input SRVs to NonPixelShaderResource cleanly and restore right after.
        commands.ResourceBarrierTransition(vb, ResourceStates.VertexAndConstantBuffer, ResourceStates.NonPixelShaderResource);
        commands.ResourceBarrierTransition(staticOutlineGpu, ResourceStates.VertexAndConstantBuffer, ResourceStates.NonPixelShaderResource);

        var dummy = EnsureDummyBuffer();
        var dummyAddr = dummy.GPUVirtualAddress;

        ulong uv1Addr = dummyAddr;
        if (hasAdditionalUv && additionalUvs is not null)
        {
            var uvGpu = cache.BufferByIdentity("original-additional-uv", MemoryMarshal.AsBytes(additionalUvs.Vertices), ResourceStates.VertexAndConstantBuffer, additionalUvs);
            commands.ResourceBarrierTransition(uvGpu, ResourceStates.VertexAndConstantBuffer, ResourceStates.NonPixelShaderResource);
            uv1Addr = uvGpu.GPUVirtualAddress;
        }

        Span<GpuRangeBounds> rangeBounds = scene.DeformationRanges.Count <= 32
            ? stackalloc GpuRangeBounds[Math.Max(1, scene.DeformationRanges.Count)]
            : new GpuRangeBounds[scene.DeformationRanges.Count];
        for (int i = 0; i < scene.DeformationRanges.Count; i++)
        {
            var r = scene.DeformationRanges[i];
            rangeBounds[i] = new((uint)r.Start, (uint)(r.Start + r.Count));
        }
        ulong rangesAddr = scene.DeformationRanges.Count > 0
            ? StageBytes(MemoryMarshal.AsBytes(rangeBounds[..scene.DeformationRanges.Count])).GpuAddress
            : dummyAddr;
        ulong widthsAddr = StageBytes(MemoryMarshal.AsBytes(widths.AsSpan())).GpuAddress;

        var cb = new OutlineExpandConstants
        {
            VertexCount = (uint)vertexCount,
            RangeCount = (uint)scene.DeformationRanges.Count,
            ModelBase = modelBase,
            WriteModern = modern ? 1u : 0u,
            WriteWidthSource = needWidthSource ? 1u : 0u,
            HasAdditionalUv = hasAdditionalUv ? 1u : 0u,
        };
        ulong cbAddr = StageStruct(in cb);

        var dummyUav = EnsureDummyUavBuffer();
        commands.SetComputeRootSignature(outlinePipeline!.Root);
        commands.SetPipelineState(outlinePipeline.State);
        commands.SetComputeRootConstantBufferView(0, cbAddr);
        commands.SetComputeRootShaderResourceView(1, vb.GPUVirtualAddress);
        commands.SetComputeRootShaderResourceView(2, staticOutlineGpu.GPUVirtualAddress);
        commands.SetComputeRootShaderResourceView(3, rangesAddr);
        commands.SetComputeRootShaderResourceView(4, widthsAddr);
        commands.SetComputeRootShaderResourceView(5, uv1Addr);
        commands.SetComputeRootUnorderedAccessView(6, expandedBuf.GPUVirtualAddress);
        commands.SetComputeRootUnorderedAccessView(7, widthSourceBuf?.GPUVirtualAddress ?? dummyUav.GPUVirtualAddress);
        commands.SetComputeRootUnorderedAccessView(8, modernBuf?.GPUVirtualAddress ?? dummyUav.GPUVirtualAddress);

        commands.Dispatch((vertexCount + ThreadsPerGroup - 1) / ThreadsPerGroup, 1, 1);
        outlineDispatches++;

        commands.ResourceBarrierUnorderedAccessView(expandedBuf);
        commands.ResourceBarrierTransition(expandedBuf, ResourceStates.UnorderedAccess, ResourceStates.VertexAndConstantBuffer);
        cache.SetWritableGpuBufferState(prefix + ":expanded", ResourceStates.VertexAndConstantBuffer);

        if (needWidthSource)
        {
            commands.ResourceBarrierUnorderedAccessView(widthSourceBuf!);
            commands.ResourceBarrierTransition(widthSourceBuf!, ResourceStates.UnorderedAccess, ResourceStates.VertexAndConstantBuffer);
            cache.SetWritableGpuBufferState(prefix + ":width-source", ResourceStates.VertexAndConstantBuffer);
        }
        if (modern)
        {
            commands.ResourceBarrierUnorderedAccessView(modernBuf!);
            commands.ResourceBarrierTransition(modernBuf!, ResourceStates.UnorderedAccess, ResourceStates.VertexAndConstantBuffer);
            cache.SetWritableGpuBufferState(prefix + ":modern-stream", ResourceStates.VertexAndConstantBuffer);
        }

        commands.ResourceBarrierTransition(vb, ResourceStates.NonPixelShaderResource, ResourceStates.VertexAndConstantBuffer);
        commands.ResourceBarrierTransition(staticOutlineGpu, ResourceStates.NonPixelShaderResource, ResourceStates.VertexAndConstantBuffer);
        if (hasAdditionalUv && additionalUvs is not null)
        {
            var uvGpu = cache.BufferByIdentity("original-additional-uv", MemoryMarshal.AsBytes(additionalUvs.Vertices), ResourceStates.VertexAndConstantBuffer, additionalUvs);
            commands.ResourceBarrierTransition(uvGpu, ResourceStates.NonPixelShaderResource, ResourceStates.VertexAndConstantBuffer);
        }

        if (streamPrefix is null)
        {
            var nextState = new OutlineSlotState
            {
                SurfaceBuffer = vb,
                StaticOutlineRevision = staticRevision,
                Ranges = scene.DeformationRanges.Select(r => (r.Start, r.Count)).ToArray(),
                ModelBaseBits = modelBaseBits,
                WidthBits = widthBits,
                HasAdditionalUv = hasAdditionalUv,
                AdditionalUvs = additionalUvs,
                Expanded = expandedBuf,
                ModernOutline = modernBuf,
                WidthSource = widthSourceBuf,
                HasModern = modern,
                HasWidthSource = needWidthSource,
            };
            slots[slot] = nextState;
            runtime.OnAbort(() =>
            {
                if (ReferenceEquals(slots[slot], nextState)) slots[slot] = prior;
            });
        }

        return (modern ? modernBuf! : expandedBuf, widthSourceBuf, expandedBuf);
    }

    private long rayGiLastFrameId = -1;
    private ID3D12Resource? rayGiLastVb, rayGiLastIb, rayGiLastTriBuf, rayGiLastPosBuf;
    private Matrix4x4 rayGiLastWorld;

    internal (ID3D12Resource Triangles, ID3D12Resource BlasPositions) BuildRayGiTriangles(
        OriginalFrameScene frame,
        ID3D12Resource vb,
        ID3D12Resource ib)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!runtime.IsRecording) throw new InvalidOperationException("GPU Ray GI triangle build requires an open GPU recording.");
        var scene = frame.Surface.Snapshot;
        int triangleCount = scene.Indices.Length / 3;
        if (triangleCount == 0) throw new InvalidOperationException("Ray GI requires at least one triangle.");

        if (rayGiLastFrameId == runtime.FrameId && ReferenceEquals(rayGiLastVb, vb) && ReferenceEquals(rayGiLastIb, ib)
            && rayGiLastWorld == scene.World && rayGiLastTriBuf is not null && rayGiLastPosBuf is not null)
            return (rayGiLastTriBuf, rayGiLastPosBuf);

        EnsureRayGiBuildPipeline();
        var materialTable = GetOrUploadTriangleMaterialTable(scene);

        var trianglesBuf = cache.WritableGpuBuffer("gpu-raygi:triangles", triangleCount, 160, out var triState, out _);
        var positionsBuf = cache.WritableGpuBuffer("gpu-raygi:blas-positions", triangleCount * 3, 12, out var posState, out _);

        var commands = runtime.Commands;
        if (triState != ResourceStates.UnorderedAccess)
            commands.ResourceBarrierTransition(trianglesBuf, triState, ResourceStates.UnorderedAccess);
        if (posState != ResourceStates.UnorderedAccess)
            commands.ResourceBarrierTransition(positionsBuf, posState, ResourceStates.UnorderedAccess);

        commands.ResourceBarrierTransition(vb, ResourceStates.VertexAndConstantBuffer, ResourceStates.NonPixelShaderResource);
        commands.ResourceBarrierTransition(ib, ResourceStates.IndexBuffer, ResourceStates.NonPixelShaderResource);

        bool hasWorld = scene.World != Matrix4x4.Identity;
        Matrix4x4 normalWorld = Matrix4x4.Identity;
        if (hasWorld)
        {
            if (!Matrix4x4.Invert(scene.World, out var inverse))
                throw new ArgumentException("Scene World must be invertible.");
            normalWorld = Matrix4x4.Transpose(inverse);
        }

        var constants = new RayGiBuildConstants
        {
            TriangleCount = (uint)triangleCount,
            HasWorld = hasWorld ? 1u : 0u,
            World = scene.World,
            NormalWorld = normalWorld,
        };
        ulong cbAddr = StageStruct(in constants);

        commands.SetComputeRootSignature(rayGiBuildPipeline!.Root);
        commands.SetPipelineState(rayGiBuildPipeline.State);
        commands.SetComputeRootConstantBufferView(0, cbAddr);
        commands.SetComputeRootShaderResourceView(1, vb.GPUVirtualAddress);
        commands.SetComputeRootShaderResourceView(2, ib.GPUVirtualAddress);
        commands.SetComputeRootShaderResourceView(3, materialTable.GPUVirtualAddress);
        commands.SetComputeRootUnorderedAccessView(4, trianglesBuf.GPUVirtualAddress);
        commands.SetComputeRootUnorderedAccessView(5, positionsBuf.GPUVirtualAddress);

        commands.Dispatch((triangleCount + ThreadsPerGroup - 1) / ThreadsPerGroup, 1, 1);
        rayGiTriangleDispatches++;

        commands.ResourceBarrierUnorderedAccessView(trianglesBuf);
        commands.ResourceBarrierUnorderedAccessView(positionsBuf);
        commands.ResourceBarrierTransition(trianglesBuf, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        commands.ResourceBarrierTransition(positionsBuf, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        cache.SetWritableGpuBufferState("gpu-raygi:triangles", ResourceStates.NonPixelShaderResource);
        cache.SetWritableGpuBufferState("gpu-raygi:blas-positions", ResourceStates.NonPixelShaderResource);

        commands.ResourceBarrierTransition(vb, ResourceStates.NonPixelShaderResource, ResourceStates.VertexAndConstantBuffer);
        commands.ResourceBarrierTransition(ib, ResourceStates.NonPixelShaderResource, ResourceStates.IndexBuffer);

        rayGiLastFrameId = runtime.FrameId;
        rayGiLastVb = vb;
        rayGiLastIb = ib;
        rayGiLastWorld = scene.World;
        rayGiLastTriBuf = trianglesBuf;
        rayGiLastPosBuf = positionsBuf;
        runtime.OnAbort(() =>
        {
            if (rayGiLastFrameId == runtime.FrameId) rayGiLastFrameId = -1;
        });

        return (trianglesBuf, positionsBuf);
    }

    internal ID3D12Resource PackTemporalBuffer(
        string streamName,
        int vertexCount,
        int stride,
        uint mode,
        ID3D12Resource currA,
        ResourceStates currAState,
        ID3D12Resource prevA,
        ResourceStates prevAState,
        ID3D12Resource? auxSurface = null,
        ResourceStates auxSurfaceState = ResourceStates.VertexAndConstantBuffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!runtime.IsRecording) throw new InvalidOperationException("Temporal buffer packing requires an open GPU recording.");
        EnsureTemporalPipeline();

        var outBuf = cache.WritableGpuBuffer(streamName, vertexCount, stride, out var outState, out _);
        var commands = runtime.Commands;
        if (outState != ResourceStates.UnorderedAccess)
            commands.ResourceBarrierTransition(outBuf, outState, ResourceStates.UnorderedAccess);

        bool samePrev = ReferenceEquals(currA, prevA);
        bool sameAuxA = ReferenceEquals(auxSurface, currA);
        bool sameAuxPrev = ReferenceEquals(auxSurface, prevA);
        if (currAState != ResourceStates.NonPixelShaderResource)
            commands.ResourceBarrierTransition(currA, currAState, ResourceStates.NonPixelShaderResource);
        if (!samePrev && prevAState != ResourceStates.NonPixelShaderResource)
            commands.ResourceBarrierTransition(prevA, prevAState, ResourceStates.NonPixelShaderResource);
        if (auxSurface is not null && !sameAuxA && !sameAuxPrev && auxSurfaceState != ResourceStates.NonPixelShaderResource)
            commands.ResourceBarrierTransition(auxSurface, auxSurfaceState, ResourceStates.NonPixelShaderResource);

        var cb = new TemporalPackConstants { VertexCount = (uint)vertexCount, Mode = mode };
        ulong cbAddr = StageStruct(in cb);

        commands.SetComputeRootSignature(temporalPipeline!.Root);
        commands.SetPipelineState(temporalPipeline.State);
        commands.SetComputeRootConstantBufferView(0, cbAddr);
        commands.SetComputeRootShaderResourceView(1, currA.GPUVirtualAddress);
        commands.SetComputeRootShaderResourceView(2, prevA.GPUVirtualAddress);
        commands.SetComputeRootShaderResourceView(3, (auxSurface ?? EnsureDummyBuffer()).GPUVirtualAddress);
        commands.SetComputeRootUnorderedAccessView(4, outBuf.GPUVirtualAddress);

        commands.Dispatch((vertexCount + ThreadsPerGroup - 1) / ThreadsPerGroup, 1, 1);

        commands.ResourceBarrierUnorderedAccessView(outBuf);
        commands.ResourceBarrierTransition(outBuf, ResourceStates.UnorderedAccess, ResourceStates.VertexAndConstantBuffer);
        cache.SetWritableGpuBufferState(streamName, ResourceStates.VertexAndConstantBuffer);

        if (currAState != ResourceStates.NonPixelShaderResource)
            commands.ResourceBarrierTransition(currA, ResourceStates.NonPixelShaderResource, currAState);
        if (!samePrev && prevAState != ResourceStates.NonPixelShaderResource)
            commands.ResourceBarrierTransition(prevA, ResourceStates.NonPixelShaderResource, prevAState);
        if (auxSurface is not null && !sameAuxA && !sameAuxPrev && auxSurfaceState != ResourceStates.NonPixelShaderResource)
            commands.ResourceBarrierTransition(auxSurface, ResourceStates.NonPixelShaderResource, auxSurfaceState);

        return outBuf;
    }

    internal ID3D12Resource? TryResolveSnapshotVerticesOnGpu(SceneSnapshot snapshot, string scratchStream)
    {
        if (!snapshot.HasDeferredDeformation) return null;
        if (TryFindRetainedTemporalSnapshot(snapshot, out var record) && record!.SurfaceVertices is not null)
            return record.SurfaceVertices;

        var vb = cache.SkinnedVertexBuffer(scratchStream, snapshot.StaticVertexView, out bool wrote);
        Skin(vb, snapshot, wrote);
        return vb;
    }

    internal (ID3D12Resource? OriginalExpanded, ID3D12Resource? ModernExpanded, ID3D12Resource? WidthSource) ResolveTemporalOutlineBuffers(
        OriginalFrameScene frame,
        ID3D12Resource surfaceVb,
        bool needOriginal,
        bool needModern,
        bool needWidthSource,
        string prefix)
    {
        if (TryFindRetainedTemporalFrame(frame, out var record) && record is not null)
        {
            if ((!needOriginal || record.OriginalExpanded is not null)
                && (!needModern || record.ModernExpanded is not null)
                && (!needWidthSource || record.WidthSource is not null))
                return (record.OriginalExpanded, record.ModernExpanded, record.WidthSource);
        }

        ID3D12Resource? origExp = null, modExp = null, widthSrc = null;
        if (needOriginal)
        {
            var res = GetOutlineStreams(frame, surfaceVb, modern: false, needWidthSource: needWidthSource, streamPrefix: prefix + ":orig");
            origExp = res.ExpandedRaw;
            widthSrc = res.WidthSource;
        }
        if (needModern)
        {
            var res = GetOutlineStreams(frame, surfaceVb, modern: true, needWidthSource: needWidthSource && widthSrc is null, streamPrefix: prefix + ":mod");
            modExp = res.ExpandedRaw;
            widthSrc ??= res.WidthSource;
        }
        return (origExp, modExp, widthSrc);
    }

    internal void CaptureTemporalSnapshot(
        OriginalFrameScene frame,
        ID3D12Resource surfaceVb,
        ID3D12Resource? origExpanded,
        ID3D12Resource? modExpanded,
        ID3D12Resource? widthSource)
    {
        if (!runtime.IsRecording) return;
        var scene = frame.Surface.Snapshot;
        int slotIdx = retainedTemporalCursor & 1;
        retainedTemporalCursor++;

        ID3D12Resource CopyStatic(string name, ID3D12Resource src, int count, int stride, ResourceStates srcState)
        {
            var dst = cache.WritableGpuBuffer(name + ":" + slotIdx, count, stride, out var dstState, out _);
            var cmds = runtime.Commands;
            if (dstState != ResourceStates.CopyDest)
                cmds.ResourceBarrierTransition(dst, dstState, ResourceStates.CopyDest);
            cmds.ResourceBarrierTransition(src, srcState, ResourceStates.CopySource);
            cmds.CopyBufferRegion(dst, 0, src, 0, checked((ulong)count * (ulong)stride));
            cmds.ResourceBarrierTransition(src, ResourceStates.CopySource, srcState);
            cmds.ResourceBarrierTransition(dst, ResourceStates.CopyDest, ResourceStates.VertexAndConstantBuffer);
            cache.SetWritableGpuBufferState(name + ":" + slotIdx, ResourceStates.VertexAndConstantBuffer);
            return dst;
        }

        int vc = scene.VertexCount;
        var savedSurface = CopyStatic("temporal-hist-vb", surfaceVb, vc, 32, ResourceStates.VertexAndConstantBuffer);
        var savedOrig = origExpanded is not null ? CopyStatic("temporal-hist-orig", origExpanded, vc, 16, ResourceStates.VertexAndConstantBuffer) : null;
        var savedMod = modExpanded is not null ? CopyStatic("temporal-hist-mod", modExpanded, vc, 16, ResourceStates.VertexAndConstantBuffer) : null;
        var savedWidth = widthSource is not null ? CopyStatic("temporal-hist-ws", widthSource, vc, 16, ResourceStates.VertexAndConstantBuffer) : null;

        var prevRecord = retainedTemporalSlots[slotIdx];
        var nextRecord = new TemporalHistoryRecord
        {
            Snapshot = scene,
            SurfaceVertices = savedSurface,
            Outline = frame.Outline,
            OutlineOptions = frame.OutlineOptions,
            Materials = frame.Materials,
            OriginalExpanded = savedOrig,
            ModernExpanded = savedMod,
            WidthSource = savedWidth,
        };
        retainedTemporalSlots[slotIdx] = nextRecord;
        runtime.OnAbort(() =>
        {
            if (ReferenceEquals(retainedTemporalSlots[slotIdx], nextRecord))
                retainedTemporalSlots[slotIdx] = prevRecord;
        });
    }

    private bool TryFindRetainedTemporalSnapshot(SceneSnapshot snapshot, out TemporalHistoryRecord? record)
    {
        for (int i = 0; i < retainedTemporalSlots.Length; i++)
        {
            if (retainedTemporalSlots[i] is { } r && ReferenceEquals(r.Snapshot, snapshot))
            {
                record = r;
                return true;
            }
        }
        record = null;
        return false;
    }

    private bool TryFindRetainedTemporalFrame(OriginalFrameScene frame, out TemporalHistoryRecord? record)
    {
        for (int i = 0; i < retainedTemporalSlots.Length; i++)
        {
            if (retainedTemporalSlots[i] is { } r
                && ReferenceEquals(r.Snapshot, frame.Surface.Snapshot)
                && ReferenceEquals(r.Outline, frame.Outline)
                && Equals(r.OutlineOptions, frame.OutlineOptions)
                && ReferenceEquals(r.Materials, frame.Materials))
            {
                record = r;
                return true;
            }
        }
        record = null;
        return false;
    }

    private StaticModelGpu GetOrUploadModel(SceneSkinningModel model)
    {
        if (modelCache.TryGetValue(model, out var cached)) return cached;
        var rest = UploadImmutableStatic(MemoryMarshal.AsBytes(model.RestVertices));
        ID3D12Resource? bindings = null, sdef = null;
        try
        {
            bindings = UploadImmutableStatic(MemoryMarshal.AsBytes(model.Bindings));
            if (model.HasSdef)
                sdef = UploadImmutableStatic(MemoryMarshal.AsBytes(model.Sdef));
        }
        catch
        {
            sdef?.Dispose();
            bindings?.Dispose();
            rest.Dispose();
            throw;
        }
        var created = new StaticModelGpu(rest, bindings, sdef);
        modelCache.Add(model, created);
        runtime.OnAbort(() =>
        {
            if (modelCache.TryGetValue(model, out var current) && ReferenceEquals(current, created))
            {
                modelCache.Remove(model);
                created.Dispose();
            }
        });
        return created;
    }

    private ID3D12Resource GetOrUploadTriangleMaterialTable(SceneSnapshot scene)
    {
        var idxId = new D3D12IndexStreamIdentity(scene);
        if (cachedTriangleMaterials is { } existing && existing.IndexIdentity.Equals(idxId)
            && existing.MaterialRanges.Length == scene.Materials.Count)
        {
            bool match = true;
            for (int i = 0; i < existing.MaterialRanges.Length; i++)
            {
                if (existing.MaterialRanges[i].StartIndex != scene.Materials[i].StartIndex
                    || existing.MaterialRanges[i].IndexCount != scene.Materials[i].IndexCount)
                { match = false; break; }
            }
            if (match) return existing.TriangleMaterials;
        }

        int triCount = scene.Indices.Length / 3;
        var ids = new uint[triCount];
        var ranges = new (int StartIndex, int IndexCount)[scene.Materials.Count];
        for (int m = 0; m < scene.Materials.Count; m++)
        {
            var mat = scene.Materials[m];
            ranges[m] = (mat.StartIndex, mat.IndexCount);
            int triStart = mat.StartIndex / 3;
            int triEnd = (mat.StartIndex + mat.IndexCount) / 3;
            ids.AsSpan(triStart, triEnd - triStart).Fill((uint)m);
        }

        var gpu = UploadImmutableStatic(MemoryMarshal.AsBytes(ids.AsSpan()));
        var created = new TopologyMaterialTableGpu(idxId, ranges, gpu);
        var prev = cachedTriangleMaterials;
        cachedTriangleMaterials = created;
        if (prev is not null) runtime.Own(prev);
        runtime.OnAbort(() =>
        {
            if (ReferenceEquals(cachedTriangleMaterials, created))
            {
                cachedTriangleMaterials = null;
                created.Dispose();
            }
        });
        return gpu;
    }

    private ID3D12Resource UploadImmutableStatic(ReadOnlySpan<byte> bytes)
    {
        var gpu = runtime.Device.CreateCommittedResource(HeapType.Default,
            ResourceDescription.Buffer((ulong)bytes.Length), ResourceStates.CopyDest);
        try
        {
            var upload = cache.StageFrameBytes(bytes);
            runtime.Commands.CopyBufferRegion(gpu, 0, upload.Resource, upload.Offset, (ulong)bytes.Length);
            runtime.Commands.ResourceBarrierTransition(gpu, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource);
            runtime.BufferUploads++;
            staticUploadBytes += bytes.Length;
            return gpu;
        }
        catch
        {
            gpu.Dispose();
            throw;
        }
    }

    private (ulong GpuAddress, D3D12BufferUploadStaging.Allocation Alloc) StageBytes(ReadOnlySpan<byte> bytes)
    {
        var alloc = cache.StageFrameBytes(bytes);
        dynamicUploadBytes += bytes.Length;
        return (alloc.Resource.GPUVirtualAddress + alloc.Offset, alloc);
    }

    private ulong StageStruct<T>(in T value) where T : unmanaged
    {
        var span = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in value), 1));
        dynamicUploadBytes += span.Length;
        return runtime.UploadConstants(span);
    }

    private ID3D12Resource EnsureDummyBuffer()
    {
        if (dummyBuffer is not null) return dummyBuffer;
        var res = runtime.Device.CreateCommittedResource(HeapType.Default, ResourceDescription.Buffer(64), ResourceStates.Common);
        dummyBuffer = res;
        return res;
    }

    private ID3D12Resource? dummyUavBuffer;
    private ID3D12Resource EnsureDummyUavBuffer()
    {
        if (dummyUavBuffer is not null) return dummyUavBuffer;
        var res = runtime.Device.CreateCommittedResource(HeapType.Default,
            ResourceDescription.Buffer(64, ResourceFlags.AllowUnorderedAccess), ResourceStates.Common);
        dummyUavBuffer = res;
        return res;
    }

    private static bool SameRanges((int Start, int Count)[]? a, IReadOnlyList<SceneDeformationRange> b)
    {
        if (a is null || a.Length != b.Count) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i].Start != b[i].Start || a[i].Count != b[i].Count) return false;
        return true;
    }

    private DxcShaderCompiler EnsureCompiler() => compiler ??= new DxcShaderCompiler();

    private void EnsureSkinPipeline()
    {
        if (skinPipeline is not null) return;
        RootParameter[] parameters =
        [
            new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(0, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(1, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(2, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(3, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(4, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(5, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(6, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(7, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(8, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(9, 0), ShaderVisibility.All),
            new(RootParameterType.UnorderedAccessView, new RootDescriptor(0, 0), ShaderVisibility.All),
        ];
        skinPipeline = CreateComputePipeline(SkinComputeSource, "CSSkinVertices", parameters);
    }

    private void EnsureOutlinePipeline()
    {
        if (outlinePipeline is not null) return;
        RootParameter[] parameters =
        [
            new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(0, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(1, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(2, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(3, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(4, 0), ShaderVisibility.All),
            new(RootParameterType.UnorderedAccessView, new RootDescriptor(0, 0), ShaderVisibility.All),
            new(RootParameterType.UnorderedAccessView, new RootDescriptor(1, 0), ShaderVisibility.All),
            new(RootParameterType.UnorderedAccessView, new RootDescriptor(2, 0), ShaderVisibility.All),
        ];
        outlinePipeline = CreateComputePipeline(OutlineComputeSource, "CSExpandOutline", parameters);
    }

    private void EnsureTemporalPipeline()
    {
        if (temporalPipeline is not null) return;
        RootParameter[] parameters =
        [
            new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(0, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(1, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(2, 0), ShaderVisibility.All),
            new(RootParameterType.UnorderedAccessView, new RootDescriptor(0, 0), ShaderVisibility.All),
        ];
        temporalPipeline = CreateComputePipeline(TemporalPackSource, "CSPackTemporal", parameters);
    }

    private void EnsureRayGiBuildPipeline()
    {
        if (rayGiBuildPipeline is not null) return;
        RootParameter[] parameters =
        [
            new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(0, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(1, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(2, 0), ShaderVisibility.All),
            new(RootParameterType.UnorderedAccessView, new RootDescriptor(0, 0), ShaderVisibility.All),
            new(RootParameterType.UnorderedAccessView, new RootDescriptor(1, 0), ShaderVisibility.All),
        ];
        rayGiBuildPipeline = CreateComputePipeline(RayGiBuildSource, "CSBuildRayGiTriangles", parameters);
    }

    private ComputePipeline CreateComputePipeline(string source, string entryPoint, RootParameter[] parameters)
    {
        var compiled = EnsureCompiler().CompileCompute(source, entryPoint, "cs_6_0");
        runtime.ShaderCompilations++;
        var root = runtime.Device.CreateRootSignature<ID3D12RootSignature>(
            new RootSignatureDescription(RootSignatureFlags.None, parameters), RootSignatureVersion.Version10);
        try
        {
            var pso = runtime.Device.CreateComputePipelineState(new ComputePipelineStateDescription
            {
                RootSignature = root,
                ComputeShader = compiled.Dxil,
            });
            runtime.PipelineCreations++;
            return new(root, pso);
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var pair in modelCache) pair.Value.Dispose();
        cachedTriangleMaterials?.Dispose();
        cachedTriangleMaterials = null;
        dummyUavBuffer?.Dispose();
        dummyUavBuffer = null;
        dummyBuffer?.Dispose();
        dummyBuffer = null;
        rayGiBuildPipeline?.Dispose();
        rayGiBuildPipeline = null;
        temporalPipeline?.Dispose();
        temporalPipeline = null;
        outlinePipeline?.Dispose();
        outlinePipeline = null;
        skinPipeline?.Dispose();
        skinPipeline = null;
    }

    private const string SkinComputeSource = """
        cbuffer SkinConstants : register(b0) {
            uint RangeStart;
            uint VertexCount;
            uint MorphCount;
            uint HasSdef;
            uint HasQdef;
            uint HasWorld;
            uint Pad0;
            uint Pad1;
            float4 WorldR0;
            float4 WorldR1;
            float4 WorldR2;
            float4 WorldR3;
            float4 NormalWorldR0;
            float4 NormalWorldR1;
            float4 NormalWorldR2;
            float4 NormalWorldR3;
        };

        ByteAddressBuffer RestVertices : register(t0);
        ByteAddressBuffer Bindings : register(t1);
        ByteAddressBuffer SdefBindings : register(t2);
        ByteAddressBuffer SkinMatrices : register(t3);
        ByteAddressBuffer SdefRotations : register(t4);
        ByteAddressBuffer QdefReal : register(t5);
        ByteAddressBuffer QdefDual : register(t6);
        ByteAddressBuffer MorphIndices : register(t7);
        ByteAddressBuffer MorphPositions : register(t8);
        ByteAddressBuffer MorphUvs : register(t9);
        RWByteAddressBuffer OutputVertices : register(u0);

        float4 LoadFloat4(ByteAddressBuffer buf, int index) {
            return asfloat(buf.Load4((uint)index * 16u));
        }

        float3 TransformPointFromPalette(float3 p, ByteAddressBuffer matrices, int bone) {
            uint addr = (uint)bone * 64u;
            float4 r0 = asfloat(matrices.Load4(addr));
            float4 r1 = asfloat(matrices.Load4(addr + 16u));
            float4 r2 = asfloat(matrices.Load4(addr + 32u));
            float4 r3 = asfloat(matrices.Load4(addr + 48u));
            return p.x * r0.xyz + p.y * r1.xyz + p.z * r2.xyz + r3.xyz;
        }

        float3 TransformNormalFromPalette(float3 n, ByteAddressBuffer matrices, int bone) {
            uint addr = (uint)bone * 64u;
            float4 r0 = asfloat(matrices.Load4(addr));
            float4 r1 = asfloat(matrices.Load4(addr + 16u));
            float4 r2 = asfloat(matrices.Load4(addr + 32u));
            return n.x * r0.xyz + n.y * r1.xyz + n.z * r2.xyz;
        }

        float4 QuatMul(float4 a, float4 b) {
            return float4(
                a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
                a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
                a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
                a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);
        }

        float3 QuatRotate(float3 v, float4 q) {
            float3 t = 2.0f * cross(q.xyz, v);
            return v + q.w * t + cross(q.xyz, t);
        }

        float4 QuatSlerp(float4 q1, float4 q2, float t) {
            float d = dot(q1, q2);
            if (d < 0.0f) {
                d = -d;
                q2 = -q2;
            }
            float s1, s2;
            if (d > 0.999995f) {
                s1 = 1.0f - t;
                s2 = t;
            } else {
                float omega = acos(clamp(d, 0.0f, 1.0f));
                float invSin = 1.0f / sin(omega);
                s1 = sin((1.0f - t) * omega) * invSin;
                s2 = sin(t * omega) * invSin;
            }
            float4 res = s1 * q1 + s2 * q2;
            float lenSq = dot(res, res);
            return lenSq > 0.0f ? res * (1.0f / sqrt(lenSq)) : float4(0.0f, 0.0f, 0.0f, 1.0f);
        }

        void AddBdefInfluence(int bone, float w, float3 vPos, float3 vNorm, inout float3 pos, inout float3 normal) {
            if (w == 0.0f) return;
            pos += TransformPointFromPalette(vPos, SkinMatrices, bone) * w;
            normal += TransformNormalFromPalette(vNorm, SkinMatrices, bone) * w;
        }

        [numthreads(256, 1, 1)]
        void CSSkinVertices(uint3 dispatchId : SV_DispatchThreadID) {
            uint tid = dispatchId.x;
            if (tid >= VertexCount) return;

            uint vAddr = tid * 32u;
            uint4 va = RestVertices.Load4(vAddr);
            uint4 vb = RestVertices.Load4(vAddr + 16u);
            float3 vPos = asfloat(va.xyz);
            float3 restNormal = float3(asfloat(va.w), asfloat(vb.x), asfloat(vb.y));
            float2 vUv = asfloat(vb.zw);

            if (MorphCount > 0u) {
                int lo = 0;
                int hi = (int)MorphCount - 1;
                while (lo <= hi) {
                    int mid = (lo + hi) >> 1;
                    int idx = asint(MorphIndices.Load((uint)mid * 4u));
                    if (idx == (int)tid) {
                        vPos += asfloat(MorphPositions.Load3((uint)mid * 12u));
                        vUv += asfloat(MorphUvs.Load2((uint)mid * 8u));
                        break;
                    }
                    if (idx < (int)tid) lo = mid + 1;
                    else hi = mid - 1;
                }
            }

            uint bAddr = tid * 36u;
            int4 bHead = asint(Bindings.Load4(bAddr));
            int kind = bHead.x;
            int b0 = bHead.y;
            int b1 = bHead.z;
            int b2 = bHead.w;
            int b3 = asint(Bindings.Load(bAddr + 16u));
            float4 weights = asfloat(Bindings.Load4(bAddr + 20u));

            float3 pos = float3(0.0f, 0.0f, 0.0f);
            float3 normal = float3(0.0f, 0.0f, 0.0f);

            if (kind == 4) {
                float4 refQ = float4(0.0f, 0.0f, 0.0f, 0.0f);
                float4 blendedReal = float4(0.0f, 0.0f, 0.0f, 0.0f);
                float4 blendedDual = float4(0.0f, 0.0f, 0.0f, 0.0f);
                bool hasRef = false;
                int bonesArr[4] = { b0, b1, b2, b3 };
                float weightsArr[4] = { weights.x, weights.y, weights.z, weights.w };
                [unroll]
                for (uint slot = 0u; slot < 4u; slot++) {
                    float w = weightsArr[slot];
                    if (w == 0.0f) continue;
                    int bone = bonesArr[slot];
                    float4 qr = LoadFloat4(QdefReal, bone);
                    float4 qd = LoadFloat4(QdefDual, bone);
                    if (!hasRef) { refQ = qr; hasRef = true; }
                    if (dot(refQ, qr) < 0.0f) w = -w;
                    blendedReal += qr * w;
                    blendedDual += qd * w;
                }
                float normSq = dot(blendedReal, blendedReal);
                float invNorm = normSq > 1e-12f ? (1.0f / sqrt(normSq)) : 1.0f;
                blendedReal *= invNorm;
                blendedDual *= invNorm;
                blendedDual -= blendedReal * dot(blendedReal, blendedDual);
                float4 transQ = QuatMul(blendedDual, float4(-blendedReal.xyz, blendedReal.w)) * 2.0f;
                pos = QuatRotate(vPos, blendedReal) + transQ.xyz;
                normal = QuatRotate(restNormal, blendedReal);
            } else if (kind == 3) {
                int bone0 = weights.x > 0.0f ? b0 : b1;
                int bone1 = weights.y > 0.0f ? b1 : b0;
                float4 rot = QuatSlerp(LoadFloat4(SdefRotations, bone0), LoadFloat4(SdefRotations, bone1), weights.y);
                uint sAddr = tid * 36u;
                float3 sdefC = asfloat(SdefBindings.Load3(sAddr));
                float3 anchor0 = asfloat(SdefBindings.Load3(sAddr + 12u));
                float3 anchor1 = asfloat(SdefBindings.Load3(sAddr + 24u));
                float3 offset = TransformPointFromPalette(anchor0, SkinMatrices, bone0) * weights.x
                    + TransformPointFromPalette(anchor1, SkinMatrices, bone1) * weights.y;
                pos = QuatRotate(vPos - sdefC, rot) + offset;
                normal = QuatRotate(restNormal, rot);
            } else {
                AddBdefInfluence(b0, weights.x, vPos, restNormal, pos, normal);
                if (kind != 0) AddBdefInfluence(b1, weights.y, vPos, restNormal, pos, normal);
                if (kind == 2) {
                    AddBdefInfluence(b2, weights.z, vPos, restNormal, pos, normal);
                    AddBdefInfluence(b3, weights.w, vPos, restNormal, pos, normal);
                }
            }

            float nLenSq = dot(normal, normal);
            if (nLenSq > 1e-12f) {
                normal *= (1.0f / sqrt(nLenSq));
            } else {
                float rLenSq = dot(restNormal, restNormal);
                normal = rLenSq > 1e-12f ? restNormal * (1.0f / sqrt(rLenSq)) : float3(0.0f, 0.0f, 0.0f);
            }

            if (HasWorld != 0u) {
                pos = pos.x * WorldR0.xyz + pos.y * WorldR1.xyz + pos.z * WorldR2.xyz + WorldR3.xyz;
                normal = normal.x * NormalWorldR0.xyz + normal.y * NormalWorldR1.xyz + normal.z * NormalWorldR2.xyz;
                float wnLenSq = dot(normal, normal);
                normal = wnLenSq > 1e-12f ? normal * (1.0f / sqrt(wnLenSq)) : float3(0.0f, 0.0f, 0.0f);
            }

            uint outAddr = (RangeStart + tid) * 32u;
            OutputVertices.Store4(outAddr, uint4(asuint(pos), asuint(normal.x)));
            OutputVertices.Store4(outAddr + 16u, uint4(asuint(normal.y), asuint(normal.z), asuint(vUv)));
        }
        """;

    private const string OutlineComputeSource = """
        cbuffer OutlineExpandConstants : register(b0) {
            uint VertexCount;
            uint RangeCount;
            float ModelBase;
            uint WriteModern;
            uint WriteWidthSource;
            uint HasAdditionalUv;
            uint Pad0;
            uint Pad1;
        };

        ByteAddressBuffer SkinnedVertices : register(t0);
        ByteAddressBuffer StaticOutline : register(t1);
        ByteAddressBuffer DeferredRanges : register(t2);
        ByteAddressBuffer MaterialWidths : register(t3);
        ByteAddressBuffer AdditionalUvs : register(t4);

        RWByteAddressBuffer OutExpanded : register(u0);
        RWByteAddressBuffer OutWidthSource : register(u1);
        RWByteAddressBuffer OutModernOutline : register(u2);

        bool IsDeferred(uint index) {
            int lo = 0;
            int hi = (int)RangeCount - 1;
            while (lo <= hi) {
                int mid = (lo + hi) >> 1;
                uint2 bounds = DeferredRanges.Load2((uint)mid * 8u);
                if (index < bounds.x) hi = mid - 1;
                else if (index >= bounds.y) lo = mid + 1;
                else return true;
            }
            return false;
        }

        [numthreads(256, 1, 1)]
        void CSExpandOutline(uint3 dispatchId : SV_DispatchThreadID) {
            uint tid = dispatchId.x;
            if (tid >= VertexCount) return;

            uint sAddr = tid * 36u;
            float3 pos = asfloat(StaticOutline.Load3(sAddr));
            float3 nrm = asfloat(StaticOutline.Load3(sAddr + 12u));
            int mode = asint(StaticOutline.Load(sAddr + 24u));
            float vScale = asfloat(StaticOutline.Load(sAddr + 28u));
            int matIdx = asint(StaticOutline.Load(sAddr + 32u));

            uint vAddr = tid * 32u;
            uint4 va = SkinnedVertices.Load4(vAddr);
            uint4 vb = SkinnedVertices.Load4(vAddr + 16u);
            float3 surfPos = asfloat(va.xyz);
            float3 surfNrm = float3(asfloat(va.w), asfloat(vb.x), asfloat(vb.y));
            float2 surfUv = asfloat(vb.zw);

            if (IsDeferred(tid)) {
                pos = surfPos;
                nrm = surfNrm;
            }

            float width = asfloat(MaterialWidths.Load((uint)matIdx * 4u));
            precise float3 expanded;
            if (mode == 1) {
                expanded = pos - nrm * 0.005f;
            } else {
                expanded = pos + (((nrm * ModelBase) * vScale) * width);
            }

            OutExpanded.Store4(tid * 16u, uint4(asuint(expanded), asuint(1.0f)));

            if (WriteWidthSource != 0u) {
                float expandFlag = mode == 0 ? 1.0f : 0.0f;
                OutWidthSource.Store4(tid * 16u, uint4(asuint(pos), asuint(expandFlag)));
            }

            if (WriteModern != 0u) {
                float2 uv1 = HasAdditionalUv != 0u ? asfloat(AdditionalUvs.Load2(tid * 64u)) : float2(0.0f, 0.0f);
                uint mAddr = tid * 52u;
                OutModernOutline.Store3(mAddr, asuint(expanded));
                OutModernOutline.Store3(mAddr + 12u, asuint(surfPos));
                OutModernOutline.Store3(mAddr + 24u, asuint(surfNrm));
                OutModernOutline.Store2(mAddr + 36u, asuint(surfUv));
                OutModernOutline.Store2(mAddr + 44u, asuint(uv1));
            }
        }
        """;

    private const string TemporalPackSource = """
        cbuffer TemporalPackConstants : register(b0) {
            uint VertexCount;
            uint Mode;
            uint Pad0;
            uint Pad1;
        };

        ByteAddressBuffer CurrA : register(t0);
        ByteAddressBuffer PrevA : register(t1);
        ByteAddressBuffer AuxSurface : register(t2);
        RWByteAddressBuffer Output : register(u0);

        [numthreads(256, 1, 1)]
        void CSPackTemporal(uint3 dispatchId : SV_DispatchThreadID) {
            uint tid = dispatchId.x;
            if (tid >= VertexCount) return;

            if (Mode == 0u) {
                // TemporalVertex: 24B (float3 Current, float3 Previous) from 32B surfaces.
                float3 currPos = asfloat(CurrA.Load3(tid * 32u));
                float3 prevPos = asfloat(PrevA.Load3(tid * 32u));
                uint outAddr = tid * 24u;
                Output.Store3(outAddr, asuint(currPos));
                Output.Store3(outAddr + 12u, asuint(prevPos));
                return;
            }
            if (Mode == 4u) {
                // SourcePair: 32B (float4 Current, float4 Previous) from 16B WidthSources.
                uint4 c = CurrA.Load4(tid * 16u);
                uint4 p = PrevA.Load4(tid * 16u);
                uint outAddr = tid * 32u;
                Output.Store4(outAddr, c);
                Output.Store4(outAddr + 16u, p);
                return;
            }

            // CharacterTemporalVertex: 56B (Current, Previous, Normal, Uv, SourcePosition)
            uint vAddr = tid * 32u;
            uint4 va = AuxSurface.Load4(vAddr);
            uint4 vb = AuxSurface.Load4(vAddr + 16u);
            float3 surfPos = asfloat(va.xyz);
            float3 surfNrm = float3(asfloat(va.w), asfloat(vb.x), asfloat(vb.y));
            float2 surfUv = asfloat(vb.zw);

            float3 cOut, pOut, oNrm;
            float2 oUv;
            if (Mode == 1u) {
                cOut = asfloat(CurrA.Load3(vAddr));
                pOut = asfloat(PrevA.Load3(vAddr));
                oNrm = surfNrm;
                oUv = surfUv;
            } else if (Mode == 2u) {
                cOut = asfloat(CurrA.Load3(tid * 16u));
                pOut = asfloat(PrevA.Load3(tid * 16u));
                oNrm = float3(0.0f, 0.0f, 0.0f);
                oUv = float2(0.0f, 0.0f);
            } else {
                cOut = asfloat(CurrA.Load3(tid * 16u));
                pOut = asfloat(PrevA.Load3(tid * 16u));
                oNrm = surfNrm;
                oUv = surfUv;
            }

            uint dst = tid * 56u;
            Output.Store3(dst, asuint(cOut));
            Output.Store3(dst + 12u, asuint(pOut));
            Output.Store3(dst + 24u, asuint(oNrm));
            Output.Store2(dst + 36u, asuint(oUv));
            Output.Store3(dst + 44u, asuint(surfPos));
        }
        """;

    private const string RayGiBuildSource = """
        cbuffer RayGiBuildConstants : register(b0) {
            uint TriangleCount;
            uint HasWorld;
            uint Pad0;
            uint Pad1;
            float4 WorldR0;
            float4 WorldR1;
            float4 WorldR2;
            float4 WorldR3;
            float4 NormalWorldR0;
            float4 NormalWorldR1;
            float4 NormalWorldR2;
            float4 NormalWorldR3;
        };

        ByteAddressBuffer SkinnedVertices : register(t0);
        ByteAddressBuffer Indices : register(t1);
        ByteAddressBuffer TriangleMaterials : register(t2);
        RWByteAddressBuffer OutTriangles : register(u0);
        RWByteAddressBuffer OutBlasPositions : register(u1);

        [numthreads(256, 1, 1)]
        void CSBuildRayGiTriangles(uint3 dispatchId : SV_DispatchThreadID) {
            uint tid = dispatchId.x;
            if (tid >= TriangleCount) return;

            uint3 idx = Indices.Load3(tid * 12u);
            uint aAddr = idx.x * 32u;
            uint bAddr = idx.y * 32u;
            uint cAddr = idx.z * 32u;

            uint4 a0 = SkinnedVertices.Load4(aAddr); uint4 a1 = SkinnedVertices.Load4(aAddr + 16u);
            uint4 b0 = SkinnedVertices.Load4(bAddr); uint4 b1 = SkinnedVertices.Load4(bAddr + 16u);
            uint4 c0 = SkinnedVertices.Load4(cAddr); uint4 c1 = SkinnedVertices.Load4(cAddr + 16u);

            float3 pa = asfloat(a0.xyz); float3 na = float3(asfloat(a0.w), asfloat(a1.x), asfloat(a1.y)); float2 uva = asfloat(a1.zw);
            float3 pb = asfloat(b0.xyz); float3 nb = float3(asfloat(b0.w), asfloat(b1.x), asfloat(b1.y)); float2 uvb = asfloat(b1.zw);
            float3 pc = asfloat(c0.xyz); float3 nc = float3(asfloat(c0.w), asfloat(c1.x), asfloat(c1.y)); float2 uvc = asfloat(c1.zw);

            float3 wp0 = pa, wp1 = pb, wp2 = pc;
            float3 wn0 = na, wn1 = nb, wn2 = nc;
            if (HasWorld != 0u) {
                wp0 = pa.x * WorldR0.xyz + pa.y * WorldR1.xyz + pa.z * WorldR2.xyz + WorldR3.xyz;
                wp1 = pb.x * WorldR0.xyz + pb.y * WorldR1.xyz + pb.z * WorldR2.xyz + WorldR3.xyz;
                wp2 = pc.x * WorldR0.xyz + pc.y * WorldR1.xyz + pc.z * WorldR2.xyz + WorldR3.xyz;
                wn0 = na.x * NormalWorldR0.xyz + na.y * NormalWorldR1.xyz + na.z * NormalWorldR2.xyz;
                wn1 = nb.x * NormalWorldR0.xyz + nb.y * NormalWorldR1.xyz + nb.z * NormalWorldR2.xyz;
                wn2 = nc.x * NormalWorldR0.xyz + nc.y * NormalWorldR1.xyz + nc.z * NormalWorldR2.xyz;
            }

            uint matIdx = TriangleMaterials.Load(tid * 4u);
            uint tAddr = tid * 160u;
            OutTriangles.Store4(tAddr, uint4(asuint(wp0), 0u));
            OutTriangles.Store4(tAddr + 16u, uint4(asuint(wp1), 0u));
            OutTriangles.Store4(tAddr + 32u, uint4(asuint(wp2), 0u));
            OutTriangles.Store4(tAddr + 48u, uint4(asuint(wn0), asuint(uva.x)));
            OutTriangles.Store4(tAddr + 64u, uint4(asuint(wn1), asuint(uvb.x)));
            OutTriangles.Store4(tAddr + 80u, uint4(asuint(wn2), asuint(uvc.x)));
            OutTriangles.Store4(tAddr + 96u, asuint(float4(uva.y, uvb.y, uvc.y, nc.z)));
            OutTriangles.Store4(tAddr + 112u, uint4(asuint(na), asuint(nb.x)));
            OutTriangles.Store4(tAddr + 128u, uint4(asuint(nb.y), asuint(nb.z), asuint(nc.x), asuint(nc.y)));
            OutTriangles.Store4(tAddr + 144u, uint4(matIdx, tid, 1u, 0u));

            uint pAddr = tid * 36u;
            OutBlasPositions.Store3(pAddr, asuint(wp0));
            OutBlasPositions.Store3(pAddr + 12u, asuint(wp1));
            OutBlasPositions.Store3(pAddr + 24u, asuint(wp2));
        }
        """;
}
