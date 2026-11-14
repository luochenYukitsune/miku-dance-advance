using Mmd.Modern.Application;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Mmd.Modern.ViewOutput.Images;
using Vortice.D3DCompiler;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12TransparentDiffusionTests
{
    [Theory]
    [InlineData("PSHorizontalAlpha")]
    [InlineData("PSCompositeAlpha")]
    public void CoverageStagesCompile(string entry)
        => Assert.Equal(new byte[] { 68, 88, 66, 67 }, Compiler.Compile(D3D12DiffusionPass.AlphaSource,
            entry, "transparent-diffusion.hlsl", "ps_5_1", ShaderFlags.EnableStrictness | ShaderFlags.OptimizationLevel3).ToArray()[..4]);

    [Theory, Trait("Category", "Gpu")]
    [InlineData(97, 73, 1, false, 0f)]
    [InlineData(97, 73, 1, false, .01f)]
    [InlineData(97, 73, 4, true, .01f)]
    [InlineData(1, 17, 1, false, .01f)]
    [InlineData(23, 1, 1, false, .01f)]
    public void AlphaHolesAndSilhouetteRetainCoverageAndMatchNormalizedOracle(int width, int height, int samples, bool smaa, float extent)
    {
        using var session = new D3D12FrameSession();
        var frame = Frame(samples, smaa);
        var before = session.Capture(frame, width, height);
        var after = session.Capture(frame, width, height, diffusion: new(true, .7f, extent));
        var expected = TransparentDiffusionReferenceImage.Apply(before.Rgba, width, height, .7, extent);
        AssertCoverage(before.Rgba, after.Rgba);
        for (int p = 0; p < after.Rgba.Length; p++)
            if ((p & 3) != 3) Assert.InRange(Math.Abs(expected[p] - after.Rgba[p]), 0, 2);
        Assert.Equal(new[] { "diffusion:x", "diffusion:y", "alpha:straight" }, session.LastRecordings.TakeLast(3).Select(r => r.Channel));
        Assert.All(session.LastTargets.Where(t => t.Name.StartsWith("$diffusion-", StringComparison.Ordinal)),
            t => Assert.Equal("R16G16B16A16_Float", t.Format));
        Assert.Equal(before.Rgba, session.Capture(frame, width, height).Rgba);
    }

    [Theory, Trait("Category", "Gpu")]
    [InlineData(0f)] [InlineData(1f / 1024)] [InlineData(.0021f)] [InlineData(1f / 255)] [InlineData(.01f)] [InlineData(.5f)] [InlineData(1f)]
    public void ConstantColorDoesNotAcquireDarkFringeAndLowAlphaIsNotSquared(float alpha)
    {
        using var session = new D3D12FrameSession();
        var color = new Vector4(.2f, .45f, .7f, alpha);
        var frame = Frame(color: color);
        var before = session.Capture(frame, 97, 73);
        var after = session.Capture(frame, 97, 73, diffusion: new(true, .7f, .01f));
        AssertCoverage(before.Rgba, after.Rgba);
        int interior = ((73 / 2) * 97 + 97 / 2) * 4;
        Assert.Equal((byte)Math.Round((double)(Half)alpha * 255, MidpointRounding.ToEven), before.Rgba[interior + 3]);
        for (int p = 0; p < after.Rgba.Length; p += 4)
        {
            if (after.Rgba[p + 3] == 0) continue;
            for (int c = 0; c < 3; c++)
            {
                float s = color[c], squared = s * s;
                float expected = s + .7f * (2 * squared - squared * squared - s);
                Assert.InRange(Math.Abs(after.Rgba[p + c] - MathF.Round(expected * 255)), 0, 1);
            }
        }
    }

    [Fact, Trait("Category", "Gpu")]
    public void EveryHalfCoverageAroundFirstRgba8BoundaryQuantizesBeforeZeroRgbDecision()
    {
        const int width = 66, height = 1;
        float threshold = .5f / 255;
        ushort center = BitConverter.HalfToUInt16Bits((Half)threshold);
        float[] values = Enumerable.Range(-16, 33).Select(delta => (float)BitConverter.UInt16BitsToHalf((ushort)(center + delta))).ToArray();
        var vertices = new List<SceneVertex>(); var indices = new List<uint>(); var materials = new List<SceneMaterial>();
        for (int i = 0; i < values.Length; i++)
        {
            float left = -1 + 2f * i / values.Length, right = -1 + 2f * (i + 1) / values.Length;
            uint start = (uint)vertices.Count;
            vertices.AddRange([new(new(left, -1, 0), -Vector3.UnitZ, Vector2.Zero), new(new(right, -1, 0), -Vector3.UnitZ, Vector2.Zero),
                new(new(right, 1, 0), -Vector3.UnitZ, Vector2.Zero), new(new(left, 1, 0), -Vector3.UnitZ, Vector2.Zero)]);
            int first = indices.Count; indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
            materials.Add(new("explicit half coverage " + i, first, 6, new(.2f, .45f, .7f, values[i]), Vector3.Zero, Vector3.Zero, 1, AlphaCutoff: 0));
        }
        var snapshot = new SceneSnapshot(vertices.ToArray(), indices.ToArray(), materials,
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 2f / width, .1f, 20));
        var frame = new OriginalFrameScene(new(snapshot, materials.Select(_ => new OriginalStyleMaterialBinding(Vector3.One, DoubleSided: true)),
            new(Vector3.One, Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, false, false, BackgroundColor: Vector4.Zero, AlphaOutput: FrameAlphaOutput.Straight));
        using var session = new D3D12FrameSession();
        // Match aspect to stripe geometry to avoid an empty one-pixel-high view.
        var before = session.Capture(frame, width, height);
        var after = session.Capture(frame, width, height, diffusion: new(true, .7f, 0));
        AssertCoverage(before.Rgba, after.Rgba);
        int nonzero = 0, zero = 0;
        for (int x = 0; x < width; x++)
        {
            int index = Math.Clamp((int)((1 - (x + .5f) / width) * values.Length), 0, values.Length - 1);
            byte expected = (byte)Math.Round((double)(Half)values[index] * 255, MidpointRounding.ToEven);
            Assert.Equal(expected, before.Rgba[x * 4 + 3]);
            if (expected == 0) zero++; else nonzero++;
        }
        Assert.True(zero > 0 && nonzero > 0);
    }

    [Theory, Trait("Category", "Gpu")]
    [InlineData(LightingMode.SoftwareRayTracing)] [InlineData(LightingMode.HardwareRayTracing)]
    public void GiSsaaAndSmaaFeedFilterBeforeFinalResolve(LightingMode mode)
    {
        using var session = new D3D12FrameSession();
        var source = D3D12RayGiTests.Bounce();
        var frame = new OriginalFrameScene(source.Surface, source.Outline, source.OutlineOptions,
            source.Options! with { AlphaOutput = FrameAlphaOutput.Straight, BackgroundColor = Vector4.Zero,
                SpatialSamples = 2, Antialiasing = FrameAntialiasing.Smaa1X }, source.Materials, source.Lighting);
        var gi = new RayGiOptions(mode, SamplesPerPixel: 4, Seed: 17, MaxRayDistance: 50, RayEpsilon: .001f);
        var before = session.Capture(frame, 97, 73, rayGiOptions: gi);
        var after = session.Capture(frame, 97, 73, rayGiOptions: gi, diffusion: new(true));
        AssertCoverage(before.Rgba, after.Rgba);
        Assert.Equal(mode, session.RayGiStatistics!.Effective);
        Assert.Equal("alpha:straight", session.LastRecordings.Last().Channel);
    }

    [Fact, Trait("Category", "Gpu")]
    public void AbortResizeAndSlidersKeepOneSubmissionZeroReadbacksAndBoundedCache()
    {
        using var session = new D3D12FrameSession(); var frame = Frame();
        var options = new D3D12DiffusionOptions(true, .6f, .008f);
        var expected = session.Capture(frame, 97, 73, diffusion: options);
        long submissions = session.Runtime.Submissions;
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(frame, 97, 73, null, (_, target) =>
        {
            Assert.Equal("$straight-alpha", target.Name);
            throw new OperationCanceledException();
        }, diffusion: options));
        Assert.Equal(submissions, session.Runtime.Submissions);
        Assert.Equal(expected.Rgba, session.Capture(frame, 97, 73, diffusion: options).Rgba);
        var warm = session.Statistics!; long reads = session.Runtime.Readbacks;
        for (int i = 0; i < 8; i++)
        {
            using var output = session.Render(frame, 97 + i % 2, 73 + i % 3,
                diffusion: options with { Strength = .1f + i * .1f });
            Assert.Equal(0, output.Statistics.ReadbackCount); Assert.Equal(1, output.Statistics.SubmissionCount);
            Assert.Equal(warm.CachedResources, output.Statistics.CachedResources);
            Assert.Equal(warm.ShaderCompilations, output.Statistics.ShaderCompilations);
            Assert.Equal(warm.PipelineCreations, output.Statistics.PipelineCreations);
            Assert.InRange(output.Statistics.PendingFrames, 0, 3);
        }
        Assert.Equal(reads, session.Runtime.Readbacks);
        Assert.True(D3D12DiffusionMemoryBudget.Measure(session.Runtime, 1024, 1024, true, alphaCoverage: true)
            > D3D12DiffusionMemoryBudget.Measure(session.Runtime, 1024, 1024, true));
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task ViewCaptureAutomaticallyUsesCoveragePreservingVariant()
    {
        var source = Frame();
        var scene = new OriginalFrameScene(source.Surface, options: source.Options! with { AlphaOutput = FrameAlphaOutput.LegacyMax });
        await using var renderer = new D3D12ViewRenderer(new(97, 73, new(Diffusion: new(true, .7f, .01f))));
        var plan = new OfflineFramePlan(97, 73, new(30), 0, 0); var sample = plan.GetSample(0);
        using var actual = await renderer.CaptureAsync(new(sample, 97, 73, scene, null, null, null), TestContext.Current.CancellationToken);
        using var direct = new D3D12FrameSession();
        var expected = direct.Capture(source, 97, 73, diffusion: new(true, .7f, .01f));
        Assert.Equal(FrameAlphaMode.Straight, actual.Alpha);
        Assert.Equal(expected.Rgba, actual.Pixels.ToArray());
        string directory = Path.Combine(Path.GetTempPath(), "mda-alpha-diffusion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "filtered.png");
        await using (var sink = new PngFrameSink(path))
        {
            await sink.BeginAsync(new(plan), TestContext.Current.CancellationToken);
            await sink.WriteAsync(sample, actual, TestContext.Current.CancellationToken);
            await sink.CompleteAsync(TestContext.Current.CancellationToken);
        }
        Assert.Equal(actual.Pixels.ToArray(), ReadPng(File.ReadAllBytes(path)));
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task CoordinatorPreflightAndPngUseTheSameTransparentCaptureContract()
    {
        var source = Frame();
        var transparent = new OriginalFrameScene(source.Surface, options: source.Options! with { AlphaOutput = FrameAlphaOutput.LegacyMax });
        var outline = new OriginalOutlineData(source.Surface.Snapshot.Vertices.ToArray().Select(v =>
            new OriginalOutlineVertex(v.Position, v.Normal, OriginalOutlineVertexMode.Expand, 0, 0)), [new(0, Vector4.Zero, false)]);
        var layout = new SceneCompositionEditor();
        layout.Add("static test pattern", "fixture.pmx", ModelRole.Prop, new(source.Surface, outline,
            new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, -5), 30, 1)));
        var coordinator = new ExportCoordinator(token => ValueTask.FromResult(new SceneAnimationSession(layout.Objects, [],
            _ => throw new InvalidOperationException("Static fixture requires no physics"), cancellationToken: token)),
            (request, _) => ValueTask.FromResult<IViewRenderer>(new TransparentExportRenderer(new(request), transparent)));
        string directory = Path.Combine(Path.GetTempPath(), "mda-alpha-diffusion-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, "coordinator.png");
        await coordinator.RunAsync(new(new(97, 73, new(30), 0, 0), DiffusionQuality()), new PngFrameSink(path),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ExportTaskState.Completed, coordinator.State);
        using var direct = new D3D12FrameSession();
        Assert.Equal(direct.Capture(source, 97, 73, diffusion: new(true, .7f, .01f)).Rgba, ReadPng(File.ReadAllBytes(path)));
    }

    // Static scene owner intentionally strips source Options. This test-only host
    // selects the authored transparent frame consistently at both real renderer
    // boundaries; the coordinator, preflight, GPU renderer and PNG sink are real.
    private sealed class TransparentExportRenderer(D3D12ViewRenderer renderer, OriginalFrameScene scene)
        : IViewRenderer, IViewSceneInputPreflight
    {
        public ViewRendererCapabilities Capabilities => renderer.Capabilities;
        public void ValidateSceneInputs(PreviewFrame frame) => renderer.ValidateSceneInputs(frame with { Scene = scene });
        public ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame, CancellationToken cancellationToken = default)
            => renderer.CaptureAsync(frame with { Scene = scene }, cancellationToken);
        public ValueTask DisposeAsync() => renderer.DisposeAsync();
    }
    private static ViewQualityOptions DiffusionQuality() => new(Diffusion: new(true, .7f, .01f));

    private static void AssertCoverage(byte[] before, byte[] after)
    {
        for (int p = 0; p < before.Length; p += 4)
        {
            Assert.Equal(before[p + 3], after[p + 3]);
            if (after[p + 3] == 0) Assert.Equal(new byte[3], after.AsSpan(p, 3).ToArray());
        }
    }
    private static byte[] ReadPng(byte[] png)
    {
        int width = 0, height = 0; using var compressed = new MemoryStream();
        for (int p = 8; p < png.Length;)
        {
            int count = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p));
            string type = System.Text.Encoding.ASCII.GetString(png, p + 4, 4);
            if (type == "IHDR") { width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p + 8)); height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p + 12)); }
            if (type == "IDAT") compressed.Write(png.AsSpan(p + 8, count));
            p += count + 12;
        }
        compressed.Position = 0; using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream(); zlib.CopyTo(raw);
        byte[] scanlines = raw.ToArray(), result = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            Assert.Equal(0, scanlines[y * (width * 4 + 1)]);
            scanlines.AsSpan(y * (width * 4 + 1) + 1, width * 4).CopyTo(result.AsSpan(y * width * 4));
        }
        return result;
    }
    internal static OriginalFrameScene Frame(int samples = 1, bool smaa = false, Vector4? color = null)
    {
        var quad = D3D12MeshTests.Quad();
        byte[] pattern = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
        {
            int p = (y * 8 + x) * 4;
            pattern[p] = (byte)(25 + x * 25); pattern[p + 1] = (byte)(35 + y * 20);
            pattern[p + 2] = (byte)((x + y) % 2 == 0 ? 210 : 40);
            pattern[p + 3] = (byte)((x is 3 or 4 && y is 3 or 4) ? 0 : (x % 2 == 0 ? 128 : 255));
        }
        var scene = new SceneSnapshot(quad.Vertices, quad.Indices,
            [new("explicit alpha test", 0, 6, color ?? Vector4.One, Vector3.Zero, Vector3.Zero, 1, color is null ? 0 : -1, AlphaCutoff: 0)],
            color is null ? [new("coverage holes", 8, 8, pattern)] : [], camera: quad.Camera, lightDirection: -Vector3.UnitZ);
        return new(new(scene, [new(Vector3.One, DoubleSided: true)],
            new(Vector3.One, Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, false, false, BackgroundColor: Vector4.Zero,
                AlphaOutput: FrameAlphaOutput.Straight, SpatialSamples: samples,
                Antialiasing: smaa ? FrameAntialiasing.Smaa1X : FrameAntialiasing.Off));
    }
}
