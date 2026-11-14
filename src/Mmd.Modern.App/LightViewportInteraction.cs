using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.App;

public enum LightDragAxis { Free, X, Y, Z }
public sealed record LightViewportHit(Guid LightId, LightDragAxis Axis);

/// <summary>Pure projection/picking and world-space transforms. No HWND, renderer, or source-file edits.</summary>
public static class LightViewportInteraction
{
    private const int RingSegments = 32;
    private static readonly LightDragAxis[] Axes = [LightDragAxis.X, LightDragAxis.Y, LightDragAxis.Z];

    public static bool TryProject(Vector3 world, SceneCamera camera, int width, int height, out Vector2 pixel)
    {
        ArgumentNullException.ThrowIfNull(camera);
        pixel = default;
        if (width <= 0 || height <= 0) return false;
        var clip = Vector4.Transform(new Vector4(world, 1), camera.ViewProjection(width, height));
        if (!float.IsFinite(clip.W) || clip.W < 1e-6f) return false;
        var p = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
        if (!Finite(p) || p.Z < 0 || p.Z > 1) return false;
        pixel = new((p.X * .5f + .5f) * width, (.5f - p.Y * .5f) * height);
        return true;
    }

    public static Vector3 Translate(Vector3 position, Vector2 deltaPixels, SceneCamera camera, int width, int height, LightDragAxis axis = LightDragAxis.Free)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (!Enum.IsDefined(axis)) throw new ArgumentOutOfRangeException(nameof(axis));
        if (width <= 0 || height <= 0 || !Finite(deltaPixels)) return position;
        var viewProjection = camera.ViewProjection(width, height);
        if (!Matrix4x4.Invert(viewProjection, out var inverse)) throw new ArgumentException("Camera is not invertible.");
        var clip = Vector4.Transform(new Vector4(position, 1), viewProjection);
        if (MathF.Abs(clip.W) < 1e-6f) return position;
        var ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
        if (axis != LightDragAxis.Free)
        {
            // Solve the projected world-axis line instead of projecting a camera-plane delta
            // onto the axis. The latter slows oblique axes and fails when depth changes.
            var basis = Axis(axis);
            var direction = Vector4.Transform(new Vector4(basis, 0), viewProjection);
            var screenAxis = new Vector2((direction.X * clip.W - clip.X * direction.W) * width,
                -(direction.Y * clip.W - clip.Y * direction.W) * height);
            float length = screenAxis.LengthSquared();
            if (!float.IsFinite(length) || length < 1e-8f) return position;
            var screenDelta = screenAxis * (Vector2.Dot(deltaPixels, screenAxis) / length);
            var target = new Vector2(ndc.X + screenDelta.X * 2 / width, ndc.Y - screenDelta.Y * 2 / height);
            var denominator = new Vector2(direction.X - target.X * direction.W, direction.Y - target.Y * direction.W);
            var numerator = new Vector2(target.X * clip.W - clip.X, target.Y * clip.W - clip.Y);
            float squared = denominator.LengthSquared();
            if (!float.IsFinite(squared) || squared < 1e-12f) return position;
            var moved = position + basis * (Vector2.Dot(numerator, denominator) / squared);
            return Finite(moved) ? moved : position;
        }
        var shifted = new Vector3(ndc.X + 2 * deltaPixels.X / width, ndc.Y - 2 * deltaPixels.Y / height, ndc.Z);
        var a = Vector4.Transform(new Vector4(ndc, 1), inverse);
        var b = Vector4.Transform(new Vector4(shifted, 1), inverse);
        if (MathF.Abs(a.W) < 1e-6f || MathF.Abs(b.W) < 1e-6f) return position;
        var delta = new Vector3(b.X, b.Y, b.Z) / b.W - new Vector3(a.X, a.Y, a.Z) / a.W;
        var result = position + delta;
        return Finite(result) ? result : position;
    }

    public static Quaternion Rotate(Quaternion rotation, Vector2 deltaPixels, SceneCamera camera)
    {
        var forward = Vector3.Normalize(camera.Target - camera.Eye);
        var right = Vector3.Normalize(camera.LeftHanded ? Vector3.Cross(camera.Up, forward) : Vector3.Cross(forward, camera.Up));
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitY, -deltaPixels.X * .01f)
            * Quaternion.CreateFromAxisAngle(right, -deltaPixels.Y * .01f) * rotation);
    }

    /// <summary>World-axis ring rotation, using the same projected ring plane that is rendered.</summary>
    public static Quaternion RotateOnAxis(Quaternion rotation, Vector3 pivot, Vector2 start, Vector2 current,
        SceneCamera camera, int width, int height, LightDragAxis axis)
    {
        if (!Enum.IsDefined(axis) || axis == LightDragAxis.Free) throw new ArgumentOutOfRangeException(nameof(axis));
        var normal = Axis(axis);
        float angle = (current.X - start.X - current.Y + start.Y) * .01f;
        if (Matrix4x4.Invert(camera.ViewProjection(width, height), out var inverse)
            && TryPlanePoint(start, out var first) && TryPlanePoint(current, out var next))
        {
            first -= pivot; next -= pivot;
            if (first.LengthSquared() > 1e-10f && next.LengthSquared() > 1e-10f)
                angle = MathF.Atan2(Vector3.Dot(normal, Vector3.Cross(first, next)), Vector3.Dot(first, next));
        }
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(normal, angle) * rotation);

        bool TryPlanePoint(Vector2 pixel, out Vector3 point)
        {
            point = default;
            float x = pixel.X * 2 / width - 1, y = 1 - pixel.Y * 2 / height;
            var near = Vector4.Transform(new Vector4(x, y, 0, 1), inverse);
            var far = Vector4.Transform(new Vector4(x, y, 1, 1), inverse);
            if (MathF.Abs(near.W) < 1e-8f || MathF.Abs(far.W) < 1e-8f) return false;
            var origin = new Vector3(near.X, near.Y, near.Z) / near.W;
            var direction = new Vector3(far.X, far.Y, far.Z) / far.W - origin;
            float denominator = Vector3.Dot(normal, direction);
            if (!float.IsFinite(denominator) || MathF.Abs(denominator) < 1e-7f) return false;
            point = origin + direction * (Vector3.Dot(normal, pivot - origin) / denominator);
            return Finite(point);
        }
    }

    public static LightViewportHit? HitTest(SceneLightingSnapshot lights, Guid? selected, Vector2 mouse,
        SceneCamera camera, int width, int height, float size, bool rotate = false)
    {
        Guid? nearest = null;
        float distance = 144, depth = float.PositiveInfinity;
        foreach (var light in lights.Lights)
        {
            if (!TryProject(light.Position, camera, width, height, out var center)) continue;
            float candidate = Vector2.DistanceSquared(mouse, center), candidateDepth = Vector3.DistanceSquared(light.Position, camera.Eye);
            if (candidate <= 144 && (nearest is null || candidate < distance || candidate == distance
                && (candidateDepth < depth || candidateDepth == depth && light.Id.CompareTo(nearest.Value) < 0)))
            { nearest = light.Id; distance = candidate; depth = candidateDepth; }
        }
        // The icon always moves the light, including when rotation rings are active.
        if (nearest is { } hit) return new(hit, LightDragAxis.Free);
        if (selected is not { } id || lights.Lights.FirstOrDefault(light => light.Id == id) is not { } active) return null;
        LightDragAxis? bestAxis = null;
        distance = 7;
        foreach (var axis in Axes)
        {
            float candidate = HandleDistance(active.Position, axis, size, mouse, camera, width, height, rotate);
            if (candidate < distance) { distance = candidate; bestAxis = axis; }
        }
        return bestAxis is { } chosen ? new(id, chosen) : null;
    }

    public static ViewportOverlaySnapshot CreateOverlay(SceneLightingSnapshot lights, Guid? selected,
        SceneCamera camera, int width, int height, float size, bool rotate = false)
    {
        var lines = new List<ViewportOverlayLine>();
        foreach (var light in lights.Lights)
        {
            if (!TryProject(light.Position, camera, width, height, out var center)) continue;
            bool active = light.Id == selected;
            // Helper contrast must not depend on lamp RGB: a white lamp still needs a
            // readable icon over the default white world, and a black lamp over black UI.
            var border = new Vector4(.10f, .16f, .21f, 1);
            var color = active ? new Vector4(.91f, .64f, .23f, 1) : light.Enabled
                ? new Vector4(.79f, .87f, .93f, 1) : new Vector4(.53f, .58f, .62f, 1);
            int radius = active ? 8 : 6;
            var corners = new[] { center - new Vector2(radius, 0), center - new Vector2(0, radius),
                center + new Vector2(radius, 0), center + new Vector2(0, radius) };
            for (int edge = 0; edge < corners.Length; edge++)
                Add(corners[edge], corners[(edge + 1) % corners.Length], border, 4.5f);
            for (int edge = 0; edge < corners.Length; edge++)
                Add(corners[edge], corners[(edge + 1) % corners.Length], color, 1.5f);
            if (!active) continue;
            foreach (var axis in Axes)
            {
                var axisColor = AxisColor(axis);
                if (rotate)
                {
                    foreach (var (a, b) in RingSegmentsFor(light.Position, axis, size)) WorldLine(a, b, axisColor);
                }
                else if (TryProject(light.Position + Axis(axis) * size, camera, width, height, out var end))
                {
                    Add(center, end, axisColor);
                    var direction = end - center;
                    if (direction.LengthSquared() < 4) continue;
                    direction = Vector2.Normalize(direction);
                    var side = new Vector2(-direction.Y, direction.X) * 4;
                    Add(end, end - direction * 8 + side, axisColor);
                    Add(end, end - direction * 8 - side, axisColor);
                }
            }
            if (light.Kind != SceneLightKind.Point) WorldLine(light.Position, light.Position + light.Direction * size * 1.4f, new(1, 1, .25f, 1));
        }
        return new(lines);

        void WorldLine(Vector3 a, Vector3 b, Vector4 color)
        {
            if (TryProject(a, camera, width, height, out var start) && TryProject(b, camera, width, height, out var end)) Add(start, end, color);
        }
        void Add(Vector2 from, Vector2 to, Vector4 color, float strokeWidth = 2.5f) =>
            lines.Add(new(new(from.X / width, from.Y / height), new(to.X / width, to.Y / height), color, Width: strokeWidth));
    }

    private static float HandleDistance(Vector3 origin, LightDragAxis axis, float size, Vector2 mouse, SceneCamera camera, int width, int height, bool rotate)
    {
        if (!rotate) return Segment(origin, origin + Axis(axis) * size);
        float nearest = float.PositiveInfinity;
        foreach (var (a, b) in RingSegmentsFor(origin, axis, size)) nearest = Math.Min(nearest, Segment(a, b));
        return nearest;

        float Segment(Vector3 a, Vector3 b) => TryProject(a, camera, width, height, out var start)
            && TryProject(b, camera, width, height, out var end) ? Distance(mouse, start, end) : float.PositiveInfinity;
    }

    private static IEnumerable<(Vector3 A, Vector3 B)> RingSegmentsFor(Vector3 origin, LightDragAxis axis, float size)
    {
        var (a, b) = axis switch
        {
            LightDragAxis.X => (Vector3.UnitY, Vector3.UnitZ),
            LightDragAxis.Y => (Vector3.UnitZ, Vector3.UnitX),
            _ => (Vector3.UnitX, Vector3.UnitY),
        };
        for (int step = 0; step < RingSegments; step++)
        {
            float first = MathF.Tau * step / RingSegments, next = MathF.Tau * (step + 1) / RingSegments;
            yield return (origin + (a * MathF.Cos(first) + b * MathF.Sin(first)) * size,
                origin + (a * MathF.Cos(next) + b * MathF.Sin(next)) * size);
        }
    }
    private static Vector3 Axis(LightDragAxis axis) => axis switch { LightDragAxis.X => Vector3.UnitX, LightDragAxis.Y => Vector3.UnitY, LightDragAxis.Z => Vector3.UnitZ, _ => Vector3.Zero };
    private static Vector4 AxisColor(LightDragAxis axis) => axis switch { LightDragAxis.X => new(.95f, .28f, .24f, 1), LightDragAxis.Y => new(.3f, .8f, .4f, 1), _ => new(.32f, .55f, 1, 1) };
    private static float Distance(Vector2 p, Vector2 a, Vector2 b)
    { var d = b - a; float n = d.LengthSquared(); return Vector2.Distance(p, n < 1e-6f ? a : a + d * Math.Clamp(Vector2.Dot(p - a, d) / n, 0, 1)); }
    private static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
