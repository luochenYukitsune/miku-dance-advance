using System.Runtime.CompilerServices;
using Mmd.Modern.Application;
using Mmd.Modern.ViewOutput;

namespace Mmd.Modern.App;

/// <summary>The editor consumes one explicitly enabled frozen PCM track. Disabled resources remain
/// authored project data; selecting an output never drops or rewrites them.</summary>
internal static class EditorSceneAudioInputs
{
    private sealed class ValidatedPcm { }
    private static readonly ConditionalWeakTable<SceneMediaAsset, ValidatedPcm> validated = new();

    internal static SceneMediaTrack? CaptureEnabled(IReadOnlyList<SceneMediaTrack> media)
    {
        ArgumentNullException.ThrowIfNull(media);
        var tracks = media.Where(value => value.Kind == SceneMediaKind.Audio && value.Enabled).Take(2).ToArray();
        if (tracks.Length > 1)
            throw new NotSupportedException("Audio output supports one enabled PCM WAV track; multiple enabled tracks require mixing and cannot be adopted.");
        var track = tracks.SingleOrDefault();
        track?.Validate();
        return track;
    }

    /// <summary>Run during background candidate preparation, before releasing the active document.
    /// Cache only successful validation, without retaining another copy of the potentially large PCM bytes.</summary>
    internal static void Validate(IReadOnlyList<SceneMediaTrack> media)
    {
        if (CaptureEnabled(media) is not { } track) return;
        _ = validated.GetValue(track.Asset, static asset =>
        {
            _ = ScenePcmAudio.FromAsset(asset);
            return new();
        });
    }
}
