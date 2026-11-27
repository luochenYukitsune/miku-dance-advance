using System.Numerics;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Standalone AMD RCAS sharpening pass decoupled from FSR1 EASU.
/// Works with TAA, SMAA, FXAA, MSAA, Diffusion, or un-antialiased output while preserving center alpha.</summary>
internal sealed class D3D12RcasPass(D3D12GpuRuntime runtime, D3D12GpuCache cache)
{
    private (byte[] Vertex, byte[] Pixel)? shaders;

    internal D3D12GpuTarget Record(D3D12RecordingContext context, D3D12GpuTarget input, D3D12SharpeningOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!ReferenceEquals(context.Runtime, runtime) || !runtime.IsRecording)
            throw new InvalidOperationException("Standalone RCAS requires an open recording frame on the owning runtime.");
        if (input.Depth || input.Format != Format.R8G8B8A8_UNorm || !input.Initialized)
            throw new InvalidOperationException("Standalone RCAS requires initialized RGBA8 display color.");

        int width = input.Width, height = input.Height;
        var device = context.Device;
        var commands = context.Commands;
        var compiled = GetShaders();
        var root = cache.Get("rcas-standalone:root", () => runtime.CreateRootSignature(
            new RootSignatureDescription(RootSignatureFlags.None,
                [new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
                 new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 0)), ShaderVisibility.Pixel)]),
            RootSignatureVersion.Version10));

        var rtv = runtime.Transients.Heap(new(DescriptorHeapType.RenderTargetView, 1));
        var srv = runtime.Transients.Heap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, DescriptorHeapFlags.ShaderVisible));
        runtime.DescriptorHighWater = Math.Max(runtime.DescriptorHighWater, 1);

        var resource = runtime.Transients.Resource(context, HeapType.Default,
            ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, (uint)width, (uint)height, mipLevels: 1, flags: ResourceFlags.AllowRenderTarget),
            ResourceStates.RenderTarget);
        var rtvHandle = rtv.GetCPUDescriptorHandleForHeapStart();
        device.CreateRenderTargetView(resource, null, rtvHandle);
        device.CreateShaderResourceView(input.Resource, null, srv.GetCPUDescriptorHandleForHeapStart());
        var output = runtime.Transients.Track(new D3D12GpuTarget("$sharpening-rcas", resource, rtvHandle, width, height, Format.R8G8B8A8_UNorm, false));

        var pipeline = cache.Get("rcas-standalone:pso", () =>
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
                RenderTargetFormats = [Format.R8G8B8A8_UNorm],
                SampleDescription = new(1, 0),
            });
            runtime.PipelineCreations++;
            return pso;
        });

        Vector4[] constants = [new(width, height, options.Sharpness, 0)];
        ulong cbAddress = context.UploadConstants(MemoryMarshal.AsBytes(constants.AsSpan()));

        commands.UnsetRenderTargets();
        context.Transition(input, ResourceStates.PixelShaderResource);
        commands.SetDescriptorHeaps(srv);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.RSSetViewport(0, 0, width, height, 0, 1);
        commands.RSSetScissorRect(new RawRect(0, 0, width, height));
        commands.OMSetRenderTargets(output.Handle);
        commands.SetGraphicsRootSignature(root);
        commands.SetPipelineState(pipeline);
        commands.SetGraphicsRootConstantBufferView(0, cbAddress);
        commands.SetGraphicsRootDescriptorTable(1, srv.GetGPUDescriptorHandleForHeapStart());
        commands.DrawInstanced(3, 1, 0, 0);
        output.Initialized = true;
        commands.UnsetRenderTargets();
        return output;
    }

    internal static IEnumerable<D3D12FxcShaderRequest> GetPrewarmRequests()
    {
        string source = BuildShaderSource();
        const ShaderFlags flags = ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3;
        yield return new(source, "VSMain", "rcas-standalone.hlsl", "vs_5_1", flags);
        yield return new(source, "PSRcasStandalone", "rcas-standalone.hlsl", "ps_5_1", flags);
    }

    internal static string BuildShaderSource() => Prefix + ReadEmbedded("ffx_a.h") + ReadEmbedded("ffx_fsr1.h") + Entries;

    private (byte[] Vertex, byte[] Pixel) GetShaders()
    {
        if (shaders is { } compiled) return compiled;
        string source = BuildShaderSource();
        const ShaderFlags flags = ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3;
        compiled = (
            D3D12FxcShaderCache.Compile(source, "VSMain", "rcas-standalone.hlsl", "vs_5_1", flags),
            D3D12FxcShaderCache.Compile(source, "PSRcasStandalone", "rcas-standalone.hlsl", "ps_5_1", flags));
        shaders = compiled;
        runtime.ShaderCompilations += 2;
        return compiled;
    }

    private static string ReadEmbedded(string name)
    {
        using var stream = typeof(D3D12RcasPass).Assembly.GetManifestResourceStream("Fsr1." + name)
            ?? throw new InvalidOperationException("Missing embedded FSR1 resource: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd() + "\n";
    }

    private const string Prefix = """
        #define A_GPU 1
        #define A_HLSL 1
        #define FSR_RCAS_F 1

        """;

    private const string Entries = """
        cbuffer RcasConstants : register(b0) { float4 Params; };
        Texture2D<float4> InputTexture : register(t0);
        AF4 FsrRcasLoadF(ASU2 p) {
            int2 maxCoord = max(int2(Params.xy) - 1, int2(0, 0));
            return InputTexture.Load(int3(clamp(p, int2(0, 0), maxCoord), 0));
        }
        void FsrRcasInputF(inout AF1 r, inout AF1 g, inout AF1 b) {}
        float4 VSMain(uint id : SV_VertexID) : SV_Position {
            float2 uv = float2((id << 1) & 2, id & 2);
            return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
        }
        float4 PSRcasStandalone(float4 position : SV_Position) : SV_Target {
            uint2 coord = uint2(position.xy);
            uint4 con;
            FsrRcasCon(con, Params.z);
            float3 color;
            FsrRcasF(color.r, color.g, color.b, coord, con);
            int2 maxCoord = max(int2(Params.xy) - 1, int2(0, 0));
            float alpha = InputTexture.Load(int3(clamp(int2(coord), int2(0, 0), maxCoord), 0)).a;
            return float4(saturate(color), alpha);
        }
        """;
}
