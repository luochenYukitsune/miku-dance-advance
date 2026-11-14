using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Frame-owned, render-resolution FSR inputs, readable by compute and pixel shaders.
/// Depth is finite, noninverted D3D device depth. Motion is previousUV-currentUV in render pixels;
/// consumers must use motionVectorScale=(1,1), with jitter cancellation disabled.</summary>
internal sealed record D3D12TemporalInputs(ID3D12Resource Depth, ID3D12Resource MotionVectors, int Width, int Height)
{
    internal ID3D12Resource? ReactiveMask { get; init; }
    internal const ResourceStates ShaderReadState = ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource;
}

/// <summary>Opaque SceneSnapshot mesh coverage only, with no changes to the scene color shaders.
/// This pass uses two-sided triangles, matching the basic mesh renderer. A caller rendering original-style
/// culling, outlines, shadows, overlays or effect geometry must establish matching coverage separately.
/// Alpha blending and texture alpha coverage are explicitly rejected until reactive/coverage support exists.
/// Geometry, targets and descriptors retire through the existing runtime's frame fence. The root/PSO are
/// cached across resize; Dispose waits for submitted work and requires no open recording.</summary>
internal sealed partial class D3D12TemporalInputPass(D3D12GpuRuntime runtime) : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct TemporalVertex(Vector3 Current, Vector3 Previous);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Constants(Matrix4x4 CurrentWorld, Matrix4x4 PreviousWorld,
        Matrix4x4 RasterViewProjection, Matrix4x4 CurrentViewProjection, Matrix4x4 PreviousViewProjection, Vector4 Extent);

    private ID3D12RootSignature? root;
    private ID3D12PipelineState? pipeline;
    private sealed record TextureCoverage(bool Opaque);
    // SceneTexture is immutable. Weak keys avoid rescanning megapixel alpha planes every frame
    // without retaining replaced scene textures or an unbounded strong-reference content cache.
    private readonly ConditionalWeakTable<SceneTexture, TextureCoverage> textureCoverage = new();
    private bool disposed;

    internal D3D12TemporalInputs Record(D3D12RecordingContext context, SceneSnapshot current, SceneSnapshot previous, int width, int height)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previous);
        if (!ReferenceEquals(context.Runtime, runtime) || !runtime.IsRecording)
            throw new InvalidOperationException("Temporal inputs require an open frame on the owning D3D12 runtime.");
        if (width is < 1 or > 4096 || height is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(width), "Temporal input extents must be 1..4096.");
        ValidatePair(current, previous);
        EnsurePipeline();

        var device = context.Device;
        var commands = context.Commands;
        var rtvHeap = context.Own(device.CreateDescriptorHeap(new(DescriptorHeapType.RenderTargetView, 2)));
        var dsvHeap = context.Own(device.CreateDescriptorHeap(new(DescriptorHeapType.DepthStencilView, 1)));
        int stride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        var depthHandle = rtvHeap.GetCPUDescriptorHandleForHeapStart();
        // CpuDescriptorHandle.Offset mutates its receiver. Offset a copy so depth keeps slot 0.
        var motionHandle = depthHandle;
        motionHandle.Offset(1, stride);
        var dsv = dsvHeap.GetCPUDescriptorHandleForHeapStart();
        runtime.DescriptorHighWater = Math.Max(runtime.DescriptorHighWater, 3);
        ID3D12Resource Target(Format format, ResourceFlags flags, ResourceStates state) => context.Own(device.CreateCommittedResource(
            HeapType.Default, ResourceDescription.Texture2D(format, (uint)width, (uint)height, mipLevels: 1, flags: flags), state));
        var depth = Target(Format.R32_Float, ResourceFlags.AllowRenderTarget, ResourceStates.RenderTarget);
        var motion = Target(Format.R32G32_Float, ResourceFlags.AllowRenderTarget, ResourceStates.RenderTarget);
        var visibility = Target(Format.D32_Float, ResourceFlags.AllowDepthStencil, ResourceStates.DepthWrite);
        device.CreateRenderTargetView(depth, null, depthHandle);
        device.CreateRenderTargetView(motion, null, motionHandle);
        device.CreateDepthStencilView(visibility, null, dsv);
        commands.UnsetRenderTargets();
        commands.OMSetRenderTargets([depthHandle, motionHandle], dsv);
        commands.ClearRenderTargetView(depthHandle, new Color4(1f, 1f, 1f, 1f));
        commands.ClearRenderTargetView(motionHandle, new Color4(0f, 0f, 0f, 0f));
        commands.ClearDepthStencilView(dsv, ClearFlags.Depth, 1, 0);
        commands.RSSetViewport(0, 0, width, height, 0, 1);
        commands.RSSetScissorRect(new RawRect(0, 0, width, height));
        commands.SetGraphicsRootSignature(root!);
        commands.SetPipelineState(pipeline!);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        if (!current.IsEmpty)
        {
            var vertices = new TemporalVertex[current.VertexCount];
            for (int i = 0; i < vertices.Length; i++) vertices[i] = new(current.GetVertex(i).Position, previous.GetVertex(i).Position);
            var vb = Upload(MemoryMarshal.AsBytes(vertices.AsSpan()));
            var ib = Upload(MemoryMarshal.AsBytes(current.Indices));
            commands.IASetVertexBuffers(0, new VertexBufferView(vb.GPUVirtualAddress, checked(vertices.Length * 24), 24));
            commands.IASetIndexBuffer(new IndexBufferView(ib.GPUVirtualAddress, checked(current.Indices.Length * 4), Format.R32_UInt));
            var currentUnjittered = current.Camera with { ProjectionJitterPixels = Vector2.Zero };
            var previousUnjittered = previous.Camera with { ProjectionJitterPixels = Vector2.Zero };
            commands.SetGraphicsRootConstantBufferView(0, context.UploadConstants(new Constants(current.World, previous.World,
                current.Camera.ViewProjection(width, height), currentUnjittered.ViewProjection(width, height),
                previousUnjittered.ViewProjection(width, height), new(width, height, 0, 0))));
            commands.DrawIndexedInstanced(current.Indices.Length, 1, 0, 0, 0);
        }
        commands.UnsetRenderTargets();
        commands.ResourceBarrierTransition(depth, ResourceStates.RenderTarget, D3D12TemporalInputs.ShaderReadState);
        commands.ResourceBarrierTransition(motion, ResourceStates.RenderTarget, D3D12TemporalInputs.ShaderReadState);
        return new(depth, motion, width, height);

        ID3D12Resource Upload(ReadOnlySpan<byte> bytes)
        {
            var resource = context.Own(device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer(bytes.Length), ResourceStates.GenericRead));
            bytes.CopyTo(resource.Map<byte>(0, bytes.Length));
            resource.Unmap(0);
            runtime.BufferUploads++;
            return resource;
        }
    }

    private void ValidatePair(SceneSnapshot current, SceneSnapshot previous)
    {
        if (current.VertexCount != previous.VertexCount || !current.Indices.SequenceEqual(previous.Indices)
            || current.Materials.Count != previous.Materials.Count)
            throw new NotSupportedException("Temporal history requires stable vertex correspondence, triangle indices and material ranges; reset history after topology changes.");
        for (int i = 0; i < current.Materials.Count; i++)
            if (current.Materials[i].StartIndex != previous.Materials[i].StartIndex || current.Materials[i].IndexCount != previous.Materials[i].IndexCount)
                throw new NotSupportedException("Temporal history requires stable material ranges; reset history after topology changes.");
        ValidateOpaque(current);
        if (!ReferenceEquals(current, previous)) ValidateOpaque(previous);
    }

    private void ValidateOpaque(SceneSnapshot scene)
    {
        var checkedTextures = new HashSet<int>();
        foreach (var material in scene.Materials)
        {
            if (material.IndexCount == 0) continue;
            if (material.Diffuse.W != 1)
                throw new NotSupportedException("Temporal mesh inputs currently require opaque material alpha=1; alpha blending requires reactive and transparency coverage support.");
            CheckTexture(material.DiffuseTexture);
            CheckTexture(material.ToonTexture);
        }
        void CheckTexture(int index)
        {
            if (index < 0 || !checkedTextures.Add(index)) return;
            if (!textureCoverage.GetValue(scene.Textures[index], static texture =>
            {
                var pixels = texture.Rgba;
                for (int a = 3; a < pixels.Length; a += 4)
                    if (pixels[a] != 255) return new(false);
                return new(true);
            }).Opaque)
                throw new NotSupportedException("Temporal mesh inputs currently require fully opaque diffuse/toon textures; alpha-test and blended coverage are unsupported.");
        }
    }

    private void EnsurePipeline()
    {
        if (pipeline is not null) return;
        var flags = ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness | ShaderFlags.OptimizationLevel3;
        var vertex = Compiler.Compile(Source, "VSMain", "TemporalInputs.hlsl", "vs_5_1", flags).ToArray();
        var pixel = Compiler.Compile(Source, "PSMain", "TemporalInputs.hlsl", "ps_5_1", flags).ToArray();
        runtime.ShaderCompilations += 2;
        var createdRoot = runtime.Device.CreateRootSignature<ID3D12RootSignature>(new RootSignatureDescription(
            RootSignatureFlags.AllowInputAssemblerInputLayout,
            [new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All)]), RootSignatureVersion.Version10);
        try
        {
            var createdPipeline = runtime.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
            {
                RootSignature = createdRoot, VertexShader = vertex, PixelShader = pixel,
                InputLayout = new InputLayoutDescription([
                    new("POSITION", 0, Format.R32G32B32_Float, 0, 0), new("POSITION", 1, Format.R32G32B32_Float, 12, 0)]),
                BlendState = BlendDescription.Opaque, SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone,
                DepthStencilState = DepthStencilDescription.Default,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetFormats = [Format.R32_Float, Format.R32G32_Float], DepthStencilFormat = Format.D32_Float,
                SampleDescription = new(1, 0)
            });
            root = createdRoot;
            pipeline = createdPipeline;
            runtime.PipelineCreations++;
        }
        catch { createdRoot.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (disposed) return;
        if (runtime.IsRecording) throw new InvalidOperationException("Submit or abort the open frame before disposing temporal inputs.");
        runtime.WaitForIdle();
        DisposeCharacterInputs();
        pipeline?.Dispose();
        root?.Dispose();
        disposed = true;
    }

    private const string Source = """
        cbuffer TemporalConstants : register(b0) {
            row_major float4x4 CurrentWorld;
            row_major float4x4 PreviousWorld;
            row_major float4x4 RasterViewProjection;
            row_major float4x4 CurrentViewProjection;
            row_major float4x4 PreviousViewProjection;
            float4 Extent;
        };
        struct Input { float3 Current : POSITION0; float3 Previous : POSITION1; };
        struct Vertex { float4 Position : SV_POSITION; float4 Current : TEXCOORD0; float4 Previous : TEXCOORD1; };
        Vertex VSMain(Input input) {
            Vertex output;
            float4 currentWorld = mul(float4(input.Current, 1), CurrentWorld);
            output.Position = mul(currentWorld, RasterViewProjection);
            output.Current = mul(currentWorld, CurrentViewProjection);
            output.Previous = mul(mul(float4(input.Previous, 1), PreviousWorld), PreviousViewProjection);
            return output;
        }
        struct Targets { float Depth : SV_Target0; float2 Motion : SV_Target1; };
        Targets PSMain(Vertex input) {
            Targets output;
            // SV_POSITION.z is raster device z/w, with the same current visibility as the DSV.
            output.Depth = input.Position.z;
            float previousW = abs(input.Previous.w) < 1e-6 ? (input.Previous.w < 0 ? -1e-6 : 1e-6) : input.Previous.w;
            float2 currentNdc = input.Current.xy / input.Current.w;
            float2 previousNdc = input.Previous.xy / previousW;
            // NDC +Y is up; texture +Y is down. Jitter was removed in both motion matrices.
            output.Motion = (previousNdc - currentNdc) * float2(.5, -.5) * Extent.xy;
            return output;
        }
        """;
}
