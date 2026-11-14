using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace Mmd.Modern.Tests;

// Diagnostic capture changes process-wide environment variables, like timing capture.
[Collection(RayGiTimingEnvironmentCollection.Name)]
[Trait("Category", "Gpu")]
public sealed class D3D12RayGiDenoiserTests
{
    private const int Size = 17;

    [Fact]
    public void ActualGpuPreservesConstantRadianceAndEveryVisibilityAlphaBit()
    {
        var data = Guides();
        for (int i = 0; i < data.Signal.Length; i++) data.Signal[i] = new(.3f, .2f, .7f, i / 300f);
        var result = Filter(data);
        Assert.Equal(data.Signal, result);
    }

    [Fact]
    public void ActualGpuReducesZeroMeanSpatialNoiseAndKeepsFiniteConvexBounds()
    {
        var data = Guides();
        for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++)
            data.Signal[y * Size + x] = new(new Vector3((x + y) % 2), (x + y) / 40f);
        var result = Filter(data);
        double error = 0; int count = 0;
        for (int y = 3; y < Size - 3; y++) for (int x = 3; x < Size - 3; x++)
        { error += Math.Pow(result[y * Size + x].X - .5, 2); count++; }
        Assert.True(error / count < .005, $"Checkerboard MSE {error / count} must fall from .25.");
        for (int i = 0; i < result.Length; i++)
        { Assert.InRange(result[i].X, 0, 1); Assert.Equal(data.Signal[i].W, result[i].W); }
        for (int i = 0; i < data.Signal.Length; i++) data.Signal[i] = new(new Vector3(i % 2 == 0 ? float.MaxValue / 8 : float.MaxValue / 16), .4f);
        Assert.All(Filter(data), value => { Assert.True(float.IsFinite(value.X)); Assert.InRange(value.X, float.MaxValue / 16, float.MaxValue / 8); });
    }

    [Theory]
    [InlineData("material")]
    [InlineData("parallel-layer")]
    [InlineData("normal")]
    [InlineData("distance")]
    [InlineData("connected-fold")]
    public void RealGpuDoesNotMixDisconnectedLayersMaterialNormalsOrDistantGeometry(string boundary)
    {
        var data = Guides();
        data.Triangles = [Triangle(0), boundary == "parallel-layer" ? Triangle(0, .01f) : Triangle(1)];
        if (boundary == "connected-fold") data.Triangles = [
            new(new(0, -100, 0, 0), new(0, 100, 0, 0), new(-100, 0, 0, 0), default, default, default, default, default, default, new(0, 0, 1, 0)),
            new(new(0, -100, 0, 0), new(100, 0, 50, 0), new(0, 100, 0, 0), default, default, default, default, default, default, new(0, 1, 1, 0))];
        for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++)
        {
            int i = y * Size + x; bool right = x >= Size / 2;
            data.Signal[i] = right ? new(0, 0, 1, .2f) : new(1, 0, 0, .8f);
            if (!right) continue;
            if (boundary is "material" or "parallel-layer" or "connected-fold") data.Normal[i].W = 2;
            if (boundary == "parallel-layer") data.Position[i].Z = .01f;
            if (boundary == "normal") data.Normal[i] = new(Vector3.UnitX, 1);
            if (boundary == "distance") data.Position[i].X += 100;
            if (boundary == "connected-fold") data.Position[i].Z = .5f * (x - Size / 2);
        }
        Assert.Equal(data.Signal, Filter(data));
    }

    [Fact]
    public void ConnectedCoplanarTrianglesSmoothNoiseAcrossTheirSharedEdge()
    {
        var data = Guides();
        for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++) data.Signal[y * Size + x] = new(new Vector3((x + y) % 2), .7f);
        var oneTriangle = Filter(data);
        data.Triangles = [Triangle(0), new(new(-100, -100, 0, 0), new(-100, 100, 0, 0), new(100, 100, 0, 0),
            default, default, default, default, default, default, new(0, 1, 1, 0))];
        for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++) data.Normal[y * Size + x].W = x > y ? 1 : 2;
        Assert.Equal(oneTriangle, Filter(data));
    }

    [Fact]
    public void InvalidGuidesBackgroundAndGrazingSurfacesKeepTheirOwnSamples()
    {
        var data = Guides();
        for (int i = 0; i < data.Signal.Length; i++) data.Signal[i] = new(i % 2, .3f, i % 3 / 3f, i / 300f);
        int[] invalid = [0, 1, 2, 3, 4, 5];
        data.Normal[0].W = 0; data.Normal[1].W = 99; data.Normal[2].W = float.NaN;
        data.Position[3].X = float.NaN; data.Position[4].W = 2; data.Normal[5] = new(0, 0, 0, 1);
        var filtered = Filter(data);
        foreach (int i in invalid) Assert.Equal(data.Signal[i], filtered[i]);
        Assert.All(filtered, value => Assert.True(float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)));
        data = Guides();
        data.Triangles = [new(new(0, -100, -100, 0), new(0, 100, -100, 0), new(0, 100, 100, 0), default, default, default, default, default, default, new(0, 0, 1, 0))];
        for (int i = 0; i < data.Signal.Length; i++) data.Signal[i] = new(i % 2, .3f, .7f, .9f);
        Assert.Equal(data.Signal, Filter(data));
    }

    [Fact]
    public void LowSampleIntegratedSignalGetsCloserToHighSampleReferenceAndOffStillBypasses()
    {
        using var session = new D3D12FrameSession();
        var frame = D3D12RayGiTests.Bounce();
        // The existing fixture has toonFactor=1; this isolates the signal from the unchanged
        // original bright endpoint while exercising real raster guides and traversal.
        var options = new RayGiOptions(LightingMode.SoftwareRayTracing, Strength: .5f, SamplesPerPixel: 4,
            Seed: 31, MaxRayDistance: 50, RayEpsilon: .001f, Composition: RayGiComposition.ToonPalette);
        var low = CaptureSignal(session, frame, options with { Denoiser = RayGiDenoiser.Spatial }, "low");
        Assert.Equal(RayGiDenoiser.Spatial, session.RayGiStatistics!.Denoiser);
        Assert.Equal(1, session.RayGiStatistics.DenoiseDispatchCount);
        var high = CaptureSignal(session, frame, options with { SamplesPerPixel = 1024 }, "reference");
        double before = 0, after = 0; int count = 0;
        for (int y = 20; y < 77; y++) for (int x = 22; x < 107; x++)
        {
            int i = y * 129 + x;
            if (low.Normal[i].W < 1 || low.Normal[i].W > 2) continue;
            Assert.Equal(low.Raw[i].W, low.Filtered[i].W);
            before += Vector3.DistanceSquared(Xyz(low.Raw[i]), Xyz(high.Filtered[i]));
            after += Vector3.DistanceSquared(Xyz(low.Filtered[i]), Xyz(high.Filtered[i])); count++;
        }
        Assert.True(count > 1000);
        Assert.True(after < before * .7, $"Incident MSE {before / count} -> {after / count}");
        var original = session.Capture(frame, 129, 97);
        Assert.Equal(original.Rgba, session.Capture(frame, 129, 97, rayGiOptions: options with { Strength = 0, Denoiser = RayGiDenoiser.Spatial }).Rgba);
        Assert.Equal(RayGiDenoiser.Off, session.RayGiStatistics!.Denoiser); Assert.Equal(0, session.RayGiStatistics.DenoiseDispatchCount);
        using var output = session.Render(frame, 129, 97, rayGiOptions: options with { Denoiser = RayGiDenoiser.Spatial });
        Assert.Equal(0, output.Statistics.ReadbackCount); Assert.Equal(1, output.Statistics.SubmissionCount);
        Save("error", new { samples = count, meanSquaredRawError = before / count, meanSquaredFilteredError = after / count });
    }

    [Fact]
    public void FilteredSoftwareAndDxrAgreeAndAbortResizeToggleDoNotKeepStaleScratch()
    {
        using var session = new D3D12FrameSession(); var source = D3D12RayGiTests.Bounce();
        var frame = new OriginalFrameScene(new(source.Surface.Snapshot,
            source.Surface.Materials.Select((m, i) => i == 0 ? m with { ToonShadowColor = new(.2f) } : m), source.Surface.Profile),
            source.Outline, source.OutlineOptions, source.Options, source.Materials);
        var options = new RayGiOptions(LightingMode.SoftwareRayTracing, Strength: .5f, SamplesPerPixel: 4,
            MaxRayDistance: 50, Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.Spatial);
        var software = session.Capture(frame, 129, 97, rayGiOptions: options);
        var raw = session.Capture(frame, 129, 97, rayGiOptions: options with { Denoiser = RayGiDenoiser.Off });
        Assert.True(software.Rgba.Zip(raw.Rgba, (a, b) => a != b).Count(changed => changed) > 100,
            "Visible GI must change with filtering; a toonFactor=1 endpoint cannot prove signal/ordering equivalence.");
        var hardware = session.Capture(frame, 129, 97, rayGiOptions: options with { Mode = LightingMode.HardwareRayTracing });
        Assert.StartsWith("DXR-raw-", session.RayGiBackendIdentity); Assert.False(session.IsWarp);
        Assert.InRange(software.Rgba.Zip(hardware.Rgba, (a, b) => Math.Abs(a - b)).Max(), 0, 1);
        Assert.Throws<InvalidOperationException>(() => session.RenderForPresent(frame, 129, 97, options,
            (_, _) => throw new InvalidOperationException("Abort after denoise")));
        Assert.Null(session.RayGiStatistics);
        Assert.Equal(software.Rgba, session.Capture(frame, 129, 97, rayGiOptions: options).Rgba);
        using (var resized = session.Render(frame, 103, 81, rayGiOptions: options)) Assert.Equal(0, resized.Statistics.ReadbackCount);
        var unfiltered = session.Capture(frame, 129, 97, rayGiOptions: options with { Denoiser = RayGiDenoiser.Off });
        Assert.Equal(0, session.RayGiStatistics!.DenoiseDispatchCount);
        Assert.Equal(software.Rgba, session.Capture(frame, 129, 97, rayGiOptions: options).Rgba);
        Assert.Equal(unfiltered.Rgba, session.Capture(frame, 129, 97, rayGiOptions: options with { Denoiser = RayGiDenoiser.Off }).Rgba);
    }

    private sealed class Data
    {
        internal Vector4[] Signal = new Vector4[Size * Size], Position = new Vector4[Size * Size], Normal = new Vector4[Size * Size];
        internal RayGiTriangleGpu[] Triangles = [Triangle(0)];
    }
    private static Data Guides()
    {
        var data = new Data();
        for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++)
        { int i = y * Size + x; data.Position[i] = new(x - Size / 2, y - Size / 2, 0, .5f); data.Normal[i] = new(0, 0, -1, 1); }
        return data;
    }
    private static RayGiTriangleGpu Triangle(uint material, float z = 0) => new(new(-100, -100, z, 0), new(100, 100, z, 0), new(100, -100, z, 0),
        default, default, default, default, default, default, new(material, 0, 1, 0));
    private static Vector4[] Filter(Data data)
    {
        using var runtime = new D3D12GpuRuntime(); Assert.False(runtime.IsWarp);
        using var filter = new D3D12RayGiDenoiser(runtime);
        var context = runtime.Begin(); var device = runtime.Device; var commands = context.Commands;
        ID3D12Resource Buffer<T>(ReadOnlySpan<T> values) where T : unmanaged
        {
            var bytes = MemoryMarshal.AsBytes(values); int length = bytes.Length;
            var upload = context.Own(device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer((ulong)length), ResourceStates.GenericRead));
            bytes.CopyTo(upload.Map<byte>(0, length)); upload.Unmap(0);
            var buffer = context.Own(device.CreateCommittedResource(HeapType.Default, ResourceDescription.Buffer((ulong)length), ResourceStates.CopyDest));
            commands.CopyBufferRegion(buffer, 0, upload, 0, (ulong)length);
            commands.ResourceBarrierTransition(buffer, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource); return buffer;
        }
        var input = Buffer<Vector4>(data.Signal); var triangles = Buffer<RayGiTriangleGpu>(data.Triangles);
        var heap = context.Own(device.CreateDescriptorHeap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 2, DescriptorHeapFlags.ShaderVisible)));
        int stride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        void Texture(Vector4[] values, int slot)
        {
            var texture = context.Own(device.CreateCommittedResource(HeapType.Default, ResourceDescription.Texture2D(Format.R32G32B32A32_Float, Size, Size, mipLevels: 1), ResourceStates.CopyDest));
            var footprints = new PlacedSubresourceFootPrint[1];
            device.GetCopyableFootprints(texture.Description, 0, 1, 0, footprints, new int[1], new ulong[1], out ulong size);
            var upload = context.Own(device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer(size), ResourceStates.GenericRead));
            var source = MemoryMarshal.AsBytes(values.AsSpan()); var mapped = upload.Map<byte>(0, (int)size);
            for (int y = 0; y < Size; y++) source.Slice(y * Size * 16, Size * 16).CopyTo(mapped.Slice(y * footprints[0].Footprint.RowPitch, Size * 16));
            upload.Unmap(0); commands.CopyTextureRegion(new TextureCopyLocation(texture, 0), 0, 0, 0, new TextureCopyLocation(upload, footprints[0]));
            commands.ResourceBarrierTransition(texture, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource);
            device.CreateShaderResourceView(texture, null, heap.GetCPUDescriptorHandleForHeapStart().Offset(slot, stride));
        }
        Texture(data.Position, 0); Texture(data.Normal, 1); commands.SetDescriptorHeaps(heap);
        var camera = new SceneCamera(new(0, 0, -10), Vector3.Zero, Vector3.UnitY, Size, .1f, 100);
        var output = filter.Record(context, input, triangles, heap.GetGPUDescriptorHandleForHeapStart(), Size, Size, data.Triangles.Length, 2, camera, new DxcShaderCompiler());
        int bytesCount = Size * Size * 16;
        var readback = context.Own(device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer((ulong)bytesCount), ResourceStates.CopyDest));
        commands.ResourceBarrierTransition(output, ResourceStates.PixelShaderResource, ResourceStates.CopySource);
        commands.CopyBufferRegion(readback, 0, output, 0, (ulong)bytesCount);
        runtime.Wait(runtime.Submit());
        try { return MemoryMarshal.Cast<byte, Vector4>(readback.Map<byte>(0, bytesCount)).ToArray(); }
        finally { readback.Unmap(0); }
    }
    private sealed record Signal(Vector4[] Raw, Vector4[] Filtered, Vector4[] Normal);
    private static Signal CaptureSignal(D3D12FrameSession session, OriginalFrameScene frame, RayGiOptions options, string name)
    {
        string root = Path.Combine(Path.GetTempPath(), "denoise-gpu-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS");
        try
        {
            Environment.SetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS", root);
            _ = session.Capture(frame, 129, 97, rayGiOptions: options);
            Vector4[] Read(string prefix) => MemoryMarshal.Cast<byte, Vector4>(File.ReadAllBytes(Directory.GetFiles(root, "*.bin", SearchOption.AllDirectories).Single(p => Path.GetFileName(p).StartsWith(prefix, StringComparison.Ordinal)))).ToArray();
            var filtered = Read("indirect-"); var raw = options.Denoiser == RayGiDenoiser.Spatial ? Read("raw-indirect-") : filtered;
            return new(raw, filtered, Read("normal-id-"));
        }
        finally { Environment.SetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS", previous); }
    }
    private static Vector3 Xyz(Vector4 v) => new(v.X, v.Y, v.Z);
    private static void Save(string name, object value)
    {
        string root = Environment.GetEnvironmentVariable("MMD_DENOISE_ARTIFACTS") ?? Path.Combine(AppContext.BaseDirectory, "denoise-test-artifacts");
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, name + ".json"), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
