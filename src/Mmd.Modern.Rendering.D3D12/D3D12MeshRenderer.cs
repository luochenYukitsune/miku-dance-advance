using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;
using Vortice.Mathematics;
using D3D12Api = Vortice.Direct3D12.D3D12;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Real indexed offscreen mesh path, independent of the fullscreen effect bootstrap.
/// Frame resources live until the submitted fence completes; readback is screenshot/CI only.</summary>
public sealed class D3D12MeshRenderer : IRenderBackend
{
    private readonly ID3D12Device device;
    private readonly ID3D12CommandQueue queue;
    private readonly ID3D12CommandAllocator allocator;
    private readonly ID3D12GraphicsCommandList commands;
    private readonly ID3D12Fence fence;
    private readonly ID3D12RootSignature root;
    private readonly ID3D12PipelineState pipeline;
    private readonly ID3D12InfoQueue? debugQueue;
    private ulong fenceValue;
    private bool disposed, rendering;
    public string AdapterName { get; }
    public bool IsWarp { get; }
    public FeatureLevel FeatureLevel { get; }
    public int LastVertexCount { get; private set; }
    public int LastIndexCount { get; private set; }
    public int LastUploadedTextures { get; private set; }
    public IReadOnlyList<string> LastDebugMessages { get; private set; } = [];

    public D3D12MeshRenderer()
    {
        _ = D3D12DebugLayerBootstrap.BeforeDeviceCreation(Environment.GetEnvironmentVariable("MMD_D3D12_DEBUG") == "1");
        (device, AdapterName, IsWarp) = CreateDevice();
        FeatureLevel = device.CheckMaxSupportedFeatureLevel();
        if (Environment.GetEnvironmentVariable("MMD_D3D12_DEBUG") == "1") debugQueue = device.QueryInterfaceOrNull<ID3D12InfoQueue>();
        queue = device.CreateCommandQueue(CommandListType.Direct);
        allocator = device.CreateCommandAllocator(CommandListType.Direct);
        commands = device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, allocator, null);
        commands.Close(); fence = device.CreateFence(0, FenceFlags.None);
        RootParameter[] parameters = [
            new(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 0)), ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 1)), ShaderVisibility.Pixel)];
        var sampler = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
        {
            Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
        };
        root = device.CreateRootSignature<ID3D12RootSignature>(new RootSignatureDescription(
            RootSignatureFlags.AllowInputAssemblerInputLayout, parameters, [sampler]), RootSignatureVersion.Version10);
        var shader = MeshShaders.Compile();
        pipeline = device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = root, VertexShader = shader.Vertex, PixelShader = shader.Pixel,
            InputLayout = new InputLayoutDescription([
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 24, 0)]),
            BlendState = BlendDescription.Opaque, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone,
            DepthStencilState = DepthStencilDescription.Default,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = [Format.R8G8B8A8_UNorm], DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0), IndexBufferStripCutValue = IndexBufferStripCutValue.Disabled,
        });
    }

    public RenderedFrame Render(SceneSnapshot scene, int width, int height)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ArgumentNullException.ThrowIfNull(scene);
        if (width is < 1 or > 4096 || height is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(width));
        if (rendering) throw new InvalidOperationException("Reentrant/concurrent rendering is unsupported.");
        rendering = true;
        var resources = new List<IDisposable>();
        T Own<T>(T value) where T : IDisposable { resources.Add(value); return value; }
        bool open = false, submitted = false;
        try
        {
            debugQueue?.ClearStoredMessages();
            LastVertexCount = scene.VertexCount; LastIndexCount = scene.Indices.Length;
            using var rtvHeap = device.CreateDescriptorHeap(new(DescriptorHeapType.RenderTargetView, 1));
            using var dsvHeap = device.CreateDescriptorHeap(new(DescriptorHeapType.DepthStencilView, 1));
            using var srvHeap = device.CreateDescriptorHeap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, scene.Textures.Count + 2, DescriptorHeapFlags.ShaderVisible));
            int srvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            var color = Own(device.CreateCommittedResource(HeapType.Default, ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, (uint)width, (uint)height, mipLevels: 1, flags: ResourceFlags.AllowRenderTarget), ResourceStates.RenderTarget));
            var depth = Own(device.CreateCommittedResource(HeapType.Default, ResourceDescription.Texture2D(Format.D32_Float, (uint)width, (uint)height, mipLevels: 1, flags: ResourceFlags.AllowDepthStencil), ResourceStates.DepthWrite));
            var rtv = rtvHeap.GetCPUDescriptorHandleForHeapStart(); var dsv = dsvHeap.GetCPUDescriptorHandleForHeapStart();
            device.CreateRenderTargetView(color, null, rtv); device.CreateDepthStencilView(depth, null, dsv);
            allocator.Reset(); commands.Reset(allocator, pipeline); open = true;
            commands.SetGraphicsRootSignature(root); commands.SetDescriptorHeaps(srvHeap);

            // Default-heap VB/IB; upload resources are retained with the frame until the fence.
            ID3D12Resource UploadBuffer(ReadOnlySpan<byte> bytes, ResourceStates finalState)
            {
                var upload = Own(device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer((ulong)bytes.Length), ResourceStates.GenericRead));
                var mapped = upload.Map<byte>(0, bytes.Length); bytes.CopyTo(mapped); upload.Unmap(0);
                var gpu = Own(device.CreateCommittedResource(HeapType.Default, ResourceDescription.Buffer((ulong)bytes.Length), ResourceStates.CopyDest));
                commands.CopyBufferRegion(gpu, 0, upload, 0, (ulong)bytes.Length);
                commands.ResourceBarrierTransition(gpu, ResourceStates.CopyDest, finalState); return gpu;
            }
            ID3D12Resource UploadSceneVertices()
            {
                int bytes=checked(scene.VertexCount*Marshal.SizeOf<SceneVertex>());
                var upload=Own(device.CreateCommittedResource(HeapType.Upload,ResourceDescription.Buffer((ulong)bytes),ResourceStates.GenericRead));
                scene.CopyVertices(0,upload.Map<SceneVertex>(0,scene.VertexCount));upload.Unmap(0);
                var gpu=Own(device.CreateCommittedResource(HeapType.Default,ResourceDescription.Buffer((ulong)bytes),ResourceStates.CopyDest));
                commands.CopyBufferRegion(gpu,0,upload,0,(ulong)bytes);
                commands.ResourceBarrierTransition(gpu,ResourceStates.CopyDest,ResourceStates.VertexAndConstantBuffer);return gpu;
            }
            var vb = scene.Indices.IsEmpty ? null : UploadSceneVertices();
            var ib = scene.Indices.IsEmpty ? null : UploadBuffer(MemoryMarshal.AsBytes(scene.Indices), ResourceStates.IndexBuffer);
            void UploadTexture(SceneTexture texture, int index)
            {
                var description = ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, (uint)texture.Width, (uint)texture.Height, mipLevels: 1);
                var gpu = Own(device.CreateCommittedResource(HeapType.Default, description, ResourceStates.CopyDest));
                var layout = new PlacedSubresourceFootPrint[1]; var rows = new int[1]; var rowSizes = new ulong[1];
                device.GetCopyableFootprints(description, 0, 1, 0, layout, rows, rowSizes, out ulong total);
                var upload = Own(device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer(total), ResourceStates.GenericRead));
                var mapped = upload.Map<byte>(0, checked((int)total));
                for (int y = 0; y < texture.Height; y++) texture.Rgba.Slice(y * texture.Width * 4, texture.Width * 4).CopyTo(mapped.Slice(y * layout[0].Footprint.RowPitch, texture.Width * 4));
                upload.Unmap(0);
                commands.CopyTextureRegion(new TextureCopyLocation(gpu, 0), 0, 0, 0, new TextureCopyLocation(upload, layout[0]));
                commands.ResourceBarrierTransition(gpu, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
                device.CreateShaderResourceView(gpu, null, srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(index, srvStride));
            }
            UploadTexture(new("white-fallback", 1, 1, [255,255,255,255]), 0);
            UploadTexture(SceneTexture.ProceduralToon(), 1);
            for (int i = 0; i < scene.Textures.Count; i++) UploadTexture(scene.Textures[i], i + 2);
            LastUploadedTextures = scene.Textures.Count + 2;
            commands.OMSetRenderTargets(rtv, dsv);
            commands.ClearRenderTargetView(rtv, new Color4(20f/255, 24f/255, 32f/255, 1));
            commands.ClearDepthStencilView(dsv, ClearFlags.Depth, 1, 0);
            commands.RSSetViewport(0, 0, width, height, 0, 1);
            commands.RSSetScissorRect(new RawRect(0, 0, width, height));
            commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            if(vb is not null && ib is not null)
            {
                commands.IASetVertexBuffers(0, new VertexBufferView(vb.GPUVirtualAddress, checked(scene.VertexCount * 32), 32));
                commands.IASetIndexBuffer(new IndexBufferView(ib.GPUVirtualAddress, checked(scene.Indices.Length * 4), Format.R32_UInt));
            }
            Matrix4x4.Invert(scene.World, out var inverse);
            var normal = Matrix4x4.Transpose(inverse);
            int draws = 0;
            foreach (var material in scene.Materials)
            {
                if (material.IndexCount == 0) continue;
                var constants = Own(device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer(512), ResourceStates.GenericRead));
                constants.SetData(new MeshConstants(scene.World, normal, scene.Camera.ViewProjection(width, height), material.Diffuse,
                    new(material.Ambient, material.Shininess), new(material.Specular, 0), new(scene.LightDirection, 0),
                    material.ToonUvTransform ?? new Vector4(1,0,0,.5f), new(material.AlphaCutoff,0,0,0)));
                commands.SetGraphicsRootConstantBufferView(0, constants.GPUVirtualAddress);
                commands.SetGraphicsRootDescriptorTable(1, srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(material.DiffuseTexture < 0 ? 0 : material.DiffuseTexture + 2, srvStride));
                commands.SetGraphicsRootDescriptorTable(2, srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(material.ToonTexture < 0 ? 1 : material.ToonTexture + 2, srvStride));
                commands.DrawIndexedInstanced(material.IndexCount, 1, material.StartIndex, 0, 0); draws++;
            }
            var footprints = new PlacedSubresourceFootPrint[1]; var rowCounts = new int[1]; var rowBytes = new ulong[1];
            device.GetCopyableFootprints(color.Description, 0, 1, 0, footprints, rowCounts, rowBytes, out ulong totalBytes);
            var readback = Own(device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(totalBytes), ResourceStates.CopyDest));
            commands.UnsetRenderTargets(); commands.ResourceBarrierTransition(color, ResourceStates.RenderTarget, ResourceStates.CopySource);
            commands.CopyTextureRegion(new TextureCopyLocation(readback, footprints[0]), 0, 0, 0, new TextureCopyLocation(color, 0));
            commands.Close(); open = false; queue.ExecuteCommandList(commands); submitted = true; WaitForGpu(); submitted = false;
            byte[] rgba = new byte[checked(width * height * 4)];
            var data = readback.Map<byte>(0, checked((int)totalBytes));
            try
            {
                for (int y = 0; y < height; y++) data.Slice(y * footprints[0].Footprint.RowPitch, width * 4).CopyTo(rgba.AsSpan(y * width * 4, width * 4));
            }
            finally { readback.Unmap(0); }
            return new(width, height, rgba, "Direct3D12Mesh", $"{AdapterName} ({FeatureLevel})", draws);
        }
        finally
        {
            if (open) { try { commands.Close(); } catch { /* Preserve original recording error. */ } }
            if (submitted) WaitForGpu();
            if (debugQueue is not null)
            {
                var messages = new List<string>();
                for (ulong i = 0; i < debugQueue.NumStoredMessages; i++) messages.Add(debugQueue.GetMessage(i).ToString());
                LastDebugMessages = messages.AsReadOnly();
            }
            for (int i = resources.Count - 1; i >= 0; i--) resources[i].Dispose();
            rendering = false;
        }
    }

    private void WaitForGpu()
    {
        queue.Signal(fence, ++fenceValue).CheckError();
        while (fence.CompletedValue < fenceValue)
        {
            if (fence.CompletedValue == ulong.MaxValue) throw new InvalidOperationException("D3D12 mesh device removed while waiting for the GPU.");
            Thread.Sleep(1);
        }
    }

    private static (ID3D12Device Device, string Name, bool Warp) CreateDevice()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
        for (int i = 0; factory.EnumAdapters1(i, out var adapter).Success && adapter is not null; i++)
        {
            using (adapter)
                if ((adapter.Description1.Flags & AdapterFlags.Software) == 0
                    && D3D12Api.D3D12CreateDevice(adapter, FeatureLevel.Level_11_0, out ID3D12Device? hardware).Success && hardware is not null)
                    return (hardware, adapter.Description1.Description, false);
        }
        using var warp = factory.EnumWarpAdapter<IDXGIAdapter1>();
        D3D12Api.D3D12CreateDevice(warp, FeatureLevel.Level_11_0, out ID3D12Device? fallback).CheckError();
        return (fallback ?? throw new InvalidOperationException("D3D12 WARP device unavailable."), warp.Description1.Description, true);
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        // A removed device runs nothing further (its fence reads UINT64_MAX); release instead of throwing.
        try { if (fence.CompletedValue != ulong.MaxValue) WaitForGpu(); }
        finally
        {
            debugQueue?.Dispose(); pipeline.Dispose(); root.Dispose(); commands.Dispose(); allocator.Dispose(); fence.Dispose(); queue.Dispose(); device.Dispose();
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct MeshConstants(Matrix4x4 World, Matrix4x4 NormalWorld, Matrix4x4 ViewProjection,
        Vector4 Diffuse, Vector4 AmbientShininess, Vector4 Specular, Vector4 LightDirection, Vector4 ToonUvTransform, Vector4 AlphaCutoff);
}
