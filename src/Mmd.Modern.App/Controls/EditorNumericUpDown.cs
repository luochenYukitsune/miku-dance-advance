using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Mmd.Modern.App.Controls;

/// <summary>
/// Keeps invalid editor text visible and exposes its validity to save/registration guards.
/// Avalonia's numeric control otherwise retains its last Value on parse errors and silently
/// replaces that text on focus loss. Valid values still use the original numeric control.
/// </summary>
public sealed class EditorNumericUpDown : NumericUpDown
{
    public static readonly RoutedEvent<RoutedEventArgs> InputValidityChangedEvent =
        RoutedEvent.Register<EditorNumericUpDown, RoutedEventArgs>(nameof(InputValidityChanged), RoutingStrategies.Bubble);

    private bool textIsValid = true, preservingInvalidText;
    private Exception? bindingError;

    protected override Type StyleKeyOverride => typeof(NumericUpDown);

    public bool IsInputValid => textIsValid && bindingError is null;

    public event EventHandler<RoutedEventArgs> InputValidityChanged
    {
        add => AddHandler(InputValidityChangedEvent, value);
        remove => RemoveHandler(InputValidityChangedEvent, value);
    }

    /// <summary>Only active inputs participate; collapsed alternatives and disabled options
    /// cannot prevent applying the settings currently shown to the user.</summary>
    public static bool HasInvalidInputs(Control root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return root.GetVisualDescendants().OfType<Control>().Prepend(root).Any(control =>
            control.IsEffectivelyEnabled && control.IsEffectivelyVisible
            && (control is EditorNumericUpDown numeric && !numeric.IsInputValid
                || DataValidationErrors.GetHasErrors(control)));
    }

    protected override void OnTextChanged(string? oldValue, string? newValue)
    {
        base.OnTextChanged(oldValue, newValue);
        if (!preservingInvalidText) ValidateText();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        if (textIsValid) { base.OnLostFocus(e); return; }
        string? invalidText = Text;
        preservingInvalidText = true;
        try { base.OnLostFocus(e); }
        finally
        {
            preservingInvalidText = false;
            SetCurrentValue(TextProperty, invalidText);
            ValidateText();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MinimumProperty || change.Property == MaximumProperty
            || change.Property == ParsingNumberStyleProperty || change.Property == NumberFormatProperty
            || change.Property == FormatStringProperty || change.Property == TextConverterProperty)
            ValidateText();
    }

    protected override void UpdateDataValidation(AvaloniaProperty property, BindingValueType state, Exception? error)
    {
        bool wasValid = IsInputValid;
        if (property == TextProperty || property == ValueProperty) bindingError = error;
        base.UpdateDataValidation(property, state, error);
        PublishValidation(wasValid);
    }

    private void ValidateText()
    {
        bool wasValid = IsInputValid;
        textIsValid = TryReadText();
        PublishValidation(wasValid);
    }

    private bool TryReadText()
    {
        if (string.IsNullOrWhiteSpace(Text)) return false;
        try
        {
            decimal? parsed;
            if (TextConverter is { } converter)
                parsed = (decimal?)converter.Convert(Text, typeof(decimal?), null, CultureInfo.CurrentCulture);
            else if (!decimal.TryParse(Text, ParsingNumberStyle, NumberFormat, out var number))
            {
                // Keep the base control's existing formatted-value round trip (e.g. a suffix).
                string? formatted = FormatString.Contains("{0", StringComparison.Ordinal)
                    ? string.Format(NumberFormat, FormatString, Value)
                    : Value?.ToString(FormatString, NumberFormat);
                return Value.HasValue && string.Equals(Text, formatted, StringComparison.Ordinal);
            }
            else parsed = number;
            return parsed.HasValue && (ClipValueToMinMax || parsed.Value >= Minimum && parsed.Value <= Maximum);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException or InvalidCastException)
        { return false; }
    }

    private void PublishValidation(bool wasValid)
    {
        DataValidationErrors.SetError(this, textIsValid ? bindingError
            : new FormatException(EditorLocalization.Text("请先修正标出的输入值，再应用设置。")));
        if (wasValid != IsInputValid) RaiseEvent(new RoutedEventArgs(InputValidityChangedEvent));
    }
}
