using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Mmd.Modern.App;

public sealed partial class EditorSceneInspector : UserControl
{
    private bool _syncingPilotToggle;
    private bool _isPiloting;
    private bool _helpersVisible = true;

    public event Action<bool>? PilotCameraToggled;
    public event Action<bool>? CameraHelpersToggled;

    public EditorSceneInspector()
    {
        InitializeComponent();
        EditorLocalization.Observe(this, static view => view.RefreshPilotLabel());
    }

    internal void SetPilotModeState(bool piloting, bool helpersVisible)
    {
        _isPiloting = piloting;
        _helpersVisible = helpersVisible;
        _syncingPilotToggle = true;
        try
        {
            if (this.FindControl<ToggleButton>("CameraInspectorPilotToggle") is { } toggle)
            {
                toggle.IsChecked = piloting;
                toggle.Content = EditorLocalization.Text(piloting
                    ? "Mda.Text.CameraPilot.Exit"
                    : "Mda.Text.CameraPilot.Enter");
            }
            if (this.FindControl<ToggleButton>("CameraHelpersToggle") is { } helpers)
            {
                helpers.IsChecked = helpersVisible;
                helpers.IsEnabled = !piloting;
            }
        }
        finally
        {
            _syncingPilotToggle = false;
        }
    }

    private void RefreshPilotLabel() => SetPilotModeState(_isPiloting, _helpersVisible);

    private void TogglePilotCameraClicked(object? sender, RoutedEventArgs args)
    {
        if (_syncingPilotToggle) return;
        PilotCameraToggled?.Invoke((sender as ToggleButton)?.IsChecked == true);
    }

    private void ToggleCameraHelpersClicked(object? sender, RoutedEventArgs args)
    {
        if (_syncingPilotToggle) return;
        CameraHelpersToggled?.Invoke((sender as ToggleButton)?.IsChecked == true);
    }

    private void ResizeCameraFields(object? sender, SizeChangedEventArgs args)
    {
        bool stack = args.NewSize.Width < 440;
        Grid.SetColumn(CameraLensFields, stack ? 0 : 1);
        Grid.SetRow(CameraLensFields, stack ? 1 : 0);
        Grid.SetColumnSpan(CameraTransformFields, stack ? 2 : 1);
        Grid.SetColumnSpan(CameraLensFields, stack ? 2 : 1);
        CameraPoseGrid.RowSpacing = stack ? 6 : 0;
    }
}
