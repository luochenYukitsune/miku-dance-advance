using System.Numerics;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

// Isolate from tests which temporarily enable diagnostic readbacks through process environment.
[Collection(RayGiTimingEnvironmentCollection.Name)]
[Trait("Category", "Gpu")]
public sealed class D3D12MixedLifecycleStressTests
{
    [Fact]
    public void QueuedDynamicSwitchesAbortResizeAndCloseKeepOwnersBoundedAndRetainedOutputsAlive()
    {
        const int warmFrames = 60;
        // Release verification can extend the same acceptance path without a
        // second stress implementation or changing the ordinary test duration.
        string? configured = Environment.GetEnvironmentVariable("MMD_GPU_STRESS_MEASURED_FRAMES");
        int measuredFrames = configured is null ? 120 : int.Parse(configured, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(measuredFrames, 120, 3600);
        var reports = new List<object>();
        for (int cycle = 0; cycle < 2; cycle++)
        {
            var session = new D3D12FrameSession();
            var leases = new List<D3D12FrameOutput>();
            try
            {
                var capabilities = session.QueryRayGiCapabilities();
                Assert.True(capabilities.HardwareAvailable, capabilities.UnavailableReason ?? session.AdapterName);
                Assert.True(capabilities.SoftwareAvailable, capabilities.UnavailableReason ?? session.AdapterName);
                int warmCached = 0, warmFrameOwned = 0, measuredCached = 0, measuredFrameOwned = 0, aborts = 0;
                long warmShaders = 0, warmPipelines = 0;
                var source = D3D12RayGiTests.Bounce();
                for (int i = 0; i < warmFrames + measuredFrames; i++)
                {
                    var (frame, width, height, gi, styles) = Select(source, i);
                    if (i >= warmFrames && i % 17 == 0)
                    {
                        long submissions = session.Runtime.Submissions;
                        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(frame, width, height, gi,
                            (_, _) => throw new OperationCanceledException("Deterministic recorded-frame abort before submission."),
                            surfaceStyles: styles));
                        Assert.False(session.Runtime.IsRecording);
                        Assert.Equal(submissions, session.Runtime.Submissions);
                        Assert.Equal(0, session.Runtime.Readbacks);
                        aborts++;
                    }

                    var output = session.Render(frame, width, height, gi, surfaceStyles: styles);
                    if (i < 3) leases.Add(output);
                    else output.Dispose();
                    var statistics = output.Statistics;
                    Assert.Equal(1, statistics.SubmissionCount);
                    Assert.Equal(0, statistics.ReadbackCount);
                    Assert.Equal(0, statistics.TotalReadbacks);
                    Assert.InRange(statistics.PendingFrames, 0, 3);
                    if (i < warmFrames)
                    {
                        warmCached = Math.Max(warmCached, statistics.CachedResources);
                        warmFrameOwned = Math.Max(warmFrameOwned, statistics.LiveFrameResources);
                        warmShaders = statistics.ShaderCompilations;
                        warmPipelines = statistics.PipelineCreations;
                    }
                    else
                    {
                        measuredCached = Math.Max(measuredCached, statistics.CachedResources);
                        measuredFrameOwned = Math.Max(measuredFrameOwned, statistics.LiveFrameResources);
                        Assert.Equal(warmShaders, statistics.ShaderCompilations);
                        Assert.Equal(warmPipelines, statistics.PipelineCreations);
                        // Changing dynamic stream owners and three fence slots can overlap briefly.
                        // These fixed bounds detect per-frame growth without relying on driver timing.
                        Assert.InRange(statistics.CachedResources, 0, warmCached + 4);
                        Assert.InRange(statistics.LiveFrameResources, 0, warmFrameOwned + 32);
                    }
                }

                var last = session.Statistics!;
                Assert.Equal(warmFrames + measuredFrames, last.TotalSubmissions);
                Assert.InRange(aborts, 1, (measuredFrames + 16) / 17);
                session.WaitForIdle();
                Assert.InRange(session.Runtime.LiveFrameResources, 0, 3);
                reports.Add(new
                {
                    cycle, frames = warmFrames + measuredFrames, aborts, warmCached, measuredCached,
                    warmFrameOwned, measuredFrameOwned, last, session.AdapterName,
                    session.DebugRequested, session.DebugLayerEnabled, session.DebugInfoQueueAvailable
                });

                session.Dispose();
                session.Dispose();
                // Each output owns its own COM reference, independently of renderer/fence retirement.
                // Description invokes the resource after the session has released every cache and queue.
                Assert.Equal(3, leases.Count);
                foreach (var output in leases)
                {
                    Assert.Equal((ulong)output.Width, output.Resource.Description.Width);
                    Assert.Equal(output.Height, output.Resource.Description.Height);
                    output.Dispose();
                    output.Dispose();
                }
            }
            finally
            {
                session.Dispose();
                foreach (var output in leases) output.Dispose();
            }
        }

        string? directory = Environment.GetEnvironmentVariable("MMD_GPU_RUNTIME_ARTIFACTS");
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"mixed-lifecycle-{2 * (warmFrames + measuredFrames)}.json"),
                JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static (OriginalFrameScene Frame, int Width, int Height, RayGiOptions Gi, D3D12SurfaceStyleMap Styles)
        Select(OriginalFrameScene source, int step)
    {
        var snapshot = source.Surface.Snapshot;
        var vertices = snapshot.Vertices.ToArray();
        float offset = .12f * MathF.Sin(step * .17f);
        for (int i = 4; i < vertices.Length; i++)
            vertices[i] = vertices[i] with { Position = vertices[i].Position + new Vector3(offset, 0, 0) };
        var camera = snapshot.Camera with { Eye = snapshot.Camera.Eye + new Vector3(offset * .1f, 0, 0) };
        int samples = step % 2 == 0 ? 1 : 2;
        var options = source.Options! with
        {
            SpatialSamples = samples, CameraOverride = camera,
            Antialiasing = step / 3 % 2 == 0 ? FrameAntialiasing.Off : FrameAntialiasing.Smaa1X
        };
        var frame = new OriginalFrameScene(new OriginalStyleScene(snapshot.WithDeformedVertices(vertices),
            source.Surface.Materials, source.Surface.Profile), options: options, materials: source.Materials);
        var mode = (step % 3) switch
        {
            0 => LightingMode.Legacy,
            1 => LightingMode.SoftwareRayTracing,
            _ => LightingMode.HardwareRayTracing
        };
        var gi = new RayGiOptions(mode, Strength: .5f, SamplesPerPixel: 1, Seed: 31,
            MaxRayDistance: 50, RayEpsilon: .001f, Composition: RayGiComposition.ToonPalette,
            Denoiser: RayGiDenoiser.TemporalSpatial);
        string[] familyIds = ["mmd", "genshin", "star-rail", "wuthering-waves", "endfield"];
        string familyId = familyIds[step % 5];
        var styles = new D3D12SurfaceStyleMap([familyId, "mmd"], null, TestStylePlugins.AllPresetCatalog);
        return (frame, step / 5 % 2 == 0 ? 49 : 61, step / 5 % 2 == 0 ? 37 : 43, gi, styles);
    }
}
