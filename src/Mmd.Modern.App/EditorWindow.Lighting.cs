using Mmd.Modern.Application;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.ComponentModel;
using Mmd.Modern.Core;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>Light authoring belongs to the right-rail lighting inspector page; the workspace owns
/// persistence, mapping snapshots and Undo. Additional-light edits commit as one transaction per edit.</summary>
public partial class EditorWindow
{
    private bool _publishingInlineLight, _refreshingLightingInspector;
    // Parent choices depend only on committed content; frame changes never rebuild the bone names.
    private (EditorShellViewModel? Shell, SceneProjectSession? Session, long Revision, bool Modern)? _lightParentContextKey;
    private bool _synchronizingLightBrowser;
    private EditorLightingViewModel? _lightBrowserModel;

    private void InitializeLightingInspector(EditorShellViewModel shell)
    {
        var inspector = this.FindControl<EditorLightInspector>("NativeLightInspector")!;
        inspector.CommitRequested = CommitInlineLighting;
        inspector.SelectionRequested = id => lightController?.Select(id);
        inspector.SelectionSetRequested = (ids, primary) =>
        {
            lightController?.SelectMany(ids, primary);
            RefreshLightingInspector(); // An empty group or another row context can keep the same viewport IDs.
        };
        inspector.EditStartingRequested = () =>
        {
            if (_shell is not { IsBusy: false, HasProject: true } current) return false;
            current.StopPlayback(); return true;
        };
        inspector.PreviewRequested = draft => lightController?.SetParameterPreview(draft) == true;
        inspector.PreviewQualityRequested = () => OpenPreviewSettings(this, new RoutedEventArgs());
        inspector.CreationCameraProvider = () => this.FindControl<EditorViewportHost>("SceneViewport")!.LightCreationCamera;
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
        if (this.FindControl<Button>("AddBrowserLightButton") is { } browserAddLight)
            browserAddLight.IsEnabled = shell.HasProject && snapshot.Lights.Count < SceneLightingSnapshot.MaxLights;
        bool lighting = shell.InspectorPage == EditorInspectorPage.Lighting;
        if (this.FindControl<Control>("BoneToolsContainer") is { } boneTools) boneTools.IsVisible = !lighting;
        if (this.FindControl<Control>("LightToolsBar") is { } lightTools) lightTools.IsVisible = lighting;
        if (!lighting) return;
        _refreshingLightingInspector = true;
        using var measure = InteractionDiagnostics.Measure("lighting.inspector-refresh", relatedId: snapshot.Lights.Count);
        try
        {
            var selected = lightController?.SelectedLightId;
            if (selected is { } selectedId && snapshot.Lights.All(light => light.Id != selectedId))
            {
                lightController?.SelectMany(lightController.SelectedLightIds.Where(id => snapshot.Lights.Any(light => light.Id == id)));
                selected = lightController?.SelectedLightId;
            }
            var inspector = this.FindControl<EditorLightInspector>("NativeLightInspector")!;
            using (InteractionDiagnostics.Measure("lighting.inspector-set"))
                inspector.SetLighting(snapshot, selected, _shell.Workspace.DocumentId, lightController?.SelectedLightIds);
            if (lightController is { } active && inspector.ViewModel is { } selection
                && (!active.SelectedLightIds.SequenceEqual(selection.SelectedLightIds) || active.SelectedLightId != selection.SelectedLight?.Id))
            {
                active.SelectMany(selection.SelectedLightIds, selection.SelectedLight?.Id);
                selected = active.SelectedLightId;
            }
            inspector.UseWorkspaceBrowser(shell.SupportsModernLightingWorkspace);
            var session = shell.Workspace.Session;
            var parentKey = (shell, session, shell.ContentRevision, shell.SupportsModernLightingWorkspace);
            if (_lightParentContextKey != parentKey)
            {
                var motions = shell.SupportsModernLightingWorkspace ? shell.Workspace.Motions : [];
                inspector.ConfigureParentContext(shell.SupportsModernLightingWorkspace && session is not null
                    ? session.Objects.Select(item => new EditorLightParentModel(item.Id, item.Name,
                        motions.FirstOrDefault(motion => motion.ObjectId == item.Id)?.Model.Bones.Select(bone => bone.Name).ToArray() ?? []))
                    : [], session is not null && shell.SupportsModernLightingWorkspace ? session.ResolveLightParentTransform : null);
                _lightParentContextKey = parentKey;
            }
            using (InteractionDiagnostics.Measure("lighting.browser-bind")) BindLightingBrowser(inspector.ViewModel);
            var light = selected is { } id ? snapshot.Lights.FirstOrDefault(item => item.Id == id) : null;
            bool multiple = lightController?.SelectedLightIds.Count > 1;
            if (!multiple && light is { Kind: SceneLightKind.Point } && lightController is { RotateHandle: true } pointController)
            {
                pointController.RotateHandle = false;
                this.FindControl<EditorViewportHost>("SceneViewport")!.RotateLightHandle = false;
            }
            if (this.FindControl<TextBlock>("SelectedLightName") is { } selectedName)
                selectedName.Text = inspector.ViewModel is { ShowBatchControls: true } batch ? batch.SelectionSummary : light?.Name ?? Text("未选择灯光");
            if (this.FindControl<ToggleButton>("LightMoveMode") is { } moveMode)
                moveMode.IsChecked = lightController?.RotateHandle != true;
            if (this.FindControl<ToggleButton>("LightRotateMode") is { } rotateMode)
                rotateMode.IsChecked = lightController?.RotateHandle == true;
            if (this.FindControl<Control>("LightMoveMode") is { } moveControl) moveControl.IsEnabled = light is not null;
            if (this.FindControl<Control>("LightRotateMode") is { } rotateControl)
                rotateControl.IsEnabled = multiple || light is { Kind: not SceneLightKind.Point };
            if (this.FindControl<Control>("DeleteLightButton") is { } deleteLight) deleteLight.IsEnabled = inspector.ViewModel?.HasSelection == true;
            if (this.FindControl<Control>("LegacyDeleteLightButton") is { } legacyDeleteLight) legacyDeleteLight.IsEnabled = light is not null;
        }
        finally { _refreshingLightingInspector = false; }
    }

    /// <summary>Commit a focused inline-light input only while that page is visible; page navigation is not an edit.</summary>
    private bool CommitPendingInlineLighting() =>
        _shell?.InspectorPage != EditorInspectorPage.Lighting || TryCommitInlineLighting();

    private bool TryCommitInlineLighting() =>
        this.FindControl<EditorLightInspector>("NativeLightInspector")!.TryCommit();

    private void CommitBrowserLight(object? sender, RoutedEventArgs args) =>
        this.FindControl<EditorLightInspector>("NativeLightInspector")!.CommitSwitch(sender);

    private void BindLightingBrowser(EditorLightingViewModel? model)
    {
        if (!ReferenceEquals(_lightBrowserModel, model))
        {
            if (_lightBrowserModel is { } previous) previous.PropertyChanged -= LightingBrowserChanged;
            _lightBrowserModel = model;
            _synchronizingLightBrowser = true;
            try { using (InteractionDiagnostics.Measure("lighting.browser-datacontext")) this.FindControl<Grid>("LightBrowserPanel")!.DataContext = model; }
            finally { _synchronizingLightBrowser = false; }
            if (model is not null) model.PropertyChanged += LightingBrowserChanged;
        }
        SynchronizeLightingBrowser();
    }
    private void LightingBrowserChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(EditorLightingViewModel.SelectedRows)) SynchronizeLightingBrowser();
        else if (args.PropertyName == nameof(EditorLightingViewModel.BrowserRows))
            Dispatcher.UIThread.Post(SynchronizeLightingBrowser);
    }
    private void SynchronizeLightingBrowser()
    {
        if (_synchronizingLightBrowser || _lightBrowserModel is not { } model
            || this.FindControl<ListBox>("WorkspaceLightList") is not { } list) return;
        _synchronizingLightBrowser = true;
        try
        {
            using var measure = InteractionDiagnostics.Measure("lighting.browser-sync");
            var visible = list.Items.Cast<object>().ToHashSet();
            var wanted = model.SelectedRows.Where(visible.Contains).ToArray();
            if (list.SelectedItems is { } selected)
            {
                foreach (var item in selected.Cast<object>().Where(item => !wanted.Contains(item)).ToArray()) selected.Remove(item);
                foreach (var item in wanted.Where(item => !selected.Contains(item))) selected.Add(item);
            }
            if (model.SelectedRows.LastOrDefault(visible.Contains) is { } last)
                using (InteractionDiagnostics.Measure("lighting.browser-scroll")) list.ScrollIntoView(last);
        }
        finally { _synchronizingLightBrowser = false; }
    }
    private void SelectBrowserLights(object? sender, SelectionChangedEventArgs args)
    {
        if (_synchronizingLightBrowser || _refreshingLightingInspector || _lightBrowserModel is not { IsRefreshingBrowser: false } model
            || sender is not ListBox { SelectedItems: { } items } list) return;
        model.SelectRows(items.Cast<object>().ToArray(), args.AddedItems.Cast<object>().LastOrDefault() ?? list.SelectedItem);
    }

    private bool CommitInlineLighting(EditorLightingDraft draft)
    {
        if (_shell is not { IsBusy: false, HasProject: true } shell) return false;
        _publishingInlineLight = true;
        try
        {
            shell.StopPlayback();
            shell.SynchronizePendingScrub();
            using (InteractionDiagnostics.Measure("lighting.workspace-edit")) shell.Workspace.EditLighting(draft);
            lightController?.SelectMany(draft.SelectedLightIds, draft.SelectedLightId);
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
    private void AddAreaLight(object? sender, RoutedEventArgs args) => AddViewportLight(SceneLightKind.Area);
    private void AddViewportLight(SceneLightKind kind)
    {
        if (_shell is not { IsBusy: false, HasProject: true } shell || !CommitPendingInlineLighting()) return;
        shell.StopPlayback();
        var model = new EditorLightingViewModel(shell.Workspace.Session!.Lighting, lightController?.SelectedLightId);
        model.SetCreationCamera(this.FindControl<EditorViewportHost>("SceneViewport")!.LightCreationCamera);
        if (!model.AddLight(kind)) { shell.OperationStatus = model.ValidationMessage; return; }
        if (!model.TryCreate(out var draft) || draft is null) { shell.OperationStatus = model.ValidationMessage; return; }
        _ = NavigateInspectorPage(EditorInspectorPage.Lighting);
        if (CommitInlineLighting(draft))
        {
            if (lightController is { } controller) controller.RotateHandle = false;
            this.FindControl<EditorViewportHost>("SceneViewport")!.RotateLightHandle = false;
            RefreshLightingInspector();
            shell.OperationStatus = draft.Snapshot.Lights.Single(light => light.Id == draft.SelectedLightId).Enabled
                ? Text("灯光已添加。直接拖动图标移动，或切换旋转调整方向。")
                : Format("Mda.Text.Context.Lighting.AddedDisabledAtCapacity", SceneLightingSnapshot.MaxActiveLights);
        }
    }

    private void DeleteViewportLight(object? sender, RoutedEventArgs args)
    {
        if (_shell is not { IsBusy: false, HasProject: true } shell
            || !CommitPendingInlineLighting()) return;
        var inspector = this.FindControl<EditorLightInspector>("NativeLightInspector")!;
        if (shell.SupportsModernLightingWorkspace) { inspector.RemoveSelectedLights(); return; }
        if (lightController?.SelectedLightId is not { } id) return;
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
