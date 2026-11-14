using System.Numerics;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Native Diffusion with separate opaque and coverage-preserving variants.
/// Scratch and descriptors retire through the recording slot fence.</summary>
internal sealed class D3D12DiffusionPass(D3D12GpuRuntime runtime, D3D12GpuCache cache)
{
    internal sealed record Result(D3D12GpuTarget Horizontal, D3D12GpuTarget Output);
    private (byte[] Vertex, byte[] Horizontal, byte[] Composite)? shaders;
    private (byte[] Vertex, byte[] Horizontal, byte[] Composite)? alphaShaders;
    internal Result Record(D3D12RecordingContext context, D3D12GpuTarget input, D3D12DiffusionOptions options)
    {
        int width = input.Width, height = input.Height;
        bool alphaCoverage = input.Format == Format.R16G16B16A16_Float;
        if (input.Depth || (!alphaCoverage && input.Format != Format.R8G8B8A8_UNorm) || !input.Initialized)
            throw new InvalidOperationException("Diffusion requires initialized opaque RGBA8 or premultiplied RGBA16F scene color.");
        var device = context.Device; var commands = context.Commands;
        var compiled = GetShaders(alphaCoverage);
        var root = cache.Get("diffusion:root", () => device.CreateRootSignature<ID3D12RootSignature>(
            new RootSignatureDescription(RootSignatureFlags.None,
                [new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
                 new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 2, 0)), ShaderVisibility.Pixel)],
                [new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
                { Filter = Filter.MinMagLinearMipPoint, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp }]), RootSignatureVersion.Version10));
        var rtv = context.Own(device.CreateDescriptorHeap(new(DescriptorHeapType.RenderTargetView, 2)));
        var srv = context.Own(device.CreateDescriptorHeap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 4, DescriptorHeapFlags.ShaderVisible)));
        int rtvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        int srvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        runtime.DescriptorHighWater = Math.Max(runtime.DescriptorHighWater, 4);
        D3D12GpuTarget Allocate(string name, int index, Format format)
        {
            var resource = context.Own(device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Texture2D(format, (uint)width, (uint)height, mipLevels: 1, flags: ResourceFlags.AllowRenderTarget), ResourceStates.RenderTarget));
            var handle = rtv.GetCPUDescriptorHandleForHeapStart().Offset(index, rtvStride);
            device.CreateRenderTargetView(resource, null, handle);
            return new(name, resource, handle, width, height, format, false);
        }
        // Preserve both original UNorm8 stores. Alpha's distinct contract keeps low
        // coverage and energy in FP16 until the existing final straight resolve.
        Format format = alphaCoverage ? Format.R16G16B16A16_Float : Format.R8G8B8A8_UNorm;
        var horizontal = Allocate("$diffusion-x", 0, format);
        var composite = Allocate("$diffusion-y", 1, format);
        // Complete both two-SRV tables, including the unused t1 of the X entry.
        device.CreateShaderResourceView(input.Resource, null, srv.GetCPUDescriptorHandleForHeapStart());
        device.CreateShaderResourceView(input.Resource, null, srv.GetCPUDescriptorHandleForHeapStart().Offset(1, srvStride));
        device.CreateShaderResourceView(input.Resource, null, srv.GetCPUDescriptorHandleForHeapStart().Offset(2, srvStride));
        device.CreateShaderResourceView(horizontal.Resource, null, srv.GetCPUDescriptorHandleForHeapStart().Offset(3, srvStride));
        commands.UnsetRenderTargets(); context.Transition(input, ResourceStates.PixelShaderResource);
        commands.SetDescriptorHeaps(srv);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.RSSetViewport(0, 0, width, height, 0, 1);
        commands.RSSetScissorRect(new RawRect(0, 0, width, height));
        Vector4[] constants = [new(width, height, options.Strength, options.Extent)];
        ulong address = context.UploadConstants(MemoryMarshal.AsBytes(constants.AsSpan()));
        void Draw(string pass, D3D12GpuTarget target, int descriptor, byte[] pixel)
        {
            var pipeline = cache.Get($"diffusion:pso:{pass}:{target.Format}", () =>
            {
                var pso = device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
                {
                    RootSignature = root, VertexShader = compiled.Vertex, PixelShader = pixel,
                    BlendState = BlendDescription.Opaque, SampleMask = uint.MaxValue,
                    RasterizerState = RasterizerDescription.CullNone, DepthStencilState = DepthStencilDescription.None,
                    PrimitiveTopologyType = PrimitiveTopologyType.Triangle, RenderTargetFormats = [target.Format], SampleDescription = new(1, 0)
                }); runtime.PipelineCreations++; return pso;
            });
            commands.OMSetRenderTargets(target.Handle);
            commands.SetGraphicsRootSignature(root); commands.SetPipelineState(pipeline);
            commands.SetGraphicsRootConstantBufferView(0, address);
            commands.SetGraphicsRootDescriptorTable(1, srv.GetGPUDescriptorHandleForHeapStart().Offset(descriptor, srvStride));
            commands.DrawInstanced(3, 1, 0, 0); target.Initialized = true; commands.UnsetRenderTargets();
        }
        Draw("x", horizontal, 0, compiled.Horizontal);
        context.Transition(horizontal, ResourceStates.PixelShaderResource);
        Draw("y", composite, 2, compiled.Composite);
        return new(horizontal, composite);
    }

    private (byte[] Vertex, byte[] Horizontal, byte[] Composite) GetShaders(bool alphaCoverage)
    {
        if (alphaCoverage)
        {
            if (alphaShaders is { } existing) return existing;
            const ShaderFlags alphaFlags = ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3;
            var created = (Compiler.Compile(AlphaSource, "VSMain", "diffusion-alpha.hlsl", "vs_5_1", alphaFlags).ToArray(),
                Compiler.Compile(AlphaSource, "PSHorizontalAlpha", "diffusion-alpha.hlsl", "ps_5_1", alphaFlags).ToArray(),
                Compiler.Compile(AlphaSource, "PSCompositeAlpha", "diffusion-alpha.hlsl", "ps_5_1", alphaFlags).ToArray());
            alphaShaders = created; runtime.ShaderCompilations += 3; return created;
        }
        if (shaders is { } compiled) return compiled;
        const ShaderFlags flags = ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3;
        compiled = (Compiler.Compile(Source, "VSMain", "diffusion-native.hlsl", "vs_5_1", flags).ToArray(),
            Compiler.Compile(Source, "PSHorizontal", "diffusion-native.hlsl", "ps_5_1", flags).ToArray(),
            Compiler.Compile(Source, "PSComposite", "diffusion-native.hlsl", "ps_5_1", flags).ToArray());
        shaders = compiled; runtime.ShaderCompilations += 3; return compiled;
    }

    // MDA compositable-alpha policy: normalize blur energy by covered samples,
    // retain exactly the center coverage, and never generate an exterior halo.
    internal const string AlphaSource = Source + """

        float3 StraightDisplay(float4 premultiplied) {
            return premultiplied.a > 0 ? saturate(premultiplied.rgb / premultiplied.a) : 0;
        }
        float4 PSHorizontalAlpha(float4 position : SV_Position) : SV_Target {
            float2 uv = position.xy / Parameters.xy;
            float stepX = Parameters.w * Parameters.y / Parameters.x;
            float4 sum = 0; float total = 0;
            [unroll] for (int i = -7; i <= 7; i++) {
                float w = Weight(i);
                float4 sampled = Original.SampleLevel(LinearClamp, uv + float2(i * stepX, 0), 0);
                float3 straight = StraightDisplay(sampled);
                sum += float4(sampled.a * straight * straight, sampled.a) * w;
                total += w;
            }
            return sum / total;
        }
        float4 PSCompositeAlpha(float4 position : SV_Position) : SV_Target {
            float4 center = Original.Load(int3(int2(position.xy), 0));
            if (center.a <= 0) return 0;
            float2 uv = position.xy / Parameters.xy;
            float4 sum = 0;
            [unroll] for (int i = -7; i <= 7; i++)
                sum += Horizontal.SampleLevel(LinearClamp, uv + float2(0, i * Parameters.w), 0) * Weight(i);
            float3 original = StraightDisplay(center);
            float3 squared = original * original;
            float3 blurred = sum.a > 0 ? sum.rgb / sum.a : squared;
            float3 screen = blurred + squared - blurred * squared;
            if (dot(squared, float3(.29, .59, .12)) > dot(screen, float3(.29, .59, .12))) screen = squared;
            return float4(lerp(original, screen, Parameters.z) * center.a, center.a);
        }
        """;

    // Independent implementation of the audited 15-tap math, not an FX/MME interpreter.
    internal const string Source = """
        cbuffer DiffusionConstants : register(b0) { float4 Parameters; };
        Texture2D<float4> Original : register(t0);
        Texture2D<float4> Horizontal : register(t1);
        SamplerState LinearClamp : register(s0);
        float4 VSMain(uint id : SV_VertexID) : SV_Position {
            float2 uv = float2((id << 1) & 2, id & 2);
            return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
        }
        float Weight(int tap) {
            float x = (float)tap / 3.5;
            return exp(-.5 * x * x);
        }
        float4 PSHorizontal(float4 position : SV_Position) : SV_Target {
            float2 uv = position.xy / Parameters.xy;
            float stepX = Parameters.w * Parameters.y / Parameters.x;
            float3 sum = 0; float total = 0;
            [unroll] for (int i = -7; i <= 7; i++) {
                float w = Weight(i);
                float3 sampled = Original.SampleLevel(LinearClamp, uv + float2(i * stepX, 0), 0).rgb;
                sum += sampled * sampled * w; total += w;
            }
            return float4(sum / total, 1);
        }
        float4 PSComposite(float4 position : SV_Position) : SV_Target {
            float2 uv = position.xy / Parameters.xy;
            float3 sum = 0; float total = 0;
            [unroll] for (int i = -7; i <= 7; i++) {
                float w = Weight(i);
                sum += Horizontal.SampleLevel(LinearClamp, uv + float2(0, i * Parameters.w), 0).rgb * w;
                total += w;
            }
            float3 original = Original.SampleLevel(LinearClamp, uv, 0).rgb;
            float3 squared = original * original;
            float3 blurred = sum / total;
            float3 screen = blurred + squared - blurred * squared;
            if (dot(squared, float3(.29, .59, .12)) > dot(screen, float3(.29, .59, .12))) screen = squared;
            return float4(lerp(original, screen, Parameters.z), 1);
        }
        """;
}
