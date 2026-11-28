using System.Numerics;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Opt-in display-domain tone curve and subtle split-tone color grading pass.
/// Preserves alpha exactly and leaves zero-alpha pixels untouched.</summary>
internal sealed class D3D12ToneCurvePass(D3D12GpuRuntime runtime, D3D12GpuCache cache)
{
    private (byte[] Vertex, byte[] Pixel)? shaders;

    internal static ulong Measure(D3D12GpuRuntime runtime, int width, int height, bool active)
    {
        if (!active) return 0;
        ulong image = runtime.Device.GetResourceAllocationInfo(0, [ResourceDescription.Texture2D(
            Format.R8G8B8A8_UNorm, (uint)width, (uint)height, mipLevels: 1, flags: ResourceFlags.AllowRenderTarget)]).SizeInBytes;
        ulong constants = runtime.Device.GetResourceAllocationInfo(0, [ResourceDescription.Buffer(1024 * 1024)]).SizeInBytes;
        if (image is 0 or ulong.MaxValue || constants is 0 or ulong.MaxValue)
            throw new NotSupportedException("Device cannot estimate tone curve resources.");
        return checked((image + constants) * D3D12SsaaGiMemoryBudget.MaximumInFlightFrames);
    }

    internal D3D12GpuTarget Record(D3D12RecordingContext context, D3D12GpuTarget input, D3D12ToneCurveOptions options)
    {
        options.Validate();
        if (!ReferenceEquals(context.Runtime, runtime) || !runtime.IsRecording || !input.Initialized || input.Depth || input.Format != Format.R8G8B8A8_UNorm)
            throw new InvalidOperationException("Tone curve requires this runtime's recording frame and initialized display RGBA8.");
        if (!options.IsActive) return input;

        var device = context.Device;
        var commands = context.Commands;
        var compiled = GetShaders();
        var root = cache.Get("tone-curve:root", () => runtime.CreateRootSignature(
            new RootSignatureDescription(RootSignatureFlags.None, [
                new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 0)), ShaderVisibility.Pixel),
                new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.Pixel)]), RootSignatureVersion.Version10));

        var resource = runtime.Transients.Resource(context, HeapType.Default,
            ResourceDescription.Texture2D(input.Format, (uint)input.Width, (uint)input.Height, mipLevels: 1, flags: ResourceFlags.AllowRenderTarget),
            ResourceStates.RenderTarget);
        var rtv = runtime.Transients.Heap(new(DescriptorHeapType.RenderTargetView, 1));
        var srv = runtime.Transients.Heap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, DescriptorHeapFlags.ShaderVisible));
        device.CreateRenderTargetView(resource, null, rtv.GetCPUDescriptorHandleForHeapStart());
        device.CreateShaderResourceView(input.Resource, null, srv.GetCPUDescriptorHandleForHeapStart());
        runtime.DescriptorHighWater = Math.Max(runtime.DescriptorHighWater, 1);

        var output = runtime.Transients.Track(new D3D12GpuTarget("$tone-curve", resource, rtv.GetCPUDescriptorHandleForHeapStart(), input.Width, input.Height, input.Format, false));
        var pipeline = cache.Get("tone-curve:pso", () =>
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

        var shadowTint = options.ShadowTint ?? Vector3.One;
        var highlightTint = options.HighlightTint ?? Vector3.One;
        Span<Vector4> constants =
        [
            new(options.Contrast, options.Shoulder, options.Saturation, 0f),
            new(shadowTint, 0f),
            new(highlightTint, 0f),
        ];

        commands.UnsetRenderTargets();
        context.Transition(input, ResourceStates.PixelShaderResource);
        commands.SetDescriptorHeaps(srv);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.RSSetViewport(0, 0, input.Width, input.Height, 0, 1);
        commands.RSSetScissorRect(new RawRect(0, 0, input.Width, input.Height));
        commands.SetGraphicsRootSignature(root);
        commands.SetPipelineState(pipeline);
        commands.SetGraphicsRootDescriptorTable(0, srv.GetGPUDescriptorHandleForHeapStart());
        commands.SetGraphicsRootConstantBufferView(1, context.UploadConstants(MemoryMarshal.AsBytes(constants)));
        commands.OMSetRenderTargets(output.Handle);
        commands.DrawInstanced(3, 1, 0, 0);
        commands.UnsetRenderTargets();
        output.Initialized = true;
        return output;
    }

    internal static IEnumerable<D3D12FxcShaderRequest> GetPrewarmRequests()
    {
        const ShaderFlags flags = ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3;
        yield return new(Source, "VSMain", "tone-curve.hlsl", "vs_5_1", flags);
        yield return new(Source, "PSMain", "tone-curve.hlsl", "ps_5_1", flags);
    }

    private (byte[] Vertex, byte[] Pixel) GetShaders()
    {
        if (shaders is { } value) return value;
        const ShaderFlags flags = ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3;
        value = (
            D3D12FxcShaderCache.Compile(Source, "VSMain", "tone-curve.hlsl", "vs_5_1", flags),
            D3D12FxcShaderCache.Compile(Source, "PSMain", "tone-curve.hlsl", "ps_5_1", flags));
        runtime.ShaderCompilations += 2;
        shaders = value;
        return value;
    }

    internal const string Source = """
        Texture2D<float4> InputTexture : register(t0);
        cbuffer ToneCurveConstants : register(b0) {
            float4 CurveParams;    // contrast, shoulder, saturation, 0
            float4 ShadowTint;     // rgb, 0
            float4 HighlightTint;  // rgb, 0
        };
        float4 VSMain(uint id : SV_VertexID) : SV_Position {
            float2 uv = float2((id << 1) & 2, id & 2);
            return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
        }
        float ApplyCurve(float x, float contrast, float shoulder) {
            x = saturate(x);
            if (contrast != 0) {
                float s = x * x * (3.0 - 2.0 * x);
                x = saturate(lerp(x, s, contrast));
            }
            if (shoulder > 0) {
                // Soft highlight shoulder compresses near-white peaks monotonically without lifting values above knee.
                float knee = 0.68;
                if (x > knee) {
                    float t = (x - knee) / (1.0 - knee);
                    float compressed = knee + (1.0 - knee) * (t / (1.0 + shoulder * t));
                    x = saturate(compressed);
                }
            }
            return x;
        }
        float4 PSMain(float4 p : SV_Position) : SV_Target {
            float4 c = InputTexture.Load(int3(int2(p.xy), 0));
            if (c.a <= 0) return c;
            float3 rgb = c.rgb;
            float peak = max(rgb.r, max(rgb.g, rgb.b));
            if (peak > 0) {
                float3 pigment = rgb / peak;
                float luma = dot(rgb, float3(0.2126, 0.7152, 0.0722));
                if (CurveParams.z != 1.0) {
                    float chroma = 1.0 - min(pigment.r, min(pigment.g, pigment.b));
                    if (chroma > 0)
                        pigment = 1.0 + (pigment - 1.0) * min(CurveParams.z, 1.0 / chroma);
                }
                float curvedPeak = ApplyCurve(peak, CurveParams.x, CurveParams.y);
                rgb = pigment * curvedPeak;
                float3 split = lerp(ShadowTint.rgb, HighlightTint.rgb, smoothstep(0.15, 0.85, luma));
                rgb = saturate(rgb * split);
            }
            return float4(rgb, c.a);
        }
        """;
}
