using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.Application;

public sealed partial class SceneProjectSession
{
    public PmmCameraPose? CameraPose { get { RequireAlive(); return animation.CameraPose; } }
    public ScenePmmCameraParent? CameraParent { get { RequireAlive(); return animation.CameraParent; } }
    public Matrix4x4? CameraParentTransform { get { RequireAlive(); return animation.CameraParentTransform; } }
    public SceneCamera? Camera { get { RequireAlive(); return animation.Camera; } }
    public Matrix4x4? ResolveCameraParentTransform(PmmCameraPose pose)
    { RequireAlive(); return animation.ResolveCameraParentTransform(pose); }
}
