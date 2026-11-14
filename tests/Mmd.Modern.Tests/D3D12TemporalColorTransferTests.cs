using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12TemporalColorTransferTests
{
    [Theory]
    [InlineData(257, 5)]
    [InlineData(1, 1)]
    [InlineData(1, 257)]
    [InlineData(257, 1)]
    public void ActualGpuSrgbTransferRoundTripsEveryByteWithoutResampling(int width, int height)
    {
        byte[] pixels = Pixels(width, height);
        using var runtime = new D3D12GpuRuntime();
        using var cache = new D3D12GpuCache(runtime);
        var context = runtime.Begin();
        try
        {
            var transfer = new D3D12TemporalColorTransfer(runtime, cache);
            var decoded = transfer.Decode(context, Input(cache, pixels, width, height));
            Assert.Equal(Format.R16G16B16A16_Float, decoded.Format);
            Assert.Equal(width, decoded.Width); Assert.Equal(height, decoded.Height);
            var decodedReadback = CopyForReadback(context, decoded);
            // Vendor temporal output is an UAV. Exercise its transition into the pixel transfer pass.
            var uavResource = context.Own(context.Device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Texture2D(decoded.Format, (uint)width, (uint)height, mipLevels: 1, flags: ResourceFlags.AllowUnorderedAccess),
                ResourceStates.CopyDest));
            context.Commands.CopyResource(uavResource, decoded.Resource);
            var uavOutput = new D3D12GpuTarget("synthetic-temporal-uav", uavResource, default, width, height, decoded.Format, false)
                { State = ResourceStates.CopyDest, Initialized = true };
            context.Transition(uavOutput, ResourceStates.UnorderedAccess);
            var encoded = transfer.Encode(context, uavOutput);
            Assert.Equal(Format.R8G8B8A8_UNorm, encoded.Format);
            Assert.Equal(width, encoded.Width); Assert.Equal(height, encoded.Height);
            var encodedReadback = CopyForReadback(context, encoded);
            runtime.Wait(runtime.Submit());
            byte[] linear = Read(decodedReadback, width * 8, height);
            byte[] result = Read(encodedReadback, width * 4, height);
            for (int i = 0; i < pixels.Length; i++)
            {
                // Independent double-precision reference checks both sides of the sRGB breakpoint (10/11).
                double display = pixels[i] / 255.0;
                double expected = i % 4 == 3 ? display : display <= .04045 ? display / 12.92 : Math.Pow((display + .055) / 1.055, 2.4);
                double actual = (double)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(linear, i * 2));
                Assert.InRange(Math.Abs(actual - expected), 0, Math.Max(2e-7, expected * .001));
                Assert.InRange(Math.Abs(result[i] - pixels[i]), 0, 1);
            }
            Assert.Equal(pixels, result);
        }
        finally { runtime.Abort(); }
    }

    [Fact]
    public void RepeatedGpuOnlyTransfersHaveBoundedResourcesAndCachedPipelines()
    {
        using var runtime = new D3D12GpuRuntime();
        using var cache = new D3D12GpuCache(runtime);
        var transfer = new D3D12TemporalColorTransfer(runtime, cache);
        int maximumResources = 0;
        for (int frame = 0; frame < 24; frame++)
        {
            var context = runtime.Begin();
            try
            {
                var source = Input(cache, Pixels(13, 9), 13, 9);
                var output = transfer.Encode(context, transfer.Decode(context, source));
                Assert.True(output.Initialized);
                Assert.Equal(ResourceStates.RenderTarget, output.State);
                Assert.Equal(3, runtime.ShaderCompilations);
                Assert.Equal(2, runtime.PipelineCreations);
                Assert.Equal(4, cache.LiveResourceCount); // One source texture, one root signature, two PSOs.
                Assert.Equal(0, runtime.Readbacks);
                runtime.Submit();
                Assert.InRange(runtime.PendingFrames, 0, 3);
                maximumResources = Math.Max(maximumResources, runtime.LiveFrameResources);
            }
            finally { runtime.Abort(); }
        }
        Assert.InRange(maximumResources, 1, 24);
        runtime.WaitForIdle();
        Assert.Equal(0, runtime.LiveFrameResources);
        Assert.Equal(24, runtime.Submissions);
        Assert.Equal(0, runtime.Readbacks);
    }

    private static byte[] Pixels(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int p = 0; p < width * height; p++)
        {
            int i = p * 4;
            byte value = (byte)(p % 256);
            pixels[i] = value;
            pixels[i + 1] = p / 256 % 2 == 0 ? value : (byte)(255 - value);
            pixels[i + 2] = p / 256 % 2 == 0 ? value : (byte)((p * 73) % 256);
            pixels[i + 3] = (byte)(p % 3 == 0 ? 255 : value);
        }
        return pixels;
    }

    private static D3D12GpuTarget Input(D3D12GpuCache cache, byte[] pixels, int width, int height)
        => new("color-transfer-input", cache.Texture(new("color-transfer-input", width, height, pixels)), default,
            width, height, Format.R8G8B8A8_UNorm, false) { State = ResourceStates.PixelShaderResource, Initialized = true };

    private static (ID3D12Resource Resource, PlacedSubresourceFootPrint Layout, int Size) CopyForReadback(D3D12RecordingContext context, D3D12GpuTarget target)
    {
        context.Transition(target, ResourceStates.CopySource);
        var footprints = new PlacedSubresourceFootPrint[1];
        context.Device.GetCopyableFootprints(target.Resource.Description, 0, 1, 0, footprints, new int[1], new ulong[1], out ulong size);
        var readback = context.Own(context.Device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(size), ResourceStates.CopyDest));
        context.Commands.CopyTextureRegion(new TextureCopyLocation(readback, footprints[0]), 0, 0, 0, new TextureCopyLocation(target.Resource, 0));
        return (readback, footprints[0], checked((int)size));
    }

    private static byte[] Read((ID3D12Resource Resource, PlacedSubresourceFootPrint Layout, int Size) source, int rowBytes, int height)
    {
        var mapped = source.Resource.Map<byte>(0, source.Size);
        byte[] result = new byte[rowBytes * height];
        try
        {
            for (int y = 0; y < height; y++)
                mapped.Slice(y * source.Layout.Footprint.RowPitch, rowBytes).CopyTo(result.AsSpan(y * rowBytes, rowBytes));
        }
        finally { source.Resource.Unmap(0); }
        return result;
    }
}
