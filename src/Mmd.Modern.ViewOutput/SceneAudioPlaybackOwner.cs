using Mmd.Modern.Application;

namespace Mmd.Modern.ViewOutput;

/// <summary>A single owned PCM output. Calls are serialized by SceneAudioPlaybackOwner on its worker.
/// Device position is diagnostic feedback only and never advances animation or physics.
/// Dispose must retain still-owned resources after a failure so a later call can retry cleanup.</summary>
public interface ISceneAudioOutput : IDisposable
{
    long PositionFrames { get; }
    void Seek(long frame);
    void Play();
    void Pause();
}

public interface ISceneAudioOutputFactory
{
    ISceneAudioOutput Open(ScenePcmAudio audio);
}

/// <summary>Latest-demand preview audio consumer. Publish from the existing scene clock on play,
/// pause, seek and playback ticks. Device operations never run on the publishing/UI thread.
/// Publish(null, null) releases output for export or project replacement. No core clock is created.</summary>
public sealed class SceneAudioPlaybackOwner(ISceneAudioOutputFactory factory) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly ISceneAudioOutputFactory factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly List<(long Sequence, TaskCompletionSource Completion)> waiters = [];
    private (SceneMediaTrack? Track, SceneMediaSample? Sample, long SeekRevision) latest;
    private long requestedSequence, completedSequence;
    private bool running, disposeRequested;
    private ISceneAudioOutput? output;
    private bool outputRequiresCleanup;
    private bool cleanupAttempted;
    private ScenePcmAudio? pcm;
    private Guid trackId;
    private bool playing;
    private long lastRequestedFrame;
    private long appliedSeekRevision;
    private Exception? lastError;
    public Exception? LastError { get { lock (gate) return lastError; } }

    /// <param name="seekRevision">Increment for an explicit seek/document replacement, then keep that
    /// value on following ticks. This preserves even a sub-80ms forward seek through coalescing.</param>
    public void Publish(SceneMediaTrack? track, SceneMediaSample? sample, long seekRevision = 0)
    {
        if ((track is null) != (sample is null)) throw new ArgumentException("Track and sample must both be supplied or both be null.");
        if (track is not null && sample is not null)
        {
            track.Validate();
            if (track.Kind != SceneMediaKind.Audio || sample.Kind != SceneMediaKind.Audio || sample.TrackId != track.Id
                || !StringComparer.Ordinal.Equals(sample.Sha256, track.Asset.Sha256) || sample.MediaTicks < 0)
                throw new ArgumentException("Audio sample identity/time does not match its captured audio track.", nameof(sample));
        }
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposeRequested, this);
            latest = (track, sample, seekRevision); requestedSequence++;
            StartWorker();
        }
    }

    /// <summary>Waits until the latest published demand has been consumed, including a reported failure.
    /// Caller cancellation cancels only this wait. Inspect LastError for device/format failures.</summary>
    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (completedSequence >= requestedSequence) return Task.CompletedTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add((requestedSequence, completion));
            return completion.Task.WaitAsync(cancellationToken);
        }
    }

    private void StartWorker()
    {
        if (running) return;
        running = true;
        _ = Task.Run(Drain);
    }

    private void Drain()
    {
        while (true)
        {
            (SceneMediaTrack? Track, SceneMediaSample? Sample, long SeekRevision) demand; long sequence;
            lock (gate)
            {
                if (completedSequence >= requestedSequence) { running = false; return; }
                demand = latest; sequence = requestedSequence;
            }
            Exception? failure = null;
            cleanupAttempted = false;
            try { Apply(demand.Track, demand.Sample, demand.SeekRevision); }
            catch (Exception error)
            {
                failure = error; outputRequiresCleanup = true;
                // A failed seek/pause must not leave old sound playing while only an error is
                // recorded. Stop/reset/close immediately; retain ownership if that cleanup fails.
                try { if (!cleanupAttempted) ReleaseOutput(); }
                catch (Exception cleanupError)
                {
                    failure = new AggregateException("Audio synchronization failed and its output still requires cleanup.", error, cleanupError);
                }
            }
            lock (gate)
            {
                lastError = failure; completedSequence = sequence;
                for (int i = waiters.Count - 1; i >= 0; i--)
                    if (waiters[i].Sequence <= sequence) { waiters[i].Completion.TrySetResult(); waiters.RemoveAt(i); }
            }
        }
    }

    private void Apply(SceneMediaTrack? track, SceneMediaSample? sample, long seekRevision)
    {
        if (outputRequiresCleanup) ReleaseOutput();
        if (track is null || sample is null || !track.Enabled) { ReleaseOutput(); return; }
        if (pcm is null || output is null || trackId != track.Id || pcm.AssetSha256 != track.Asset.Sha256)
        {
            ReleaseOutput();
            var nextPcm = ScenePcmAudio.FromAsset(track.Asset);
            output = factory.Open(nextPcm);
            pcm = nextPcm; trackId = track.Id; playing = false; cleanupAttempted = false;
        }
        long requestedFrame = pcm.FrameAtTicks(sample.MediaTicks);
        if (!sample.Active || !sample.Playing || requestedFrame >= pcm.SampleCount)
        {
            output.Pause(); output.Seek(requestedFrame);
            playing = false; lastRequestedFrame = requestedFrame; appliedSeekRevision = seekRevision; return;
        }
        // Seek backward immediately; repair forward jumps/device drift above 80ms. The device
        // position only corrects this consumer against the authoritative source-clock sample.
        long deviceFrame = output.PositionFrames;
        if (!playing || seekRevision != appliedSeekRevision || requestedFrame < lastRequestedFrame
            || Math.Abs(deviceFrame - requestedFrame) > pcm.SampleRate * 80L / 1000)
        {
            output.Pause(); output.Seek(requestedFrame);
        }
        output.Play(); playing = true; lastRequestedFrame = requestedFrame; appliedSeekRevision = seekRevision;
    }

    private void ReleaseOutput()
    {
        if (output is not null) { cleanupAttempted = true; output.Dispose(); output = null; }
        pcm = null; trackId = Guid.Empty; playing = false; outputRequiresCleanup = false;
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            disposeRequested = true; latest = (null, null, 0); requestedSequence++; StartWorker();
        }
        await FlushAsync().ConfigureAwait(false);
        lock (gate)
            if (lastError is not null) throw new InvalidOperationException("Audio cleanup failed; DisposeAsync can be retried.", lastError);
    }
}
