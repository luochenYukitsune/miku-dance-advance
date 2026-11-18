using System.Numerics;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class OfflineFrameTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void DurationRangeResamplesThirtyFpsSourceWithoutAddingAnExtraVideoFrame()
    {
        var plan = new OfflineFramePlan(1920, 1080, new(60), 0, 300, includeEndFrame: false);
        Assert.Equal(600, plan.FrameCount);
        Assert.Equal(new OfflineFrameTime(0, 1), plan.GetSample(0).RequestedSourceFrame);
        Assert.Equal(new OfflineFrameTime(1, 2), plan.GetSample(1).RequestedSourceFrame);
        Assert.Equal(new OfflineFrameTime(599, 2), plan.GetSample(599).RequestedSourceFrame);
        Assert.Equal(new OfflineFrameTime(599, 60), plan.GetSample(599).PresentationTime);
        Assert.Equal(plan.GetSample(1).RequestedSourceFrame, plan.GetSample(1).SampledSourceFrame);
        Assert.Equal(601, new OfflineFramePlan(1920, 1080, new(60), 0, 300).FrameCount);
    }

    [Fact]
    public void DurationRangeUsesItsOwnStartAndRoundsOnlyTheLastOutputInterval()
    {
        var offset = new OfflineFramePlan(16, 16, new(60), 90, 390, includeEndFrame: false);
        Assert.Equal(600, offset.FrameCount);
        Assert.Equal(new OfflineFrameTime(181, 2), offset.GetSample(1).RequestedSourceFrame);
        Assert.Equal(3, new OfflineFramePlan(16, 16, new(7), 10, 20, includeEndFrame: false).FrameCount);
        Assert.Equal(1, new OfflineFramePlan(16, 16, new(60), 10, 10, includeEndFrame: false).FrameCount);
        Assert.Equal(60000, new OfflineFramePlan(16, 16, new(60000, 1001), 0, 30030, includeEndFrame: false).FrameCount);
    }

    [Fact]
    public void RationalNtscClockDoesNotAccumulateErrorOrChangeMmdThirtyFps()
    {
        var plan = new OfflineFramePlan(3840, 2160, new(60000, 1001), 0, 30030);
        Assert.Equal(60001, plan.FrameCount);
        var last = plan.GetSample(60000);
        Assert.Equal(new OfflineFrameTime(1001, 1), last.PresentationTime);
        Assert.Equal(new OfflineFrameTime(30030, 1), last.RequestedSourceFrame);
        Assert.Equal(last.RequestedSourceFrame, last.SampledSourceFrame);
        Assert.Equal(new OfflineFrameRate(60), new OfflineFrameRate(120, 2));
    }

    [Fact]
    public void InclusiveRangeDoesNotInventAnEndFrameOutsideTheOutputCadence()
    {
        var plan = new OfflineFramePlan(1920, 1080, new(7), 10, 40, 5);
        Assert.Equal(8, plan.FrameCount); Assert.Equal(5u, plan.SimulationStartFrame);
        Assert.Equal(new OfflineFrameTime(100, 7), plan.GetSample(1).RequestedSourceFrame);
        Assert.Equal(new OfflineFrameTime(57, 4), plan.GetSample(1).SampledSourceFrame);
        Assert.Equal(new OfflineFrameTime(40, 1), plan.GetSample(7).RequestedSourceFrame);
        Assert.Throws<ArgumentOutOfRangeException>(() => plan.GetSample(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => plan.GetSample(8));
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(-1, 1)] [InlineData(1, 0)] [InlineData(1000001, 1)]
    public void InvalidFrameRatesAreRejected(int numerator, int denominator)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new OfflineFrameRate(numerator, denominator));

    [Fact]
    public void InvalidDimensionsRangesAndBudgetsAreRejectedBeforeRunning()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OfflineFramePlan(0, 10, new(30), 0, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OfflineFramePlan(32768, 32768, new(30), 0, 30));
        Assert.Throws<ArgumentException>(() => new OfflineFramePlan(10, 10, default, 0, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OfflineFramePlan(10, 10, new(30), 2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OfflineFramePlan(10, 10, new(30), 1, 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OfflineFramePlan(10, 10, new(1000000), 0, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OfflineFramePlan(10, 10, new(1), 0, uint.MaxValue));
        Assert.Throws<InvalidOperationException>(() => default(OfflineFrameTime).ToDouble());
    }

    [Fact]
    public void RationalConversionPreservesOrdinaryFiniteDivision()
    {
        Assert.Equal(.1, new OfflineFrameTime(1, 10).ToDouble());
        Assert.Equal(599d / 2, new OfflineFrameTime(599, 2).ToDouble());
        var time = new OfflineFrameTime((BigInteger.One << 100) + 12345, 1001);
        Assert.Equal((double)time.Numerator / (double)time.Denominator, time.ToDouble());
    }

    [Theory]
    [InlineData(1, 1074, 1L)]
    [InlineData(3, 1074, 3L)]
    [InlineData(1, 1075, 0L)]
    [InlineData(3, 1075, 2L)]
    [InlineData(5, 1075, 2L)]
    public void RationalConversionPreservesSubnormalsAndRoundsTheirMidpointsToEven(int numerator, int denominatorBits, long expectedBits)
        => Assert.Equal(expectedBits, BitConverter.DoubleToInt64Bits(
            new OfflineFrameTime(numerator, BigInteger.One << denominatorBits).ToDouble()));

    [Fact]
    public void RationalConversionAvoidsNaNForHugeTermsAndKeepsRealOverflowAndUnderflow()
    {
        var huge = BigInteger.One << 2048;
        Assert.Equal(1d, new OfflineFrameTime(huge + 1, huge - 1).ToDouble());
        Assert.Equal(3d, new OfflineFrameTime(3 * huge + 1, huge + 1).ToDouble());
        Assert.Equal(Math.ScaleB(1d, 1022), new OfflineFrameTime((BigInteger.One << 1076) + 1, BigInteger.One << 54).ToDouble());
        var maximum = ((BigInteger.One << 53) - 1) << 971;
        Assert.Equal(double.MaxValue, new OfflineFrameTime(maximum * huge + 1, huge).ToDouble());
        Assert.Equal(double.PositiveInfinity, new OfflineFrameTime(huge, 1).ToDouble());
        Assert.Equal(0d, new OfflineFrameTime(1, huge + 1).ToDouble());
    }

    [Fact]
    public async Task LowAndHighOutputFpsExecuteTheSameOneHundredTwentyPhysicalTicks()
    {
        var lowBackend = new SceneAnimationSessionTests.FallingBackend();
        var highBackend = new SceneAnimationSessionTests.FallingBackend();
        using var low = Session(lowBackend); using var high = Session(highBackend);
        var frames = new List<OfflineFrame>();
        var a = await OfflineFrameRunner.RunAsync(low, new(1920, 1080, new(1), 0, 30),
            (frame, _) => { frames.Add(frame); return ValueTask.CompletedTask; }, cancellationToken: Token);
        var b = await OfflineFrameRunner.RunAsync(high, new(1920, 1080, new(60), 0, 30),
            (_, _) => ValueTask.CompletedTask, cancellationToken: Token);
        Assert.Equal(120, a.SimulationTicks); Assert.Equal(120, b.SimulationTicks);
        Assert.Equal(120, lowBackend.Steps); Assert.Equal(120, highBackend.Steps);
        Assert.Equal(low.Frame.Surface.Snapshot.Vertices, high.Frame.Surface.Snapshot.Vertices);
        Assert.Equal(2, a.CompletedFrames); Assert.Equal(61, b.CompletedFrames);
        Assert.Equal(1920, frames[0].Width); Assert.Equal(1080, frames[0].Height);
        Assert.Single(frames[0].MorphChannels);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<Guid, CharacterMorphChannels>)frames[0].MorphChannels).Clear());
        Assert.Equal(Vector3.Zero, frames[0].Scene.Surface.Snapshot.Vertices[0].Position);
        Assert.InRange(Math.Abs(frames[1].Scene.Surface.Snapshot.Vertices[0].Position.Y + 1), 0, 2e-5f);
    }

    [Fact]
    public async Task OffGridOutputReportsHeldPoseTimeAndKeepsFixedPhysicsCadence()
    {
        var backend = new SceneAnimationSessionTests.FallingBackend(); using var session = Session(backend);
        var frames = new List<OfflineFrame>();
        await OfflineFrameRunner.RunAsync(session, new(1280, 720, new(7), 0, 30),
            (frame, _) => { frames.Add(frame); return ValueTask.CompletedTask; }, cancellationToken: Token);
        Assert.Equal(new OfflineFrameTime(1, 7), frames[1].Sample.PresentationTime);
        Assert.Equal(new OfflineFrameTime(30, 7), frames[1].Sample.RequestedSourceFrame);
        Assert.Equal(new OfflineFrameTime(17, 4), frames[1].Sample.SampledSourceFrame);
        Assert.InRange(Math.Abs(frames[1].Scene.Surface.Snapshot.Vertices[0].Position.Y + 17f / 120), 0, 2e-5f);
        Assert.Equal(120, backend.Steps);
    }

    [Fact]
    public async Task PreRollSimulatesUncapturedSourceFramesAndProgressCountsOnlyOutput()
    {
        var backend = new SceneAnimationSessionTests.FallingBackend(); using var session = Session(backend);
        var frames = new List<OfflineFrame>(); var progress = new ProgressSink();
        var result = await OfflineFrameRunner.RunAsync(session, new(1920, 1080, new(2), 15, 30, 15),
            (frame, _) => { frames.Add(frame); return ValueTask.CompletedTask; }, progress, Token);
        Assert.Equal(2, frames.Count); Assert.Equal(120, result.SimulationTicks);
        Assert.InRange(Math.Abs(frames[0].Scene.Surface.Snapshot.Vertices[0].Position.Y + .5f), 0, 2e-5f);
        Assert.Equal(new OfflineFrameTime(0, 1), frames[0].Sample.PresentationTime);
        Assert.Equal(new OfflineFrameTime(15, 1), frames[0].Sample.RequestedSourceFrame);
        Assert.Equal(new long[] { 0, 1, 2 }, progress.Items.Select(item => item.CompletedFrames));
        Assert.Equal(new long[] { 0, 60, 120 }, progress.Items.Select(item => item.SimulationTicks));
    }

    [Fact]
    public async Task PreCancelledOrOutOfDurationOutputDoesNotSeekTheSession()
    {
        var backend = new SceneAnimationSessionTests.FallingBackend(); using var session = Session(backend);
        session.Advance(.1, Token); var before = session.Frame; int steps = backend.Steps;
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OfflineFrameRunner.RunAsync(session,
            new(10, 10, new(30), 0, 30), (_, _) => ValueTask.CompletedTask, cancellationToken: cancel.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => OfflineFrameRunner.RunAsync(session,
            new(10, 10, new(30), 0, 31), (_, _) => ValueTask.CompletedTask, cancellationToken: Token));
        Assert.Same(before, session.Frame); Assert.Equal(steps, backend.Steps);
    }

    [Fact]
    public async Task CancellationAfterCaptureStopsBeforeAnotherFrameAndDoesNotOwnSession()
    {
        var backend = new SceneAnimationSessionTests.FallingBackend(); using var session = Session(backend);
        using var cancel = new CancellationTokenSource(); var progress = new ProgressSink(); int captures = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OfflineFrameRunner.RunAsync(session,
            new(10, 10, new(30), 0, 30), (_, _) => { captures++; cancel.Cancel(); return ValueTask.CompletedTask; }, progress, cancel.Token));
        Assert.Equal(1, captures); Assert.Equal(1, progress.Items[^1].CompletedFrames);
        Assert.Equal(0, backend.DisposeCount); Assert.False(session.IsFaulted);
        session.Advance(1.0 / 30, Token); Assert.Equal(4, backend.Steps);
    }

    [Fact]
    public async Task CaptureFailureCanRestartDeterministicallyWithoutDisposingTheSession()
    {
        var backend = new SceneAnimationSessionTests.FallingBackend(); using var session = Session(backend);
        await Assert.ThrowsAsync<IOException>(() => OfflineFrameRunner.RunAsync(session,
            new(10, 10, new(1), 0, 30), (frame, _) => frame.Sample.Index == 1
                ? throw new IOException("fixture sink failed") : ValueTask.CompletedTask, cancellationToken: Token));
        var failed = session.Frame;
        await OfflineFrameRunner.RunAsync(session, new(10, 10, new(1), 0, 30),
            (_, _) => ValueTask.CompletedTask, cancellationToken: Token);
        Assert.Equal(failed.Surface.Snapshot.Vertices, session.Frame.Surface.Snapshot.Vertices);
        Assert.Equal(120, backend.Steps); Assert.Equal(0, backend.DisposeCount); Assert.False(session.IsFaulted);
    }

    [Fact]
    public async Task CaptureCannotSilentlyAdvanceTheOwnedTimeline()
    {
        var backend = new SceneAnimationSessionTests.FallingBackend(); using var session = Session(backend);
        await Assert.ThrowsAsync<InvalidOperationException>(() => OfflineFrameRunner.RunAsync(session,
            new(10, 10, new(30), 0, 30), (_, _) => { session.Advance(.1, Token); return ValueTask.CompletedTask; }, cancellationToken: Token));
    }

    [Fact]
    public async Task OneFrameIntervalDoesNotAdvancePhysics()
    {
        var backend = new SceneAnimationSessionTests.FallingBackend(); using var session = Session(backend);
        var result = await OfflineFrameRunner.RunAsync(session, new(10, 10, new(60), 0, 0),
            (_, _) => ValueTask.CompletedTask, cancellationToken: Token);
        Assert.Equal(1, result.CompletedFrames); Assert.Equal(0, result.SimulationTicks); Assert.Equal(0, backend.Steps);
    }

    [Fact]
    public async Task CancellationAfterCompletedProgressDoesNotClaimAnotherOutputAttempt()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var backend = new SceneAnimationSessionTests.FallingBackend();
        using var session = Session(backend);
        var progress = new CancelAfterFrame(cancellation);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OfflineFrameRunner.RunAsync(session,
            new(10, 10, new(60), 0, 30), (_, _) => ValueTask.CompletedTask, progress, cancellation.Token));
        var context = Assert.IsType<OfflineFrameFailureContext>(error.Data[OfflineFrameFailureContext.DataKey]);
        Assert.Equal("between-frames", context.Phase); Assert.Equal(1, context.CompletedFrames);
        Assert.Null(context.Sample); Assert.Null(context.AttemptingSimulationTick);
        Assert.Equal(0, context.SimulationTicks); Assert.Equal(0, backend.Steps);
    }

    private sealed class CancelAfterFrame(CancellationTokenSource cancellation) : IProgress<OfflineFrameProgress>
    {
        public void Report(OfflineFrameProgress value) { if (value.CompletedFrames == 1) cancellation.Cancel(); }
    }

    private sealed class ProgressSink : IProgress<OfflineFrameProgress>
    {
        public List<OfflineFrameProgress> Items { get; } = [];
        public void Report(OfflineFrameProgress value) => Items.Add(value);
    }

    internal static SceneAnimationSession Session(SceneAnimationSessionTests.FallingBackend backend)
    {
        var basic = EditedMotionProjectTests.Model();
        var model = new PmxModel { Header = basic.Header, Name = basic.Name, EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = basic.Vertices, Faces = basic.Faces, Materials = basic.Materials, Bones = basic.Bones,
            Textures = [], Morphs = [], DisplayFrames = [], Joints = [], SoftBodies = [], RigidBodies = [new()
            { Name = "body", EnglishName = "", BoneIndex = 0, Group = 0, CollisionGroup = 0, Shape = PmxRigidBodyShape.Sphere,
                ShapeSize = new(.2f), Translation = Vector3.Zero, Rotation = Vector3.Zero, Mass = 1, TranslationDamping = 0,
                RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Dynamic }] };
        var layout = new SceneCompositionEditor(); var actor = layout.Add("actor", "actor.pmx", ModelRole.Character, EditedMotionProjectTests.Rest(model));
        var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 30 }] };
        return new(layout.Objects, [new(actor.Id, model, motion)], _ => backend, cancellationToken: Token);
    }
}
