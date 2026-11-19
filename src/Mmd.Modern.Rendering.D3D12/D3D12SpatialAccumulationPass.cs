using System.Runtime.InteropServices;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Accumulates complete, same-frame RGBA8 spatial samples on the GPU. The float buffer,
/// fixed sample descriptor and resolved color belong to the recording fence, independent of sample count.
/// Samples are averaged in the existing frame color domain; this pass does not change gamma or alpha semantics.</summary>
internal sealed class D3D12SpatialAccumulationPass(D3D12GpuRuntime runtime, D3D12GpuCache cache)
{
    private (byte[] Compute, byte[] Vertex, byte[] Pixel)? shaders;

    internal Workspace Begin(D3D12RecordingContext context, int width, int height, int samples, Format outputFormat=Format.R8G8B8A8_UNorm)
    {
        ValidateContext(context);
        if (width is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(width));
        if (height is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(height));
        if (samples is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(samples));
        if(outputFormat is not (Format.R8G8B8A8_UNorm or Format.R16G16B16A16_Float))throw new ArgumentOutOfRangeException(nameof(outputFormat));
        var device = context.Device;
        var pipeline = GetPipeline(outputFormat);
        ulong bytes = checked((ulong)width * (ulong)height * 16);
        // Sample zero overwrites the whole pooled accumulation buffer; the opaque resolve overwrites the output.
        var accumulation = runtime.Transients.Resource(context, HeapType.Default,
            ResourceDescription.Buffer(bytes, ResourceFlags.AllowUnorderedAccess), ResourceStates.UnorderedAccess);
        var srvHeap = runtime.Transients.Heap(new(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, DescriptorHeapFlags.ShaderVisible));
        var rtvHeap = runtime.Transients.Heap(new(DescriptorHeapType.RenderTargetView, 1));
        var outputResource = runtime.Transients.Resource(context, HeapType.Default,
            ResourceDescription.Texture2D(outputFormat, (uint)width, (uint)height,
                mipLevels: 1, flags: ResourceFlags.AllowRenderTarget), ResourceStates.RenderTarget);
        var outputHandle = rtvHeap.GetCPUDescriptorHandleForHeapStart();
        device.CreateRenderTargetView(outputResource, null, outputHandle);
        var output = runtime.Transients.Track(new D3D12GpuTarget("$ssaa-output", outputResource, outputHandle,
            width, height, outputFormat, false));
        runtime.DescriptorHighWater = Math.Max(runtime.DescriptorHighWater, 1);
        return new(context, pipeline, accumulation, srvHeap, output, samples);
    }

    private void ValidateContext(D3D12RecordingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!ReferenceEquals(context.Runtime, runtime) || !runtime.IsRecording)
            throw new InvalidOperationException("Spatial accumulation requires this runtime's open recording frame.");
    }

    private Pipeline GetPipeline(Format outputFormat)
    {
        var device = runtime.Device;
        var binary = GetShaders();
        var computeRoot = cache.Get("ssaa:accumulate-root", () => runtime.CreateRootSignature(
            new RootSignatureDescription(RootSignatureFlags.None,
                [new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
                 new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 0)), ShaderVisibility.All),
                 new(RootParameterType.UnorderedAccessView, new RootDescriptor(0, 0), ShaderVisibility.All)]),
            RootSignatureVersion.Version10));
        var compute = cache.Get("ssaa:accumulate-pso", () =>
        {
            var created = runtime.CreateComputePipelineState(new ComputePipelineStateDescription
            { RootSignature = computeRoot, ComputeShader = binary.Compute });
            runtime.PipelineCreations++;
            return created;
        });
        var resolveRoot = cache.Get("ssaa:resolve-root", () => runtime.CreateRootSignature(
            new RootSignatureDescription(RootSignatureFlags.None,
                [new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.Pixel),
                 new(RootParameterType.ShaderResourceView, new RootDescriptor(0, 0), ShaderVisibility.Pixel)]),
            RootSignatureVersion.Version10));
        var resolve = cache.Get(outputFormat==Format.R8G8B8A8_UNorm?"ssaa:resolve-pso":"ssaa:resolve-pso:fp16", () =>
        {
            var created = runtime.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
            {
                RootSignature = resolveRoot, VertexShader = binary.Vertex, PixelShader = binary.Pixel,
                BlendState = BlendDescription.Opaque, SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone, DepthStencilState = DepthStencilDescription.None,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle, RenderTargetFormats = [outputFormat],
                SampleDescription = new(1, 0)
            });
            runtime.PipelineCreations++;
            return created;
        });
        return new(computeRoot, compute, resolveRoot, resolve);
    }

    internal static IEnumerable<D3D12FxcShaderRequest> GetPrewarmRequests()
    {
        var flags = ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness;
        yield return new(ComputeSource, "CSAccumulate", "spatial-accumulation.hlsl", "cs_5_1", flags);
        yield return new(ResolveSource, "VSResolve", "spatial-resolve.hlsl", "vs_5_1", flags);
        yield return new(ResolveSource, "PSResolve", "spatial-resolve.hlsl", "ps_5_1", flags);
    }

    private (byte[] Compute, byte[] Vertex, byte[] Pixel) GetShaders()
    {
        if (shaders is { } found) return found;
        var flags = ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness;
        var compute = D3D12FxcShaderCache.Compile(ComputeSource, "CSAccumulate", "spatial-accumulation.hlsl", "cs_5_1", flags);
        var vertex = D3D12FxcShaderCache.Compile(ResolveSource, "VSResolve", "spatial-resolve.hlsl", "vs_5_1", flags);
        var pixel = D3D12FxcShaderCache.Compile(ResolveSource, "PSResolve", "spatial-resolve.hlsl", "ps_5_1", flags);
        runtime.ShaderCompilations += 3;
        return (shaders = (compute, vertex, pixel)).Value;
    }

    internal sealed record Pipeline(ID3D12RootSignature ComputeRoot, ID3D12PipelineState Compute,
        ID3D12RootSignature ResolveRoot, ID3D12PipelineState Resolve);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Constants(uint Width, uint Height, uint SampleIndex, uint Samples);

    internal sealed class Workspace
    {
        private readonly D3D12RecordingContext recording;
        private readonly long frameId;
        private readonly Pipeline pipeline;
        private readonly ID3D12Resource accumulation;
        private readonly ID3D12DescriptorHeap sampleHeap;
        private readonly D3D12GpuTarget output;
        private ID3D12Resource? sampleResource;
        private int accumulated;
        private bool resolved;
        internal int Samples { get; }

        internal Workspace(D3D12RecordingContext recording, Pipeline pipeline, ID3D12Resource accumulation,
            ID3D12DescriptorHeap sampleHeap, D3D12GpuTarget output, int samples)
        {
            this.recording = recording;
            frameId = recording.Runtime.FrameId;
            this.pipeline = pipeline;
            this.accumulation = accumulation;
            this.sampleHeap = sampleHeap;
            this.output = output;
            Samples = samples;
        }

        /// <summary>The caller reuses one sample color resource for the entire sequence. Its SRV is written
        /// once, so every previously recorded dispatch keeps a stable descriptor until submission completes.</summary>
        internal void Accumulate(D3D12RecordingContext context, D3D12GpuTarget sample, int sampleIndex)
        {
            ValidateRecording(context);
            ArgumentNullException.ThrowIfNull(sample);
            if (resolved || accumulated >= Samples)
                throw new InvalidOperationException("The spatial sample sequence is already complete.");
            if (sampleIndex != accumulated)
                throw new ArgumentOutOfRangeException(nameof(sampleIndex), "Spatial samples must be recorded once in ascending order from zero.");
            if (sample.Depth || sample.Format != output.Format || !sample.Initialized
                || sample.Width != output.Width || sample.Height != output.Height)
                throw new InvalidOperationException("Spatial accumulation requires initialized samples matching the output format and size.");
            if (sampleResource is null)
            {
                sampleResource = sample.Resource;
                context.Device.CreateShaderResourceView(sampleResource, null, sampleHeap.GetCPUDescriptorHandleForHeapStart());
            }
            else if (sample.Resource.NativePointer != sampleResource.NativePointer)
                throw new InvalidOperationException("Spatial accumulation must reuse its first sample color resource; the in-flight SRV cannot be overwritten.");

            var commands = context.Commands;
            commands.UnsetRenderTargets();
            context.Transition(sample, ResourceStates.NonPixelShaderResource);
            commands.SetDescriptorHeaps(sampleHeap);
            commands.SetComputeRootSignature(pipeline.ComputeRoot);
            commands.SetPipelineState(pipeline.Compute);
            commands.SetComputeRootConstantBufferView(0, context.UploadConstants(
                new Constants((uint)output.Width, (uint)output.Height, (uint)sampleIndex, (uint)Samples)));
            commands.SetComputeRootDescriptorTable(1, sampleHeap.GetGPUDescriptorHandleForHeapStart());
            commands.SetComputeRootUnorderedAccessView(2, accumulation.GPUVirtualAddress);
            commands.Dispatch((output.Width + 7) / 8, (output.Height + 7) / 8, 1);
            commands.ResourceBarrierUnorderedAccessView(accumulation);
            accumulated++;
        }

        internal D3D12GpuTarget Resolve(D3D12RecordingContext context)
        {
            ValidateRecording(context);
            if (resolved || accumulated != Samples)
                throw new InvalidOperationException("Spatial resolve requires exactly the requested samples and may run only once.");
            var commands = context.Commands;
            commands.UnsetRenderTargets();
            commands.ResourceBarrierTransition(accumulation, ResourceStates.UnorderedAccess, ResourceStates.PixelShaderResource);
            commands.OMSetRenderTargets(output.Handle);
            commands.RSSetViewport(0, 0, output.Width, output.Height, 0, 1);
            commands.RSSetScissorRect(new RawRect(0, 0, output.Width, output.Height));
            commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            commands.SetGraphicsRootSignature(pipeline.ResolveRoot);
            commands.SetPipelineState(pipeline.Resolve);
            commands.SetGraphicsRootConstantBufferView(0, context.UploadConstants(
                new Constants((uint)output.Width, (uint)output.Height, 0, (uint)Samples)));
            commands.SetGraphicsRootShaderResourceView(1, accumulation.GPUVirtualAddress);
            commands.DrawInstanced(3, 1, 0, 0);
            commands.UnsetRenderTargets();
            output.Initialized = true;
            resolved = true;
            return output;
        }

        private void ValidateRecording(D3D12RecordingContext context)
        {
            if (!ReferenceEquals(context, recording) || !recording.Runtime.IsRecording || recording.Runtime.FrameId != frameId)
                throw new InvalidOperationException("Spatial accumulation workspace belongs to one open recording frame.");
        }
    }

    private const string ComputeSource = """
        cbuffer SpatialConstants : register(b0) { uint Width; uint Height; uint SampleIndex; uint Samples; };
        Texture2D<float4> SampleColor : register(t0);
        RWStructuredBuffer<float4> Accumulation : register(u0);
        [numthreads(8, 8, 1)]
        void CSAccumulate(uint3 id : SV_DispatchThreadID) {
            if (id.x >= Width || id.y >= Height) return;
            uint index = id.y * Width + id.x;
            float4 contribution = SampleColor.Load(int3(id.xy, 0)) / float(Samples);
            // Every element is overwritten on sample zero; no clear or uninitialized UAV read is needed.
            if (SampleIndex == 0) Accumulation[index] = contribution;
            else Accumulation[index] = Accumulation[index] + contribution;
        }
        """;

    private const string ResolveSource = """
        cbuffer SpatialConstants : register(b0) { uint Width; uint Height; uint SampleIndex; uint Samples; };
        StructuredBuffer<float4> Accumulation : register(t0);
        float4 VSResolve(uint id : SV_VertexID) : SV_Position {
            float2 uv = float2((id << 1) & 2, id & 2);
            return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
        }
        float4 PSResolve(float4 position : SV_Position) : SV_Target {
            // Root SRVs have no descriptor bounds. Clamp helper lanes even if FXC flattens branches.
            uint2 pixel = min(uint2(position.xy), uint2(Width - 1, Height - 1));
            return Accumulation[pixel.y * Width + pixel.x];
        }
        """;
}
