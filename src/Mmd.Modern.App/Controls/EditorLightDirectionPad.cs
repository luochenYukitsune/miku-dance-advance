using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Automation;

namespace Mmd.Modern.App.Controls;

/// <summary>A small direct-manipulation control for MMD's positionless main-light direction.</summary>
public sealed class EditorLightDirectionPad : Control
{
    public static readonly StyledProperty<Vector3> DirectionProperty =
        AvaloniaProperty.Register<EditorLightDirectionPad, Vector3>(nameof(Direction), new(-.5f, -1, .5f),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    private Point? origin;
    private Vector3 before;
    public Vector3 Direction { get => GetValue(DirectionProperty); set => SetValue(DirectionProperty, value); }
    static EditorLightDirectionPad() => AffectsRender<EditorLightDirectionPad>(DirectionProperty);
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty) InvalidateVisual();
    }
    public EditorLightDirectionPad()
    {
        Focusable = true; MinHeight = 112; Cursor = new Cursor(StandardCursorType.Cross);
        AutomationProperties.SetName(this, EditorLocalization.Text("主光方向"));
        EditorLocalization.Observe(this, static pad => AutomationProperties.SetName(pad, EditorLocalization.Text("主光方向")));
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        double radius = Math.Max(1, Math.Min(Bounds.Width, Bounds.Height) / 2 - 16);
        var muted = new Pen(Brush("EditorBorder", Brushes.SlateGray), 1);
        var foreground = Brush("EditorText", Brushes.White);
        context.DrawRectangle(Brush("EditorInputSurface", Brushes.Transparent), null, new Rect(Bounds.Size));
        context.DrawEllipse(null, muted, center, radius, radius);
        context.DrawLine(muted, new(center.X - radius, center.Y), new(center.X + radius, center.Y));
        context.DrawLine(muted, new(center.X, center.Y - radius), new(center.X, center.Y + radius));
        var direction = Direction;
        if (!Finite(direction) || direction.LengthSquared() < 1e-12f) return;
        direction = Vector3.Normalize(direction);
        var point = new Point(center.X + direction.X * radius, center.Y - direction.Y * radius);
        context.DrawLine(new Pen(foreground, 2, direction.Z >= 0 ? null : DashStyle.Dash), center, point);
        var accent = Brush("MdaAccent", foreground);
        context.DrawEllipse(direction.Z >= 0 ? accent : null, new Pen(accent, 2), point, 5, 5);
        if (IsFocused) context.DrawRectangle(null, new Pen(foreground, 1), new Rect(Bounds.Size).Deflate(1));
    }
    protected override void OnPointerPressed(PointerPressedEventArgs args)
    {
        base.OnPointerPressed(args);
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus(); origin = args.GetPosition(this); before = Direction; args.Pointer.Capture(this); args.Handled = true;
        PointAt(origin.Value, args.KeyModifiers);
    }
    protected override void OnPointerMoved(PointerEventArgs args)
    {
        base.OnPointerMoved(args);
        if (origin is null) return;
        PointAt(args.GetPosition(this), args.KeyModifiers);
        args.Handled = true;
    }
    /// <summary>Absolute aim: the light travels towards the pressed point of the front view. The
    /// hemisphere (towards or away from the camera) is kept from the current direction; Shift snaps
    /// the result to 15° steps of azimuth and elevation.</summary>
    private void PointAt(Point position, KeyModifiers modifiers)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        double radius = Math.Max(1, Math.Min(Bounds.Width, Bounds.Height) / 2 - 16);
        double x = (position.X - center.X) / radius, y = -(position.Y - center.Y) / radius;
        double planar = x * x + y * y;
        if (planar > 1) { double scale = 1 / Math.Sqrt(planar); x *= scale; y *= scale; planar = 1; }
        float zSign = before.Z < 0 ? -1 : 1;
        var unit = new Vector3((float)x, (float)y, zSign * (float)Math.Sqrt(Math.Max(0, 1 - planar)));
        if (unit.LengthSquared() < 1e-12f) return;
        float length = Finite(before) && before.LengthSquared() > 1e-12f ? before.Length() : 1;
        var direction = Vector3.Normalize(unit) * length;
        if (modifiers.HasFlag(KeyModifiers.Shift)) direction = EditorLightAngles.Snap(direction, 15);
        SetCurrentValue(DirectionProperty, direction);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs args)
    { base.OnPointerReleased(args); origin = null; args.Pointer.Capture(null); args.Handled = true; }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs args)
    { base.OnPointerCaptureLost(args); if (origin is not null) SetCurrentValue(DirectionProperty, before); origin = null; }
    protected override void OnKeyDown(KeyEventArgs args)
    {
        base.OnKeyDown(args);
        if (args.Key == Key.Escape && origin is not null)
        { SetCurrentValue(DirectionProperty, before); origin = null; args.Handled = true; return; }
        float step = args.KeyModifiers.HasFlag(KeyModifiers.Shift) ? .15f : .03f;
        var rotation = args.Key switch
        {
            Key.Left => Quaternion.CreateFromAxisAngle(Vector3.UnitY, -step),
            Key.Right => Quaternion.CreateFromAxisAngle(Vector3.UnitY, step),
            Key.Up => Quaternion.CreateFromAxisAngle(Vector3.UnitX, -step),
            Key.Down => Quaternion.CreateFromAxisAngle(Vector3.UnitX, step), _ => Quaternion.Identity
        };
        if (rotation == Quaternion.Identity) return;
        SetCurrentValue(DirectionProperty, Vector3.Transform(Direction, rotation)); args.Handled = true;
    }
    private IBrush Brush(string key, IBrush fallback) => this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;
    private static bool Finite(Vector3 vector) => float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
}
