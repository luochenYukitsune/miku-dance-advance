using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.App;

public enum LightDragAxis { Free, X, Y, Z }
/// <summary>Target hits drag the primary light's aim point; the light stays in place and turns toward it.</summary>
public sealed record LightViewportHit(Guid LightId, LightDragAxis Axis, bool Target = false);

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
        SceneCamera camera, int width, int height, float size, bool rotate = false, Vector3? selectionPivot = null,
        IReadOnlyCollection<Guid>? selectedIds = null)
    {
        if (selected is { } aimed && (selectedIds is null || selectedIds.Count == 1)
            && lights.Lights.FirstOrDefault(light => light.Id == aimed) is { Kind: not SceneLightKind.Point } targetLight
            && TryProject(targetLight.Target, camera, width, height, out var targetPixel)
            && Vector2.DistanceSquared(mouse, targetPixel) <= 81)
            return new(aimed, LightDragAxis.Free, Target: true);
        Guid? nearest = null;
        float distance = 144, depth = float.PositiveInfinity;
        foreach (var light in lights.Lights)
        {
            if (!TryProject(light.Position, camera, width, height, out var center)) continue;
            float candidate = Vector2.DistanceSquared(mouse, center), candidateDepth = Vector3.DistanceSquared(light.Position, camera.Eye);
            bool candidateSelected = selectedIds?.Contains(light.Id) ?? light.Id == selected;
            bool previousActive = nearest is { } previous && (selectedIds?.Contains(previous) ?? previous == selected);
            if (candidate <= 144 && (nearest is null || candidate < distance || candidate == distance
                && (candidateDepth < depth || candidateDepth == depth && (candidateSelected && !previousActive
                    || candidateSelected == previousActive && light.Id.CompareTo(nearest.Value) < 0))))
            { nearest = light.Id; distance = candidate; depth = candidateDepth; }
        }
        // The icon always moves the light, including when rotation rings are active.
        if (nearest is { } hit) return new(hit, LightDragAxis.Free);
        if (selected is not { } id || lights.Lights.FirstOrDefault(light => light.Id == id) is not { } active) return null;
        LightDragAxis? bestAxis = null;
        distance = 7;
        foreach (var axis in Axes)
        {
            float candidate = HandleDistance(selectionPivot ?? active.Position, axis, size, mouse, camera, width, height, rotate);
            if (candidate < distance) { distance = candidate; bestAxis = axis; }
        }
        return bestAxis is { } chosen ? new(id, chosen) : null;
    }

    public static ViewportOverlaySnapshot CreateOverlay(SceneLightingSnapshot lights, Guid? selected,
        SceneCamera camera, int width, int height, float size, bool rotate = false,
        IReadOnlyCollection<Guid>? selectedIds = null, Vector3? selectionPivot = null, bool showDetails = true)
    {
        var lines = new List<ViewportOverlayLine>();
        foreach (var light in lights.Lights)
        {
            if (!TryProject(light.Position, camera, width, height, out var center)) continue;
            bool active = selectedIds?.Contains(light.Id) ?? light.Id == selected;
            // Helper contrast must not depend on lamp RGB: a white lamp still needs a
            // readable icon over the default white world, and a black lamp over black UI.
            var border = new Vector4(.10f, .16f, .21f, 1);
            var color = active ? new Vector4(.91f, .64f, .23f, 1) : light.Enabled
                ? new Vector4(.79f, .87f, .93f, 1) : new Vector4(.53f, .58f, .62f, 1);
            int radius = active ? 8 : 6;
            // Native square primitives keep all 128 authoring markers within the existing
            // overlay constant budget, leaving space for selection rings and scene guides.
            var marker = new Vector2(center.X / width, center.Y / height);
            lines.Add(new(marker, marker, border, Width: 4.5f, Shape: ViewportOverlayShape.Square, Radius: radius));
            lines.Add(new(marker, marker, color, Width: 1.5f, Shape: ViewportOverlayShape.Square, Radius: radius));
            if (active && light.Kind != SceneLightKind.Point)
            {
                // The primary light shows where it is actually aimed; other selected lights keep a short pointer.
                bool primaryLight = light.Id == selected;
                WorldLine(light.Position, primaryLight ? light.Target : light.Position + light.Direction * size * 1.4f, new(1, 1, .25f, 1),
                    primaryLight ? 1.5f : 2.5f);
                if (primaryLight && TryProject(light.Target, camera, width, height, out var aim))
                {
                    var target = new Vector2(aim.X / width, aim.Y / height);
                    lines.Add(new(target, target, border, Width: 4.5f, Shape: ViewportOverlayShape.Square, Radius: 6));
                    lines.Add(new(target, target, new(1, 1, .25f, 1), Width: 1.5f, Shape: ViewportOverlayShape.Square, Radius: 6));
                }
            }
            // Detailed coverage belongs only to the primary selection. Even 128 selected
            // authoring lights leave room for shared handles and scene guides in the 1024 budget.
            if (showDetails && light.Id == selected)
                foreach (var (start, end) in DetailSegments(light)) WorldLine(start, end, new(.91f, .64f, .23f, .85f), 1.25f);
        }
        if (lights.Lights.FirstOrDefault(light => light.Id == selected) is { } primary
            && TryProject(selectionPivot ?? primary.Position, camera, width, height, out var handleCenter))
        {
            var pivot = selectionPivot ?? primary.Position;
            foreach (var axis in Axes)
            {
                var axisColor = AxisColor(axis);
                if (rotate)
                {
                    foreach (var (a, b) in RingSegmentsFor(pivot, axis, size)) WorldLine(a, b, axisColor);
                }
                else if (TryProject(pivot + Axis(axis) * size, camera, width, height, out var end))
                {
                    Add(handleCenter, end, axisColor);
                    var direction = end - handleCenter;
                    if (direction.LengthSquared() < 4) continue;
                    direction = Vector2.Normalize(direction);
                    var side = new Vector2(-direction.Y, direction.X) * 4;
                    Add(end, end - direction * 8 + side, axisColor);
                    Add(end, end - direction * 8 - side, axisColor);
                }
            }
        }
        return new(lines);

        void WorldLine(Vector3 a, Vector3 b, Vector4 color, float strokeWidth = 2.5f)
        {
            if (TryProjectSegment(a, b, camera, width, height, out var start, out var end)) Add(start, end, color, strokeWidth);
        }
        void Add(Vector2 from, Vector2 to, Vector4 color, float strokeWidth = 2.5f) =>
            lines.Add(new(new(from.X / width, from.Y / height), new(to.X / width, to.Y / height), color, Width: strokeWidth));
    }

    /// <summary>Actual scene-unit coverage, independent of the fixed screen-size editing handles. Range is where
    /// the shared falloff reaches zero (<see cref="SceneLightFalloff"/>); a spot also marks its beam at the aim distance.</summary>
    internal static IEnumerable<(Vector3 Start, Vector3 End)> DetailSegments(NativeSceneLight light)
    {
        if (light.Kind == SceneLightKind.Point)
        {
            foreach (var axis in Axes)
                foreach (var segment in RingSegmentsFor(light.Position, axis, light.Range)) yield return segment;
        }
        else if (light.Kind == SceneLightKind.Spot)
        {
            // Range attenuates by radial distance, so the cone edge ends on the range
            // sphere. sin/cos also stay finite for the supported PI/2 half-angle.
            float radius = light.Range * MathF.Sin(light.OuterConeRadians);
            var centre = light.Position + light.Direction * (light.Range * MathF.Cos(light.OuterConeRadians));
            var right = Vector3.Transform(Vector3.UnitX, light.Rotation);
            var up = Vector3.Transform(Vector3.UnitY, light.Rotation);
            // The beam ring sits at the aim, clamped onto the cone inside the range sphere.
            float cosine = MathF.Max(MathF.Cos(light.OuterConeRadians), 0);
            float aim = MathF.Min(light.EffectiveAimDistance, light.Range * cosine);
            float beam = aim * MathF.Sin(light.OuterConeRadians) / MathF.Max(cosine, 1e-6f);
            var beamCentre = light.Position + light.Direction * aim;
            for (int step = 0; step < RingSegments; step++)
            {
                float first = MathF.Tau * step / RingSegments, next = MathF.Tau * (step + 1) / RingSegments;
                var a = centre + (right * MathF.Cos(first) + up * MathF.Sin(first)) * radius;
                var b = centre + (right * MathF.Cos(next) + up * MathF.Sin(next)) * radius;
                yield return (a, b);
                if (step % (RingSegments / 4) == 0) yield return (light.Position, a);
                if (step % 2 == 0)
                    yield return (beamCentre + (right * MathF.Cos(first) + up * MathF.Sin(first)) * beam,
                        beamCentre + (right * MathF.Cos(next) + up * MathF.Sin(next)) * beam);
            }
        }
        else if (light.Kind == SceneLightKind.Area && light.AreaSize is { } area)
        {
            var half = area * .5f;
            var corners = new[] { new Vector3(-half.X, -half.Y, 0), new Vector3(half.X, -half.Y, 0),
                new Vector3(half.X, half.Y, 0), new Vector3(-half.X, half.Y, 0) };
            for (int index = 0; index < corners.Length; index++)
                yield return (World(corners[index]), World(corners[(index + 1) % corners.Length]));
            Vector3 World(Vector3 local) => light.Position + Vector3.Transform(local, light.Rotation);
        }
    }

    /// <summary>Clip world coverage to the camera frustum before division. Large ranges often
    /// cross the viewport with both original endpoints outside; those lines must remain visible.</summary>
    internal static bool TryProjectSegment(Vector3 a, Vector3 b, SceneCamera camera, int width, int height,
        out Vector2 start, out Vector2 end)
    {
        start = end = default;
        if (width <= 0 || height <= 0 || !Finite(a) || !Finite(b)) return false;
        var matrix = camera.ViewProjection(width, height);
        var first = Vector4.Transform(new Vector4(a, 1), matrix);
        var last = Vector4.Transform(new Vector4(b, 1), matrix);
        if (!Finite(first) || !Finite(last)) return false;
        double low = 0, high = 1;
        if (!Clip((double)first.W + first.X, (double)last.W + last.X) || !Clip((double)first.W - first.X, (double)last.W - last.X)
            || !Clip((double)first.W + first.Y, (double)last.W + last.Y) || !Clip((double)first.W - first.Y, (double)last.W - last.Y)
            || !Clip(first.Z, last.Z) || !Clip((double)first.W - first.Z, (double)last.W - last.Z)
            || !Clip(first.W - 1e-6f, last.W - 1e-6f)) return false;
        start = Project(low); end = Project(high); return Finite(start) && Finite(end);

        bool Clip(double from, double to)
        {
            if (from < 0 && to < 0) return false;
            if (from >= 0 && to >= 0) return true;
            double crossing = from / (from - to);
            if (from < 0) low = Math.Max(low, crossing); else high = Math.Min(high, crossing);
            return low <= high;
        }
        Vector2 Project(double t)
        {
            // Double interpolation avoids overflow from subtracting opposite finite floats.
            double w = first.W * (1 - t) + last.W * t;
            double x = (first.X * (1 - t) + last.X * t) / w;
            double y = (first.Y * (1 - t) + last.Y * t) / w;
            return new((float)Math.Clamp((x * .5 + .5) * width, 0, width),
                (float)Math.Clamp((.5 - y * .5) * height, 0, height));
        }
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
    private static bool Finite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
