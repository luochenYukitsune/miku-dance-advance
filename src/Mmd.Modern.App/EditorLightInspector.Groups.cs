using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Core;

namespace Mmd.Modern.App;

public partial class EditorLightInspector
{
    private bool performingSelectionAction;
    private sealed record GroupParameterEdit(EditorLightParameterSlider Slider, SceneLightingSnapshot Source, Guid? Document,
        Guid[] LightIds, Guid[] GroupIds, Dictionary<Guid, EditorRecoveryNativeLightDraft> Lights,
        Dictionary<Guid, EditorRecoveryLightGroupDraft> Groups, Guid? EditedGroup);
    private GroupParameterEdit? groupParameterEdit;

    public bool RemoveSelectedLights() => ApplySelectionAction(model => { model.RemoveSelection(); return true; });
    private void DuplicateLights(object? sender, RoutedEventArgs args) => ApplySelectionAction(model => model.DuplicateSelection());
    private void GroupLights(object? sender, RoutedEventArgs args) => ApplySelectionAction(model => model.GroupSelection());
    private void DissolveLightGroups(object? sender, RoutedEventArgs args) => ApplySelectionAction(model => model.DissolveSelectedGroups());
    private void EnableLights(object? sender, RoutedEventArgs args) => ApplySelectionAction(model => model.SetSelectedEnabled(true));
    private void DisableLights(object? sender, RoutedEventArgs args) => ApplySelectionAction(model => model.SetSelectedEnabled(false));
    private void SetBatchIntensity(object? sender, RoutedEventArgs args)
    {
        if (this.FindControl<EditorNumericUpDown>("BatchLightIntensityInput") is { IsInputValid: false })
        { ViewModel?.SetValidationMessage("Mda.Text.Context.Lighting.PositiveBrightness"); return; }
        ApplySelectionAction(model => model.SetSelectedIntensity(model.BatchIntensity));
    }
    internal bool ApplySelectionAction(Func<EditorLightingViewModel, bool> action)
    {
        if (!modernParameters || refreshing || committing || recoveryRestoreDepth != 0 || EditStartingRequested?.Invoke() == false || !TryCommit()
            || ViewModel is not { } model || !model.HasSelection) return false;
        performingSelectionAction = true;
        try { return action(model) && TryCommit(); }
        finally { performingSelectionAction = false; }
    }

    // This field is an optional batch command argument. Its unfinished text is not project state
    // and cannot block a group switch, a slider gesture or another ordinary property commit.
    private bool HasInvalidCommitInputs() => this.GetVisualDescendants().OfType<Control>().Any(control =>
        control.IsEffectivelyEnabled && control.IsEffectivelyVisible
        && control.Name != "BatchLightIntensityInput"
        && !control.GetVisualAncestors().OfType<Control>().Any(parent => parent.Name == "BatchLightIntensityInput")
        && (control is EditorNumericUpDown { IsInputValid: false } || DataValidationErrors.GetHasErrors(control)));

    private void BeginGroupParameterEdit(object? sender, CancelEventArgs args)
    {
        if (sender is not EditorLightParameterSlider slider || !modernParameters || refreshing || recoveryRestoreDepth != 0
            || EditStartingRequested?.Invoke() == false || !TryCommit() || ViewModel is not { HasSelection: true } model)
        { args.Cancel = true; return; }
        var group = slider.DataContext as EditorLightGroupItem;
        groupParameterEdit = new(slider, source, sourceDocumentId, model.SelectedLightIds.ToArray(), model.SelectedGroupIds.ToArray(),
            model.Lights.Where(light => model.SelectedLightIds.Contains(light.Id)).ToDictionary(light => light.Id, light => light.CaptureRecoveryDraft()),
            model.Groups.Where(item => group?.Id == item.Id).ToDictionary(item => item.Id, item => item.CaptureDraft()), group?.Id);
    }
    private void PreviewGroupParameterEdit(object? sender, EventArgs args)
    {
        if (updatingParameter || groupParameterEdit is not { } edit || !ReferenceEquals(edit.Slider, sender) || ViewModel is not { } model) return;
        if (!ReferenceEquals(edit.Source, source) || edit.Document != sourceDocumentId
            || !edit.LightIds.SequenceEqual(model.SelectedLightIds) || !edit.GroupIds.SequenceEqual(model.SelectedGroupIds))
        { CancelActiveGroupParameterEdit(); return; }
        updatingParameter = true;
        try
        {
            if (edit.EditedGroup is { } id)
                model.Groups.Single(group => group.Id == id).Intensity = EditorLightParameterScale.BrightnessValue(edit.Slider.Value);
            else if (!model.ScaleSelectedIntensity(edit.Source, (decimal)Math.Pow(10, edit.Slider.Value)))
            { CancelActiveGroupParameterEdit(); return; }
            if (!model.TryCreate(out var draft) || draft is null || PreviewRequested?.Invoke(draft) == false)
                CancelActiveGroupParameterEdit();
        }
        finally { updatingParameter = false; }
    }
    private void CompleteGroupParameterEdit(object? sender, EventArgs args)
    {
        if (groupParameterEdit is not { } edit || !ReferenceEquals(edit.Slider, sender)) return;
        groupParameterEdit = null;
        try { TryCommit(); }
        finally { ViewModel?.ResetRelativeBrightness(); PreviewRequested?.Invoke(null); }
    }
    private void CancelGroupParameterEdit(object? sender, EventArgs args)
    {
        if (groupParameterEdit is { } edit && ReferenceEquals(edit.Slider, sender)) CancelActiveGroupParameterEdit();
    }
    private void CancelActiveGroupParameterEdit()
    {
        if (groupParameterEdit is not { } edit) return;
        groupParameterEdit = null;
        edit.Slider.CancelEdit();
        if (ViewModel is { } model)
        {
            foreach (var light in model.Lights)
                if (edit.Lights.TryGetValue(light.Id, out var before)) light.RestoreInput(before);
            foreach (var group in model.Groups)
                if (edit.Groups.TryGetValue(group.Id, out var before)) group.RestoreInput(before);
            model.ResetRelativeBrightness();
        }
        PreviewRequested?.Invoke(null);
    }
}
