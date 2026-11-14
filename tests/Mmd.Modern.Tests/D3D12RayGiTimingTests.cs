using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

// These tests change process environment variables. Only this collection is nonparallel.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RayGiTimingEnvironmentCollection
{
    public const string Name = "Ray GI explicit timing environment";
}

[Collection(RayGiTimingEnvironmentCollection.Name)]
[Trait("Category", "Gpu")]
public sealed class D3D12RayGiTimingTests
{
    private static readonly RayGiOptions Gi = new(LightingMode.SoftwareRayTracing,
        SamplesPerPixel: 1, Seed: 17, MaxRayDistance: 50, RayEpsilon: .001f);

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void ExplicitCaptureAddsOneTimestampReadbackOnTheExistingSubmission(LightingMode mode)
    {
        using var environment = new TimingEnvironment(mode.ToString());
        var frame = D3D12RayGiTests.Bounce();
        using var session = new D3D12FrameSession();
        var options = Gi with { Mode = mode };
        var baseline = session.Capture(frame, 63, 47, rayGiOptions: options);
        Assert.Null(session.RayGiStatistics!.GpuMilliseconds);
        Assert.Equal(1, session.Statistics!.ReadbackCount);
        environment.EnableTiming();
        var timed = session.Capture(frame, 63, 47, rayGiOptions: options);
        Assert.Equal(baseline.Rgba, timed.Rgba);
        Assert.Equal(2, session.Statistics!.ReadbackCount);
        Assert.Equal(1, session.Statistics.SubmissionCount);
        Assert.Equal(3, session.Statistics.TotalReadbacks);
        Assert.Equal(2, session.Statistics.TotalSubmissions);
        Assert.False(session.RayGiStatistics!.GpuCountersCaptured);
        AssertResults(session, environment, 1);

        Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING", null);
        session.Capture(frame, 63, 47, rayGiOptions: options);
        Assert.Null(session.RayGiStatistics!.GpuMilliseconds);
        Assert.Equal(1, session.Statistics!.ReadbackCount);
        Assert.Single(Directory.GetFiles(environment.TimingDirectory, "timings.json", SearchOption.AllDirectories));
        Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING", " ");
        session.Capture(frame, 63, 47, rayGiOptions: options);
        Assert.Null(session.RayGiStatistics!.GpuMilliseconds);
        Assert.Equal(1, session.Statistics!.ReadbackCount);
        Assert.Single(Directory.GetFiles(environment.TimingDirectory, "timings.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void ColdHardwareCaptureTimesItsSameFrameAccelerationBuildAndWarmCaptureClearsIt()
    {
        using var environment = new TimingEnvironment("hardware-cold-warm");
        environment.EnableTiming();
        var frame = D3D12RayGiTests.Bounce();
        using var session = new D3D12FrameSession();
        var options = Gi with { Mode = LightingMode.HardwareRayTracing };
        session.Capture(frame, 63, 47, rayGiOptions: options);
        Assert.Equal(1, session.RayGiStatistics!.AccelerationBuilds);
        Assert.Equal(2, session.Statistics!.ReadbackCount);
        Assert.Equal(1, session.Statistics.SubmissionCount);
        AssertResults(session, environment, 1);
        session.Capture(frame, 63, 47, rayGiOptions: options);
        Assert.Equal(0, session.RayGiStatistics!.AccelerationBuilds);
        Assert.Equal(2, session.Statistics!.ReadbackCount);
        Assert.Equal(1, session.Statistics.SubmissionCount);
        AssertResults(session, environment, 1);
        Assert.Equal(2, Directory.GetFiles(environment.TimingDirectory, "timings.json", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void EnabledTimingDoesNotCreateQueriesOrReadbacksForRenderPresentOrOff()
    {
        using var environment = new TimingEnvironment("render-present-off");
        var frame = D3D12RayGiTests.Bounce();
        using var session = new D3D12FrameSession();
        using (var warm = session.Render(frame, 63, 47, rayGiOptions: Gi)) { }
        using (var warm = session.Render(frame, 63, 47, rayGiOptions: Gi)) { }
        int untimedAllocations = session.Statistics!.FrameOwnedAllocations;
        environment.EnableTiming();
        Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING_STAGES", "1");
        using (var output = session.Render(frame, 63, 47, rayGiOptions: Gi))
        {
            Assert.Equal(0, output.Statistics.ReadbackCount);
            Assert.Equal(untimedAllocations, output.Statistics.FrameOwnedAllocations);
            Assert.Null(session.RayGiStatistics!.GpuMilliseconds);
            Assert.Empty(session.RayGiStatistics.CpuStages);
        }
        bool appended = false;
        using (var output = session.RenderForPresent(frame, 63, 47, Gi, (_, _) => appended = true))
        {
            Assert.True(appended);
            Assert.Equal(0, output.Statistics.ReadbackCount);
            Assert.Equal(1, output.Statistics.SubmissionCount);
            Assert.Equal(untimedAllocations, output.Statistics.FrameOwnedAllocations);
            Assert.Null(session.RayGiStatistics!.GpuMilliseconds);
        }
        using (var output = session.Render(frame, 63, 47, rayGiOptions: new()))
        {
            Assert.Equal(0, output.Statistics.ReadbackCount);
            Assert.Null(session.RayGiStatistics);
        }
        session.Capture(frame, 63, 47, rayGiOptions: new());
        Assert.Equal(1, session.Statistics!.ReadbackCount); // Explicit final pixels only.
        Assert.Null(session.RayGiStatistics);
        Assert.False(Directory.Exists(environment.TimingDirectory));
        Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING", null);
        session.Capture(frame, 63, 47, rayGiOptions: Gi);
        Assert.Equal(1, session.Statistics!.ReadbackCount);
        Assert.Null(session.RayGiStatistics!.GpuMilliseconds);
        Assert.Contains(session.RayGiStatistics.CpuStages, stage => stage.Name == "FromOriginal");
        Assert.All(session.RayGiStatistics.CpuStages, stage => Assert.True(double.IsFinite(stage.CpuMilliseconds) && stage.CpuMilliseconds >= 0));
        Assert.False(Directory.Exists(environment.TimingDirectory));
        environment.Save("normal-paths.json", new { session.Statistics, appended, untimedAllocations });
    }

    [Fact]
    public void ExplicitCpuStagesAreAvailableBeforePreviewSubmissionAndResetWithTheNextFrame()
    {
        using var environment = new TimingEnvironment("preview-cpu-stages");
        using var session = new D3D12FrameSession();
        var source = D3D12RayGiTests.Bounce();
        var dynamicMesh = source.Surface.Snapshot.WithDeformedVertices(source.Surface.Snapshot.Vertices.ToArray());
        var frame = new OriginalFrameScene(new OriginalStyleScene(dynamicMesh, source.Surface.Materials, source.Surface.Profile),
            options: source.Options! with { SpatialSamples = 2 }, materials: source.Materials);
        var options = Gi with { Mode = LightingMode.HardwareRayTracing };
        Assert.True(dynamicMesh.HasDynamicVertices);
        Environment.SetEnvironmentVariable("MMD_RAYGI_CPU_TIMING_STAGES", "1");
        D3D12RayGiStatistics? recorded = null;
        using (var output = session.RenderForPresent(frame, 63, 47, options, (_, _) =>
        {
            // This callback runs before Submit, without Capture's fence wait or diagnostics completion.
            recorded = Assert.IsType<D3D12RayGiStatistics>(session.RayGiStatistics);
            Assert.Equal(2, recorded.DispatchCount);
            Assert.Contains(recorded.CpuStages, stage => stage.Name == "TriangleAssembly");
            Assert.Contains(recorded.CpuStages, stage => stage.Name == "TriangleUpload");
            Assert.Equal(recorded.CpuStages.Count, recorded.CpuStages.Select(stage => stage.Name).Distinct().Count());
            Assert.All(recorded.CpuStages, stage => Assert.True(double.IsFinite(stage.CpuMilliseconds) && stage.CpuMilliseconds >= 0));
            Assert.InRange(recorded.CpuStages.Sum(stage => stage.CpuMilliseconds), 0, recorded.RecordCpuMilliseconds + .001);
            Assert.Null(recorded.GpuMilliseconds);
            Assert.Empty(recorded.GpuStages);
            Assert.False(recorded.GpuCountersCaptured);
        }))
        {
            Assert.Equal(0, output.Statistics.ReadbackCount);
            Assert.Equal(1, output.Statistics.SubmissionCount);
        }
        Assert.NotNull(recorded);
        Assert.False(Directory.Exists(environment.TimingDirectory));
        Assert.False(Directory.Exists(environment.DiagnosticsDirectory));

        Environment.SetEnvironmentVariable("MMD_RAYGI_CPU_TIMING_STAGES", "0");
        using (var output = session.Render(frame, 63, 47, rayGiOptions: options))
        {
            Assert.Equal(0, output.Statistics.ReadbackCount);
            Assert.Empty(session.RayGiStatistics!.CpuStages);
        }
        Assert.Contains(recorded.CpuStages, stage => stage.Name == "TriangleAssembly");

        // The existing Capture-only switch still works independently of the preview CPU switch.
        Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING_STAGES", "1");
        session.Capture(frame, 63, 47, rayGiOptions: options);
        Assert.Equal(1, session.Statistics!.ReadbackCount);
        Assert.Contains(session.RayGiStatistics!.CpuStages, stage => stage.Name == "FromOriginal");
        Assert.DoesNotContain(session.RayGiStatistics.CpuStages, stage => stage.Name == "TriangleAssembly");
        Assert.Null(session.RayGiStatistics.GpuMilliseconds);
        Assert.Empty(session.RayGiStatistics.GpuStages);
    }


    [Fact]
    public void TimingAndExistingDebugReadbacksKeepTheirIndependentStatistics()
    {
        using var environment = new TimingEnvironment("timing-and-counters");
        environment.EnableTiming();
        Environment.SetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS", environment.DiagnosticsDirectory);
        using var session = new D3D12FrameSession();
        session.Capture(D3D12RayGiTests.Bounce(), 63, 47, rayGiOptions: Gi);
        Assert.Equal(6, session.Statistics!.ReadbackCount); // Final pixels + timing + four GI diagnostics.
        Assert.Equal(1, session.Statistics.SubmissionCount);
        Assert.True(session.RayGiStatistics!.GpuCountersCaptured);
        AssertResults(session, environment, 1);
        string statistics = Assert.Single(Directory.GetFiles(environment.DiagnosticsDirectory, "statistics.json", SearchOption.AllDirectories));
        Assert.Equal(session.RayGiStatistics.GpuMilliseconds,
            JsonSerializer.Deserialize<D3D12RayGiStatistics>(File.ReadAllText(statistics))!.GpuMilliseconds);
    }

    private static void AssertResults(D3D12FrameSession session, TimingEnvironment environment, int count)
    {
        Assert.NotNull(session.RayGiStatistics!.GpuMilliseconds);
        Assert.True(double.IsFinite(session.RayGiStatistics.GpuMilliseconds.Value));
        Assert.True(session.RayGiStatistics.GpuMilliseconds >= 0);
        string file = Path.Combine(environment.TimingDirectory, session.Statistics!.DeviceIdentity,
            "frame-" + session.Statistics.FrameId, "timings.json");
        Assert.True(File.Exists(file));
        var results = JsonSerializer.Deserialize<D3D12CaptureTimingResult[]>(File.ReadAllText(file))!;
        Assert.Equal(count, results.Length);
        Assert.All(results, r =>
        {
            Assert.True(r.Frequency > 0);
            Assert.True(r.EndTicks >= r.BeginTicks);
            Assert.True(double.IsFinite(r.GpuMilliseconds) && r.GpuMilliseconds >= 0);
            Assert.Equal(session.Statistics!.FrameId, r.FrameId);
            Assert.Equal(session.Statistics.SubmittedFence, r.SubmittedFence);
            Assert.True(session.Statistics.CompletedFence >= r.SubmittedFence);
            Assert.Equal("GI GBuffer + AS build + compute", r.Label);
        });
        Assert.Equal(results.Sum(r => r.GpuMilliseconds), session.RayGiStatistics.GpuMilliseconds);
        environment.Save("frame-" + session.Statistics.FrameId + "-result.json", new { session.AdapterName, session.IsWarp, session.Statistics, session.RayGiStatistics, results });
    }

    private sealed class TimingEnvironment : IDisposable
    {
        private readonly string? previousTiming = Environment.GetEnvironmentVariable("MMD_RAYGI_TIMING");
        private readonly string? previousStages = Environment.GetEnvironmentVariable("MMD_RAYGI_TIMING_STAGES");
        private readonly string? previousCpuStages = Environment.GetEnvironmentVariable("MMD_RAYGI_CPU_TIMING_STAGES");
        private readonly string? previousDiagnostics = Environment.GetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS");
        private readonly string root;
        internal string TimingDirectory => Path.Combine(root, "timings");
        internal string DiagnosticsDirectory => Path.Combine(root, "diagnostics");
        internal TimingEnvironment(string name)
        {
            string artifacts = Environment.GetEnvironmentVariable("MMD_TIMING_HOOK_ARTIFACTS") ?? Path.GetTempPath();
            root = Path.Combine(artifacts, name + "-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING", null);
            Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING_STAGES", null);
            Environment.SetEnvironmentVariable("MMD_RAYGI_CPU_TIMING_STAGES", null);
            Environment.SetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS", null);
        }
        internal void EnableTiming() => Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING", TimingDirectory);
        internal void Save(string name, object value)
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING", previousTiming);
            Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING_STAGES", previousStages);
            Environment.SetEnvironmentVariable("MMD_RAYGI_CPU_TIMING_STAGES", previousCpuStages);
            Environment.SetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS", previousDiagnostics);
        }
    }
}
