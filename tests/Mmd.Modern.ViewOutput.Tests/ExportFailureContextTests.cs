using Mmd.Modern.Application;
using System.Collections;
using System.Collections.ObjectModel;
using Xunit;

namespace Mmd.Modern.ViewOutput.Tests;

public sealed class ExportFailureContextTests
{
    [Theory]
    [InlineData("capture", "renderer.capture")]
    [InlineData("write", "sink.write")]
    public async Task OriginalFailureAndItsExactAttemptSurviveCleanup(string stage, string expectedPhase)
    {
        var fixture = new OutputFixture(); var original = new IOException("original " + stage);
        var sink = new TestSink { FailureStage = stage, Failure = original, AbortFailure = true };
        if (stage == "capture") fixture.Renderer.CaptureFailure = original;
        var actual = await Assert.ThrowsAsync<IOException>(() => fixture.Coordinator().RunAsync(
            new(new(2, 2, new(7), 10, 20, 5)), sink, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Same(original, actual);
        Assert.Equal(expectedPhase, actual.Data["MDA.ViewOutput.Phase"]);
        var context = Assert.IsType<OfflineFrameFailureContext>(actual.Data[OfflineFrameFailureContext.DataKey]);
        Assert.Equal("capture", context.Phase);
        Assert.Equal(0, context.CompletedFrames);
        Assert.Equal(0, context.Sample!.Index);
        Assert.Equal(new OfflineFrameTime(10, 1), context.Sample.SampledSourceFrame);
        Assert.Equal(20, context.SimulationTicks);
        Assert.Null(context.AttemptingSimulationTick);
        Assert.NotNull(actual.Data["ViewOutputCleanupErrors"]);
        Assert.True(sink.Aborted); Assert.True(sink.Disposed); Assert.Equal(1, fixture.Renderer.Disposals);
    }

    [Fact]
    public async Task ReadOnlyExceptionDataCannotReplaceTheOriginalFailureOrSkipCleanup()
    {
        var fixture = new OutputFixture(); var original = new ReadOnlyDataException();
        fixture.Renderer.CaptureFailure = original;
        var sink = new TestSink { AbortFailure = true };
        var actual = await Assert.ThrowsAsync<ReadOnlyDataException>(() => fixture.Coordinator().RunAsync(
            new(new(2, 2, new(30), 0, 0)), sink, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Same(original, actual);
        Assert.True(sink.Aborted); Assert.True(sink.Disposed); Assert.Equal(1, fixture.Renderer.Disposals);
    }

    private sealed class ReadOnlyDataException : IOException
    {
        private readonly IDictionary data = new ReadOnlyDictionary<string, object>(new Dictionary<string, object>());
        public override IDictionary Data => data;
    }
}
