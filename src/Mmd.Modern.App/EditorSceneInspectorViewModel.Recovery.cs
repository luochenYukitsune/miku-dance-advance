using Mmd.Modern.Application;

namespace Mmd.Modern.App;

public sealed partial class EditorSceneInspectorViewModel
{
    internal EditorRecoverySceneState CaptureRecoveryState() => new()
    {
        CurrentFrame = currentFrame,
        SceneSelectionIndex = sceneSelectionIndex,
        CameraPose = cameraPose,
        LightPose = lightPose,
        CameraDraft = cameraDraft,
        LightDraft = lightDraft,
        CenterX = centerX, CenterY = centerY, CenterZ = centerZ,
        RotationX = rotationX, RotationY = rotationY, RotationZ = rotationZ,
        Distance = distance, Fov = fov, Perspective = perspective,
        ColorR = colorR, ColorG = colorG, ColorB = colorB, Intensity = intensity,
        DirectionX = directionX, DirectionY = directionY, DirectionZ = directionZ
    };

    internal void ValidateRecoveryContext(EditorRecoverySceneState saved, IReadOnlyList<SceneActorMotion> actors)
    {
        if (saved.CameraPose.ParentModelIndex < 0) return;
        var parent = pmmControls?.ResolveCameraParent(saved.CameraPose)
            ?? throw EditorRecoverySessionState.Invalid("A recovered parented camera requires its PMM actor and bone provenance.");
        var actor = actors.SingleOrDefault(item => item.ObjectId == parent.ObjectId);
        if (actor is null || (uint)parent.BoneIndex >= (uint)actor.Model.Bones.Count
            || !cameraTargets.TryGetValue(parent.ObjectId, out var target)
            || (uint)parent.BoneIndex >= (uint)target.Model.Bones.Count)
            throw EditorRecoverySessionState.Invalid("The recovered camera parent is absent from the project.");
    }

    /// <summary>Called only after schema, roster and PMM parent validation completes for both editors.</summary>
    internal void RestoreRecoveryState(EditorRecoverySceneState saved)
    {
        // Ordinary property setters update and publish immediately. Restore the complete snapshot
        // first so incomplete typed fields never leak an invalid intermediate camera to the host.
        currentFrame = saved.CurrentFrame;
        sceneSelectionIndex = saved.SceneSelectionIndex;
        cameraPose = saved.CameraPose; lightPose = saved.LightPose;
        cameraDraft = saved.CameraDraft; lightDraft = saved.LightDraft;
        centerX = saved.CenterX; centerY = saved.CenterY; centerZ = saved.CenterZ;
        rotationX = saved.RotationX; rotationY = saved.RotationY; rotationZ = saved.RotationZ;
        distance = saved.Distance; fov = saved.Fov; perspective = saved.Perspective;
        colorR = saved.ColorR; colorG = saved.ColorG; colorB = saved.ColorB; intensity = saved.Intensity;
        directionX = saved.DirectionX; directionY = saved.DirectionY; directionZ = saved.DirectionZ;
        contextRevision++;
        foreach (string name in new[] { nameof(CurrentFrame), nameof(SceneSelectionIndex), nameof(IsWorldInspector),
            nameof(IsCameraInspector), nameof(IsLightInspector), nameof(ShowsPrimaryLightTrack), nameof(ActiveTimelineTrack), nameof(CameraCenterX), nameof(CameraCenterY),
            nameof(CameraCenterZ), nameof(CameraRotationX), nameof(CameraRotationY), nameof(CameraRotationZ),
            nameof(CameraDistance), nameof(CameraFov), nameof(CameraProjectionIndex), nameof(LightColorR),
            nameof(LightColorG), nameof(LightColorB), nameof(LightIntensity), nameof(LightDirectionX),
            nameof(LightDirectionY), nameof(LightDirectionZ) }) Changed(name);
        DraftChanged(); PublishCamera(); PublishLight();
    }
}
