using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Mmd.Modern.App.Controls;

/// <summary>
/// A slider paired with a numeric box for everyday values (morph weight, FOV, strengths).
/// <see cref="Number"/> is the authored value; the thumb only displays it, clamped to the slider
/// range, so loading or typing a value outside the range is never rewritten by coercion.
/// Only a user gesture writes back:
/// <list type="bullet">
/// <item>click anywhere on the track to jump there and keep dragging; Escape restores the value from before the drag;</item>
/// <item>double-click resets to <see cref="DefaultValue"/>;</item>
/// <item>the mouse wheel adjusts only once the slider has focus, so it never steals panel scrolling; Shift gives a finer step;</item>
/// <item>arrow keys, PageUp/PageDown, Home/End.</item>
/// </list>
/// </summary>
public sealed class EditorValueSlider : Slider
{
    public static readonly StyledProperty<decimal?> NumberProperty =
        AvaloniaProperty.Register<EditorValueSlider, decimal?>(nameof(Number), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> DefaultValueProperty =
        AvaloniaProperty.Register<EditorValueSlider, double>(nameof(DefaultValue), double.NaN);
    /// <summary>Written values are rounded to this step (0 = no rounding).</summary>
    public static readonly StyledProperty<double> StepProperty =
        AvaloniaProperty.Register<EditorValueSlider, double>(nameof(Step));

    private bool syncing, gesture;
    private decimal? gestureStart;

    protected override Type StyleKeyOverride => typeof(Slider);

    public decimal? Number { get => GetValue(NumberProperty); set => SetValue(NumberProperty, value); }
    public double DefaultValue { get => GetValue(DefaultValueProperty); set => SetValue(DefaultValueProperty, value); }
    public double Step { get => GetValue(StepProperty); set => SetValue(StepProperty, value); }

    public EditorValueSlider()
    {
        EditorNumberMenu.Attach(this, () => Number, () => double.IsFinite(DefaultValue) ? (decimal)DefaultValue : null,
            value => Write(Math.Clamp((double)value, Minimum, Maximum)));
        Focusable = true; // Avalonia's slider already jumps to the pressed point and keeps dragging.
        AddHandler(PointerPressedEvent, (_, args) =>
        {
            if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            BeginGesture(); Focus(NavigationMethod.Pointer);
        }, RoutingStrategies.Tunnel, true);
        AddHandler(PointerReleasedEvent, (_, _) => gesture = false, RoutingStrategies.Bubble, true);
        AddHandler(PointerCaptureLostEvent, (_, _) => gesture = false, RoutingStrategies.Bubble, true);
        AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Escape && gesture && gestureStart != Number)
            { gesture = false; SetCurrentValue(NumberProperty, gestureStart); args.Handled = true; return; }
            if (args.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
                BeginGesture();
        }, RoutingStrategies.Tunnel, true);
        AddHandler(KeyUpEvent, (_, _) => gesture = false, RoutingStrategies.Bubble, true);
        DoubleTapped += (_, _) => ResetToDefault();
        LostFocus += (_, _) => gesture = false;
    }

    internal void ResetToDefault()
    {
        if (double.IsNaN(DefaultValue) || !IsEffectivelyEnabled) return;
        gesture = false; Write(DefaultValue);
    }

    private void BeginGesture()
    {
        if (gesture) return;
        gesture = true; gestureStart = Number;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        // An unfocused slider passes the wheel on, so scrolling a long inspector never edits values.
        if (!IsFocused || !IsEffectivelyEnabled || e.Delta.Y == 0) { base.OnPointerWheelChanged(e); return; }
        double step = SmallChange > 0 ? SmallChange : (Maximum - Minimum) / 100;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) step /= 10;
        double current = Number is { } number ? (double)number : Value;
        Write(Math.Clamp(current + Math.Sign(e.Delta.Y) * step, Minimum, Maximum));
        e.Handled = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == NumberProperty || change.Property == MinimumProperty || change.Property == MaximumProperty)
            Sync();
        else if (change.Property == ValueProperty && !syncing && gesture)
            Write(Value);
    }

    private void Sync()
    {
        syncing = true;
        try
        {
            double shown = Number is { } number ? (double)number : Minimum;
            SetCurrentValue(ValueProperty, Math.Clamp(shown, Minimum, Math.Max(Minimum, Maximum)));
            PseudoClasses.Set(":empty", Number is null);
        }
        finally { syncing = false; }
    }

    private void Write(double value)
    {
        if (!double.IsFinite(value)) return;
        if (Step > 0) value = Math.Clamp(Math.Round(value / Step) * Step, Minimum, Maximum);
        decimal written = Math.Round((decimal)value, 6);
        if (Number == written) { Sync(); return; }
        SetCurrentValue(NumberProperty, written);
    }
}
