using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12StarMcHairHistoryTests
{
    private const int Width = 96, Height = 72;

    [Theory, Trait("Category", "Gpu")]
    [InlineData(T8cTestHelpers.StarRailStyleId, "Blue")]
    [InlineData(T8cTestHelpers.WutheringStyleId, "Blue")]
    [InlineData(T8cTestHelpers.WutheringStyleId, "Alpha")]
    public void SubmittedParametersAndRemovalResetBothHistoriesWhileAbortRetainsTheSubmittedKey(
        string family, string channel)
    {
        using var session = new D3D12FrameSession();
        RequireSoftwareGi(session);
        var temporal = NativeAa(session); var gi = Gi();
        var frame = Frame(Texture(), Texture("independent shadow data"));
        var binding = T8cTestHelpers.StarMcHairBinding(family, 0, 1, channel);
        var baseline = Map(binding, family);
        long readbacks = session.Runtime.Readbacks;
        Render(baseline, 1, "FirstFrame", "FirstFrame");
        Render(Map(binding with { }, family), 2, "None", "None");
        foreach (var changed in new[] {
            T8cTestHelpers.StarMcHairBinding(family, 0, 1, channel, highlightCoefficient: .3f),
            T8cTestHelpers.StarMcHairBinding(family, 0, 1, channel, highlightBrightness: -1),
            T8cTestHelpers.StarMcHairBinding(family, 0, 1, channel, shadowCoefficient: .25f),
            T8cTestHelpers.StarMcHairBinding(family, 0, 1, channel, shadowSoftness: .7f),
            T8cTestHelpers.StarMcHairBinding(family, 0, 1, channel, intensity: .2f) })
        {
            Render(Map(changed, family), 1, "ExplicitReset", "ExplicitRequest");
            Render(Map(changed with { }, family), 2, "None", "None");
        }
        if (family == T8cTestHelpers.WutheringStyleId)
        {
            var changed = T8cTestHelpers.StarMcHairBinding(family, 0, 1, channel == "Blue" ? "Alpha" : "Blue");
            Render(Map(changed, family), 1, "ExplicitReset", "ExplicitRequest");
            Render(Map(changed with { }, family), 2, "None", "None");
        }
        Render(Map(null, family), 1, "ExplicitReset", "ExplicitRequest");
        Render(T8cTestHelpers.CreateDirectMap([family, T8cTestHelpers.MmdStyleId],
            [new(D3D12CharacterMaterialRole.Hair, .99f, .01f, EyeGeometricShadowStrength: .8f), new()]), 2, "None", "None");
        Render(baseline, 1, "ExplicitReset", "ExplicitRequest");
        Render(baseline, 2, "None", "None");

        long submissions = session.Runtime.Submissions;
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Render(frame, Width, Height, gi,
            temporalUpscaling: temporal, surfaceStyles: Map(T8cTestHelpers.StarMcHairBinding(family, 2, 1, channel), family)));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Render(frame, Width, Height, gi,
            temporalUpscaling: temporal, surfaceStyles: Map(T8cTestHelpers.StarMcHairBinding(family, 0, 2, channel), family)));
        Assert.Equal(submissions, session.Runtime.Submissions);
        Assert.False(session.Runtime.IsRecording);
        Render(baseline, 3, "None", "None");
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(frame, Width, Height, gi,
            (_, _) => throw new OperationCanceledException("Abort unsubmitted Star/Wuthering hair parameters."),
            temporalUpscaling: temporal, surfaceStyles: Map(T8cTestHelpers.StarMcHairBinding(family, 0, 1, channel, highlightBrightness: 5), family)));
        Assert.Equal(submissions + 1, session.Runtime.Submissions);
        Assert.False(session.Runtime.IsRecording);
        Render(baseline, 4, "None", "AbortedFrame");
        Render(baseline, 5, "None", "None");
        var warm = session.Statistics!;
        for (int i = 0; i < 4; i++)
        {
            var map = Map(T8cTestHelpers.StarMcHairBinding(family, 0, 1, channel, intensity: .1f + i * .1f), family);
            Render(map, i == 0 ? 6UL : 1UL, i == 0 ? "None" : "ExplicitReset", i == 0 ? "None" : "ExplicitRequest");
            Assert.Equal(warm.ShaderCompilations, session.Statistics!.ShaderCompilations);
            Assert.Equal(warm.PipelineCreations, session.Statistics.PipelineCreations);
            Assert.InRange(session.Statistics.CachedResources, 0, warm.CachedResources + 2);
        }
        Assert.Equal(readbacks, session.Runtime.Readbacks);

        void Render(D3D12SurfaceStyleMap map, ulong frames, string giReason, string aaReason)
            => AssertHistories(session, frame, map, temporal, gi, frames, giReason, aaReason);
    }

    [Theory, Trait("Category", "Gpu")]
    [InlineData(T8cTestHelpers.StarRailStyleId, "Blue")]
    [InlineData(T8cTestHelpers.WutheringStyleId, "Blue")]
    [InlineData(T8cTestHelpers.WutheringStyleId, "Alpha")]
    public void EqualSelectedMasksAndUnusedChannelsStillConservativelyResetActualWholeSceneHistories(
        string family, string channel)
    {
        using var session = new D3D12FrameSession();
        RequireSoftwareGi(session);
        var temporal = NativeAa(session); var gi = Gi();
        var binding = T8cTestHelpers.StarMcHairBinding(family, 0, 1, channel);
        var map = Map(binding, family);
        var initial = Frame(Texture(), Texture("shadow owner"));
        var newOwner = Frame(Texture("new highlight owner"), Texture("new shadow owner"));
        var unusedHighlight = channel == "Blue"
            ? Texture(r: 0, g: 240, a: 0) : Texture(r: 0, g: 240, b: 0);
        var unusedChannels = Frame(unusedHighlight, Texture(g: 240, b: 0, a: 0));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(map.CaptureResolvedInputs(initial), map.CaptureResolvedInputs(newOwner)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(map.CaptureResolvedInputs(initial), map.CaptureResolvedInputs(unusedChannels)));
        AssertHistories(session, initial, map, temporal, gi, 1, "FirstFrame", "FirstFrame");
        AssertHistories(session, initial, Map(binding with { }, family), temporal, gi, 2, "None", "None");
        // Same RGBA with a new SceneTexture owner keeps plugin resolved bytes equal while resetting whole-scene AA/GI.
        AssertHistories(session, newOwner, map, temporal, gi, 1, "TexturesChanged", "SceneStructureChanged");
        AssertHistories(session, newOwner, Map(binding with { }, family), temporal, gi, 2, "None", "None");
        AssertHistories(session, unusedChannels, map, temporal, gi, 1, "ExplicitReset", "ExplicitRequest");
        AssertHistories(session, unusedChannels, map, temporal, gi, 2, "None", "None");
        var changedHighlight = Frame(channel == "Blue" ? Texture(b: 0) : Texture(a: 0), Texture());
        AssertHistories(session, changedHighlight, map, temporal, gi, 1, "ExplicitReset", "ExplicitRequest");
        AssertHistories(session, changedHighlight, map, temporal, gi, 2, "None", "None");
        var changedShadow = Frame(channel == "Blue" ? Texture(b: 0) : Texture(a: 0), Texture(r: 32));
        AssertHistories(session, changedShadow, map, temporal, gi, 1, "ExplicitReset", "ExplicitRequest");
        AssertHistories(session, changedShadow, map, temporal, gi, 2, "None", "None");
    }

    [Theory, Trait("Category", "Gpu")]
    [InlineData(T8cTestHelpers.StarRailStyleId, "Blue")]
    [InlineData(T8cTestHelpers.WutheringStyleId, "Blue")]
    [InlineData(T8cTestHelpers.WutheringStyleId, "Alpha")]
    public void AbortedMaskAndScalarEditsRetainTheSuccessfulKeyAndRetryMatchesFreshOutput(
        string family, string channel)
    {
        using var session = new D3D12FrameSession();
        RequireSoftwareGi(session);
        var temporal = NativeAa(session); var gi = Gi();
        var frame = Frame(Texture(), Texture());
        var changedFrame = Frame(channel == "Blue" ? Texture(b: 0) : Texture(a: 0), Texture(r: 32));
        var binding = T8cTestHelpers.StarMcHairBinding(family, 0, 1, channel);
        var map = Map(binding, family);
        var changed = Map(T8cTestHelpers.StarMcHairBinding(family, 0, 1, channel, highlightBrightness: -1), family);
        AssertHistories(session, frame, map, temporal, gi, 1, "FirstFrame", "FirstFrame");
        AssertHistories(session, frame, map, temporal, gi, 2, "None", "None");
        long submissions = session.Runtime.Submissions;
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(changedFrame, Width, Height, gi,
            (_, _) => throw new OperationCanceledException("Abort independent Star/Wuthering hair masks and scalar before Submit."),
            temporalUpscaling: temporal, surfaceStyles: changed));
        Assert.Equal(submissions, session.Runtime.Submissions);
        Assert.False(session.Runtime.IsRecording);
        AssertHistories(session, frame, map, temporal, gi, 3, "None", "AbortedFrame");
        AssertHistories(session, frame, map, temporal, gi, 4, "None", "None");
        var actual = session.Capture(changedFrame, Width, Height, gi, temporalUpscaling: temporal, surfaceStyles: changed);
        Assert.Equal("ExplicitReset", session.RayGiStatistics!.TemporalResetReason);
        Assert.Equal(1UL, session.RayGiStatistics.TemporalHistoryFrames);
        Assert.Equal("ExplicitRequest", session.Statistics!.TemporalUpscaling!.ResetReason);
        using var fresh = new D3D12FrameSession();
        var reference = fresh.Capture(changedFrame, Width, Height, gi, temporalUpscaling: temporal, surfaceStyles: changed);
        Assert.Equal(reference.Rgba, actual.Rgba);
        Assert.Contains(actual.Rgba, value => value is > 0 and < 255);
        AssertHistories(session, changedFrame, changed, temporal, gi, 2, "None", "None");
        var restored = session.Capture(frame, Width, Height, gi, temporalUpscaling: temporal, surfaceStyles: map);
        Assert.Equal("ExplicitReset", session.RayGiStatistics.TemporalResetReason);
        Assert.Equal(1UL, session.RayGiStatistics.TemporalHistoryFrames);
        int changedPixels = 0;
        for (int i = 0; i < actual.Rgba.Length; i += 4)
        {
            Assert.Equal(restored.Rgba[i + 3], actual.Rgba[i + 3]);
            if (!restored.Rgba.AsSpan(i, 3).SequenceEqual(actual.Rgba.AsSpan(i, 3))) changedPixels++;
        }
        Assert.True(changedPixels > 150, "Dedicated highlight mask/scalar edits must affect visible hair as well as history counters.");
    }

    private static void AssertHistories(D3D12FrameSession session, OriginalFrameScene frame, D3D12SurfaceStyleMap map,
        D3D12TemporalUpscalingOptions temporal, RayGiOptions gi, ulong frames, string giReason, string aaReason)
    {
        using var output = session.Render(frame, Width, Height, gi, temporalUpscaling: temporal, surfaceStyles: map);
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
    }

    private static D3D12TemporalUpscalingOptions NativeAa(D3D12FrameSession session)
    {
        string loader = Environment.GetEnvironmentVariable("MMD_FSR_LOADER_PATH")
            ?? throw new InvalidOperationException("Set MMD_FSR_LOADER_PATH to the real SDK loader for this explicit GPU test.");
        var probe = D3D12FsrRuntimeProbe.Probe(session.Runtime.Device, loader, session.AdapterName, createContexts: false);
        Assert.Equal(D3D12FsrRuntimeStatus.QuerySucceeded, probe.Status);
        Assert.NotEmpty(probe.Providers);
        return new() { Mode = D3D12TemporalUpscalingMode.Fsr, QualityPreset = D3D12FsrQualityPreset.NativeAa, LoaderPath = loader };
    }

    private static D3D12SurfaceStyleMap Map(StyleMaterialBindingData? binding, string family)
        => T8cTestHelpers.CreateDirectMap([family, T8cTestHelpers.MmdStyleId],
            [T8cTestHelpers.MaterialWithBinding(family, binding, D3D12CharacterMaterialRole.Hair), new()]);

    private static RayGiOptions Gi() => new(LightingMode.SoftwareRayTracing,
        SamplesPerPixel: 4, Seed: 31, MaxRayDistance: 30, RayEpsilon: .001f,
        Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.TemporalSpatial);

    private static void RequireSoftwareGi(D3D12FrameSession session)
    {
        var capabilities = session.QueryRayGiCapabilities();
        Assert.True(capabilities.SoftwareAvailable,
            capabilities.UnavailableReason ?? $"sm={capabilities.ShaderModel}, compiler={capabilities.CompilerAvailable}");
    }

    private static SceneTexture Texture(string name = "explicit numerical hair history data", byte r = 64, byte g = 17, byte b = 255, byte a = 255)
        => new(name, 1, 1, [r, g, b, a]);

    private static OriginalFrameScene Frame(SceneTexture highlight, SceneTexture shadow)
    {
        SceneVertex[] vertices =
        [
            new(new(-1.5f, -1.1f, 0), -Vector3.UnitZ, new(0, 1)),
            new(new(1.5f, -1.1f, 0), -Vector3.UnitZ, new(1, 1)),
            new(new(1.5f, 1.1f, 0), -Vector3.UnitZ, new(1, 0)),
            new(new(-1.5f, 1.1f, 0), -Vector3.UnitZ, new(0, 0)),
            new(new(-5, -5, -7), Vector3.UnitZ, Vector2.Zero),
            new(new(5, -5, -7), Vector3.UnitZ, Vector2.Zero),
            new(new(5, 5, -7), Vector3.UnitZ, Vector2.Zero),
            new(new(-5, 5, -7), Vector3.UnitZ, Vector2.Zero),
        ];
        var snapshot = new SceneSnapshot(vertices, [0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6],
            [new("explicit hair receiver", 0, 6, new(.25f, .18f, .12f, 1), new(.8f, .4f, .2f), Vector3.Zero, 10),
                new("indirect wall", 6, 6, new(.8f, .18f, .12f, 1), Vector3.Zero, Vector3.Zero, 1)],
            [highlight, shadow], camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 4, .1f, 30, VerticalFovDegrees: 45),
            lightDirection: -Vector3.UnitZ);
        return new(new(snapshot, [new(new(.22f), DoubleSided: true), new(Vector3.One, DoubleSided: true)],
            new(Vector3.One, Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: new(0, 0, 0, 1)),
            materials: [new(false, false, false), new(false, false, false)]);
    }
}
