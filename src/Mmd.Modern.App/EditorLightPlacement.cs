using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;

namespace Mmd.Modern.App;

/// <summary>Captures a local starting light once. It never attaches an authored light to the camera.</summary>
internal static class EditorLightPlacement
{
    public static readonly PmmCameraPose DefaultCamera = new(new(0, 10, 0), Vector3.Zero, -45, 30, true);

    public static NativeSceneLight Create(Guid id, string name, SceneLightKind kind, PmmCameraPose pose)
    {
        var camera = ValidateCamera(pose);
        // PMM LookAt is the orbit center; SceneCamera.Target is just a point along the view direction.
        var focus = pose.LookAt;
        var forward = Vector3.Normalize(camera.Target - camera.Eye);
        var up = Vector3.Normalize(camera.Up);
        float span = Math.Clamp(camera.OrthographicHeight, 2, 120);
        var position = focus - forward * (span * .4f) + up * (span * .25f);
        var travel = Vector3.Normalize(focus - position);
        var rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(
            Matrix4x4.CreateWorld(Vector3.Zero, travel, up)));
        // The existing renderer uses artistic I / distance^2 units, not lumens.
        // Keep the initial illuminance local and stable when zooming between scene scales.
        float intensity = kind == SceneLightKind.Directional ? .3f : .5f * Vector3.DistanceSquared(position, focus);
        return new(id, name, kind, position, rotation, Vector3.One, intensity, span * 3,
            AreaSize: kind == SceneLightKind.Area ? new(span * .25f) : null);
    }

    public static SceneCamera ValidateCamera(PmmCameraPose pose)
    {
        if (pose.ParentModelIndex >= 0)
            throw new ArgumentException("Light placement requires a world-space editor camera.", nameof(pose));
        var camera = pose.CreateCamera();
        _ = camera.ViewProjection(1, 1);
        return camera;
    }
}
