using System.Numerics;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Built-in zero-external-dependency Native TAA pass reusing D3D12TemporalInputs
/// (depth, motion vectors, reactive mask) with 3x3 closest-depth velocity dilation,
/// 5-tap Catmull-Rom history reprojection, YCoCg variance clipping, and Karis-weighted blend.</summary>
internal sealed class D3D12NativeTaaPass(D3D12GpuRuntime runtime, D3D12GpuCache cache) : IDisposable
{
    internal sealed record Result(D3D12GpuTarget Output, bool EffectiveReset);

    private const int HistoryRingCount = D3D12SsaaGiMemoryBudget.MaximumInFlightFrames + 1;
    private readonly ID3D12Resource?[] historyRing = new ID3D12Resource?[HistoryRingCount];
    private readonly ResourceStates[] historyStates = new ResourceStates[HistoryRingCount];
    private int historyWidth, historyHeight;
    private int committedIndex, pendingIndex;
    private bool hasCommittedHistory, hasPendingCommit, pendingWasReset;
    private ID3D12Resource? zeroReactiveFallback;
    private (byte[] Vertex, byte[] Pixel)? shaders;
    private bool disposed;

    internal bool LastReset { get; private set; }

    internal Result Record(
        D3D12RecordingContext context,
        D3D12GpuTarget currentColor,
        D3D12TemporalInputs temporalInputs,
        Vector2 jitterPixels,
        bool resetHistory)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!ReferenceEquals(context.Runtime, runtime) || !runtime.IsRecording)
            throw new InvalidOperationException("Native TAA requires an open recording frame on the owning runtime.");
        if (currentColor.Depth || currentColor.Format != Format.R8G8B8A8_UNorm || !currentColor.Initialized)
            throw new InvalidOperationException("Native TAA requires initialized display RGBA8 scene color.");
        if (temporalInputs.Width != currentColor.Width || temporalInputs.Height != currentColor.Height)
            throw new InvalidOperationException("Native TAA requires render-resolution temporal inputs matching scene color.");

        int width = currentColor.Width, height = currentColor.Height;
        var device = context.Device;
        var commands = context.Commands;

        bool sizeChanged = historyRing[0] is null || historyWidth != width || historyHeight != height;
        if (sizeChanged)
        {
            RetireHistoryRing(context);
            for (int i = 0; i < HistoryRingCount; i++)
            {
                historyRing[i] = device.CreateCommittedResource(
                    HeapType.Default,
                    ResourceDescription.Texture2D(Format.R16G16B16A16_Float, (uint)width, (uint)height, mipLevels: 1, flags: ResourceFlags.AllowRenderTarget),
                    ResourceStates.PixelShaderResource);
                historyStates[i] = ResourceStates.PixelShaderResource;
            }
            historyWidth = width;
            historyHeight = height;
            committedIndex = 0;
            hasCommittedHistory = false;
        }

        bool effectiveReset = resetHistory || sizeChanged || !hasCommittedHistory;
        int readIdx = committedIndex;
        int writeIdx = (committedIndex + 1) % HistoryRingCount;
        var historyRead = historyRing[readIdx]!;
        var historyWrite = historyRing[writeIdx]!;

        var reactiveResource = temporalInputs.ReactiveMask ?? GetZeroReactiveFallback(context);

        var compiled = GetShaders();
        var root = cache.Get("native-taa:root", () => runtime.CreateRootSignature(
            new RootSignatureDescription(RootSignatureFlags.None,
                [new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
                 new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 5, 0)), ShaderVisibility.Pixel)],
                [new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
                {
                    Filter = Filter.MinMagLinearMipPoint,
                    AddressU = TextureAddressMode.Clamp,
                    AddressV = TextureAddressMode.Clamp,
                    AddressW = TextureAddressMode.Clamp,
                }]),
            RootSignatureVersion.Version10));

        var rtvHeap = runtime.Transients.Heap(new(DescriptorHeapType.RenderTargetView, 2));
        var srvHeap = runtime.Transients.Heap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 5, DescriptorHeapFlags.ShaderVisible));
        int rtvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        int srvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        runtime.DescriptorHighWater = Math.Max(runtime.DescriptorHighWater, 5);

        var outputResource = runtime.Transients.Resource(context, HeapType.Default,
            ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, (uint)width, (uint)height, mipLevels: 1, flags: ResourceFlags.AllowRenderTarget),
            ResourceStates.RenderTarget);
        var outputRtv = rtvHeap.GetCPUDescriptorHandleForHeapStart();
        var historyRtv = outputRtv;
        historyRtv.Offset(1, rtvStride);
        device.CreateRenderTargetView(outputResource, null, outputRtv);
        device.CreateRenderTargetView(historyWrite, null, historyRtv);
        var output = runtime.Transients.Track(new D3D12GpuTarget("$taa-output", outputResource, outputRtv, width, height, Format.R8G8B8A8_UNorm, false));

        device.CreateShaderResourceView(currentColor.Resource, null, srvHeap.GetCPUDescriptorHandleForHeapStart());
        device.CreateShaderResourceView(historyRead, null, srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(1, srvStride));
        device.CreateShaderResourceView(temporalInputs.Depth, null, srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(2, srvStride));
        device.CreateShaderResourceView(temporalInputs.MotionVectors, null, srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(3, srvStride));
        device.CreateShaderResourceView(reactiveResource, null, srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(4, srvStride));

        var pipeline = cache.Get("native-taa:pso", () =>
        {
            var pso = runtime.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
            {
                RootSignature = root,
                VertexShader = compiled.Vertex,
                PixelShader = compiled.Pixel,
                BlendState = BlendDescription.Opaque,
                SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone,
                DepthStencilState = DepthStencilDescription.None,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetFormats = [Format.R8G8B8A8_UNorm, Format.R16G16B16A16_Float],
                SampleDescription = new(1, 0),
            });
            runtime.PipelineCreations++;
            return pso;
        });

        commands.UnsetRenderTargets();
        context.Transition(currentColor, ResourceStates.PixelShaderResource);
        TransitionHistory(commands, readIdx, ResourceStates.PixelShaderResource);
        TransitionHistory(commands, writeIdx, ResourceStates.RenderTarget);

        Vector4[] constants =
        [
            new(width, height, 1f / width, 1f / height),
            new(jitterPixels.X, jitterPixels.Y, effectiveReset ? 1f : 0f, 0.1f),
        ];
        ulong cbAddress = context.UploadConstants(MemoryMarshal.AsBytes(constants.AsSpan()));

        commands.SetDescriptorHeaps(srvHeap);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.RSSetViewport(0, 0, width, height, 0, 1);
        commands.RSSetScissorRect(new RawRect(0, 0, width, height));
        commands.OMSetRenderTargets([outputRtv, historyRtv]);
        commands.SetGraphicsRootSignature(root);
        commands.SetPipelineState(pipeline);
        commands.SetGraphicsRootConstantBufferView(0, cbAddress);
        commands.SetGraphicsRootDescriptorTable(1, srvHeap.GetGPUDescriptorHandleForHeapStart());
        commands.DrawInstanced(3, 1, 0, 0);
        output.Initialized = true;
        commands.UnsetRenderTargets();
        TransitionHistory(commands, writeIdx, ResourceStates.PixelShaderResource);

        pendingIndex = writeIdx;
        hasPendingCommit = true;
        pendingWasReset = effectiveReset;
        LastReset = effectiveReset;
        return new(output, effectiveReset);
    }

    internal void CommitHistory()
    {
        if (!hasPendingCommit) return;
        committedIndex = pendingIndex;
        hasCommittedHistory = true;
        hasPendingCommit = false;
    }

    internal void AbortPending()
    {
        hasPendingCommit = false;
        if (pendingWasReset) hasCommittedHistory = false;
    }

    internal void InvalidateHistory()
    {
        hasCommittedHistory = false;
        hasPendingCommit = false;
    }

    private void TransitionHistory(ID3D12GraphicsCommandList commands, int index, ResourceStates targetState)
    {
        if (historyStates[index] == targetState) return;
        commands.ResourceBarrierTransition(historyRing[index]!, historyStates[index], targetState);
        historyStates[index] = targetState;
    }

    private void RetireHistoryRing(D3D12RecordingContext context)
    {
        for (int i = 0; i < HistoryRingCount; i++)
        {
            if (historyRing[i] is { } existing)
            {
                context.Own(existing);
                historyRing[i] = null;
                historyStates[i] = ResourceStates.Common;
            }
        }
    }

    private ID3D12Resource GetZeroReactiveFallback(D3D12RecordingContext context)
    {
        if (zeroReactiveFallback is not null) return zeroReactiveFallback;
        var device = context.Device;
        var desc = ResourceDescription.Texture2D(Format.R8_UNorm, 1, 1, mipLevels: 1);
        var resource = device.CreateCommittedResource(HeapType.Default, desc, ResourceStates.CopyDest);
        try
        {
            var footprint = new PlacedSubresourceFootPrint[1];
            device.GetCopyableFootprints(desc, 0, 1, 0, footprint, new int[1], new ulong[1], out ulong bytes);
            var staging = context.Own(device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer(bytes), ResourceStates.GenericRead));
            var mapped = staging.Map<byte>(0, checked((int)bytes));
            try { mapped.Clear(); }
            finally { staging.Unmap(0); }
            context.Commands.CopyTextureRegion(new TextureCopyLocation(resource, 0), 0, 0, 0, new TextureCopyLocation(staging, footprint[0]));
            context.Commands.ResourceBarrierTransition(resource, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
            runtime.TextureUploads++;
            zeroReactiveFallback = resource;
            runtime.OnAbort(() =>
            {
                if (ReferenceEquals(zeroReactiveFallback, resource))
                {
                    zeroReactiveFallback = null;
                    resource.Dispose();
                }
            });
            return resource;
        }
        catch
        {
            resource.Dispose();
            throw;
        }
    }

    internal static IEnumerable<D3D12FxcShaderRequest> GetPrewarmRequests()
    {
        const ShaderFlags flags = ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3;
        yield return new(Source, "VSMain", "native-taa.hlsl", "vs_5_1", flags);
        yield return new(Source, "PSTaa", "native-taa.hlsl", "ps_5_1", flags);
    }

    private (byte[] Vertex, byte[] Pixel) GetShaders()
    {
        if (shaders is { } compiled) return compiled;
        const ShaderFlags flags = ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3;
        compiled = (
            D3D12FxcShaderCache.Compile(Source, "VSMain", "native-taa.hlsl", "vs_5_1", flags),
            D3D12FxcShaderCache.Compile(Source, "PSTaa", "native-taa.hlsl", "ps_5_1", flags));
        shaders = compiled;
        runtime.ShaderCompilations += 2;
        return compiled;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        for (int i = 0; i < HistoryRingCount; i++)
        {
            historyRing[i]?.Dispose();
            historyRing[i] = null;
        }
        zeroReactiveFallback?.Dispose();
        zeroReactiveFallback = null;
    }

    internal const string Source = """
        cbuffer TaaConstants : register(b0) {
            float4 Resolution; // width, height, 1/width, 1/height
            float4 Params;     // jitterX, jitterY, resetFlag, baseAlpha
        };
        Texture2D<float4> CurrentColor : register(t0);
        Texture2D<float4> HistoryColor : register(t1);
        Texture2D<float> DepthTexture : register(t2);
        Texture2D<float2> MotionVectors : register(t3);
        Texture2D<float> ReactiveMask : register(t4);
        SamplerState LinearClamp : register(s0);

        struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
        struct TaaOut { float4 Display : SV_Target0; float4 History : SV_Target1; };

        VSOut VSMain(uint id : SV_VertexID) {
            VSOut o;
            o.Uv = float2((id << 1) & 2, id & 2);
            o.Position = float4(o.Uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return o;
        }

        float3 RgbToYCoCg(float3 rgb) {
            float y  = dot(rgb, float3( 0.25,  0.50,  0.25));
            float co = dot(rgb, float3( 0.50,  0.00, -0.50));
            float cg = dot(rgb, float3(-0.25,  0.50, -0.25));
            return float3(y, co, cg);
        }

        float3 YCoCgToRgb(float3 ycocg) {
            float tmp = ycocg.x - ycocg.z;
            return saturate(float3(tmp + ycocg.y, ycocg.x + ycocg.z, tmp - ycocg.y));
        }

        float3 ClipAabb(float3 aabbMin, float3 aabbMax, float3 history, float3 center) {
            float3 boxCenter = 0.5 * (aabbMax + aabbMin);
            float3 boxExtents = max(0.5 * (aabbMax - aabbMin), 1e-5);
            float3 offset = history - boxCenter;
            float3 unit = abs(offset / boxExtents);
            float maxUnit = max(unit.x, max(unit.y, unit.z));
            if (maxUnit > 1.0)
                return boxCenter + offset / maxUnit;
            return history;
        }

        float3 SampleHistoryCatmullRom(float2 uv, float2 texSize, float2 invTexSize) {
            float2 samplePos = uv * texSize;
            float2 texPos1 = floor(samplePos - 0.5) + 0.5;
            float2 f = samplePos - texPos1;
            float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
            float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
            float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
            float2 w3 = f * f * (-0.5 + 0.5 * f);
            float2 w12 = w1 + w2;
            float2 offset12 = w2 / max(w12, 1e-5);
            float2 minUv = 0.5 * invTexSize;
            float2 maxUv = 1.0 - minUv;
            float2 texPos0 = clamp((texPos1 - 1.0) * invTexSize, minUv, maxUv);
            float2 texPos3 = clamp((texPos1 + 2.0) * invTexSize, minUv, maxUv);
            float2 texPos12 = clamp((texPos1 + offset12) * invTexSize, minUv, maxUv);

            float wA = w12.x * w0.y;
            float wB = w0.x * w12.y;
            float wC = w12.x * w12.y;
            float wD = w3.x * w12.y;
            float wE = w12.x * w3.y;
            float3 result = 0.0;
            result += HistoryColor.SampleLevel(LinearClamp, float2(texPos12.x, texPos0.y), 0).rgb * wA;
            result += HistoryColor.SampleLevel(LinearClamp, float2(texPos0.x, texPos12.y), 0).rgb * wB;
            result += HistoryColor.SampleLevel(LinearClamp, float2(texPos12.x, texPos12.y), 0).rgb * wC;
            result += HistoryColor.SampleLevel(LinearClamp, float2(texPos3.x, texPos12.y), 0).rgb * wD;
            result += HistoryColor.SampleLevel(LinearClamp, float2(texPos12.x, texPos3.y), 0).rgb * wE;
            float totalW = wA + wB + wC + wD + wE;
            return saturate(result / max(totalW, 1e-5));
        }

        TaaOut PSTaa(VSOut input) {
            int2 maxCoord = max(int2(Resolution.xy) - 1, int2(0, 0));
            int2 coord = clamp(int2(input.Position.xy), int2(0, 0), maxCoord);
            float4 currentSample = CurrentColor.Load(int3(coord, 0));
            float3 currentRgb = currentSample.rgb;

            TaaOut output;
            if (Params.z > 0.5) {
                output.Display = float4(currentRgb, currentSample.a);
                output.History = float4(currentRgb, 1.0);
                return output;
            }

            // 3x3 closest-depth velocity dilation + YCoCg neighborhood moments.
            float closestDepth = 2.0;
            int2 closestCoord = coord;
            float3 m1 = 0.0;
            float3 m2 = 0.0;
            float3 minYCoCg = float3(1e5, 1e5, 1e5);
            float3 maxYCoCg = float3(-1e5, -1e5, -1e5);

            [unroll] for (int dy = -1; dy <= 1; dy++) {
                [unroll] for (int dx = -1; dx <= 1; dx++) {
                    int2 sc = clamp(coord + int2(dx, dy), int2(0, 0), maxCoord);
                    float d = DepthTexture.Load(int3(sc, 0));
                    if (d < closestDepth) {
                        closestDepth = d;
                        closestCoord = sc;
                    }
                    float3 c = RgbToYCoCg(CurrentColor.Load(int3(sc, 0)).rgb);
                    m1 += c;
                    m2 += c * c;
                    minYCoCg = min(minYCoCg, c);
                    maxYCoCg = max(maxYCoCg, c);
                }
            }

            float2 motionPixels = MotionVectors.Load(int3(closestCoord, 0));
            float2 prevUv = input.Uv + motionPixels * Resolution.zw;
            bool outOfBounds = any(prevUv < 0.0) || any(prevUv > 1.0);
            if (outOfBounds) {
                output.Display = float4(currentRgb, currentSample.a);
                output.History = float4(currentRgb, 1.0);
                return output;
            }

            float3 mean = m1 / 9.0;
            float3 stddev = sqrt(max(m2 / 9.0 - mean * mean, 0.0));
            float3 aabbMin = max(minYCoCg, mean - 1.15 * stddev);
            float3 aabbMax = min(maxYCoCg, mean + 1.15 * stddev);

            float3 historyRgb = SampleHistoryCatmullRom(prevUv, Resolution.xy, Resolution.zw);
            float3 currentYCoCg = RgbToYCoCg(currentRgb);
            float3 historyYCoCg = ClipAabb(aabbMin, aabbMax, RgbToYCoCg(historyRgb), currentYCoCg);
            float3 clampedHistory = YCoCgToRgb(historyYCoCg);

            float reactive = ReactiveMask.Load(int3(coord, 0));
            float velocityWeight = saturate(length(motionPixels) * 0.015) * 0.12;
            float blendFactor = saturate(max(Params.w + velocityWeight, reactive * 0.85));

            // Karis luminance weighting reduces subpixel highlight flicker.
            float lumaCurr = dot(currentRgb, float3(0.2126, 0.7152, 0.0722));
            float lumaHist = dot(clampedHistory, float3(0.2126, 0.7152, 0.0722));
            float wCurr = blendFactor / (1.0 + lumaCurr);
            float wHist = (1.0 - blendFactor) / (1.0 + lumaHist);
            float3 resolved = saturate((currentRgb * wCurr + clampedHistory * wHist) / max(wCurr + wHist, 1e-5));

            output.Display = float4(resolved, currentSample.a);
            output.History = float4(resolved, 1.0);
            return output;
        }
        """;
}
