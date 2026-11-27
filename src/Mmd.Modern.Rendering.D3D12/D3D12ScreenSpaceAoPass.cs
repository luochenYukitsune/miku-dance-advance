using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Depth-based horizon screen-space ambient occlusion (GTAO/SSAO) with bilateral edge-preserving blur,
/// plus a 1x MSAA depth resolve pass so MSAA color/depth align seamlessly with SSAO.</summary>
internal sealed class D3D12ScreenSpaceAoPass(D3D12GpuRuntime runtime, D3D12GpuCache cache)
{
    internal sealed record Result(D3D12GpuTarget RawAo, D3D12GpuTarget Output);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct SsaoConstants(
        Matrix4x4 InvViewProjection,
        Vector4 CameraEyeAndOrtho,    // xyz = camera eye, w = 1 if orthographic else 0
        Vector4 CameraOrthoTowardEye, // xyz = normalize(Eye - Target), w = 0
        Vector4 Metrics,              // width, height, 1/width, 1/height
        Vector4 Params);              // strength, radius, sampleCount, projScalePixels

    private (byte[] Vertex, byte[] Raw, byte[] Composite, byte[] MsaaDepthResolve)? shaders;

    internal static Format DepthResourceFormat(Format dsvFormat) => dsvFormat switch
    {
        Format.D32_Float => Format.R32_Typeless,
        Format.D24_UNorm_S8_UInt => Format.R24G8_Typeless,
        _ => throw new NotSupportedException($"Unsupported depth format for SSAO: {dsvFormat}."),
    };

    internal static Format DepthSrvFormat(Format dsvFormat) => dsvFormat switch
    {
        Format.D32_Float => Format.R32_Float,
        Format.D24_UNorm_S8_UInt => Format.R24_UNorm_X8_Typeless,
        _ => throw new NotSupportedException($"Unsupported depth SRV format for SSAO: {dsvFormat}."),
    };

    /// <summary>Resolves a multisampled depth buffer to a 1x R32_Float depth target using minimum (nearest) sample depth.</summary>
    internal D3D12GpuTarget ResolveMsaaDepth(
        D3D12RecordingContext context,
        D3D12GpuTarget msaaDepth,
        int sampleCount)
    {
        if (!ReferenceEquals(context.Runtime, runtime) || !runtime.IsRecording)
            throw new InvalidOperationException("MSAA depth resolve requires an open recording frame.");
        if (!msaaDepth.Depth || sampleCount <= 1)
            throw new ArgumentException("MSAA depth resolve requires a multisampled depth target.", nameof(msaaDepth));

        int width = msaaDepth.Width, height = msaaDepth.Height;
        var device = context.Device;
        var commands = context.Commands;
        var compiled = GetShaders();

        var root = cache.Get("msaa-depth-resolve:root", () => runtime.CreateRootSignature(
            new RootSignatureDescription(RootSignatureFlags.None,
                [new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.Pixel),
                 new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 0)), ShaderVisibility.Pixel)]),
            RootSignatureVersion.Version10));

        var rtvHeap = runtime.Transients.Heap(new(DescriptorHeapType.RenderTargetView, 1));
        var srvHeap = runtime.Transients.Heap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, DescriptorHeapFlags.ShaderVisible));
        runtime.DescriptorHighWater = Math.Max(runtime.DescriptorHighWater, 1);

        var resolvedResource = runtime.Transients.Resource(context, HeapType.Default,
            ResourceDescription.Texture2D(Format.R32_Float, (uint)width, (uint)height, mipLevels: 1, flags: ResourceFlags.AllowRenderTarget),
            ResourceStates.RenderTarget);
        var rtvHandle = rtvHeap.GetCPUDescriptorHandleForHeapStart();
        device.CreateRenderTargetView(resolvedResource, null, rtvHandle);
        device.CreateShaderResourceView(msaaDepth.Resource, new ShaderResourceViewDescription
        {
            Format = DepthSrvFormat(msaaDepth.Format),
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2DMultisampled,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
        }, srvHeap.GetCPUDescriptorHandleForHeapStart());

        var output = runtime.Transients.Track(new D3D12GpuTarget("$msaa-depth-1x", resolvedResource, rtvHandle, width, height, Format.R32_Float, false));

        var pipeline = cache.Get("msaa-depth-resolve:pso", () =>
        {
            var pso = runtime.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
            {
                RootSignature = root,
                VertexShader = compiled.Vertex,
                PixelShader = compiled.MsaaDepthResolve,
                BlendState = BlendDescription.Opaque,
                SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone,
                DepthStencilState = DepthStencilDescription.None,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetFormats = [Format.R32_Float],
                SampleDescription = new(1, 0),
            });
            runtime.PipelineCreations++;
            return pso;
        });

        commands.UnsetRenderTargets();
        context.Transition(msaaDepth, ResourceStates.PixelShaderResource);
        commands.SetDescriptorHeaps(srvHeap);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.RSSetViewport(0, 0, width, height, 0, 1);
        commands.RSSetScissorRect(new RawRect(0, 0, width, height));
        commands.OMSetRenderTargets(output.Handle);
        commands.SetGraphicsRootSignature(root);
        commands.SetPipelineState(pipeline);
        commands.SetGraphicsRootConstantBufferView(0, context.UploadConstants(new Vector4(sampleCount, width, height, 0)));
        commands.SetGraphicsRootDescriptorTable(1, srvHeap.GetGPUDescriptorHandleForHeapStart());
        commands.DrawInstanced(3, 1, 0, 0);
        output.Initialized = true;
        commands.UnsetRenderTargets();
        return output;
    }

    internal Result Record(
        D3D12RecordingContext context,
        D3D12GpuTarget colorInput,
        D3D12GpuTarget depthInput,
        SceneCamera camera,
        ScreenSpaceAoOptions options,
        Result? reusableTargets = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!ReferenceEquals(context.Runtime, runtime) || !runtime.IsRecording)
            throw new InvalidOperationException("SSAO requires an open recording frame on the owning runtime.");
        if (colorInput.Depth || colorInput.Format is not (Format.R8G8B8A8_UNorm or Format.R16G16B16A16_Float) || !colorInput.Initialized)
            throw new InvalidOperationException("SSAO requires initialized 1x RGBA8 or RGBA16F color.");
        if (depthInput.Width != colorInput.Width || depthInput.Height != colorInput.Height)
            throw new InvalidOperationException("SSAO requires 1x depth matching color dimensions.");

        int width = colorInput.Width, height = colorInput.Height;
        var device = context.Device;
        var commands = context.Commands;
        var compiled = GetShaders();

        var root = cache.Get("ssao:root", () => runtime.CreateRootSignature(
            new RootSignatureDescription(RootSignatureFlags.None,
                [new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.Pixel),
                 new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 3, 0)), ShaderVisibility.Pixel)],
                [new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
                {
                    Filter = Filter.MinMagMipPoint,
                    AddressU = TextureAddressMode.Clamp,
                    AddressV = TextureAddressMode.Clamp,
                    AddressW = TextureAddressMode.Clamp,
                }]),
            RootSignatureVersion.Version10));

        var srvHeap = runtime.Transients.Heap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 3, DescriptorHeapFlags.ShaderVisible));
        int srvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        runtime.DescriptorHighWater = Math.Max(runtime.DescriptorHighWater, 3);

        D3D12GpuTarget rawAo, output;
        if (reusableTargets is not null)
        {
            rawAo = reusableTargets.RawAo;
            output = reusableTargets.Output;
            if (rawAo.Width != width || rawAo.Height != height || output.Width != width || output.Height != height || output.Format != colorInput.Format)
                throw new InvalidOperationException("Reusable SSAO targets must match current frame dimensions and color format.");
            context.Transition(rawAo, ResourceStates.RenderTarget);
            context.Transition(output, ResourceStates.RenderTarget);
        }
        else
        {
            var rtvHeap = runtime.Transients.Heap(new(DescriptorHeapType.RenderTargetView, 2));
            int rtvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
            var rawResource = runtime.Transients.Resource(context, HeapType.Default,
                ResourceDescription.Texture2D(Format.R8_UNorm, (uint)width, (uint)height, mipLevels: 1, flags: ResourceFlags.AllowRenderTarget),
                ResourceStates.RenderTarget);
            var outResource = runtime.Transients.Resource(context, HeapType.Default,
                ResourceDescription.Texture2D(colorInput.Format, (uint)width, (uint)height, mipLevels: 1, flags: ResourceFlags.AllowRenderTarget),
                ResourceStates.RenderTarget);
            var rawRtv = rtvHeap.GetCPUDescriptorHandleForHeapStart();
            var outRtv = rawRtv;
            outRtv.Offset(1, rtvStride);
            device.CreateRenderTargetView(rawResource, null, rawRtv);
            device.CreateRenderTargetView(outResource, null, outRtv);
            rawAo = runtime.Transients.Track(new D3D12GpuTarget("$ssao-raw", rawResource, rawRtv, width, height, Format.R8_UNorm, false));
            output = runtime.Transients.Track(new D3D12GpuTarget("$ssao-output", outResource, outRtv, width, height, colorInput.Format, false));
        }

        Format depthSrvFormat = depthInput.Depth ? DepthSrvFormat(depthInput.Format) : depthInput.Format;
        device.CreateShaderResourceView(colorInput.Resource, null, srvHeap.GetCPUDescriptorHandleForHeapStart());
        device.CreateShaderResourceView(rawAo.Resource, null, srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(1, srvStride));
        device.CreateShaderResourceView(depthInput.Resource, new ShaderResourceViewDescription
        {
            Format = depthSrvFormat,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 },
        }, srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(2, srvStride));

        var rawPipeline = cache.Get("ssao:pso:raw", () =>
        {
            var pso = runtime.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
            {
                RootSignature = root,
                VertexShader = compiled.Vertex,
                PixelShader = compiled.Raw,
                BlendState = BlendDescription.Opaque,
                SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone,
                DepthStencilState = DepthStencilDescription.None,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetFormats = [Format.R8_UNorm],
                SampleDescription = new(1, 0),
            });
            runtime.PipelineCreations++;
            return pso;
        });

        var compositePipeline = cache.Get($"ssao:pso:composite:{colorInput.Format}", () =>
        {
            var pso = runtime.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
            {
                RootSignature = root,
                VertexShader = compiled.Vertex,
                PixelShader = compiled.Composite,
                BlendState = BlendDescription.Opaque,
                SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone,
                DepthStencilState = DepthStencilDescription.None,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetFormats = [colorInput.Format],
                SampleDescription = new(1, 0),
            });
            runtime.PipelineCreations++;
            return pso;
        });

        var vp = camera.ViewProjection(width, height);
        if (!Matrix4x4.Invert(vp, out var invVp))
            invVp = Matrix4x4.Identity;
        bool isOrtho = camera.VerticalFovDegrees is null;
        float projScalePixels = isOrtho
            ? height / MathF.Max(camera.OrthographicHeight, 1e-4f)
            : 0.5f * height / MathF.Tan(camera.VerticalFovDegrees!.Value * (MathF.PI / 360f));

        var orthoTowardEye = Vector3.Normalize(camera.Eye - camera.Target);
        ulong cbAddress = context.UploadConstants(new SsaoConstants(
            invVp,
            new Vector4(camera.Eye, isOrtho ? 1f : 0f),
            new Vector4(orthoTowardEye, 0f),
            new Vector4(width, height, 1f / width, 1f / height),
            new Vector4(options.Strength, options.Radius, options.SampleCount, projScalePixels)));

        commands.UnsetRenderTargets();
        context.Transition(colorInput, ResourceStates.PixelShaderResource);
        context.Transition(depthInput, ResourceStates.PixelShaderResource);

        commands.SetDescriptorHeaps(srvHeap);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.RSSetViewport(0, 0, width, height, 0, 1);
        commands.RSSetScissorRect(new RawRect(0, 0, width, height));
        commands.SetGraphicsRootSignature(root);
        commands.SetGraphicsRootConstantBufferView(0, cbAddress);
        commands.SetGraphicsRootDescriptorTable(1, srvHeap.GetGPUDescriptorHandleForHeapStart());

        // Pass 1: horizon-based GTAO/SSAO evaluation.
        commands.OMSetRenderTargets(rawAo.Handle);
        commands.SetPipelineState(rawPipeline);
        commands.DrawInstanced(3, 1, 0, 0);
        rawAo.Initialized = true;
        commands.UnsetRenderTargets();

        // Pass 2: depth-aware bilateral blur + color modulation.
        context.Transition(rawAo, ResourceStates.PixelShaderResource);
        commands.OMSetRenderTargets(output.Handle);
        commands.SetPipelineState(compositePipeline);
        commands.DrawInstanced(3, 1, 0, 0);
        output.Initialized = true;
        commands.UnsetRenderTargets();

        return new(rawAo, output);
    }

    internal static IEnumerable<D3D12FxcShaderRequest> GetPrewarmRequests()
    {
        const ShaderFlags flags = ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3;
        yield return new(Source, "VSMain", "ssao-gtao.hlsl", "vs_5_1", flags);
        yield return new(Source, "PSSsaoRaw", "ssao-gtao.hlsl", "ps_5_1", flags);
        yield return new(Source, "PSSsaoComposite", "ssao-gtao.hlsl", "ps_5_1", flags);
        yield return new(MsaaResolveSource, "PSResolveDepth", "msaa-depth-resolve.hlsl", "ps_5_1", flags);
    }

    private (byte[] Vertex, byte[] Raw, byte[] Composite, byte[] MsaaDepthResolve) GetShaders()
    {
        if (shaders is { } compiled) return compiled;
        const ShaderFlags flags = ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3;
        compiled = (
            D3D12FxcShaderCache.Compile(Source, "VSMain", "ssao-gtao.hlsl", "vs_5_1", flags),
            D3D12FxcShaderCache.Compile(Source, "PSSsaoRaw", "ssao-gtao.hlsl", "ps_5_1", flags),
            D3D12FxcShaderCache.Compile(Source, "PSSsaoComposite", "ssao-gtao.hlsl", "ps_5_1", flags),
            D3D12FxcShaderCache.Compile(MsaaResolveSource, "PSResolveDepth", "msaa-depth-resolve.hlsl", "ps_5_1", flags));
        shaders = compiled;
        runtime.ShaderCompilations += 4;
        return compiled;
    }

    internal const string MsaaResolveSource = """
        cbuffer ResolveConstants : register(b0) { float4 Info; };
        Texture2DMS<float> MsaaDepth : register(t0);
        float PSResolveDepth(float4 pos : SV_Position) : SV_Target {
            int2 coord = clamp(int2(pos.xy), int2(0, 0), max(int2(Info.yz) - 1, int2(0, 0)));
            int count = max((int)Info.x, 1);
            float minDepth = 1.0;
            [loop] for (int s = 0; s < count; s++) {
                minDepth = min(minDepth, MsaaDepth.Load(coord, s));
            }
            return minDepth;
        }
        """;

    internal const string Source = """
        cbuffer SsaoConstants : register(b0) {
            row_major float4x4 InvViewProjection;
            float4 CameraEyeAndOrtho;
            float4 CameraOrthoTowardEye;
            float4 Metrics; // width, height, 1/width, 1/height
            float4 Params;  // strength, radius, sampleCount, projScalePixels
        };
        Texture2D<float4> ColorTexture : register(t0);
        Texture2D<float> RawAoTexture : register(t1);
        Texture2D<float> DepthTexture : register(t2);
        SamplerState PointClamp : register(s0);

        struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };

        VSOut VSMain(uint id : SV_VertexID) {
            VSOut o;
            o.Uv = float2((id << 1) & 2, id & 2);
            o.Position = float4(o.Uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return o;
        }

        float3 ReconstructWorldPos(float2 uv, float depth) {
            float4 clip = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, depth, 1.0);
            float4 worldH = mul(clip, InvViewProjection);
            return worldH.xyz / max(abs(worldH.w), 1e-6) * (worldH.w < 0.0 ? -1.0 : 1.0);
        }

        float InterleavedGradientNoise(float2 pixel) {
            return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
        }

        float PSSsaoRaw(VSOut input) : SV_Target {
            float2 uv = input.Uv;
            float centerDepth = DepthTexture.SampleLevel(PointClamp, uv, 0);
            if (centerDepth >= 0.999999) return 1.0;

            float3 centerPos = ReconstructWorldPos(uv, centerDepth);
            float3 toCamera = CameraEyeAndOrtho.xyz - centerPos;
            float cameraDist = max(length(toCamera), 1e-4);
            float3 viewDir = CameraEyeAndOrtho.w > 0.5 ? CameraOrthoTowardEye.xyz : (toCamera / cameraDist);

            // Discontinuity-aware normal reconstruction from adjacent depth texels.
            float2 dx = float2(Metrics.z, 0.0);
            float2 dy = float2(0.0, Metrics.w);
            float dL = DepthTexture.SampleLevel(PointClamp, uv - dx, 0);
            float dR = DepthTexture.SampleLevel(PointClamp, uv + dx, 0);
            float dT = DepthTexture.SampleLevel(PointClamp, uv - dy, 0);
            float dB = DepthTexture.SampleLevel(PointClamp, uv + dy, 0);

            float3 pL = ReconstructWorldPos(uv - dx, dL);
            float3 pR = ReconstructWorldPos(uv + dx, dR);
            float3 pT = ReconstructWorldPos(uv - dy, dT);
            float3 pB = ReconstructWorldPos(uv + dy, dB);

            float3 ddxP = (abs(dR - centerDepth) < abs(centerDepth - dL)) ? (pR - centerPos) : (centerPos - pL);
            float3 ddyP = (abs(dB - centerDepth) < abs(centerDepth - dT)) ? (pB - centerPos) : (centerPos - pT);
            float3 rawNormal = cross(ddxP, ddyP);
            float normalLen2 = dot(rawNormal, rawNormal);
            if (normalLen2 < 1e-12) return 1.0;
            float3 normal = rawNormal * rsqrt(normalLen2);
            if (dot(normal, viewDir) < 0.0) normal = -normal;

            float radius = Params.y;
            float invRadius2 = 1.0 / max(radius * radius, 1e-6);
            float screenRadius = CameraEyeAndOrtho.w > 0.5
                ? radius * Params.w
                : radius * Params.w / cameraDist;
            screenRadius = clamp(screenRadius, 2.0, 48.0);

            int sampleCount = (int)(Params.z + 0.5);
            int slices = sampleCount <= 4 ? 2 : 4;
            int steps = sampleCount >= 16 ? 4 : 2;
            float noiseAngle = InterleavedGradientNoise(input.Position.xy) * 3.14159265;
            float noiseStep = InterleavedGradientNoise(input.Position.xy + float2(17.0, 31.0));

            float occlusionSum = 0.0;
            [loop] for (int s = 0; s < slices; s++) {
                float phi = noiseAngle + ((float)s / (float)slices) * 3.14159265;
                float2 dir = float2(cos(phi), sin(phi)) * Metrics.zw;

                [unroll] for (int side = -1; side <= 1; side += 2) {
                    float maxHorizon = 0.0;
                    [loop] for (int st = 0; st < steps; st++) {
                        float fracDist = ((float)st + 0.35 + 0.65 * noiseStep) / (float)steps;
                        float pixelDist = max(1.0, fracDist * screenRadius);
                        float2 sampleUv = uv + dir * (pixelDist * (float)side);
                        if (any(sampleUv < 0.0) || any(sampleUv > 1.0)) continue;
                        float sampleDepth = DepthTexture.SampleLevel(PointClamp, sampleUv, 0);
                        if (sampleDepth >= 0.999999) continue;
                        float3 delta = ReconstructWorldPos(sampleUv, sampleDepth) - centerPos;
                        float dist2 = dot(delta, delta);
                        if (dist2 <= 1e-6 || dist2 >= radius * radius) continue;
                        float elevation = dot(delta * rsqrt(dist2), normal);
                        float falloff = saturate(1.0 - dist2 * invRadius2);
                        float occ = saturate((elevation - 0.08) / 0.92) * falloff;
                        maxHorizon = max(maxHorizon, occ);
                    }
                    occlusionSum += maxHorizon;
                }
            }

            float avgOcclusion = occlusionSum / (float)(slices * 2);
            return saturate(1.0 - avgOcclusion * Params.x);
        }

        float4 PSSsaoComposite(VSOut input) : SV_Target {
            int2 maxCoord = max(int2(Metrics.xy) - 1, int2(0, 0));
            int2 coord = clamp(int2(input.Position.xy), int2(0, 0), maxCoord);
            float4 color = ColorTexture.Load(int3(coord, 0));
            float centerDepth = DepthTexture.Load(int3(coord, 0));
            if (centerDepth >= 0.999999) return color;

            float centerAo = RawAoTexture.Load(int3(coord, 0));
            float aoSum = centerAo;
            float weightSum = 1.0;

            static const float kSpatial[3] = { 1.0, 0.72, 0.36 };
            [unroll] for (int dy = -2; dy <= 2; dy++) {
                [unroll] for (int dx = -2; dx <= 2; dx++) {
                    if (dx == 0 && dy == 0) continue;
                    int2 sc = clamp(coord + int2(dx, dy), int2(0, 0), maxCoord);
                    float d = DepthTexture.Load(int3(sc, 0));
                    if (d >= 0.999999) continue;
                    float depthDiff = abs(d - centerDepth);
                    float wDepth = exp(-depthDiff * 180.0);
                    float wSpatial = kSpatial[abs(dx)] * kSpatial[abs(dy)];
                    float w = wSpatial * wDepth;
                    aoSum += RawAoTexture.Load(int3(sc, 0)) * w;
                    weightSum += w;
                }
            }

            float ao = saturate(aoSum / max(weightSum, 1e-5));
            return float4(color.rgb * ao, color.a);
        }
        """;
}
