using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using static Mmd.Modern.App.EditorContextMenu;

namespace Mmd.Modern.App;

public partial class EditorWindow
{
    private void InitializeOutlineContextMenus()
    {
        var browser = ModelBrowserSurface;
        browser.AddHandler(PointerPressedEvent, (_, args) =>
        {
            if (args.GetCurrentPoint(browser).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed
                || _shell is not { } shell || args.Source is not Visual source) return;
            if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not EditorModelItem
                && source.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is not EditorElementNode) return;
            shell.ViewportPickedLightId = null;
            if (!shell.IsSceneInspector) return;
            // Clicking the already-selected model or channel raises no SelectionChanged event.
            if (shell.IsLightingPage && !NavigateInspectorPage(EditorInspectorPage.Animation)) args.Handled = true;
            else shell.SceneSelectionIndex = -1;
        }, RoutingStrategies.Tunnel);
        AttachOutlineMenu<ListBoxItem>(browser.FindControl<Grid>("ModelListArea")!, row => ModelContextItems(row?.DataContext as EditorModelItem));
        foreach (var tree in new[] { browser.FindControl<TreeView>("BoneTree")!, browser.FindControl<TreeView>("MorphTree")! })
        {
            bool bone = tree.Name == "BoneTree";
            AttachOutlineMenu<TreeViewItem>(tree, row => TreeContextItems(row?.DataContext as EditorElementNode, bone));
            tree.AddHandler(KeyDownEvent, (_, args) =>
            {
                if (args.Key != Key.Enter || args.KeyModifiers != KeyModifiers.None || _shell is not { IsAnimationInspector: true } shell
                    || tree.SelectedItem is not EditorElementNode { IsLeaf: true }) return;
                var command = bone ? shell.Animation.RegisterPoseCommand : shell.Animation.RegisterKeyframeCommand;
                if (command.CanExecute(null)) { command.Execute(null); args.Handled = true; }
            }, RoutingStrategies.Tunnel);
        }
        SceneObjects.AddHandler(PointerPressedEvent, (_, args) =>
        {
            if (_shell is { } shell && args.GetCurrentPoint(SceneObjects).Properties.IsLeftButtonPressed
                && (args.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null)
                shell.ViewportPickedLightId = null;
        }, RoutingStrategies.Tunnel);
        AttachOutlineMenu<ListBoxItem>(SceneObjects, row => SceneContextItems(row));
        AttachOutlineMenu<ListBoxItem>(LightListArea, row => LightRowContextItems(row?.DataContext));
        WorkspaceLightList.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key != Key.Delete || args.KeyModifiers != KeyModifiers.None || _shell is not { IsBusy: false, IsModernLightingPage: true }
                || _lightBrowserModel?.HasSelection != true) return;
            DeleteViewportLight(this, new RoutedEventArgs());
            args.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    private static void AttachOutlineMenu<TRow>(Control area, Func<TRow?, Control[]> items) where TRow : Control
    {
        // ListBox normally updates selection on right press. Defer it to the target-aware menu
        // request so an existing multiselection and blank-area clicks both retain their targets.
        area.AddHandler(PointerPressedEvent, (_, args) =>
        {
            if (args.GetCurrentPoint(area).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed
                && (args.Source as Visual)?.FindAncestorOfType<ScrollBar>(includeSelf: true) is null)
                args.Handled = true;
        }, RoutingStrategies.Tunnel);
        area.AddHandler(ContextRequestedEvent, (_, args) =>
        {
            if (args.Source is not Visual source || source.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null) return;
            var row = source.FindAncestorOfType<TRow>(includeSelf: true);
            Point? position = args.TryGetPosition(area, out var point) ? point : null;
            if (row is null && position is null)
            {
                if (source.FindAncestorOfType<ListBox>(includeSelf: true) is { SelectedItem: { } selected } list)
                    row = list.ContainerFromItem(selected) as TRow;
                else if (area is TreeView { SelectedItem: { } node } tree)
                    row = tree.GetVisualDescendants().OfType<TRow>().FirstOrDefault(item => ReferenceEquals(item.DataContext, node));
            }
            var controls = items(row);
            if (controls.Length == 0) return;
            row?.Focus();
            EditorContextMenu.Open(area, position, controls);
            args.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    private Control[] ModelContextItems(EditorModelItem? model)
    {
        if (_shell is not { } shell) return [];
        var browser = shell.Browser;
        if (model is null)
            return [Item("Mda.Text.b6ab3d8870aa", () => shell.ImportModelCommand.Execute(null), shell.HasProject && browser.CanImportModel)];
        if (!browser.Models.Contains(model) || !NavigateInspectorPage(EditorInspectorPage.Animation)) return [];
        shell.ViewportPickedLightId = null;
        browser.SelectedModel = model;
        shell.SceneSelectionIndex = -1;
        bool ready = shell.HasProject && !shell.IsBusy;
        return [
            Item("Mda.Text.814c34f6f2d1", () => OpenModelProperties(this, new RoutedEventArgs()), ready),
            Item("Mda.Text.Ux.ModelBrowser.RigSettings", () => OpenRigSettings(this, new RoutedEventArgs()), ready),
            Item("Mda.Text.25aed4b73b5c", shell.ImportMotionCommand),
            FocusSelectionMenuItem(modelOnly: true), new Separator(),
            Item("Mda.Text.Context.ReplaceModel.Action", browser.ReplaceModelCommand!),
            Item("Mda.Text.6135d4159e89", browser.RemoveModelCommand!, "Delete")
        ];
    }

    private Control[] TreeContextItems(EditorElementNode? node, bool bone)
    {
        if (node is null || _shell is not { } shell || shell.Browser.SelectedModel is null
            || !NavigateInspectorPage(EditorInspectorPage.Animation)) return [];
        shell.ViewportPickedLightId = null;
        var browser = shell.Browser;
        browser.HierarchyTab = bone ? 0 : 1;
        shell.SceneSelectionIndex = -1;
        if (bone) browser.SelectBoneNode(node);
        else browser.SelectMorphNode(node);
        if (node.IsLeaf)
            return bone ? [.. AnimationContextItems(shell.Animation), new Separator(), FocusSelectionMenuItem()]
                : AnimationContextItems(shell.Animation);
        var groups = bone ? browser.BoneGroups : browser.MorphGroups;
        return [
            Item(node.IsExpanded ? "Mda.Text.Menu.Collapse" : "Mda.Text.Menu.Expand", () => node.IsExpanded = !node.IsExpanded),
            new Separator(),
            Item("Mda.Text.Menu.ExpandAll", () => { foreach (var group in groups) group.IsExpanded = true; }, groups.Any(group => !group.IsExpanded)),
            Item("Mda.Text.Menu.CollapseAll", () => { foreach (var group in groups) group.IsExpanded = false; }, groups.Any(group => group.IsExpanded))
        ];
    }

    internal Control[] AnimationContextItems(EditorAnimationViewModel animation)
    {
        if (animation.IsMorphSelected)
            return [
                Item("Mda.Text.3cf1e4dc9adc", animation.RegisterKeyframeCommand, "Enter"),
                Item("Mda.Text.Menu.ZeroMorph", () => _ = animation.ZeroMorphAtCurrentFrameAsync(),
                    animation.MorphWeight is not (null or 0) && animation.CanRegister)
            ];
        if (!animation.IsBoneSelected) return [];
        return [
            Item("Mda.Text.3cf1e4dc9adc", animation.RegisterPoseCommand, "Enter"),
            Item("Mda.Text.Menu.ResetBonePose", animation.ResetPoseCommand),
            new Separator(),
            Item("Mda.Text.Layout.CopyPose", animation.CopyPoseCommand),
            Item("Mda.Text.Layout.MirrorPose", animation.PasteMirroredPoseCommand)
        ];
    }

    private Control[] SceneContextItems(ListBoxItem? row)
    {
        if (row is null || _shell is not { } shell) return [];
        if (row == OutlineTrackLight)
        {
            if (shell.HasProject) ActivateTrackLight();
            else shell.SceneSelectionIndex = 2;
            if (!shell.SceneInspector.ShowsPrimaryLightTrack) return [];
            return [.. MainLightContextItems(shell.SceneInspector), Item("Mda.Text.Menu.DefaultMainLight",
                () => shell.SceneInspector.AimLight("default"), shell.HasProject && !shell.IsBusy && shell.SceneInspector.CanEdit
                    && shell.SceneInspector.LightPose.Direction != EditorSceneInspectorViewModel.DefaultLightDirection)];
        }
        if (!NavigateInspectorPage(EditorInspectorPage.Animation)) return [];
        shell.ViewportPickedLightId = null;
        shell.SceneSelectionIndex = row == OutlineCamera ? 1 : 0;
        return row == OutlineCamera ? [.. CameraContextItems(shell.SceneInspector), new Separator(), ResetViewMenuItem()]
            : [Item("Mda.Text.Context.Inspector.Style.PreviewQuality", () => OpenPreviewSettings(this, new RoutedEventArgs()), !shell.IsBusy),
                WorldGuidesMenuItem()];
    }

    internal Control[] CameraContextItems(EditorSceneInspectorViewModel scene) => [
        Item("Mda.Text.a619bd96a115", scene.RegisterCameraKeyframeCommand),
        Item("Mda.Text.70403791a994", scene.RevertDraftCommand)
    ];

    internal Control[] MainLightContextItems(EditorSceneInspectorViewModel scene)
    {
        bool ready = _shell is { IsBusy: false, HasProject: true } && scene.CanEdit;
        return [
            Item("Mda.Text.e4b84a93325e", scene.RegisterLightKeyframeCommand),
            Item("Mda.Text.Context.Lighting.AimFromCamera", scene.AimLightFromCamera, ready && !scene.HasCameraValidation)
        ];
    }

    private Control[] LightRowContextItems(object? row)
    {
        if (_shell?.SupportsModernLightingWorkspace != true) return [];
        if (row is null) return AddLightContextItems();
        // Entering the lighting page can rebuild the inspector. Resolve the original row by ID.
        if (!NavigateInspectorPage(EditorInspectorPage.Lighting) || _lightBrowserModel is not { } model) return [];
        object? target = row switch
        {
            EditorLightGroupItem group => model.Groups.FirstOrDefault(value => value.Id == group.Id),
            EditorNativeLightItem light => model.Lights.FirstOrDefault(value => value.Id == light.Id),
            _ => null
        };
        if (target is null) return [];
        if (!model.SelectedRows.Contains(target)) model.SelectRows([target], target);
        return SelectedLightContextItems();
    }

    internal Control[] SelectedLightContextItems()
    {
        if (_shell is not { SupportsModernLightingWorkspace: true } shell || _lightBrowserModel is not { HasSelection: true } model) return [];
        bool canEdit = !shell.IsBusy && shell.HasProject;
        var rows = model.SelectedRows;
        var targetLights = model.SelectedLightIds;
        var targetGroups = model.SelectedGroupIds;
        var document = shell.Workspace.DocumentId;
        MenuItem ActionItem(string label, Func<EditorLightingViewModel, bool> action, bool allowed = true, string? gesture = null) =>
            Item(label, () =>
            {
                if (_shell is not { IsBusy: false, HasProject: true } current || current.Workspace.DocumentId != document
                    || _lightBrowserModel is not { } selection || !selection.SelectedLightIds.SequenceEqual(targetLights)
                    || !selection.SelectedGroupIds.SequenceEqual(targetGroups)) return;
                NativeLightInspector.ApplySelectionAction(action);
            }, canEdit && allowed, gesture);
        List<Control> result = [
            ActionItem("Mda.Text.Context.Lighting.EnableSelection", m => m.SetSelectedEnabled(true), model.CanEnableSelection),
            ActionItem("Mda.Text.Context.Lighting.DisableSelection", m => m.SetSelectedEnabled(false), model.CanDisableSelection),
            new Separator(),
            ActionItem("Mda.Text.63d90d977348", m => m.DuplicateSelection(), model.CanDuplicateSelection),
            ActionItem("Mda.Text.2f9daa828907", m => { m.RemoveSelection(); return true; }, gesture: "Delete"),
            new Separator()
        ];
        if (model.CanDissolveSelection) result.Add(ActionItem("Mda.Text.Context.Lighting.DissolveGroup", m => m.DissolveSelectedGroups()));
        else
        {
            result.Add(ActionItem("Mda.Text.Context.Lighting.GroupAction", m => m.GroupSelection(), model.CanGroupSelection));
            if (rows.OfType<EditorNativeLightItem>().Any(light => light.GroupId.HasValue))
                result.Add(ActionItem("Mda.Text.Menu.RemoveFromLightGroup", m => m.RemoveSelectedLightsFromGroups()));
        }
        return result.ToArray();
    }

    private void RefreshAddLightMenu(object? sender, EventArgs args)
    {
        if (sender is not MenuFlyout flyout) return;
        foreach (var item in flyout.Items.OfType<MenuItem>())
        {
            if (item.Tag is string tag && Enum.TryParse<SceneLightingBuiltinPreset>(tag, out var preset))
                item.IsEnabled = NativeLightInspector.CanAddBuiltinLighting(preset);
            else if (item.Name == "ImportLightGroupMenuItem") item.IsEnabled = StorageProvider.CanOpen;
        }
    }

    internal Control[] AddLightContextItems()
    {
        if (_shell is not { SupportsModernLightingWorkspace: true } shell) return [];
        var model = _lightBrowserModel;
        bool canAdd = shell.HasProject && !shell.IsBusy && model?.CanAddLight == true;
        MenuItem Light(string label, SceneLightKind kind) => Item(label, () => AddViewportLight(kind), canAdd);
        MenuItem Preset(string label, SceneLightingBuiltinPreset kind) =>
            Item(label, () => AddLightGroup(kind), canAdd && NativeLightInspector.CanAddBuiltinLighting(kind));
        return [
            Light("Mda.Text.dcf867d64ba7", SceneLightKind.Directional), Light("Mda.Text.164901accbd4", SceneLightKind.Point),
            Light("Mda.Text.5af0185beb3e", SceneLightKind.Spot), Light("Mda.Text.Context.Lighting.Area", SceneLightKind.Area),
            new Separator(),
            Preset("Mda.Text.Layout.ThreePoint", SceneLightingBuiltinPreset.PortraitSoft),
            Preset("Mda.Text.Context.Lighting.Presets.IndoorWindow", SceneLightingBuiltinPreset.IndoorWindow),
            Item("Mda.Text.Layout.ImportLights", () => ImportOutlineLightingPreset(this, new RoutedEventArgs()), canAdd && StorageProvider.CanOpen)
        ];
    }
}
