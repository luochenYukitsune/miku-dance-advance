using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>Actual tiny-scene readback witnesses, not a large-project performance claim.</summary>
[Trait("Category", "Gpu")]
[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12NativeLightCapacityTests
{
    [Theory]
    [InlineData(D3D12SurfaceStyle.Mmd)]
    [InlineData(D3D12SurfaceStyle.GenshinPmx)]
    [InlineData(D3D12SurfaceStyle.StarRailPmx)]
    [InlineData(D3D12SurfaceStyle.WutheringPmx)]
    [InlineData(D3D12SurfaceStyle.EndfieldPmx)]
    public void ThirtyTwoDirectLightsRetainTheSameEnergyAsOneAcrossAllSurfaceFamilies(D3D12SurfaceStyle family)
    {
        using var session = new D3D12FrameSession();
        var frame = D3D12NativeLightTests.Fixture();
        var map = new D3D12SurfaceStyleMap([family, D3D12SurfaceStyle.Mmd]);
        var light = D3D12NativeLightTests.Light(SceneLightKind.Directional) with { CastShadows = false, Intensity = .25f };
        var single = session.Capture(D3D12NativeLightTests.WithLight(frame, [light]), 129, 97, surfaceStyles: map);
        var lights = Enumerable.Range(0, 128).Select(index => light with { Id = Guid.NewGuid(), Intensity = light.Intensity / 32, Enabled = index < 32 }).ToArray();
        var full = D3D12NativeLightTests.WithLight(frame, lights);
        var watch = Stopwatch.StartNew();
        var multiple = session.Capture(full, 129, 97, surfaceStyles: map); watch.Stop();
        Assert.Equal(32, session.NativeLightStatistics!.EnabledLights);
        Assert.Equal(0, session.NativeLightStatistics.ShadowFaces);
        Assert.Equal(single.Rgba.Length, multiple.Rgba.Length);
        for (int index = 0; index < single.Rgba.Length; index++)
            Assert.InRange(Math.Abs(single.Rgba[index] - multiple.Rgba[index]), 0, 1);
        Assert.Equal(multiple.Rgba, session.Capture(full, 129, 97, surfaceStyles: map).Rgba);
        Evidence("direct-" + family, session, watch.Elapsed.TotalMilliseconds, multiple);
    }

    [Theory]
    [InlineData(SceneLightKind.Directional, 32)]
    [InlineData(SceneLightKind.Spot, 32)]
    [InlineData(SceneLightKind.Point, 192)]
    public void ThirtySecondLightUsesItsOwnPackedShadowFacesAndTheAtlasCanShrink(SceneLightKind kind, int faces)
    {
        using var session = new D3D12FrameSession(); var frame = D3D12NativeLightTests.Fixture();
        var lights = Enumerable.Range(0, 32).Select(index => D3D12NativeLightTests.Light(kind) with
            { Intensity = index == 31 ? (kind == SceneLightKind.Directional ? 1 : 4) : .00001f }).ToArray();
        var watch = Stopwatch.StartNew();
        var shadowed = session.Capture(D3D12NativeLightTests.WithLight(frame, lights), 129, 97); watch.Stop();
        Assert.Equal(32, session.NativeLightStatistics!.EnabledLights); Assert.Equal(faces, session.NativeLightStatistics.ShadowFaces);
        Assert.Equal(faces, session.NativeLightStatistics.ShadowDrawCalls);
        Evidence("shadow-" + kind, session, watch.Elapsed.TotalMilliseconds, shadowed);
        var noLastShadow = lights.Select((light, index) => index == 31 ? light with { CastShadows = false } : light).ToArray();
        var lit = session.Capture(D3D12NativeLightTests.WithLight(frame, noLastShadow), 129, 97);
        int pixel = (48 * 129 + 64) * 4;
        Assert.True(lit.Rgba[pixel] > shadowed.Rgba[pixel] + 40, $"{kind}: last-light lit={lit.Rgba[pixel]}, shadow={shadowed.Rgba[pixel]}");
        Assert.Equal(shadowed.Rgba, session.Capture(D3D12NativeLightTests.WithLight(frame, lights), 129, 97).Rgba);
        var onlyLast = session.Capture(D3D12NativeLightTests.WithLight(frame, [lights[^1]]), 129, 97);
        Assert.Equal(kind == SceneLightKind.Point ? 6 : 1, session.NativeLightStatistics!.ShadowFaces);
        Assert.InRange(Math.Abs(shadowed.Rgba[pixel] - onlyLast.Rgba[pixel]), 0, 1);
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void SecondaryTransportConsumesThirtyTwoNativeLights(LightingMode mode)
    {
        using var session = new D3D12FrameSession();
        var capabilities = session.QueryRayGiCapabilities();
        Assert.True(mode == LightingMode.HardwareRayTracing ? capabilities.HardwareAvailable : capabilities.SoftwareAvailable,
            capabilities.UnavailableReason ?? "Required GI provider unavailable.");
        var light = NativeLightGiContractTests.Light(SceneLightKind.Point);
        var lights = Enumerable.Range(0, 32).Select(_ => light with { Id = Guid.NewGuid(), Intensity = light.Intensity / 32 }).ToArray();
        var gi = NativeLightGiContractTests.Gi(mode);
        var single = session.Capture(NativeLightGiContractTests.Fixture([light]), 79, 59, rayGiOptions: gi);
        var watch = Stopwatch.StartNew();
        var full = session.Capture(NativeLightGiContractTests.Fixture(lights), 79, 59, rayGiOptions: gi); watch.Stop();
        Assert.Equal(32, session.RayGiStatistics!.SecondaryNativeLights);
        Assert.True(session.NativeLightStatistics!.NativeLightsContributeToGi);
        for (int index = 0; index < full.Rgba.Length; index++) Assert.InRange(Math.Abs(single.Rgba[index] - full.Rgba[index]), 0, 1);
        Evidence("secondary-" + mode, session, watch.Elapsed.TotalMilliseconds, full);
    }

    [Fact]
    public void SharedLightTableDoesNotBecomeAFullTableUploadForEveryMaterial()
    {
        using var session = new D3D12FrameSession(); var template = D3D12NativeLightTests.Fixture();
        var source = template.Surface.Snapshot;
        var materials = Enumerable.Range(0, 64).Select(index => source.Materials[0] with
            { Name = "receiver " + index, StartIndex = index * 6 }).ToArray();
        var receiverIndices = source.Indices[..6].ToArray();
        var scene = new SceneSnapshot(source.Vertices.ToArray(), Enumerable.Range(0, 64).SelectMany(_ => receiverIndices).ToArray(),
            materials, source.Textures, source.World, source.Camera, source.LightDirection);
        var lights = Enumerable.Range(0, 32).Select(_ => D3D12NativeLightTests.Light(SceneLightKind.Directional) with
            { CastShadows = false, Intensity = .25f / 32 }).ToArray();
        var frame = new OriginalFrameScene(new(scene, Enumerable.Repeat(template.Surface.Materials[0], 64), template.Surface.Profile),
            options: template.Options, materials: Enumerable.Repeat(template.Materials[0], 64), lighting: new(lights));
        session.Capture(frame, 129, 97);
        Assert.Equal(64, session.Statistics!.SceneDrawCalls);
        // A 32-light table is about 14 KiB. Uploading it for all 64 materials would exceed
        // 896 KiB; this bound allows the shared table plus ordinary per-material parameters.
        Assert.InRange(session.Statistics.ConstantBytes, 1, 128 * 1024);
    }

    private static void Evidence(string name, D3D12FrameSession session, double milliseconds, RenderedFrame frame)
    {
        string? directory = Environment.GetEnvironmentVariable("MDA_LIGHTING_GPU_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, name + ".rgba"), frame.Rgba);
        File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new
        {
            session.AdapterName, session.IsWarp, frame.Width, frame.Height, ReadbackMilliseconds = milliseconds,
            session.Statistics, session.NativeLightStatistics, session.RayGiStatistics,
            Scope = "Tiny fixture readback including CPU wait; not real-project interaction latency or FPS."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
