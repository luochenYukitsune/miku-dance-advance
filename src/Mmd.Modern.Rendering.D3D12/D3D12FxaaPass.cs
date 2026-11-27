using System.Numerics;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Single-pass FXAA 3.11 (PC Quality 29) post-antialiasing pass.
/// Supports both opaque RGBA8 and straight-alpha premultiplied RGBA16F targets.</summary>
internal sealed class D3D12FxaaPass(D3D12GpuRuntime runtime, D3D12GpuCache cache)
{
    private (byte[] Vertex, byte[] Pixel)? opaqueShaders;
    private (byte[] Vertex, byte[] Pixel)? alphaShaders;

    internal D3D12GpuTarget Record(D3D12RecordingContext context, D3D12GpuTarget input, bool alphaCoverage = false)
    {
        if (!ReferenceEquals(context.Runtime, runtime) || !runtime.IsRecording)
            throw new InvalidOperationException("FXAA requires an open recording frame on the owning runtime.");
        if (input.Depth || input.Format is not (Format.R8G8B8A8_UNorm or Format.R16G16B16A16_Float) || !input.Initialized)
            throw new InvalidOperationException("FXAA requires initialized RGBA8 or RGBA16F frame color.");

        int width = input.Width, height = input.Height;
        var device = context.Device;
        var commands = context.Commands;
        var compiled = GetShaders(alphaCoverage);

        var root = cache.Get("fxaa:root", () => runtime.CreateRootSignature(
            new RootSignatureDescription(RootSignatureFlags.None,
                [new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
                 new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 0)), ShaderVisibility.Pixel)],
                [new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
                {
                    Filter = Filter.MinMagLinearMipPoint,
                    AddressU = TextureAddressMode.Clamp,
                    AddressV = TextureAddressMode.Clamp,
                    AddressW = TextureAddressMode.Clamp,
                }]),
            RootSignatureVersion.Version10));

        var rtvHeap = runtime.Transients.Heap(new(DescriptorHeapType.RenderTargetView, 1));
        var srvHeap = runtime.Transients.Heap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, DescriptorHeapFlags.ShaderVisible));
        runtime.DescriptorHighWater = Math.Max(runtime.DescriptorHighWater, 1);

        var resource = runtime.Transients.Resource(context, HeapType.Default,
            ResourceDescription.Texture2D(input.Format, (uint)width, (uint)height, mipLevels: 1, flags: ResourceFlags.AllowRenderTarget),
            ResourceStates.RenderTarget);
        var rtvHandle = rtvHeap.GetCPUDescriptorHandleForHeapStart();
        device.CreateRenderTargetView(resource, null, rtvHandle);
        device.CreateShaderResourceView(input.Resource, null, srvHeap.GetCPUDescriptorHandleForHeapStart());
        var output = runtime.Transients.Track(new D3D12GpuTarget("$fxaa-output", resource, rtvHandle, width, height, input.Format, false));

        string psoKey = $"fxaa:pso:{(alphaCoverage ? "alpha" : "opaque")}:{input.Format}";
        var pipeline = cache.Get(psoKey, () =>
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
                RenderTargetFormats = [input.Format],
                SampleDescription = new(1, 0),
            });
            runtime.PipelineCreations++;
            return pso;
        });

        ulong cbAddress = context.UploadConstants(new Vector4(1f / width, 1f / height, width, height));

        commands.UnsetRenderTargets();
        context.Transition(input, ResourceStates.PixelShaderResource);
        commands.SetDescriptorHeaps(srvHeap);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.RSSetViewport(0, 0, width, height, 0, 1);
        commands.RSSetScissorRect(new RawRect(0, 0, width, height));
        commands.OMSetRenderTargets(output.Handle);
        commands.SetGraphicsRootSignature(root);
        commands.SetPipelineState(pipeline);
        commands.SetGraphicsRootConstantBufferView(0, cbAddress);
        commands.SetGraphicsRootDescriptorTable(1, srvHeap.GetGPUDescriptorHandleForHeapStart());
        commands.DrawInstanced(3, 1, 0, 0);
        output.Initialized = true;
        commands.UnsetRenderTargets();
        return output;
    }

    internal static IEnumerable<D3D12FxcShaderRequest> GetPrewarmRequests(bool alphaCoverage)
    {
        const ShaderFlags flags = ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3;
        string entry = alphaCoverage ? "PSFxaaAlpha" : "PSFxaa";
        yield return new(Source, "VSMain", "fxaa311.hlsl", "vs_5_1", flags);
        yield return new(Source, entry, "fxaa311.hlsl", "ps_5_1", flags);
    }

    private (byte[] Vertex, byte[] Pixel) GetShaders(bool alphaCoverage)
    {
        const ShaderFlags flags = ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3;
        if (alphaCoverage)
        {
            if (alphaShaders is { } existing) return existing;
            var created = (
                D3D12FxcShaderCache.Compile(Source, "VSMain", "fxaa311.hlsl", "vs_5_1", flags),
                D3D12FxcShaderCache.Compile(Source, "PSFxaaAlpha", "fxaa311.hlsl", "ps_5_1", flags));
            alphaShaders = created;
            runtime.ShaderCompilations += 2;
            return created;
        }
        if (opaqueShaders is { } compiled) return compiled;
        compiled = (
            D3D12FxcShaderCache.Compile(Source, "VSMain", "fxaa311.hlsl", "vs_5_1", flags),
            D3D12FxcShaderCache.Compile(Source, "PSFxaa", "fxaa311.hlsl", "ps_5_1", flags));
        opaqueShaders = compiled;
        runtime.ShaderCompilations += 2;
        return compiled;
    }

    // FXAA 3.11 PC Quality (12-step tangent search, subpixel quality 0.75).
    internal const string Source = """
        cbuffer FxaaConstants : register(b0) { float4 Metrics; };
        Texture2D<float4> InputTexture : register(t0);
        SamplerState LinearClamp : register(s0);

        struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };

        VSOut VSMain(uint id : SV_VertexID) {
            VSOut o;
            o.Uv = float2((id << 1) & 2, id & 2);
            o.Position = float4(o.Uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return o;
        }

        float FxaaLuma(float3 rgb) {
            return dot(rgb, float3(0.299, 0.587, 0.114));
        }

        float FxaaChannel(float4 rgba, bool useAlpha) {
            return useAlpha ? rgba.a : FxaaLuma(rgba.rgb);
        }

        float4 FxaaCore(float2 uv, bool alphaCoverage) {
            static const float EDGE_THRESHOLD = 0.125;
            static const float EDGE_THRESHOLD_MIN = 0.0312;
            static const float SUBPIX_QUALITY = 0.75;
            static const float kSteps[12] = { 1.0, 1.0, 1.0, 1.0, 1.0, 1.5, 2.0, 2.0, 2.0, 2.0, 4.0, 8.0 };

            float2 invSize = Metrics.xy;
            float4 center = InputTexture.SampleLevel(LinearClamp, uv, 0);
            float4 sampleN = InputTexture.SampleLevel(LinearClamp, uv + float2(0, -invSize.y), 0);
            float4 sampleS = InputTexture.SampleLevel(LinearClamp, uv + float2(0,  invSize.y), 0);
            float4 sampleW = InputTexture.SampleLevel(LinearClamp, uv + float2(-invSize.x, 0), 0);
            float4 sampleE = InputTexture.SampleLevel(LinearClamp, uv + float2( invSize.x, 0), 0);

            float lM = FxaaLuma(center.rgb);
            float lN = FxaaLuma(sampleN.rgb);
            float lS = FxaaLuma(sampleS.rgb);
            float lW = FxaaLuma(sampleW.rgb);
            float lE = FxaaLuma(sampleE.rgb);
            float lumaMin = min(lM, min(min(lN, lS), min(lW, lE)));
            float lumaMax = max(lM, max(max(lN, lS), max(lW, lE)));
            float lumaRange = lumaMax - lumaMin;

            bool useAlpha = false;
            float lumaM = lM, lumaN = lN, lumaS = lS, lumaW = lW, lumaE = lE;
            float rangeMax = lumaMax;
            float range = lumaRange;

            if (alphaCoverage) {
                float aM = center.a, aN = sampleN.a, aS = sampleS.a, aW = sampleW.a, aE = sampleE.a;
                float alphaMin = min(aM, min(min(aN, aS), min(aW, aE)));
                float alphaMax = max(aM, max(max(aN, aS), max(aW, aE)));
                float alphaRange = alphaMax - alphaMin;
                if (alphaRange > lumaRange) {
                    useAlpha = true;
                    lumaM = aM; lumaN = aN; lumaS = aS; lumaW = aW; lumaE = aE;
                    rangeMax = alphaMax;
                    range = alphaRange;
                }
            }

            if (range < max(EDGE_THRESHOLD_MIN, rangeMax * EDGE_THRESHOLD)) {
                return center;
            }

            float lumaNW = FxaaChannel(InputTexture.SampleLevel(LinearClamp, uv + float2(-invSize.x, -invSize.y), 0), useAlpha);
            float lumaNE = FxaaChannel(InputTexture.SampleLevel(LinearClamp, uv + float2( invSize.x, -invSize.y), 0), useAlpha);
            float lumaSW = FxaaChannel(InputTexture.SampleLevel(LinearClamp, uv + float2(-invSize.x,  invSize.y), 0), useAlpha);
            float lumaSE = FxaaChannel(InputTexture.SampleLevel(LinearClamp, uv + float2( invSize.x,  invSize.y), 0), useAlpha);

            float lumaNS = lumaN + lumaS;
            float lumaWE = lumaW + lumaE;
            float subpixRcpRange = 1.0 / range;
            float subpixNSWE = lumaNS + lumaWE;
            float edgeHorz1 = (-2.0 * lumaM) + lumaNS;
            float edgeVert1 = (-2.0 * lumaM) + lumaWE;

            float lumaNESE = lumaNE + lumaSE;
            float lumaNWNE = lumaNW + lumaNE;
            float edgeHorz2 = (-2.0 * lumaE) + lumaNESE;
            float edgeVert2 = (-2.0 * lumaN) + lumaNWNE;

            float lumaNWSW = lumaNW + lumaSW;
            float lumaSWSE = lumaSW + lumaSE;
            float edgeHorz4 = (abs(edgeHorz1) * 2.0) + abs(edgeHorz2);
            float edgeVert4 = (abs(edgeVert1) * 2.0) + abs(edgeVert2);
            float edgeHorz3 = (-2.0 * lumaW) + lumaNWSW;
            float edgeVert3 = (-2.0 * lumaS) + lumaSWSE;
            float edgeHorz = abs(edgeHorz3) + edgeHorz4;
            float edgeVert = abs(edgeVert3) + edgeVert4;

            float subpixNWSWNESE = lumaNWSW + lumaNESE;
            bool isHorizontal = edgeHorz >= edgeVert;

            float lengthSign = isHorizontal ? invSize.y : invSize.x;
            float luma1 = isHorizontal ? lumaN : lumaW;
            float luma2 = isHorizontal ? lumaS : lumaE;
            float gradient1 = luma1 - lumaM;
            float gradient2 = luma2 - lumaM;
            bool is1Steepest = abs(gradient1) >= abs(gradient2);
            float gradientScaled = 0.25 * max(abs(gradient1), abs(gradient2));

            float lumaLocalAverage;
            if (is1Steepest) {
                lengthSign = -lengthSign;
                lumaLocalAverage = 0.5 * (luma1 + lumaM);
            } else {
                lumaLocalAverage = 0.5 * (luma2 + lumaM);
            }

            float2 currentUv = uv;
            if (isHorizontal) currentUv.y += lengthSign * 0.5;
            else currentUv.x += lengthSign * 0.5;

            float2 offset = isHorizontal ? float2(invSize.x, 0.0) : float2(0.0, invSize.y);
            float2 uv1 = currentUv - offset;
            float2 uv2 = currentUv + offset;

            float lumaEnd1 = FxaaChannel(InputTexture.SampleLevel(LinearClamp, uv1, 0), useAlpha) - lumaLocalAverage;
            float lumaEnd2 = FxaaChannel(InputTexture.SampleLevel(LinearClamp, uv2, 0), useAlpha) - lumaLocalAverage;
            bool reached1 = abs(lumaEnd1) >= gradientScaled;
            bool reached2 = abs(lumaEnd2) >= gradientScaled;
            bool reachedBoth = reached1 && reached2;

            if (!reached1) uv1 -= offset;
            if (!reached2) uv2 += offset;

            if (!reachedBoth) {
                [unroll] for (int i = 1; i < 12; i++) {
                    if (!reached1) {
                        lumaEnd1 = FxaaChannel(InputTexture.SampleLevel(LinearClamp, uv1, 0), useAlpha) - lumaLocalAverage;
                        reached1 = abs(lumaEnd1) >= gradientScaled;
                    }
                    if (!reached2) {
                        lumaEnd2 = FxaaChannel(InputTexture.SampleLevel(LinearClamp, uv2, 0), useAlpha) - lumaLocalAverage;
                        reached2 = abs(lumaEnd2) >= gradientScaled;
                    }
                    reachedBoth = reached1 && reached2;
                    if (!reached1) uv1 -= offset * kSteps[i];
                    if (!reached2) uv2 += offset * kSteps[i];
                    if (reachedBoth) break;
                }
            }

            float distance1 = isHorizontal ? (uv.x - uv1.x) : (uv.y - uv1.y);
            float distance2 = isHorizontal ? (uv2.x - uv.x) : (uv2.y - uv.y);
            bool isDirection1 = distance1 < distance2;
            float distanceFinal = min(distance1, distance2);
            float edgeThickness = max(distance1 + distance2, 1e-6);
            float pixelOffset = -distanceFinal / edgeThickness + 0.5;

            bool isLumaCenterSmaller = lumaM < lumaLocalAverage;
            bool correctVariation = ((isDirection1 ? lumaEnd1 : lumaEnd2) < 0.0) != isLumaCenterSmaller;
            float finalOffset = correctVariation ? pixelOffset : 0.0;

            float lumaAverage = (1.0 / 12.0) * (2.0 * subpixNSWE + subpixNWSWNESE);
            float subpixOffset1 = saturate(abs(lumaAverage - lumaM) * subpixRcpRange);
            float subpixOffset2 = (-2.0 * subpixOffset1 + 3.0) * subpixOffset1 * subpixOffset1;
            float subpixOffsetFinal = subpixOffset2 * subpixOffset2 * SUBPIX_QUALITY;
            finalOffset = max(finalOffset, subpixOffsetFinal);

            float2 finalUv = uv;
            if (isHorizontal) finalUv.y += finalOffset * lengthSign;
            else finalUv.x += finalOffset * lengthSign;

            float4 sampled = InputTexture.SampleLevel(LinearClamp, finalUv, 0);
            return alphaCoverage ? sampled : float4(sampled.rgb, center.a);
        }

        float4 PSFxaa(VSOut i) : SV_Target {
            return FxaaCore(i.Uv, false);
        }

        float4 PSFxaaAlpha(VSOut i) : SV_Target {
            return FxaaCore(i.Uv, true);
        }
        """;
}
