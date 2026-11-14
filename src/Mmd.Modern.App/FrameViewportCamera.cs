using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.App;

public sealed class FrameViewportCamera
{
    private double yaw, pitch, zoom = 1;
    public Vector3 Center { get; }
    public Vector3 Extent { get; }
    public float Span { get; }
    /// <summary>Screen-right/up offset in multiples of the scene span; independent of orbit orientation.</summary>
    public Vector2 Pan {get;set;}
    public double YawDegrees { get => yaw; set => yaw = double.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value)); }
    public double PitchDegrees { get => pitch; set => pitch = Math.Clamp(double.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value)), -85, 85); }
    public double Zoom { get => zoom; set => zoom = Math.Clamp(double.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value)), .15, 8); }
    public FrameViewportCamera(Vector3 min, Vector3 max)
    {
        Center = (min + max) * .5f; Extent = max - min;
        Span = Math.Max(.1f, Math.Max(Extent.X, Math.Max(Extent.Y, Extent.Z)));
    }
    public SceneCamera CreateCamera(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        float y = (float)(yaw * Math.PI / 180), p = (float)(pitch * Math.PI / 180);
        var direction = new Vector3(MathF.Sin(y) * MathF.Cos(p), MathF.Sin(p), -MathF.Cos(y) * MathF.Cos(p));
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, direction));
        var up = Vector3.Cross(direction, right);
        float projectedWidth = MathF.Abs(right.X) * Extent.X + MathF.Abs(right.Y) * Extent.Y + MathF.Abs(right.Z) * Extent.Z;
        float projectedHeight = MathF.Abs(up.X) * Extent.X + MathF.Abs(up.Y) * Extent.Y + MathF.Abs(up.Z) * Extent.Z;
        float fit = Math.Max(.1f, Math.Max(projectedHeight, projectedWidth * height / width)) * 1.2f * (float)zoom;
        if(!float.IsFinite(Pan.X)||!float.IsFinite(Pan.Y))throw new ArgumentOutOfRangeException(nameof(Pan));
        var target=Center+(right*Pan.X+up*Pan.Y)*Span;
        return new(target + direction * (Span * 2), target, Vector3.UnitY, fit, .01f, Span * 6);
    }
}
