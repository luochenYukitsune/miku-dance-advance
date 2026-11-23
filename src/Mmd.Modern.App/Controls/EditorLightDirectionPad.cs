using System.ComponentModel;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Mmd.Modern.App.Controls;

/// <summary>Edits the source direction on a two-hemisphere disk; the host owns preview and commit.</summary>
public sealed class EditorLightDirectionPad : Control
{
    public static readonly StyledProperty<Vector3> DirectionProperty =
        AvaloniaProperty.Register<EditorLightDirectionPad, Vector3>(nameof(Direction), new(-.5f, -1, .5f),
            defaultBindingMode: Avalonia.Data.BindingMode.OneWay);
    private IPointer? pointer;
    private readonly HashSet<Key> heldKeys = [];
    private readonly HashSet<Key> canceledKeys = [];
    private bool backHemisphere;
    private double lastAzimuth;
    private Vector3 original, current;
    private bool startingHemisphere;
    private bool Editing => pointer is not null || heldKeys.Count != 0;
    public Vector3 Direction { get => GetValue(DirectionProperty); set => SetValue(DirectionProperty, value); }
    public event EventHandler<CancelEventArgs>? EditStarted;
    public event EventHandler<Vector3>? EditPreview;
    public event EventHandler? EditCompleted;
    public event EventHandler? EditCanceled;
    static EditorLightDirectionPad() => AffectsRender<EditorLightDirectionPad>(DirectionProperty);
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty) InvalidateVisual();
        else if (change.Property == DirectionProperty && change.NewValue is Vector3 vec && Finite(vec) && vec.LengthSquared() > 1e-12f)
        {
            var normalized = Vector3.Normalize(vec);
            if (MathF.Abs(normalized.Z) > 1e-5f)
                backHemisphere = normalized.Z < 0;
        }
    }
    public EditorLightDirectionPad()
    {
        Focusable = true; MinHeight = 112; Cursor = new Cursor(StandardCursorType.Cross);
        DetachedFromVisualTree += (_, _) => CancelEdit();
        DataContextChanged += (_, _) => CancelEdit();
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
        var direction = Editing ? current : Direction;
        if (!Finite(direction) || direction.LengthSquared() < 1e-12f) return;
        var travel = Vector3.Normalize(direction);
        var source = -travel;
        var point = new Point(center.X + source.X * radius, center.Y - source.Y * radius);
        bool isFront = MathF.Abs(travel.Z) > 1e-5f ? travel.Z > 0 : !backHemisphere;
        context.DrawLine(new Pen(foreground, 2, isFront ? null : DashStyle.Dash), center, point);
        var accent = Brush("MdaAccent", foreground);
        context.DrawEllipse(isFront ? accent : null, new Pen(accent, 2), point, 5, 5);
        if (IsFocused) context.DrawRectangle(null, new Pen(foreground, 1), new Rect(Bounds.Size).Deflate(1));
    }
    protected override void OnPointerPressed(PointerPressedEventArgs args)
    {
        base.OnPointerPressed(args);
        var props = args.GetCurrentPoint(this).Properties;
        if (Editing || !(props.IsLeftButtonPressed || props.IsRightButtonPressed)) return;
        Focus();
        if (!Begin()) return;
        pointer = args.Pointer;
        args.Handled = true;
        if (props.IsRightButtonPressed)
        {
            Preview(EditorLightAngles.FlipHemisphereDirection(original, backHemisphere));
            if (Editing) Finish(false);
            return;
        }
        pointer.Capture(this);
        PointAt(args.GetPosition(this), args.KeyModifiers);
    }

    protected override void OnPointerMoved(PointerEventArgs args)
    {
        base.OnPointerMoved(args);
        if (pointer != args.Pointer || args.Pointer.Captured != this) return;
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
        float length = original.Length();
        var direction = -Vector3.Normalize(unitSource) * (float.IsFinite(length) && length > 1e-6f ? length : 1);
        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            var (azimuth, elevation) = EditorLightAngles.Angles(direction, lastAzimuth);
            direction = EditorLightAngles.Direction(Math.Round(azimuth / 15) * 15, Math.Round(elevation / 15) * 15, direction.Length());
        }
        Preview(direction);
    }

    private bool Begin()
    {
        var args = new CancelEventArgs();
        EditStarted?.Invoke(this, args);
        if (args.Cancel) return false;
        original = current = Direction;
        startingHemisphere = backHemisphere;
        return true;
    }

    private void Preview(Vector3 direction)
    {
        if (current == direction) return;
        current = direction;
        if (MathF.Abs(direction.Z) > direction.Length() * 1e-5f) backHemisphere = direction.Z < 0;
        InvalidateVisual();
        EditPreview?.Invoke(this, direction);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs args)
    {
        base.OnPointerReleased(args);
        if (pointer != args.Pointer || args.InitialPressMouseButton != MouseButton.Left) return;
        PointAt(args.GetPosition(this), args.KeyModifiers);
        if (Editing) Finish(false);
        args.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs args)
    {
        base.OnPointerCaptureLost(args);
        if (args.Pointer == pointer) CancelEdit();
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        base.OnKeyDown(args);
        if (args.Key == Key.Escape && Editing)
        {
            CancelEdit(); args.Handled = true;
            return;
        }
        if (args.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)
            || (args.KeyModifiers & ~KeyModifiers.Shift) != 0 || pointer is not null) return;
        args.Handled = true;
        if (canceledKeys.Contains(args.Key) || !Editing && !Begin()) return;
        heldKeys.Add(args.Key);
        double step = args.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 15 : 5;
        var (azimuth, elevation) = EditorLightAngles.Angles(current, lastAzimuth);
        if (args.Key == Key.Left) azimuth -= step;
        if (args.Key == Key.Right) azimuth += step;
        if (args.Key == Key.Up) elevation += step;
        if (args.Key == Key.Down) elevation -= step;
        lastAzimuth = azimuth;
        Preview(EditorLightAngles.Direction(azimuth, elevation, current.Length()));
    }

    protected override void OnKeyUp(KeyEventArgs args)
    {
        base.OnKeyUp(args);
        if (canceledKeys.Remove(args.Key)) { args.Handled = true; return; }
        if (!heldKeys.Remove(args.Key)) return;
        args.Handled = true;
        if (heldKeys.Count == 0) Finish(false);
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs args)
    {
        base.OnLostFocus(args);
        CancelEdit();
        canceledKeys.Clear();
    }

    internal void CancelEdit()
    {
        if (Editing) Finish(true);
    }

    private void Finish(bool cancel)
    {
        var captured = pointer;
        pointer = null;
        if (cancel) canceledKeys.UnionWith(heldKeys);
        heldKeys.Clear();
        if (captured?.Captured == this) captured.Capture(null);
        if (cancel)
        {
            backHemisphere = startingHemisphere;
            EditCanceled?.Invoke(this, EventArgs.Empty);
        }
        else EditCompleted?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }
    private IBrush Brush(string key, IBrush fallback) => this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;
    private static bool Finite(Vector3 vector) => float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
}
