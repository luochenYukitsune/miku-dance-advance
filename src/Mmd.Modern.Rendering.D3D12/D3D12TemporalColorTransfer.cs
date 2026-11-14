using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Bridges the accepted display-domain toon result and FSR's linear input/output contract.</summary>
/// <remarks>Only the standard sRGB transfer is applied; no exposure, tone mapping, filtering, or scene shading changes.</remarks>
internal sealed class D3D12TemporalColorTransfer(D3D12GpuRuntime runtime, D3D12GpuCache cache)
{
    private (byte[] Vertex, byte[] Decode, byte[] Encode)? shaders;

    internal D3D12GpuTarget Decode(D3D12RecordingContext context, D3D12GpuTarget input)
    {
        ValidateContext(context);
        if (input.Depth || input.Format != Format.R8G8B8A8_UNorm || !input.Initialized)
            throw new InvalidOperationException("Temporal color decode requires initialized display-domain RGBA8.");
        return Record(context, input, false);
    }

    internal D3D12GpuTarget Encode(D3D12RecordingContext context, D3D12GpuTarget input)
    {
        ValidateContext(context);
        if (input.Depth || !input.Initialized || input.Format is not (Format.R16G16B16A16_Float or Format.R32G32B32A32_Float))
            throw new InvalidOperationException("Temporal color encode requires initialized linear RGBA float color.");
        return Record(context, input, true);
    }

    private void ValidateContext(D3D12RecordingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!ReferenceEquals(context.Runtime, runtime) || !runtime.IsRecording)
            throw new InvalidOperationException("Temporal color transfer requires this runtime's open recording frame.");
    }

    private D3D12GpuTarget Record(D3D12RecordingContext context, D3D12GpuTarget input, bool encode)
    {
        var device = context.Device;
        var commands = context.Commands;
        var compiled = GetShaders();
        var root = cache.Get("temporal-color:root", () => device.CreateRootSignature<ID3D12RootSignature>(
            new RootSignatureDescription(RootSignatureFlags.None,
                [new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 0)), ShaderVisibility.Pixel)]),
            RootSignatureVersion.Version10));
        Format format = encode ? Format.R8G8B8A8_UNorm : Format.R16G16B16A16_Float;
        var resource = context.Own(device.CreateCommittedResource(HeapType.Default,
            ResourceDescription.Texture2D(format, (uint)input.Width, (uint)input.Height, mipLevels: 1, flags: ResourceFlags.AllowRenderTarget),
            ResourceStates.RenderTarget));
        var rtv = context.Own(device.CreateDescriptorHeap(new(DescriptorHeapType.RenderTargetView, 1)));
        var srv = context.Own(device.CreateDescriptorHeap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, DescriptorHeapFlags.ShaderVisible)));
        var outputHandle = rtv.GetCPUDescriptorHandleForHeapStart();
        device.CreateRenderTargetView(resource, null, outputHandle);
        device.CreateShaderResourceView(input.Resource, null, srv.GetCPUDescriptorHandleForHeapStart());
        runtime.DescriptorHighWater = Math.Max(runtime.DescriptorHighWater, 1);
        var target = new D3D12GpuTarget(encode ? "$temporal-color-display" : "$temporal-color-linear",
            resource, outputHandle, input.Width, input.Height, format, false);
        var pipeline = cache.Get(encode ? "temporal-color:pso:encode" : "temporal-color:pso:decode", () =>
        {
            var pso = device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
            {
                RootSignature = root, VertexShader = compiled.Vertex, PixelShader = encode ? compiled.Encode : compiled.Decode,
                BlendState = BlendDescription.Opaque, SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone, DepthStencilState = DepthStencilDescription.None,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle, RenderTargetFormats = [format], SampleDescription = new(1, 0)
            });
            runtime.PipelineCreations++;
            return pso;
        });
        commands.UnsetRenderTargets();
        context.Transition(input, ResourceStates.PixelShaderResource);
        commands.SetDescriptorHeaps(srv);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.RSSetViewport(0, 0, input.Width, input.Height, 0, 1);
        commands.RSSetScissorRect(new RawRect(0, 0, input.Width, input.Height));
        commands.SetGraphicsRootSignature(root);
        commands.SetPipelineState(pipeline);
        commands.SetGraphicsRootDescriptorTable(0, srv.GetGPUDescriptorHandleForHeapStart());
        commands.OMSetRenderTargets(target.Handle);
        commands.DrawInstanced(3, 1, 0, 0);
        target.Initialized = true;
        commands.UnsetRenderTargets();
        return target;
    }

    private (byte[] Vertex, byte[] Decode, byte[] Encode) GetShaders()
    {
        if (shaders is { } compiled) return compiled;
        const ShaderFlags flags = ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3;
        compiled = (Compiler.Compile(Shader, "VSMain", "temporal-color-transfer.hlsl", "vs_5_1", flags).ToArray(),
            Compiler.Compile(Shader, "PSDecode", "temporal-color-transfer.hlsl", "ps_5_1", flags).ToArray(),
            Compiler.Compile(Shader, "PSEncode", "temporal-color-transfer.hlsl", "ps_5_1", flags).ToArray());
        shaders = compiled;
        runtime.ShaderCompilations += 3;
        return compiled;
    }

    private const string Shader = """
        Texture2D<float4> InputTexture : register(t0);
        float4 VSMain(uint id : SV_VertexID) : SV_Position {
            float2 uv = float2((id << 1) & 2, id & 2);
            return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
        }
        float Decode(float value) {
            return value <= 0.04045 ? value / 12.92 : pow((value + 0.055) / 1.055, 2.4);
        }
        float Encode(float value) {
            return value <= 0.0031308 ? value * 12.92 : 1.055 * pow(max(value, 0.0), 1.0 / 2.4) - 0.055;
        }
        float4 PSDecode(float4 position : SV_Position) : SV_Target {
            float4 color = InputTexture.Load(int3(int2(position.xy), 0));
            return float4(Decode(color.r), Decode(color.g), Decode(color.b), color.a);
        }
        float4 PSEncode(float4 position : SV_Position) : SV_Target {
            float4 color = InputTexture.Load(int3(int2(position.xy), 0));
            return float4(Encode(color.r), Encode(color.g), Encode(color.b), color.a);
        }
        """;
}
