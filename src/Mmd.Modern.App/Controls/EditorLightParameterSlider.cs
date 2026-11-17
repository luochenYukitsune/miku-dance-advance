using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Mmd.Modern.App.Controls;

/// <summary>A standard slider with an explicit, cancelable edit boundary. Value is display-only
/// outside a user gesture, so coercing the thumb cannot overwrite an authored HDR/range value.</summary>
public sealed class EditorLightParameterSlider : Slider
{
    private bool editing, pointerEdit, releasing, suppressPointerUntilRelease;
    private IPointer? editPointer;
    private readonly HashSet<Key> pressedKeys = [];
    private readonly HashSet<Key> canceledHeldKeys = [];
    protected override Type StyleKeyOverride => typeof(Slider);
    public event EventHandler<CancelEventArgs>? EditStarted;
    public event EventHandler? EditPreview;
    public event EventHandler? EditCompleted;
    public event EventHandler? EditCanceled;

    public EditorLightParameterSlider()
    {
        AddHandler(PointerPressedEvent, BeginPointer, RoutingStrategies.Tunnel, true);
        AddHandler(PointerMovedEvent, (_, args) => { if (suppressPointerUntilRelease) args.Handled = true; }, RoutingStrategies.Tunnel, true);
        AddHandler(PointerReleasedEvent, (_, _) => releasing = true, RoutingStrategies.Tunnel, true);
        AddHandler(PointerReleasedEvent, (_, _) => { if (pointerEdit) Finish(false); releasing = false; suppressPointerUntilRelease = false; }, RoutingStrategies.Bubble, true);
        AddHandler(KeyDownEvent, BeginKey, RoutingStrategies.Tunnel, true);
        AddHandler(KeyUpEvent, EndKey, RoutingStrategies.Tunnel, true);
        LostFocus += (_, _) => { if (editing) Finish(true); };
        DetachedFromVisualTree += (_, _) => { if (editing) Finish(true); };
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        // Thumb completion also fires on capture loss. Distinguish it from an actual release
        // by observing release in the tunnel, before the template handles the event.
        if (Track?.Thumb is { } thumb) thumb.PointerCaptureLost += CaptureLost;
        if (e.NameScope.Find<Button>("PART_DecreaseButton") is { } decrease) decrease.PointerCaptureLost += CaptureLost;
        if (e.NameScope.Find<Button>("PART_IncreaseButton") is { } increase) increase.PointerCaptureLost += CaptureLost;
    }
    private void CaptureLost(object? sender, PointerCaptureLostEventArgs args)
    { if (pointerEdit && !releasing) Finish(true); }

    private bool Begin()
    {
        if (editing) return true;
        var args = new CancelEventArgs(); EditStarted?.Invoke(this, args);
        if (args.Cancel) return false;
        editing = true; return true;
    }
    private void BeginPointer(object? sender, PointerPressedEventArgs args)
    {
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        suppressPointerUntilRelease = false;
        if (!Begin()) { args.Handled = true; return; }
        pointerEdit = true; releasing = false; editPointer = args.Pointer;
    }
    private static bool IsAdjustmentKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down
        or Key.PageUp or Key.PageDown or Key.Home or Key.End;
    private void BeginKey(object? sender, KeyEventArgs args)
    {
        // Escape ends this gesture, not the physical key press. Its remaining auto-repeat
        // events must not start a second gesture before the matching release arrives.
        if (canceledHeldKeys.Contains(args.Key)) { args.Handled = true; return; }
        if (args.Key == Key.Escape && editing) { Finish(true); args.Handled = true; return; }
        if (args.KeyModifiers != KeyModifiers.None || !IsAdjustmentKey(args.Key)) return;
        if (!Begin()) { args.Handled = true; return; }
        pressedKeys.Add(args.Key);
    }
    private void EndKey(object? sender, KeyEventArgs args)
    {
        if (canceledHeldKeys.Remove(args.Key)) { args.Handled = true; return; }
        if (!pressedKeys.Remove(args.Key) || pressedKeys.Count != 0 || pointerEdit) return;
        Finish(false);
    }
    internal void CancelEdit() { if (editing) Finish(true); }
    private void Finish(bool cancel)
    {
        if (!editing) return;
        var pointer = editPointer;
        bool wasPointer = pointerEdit;
        if (cancel) canceledHeldKeys.UnionWith(pressedKeys);
        editing = false; pointerEdit = false; pressedKeys.Clear();
        editPointer = null;
        if (cancel && wasPointer) { suppressPointerUntilRelease = true; pointer?.Capture(null); }
        if (cancel) EditCanceled?.Invoke(this, EventArgs.Empty);
        else EditCompleted?.Invoke(this, EventArgs.Empty);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty && editing) EditPreview?.Invoke(this, EventArgs.Empty);
    }
}
