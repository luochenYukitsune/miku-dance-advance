using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D12;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Recorded Clustered Forward 3D frustum grid and compute light-culling statistics.</summary>
public sealed record D3D12ClusteredLightStatistics(
    bool Active,
    int GridX,
    int GridY,
    int GridZ,
    int TotalClusters,
    int ActiveLightCount,
    int MaxLightsPerCluster,
    int DispatchCount);

/// <summary>Rewrites forward NativeDirect light iteration to look up the fragment's 3D frustum cluster
/// light list in space3 while keeping Forward shaders untouched.</summary>
internal static class OriginalClusteredLightShader
{
    internal const string HlslHeader = """
        cbuffer ClusteredLightConstants : register(b0, space3) {
            row_major float4x4 ClusterView;
            float4 ClusterParams;
            float4 ClusterViewport;
            uint4 ClusterGridDims;
        };
        StructuredBuffer<uint2> ClusterLightGrid : register(t0, space3);
        StructuredBuffer<uint> ClusterLightIndices : register(t1, space3);
        uint ComputeClusterIndex(float2 screenPos, float3 worldPos) {
            uint cx = min((uint)max(floor(screenPos.x * ClusterViewport.x * ClusterGridDims.x), 0.0), ClusterGridDims.x - 1u);
            uint cy = min((uint)max(floor(screenPos.y * ClusterViewport.y * ClusterGridDims.y), 0.0), ClusterGridDims.y - 1u);
            float viewZ = mul(float4(worldPos, 1.0), ClusterView).z;
            float depth = viewZ * ClusterViewport.z;
            float sliceF;
            if (ClusterParams.w > 0.5) {
                sliceF = saturate((depth - ClusterParams.x) / max(ClusterParams.y - ClusterParams.x, 1e-30)) * ClusterGridDims.z;
            } else {
                sliceF = log(max(depth, ClusterParams.x) / ClusterParams.x) / max(ClusterParams.z, 1e-30) * ClusterGridDims.z;
            }
            uint cz = min((uint)max(floor(sliceF), 0.0), ClusterGridDims.z - 1u);
            return (cz * ClusterGridDims.y + cy) * ClusterGridDims.x + cx;
        }
        """;

    private const string CompactLoopAnchor = "[loop] for(uint i=0;i<(uint)NativeInfo.x;i++) {";
    private const string SpacedLoopAnchor = "[loop] for (uint i = 0; i < (uint)NativeInfo.x; i++) {";

    internal const string ClusteredLoopReplacement = """
        uint mdaClusterIndex = ComputeClusterIndex(v.Position.xy, v.WorldPosition);
        uint2 mdaClusterRange = ClusterLightGrid[mdaClusterIndex];
        [loop] for (uint mdaClusterSlot = 0; mdaClusterSlot < mdaClusterRange.y; mdaClusterSlot++) {
            uint i = ClusterLightIndices[mdaClusterRange.x + mdaClusterSlot];
        """;

    internal static string Apply(string source)
    {
        int insertAt = source.IndexOf("float3 NativeDirect(", StringComparison.Ordinal);
        if (insertAt < 0)
            throw new InvalidOperationException("Clustered light shader NativeDirect anchor missing.");

        string anchor;
        int loopAt = source.IndexOf(CompactLoopAnchor, insertAt, StringComparison.Ordinal);
        if (loopAt >= 0)
        {
            anchor = CompactLoopAnchor;
        }
        else
        {
            loopAt = source.IndexOf(SpacedLoopAnchor, insertAt, StringComparison.Ordinal);
            if (loopAt < 0)
                throw new InvalidOperationException("Clustered light shader loop anchor missing.");
            anchor = SpacedLoopAnchor;
        }

        var sb = new System.Text.StringBuilder(source.Length + HlslHeader.Length + ClusteredLoopReplacement.Length + 1);
        sb.Append(source, 0, insertAt);
        sb.Append(HlslHeader);
        sb.Append('\n');
        sb.Append(source, insertAt, loopAt - insertAt);
        sb.Append(ClusteredLoopReplacement);
        sb.Append(source, loopAt + anchor.Length, source.Length - (loopAt + anchor.Length));
        return sb.ToString();
    }
}

/// <summary>GPU compute pass that builds 3D frustum cluster AABBs (exponential depth slices for perspective,
/// uniform slices for orthographic) and culls native scene lights into per-cluster offset/count + index buffers.</summary>
internal sealed class D3D12ClusteredLightPass(D3D12GpuRuntime runtime, D3D12GpuCache cache)
{
    private (byte[] Build, byte[] Cull)? shaders;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct BuildConstants(
        Matrix4x4 InverseProjection,
        Vector4 NearFarLogOrtho,
        RayGiUInt4 GridAndSign);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CullConstants(
        Matrix4x4 View,
        RayGiUInt4 GridAndCount,
        RayGiUInt4 Limits);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct PixelConstants(
        Matrix4x4 ClusterView,
        Vector4 ClusterParams,
        Vector4 ClusterViewport,
        RayGiUInt4 ClusterGridDims);

    internal sealed record PreparedClusteredLights(
        ulong PixelConstantsAddress,
        ID3D12Resource LightGrid,
        ID3D12Resource LightIndices,
        D3D12ClusteredLightStatistics Statistics);

    internal PreparedClusteredLights Record(
        D3D12RecordingContext context,
        SceneCamera camera,
        int width,
        int height,
        ulong nativeFrameConstantsAddress,
        int lightCount,
        ClusterGridDimensions? gridOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(camera);
        if (!ReferenceEquals(context.Runtime, runtime) || !runtime.IsRecording)
            throw new InvalidOperationException("Clustered light pass requires this runtime's active recording frame.");
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));

        var grid = gridOverride ?? ClusterGridDimensions.Default;
        grid.Validate();
        int totalClusters = grid.TotalClusters;

        var view = ClusteredLightingMath.CreateViewMatrix(camera);
        var projection = ClusteredLightingMath.CreateProjectionMatrix(camera, width, height);
        if (!Matrix4x4.Invert(projection, out var inverseProjection))
            throw new InvalidOperationException("Camera projection matrix must be invertible for clustered lighting.");

        bool orthographic = camera.VerticalFovDegrees is null;
        float near = camera.Near;
        float far = camera.Far;
        float logRatio = MathF.Log(far / near);

        var pipeline = GetPipelines();
        ulong aabbBytes = checked((ulong)totalClusters * 32UL);
        ulong gridBytes = checked((ulong)totalClusters * 8UL);
        ulong indexBytes = checked((ulong)grid.TotalIndexCapacity * 4UL);

        var aabbBuffer = runtime.Transients.Resource(context, HeapType.Default,
            ResourceDescription.Buffer(aabbBytes, ResourceFlags.AllowUnorderedAccess), ResourceStates.UnorderedAccess);
        var lightGridBuffer = runtime.Transients.Resource(context, HeapType.Default,
            ResourceDescription.Buffer(gridBytes, ResourceFlags.AllowUnorderedAccess), ResourceStates.UnorderedAccess);
        var lightIndexBuffer = runtime.Transients.Resource(context, HeapType.Default,
            ResourceDescription.Buffer(indexBytes, ResourceFlags.AllowUnorderedAccess), ResourceStates.UnorderedAccess);

        var commands = context.Commands;

        // Pass 1: Build 3D frustum cluster AABBs in view space.
        var buildCb = new BuildConstants(
            inverseProjection,
            new Vector4(near, far, logRatio, orthographic ? 1f : 0f),
            new RayGiUInt4((uint)grid.X, (uint)grid.Y, (uint)grid.Z, camera.LeftHanded ? 1u : 0u));

        commands.SetComputeRootSignature(pipeline.BuildRoot);
        commands.SetPipelineState(pipeline.BuildState);
        commands.SetComputeRootConstantBufferView(0, context.UploadConstants(buildCb));
        commands.SetComputeRootUnorderedAccessView(1, aabbBuffer.GPUVirtualAddress);
        commands.Dispatch((grid.X + 3) / 4, (grid.Y + 3) / 4, (grid.Z + 3) / 4);
        commands.ResourceBarrierTransition(aabbBuffer, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);

        // Pass 2: Cull all active native lights against each cluster AABB.
        var cullCb = new CullConstants(
            view,
            new RayGiUInt4((uint)grid.X, (uint)grid.Y, (uint)grid.Z, (uint)Math.Max(0, lightCount)),
            new RayGiUInt4((uint)grid.MaxLightsPerCluster, (uint)totalClusters, 0u, 0u));

        commands.SetComputeRootSignature(pipeline.CullRoot);
        commands.SetPipelineState(pipeline.CullState);
        commands.SetComputeRootConstantBufferView(0, context.UploadConstants(cullCb));
        commands.SetComputeRootConstantBufferView(1, nativeFrameConstantsAddress);
        commands.SetComputeRootShaderResourceView(2, aabbBuffer.GPUVirtualAddress);
        commands.SetComputeRootUnorderedAccessView(3, lightGridBuffer.GPUVirtualAddress);
        commands.SetComputeRootUnorderedAccessView(4, lightIndexBuffer.GPUVirtualAddress);
        commands.Dispatch((totalClusters + 63) / 64, 1, 1);

        commands.ResourceBarrierTransition(lightGridBuffer, ResourceStates.UnorderedAccess, ResourceStates.PixelShaderResource);
        commands.ResourceBarrierTransition(lightIndexBuffer, ResourceStates.UnorderedAccess, ResourceStates.PixelShaderResource);

        var pixelCb = new PixelConstants(
            view,
            new Vector4(near, far, logRatio, orthographic ? 1f : 0f),
            new Vector4(1f / width, 1f / height, camera.LeftHanded ? 1f : -1f, 0f),
            new RayGiUInt4((uint)grid.X, (uint)grid.Y, (uint)grid.Z, (uint)grid.MaxLightsPerCluster));
        ulong pixelCbAddr = context.UploadConstants(pixelCb);

        var stats = new D3D12ClusteredLightStatistics(
            Active: true,
            GridX: grid.X,
            GridY: grid.Y,
            GridZ: grid.Z,
            TotalClusters: totalClusters,
            ActiveLightCount: lightCount,
            MaxLightsPerCluster: grid.MaxLightsPerCluster,
            DispatchCount: 2);

        return new PreparedClusteredLights(pixelCbAddr, lightGridBuffer, lightIndexBuffer, stats);
    }

    private sealed record Pipelines(
        ID3D12RootSignature BuildRoot,
        ID3D12PipelineState BuildState,
        ID3D12RootSignature CullRoot,
        ID3D12PipelineState CullState);

    private Pipelines GetPipelines()
    {
        var compiled = GetShaders();
        var buildRoot = cache.Get("clustered:build-root", () => runtime.CreateRootSignature(
            new RootSignatureDescription(RootSignatureFlags.None,
            [
                new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
                new(RootParameterType.UnorderedAccessView, new RootDescriptor(0, 0), ShaderVisibility.All),
            ]), RootSignatureVersion.Version10));
        var buildState = cache.Get("clustered:build-pso", () =>
        {
            var pso = runtime.CreateComputePipelineState(new ComputePipelineStateDescription
            { RootSignature = buildRoot, ComputeShader = compiled.Build });
            runtime.PipelineCreations++;
            return pso;
        });
        var cullRoot = cache.Get("clustered:cull-root", () => runtime.CreateRootSignature(
            new RootSignatureDescription(RootSignatureFlags.None,
            [
                new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
                new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 1), ShaderVisibility.All),
                new(RootParameterType.ShaderResourceView, new RootDescriptor(0, 0), ShaderVisibility.All),
                new(RootParameterType.UnorderedAccessView, new RootDescriptor(0, 0), ShaderVisibility.All),
                new(RootParameterType.UnorderedAccessView, new RootDescriptor(1, 0), ShaderVisibility.All),
            ]), RootSignatureVersion.Version10));
        var cullState = cache.Get("clustered:cull-pso", () =>
        {
            var pso = runtime.CreateComputePipelineState(new ComputePipelineStateDescription
            { RootSignature = cullRoot, ComputeShader = compiled.Cull });
            runtime.PipelineCreations++;
            return pso;
        });
        return new Pipelines(buildRoot, buildState, cullRoot, cullState);
    }

    internal static IEnumerable<D3D12FxcShaderRequest> GetPrewarmRequests()
    {
        const ShaderFlags flags = ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness;
        yield return new(BuildSource, "CSBuildClusters", "clustered-build.hlsl", "cs_5_1", flags);
        yield return new(CullSource, "CSCullLights", "clustered-cull.hlsl", "cs_5_1", flags);
    }

    private (byte[] Build, byte[] Cull) GetShaders()
    {
        if (shaders is { } cached) return cached;
        const ShaderFlags flags = ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness;
        var build = D3D12FxcShaderCache.Compile(BuildSource, "CSBuildClusters", "clustered-build.hlsl", "cs_5_1", flags);
        var cull = D3D12FxcShaderCache.Compile(CullSource, "CSCullLights", "clustered-cull.hlsl", "cs_5_1", flags);
        runtime.ShaderCompilations += 2;
        return (shaders = (build, cull)).Value;
    }

    internal const string BuildSource = """
        struct ClusterAabb {
            float4 Min;
            float4 Max;
        };
        cbuffer BuildConstants : register(b0) {
            row_major float4x4 InverseProjection;
            float4 NearFarLogOrtho;
            uint4 GridAndSign;
        };
        RWStructuredBuffer<ClusterAabb> ClusterAabbs : register(u0);

        float3 UnprojectNear(float nx, float ny) {
            float4 clip = mul(float4(nx, ny, 0.0, 1.0), InverseProjection);
            return clip.xyz / clip.w;
        }

        float3 ProjectToDepth(float3 nearPoint, float targetViewZ, bool orthographic) {
            if (orthographic) return float3(nearPoint.x, nearPoint.y, targetViewZ);
            float scale = abs(nearPoint.z) > 1e-30 ? targetViewZ / nearPoint.z : 1.0;
            return nearPoint * scale;
        }

        [numthreads(4, 4, 4)]
        void CSBuildClusters(uint3 id : SV_DispatchThreadID) {
            if (id.x >= GridAndSign.x || id.y >= GridAndSign.y || id.z >= GridAndSign.z) return;
            float ndcMinX = ((float)id.x / GridAndSign.x) * 2.0 - 1.0;
            float ndcMaxX = ((float)(id.x + 1u) / GridAndSign.x) * 2.0 - 1.0;
            float ndcMaxY = 1.0 - ((float)id.y / GridAndSign.y) * 2.0;
            float ndcMinY = 1.0 - ((float)(id.y + 1u) / GridAndSign.y) * 2.0;

            float nearD = NearFarLogOrtho.x;
            float farD = NearFarLogOrtho.y;
            bool ortho = NearFarLogOrtho.w > 0.5;
            float t0 = (float)id.z / GridAndSign.z;
            float t1 = (float)(id.z + 1u) / GridAndSign.z;
            float depthNear = ortho ? lerp(nearD, farD, t0) : nearD * pow(farD / nearD, t0);
            float depthFar = ortho ? lerp(nearD, farD, t1) : nearD * pow(farD / nearD, t1);

            float zSign = GridAndSign.w != 0u ? 1.0 : -1.0;
            float viewZNear = zSign * depthNear;
            float viewZFar = zSign * depthFar;

            float3 p00 = UnprojectNear(ndcMinX, ndcMinY);
            float3 p10 = UnprojectNear(ndcMaxX, ndcMinY);
            float3 p01 = UnprojectNear(ndcMinX, ndcMaxY);
            float3 p11 = UnprojectNear(ndcMaxX, ndcMaxY);

            float3 c0 = ProjectToDepth(p00, viewZNear, ortho);
            float3 c1 = ProjectToDepth(p10, viewZNear, ortho);
            float3 c2 = ProjectToDepth(p01, viewZNear, ortho);
            float3 c3 = ProjectToDepth(p11, viewZNear, ortho);
            float3 c4 = ProjectToDepth(p00, viewZFar, ortho);
            float3 c5 = ProjectToDepth(p10, viewZFar, ortho);
            float3 c6 = ProjectToDepth(p01, viewZFar, ortho);
            float3 c7 = ProjectToDepth(p11, viewZFar, ortho);

            float3 bMin = min(min(min(c0, c1), min(c2, c3)), min(min(c4, c5), min(c6, c7)));
            float3 bMax = max(max(max(c0, c1), max(c2, c3)), max(max(c4, c5), max(c6, c7)));

            uint index = (id.z * GridAndSign.y + id.y) * GridAndSign.x + id.x;
            ClusterAabb result;
            result.Min = float4(bMin, 0.0);
            result.Max = float4(bMax, 0.0);
            ClusterAabbs[index] = result;
        }
        """;

    internal const string CullSource = """
        struct ClusterAabb {
            float4 Min;
            float4 Max;
        };
        struct NativeLight {
            float4 PositionKind; float4 DirectionRange; float4 ColorIntensity; float4 ConeShadow; float4 AreaRightTile;
        };
        cbuffer CullConstants : register(b0) {
            row_major float4x4 View;
            uint4 GridAndCount;
            uint4 Limits;
        };
        cbuffer NativeSharedLightConstants : register(b0, space1) {
            float4 NativeAtlasLayout;
            NativeLight NativeLights[32];
            row_major float4x4 NativeShadowMatrices[768];
        };
        StructuredBuffer<ClusterAabb> ClusterAabbs : register(t0);
        RWStructuredBuffer<uint2> ClusterLightGrid : register(u0);
        RWStructuredBuffer<uint> ClusterLightIndices : register(u1);

        bool IntersectsSphereAabb(float3 center, float radius, float3 bMin, float3 bMax) {
            float3 closest = clamp(center, bMin, bMax);
            float3 delta = closest - center;
            return dot(delta, delta) <= radius * radius;
        }

        [numthreads(64, 1, 1)]
        void CSCullLights(uint3 id : SV_DispatchThreadID) {
            uint clusterIndex = id.x;
            if (clusterIndex >= Limits.y) return;

            ClusterAabb box = ClusterAabbs[clusterIndex];
            uint maxLights = Limits.x;
            uint offset = clusterIndex * maxLights;
            uint count = 0u;
            uint lightCount = min(GridAndCount.w, 32u);

            [loop] for (uint i = 0u; i < lightCount; i++) {
                NativeLight light = NativeLights[i];
                float kind = light.PositionKind.w;
                bool visible = false;
                if (kind == 0.0) {
                    visible = true;
                } else {
                    float3 viewPos = mul(float4(light.PositionKind.xyz, 1.0), View).xyz;
                    float range = light.DirectionRange.w;
                    if (kind == 1.0) {
                        visible = IntersectsSphereAabb(viewPos, range, box.Min.xyz, box.Max.xyz);
                    } else if (kind == 2.0) {
                        if (IntersectsSphereAabb(viewPos, range, box.Min.xyz, box.Max.xyz)) {
                            float3 viewDir = normalize(mul(float4(light.DirectionRange.xyz, 0.0), View).xyz);
                            float cosOuter = clamp(light.ConeShadow.y, 0.0, 1.0);
                            float sinOuter = sqrt(max(1.0 - cosOuter * cosOuter, 0.0));
                            float3 coneCenter;
                            float coneRadius;
                            if (cosOuter < 0.70710678) {
                                coneCenter = viewPos + viewDir * (range * cosOuter);
                                coneRadius = range * sinOuter;
                            } else {
                                coneRadius = range / max(2.0 * cosOuter, 1e-4);
                                coneCenter = viewPos + viewDir * coneRadius;
                            }
                            visible = IntersectsSphereAabb(coneCenter, coneRadius, box.Min.xyz, box.Max.xyz);
                        }
                    } else {
                        float halfW = light.ConeShadow.x;
                        float halfH = light.ConeShadow.y;
                        float areaExtent = sqrt(halfW * halfW + halfH * halfH) * 0.5773502691896258;
                        visible = IntersectsSphereAabb(viewPos, range + areaExtent, box.Min.xyz, box.Max.xyz);
                    }
                }
                if (visible && count < maxLights) {
                    ClusterLightIndices[offset + count] = i;
                    count++;
                }
            }
            ClusterLightGrid[clusterIndex] = uint2(offset, count);
        }
        """;
}
