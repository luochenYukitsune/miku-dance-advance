using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
[Trait("Category", "Gpu")]
public sealed class D3D12RayGiRefitMeasurementTests
{
    private const int Cells = 32, Width = 96, Height = 72, WarmupFrames = 3, MeasuredFrames = 16;
    private static readonly RayGiOptions Gi = new(LightingMode.HardwareRayTracing, Strength: .5f,
        SamplesPerPixel: 1, Seed: 17, MaxRayDistance: 50, RayEpsilon: .001f);

    [Fact]
    public void RecordComparableBuildAndUpdateGpuIntervalsWithoutAssumingASpeedup()
    {
        using var environment = new MeasurementEnvironment();
        var source = SubdividedBounce();
        using var build = new D3D12FrameSession();
        using var update = new D3D12FrameSession();
        Assert.False(build.IsWarp);
        Assert.False(update.IsWarp);
        var support = update.QueryRayGiCapabilities();
        Assert.True(support.HardwareAvailable, support.UnavailableReason);
        Assert.Equal(build.AdapterName, update.AdapterName);

        List<Sample> samples = [];
        OriginalFrameScene? finalDynamic = null;
        for (int step = 0; step < WarmupFrames + MeasuredFrames; step++)
        {
            var vertices = source.Surface.Snapshot.Vertices.ToArray();
            int wallStart = (Cells + 1) * (Cells + 1);
            for (int i = wallStart; i < vertices.Length; i++)
            {
                var vertex = vertices[i];
                float bend = .2f * MathF.Sin(vertex.Position.X * .3f + step * .17f);
                vertices[i] = vertex with { Position = vertex.Position + new Vector3(0, 0, bend) };
            }
            var mesh = source.Surface.Snapshot;
            var staticMesh = new SceneSnapshot(vertices, mesh.Indices, mesh.Materials, mesh.Textures,
                mesh.World, mesh.Camera, mesh.LightDirection);
            var dynamicMesh = mesh.WithDeformedVertices(vertices);
            Assert.False(staticMesh.HasDynamicVertices);
            Assert.True(dynamicMesh.HasDynamicVertices);
            Assert.Equal(staticMesh.Vertices.ToArray(), dynamicMesh.Vertices.ToArray());
            var fullBuild = WithSnapshot(source, staticMesh);
            finalDynamic = WithSnapshot(source, dynamicMesh);

            // Alternate order to avoid giving one path every first measurement in the pair.
            (RenderedFrame Pixels, Sample Metrics) rebuilt;
            (RenderedFrame Pixels, Sample Metrics) refitted;
            if ((step & 1) == 0)
            {
                rebuilt = Capture(build, fullBuild, step, "full-build", environment.TimingDirectory);
                refitted = Capture(update, finalDynamic, step, "incremental-update", environment.TimingDirectory);
            }
            else
            {
                refitted = Capture(update, finalDynamic, step, "incremental-update", environment.TimingDirectory);
                rebuilt = Capture(build, fullBuild, step, "full-build", environment.TimingDirectory);
            }
            Assert.Equal(rebuilt.Pixels.Rgba, refitted.Pixels.Rgba);
            Assert.Equal(1, rebuilt.Metrics.AccelerationBuilds);
            Assert.Equal(0, rebuilt.Metrics.AccelerationUpdates);
            Assert.Equal(step == 0 ? 1 : 0, refitted.Metrics.AccelerationBuilds);
            Assert.Equal(step == 0 ? 0 : 1, refitted.Metrics.AccelerationUpdates);
            samples.Add(rebuilt.Metrics);
            samples.Add(refitted.Metrics);
        }

        // Timing is an explicit Capture diagnostic. It must not add readbacks to ordinary Render.
        using (var normal = update.Render(finalDynamic!, Width, Height, rayGiOptions: Gi))
        {
            Assert.Equal(0, normal.Statistics.ReadbackCount);
            Assert.Equal(1, normal.Statistics.SubmissionCount);
            Assert.Null(update.RayGiStatistics!.GpuMilliseconds);
        }
        var measured = samples.Where(s => !s.Warmup).ToArray();
        var rebuildSummary = Summarize(measured.Where(s => s.Path == "full-build").ToArray());
        var updateSummary = Summarize(measured.Where(s => s.Path == "incremental-update").ToArray());
        environment.Save(new
        {
            schemaVersion = 1,
            status = "passed",
            scope = "Actual queue timestamps for GI GBuffer + same-frame BLAS/TLAS build or update + compute; excludes CPU BVH construction, later surface composition, presentation and complete-frame GPU time.",
            limitations = "Self-authored 4096-triangle deformation fixture; finite 16-pair sample, not a character or general performance guarantee. Static rebuild inputs use PreferFastTrace; dynamic update inputs add AllowUpdate. Static immutable vertex cache policy also differs, so CPU/resource results are diagnostic only. Out-of-place updates still allocate destination structures.",
            adapter = update.AdapterName,
            support,
            width = Width, height = Height, cells = Cells,
            triangles = source.Surface.Snapshot.Indices.Length / 3,
            warmupFramesPerPath = WarmupFrames,
            measuredFramesPerPath = MeasuredFrames,
            alternatingCaptureOrder = true,
            identicalPixelsForEveryPair = true,
            captureReadbacks = "One final image plus one explicit timestamp readback per captured frame; normal Render remains zero.",
            rebuild = rebuildSummary,
            update = updateSummary,
            measuredMedianReductionPercent = rebuildSummary.MedianMilliseconds > 0
                ? 100 * (1 - updateSummary.MedianMilliseconds / rebuildSummary.MedianMilliseconds) : (double?)null,
            samples,
        });
    }

    private static (RenderedFrame Pixels, Sample Metrics) Capture(D3D12FrameSession session,
        OriginalFrameScene scene, int step, string path, string timingDirectory)
    {
        var pixels = session.Capture(scene, Width, Height, rayGiOptions: Gi);
        var gi = session.RayGiStatistics!;
        var frame = session.Statistics!;
        Assert.Equal(LightingMode.HardwareRayTracing, gi.Effective);
        Assert.StartsWith("DXR-raw-", session.RayGiBackendIdentity);
        Assert.Equal(2, frame.ReadbackCount);
        Assert.Equal(1, frame.SubmissionCount);
        Assert.False(gi.GpuCountersCaptured);
        Assert.NotNull(gi.GpuMilliseconds);
        Assert.True(double.IsFinite(gi.GpuMilliseconds.Value) && gi.GpuMilliseconds.Value >= 0);
        string file = Path.Combine(timingDirectory, frame.DeviceIdentity, "frame-" + frame.FrameId, "timings.json");
        var timing = Assert.Single(JsonSerializer.Deserialize<D3D12CaptureTimingResult[]>(File.ReadAllText(file))!);
        Assert.True(timing.Frequency > 0 && timing.EndTicks >= timing.BeginTicks);
        Assert.Equal(frame.FrameId, timing.FrameId);
        Assert.Equal(frame.SubmittedFence, timing.SubmittedFence);
        Assert.True(frame.CompletedFence >= timing.SubmittedFence);
        Assert.Equal(timing.GpuMilliseconds, gi.GpuMilliseconds.Value);
        string expectedOperation = path == "incremental-update" && step > 0 ? "update" : "build";
        Assert.Equal("GI GBuffer + AS " + expectedOperation + " + compute", timing.Label);
        return (pixels, new(path, step, step < WarmupFrames, gi.AccelerationBuilds,
            gi.AccelerationUpdates, gi.GpuMilliseconds.Value, gi.RecordCpuMilliseconds,
            frame.FrameOwnedAllocations, frame.LiveFrameResources, frame.CachedResources,
            frame.BufferUploads, Convert.ToHexString(SHA256.HashData(pixels.Rgba)), timing));
    }

    private static Summary Summarize(Sample[] samples)
    {
        Assert.Equal(MeasuredFrames, samples.Length);
        var values = samples.Select(s => s.GpuMilliseconds).Order().ToArray();
        double median = (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
        double p95 = values[(int)Math.Ceiling(values.Length * .95) - 1];
        return new(values.Length, values[0], median, p95, values[^1], values.Average());
    }

    private static OriginalFrameScene SubdividedBounce()
    {
        var source = D3D12RayGiTests.Bounce();
        List<SceneVertex> vertices = [];
        List<uint> indices = [];
        for (int quad = 0; quad < 2; quad++)
        {
            int first = vertices.Count;
            float half = quad == 0 ? 2 : 15, z = quad == 0 ? 0 : -8;
            Vector3 normal = quad == 0 ? -Vector3.UnitZ : Vector3.UnitZ;
            for (int y = 0; y <= Cells; y++)
                for (int x = 0; x <= Cells; x++)
                    vertices.Add(new(new(-half + 2 * half * x / Cells, -half + 2 * half * y / Cells, z),
                        normal, new((float)x / Cells, 1 - (float)y / Cells)));
            for (int y = 0; y < Cells; y++)
                for (int x = 0; x < Cells; x++)
                {
                    uint a = (uint)(first + y * (Cells + 1) + x), b = a + 1;
                    uint d = a + Cells + 1, c = d + 1;
                    indices.AddRange([a, c, b, a, d, c]);
                }
        }
        int perMaterial = Cells * Cells * 6;
        var materials = source.Surface.Snapshot.Materials.Select((m, i) => m with
            { StartIndex = i * perMaterial, IndexCount = perMaterial }).ToArray();
        var mesh = source.Surface.Snapshot;
        return WithSnapshot(source, new(vertices.ToArray(), indices.ToArray(), materials, mesh.Textures,
            mesh.World, mesh.Camera, mesh.LightDirection));
    }

    private static OriginalFrameScene WithSnapshot(OriginalFrameScene frame, SceneSnapshot snapshot)
        => new(new OriginalStyleScene(snapshot, frame.Surface.Materials, frame.Surface.Profile),
            options: frame.Options, materials: frame.Materials);

    private sealed record Sample(string Path, int Step, bool Warmup, long AccelerationBuilds,
        long AccelerationUpdates, double GpuMilliseconds, double RecordCpuMilliseconds,
        int FrameOwnedAllocations, int LiveFrameResources, int CachedResources, long BufferUploads,
        string RgbaSha256, D3D12CaptureTimingResult Timing);

    private sealed record Summary(int Count, double MinMilliseconds, double MedianMilliseconds,
        double P95Milliseconds, double MaxMilliseconds, double MeanMilliseconds);

    private sealed class MeasurementEnvironment : IDisposable
    {
        private readonly string? previousTiming = Environment.GetEnvironmentVariable("MMD_RAYGI_TIMING");
        private readonly string? previousDiagnostics = Environment.GetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS");
        private readonly string directory;
        internal string TimingDirectory => Path.Combine(directory, "timestamps");

        internal MeasurementEnvironment()
        {
            string? root = Environment.GetEnvironmentVariable("MMD_REFIT_MEASUREMENT_ARTIFACTS");
            if (root is null)
            {
                var current = new DirectoryInfo(Directory.GetCurrentDirectory());
                while (current is not null && !File.Exists(Path.Combine(current.FullName, "Mmd.Modern.slnx"))) current = current.Parent;
                root = current is null ? Path.Combine(Path.GetTempPath(), "mmd-refit-measurement")
                    : Path.Combine(current.FullName, "artifacts", "stage-20261022-additional-uv", "refit-measurement");
            }
            directory = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Environment.SetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS", null);
            Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING", TimingDirectory);
        }

        internal void Save(object value) => File.WriteAllText(Path.Combine(directory, "result.json"),
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING", previousTiming);
            Environment.SetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS", previousDiagnostics);
        }
    }
}
