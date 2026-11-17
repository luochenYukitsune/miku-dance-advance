using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Mmd.Modern.App;

public partial class EditorWindow
{
    private void InitializeLightHelperTools(EditorShellViewModel shell)
    {
        var toggle = this.FindControl<ToggleButton>("LightHelpersToggle")!;
        toggle.IsVisible = shell.SupportsModernLightingWorkspace;
        if (lightController is { } controller) controller.HelpersVisible = toggle.IsChecked == true;
    }

    private void ToggleLightHelpers(object? sender, RoutedEventArgs args)
    {
        if (lightController is { } controller && sender is ToggleButton toggle)
            controller.HelpersVisible = toggle.IsChecked == true;
    }
}
