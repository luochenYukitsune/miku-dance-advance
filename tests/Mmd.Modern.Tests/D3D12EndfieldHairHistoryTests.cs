using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;
using static Mmd.Modern.Tests.D3D12EndfieldHairBindingTests;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12EndfieldHairHistoryTests
{
    private const int Width = 96, Height = 72;

    [Fact, Trait("Category", "Gpu")]
    public void SubmittedInputsResetBothHistoriesWhileUnsubmittedScalarAndStreamChangesRetainTheKey()
    {
        using var session = new D3D12FrameSession();
        var temporal = NativeAa(session); var gi = Gi();
        var frame = Frame(); var binding = new D3D12EndfieldHairBinding(0, new(.06f, .04f, .02f));
        var map = Map(binding);
        long readbacks = session.Runtime.Readbacks;
        Render(frame, map, 1, "FirstFrame", "FirstFrame");
        Render(frame, Map(binding with { }), 2, "None", "None");
        foreach (var changed in new[] { binding with { Brightness = 1f }, binding with { PaletteColor = new(.03f) },
            binding with { Brightness = -2f } })
        {
            Render(frame, Map(changed), 1, "ExplicitReset", "ExplicitRequest");
            Render(frame, Map(changed with { }), 2, "None", "None");
        }
        Render(frame, map, 1, "ExplicitReset", "ExplicitRequest");
        Render(frame, map, 2, "None", "None");
        long submissions = session.Runtime.Submissions;
        Assert.Throws<ArgumentException>(() => session.Render(Frame(seeds: false), Width, Height, gi,
            temporalUpscaling: temporal, surfaceStyles: map));
        Assert.Equal(submissions, session.Runtime.Submissions);
        Render(frame, map, 3, "None", "None");
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(Frame(seed: Vector3.UnitY), Width, Height, gi,
            (_, _) => throw new OperationCanceledException("Abort detached Endfield radial inputs."),
            temporalUpscaling: temporal, surfaceStyles: Map(binding with { Brightness = 2f })));
        Assert.False(session.Runtime.IsRecording);
        Render(frame, map, 4, "None", "AbortedFrame");
        Render(frame, map, 5, "None", "None");
        var differentSeeds = Frame(seed: Vector3.UnitY);
        Render(differentSeeds, map, 1, "ExplicitReset", "ExplicitRequest");
        Render(differentSeeds, map, 2, "None", "None");
        Render(differentSeeds, Map(null), 1, "ExplicitReset", "ExplicitRequest");
        Render(differentSeeds, Map(null), 2, "None", "None");
        var warm = session.Statistics!;
        for (int i = 0; i < 4; i++)
        {
            Render(differentSeeds, Map(binding with { Brightness = i }), 1, "ExplicitReset", "ExplicitRequest");
            Assert.Equal(warm.ShaderCompilations, session.Statistics!.ShaderCompilations);
            Assert.Equal(warm.PipelineCreations, session.Statistics.PipelineCreations);
            Assert.InRange(session.Statistics.CachedResources, 0, warm.CachedResources + 2);
        }
        Assert.Equal(readbacks, session.Runtime.Readbacks);

        void Render(OriginalFrameScene input, D3D12SurfaceStyleMap styles, ulong count, string giReset, string aaReset)
            => AssertHistories(session, input, styles, temporal, gi, count, giReset, aaReset);
    }

    [Fact, Trait("Category", "Gpu")]
    public void GreenKeysIgnoreCoverageButActualWholeSceneOwnerPolicyIsConservativeAndAbortRetryMatchesFresh()
    {
        using var session = new D3D12FrameSession();
        var temporal = NativeAa(session); var gi = Gi();
        var binding = new D3D12EndfieldHairBinding(0, new(.06f, .04f, .02f));
        var map = Map(binding); var frame = Frame();
        var newOwner = Frame(Texture("new owner"));
        var unusedChannels = Frame(Texture(r: 0, b: 255, a: 255));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(map.CaptureFaceTextures(frame), map.CaptureFaceTextures(unusedChannels)));
        AssertHistories(session, frame, map, temporal, gi, 1, "FirstFrame", "FirstFrame");
        AssertHistories(session, frame, map, temporal, gi, 2, "None", "None");
        AssertHistories(session, newOwner, map, temporal, gi, 1, "TexturesChanged", "SceneStructureChanged");
        AssertHistories(session, unusedChannels, map, temporal, gi, 1, "TexturesChanged", "SceneStructureChanged");
        var changedFrame = Frame(Texture(g: 0), seed: Vector3.UnitY);
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(changedFrame, Width, Height, gi,
            (_, _) => throw new OperationCanceledException("Abort Green and raw stream edit before submit."),
            temporalUpscaling: temporal, surfaceStyles: map));
        AssertHistories(session, unusedChannels, map, temporal, gi, 2, "None", "AbortedFrame");
        var actual = session.Capture(changedFrame, Width, Height, gi, temporalUpscaling: temporal, surfaceStyles: map);
        Assert.Equal("ExplicitReset", session.RayGiStatistics!.TemporalResetReason);
        Assert.Equal("ExplicitRequest", session.Statistics!.TemporalUpscaling!.ResetReason);
        using var fresh = new D3D12FrameSession();
        var expected = fresh.Capture(changedFrame, Width, Height, gi, temporalUpscaling: temporal, surfaceStyles: map);
        Assert.Equal(expected.Rgba, actual.Rgba);
        var restored = session.Capture(frame, Width, Height, gi, temporalUpscaling: temporal, surfaceStyles: map);
        int different = 0;
        for (int i = 0; i < actual.Rgba.Length; i += 4)
        {
            Assert.Equal(restored.Rgba[i + 3], actual.Rgba[i + 3]);
            if (!restored.Rgba.AsSpan(i, 3).SequenceEqual(actual.Rgba.AsSpan(i, 3))) different++;
        }
        Assert.True(different > 150, "Numerical Green must affect the actual radial lobe independently of PMX specular and coverage.");
    }

    private static void AssertHistories(D3D12FrameSession session, OriginalFrameScene frame, D3D12SurfaceStyleMap map,
        D3D12TemporalUpscalingOptions temporal, RayGiOptions gi, ulong count, string giReset, string aaReset)
    {
        using var output = session.Render(frame, Width, Height, gi, temporalUpscaling: temporal, surfaceStyles: map);
        Assert.Equal(0, output.Statistics.ReadbackCount);
        Assert.Equal(count, session.RayGiStatistics!.TemporalHistoryFrames);
        Assert.Equal(giReset, session.RayGiStatistics.TemporalResetReason);
        Assert.Equal(aaReset, output.Statistics.TemporalUpscaling!.ResetReason);
    }

    // Temporal reconstruction requires perspective; the binding-only fixture remains orthographic.
    private static OriginalFrameScene Frame(SceneTexture? texture = null, bool seeds = true, Vector3? seed = null)
    {
        var original = D3D12EndfieldHairBindingTests.Frame(texture, seeds, seed);
        var snapshot = original.Surface.Snapshot;
        var perspective = snapshot.WithCamera(snapshot.Camera with { VerticalFovDegrees = 45 });
        return new(new(perspective, original.Surface.Materials, original.Surface.Profile),
            original.Outline, original.OutlineOptions, original.Options, original.Materials,
            original.Lighting, original.EditorOverlay);
    }

    private static D3D12TemporalUpscalingOptions NativeAa(D3D12FrameSession session)
    {
        Assert.True(session.QueryRayGiCapabilities().SoftwareAvailable);
        string loader = Environment.GetEnvironmentVariable("MMD_FSR_LOADER_PATH")
            ?? throw new InvalidOperationException("Set the real MMD_FSR_LOADER_PATH for this GPU test.");
        var probe = D3D12FsrRuntimeProbe.Probe(session.Runtime.Device, loader, session.AdapterName, createContexts: false);
        Assert.Equal(D3D12FsrRuntimeStatus.QuerySucceeded, probe.Status);
        return new() { Mode = D3D12TemporalUpscalingMode.Fsr, QualityPreset = D3D12FsrQualityPreset.NativeAa, LoaderPath = loader };
    }
    private static RayGiOptions Gi() => new(LightingMode.SoftwareRayTracing, SamplesPerPixel: 4, Seed: 31,
        MaxRayDistance: 30, RayEpsilon: .001f, Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.TemporalSpatial);
}
