using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12PmxMaterialTreatmentHistoryTests
{
    private const int Width = 96, Height = 72;

    [Theory, Trait("Category", "Gpu")]
    [InlineData(T8cTestHelpers.GenshinStyleId)]
    [InlineData(T8cTestHelpers.StarRailStyleId)]
    [InlineData(T8cTestHelpers.WutheringStyleId)]
    [InlineData(T8cTestHelpers.EndfieldStyleId)]
    public void SuccessfulScalarEditsResetBothHistoriesOnceAndValidationRetainsTheSubmittedKey(string family)
    {
        using var session = new D3D12FrameSession();
        RequireSoftwareGi(session);
        var temporal = NativeAa(session); var gi = Gi();
        var frame = Frame(Texture());
        var treatment = new D3D12PmxMaterialTreatment(1.02f, .9f);
        long readbacks = session.Runtime.Readbacks;
        Render(Map(treatment, family), 1, "FirstFrame", "FirstFrame");
        Render(Map(treatment with { }, family), 2, "None", "None");
        foreach (var changed in new[] { treatment with { Saturation = .98f }, treatment with { TransitionWidthScale = 1.15f } })
        {
            Render(Map(changed, family), 1, "ExplicitReset", "ExplicitRequest");
            Render(Map(changed with { }, family), 2, "None", "None");
        }
        Render(Map(null, family), 1, "ExplicitReset", "ExplicitRequest");
        Render(Map(new(), family), 2, "None", "None");
        Render(Map(treatment, family), 1, "ExplicitReset", "ExplicitRequest");
        Render(Map(treatment with { }, family), 2, "None", "None");

        long submissions = session.Runtime.Submissions;
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Render(frame, Width, Height, gi,
            temporalUpscaling: temporal, surfaceStyles: Map(treatment with { Saturation = float.NaN }, family)));
        var invalidWidth = Frame(frame.Surface.Snapshot.Textures[0], new(.05f, .1f));
        Assert.Throws<ArgumentException>(() => session.Render(invalidWidth, Width, Height, gi,
            temporalUpscaling: temporal, surfaceStyles: Map(treatment with { TransitionWidthScale = 1.5f }, family)));
        Assert.Equal(submissions, session.Runtime.Submissions);
        Assert.False(session.Runtime.IsRecording);
        Render(Map(treatment, family), 3, "None", "None");

        var warm = session.Statistics!;
        for (int i = 0; i < 4; i++)
        {
            var changed = new D3D12PmxMaterialTreatment(.9f + i * .03f, .85f + i * .05f);
            Render(Map(changed, family), 1, "ExplicitReset", "ExplicitRequest");
            Assert.Equal(warm.ShaderCompilations, session.Statistics!.ShaderCompilations);
            Assert.Equal(warm.PipelineCreations, session.Statistics.PipelineCreations);
            Assert.InRange(session.Statistics.CachedResources, 0, warm.CachedResources + 2);
        }
        var last = new D3D12PmxMaterialTreatment(.9f + 3 * .03f, .85f + 3 * .05f);
        var ownerReplacement = Frame(Texture("same texels with a new owner"));
        AssertHistories(session, ownerReplacement, Map(last, family), temporal, gi, 1, "TexturesChanged", "SceneStructureChanged");
        AssertHistories(session, ownerReplacement, Map(last with { }, family), temporal, gi, 2, "None", "None");
        Assert.Equal(readbacks, session.Runtime.Readbacks);

        void Render(D3D12SurfaceStyleMap map, ulong frames, string giReason, string aaReason)
            => AssertHistories(session, frame, map, temporal, gi, frames, giReason, aaReason);
    }

    [Theory, Trait("Category", "Gpu")]
    [InlineData(T8cTestHelpers.GenshinStyleId)]
    [InlineData(T8cTestHelpers.StarRailStyleId)]
    [InlineData(T8cTestHelpers.WutheringStyleId)]
    [InlineData(T8cTestHelpers.EndfieldStyleId)]
    public void AbortedEditsKeepTheSuccessfulKeyAndRetryMatchesAFreshCapture(string family)
    {
        using var session = new D3D12FrameSession();
        RequireSoftwareGi(session);
        var temporal = NativeAa(session); var gi = Gi();
        var frame = Frame(Texture());
        var treatment = new D3D12PmxMaterialTreatment(1.02f, .9f);
        var baseline = Map(treatment, family); var changed = Map(new(.7f, 1.4f), family);
        AssertHistories(session, frame, baseline, temporal, gi, 1, "FirstFrame", "FirstFrame");
        AssertHistories(session, frame, Map(treatment with { }, family), temporal, gi, 2, "None", "None");
        long submissions = session.Runtime.Submissions;
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(frame, Width, Height, gi,
            (_, _) => throw new OperationCanceledException("Abort ordinary-PMX values before Submit."),
            temporalUpscaling: temporal, surfaceStyles: changed));
        Assert.Equal(submissions, session.Runtime.Submissions);
        Assert.False(session.Runtime.IsRecording);
        AssertHistories(session, frame, baseline, temporal, gi, 3, "None", "AbortedFrame");
        AssertHistories(session, frame, baseline, temporal, gi, 4, "None", "None");
        var actual = session.Capture(frame, Width, Height, gi, temporalUpscaling: temporal, surfaceStyles: changed);
        Assert.Equal("ExplicitReset", session.RayGiStatistics!.TemporalResetReason);
        Assert.Equal(1UL, session.RayGiStatistics.TemporalHistoryFrames);
        Assert.Equal("ExplicitRequest", session.Statistics!.TemporalUpscaling!.ResetReason);
        using var fresh = new D3D12FrameSession();
        var reference = fresh.Capture(frame, Width, Height, gi, temporalUpscaling: temporal, surfaceStyles: changed);
        Assert.Equal(reference.Rgba, actual.Rgba);
        Assert.Contains(actual.Rgba, value => value is > 0 and < 255);
        AssertHistories(session, frame, Map(new(.7f, 1.4f), family), temporal, gi, 2, "None", "None");
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

    private static D3D12SurfaceStyleMap Map(D3D12PmxMaterialTreatment? treatment, string family)
        => T8cTestHelpers.CreateDirectMap([family, T8cTestHelpers.MmdStyleId], [new(PmxMaterialTreatment: treatment), new()]);

    private static RayGiOptions Gi() => new(LightingMode.SoftwareRayTracing,
        SamplesPerPixel: 4, Seed: 31, MaxRayDistance: 30, RayEpsilon: .001f,
        Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.TemporalSpatial);

    private static void RequireSoftwareGi(D3D12FrameSession session)
    {
        var capabilities = session.QueryRayGiCapabilities();
        Assert.True(capabilities.SoftwareAvailable,
            capabilities.UnavailableReason ?? $"sm={capabilities.ShaderModel}, compiler={capabilities.CompilerAvailable}");
    }

    private static SceneTexture Texture(string name = "ordinary-PMX history owner") => new(name, 1, 1, [80, 40, 120, 255]);

    private static OriginalFrameScene Frame(SceneTexture texture, CharacterToonResponse? response = null)
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
            [new("explicit ordinary receiver", 0, 6, new(.25f, .18f, .12f, 1), Vector3.Zero, Vector3.Zero, 10),
                new("indirect wall", 6, 6, new(.8f, .18f, .12f, 1), Vector3.Zero, Vector3.Zero, 1)],
            [texture], camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 4, .1f, 30, VerticalFovDegrees: 45),
            lightDirection: -Vector3.UnitZ);
        return new(new(snapshot, [new(new(.22f), DoubleSided: true), new(Vector3.One, DoubleSided: true)],
            new(Vector3.One, Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: new(0, 0, 0, 1)),
            materials: [new(false, false, false) { CharacterToonResponse = response }, new(false, false, false)]);
    }
}
