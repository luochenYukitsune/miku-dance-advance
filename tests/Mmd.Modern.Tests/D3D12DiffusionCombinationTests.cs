using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

// Fresh integration supplement. Root must copy into the test project, compile and
// execute it before counting coverage. No product-only hooks or CPU rendering path.
[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12DiffusionCombinationTests
{
    private const int Width = 97, Height = 73;

    [Fact, Trait("Category", "Gpu")]
    public void DownstreamFilterEditsKeepNativeAaAndSoftwareGiHistoriesWarm()
    {
        using var session = new D3D12FrameSession();
        RequireGi(session);
        var frame = Frame(); var temporal = Fsr(D3D12FsrQualityPreset.NativeAa); var gi = Gi();
        D3D12DiffusionOptions?[] sequence = [null, null, new(true, .7f, .01f),
            new(true, .4f, .002f), D3D12DiffusionOptions.Off, new(true, .6f, .006f), null];
        long reads = session.Runtime.Readbacks;
        foreach (var item in sequence.Select((options, index) => (options, index)))
        {
            using var output = session.Render(frame, Width, Height, gi,
                temporalUpscaling: temporal, diffusion: item.options);
            Assert.Equal(0, output.Statistics.ReadbackCount);
            Assert.Equal(1, output.Statistics.SubmissionCount);
            var lighting = Assert.IsType<D3D12RayGiStatistics>(session.RayGiStatistics);
            var aa = Assert.IsType<D3D12TemporalUpscalingStatistics>(session.TemporalUpscalingStatistics);
            Assert.Equal((ulong)item.index + 1, lighting.TemporalHistoryFrames);
            Assert.Equal(item.index == 0 ? "FirstFrame" : "None", lighting.TemporalResetReason);
            Assert.Equal(item.index == 0 ? "FirstFrame" : "None", aa.ResetReason);
            Assert.Equal(item.index + 1L, aa.SubmittedFrames);
            Assert.Equal(item.options?.IsActive == true ? 2 : 0,
                session.LastRecordings.Count(r => r.Channel.StartsWith("diffusion:", StringComparison.Ordinal)));
            Assert.InRange(output.Statistics.PendingFrames, 0, 3);
        }
        Assert.Equal(reads, session.Runtime.Readbacks);
    }

    [Theory, Trait("Category", "Gpu")]
    [InlineData(false)]
    [InlineData(true)]
    public void ReconstructionFinishesBeforeDisplaySizedDiffusionAndBypassRestoresExactOutput(bool temporalMode)
    {
        using var baseline = new D3D12FrameSession(); using var filtered = new D3D12FrameSession();
        var frame = Frame();
        var temporal = temporalMode ? Fsr(D3D12FsrQualityPreset.Quality) : null;
        var spatial = temporalMode ? null : new D3D12SpatialUpscalingOptions { Mode = D3D12SpatialUpscalingMode.Fsr1 };
        // Paired independently owned SDK sessions receive precisely the same frames.
        // This avoids comparing different jitter/history phases after a second capture.
        for (int phase = 0; phase < 5; phase++)
        {
            D3D12DiffusionOptions? options = phase is 1 or 2 ? new(true, .7f, .01f) : null;
            var before = baseline.Capture(frame, Width, Height, upscaling: spatial, temporalUpscaling: temporal);
            var after = filtered.Capture(frame, Width, Height, upscaling: spatial, temporalUpscaling: temporal, diffusion: options);
            Assert.Equal(Width, after.Width); Assert.Equal(Height, after.Height);
            if (options is null) Assert.Equal(before.Rgba, after.Rgba);
            else
            {
                AssertImage(DiffusionReferenceImage.Apply(before.Rgba, Width, Height, options.Strength, options.Extent), after.Rgba);
                Assert.NotEqual(before.Rgba, after.Rgba);
                var targets = filtered.LastTargets.Where(t => t.Name.StartsWith("$diffusion-", StringComparison.Ordinal)).ToArray();
                Assert.Equal(2, targets.Length);
                Assert.All(targets, t => { Assert.Equal(Width, t.Width); Assert.Equal(Height, t.Height); });
                Assert.Equal(new[] { "diffusion:x", "diffusion:y" }, filtered.LastRecordings
                    .Where(r => r.Channel.StartsWith("diffusion:", StringComparison.Ordinal)).Select(r => r.Channel));
            }
            if (temporalMode)
            {
                Assert.Equal(baseline.TemporalUpscalingStatistics!.JitterPhase, filtered.TemporalUpscalingStatistics!.JitterPhase);
                Assert.Equal(baseline.TemporalUpscalingStatistics.ResetReason, filtered.TemporalUpscalingStatistics.ResetReason);
                Assert.True(filtered.TemporalUpscalingStatistics.RenderHeight < Height);
            }
            else Assert.True(filtered.Statistics!.UpscalingInputHeight < Height);
        }
    }

    [Fact, Trait("Category", "Gpu")]
    public void AbortAfterBothDiffusionPassesRetainsGiAndRetryMatchesIndependentReplayedHistory()
    {
        using var actual = new D3D12FrameSession(); using var reference = new D3D12FrameSession();
        RequireGi(actual); RequireGi(reference);
        var frame = Frame(); var temporal = Fsr(D3D12FsrQualityPreset.NativeAa); var gi = Gi();
        var diffusion = new D3D12DiffusionOptions(true, .65f, .008f);
        for (int i = 0; i < 2; i++)
        {
            using var a = actual.Render(frame, Width, Height, gi, temporalUpscaling: temporal, diffusion: diffusion);
            using var b = reference.Render(frame, Width, Height, gi, temporalUpscaling: temporal, diffusion: diffusion);
        }
        long submissions = actual.Runtime.Submissions;
        bool appended = false;
        Assert.Throws<OperationCanceledException>(() => actual.RenderForPresent(frame, Width, Height, gi,
            (_, target) =>
            {
                appended = true;
                Assert.Equal("$diffusion-y", target.Name);
                Assert.Equal(Width, target.Width); Assert.Equal(Height, target.Height);
                throw new OperationCanceledException("Abort only after both downstream filter draws have been recorded.");
            }, temporalUpscaling: temporal, diffusion: diffusion with { Strength = .35f }));
        Assert.True(appended); Assert.False(actual.Runtime.IsRecording);
        Assert.Equal(submissions, actual.Runtime.Submissions);
        // Existing transaction contract: aborted SDK dispatch rebuilds AA, whereas
        // software-GI keeps its two previously submitted frames. Replay that exact
        // state in the independent reference, resetting only AA explicitly.
        var retry = actual.Capture(frame, Width, Height, gi, temporalUpscaling: temporal, diffusion: diffusion);
        Assert.Equal("AbortedFrame", actual.TemporalUpscalingStatistics!.ResetReason);
        Assert.Equal("None", actual.RayGiStatistics!.TemporalResetReason);
        Assert.Equal(3UL, actual.RayGiStatistics.TemporalHistoryFrames);
        var expected = reference.Capture(frame, Width, Height, gi,
            temporalUpscaling: temporal with { ResetHistory = true }, diffusion: diffusion);
        Assert.Equal(expected.Rgba, retry.Rgba);
        actual.WaitForIdle();
        var warm = actual.Statistics!;
        long reads = actual.Runtime.Readbacks;
        for (int i = 0; i < 6; i++)
        {
            using var output = actual.Render(frame, Width, Height, gi, temporalUpscaling: temporal,
                diffusion: diffusion with { Strength = .3f + i * .05f });
            Assert.Equal("None", actual.TemporalUpscalingStatistics!.ResetReason);
            Assert.Equal("None", actual.RayGiStatistics!.TemporalResetReason);
            Assert.Equal((ulong)i + 4, actual.RayGiStatistics.TemporalHistoryFrames);
            Assert.Equal(warm.ShaderCompilations, output.Statistics.ShaderCompilations);
            Assert.Equal(warm.PipelineCreations, output.Statistics.PipelineCreations);
            Assert.Equal(warm.CachedResources, output.Statistics.CachedResources);
            Assert.InRange(output.Statistics.PendingFrames, 0, 3);
            // Exactly three retained recording slots, bounded by one complete
            // frame's owned allocations plus its constant pages. No per-slider cache.
            Assert.InRange(output.Statistics.LiveFrameResources, 0,
                checked(3 * (warm.FrameOwnedAllocations + warm.ConstantAllocations + 4)));
            Assert.Equal(0, output.Statistics.ReadbackCount);
        }
        Assert.Equal(reads, actual.Runtime.Readbacks);
    }

    private static void AssertImage(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < actual.Length; i++)
            if ((i & 3) == 3) Assert.Equal((byte)255, actual[i]);
            else Assert.InRange(Math.Abs(expected[i] - actual[i]), 0, 2);
    }

    private static D3D12TemporalUpscalingOptions Fsr(D3D12FsrQualityPreset quality)
        => new() { Mode = D3D12TemporalUpscalingMode.Fsr, QualityPreset = quality,
            LoaderPath = Environment.GetEnvironmentVariable("MMD_FSR_LOADER_PATH")
                ?? throw new InvalidOperationException("Explicit GPU test requires the installed real FSR SDK loader.") };
    private static void RequireGi(D3D12FrameSession session)
    {
        var capabilities = session.QueryRayGiCapabilities();
        Assert.True(capabilities.SoftwareAvailable, capabilities.UnavailableReason);
    }
    private static RayGiOptions Gi() => new(LightingMode.SoftwareRayTracing, SamplesPerPixel: 4, Seed: 31,
        MaxRayDistance: 30, RayEpsilon: .001f, Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.TemporalSpatial);

    private static OriginalFrameScene Frame()
    {
        byte[] pixels = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
        { int p = (y * 8 + x) * 4; pixels[p] = (byte)(25 + x * 23); pixels[p + 1] = (byte)(20 + y * 21); pixels[p + 2] = (byte)((x + y) % 2 == 0 ? 190 : 35); pixels[p + 3] = 255; }
        SceneVertex[] vertices =
        [new(new(-1.5f, -1.1f, 0), -Vector3.UnitZ, new(0, 1)), new(new(1.5f, -1.1f, 0), -Vector3.UnitZ, new(1, 1)),
            new(new(1.5f, 1.1f, 0), -Vector3.UnitZ, new(1, 0)), new(new(-1.5f, 1.1f, 0), -Vector3.UnitZ, new(0, 0)),
            new(new(-5, -5, -7), Vector3.UnitZ, Vector2.Zero), new(new(5, -5, -7), Vector3.UnitZ, Vector2.Zero),
            new(new(5, 5, -7), Vector3.UnitZ, Vector2.Zero), new(new(-5, 5, -7), Vector3.UnitZ, Vector2.Zero)];
        var snapshot = new SceneSnapshot(vertices, [0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6],
            [new("pattern receiver", 0, 6, new(.25f, .18f, .12f, 1), Vector3.Zero, Vector3.Zero, 10, 0),
                new("indirect wall", 6, 6, new(.8f, .18f, .12f, 1), Vector3.Zero, Vector3.Zero, 1)],
            [new("explicit unchanged test pattern", 8, 8, pixels)],
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 4, .1f, 30, VerticalFovDegrees: 45), lightDirection: -Vector3.UnitZ);
        return new(new(snapshot, [new(new(.22f), DoubleSided: true), new(Vector3.One, DoubleSided: true)],
            new(Vector3.One, Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: new(0, 0, 0, 1)),
            materials: [new(false, false, false), new(false, false, false)]);
    }
}
