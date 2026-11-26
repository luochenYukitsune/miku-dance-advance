using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Mmd.Modern.App;

/// <summary>Resolves a secondary-button gesture in logical viewport coordinates. Native rendering
/// surfaces can forward their input to the same methods used by Avalonia pointer events.</summary>
internal sealed class ViewportContextGesture
{
    private const double DragThreshold = 4;
    private readonly Control surface;
    private readonly Action beginPan;
    private readonly Action<Vector> pan;
    private readonly Action<Point> showMenu;
    private readonly Action revertPan;
    private IPointer? captured;
    private Point origin, last;
    private uint pressedAt;
    private bool active, dragged, suppressMenu;

    internal ViewportContextGesture(Control surface, Action beginPan, Action<Vector> pan, Action<Point> showMenu, Action revertPan)
    {
        this.surface = surface;
        this.beginPan = beginPan;
        this.pan = pan;
        this.showMenu = showMenu;
        this.revertPan = revertPan;
        surface.AddHandler(InputElement.PointerPressedEvent, Pressed, RoutingStrategies.Tunnel);
        surface.AddHandler(InputElement.PointerMovedEvent, Moved, RoutingStrategies.Tunnel);
        surface.AddHandler(InputElement.PointerReleasedEvent, Released, RoutingStrategies.Tunnel);
        surface.PointerCaptureLost += (_, _) => { captured = null; active = false; };
        surface.PointerWheelChanged += (_, _) => SuppressMenu();
        surface.LostFocus += (_, _) => { if (captured is not null) Cancel(); };
        surface.DetachedFromVisualTree += (_, _) => Cancel();
        surface.KeyDown += (_, args) =>
        {
            if (active && args.Key == Key.Escape) { Revert(); args.Handled = true; }
        };
    }

    internal void Begin(Point point, uint time)
    {
        origin = last = point;
        pressedAt = time;
        active = true;
        dragged = suppressMenu = false;
        beginPan();
    }

    internal void Move(Point point)
    {
        if (!active) return;
        Vector delta = point - origin;
        dragged |= delta.Length > DragThreshold;
        if (dragged && point != last) pan(delta);
        last = point;
    }

    internal void End(Point point, uint time)
    {
        if (!active) return;
        Move(point);
        var duration = unchecked(time - pressedAt);
        var hold = TopLevel.GetTopLevel(surface)?.PlatformSettings?.HoldWaitDuration ?? TimeSpan.FromMilliseconds(500);
        bool click = !dragged && !suppressMenu && duration <= hold.TotalMilliseconds;
        Cancel();
        if (click) showMenu(point);
    }

    internal void SuppressMenu() => suppressMenu = true;
    internal void Cancel()
    {
        active = false;
        var pointer = captured;
        captured = null;
        if (pointer?.Captured == surface) pointer.Capture(null);
    }

    internal void Revert()
    {
        bool restore = active && dragged;
        Cancel();
        if (restore) revertPan();
    }

    private void Pressed(object? sender, PointerPressedEventArgs args)
    {
        var properties = args.GetCurrentPoint(surface).Properties;
        if (active) { SuppressMenu(); return; }
        if (properties.PointerUpdateKind != PointerUpdateKind.RightButtonPressed ||
            properties.IsLeftButtonPressed || properties.IsMiddleButtonPressed) return;
        surface.Focus(NavigationMethod.Pointer);
        captured = args.Pointer;
        captured.Capture(surface);
        Begin(args.GetPosition(surface), unchecked((uint)args.Timestamp));
        args.Handled = true;
    }

    private void Moved(object? sender, PointerEventArgs args)
    {
        if (captured != args.Pointer) return;
        Move(args.GetPosition(surface));
        args.Handled = true;
    }

    private void Released(object? sender, PointerReleasedEventArgs args)
    {
        if (captured != args.Pointer || args.InitialPressMouseButton != MouseButton.Right) return;
        args.Handled = true;
        End(args.GetPosition(surface), unchecked((uint)args.Timestamp));
    }
}
