using System.Numerics;
using Avalonia.Controls;
using Mmd.Modern.Core;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>Authored model/bone names for the light inspector; this context owns no animation clock.</summary>
public sealed record EditorLightParentModel(Guid ObjectId, string Name, IReadOnlyList<string> BoneNames);
public sealed record EditorLightParentModelChoice(Guid? ObjectId, string AuthoredName)
{
    public string Label => ObjectId is null ? Text("Mda.Text.Context.Lighting.ParentNone") : AuthoredName;
    public override string ToString() => Label;
}
public sealed record EditorLightParentBoneChoice(int? BoneIndex, string AuthoredName)
{
    public string Label => BoneIndex is null ? Text("Mda.Text.Context.Lighting.ParentWholeModel") : $"{BoneIndex} · {AuthoredName}";
    public override string ToString() => Label;
}

public partial class EditorLightInspector
{
    private EditorLightParentModel[] parentModels = [];
    private Func<SceneLightParent, Matrix4x4>? resolveParentTransform;
    private bool refreshingParents;

    internal void ConfigureParentContext(IEnumerable<EditorLightParentModel> models,
        Func<SceneLightParent, Matrix4x4>? resolve)
    {
        // Copy the short choice metadata; never retain a mutable collection of scene objects.
        parentModels = models.Select(model => model with { BoneNames = model.BoneNames.ToArray() }).ToArray();
        resolveParentTransform = resolve;
        RefreshParentControls();
    }

    private void RefreshParentControls()
    {
        if (refreshingParents) return;
        refreshingParents = true;
        try
        {
            var panel = this.FindControl<StackPanel>("LightParentProperties")!;
            var models = this.FindControl<ComboBox>("LightParentModelChoice")!;
            var bones = this.FindControl<ComboBox>("LightParentBoneChoice")!;
            bool available = modernParameters && ViewModel is { ShowSingleLight: true } && resolveParentTransform is not null;
            panel.IsVisible = available;
            models.IsEnabled = available;
            var parent = ViewModel?.SelectedLight is { } selected
                ? source.Lights.FirstOrDefault(light => light.Id == selected.Id)?.Parent : null;
            models.ItemsSource = new[] { new EditorLightParentModelChoice(null, "") }
                .Concat(parentModels.Select(model => new EditorLightParentModelChoice(model.ObjectId, model.Name))).ToArray();
            models.SelectedItem = models.Items.Cast<EditorLightParentModelChoice>().FirstOrDefault(model => model.ObjectId == parent?.ObjectId);
            var target = parent is null ? null : parentModels.FirstOrDefault(model => model.ObjectId == parent.ObjectId);
            bones.ItemsSource = new[] { new EditorLightParentBoneChoice(null, "") }
                .Concat(target?.BoneNames.Select((name, index) => new EditorLightParentBoneChoice(index, name)) ?? []).ToArray();
            bones.SelectedItem = bones.Items.Cast<EditorLightParentBoneChoice>().FirstOrDefault(bone => bone.BoneIndex == parent?.BoneIndex);
            bones.IsEnabled = available && target is not null;
        }
        finally { refreshingParents = false; }
        RefreshReceiverControls();
    }

    private void SelectLightParentModel(object? sender, SelectionChangedEventArgs args)
    {
        if (refreshingParents || sender is not ComboBox { SelectedItem: EditorLightParentModelChoice choice }) return;
        ChangeLightParent(choice.ObjectId is { } id ? new(id) : null);
    }

    private void SelectLightParentBone(object? sender, SelectionChangedEventArgs args)
    {
        if (refreshingParents || sender is not ComboBox { SelectedItem: EditorLightParentBoneChoice choice }
            || this.FindControl<ComboBox>("LightParentModelChoice")?.SelectedItem is not EditorLightParentModelChoice { ObjectId: { } id }) return;
        ChangeLightParent(new(id, choice.BoneIndex));
    }

    private void ChangeLightParent(SceneLightParent? parent)
    {
        try
        {
            if (!modernParameters || refreshing || committing || recoveryRestoreDepth != 0
                || ViewModel is not { ShowSingleLight: true, SelectedLight: { } selected } model || resolveParentTransform is not { } resolve) return;
            var before = source.Lights.Single(light => light.Id == selected.Id);
            if (before.Parent == parent) return;
            if (EditStartingRequested?.Invoke() == false) return;
            CancelActiveParameterEdit();
            if (!TryReadLightingDraft(out var draft) || draft is null) return;
            if (parent is not null)
            {
                var target = parentModels.SingleOrDefault(model => model.ObjectId == parent.ObjectId);
                if (target is null || parent.BoneIndex is { } bone && (uint)bone >= (uint)target.BoneNames.Count)
                    throw new InvalidOperationException("The selected light parent is no longer available.");
            }
            var current = draft.Snapshot.Lights.Single(light => light.Id == selected.Id);
            var world = current.Parent is { } oldParent ? SceneLightTransform.ToWorld(current, resolve(oldParent)) : current;
            var attached = parent is null ? world : SceneLightTransform.ToLocal(world, parent, resolve(parent));
            var snapshot = draft.Snapshot.WithLights(draft.Snapshot.Lights.Select(light => light.Id == current.Id ? attached : light));
            var candidate = new EditorLightingDraft(snapshot, draft.SelectedLightId, draft.SelectedLightIds, draft.SelectedGroupIds);
            committing = true;
            bool accepted;
            try { accepted = CommitRequested?.Invoke(candidate) == true; }
            finally { committing = false; }
            if (!accepted) { model.SetValidationMessage("Mda.Text.Context.Lighting.ParentChangeFailed"); return; }
            // A successful attachment becomes the next item's immutable original, including Parent.
            // This also prevents an ordinary subsequent numeric edit from restoring the old parent.
            // Every validated edit is committed now: do not restore its previous world-space numeric
            // draft over the new local transform while SetLighting preserves unrelated pending input.
            source = draft.Snapshot;
            SetLighting(snapshot, candidate.SelectedLightId, sourceDocumentId, candidate.SelectedLightIds);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        { ViewModel?.SetValidationMessage("Mda.Text.Context.Lighting.ParentChangeFailed"); }
        finally { RefreshParentControls(); }
    }
}
