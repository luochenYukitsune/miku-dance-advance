using System.Numerics;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12SmaaTests
{
    [Fact]
    public void ActualGpuSmaaChangesDiagonalAfterSceneAndKeepsOffExact()
    {
        using var session = new D3D12FrameSession();
        var frame = Diagonal();
        var original = session.Capture(frame, 97, 73);
        var filtered = session.Capture(WithSmaa(frame), 97, 73);
        Assert.NotEqual(original.Rgba, filtered.Rgba);
        Assert.Equal(FrameAntialiasing.Smaa1X, session.Statistics!.Antialiasing);
        Assert.Equal(3, session.Statistics.AntialiasingPassDrawCalls);
        Assert.Equal(0, session.Statistics.EffectPassDrawCalls);
        Assert.Equal(1, session.Statistics.SubmissionCount);
        Assert.Equal(1, session.Statistics.ReadbackCount);
        Assert.Equal(new[] { "original-frame", "smaa:edges", "smaa:weights", "smaa:neighborhood" }, session.LastRecordings.Select(r => r.Channel));
        Assert.Equal(original.Rgba, session.Capture(frame, 97, 73).Rgba);
        Assert.Equal(FrameAntialiasing.Off, session.Statistics.Antialiasing);
        Assert.Equal(0, session.Statistics.AntialiasingPassDrawCalls);
    }

    [Theory]
    [InlineData(97, 73)]
    [InlineData(1, 1)]
    public void ThreeActualGpuPassesKeepConstantRgbaAndClearEveryIntermediateChannel(int width, int height)
    {
        var pixels = Pixels(width, height, (_, _) => new byte[] { 41, 88, 177, 113 });
        var result = Filter(pixels, width, height);
        Assert.Equal(pixels, result.Output);
        Assert.All(result.Edges, value => Assert.Equal(0, value));
        Assert.All(result.Weights, value => Assert.Equal(0, value));
        Save("constant-" + width, new { width, height, rgbaExact = true, edgesZero = true, weightsZero = true, result.Adapter, result.IsWarp });
    }

    [Fact]
    public void ActualEdgesAndWeightsAreNonzeroAndSparseDiagonalChangesOnlyLocalPixels()
    {
        const int width = 97, height = 73;
        bool Inside(double x, double y) => x >= 18 && x < 78 && y >= 16 && y < 16 + (x - 18) * .55;
        var pixels = Pixels(width, height, (x, y) => Gray(Inside(x + .5, y + .5) ? (byte)0 : (byte)255));
        var result = Filter(pixels, width, height);
        Assert.Contains(result.Edges, value => value != 0);
        Assert.Contains(result.Weights, value => value != 0);
        int changed = 0, fractional = 0;
        double before = 0, after = 0;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            Assert.Equal(0, result.Edges[i + 2]); Assert.Equal(0, result.Edges[i + 3]);
            Assert.Equal(255, result.Output[i + 3]);
            if (pixels[i] != result.Output[i])
            {
                changed++;
                bool nearbyEdge = false;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                {
                    int px = Math.Clamp(x + dx, 0, width - 1), py = Math.Clamp(y + dy, 0, height - 1);
                    int j = (py * width + px) * 4;
                    nearbyEdge |= result.Edges[j] != 0 || result.Edges[j + 1] != 0;
                }
                Assert.True(nearbyEdge, $"Unexpected nonlocal change at {x},{y}.");
            }
            if (result.Output[i] is > 0 and < 255) fractional++;
            double coverage = 0;
            for (int sy = 0; sy < 16; sy++) for (int sx = 0; sx < 16; sx++)
                if (Inside(x + (sx + .5) / 16, y + (sy + .5) / 16)) coverage++;
            double reference = 255 * (1 - coverage / 256);
            before += Math.Pow(pixels[i] - reference, 2); after += Math.Pow(result.Output[i] - reference, 2);
        }
        Assert.InRange(changed, 20, width * height / 5);
        Assert.InRange(fractional, 20, width * height / 5);
        Assert.True(after < before * .95, $"Analytic 16x16 coverage MSE: {before / (width * height)} -> {after / (width * height)}");
        SaveImages("sparse-diagonal", pixels, result, width, height);
        Save("sparse-diagonal", new { width, height, changed, fractional, mseBefore = before / (width * height), mseAfter = after / (width * height), result.Adapter, result.IsWarp });
    }

    [Fact]
    public void ThinBlackDiagonalKeepsDarkCoreAndDoesNotBecomeBroadBlur()
    {
        const int width = 97, height = 73;
        var pixels = Pixels(width, height, (x, y) => Gray(x is >= 12 and < 84 && Math.Abs(y + .5 - (14 + .48 * (x + .5))) < .7 ? (byte)0 : (byte)255));
        var result = Filter(pixels, width, height);
        int darkCore = 0, affected = 0; double inputDarkness = 0, outputDarkness = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (result.Output[i] < 32) darkCore++;
            if (result.Output[i] < 255) affected++;
            inputDarkness += 255 - pixels[i]; outputDarkness += 255 - result.Output[i];
        }
        Assert.True(darkCore >= 20);
        Assert.InRange(outputDarkness / inputDarkness, .75, 1.25);
        Assert.InRange(affected, 80, 300);
        SaveImages("thin-black-line", pixels, result, width, height);
        Save("thin-black-line", new { darkCore, affected, darknessRatio = outputDarkness / inputDarkness });
    }

    [Fact]
    public void LumaDoesNotInventEdgesFromAlphaOnlyAndNeighborhoodBlendsRealTransparency()
    {
        const int width = 97, height = 73;
        var alphaOnly = Pixels(width, height, (x, y) => new byte[] { 91, 122, 180, (byte)((x * 13 + y * 7) % 256) });
        var flat = Filter(alphaOnly, width, height);
        Assert.Equal(alphaOnly, flat.Output);
        Assert.All(flat.Edges, value => Assert.Equal(0, value));
        var transparentEdge = Pixels(width, height, (x, y) => x > 15 && x < 80 && y > 12 && y < 12 + x * .5
            ? new byte[] { 0, 0, 0, 64 } : new byte[] { 255, 255, 255, 192 });
        var filtered = Filter(transparentEdge, width, height);
        Assert.Contains(Enumerable.Range(0, width * height), i => filtered.Output[i * 4 + 3] is > 64 and < 192);
        for (int i = 0; i < width * height; i++) Assert.InRange(filtered.Output[i * 4 + 3], 64, 192);
    }

    [Fact]
    public void OffNeverCreatesSmaaAndRetainedResizeToggleUsesCachedLutsAndPipelinesWithZeroReadback()
    {
        using var session = new D3D12FrameSession();
        var off = Diagonal(); var on = WithSmaa(off);
        var baseline = session.Capture(off, 97, 73);
        for (int i = 0; i < 4; i++) { using var warm = session.Render(off, 97, 73); }
        var original = session.Statistics!;
        using (var output = session.Render(on, 97, 73))
        {
            Assert.Equal(0, output.Statistics.ReadbackCount);
            Assert.Equal(original.TextureUploads + 2, output.Statistics.TextureUploads);
            Assert.Equal(original.ShaderCompilations + 6, output.Statistics.ShaderCompilations);
            Assert.Equal(original.PipelineCreations + 3, output.Statistics.PipelineCreations);
            Assert.Equal(0, output.Statistics.EffectPassDrawCalls);
        }
        var warmSmaa = session.Statistics!;
        foreach (var size in new[] { (1, 1), (39, 31), (161, 103), (97, 73), (39, 31), (97, 73) })
        {
            using var output = session.Render(on, size.Item1, size.Item2);
            Assert.Equal(size.Item1, output.Width); Assert.Equal(size.Item2, output.Height);
            Assert.Equal(0, output.Statistics.ReadbackCount); Assert.Equal(1, output.Statistics.SubmissionCount);
            Assert.Equal(FrameAntialiasing.Smaa1X, output.Statistics.Antialiasing);
            Assert.Equal(3, output.Statistics.AntialiasingPassDrawCalls);
            Assert.Equal(warmSmaa.ShaderCompilations, output.Statistics.ShaderCompilations);
            Assert.Equal(warmSmaa.PipelineCreations, output.Statistics.PipelineCreations);
            Assert.Equal(warmSmaa.TextureUploads, output.Statistics.TextureUploads);
            Assert.Equal(warmSmaa.CachedResources, output.Statistics.CachedResources);
        }
        using (var output = session.Render(off, 97, 73))
        {
            Assert.Equal(original.FrameOwnedAllocations, output.Statistics.FrameOwnedAllocations);
            Assert.Equal(0, output.Statistics.AntialiasingPassDrawCalls);
        }
        Assert.Equal(baseline.Rgba, session.Capture(off, 97, 73).Rgba);
        var smaa = session.Capture(on, 97, 73);
        using var fresh = new D3D12FrameSession();
        Assert.Equal(smaa.Rgba, fresh.Capture(on, 97, 73).Rgba);
        Save("retained-resize-toggle", new { original, warmSmaa, final = session.Statistics, session.AdapterName, session.IsWarp, session.DebugLayerEnabled, session.DebugInfoQueueAvailable });
    }

    [Fact]
    public void AbortedFirstSmaaFrameRemovesUnsubmittedLookupCacheAndNextFrameMatchesFresh()
    {
        using var runtime = new D3D12GpuRuntime();
        using var cache = new D3D12GpuCache(runtime);
        var pass = new D3D12SmaaPass(runtime, cache);
        var pixels = Pixels(97, 73, (x, y) => Gray(x > y ? (byte)0 : (byte)255));
        var context = runtime.Begin();
        pass.Record(context, Input(cache, pixels, 97, 73));
        Assert.True(cache.LiveResourceCount > 0);
        runtime.Abort();
        Assert.Equal(0, cache.LiveResourceCount);
        Assert.Equal(0, runtime.Submissions);
        var recovered = Filter(pixels, 97, 73, runtime, cache, pass);
        var fresh = Filter(pixels, 97, 73);
        Assert.Equal(fresh.Edges, recovered.Edges); Assert.Equal(fresh.Weights, recovered.Weights); Assert.Equal(fresh.Output, recovered.Output);
        Assert.Equal(6, runtime.TextureUploads); // input plus two LUTs, each re-uploaded after abort.
    }

    [Fact]
    public void PresentCallbackGetsIndependentCopySourceAndAbortRestoresNextCapture()
    {
        using var session = new D3D12FrameSession();
        var frame = WithSmaa(Diagonal());
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(frame, 97, 73, null,
            (_, target) => { Assert.Equal("$smaa-output", target.Name); Assert.Equal(ResourceStates.CopySource, target.State); throw new OperationCanceledException("Synthetic present cancellation"); }));
        var recovered = session.Capture(frame, 97, 73);
        using var fresh = new D3D12FrameSession();
        Assert.Equal(fresh.Capture(frame, 97, 73).Rgba, recovered.Rgba);
        using var retained = session.RenderForPresent(frame, 97, 73, null,
            (_, target) => { Assert.Equal("$smaa-output", target.Name); Assert.Equal(ResourceStates.CopySource, target.State); });
        Assert.Equal(0, retained.Statistics.ReadbackCount); Assert.Equal(1, retained.Statistics.SubmissionCount);
        session.WaitForIdle(); // The retained output remains readable through its own COM lease after fence retirement.
        Assert.Equal((ulong)97, retained.Resource.Description.Width);
    }


    private sealed record Filtered(byte[] Edges, byte[] Weights, byte[] Output, string Adapter, bool IsWarp);
    private static Filtered Filter(byte[] pixels, int width, int height,
        D3D12GpuRuntime? runtime = null, D3D12GpuCache? cache = null, D3D12SmaaPass? pass = null)
    {
        bool owned = runtime is null;
        runtime ??= new D3D12GpuRuntime(); cache ??= new(runtime); pass ??= new(runtime, cache);
        try
        {
            var context = runtime.Begin();
            var input = Input(cache, pixels, width, height);
            var result = pass.Record(context, input);
            Assert.Equal(4, new[] { input.Resource.NativePointer, result.Edges.Resource.NativePointer,
                result.Weights.Resource.NativePointer, result.Output.Resource.NativePointer }.Distinct().Count());
            (ID3D12Resource Resource, PlacedSubresourceFootPrint Footprint, int Size) Readback(D3D12GpuTarget target)
            {
                context.Transition(target, ResourceStates.CopySource);
                var footprint = new PlacedSubresourceFootPrint[1];
                runtime.Device.GetCopyableFootprints(target.Resource.Description, 0, 1, 0, footprint, new int[1], new ulong[1], out ulong bytes);
                var buffer = context.Own(runtime.Device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(bytes), ResourceStates.CopyDest));
                context.Commands.CopyTextureRegion(new TextureCopyLocation(buffer, footprint[0]), 0, 0, 0, new TextureCopyLocation(target.Resource, 0));
                return (buffer, footprint[0], checked((int)bytes));
            }
            var buffers = new[] { Readback(result.Edges), Readback(result.Weights), Readback(result.Output) };
            runtime.Wait(runtime.Submit());
            byte[] Read((ID3D12Resource Resource, PlacedSubresourceFootPrint Footprint, int Size) buffer)
            {
                var data = buffer.Resource.Map<byte>(0, buffer.Size); var output = new byte[width * height * 4];
                try { for (int y = 0; y < height; y++) data.Slice(y * buffer.Footprint.Footprint.RowPitch, width * 4).CopyTo(output.AsSpan(y * width * 4)); }
                finally { buffer.Resource.Unmap(0); }
                return output;
            }
            return new(Read(buffers[0]), Read(buffers[1]), Read(buffers[2]), runtime.AdapterName, runtime.IsWarp);
        }
        finally { if (owned) { cache.Dispose(); runtime.Dispose(); } }
    }

    private static D3D12GpuTarget Input(D3D12GpuCache cache, byte[] pixels, int width, int height) =>
        new("test-input", cache.Texture(new("synthetic", width, height, pixels)), default, width, height, Format.R8G8B8A8_UNorm, false)
        { State = ResourceStates.PixelShaderResource, Initialized = true };
    private static OriginalFrameScene WithSmaa(OriginalFrameScene frame) => new(frame.Surface, frame.Outline, frame.OutlineOptions,
        frame.Options! with { Antialiasing = FrameAntialiasing.Smaa1X }, frame.Materials);
    private static byte[] Gray(byte gray) => [gray, gray, gray, 255];
    private static byte[] Pixels(int width, int height, Func<int, int, byte[]> pixel)
    {
        var data = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) pixel(x, y).CopyTo(data, (y * width + x) * 4);
        return data;
    }
    private static string EvidenceDirectory => Environment.GetEnvironmentVariable("MMD_SMAA_ARTIFACTS") ?? Path.Combine(AppContext.BaseDirectory, "smaa-test-artifacts");
    private static void Save(string name, object value)
    {
        Directory.CreateDirectory(EvidenceDirectory);
        File.WriteAllText(Path.Combine(EvidenceDirectory, name + ".json"), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static void SaveImages(string name, byte[] input, Filtered result, int width, int height)
    {
        Directory.CreateDirectory(EvidenceDirectory);
        foreach (var image in new[] { ("input", input), ("edges", result.Edges), ("weights", result.Weights), ("output", result.Output) })
        {
            File.WriteAllBytes(Path.Combine(EvidenceDirectory, name + "-" + image.Item1 + ".rgba"), image.Item2);
            using var writer = new BinaryWriter(File.Create(Path.Combine(EvidenceDirectory, name + "-" + image.Item1 + ".bmp")));
            writer.Write((ushort)0x4d42); writer.Write(54 + width * height * 4); writer.Write(0); writer.Write(54);
            writer.Write(40); writer.Write(width); writer.Write(-height); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(0); writer.Write(width * height * 4); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
            for (int i = 0; i < image.Item2.Length; i += 4)
            { writer.Write(image.Item2[i + 2]); writer.Write(image.Item2[i + 1]); writer.Write(image.Item2[i]); writer.Write((byte)255); }
        }
    }

    private static OriginalFrameScene Diagonal()
    {
        SceneVertex[] vertices = [new(new(-.8f, -.7f, 0), -Vector3.UnitZ, Vector2.Zero),
            new(new(.8f, -.4f, 0), -Vector3.UnitZ, Vector2.Zero), new(new(-.5f, .9f, 0), -Vector3.UnitZ, Vector2.Zero)];
        var snapshot = new SceneSnapshot(vertices, new uint[] { 0, 1, 2 },
            [new("white", 0, 3, Vector4.One, Vector3.Zero, Vector3.One, 1)],
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 2, .1f, 20), lightDirection: -Vector3.UnitZ);
        return new(new(snapshot, [new(Vector3.One, DoubleSided: true)]),
            options: new(OriginalAlphaBlendMode.Normal, true, false));
    }
}
