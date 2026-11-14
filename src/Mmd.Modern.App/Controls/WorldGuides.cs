using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Mmd.Modern.Core;

namespace Mmd.Modern.App.Controls;

/// <summary>
/// Camera-projected editor guides. Native previews retain their clipped device depth
/// so scene geometry hides them; selection handles use the separate always-visible overlay.
/// </summary>
public sealed class WorldGuides : Control
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<WorldGuides, bool>(nameof(IsActive), false);

    public static readonly StyledProperty<SceneCamera?> CameraProperty =
        AvaloniaProperty.Register<WorldGuides, SceneCamera?>(nameof(Camera));

    private static readonly ViewportOverlaySnapshot EmptyOverlay = new([]);

    static WorldGuides()
    {
        AffectsRender<WorldGuides>(IsActiveProperty, CameraProperty, BoundsProperty);
    }

    public WorldGuides() => IsHitTestVisible = false;

    /// <summary>Inactive guides draw nothing over the white viewport.</summary>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>No camera means no guides; never substitutes a screen-space grid.</summary>
    public SceneCamera? Camera
    {
        get => GetValue(CameraProperty);
        set => SetValue(CameraProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!IsActive || Camera is not { } camera ||
            !double.IsFinite(Bounds.Width) || !double.IsFinite(Bounds.Height) ||
            Bounds.Width < 1 || Bounds.Height < 1 ||
            Bounds.Width > int.MaxValue || Bounds.Height > int.MaxValue)
            return;

        var overlay = BuildOverlay(camera, (int)Bounds.Width, (int)Bounds.Height);
        using (context.PushClip(new Rect(Bounds.Size)))
        {
            foreach (var line in overlay.Lines)
            {
                var color = line.Color;
                var brush = new SolidColorBrush(Color.FromArgb(ToByte(color.W),
                    ToByte(color.X), ToByte(color.Y), ToByte(color.Z)));
                context.DrawLine(new Pen(brush, 1),
                    new Point(line.Start.X * Bounds.Width, line.Start.Y * Bounds.Height),
                    new Point(line.End.X * Bounds.Width, line.End.Y * Bounds.Height));
            }
        }
    }

    /// <summary>
    /// Builds at most 45 lines using MMD's five-unit, 100-unit-wide ground grid
    /// and directional world axes. Clip in
    /// homogeneous space before dividing by W, including near-plane crossings.
    /// Coordinates are normalized for the existing ViewportOverlaySnapshot API.
    /// </summary>
    public static ViewportOverlaySnapshot BuildOverlay(SceneCamera? camera, int width, int height)
    {
        if (camera is null || width <= 0 || height <= 0)
            return EmptyOverlay;

        var transform = (camera with { ProjectionJitterPixels = Vector2.Zero }).ViewProjection(width, height);
        var lines = new List<ViewportOverlayLine>(45);
        const float extent = 50;
        const float ground = .1f;
        const float axisLength = 65;
        var origin = new Vector3(0, ground, 0);
        var gridColor = new Vector4(180 / 255f, 180 / 255f, 180 / 255f, 1);
        for (var index = -10; index <= 10; index++)
        {
            if (index == 0) continue; // The colored axes occupy the origin lines.
            var offset = index * 5f;
            Add(new(-extent, ground, offset), new(extent, ground, offset), gridColor);
            Add(new(offset, ground, -extent), new(offset, ground, extent), gridColor);
        }
        Add(origin, new(0, ground, extent), gridColor);
        Add(origin, new(-extent, ground, 0), gridColor);
        Add(origin, new(axisLength, ground, 0), new(1, 0, 0, 1));
        Add(origin, new(0, axisLength, 0), new(0, 1, 0, 1));
        Add(origin, new(0, ground, -axisLength), new(0, 0, 1, 1));
        return new(lines);

        void Add(Vector3 start, Vector3 end, Vector4 color)
        {
            var a = Vector4.Transform(new Vector4(start, 1), transform);
            var b = Vector4.Transform(new Vector4(end, 1), transform);
            if (!ClipSegment(ref a, ref b)) return;
            var from = new Vector2(a.X / a.W * .5f + .5f, .5f - a.Y / a.W * .5f);
            var to = new Vector2(b.X / b.W * .5f + .5f, .5f - b.Y / b.W * .5f);
            lines.Add(new(Vector2.Clamp(from, Vector2.Zero, Vector2.One),
                Vector2.Clamp(to, Vector2.Zero, Vector2.One), color,
                DeviceDepth: new(Math.Clamp(a.Z / a.W, 0, 1), Math.Clamp(b.Z / b.W, 0, 1))));
        }
    }

    private static bool ClipSegment(ref Vector4 start, ref Vector4 end)
    {
        if (!Finite(start) || !Finite(end)) return false;
        float enter = 0, leave = 1;
        for (var plane = 0; plane < 7; plane++)
        {
            var a = Distance(start, plane);
            var b = Distance(end, plane);
            if (!float.IsFinite(a) || !float.IsFinite(b) || (a < 0 && b < 0))
                return false;
            if (a >= 0 && b >= 0) continue;
            var t = a / (a - b);
            if (a < 0) enter = MathF.Max(enter, t);
            else leave = MathF.Min(leave, t);
            if (enter > leave) return false;
        }
        var original = start;
        start = Vector4.Lerp(original, end, enter);
        end = Vector4.Lerp(original, end, leave);
        return Finite(start) && Finite(end) && start.W > 0 && end.W > 0;
    }

    private static float Distance(Vector4 point, int plane) => plane switch
    {
        0 => point.Z,
        1 => point.W - point.Z,
        2 => point.W + point.X,
        3 => point.W - point.X,
        4 => point.W + point.Y,
        5 => point.W - point.Y,
        _ => point.W - 1e-6f
    };

    private static bool Finite(Vector4 value) => float.IsFinite(value.X) &&
        float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static byte ToByte(float value) => (byte)MathF.Round(Math.Clamp(value, 0, 1) * 255);
}
