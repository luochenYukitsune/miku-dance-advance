using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12SsaaGiTests
{
    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing, false)]
    [InlineData(LightingMode.HardwareRayTracing, false)]
    [InlineData(LightingMode.SoftwareRayTracing, true)]
    [InlineData(LightingMode.HardwareRayTracing, true)]
    public void TwoActualGiSamplesMatchIndependentJitteredCaptures(LightingMode mode, bool denoise)
    {
        const int width = 69, height = 51, samples = 2;
        using var session = new D3D12FrameSession();
        RequireBackend(session, mode);
        var frame = Fixture();
        var camera = frame.Surface.Snapshot.Camera with { ProjectionJitterPixels = new(.125f, -.0625f) };
        frame = WithOptions(frame, frame.Options! with { CameraOverride = camera });
        var options = Gi(mode, denoise);
        var off = session.Capture(WithOptions(frame, frame.Options! with { SpatialSamples = samples }), width, height);
        var result = session.Capture(WithOptions(frame, frame.Options! with { SpatialSamples = samples }), width, height, rayGiOptions: options);
        Assert.NotEqual(off.Rgba, result.Rgba);
        Assert.Equal(samples, session.RayGiStatistics!.DispatchCount);
        Assert.Equal(denoise ? samples : 0, session.RayGiStatistics.DenoiseDispatchCount);
        Assert.Equal(mode, session.RayGiStatistics.Effective);
        Assert.Equal(samples * frame.Materials.Count, session.RayGiStatistics.GBufferDrawCalls);
        Assert.Equal(samples, session.Statistics!.SpatialAccumulationDispatches);
        Assert.Equal(1, session.Statistics.SpatialResolveDrawCalls);
        Assert.Equal(1, session.Statistics.SubmissionCount);
        Assert.Equal(samples, session.LastRecordings.Count(r => r.Channel == "original-frame"));

        var sum = new int[result.Rgba.Length];
        for (int sample = 0; sample < samples; sample++)
        {
            var jittered = WithOptions(frame, frame.Options! with
            { CameraOverride = camera with { ProjectionJitterPixels = camera.ProjectionJitterPixels + SpatialSampling.JitterPixels(samples, sample) } });
            var single = session.Capture(jittered, width, height, rayGiOptions: options);
            for (int p = 0; p < sum.Length; p++) sum[p] += single.Rgba[p];
        }
        for (int p = 0; p < sum.Length; p++)
            Assert.InRange(Math.Abs(result.Rgba[p] - (double)sum[p] / samples), 0, 1.01);
        Assert.Equal(off.Rgba, session.Capture(WithOptions(frame, frame.Options! with { SpatialSamples = samples }), width, height).Rgba);
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void ThirtyTwoSamplesRetainUniqueDescriptorsUntilFenceAndResourcesStayBounded(LightingMode mode)
    {
        using var session = new D3D12FrameSession();
        RequireBackend(session, mode);
        var frame = Fixture();
        var options = Gi(mode, true) with { SamplesPerPixel = 1 };
        using (session.Render(WithOptions(frame, frame.Options! with { SpatialSamples = 2 }), 17, 13, rayGiOptions: options)) { }
        var warm = session.Statistics!;
        var selected = WithOptions(frame, frame.Options! with { SpatialSamples = 32 });
        int? owned = null;
        for (int i = 0; i < 6; i++)
        {
            using var output = session.Render(selected, 17, 13, rayGiOptions: options);
            var stats = output.Statistics;
            Assert.Equal(32, session.RayGiStatistics!.DispatchCount);
            Assert.Equal(32, session.RayGiStatistics.DenoiseDispatchCount);
            Assert.Equal(0, session.RayGiStatistics.AccelerationBuilds);
            Assert.Equal(32, stats.SpatialAccumulationDispatches);
            Assert.Equal(0, stats.ReadbackCount);
            Assert.Equal(1, stats.SubmissionCount);
            Assert.Equal(frame.Surface.Snapshot.Textures.Count + 4 + 2 * 32, stats.DescriptorHighWater);
            Assert.Equal(warm.CachedResources, stats.CachedResources);
            Assert.Equal(warm.BufferUploads, stats.BufferUploads);
            owned ??= stats.FrameOwnedAllocations;
            Assert.Equal(owned.Value, stats.FrameOwnedAllocations);
            // GI receiver images are frame-owned per sample, never retained in a growing cache.
            Assert.InRange(stats.FrameOwnedAllocations, warm.FrameOwnedAllocations, warm.FrameOwnedAllocations + 31 * 12 + 12);
            Assert.InRange(stats.LiveFrameResources, 1, 3 * (owned.Value + 2));
            Assert.InRange(stats.PendingFrames, 0, 3);
        }
        session.WaitForIdle();
        // Only the reusable upload constant pages may remain after all frame fences retire.
        Assert.InRange(session.Runtime.LiveFrameResources, 0, 3);
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void SmaaFollowsAllGiSamplesAndCanceledSubmissionRecoversExactly(LightingMode mode)
    {
        using var session = new D3D12FrameSession();
        RequireBackend(session, mode);
        var frame = Fixture();
        frame = WithOptions(frame, frame.Options! with { SpatialSamples = 2, Antialiasing = FrameAntialiasing.Smaa1X });
        var options = Gi(mode, true);
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(frame, 47, 35, options,
            (_, target) =>
            {
                Assert.Equal(ResourceStates.CopySource, target.State);
                throw new OperationCanceledException("Synthetic SSAA/GI present cancellation");
            }));
        Assert.False(session.Runtime.IsRecording);
        Assert.Equal(0, session.Runtime.Submissions);
        var recovered = session.Capture(frame, 47, 35, rayGiOptions: options);
        Assert.Equal(2, session.RayGiStatistics!.DispatchCount);
        Assert.Equal(3, session.Statistics!.AntialiasingPassDrawCalls);
        Assert.Equal(new[] { "smaa:edges", "smaa:weights", "smaa:neighborhood" }, session.LastRecordings.TakeLast(3).Select(r => r.Channel));
        using var fresh = new D3D12FrameSession();
        Assert.Equal(fresh.Capture(frame, 47, 35, rayGiOptions: options).Rgba, recovered.Rgba);
        using var retained = session.Render(frame, 47, 35, rayGiOptions: options);
        Assert.Equal(0, retained.Statistics.ReadbackCount);
    }

    [Fact]
    public void IdentityAndSingleSampleKeepExistingPathAndInvalidPaletteRejectsBeforeBegin()
    {
        using var session = new D3D12FrameSession();
        var frame = Fixture();
        var baseline = session.Capture(frame, 47, 35);
        var identity = WithOptions(frame, frame.Options! with { SpatialSamples = 1 });
        Assert.Equal(baseline.Rgba, session.Capture(identity, 47, 35, rayGiOptions: RayGiOptions.Off).Rgba);
        var bindings = frame.Surface.Materials.ToArray();
        bindings[0] = bindings[0] with { TextureFactors = OriginalMaterialTextureFactors.Identity with
        { Toon = new(new(8,8,8,1), Vector4.Zero) } };
        var changed = new OriginalFrameScene(new OriginalStyleScene(frame.Surface.Snapshot, bindings, frame.Surface.Profile),
            frame.Outline, frame.OutlineOptions, frame.Options, frame.Materials, frame.Lighting, frame.EditorOverlay);
        foreach (int samples in new[] { 1, 2 })
        {
            long frameId = session.Runtime.FrameId, submissions = session.Runtime.Submissions;
            var error = Assert.Throws<NotSupportedException>(() => session.Capture(
                WithOptions(changed, changed.Options! with { SpatialSamples = samples }), 47, 35,
                rayGiOptions: Gi(LightingMode.SoftwareRayTracing, true)));
            Assert.Contains("ToonPalette", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(session.Runtime.IsRecording);
            Assert.Equal(frameId, session.Runtime.FrameId);
            Assert.Equal(submissions, session.Runtime.Submissions);
        }
        Assert.Equal(baseline.Rgba, session.Capture(frame, 47, 35).Rgba);
    }

    private static void RequireBackend(D3D12FrameSession session, LightingMode mode)
    {
        var support = session.QueryRayGiCapabilities();
        Assert.True(mode == LightingMode.HardwareRayTracing ? support.HardwareAvailable : support.SoftwareAvailable,
            $"Requested {mode}: adapter={session.AdapterName}; tier={support.RawRaytracingTier}; SM={support.ShaderModel}; {support.UnavailableReason}");
    }

    private static RayGiOptions Gi(LightingMode mode, bool denoise) => new(mode, Strength: .5f,
        SamplesPerPixel: 4, Seed: 31, MaxRayDistance: 50, RayEpsilon: .001f,
        Composition: denoise ? RayGiComposition.ToonPalette : RayGiComposition.Additive,
        Denoiser: denoise ? RayGiDenoiser.Spatial : RayGiDenoiser.Off);

    private static OriginalFrameScene Fixture()
    {
        var frame = D3D12RayGiTests.Bounce();
        var bindings = frame.Surface.Materials.ToArray();
        bindings[0] = bindings[0] with { ToonShadowColor = new(.2f, .3f, .4f) };
        return new(new OriginalStyleScene(frame.Surface.Snapshot, bindings, frame.Surface.Profile),
            frame.Outline, frame.OutlineOptions, frame.Options, frame.Materials, frame.Lighting, frame.EditorOverlay);
    }

    private static OriginalFrameScene WithOptions(OriginalFrameScene frame, OriginalFrameOptions options) =>
        new(frame.Surface, frame.Outline, frame.OutlineOptions, options, frame.Materials, frame.Lighting, frame.EditorOverlay);
}
