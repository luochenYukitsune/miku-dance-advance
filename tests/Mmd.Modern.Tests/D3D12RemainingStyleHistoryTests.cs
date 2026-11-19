using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12RemainingStyleHistoryTests
{
    private const int Width = 96, Height = 72;

    [Theory, Trait("Category", "Gpu")]
    [InlineData("wuthering-waves")]
    [InlineData("endfield")]
    public void ActualFsrAndGiResetOnceForSubmittedFamilyAndFaceChangesOnly(string family)
    {
        using var session = new D3D12FrameSession();
        RequireSoftwareGi(session);
        string loader = Environment.GetEnvironmentVariable("MMD_FSR_LOADER_PATH")
            ?? throw new InvalidOperationException("Set MMD_FSR_LOADER_PATH to the actual optional SDK loader for this explicit GPU test.");
        var probe = D3D12FsrRuntimeProbe.Probe(session.Runtime.Device, loader, session.AdapterName, createContexts: false);
        Assert.Equal(D3D12FsrRuntimeStatus.QuerySucceeded, probe.Status);
        Assert.NotEmpty(probe.Providers);
        var temporal = new D3D12TemporalUpscalingOptions
        {
            Mode = D3D12TemporalUpscalingMode.Fsr,
            QualityPreset = D3D12FsrQualityPreset.NativeAa,
            LoaderPath = loader
        };
        var frame = Frame();
        var gi = Gi();
        var mmd = Map("mmd");
        var selectedFamily = Map(family);
        var genshin = Map("genshin");

        Render(mmd, 1, "FirstFrame", "FirstFrame");
        Render(selectedFamily, 1, "ExplicitReset", "ExplicitRequest");
        Render(new(selectedFamily.StyleIds, null, TestStylePlugins.AllPresetCatalog), 2, "None", "None");
        Render(genshin, 1, "ExplicitReset", "ExplicitRequest");
        Render(new(genshin.StyleIds, null, TestStylePlugins.AllPresetCatalog), 2, "None", "None");
        Render(selectedFamily, 1, "ExplicitReset", "ExplicitRequest");
        Render(selectedFamily, 2, "None", "None");

        // Consumed Face values invalidate both histories; neither parameter can cause
        // a second reset when the next submitted frame has identical effective inputs.
        var normal = Map(family, .35f, 1);
        var face = Map(family, .35f, .35f);
        Render(normal, 1, "ExplicitReset", "ExplicitRequest");
        Render(normal, 2, "None", "None");
        Render(face, 1, "ExplicitReset", "ExplicitRequest");
        Render(Map(family, .35f, .35f), 2, "None", "None");

        long submitted = session.Runtime.Submissions;
        long readbacks = session.Runtime.Readbacks;
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(frame, Width, Height, gi,
            (_, _) => throw new OperationCanceledException("Abort a pending Genshin family before submission."),
            temporalUpscaling: temporal, surfaceStyles: genshin));
        Assert.Equal(submitted, session.Runtime.Submissions);
        Assert.False(session.Runtime.IsRecording);
        // GI retains its last committed selected-family samples. FSR correctly repairs its
        // aborted native context, rather than reporting a spurious family change.
        Render(face, 3, "None", "AbortedFrame");
        Render(face, 4, "None", "None");

        // All shader families and the post-abort context have now been exercised.
        // Repeated family switches may clear histories, but must not grow the cache.
        var warm = session.Statistics!;
        foreach (var selected in new[] { mmd, selectedFamily, genshin, selectedFamily, mmd, selectedFamily })
        {
            Render(selected, 1, "ExplicitReset", "ExplicitRequest");
            Render(selected, 2, "None", "None");
            Assert.Equal(warm.ShaderCompilations, session.Statistics!.ShaderCompilations);
            Assert.Equal(warm.PipelineCreations, session.Statistics.PipelineCreations);
            Assert.InRange(session.Statistics.CachedResources, 0, warm.CachedResources + 2);
            Assert.InRange(session.Statistics.PendingFrames, 0, 3);
        }
        Assert.Equal(readbacks, session.Runtime.Readbacks);

        void Render(D3D12SurfaceStyleMap map, ulong frames, string giReason, string aaReason)
        {
            using var output = session.Render(frame, Width, Height, gi,
                temporalUpscaling: temporal, surfaceStyles: map);
            Assert.Equal(0, output.Statistics.ReadbackCount);
            Assert.Equal(1, output.Statistics.SubmissionCount);
            var lighting = Assert.IsType<D3D12RayGiStatistics>(session.RayGiStatistics);
            Assert.Equal(LightingMode.SoftwareRayTracing, lighting.Effective);
            Assert.Equal(frames, lighting.TemporalHistoryFrames);
            Assert.Equal(giReason, lighting.TemporalResetReason);
            Assert.Equal(giReason != "None", lighting.TemporalResetHistory);
            var aa = Assert.IsType<D3D12TemporalUpscalingStatistics>(output.Statistics.TemporalUpscaling);
            Assert.Equal(aaReason, aa.ResetReason);
            Assert.Equal(aaReason != "None", aa.ResetHistory);
            Assert.Equal(Width, aa.RenderWidth);
            Assert.Equal(Height, aa.RenderHeight);
            Assert.Contains(session.LastRecordings, record => record.Channel == "fsr:temporal");
        }
    }

    [Theory, Trait("Category", "Gpu")]
    [InlineData("wuthering-waves")]
    [InlineData("endfield")]
    public void AbortedSelectionDoesNotCommitAndItsSubmittedResetMatchesFreshPixels(string family)
    {
        using var session = new D3D12FrameSession();
        RequireSoftwareGi(session);
        var frame = Frame();
        var gi = Gi();
        var genshin = Map("genshin");
        var selectedFamily = Map(family, .35f, .35f);
        using (session.Render(frame, Width, Height, gi, surfaceStyles: genshin)) { }
        using (session.Render(frame, Width, Height, gi, surfaceStyles: genshin)) { }
        Assert.Equal(2UL, session.RayGiStatistics!.TemporalHistoryFrames);
        long submitted = session.Runtime.Submissions, readbacks = session.Runtime.Readbacks;
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(frame, Width, Height, gi,
            (_, _) => throw new OperationCanceledException("Abort the unsubmitted selected family and Face controls."),
            surfaceStyles: selectedFamily));
        Assert.Equal(submitted, session.Runtime.Submissions);
        Assert.False(session.Runtime.IsRecording);
        using (var retained = session.Render(frame, Width, Height, gi, surfaceStyles: genshin))
        {
            Assert.Equal(0, retained.Statistics.ReadbackCount);
            Assert.Equal(3UL, session.RayGiStatistics!.TemporalHistoryFrames);
            Assert.Equal("None", session.RayGiStatistics.TemporalResetReason);
        }
        Assert.Equal(readbacks, session.Runtime.Readbacks);

        var actual = session.Capture(frame, Width, Height, gi, surfaceStyles: selectedFamily);
        Assert.Equal("ExplicitReset", session.RayGiStatistics!.TemporalResetReason);
        Assert.Equal(1UL, session.RayGiStatistics.TemporalHistoryFrames);
        using var fresh = new D3D12FrameSession();
        var reference = fresh.Capture(frame, Width, Height, gi, surfaceStyles: selectedFamily);
        Assert.Equal(reference.Rgba, actual.Rgba);
        Assert.Contains(actual.Rgba, value => value is > 0 and < 255);
        using (session.Render(frame, Width, Height, gi, surfaceStyles: selectedFamily)) { }
        Assert.Equal("None", session.RayGiStatistics.TemporalResetReason);
        Assert.Equal(2UL, session.RayGiStatistics.TemporalHistoryFrames);
    }

    private static D3D12SurfaceStyleMap Map(string family, float normal = 1, float shadow = 1)
        => new([family, "mmd"],
            [new(D3D12CharacterMaterialRole.Face, normal, shadow), new()],
            TestStylePlugins.AllPresetCatalog);

    private static RayGiOptions Gi() => new(LightingMode.SoftwareRayTracing,
        SamplesPerPixel: 4, Seed: 17, MaxRayDistance: 50, RayEpsilon: .001f,
        Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.TemporalSpatial);

    private static void RequireSoftwareGi(D3D12FrameSession session)
    {
        var capabilities = session.QueryRayGiCapabilities();
        Assert.True(capabilities.SoftwareAvailable,
            capabilities.UnavailableReason ?? $"sm={capabilities.ShaderModel}, compiler={capabilities.CompilerAvailable}");
    }

    private static OriginalFrameScene Frame()
    {
        var source = D3D12RayGiTests.Bounce();
        var snapshot = source.Surface.Snapshot;
        var light = Vector3.Normalize(snapshot.LightDirection);
        var tangent = Vector3.Normalize(-Vector3.UnitZ - light * Vector3.Dot(-Vector3.UnitZ, light));
        const float nDotL = -.075f;
        var normal = light * nDotL + tangent * MathF.Sqrt(1 - nDotL * nDotL);
        var vertices = snapshot.Vertices.ToArray();
        for (int i = 0; i < 4; i++) vertices[i] = vertices[i] with { Normal = normal };
        var mesh = snapshot.WithDeformedVertices(vertices)
            .WithCamera(snapshot.Camera with { VerticalFovDegrees = 45 });
        var bindings = source.Surface.Materials.ToArray();
        bindings[0] = bindings[0] with { ToonShadowColor = new(.25f) };
        return new(new OriginalStyleScene(mesh, bindings, source.Surface.Profile),
            options: source.Options! with { BackgroundColor = new(0, 0, 0, 1) }, materials: source.Materials);
    }
}
