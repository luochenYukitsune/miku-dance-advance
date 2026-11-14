using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Mmd.Modern.Core;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>Light authoring belongs to the right-rail lighting inspector page; the workspace owns
/// persistence, mapping snapshots and Undo. Additional-light edits commit as one transaction per edit.</summary>
public partial class EditorWindow
{
    private bool _publishingInlineLight, _refreshingLightingInspector;

    private void InitializeLightingInspector(EditorShellViewModel shell)
    {
        var inspector = this.FindControl<EditorLightInspector>("NativeLightInspector")!;
        inspector.CommitRequested = CommitInlineLighting;
        inspector.SelectionRequested = id => lightController?.Select(id);
        if (lightController is not null)
        {
            lightController.Changed += LightToolsChanged;
            lightController.CanBeginEdit = CanBeginLightGesture;
            lightController.IsEnabled = true;
        }
        if (boneController is not null) boneController.CanBeginInteraction = CanBeginBoneInteraction;
        InitializeInspectorPages(shell);
        RefreshLightingInspector();
    }

    private bool CanBeginBoneInteraction()
    {
        if (_shell is not { IsBusy: false } shell) return false;
        long revision = shell.ContentRevision;
        if (!CommitPendingInlineLighting()) return false;
        if (shell.InspectorPage == EditorInspectorPage.Lighting)
        {
            // Move/rotate handles are explicit animation editing and return to the animation page.
            // A plain select is ordinary object selection: it updates the shared selection but keeps the
            // user's manually chosen lighting page, releasing only the additional-light viewport handle.
            if (boneController?.IsManipulationEnabled == true)
            {
                if (!LeaveLightContext()) return false;
            }
            else if (lightController?.SelectedLightId is not null)
            {
                lightController.Select(null);
            }
        }
        // Committing a light invalidates the bone hit geometry captured before this callback.
        // Consume this click; the render requested above supplies the next valid bone target.
        return revision == shell.ContentRevision;
    }

    private bool CanBeginLightGesture()
    {
        if (_shell is not { IsBusy: false, HasProject: true } shell) return false;
        shell.StopPlayback();
        return CommitPendingInlineLighting();
    }

    private void OpenSceneLighting(object? sender, RoutedEventArgs args)
    {
        if (_shell is not { IsBusy: false, HasProject: true } shell) return;
        shell.StopPlayback();
        _ = NavigateInspectorPage(EditorInspectorPage.Lighting);
    }

    private void CloseSceneLighting(object? sender, RoutedEventArgs args)
    {
        _ = NavigateInspectorPage(EditorInspectorPage.Animation);
    }

    private void LightToolsChanged(object? sender, EventArgs args)
    {
        if (Dispatcher.UIThread.CheckAccess()) OnLightToolsChanged();
        else Dispatcher.UIThread.Post(OnLightToolsChanged);
    }

    private void OnLightToolsChanged()
    {
        if (_shell is not { } shell || _adjustingInspectorPage)
        {
            RefreshLightingInspector();
            return;
        }
        // Selecting a light (viewport click, added light, explicit select) is the explicit entry into
        // light editing. Undo/frame/theme/language refreshes keep the null selection and never navigate.
        if (lightController?.SelectedLightId is { } && shell.InspectorPage != EditorInspectorPage.Lighting)
            _ = NavigateInspectorPage(EditorInspectorPage.Lighting);
        RefreshLightingInspector();
    }

    private void RefreshLightingInspector()
    {
        if (_publishingInlineLight || _refreshingLightingInspector || _shell is not { } shell) return;
        var snapshot = shell.Workspace.Session?.Lighting ?? SceneLightingSnapshot.Empty;
        if (this.FindControl<Button>("AddViewportLightButton") is { } addLight)
            addLight.IsEnabled = shell.HasProject && snapshot.Lights.Count < SceneLightingSnapshot.MaxLights;
        bool lighting = shell.InspectorPage == EditorInspectorPage.Lighting;
        if (this.FindControl<Control>("BoneToolsContainer") is { } boneTools) boneTools.IsVisible = !lighting;
        if (this.FindControl<Control>("LightToolsBar") is { } lightTools) lightTools.IsVisible = lighting;
        if (!lighting) return;
        _refreshingLightingInspector = true;
        try
        {
            var selected = lightController?.SelectedLightId;
            if (selected is { } selectedId && snapshot.Lights.All(light => light.Id != selectedId))
            {
                lightController?.Select(null);
                selected = null;
            }
            var inspector = this.FindControl<EditorLightInspector>("NativeLightInspector")!;
            inspector.SetLighting(snapshot, selected, _shell.Workspace.DocumentId);
            var light = selected is { } id ? snapshot.Lights.FirstOrDefault(item => item.Id == id) : null;
            if (light is { Kind: SceneLightKind.Point } && lightController is { RotateHandle: true } pointController)
            {
                pointController.RotateHandle = false;
                this.FindControl<EditorViewportHost>("SceneViewport")!.RotateLightHandle = false;
            }
            if (this.FindControl<TextBlock>("SelectedLightName") is { } selectedName)
                selectedName.Text = light?.Name ?? Text("未选择灯光");
            if (this.FindControl<ToggleButton>("LightMoveMode") is { } moveMode)
                moveMode.IsChecked = lightController?.RotateHandle != true;
            if (this.FindControl<ToggleButton>("LightRotateMode") is { } rotateMode)
                rotateMode.IsChecked = lightController?.RotateHandle == true;
            if (this.FindControl<Control>("LightMoveMode") is { } moveControl) moveControl.IsEnabled = light is not null;
            if (this.FindControl<Control>("LightRotateMode") is { } rotateControl)
                rotateControl.IsEnabled = light is { Kind: not SceneLightKind.Point };
            if (this.FindControl<Control>("DeleteLightButton") is { } deleteLight) deleteLight.IsEnabled = light is not null;
        }
        finally { _refreshingLightingInspector = false; }
    }

    /// <summary>Commit a focused inline-light input only while that page is visible; page navigation is not an edit.</summary>
    private bool CommitPendingInlineLighting() =>
        _shell?.InspectorPage != EditorInspectorPage.Lighting || TryCommitInlineLighting();

    private bool TryCommitInlineLighting() =>
        this.FindControl<EditorLightInspector>("NativeLightInspector")!.TryCommit();

    private bool CommitInlineLighting(EditorLightingDraft draft)
    {
        if (_shell is not { IsBusy: false, HasProject: true } shell) return false;
        _publishingInlineLight = true;
        try
        {
            shell.StopPlayback();
            shell.Workspace.EditLighting(draft);
            lightController?.Select(draft.SelectedLightId);
            return true;
        }
        catch (Exception error) { shell.ReportActivityFailure(error); return false; }
        finally
        {
            _publishingInlineLight = false;
            Dispatcher.UIThread.Post(RefreshLightingInspector);
        }
    }

    private void CommitViewportLighting(EditorLightingDraft draft)
    {
        if (CommitInlineLighting(draft)) RefreshLightingInspector();
    }

    private void AddDirectionalLight(object? sender, RoutedEventArgs args) => AddViewportLight(SceneLightKind.Directional);
    private void AddPointLight(object? sender, RoutedEventArgs args) => AddViewportLight(SceneLightKind.Point);
    private void AddSpotLight(object? sender, RoutedEventArgs args) => AddViewportLight(SceneLightKind.Spot);
    private void AddViewportLight(SceneLightKind kind)
    {
        if (_shell is not { IsBusy: false, HasProject: true } shell || !CommitPendingInlineLighting()) return;
        shell.StopPlayback();
        var model = new EditorLightingViewModel(shell.Workspace.Session!.Lighting, lightController?.SelectedLightId);
        model.SetCreationCamera(this.FindControl<EditorViewportHost>("SceneViewport")!.LightCreationCamera);
        model.AddLight(kind);
        if (!model.TryCreate(out var draft) || draft is null) { shell.OperationStatus = model.ValidationMessage; return; }
        _ = NavigateInspectorPage(EditorInspectorPage.Lighting);
        if (CommitInlineLighting(draft))
        {
            if (lightController is { } controller) controller.RotateHandle = false;
            this.FindControl<EditorViewportHost>("SceneViewport")!.RotateLightHandle = false;
            RefreshLightingInspector();
            shell.OperationStatus = Text("灯光已添加。直接拖动图标移动，或切换旋转调整方向。");
        }
    }

    private void DeleteViewportLight(object? sender, RoutedEventArgs args)
    {
        if (_shell is not { IsBusy: false, HasProject: true } shell || lightController?.SelectedLightId is not { } id
            || !CommitPendingInlineLighting()) return;
        var model = new EditorLightingViewModel(shell.Workspace.Session!.Lighting, id);
        model.RemoveSelectedLight();
        if (model.TryCreate(out var draft) && draft is not null && CommitInlineLighting(draft)) RefreshLightingInspector();
    }

    private void SelectLightMove(object? sender, RoutedEventArgs args) => SetLightTransformMode(false);
    private void SelectLightRotate(object? sender, RoutedEventArgs args) => SetLightTransformMode(true);
    private void SetLightTransformMode(bool rotate)
    {
        if (lightController is not { } controller) return;
        controller.RotateHandle = rotate;
        this.FindControl<EditorViewportHost>("SceneViewport")!.RotateLightHandle = rotate;
        RefreshLightingInspector();
    }

    /// <summary>Return to animation editing. The current light selection belongs to the lighting page and is
    /// dropped; a pending inline input is committed by the page-transition guard before this runs.</summary>
    private bool LeaveLightContext()
    {
        if (_shell is not { } shell) return false;
        if (shell.InspectorPage == EditorInspectorPage.Lighting)
            return NavigateInspectorPage(EditorInspectorPage.Animation);
        if (lightController?.SelectedLightId is not null) lightController.Select(null);
        return true;
    }
}
