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

/// <summary>Screen-space constant-pixel-width depth rim light reusing 1x or MSAA-resolved depth.
/// Supports opaque RGBA8 and premultiplied RGBA16F (StraightAlpha coverage-preserving).
/// Scratch targets and descriptors retire through the recording slot fence.</summary>
internal sealed class D3D12DepthRimPass(D3D12GpuRuntime runtime, D3D12GpuCache cache)
{
    private static readonly Vector3 DefaultRimColor = new(1f, 0.96f, 0.90f);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DepthRimConstants(
        Matrix4x4 InvViewProjection,
        Matrix4x4 ViewProjection,
        Vector4 CameraEyeAndOrtho, // xyz = camera eye, w = 1 if orthographic else 0
        Vector4 CameraForward,     // xyz = normalize(target - eye), w = 0
        Vector4 LightDirAndBias,   // xyz = normalize(lightDirection), w = lightBias
        Vector4 Metrics,           // width, height, 1/width, 1/height
        Vector4 Params,            // strength, widthPixels, depthThreshold, 0
        Vector4 RimColor);         // rgb, 0

    private (byte[] Vertex, byte[] Pixel)? shaders;
    private (byte[] Vertex, byte[] Pixel)? alphaShaders;

    internal static ulong Measure(
        D3D12GpuRuntime runtime,
        int width,
        int height,
        bool active,
        bool alphaCoverage = false,
        bool msaaResolve = false)
    {
        if (!active) return 0;
        ulong Allocation(ResourceDescription description)
        {
            ulong bytes = runtime.Device.GetResourceAllocationInfo(0, [description]).SizeInBytes;
            if (bytes == 0 || bytes == ulong.MaxValue)
                throw new NotSupportedException("Device cannot estimate Depth Rim resources.");
            return bytes;
        }

        ulong colorOut = Allocation(ResourceDescription.Texture2D(
            alphaCoverage ? Format.R16G16B16A16_Float : Format.R8G8B8A8_UNorm,
            (uint)width, (uint)height, mipLevels: 1, flags: ResourceFlags.AllowRenderTarget));
        ulong resolve = msaaResolve
            ? Allocation(ResourceDescription.Texture2D(
                Format.R32_Float, (uint)width, (uint)height, mipLevels: 1, flags: ResourceFlags.AllowRenderTarget))
            : 0UL;
        ulong constants = Allocation(ResourceDescription.Buffer(1024 * 1024));
        return checked((colorOut + resolve + constants) * D3D12SsaaGiMemoryBudget.MaximumInFlightFrames);
    }

    internal D3D12GpuTarget Record(
        D3D12RecordingContext context,
        D3D12GpuTarget colorInput,
        D3D12GpuTarget depthInput,
        SceneCamera camera,
        Vector3 lightDirection,
        D3D12DepthRimOptions options,
        D3D12GpuTarget? reusableOutput = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!ReferenceEquals(context.Runtime, runtime) || !runtime.IsRecording)
            throw new InvalidOperationException("Depth rim requires an open recording frame on the owning runtime.");
        bool alphaCoverage = colorInput.Format == Format.R16G16B16A16_Float;
        if (colorInput.Depth || (!alphaCoverage && colorInput.Format != Format.R8G8B8A8_UNorm) || !colorInput.Initialized)
            throw new InvalidOperationException("Depth rim requires initialized 1x RGBA8 or RGBA16F color.");
        if (depthInput.Width != colorInput.Width || depthInput.Height != colorInput.Height)
            throw new InvalidOperationException("Depth rim requires 1x depth matching color dimensions.");
        if (!options.IsActive) return colorInput;

        int width = colorInput.Width, height = colorInput.Height;
        var device = context.Device;
        var commands = context.Commands;
        var compiled = GetShaders(alphaCoverage);

        var root = cache.Get("depth-rim:root", () => runtime.CreateRootSignature(
            new RootSignatureDescription(RootSignatureFlags.None,
                [new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.Pixel),
                 new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 2, 0)), ShaderVisibility.Pixel)],
                [new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
                {
                    Filter = Filter.MinMagMipPoint,
                    AddressU = TextureAddressMode.Clamp,
                    AddressV = TextureAddressMode.Clamp,
                    AddressW = TextureAddressMode.Clamp,
                }]),
            RootSignatureVersion.Version10));

        var srvHeap = runtime.Transients.Heap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 2, DescriptorHeapFlags.ShaderVisible));
        int srvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        runtime.DescriptorHighWater = Math.Max(runtime.DescriptorHighWater, 2);

        D3D12GpuTarget output;
        if (reusableOutput is not null)
        {
            output = reusableOutput;
            if (output.Width != width || output.Height != height || output.Format != colorInput.Format)
                throw new InvalidOperationException("Reusable depth rim target must match current frame dimensions and color format.");
            context.Transition(output, ResourceStates.RenderTarget);
        }
        else
        {
            var rtvHeap = runtime.Transients.Heap(new(DescriptorHeapType.RenderTargetView, 1));
            var outResource = runtime.Transients.Resource(context, HeapType.Default,
                ResourceDescription.Texture2D(colorInput.Format, (uint)width, (uint)height, mipLevels: 1, flags: ResourceFlags.AllowRenderTarget),
                ResourceStates.RenderTarget);
            var outRtv = rtvHeap.GetCPUDescriptorHandleForHeapStart();
            device.CreateRenderTargetView(outResource, null, outRtv);
            output = runtime.Transients.Track(new D3D12GpuTarget("$depth-rim", outResource, outRtv, width, height, colorInput.Format, false));
        }

        Format depthSrvFormat = depthInput.Depth ? D3D12ScreenSpaceAoPass.DepthSrvFormat(depthInput.Format) : depthInput.Format;
        device.CreateShaderResourceView(colorInput.Resource, null, srvHeap.GetCPUDescriptorHandleForHeapStart());
        device.CreateShaderResourceView(depthInput.Resource, new ShaderResourceViewDescription
        {
            Format = depthSrvFormat,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 },
        }, srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(1, srvStride));

        string psoKey = $"depth-rim:pso:{(alphaCoverage ? "alpha" : "opaque")}:{colorInput.Format}";
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
        var camForward = camera.Target - camera.Eye;
        camForward = camForward.LengthSquared() > 1e-8f ? Vector3.Normalize(camForward) : Vector3.UnitZ;
        float lightLenSq = lightDirection.LengthSquared();
        var lightDir = float.IsFinite(lightLenSq) && lightLenSq > 1e-8f ? Vector3.Normalize(lightDirection) : Vector3.UnitY;
        var rimColor = options.Color ?? DefaultRimColor;

        ulong cbAddress = context.UploadConstants(new DepthRimConstants(
            invVp,
            vp,
            new Vector4(camera.Eye, isOrtho ? 1f : 0f),
            new Vector4(camForward, 0f),
            new Vector4(lightDir, options.LightBias),
            new Vector4(width, height, 1f / width, 1f / height),
            new Vector4(options.Strength, options.WidthPixels, options.DepthThreshold, 0f),
            new Vector4(rimColor, 0f)));

        commands.UnsetRenderTargets();
        context.Transition(colorInput, ResourceStates.PixelShaderResource);
        context.Transition(depthInput, ResourceStates.PixelShaderResource);

        commands.SetDescriptorHeaps(srvHeap);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.RSSetViewport(0, 0, width, height, 0, 1);
        commands.RSSetScissorRect(new RawRect(0, 0, width, height));
        commands.SetGraphicsRootSignature(root);
        commands.SetPipelineState(pipeline);
        commands.SetGraphicsRootConstantBufferView(0, cbAddress);
        commands.SetGraphicsRootDescriptorTable(1, srvHeap.GetGPUDescriptorHandleForHeapStart());
        commands.OMSetRenderTargets(output.Handle);
        commands.DrawInstanced(3, 1, 0, 0);
        commands.UnsetRenderTargets();
        output.Initialized = true;

        return output;
    }

    internal static IEnumerable<D3D12FxcShaderRequest> GetPrewarmRequests(bool alphaCoverage)
    {
        const ShaderFlags flags = ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3;
        if (alphaCoverage)
        {
            yield return new(AlphaSource, "VSMain", "depth-rim-alpha.hlsl", "vs_5_1", flags);
            yield return new(AlphaSource, "PSMainAlpha", "depth-rim-alpha.hlsl", "ps_5_1", flags);
        }
        else
        {
            yield return new(Source, "VSMain", "depth-rim.hlsl", "vs_5_1", flags);
            yield return new(Source, "PSMain", "depth-rim.hlsl", "ps_5_1", flags);
        }
        foreach (var req in D3D12ScreenSpaceAoPass.GetResolvePrewarmRequests())
            yield return req;
    }

    private (byte[] Vertex, byte[] Pixel) GetShaders(bool alphaCoverage)
    {
        const ShaderFlags flags = ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3;
        if (alphaCoverage)
        {
            if (alphaShaders is { } existing) return existing;
            var created = (
                D3D12FxcShaderCache.Compile(AlphaSource, "VSMain", "depth-rim-alpha.hlsl", "vs_5_1", flags),
                D3D12FxcShaderCache.Compile(AlphaSource, "PSMainAlpha", "depth-rim-alpha.hlsl", "ps_5_1", flags));
            alphaShaders = created;
            runtime.ShaderCompilations += 2;
            return created;
        }
        if (shaders is { } compiled) return compiled;
        compiled = (
            D3D12FxcShaderCache.Compile(Source, "VSMain", "depth-rim.hlsl", "vs_5_1", flags),
            D3D12FxcShaderCache.Compile(Source, "PSMain", "depth-rim.hlsl", "ps_5_1", flags));
        shaders = compiled;
        runtime.ShaderCompilations += 2;
        return compiled;
    }

    internal const string AlphaSource = Source + """

        float4 PSMainAlpha(VSOut input) : SV_Target {
            int2 maxCoord = max(int2(Metrics.xy) - 1, int2(0, 0));
            int2 coord = clamp(int2(input.Position.xy), int2(0, 0), maxCoord);
            float4 center = ColorTexture.Load(int3(coord, 0));
            if (center.a <= 0.0) return float4(0, 0, 0, 0);
            float centerDepth = DepthTexture.Load(int3(coord, 0));
            if (centerDepth >= 0.999999) return center;
            float3 straight = saturate(center.rgb / center.a);
            float rim = ComputeRimMask(coord, (float2(coord) + 0.5) * Metrics.zw, centerDepth);
            float3 result = ApplyRimColor(straight, rim);
            return float4(result * center.a, center.a);
        }
        """;

    internal const string Source = """
        cbuffer DepthRimConstants : register(b0) {
            row_major float4x4 InvViewProjection;
            row_major float4x4 ViewProjection;
            float4 CameraEyeAndOrtho; // xyz = eye, w = 1 if ortho else 0
            float4 CameraForward;     // xyz = normalize(target - eye), w = 0
            float4 LightDirAndBias;   // xyz = lightDir, w = lightBias
            float4 Metrics;           // width, height, 1/width, 1/height
            float4 Params;            // strength, widthPixels, depthThreshold, 0
            float4 RimColor;          // rgb, 0
        };
        Texture2D<float4> ColorTexture : register(t0);
        Texture2D<float> DepthTexture : register(t1);
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

        float EvalDepthJump(float centerDist, float zSample, float zOpp, bool isOrtho, float threshold) {
            float expectedZ = centerDist;
            if (isOrtho) {
                expectedZ = max(2.0 * centerDist - zOpp, centerDist);
            } else if (centerDist > 1e-5 && zOpp > 1e-5) {
                // Screen-space reciprocal view depth (1/z) is linear across a flat perspective plane,
                // preventing false rim on steep tilted surfaces even at wide WidthPixels.
                float predInvZ = 2.0 / centerDist - 1.0 / zOpp;
                if (predInvZ <= 1e-6) return 0.0;
                expectedZ = max(1.0 / predInvZ, centerDist);
            }
            return saturate((zSample - expectedZ - threshold * 0.5) / threshold);
        }

        float EvalRingRim(
            int2 coord,
            int2 size,
            float centerDist,
            float threshold,
            float radiusPixels,
            float2 lightUvDir,
            float hasScreenLight)
        {
            static const float2 kDirs[4] = {
                float2(1.0, 0.0),
                float2(0.0, 1.0),
                float2(0.70710678, 0.70710678),
                float2(-0.70710678, 0.70710678)
            };

            bool isOrtho = CameraEyeAndOrtho.w > 0.5;
            float maxRim = 0.0;
            [unroll] for (int i = 0; i < 4; i++) {
                float2 d = kDirs[i];
                int2 step = int2(d * radiusPixels + sign(d) * 0.5);
                if (step.x == 0 && step.y == 0) continue;

                int2 coordP = coord + step;
                int2 coordM = coord - step;

                bool outP = any(coordP < 0) || any(coordP >= size);
                bool outM = any(coordM < 0) || any(coordM >= size);

                float rawP = outP ? 1.0 : DepthTexture.Load(int3(coordP, 0));
                float rawM = outM ? 1.0 : DepthTexture.Load(int3(coordM, 0));

                bool bgP = !outP && rawP >= 0.999999;
                bool bgM = !outM && rawM >= 0.999999;

                float2 uvP = (float2(coordP) + 0.5) * Metrics.zw;
                float2 uvM = (float2(coordM) + 0.5) * Metrics.zw;

                float zP = (outP || bgP)
                    ? (centerDist + threshold * 8.0)
                    : dot(ReconstructWorldPos(uvP, rawP) - CameraEyeAndOrtho.xyz, CameraForward.xyz);
                float zM = (outM || bgM)
                    ? (centerDist + threshold * 8.0)
                    : dot(ReconstructWorldPos(uvM, rawM) - CameraEyeAndOrtho.xyz, CameraForward.xyz);

                float discP = (outP || (!bgP && outM)) ? 0.0 : (bgP ? 1.0 : EvalDepthJump(centerDist, zP, zM, isOrtho, threshold));
                float discM = (outM || (!bgM && outP)) ? 0.0 : (bgM ? 1.0 : EvalDepthJump(centerDist, zM, zP, isOrtho, threshold));

                float alignP = lerp(0.75, smoothstep(-0.25, 0.45, dot(d, lightUvDir)), hasScreenLight);
                float alignM = lerp(0.75, smoothstep(-0.25, 0.45, dot(-d, lightUvDir)), hasScreenLight);

                float wP = lerp(1.0, alignP, LightDirAndBias.w);
                float wM = lerp(1.0, alignM, LightDirAndBias.w);

                maxRim = max(maxRim, max(discP * wP, discM * wM));
            }
            return maxRim;
        }

        float ComputeRimMask(int2 coord, float2 uv, float centerDepth) {
            float3 centerPos = ReconstructWorldPos(uv, centerDepth);
            float centerDist = dot(centerPos - CameraEyeAndOrtho.xyz, CameraForward.xyz);
            float distScale = CameraEyeAndOrtho.w > 0.5 ? 1.0 : max(abs(centerDist) * 0.05, 0.25);
            float threshold = max(Params.z * distScale, 1e-4);

            float stepLen = max(abs(centerDist) * 0.05, 0.1);
            float4 clipC = mul(float4(centerPos, 1.0), ViewProjection);
            float4 clipL = mul(float4(centerPos + LightDirAndBias.xyz * stepLen, 1.0), ViewProjection);
            float2 ndcC = clipC.xy / max(abs(clipC.w), 1e-6) * (clipC.w < 0.0 ? -1.0 : 1.0);
            float2 ndcL = clipL.xy / max(abs(clipL.w), 1e-6) * (clipL.w < 0.0 ? -1.0 : 1.0);
            float2 pxDelta = float2(ndcL.x - ndcC.x, -(ndcL.y - ndcC.y)) * Metrics.xy * 0.5;
            float pxLen = length(pxDelta);
            float2 lightUvDir = pxLen > 1e-4 ? (pxDelta / pxLen) : float2(0.0, 0.0);
            float hasScreenLight = saturate(pxLen * 0.5);

            int2 size = max(int2(Metrics.xy), int2(1, 1));
            float fullRadius = Params.y;
            float innerRadius = max(Params.y * 0.45, 0.5);
            float edgeOuter = EvalRingRim(coord, size, centerDist, threshold, fullRadius, lightUvDir, hasScreenLight);
            float edgeInner = EvalRingRim(coord, size, centerDist, threshold, innerRadius, lightUvDir, hasScreenLight);
            return saturate(edgeOuter * 0.45 + edgeInner * 0.55);
        }

        float3 ApplyRimColor(float3 baseColor, float rimMask) {
            float3 added = RimColor.rgb * (rimMask * Params.x);
            return saturate(baseColor + added * (1.0 - 0.25 * baseColor));
        }

        float4 PSMain(VSOut input) : SV_Target {
            int2 maxCoord = max(int2(Metrics.xy) - 1, int2(0, 0));
            int2 coord = clamp(int2(input.Position.xy), int2(0, 0), maxCoord);
            float4 center = ColorTexture.Load(int3(coord, 0));
            float centerDepth = DepthTexture.Load(int3(coord, 0));
            if (centerDepth >= 0.999999) return center;
            float rim = ComputeRimMask(coord, (float2(coord) + 0.5) * Metrics.zw, centerDepth);
            return float4(ApplyRimColor(center.rgb, rim), center.a);
        }
        """;
}
