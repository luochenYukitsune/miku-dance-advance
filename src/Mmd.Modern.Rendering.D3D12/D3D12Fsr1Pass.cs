using System.Numerics;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>AMD FSR1 FP32 EASU and optional RCAS. All image/descriptor allocations retire with the recording fence.</summary>
internal sealed class D3D12Fsr1Pass(D3D12GpuRuntime runtime, D3D12GpuCache cache)
{
    internal sealed record Result(D3D12GpuTarget Easu, D3D12GpuTarget? Rcas)
    { internal D3D12GpuTarget Output => Rcas ?? Easu; }
    private (byte[] Vertex, byte[] Easu, byte[] Rcas)? shaders;

    internal Result Record(D3D12RecordingContext context, D3D12GpuTarget input, int width, int height, D3D12SpatialUpscalingOptions options)
    {
        if (input.Depth || input.Format != Format.R8G8B8A8_UNorm || !input.Initialized)
            throw new InvalidOperationException("FSR1 requires initialized RGBA8 scene color.");
        var device = context.Device; var commands = context.Commands;
        var compiled = GetShaders();
        var root = cache.Get("fsr1:root", () => runtime.CreateRootSignature(
            new RootSignatureDescription(RootSignatureFlags.None,
                [new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
                 new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 0)), ShaderVisibility.Pixel)],
                [new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
                { Filter = Filter.MinMagLinearMipPoint, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp }]), RootSignatureVersion.Version10));
        var rtv = context.Own(device.CreateDescriptorHeap(new(DescriptorHeapType.RenderTargetView, 2)));
        var srv = context.Own(device.CreateDescriptorHeap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 2, DescriptorHeapFlags.ShaderVisible)));
        int rtvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        int srvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        runtime.DescriptorHighWater = Math.Max(runtime.DescriptorHighWater, 2);
        D3D12GpuTarget Allocate(string name, int index, Format format)
        {
            var resource = context.Own(device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Texture2D(format, (uint)width, (uint)height, mipLevels: 1, flags: ResourceFlags.AllowRenderTarget), ResourceStates.RenderTarget));
            var handle = rtv.GetCPUDescriptorHandleForHeapStart().Offset(index, rtvStride);
            device.CreateRenderTargetView(resource, null, handle);
            return new(name, resource, handle, width, height, format, false);
        }
        // Keep reconstructed samples at float precision between filters. Final output follows the existing RGBA8 contract.
        var easu = Allocate("$fsr1-easu", 0, options.EnableRcas ? Format.R32G32B32A32_Float : Format.R8G8B8A8_UNorm);
        var rcas = options.EnableRcas ? Allocate("$fsr1-rcas", 1, Format.R8G8B8A8_UNorm) : null;
        device.CreateShaderResourceView(input.Resource, null, srv.GetCPUDescriptorHandleForHeapStart());
        if (rcas is not null) device.CreateShaderResourceView(easu.Resource, null, srv.GetCPUDescriptorHandleForHeapStart().Offset(1, srvStride));
        commands.UnsetRenderTargets(); context.Transition(input, ResourceStates.PixelShaderResource);
        commands.SetDescriptorHeaps(srv);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.RSSetViewport(0, 0, width, height, 0, 1);
        commands.RSSetScissorRect(new RawRect(0, 0, width, height));
        Vector4[] constants = [new(input.Width, input.Height, width, height), new(options.RcasSharpness, 0, 0, 0)];
        ulong address = context.UploadConstants(MemoryMarshal.AsBytes(constants.AsSpan()));
        void Draw(string pass, D3D12GpuTarget target, int descriptor, byte[] pixel)
        {
            var pipeline = cache.Get($"fsr1:pso:{pass}:{target.Format}", () =>
            {
                var pso = runtime.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
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
        Draw("easu", easu, 0, compiled.Easu);
        if (rcas is not null) { context.Transition(easu, ResourceStates.PixelShaderResource); Draw("rcas", rcas, 1, compiled.Rcas); }
        return new(easu, rcas);
    }

    internal static IEnumerable<D3D12FxcShaderRequest> GetPrewarmRequests()
    {
        string source = Prefix + Read("ffx_a.h") + Read("ffx_fsr1.h") + Entries;
        var flags = ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3;
        yield return new(source, "VSMain", "fsr1-wrapper.hlsl", "vs_5_1", flags);
        yield return new(source, "PSEasu", "fsr1-wrapper.hlsl", "ps_5_1", flags);
        yield return new(source, "PSRcas", "fsr1-wrapper.hlsl", "ps_5_1", flags);
    }

    private (byte[] Vertex, byte[] Easu, byte[] Rcas) GetShaders()
    {
        if (shaders is { } compiled) return compiled;
        string source = Prefix + Read("ffx_a.h") + Read("ffx_fsr1.h") + Entries;
        var flags = ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3;
        compiled = (D3D12FxcShaderCache.Compile(source, "VSMain", "fsr1-wrapper.hlsl", "vs_5_1", flags),
            D3D12FxcShaderCache.Compile(source, "PSEasu", "fsr1-wrapper.hlsl", "ps_5_1", flags),
            D3D12FxcShaderCache.Compile(source, "PSRcas", "fsr1-wrapper.hlsl", "ps_5_1", flags));
        shaders = compiled; runtime.ShaderCompilations += 3; return compiled;
    }
    private static string Read(string name)
    {
        using var stream = typeof(D3D12Fsr1Pass).Assembly.GetManifestResourceStream("Fsr1." + name)
            ?? throw new InvalidOperationException("Missing embedded FSR1 resource: " + name);
        using var reader = new StreamReader(stream); return reader.ReadToEnd() + "\n";
    }
    private const string Prefix = """
        #define A_GPU 1
        #define A_HLSL 1
        #define FSR_EASU_F 1
        #define FSR_RCAS_F 1

        """;
    private const string Entries = """
        cbuffer FsrConstants : register(b0) { float4 Extents; float4 Sharpness; };
        Texture2D<float4> InputTexture : register(t0);
        SamplerState LinearClamp : register(s0);
        AF4 FsrEasuRF(AF2 p) { return InputTexture.GatherRed(LinearClamp, p); }
        AF4 FsrEasuGF(AF2 p) { return InputTexture.GatherGreen(LinearClamp, p); }
        AF4 FsrEasuBF(AF2 p) { return InputTexture.GatherBlue(LinearClamp, p); }
        AF4 FsrRcasLoadF(ASU2 p) {
            // Texture.Load does not obey sampler addressing. Clamp all five taps, including one-pixel axes.
            return InputTexture.Load(int3(clamp(p, int2(0, 0), int2(Extents.zw) - 1), 0));
        }
        void FsrRcasInputF(inout AF1 r, inout AF1 g, inout AF1 b) {}
        float4 VSMain(uint id : SV_VertexID) : SV_Position {
            float2 uv = float2((id << 1) & 2, id & 2);
            return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
        }
        float4 PSEasu(float4 position : SV_Position) : SV_Target {
            uint4 c0, c1, c2, c3;
            FsrEasuCon(c0, c1, c2, c3, Extents.x, Extents.y, Extents.x, Extents.y, Extents.z, Extents.w);
            float3 color; FsrEasuF(color, uint2(position.xy), c0, c1, c2, c3);
            return float4(color, 1);
        }
        float4 PSRcas(float4 position : SV_Position) : SV_Target {
            uint4 con; FsrRcasCon(con, Sharpness.x);
            float3 color; FsrRcasF(color.r, color.g, color.b, uint2(position.xy), con);
            return float4(color, 1);
        }
        """;
}
