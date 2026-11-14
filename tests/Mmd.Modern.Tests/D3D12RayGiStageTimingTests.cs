using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
[Trait("Category", "Gpu")]
public sealed class D3D12RayGiStageTimingTests
{
    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing, 4)]
    [InlineData(LightingMode.HardwareRayTracing, 5)]
    public void OptionalStagesKeepAggregateTimeAndPixelsWithoutAnotherSubmission(LightingMode mode, int expectedStages)
    {
        using var environment = new StageEnvironment();
        using var session = new D3D12FrameSession();
        var options = new RayGiOptions(mode, SamplesPerPixel: 2, Seed: 17, MaxRayDistance: 50,
            RayEpsilon: .001f, Composition: RayGiComposition.ToonPalette,
            Denoiser: RayGiDenoiser.TemporalSpatial) { ResetTemporalHistory = true };
        var frame = D3D12RayGiTests.Bounce();
        var ordinary = session.Capture(frame, 63, 47, rayGiOptions: options);
        Assert.Equal(2, session.Statistics!.ReadbackCount);
        Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING_STAGES", "1");
        var staged = session.Capture(frame, 63, 47, rayGiOptions: options);
        Assert.Equal(ordinary.Rgba, staged.Rgba);
        Assert.Equal(2 + expectedStages, session.Statistics!.ReadbackCount);
        Assert.Equal(1, session.Statistics.SubmissionCount);
        Assert.False(session.RayGiStatistics!.GpuCountersCaptured);
        string path = Path.Combine(environment.Directory, session.Statistics.DeviceIdentity, "frame-" + session.Statistics.FrameId, "timings.json");
        var timings = JsonSerializer.Deserialize<D3D12CaptureTimingResult[]>(File.ReadAllText(path))!;
        var stages = timings.Where(t => t.Label.StartsWith("GI stage/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(expectedStages, stages.Length);
        Assert.Contains(stages, t => t.Label == "GI stage/GBuffer");
        Assert.Contains(stages, t => t.Label == "GI stage/Integrate");
        Assert.Contains(stages, t => t.Label == "GI stage/Temporal");
        Assert.Contains(stages, t => t.Label == "GI stage/Spatial");
        Assert.Equal(Assert.Single(timings.Except(stages)).GpuMilliseconds, session.RayGiStatistics.GpuMilliseconds);
        Assert.True(stages.Sum(t => t.GpuMilliseconds) <= session.RayGiStatistics.GpuMilliseconds + .001);
        using var stats = JsonDocument.Parse(JsonSerializer.Serialize(session.RayGiStatistics));
        Assert.Equal(expectedStages, stats.RootElement.GetProperty("GpuStages").GetArrayLength());
        Assert.All(timings, t =>
        {
            Assert.Equal(session.Statistics.FrameId, t.FrameId);
            Assert.Equal(session.Statistics.SubmittedFence, t.SubmittedFence);
            Assert.True(t.EndTicks >= t.BeginTicks);
        });
    }

    [Fact]
    public void StageFlagAloneOrRenderCannotEnableTimestampReadbacks()
    {
        using var environment = new StageEnvironment();
        Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING_STAGES", "1");
        using var session = new D3D12FrameSession();
        var frame = D3D12RayGiTests.Bounce();
        var options = new RayGiOptions(LightingMode.HardwareRayTracing, SamplesPerPixel: 1);
        using (var output = session.Render(frame, 63, 47, rayGiOptions: options))
        {
            Assert.Equal(0, output.Statistics.ReadbackCount);
            Assert.Null(session.RayGiStatistics!.GpuMilliseconds);
        }
        Assert.False(System.IO.Directory.Exists(environment.Directory));
        Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING", null);
        session.Capture(frame, 63, 47, rayGiOptions: options);
        Assert.Equal(1, session.Statistics!.ReadbackCount);
        Assert.Null(session.RayGiStatistics!.GpuMilliseconds);
        Assert.False(System.IO.Directory.Exists(environment.Directory));
    }

    private sealed class StageEnvironment : IDisposable
    {
        private readonly string? timing = Environment.GetEnvironmentVariable("MMD_RAYGI_TIMING");
        private readonly string? stages = Environment.GetEnvironmentVariable("MMD_RAYGI_TIMING_STAGES");
        private readonly string? cpuStages = Environment.GetEnvironmentVariable("MMD_RAYGI_CPU_TIMING_STAGES");
        private readonly string? counters = Environment.GetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS");
        internal string Directory { get; } = Path.Combine(Environment.GetEnvironmentVariable("MMD_TIMING_HOOK_ARTIFACTS") ?? Path.GetTempPath(), "stages-" + Guid.NewGuid().ToString("N"));
        internal StageEnvironment()
        {
            Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING", Directory);
            Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING_STAGES", null);
            Environment.SetEnvironmentVariable("MMD_RAYGI_CPU_TIMING_STAGES", null);
            Environment.SetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS", null);
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING", timing);
            Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING_STAGES", stages);
            Environment.SetEnvironmentVariable("MMD_RAYGI_CPU_TIMING_STAGES", cpuStages);
            Environment.SetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS", counters);
        }
    }
}
