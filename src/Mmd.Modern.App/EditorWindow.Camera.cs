using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Mmd.Modern.App;

public partial class EditorWindow
{
    private EditorCameraViewportController? _cameraController;
    private bool _syncingCameraPilotUi;

    private void InitializeCameraController(EditorShellViewModel shell)
    {
        var viewport = this.FindControl<EditorViewportHost>("SceneViewport")!;
        if (_cameraController is not null) _cameraController.Changed -= OnCameraControllerChanged;

        _cameraController = new EditorCameraViewportController(
            shell.SceneInspector,
            pose => shell.Workspace.Session?.ResolveCameraParentTransform(pose),
            isSelected: () => _shell?.SceneSelectionIndex == 1,
            selectCamera: SelectCameraComponent,
            enterPilotMode: EnterCameraPilotMode,
            redraw: viewport.RequestRender);
        _cameraController.Changed += OnCameraControllerChanged;

        viewport.CameraController = _cameraController;
        viewport.SetCameraPilotMode(false, initializeFreeViewOffset: true);
        SyncCameraPilotUi();
    }

    private void DetachCameraController(EditorViewportHost viewport)
    {
        if (_cameraController is not null)
        {
            _cameraController.Changed -= OnCameraControllerChanged;
            _cameraController.CancelDrag();
            _cameraController = null;
        }
        viewport.CameraController = null;
    }

    private void SelectCameraComponent()
    {
        if (_shell is not { } shell) return;
        if (shell.SceneSelectionIndex != 1)
            shell.SceneSelectionIndex = 1;
        this.FindControl<EditorViewportHost>("SceneViewport")?.RequestRender();
    }

    internal void EnterCameraPilotMode()
    {
        if (_shell is not { } shell) return;
        var viewport = this.FindControl<EditorViewportHost>("SceneViewport")!;
        if (shell.SceneSelectionIndex != 1)
            shell.SceneSelectionIndex = 1;
        viewport.SetCameraPilotMode(true);
        SyncCameraPilotUi();
    }

    internal void ExitCameraPilotMode()
    {
        var viewport = this.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.SetCameraPilotMode(false, initializeFreeViewOffset: true);
        SyncCameraPilotUi();
    }

    private void ToggleViewportCameraPilot(object? sender, RoutedEventArgs args)
    {
        if (_syncingCameraPilotUi) return;
        bool wantPilot = (sender as ToggleButton)?.IsChecked == true;
        if (wantPilot) EnterCameraPilotMode();
        else ExitCameraPilotMode();
    }

    private void ExitCameraPilotModeClicked(object? sender, RoutedEventArgs args) => ExitCameraPilotMode();

    private void ToggleCameraHelpers(object? sender, RoutedEventArgs args)
    {
        if (_syncingCameraPilotUi || _cameraController is null) return;
        _cameraController.HelpersVisible = (sender as ToggleButton)?.IsChecked == true;
        SyncCameraPilotUi();
    }

    private void OutlineCameraDoubleTapped(object? sender, TappedEventArgs args)
    {
        EnterCameraPilotMode();
        args.Handled = true;
    }

    private void OnSceneInspectorPilotToggled(bool pilot)
    {
        if (_syncingCameraPilotUi) return;
        if (pilot) EnterCameraPilotMode();
        else ExitCameraPilotMode();
    }

    private void OnSceneInspectorHelpersToggled(bool visible)
    {
        if (_syncingCameraPilotUi || _cameraController is null) return;
        _cameraController.HelpersVisible = visible;
        SyncCameraPilotUi();
    }

    private void OnViewportCameraPilotModeChanged(bool piloting)
    {
        if (Dispatcher.UIThread.CheckAccess()) SyncCameraPilotUi();
        else Dispatcher.UIThread.Post(SyncCameraPilotUi);
    }

    private void OnCameraControllerChanged(object? sender, EventArgs args)
    {
        if (Dispatcher.UIThread.CheckAccess()) SyncCameraPilotUi();
        else Dispatcher.UIThread.Post(SyncCameraPilotUi);
    }

    private void SyncCameraPilotUi()
    {
        if (_syncingCameraPilotUi) return;
        _syncingCameraPilotUi = true;
        try
        {
            var viewport = this.FindControl<EditorViewportHost>("SceneViewport");
            bool piloting = viewport?.IsPilotingCamera == true;
            bool helpersVisible = _cameraController?.HelpersVisible ?? true;

            if (this.FindControl<ToggleButton>("ViewportCameraPilotToggle") is { } pilotToggle)
                pilotToggle.IsChecked = piloting;
            if (this.FindControl<Border>("CameraPilotBanner") is { } banner)
                banner.IsVisible = piloting;
            if (this.FindControl<EditorSceneInspector>("SceneInspectorView") is { } inspector)
                inspector.SetPilotModeState(piloting, helpersVisible);
        }
        finally
        {
            _syncingCameraPilotUi = false;
        }
    }
}
