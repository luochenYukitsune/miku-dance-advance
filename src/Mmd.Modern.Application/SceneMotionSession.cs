using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

public sealed partial class SceneAnimationSession
{
    private SceneMotionAsset? sceneMotion;
    private VmdSceneTracks? sceneTracks;

    /// <summary>Captured scene VMD bytes. Retained character records do not extend the scene clock.</summary>
    public SceneMotionAsset? SceneMotion { get { RequireAlive(); return sceneMotion; } }
    /// <summary>Authored parent-local pose at the shared clock. Resolved world camera is exposed separately.</summary>
    public PmmCameraPose? CameraPose
    {
        get
        {
            RequireAlive();
            var local = sceneTracks?.CameraAt(CurrentFrame) ?? pmmControls?.CameraAt(CurrentFrame);
            if (local is not { } pose || pmmControls is null) return local;
            var target = pmmControls.CameraAt(CurrentFrame);
            return pose with { ParentModelIndex = target.ParentModelIndex, ParentBoneIndex = target.ParentBoneIndex,
                IgnoreParentRotation = target.IgnoreParentRotation };
        }
    }
    public PmmLightPose? LightPose { get { RequireAlive(); return sceneTracks?.LightAt(CurrentFrame); } }
    public VmdSelfShadowPose? SelfShadowPose { get { RequireAlive(); return sceneTracks?.SelfShadowAt(CurrentFrame); } }

    /// <summary>Validates a candidate before publishing it; neither actor solvers nor render settings are changed.</summary>
    public void BindSceneMotion(SceneMotionAsset? asset, CancellationToken cancellationToken = default)
    {
        RequireHealthyWithoutPreviewCompletion(); cancellationToken.ThrowIfCancellationRequested();
        var candidate = PrepareSceneMotion(asset, CurrentFrame);
        cancellationToken.ThrowIfCancellationRequested();
        CompletePreviewLeaves(cancellationToken);
        sceneMotion = asset; sceneTracks = candidate; DurationFrames = Duration(entries);
    }

    internal static VmdSceneTracks? PrepareSceneMotion(SceneMotionAsset? asset, double frame)
    {
        if (asset is null) return null;
        if (asset.Motion.CameraKeyframes.Count == 0 && asset.Motion.LightKeyframes.Count == 0 && asset.Motion.SelfShadowKeyframes.Count == 0)
            throw new NotSupportedException("A scene VMD must contain camera, light or self-shadow keyframes; character-only VMD belongs to an actor.");
        var tracks = new VmdSceneTracks(asset.Motion);
        _ = tracks.CameraAt(frame); _ = tracks.LightAt(frame); _ = tracks.SelfShadowAt(frame);
        return tracks;
    }
}
