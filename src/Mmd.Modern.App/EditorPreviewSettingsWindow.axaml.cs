using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Mmd.Modern.ViewOutput;

namespace Mmd.Modern.App;

/// <summary>Returns a detached ViewQualityOptions on Apply; Cancel and window close return null.</summary>
public partial class EditorPreviewSettingsWindow : Window
{
    public EditorPreviewSettingsWindow() : this(new EditorPreviewSettingsViewModel()) { }

    public EditorPreviewSettingsWindow(ViewQualityOptions current, EditorPreviewCapabilities? capabilities = null,
        EditorPreviewRuntimeInfo? runtime = null)
        : this(new EditorPreviewSettingsViewModel(current, capabilities, runtime)) { }

    public EditorPreviewSettingsWindow(EditorPreviewSettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent(); DataContext = viewModel;
        Opened += (_, _) =>
        {
            RequestedThemeVariant = Owner?.ActualThemeVariant ?? ThemeVariant.Dark;
            if (Owner is not { } owner) return;
            FontFamily = owner.FontFamily; FontSize = owner.FontSize; Resources["OutputFont"] = owner.FontFamily;
        };
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            Close(null); args.Handled = true;
        };
    }

    public EditorPreviewSettingsViewModel ViewModel => (EditorPreviewSettingsViewModel)DataContext!;

    private void ApplySettings(object? sender, RoutedEventArgs args)
    {
        if (this.GetVisualDescendants().OfType<Control>().Any(control =>
            control.IsEffectivelyEnabled && DataValidationErrors.GetHasErrors(control)))
        { ViewModel.SetValidationMessage("请先修正标出的输入值，再应用设置。"); return; }
        if (ViewModel.TryCreate(out var result)) Close(result);
    }

    private void CancelSettings(object? sender, RoutedEventArgs args) => Close(null);
}
