using Mmd.Modern.Application;
using Xunit;

namespace Mmd.Modern.ViewOutput.Tests;

public sealed class SceneAudioPlaybackOwnerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SourceSamplesDrivePauseResumeSmallSeekDriftAndEndWithoutAnotherClock()
    {
        var device = new FakeOutput(); var factory = new FakeFactory(device);
        await using var owner = new SceneAudioPlaybackOwner(factory);
        SceneMediaTrack track = Track();
        owner.Publish(track, Sample(track, 0, true)); await owner.FlushAsync(Token);
        Assert.Equal(0, device.PositionFrames); Assert.True(device.Playing);

        device.PositionFrames = 800;
        owner.Publish(track, Sample(track, TimeSpan.TicksPerSecond / 10, false)); await owner.FlushAsync(Token);
        Assert.False(device.Playing); Assert.Equal(800, device.PositionFrames);
        owner.Publish(track, Sample(track, TimeSpan.TicksPerSecond / 10, true)); await owner.FlushAsync(Token);
        Assert.True(device.Playing); Assert.Equal(800, device.PositionFrames);

        int beforeSeek = device.Seeks;
        owner.Publish(track, Sample(track, TimeSpan.TicksPerSecond / 10 + 10000, true), seekRevision: 1);
        await owner.FlushAsync(Token);
        Assert.Equal(808, device.PositionFrames); Assert.True(device.Seeks > beforeSeek);

        device.PositionFrames = 4000;
        owner.Publish(track, Sample(track, TimeSpan.TicksPerSecond / 5, true), seekRevision: 1);
        await owner.FlushAsync(Token); Assert.Equal(1600, device.PositionFrames);
        owner.Publish(track, Sample(track, 10 * TimeSpan.TicksPerSecond, true), seekRevision: 1);
        await owner.FlushAsync(Token); Assert.False(device.Playing); Assert.Equal(16000, device.PositionFrames);
        Assert.Null(owner.LastError); Assert.Equal(1, factory.Opens);
    }

    [Fact]
    public async Task PublishIsNonblockingAndWorkerConsumesLatestDemandBeforeReleasingOwnedOutput()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var device = new FakeOutput();
        var factory = new FakeFactory(device) { BeforeOpen = () => { entered.TrySetResult(); release.Wait(Token); } };
        await using var owner = new SceneAudioPlaybackOwner(factory);
        SceneMediaTrack track = Track();
        try
        {
            owner.Publish(track, Sample(track, 0, true)); await entered.Task.WaitAsync(Token);
            owner.Publish(track, Sample(track, TimeSpan.TicksPerSecond, false));
        }
        finally { release.Set(); }
        await owner.FlushAsync(Token);
        Assert.False(device.Playing); Assert.Equal(8000, device.PositionFrames);
        owner.Publish(null, null); await owner.FlushAsync(Token);
        Assert.True(device.Disposed); Assert.Null(owner.LastError);
    }

    [Fact]
    public async Task IdentityMismatchIsRejectedAndFailedCleanupCanBeRetried()
    {
        var device = new FakeOutput { FailDisposeOnce = true };
        var owner = new SceneAudioPlaybackOwner(new FakeFactory(device)); SceneMediaTrack track = Track();
        Assert.Throws<ArgumentException>(() => owner.Publish(track, Sample(track, 0, true) with { Sha256 = "different" }));
        owner.Publish(track, Sample(track, 0, true)); await owner.FlushAsync(Token);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await owner.DisposeAsync());
        Assert.False(device.Disposed); Assert.NotNull(owner.LastError);
        await owner.DisposeAsync(); Assert.True(device.Disposed); Assert.Null(owner.LastError);
    }

    [Fact]
    public async Task SynchronizationFailureStopsOldAudioAndPreservesTheOriginalError()
    {
        var device = new FakeOutput();
        await using var owner = new SceneAudioPlaybackOwner(new FakeFactory(device)); SceneMediaTrack track = Track();
        owner.Publish(track, Sample(track, 0, true)); await owner.FlushAsync(Token);
        Assert.True(device.Playing); device.FailPauseOnce = true;
        owner.Publish(track, Sample(track, 10000, false)); await owner.FlushAsync(Token);
        Assert.True(device.Disposed); Assert.False(device.Playing);
        Assert.IsType<IOException>(owner.LastError);
    }

    [Fact]
    public async Task DisabledUnsupportedAudioIsCarriedWithoutOpeningAConsumer()
    {
        var factory = new FakeFactory(new FakeOutput()); await using var owner = new SceneAudioPlaybackOwner(factory);
        var track = new SceneMediaTrack(Guid.NewGuid(), SceneMediaKind.Audio,
            SceneMediaAsset.FromBytes("disabled opaque audio"u8, "unsupported.wav"), 0, false);
        owner.Publish(track, Sample(track, 0, true)); await owner.FlushAsync(Token);
        Assert.Equal(0, factory.Opens); Assert.Null(owner.LastError);
    }

    private static SceneMediaTrack Track() => new(Guid.NewGuid(), SceneMediaKind.Audio,
        SceneMediaAsset.FromBytes(ScenePcmAudioTests.Wav(new byte[32000]), "audio.wav"), 0, true);
    private static SceneMediaSample Sample(SceneMediaTrack track, long ticks, bool playing)
        => new(track.Id, SceneMediaKind.Audio, track.Asset.Sha256, true, ticks, playing);

    private sealed class FakeFactory(FakeOutput output) : ISceneAudioOutputFactory
    {
        public int Opens { get; private set; }
        public Action? BeforeOpen { get; init; }
        public ISceneAudioOutput Open(ScenePcmAudio audio) { BeforeOpen?.Invoke(); Opens++; return output; }
    }

    private sealed class FakeOutput : ISceneAudioOutput
    {
        public long PositionFrames { get; set; }
        public bool Playing { get; private set; }
        public bool Disposed { get; private set; }
        public bool FailDisposeOnce { get; set; }
        public bool FailPauseOnce { get; set; }
        public int Seeks { get; private set; }
        public void Seek(long frame) { PositionFrames = frame; Seeks++; }
        public void Play() => Playing = true;
        public void Pause()
        {
            if (FailPauseOnce) { FailPauseOnce = false; throw new IOException("Injected audio pause failure."); }
            Playing = false;
        }
        public void Dispose()
        {
            if (FailDisposeOnce) { FailDisposeOnce = false; throw new IOException("Injected retryable device cleanup failure."); }
            Playing = false; Disposed = true;
        }
    }
}
