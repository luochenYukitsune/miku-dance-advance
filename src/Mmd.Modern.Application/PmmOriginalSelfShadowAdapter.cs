using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.Application;

/// <summary>Maps sampled original camera/light/shadow fields to the original host's paired
/// camera-relative shadow space. A modern world-space eye alone is insufficient.</summary>
public static class PmmOriginalSelfShadowAdapter
{
    /// <summary>Mode zero disables the selected SM3 shadow map. This is a modern dispatcher
    /// policy, not the original fixed-function global-disable path. With no settings, the
    /// selected modern map policy is 2048 square, point clamp and LessEqual. Raw Distance is S,
    /// not the integer UI distance. A parented camera also requires its solved rig transform.</summary>
    public static OriginalSelfShadowOptions? Create(PmmCameraPose camera, PmmLightPose light,
        VmdSelfShadowPose shadow, Matrix4x4 modelWorld, OriginalSelfShadowOptions? settings = null,
        Matrix4x4? cameraParentTransform = null)
    {
        if (shadow.Mode == 0) return null;
        if (shadow.Mode > 2) throw new NotSupportedException("Only original self-shadow modes zero, one and two are supported.");
        if (camera.ParentModelIndex >= 0 && cameraParentTransform is null)
            throw new NotSupportedException("Original animated shadow projection for a parented camera requires a validated parent-space mapping.");
        var parentInverse = Matrix4x4.Identity;
        if (cameraParentTransform is { } parent && (!Matrix4x4.Invert(parent, out parentInverse)
            || !Finite(parentInverse) || parent.M14 != 0 || parent.M24 != 0 || parent.M34 != 0 || parent.M44 != 1))
            throw new ArgumentException("Camera parent must be a finite, invertible affine transform.", nameof(cameraParentTransform));
        if (camera.IgnoreParentRotation && cameraParentTransform is { } translationParent)
            parentInverse = Matrix4x4.CreateTranslation(-translationParent.Translation);
        // Original main render host: row vectors, Y then X then Z. Preserve raw light length.
        // Keep the native split between geometry space and its local view eye.
        var q = Matrix4x4.CreateRotationY(camera.Angle.Y)
            * Matrix4x4.CreateRotationX(camera.Angle.X)
            * Matrix4x4.CreateRotationZ(camera.Angle.Z);
        var world = cameraParentTransform is null
            ? modelWorld * Matrix4x4.CreateTranslation(-camera.LookAt) * q
            : modelWorld * parentInverse * q;
        var direction = cameraParentTransform is null ? light.Direction : Vector3.TransformNormal(light.Direction, parentInverse);
        var transform = OriginalShadowTransform.Create(world,
            cameraParentTransform is null ? new(camera.ViewOffset,camera.Distance) : camera.LookAt,
            Vector3.TransformNormal(direction, q), shadow.Distance, shadow.Mode);
        var selected = settings ?? new OriginalSelfShadowOptions(Matrix4x4.Identity, 2048, 2048,
            true, false, OriginalTextureSampling.PointClamp, OriginalDepthComparison.LessEqual);
        var result = selected with { LightViewProjection = transform.LightViewProjection,
            PartialFar = transform.PartialFar, Generate = true };
        OriginalFrameMath.Validate(new(OriginalAlphaBlendMode.Normal, true, false, SelfShadow: result));
        return result;
    }

    private static bool Finite(Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14)
        && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24)
        && float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34)
        && float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);
}
