using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
[Trait("Category", "Gpu")]
public sealed class D3D12TemporalSsaaTests
{
    private const int Width = 69, Height = 51;

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void ThreeLogicalFramesMatchTwoIndependentJitteredHistoriesWhileGeometryAndCameraMove(LightingMode mode)
    {
        using var combined = new D3D12FrameSession();
        using var sampleZero = new D3D12FrameSession();
        using var sampleOne = new D3D12FrameSession();
        RequireBackend(combined, mode);
        var independent = new[] { sampleZero, sampleOne };
        var source = Fixture();
        var gi = Gi(mode);
        byte[]? first = null;
        for (int logicalFrame = 0; logicalFrame < 3; logicalFrame++)
        {
            // A small foreground wall supplies both subpixel coverage and genuine disocclusion.
            // Stable topology with changed positions/normals exercises previous-triangle leases.
            var pose = MovingPose(source, logicalFrame);
            var selected = WithOptions(pose, pose.Options! with { SpatialSamples = 2 });
            var actual = combined.Capture(selected, Width, Height, gi);
            var statistics = AssertTemporal(combined, mode, 2, Width, Height, (ulong)logicalFrame + 1);
            Assert.Equal(logicalFrame == 0, statistics.TemporalResetHistory);
            Assert.Equal(1, statistics.PaletteBandReceiverMaterials);
            Assert.Equal(mode == LightingMode.HardwareRayTracing && logicalFrame == 0 ? 1 : 0, statistics.AccelerationBuilds);
            Assert.Equal(mode == LightingMode.HardwareRayTracing && logicalFrame > 0 ? 1 : 0, statistics.AccelerationUpdates);
            Assert.Equal(1, combined.Statistics!.SubmissionCount);
            Assert.Equal(2, combined.Statistics.SpatialAccumulationDispatches);
            Assert.Equal(1, combined.Statistics.SpatialResolveDrawCalls);

            var sum = new int[actual.Rgba.Length];
            for (int sample = 0; sample < independent.Length; sample++)
            {
                var camera = pose.Options!.CameraOverride!;
                var jittered = WithOptions(pose, pose.Options with
                {
                    SpatialSamples = 1,
                    CameraOverride = camera with
                    {
                        ProjectionJitterPixels = camera.ProjectionJitterPixels + SpatialSampling.JitterPixels(2, sample)
                    }
                });
                var reference = independent[sample].Capture(jittered, Width, Height, gi);
                var referenceStatistics = AssertTemporal(independent[sample], mode, 1, Width, Height, (ulong)logicalFrame + 1);
                Assert.Equal(statistics.Seed, referenceStatistics.Seed);
                Assert.Equal(statistics.TemporalResetHistory, referenceStatistics.TemporalResetHistory);
                for (int channel = 0; channel < sum.Length; channel++) sum[channel] += reference.Rgba[channel];
            }

            // Independent RGBA8 outputs introduce at most one quantization step relative to
            // the target's floating GPU accumulation followed by its single RGBA8 resolve.
            for (int channel = 0; channel < sum.Length; channel++)
                Assert.InRange(Math.Abs(actual.Rgba[channel] - sum[channel] / 2.0), 0, 1.01);
            if (logicalFrame == 0) first = actual.Rgba;
            else Assert.NotEqual(first, actual.Rgba);
        }
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void AbortedSampleCountChangePreservesOldBanksAndSuccessfulChangesNeverResurrectStaleHistory(LightingMode mode)
    {
        using var session = new D3D12FrameSession();
        using var control = new D3D12FrameSession();
        RequireBackend(session, mode);
        var frame = Fixture();
        var selected = WithOptions(frame, frame.Options! with { SpatialSamples = 2 });
        var four = WithOptions(frame, frame.Options! with { SpatialSamples = 4 });
        var gi = Gi(mode);
        for (int i = 0; i < 2; i++)
            Assert.Equal(control.Capture(selected, Width, Height, gi).Rgba, session.Capture(selected, Width, Height, gi).Rgba);

        long submissions = session.Runtime.Submissions, frameId = session.Runtime.FrameId;
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Render(four, 0, Height, gi));
        Assert.Equal(frameId, session.Runtime.FrameId);
        Assert.Equal(submissions, session.Runtime.Submissions);
        var excessive = WithOptions(frame, frame.Options! with { SpatialSamples = 32 });
        var budgetError = Assert.Throws<D3D12GiMemoryBudgetExceededException>(() => session.Render(excessive, 4096, 4096, gi));
        Assert.Contains("no GPU frame was begun", budgetError.Message);
        Assert.Equal(frameId, session.Runtime.FrameId);
        Assert.Equal(submissions, session.Runtime.Submissions);
        Assert.False(session.Runtime.IsRecording);
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(four, Width, Height, gi,
            (_, target) =>
            {
                Assert.Equal(ResourceStates.CopySource, target.State);
                AssertTemporal(session, mode, 4, Width, Height, 1);
                throw new OperationCanceledException("Abort after every new sample bank was recorded, before Submit.");
            }));
        Assert.False(session.Runtime.IsRecording);
        Assert.Equal(submissions, session.Runtime.Submissions);
        Assert.Null(session.RayGiStatistics);
        Assert.Equal(control.Capture(selected, Width, Height, gi).Rgba, session.Capture(selected, Width, Height, gi).Rgba);
        AssertTemporal(session, mode, 2, Width, Height, 3);
        Assert.False(session.RayGiStatistics!.TemporalResetHistory);
        Assert.Equal(control.RayGiStatistics!.Seed, session.RayGiStatistics.Seed);

        foreach (var changed in new[] { four, selected })
        {
            var actual = session.Capture(changed, Width, Height, gi);
            Assert.Equal(Fresh(changed, Width, Height, gi).Rgba, actual.Rgba);
            var statistics = AssertTemporal(session, mode, changed.Options!.SpatialSamples, Width, Height, 1);
            Assert.True(statistics.TemporalResetHistory);
            Assert.Equal("SampleCountChanged", statistics.TemporalResetReason);
            Assert.Equal(gi.Seed, statistics.Seed);
        }
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void OffEmptyResizeGenerationAndCutResetEveryBankAndSmaaStaysAfterSpatialResolve(LightingMode mode)
    {
        using var session = new D3D12FrameSession();
        RequireBackend(session, mode);
        var frame = Fixture();
        var selected = WithOptions(frame, frame.Options! with { SpatialSamples = 2 });
        var gi = Gi(mode);
        var baseline = session.Capture(selected, Width, Height, RayGiOptions.Off);
        for (int i = 0; i < 2; i++) using (session.Render(selected, Width, Height, gi)) { }
        AssertTemporal(session, mode, 2, Width, Height, 2);
        Assert.Equal(baseline.Rgba, session.Capture(selected, Width, Height, RayGiOptions.Off).Rgba);
        Assert.Null(session.RayGiStatistics);
        Assert.Equal(Fresh(selected, Width, Height, gi).Rgba, session.Capture(selected, Width, Height, gi).Rgba);
        Assert.True(AssertTemporal(session, mode, 2, Width, Height, 1).TemporalResetHistory);

        var empty = new OriginalFrameScene(new OriginalStyleScene(SceneSnapshot.CreateEmpty(), []),
            options: selected.Options! with { SpatialSamples = 32 });
        using (var output = session.Render(empty, Width, Height, gi))
        {
            Assert.Equal(0, output.Statistics.ReadbackCount);
            Assert.Equal(1, output.Statistics.SubmissionCount);
            Assert.Null(session.RayGiStatistics);
            Assert.Equal(32, output.Statistics.SpatialAccumulationDispatches);
            Assert.Equal(1, output.Statistics.SpatialResolveDrawCalls);
            Assert.Equal(33, session.LastRecordings.Count);
            Assert.Equal(32, session.LastRecordings.Count(value => value.Channel == "ssaa:accumulate"));
            Assert.Equal("ssaa:resolve", session.LastRecordings[^1].Channel);
            Assert.DoesNotContain(session.LastRecordings, value => value.Channel == "original-frame");
        }
        Assert.Equal(Fresh(selected, Width, Height, gi).Rgba, session.Capture(selected, Width, Height, gi).Rgba);
        Assert.True(AssertTemporal(session, mode, 2, Width, Height, 1).TemporalResetHistory);

        const int resizedWidth = 47, resizedHeight = 35;
        Assert.Equal(Fresh(selected, resizedWidth, resizedHeight, gi).Rgba,
            session.Capture(selected, resizedWidth, resizedHeight, gi).Rgba);
        Assert.Equal("Resized", AssertTemporal(session, mode, 2, resizedWidth, resizedHeight, 1).TemporalResetReason);
        foreach (var reset in new[] { gi with { TemporalHistoryGeneration = 19 }, gi with { TemporalHistoryGeneration = 19, TemporalCameraCutKey = 23 } })
        {
            Assert.Equal(Fresh(selected, resizedWidth, resizedHeight, reset).Rgba,
                session.Capture(selected, resizedWidth, resizedHeight, reset).Rgba);
            Assert.True(AssertTemporal(session, mode, 2, resizedWidth, resizedHeight, 1, reset.Seed).TemporalResetHistory);
        }

        var smaa = WithOptions(selected, selected.Options! with { Antialiasing = FrameAntialiasing.Smaa1X, SmaaQuality = SmaaQuality.High });
        using var retained = session.Render(smaa, resizedWidth, resizedHeight,
            gi with { TemporalHistoryGeneration = 19, TemporalCameraCutKey = 23 });
        AssertTemporal(session, mode, 2, resizedWidth, resizedHeight, 2);
        Assert.Equal(0, retained.Statistics.ReadbackCount);
        Assert.Equal(1, retained.Statistics.SubmissionCount);
        Assert.Equal(2, retained.Statistics.SpatialAccumulationDispatches);
        Assert.Equal(1, retained.Statistics.SpatialResolveDrawCalls);
        Assert.Equal(3, retained.Statistics.AntialiasingPassDrawCalls);
        Assert.Equal(new[] { "smaa:edges", "smaa:weights", "smaa:neighborhood" }, session.LastRecordings.TakeLast(3).Select(value => value.Channel));
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void ThirtyTwoSmallBanksShareOneAccelerationAndThirtyQueuedFramesRemainBoundedWithoutReadback(LightingMode mode)
    {
        const int width = 17, height = 13;
        using var session = new D3D12FrameSession();
        RequireBackend(session, mode);
        var frame = Fixture();
        var gi = Gi(mode) with { SamplesPerPixel = 1 };
        var thirtyTwo = WithOptions(frame, frame.Options! with { SpatialSamples = 32 });
        int descriptorHighWater;
        using (var output = session.Render(thirtyTwo, width, height, gi))
        {
            var statistics = AssertTemporal(session, mode, 32, width, height, 1);
            Assert.Equal(mode == LightingMode.HardwareRayTracing ? 1 : 0, statistics.AccelerationBuilds);
            Assert.Equal(0, statistics.AccelerationUpdates);
            Assert.Equal(0, output.Statistics.ReadbackCount);
            Assert.Equal(1, output.Statistics.SubmissionCount);
            Assert.Equal(32, output.Statistics.SpatialAccumulationDispatches);
            Assert.Equal(1, output.Statistics.SpatialResolveDrawCalls);
            Assert.Equal(frame.Surface.Snapshot.Textures.Count + 4 + 2 * 32, output.Statistics.DescriptorHighWater);
            descriptorHighWater = output.Statistics.DescriptorHighWater;
        }

        var two = WithOptions(frame, frame.Options! with { SpatialSamples = 2 });
        int low = int.MaxValue, high = 0;
        D3D12FrameStatistics? warm = null;
        for (int i = 0; i < 30; i++)
        {
            using var output = session.Render(two, width, height, gi);
            var statistics = AssertTemporal(session, mode, 2, width, height, (ulong)i + 1);
            Assert.Equal(i == 0, statistics.TemporalResetHistory);
            Assert.Equal(0, statistics.AccelerationBuilds);
            Assert.Equal(0, statistics.AccelerationUpdates);
            Assert.Equal(0, output.Statistics.ReadbackCount);
            Assert.Equal(1, output.Statistics.SubmissionCount);
            // This counter records the session-wide maximum; selecting 2x must not
            // exceed the earlier exact 32x reservation or incorrectly lower it.
            Assert.Equal(descriptorHighWater, output.Statistics.DescriptorHighWater);
            Assert.InRange(output.Statistics.PendingFrames, 0, 3);
            if (i < 10) continue;
            warm ??= output.Statistics;
            Assert.Equal(warm.CachedResources, output.Statistics.CachedResources);
            Assert.Equal(warm.BufferUploads, output.Statistics.BufferUploads);
            Assert.Equal(warm.ShaderCompilations, output.Statistics.ShaderCompilations);
            Assert.Equal(warm.PipelineCreations, output.Statistics.PipelineCreations);
            Assert.Equal(warm.FrameOwnedAllocations, output.Statistics.FrameOwnedAllocations);
            low = Math.Min(low, output.Statistics.LiveFrameResources);
            high = Math.Max(high, output.Statistics.LiveFrameResources);
        }
        Assert.InRange(high - low, 0, 8);
        session.WaitForIdle();
        Assert.InRange(session.Runtime.LiveFrameResources, 0, 3);
    }

    private static D3D12RayGiStatistics AssertTemporal(D3D12FrameSession session, LightingMode mode, int samples,
        int width, int height, ulong frames, uint seed = 31)
    {
        var statistics = Assert.IsType<D3D12RayGiStatistics>(session.RayGiStatistics);
        Assert.Equal(mode, statistics.Effective);
        Assert.Equal(RayGiDenoiser.TemporalSpatial, statistics.Denoiser);
        Assert.Equal(samples, statistics.DispatchCount);
        Assert.Equal(2 * samples, statistics.GBufferDrawCalls);
        Assert.Equal(2 * samples, statistics.DenoiseDispatchCount);
        Assert.Equal(frames, statistics.TemporalHistoryFrames);
        Assert.Equal((long)width * height * 64 * samples, statistics.TemporalHistoryBytes);
        Assert.Equal(unchecked(seed + (uint)(frames - 1) * 0x9e3779b9u), statistics.Seed);
        return statistics;
    }

    private static void RequireBackend(D3D12FrameSession session, LightingMode mode)
    {
        var capabilities = session.QueryRayGiCapabilities();
        Assert.True(mode == LightingMode.HardwareRayTracing ? capabilities.HardwareAvailable : capabilities.SoftwareAvailable,
            $"Requested {mode}: adapter={session.AdapterName}; tier={capabilities.RawRaytracingTier}; SM={capabilities.ShaderModel}; {capabilities.UnavailableReason}");
    }

    private static RayGiOptions Gi(LightingMode mode) => new(mode, Strength: .5f, SamplesPerPixel: 4,
        Seed: 31, MaxRayDistance: 50, RayEpsilon: .001f, Composition: RayGiComposition.ToonPalette,
        Denoiser: RayGiDenoiser.TemporalSpatial);

    private static RenderedFrame Fresh(OriginalFrameScene frame, int width, int height, RayGiOptions gi)
    {
        using var fresh = new D3D12FrameSession();
        return fresh.Capture(frame, width, height, gi);
    }

    private static OriginalFrameScene Fixture()
    {
        var source = D3D12RayGiTests.Bounce();
        var bindings = source.Surface.Materials.ToArray();
        bindings[0] = bindings[0] with { ToonShadowColor = new(.2f, .3f, .4f) };
        var flags = source.Materials.ToArray();
        flags[0] = flags[0] with { GiToonResponseOverride = RayGiToonResponse.PreservePaletteBands };
        flags[1] = flags[1] with { GiToonResponseOverride = RayGiToonResponse.Continuous };
        var camera = source.Surface.Snapshot.Camera with { ProjectionJitterPixels = new(.125f, -.0625f) };
        return new(new OriginalStyleScene(source.Surface.Snapshot, bindings, source.Surface.Profile), source.Outline,
            source.OutlineOptions, source.Options! with { CameraOverride = camera }, flags, source.Lighting, source.EditorOverlay);
    }

    private static OriginalFrameScene MovingPose(OriginalFrameScene source, int logicalFrame)
    {
        var vertices = source.Surface.Snapshot.Vertices.ToArray();
        float offset = logicalFrame == 2 ? .45f : 0;
        for (int i = 4; i < vertices.Length; i++) vertices[i] = vertices[i] with
        {
            Position = new(vertices[i].Position.X * (.75f / 15) + offset, vertices[i].Position.Y * (.75f / 15), -2)
        };
        if (logicalFrame == 2)
        {
            var rotation = Matrix4x4.CreateRotationY(5 * MathF.PI / 180);
            for (int i = 0; i < 4; i++) vertices[i] = vertices[i] with { Normal = Vector3.TransformNormal(vertices[i].Normal, rotation) };
        }
        var camera = source.Options!.CameraOverride!;
        camera = camera with { Eye = camera.Eye + new Vector3(.025f * logicalFrame, 0, 0), Target = camera.Target + new Vector3(.025f * logicalFrame, 0, 0) };
        return new(new OriginalStyleScene(source.Surface.Snapshot.WithDeformedVertices(vertices), source.Surface.Materials, source.Surface.Profile),
            source.Outline, source.OutlineOptions, source.Options with { CameraOverride = camera }, source.Materials, source.Lighting, source.EditorOverlay);
    }

    private static OriginalFrameScene WithOptions(OriginalFrameScene frame, OriginalFrameOptions options)
        => new(frame.Surface, frame.Outline, frame.OutlineOptions, options, frame.Materials, frame.Lighting, frame.EditorOverlay);
}
