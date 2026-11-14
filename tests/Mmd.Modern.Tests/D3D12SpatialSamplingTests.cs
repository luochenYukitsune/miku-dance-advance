using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12SpatialSamplingTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void EverySpatialSampleIsRenderedAndGpuAverageMatchesIndependentCaptures(int samples)
    {
        const int width = 97, height = 73;
        using var session = new D3D12FrameSession();
        var frame = Diagonal();
        var before = session.Capture(frame, width, height);
        var result = session.Capture(Options(frame, frame.Options! with { SpatialSamples = samples }), width, height);
        Assert.Equal(samples, session.LastRecordings.Count(r => r.Channel == "original-frame"));
        Assert.Equal(1, session.Statistics!.ReadbackCount);
        Assert.Equal(1, session.Statistics.SubmissionCount);
        Assert.Equal(0, session.Statistics.AntialiasingPassDrawCalls);
        Assert.Equal(samples, session.Statistics.SpatialSamples);
        Assert.Equal(samples, session.Statistics.SpatialAccumulationDispatches);
        Assert.Equal(1, session.Statistics.SpatialResolveDrawCalls);
        Assert.NotEqual(before.Rgba, result.Rgba);
        var sum = new int[result.Rgba.Length];
        for (int i = 0; i < samples; i++)
        {
            var camera = frame.Surface.Snapshot.Camera with { ProjectionJitterPixels = SpatialSampling.JitterPixels(samples, i) };
            var single = session.Capture(Options(frame, frame.Options! with { CameraOverride = camera }), width, height);
            for (int p = 0; p < sum.Length; p++) sum[p] += single.Rgba[p];
        }
        for (int p = 0; p < sum.Length; p++)
            Assert.InRange(Math.Abs(result.Rgba[p] - (double)sum[p] / samples), 0, 1.01);
        Assert.Equal(before.Rgba, session.Capture(frame, width, height).Rgba);
    }

    [Fact]
    public void EachSmaaQualityCompilesRealPresetAndCachesSeparately()
    {
        using var session = new D3D12FrameSession();
        var frame = Diagonal();
        using (session.Render(frame, 97, 73)) { }
        long shaders = session.Statistics!.ShaderCompilations;
        var outputs = new Dictionary<SmaaQuality, byte[]>();
        foreach (var quality in Enum.GetValues<SmaaQuality>())
        {
            var selected = Options(frame, frame.Options! with { Antialiasing = FrameAntialiasing.Smaa1X, SmaaQuality = quality });
            outputs[quality] = session.Capture(selected, 97, 73).Rgba;
            shaders += 6;
            Assert.Equal(shaders, session.Statistics!.ShaderCompilations);
            Assert.Equal(3, session.Statistics.AntialiasingPassDrawCalls);
            Assert.Equal(quality, session.Statistics.SmaaQuality);
        }
        foreach (var quality in Enum.GetValues<SmaaQuality>().Reverse())
        {
            var selected = Options(frame, frame.Options! with { Antialiasing = FrameAntialiasing.Smaa1X, SmaaQuality = quality });
            Assert.Equal(outputs[quality], session.Capture(selected, 97, 73).Rgba);
            Assert.Equal(shaders, session.Statistics!.ShaderCompilations);
        }
        var highDefault = Options(frame, frame.Options! with { Antialiasing = FrameAntialiasing.Smaa1X });
        Assert.Equal(outputs[SmaaQuality.High], session.Capture(highDefault, 97, 73).Rgba);
    }

    [Fact]
    public void StackedSmaaRunsOnceAfterAllSamplesAndRetainedResizeHasNoReadback()
    {
        using var session = new D3D12FrameSession();
        var original = Diagonal();
        var stacked = Options(original, original.Options! with {
            SpatialSamples = 4, Antialiasing = FrameAntialiasing.Smaa1X, SmaaQuality = SmaaQuality.Ultra });
        var baseline = session.Capture(stacked, 97, 73);
        Assert.Equal(4, session.LastRecordings.Count(r => r.Channel == "original-frame"));
        Assert.Equal(new[] { "smaa:edges", "smaa:weights", "smaa:neighborhood" },
            session.LastRecordings.TakeLast(3).Select(r => r.Channel));
        Assert.All(session.LastTargets, t => { Assert.Equal(97, t.Width); Assert.Equal(73, t.Height); });
        long compilations = session.Statistics!.ShaderCompilations;
        long pipelines = session.Statistics.PipelineCreations;
        foreach (var size in new[] { (1, 1), (39, 31), (161, 103), (97, 73) })
        {
            using var image = session.Render(stacked, size.Item1, size.Item2);
            Assert.Equal(0, image.Statistics.ReadbackCount);
            Assert.Equal(1, image.Statistics.SubmissionCount);
            Assert.Equal(3, image.Statistics.AntialiasingPassDrawCalls);
            Assert.Equal(compilations, image.Statistics.ShaderCompilations);
            Assert.Equal(pipelines, image.Statistics.PipelineCreations);
        }
        Assert.Equal(baseline.Rgba, session.Capture(stacked, 97, 73).Rgba);
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(stacked, 97, 73, null,
            (_, target) => { Assert.Equal(ResourceStates.CopySource, target.State); throw new OperationCanceledException("Synthetic present cancellation"); }));
        Assert.Equal(baseline.Rgba, session.Capture(stacked, 97, 73).Rgba);
        using var fresh = new D3D12FrameSession();
        Assert.Equal(baseline.Rgba, fresh.Capture(stacked, 97, 73).Rgba);
    }

    [Fact]
    public void SpatialSamplesKeepClearColorAndAlphaExactAndResourceCountBounded()
    {
        using var session = new D3D12FrameSession();
        var original = Diagonal();
        var selected = Options(original, original.Options! with { SpatialSamples = 2, BackgroundColor = new(41 / 255f, 88 / 255f, 177 / 255f, 113 / 255f) });
        var first = session.Capture(selected, 97, 73);
        Assert.Equal(new byte[] { 41, 88, 177, 113 }, first.Rgba.Take(4));
        Assert.Equal(first.Rgba, session.Capture(selected, 97, 73).Rgba);
        int allocations = session.Statistics!.FrameOwnedAllocations;
        var high = session.Capture(Options(original, selected.Options! with { SpatialSamples = 32 }), 97, 73);
        Assert.Equal(new byte[] { 41, 88, 177, 113 }, high.Rgba.Take(4));
        // Constants may need additional pages, but image/descriptor allocations must not multiply by N.
        Assert.InRange(session.Statistics!.FrameOwnedAllocations, allocations, allocations + 2);
        Assert.Equal(32, session.LastRecordings.Count(r => r.Channel == "original-frame"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpatialAverageIncludesOriginalSelfShadowAndGroundStencil(bool shadow)
    {
        using var session = new D3D12FrameSession();
        var frame = shadow ? D3D12OriginalFrameTests.Shadow(true) : D3D12OriginalFrameTests.Ground(true);
        const int width = 97, height = 73, samples = 4;
        var averaged = session.Capture(Options(frame, frame.Options! with { SpatialSamples = samples }), width, height);
        var sum = new int[averaged.Rgba.Length];
        for (int i = 0; i < samples; i++)
        {
            var camera = frame.Surface.Snapshot.Camera with { ProjectionJitterPixels = SpatialSampling.JitterPixels(samples, i) };
            var single = session.Capture(Options(frame, frame.Options! with { CameraOverride = camera }), width, height);
            for (int p = 0; p < sum.Length; p++) sum[p] += single.Rgba[p];
        }
        for (int p = 0; p < sum.Length; p++) Assert.InRange(Math.Abs(averaged.Rgba[p] - (double)sum[p] / samples), 0, 1.01);
    }


    internal static OriginalFrameScene Options(OriginalFrameScene frame, OriginalFrameOptions options) =>
        new(frame.Surface, frame.Outline, frame.OutlineOptions, options, frame.Materials, frame.Lighting, frame.EditorOverlay);

    internal static OriginalFrameScene Diagonal()
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
