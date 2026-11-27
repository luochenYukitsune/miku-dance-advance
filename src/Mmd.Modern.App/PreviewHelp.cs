using Avalonia;
using Avalonia.Controls;

namespace Mmd.Modern.App;

/// <summary>Attached help topic identifier used by <see cref="EditorPreviewSettingsWindow"/> to update the right-hand explanation card.</summary>
public static class PreviewHelp
{
    public static readonly AttachedProperty<string?> TopicProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Topic", typeof(PreviewHelp));

    public static string? GetTopic(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.GetValue(TopicProperty);
    }

    public static void SetTopic(Control control, string? value)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.SetValue(TopicProperty, value);
    }
}
