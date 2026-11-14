using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using D3D12Feature = Vortice.Direct3D12.Feature;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>
/// Hardware inline-RayQuery provider. The shared GI renderer owns the compute pipeline,
/// bindings and frame submission; this class owns only acceleration-structure construction
/// and the backend-specific TraceClosest/TraceAny source.
/// </summary>
internal static class D3D12HardwareRayGi
{
    internal sealed class AccelerationStructure : IDisposable
    {
        internal ID3D12Resource BottomLevel { get; }
        internal ID3D12Resource TopLevel { get; }
        internal string BackendIdentity { get; }
        internal long OperationFrameId { get; }
        internal int TriangleCount { get; }
        internal bool UpdateCapable { get; }
        internal bool WasUpdate { get; }
        internal Vector3[] BuildSamples { get; }
        internal int UpdatesSinceBuild { get; }
        internal AccelerationStructure(ID3D12Resource bottomLevel, ID3D12Resource topLevel, string backendIdentity,
            long operationFrameId, int triangleCount, bool updateCapable, bool wasUpdate,
            Vector3[] buildSamples, int updatesSinceBuild)
        {
            BottomLevel = bottomLevel;
            TopLevel = topLevel;
            BackendIdentity = backendIdentity;
            OperationFrameId = operationFrameId;
            TriangleCount = triangleCount;
            UpdateCapable = updateCapable;
            WasUpdate = wasUpdate;
            BuildSamples = buildSamples;
            UpdatesSinceBuild = updatesSinceBuild;
        }
        public void Dispose()
        {
            TopLevel.Dispose();
            BottomLevel.Dispose();
        }
    }

    internal static string TraceSource => """
        bool TraceClosest(GiRay ray, out GiHit hit) {
            RayDesc desc;
            desc.Origin=ray.Origin; desc.Direction=ray.Direction; desc.TMin=ray.TMin; desc.TMax=ray.TMax;
            RayQuery<RAY_FLAG_FORCE_NON_OPAQUE> query;
            query.TraceRayInline(GiHardwareScene,RAY_FLAG_NONE,0xff,desc);
            bool found=false; float closest=ray.TMax;
            while(query.Proceed()) {
                if(query.CandidateType()!=CANDIDATE_NON_OPAQUE_TRIANGLE) continue;
                uint triangleIndex=query.CandidatePrimitiveIndex();
                float2 barycentrics=query.CandidateTriangleBarycentrics();
                float distance=query.CandidateTriangleRayT();
                if(distance<ray.TMin||distance>closest) continue;
                if(!AcceptCandidate(triangleIndex,barycentrics,ray.Direction)) continue;
                found=true; closest=distance;
                GiTriangle tri=GiTriangles[triangleIndex];
                uint front=dot(cross(tri.P1.xyz-tri.P0.xyz,tri.P2.xyz-tri.P0.xyz),ray.Direction)<0?1:0;
                hit.TriangleIndex=triangleIndex; hit.T=distance; hit.Barycentrics=barycentrics; hit.FrontFace=front;
            }
            return found;
        }
        bool TraceAny(GiRay ray) {
            RayDesc desc;
            desc.Origin=ray.Origin; desc.Direction=ray.Direction; desc.TMin=ray.TMin; desc.TMax=ray.TMax;
            RayQuery<RAY_FLAG_FORCE_NON_OPAQUE> query;
            query.TraceRayInline(GiHardwareScene,RAY_FLAG_NONE,0xff,desc);
            while(query.Proceed()) {
                if(query.CandidateType()!=CANDIDATE_NON_OPAQUE_TRIANGLE) continue;
                if(AcceptCandidate(query.CandidatePrimitiveIndex(),query.CandidateTriangleBarycentrics(),ray.Direction))
                    return true;
            }
            return false;
        }
        """;

    internal static D3D12RayGiCapabilities Inspect(D3D12GpuRuntime runtime, bool compilerAvailable)
    {
        int rawTier = 0;
        int shaderModel = 0;
        bool device5 = false;
        bool list4 = false;
        try
        {
            var options = new FeatureDataD3D12Options5();
            if (!runtime.Device.CheckFeatureSupport(D3D12Feature.Options5, ref options))
                throw new InvalidOperationException("D3D12 Options5 query was rejected.");
            rawTier = unchecked((int)options.RaytracingTier);
            foreach (var candidate in new[]
            {
                (ShaderModel.Model6_8, 68), (ShaderModel.Model6_7, 67), (ShaderModel.Model6_6, 66),
                (ShaderModel.Model6_5, 65), (ShaderModel.Model6_4, 64), (ShaderModel.Model6_3, 63),
                (ShaderModel.Model6_2, 62), (ShaderModel.Model6_1, 61), (ShaderModel.Model6_0, 60),
            })
            {
                var model = new FeatureDataShaderModel { HighestShaderModel = candidate.Item1 };
                if (runtime.Device.CheckFeatureSupport(D3D12Feature.ShaderModel, ref model))
                {
                    shaderModel = Math.Min(candidate.Item2, ShaderModelNumber(model.HighestShaderModel));
                    break;
                }
            }
            using var device = runtime.Device.QueryInterfaceOrNull<ID3D12Device5>();
            device5 = device is not null;
            using var allocator = runtime.Device.CreateCommandAllocator(CommandListType.Direct);
            using var probeList = runtime.Device.CreateCommandList<ID3D12GraphicsCommandList>(
                CommandListType.Direct, allocator, null);
            using var list = probeList.QueryInterfaceOrNull<ID3D12GraphicsCommandList4>();
            list4 = list is not null;
            probeList.Close();
        }
        catch (Exception exception)
        {
            return new(runtime.IsWarp, true, 0, 0, false, false, compilerAvailable, true, false,
                $"Ray-tracing capability query failed: {exception}");
        }
        return new(runtime.IsWarp, true, shaderModel, rawTier, device5, list4, compilerAvailable,
            true, device5 && list4 && rawTier >= 11 && shaderModel >= 65, null);
    }

    private static int ShaderModelNumber(ShaderModel model) => model switch
    {
        ShaderModel.Model5_1 => 51,
        ShaderModel.Model6_0 => 60,
        ShaderModel.Model6_1 => 61,
        ShaderModel.Model6_2 => 62,
        ShaderModel.Model6_3 => 63,
        ShaderModel.Model6_4 => 64,
        ShaderModel.Model6_5 => 65,
        ShaderModel.Model6_6 => 66,
        ShaderModel.Model6_7 => 67,
        ShaderModel.Model6_8 => 68,
        _ => (int)model >= 0 && (int)model <= 8 ? 50 + (int)model : 0,
    };

    internal static AccelerationStructure GetAccelerationStructure(
        D3D12RecordingContext context,
        D3D12GpuCache cache,
        RayGiScene scene, bool dynamicGeometry, bool forceRebuild = false,
        ID3D12Resource? triangleBuffer = null, DxcShaderCompiler? compiler = null, RayGiTriangleGpu[]? scratchRows = null)
    {
        object identity = forceRebuild ? new object() : scene.GeometryIdentity;
        return cache.GetReplaceable<AccelerationStructure>("hardware-raygi", identity,
            previous => Build(context, cache, scene, dynamicGeometry, forceRebuild ? null : previous, triangleBuffer, compiler, scratchRows));
    }

    internal static RaytracingAccelerationStructureBuildFlags BuildFlags(bool dynamicGeometry)
        => RaytracingAccelerationStructureBuildFlags.PreferFastTrace
            | (dynamicGeometry ? RaytracingAccelerationStructureBuildFlags.AllowUpdate : 0);

    // Mirrors the single flattened geometry/instance layout below, without creating GPU resources.
    // Preflight must query AllowUpdate and reserve max(build scratch, update scratch), like Build.
    internal static (ulong Bottom, ulong Top, ulong BottomScratch, ulong TopScratch) PrebuildResources(
        ID3D12Device device, int triangleCount, bool dynamicGeometry)
    {
        using var device5 = device.QueryInterface<ID3D12Device5>();
        var flags = BuildFlags(dynamicGeometry);
        var bottom = device5.GetRaytracingAccelerationStructurePrebuildInfo(new BuildRaytracingAccelerationStructureInputs
        {
            Type = RaytracingAccelerationStructureType.BottomLevel, Flags = flags,
            DescriptorsCount = 1, Layout = ElementsLayout.Array,
            GeometryDescriptions = [new RaytracingGeometryDescription
            {
                Type = RaytracingGeometryType.Triangles, Flags = RaytracingGeometryFlags.Opaque,
                Triangles = new RaytracingGeometryTrianglesDescription
                {
                    VertexFormat = Format.R32G32B32_Float, VertexCount = checked(triangleCount * 3),
                    VertexBuffer = new(0, 12), IndexFormat = Format.Unknown,
                },
            }],
        });
        var top = device5.GetRaytracingAccelerationStructurePrebuildInfo(new BuildRaytracingAccelerationStructureInputs
        {
            Type = RaytracingAccelerationStructureType.TopLevel, Flags = flags,
            DescriptorsCount = 1, Layout = ElementsLayout.Array,
        });
        if (bottom.ResultDataMaxSizeInBytes == 0 || top.ResultDataMaxSizeInBytes == 0)
            throw new NotSupportedException("The device could not estimate DXR acceleration-structure storage.");
        return (bottom.ResultDataMaxSizeInBytes, top.ResultDataMaxSizeInBytes,
            Math.Max(bottom.ScratchDataSizeInBytes, bottom.UpdateScratchDataSizeInBytes),
            Math.Max(top.ScratchDataSizeInBytes, top.UpdateScratchDataSizeInBytes));
    }

    internal static float[] PackPositions(RayGiScene scene, int maxDegree)
    {
        int triangleCount = scene.Triangles.Length;
        float[] positions = new float[checked(triangleCount * 9)];
        void CopyRange(int start, int end)
        {
            var triangles = scene.Triangles;
            for (int i = start, p = start * 9; i < end; i++)
            {
                var triangle = triangles[i];
                positions[p++] = triangle.P0.X; positions[p++] = triangle.P0.Y; positions[p++] = triangle.P0.Z;
                positions[p++] = triangle.P1.X; positions[p++] = triangle.P1.Y; positions[p++] = triangle.P1.Z;
                positions[p++] = triangle.P2.X; positions[p++] = triangle.P2.Y; positions[p++] = triangle.P2.Z;
            }
        }
        int degree = Math.Clamp(maxDegree, 1, 16);
        if (triangleCount < 32768 || degree == 1) CopyRange(0, triangleCount);
        else
        {
            const int blockSize = 4096;
            // Immutable input and disjoint output blocks keep the flattened primitive order.
            Parallel.For(0, (triangleCount + blockSize - 1) / blockSize,
                new ParallelOptions { MaxDegreeOfParallelism = degree, TaskScheduler = TaskScheduler.Default }, block =>
                {
                    int start = block * blockSize;
                    CopyRange(start, Math.Min(start + blockSize, triangleCount));
                });
        }
        return positions;
    }

    internal static int PositionByteCount(int triangleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(triangleCount);
        return checked(triangleCount * 9 * sizeof(float));
    }

    internal static void WritePositions(RayGiScene scene, int maxDegree, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(scene);
        WritePositionsCore(scene, null, maxDegree, destination);
    }

    private static unsafe void WritePositionsCore(RayGiScene scene, RayGiTriangleGpu[]? scratchRows, int maxDegree, Span<byte> destination)
    {
        int triangleCount = scene.TriangleCount;
        if (destination.Length != PositionByteCount(triangleCount))
            throw new ArgumentException("Position output must match the exact triangle payload length.", nameof(destination));
        if (triangleCount == 0) return;
        int degree = Math.Clamp(maxDegree, 1, 16);
        // The caller owns this mapped range until every worker has joined. Capture only its
        // address, never a Span or fixed local, and give each worker a disjoint output block.
        fixed (byte* pointer = destination)
        {
            nint address = (nint)pointer;
            void CopyRange(int start, int end)
            {
                var triangles = scratchRows is null ? scene.Triangles : scratchRows.AsSpan();
                var positions = new Span<float>((void*)(address + checked(start * 9 * sizeof(float))), checked((end - start) * 9));
                for (int i = start, p = 0; i < end; i++)
                {
                    ref readonly var triangle = ref triangles[i];
                    positions[p++] = triangle.P0.X; positions[p++] = triangle.P0.Y; positions[p++] = triangle.P0.Z;
                    positions[p++] = triangle.P1.X; positions[p++] = triangle.P1.Y; positions[p++] = triangle.P1.Z;
                    positions[p++] = triangle.P2.X; positions[p++] = triangle.P2.Y; positions[p++] = triangle.P2.Z;
                }
            }
            if (triangleCount < 32768 || degree == 1) CopyRange(0, triangleCount);
            else
            {
                const int blockSize = 4096;
                Parallel.For(0, (triangleCount + blockSize - 1) / blockSize,
                    new ParallelOptions { MaxDegreeOfParallelism = degree, TaskScheduler = TaskScheduler.Default }, block =>
                    {
                        int start = block * blockSize;
                        CopyRange(start, Math.Min(start + blockSize, triangleCount));
                    });
            }
        }
    }

    private static Vector3[] SampleGeometry(ReadOnlySpan<RayGiTriangleGpu> triangles)
    {
        const int blockSize = 4096;
        var samples = new Vector3[((triangles.Length + blockSize - 1) / blockSize) * 9];
        int p = 0;
        for (int start = 0; start < triangles.Length; start += blockSize)
        {
            int end = Math.Min(start + blockSize, triangles.Length) - 1;
            Copy(in triangles[start]); Copy(in triangles[start + (end - start) / 2]); Copy(in triangles[end]);
        }
        void Copy(in RayGiTriangleGpu triangle)
        {
            samples[p++] = new(triangle.P0.X, triangle.P0.Y, triangle.P0.Z);
            samples[p++] = new(triangle.P1.X, triangle.P1.Y, triangle.P1.Z);
            samples[p++] = new(triangle.P2.X, triangle.P2.Y, triangle.P2.Z);
        }
        return samples;
    }

    internal static bool RequiresRebuild(ReadOnlySpan<Vector3> built, ReadOnlySpan<Vector3> current, int updates)
    {
        // Refit retains the original tree topology. Bound its age even when sparse samples miss motion.
        if (updates >= 32 || built.Length != current.Length) return true;
        for (int start = 0; start < built.Length; start += 9)
        {
            var min = built[start];
            var max = min;
            int end = Math.Min(start + 9, built.Length);
            for (int i = start + 1; i < end; i++)
            {
                min = Vector3.Min(min, built[i]);
                max = Vector3.Max(max, built[i]);
            }
            double x = (double)max.X - min.X, y = (double)max.Y - min.Y, z = (double)max.Z - min.Z;
            double limitSquared = Math.Max(1e-12, (x * x + y * y + z * z) / 16);
            for (int i = start; i < end; i++)
            {
                x = (double)current[i].X - built[i].X;
                y = (double)current[i].Y - built[i].Y;
                z = (double)current[i].Z - built[i].Z;
                if (x * x + y * y + z * z > limitSquared) return true;
            }
        }
        return false;
    }

    private static AccelerationStructure Build(D3D12RecordingContext context, D3D12GpuCache cache, RayGiScene scene,
        bool dynamicGeometry, AccelerationStructure? previous, ID3D12Resource? triangleBuffer, DxcShaderCompiler? compiler,
        RayGiTriangleGpu[]? scratchRows)
    {
        var device = context.Device;
        using var device5 = device.QueryInterface<ID3D12Device5>();
        using var commands4 = context.Commands.QueryInterface<ID3D12GraphicsCommandList4>();

        int triangleCount = scene.TriangleCount;
        if (triangleCount == 0) throw new InvalidOperationException("Hardware ray tracing requires at least one triangle.");
        // The current descriptor is always one flattened, nonindexed R32G32B32 triangle array.
        // Indexed/multidescriptor layouts would need a stronger compatibility key.
        var samples = SampleGeometry(scratchRows is null ? scene.Triangles : scratchRows);
        bool update = dynamicGeometry && previous is { UpdateCapable: true } && previous.TriangleCount == triangleCount
            && !RequiresRebuild(previous.BuildSamples, samples, previous.UpdatesSinceBuild);
        var flags = BuildFlags(dynamicGeometry);
        int positionBytes = PositionByteCount(triangleCount);
        var vertexBuffer = TryGatherPositions(context, cache, triangleBuffer, compiler, triangleCount, positionBytes)
            ?? cache.FrameBuffer(positionBytes, ResourceStates.NonPixelShaderResource,
                destination => WritePositionsCore(scene, scratchRows, Environment.ProcessorCount, destination));

        var geometry = new RaytracingGeometryDescription
        {
            Type = RaytracingGeometryType.Triangles,
            Flags = RaytracingGeometryFlags.Opaque,
            Triangles = new RaytracingGeometryTrianglesDescription
            {
                VertexFormat = Format.R32G32B32_Float,
                VertexCount = checked(triangleCount * 3),
                VertexBuffer = new(vertexBuffer.GPUVirtualAddress, 12),
                IndexFormat = Format.Unknown,
            },
        };
        var bottomInputs = new BuildRaytracingAccelerationStructureInputs
        {
            Type = RaytracingAccelerationStructureType.BottomLevel,
            Flags = flags,
            DescriptorsCount = 1,
            Layout = ElementsLayout.Array,
            GeometryDescriptions = [geometry],
        };
        var bottomInfo = device5.GetRaytracingAccelerationStructurePrebuildInfo(bottomInputs);
        if (bottomInfo.ResultDataMaxSizeInBytes == 0)
            throw new InvalidOperationException("The device returned no bottom-level acceleration-structure storage.");
        using var construction = new D3D12ConstructionScope();
        var bottom = construction.Own(device.CreateCommittedResource(HeapType.Default,
            ResourceDescription.Buffer(bottomInfo.ResultDataMaxSizeInBytes, ResourceFlags.AllowUnorderedAccess),
            ResourceStates.RaytracingAccelerationStructure));
            var bottomScratch = context.Own(device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Buffer(Math.Max(bottomInfo.ScratchDataSizeInBytes, bottomInfo.UpdateScratchDataSizeInBytes), ResourceFlags.AllowUnorderedAccess),
                ResourceStates.UnorderedAccess));
            if (update) bottomInputs.Flags |= RaytracingAccelerationStructureBuildFlags.PerformUpdate;
            commands4.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription
            {
                Inputs = bottomInputs,
                SourceAccelerationStructureData = update ? previous!.BottomLevel.GPUVirtualAddress : 0,
                DestinationAccelerationStructureData = bottom.GPUVirtualAddress,
                ScratchAccelerationStructureData = bottomScratch.GPUVirtualAddress,
            });
            context.Commands.ResourceBarrierUnorderedAccessView(bottom);

            var instance = new RaytracingInstanceDescription
            {
                Transform = new Matrix3x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0),
                InstanceID = (Vortice.UInt24)0,
                InstanceMask = 255,
                InstanceContributionToHitGroupIndex = (Vortice.UInt24)0,
                Flags = RaytracingInstanceFlags.TriangleCullDisable,
                AccelerationStructure = bottom.GPUVirtualAddress,
            };
            int instanceSize = Marshal.SizeOf<RaytracingInstanceDescription>();
            var instanceUpload = context.Own(device.CreateCommittedResource(HeapType.Upload,
                ResourceDescription.Buffer((ulong)instanceSize), ResourceStates.GenericRead));
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref instance, 1))
                .CopyTo(instanceUpload.Map<byte>(0, instanceSize));
            instanceUpload.Unmap(0);
            var topInputs = new BuildRaytracingAccelerationStructureInputs
            {
                Type = RaytracingAccelerationStructureType.TopLevel,
                Flags = flags,
                DescriptorsCount = 1,
                Layout = ElementsLayout.Array,
                InstanceDescriptions = instanceUpload.GPUVirtualAddress,
            };
            var topInfo = device5.GetRaytracingAccelerationStructurePrebuildInfo(topInputs);
            if (topInfo.ResultDataMaxSizeInBytes == 0)
                throw new InvalidOperationException("The device returned no top-level acceleration-structure storage.");
            var top = construction.Own(device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Buffer(topInfo.ResultDataMaxSizeInBytes, ResourceFlags.AllowUnorderedAccess),
                ResourceStates.RaytracingAccelerationStructure));
            var topScratch = context.Own(device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Buffer(Math.Max(topInfo.ScratchDataSizeInBytes, topInfo.UpdateScratchDataSizeInBytes), ResourceFlags.AllowUnorderedAccess),
                ResourceStates.UnorderedAccess));
            if (update) topInputs.Flags |= RaytracingAccelerationStructureBuildFlags.PerformUpdate;
            commands4.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription
            {
                Inputs = topInputs,
                SourceAccelerationStructureData = update ? previous!.TopLevel.GPUVirtualAddress : 0,
                DestinationAccelerationStructureData = top.GPUVirtualAddress,
                ScratchAccelerationStructureData = topScratch.GPUVirtualAddress,
            });
            context.Commands.ResourceBarrierUnorderedAccessView(top);
            var options = new FeatureDataD3D12Options5();
            device5.CheckFeatureSupport(D3D12Feature.Options5, ref options);
            var result = new AccelerationStructure(bottom, top, $"DXR-raw-{unchecked((int)options.RaytracingTier)}", context.Runtime.FrameId, triangleCount, dynamicGeometry, update,
                update ? previous!.BuildSamples : samples, update ? previous!.UpdatesSinceBuild + 1 : 0);
            construction.Complete();
            return result;
    }

    private static ID3D12Resource? TryGatherPositions(D3D12RecordingContext context, D3D12GpuCache cache,
        ID3D12Resource? triangleBuffer, DxcShaderCompiler? compiler, int triangleCount, int positionBytes)
    {
        if (triangleBuffer is null || compiler is null || !PositionGatherPipeline.LayoutMatches)
            return null;

        const int maxDispatchGroups = 65535;
        int groups = (triangleCount + PositionGatherPipeline.ThreadsPerGroup - 1) / PositionGatherPipeline.ThreadsPerGroup;
        if (groups > maxDispatchGroups)
            return null;
        var source = triangleBuffer.Description;
        if (source.Dimension != ResourceDimension.Buffer || source.Width != (ulong)triangleCount * PositionGatherPipeline.TriangleStride
            || (source.Flags & ResourceFlags.DenyShaderResource) != 0)
            return null;

        var pipeline = cache.Get("hardware-raygi-position-gather", () => PositionGatherPipeline.Create(context, compiler));
        if (pipeline.State is null)
            return null;

        var positions = context.Own(context.Device.CreateCommittedResource(HeapType.Default,
            ResourceDescription.Buffer((ulong)positionBytes, ResourceFlags.AllowUnorderedAccess),
            ResourceStates.UnorderedAccess));
        var commands = context.Commands;
        commands.SetComputeRootSignature(pipeline.Root!);
        commands.SetPipelineState(pipeline.State);
        commands.SetComputeRoot32BitConstant(0, triangleCount, 0);
        commands.SetComputeRootShaderResourceView(1, triangleBuffer.GPUVirtualAddress);
        commands.SetComputeRootUnorderedAccessView(2, positions.GPUVirtualAddress);
        commands.Dispatch(groups, 1, 1);
        commands.ResourceBarrierTransition(positions, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        return positions;
    }

    private sealed class PositionGatherPipeline(ID3D12RootSignature? root, ID3D12PipelineState? state) : IDisposable
    {
        internal const int ThreadsPerGroup = 64;
        internal const int TriangleStride = 160;
        internal static readonly bool LayoutMatches =
            Unsafe.SizeOf<RayGiTriangleGpu>() == TriangleStride
            && Marshal.OffsetOf<RayGiTriangleGpu>(nameof(RayGiTriangleGpu.P0)) == 0
            && Marshal.OffsetOf<RayGiTriangleGpu>(nameof(RayGiTriangleGpu.P1)) == 16
            && Marshal.OffsetOf<RayGiTriangleGpu>(nameof(RayGiTriangleGpu.P2)) == 32;

        // Copy integer words so DXR receives the same float bits and nonindexed primitive order.
        private static readonly string Source = $$"""
            cbuffer GatherConstants : register(b0) { uint TriangleCount; }
            ByteAddressBuffer Triangles : register(t0);
            RWByteAddressBuffer Positions : register(u0);
            [numthreads({{ThreadsPerGroup}}, 1, 1)]
            void CSMain(uint3 id : SV_DispatchThreadID)
            {
                if (id.x >= TriangleCount) return;
                uint source = id.x * {{TriangleStride}};
                uint target = id.x * 36;
                Positions.Store3(target,      Triangles.Load3(source));
                Positions.Store3(target + 12, Triangles.Load3(source + 16));
                Positions.Store3(target + 24, Triangles.Load3(source + 32));
            }
            """;

        internal ID3D12RootSignature? Root { get; } = root;
        internal ID3D12PipelineState? State { get; } = state;

        internal static PositionGatherPipeline Create(D3D12RecordingContext context, DxcShaderCompiler compiler)
        {
            try
            {
                var shader = compiler.CompileCompute(Source, "CSMain", "cs_6_0");
                RootParameter[] parameters =
                [
                    new(new RootConstants(0, 0, 1), ShaderVisibility.All),
                    new(RootParameterType.ShaderResourceView, new RootDescriptor(0, 0), ShaderVisibility.All),
                    new(RootParameterType.UnorderedAccessView, new RootDescriptor(0, 0), ShaderVisibility.All),
                ];
                using var construction = new D3D12ConstructionScope();
                var signature = construction.Own(context.Device.CreateRootSignature<ID3D12RootSignature>(
                    new RootSignatureDescription(RootSignatureFlags.None, parameters), RootSignatureVersion.Version10));
                var pipeline = construction.Own(context.Device.CreateComputePipelineState(
                    new ComputePipelineStateDescription { RootSignature = signature, ComputeShader = shader.Dxil }));
                var result = new PositionGatherPipeline(signature, pipeline);
                construction.Complete();
                context.Runtime.ShaderCompilations++;
                context.Runtime.PipelineCreations++;
                return result;
            }
            catch (DxcShaderCompilationException)
            {
                return new(null, null);
            }
            catch (SharpGenException exception) when (exception.ResultCode.Code == Result.InvalidArg.Code
                || exception.ResultCode.Code == Vortice.DXGI.ResultCode.Unsupported.Code)
            {
                return new(null, null);
            }
        }

        public void Dispose()
        {
            State?.Dispose();
            Root?.Dispose();
        }
    }
}
