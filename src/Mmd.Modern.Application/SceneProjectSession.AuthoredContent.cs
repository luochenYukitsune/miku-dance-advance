namespace Mmd.Modern.Application;

/// <summary>A detached view of authored inputs, without sampling or editing the playback session.</summary>
public sealed record SceneProjectAuthoredContent(IReadOnlyList<SceneActorMotion> Motions, SceneMotionAsset? SceneMotion)
{
    public IReadOnlyList<SceneProjectAccessory> Accessories { get; init; } = Array.AsReadOnly(Array.Empty<SceneProjectAccessory>());
}

public sealed partial class SceneProjectSession
{
    /// <summary>Read-only containers retain immutable model/motion assets. Reading creates no undo entry
    /// and always reflects the current committed content, including undo and redo.</summary>
    public SceneProjectAuthoredContent CaptureAuthoredContent()
    {
        RequireIdle();
        return new(Array.AsReadOnly(content.Motions.ToArray()), content.SceneMotion)
            { Accessories = Array.AsReadOnly(content.Accessories.ToArray()) };
    }
}
