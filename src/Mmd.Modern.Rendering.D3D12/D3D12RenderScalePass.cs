using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Bilinear resample pass for per-view RenderScale (0.5x–1.5x) when upscaling and SSAA are off.</summary>
internal sealed class D3D12RenderScalePass(D3D12GpuRuntime runtime, D3D12GpuCache cache)
{
    private (byte[] Vertex, byte[] Pixel)? shaders;

    internal D3D12GpuTarget Record(D3D12RecordingContext context, D3D12GpuTarget input, int displayWidth, int displayHeight)
    {
        if (!ReferenceEquals(context.Runtime, runtime) || !runtime.IsRecording || !input.Initialized || input.Depth)
            throw new InvalidOperationException("RenderScale requires this runtime's recording frame and an initialized color target.");
        if (input.Width == displayWidth && input.Height == displayHeight)
            return input;

        var device = context.Device;
        var commands = context.Commands;
        var compiled = GetShaders();
        var root = cache.Get("render-scale:root", () => runtime.CreateRootSignature(
            new RootSignatureDescription(RootSignatureFlags.None,
                [new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 0)), ShaderVisibility.Pixel)],
                [new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
                {
                    Filter = Filter.MinMagLinearMipPoint,
                    AddressU = TextureAddressMode.Clamp,
                    AddressV = TextureAddressMode.Clamp,
                    AddressW = TextureAddressMode.Clamp
                }]), RootSignatureVersion.Version10));

        var resource = runtime.Transients.Resource(context, HeapType.Default,
            ResourceDescription.Texture2D(input.Format, (uint)displayWidth, (uint)displayHeight, mipLevels: 1, flags: ResourceFlags.AllowRenderTarget),
            ResourceStates.RenderTarget);
        var rtv = runtime.Transients.Heap(new(DescriptorHeapType.RenderTargetView, 1));
        var srv = runtime.Transients.Heap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, DescriptorHeapFlags.ShaderVisible));
        device.CreateRenderTargetView(resource, null, rtv.GetCPUDescriptorHandleForHeapStart());
        device.CreateShaderResourceView(input.Resource, null, srv.GetCPUDescriptorHandleForHeapStart());
        runtime.DescriptorHighWater = Math.Max(runtime.DescriptorHighWater, 1);

        var output = runtime.Transients.Track(new D3D12GpuTarget("$render-scale", resource, rtv.GetCPUDescriptorHandleForHeapStart(),
            displayWidth, displayHeight, input.Format, false));

        var pipeline = cache.Get($"render-scale:pso:{input.Format}", () =>
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
                SampleDescription = new(1, 0)
            });
            runtime.PipelineCreations++;
            return pso;
        });

        commands.UnsetRenderTargets();
        context.Transition(input, ResourceStates.PixelShaderResource);
        commands.SetDescriptorHeaps(srv);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.RSSetViewport(0, 0, displayWidth, displayHeight, 0, 1);
        commands.RSSetScissorRect(new RawRect(0, 0, displayWidth, displayHeight));
        commands.SetGraphicsRootSignature(root);
        commands.SetPipelineState(pipeline);
        commands.SetGraphicsRootDescriptorTable(0, srv.GetGPUDescriptorHandleForHeapStart());
        commands.OMSetRenderTargets(output.Handle);
        commands.DrawInstanced(3, 1, 0, 0);
        commands.UnsetRenderTargets();
        output.Initialized = true;
        return output;
    }

    internal static IEnumerable<D3D12FxcShaderRequest> GetPrewarmRequests()
    {
        const ShaderFlags flags = ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness;
        yield return new(Source, "VSMain", "render-scale.hlsl", "vs_5_1", flags);
        yield return new(Source, "PSMain", "render-scale.hlsl", "ps_5_1", flags);
    }

    private (byte[] Vertex, byte[] Pixel) GetShaders()
    {
        if (shaders is { } value) return value;
        const ShaderFlags flags = ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness;
        value = (D3D12FxcShaderCache.Compile(Source, "VSMain", "render-scale.hlsl", "vs_5_1", flags),
            D3D12FxcShaderCache.Compile(Source, "PSMain", "render-scale.hlsl", "ps_5_1", flags));
        runtime.ShaderCompilations += 2;
        shaders = value;
        return value;
    }

    internal const string Source = """
        Texture2D<float4> InputTexture : register(t0);
        SamplerState LinearClamp : register(s0);

        struct VSOut {
            float4 Pos : SV_Position;
            float2 Uv  : TEXCOORD0;
        };

        VSOut VSMain(uint id : SV_VertexID) {
            VSOut o;
            float2 uv = float2((id << 1) & 2, id & 2);
            o.Pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
            o.Uv = uv;
            return o;
        }

        float4 PSMain(VSOut i) : SV_Target {
            return InputTexture.SampleLevel(LinearClamp, i.Uv, 0);
        }
        """;
}
