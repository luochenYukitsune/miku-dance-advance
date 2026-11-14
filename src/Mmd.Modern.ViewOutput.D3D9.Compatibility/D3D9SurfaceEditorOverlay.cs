using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D9.Compatibility;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

/// <summary>Separate editor transport. Scene capture/FX packets retain their original strict scope.</summary>
internal static class D3D9SurfaceEditorOverlay
{
    internal static OriginalFrameScene SceneWithoutOverlay(OriginalFrameScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return scene.EditorOverlay is null ? scene : new(scene.Surface, scene.Outline, scene.OutlineOptions,
            scene.Options, scene.Materials, scene.Lighting);
    }

    internal static PreviewFrame FrameWithoutOverlay(PreviewFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return frame.Scene.EditorOverlay is null ? frame : frame with { Scene = SceneWithoutOverlay(frame.Scene) };
    }

    internal static D3D9EditorOverlayPrimitive[] Freeze(PreviewFrame frame, bool classicPasses)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Scene.EditorOverlay is not { Lines.Count: > 0 } overlay) return [];
        var camera = D3D9SurfaceFrameEncoder.ResolveCamera(D3D9SurfaceFrameInput.FromPreview(frame));
        bool remapDepth = classicPasses && camera.VerticalFovDegrees is not null;
        var primitives = new List<D3D9EditorOverlayPrimitive>(overlay.Lines.Count);
        foreach (var line in overlay.Lines)
        {
            var start = line.Start; var end = line.End; var depth = line.DeviceDepth;
            if (remapDepth && depth is { } clipped)
            {
                double a = RemapDepth(clipped.X, camera), b = RemapDepth(clipped.Y, camera);
                if (!ClipDepth(ref start, ref end, ref a, ref b)) continue;
                depth = new((float)a, (float)b);
            }
            primitives.Add(new(start, end, line.Color, line.Width,
                (D3D9EditorOverlayShape)line.Shape, line.Radius, depth));
        }
        return primitives.ToArray();
    }

    // Device depth is affine in reciprocal view distance for both D3D handednesses.
    // XY framing is unchanged, but original MMD's perspective uses near=1/far=100000.
    private static double RemapDepth(float depth, SceneCamera camera)
    {
        double near = camera.Near, far = camera.Far;
        double a = far / (far - near), b = -far * near / (far - near);
        const double classicA = 100000.0 / 99999, classicB = -100000.0 / 99999;
        return classicA + classicB * (depth - a) / b;
    }

    private static bool ClipDepth(ref Vector2 start, ref Vector2 end, ref double a, ref double b)
    {
        if (!double.IsFinite(a) || !double.IsFinite(b) || a < 0 && b < 0 || a > 1 && b > 1) return false;
        var originalStart = start; var originalEnd = end;
        double from = 0, to = 1, delta = b - a;
        if (a < 0) from = -a / delta;
        else if (a > 1) from = (1 - a) / delta;
        if (b < 0) to = -a / delta;
        else if (b > 1) to = (1 - a) / delta;
        if (from > to) return false;
        start = Vector2.Lerp(originalStart, originalEnd, (float)from);
        end = Vector2.Lerp(originalStart, originalEnd, (float)to);
        double originalA = a;
        a = Math.Clamp(originalA + delta * from, 0, 1);
        b = Math.Clamp(originalA + delta * to, 0, 1);
        return true;
    }
}
