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
    private const float HemisphereEpsilon = 1e-5f;
    private Point? origin;
    private Vector3 before;
    private bool beforeBackHemisphere;
    private bool backHemisphere;
    private bool keyEditing;
    private double lastAzimuth;
    public Vector3 Direction { get => GetValue(DirectionProperty); set => SetValue(DirectionProperty, value); }
    public event EventHandler<System.ComponentModel.CancelEventArgs>? EditStarted;
    public event EventHandler<Vector3>? EditPreview;
    public event EventHandler<Vector3>? EditCompleted;
    public event EventHandler? EditCanceled;
    static EditorLightDirectionPad() => AffectsRender<EditorLightDirectionPad>(DirectionProperty);
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty) InvalidateVisual();
        else if (change.Property == DirectionProperty && change.NewValue is Vector3 vec && Finite(vec) && vec.LengthSquared() > 1e-12f)
        {
            var normalized = Vector3.Normalize(vec);
            if (MathF.Abs(normalized.Z) > HemisphereEpsilon)
                backHemisphere = normalized.Z < 0;
        }
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
        var innerRing = new Pen(Brush("EditorBorder", Brushes.SlateGray), 1, DashStyle.Dot);
        var foreground = Brush("EditorText", Brushes.White);
        context.DrawRectangle(Brush("EditorInputSurface", Brushes.Transparent), null, new Rect(Bounds.Size));
        context.DrawEllipse(null, muted, center, radius, radius);
        context.DrawEllipse(null, innerRing, center, radius * Math.Sqrt(0.5), radius * Math.Sqrt(0.5));
        context.DrawLine(muted, new(center.X - radius, center.Y), new(center.X + radius, center.Y));
        context.DrawLine(muted, new(center.X, center.Y - radius), new(center.X, center.Y + radius));
        context.DrawEllipse(muted.Brush, null, center, 2, 2);
        var direction = Direction;
        if (!Finite(direction) || direction.LengthSquared() < 1e-12f) return;
        var travel = Vector3.Normalize(direction);
        var source = -travel;
        var point = new Point(center.X + source.X * radius, center.Y - source.Y * radius);
        bool isFront = MathF.Abs(travel.Z) > HemisphereEpsilon ? travel.Z > 0 : !backHemisphere;
        context.DrawLine(new Pen(foreground, 2, isFront ? null : DashStyle.Dash), center, point);
        var accent = Brush("MdaAccent", foreground);
        context.DrawEllipse(isFront ? accent : null, new Pen(accent, 2), point, 5, 5);
        if (IsFocused) context.DrawRectangle(null, new Pen(foreground, 1), new Rect(Bounds.Size).Deflate(1));
    }
    protected override void OnPointerPressed(PointerPressedEventArgs args)
    {
        base.OnPointerPressed(args);
        var props = args.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            Focus();
            args.Handled = true;
            FlipHemisphere();
            return;
        }
        if (!props.IsLeftButtonPressed) return;
        CompleteKeyEdit();
        Focus();
        if (!BeginEdit()) return;
        origin = args.GetPosition(this); before = Direction; beforeBackHemisphere = backHemisphere;
        args.Pointer.Capture(this); args.Handled = true;
        PointAt(origin.Value, args.KeyModifiers);
    }
    protected override void OnPointerMoved(PointerEventArgs args)
    {
        base.OnPointerMoved(args);
        if (origin is null) return;
        PointAt(args.GetPosition(this), args.KeyModifiers);
        args.Handled = true;
    }
    /// <summary>Absolute aim in light-source space: clicking puts the light source at that front-view
    /// position so the beam travels opposite to it. Back/front hemisphere is preserved across rim drags;
    /// Shift snaps to 15° steps of azimuth and elevation.</summary>
    private void PointAt(Point position, KeyModifiers modifiers)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        double radius = Math.Max(1, Math.Min(Bounds.Width, Bounds.Height) / 2 - 16);
        double x = (position.X - center.X) / radius, y = -(position.Y - center.Y) / radius;
        double planar = x * x + y * y;
        if (planar > 1) { double scale = 1 / Math.Sqrt(planar); x *= scale; y *= scale; planar = 1; }
        float sourceZSign = backHemisphere ? 1f : -1f;
        var unitSource = new Vector3((float)x, (float)y, sourceZSign * (float)Math.Sqrt(Math.Max(0, 1 - planar)));
        if (unitSource.LengthSquared() < 1e-12f) return;
        float length = Finite(before) && before.LengthSquared() > 1e-12f ? before.Length() : 1;
        var direction = -Vector3.Normalize(unitSource) * length;
        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            direction = EditorLightAngles.Snap(direction, 15);
            if (Finite(direction) && direction.LengthSquared() > 1e-12f)
            {
                var snapped = Vector3.Normalize(direction);
                if (MathF.Abs(snapped.Z) > HemisphereEpsilon) backHemisphere = snapped.Z < 0;
            }
        }
        SetCurrentValue(DirectionProperty, direction);
        EditPreview?.Invoke(this, direction);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs args)
    {
        base.OnPointerReleased(args);
        if (origin is null) return;
        origin = null;
        args.Pointer.Capture(null);
        args.Handled = true;
        EditCompleted?.Invoke(this, Direction);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs args)
    {
        base.OnPointerCaptureLost(args);
        if (origin is null) return;
        origin = null;
        backHemisphere = beforeBackHemisphere;
        SetCurrentValue(DirectionProperty, before);
        EditCanceled?.Invoke(this, EventArgs.Empty);
    }
    protected override void OnKeyDown(KeyEventArgs args)
    {
        base.OnKeyDown(args);
        if (args.Key == Key.Escape && (origin is not null || keyEditing))
        {
            origin = null;
            keyEditing = false;
            backHemisphere = beforeBackHemisphere;
            SetCurrentValue(DirectionProperty, before);
            EditCanceled?.Invoke(this, EventArgs.Empty);
            args.Handled = true;
            return;
        }
        if (args.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        double stepDeg = args.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 15d : 5d;
        double dAz = args.Key switch { Key.Left => -stepDeg, Key.Right => stepDeg, _ => 0d };
        double dEl = args.Key switch { Key.Up => stepDeg, Key.Down => -stepDeg, _ => 0d };
        if (origin is null && !keyEditing)
        {
            before = Direction;
            beforeBackHemisphere = backHemisphere;
            if (!BeginEdit()) return;
            keyEditing = true;
        }
        var current = Direction;
        float length = Finite(current) && current.LengthSquared() > 1e-12f ? current.Length() : 1f;
        var (azimuth, elevation) = EditorLightAngles.Angles(current, lastAzimuth);
        if (Math.Abs(elevation) < 89.9) lastAzimuth = azimuth;
        double nextAzimuth = (azimuth + dAz) % 360d;
        if (nextAzimuth < 0) nextAzimuth += 360d;
        double nextElevation = Math.Clamp(elevation + dEl, -90d, 90d);
        if (Math.Abs(nextElevation) < 89.9) lastAzimuth = nextAzimuth;
        var next = EditorLightAngles.Direction(nextAzimuth, nextElevation, length);
        SetCurrentValue(DirectionProperty, next);
        EditPreview?.Invoke(this, next);
        args.Handled = true;
    }
    protected override void OnKeyUp(KeyEventArgs args)
    {
        base.OnKeyUp(args);
        if (keyEditing && args.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            CompleteKeyEdit();
            args.Handled = true;
        }
    }
    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs args)
    {
        base.OnLostFocus(args);
        CompleteKeyEdit();
    }
    internal void FlipHemisphere()
    {
        if (origin is null && !keyEditing && !BeginEdit()) return;
        var next = FlipHemisphereDirection(Direction, backHemisphere);
        backHemisphere = !backHemisphere;
        if (origin is null && !keyEditing) beforeBackHemisphere = backHemisphere;
        SetCurrentValue(DirectionProperty, next);
        EditPreview?.Invoke(this, next);
        if (origin is null && !keyEditing)
            EditCompleted?.Invoke(this, next);
    }
    internal static Vector3 FlipHemisphereDirection(Vector3 direction, bool currentlyBacklit)
    {
        if (!Finite(direction) || direction.LengthSquared() < 1e-12f)
            return currentlyBacklit ? new Vector3(0, 0, 1) : new Vector3(0, 0, -1);
        float length = direction.Length();
        if (MathF.Abs(direction.Z) > length * 1e-4f)
            return new Vector3(direction.X, direction.Y, -direction.Z);
        float targetTravelZSign = currentlyBacklit ? 1f : -1f;
        var tilted = new Vector3(direction.X, direction.Y, targetTravelZSign * length * 0.1f);
        return Vector3.Normalize(tilted) * length;
    }
    private bool BeginEdit()
    {
        var args = new System.ComponentModel.CancelEventArgs();
        EditStarted?.Invoke(this, args);
        return !args.Cancel;
    }
    private void CompleteKeyEdit()
    {
        if (!keyEditing) return;
        keyEditing = false;
        EditCompleted?.Invoke(this, Direction);
    }
    private IBrush Brush(string key, IBrush fallback) => this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;
    private static bool Finite(Vector3 vector) => float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
}
