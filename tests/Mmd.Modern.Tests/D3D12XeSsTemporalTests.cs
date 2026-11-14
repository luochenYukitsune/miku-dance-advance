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

public sealed class D3D12XeSsAbiTests
{
    [Fact]
    public void PinnedOfficialX64AbiUsesEightBytePackingAndFullExecuteTail()
    {
        D3D12XeSsTemporalPass.ValidateAbi();
        Assert.Equal(8, Marshal.SizeOf<D3D12XeSsTemporalPass.NativeVersion>());
        Assert.Equal(24, Marshal.SizeOf<D3D12XeSsTemporalPass.NativeProperties>());
        Assert.Equal(64, Marshal.SizeOf<D3D12XeSsTemporalPass.InitParameters>());
        Assert.Equal(136, Marshal.SizeOf<D3D12XeSsTemporalPass.ExecuteParameters>());
        Assert.Equal(120, Marshal.OffsetOf<D3D12XeSsTemporalPass.ExecuteParameters>(nameof(D3D12XeSsTemporalPass.ExecuteParameters.DescriptorHeap)).ToInt32());
        Assert.Equal(106, (int)D3D12XeSsQuality.AntiAliasing);
        Assert.Equal(103, (int)D3D12XeSsQuality.Quality);
    }
}

[Trait("Category", "Gpu")]
public sealed class D3D12XeSsTemporalTests(ITestOutputHelper output)
{
    private static string Library => Environment.GetEnvironmentVariable("MMD_XESS_RUNTIME_PATH")
        ?? throw new InvalidOperationException("Set MMD_XESS_RUNTIME_PATH to the optional official libxess.dll for this explicit GPU test.");

    [Fact]
    public void ActualDeviceQueryAndFourConstantFramesExecuteWithoutApplicationReadback()
    {
        using var runtime = new D3D12GpuRuntime();
        using var inputs = new D3D12TemporalInputPass(runtime);
        using var pass = new D3D12XeSsTemporalPass(runtime, Library, 192, 144);
        Assert.True(pass.DeviceQuery.Initialized);
        Assert.NotNull(pass.DeviceQuery.RuntimeVersion);
        Assert.Equal(0, pass.DeviceQuery.InitializeCode);
        var scene = SceneSnapshot.CreateEmpty();
        for (int frame = 0; frame < 4; frame++)
        {
            var context = runtime.Begin();
            try
            {
                var temporal = inputs.Record(context, scene, scene, pass.InputWidth, pass.InputHeight);
                var color = ColorFromDepth(context, temporal, true);
                var result = pass.Record(context, color, temporal, Vector2.Zero, 1, false);
                Assert.Equal(frame == 0, pass.LastReset);
                var copy = frame == 3 ? Copy(context, result) : null;
                runtime.Wait(runtime.Submit());
                if (copy is {} completed)
                {
                    var values = Read(completed, 192, 144);
                    for (int i = 0; i < values.Length; i += 4)
                    {
                        Assert.InRange(values[i], .23f, .27f);
                        Assert.InRange(values[i + 1], .48f, .52f);
                        Assert.InRange(values[i + 2], .73f, .77f);
                    }
                }
            }
            finally { runtime.Abort(); }
        }
        Assert.Equal(4, pass.RecordedDispatches);
        Assert.Equal(4, runtime.Submissions); Assert.Equal(0, runtime.Readbacks);
        output.WriteLine($"Adapter={runtime.AdapterName}; query={pass.DeviceQuery}; execute={pass.RecordedDispatches}; fence={runtime.CompletedFence}");
    }

    [Fact]
    public void EightMovingFramesUseActualDepthBackwardMotionAndJitterThenAbortInvalidatesHistory()
    {
        using var runtime = new D3D12GpuRuntime();
        using var inputs = new D3D12TemporalInputPass(runtime);
        using var pass = new D3D12XeSsTemporalPass(runtime, Library, 192, 144);
        var previous = Scene(0, Vector2.Zero);
        float[]? first = null, last = null;
        for (int frame = 0; frame < 8; frame++)
        {
            Vector2 jitter = new(frame % 2 == 0 ? -.25f : .25f, (frame % 3 - 1) / 3f);
            var current = Scene(frame * .07f, jitter);
            var context = runtime.Begin();
            try
            {
                var temporal = inputs.Record(context, current, previous, pass.InputWidth, pass.InputHeight);
                var result = pass.Record(context, ColorFromDepth(context, temporal, false), temporal, jitter, 1, false);
                var copy = Copy(context, result); runtime.Wait(runtime.Submit());
                var values = Read(copy, 192, 144); first ??= values; last = values;
                Assert.True(values.Where((_, index) => index % 4 == 0).Count(value => value > .5f) > 100);
                Assert.Equal(frame == 0, pass.LastReset);
            }
            finally { runtime.Abort(); }
            previous = current;
        }
        double Centroid(float[] values)
        {
            double total = 0, weighted = 0;
            for (int y = 0; y < 144; y++) for (int x = 0; x < 192; x++)
            { double weight = Math.Max(0, values[(y * 192 + x) * 4]); total += weight; weighted += weight * x; }
            return weighted / total;
        }
        double travel = Centroid(last!) - Centroid(first!);
        // Independent perspective expectation: .49 * cot(pi/8) / 5 * 144 / 2 = 17.03 output pixels.
        Assert.InRange(travel, 13, 21);
        Assert.Equal(8, pass.RecordedDispatches); Assert.Equal(0, runtime.Readbacks);
        var abortedContext = runtime.Begin();
        try
        {
            var temporal = inputs.Record(abortedContext, previous, previous, pass.InputWidth, pass.InputHeight);
            pass.Record(abortedContext, ColorFromDepth(abortedContext, temporal, false), temporal, Vector2.Zero, 1, false);
        }
        finally { runtime.Abort(); }
        Assert.True(pass.RequiresRecreation);
        pass.Dispose();
        using var recovered = new D3D12XeSsTemporalPass(runtime, Library, 192, 144);
        var recoveredContext = runtime.Begin();
        try
        {
            var temporal = inputs.Record(recoveredContext, previous, previous, recovered.InputWidth, recovered.InputHeight);
            var result = recovered.Record(recoveredContext, ColorFromDepth(recoveredContext, temporal, true), temporal, Vector2.Zero, 1, false);
            var copy = Copy(recoveredContext, result); runtime.Wait(runtime.Submit());
            var values = Read(copy, 192, 144);
            Assert.InRange(values[values.Length / 2], .23f, .27f);
            Assert.True(recovered.LastReset); Assert.False(recovered.RequiresRecreation);
        }
        finally { runtime.Abort(); }
        output.WriteLine($"query={pass.DeviceQuery}; moving frames=8; centroid travel={travel:F3}; aborted history invalidated");
    }

    private static SceneSnapshot Scene(float x, Vector2 jitter)
    {
        SceneVertex[] vertices = [new(new(-.8f + x, -.8f, 0), -Vector3.UnitZ, Vector2.Zero),
            new(new(.8f + x, -.8f, 0), -Vector3.UnitZ, Vector2.Zero), new(new(x, .8f, 0), -Vector3.UnitZ, Vector2.Zero)];
        return new(vertices, new uint[] { 0, 1, 2 }, [new("opaque", 0, 3, Vector4.One, Vector3.Zero, Vector3.One, 1)],
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 2, 1, 11, LeftHanded: true)
            { VerticalFovDegrees = 45, ProjectionJitterPixels = jitter });
    }

    private static D3D12GpuTarget ColorFromDepth(D3D12RecordingContext context, D3D12TemporalInputs inputs, bool constant)
    {
        string shader = """
            Texture2D<float> Depth : register(t0);
            float4 VS(uint id : SV_VertexID) : SV_Position {
                float2 uv = float2((id << 1) & 2, id & 2); return float4(uv * float2(2,-2) + float2(-1,1),0,1);
            }
            float4 PS(float4 pos : SV_Position) : SV_Target { COLOR }
            """.Replace("COLOR", constant ? "return float4(.25,.5,.75,1);" : "return float4(Depth.Load(int3(pos.xy,0)) < .999 ? 1 : 0,0,0,1);");
        var device = context.Device;
        var root = context.Own(device.CreateRootSignature<ID3D12RootSignature>(new RootSignatureDescription(RootSignatureFlags.None,
            [new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 0)), ShaderVisibility.Pixel)]), RootSignatureVersion.Version10));
        var pso = context.Own(device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = root, VertexShader = Compiler.Compile(shader, "VS", "xess-test-color.hlsl", "vs_5_1").ToArray(),
            PixelShader = Compiler.Compile(shader, "PS", "xess-test-color.hlsl", "ps_5_1").ToArray(),
            BlendState = BlendDescription.Opaque, SampleMask = uint.MaxValue, RasterizerState = RasterizerDescription.CullNone,
            DepthStencilState = DepthStencilDescription.None, PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = [Format.R16G16B16A16_Float], SampleDescription = new(1, 0)
        }));
        var rtv = context.Own(device.CreateDescriptorHeap(new(DescriptorHeapType.RenderTargetView, 1)));
        var srv = context.Own(device.CreateDescriptorHeap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, DescriptorHeapFlags.ShaderVisible)));
        var resource = context.Own(device.CreateCommittedResource(HeapType.Default,
            ResourceDescription.Texture2D(Format.R16G16B16A16_Float, (uint)inputs.Width, (uint)inputs.Height, mipLevels: 1, flags: ResourceFlags.AllowRenderTarget), ResourceStates.RenderTarget));
        device.CreateRenderTargetView(resource, null, rtv.GetCPUDescriptorHandleForHeapStart());
        device.CreateShaderResourceView(inputs.Depth, null, srv.GetCPUDescriptorHandleForHeapStart());
        var commands = context.Commands; commands.SetDescriptorHeaps(srv); commands.SetGraphicsRootSignature(root); commands.SetPipelineState(pso);
        commands.SetGraphicsRootDescriptorTable(0, srv.GetGPUDescriptorHandleForHeapStart());
        commands.OMSetRenderTargets(rtv.GetCPUDescriptorHandleForHeapStart()); commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.RSSetViewport(0, 0, inputs.Width, inputs.Height); commands.RSSetScissorRect(new RawRect(0, 0, inputs.Width, inputs.Height));
        commands.DrawInstanced(3, 1, 0, 0); commands.UnsetRenderTargets();
        return new("xess-linear", resource, rtv.GetCPUDescriptorHandleForHeapStart(), inputs.Width, inputs.Height, Format.R16G16B16A16_Float, false) { Initialized = true };
    }

    private sealed record Readback(ID3D12Resource Buffer, PlacedSubresourceFootPrint Layout, int Bytes);
    private static Readback Copy(D3D12RecordingContext context, D3D12GpuTarget result)
    {
        var layout = new PlacedSubresourceFootPrint[1];
        context.Device.GetCopyableFootprints(result.Resource.Description, 0, 1, 0, layout, new int[1], new ulong[1], out ulong size);
        var buffer = context.Own(context.Device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(size), ResourceStates.CopyDest));
        context.Transition(result, ResourceStates.CopySource);
        context.Commands.CopyTextureRegion(new TextureCopyLocation(buffer, layout[0]), 0, 0, 0, new TextureCopyLocation(result.Resource, 0));
        return new(buffer, layout[0], checked((int)size));
    }
    private static float[] Read(Readback copy, int width, int height)
    {
        var image = new float[width * height * 4]; var bytes = copy.Buffer.Map<byte>(0, copy.Bytes);
        try
        {
            for (int y = 0; y < height; y++) for (int x = 0; x < width * 4; x++)
                image[y * width * 4 + x] = (float)BitConverter.UInt16BitsToHalf(MemoryMarshal.Read<ushort>(bytes.Slice(y * copy.Layout.Footprint.RowPitch + x * 2, 2)));
        }
        finally { copy.Buffer.Unmap(0); }
        Assert.All(image, value => Assert.True(float.IsFinite(value)));
        return image;
    }
}
