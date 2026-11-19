using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12FaceSdfHistoryTests
{
    private const int Width = 96, Height = 72;

    [Theory, Trait("Category", "Gpu")]
    [InlineData(T8cTestHelpers.StarRailStyleId)]
    [InlineData(T8cTestHelpers.WutheringStyleId)]
    public void SubmittedFaceDataResetsBothHistoriesOnceAndUnusedBindingsRetainThem(string family)
    {
        using var session = new D3D12FrameSession();
        RequireSoftwareGi(session);
        var temporal = NativeAa(session);
        var frame = Frame(Mask());
        var editedFrame = Frame(Mask(invert: true));
        var sdf = Binding(family);
        var baseline = Map(family, sdf);
        var head = Map(family, Binding(family, right: -Vector3.UnitX));
        var transition = Map(family, Binding(family, transition: (family == T8cTestHelpers.WutheringStyleId ? .1f : 1f) * 1.5f));
        var disabled = Map(family, sdf, strength: 0);
        var ordinary = Map(family, null);
        var gi = Gi();
        long readbacks = session.Runtime.Readbacks;

        Render(frame, baseline, 1, "FirstFrame", "FirstFrame");
        Render(frame, Map(family, Binding(family, forward: -Vector3.UnitZ * 2, right: Vector3.UnitX * 3)), 2, "None", "None");
        // Reuse the same public map with a different immutable numerical texture.
        // The renderer must compare against the detached last submitted texture data.
        Render(editedFrame, baseline, 1, "ExplicitReset", "ExplicitRequest");
        Render(editedFrame, Map(family, sdf), 2, "None", "None");
        Render(editedFrame, head, 1, "ExplicitReset", "ExplicitRequest");
        Render(editedFrame, head, 2, "None", "None");
        Render(editedFrame, transition, 1, "ExplicitReset", "ExplicitRequest");
        Render(editedFrame, transition, 2, "None", "None");
        Render(editedFrame, disabled, 1, "ExplicitReset", "ExplicitRequest");
        Render(editedFrame, Map(family, Binding(family, offset: -.25f, right: -Vector3.UnitX,
            transition: (family == T8cTestHelpers.WutheringStyleId ? .1f : 1f) * 2f), strength: 0), 2, "None", "None");
        Render(editedFrame, Map(family, null, strength: 0), 3, "None", "None");
        Render(editedFrame, ordinary, 1, "ExplicitReset", "ExplicitRequest");
        Render(editedFrame, ordinary, 2, "None", "None");

        long submitted = session.Runtime.Submissions;
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(editedFrame, Width, Height, gi,
            (_, _) => throw new OperationCanceledException("Abort an unsubmitted FaceSDF binding."),
            temporalUpscaling: temporal, surfaceStyles: head));
        Assert.Equal(submitted, session.Runtime.Submissions);
        Assert.False(session.Runtime.IsRecording);
        // The GI history retains its submitted ordinary face. The SDK context needs
        // its established abort repair, rather than a committed FaceSDF selection.
        Render(editedFrame, ordinary, 3, "None", "AbortedFrame");
        Render(editedFrame, ordinary, 4, "None", "None");

        var warm = session.Statistics!;
        foreach (var selected in new[] { baseline, head, transition, disabled, ordinary, baseline })
        {
            Render(editedFrame, selected, 1, "ExplicitReset", "ExplicitRequest");
            Render(editedFrame, selected, 2, "None", "None");
            Assert.Equal(warm.ShaderCompilations, session.Statistics!.ShaderCompilations);
            Assert.Equal(warm.PipelineCreations, session.Statistics.PipelineCreations);
            Assert.InRange(session.Statistics.CachedResources, 0, warm.CachedResources + 2);
            Assert.InRange(session.Statistics.PendingFrames, 0, 3);
        }
        Assert.Equal(readbacks, session.Runtime.Readbacks);

        void Render(OriginalFrameScene input, D3D12SurfaceStyleMap map, ulong frames, string giReason, string aaReason)
        {
            using var output = session.Render(input, Width, Height, gi,
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
    [InlineData(T8cTestHelpers.StarRailStyleId)]
    [InlineData(T8cTestHelpers.WutheringStyleId)]
    public void AbortedMaskDoesNotCommitAndRetryMatchesAFreshFirstFrame(string family)
    {
        using var session = new D3D12FrameSession();
        RequireSoftwareGi(session);
        var baselineFrame = Frame(Mask());
        var changedFrame = Frame(Mask(invert: true));
        var map = Map(family, Binding(family));
        var gi = Gi();
        using (session.Render(baselineFrame, Width, Height, gi, surfaceStyles: map)) { }
        using (session.Render(baselineFrame, Width, Height, gi, surfaceStyles: map)) { }
        Assert.Equal(2UL, session.RayGiStatistics!.TemporalHistoryFrames);
        long submitted = session.Runtime.Submissions, readbacks = session.Runtime.Readbacks;
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(changedFrame, Width, Height, gi,
            (_, _) => throw new OperationCanceledException("Abort different texels while reusing the public FaceSDF map."),
            surfaceStyles: map));
        Assert.Equal(submitted, session.Runtime.Submissions);
        Assert.False(session.Runtime.IsRecording);
        using (var retained = session.Render(baselineFrame, Width, Height, gi, surfaceStyles: map))
        {
            Assert.Equal(0, retained.Statistics.ReadbackCount);
            Assert.Equal(3UL, session.RayGiStatistics!.TemporalHistoryFrames);
            Assert.Equal("None", session.RayGiStatistics.TemporalResetReason);
        }
        Assert.Equal(readbacks, session.Runtime.Readbacks);

        var actual = session.Capture(changedFrame, Width, Height, gi, surfaceStyles: map);
        Assert.Equal("ExplicitReset", session.RayGiStatistics!.TemporalResetReason);
        Assert.Equal(1UL, session.RayGiStatistics.TemporalHistoryFrames);
        using var fresh = new D3D12FrameSession();
        var reference = fresh.Capture(changedFrame, Width, Height, gi, surfaceStyles: map);
        Assert.Equal(reference.Rgba, actual.Rgba);
        Assert.Contains(actual.Rgba, value => value is > 0 and < 255);
        var original = fresh.Capture(baselineFrame, Width, Height, gi, surfaceStyles: map);
        Assert.Equal("ExplicitReset", fresh.RayGiStatistics!.TemporalResetReason);
        Assert.True(ChangedPixels(actual, original) > 150,
            "The original asymmetric mask must change visible face lighting, not only the history statistics.");
        using (session.Render(changedFrame, Width, Height, gi, surfaceStyles: map)) { }
        Assert.Equal("None", session.RayGiStatistics.TemporalResetReason);
        Assert.Equal(2UL, session.RayGiStatistics.TemporalHistoryFrames);
    }

    [Fact]
    public void CapturedFaceDataTracksBoundTexturePixelsRatherThanTextureOwnerLabel()
    {
        var sdf = Binding(T8cTestHelpers.StarRailStyleId);
        var map = Map(T8cTestHelpers.StarRailStyleId, sdf);
        var first = map.CaptureResolvedInputs(Frame(Mask()));
        var equalOwner = Map(T8cTestHelpers.StarRailStyleId, sdf).CaptureResolvedInputs(Frame(Mask(name: "different label")));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, equalOwner));
        var alternateChannels = map.CaptureResolvedInputs(Frame(Mask(otherChannels: true)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, alternateChannels));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(Mask(invert: true)))));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(first, map.CaptureResolvedInputs(Frame(Mask()))));
        // This is the surface-key contract. Actual AA/GI retains its pre-existing
        // conservative scene-texture identity reset when the texture owner changes.
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

    private static StyleMaterialBindingData Binding(
        string family,
        Vector3? forward = null,
        Vector3? right = null,
        float? transition = null,
        float offset = 1f)
        => T8cTestHelpers.FaceSdfBinding(
            family,
            0,
            forward: forward ?? -Vector3.UnitZ,
            right: right ?? Vector3.UnitX,
            transition: transition ?? (family == T8cTestHelpers.WutheringStyleId ? .1f : 1f),
            offset: offset);

    private static D3D12SurfaceStyleMap Map(string family, StyleMaterialBindingData? sdf, float strength = .75f)
        => T8cTestHelpers.CreateDirectMap(
            [family, T8cTestHelpers.MmdStyleId],
            [
                T8cTestHelpers.MaterialWithBinding(family, sdf, D3D12CharacterMaterialRole.Face, strength, .35f, buildWhenNull: true),
                new(),
            ]);

    private static RayGiOptions Gi() => new(LightingMode.SoftwareRayTracing,
        SamplesPerPixel: 4, Seed: 31, MaxRayDistance: 30, RayEpsilon: .001f,
        Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.TemporalSpatial);

    private static void RequireSoftwareGi(D3D12FrameSession session)
    {
        var capabilities = session.QueryRayGiCapabilities();
        Assert.True(capabilities.SoftwareAvailable,
            capabilities.UnavailableReason ?? $"sm={capabilities.ShaderModel}, compiler={capabilities.CompilerAvailable}");
    }

    private static SceneTexture Mask(bool invert = false, bool otherChannels = false, string name = "original face history mask")
    {
        byte[] samples = [20, 72, 160, 236, 44, 100, 196, 252];
        var pixels = new byte[samples.Length * 4];
        for (int i = 0; i < samples.Length; i++)
        {
            byte value = invert ? (byte)(255 - samples[i]) : samples[i];
            pixels[i * 4] = value;
            pixels[i * 4 + 1] = otherChannels ? (byte)(255 - value) : value;
            pixels[i * 4 + 2] = otherChannels ? (byte)17 : value;
            pixels[i * 4 + 3] = otherChannels ? (byte)255 : (byte)0;
        }
        return new(name, 4, 2, pixels);
    }

    private static OriginalFrameScene Frame(SceneTexture mask)
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
            [new("face receiver", 0, 6, new(.65f, .48f, .32f, 1), Vector3.Zero, Vector3.Zero, 1),
                new("indirect wall", 6, 6, new(.8f, .18f, .12f, 1), Vector3.Zero, Vector3.Zero, 1)],
            [mask], camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 4, .1f, 30, VerticalFovDegrees: 45),
            lightDirection: Vector3.Normalize(new(.8f, .6f, .5f)));
        return new(new(snapshot, [new(new(.22f), DoubleSided: true), new(Vector3.One, DoubleSided: true)],
            new(Vector3.One, Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: new(0, 0, 0, 1)),
            materials: [new(false, false, false), new(false, false, false)]);
    }

    private static int ChangedPixels(RenderedFrame a, RenderedFrame b)
    {
        int changed = 0;
        for (int i = 0; i < a.Rgba.Length; i += 4)
            if (!a.Rgba.AsSpan(i, 3).SequenceEqual(b.Rgba.AsSpan(i, 3))) changed++;
        return changed;
    }
}
