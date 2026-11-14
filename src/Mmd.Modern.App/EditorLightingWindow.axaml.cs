using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Mmd.Modern.Core;

namespace Mmd.Modern.App;

/// <summary>A detached native-light draft; the owner commits the returned result in one workspace transaction.</summary>
public partial class EditorLightingWindow : Window
{
    public EditorLightingWindow() => InitializeComponent();
    public EditorLightingWindow(SceneLightingSnapshot snapshot, Guid? selectedLightId = null) : this()
    {
        DataContext = new EditorLightingViewModel(snapshot, selectedLightId);
        Opened += (_, _) =>
        {
            RequestedThemeVariant = Owner?.ActualThemeVariant ?? ThemeVariant.Dark;
            if (Owner is { } owner) { FontFamily = owner.FontFamily; Resources["OutputFont"] = owner.FontFamily; }
        };
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { Close(null); args.Handled = true; } };
    }
    public EditorLightingViewModel ViewModel => (EditorLightingViewModel)DataContext!;
    private void AddPoint(object? sender, RoutedEventArgs args) => ViewModel.AddLight(SceneLightKind.Point);
    private void AddSpot(object? sender, RoutedEventArgs args) => ViewModel.AddLight(SceneLightKind.Spot);
    private void AddDirectional(object? sender, RoutedEventArgs args) => ViewModel.AddLight(SceneLightKind.Directional);
    private void RemoveLight(object? sender, RoutedEventArgs args) => ViewModel.RemoveSelectedLight();
    private void CancelLighting(object? sender, RoutedEventArgs args) => Close(null);
    private void ApplyLighting(object? sender, RoutedEventArgs args)
    {
        if (this.GetVisualDescendants().OfType<Control>().Any(control => control.IsEffectivelyEnabled && DataValidationErrors.GetHasErrors(control)))
        { ViewModel.SetValidationMessage("请先修正标出的输入值，再应用光源。"); return; }
        if (ViewModel.TryCreate(out var draft)) Close(draft);
    }
}
