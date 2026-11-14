using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12FsrTemporalDispatchTests(ITestOutputHelper output)
{
    private static string Loader => Environment.GetEnvironmentVariable("MMD_FSR_LOADER_PATH")
        ?? throw new InvalidOperationException("Set MMD_FSR_LOADER_PATH to the optional signed SDK loader DLL for this explicit GPU test.");

    [Fact]
    public void ActualDefaultProviderDispatchPreservesLinearConstantColorAndRetainsHistoryWithoutReadback()
    {
        using var runtime = new D3D12GpuRuntime();
        using var inputs = new D3D12TemporalInputPass(runtime);
        using var cache = new D3D12GpuCache(runtime);
        using var pass = new D3D12FsrTemporalPass(runtime, Loader, 64, 48, 96, 72);
        var scene = SceneSnapshot.CreateEmpty();
        D3D12GpuTarget? color = null;
        byte[] pixels = new byte[64 * 48 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 41; pixels[i + 1] = 88; pixels[i + 2] = 177; pixels[i + 3] = 255; }
        for (int frame = 0; frame < 4; frame++)
        {
            var context = runtime.Begin();
            var temporal = inputs.Record(context, scene, scene, 64, 48);
            color ??= new D3D12GpuTarget("linear-constant", cache.Texture(new("constant", 64, 48, pixels)), default,
                64, 48, Format.R8G8B8A8_UNorm, false) { State = ResourceStates.PixelShaderResource, Initialized = true };
            var result = pass.Record(context, color, temporal, Vector2.Zero, 1000f / 60, false, .1f, 100, MathF.PI / 4);
            Assert.Equal(frame == 0, pass.LastReset);
            (ID3D12Resource Buffer, PlacedSubresourceFootPrint Layout, int Bytes)? copy = frame == 3 ? Copy(context, result) : null;
            runtime.Wait(runtime.Submit());
            if (copy is not null)
            {
                var image = Read(copy.Value, 96, 72);
                for (int i = 0; i < image.Length; i += 4)
                { Assert.InRange(image[i], 39, 43); Assert.InRange(image[i + 1], 86, 90); Assert.InRange(image[i + 2], 175, 179); }
            }
        }
        Assert.Equal(4, pass.RecordedDispatches);
        Assert.Equal(4, runtime.Submissions); Assert.Equal(0, runtime.Readbacks);
        Assert.NotEqual(0ul, pass.ProviderId); Assert.False(string.IsNullOrWhiteSpace(pass.ProviderName));
        output.WriteLine($"Adapter={runtime.AdapterName}; provider={pass.ProviderName}; id={pass.ProviderId}; ffxDispatch={pass.RecordedDispatches}; completedFence={runtime.CompletedFence}");
    }

    [Fact]
    public void ActualGpuMovingOpaqueGeometryUsesDepthMotionHistoryAndCameraJitter()
    {
        using var runtime = new D3D12GpuRuntime();
        using var inputs = new D3D12TemporalInputPass(runtime);
        using var pass = new D3D12FsrTemporalPass(runtime, Loader, 64, 48, 96, 72);
        var previous = Scene(0, Vector2.Zero);
        byte[]? first = null, last = null;
        for (int i = 0; i < 8; i++)
        {
            Vector2 jitter = new((i % 2 == 0 ? -.25f : .25f), (i % 3 - 1) / 3f);
            var current = Scene(i * .07f, jitter);
            var context = runtime.Begin();
            try
            {
                var temporal = inputs.Record(context, current, previous, 64, 48);
                // GPU-derived opaque test color shares exact raster coverage with depth. No synthesized motion or CPU image upload.
                var color = ColorFromDepth(context, temporal);
                var result = pass.Record(context, color, temporal, jitter, 1000f / 60, false, 1, 11, MathF.PI / 4);
                var copy = Copy(context, result); runtime.Wait(runtime.Submit());
                var image = Read(copy, 96, 72); first ??= image; last = image;
                Assert.True(image.Where((_, index) => index % 4 == 0).Count(value => value > 128) > 25);
                Assert.Equal(i == 0, pass.LastReset);
            }
            finally { runtime.Abort(); }
            previous = current;
        }
        double Centroid(byte[] image)
        {
            double total = 0, weighted = 0;
            for (int y = 0; y < 72; y++) for (int x = 0; x < 96; x++)
            { double red = image[(y * 96 + x) * 4]; total += red; weighted += red * x; }
            return weighted / total;
        }
        double movement = Centroid(last!) - Centroid(first!);
        // Independent perspective projection: x travel .49 world units at view z5 gives .49*cot(pi/8)/5*72/2 = 8.52 output pixels.
        Assert.InRange(movement, 6.5, 10.5);
        Assert.Equal(8, pass.RecordedDispatches); Assert.Equal(0, runtime.Readbacks);
        output.WriteLine($"provider={pass.ProviderName}; GPU temporal frames=8; centroid movement={movement:F3} pixels; completedFence={runtime.CompletedFence}");
    }

    private static SceneSnapshot Scene(float x, Vector2 jitter)
    {
        SceneVertex[] vertices = [new(new(-.8f + x, -.8f, 0), -Vector3.UnitZ, Vector2.Zero),
            new(new(.8f + x, -.8f, 0), -Vector3.UnitZ, Vector2.Zero), new(new(x, .8f, 0), -Vector3.UnitZ, Vector2.Zero)];
        return new(vertices, new uint[] { 0, 1, 2 }, [new("opaque", 0, 3, Vector4.One, Vector3.Zero, Vector3.One, 1)],
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 2, 1, 11, LeftHanded: true)
            { VerticalFovDegrees = 45, ProjectionJitterPixels = jitter });
    }

    [Fact]
    public void AbortedFirstNativeDispatchRequiresFreshHistoryAndFreshContextRecoversOnGpu()
    {
        using var runtime = new D3D12GpuRuntime();
        using var inputs = new D3D12TemporalInputPass(runtime);
        using var cache = new D3D12GpuCache(runtime);
        var scene = SceneSnapshot.CreateEmpty();
        byte[] pixels = new byte[64 * 48 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 128; pixels[i + 3] = 255; }
        D3D12GpuTarget Record(D3D12FsrTemporalPass pass, D3D12RecordingContext context)
        {
            var temporal = inputs.Record(context, scene, scene, 64, 48);
            var color = new D3D12GpuTarget("abort-linear", cache.Texture(new("abort", 64, 48, pixels)), default,
                64, 48, Format.R8G8B8A8_UNorm, false) { State = ResourceStates.PixelShaderResource, Initialized = true };
            return pass.Record(context, color, temporal, Vector2.Zero, 16, false, .1f, 100, MathF.PI / 4);
        }
        using (var aborted = new D3D12FsrTemporalPass(runtime, Loader, 64, 48, 96, 72))
        {
            try { Record(aborted, runtime.Begin()); }
            finally { runtime.Abort(); }
            Assert.True(aborted.RequiresRecreation); Assert.Equal(0, runtime.Submissions);
            var context = runtime.Begin();
            try { Assert.Throws<InvalidOperationException>(() => Record(aborted, context)); }
            finally { runtime.Abort(); }
        }
        using var recovered = new D3D12FsrTemporalPass(runtime, Loader, 64, 48, 96, 72);
        var recording = runtime.Begin();
        try
        {
            var result = Record(recovered, recording); var copy = Copy(recording, result);
            runtime.Wait(runtime.Submit()); var image = Read(copy, 96, 72);
            for (int i = 0; i < image.Length; i += 4)
            { Assert.InRange(image[i], 126, 130); Assert.InRange(image[i + 1], 0, 2); Assert.InRange(image[i + 2], 0, 2); }
            Assert.True(recovered.LastReset); Assert.False(recovered.RequiresRecreation);
        }
        finally { runtime.Abort(); }
    }

    private static D3D12GpuTarget ColorFromDepth(D3D12RecordingContext context, D3D12TemporalInputs inputs)
    {
        const string shader = """
            Texture2D<float> Depth : register(t0);
            float4 VS(uint id : SV_VertexID) : SV_Position {
                float2 uv = float2((id << 1) & 2, id & 2); return float4(uv * float2(2,-2) + float2(-1,1),0,1);
            }
            float4 PS(float4 pos : SV_Position) : SV_Target { return float4(Depth.Load(int3(pos.xy,0)) < .999 ? 1 : 0,0,0,1); }
            """;
        var device = context.Device;
        var root = context.Own(device.CreateRootSignature<ID3D12RootSignature>(new RootSignatureDescription(RootSignatureFlags.None,
            [new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 0)), ShaderVisibility.Pixel)]), RootSignatureVersion.Version10));
        var pso = context.Own(device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = root,
            VertexShader = Compiler.Compile(shader, "VS", "temporal-test-color.hlsl", "vs_5_1").ToArray(),
            PixelShader = Compiler.Compile(shader, "PS", "temporal-test-color.hlsl", "ps_5_1").ToArray(),
            BlendState = BlendDescription.Opaque, SampleMask = uint.MaxValue, RasterizerState = RasterizerDescription.CullNone,
            DepthStencilState = DepthStencilDescription.None, PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = [Format.R8G8B8A8_UNorm], SampleDescription = new(1, 0)
        }));
        var rtv = context.Own(device.CreateDescriptorHeap(new(DescriptorHeapType.RenderTargetView, 1)));
        var srv = context.Own(device.CreateDescriptorHeap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, DescriptorHeapFlags.ShaderVisible)));
        var resource = context.Own(device.CreateCommittedResource(HeapType.Default,
            ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, (uint)inputs.Width, (uint)inputs.Height, mipLevels: 1, flags: ResourceFlags.AllowRenderTarget), ResourceStates.RenderTarget));
        device.CreateRenderTargetView(resource, null, rtv.GetCPUDescriptorHandleForHeapStart());
        device.CreateShaderResourceView(inputs.Depth, null, srv.GetCPUDescriptorHandleForHeapStart());
        var commands = context.Commands; commands.SetDescriptorHeaps(srv); commands.SetGraphicsRootSignature(root); commands.SetPipelineState(pso);
        commands.SetGraphicsRootDescriptorTable(0, srv.GetGPUDescriptorHandleForHeapStart());
        commands.OMSetRenderTargets(rtv.GetCPUDescriptorHandleForHeapStart()); commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.RSSetViewport(0, 0, inputs.Width, inputs.Height); commands.RSSetScissorRect(new RawRect(0, 0, inputs.Width, inputs.Height));
        commands.DrawInstanced(3, 1, 0, 0); commands.UnsetRenderTargets();
        return new("linear-opaque", resource, rtv.GetCPUDescriptorHandleForHeapStart(), inputs.Width, inputs.Height, Format.R8G8B8A8_UNorm, false) { Initialized = true };
    }

    private static (ID3D12Resource Buffer, PlacedSubresourceFootPrint Layout, int Bytes) Copy(D3D12RecordingContext context, D3D12GpuTarget result)
    {
        var layout = new PlacedSubresourceFootPrint[1];
        context.Device.GetCopyableFootprints(result.Resource.Description, 0, 1, 0, layout, new int[1], new ulong[1], out ulong size);
        var buffer = context.Own(context.Device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(size), ResourceStates.CopyDest));
        context.Transition(result, ResourceStates.CopySource);
        context.Commands.CopyTextureRegion(new TextureCopyLocation(buffer, layout[0]), 0, 0, 0, new TextureCopyLocation(result.Resource, 0));
        return (buffer, layout[0], checked((int)size));
    }

    private static byte[] Read((ID3D12Resource Buffer, PlacedSubresourceFootPrint Layout, int Bytes) copy, int width, int height)
    {
        var image = new byte[width * height * 4]; var data = copy.Buffer.Map<byte>(0, copy.Bytes);
        try { for (int y = 0; y < height; y++) data.Slice(y * copy.Layout.Footprint.RowPitch, width * 4).CopyTo(image.AsSpan(y * width * 4)); }
        finally { copy.Buffer.Unmap(0); }
        return image;
    }
}
