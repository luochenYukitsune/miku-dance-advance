using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class GenshinMetallicHistoryGpuTests
{
    private const int Width = 96, Height = 72;
    private static readonly D3D12GenshinMetallicBinding Binding = new(0, 1,
        D3D12MatcapColorEncoding.SrgbRgb, new(.2f, .25f, .3f), new(.8f, .7f, .6f), new(.7f));

    [Fact, Trait("Category", "Gpu")]
    public void SubmittedParametersResetBothHistoriesAndAbortedFutureInputsNeverBecomeTheCommittedBinding()
    {
        using var session = new D3D12FrameSession();
        var temporal = NativeAa(session); var gi = Gi();
        var frame = Frame(Lightmap(), Color());
        var original = Map(Binding);
        long readbacks = session.Runtime.Readbacks;
        Render(original, 1, "FirstFrame", "FirstFrame");
        Render(Map(Binding with { }), 2, "None", "None");
        // PrimaryResponse does not consume the authored Uniform input.
        Render(Map(Binding with { UniformLitFactor = .1f }), 3, "None", "None");
        foreach (var edited in new[]
        {
            Binding with { EnableMetallics = .5f },
            Binding with { TileScale = 2, Brightness = .4f },
            Binding with { DarkColor = new(.4f), LightColor = new(.9f), ShadowMultiplyColor = new(.5f) },
            Binding with { LitMode = D3D12GenshinMetallicLitMode.Uniform, UniformLitFactor = .8f },
            Binding with { LitMode = D3D12GenshinMetallicLitMode.Uniform, UniformLitFactor = 1.2f }
        })
        {
            Render(Map(edited), 1, "ExplicitReset", "ExplicitRequest");
            Render(Map(edited with { }), 2, "None", "None");
        }
        Render(original, 1, "ExplicitReset", "ExplicitRequest");
        Render(original, 2, "None", "None");
        long submissions = session.Runtime.Submissions;
        var future = Map(Binding with { Brightness = .15f, DarkColor = new(.05f) });
        var futureFrame = Frame(Lightmap(80), Color(green: 12));
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(futureFrame, Width, Height, gi,
            (_, _) => throw new OperationCanceledException("Abort unsubmitted metallic images and palette."),
            temporalUpscaling: temporal, surfaceStyles: future));
        Assert.Equal(submissions, session.Runtime.Submissions);
        Assert.False(session.Runtime.IsRecording);
        Render(original, 3, "None", "AbortedFrame");
        Render(original, 4, "None", "None");
        // A successful retry must still observe the edit against the last submitted binding.
        AssertHistories(session, futureFrame, future, temporal, gi, 1, "ExplicitReset", "ExplicitRequest");
        AssertHistories(session, futureFrame, future, temporal, gi, 2, "None", "None");
        var disabled = Binding with { EnableMetallics = 0 };
        AssertHistories(session, futureFrame, Map(disabled), temporal, gi, 1, "ExplicitReset", "ExplicitRequest");
        AssertHistories(session, futureFrame, Map(disabled with { Brightness = 4 }), temporal, gi, 2, "None", "None");
        AssertHistories(session, futureFrame, Map(null), temporal, gi, 3, "None", "None");
        Assert.Equal(readbacks, session.Runtime.Readbacks);

        // Compare disabled output with the ordinary branch using identical first-sample histories.
        var resetGi = gi with { ResetTemporalHistory = true };
        var resetAa = temporal with { ResetHistory = true };
        var ordinary = session.Capture(frame, Width, Height, resetGi, temporalUpscaling: resetAa, surfaceStyles: Map(null));
        var off = session.Capture(frame, Width, Height, resetGi, temporalUpscaling: resetAa, surfaceStyles: Map(disabled));
        Assert.Equal(ordinary.Rgba, off.Rgba);

        void Render(D3D12SurfaceStyleMap map, ulong frames, string giReason, string aaReason)
            => AssertHistories(session, frame, map, temporal, gi, frames, giReason, aaReason);
    }

    [Fact, Trait("Category", "Gpu")]
    public void NewImageOwnersConservativelyResetHistoriesWhileConsumedRedAndRgbChangesRequestExplicitReset()
    {
        using var session = new D3D12FrameSession();
        var temporal = NativeAa(session); var gi = Gi(); var map = Map(Binding);
        var initial = Frame(Lightmap(), Color());
        var newOwner = Frame(Lightmap(), Color());
        var alphaOnly = Frame(Lightmap(alpha: 244), Color(alpha: 231));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(map.CaptureFaceTextures(initial), map.CaptureFaceTextures(newOwner)));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(map.CaptureFaceTextures(initial), map.CaptureFaceTextures(alphaOnly)));
        Render(initial, 1, "FirstFrame", "FirstFrame");
        Render(initial, 2, "None", "None");
        // Full SceneTexture owner identity remains a conservative outer history guard;
        // consumed-key Alpha equivalence does not promise actual AA/GI history retention.
        Render(newOwner, 1, "TexturesChanged", "SceneStructureChanged");
        Render(newOwner, 2, "None", "None");
        Render(alphaOnly, 1, "TexturesChanged", "SceneStructureChanged");
        Render(alphaOnly, 2, "None", "None");
        foreach (var changed in new[]
        {
            Frame(Lightmap(red: 80), Color(alpha: 231)),
            Frame(Lightmap(red: 80), Color(red: 15)),
            Frame(Lightmap(red: 80), Color(red: 15, green: 12)),
            Frame(Lightmap(red: 80), Color(red: 15, green: 12, blue: 25))
        })
        {
            Render(changed, 1, "ExplicitReset", "ExplicitRequest");
            Render(changed, 2, "None", "None");
        }

        void Render(OriginalFrameScene frame, ulong frames, string giReason, string aaReason)
            => AssertHistories(session, frame, map, temporal, gi, frames, giReason, aaReason);
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
        var capabilities = session.QueryRayGiCapabilities();
        Assert.True(capabilities.SoftwareAvailable, capabilities.UnavailableReason);
        string loader = Environment.GetEnvironmentVariable("MMD_FSR_LOADER_PATH")
            ?? throw new InvalidOperationException("Set MMD_FSR_LOADER_PATH to the real SDK loader for metallic history acceptance.");
        var probe = D3D12FsrRuntimeProbe.Probe(session.Runtime.Device, loader, session.AdapterName, createContexts: false);
        Assert.Equal(D3D12FsrRuntimeStatus.QuerySucceeded, probe.Status);
        Assert.NotEmpty(probe.Providers);
        return new() { Mode = D3D12TemporalUpscalingMode.Fsr, QualityPreset = D3D12FsrQualityPreset.NativeAa, LoaderPath = loader };
    }

    private static D3D12SurfaceStyleMap Map(D3D12GenshinMetallicBinding? binding)
        => new([D3D12SurfaceStyle.GenshinPmx, D3D12SurfaceStyle.Mmd], [new(GenshinMetallic: binding), new()]);

    private static RayGiOptions Gi() => new(LightingMode.SoftwareRayTracing,
        SamplesPerPixel: 4, Seed: 31, MaxRayDistance: 30, RayEpsilon: .001f,
        Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.TemporalSpatial);

    private static SceneTexture Lightmap(byte red = 240, byte alpha = 0) => new("explicit R gate", 1, 1, [red, 128, 255, alpha]);
    private static SceneTexture Color(byte red = 180, byte green = 100, byte blue = 210, byte alpha = 0)
        => new("explicit ChannelPacked color", 1, 1, [red, green, blue, alpha]);

    private static OriginalFrameScene Frame(SceneTexture lightmap, SceneTexture color)
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
            new(new(-5, 5, -7), Vector3.UnitZ, Vector2.Zero)
        ];
        var snapshot = new SceneSnapshot(vertices, [0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6],
            [new("explicit metallic receiver", 0, 6, new(.25f, .18f, .12f, 1), Vector3.Zero, Vector3.Zero, 10),
                new("indirect wall", 6, 6, new(.8f, .18f, .12f, 1), Vector3.Zero, Vector3.Zero, 1)],
            [lightmap, color], camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 4, .1f, 30, VerticalFovDegrees: 45),
            lightDirection: -Vector3.UnitZ);
        return new(new(snapshot, [new(new(.22f), DoubleSided: true), new(Vector3.One, DoubleSided: true)],
            new(Vector3.One, Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: new(0, 0, 0, 1)),
            materials: [new(false, false, false), new(false, false, false)]);
    }
}
