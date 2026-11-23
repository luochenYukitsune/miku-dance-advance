using System.Numerics;
using System.Runtime.CompilerServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.ViewOutput;

namespace Mmd.Modern.App;

/// <summary>The VMD light has a direction and no position. Draw its source around the camera target.
/// A drag previews a pose locally and publishes a single unregistered scene draft on release.</summary>
internal sealed class EditorMainLightViewport(EditorSceneInspectorViewModel model, Func<bool> begin, Action redraw)
{
    private sealed record Handle(SceneCamera Camera, int Width, int Height, long Revision, Vector2 Source, Vector2 Tip,
        float Radius, Vector3 Right, Vector3 Up, Vector3 Front);
    private readonly ConditionalWeakTable<PreviewFrame, Handle> awaiting = new();
    private Handle? presented;
    private Handle? gesture;
    private Vector2 start;
    private Vector3 direction;
    internal bool IsDragging => gesture is not null;
    internal Vector3? PreviewDirection => IsDragging ? direction : null;
    internal void ClearPresentation()
    {
        presented = null;
        awaiting.Clear();
    }

    internal PreviewFrame Decorate(PreviewFrame frame, SceneCamera camera)
    {
        var pose = model.LightPose;
        if (gesture is { } active)
        {
            if (active.Revision == model.PoseRevision && active.Camera == camera &&
                active.Width == frame.Width && active.Height == frame.Height)
                frame = frame with { Light = pose with { Direction = direction } };
            else Cancel();
        }
        var ray = frame.Light?.Direction ?? pose.Direction;
        var length = ray.Length();
        if (!float.IsFinite(length) || length < 1e-6f) return frame;
        var front = Vector3.Normalize(camera.Eye - camera.Target);
        var right = Vector3.Normalize(camera.LeftHanded ? Vector3.Cross(camera.Up, -front) : Vector3.Cross(-front, camera.Up));
        var up = Vector3.Normalize(camera.LeftHanded ? Vector3.Cross(-front, right) : Vector3.Cross(front, right));
        var size = new Vector2(frame.Width, frame.Height);
        var center = size * .5f;
        float radius = Math.Clamp(Math.Min(frame.Width, frame.Height) * .23f, 1f, 140f);
        var source = center + radius * new Vector2(-Vector3.Dot(ray, right), Vector3.Dot(ray, up)) / length;
        var tip = Vector2.Lerp(source, center, .78f);
        var handle = new Handle(camera, frame.Width, frame.Height, model.PoseRevision, source, tip, radius, right, up, front);
        var outline = new Vector4(.18f, .12f, .04f, 1);
        var gold = new Vector4(1, .8f, .25f, 1);
        var lines = new List<ViewportOverlayLine>();
        void Stroke(Vector2 a, Vector2 b, float width, ViewportOverlayShape shape = ViewportOverlayShape.Segment, float shapeRadius = 5)
        {
            lines.Add(new(a / size, b / size, outline, width + 2, shape, shapeRadius + 1));
            lines.Add(new(a / size, b / size, gold, width, shape, shapeRadius));
        }
        if (Vector2.DistanceSquared(source, tip) > 144)
        {
            var heading = Vector2.Normalize(tip - source);
            Stroke(source + heading * 9, tip, 2);
            var side = new Vector2(-heading.Y, heading.X) * 4;
            Stroke(tip, tip - heading * 8 + side, 2);
            Stroke(tip, tip - heading * 8 - side, 2);
        }
        Stroke(source, source, 2, ViewportOverlayShape.Circle, 7);
        for (int i = 0; i < 8; ++i)
        {
            var unit = new Vector2(MathF.Cos(i * MathF.PI / 4), MathF.Sin(i * MathF.PI / 4));
            Stroke(source + unit * 10, source + unit * 14, 2);
        }
        var scene = frame.Scene;
        var existing = scene.EditorOverlay?.Lines ?? [];
        var overlay = new ViewportOverlaySnapshot(existing.Take(ViewportOverlaySnapshot.MaxLines - lines.Count).Concat(lines));
        var result = frame with { Scene = new(scene.Surface, scene.Outline, scene.OutlineOptions, scene.Options,
            scene.Materials, scene.Lighting, overlay) };
        awaiting.Add(result, handle);
        return result;
    }

    internal void FramePresented(PreviewFrame frame) =>
        presented = awaiting.TryGetValue(frame, out var pose) ? pose : null;

    internal bool TryBegin(Vector2 pointer, SceneCamera camera, int width, int height)
    {
        if (gesture is not null || presented is not { } hit || hit.Revision != model.PoseRevision ||
            hit.Camera != camera || hit.Width != width || hit.Height != height) return false;
        var segment = hit.Tip - hit.Source;
        var along = segment.LengthSquared() > 144 ? Math.Clamp(Vector2.Dot(pointer - hit.Source, segment) / segment.LengthSquared(), 0, 1) : 0;
        if (Vector2.DistanceSquared(pointer, hit.Source) > 19 * 19 &&
            Vector2.DistanceSquared(pointer, hit.Source + segment * along) > 10 * 10) return false;
        if (!begin() || hit.Revision != model.PoseRevision) return true;
        gesture = hit;
        start = pointer;
        direction = model.LightPose.Direction;
        return true;
    }

    internal void Move(Vector2 pointer)
    {
        if (gesture is not { } handle) return;
        if (handle.Revision != model.PoseRevision) { Cancel(); return; }
        var source = -model.LightPose.Direction;
        var delta = (pointer - start) / handle.Radius;
        float sign = Vector3.Dot(source, handle.Front) < 0 ? -1 : 1;
        // Flip the axes on the far hemisphere so that grabbing the source always pulls it toward the cursor.
        float handedness = Vector3.Dot(Vector3.Cross(handle.Right, handle.Up), handle.Front) < 0 ? -1 : 1;
        var rotation = Quaternion.CreateFromAxisAngle(handle.Up, delta.X * sign * handedness)
            * Quaternion.CreateFromAxisAngle(handle.Right, delta.Y * sign * handedness);
        var next = Vector3.Transform(-source, rotation);
        if (next == direction) return;
        direction = next;
        redraw();
    }

    internal void End(Vector2 pointer)
    {
        Move(pointer);
        if (gesture is null) return;
        gesture = null;
        model.LightDirectionVector = direction;
        redraw();
    }

    internal void Cancel()
    {
        if (gesture is null) return;
        gesture = null;
        redraw();
    }
}
