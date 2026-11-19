using Mmd.Modern.Application;
using Mmd.Modern.ViewOutput;

namespace Mmd.Modern.App;

public sealed partial class EditorShellViewModel
{
    // Constructing the factory opens no device. Tests can replace it before the first playback.
    internal ISceneAudioOutputFactory AudioOutputFactory { get; set; } = new WindowsSceneAudioOutputFactory();
    private SceneAudioPlaybackOwner? audioPlayback;
    private SceneProjectSession? audioProject;
    private Guid audioDocumentId;
    private long audioSeekRevision, audioDemandRevision;
    private bool audioHasDemand, observingAudio, audioBlocked, audioDisposing, audioDisposed;
    private string? reportedAudioFailure;
    internal bool HasAudioPlaybackOwner => audioPlayback is not null;

    private void RefreshAudioWorkspace()
    {
        if (audioDocumentId != Workspace.DocumentId || !ReferenceEquals(audioProject, Workspace.Session))
        {
            // A replacement/restored document always arrives paused. The output retains only its
            // frozen bytes, so releasing it never reads the now-retired original session.
            playbackTimer.Stop();
            if (IsPlaying) IsPlaying = false;
            audioDocumentId = Workspace.DocumentId; audioProject = Workspace.Session;
            audioSeekRevision++; audioBlocked = false; reportedAudioFailure = null;
            PublishAudio(null, null);
        }
        else SynchronizeAudioPlayback(explicitSeek: true);
    }

    private void SynchronizeAudioPlayback(bool explicitSeek = false)
    {
        if (audioDisposed || audioDisposing) return;
        if (explicitSeek) audioSeekRevision++;
        try
        {
            if (audioBlocked || Workspace.Session is not { } session)
            { PublishAudio(null, null); return; }
            var track = EditorSceneAudioInputs.CaptureEnabled(session.Media);
            if (track is null) { PublishAudio(null, null); return; }
            // Loading or seeking a paused document must not open a device. A paused owner which
            // already has a demand still receives its exact seek/pause sample.
            if (!IsPlaying && !audioHasDemand) return;
            audioPlayback ??= new(AudioOutputFactory);
            var sample = session.SampleMedia(IsPlaying).Single(value => value.TrackId == track.Id);
            PublishAudio(track, sample);
        }
        catch (Exception error) { FailAudioPlayback(error); }
    }

    private void PublishAudio(SceneMediaTrack? track, SceneMediaSample? sample)
    {
        if (audioPlayback is null || audioDisposed || audioDisposing) return;
        audioHasDemand = track is not null;
        audioDemandRevision++;
        audioPlayback.Publish(track, sample, audioSeekRevision);
        ObserveAudioPlayback();
    }

    private async void ObserveAudioPlayback()
    {
        if (observingAudio || audioPlayback is not { } owner || audioDisposed || audioDisposing) return;
        observingAudio = true;
        try
        {
            while (!audioDisposed && !audioDisposing && ReferenceEquals(owner, audioPlayback))
            {
                long revision = audioDemandRevision;
                await owner.FlushAsync();
                if (audioDisposed || audioDisposing || !ReferenceEquals(owner, audioPlayback)) return;
                if (revision != audioDemandRevision) continue;
                if (owner.LastError is { } error) FailAudioPlayback(error);
                return;
            }
        }
        catch (Exception error) { if (!audioDisposed && !audioDisposing) FailAudioPlayback(error); }
        finally { observingAudio = false; }
    }

    private void FailAudioPlayback(Exception error)
    {
        audioBlocked = true;
        playbackTimer.Stop();
        if (IsPlaying) IsPlaying = false;
        // Retire stale sound after any format/device failure. The owner keeps failed cleanup
        // retryable; explicit export/close awaits this release and surfaces its result.
        if (audioHasDemand) PublishAudio(null, null);
        string identity = error.GetType().FullName + ":" + error.Message;
        if (reportedAudioFailure == identity) return;
        reportedAudioFailure = identity;
        ReportActivityFailure(new InvalidOperationException(EditorLocalization.Text("Mda.Text.Audio.PlaybackFailed"), error));
    }

    internal async Task ReleaseAudioPlaybackAsync(CancellationToken token = default)
    {
        StopPlayback();
        if (audioDisposing && audioPlayback is { } retained)
        {
            await retained.DisposeAsync();
            audioPlayback = null; audioHasDemand = false; audioDisposed = true; audioDisposing = false;
            return;
        }
        audioSeekRevision++;
        PublishAudio(null, null);
        if (audioPlayback is not { } owner) return;
        await owner.FlushAsync(token);
        if (owner.LastError is { } error)
            throw new InvalidOperationException(EditorLocalization.Text("Mda.Text.Audio.ReleaseFailed"), error);
    }

    private void DisposeAudioPlayback()
    {
        if (audioDisposed) return;
        audioDisposing = true;
        if (audioPlayback is { } owner)
        {
            // Device operations run on the owner's worker and never need this UI thread.
            // On failure the owner is kept (not nulled) so a later Dispose call could retry; the window close path
            // calls Dispose once and does not.
            owner.DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
            audioPlayback = null;
        }
        audioHasDemand = false; audioDisposed = true; audioDisposing = false;
    }
}
