using Mmd.Modern.Application;

namespace Mmd.Modern.App;

/// <summary>Output defaults derived from the committed project, independently of the timeline cursor.</summary>
public static class EditorExportRange
{
    /// <summary>
    /// Reuses the application's authored-track maximum: all actors' bone, morph and IK/model
    /// visibility keys, scene camera/light/self-shadow keys and retained PMM controls.
    /// An empty editor or a project without later keys ends at frame zero.
    /// Read this when opening a new or automatic output range; a manual range remains the draft's own value.
    /// </summary>
    public static uint GetLastKeyframe(SceneProjectSession? project) => project?.DurationFrames ?? 0u;
}
