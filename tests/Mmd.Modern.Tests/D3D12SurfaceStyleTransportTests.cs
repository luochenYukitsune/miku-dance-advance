using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12SurfaceStyleTransportTests
{
    private const int Width = 96, Height = 72;

    [Fact]
    public void MaterialMapOwnsAnImmutableSnapshotAndValidatesEveryFrameMaterial()
    {
        var values = new[] { "genshin", "mmd" };
        var map = new D3D12SurfaceStyleMap(values, null, TestStylePlugins.AllPresetCatalog);
        values[0] = "mmd";
        Assert.Equal("genshin", map.StyleIds[0]);
        Assert.Equal(2, map.Count);
        Assert.False(map.IsDefault);
        var mutableView = Assert.IsAssignableFrom<IList<string>>(map.StyleIds);
        Assert.Throws<NotSupportedException>(() => mutableView[0] = "mmd");
        map.Validate(Frame());
        Assert.Throws<ArgumentNullException>(() => map.Validate(null!));
        Assert.Throws<ArgumentNullException>(() => new D3D12SurfaceStyleMap(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void MaterialMapRejectsMissingAndExtraBindings(int count)
    {
        var map = new D3D12SurfaceStyleMap(Enumerable.Repeat("mmd", count));
        Assert.Throws<ArgumentException>(() => map.Validate(Frame()));
    }

    [Fact]
    public void UndefinedFamiliesAndNonModernGenshinRequestsAreRejected()
    {
        var frame = Frame();
        Assert.Throws<ArgumentException>(() =>
            new D3D12SurfaceStyleMap(["../bad", "mmd"]));
        Assert.Throws<NotSupportedException>(() =>
            new D3D12SurfaceStyleMap(["unregistered-style", "mmd"], null, TestStylePlugins.AllPresetCatalog).Validate(frame));
        var legacy = new OriginalFrameScene(frame.Surface, materials: frame.Materials);
        var genshin = new D3D12SurfaceStyleMap(["genshin", "mmd"], null, TestStylePlugins.AllPresetCatalog);
        Assert.Contains("modern frame options", Assert.Throws<NotSupportedException>(() => genshin.Validate(legacy)).Message);
        new D3D12SurfaceStyleMap(["mmd", "mmd"]).Validate(legacy);
        new D3D12SurfaceStyleMap([]).Validate(new(new OriginalStyleScene(SceneSnapshot.CreateEmpty(), [])));
    }

    [Fact]
    public void EffectiveFamilyEqualityTreatsNullAndAllMmdAsTheSameDefault()
    {
        var empty = new D3D12SurfaceStyleMap([]);
        var defaults = new D3D12SurfaceStyleMap(["mmd", "mmd"]);
        var mixed = new D3D12SurfaceStyleMap(["genshin", "mmd"], null, TestStylePlugins.AllPresetCatalog);
        Assert.True(empty.IsDefault);
        Assert.True(defaults.IsDefault);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(null, null));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(null, empty));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(defaults, null));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(empty, defaults));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(mixed,
            new(["genshin", "mmd"], null, TestStylePlugins.AllPresetCatalog)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(mixed, defaults));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(null, mixed));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(mixed,
            new(["mmd", "genshin"], null, TestStylePlugins.AllPresetCatalog)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(mixed, new(["genshin"], null, TestStylePlugins.AllPresetCatalog)));
    }

    [Theory, Trait("Category", "Gpu")]
    [InlineData(1)]
    [InlineData(2)]
    public void ActualSurfaceFamilyReachesEverySsaaSampleAndRestoresDefaultPixels(int samples)
    {
        using var session = new D3D12FrameSession();
        var frame = SurfaceFixture(samples);
        var defaults = new D3D12SurfaceStyleMap(["mmd"]);
        var genshin = new D3D12SurfaceStyleMap(["genshin"], null, TestStylePlugins.AllPresetCatalog);
        var baseline = session.Capture(frame, Width, Height);
        Assert.Equal(baseline.Rgba, session.Capture(frame, Width, Height, surfaceStyles: defaults).Rgba);
        var selected = session.Capture(frame, Width, Height, surfaceStyles: genshin);
        int changed = 0;
        for (int i = 0; i < baseline.Rgba.Length; i += 4)
            if (!baseline.Rgba.AsSpan(i, 3).SequenceEqual(selected.Rgba.AsSpan(i, 3))) changed++;
        Assert.True(changed > 100, $"The selected surface shader must change receiver RGB; changed {changed} pixels.");
        Assert.Equal(baseline.Rgba, session.Capture(frame, Width, Height, surfaceStyles: defaults).Rgba);
        Assert.Equal(baseline.Rgba, session.Capture(frame, Width, Height).Rgba);
        long readbacks = session.Runtime.Readbacks;
        using var rendered = session.Render(frame, Width, Height, surfaceStyles: genshin);
        Assert.Equal(0, rendered.Statistics.ReadbackCount);
        Assert.Equal(readbacks, session.Runtime.Readbacks);
        Assert.Equal(1, rendered.Statistics.SubmissionCount);
        Assert.Equal(samples, rendered.Statistics.SpatialSamples);
        var recordings = session.LastRecordings.Where(record => record.Channel == "original-frame").ToArray();
        Assert.Equal(samples, recordings.Length);
        Assert.Single(recordings.Select(record => record.DeviceIdentity).Distinct());
        Assert.Single(recordings.Select(record => record.CommandListIdentity).Distinct());
    }

    [Fact, Trait("Category", "Gpu")]
    public void InvalidMapPrecedesNativeSdkLoadingRecordingAndStatisticsChanges()
    {
        using var session = new D3D12FrameSession();
        var frame = Frame();
        using (session.Render(frame, Width, Height)) { }
        var statistics = session.Statistics;
        long frameId = session.Runtime.FrameId, submissions = session.Runtime.Submissions;
        long readbacks = session.Runtime.Readbacks, compilations = session.Runtime.ShaderCompilations;
        long pipelines = session.Runtime.PipelineCreations;
        var invalidLoader = new D3D12TemporalUpscalingOptions
        { Mode = D3D12TemporalUpscalingMode.Fsr, LoaderPath = "missing-style-map-runtime-must-not-load.dll" };
        Assert.Throws<ArgumentException>(() => session.Render(frame, Width, Height,
            temporalUpscaling: invalidLoader, surfaceStyles: new(["genshin"], null, TestStylePlugins.AllPresetCatalog)));
        var legacy = new OriginalFrameScene(frame.Surface, materials: frame.Materials);
        Assert.Throws<NotSupportedException>(() => session.Capture(legacy, Width, Height,
            temporalUpscaling: invalidLoader, surfaceStyles: new(["genshin", "mmd"], null, TestStylePlugins.AllPresetCatalog)));
        Assert.False(session.Runtime.IsRecording);
        Assert.Equal(frameId, session.Runtime.FrameId);
        Assert.Equal(submissions, session.Runtime.Submissions);
        Assert.Equal(readbacks, session.Runtime.Readbacks);
        Assert.Equal(compilations, session.Runtime.ShaderCompilations);
        Assert.Equal(pipelines, session.Runtime.PipelineCreations);
        Assert.Same(statistics, session.Statistics);
    }

    [Fact, Trait("Category", "Gpu")]
    public void ActualFsrAndGiResetOnlyOnSubmittedFamilyChangesAndRetainEqualMaps()
    {
        using var session = new D3D12FrameSession();
        var frame = Frame();
        var temporal = new D3D12TemporalUpscalingOptions
        {
            Mode = D3D12TemporalUpscalingMode.Fsr,
            LoaderPath = Environment.GetEnvironmentVariable("MMD_FSR_LOADER_PATH")
                ?? throw new InvalidOperationException("Set MMD_FSR_LOADER_PATH to the installed official SDK loader for this explicit GPU test.")
        };
        var gi = new RayGiOptions(LightingMode.SoftwareRayTracing, SamplesPerPixel: 4, Seed: 17,
            MaxRayDistance: 50, RayEpsilon: .001f, Composition: RayGiComposition.ToonPalette,
            Denoiser: RayGiDenoiser.TemporalSpatial);
        var defaults = new D3D12SurfaceStyleMap(["mmd", "mmd"]);
        var genshin = new D3D12SurfaceStyleMap(["genshin", "mmd"], null, TestStylePlugins.AllPresetCatalog);

        Render(null, 1, "FirstFrame", "FirstFrame");
        Render(defaults, 2, "None", "None");
        Render(genshin, 1, "ExplicitReset", "ExplicitRequest");
        Render(new(genshin.StyleIds, null, TestStylePlugins.AllPresetCatalog), 2, "None", "None");

        // A recorded but unsubmitted default request cannot replace the committed Genshin family.
        long submitted = session.Runtime.Submissions;
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(frame, Width, Height, gi,
            (_, _) => throw new OperationCanceledException("Cancel the pending style change before Submit."),
            temporalUpscaling: temporal, surfaceStyles: defaults));
        Assert.Equal(submitted, session.Runtime.Submissions);
        Assert.False(session.Runtime.IsRecording);
        Render(new(genshin.StyleIds, null, TestStylePlugins.AllPresetCatalog), 3, "None", "AbortedFrame");
        Render(defaults, 1, "ExplicitReset", "ExplicitRequest");
        Render(null, 2, "None", "None");
        gi = gi with { Strength = gi.Strength + 1 };
        Render(defaults, 1, "SettingsChanged", "GiConfigurationChanged");
        Render(null, 2, "None", "None");

        void Render(D3D12SurfaceStyleMap? map, ulong historyFrames, string giReason, string aaReason)
        {
            using var gpu = session.Render(frame, Width, Height, gi, temporalUpscaling: temporal, surfaceStyles: map);
            Assert.Equal(0, gpu.Statistics.ReadbackCount);
            Assert.Equal(1, gpu.Statistics.SubmissionCount);
            var giStatistics = Assert.IsType<D3D12RayGiStatistics>(session.RayGiStatistics);
            Assert.Equal(historyFrames, giStatistics.TemporalHistoryFrames);
            Assert.Equal(giReason, giStatistics.TemporalResetReason);
            Assert.Equal(giReason != "None", giStatistics.TemporalResetHistory);
            var aa = Assert.IsType<D3D12TemporalUpscalingStatistics>(gpu.Statistics.TemporalUpscaling);
            Assert.Equal(aaReason, aa.ResetReason);
            Assert.Equal(aaReason != "None", aa.ResetHistory);
        }
    }

    private static OriginalFrameScene Frame()
    {
        var source = D3D12RayGiTests.Bounce();
        var mesh = source.Surface.Snapshot.WithCamera(source.Surface.Snapshot.Camera with { VerticalFovDegrees = 45 });
        var bindings = source.Surface.Materials.ToArray();
        bindings[0] = bindings[0] with { ToonShadowColor = new(.25f) };
        return new(new OriginalStyleScene(mesh, bindings, source.Surface.Profile), options: source.Options, materials: source.Materials);
    }

    private static OriginalFrameScene SurfaceFixture(int samples)
    {
        var box = D3D12OriginalOutlineTests.Box();
        var source = box.Surface.Snapshot;
        const float nDotL = .175f;
        var normal = new Vector3(nDotL, 0, -MathF.Sqrt(1 - nDotL * nDotL));
        var vertices = source.Vertices.ToArray().Select(vertex => vertex with { Normal = normal }).ToArray();
        var material = source.Materials[0] with { Diffuse = new(.6f, .4f, .2f, 1) };
        var mesh = new SceneSnapshot(vertices, source.Indices, [material], camera: source.Camera, lightDirection: Vector3.UnitX);
        return new(new OriginalStyleScene(mesh, [new(new(.2f))], box.Surface.Profile),
            options: D3D12OriginalFrameTests.Options() with { SpatialSamples = samples });
    }
}
