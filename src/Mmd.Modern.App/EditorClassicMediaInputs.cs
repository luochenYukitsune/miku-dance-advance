using Mmd.Modern.Application;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;

namespace Mmd.Modern.App;

/// <summary>Captures original layout and immutable media track bytes at binding. Rendering never
/// reaches back into the workspace or the original PMM/media directory.</summary>
internal static class EditorClassicMediaInputs
{
    internal static IReadOnlyList<D3D9ClassicMediaSource> Capture(SceneProjectSession source,
        out D3D9ClassicScreenCaptureMode screenCaptureMode)
    {
        ArgumentNullException.ThrowIfNull(source);
        var document = source.PmmControls?.ExportDocument();
        screenCaptureMode = (D3D9ClassicScreenCaptureMode)(document?.Settings.ScreenCaptureMode ?? 0);
        if (!Enum.IsDefined(screenCaptureMode))
            throw new NotSupportedException("Unknown original PMM screen capture mode.");
        EditorSceneAudioInputs.Validate(source.Media);
        var visualMedia = source.Media.Where(value => value.Kind != SceneMediaKind.Audio).ToArray();
        if (visualMedia.Length == 0) return [];
        if (document is null)
            throw new NotSupportedException("Classic backgrounds require their captured original PMM layout.");
        var settings = document.Settings;
        bool refreshVideo = screenCaptureMode == D3D9ClassicScreenCaptureMode.BackgroundRefresh;
        var captured = visualMedia.Select(track => new D3D9ClassicMediaSource(
            // The original AVI background gate is exactly == 1, not nonzero;
            // another retained PMM raw value must not become a visible layer.
            track.Kind == SceneMediaKind.Video ? track with { Enabled = (track.Enabled && settings.VideoEnabledRaw == 1) || refreshVideo } : track,
            track.Kind switch
        {
            SceneMediaKind.BackgroundImage => new(settings.ImageOffsetX, settings.ImageOffsetY, settings.ImageScale),
            SceneMediaKind.Video => new(settings.VideoOffsetX, settings.VideoOffsetY, settings.VideoScale),
            _ => throw new NotSupportedException("Unknown Classic background kind.")
        }, DrawBackground: track.Kind != SceneMediaKind.Video || (track.Enabled && settings.VideoEnabledRaw == 1))).ToArray();
        if (captured.Length > 2 || captured.Select(value => value.Track.Kind).Distinct().Count() != captured.Length)
            throw new NotSupportedException("Classic backgrounds support the original single image and single video tracks.");
        foreach (var item in captured)
            if (!float.IsFinite(item.Placement.Scale)) throw new InvalidDataException("Classic PMM media scale must be finite.");
        return Array.AsReadOnly(captured);
    }
}
