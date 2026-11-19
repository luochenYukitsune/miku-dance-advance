using System.Runtime.InteropServices;
using System.Numerics;
using Mmd.Modern.Core;
using Vortice.Direct3D12;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Realtime preview GI acceleration pass: downsamples full-resolution G-buffer guides to
/// half resolution with representative valid surface points, and upsamples traced/filtered incident
/// radiance back to full resolution via depth- and normal-aware joint-bilateral filtering.</summary>
internal sealed class D3D12RayGiHalfResPass(D3D12GpuRuntime runtime) : IDisposable
{
    private ID3D12RootSignature? downsampleRoot, upsampleRoot;
    private ID3D12PipelineState? downsamplePipeline, upsamplePipeline;
    private int cachedSrvStride;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DownsampleConstants(RayGiUInt4 Dimensions);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct UpsampleConstants(RayGiUInt4 Dimensions, Vector4 ViewGuide, Vector4 CameraEyeOrtho);

    internal static (int Width, int Height) HalfDimensions(int width, int height)
        => ((width + 1) / 2, (height + 1) / 2);

    internal static (Vector4 ViewGuide, Vector4 CameraEyeOrtho) CameraGuides(SceneCamera camera, int fullHeight)
    {
        double dx = (double)camera.Target.X - camera.Eye.X;
        double dy = (double)camera.Target.Y - camera.Eye.Y;
        double dz = (double)camera.Target.Z - camera.Eye.Z;
        double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        double pixel = camera.VerticalFovDegrees is { } fov
            ? 2.0 * Math.Tan(fov * Math.PI / 360.0) / fullHeight
            : (double)camera.OrthographicHeight / fullHeight;
        var eyeOrtho = new Vector4(camera.Eye, camera.VerticalFovDegrees is null ? 0f : 1f);
        if (fullHeight <= 0 || !double.IsFinite(length) || length <= 0 || !double.IsFinite(pixel) || pixel <= 0 || pixel > float.MaxValue)
            return (default, eyeOrtho);
        var guide = new Vector4((float)(dx / length), (float)(dy / length), (float)(dz / length), (float)pixel);
        return (guide.W > 0 ? guide : default, eyeOrtho);
    }

    internal ID3D12DescriptorHeap CreateDescriptorHeap(D3D12GpuRuntime ownerRuntime,
        ID3D12Resource fullPosition, ID3D12Resource fullNormal,
        ID3D12Resource halfPosition, ID3D12Resource halfNormal, out int stride)
    {
        var device = ownerRuntime.Device;
        var heap = ownerRuntime.Transients.Heap(new(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            6, DescriptorHeapFlags.ShaderVisible));
        stride = GetSrvStride(device);
        WriteDescriptors(device, heap, stride, fullPosition, fullNormal, halfPosition, halfNormal);
        ownerRuntime.DescriptorHighWater = Math.Max(ownerRuntime.DescriptorHighWater, 6);
        return heap;
    }

    internal ID3D12DescriptorHeap AcquireHalfResDescriptorHeap(D3D12GpuRuntime ownerRuntime,
        D3D12RayGiWorkspace workspace, D3D12RayGiWorkspace.HalfResWorkspace halfWorkspace, out int stride)
    {
        if (!workspace.IsSlotReusable)
        {
            return CreateDescriptorHeap(ownerRuntime, workspace.Position.Resource, workspace.Normal.Resource,
                halfWorkspace.Position.Resource, halfWorkspace.Normal.Resource, out stride);
        }

        var device = ownerRuntime.Device;
        stride = GetSrvStride(device);
        ownerRuntime.DescriptorHighWater = Math.Max(ownerRuntime.DescriptorHighWater, 6);
        if (halfWorkspace.DescriptorHeap is { } cached)
            return cached;

        var heap = device.CreateDescriptorHeap(new(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            6, DescriptorHeapFlags.ShaderVisible));
        try
        {
            WriteDescriptors(device, heap, stride, workspace.Position.Resource, workspace.Normal.Resource,
                halfWorkspace.Position.Resource, halfWorkspace.Normal.Resource);
            halfWorkspace.AttachDescriptorHeap(heap);
            return heap;
        }
        catch
        {
            heap.Dispose();
            throw;
        }
    }

    private int GetSrvStride(ID3D12Device device)
    {
        if (ReferenceEquals(device, runtime.Device))
        {
            return cachedSrvStride != 0 ? cachedSrvStride : (cachedSrvStride = device.GetDescriptorHandleIncrementSize(
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView));
        }
        return device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
    }

    private static void WriteDescriptors(ID3D12Device device, ID3D12DescriptorHeap heap, int stride,
        ID3D12Resource fullPosition, ID3D12Resource fullNormal,
        ID3D12Resource halfPosition, ID3D12Resource halfNormal)
    {
        var cpuStart = heap.GetCPUDescriptorHandleForHeapStart();
        device.CreateShaderResourceView(fullPosition, null, CpuSlot(cpuStart, 0, stride));
        device.CreateShaderResourceView(fullNormal, null, CpuSlot(cpuStart, 1, stride));
        device.CreateShaderResourceView(halfPosition, null, CpuSlot(cpuStart, 2, stride));
        device.CreateShaderResourceView(halfNormal, null, CpuSlot(cpuStart, 3, stride));
        device.CreateUnorderedAccessView(halfPosition, null, null, CpuSlot(cpuStart, 4, stride));
        device.CreateUnorderedAccessView(halfNormal, null, null, CpuSlot(cpuStart, 5, stride));
    }

    internal void Downsample(D3D12RecordingContext context, ID3D12DescriptorHeap heap, int stride,
        D3D12GiWorkspaceResource fullPosition, D3D12GiWorkspaceResource fullNormal,
        D3D12RayGiWorkspace.HalfResWorkspace half, int fullWidth, int fullHeight,
        DxcShaderCompiler compiler)
    {
        EnsureDownsamplePipeline(compiler);
        var shaderRead = ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource;
        fullPosition.Transition(context, shaderRead);
        fullNormal.Transition(context, shaderRead);
        half.Position.Transition(context, ResourceStates.UnorderedAccess);
        half.Normal.Transition(context, ResourceStates.UnorderedAccess);

        var commands = context.Commands;
        var gpuStart = heap.GetGPUDescriptorHandleForHeapStart();
        commands.SetDescriptorHeaps(heap);
        commands.SetComputeRootSignature(downsampleRoot!);
        commands.SetPipelineState(downsamplePipeline!);
        commands.SetComputeRootConstantBufferView(0, context.UploadConstants(
            new DownsampleConstants(new((uint)fullWidth, (uint)fullHeight, (uint)half.Width, (uint)half.Height))));
        commands.SetComputeRootDescriptorTable(1, GpuSlot(gpuStart, 0, stride));
        commands.SetComputeRootDescriptorTable(2, GpuSlot(gpuStart, 4, stride));
        commands.Dispatch((half.Width + 7) / 8, (half.Height + 7) / 8, 1);

        commands.ResourceBarrierUnorderedAccessView(half.Position.Resource);
        commands.ResourceBarrierUnorderedAccessView(half.Normal.Resource);
        half.Position.Transition(context, shaderRead);
        half.Normal.Transition(context, shaderRead);
    }

    internal ID3D12Resource Upsample(D3D12RecordingContext context, ID3D12DescriptorHeap heap, int stride,
        D3D12GiWorkspaceResource halfSignal, D3D12GiWorkspaceResource fullOutput,
        SceneCamera camera, int fullWidth, int fullHeight, int halfWidth, int halfHeight,
        DxcShaderCompiler compiler)
    {
        EnsureUpsamplePipeline(compiler);
        halfSignal.Transition(context, ResourceStates.NonPixelShaderResource);
        fullOutput.Transition(context, ResourceStates.UnorderedAccess);

        var (viewGuide, cameraEyeOrtho) = CameraGuides(camera, fullHeight);
        var commands = context.Commands;
        var gpuStart = heap.GetGPUDescriptorHandleForHeapStart();
        commands.SetDescriptorHeaps(heap);
        commands.SetComputeRootSignature(upsampleRoot!);
        commands.SetPipelineState(upsamplePipeline!);
        commands.SetComputeRootConstantBufferView(0, context.UploadConstants(
            new UpsampleConstants(new((uint)fullWidth, (uint)fullHeight, (uint)halfWidth, (uint)halfHeight),
                viewGuide, cameraEyeOrtho)));
        commands.SetComputeRootDescriptorTable(1, GpuSlot(gpuStart, 0, stride));
        commands.SetComputeRootDescriptorTable(2, GpuSlot(gpuStart, 2, stride));
        commands.SetComputeRootShaderResourceView(3, halfSignal.Resource.GPUVirtualAddress);
        commands.SetComputeRootUnorderedAccessView(4, fullOutput.Resource.GPUVirtualAddress);
        commands.Dispatch((fullWidth + 7) / 8, (fullHeight + 7) / 8, 1);

        commands.ResourceBarrierUnorderedAccessView(fullOutput.Resource);
        fullOutput.Transition(context, ResourceStates.PixelShaderResource);
        return fullOutput.Resource;
    }

    private static CpuDescriptorHandle CpuSlot(CpuDescriptorHandle start, int slot, int stride)
    {
        var handle = start;
        handle.Offset(slot, stride);
        return handle;
    }

    private static GpuDescriptorHandle GpuSlot(GpuDescriptorHandle start, int slot, int stride)
    {
        var handle = start;
        handle.Offset(slot, stride);
        return handle;
    }

    internal static IEnumerable<DxcShaderWarmupRequest> GetPrewarmRequests() =>
    [
        new(DownsampleSource, "DownsampleCS", "cs_6_0"),
        new(UpsampleSource, "UpsampleCS", "cs_6_0")
    ];

    private void EnsureDownsamplePipeline(DxcShaderCompiler compiler)
    {
        if (downsamplePipeline is not null) return;
        var shader = compiler.CompileCompute(DownsampleSource, "DownsampleCS", "cs_6_0");
        RootParameter[] parameters =
        [
            new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 2, 0)), ShaderVisibility.All),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.UnorderedAccessView, 2, 0)), ShaderVisibility.All)
        ];
        var signature = runtime.CreateRootSignature(
            new RootSignatureDescription(RootSignatureFlags.None, parameters), RootSignatureVersion.Version10);
        try
        {
            var state = runtime.CreateComputePipelineState(
                new ComputePipelineStateDescription { RootSignature = signature, ComputeShader = shader.Dxil });
            downsampleRoot = signature;
            downsamplePipeline = state;
            runtime.ShaderCompilations++;
            runtime.PipelineCreations++;
        }
        catch
        {
            signature.Dispose();
            throw;
        }
    }

    private void EnsureUpsamplePipeline(DxcShaderCompiler compiler)
    {
        if (upsamplePipeline is not null) return;
        var shader = compiler.CompileCompute(UpsampleSource, "UpsampleCS", "cs_6_0");
        RootParameter[] parameters =
        [
            new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 2, 0)), ShaderVisibility.All),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 2, 2)), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor(4, 0), ShaderVisibility.All),
            new(RootParameterType.UnorderedAccessView, new RootDescriptor(0, 0), ShaderVisibility.All)
        ];
        var signature = runtime.CreateRootSignature(
            new RootSignatureDescription(RootSignatureFlags.None, parameters), RootSignatureVersion.Version10);
        try
        {
            var state = runtime.CreateComputePipelineState(
                new ComputePipelineStateDescription { RootSignature = signature, ComputeShader = shader.Dxil });
            upsampleRoot = signature;
            upsamplePipeline = state;
            runtime.ShaderCompilations++;
            runtime.PipelineCreations++;
        }
        catch
        {
            signature.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        downsamplePipeline?.Dispose();
        downsampleRoot?.Dispose();
        downsamplePipeline = null;
        downsampleRoot = null;
        upsamplePipeline?.Dispose();
        upsampleRoot?.Dispose();
        upsamplePipeline = null;
        upsampleRoot = null;
    }

    internal const string DownsampleSource = """
        cbuffer HalfResConstants : register(b0) { uint4 Dimensions; };
        Texture2D<float4> FullPosition : register(t0);
        Texture2D<float4> FullNormal : register(t1);
        RWTexture2D<float4> HalfPosition : register(u0);
        RWTexture2D<float4> HalfNormal : register(u1);

        [numthreads(8, 8, 1)]
        void DownsampleCS(uint3 thread : SV_DispatchThreadID) {
            if (thread.x >= Dimensions.z || thread.y >= Dimensions.w) return;
            int2 basePixel = int2(thread.xy) * 2;
            int2 maxPixel = int2(Dimensions.xy) - 1;
            float4 bestPos = 0;
            float4 bestNorm = 0;
            float bestDepth = 2.0;
            bool found = false;
            [unroll] for (int dy = 0; dy < 2; dy++) {
                [unroll] for (int dx = 0; dx < 2; dx++) {
                    int2 p = min(basePixel + int2(dx, dy), maxPixel);
                    float4 pos = FullPosition.Load(int3(p, 0));
                    float4 norm = FullNormal.Load(int3(p, 0));
                    if (all(isfinite(pos)) && all(isfinite(norm)) && norm.w >= 1.0 && pos.w >= 0.0 && pos.w <= 1.0) {
                        if (!found || pos.w < bestDepth) {
                            bestPos = pos;
                            bestNorm = norm;
                            bestDepth = pos.w;
                            found = true;
                        }
                    }
                }
            }
            HalfPosition[thread.xy] = bestPos;
            HalfNormal[thread.xy] = bestNorm;
        }
        """;

    internal const string UpsampleSource = """
        cbuffer UpsampleConstants : register(b0) {
            uint4 Dimensions;
            float4 ViewGuide;
            float4 CameraEyeOrtho;
        };
        Texture2D<float4> FullPosition : register(t0);
        Texture2D<float4> FullNormal : register(t1);
        Texture2D<float4> HalfPosition : register(t2);
        Texture2D<float4> HalfNormal : register(t3);
        StructuredBuffer<float4> HalfIndirect : register(t4);
        RWStructuredBuffer<float4> FullIndirect : register(u0);

        bool UnitVector(float3 value, out float3 unit) {
            unit = 0;
            if (!all(isfinite(value))) return false;
            float scale = max(abs(value.x), max(abs(value.y), abs(value.z)));
            if (!(scale > 0.0)) return false;
            value /= scale;
            float magnitude = length(value);
            if (!isfinite(magnitude) || !(magnitude > 0.0)) return false;
            unit = value / magnitude;
            return all(isfinite(unit));
        }

        float BlendFinite(float a, float b, float t) {
            precise float result = (a >= 0.0) == (b >= 0.0) ? a + (b - a) * t : a * (1.0 - t) + b * t;
            return clamp(result, min(a, b), max(a, b));
        }

        float4 BlendFinite4(float4 a, float4 b, float t) {
            return float4(
                BlendFinite(a.x, b.x, t),
                BlendFinite(a.y, b.y, t),
                BlendFinite(a.z, b.z, t),
                BlendFinite(a.w, b.w, t));
        }

        float ComputePixelWorldSize(float3 worldPos) {
            if (ViewGuide.w > 0.0 && all(isfinite(ViewGuide))) {
                float viewDepth = CameraEyeOrtho.w > 0.5
                    ? max(abs(dot(worldPos - CameraEyeOrtho.xyz, ViewGuide.xyz)), 1e-4)
                    : 1.0;
                float s = ViewGuide.w * viewDepth;
                if (isfinite(s) && s > 1e-4) return s;
            }
            return max(length(worldPos) * 0.002, 1e-4);
        }

        [numthreads(8, 8, 1)]
        void UpsampleCS(uint3 thread : SV_DispatchThreadID) {
            if (thread.x >= Dimensions.x || thread.y >= Dimensions.y) return;
            uint fullIndex = thread.y * Dimensions.x + thread.x;
            float4 fullPos = FullPosition.Load(int3(thread.xy, 0));
            float4 fullNorm = FullNormal.Load(int3(thread.xy, 0));
            float3 n0;
            if (!all(isfinite(fullPos)) || !all(isfinite(fullNorm)) || fullNorm.w < 1.0
                || fullPos.w < 0.0 || fullPos.w > 1.0 || !UnitVector(fullNorm.xyz, n0)) {
                FullIndirect[fullIndex] = 0;
                return;
            }

            float pixelWorldSize = ComputePixelWorldSize(fullPos.xyz);
            float2 halfCoord = (float2(thread.xy) + 0.5) * 0.5 - 0.5;
            int2 baseHalf = int2(floor(halfCoord));
            float2 fracHalf = halfCoord - float2(baseHalf);
            int2 maxHalf = int2(Dimensions.zw) - 1;

            float4 accum = 0;
            float totalWeight = 0;
            [unroll] for (int dy = 0; dy < 2; dy++) {
                [unroll] for (int dx = 0; dx < 2; dx++) {
                    int2 tap = clamp(baseHalf + int2(dx, dy), int2(0, 0), maxHalf);
                    float spatialW = (dx == 0 ? (1.0 - fracHalf.x) : fracHalf.x)
                                   * (dy == 0 ? (1.0 - fracHalf.y) : fracHalf.y);
                    if (!(spatialW > 0.0)) continue;
                    float4 halfPos = HalfPosition.Load(int3(tap, 0));
                    float4 halfNorm = HalfNormal.Load(int3(tap, 0));
                    float4 halfGi = HalfIndirect[(uint)tap.y * Dimensions.z + (uint)tap.x];
                    float3 n1;
                    if (!all(isfinite(halfPos)) || !all(isfinite(halfNorm)) || !all(isfinite(halfGi))
                        || halfNorm.w < 1.0 || any(halfGi.rgb < 0.0) || halfGi.a < 0.0
                        || !UnitVector(halfNorm.xyz, n1)) continue;
                    float ndot = saturate(dot(n0, n1));
                    if (ndot < 0.5) continue;
                    float wNormal = pow(ndot, 16.0);
                    float3 delta = halfPos.xyz - fullPos.xyz;
                    float planeErr = max(abs(dot(delta, n0)), abs(dot(delta, n1))) / pixelWorldSize;
                    float distErr = length(delta) / pixelWorldSize;
                    float wPlane = exp2(-(planeErr * planeErr) * 0.25);
                    float wDist = exp2(-(distErr * distErr) * 0.02);
                    float w = spatialW * wNormal * wPlane * wDist;
                    if (!isfinite(w) || !(w > 1e-8)) continue;
                    float next = totalWeight + w;
                    accum = totalWeight == 0.0 ? halfGi : BlendFinite4(accum, halfGi, w / next);
                    totalWeight = next;
                }
            }

            if (totalWeight > 1e-6 && all(isfinite(accum))) {
                FullIndirect[fullIndex] = float4(max(accum.rgb, 0.0), saturate(accum.a));
                return;
            }

            // Fallback across 3x3 half-res neighborhood for thin silhouettes and steep boundaries.
            int2 centerHalf = clamp(int2(thread.xy) / 2, int2(0, 0), maxHalf);
            float4 bestGi = float4(0, 0, 0, 1);
            float bestScore = 0.0;
            [unroll] for (int dy = -1; dy <= 1; dy++) {
                [unroll] for (int dx = -1; dx <= 1; dx++) {
                    int2 tap = clamp(centerHalf + int2(dx, dy), int2(0, 0), maxHalf);
                    float4 halfPos = HalfPosition.Load(int3(tap, 0));
                    float4 halfNorm = HalfNormal.Load(int3(tap, 0));
                    float4 halfGi = HalfIndirect[(uint)tap.y * Dimensions.z + (uint)tap.x];
                    float3 n1;
                    if (!all(isfinite(halfPos)) || !all(isfinite(halfNorm)) || !all(isfinite(halfGi))
                        || halfNorm.w < 1.0 || any(halfGi.rgb < 0.0) || halfGi.a < 0.0
                        || !UnitVector(halfNorm.xyz, n1)) continue;
                    float ndot = max(saturate(dot(n0, n1)), 0.05);
                    float3 delta = halfPos.xyz - fullPos.xyz;
                    float planeErr = max(abs(dot(delta, n0)), abs(dot(delta, n1))) / pixelWorldSize;
                    float distErr = length(delta) / pixelWorldSize;
                    float score = pow(ndot, 8.0) * exp2(-(planeErr * planeErr) * 0.1) * exp2(-(distErr * distErr) * 0.01);
                    if (isfinite(score) && score > bestScore) {
                        bestScore = score;
                        bestGi = halfGi;
                    }
                }
            }
            FullIndirect[fullIndex] = float4(max(bestGi.rgb, 0.0), saturate(bestGi.a));
        }
        """;
}
