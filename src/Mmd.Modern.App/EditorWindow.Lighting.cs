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
    private EditorMainLightViewport? _mainLightViewport;
    private bool _synchronizingLightBrowser, _trackLightActive;
    private (Guid? Document, int Frame, System.Numerics.Vector3 Color)? _mutedTrackColor;
    private EditorLightingViewModel? _lightBrowserModel;
    private (Guid? Document, Guid Id, bool IsGroup)? _lastRevealedLightRow;
    private (SceneLightingSnapshot Snapshot, Guid? Document, IReadOnlyList<Guid>? Selection, Guid? Primary)? _outlineSource;

    private void InitializeLightingInspector(EditorShellViewModel shell)
    {
        var inspector = this.FindControl<EditorLightInspector>("NativeLightInspector")!;
        var viewport = this.FindControl<EditorViewportHost>("SceneViewport")!;
        _trackLightActive = false;
        _outlineSource = null;
        _mainLightViewport = new(shell.SceneInspector, CanBeginLightGesture, viewport.RequestRender);
        inspector.TrackLightProvider = () => lightController?.SelectedLightId is null && inspector.ViewModel?.SelectedGroupIds.Count is not > 0 ? shell.SceneInspector : null;
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
            lightController.IsEnabled = shell.SupportsModernLightingWorkspace;
        }
        InitializeInspectorPages(shell);
        RefreshLightingInspector();
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
        // An outline click navigates explicitly. A viewport drag keeps the current properties page.
        this.FindControl<EditorViewportHost>("SceneViewport")!.SetBoneToolsEnabled(
            !shell.IsLightingPage && lightController?.IsDragging != true);
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
        var inspector = this.FindControl<EditorLightInspector>("NativeLightInspector")!;
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
            var outlineSource = (snapshot, shell.Workspace.DocumentId, lightController?.SelectedLightIds, selected);
            if (lighting || _outlineSource != outlineSource)
            {
                using (InteractionDiagnostics.Measure("lighting.inspector-set"))
                    inspector.SetLighting(snapshot, selected, shell.Workspace.DocumentId, lightController?.SelectedLightIds);
                _outlineSource = outlineSource;
            }
            if (lightController is { } active && inspector.ViewModel is { } selection
                && (!active.SelectedLightIds.SequenceEqual(selection.SelectedLightIds) || active.SelectedLightId != selection.SelectedLight?.Id))
            {
                active.SelectMany(selection.SelectedLightIds, selection.SelectedLight?.Id);
                selected = active.SelectedLightId;
            }
            inspector.UseWorkspaceBrowser(shell.SupportsModernLightingWorkspace);
            bool trackLight = !shell.SupportsModernLightingWorkspace || selected is null && inspector.ViewModel?.SelectedGroupIds.Count is not > 0;
            shell.SceneInspector.SetPrimaryLightTrackActive(trackLight);
            if (lighting || lightController?.SelectedLightId is not null) _trackLightActive = trackLight;
            var mainSection = this.FindControl<Expander>("MainLightSection")!;
            mainSection.IsVisible = trackLight;
            this.FindControl<ListBoxItem>("OutlineTrackLight")!.Classes.Set("passiveSceneTrack", !trackLight);
            this.FindControl<Control>("NativeLightProperties")!.IsVisible = shell.SupportsModernLightingWorkspace;
            this.FindControl<Control>("NativeLightHeader")!.IsVisible = !trackLight;
            this.FindControl<EditorViewportHost>("SceneViewport")!.MainLightViewport =
                _trackLightActive && trackLight && shell.HasProject && !shell.IsBusy && lightController?.HelpersVisible == true
                    ? _mainLightViewport : null;
            inspector.FindControl<TextBlock>("EmptyLightPrompt")!.IsVisible = false;
            inspector.FindControl<Control>("TrackLightPresetTools")!.IsVisible = trackLight && shell.HasProject;
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
            var lightSwitch = this.FindControl<ToggleButton>("ViewportLightEnabled")!;
            lightSwitch.IsEnabled = shell.HasProject && !shell.IsBusy && !multiple && (trackLight || light is not null);
            lightSwitch.IsChecked = trackLight ? shell.SceneInspector.LightPose.Color != System.Numerics.Vector3.Zero : light?.Enabled == true;
            if (!multiple && light is { Kind: SceneLightKind.Point } && lightController is { RotateHandle: true } pointController)
            {
                pointController.RotateHandle = false;
                this.FindControl<EditorViewportHost>("SceneViewport")!.RotateLightHandle = false;
            }
            if (this.FindControl<TextBlock>("SelectedLightName") is { } selectedName)
                selectedName.Text = inspector.ViewModel is { ShowBatchControls: true } batch ? batch.SelectionSummary : light?.Name ?? (trackLight ? Text("Mda.Text.Layout.TrackLight") : Text("未选择灯光"));
            if (this.FindControl<ToggleButton>("LightMoveMode") is { } moveMode)
                moveMode.IsChecked = lightController?.RotateHandle != true;
            if (this.FindControl<ToggleButton>("LightRotateMode") is { } rotateMode)
                rotateMode.IsChecked = lightController?.RotateHandle == true;
            if (this.FindControl<Control>("LightMoveMode") is { } moveControl) moveControl.IsEnabled = light is not null;
            if (this.FindControl<Control>("LightRotateMode") is { } rotateControl)
                rotateControl.IsEnabled = multiple || light is { Kind: not SceneLightKind.Point };
            if (this.FindControl<Control>("DeleteLightButton") is { } deleteLight) deleteLight.IsEnabled = inspector.ViewModel?.HasSelection == true;
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
            var last = wanted.LastOrDefault();
            (Guid? Document, Guid Id, bool IsGroup)? target = last switch
            {
                EditorNativeLightItem light => (_shell?.Workspace.DocumentId, light.Id, false),
                EditorLightGroupItem group => (_shell?.Workspace.DocumentId, group.Id, true),
                _ => null
            };
            if (target != _lastRevealedLightRow)
            {
                _lastRevealedLightRow = target;
                if (last is not null && list.IsEffectivelyVisible)
                    using (InteractionDiagnostics.Measure("lighting.browser-scroll")) list.ScrollIntoView(last);
            }
        }
        finally { _synchronizingLightBrowser = false; }
    }
    private void SelectBrowserLights(object? sender, SelectionChangedEventArgs args)
    {
        if (_synchronizingLightBrowser || _refreshingLightingInspector || _lightBrowserModel is not { IsRefreshingBrowser: false } model
            || sender is not ListBox { SelectedItems: { } items } list) return;
        var requestedRows = items.Cast<object>().ToArray();
        var primary = args.AddedItems.Cast<object>().LastOrDefault() ?? list.SelectedItem;
        if (requestedRows.Length > 0 && !NavigateInspectorPage(EditorInspectorPage.Lighting))
        {
            SynchronizeLightingBrowser();
            return;
        }
        model.SelectRows(requestedRows, primary);
        RefreshLightingInspector();
    }

    private void SelectTrackLight(object? sender, Avalonia.Input.PointerPressedEventArgs args)
    {
        if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) ActivateTrackLight();
    }

    private void ActivateTrackLight()
    {
        if (_shell is not { HasProject: true } shell || !CommitPendingInlineLighting()
            || !NavigateInspectorPage(EditorInspectorPage.Lighting)) return;
        _lightBrowserModel?.RestoreSelection([], null, []);
        lightController?.Select(null);
        shell.SceneSelectionIndex = 2;
        RefreshLightingInspector();
    }

    private void AddThreePointLighting(object? sender, RoutedEventArgs args) => AddLightGroup(SceneLightingBuiltinPreset.PortraitSoft);
    private void AddWindowLighting(object? sender, RoutedEventArgs args) => AddLightGroup(SceneLightingBuiltinPreset.IndoorWindow);

    private void AddLightGroup(SceneLightingBuiltinPreset kind)
    {
        if (_shell is not { IsBusy: false, HasProject: true, SupportsModernLightingWorkspace: true }
            || !CommitPendingInlineLighting()) return;
        var inspector = this.FindControl<EditorLightInspector>("NativeLightInspector")!;
        if (inspector.AddBuiltinLighting(kind)) NavigateInspectorPage(EditorInspectorPage.Lighting);
    }

    private async void ImportOutlineLightingPreset(object? sender, RoutedEventArgs args)
    {
        if (_shell is not { IsBusy: false, HasProject: true, SupportsModernLightingWorkspace: true }
            || !CommitPendingInlineLighting()) return;
        var inspector = this.FindControl<EditorLightInspector>("NativeLightInspector")!;
        if (await inspector.ImportLightGroupAsync()) NavigateInspectorPage(EditorInspectorPage.Lighting);
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
        if (_shell is not { IsBusy: false, HasProject: true, SupportsModernLightingWorkspace: true } shell || !CommitPendingInlineLighting()) return;
        shell.StopPlayback();
        var model = new EditorLightingViewModel(shell.Workspace.Session!.Lighting, lightController?.SelectedLightId);
        model.SetCreationCamera(this.FindControl<EditorViewportHost>("SceneViewport")!.LightCreationCamera);
        if (!model.AddLight(kind)) { shell.OperationStatus = model.ValidationMessage; return; }
        if (!model.TryCreate(out var draft) || draft is null) { shell.OperationStatus = model.ValidationMessage; return; }
        _ = NavigateInspectorPage(EditorInspectorPage.Lighting);
        if (CommitInlineLighting(draft))
        {
            // Moving a directional lamp only moves its icon. Start with the tool that
            // changes the light; positional lamps share the current move/rotate tool.
            bool rotate = kind == SceneLightKind.Directional
                || kind != SceneLightKind.Point && lightController?.RotateHandle == true;
            SetLightTransformMode(rotate);
            shell.OperationStatus = draft.Snapshot.Lights.Single(light => light.Id == draft.SelectedLightId).Enabled
                ? Text(kind switch
                {
                    SceneLightKind.Directional => "Mda.Text.Context.Lighting.AddedDirectional",
                    SceneLightKind.Point => "Mda.Text.Context.Lighting.AddedPoint",
                    _ => "Mda.Text.Context.Lighting.AddedPositional"
                })
                : Format("Mda.Text.Context.Lighting.AddedDisabledAtCapacity", SceneLightingSnapshot.MaxActiveLights);
        }
    }

    private void ToggleViewportLight(object? sender, RoutedEventArgs args)
    {
        if (_shell is not { IsBusy: false, HasProject: true } shell || sender is not ToggleButton button) return;
        try
        {
            bool enable = button.IsChecked == true;
            if (!CanBeginLightGesture()) return;
            if (lightController?.SelectedLightId is { } id)
            {
                var scene = shell.Workspace.Session!.Lighting;
                if (lightController.SelectedLightIds.Count != 1) return;
                var updated = scene.WithLights(scene.Lights.Select(light => light.Id == id ? light with { Enabled = enable } : light));
                CommitInlineLighting(new(updated, id));
            }
            else
            {
                var model = shell.SceneInspector;
                if (!model.CanEdit || model.HasLightValidation) return;
                var pose = model.LightPose;
                if (!enable) _mutedTrackColor = (shell.Workspace.DocumentId, shell.CurrentFrame, pose.Color);
                var onColor = _mutedTrackColor is { } stored && stored.Document == shell.Workspace.DocumentId && stored.Frame == shell.CurrentFrame
                    ? stored.Color : EditorSceneInspectorViewModel.DefaultLightColor;
                model.SetLightDraft(pose with { Color = enable ? onColor : System.Numerics.Vector3.Zero });
            }
        }
        catch (ArgumentException error) { shell.ReportActivityFailure(error); }
        finally { RefreshLightingInspector(); }
    }

    private void DeleteViewportLight(object? sender, RoutedEventArgs args)
    {
        if (_shell is not { IsBusy: false, HasProject: true } shell
            || !CommitPendingInlineLighting()) return;
        if (shell.SupportsModernLightingWorkspace)
            this.FindControl<EditorLightInspector>("NativeLightInspector")!.RemoveSelectedLights();
    }

    private void SelectLightMove(object? sender, RoutedEventArgs args) => SetLightTransformMode(false);
    private void SelectLightRotate(object? sender, RoutedEventArgs args) => SetLightTransformMode(true);
    private void SetLightTransformMode(bool rotate)
    {
        if (lightController is not { } controller) return;
        if (!controller.HelpersVisible)
        {
            this.FindControl<ToggleButton>("LightHelpersToggle")!.IsChecked = true;
            controller.HelpersVisible = true;
        }
        controller.RotateHandle = rotate;
        // Selection refresh can coerce a single point light back to move mode.
        this.FindControl<EditorViewportHost>("SceneViewport")!.RotateLightHandle = controller.RotateHandle;
        RefreshLightingInspector();
    }

    /// <summary>Activate the bone tools while retaining the lighting inspector's selection and draft.</summary>
    private bool LeaveLightContext()
    {
        if (_shell is not { } shell) return false;
        if (shell.InspectorPage == EditorInspectorPage.Lighting)
            return NavigateInspectorPage(EditorInspectorPage.Animation);
        shell.ViewportPickedLightId = null;
        return true;
    }
}
