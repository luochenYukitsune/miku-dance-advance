using Mmd.Modern.ViewOutput.FFmpeg;

namespace Mmd.Modern.App;

public static partial class EditorExportService
{
    /// <summary>Background playback uses the same bundled executable resolver as export.
    /// It never searches PATH, installs codecs, or reads a mutable workspace preference.</summary>
    internal static FfmpegToolchain ResolveBundledMediaTools()
        => new(RequireToolPath(null, "FFmpeg"), RequireToolPath(null, "FFprobe"));
}
