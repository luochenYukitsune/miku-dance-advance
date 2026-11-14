using System.Collections;
using System.Reflection;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
[Trait("Category", "Gpu")]
public sealed class D3D12RayGiCounterVariantTests
{
    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void OnlyExplicitDiagnosticCaptureCompilesHotCountersAndPreservesPixels(LightingMode mode)
    {
        var previous = Environment.GetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS");
        var timing = Environment.GetEnvironmentVariable("MMD_RAYGI_TIMING");
        string directory = Path.Combine(Environment.GetEnvironmentVariable("MMD_TIMING_HOOK_ARTIFACTS") ?? Path.GetTempPath(), "counter-variant-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING", null);
            Environment.SetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS", null);
            using var session = new D3D12FrameSession();
            var frame = D3D12RayGiTests.Bounce();
            var options = new RayGiOptions(mode, SamplesPerPixel: 4, Seed: 17, MaxRayDistance: 50, RayEpsilon: .001f);
            var ordinary = session.Capture(frame, 63, 47, rayGiOptions: options);
            Assert.Equal([false], Variants(session));
            Assert.Equal(1, session.Statistics!.ReadbackCount);
            Environment.SetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS", directory);
            using (var rendered = session.Render(frame, 63, 47, rayGiOptions: options))
            {
                Assert.Equal(0, rendered.Statistics.ReadbackCount);
                Assert.False(session.RayGiStatistics!.GpuCountersCaptured);
                Assert.Equal([false], Variants(session));
            }
            var counted = session.Capture(frame, 63, 47, rayGiOptions: options);
            Assert.Equal(ordinary.Rgba, counted.Rgba);
            Assert.Equal([false, true], Variants(session));
            Assert.True(session.RayGiStatistics!.GpuCountersCaptured);
            Assert.True(session.RayGiStatistics.SecondaryHits > 0);
            Assert.Equal(0u, session.RayGiStatistics.TraversalOverflows);
            Assert.Equal(5, session.Statistics!.ReadbackCount);
            using (var rendered = session.Render(frame, 63, 47, rayGiOptions: options))
            {
                Assert.Equal(0, rendered.Statistics.ReadbackCount);
                Assert.False(session.RayGiStatistics!.GpuCountersCaptured);
                Assert.Equal([false, true], Variants(session));
            }
            Environment.SetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS", null);
            var restored = session.Capture(frame, 63, 47, rayGiOptions: options);
            Assert.Equal(ordinary.Rgba, restored.Rgba);
            Assert.Equal(1, session.Statistics!.ReadbackCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MMD_RAYGI_DIAGNOSTICS", previous);
            Environment.SetEnvironmentVariable("MMD_RAYGI_TIMING", timing);
        }
    }

    private static bool[] Variants(D3D12FrameSession session)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var renderer = typeof(D3D12FrameSession).GetField("renderer", flags)!.GetValue(session)!;
        var gi = renderer.GetType().GetField("rayGiRenderer", flags)!.GetValue(renderer)!;
        var pipelines = (IDictionary)gi.GetType().GetField("computePipelines", flags)!.GetValue(gi)!;
        return pipelines.Keys.Cast<object>().Select(key =>
        {
            var field = key.GetType().GetField("Item6");
            Assert.NotNull(field);
            return (bool)field.GetValue(key)!;
        }).Order().ToArray();
    }
}
