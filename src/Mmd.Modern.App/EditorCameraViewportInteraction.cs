using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;

namespace Mmd.Modern.App;

public enum CameraGizmoPart { None, Body, AxisX, AxisY, AxisZ, Target }

public sealed record CameraViewportHit(CameraGizmoPart Part);

/// <summary>Pure 3D camera-actor overlay geometry, hit-testing, and UE5-style transform math.</summary>
public static class EditorCameraViewportInteraction
{
    private static readonly Vector4 UnselectedBodyColor = new(0.28f, 0.70f, 0.96f, 0.92f);
    private static readonly Vector4 SelectedBodyColor = new(0.16f, 0.84f, 1.00f, 1.00f);
    private static readonly Vector4 FrustumColor = new(0.32f, 0.76f, 0.98f, 0.70f);
    private static readonly Vector4 SelectedFrustumColor = new(0.22f, 0.88f, 1.00f, 0.95f);
    private static readonly Vector4 TargetColor = new(1.00f, 0.78f, 0.24f, 0.95f);
    private static readonly Vector4 ActiveColor = new(1.00f, 0.92f, 0.35f, 1.00f);
    private static readonly Vector4 OutlineColor = new(0.08f, 0.10f, 0.14f, 0.88f);
    private static readonly Vector4 AxisXColor = new(0.95f, 0.28f, 0.28f, 1.00f);
    private static readonly Vector4 AxisYColor = new(0.24f, 0.84f, 0.36f, 1.00f);
    private static readonly Vector4 AxisZColor = new(0.28f, 0.56f, 0.98f, 1.00f);

    public static Vector3 ResolveLookAtWorld(PmmCameraPose pose, SceneCamera resolvedCamera)
    {
        ArgumentNullException.ThrowIfNull(resolvedCamera);
        if (pose.ParentModelIndex < 0)
            return pose.ToKeyframePose().LookAt;

        var forward = Vector3.Normalize(resolvedCamera.Target - resolvedCamera.Eye);
        float depth = MathF.Max(1f, MathF.Abs(pose.CameraDepth));
        return resolvedCamera.Eye + forward * depth;
    }

    public static PmmCameraPose TranslateBody(PmmCameraPose pose, Vector3 worldDelta)
    {
        if (pose.ParentModelIndex >= 0 || !Finite(worldDelta)) return pose;
        return pose with { LookAt = pose.LookAt + worldDelta };
    }

    public static PmmCameraPose AimAtTarget(PmmCameraPose pose, Vector3 newLookAt)
    {
        if (pose.ParentModelIndex >= 0 || !Finite(newLookAt)) return pose;
        var current = pose.CreateCamera();
        var eye = current.Eye;
        var offset = newLookAt - eye;
        float dist = offset.Length();
        if (!float.IsFinite(dist) || dist < 0.05f) return pose;

        var forward = offset / dist;
        float pitch = MathF.Asin(Math.Clamp(forward.Y, -1f, 1f));
        float yaw = MathF.Atan2(-forward.X, forward.Z);
        return pose with
        {
            LookAt = newLookAt,
            Angle = new Vector3(pitch, yaw, pose.Angle.Z),
            Distance = -dist,
            ViewOffset = Vector2.Zero
        };
    }

    /// <summary>Creates an external UE5-style observation camera framing both the target and the scene camera actor.</summary>
    public static PmmCameraPose CreateObservationCameraAround(PmmCameraPose scenePose, Matrix4x4? parentTransform = null)
    {
        var sceneCamera = scenePose.CreateCamera(parentTransform);
        var lookAt = ResolveLookAtWorld(scenePose, sceneCamera);
        var toEye = sceneCamera.Eye - lookAt;
        float baseDistance = MathF.Max(12f, toEye.Length());
        var center = Vector3.Lerp(lookAt, sceneCamera.Eye, 0.35f);

        var forward = Vector3.Normalize(sceneCamera.Target - sceneCamera.Eye);
        float basePitch = MathF.Asin(Math.Clamp(forward.Y, -0.95f, 0.95f));
        float baseYaw = MathF.Atan2(-forward.X, forward.Z);

        float pitch = Math.Clamp(basePitch - 0.22f, -1.25f, 1.25f);
        float yaw = baseYaw + 0.48f;
        float obsDistance = Math.Clamp(baseDistance * 1.48f, 18f, 360f);

        return new PmmCameraPose(
            LookAt: center,
            Angle: new Vector3(pitch, yaw, 0f),
            Distance: -obsDistance,
            Fov: 32f,
            Perspective: true);
    }

    public static ViewportOverlaySnapshot CreateOverlay(
        SceneCamera sceneCamera,
        PmmCameraPose scenePose,
        SceneCamera viewCamera,
        int width,
        int height,
        bool selected,
        CameraGizmoPart activePart = CameraGizmoPart.None)
    {
        ArgumentNullException.ThrowIfNull(sceneCamera);
        ArgumentNullException.ThrowIfNull(viewCamera);
        if (width <= 0 || height <= 0) return new([]);

        var basis = ComputeBasis(sceneCamera);
        if (!basis.Valid) return new([]);

        var eye = sceneCamera.Eye;
        var lookAt = ResolveLookAtWorld(scenePose, sceneCamera);
        float viewDistance = MathF.Max(1f, Vector3.Distance(viewCamera.Eye, eye));
        float scale = Math.Clamp(viewDistance * 0.036f, 0.45f, 3.0f);
        float aspect = Math.Clamp(width / (float)height, 0.4f, 3.5f);

        var lines = new List<ViewportOverlayLine>(80);
        var invSize = new Vector2(1f / width, 1f / height);

        void AddWorldSegment(Vector3 a, Vector3 b, Vector4 color, float thickness)
        {
            if (!TryProjectSegment(a, b, viewCamera, width, height, out var pa, out var pb)) return;
            lines.Add(new(pa * invSize, pb * invSize, color, thickness));
        }

        void AddWorldCircle(Vector3 point, Vector4 color, float thickness, float radius)
        {
            if (!LightViewportInteraction.TryProject(point, viewCamera, width, height, out var p)) return;
            lines.Add(new(p * invSize, p * invSize, OutlineColor, thickness + 1.5f, ViewportOverlayShape.Circle, radius + 1f));
            lines.Add(new(p * invSize, p * invSize, color, thickness, ViewportOverlayShape.Circle, radius));
        }

        var bodyColor = activePart == CameraGizmoPart.Body
            ? ActiveColor
            : (selected ? SelectedBodyColor : UnselectedBodyColor);
        var frustumColor = selected ? SelectedFrustumColor : FrustumColor;
        float bodyWidth = selected ? 2.1f : 1.5f;

        // 1. Camera body box (sits just behind eye so the lens apex meets the optical center).
        float halfW = scale * 0.62f;
        float halfH = scale * 0.44f;
        float bodyBack = -scale * 1.45f;
        float bodyFront = -scale * 0.15f;

        Vector3 BoxCorner(float rx, float uy, float fz) =>
            eye + basis.Right * rx + basis.Up * uy + basis.Forward * fz;

        var b0 = BoxCorner(-halfW, -halfH, bodyBack);
        var b1 = BoxCorner(halfW, -halfH, bodyBack);
        var b2 = BoxCorner(halfW, halfH, bodyBack);
        var b3 = BoxCorner(-halfW, halfH, bodyBack);
        var f0 = BoxCorner(-halfW, -halfH, bodyFront);
        var f1 = BoxCorner(halfW, -halfH, bodyFront);
        var f2 = BoxCorner(halfW, halfH, bodyFront);
        var f3 = BoxCorner(-halfW, halfH, bodyFront);

        AddWorldSegment(b0, b1, bodyColor, bodyWidth);
        AddWorldSegment(b1, b2, bodyColor, bodyWidth);
        AddWorldSegment(b2, b3, bodyColor, bodyWidth);
        AddWorldSegment(b3, b0, bodyColor, bodyWidth);
        AddWorldSegment(f0, f1, bodyColor, bodyWidth);
        AddWorldSegment(f1, f2, bodyColor, bodyWidth);
        AddWorldSegment(f2, f3, bodyColor, bodyWidth);
        AddWorldSegment(f3, f0, bodyColor, bodyWidth);
        AddWorldSegment(b0, f0, bodyColor, bodyWidth);
        AddWorldSegment(b1, f1, bodyColor, bodyWidth);
        AddWorldSegment(b2, f2, bodyColor, bodyWidth);
        AddWorldSegment(b3, f3, bodyColor, bodyWidth);

        // 2. Lens cone + UE5-style view frustum + top-up chevron.
        float frustumDepth = Math.Clamp(Vector3.Distance(eye, lookAt) * 0.34f, scale * 3.2f, 24f);
        float halfFrustumH = sceneCamera.VerticalFovDegrees is { } fovDeg
            ? frustumDepth * MathF.Tan(Math.Clamp(fovDeg, 1f, 170f) * 0.5f * MathF.PI / 180f)
            : MathF.Max(scale, sceneCamera.OrthographicHeight * 0.25f);
        halfFrustumH = Math.Clamp(halfFrustumH, scale * 0.65f, frustumDepth * 1.4f);
        float halfFrustumW = halfFrustumH * aspect;

        var fr0 = BoxCorner(-halfFrustumW, -halfFrustumH, frustumDepth);
        var fr1 = BoxCorner(halfFrustumW, -halfFrustumH, frustumDepth);
        var fr2 = BoxCorner(halfFrustumW, halfFrustumH, frustumDepth);
        var fr3 = BoxCorner(-halfFrustumW, halfFrustumH, frustumDepth);

        AddWorldSegment(eye, fr0, frustumColor, 1.25f);
        AddWorldSegment(eye, fr1, frustumColor, 1.25f);
        AddWorldSegment(eye, fr2, frustumColor, 1.25f);
        AddWorldSegment(eye, fr3, frustumColor, 1.25f);
        AddWorldSegment(fr0, fr1, frustumColor, 1.45f);
        AddWorldSegment(fr1, fr2, frustumColor, 1.45f);
        AddWorldSegment(fr2, fr3, frustumColor, 1.45f);
        AddWorldSegment(fr3, fr0, frustumColor, 1.45f);

        // Up orientation triangle above the top frustum edge.
        var upBaseL = BoxCorner(-halfFrustumW * 0.28f, halfFrustumH + scale * 0.14f, frustumDepth);
        var upBaseR = BoxCorner(halfFrustumW * 0.28f, halfFrustumH + scale * 0.14f, frustumDepth);
        var upTip = BoxCorner(0f, halfFrustumH + scale * 0.72f, frustumDepth);
        AddWorldSegment(upBaseL, upBaseR, bodyColor, 1.6f);
        AddWorldSegment(upBaseL, upTip, bodyColor, 1.6f);
        AddWorldSegment(upBaseR, upTip, bodyColor, 1.6f);

        // 3. LookAt tether (dashed) & target handle.
        float targetDist = Vector3.Distance(eye, lookAt);
        if (targetDist > scale * 1.5f)
        {
            const int dashes = 6;
            for (int i = 0; i < dashes; i++)
            {
                float t0 = (i + 0.15f) / dashes;
                float t1 = (i + 0.65f) / dashes;
                AddWorldSegment(Vector3.Lerp(eye, lookAt, t0), Vector3.Lerp(eye, lookAt, t1),
                    selected ? TargetColor : new Vector4(0.60f, 0.76f, 0.88f, 0.55f), 1.15f);
            }
        }

        var targetColor = activePart == CameraGizmoPart.Target ? ActiveColor : TargetColor;
        AddWorldCircle(lookAt, targetColor, selected ? 2.0f : 1.4f, selected ? 6.5f : 4.5f);

        // 4. World-space XYZ translation gizmo when selected and unparented.
        if (selected && scenePose.ParentModelIndex < 0)
        {
            float axisLen = scale * 2.75f;
            AddWorldSegment(eye, eye + Vector3.UnitX * axisLen,
                activePart == CameraGizmoPart.AxisX ? ActiveColor : AxisXColor,
                activePart == CameraGizmoPart.AxisX ? 3.0f : 2.3f);
            AddWorldSegment(eye, eye + Vector3.UnitY * axisLen,
                activePart == CameraGizmoPart.AxisY ? ActiveColor : AxisYColor,
                activePart == CameraGizmoPart.AxisY ? 3.0f : 2.3f);
            AddWorldSegment(eye, eye + Vector3.UnitZ * axisLen,
                activePart == CameraGizmoPart.AxisZ ? ActiveColor : AxisZColor,
                activePart == CameraGizmoPart.AxisZ ? 3.0f : 2.3f);
            AddWorldCircle(eye, bodyColor, 2.0f, 5.5f);
        }

        return new ViewportOverlaySnapshot(lines.Take(ViewportOverlaySnapshot.MaxLines));
    }

    public static CameraViewportHit? HitTest(
        Vector2 pointer,
        SceneCamera sceneCamera,
        PmmCameraPose scenePose,
        SceneCamera viewCamera,
        int width,
        int height,
        bool selected)
    {
        ArgumentNullException.ThrowIfNull(sceneCamera);
        ArgumentNullException.ThrowIfNull(viewCamera);
        if (width <= 0 || height <= 0 || !Finite(pointer)) return null;

        var basis = ComputeBasis(sceneCamera);
        if (!basis.Valid) return null;

        var eye = sceneCamera.Eye;
        var lookAt = ResolveLookAtWorld(scenePose, sceneCamera);
        float viewDistance = MathF.Max(1f, Vector3.Distance(viewCamera.Eye, eye));
        float scale = Math.Clamp(viewDistance * 0.036f, 0.45f, 3.0f);

        // 1. Selected gizmo axes and LookAt target take highest priority for precise manipulation.
        if (selected && scenePose.ParentModelIndex < 0)
        {
            float axisLen = scale * 2.75f;
            CameraGizmoPart bestAxis = CameraGizmoPart.None;
            float bestAxisDistSq = 11f * 11f;

            void CheckAxis(CameraGizmoPart part, Vector3 dir)
            {
                if (!TryProjectSegment(eye, eye + dir * axisLen, viewCamera, width, height, out var pa, out var pb)) return;
                float distSq = DistanceToSegmentSquared(pointer, pa, pb);
                if (distSq < bestAxisDistSq)
                {
                    bestAxisDistSq = distSq;
                    bestAxis = part;
                }
            }

            CheckAxis(CameraGizmoPart.AxisX, Vector3.UnitX);
            CheckAxis(CameraGizmoPart.AxisY, Vector3.UnitY);
            CheckAxis(CameraGizmoPart.AxisZ, Vector3.UnitZ);
            if (bestAxis != CameraGizmoPart.None)
                return new CameraViewportHit(bestAxis);

            if (LightViewportInteraction.TryProject(lookAt, viewCamera, width, height, out var targetPx)
                && Vector2.DistanceSquared(pointer, targetPx) <= 14f * 14f)
            {
                return new CameraViewportHit(CameraGizmoPart.Target);
            }
        }

        // 2. Camera body / optical center / lens click always selects the camera component.
        if (LightViewportInteraction.TryProject(eye, viewCamera, width, height, out var eyePx)
            && Vector2.DistanceSquared(pointer, eyePx) <= 22f * 22f)
        {
            return new CameraViewportHit(CameraGizmoPart.Body);
        }

        var bodyCenter = eye - basis.Forward * (scale * 0.8f);
        if (LightViewportInteraction.TryProject(bodyCenter, viewCamera, width, height, out var bodyPx)
            && Vector2.DistanceSquared(pointer, bodyPx) <= 22f * 22f)
        {
            return new CameraViewportHit(CameraGizmoPart.Body);
        }

        // 3. Clicking on the front frustum outline also selects the camera actor smoothly.
        float aspect = Math.Clamp(width / (float)height, 0.4f, 3.5f);
        float frustumDepth = Math.Clamp(Vector3.Distance(eye, lookAt) * 0.34f, scale * 3.2f, 24f);
        float halfFrustumH = sceneCamera.VerticalFovDegrees is { } fovDeg
            ? frustumDepth * MathF.Tan(Math.Clamp(fovDeg, 1f, 170f) * 0.5f * MathF.PI / 180f)
            : MathF.Max(scale, sceneCamera.OrthographicHeight * 0.25f);
        halfFrustumH = Math.Clamp(halfFrustumH, scale * 0.65f, frustumDepth * 1.4f);
        float halfFrustumW = halfFrustumH * aspect;

        var fr0 = eye - basis.Right * halfFrustumW - basis.Up * halfFrustumH + basis.Forward * frustumDepth;
        var fr1 = eye + basis.Right * halfFrustumW - basis.Up * halfFrustumH + basis.Forward * frustumDepth;
        var fr2 = eye + basis.Right * halfFrustumW + basis.Up * halfFrustumH + basis.Forward * frustumDepth;
        var fr3 = eye - basis.Right * halfFrustumW + basis.Up * halfFrustumH + basis.Forward * frustumDepth;

        foreach (var (a, b) in new[] { (eye, fr0), (eye, fr1), (eye, fr2), (eye, fr3), (fr0, fr1), (fr1, fr2), (fr2, fr3), (fr3, fr0) })
        {
            if (TryProjectSegment(a, b, viewCamera, width, height, out var pa, out var pb)
                && DistanceToSegmentSquared(pointer, pa, pb) <= 9f * 9f)
            {
                return new CameraViewportHit(CameraGizmoPart.Body);
            }
        }

        return null;
    }

    private readonly record struct Basis(Vector3 Right, Vector3 Up, Vector3 Forward, bool Valid);

    private static Basis ComputeBasis(SceneCamera camera)
    {
        var rawForward = camera.Target - camera.Eye;
        if (!Finite(rawForward) || rawForward.LengthSquared() < 1e-8f) return default;
        var forward = Vector3.Normalize(rawForward);
        var rawRight = Vector3.Cross(camera.Up, forward);
        if (rawRight.LengthSquared() < 1e-8f)
            rawRight = Vector3.Cross(Vector3.UnitX, forward);
        if (rawRight.LengthSquared() < 1e-8f) return default;
        var right = Vector3.Normalize(rawRight);
        var up = Vector3.Normalize(Vector3.Cross(forward, right));
        return new(right, up, forward, Finite(right) && Finite(up) && Finite(forward));
    }

    private static bool TryProjectSegment(Vector3 a, Vector3 b, SceneCamera viewCamera, int width, int height, out Vector2 pa, out Vector2 pb)
    {
        pa = pb = default;
        if (!LightViewportInteraction.TryProject(a, viewCamera, width, height, out pa)) return false;
        if (!LightViewportInteraction.TryProject(b, viewCamera, width, height, out pb)) return false;
        return Finite(pa) && Finite(pb);
    }

    private static float DistanceToSegmentSquared(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float lenSq = ab.LengthSquared();
        if (lenSq < 1e-6f) return Vector2.DistanceSquared(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / lenSq, 0f, 1f);
        return Vector2.DistanceSquared(p, a + ab * t);
    }

    private static bool Finite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
