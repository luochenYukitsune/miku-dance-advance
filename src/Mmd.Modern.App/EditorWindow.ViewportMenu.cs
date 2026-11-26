using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Mmd.Modern.Application;

namespace Mmd.Modern.App;

public partial class EditorWindow
{
    private void OpenViewportContextMenu(Point position) =>
        EditorContextMenu.Open(this.FindControl<EditorViewportHost>("SceneViewport")!, position, ViewportMenuItems());

    internal Control[] ViewportMenuItems()
    {
        if (_shell is not { } shell) return [];
        List<Control> items = [];
        if (shell.HasProject)
        {
            if (HasViewportLightTarget(shell)) items.AddRange(SelectedLightContextItems());
            else if (shell.IsLightingPage) items.AddRange(MainLightContextItems(shell.SceneInspector));
            else if (shell.IsCameraInspector) items.AddRange(CameraContextItems(shell.SceneInspector));
            else if (shell.IsAnimationInspector) items.AddRange(AnimationContextItems(shell.Animation));
        }

        items.Add(new Separator());
        items.Add(ResetViewMenuItem());
        items.Add(FocusSelectionMenuItem());
        items.Add(WorldGuidesMenuItem());
        if (boneController is { IsEnabled: true } bones && shell.Browser.HasSelectedModel)
            items.Add(CheckItem("Mda.Text.Menu.ShowBones", bones.ShowBones, () => bones.ShowBones = !bones.ShowBones));
        items.Add(new Separator());
        items.Add(EditorContextMenu.Item("Mda.Text.b6ab3d8870aa", shell.ImportModelCommand));
        if (shell.SupportsModernLightingWorkspace)
        {
            var lights = EditorContextMenu.VisibleItems(AddLightContextItems());
            if (lights.Length != 0)
                items.Add(new MenuItem { Header = EditorLocalization.Text("Mda.Text.0bb6877c08de"), ItemsSource = lights });
        }
        items.Add(new Separator());
        items.Add(EditorContextMenu.Item("Mda.Text.926a50b98ece", shell.UndoCommand, "Ctrl+Z"));
        items.Add(EditorContextMenu.Item("Mda.Text.03717b6f1070", shell.RedoCommand, "Ctrl+Y"));
        items.Add(EditorContextMenu.Item("Mda.Text.cafed7fda215", () => OpenPreviewSettings(this, new RoutedEventArgs()), !shell.IsBusy));
        return EditorContextMenu.VisibleItems(items);
    }

    private bool HasViewportLightTarget(EditorShellViewModel shell) =>
        shell.SupportsModernLightingWorkspace && _lightBrowserModel is { HasSelection: true } selection
        && (shell.IsLightingPage || shell.ViewportPickedLightId is { } picked && selection.SelectedLightIds.Contains(picked));

    private static MenuItem CheckItem(string label, bool value, Action toggle)
    {
        var item = EditorContextMenu.Item(label, toggle);
        item.ToggleType = MenuItemToggleType.CheckBox;
        item.IsChecked = value;
        return item;
    }

    private MenuItem WorldGuidesMenuItem() => CheckItem("Mda.Text.Ux.View.ShowWorldGuides", _shell!.ShowWorldGuides,
        () => _shell.ShowWorldGuides = !_shell.ShowWorldGuides);

    private MenuItem ResetViewMenuItem() => EditorContextMenu.Item("Mda.Text.Menu.ResetView",
        () => this.FindControl<EditorViewportHost>("SceneViewport")!.ResetView(), _shell is { IsBusy: false });

    private MenuItem FocusSelectionMenuItem(bool modelOnly = false)
    {
        var shell = _shell!;
        var model = shell.Browser.SelectedModel;
        var bone = modelOnly ? null : shell.Animation.SelectedBoneIndex;
        bool targetSelected = shell.IsAnimationInspector && !shell.IsLightingPage && !HasViewportLightTarget(shell) && model is not null
            && shell.Workspace.Session?.Actors.Any(actor => actor.ObjectId == model.ObjectId && actor.Visible) == true;
        return EditorContextMenu.Item(modelOnly ? "Mda.Text.Menu.FocusModel" : "Mda.Text.Menu.FocusSelection", () =>
        {
            if (!targetSelected || shell.IsBusy || !ReferenceEquals(shell.Browser.SelectedModel, model)
                || !modelOnly && bone != shell.Animation.SelectedBoneIndex) return;
            var id = model!.ObjectId;
            var snapshot = CaptureEditorSnapshot([new SceneObjectRenderRequest(id, bone is int i ? [i] : [],
                includeSourceGeometry: !bone.HasValue)]);
            if (!snapshot.SceneInputs.TryGetValue(id, out var inputs)) return;
            var viewport = this.FindControl<EditorViewportHost>("SceneViewport")!;
            if (bone is int index && inputs.BoneWorldMatrices.TryGetValue(index, out var matrix))
                viewport.FocusOn(matrix.Translation);
            else if (inputs.SourceGeometry is { IsEmpty: false } mesh)
            {
                var min = new Vector3(float.PositiveInfinity);
                var max = new Vector3(float.NegativeInfinity);
                for (int chunk = 0; chunk < mesh.VertexChunkCount; chunk++)
                    foreach (var vertex in mesh.GetVertexChunk(chunk))
                    {
                        min = Vector3.Min(min, vertex.Position);
                        max = Vector3.Max(max, vertex.Position);
                    }
                viewport.FocusOn(Vector3.Transform((min + max) / 2, mesh.World));
            }
            else if (shell.Workspace.Session?.Objects.FirstOrDefault(actor => actor.Id == id) is { } placement)
                viewport.FocusOn(placement.Position);
        }, targetSelected && shell.HasProject && !shell.IsBusy);
    }
}
