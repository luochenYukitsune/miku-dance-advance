using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Mmd.Modern.App;

/// <summary>Main-light authoring hosted by the right-rail Lighting page. The scene view model owns
/// the detached pose and its one-transaction registration; this control only binds to it.</summary>
public sealed partial class EditorMainLightInspector : UserControl
{
    public EditorMainLightInspector() => AvaloniaXamlLoader.Load(this);

    private void AimMainLight(object? sender, RoutedEventArgs args)
    {
        if (DataContext is EditorSceneInspectorViewModel model && sender is Button { Tag: string preset }) model.AimLight(preset);
    }

    private void AimMainLightFromCamera(object? sender, RoutedEventArgs args)
    {
        if (DataContext is EditorSceneInspectorViewModel model) model.AimLightFromCamera();
    }

    private void FlipMainLightHemisphere(object? sender, RoutedEventArgs args)
    {
        if (DataContext is EditorSceneInspectorViewModel model) model.FlipMainLightHemisphere();
    }

    private void MainLightColor(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not EditorSceneInspectorViewModel model || sender is not Button { Tag: string color }) return;
        var value = Color.Parse(color);
        model.LightColorR = value.R / 255m; model.LightColorG = value.G / 255m; model.LightColorB = value.B / 255m;
    }
}
