using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
[Trait("Category", "Gpu")]
public sealed class D3D12RayGiEmptyFrameTemporalTests
{
    private const int Width = 96, Height = 72;
    private static readonly RayGiOptions Gi = new(LightingMode.SoftwareRayTracing, Strength: .5f,
        SamplesPerPixel: 2, Seed: 31, MaxRayDistance: 50, RayEpsilon: .001f,
        Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.TemporalSpatial);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulEmptyFramesResetReturningReceiversToFreshGpuOutput(bool captureEmpty)
    {
        using var session = new D3D12FrameSession();
        using var fresh = new D3D12FrameSession();
        var frame = D3D12RayGiTests.Bounce(); var empty = Empty();
        Warm(session, frame);
        for (int i = 0; i < 2; i++)
        {
            long submissions = session.Runtime.Submissions;
            if (captureEmpty) session.Capture(empty, Width, Height, Gi);
            else
            {
                using var gpu = session.Render(empty, Width, Height, Gi);
                Assert.Equal(0, gpu.Statistics.ReadbackCount);
            }
            Assert.Equal(submissions + 1, session.Runtime.Submissions);
            Assert.Equal(1, session.Statistics!.SubmissionCount);
            Assert.Equal(captureEmpty ? 1 : 0, session.Statistics.ReadbackCount);
            Assert.Null(session.RayGiStatistics);
            Assert.Null(session.RayGiBackendIdentity);
            Assert.Empty(session.LastRecordings);
        }

        // Returning the exact old immutable scene must not resurrect its pre-empty history.
        var actual = session.Capture(frame, Width, Height, Gi);
        Assert.Equal(fresh.Capture(frame, Width, Height, Gi).Rgba, actual.Rgba);
        AssertGi(session, 1, "EmptyFrame");
        using var continued = session.Render(frame, Width, Height, Gi);
        Assert.Equal(0, continued.Statistics.ReadbackCount);
        AssertGi(session, 2, "None");
    }

    [Fact]
    public void RejectedAndAbortedEmptyFramesPreserveSubmittedHistoryAndSeed()
    {
        using var session = new D3D12FrameSession();
        using var control = new D3D12FrameSession();
        var frame = D3D12RayGiTests.Bounce(); var empty = Empty();
        Warm(session, frame); Warm(control, frame);
        long submissions = session.Runtime.Submissions, frameId = session.Runtime.FrameId;
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Render(empty, 0, Height, Gi));
        Assert.Equal(frameId, session.Runtime.FrameId);
        Assert.Equal(submissions, session.Runtime.Submissions);
        Assert.False(session.Runtime.IsRecording);

        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(empty, Width, Height, Gi,
            (_, _) =>
            {
                Assert.Null(session.RayGiStatistics);
                throw new OperationCanceledException("Abort the empty frame before Submit.");
            }));
        Assert.False(session.Runtime.IsRecording);
        Assert.Equal(submissions, session.Runtime.Submissions);
        Assert.Null(session.RayGiStatistics);

        Assert.Equal(control.Capture(frame, Width, Height, Gi).Rgba,
            session.Capture(frame, Width, Height, Gi).Rgba);
        AssertGi(session, 3, "None");
        Assert.Equal(control.RayGiStatistics!.Seed, session.RayGiStatistics!.Seed);
    }

    [Fact]
    public void SuccessfulOffEmptyFrameKeepsModeChangedResetContract()
    {
        using var session = new D3D12FrameSession();
        using var fresh = new D3D12FrameSession();
        var frame = D3D12RayGiTests.Bounce();
        Warm(session, frame);
        using (var gpu = session.Render(Empty(), Width, Height, RayGiOptions.Off))
        {
            Assert.Equal(0, gpu.Statistics.ReadbackCount);
            Assert.Equal(1, gpu.Statistics.SubmissionCount);
            Assert.Null(session.RayGiStatistics);
        }
        Assert.Equal(fresh.Capture(frame, Width, Height, Gi).Rgba,
            session.Capture(frame, Width, Height, Gi).Rgba);
        AssertGi(session, 1, "ModeChanged");
    }

    private static void Warm(D3D12FrameSession session, OriginalFrameScene frame)
    {
        for (ulong i = 1; i <= 2; i++)
        {
            using var gpu = session.Render(frame, Width, Height, Gi);
            Assert.Equal(0, gpu.Statistics.ReadbackCount);
            AssertGi(session, i, i == 1 ? "FirstFrame" : "None");
        }
    }

    private static void AssertGi(D3D12FrameSession session, ulong frames, string reason)
    {
        var stats = Assert.IsType<D3D12RayGiStatistics>(session.RayGiStatistics);
        Assert.Equal(RayGiDenoiser.TemporalSpatial, stats.Denoiser);
        Assert.Equal(1, stats.DispatchCount);
        Assert.Equal(2, stats.DenoiseDispatchCount);
        Assert.Equal(frames, stats.TemporalHistoryFrames);
        Assert.Equal(reason != "None", stats.TemporalResetHistory);
        Assert.Equal(reason, stats.TemporalResetReason);
        Assert.Equal(unchecked(Gi.Seed + (uint)(frames - 1) * 0x9e3779b9u), stats.Seed);
        Assert.Equal(Width * Height * 64L, stats.TemporalHistoryBytes);
    }

    private static OriginalFrameScene Empty()
        => new(new OriginalStyleScene(SceneSnapshot.CreateEmpty(), []),
            options: new(OriginalAlphaBlendMode.Normal, true, false));
}
