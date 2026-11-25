using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>Right-rail page navigation is explicit user intent. Selection, frame, theme, language and
/// workspace refreshes only update page content; they never change the page the user chose.</summary>
public partial class EditorWindow
{
    private EditorInspectorPage? _observedInspectorPage;
    private bool _committingStyle, _adjustingInspectorPage, _syncingInspectorTabs, _restoringLightContext;

    private void InitializeInspectorPages(EditorShellViewModel shell)
    {
        _observedInspectorPage = shell.InspectorPage;
        _lightingOutlineActive = false;
        _nonLightingOutlineFilter = 0;
        if (dockReady && OutlineFilter.SelectedIndex != 0) OutlineFilter.SelectedIndex = 0;
        ApplyInspectorPagePresentation(shell);
    }

    /// <summary>One explicit transition for every entry point. Leaving the lighting page commits a focused
    /// inline-light edit (never an empty transaction) and is refused while its input is invalid, so page
    /// navigation cannot silently lose or bypass input validation.</summary>
    private bool NavigateInspectorPage(EditorInspectorPage page)
    {
        if (_shell is not { } shell) return false;
        if (shell.InspectorPage == page) return true;
        if (shell.InspectorPage == EditorInspectorPage.Lighting && !TryCommitInlineLighting()) return false;
        shell.ShowInspectorPage(page);
        return true;
    }

    /// <summary>Tab entry. The control is bound one-way: validation runs while the current page is still
    /// visible, and a refused transition restores the actual page tab.</summary>
    private void SelectInspectorPage(object? sender, SelectionChangedEventArgs args)
    {
        if (_syncingInspectorTabs || sender is not TabControl tabs || _shell is not { } shell) return;
        int index = tabs.SelectedIndex;
        if (index is < 0 or > 3 || index == shell.RightInspectorPageIndex) return;
        _syncingInspectorTabs = true;
        try
        {
            if (!NavigateInspectorPage((EditorInspectorPage)index))
                tabs.SelectedIndex = shell.RightInspectorPageIndex;
        }
        finally { _syncingInspectorTabs = false; }
    }

    private void OnInspectorPageChanged(EditorShellViewModel shell)
    {
        if (_adjustingInspectorPage) return;
        var page = shell.InspectorPage;
        if (_observedInspectorPage == page) return;
        _adjustingInspectorPage = true;
        try
        {
            bool leavingLighting = _observedInspectorPage == EditorInspectorPage.Lighting
                && page != EditorInspectorPage.Lighting;
            _observedInspectorPage = page;
            if (page == EditorInspectorPage.Lighting)
            {
                shell.StopPlayback();
                if (shell.HasProject)
                {
                    // Main-light context also drives the existing scene-registration guards.
                    if (shell.SceneSelectionIndex != 2)
                    {
                        shell.SceneSelectionIndex = 2;
                        if (shell.SupportsModernLightingWorkspace && !_trackLightActive && lightController?.SelectedLightId is null
                            && shell.Workspace.Session?.Lighting.Lights.FirstOrDefault() is { } first)
                            lightController?.Select(first.Id);
                    }
                }
            }
            else if (leavingLighting)
            {
                lightController?.CancelDrag();
                lightController?.SetParameterPreview(null);
                if (shell.SceneSelectionIndex == 2) shell.SceneSelectionIndex = -1;
            }
            shell.RefreshInspectorPages();
            ApplyInspectorPagePresentation(shell);
            RefreshLightingInspector();
        }
        finally { _adjustingInspectorPage = false; }
    }

    /// <summary>Only explicit scene-target selection navigates: the main light opens the lighting page,
    /// world/camera open the animation page. Ordinary object/selection refreshes stay on the page.</summary>
    private void OnSceneSelectionIndexChanged()
    {
        if (_shell is not { } shell || _adjustingInspectorPage || _restoringLightContext) return;
        if (shell.InspectorPage == EditorInspectorPage.Lighting)
        {
            if (shell.SceneSelectionIndex == -1)
            {
                // Ordinary model/bone selection refreshes shared selection while the manually chosen
                // light page keeps its main-light context (and therefore its registration/draft gates).
                RestoreLightContext(shell);
            }
            else if (shell.SceneSelectionIndex is 0 or 1)
            {
                // World/camera rows are explicit scene-context entries; they leave the light page.
                if (!NavigateInspectorPage(EditorInspectorPage.Animation)) RestoreLightContext(shell);
            }
            return;
        }
        if (shell.SceneSelectionIndex == 2)
        {
            if (NavigateInspectorPage(EditorInspectorPage.Lighting)) lightController?.Select(null);
        }
        else if (shell.SceneSelectionIndex is 0 or 1)
            _ = NavigateInspectorPage(EditorInspectorPage.Animation);
    }

    /// <summary>The chosen lighting page keeps its main-light context after an ordinary selection.</summary>
    private void RestoreLightContext(EditorShellViewModel shell)
    {
        _restoringLightContext = true;
        try { shell.SceneSelectionIndex = 2; } finally { _restoringLightContext = false; }
    }

    private void ApplyInspectorPagePresentation(EditorShellViewModel shell)
    {
        bool lighting = shell.InspectorPage == EditorInspectorPage.Lighting;
        this.FindControl<EditorViewportHost>("SceneViewport")!.SetBoneToolsEnabled(!lighting && lightController?.IsDragging != true);
        this.FindControl<Control>("BoneToolsContainer")!.IsVisible = !lighting;
        this.FindControl<Control>("LightToolsBar")!.IsVisible = lighting;
        SyncOutlineFilterForInspectorPage(lighting);
    }

    /// <summary>One explicit validated workspace transaction for the project-default style. The candidate's
    /// inherited-model mapping compatibility is checked inside the workspace before anything is committed.</summary>
    private async void CommitProjectDefaultStyle(object? sender, SelectionChangedEventArgs args)
    {
        if (_committingStyle || _shell is not { IsBusy: false, HasProject: true } shell || shell.StyleInspector.IsRefreshing) return;
        if (!shell.StyleInspector.CanEdit || sender is not ComboBox { SelectedItem: EditorStyleInspectorChoice { IsRetained: false } choice }) return;
        var session = shell.Workspace.Session;
        // Binding/refresh initialization and a failed/reverted candidate must not rewrite the project.
        if (session is null || !session.HasSupportedRenderingStyles
            || choice.Reference == session.RenderingStyles.DefaultStyle) return;
        _committingStyle = true;
        try
        {
            shell.StopPlayback();
            shell.SynchronizePendingScrub();
            if (!CommitPendingInlineLighting()) return;
            if (!ReferenceEquals(session, shell.Workspace.Session) || !shell.StyleInspector.CanEdit) return;
            try
            {
                bool changed = shell.Workspace.SetDefaultRenderingStyle(choice.Reference);
                string message = Text(changed
                    ? "Mda.Text.Context.Inspector.Style.StatusApplied"
                    : "Mda.Text.Context.Inspector.Style.StatusUnchanged");
                shell.StyleInspector.ReportStatus(message);
                shell.OperationStatus = message;
            }
            catch (Exception error)
            {
                shell.StyleInspector.ReportStatus(error.Message);
                await ShowErrorAsync(error);
            }
        }
        finally
        {
            _committingStyle = false;
            shell.RefreshInspectorPages();
        }
    }

    private async void CommitModelStyleOverride(object? sender, SelectionChangedEventArgs args)
    {
        if (_committingStyle || _shell is not { IsBusy: false } shell || shell.StyleInspector.IsRefreshing
            || !shell.StyleInspector.CanEditSelectedModelStyle || shell.Browser.SelectedModel is not { } model
            || sender is not ComboBox { SelectedItem: EditorStyleInspectorChoice { IsRetained: false } choice }) return;
        _committingStyle = true;
        try
        {
            shell.StopPlayback();
            shell.SynchronizePendingScrub();
            if (CommitPendingInlineLighting()) shell.Workspace.SetModelStyle(model.ObjectId, choice.Reference);
        }
        catch (Exception error) { await ShowErrorAsync(error); }
        finally
        {
            shell.RefreshInspectorPages();
            ((ComboBox)sender).SelectedItem = shell.StyleInspector.ModelOverrideChoice;
            _committingStyle = false;
        }
    }

    private async void InheritSelectedModelStyle(object? sender, RoutedEventArgs args)
    {
        if (_committingStyle || _shell is not { IsBusy: false } shell || !shell.StyleInspector.CanInheritSelectedModelStyle
            || shell.Browser.SelectedModel is not { } model) return;
        _committingStyle = true;
        try
        {
            shell.StopPlayback();
            shell.SynchronizePendingScrub();
            if (!CommitPendingInlineLighting()) return;
            shell.Workspace.InheritModelStyle(model.ObjectId);
        }
        catch (Exception error)
        {
            shell.StyleInspector.ReportStatus(error.Message);
            await ShowErrorAsync(error);
        }
        finally { _committingStyle = false; }
    }

    private async void EnableMissingStylePlugin(object? sender, RoutedEventArgs args)
    {
        if (_committingStyle || _shell is not { IsBusy: false, HasProject: true } shell) return;
        if (!shell.StyleInspector.CanEnableMissingStylePlugin || shell.StyleInspector.MissingStyleId is not { } missingId) return;
        var owner = shell.Workspace.Session;
        _committingStyle = true;
        try
        {
            shell.StopPlayback();
            shell.SynchronizePendingScrub();
            if (!CommitPendingInlineLighting()) return;
            var outcome = await shell.StyleInspector.EnablePluginForStyleAsync(missingId);
            if (!ReferenceEquals(owner, shell.Workspace.Session)) return;
            if (!outcome.Succeeded)
            {
                string diag = outcome.Diagnostic ?? Text("Mda.Text.Plugins.Failed");
                shell.StyleInspector.ReportStatus(diag);
                shell.OperationStatus = diag;
                return;
            }

            string applied = Format("Mda.Text.Context.Inspector.Style.PluginEnabledApplied", missingId);
            shell.StyleInspector.ReportStatus(applied);
            shell.OperationStatus = applied;
        }
        catch (Exception error)
        {
            shell.StyleInspector.ReportStatus(error.Message);
            await ShowErrorAsync(error);
        }
        finally
        {
            _committingStyle = false;
        }
    }

    private async void OpenStyleReplacementDialog(object? sender, RoutedEventArgs args)
    {
        if (_committingStyle || _shell is not { IsBusy: false, HasProject: true } shell) return;
        _committingStyle = true;
        try
        {
            shell.StopPlayback();
            shell.SynchronizePendingScrub();
            if (!CommitPendingInlineLighting()) return;
            if (!await CheckPoseDraftsAsync(preserveModelDrafts: true)) return;
            bool applied = await EditorStyleReplacementDialog.ShowAsync(this, shell.Workspace, shell.PluginService);
            if (applied)
            {
                string message = Text("Mda.Text.StyleReplacement.AppliedStatus");
                shell.StyleInspector.ReportStatus(message);
                shell.OperationStatus = message;
            }
        }
        catch (Exception error)
        {
            shell.StyleInspector.ReportStatus(error.Message);
            await ShowErrorAsync(error);
        }
        finally
        {
            _committingStyle = false;
        }
    }

    private async void ToggleGuessMaterialRoles(object? sender, RoutedEventArgs args)
    {
        if (_committingStyle || _shell is not { IsBusy: false, HasProject: true } shell) return;
        bool nextValue = (sender as CheckBox)?.IsChecked ?? !shell.Workspace.GuessMaterialRoles;
        if (nextValue == shell.Workspace.GuessMaterialRoles) return;
        _committingStyle = true;
        try
        {
            shell.StopPlayback();
            shell.SynchronizePendingScrub();
            if (!CommitPendingInlineLighting()) return;
            if (!await CheckPoseDraftsAsync(preserveModelDrafts: true)) return;
            if (shell.Workspace.SetGuessMaterialRoles(nextValue))
            {
                string message = Text("Mda.Text.Context.Inspector.Style.GuessRolesUpdated");
                shell.RefreshInspectorPages();
                shell.StyleInspector.ReportStatus(message);
                shell.OperationStatus = message;
            }
        }
        catch (Exception error)
        {
            shell.StyleInspector.ReportStatus(error.Message);
            await ShowErrorAsync(error);
        }
        finally
        {
            _committingStyle = false;
            shell.RefreshInspectorPages();
            if (sender is CheckBox box)
                box.IsChecked = shell.Workspace.GuessMaterialRoles;
        }
    }

    private void MaterialListDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (_shell is not { IsBusy: false } shell || !shell.MaterialInspector.CanEdit) return;
        if (args.Source is Visual source
            && (source.FindAncestorOfType<Button>() is not null
                || source.FindAncestorOfType<ScrollBar>() is not null))
            return;
        if ((args.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext is EditorMaterialPropertiesItem item)
            shell.MaterialInspector.SelectedMaterial = item;
        else if (shell.MaterialInspector.SelectedMaterial is null)
            return;
        args.Handled = true;
        EditSelectedMaterial(sender, args);
    }

    /// <summary>Opens the existing model-properties window on its material tab, focused on the page's row.</summary>
    private async void EditSelectedMaterial(object? sender, RoutedEventArgs args)
    {
        if (_shell is not { IsBusy: false } shell || shell.MaterialInspector.ModelObjectId is not { } objectId) return;
        if (!await CheckPoseDraftsAsync()) return;
        try
        {
            var draft = await new EditorModelPropertiesWindow(shell.Workspace, objectId, 1,
                shell.MaterialInspector.SelectedMaterialIndex).ShowDialog<EditorModelPropertiesDraft?>(this);
            if (draft is null || !ReferenceEquals(shell, _shell)) return;
            shell.StopPlayback();
            shell.SynchronizePendingScrub();
            shell.Workspace.ApplyModelProperties(draft);
            shell.OperationStatus = Text("模型属性已应用。");
            shell.RefreshInspectorPages();
        }
        catch (Exception error) { await ShowErrorAsync(error); }
    }
}
