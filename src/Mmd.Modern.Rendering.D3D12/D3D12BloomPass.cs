using System.Numerics;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Multi-level mip-pyramid bloom on display-domain scene color (opaque RGBA8 or premultiplied RGBA16F).
/// Scratch targets and descriptors retire through the recording slot fence.</summary>
internal sealed class D3D12BloomPass(D3D12GpuRuntime runtime, D3D12GpuCache cache)
{
    private const int MaxMipLevels = 5;

    internal sealed record Result(D3D12GpuTarget Output, int DrawCalls);

    private (byte[] Vertex, byte[] Prefilter, byte[] Downsample, byte[] Upsample, byte[] Composite)? shaders;
    private (byte[] Vertex, byte[] Prefilter, byte[] Downsample, byte[] Upsample, byte[] Composite)? alphaShaders;

    private static List<(int Width, int Height)> ComputeMipChain(int width, int height)
    {
        var levels = new List<(int Width, int Height)>(MaxMipLevels);
        int w = Math.Max(1, width / 2);
        int h = Math.Max(1, height / 2);
        for (int i = 0; i < MaxMipLevels; i++)
        {
            levels.Add((w, h));
            if (w <= 2 || h <= 2) break;
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }
        return levels;
    }

    internal static ulong Measure(D3D12GpuRuntime runtime, int width, int height, bool active, bool alphaCoverage = false)
    {
        if (!active) return 0;
        ulong Allocation(ResourceDescription description)
        {
            ulong bytes = runtime.Device.GetResourceAllocationInfo(0, [description]).SizeInBytes;
            if (bytes == 0 || bytes == ulong.MaxValue)
                throw new NotSupportedException("Device cannot estimate Bloom resources.");
            return bytes;
        }
        ulong sum = Allocation(ResourceDescription.Texture2D(
            alphaCoverage ? Format.R16G16B16A16_Float : Format.R8G8B8A8_UNorm,
            (uint)width, (uint)height, mipLevels: 1, flags: ResourceFlags.AllowRenderTarget));
        var mips = ComputeMipChain(width, height);
        for (int i = 0; i < mips.Count; i++)
        {
            ulong mipBytes = Allocation(ResourceDescription.Texture2D(
                Format.R16G16B16A16_Float, (uint)mips[i].Width, (uint)mips[i].Height,
                mipLevels: 1, flags: ResourceFlags.AllowRenderTarget));
            // Downsample uses every mip; upsample allocates one target per mip except the coarsest.
            sum = checked(sum + mipBytes * (i == mips.Count - 1 ? 1UL : 2UL));
        }
        ulong constants = Allocation(ResourceDescription.Buffer(1024 * 1024));
        return checked((sum + constants) * D3D12SsaaGiMemoryBudget.MaximumInFlightFrames);
    }

    internal Result Record(D3D12RecordingContext context, D3D12GpuTarget input, D3D12BloomOptions options)
    {
        options.Validate();
        int width = input.Width, height = input.Height;
        bool alphaCoverage = input.Format == Format.R16G16B16A16_Float;
        if (input.Depth || (!alphaCoverage && input.Format != Format.R8G8B8A8_UNorm) || !input.Initialized)
            throw new InvalidOperationException("Bloom requires initialized opaque RGBA8 or premultiplied RGBA16F scene color.");

        var device = context.Device;
        var commands = context.Commands;
        var compiled = GetShaders(alphaCoverage);
        var root = cache.Get("bloom:root", () => runtime.CreateRootSignature(
            new RootSignatureDescription(RootSignatureFlags.None,
                [new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
                 new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 2, 0)), ShaderVisibility.Pixel)],
                [new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
                { Filter = Filter.MinMagLinearMipPoint, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp }]), RootSignatureVersion.Version10));

        var mips = ComputeMipChain(width, height);
        int mipCount = mips.Count;
        // RTVs: mipCount down targets + (mipCount - 1) up targets + 1 final composite target.
        int rtvCount = mipCount + Math.Max(0, mipCount - 1) + 1;
        // SRVs: 2 slots per draw call; total draws = 1 (prefilter) + (mipCount - 1) (down) + (mipCount - 1) (up) + 1 (composite).
        int totalDraws = 2 + Math.Max(0, mipCount - 1) * 2;
        var rtv = runtime.Transients.Heap(new(DescriptorHeapType.RenderTargetView, rtvCount));
        var srv = runtime.Transients.Heap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, totalDraws * 2, DescriptorHeapFlags.ShaderVisible));
        int rtvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        int srvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        runtime.DescriptorHighWater = Math.Max(runtime.DescriptorHighWater, totalDraws * 2);

        int nextRtv = 0;
        D3D12GpuTarget Allocate(string name, int w, int h, Format format)
        {
            var resource = runtime.Transients.Resource(context, HeapType.Default,
                ResourceDescription.Texture2D(format, (uint)w, (uint)h, mipLevels: 1, flags: ResourceFlags.AllowRenderTarget),
                ResourceStates.RenderTarget);
            var handle = rtv.GetCPUDescriptorHandleForHeapStart().Offset(nextRtv++, rtvStride);
            device.CreateRenderTargetView(resource, null, handle);
            return runtime.Transients.Track(new(name, resource, handle, w, h, format, false));
        }

        var downTargets = new D3D12GpuTarget[mipCount];
        for (int i = 0; i < mipCount; i++)
            downTargets[i] = Allocate($"$bloom-down-{i}", mips[i].Width, mips[i].Height, Format.R16G16B16A16_Float);

        var upTargets = new D3D12GpuTarget[mipCount];
        upTargets[mipCount - 1] = downTargets[mipCount - 1];
        for (int i = mipCount - 2; i >= 0; i--)
            upTargets[i] = Allocate($"$bloom-up-{i}", mips[i].Width, mips[i].Height, Format.R16G16B16A16_Float);

        Format outputFormat = alphaCoverage ? Format.R16G16B16A16_Float : Format.R8G8B8A8_UNorm;
        var composite = Allocate("$bloom-composite", width, height, outputFormat);

        commands.UnsetRenderTargets();
        context.Transition(input, ResourceStates.PixelShaderResource);
        commands.SetDescriptorHeaps(srv);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        int nextSrv = 0;
        void DrawPass(string passKey, D3D12GpuTarget target, D3D12GpuTarget src0, D3D12GpuTarget src1, byte[] pixel, Vector4 p0, Vector4 p1)
        {
            int slot = nextSrv;
            nextSrv += 2;
            device.CreateShaderResourceView(src0.Resource, null, srv.GetCPUDescriptorHandleForHeapStart().Offset(slot, srvStride));
            device.CreateShaderResourceView(src1.Resource, null, srv.GetCPUDescriptorHandleForHeapStart().Offset(slot + 1, srvStride));

            var pipeline = cache.Get($"bloom:pso:{passKey}:{target.Format}", () =>
            {
                var pso = runtime.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
                {
                    RootSignature = root,
                    VertexShader = compiled.Vertex,
                    PixelShader = pixel,
                    BlendState = BlendDescription.Opaque,
                    SampleMask = uint.MaxValue,
                    RasterizerState = RasterizerDescription.CullNone,
                    DepthStencilState = DepthStencilDescription.None,
                    PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                    RenderTargetFormats = [target.Format],
                    SampleDescription = new(1, 0),
                });
                runtime.PipelineCreations++;
                return pso;
            });

            Span<Vector4> constants = [p0, p1];
            ulong address = context.UploadConstants(MemoryMarshal.AsBytes(constants));

            commands.RSSetViewport(0, 0, target.Width, target.Height, 0, 1);
            commands.RSSetScissorRect(new RawRect(0, 0, target.Width, target.Height));
            commands.OMSetRenderTargets(target.Handle);
            commands.SetGraphicsRootSignature(root);
            commands.SetPipelineState(pipeline);
            commands.SetGraphicsRootConstantBufferView(0, address);
            commands.SetGraphicsRootDescriptorTable(1, srv.GetGPUDescriptorHandleForHeapStart().Offset(slot, srvStride));
            commands.DrawInstanced(3, 1, 0, 0);
            target.Initialized = true;
            commands.UnsetRenderTargets();
        }

        // 1. Prefilter: extract soft-kneed bright regions into downTargets[0].
        Vector4 bloomParams = new(options.Threshold, options.SoftKnee, options.Intensity, options.Scatter);
        DrawPass("prefilter", downTargets[0], input, input, compiled.Prefilter,
            new(1f / width, 1f / height, width, height), bloomParams);

        // 2. Downsample chain.
        for (int i = 1; i < mipCount; i++)
        {
            var prev = downTargets[i - 1];
            context.Transition(prev, ResourceStates.PixelShaderResource);
            DrawPass("downsample", downTargets[i], prev, prev, compiled.Downsample,
                new(1f / prev.Width, 1f / prev.Height, prev.Width, prev.Height), bloomParams);
        }

        // 3. Upsample chain.
        context.Transition(downTargets[mipCount - 1], ResourceStates.PixelShaderResource);
        for (int i = mipCount - 2; i >= 0; i--)
        {
            var low = upTargets[i + 1];
            var high = downTargets[i];
            context.Transition(low, ResourceStates.PixelShaderResource);
            context.Transition(high, ResourceStates.PixelShaderResource);
            DrawPass("upsample", upTargets[i], high, low, compiled.Upsample,
                new(1f / low.Width, 1f / low.Height, low.Width, low.Height), bloomParams);
        }

        // 4. Composite bloom into final scene target.
        context.Transition(upTargets[0], ResourceStates.PixelShaderResource);
        DrawPass("composite", composite, input, upTargets[0], compiled.Composite,
            new(1f / width, 1f / height, width, height), bloomParams);

        return new(composite, totalDraws);
    }

    internal static IEnumerable<D3D12FxcShaderRequest> GetPrewarmRequests(bool alphaCoverage)
    {
        const ShaderFlags flags = ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3;
        if (alphaCoverage)
        {
            yield return new(AlphaSource, "VSMain", "bloom-alpha.hlsl", "vs_5_1", flags);
            yield return new(AlphaSource, "PSPrefilterAlpha", "bloom-alpha.hlsl", "ps_5_1", flags);
            yield return new(AlphaSource, "PSDownsample", "bloom-alpha.hlsl", "ps_5_1", flags);
            yield return new(AlphaSource, "PSUpsample", "bloom-alpha.hlsl", "ps_5_1", flags);
            yield return new(AlphaSource, "PSCompositeAlpha", "bloom-alpha.hlsl", "ps_5_1", flags);
        }
        else
        {
            yield return new(Source, "VSMain", "bloom-native.hlsl", "vs_5_1", flags);
            yield return new(Source, "PSPrefilter", "bloom-native.hlsl", "ps_5_1", flags);
            yield return new(Source, "PSDownsample", "bloom-native.hlsl", "ps_5_1", flags);
            yield return new(Source, "PSUpsample", "bloom-native.hlsl", "ps_5_1", flags);
            yield return new(Source, "PSComposite", "bloom-native.hlsl", "ps_5_1", flags);
        }
    }

    private (byte[] Vertex, byte[] Prefilter, byte[] Downsample, byte[] Upsample, byte[] Composite) GetShaders(bool alphaCoverage)
    {
        const ShaderFlags flags = ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3;
        if (alphaCoverage)
        {
            if (alphaShaders is { } existing) return existing;
            var created = (
                D3D12FxcShaderCache.Compile(AlphaSource, "VSMain", "bloom-alpha.hlsl", "vs_5_1", flags),
                D3D12FxcShaderCache.Compile(AlphaSource, "PSPrefilterAlpha", "bloom-alpha.hlsl", "ps_5_1", flags),
                D3D12FxcShaderCache.Compile(AlphaSource, "PSDownsample", "bloom-alpha.hlsl", "ps_5_1", flags),
                D3D12FxcShaderCache.Compile(AlphaSource, "PSUpsample", "bloom-alpha.hlsl", "ps_5_1", flags),
                D3D12FxcShaderCache.Compile(AlphaSource, "PSCompositeAlpha", "bloom-alpha.hlsl", "ps_5_1", flags));
            alphaShaders = created;
            runtime.ShaderCompilations += 5;
            return created;
        }
        if (shaders is { } compiled) return compiled;
        compiled = (
            D3D12FxcShaderCache.Compile(Source, "VSMain", "bloom-native.hlsl", "vs_5_1", flags),
            D3D12FxcShaderCache.Compile(Source, "PSPrefilter", "bloom-native.hlsl", "ps_5_1", flags),
            D3D12FxcShaderCache.Compile(Source, "PSDownsample", "bloom-native.hlsl", "ps_5_1", flags),
            D3D12FxcShaderCache.Compile(Source, "PSUpsample", "bloom-native.hlsl", "ps_5_1", flags),
            D3D12FxcShaderCache.Compile(Source, "PSComposite", "bloom-native.hlsl", "ps_5_1", flags));
        shaders = compiled;
        runtime.ShaderCompilations += 5;
        return compiled;
    }

    internal const string AlphaSource = Source + """

        float3 BloomStraight(float4 premultiplied) {
            return premultiplied.a > 0 ? saturate(premultiplied.rgb / premultiplied.a) : 0;
        }
        float3 SamplePrefilterTapAlpha(float2 uv) {
            float4 s = Source0.SampleLevel(LinearClamp, uv, 0);
            return BloomStraight(s) * s.a;
        }
        float4 PSPrefilterAlpha(VSOut input) : SV_Target {
            float2 uv = input.Uv;
            float2 d = TexelSize.xy * 0.5;
            float3 c0 = SamplePrefilterTapAlpha(uv + float2(-d.x, -d.y));
            float3 c1 = SamplePrefilterTapAlpha(uv + float2( d.x, -d.y));
            float3 c2 = SamplePrefilterTapAlpha(uv + float2(-d.x,  d.y));
            float3 c3 = SamplePrefilterTapAlpha(uv + float2( d.x,  d.y));
            float3 avg = (c0 + c1 + c2 + c3) * 0.25;
            return float4(ExtractBright(avg), 1.0);
        }
        float4 PSCompositeAlpha(VSOut input) : SV_Target {
            float4 center = Source0.Load(int3(int2(input.Position.xy), 0));
            if (center.a <= 0) return 0;
            float3 original = BloomStraight(center);
            float3 bloom = max(Source1.SampleLevel(LinearClamp, input.Uv, 0).rgb, 0);
            float3 result = CompositeBloom(original, bloom);
            return float4(result * center.a, center.a);
        }
        """;

    internal const string Source = """
        cbuffer BloomConstants : register(b0) {
            float4 TexelSize;   // 1/srcW, 1/srcH, srcW, srcH
            float4 BloomParams; // threshold, knee, intensity, scatter
        };
        Texture2D<float4> Source0 : register(t0);
        Texture2D<float4> Source1 : register(t1);
        SamplerState LinearClamp : register(s0);

        struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
        VSOut VSMain(uint id : SV_VertexID) {
            VSOut o;
            o.Uv = float2((id << 1) & 2, id & 2);
            o.Position = float4(o.Uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return o;
        }

        float3 ExtractBright(float3 color) {
            // Decode approximate sRGB to linear-ish before thresholding so midtones stay sharp
            // and only genuinely bright accents bloom cleanly.
            float3 lin = color * color;
            float br = max(lin.r, max(lin.g, lin.b));
            float thr = BloomParams.x * BloomParams.x;
            float knee = max(thr * BloomParams.y, 1e-4);
            float soft = br - thr + knee;
            soft = clamp(soft, 0.0, 2.0 * knee);
            soft = (soft * soft) / (4.0 * knee + 1e-5);
            float contribution = max(soft, br - thr) / max(br, 1e-5);
            return lin * saturate(contribution);
        }

        float3 CompositeBloom(float3 original, float3 bloom) {
            float3 linOrig = original * original;
            float3 added = bloom * BloomParams.z;
            // Hue-preserving soft highlight roll-off prevents bright accents from clipping into flat white.
            float3 combined = linOrig + added * (1.0 - 0.35 * saturate(linOrig));
            return sqrt(saturate(combined));
        }

        float4 PSPrefilter(VSOut input) : SV_Target {
            float2 uv = input.Uv;
            float2 d = TexelSize.xy * 0.5;
            float3 c0 = Source0.SampleLevel(LinearClamp, uv + float2(-d.x, -d.y), 0).rgb;
            float3 c1 = Source0.SampleLevel(LinearClamp, uv + float2( d.x, -d.y), 0).rgb;
            float3 c2 = Source0.SampleLevel(LinearClamp, uv + float2(-d.x,  d.y), 0).rgb;
            float3 c3 = Source0.SampleLevel(LinearClamp, uv + float2( d.x,  d.y), 0).rgb;
            float3 avg = (c0 + c1 + c2 + c3) * 0.25;
            return float4(ExtractBright(avg), 1.0);
        }

        float4 PSDownsample(VSOut input) : SV_Target {
            float2 uv = input.Uv;
            float2 d = TexelSize.xy;
            float3 c  = Source0.SampleLevel(LinearClamp, uv, 0).rgb * 4.0;
            c += Source0.SampleLevel(LinearClamp, uv + float2(-d.x, -d.y), 0).rgb;
            c += Source0.SampleLevel(LinearClamp, uv + float2( d.x, -d.y), 0).rgb;
            c += Source0.SampleLevel(LinearClamp, uv + float2(-d.x,  d.y), 0).rgb;
            c += Source0.SampleLevel(LinearClamp, uv + float2( d.x,  d.y), 0).rgb;
            c += Source0.SampleLevel(LinearClamp, uv + float2(-d.x * 2.0, 0), 0).rgb * 0.5;
            c += Source0.SampleLevel(LinearClamp, uv + float2( d.x * 2.0, 0), 0).rgb * 0.5;
            c += Source0.SampleLevel(LinearClamp, uv + float2(0, -d.y * 2.0), 0).rgb * 0.5;
            c += Source0.SampleLevel(LinearClamp, uv + float2(0,  d.y * 2.0), 0).rgb * 0.5;
            return float4(c / 10.0, 1.0);
        }

        float4 PSUpsample(VSOut input) : SV_Target {
            float2 uv = input.Uv;
            float2 d = TexelSize.xy;
            float3 low =
                  Source1.SampleLevel(LinearClamp, uv + float2(-d.x, -d.y), 0).rgb * 1.0
                + Source1.SampleLevel(LinearClamp, uv + float2(   0, -d.y), 0).rgb * 2.0
                + Source1.SampleLevel(LinearClamp, uv + float2( d.x, -d.y), 0).rgb * 1.0
                + Source1.SampleLevel(LinearClamp, uv + float2(-d.x,    0), 0).rgb * 2.0
                + Source1.SampleLevel(LinearClamp, uv,                      0).rgb * 4.0
                + Source1.SampleLevel(LinearClamp, uv + float2( d.x,    0), 0).rgb * 2.0
                + Source1.SampleLevel(LinearClamp, uv + float2(-d.x,  d.y), 0).rgb * 1.0
                + Source1.SampleLevel(LinearClamp, uv + float2(   0,  d.y), 0).rgb * 2.0
                + Source1.SampleLevel(LinearClamp, uv + float2( d.x,  d.y), 0).rgb * 1.0;
            low /= 16.0;
            float3 high = Source0.SampleLevel(LinearClamp, uv, 0).rgb;
            return float4(lerp(high, low, BloomParams.w), 1.0);
        }

        float4 PSComposite(VSOut input) : SV_Target {
            float4 orig = Source0.Load(int3(int2(input.Position.xy), 0));
            float3 bloom = max(Source1.SampleLevel(LinearClamp, input.Uv, 0).rgb, 0);
            return float4(CompositeBloom(orig.rgb, bloom), orig.a);
        }
        """;
}
