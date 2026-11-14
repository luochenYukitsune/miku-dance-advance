using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;
using static Mmd.Modern.Tests.D3D12SubTextureTemporalCoverageTests;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12SubTextureTemporalProductTests
{
    private const int Width = 96, Height = 72;

    [Theory]
    [InlineData(D3D12TemporalUpscalingMode.Fsr)]
    [InlineData(D3D12TemporalUpscalingMode.XeSs)]
    [InlineData(D3D12TemporalUpscalingMode.Dlss)]
    public void ActualNativeAaConsumesSharedUv1CoverageAndResetsOnUvAndTechniqueAlphaEdits(D3D12TemporalUpscalingMode mode)
    {
        using var session = new D3D12FrameSession(); var options = Options(mode); var frame = Perspective(Fixture(new(.25f)));
        Render(session, frame, options, true);
        Render(session, WithUvs(frame, new(.25f)), options, false); // Fresh equal UV1 stream retains history.
        var changed = WithUvs(frame, new(.75f));
        Render(session, changed, options, true, "SceneStructureChanged");
        Render(session, WithUvs(frame, new(.75f)), options, false);
        foreach (bool additive in new[] { true, false })
        {
            var binding = changed.Surface.Materials[0] with { SubTextureAdditive = additive,
                TextureFactors = OriginalMaterialTextureFactors.Identity with { Toon = new(new(1, 1, 1, additive ? .4f : .8f), Vector4.Zero) } };
            changed = Copy(changed, new(changed.Surface.Snapshot, [binding], changed.Surface.Profile));
            Render(session, changed, options, true, "SceneStructureChanged");
            Render(session, changed, options, false);
        }
        Assert.Equal(0, session.Runtime.Readbacks);
        using var captured = session.Render(frame, Width, Height, temporalUpscaling: options);
        Assert.Equal(mode, captured.Statistics.TemporalUpscaling!.Mode);
    }

    [Theory]
    [InlineData(D3D12TemporalUpscalingMode.Fsr)]
    [InlineData(D3D12TemporalUpscalingMode.XeSs)]
    [InlineData(D3D12TemporalUpscalingMode.Dlss)]
    public void ResizeAbortOffAndVendorChangesRetireResourcesAndRestoreOrdinaryColor(D3D12TemporalUpscalingMode mode)
    {
        using var session = new D3D12FrameSession(); var options = Options(mode); var source = Perspective(Fixture(new(.25f)));
        var ordinaryBinding = source.Surface.Materials[0] with { SphereMode = OriginalSphereMode.Multiply };
        var ordinary = Copy(source, new(source.Surface.Snapshot, [ordinaryBinding], source.Surface.Profile));
        var baseline = session.Capture(ordinary, Width, Height);
        Render(session, source, options, true);
        Render(session, source, options, false);
        long submitted = session.Runtime.Submissions;
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(WithUvs(source, new(.75f)), Width, Height,
            null, (_, _) => throw new OperationCanceledException("Abort mode3 current-primary coverage."), temporalUpscaling: options));
        Assert.Equal(submitted, session.Runtime.Submissions); Assert.False(session.Runtime.IsRecording);
        Render(session, source, options, true);
        using (var resized = session.Render(source, Width + 2, Height, temporalUpscaling: options))
        { Assert.True(resized.Statistics.TemporalUpscaling!.ResetHistory); Assert.Equal(0, resized.Statistics.ReadbackCount); }
        long shaders = session.Runtime.ShaderCompilations, pipelines = session.Runtime.PipelineCreations;
        int cached = session.Statistics!.CachedResources, low = int.MaxValue, high = 0;
        for (int i = 0; i < 24; i++)
        {
            using var image = session.Render(WithUvs(source, new(.2f + i * .01f, .5f)), Width + 2, Height, temporalUpscaling: options);
            Assert.Equal(0, image.Statistics.ReadbackCount); Assert.InRange(image.Statistics.PendingFrames, 0, 3);
            Assert.InRange(image.Statistics.CachedResources, 0, cached + 2);
            Assert.Equal(shaders, image.Statistics.ShaderCompilations); Assert.Equal(pipelines, image.Statistics.PipelineCreations);
            if (i >= 10) { low = Math.Min(low, image.Statistics.LiveFrameResources); high = Math.Max(high, image.Statistics.LiveFrameResources); }
        }
        Assert.InRange(high - low, 0, 8);
        Assert.Equal(baseline.Rgba, session.Capture(ordinary, Width, Height).Rgba);
        Assert.Null(session.TemporalUpscalingStatistics);
        Render(session, source, options, true);
        var next = mode == D3D12TemporalUpscalingMode.Fsr ? D3D12TemporalUpscalingMode.XeSs : D3D12TemporalUpscalingMode.Fsr;
        Render(session, source, Options(next), true);
        Assert.Equal(baseline.Rgba, session.Capture(ordinary, Width, Height).Rgba);
        session.WaitForIdle(); Assert.InRange(session.Runtime.LiveFrameResources, 0, 3);
    }

    [Theory]
    [InlineData(D3D12TemporalUpscalingMode.Fsr)]
    [InlineData(D3D12TemporalUpscalingMode.XeSs)]
    [InlineData(D3D12TemporalUpscalingMode.Dlss)]
    public void TransparentOutputAndSsaaTemporalRemainRejectedBeforeSubmission(D3D12TemporalUpscalingMode mode)
    {
        using var session = new D3D12FrameSession(); var source = Perspective(Fixture(new(.25f)));
        var options = new D3D12TemporalUpscalingOptions { Mode = mode, LoaderPath = "missing-runtime-must-not-load.dll" };
        long submitted = session.Runtime.Submissions, frame = session.Runtime.FrameId;
        var transparent = Copy(source, options: source.Options! with { BackgroundColor = Vector4.Zero });
        var ssaa = Copy(source, options: source.Options! with { SpatialSamples = 2 });
        Assert.Throws<NotSupportedException>(() => session.Render(transparent, Width, Height, temporalUpscaling: options));
        Assert.Throws<NotSupportedException>(() => session.Render(ssaa, Width, Height, temporalUpscaling: options));
        Assert.Equal(submitted, session.Runtime.Submissions); Assert.Equal(frame, session.Runtime.FrameId);
        Assert.False(session.Runtime.IsRecording);
    }

    private static void Render(D3D12FrameSession session, OriginalFrameScene frame, D3D12TemporalUpscalingOptions options,
        bool reset, string? reason = null)
    {
        using var gpu = session.Render(frame, Width, Height, temporalUpscaling: options);
        var stats = Assert.IsType<D3D12TemporalUpscalingStatistics>(gpu.Statistics.TemporalUpscaling);
        Assert.Equal(reset, stats.ResetHistory); if (reason is not null) Assert.Equal(reason, stats.ResetReason);
        Assert.Equal(options.Mode, stats.Mode); Assert.Equal(0, gpu.Statistics.ReadbackCount);
        Assert.Equal(Width, stats.RenderWidth); Assert.Equal(Height, stats.RenderHeight);
    }

    private static OriginalFrameScene Perspective(OriginalFrameScene frame) => Copy(frame, new(
        frame.Surface.Snapshot.WithCamera(frame.Surface.Snapshot.Camera with { VerticalFovDegrees = 45 }), frame.Surface.Materials, frame.Surface.Profile));

    private static OriginalFrameScene WithUvs(OriginalFrameScene frame, Vector2 xy)
    {
        var scene = frame.Surface.Snapshot;
        var uv = new SceneAdditionalUvs(4, Enumerable.Repeat(new SceneAdditionalUvVertex(new(xy, 31, 37), new(41), new(43), new(47)), scene.Vertices.Length).ToArray());
        return Copy(frame, new(scene.WithAdditionalUvs(uv), frame.Surface.Materials, frame.Surface.Profile));
    }

    private static D3D12TemporalUpscalingOptions Options(D3D12TemporalUpscalingMode mode)
    {
        string variable = mode switch { D3D12TemporalUpscalingMode.Fsr => "MMD_FSR_LOADER_PATH",
            D3D12TemporalUpscalingMode.XeSs => "MMD_XESS_RUNTIME_PATH", _ => "MMD_DLSS_BRIDGE_PATH" };
        return new() { Mode = mode, LoaderPath = Environment.GetEnvironmentVariable(variable)
            ?? throw new InvalidOperationException($"Set {variable} for actual SDK tests."),
            DlssSdkDirectory = Environment.GetEnvironmentVariable("MMD_DLSS_SDK_DIRECTORY") };
    }
}
