using System.Collections.Immutable;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Core;

namespace Mmd.Modern.App;

public partial class EditorLightInspector
{
    private sealed record GlobalParameterEdit(EditorLightParameterSlider Slider, SceneLightingSnapshot Source,
        Guid? Document, EditorRecoveryGlobalLightingDraft Before);
    private GlobalParameterEdit? globalParameterEdit;
    private ImmutableSortedDictionary<string, string> rawGlobalNumericText = ImmutableSortedDictionary<string, string>.Empty;
    public Action? PreviewQualityRequested { get; set; }

    private void OpenGlobalPreviewQuality(object? sender, RoutedEventArgs args)
    {
        if (modernParameters && recoveryRestoreDepth == 0) PreviewQualityRequested?.Invoke();
    }
    private void CommitGlobalInput(object? sender, RoutedEventArgs args)
    {
        if (!refreshing && !committing && globalParameterEdit is null && recoveryRestoreDepth == 0) TryCommitGlobalSettings();
    }
    private bool TryReadGlobalSettings(out SceneLightingSettings? settings)
    {
        settings = null;
        if (ViewModel is not { } model) return false;
        CaptureRawControlText();
        if (rawGlobalNumericText.Count > 0 || EditorRecoveryGlobalLightingDraft.NumericNames.Any(name =>
            this.FindControl<EditorNumericUpDown>(name) is { Text: not null, IsInputValid: false }))
        { model.SetValidationMessage("Mda.Text.Context.Lighting.Global.Invalid"); return false; }
        try { settings = model.BuildGlobalSettings(); return true; }
        catch (ArgumentException) { model.SetValidationMessage("Mda.Text.Context.Lighting.Global.Invalid"); return false; }
    }
    // The global controls edit this document's lighting snapshot independently of unfinished
    // per-lamp inputs. Neither a global gesture nor its Undo can publish or discard those inputs.
    private bool TryCommitGlobalSettings()
    {
        if (!modernParameters || refreshing || committing || recoveryRestoreDepth != 0 || ViewModel is null) return false;
        if (!TryReadGlobalSettings(out var settings) || settings is null) return false;
        if (settings == source.Settings) return true;
        var draft = new EditorLightingDraft(source.WithSettings(settings), ViewModel.SelectedLight?.Id,
            ViewModel.SelectedLightIds, ViewModel.SelectedGroupIds);
        committing = true;
        try
        {
            if (CommitRequested?.Invoke(draft) != true) return false;
            source = draft.Snapshot;
            ViewModel.SetValidationMessage("");
            return true;
        }
        finally { committing = false; }
    }
    private void BeginGlobalParameterEdit(object? sender, CancelEventArgs args)
    {
        if (sender is not EditorLightParameterSlider slider || !modernParameters || refreshing || committing
            || recoveryRestoreDepth != 0 || EditStartingRequested?.Invoke() == false || ViewModel is not { } model)
        { args.Cancel = true; return; }
        CancelActiveParameterEdit(); CaptureRawControlText();
        globalParameterEdit = new(slider, source, sourceDocumentId, model.CaptureGlobalDraft() with { NumericText = rawGlobalNumericText });
    }
    private void PreviewGlobalParameterEdit(object? sender, EventArgs args)
    {
        if (updatingParameter || globalParameterEdit is not { } edit || !ReferenceEquals(sender, edit.Slider) || ViewModel is not { } model) return;
        if (!ReferenceEquals(source, edit.Source) || sourceDocumentId != edit.Document)
        { CancelActiveGlobalParameterEdit(); return; }
        updatingParameter = true;
        try
        {
            string name;
            if (Equals(edit.Slider.Tag, "environment"))
            { model.EnvironmentStrength = (decimal)edit.Slider.Value; name = "GlobalEnvironmentInput"; }
            else if (Equals(edit.Slider.Tag, "exposure"))
            { model.ExposureEv = (decimal)edit.Slider.Value; name = "GlobalExposureInput"; }
            else return;
            rawGlobalNumericText = rawGlobalNumericText.Remove(name);
            var input = this.FindControl<EditorNumericUpDown>(name)!;
            input.SetCurrentValue(NumericUpDown.TextProperty, input.Value?.ToString(input.FormatString, input.NumberFormat));
            if (!TryReadGlobalSettings(out var settings) || settings is null
                || PreviewRequested?.Invoke(new(source.WithSettings(settings), model.SelectedLight?.Id,
                    model.SelectedLightIds, model.SelectedGroupIds)) == false)
                CancelActiveGlobalParameterEdit();
        }
        finally { updatingParameter = false; }
    }
    private void CompleteGlobalParameterEdit(object? sender, EventArgs args)
    {
        if (globalParameterEdit is not { } edit || !ReferenceEquals(sender, edit.Slider)) return;
        globalParameterEdit = null;
        try { TryCommitGlobalSettings(); }
        finally { PreviewRequested?.Invoke(null); }
    }
    private void CancelGlobalParameterEdit(object? sender, EventArgs args)
    {
        if (globalParameterEdit is { } edit && ReferenceEquals(sender, edit.Slider)) CancelActiveGlobalParameterEdit();
    }
    private void CancelActiveGlobalParameterEdit()
    {
        if (globalParameterEdit is not { } edit) return;
        globalParameterEdit = null; edit.Slider.CancelEdit();
        ViewModel?.RestoreGlobalDraft(edit.Before);
        rawGlobalNumericText = edit.Before.NumericText;
        foreach (string name in EditorRecoveryGlobalLightingDraft.NumericNames)
        {
            var input = this.FindControl<EditorNumericUpDown>(name)!;
            input.SetCurrentValue(NumericUpDown.TextProperty, rawGlobalNumericText.GetValueOrDefault(name)
                ?? input.Value?.ToString(input.FormatString, input.NumberFormat));
        }
        PreviewRequested?.Invoke(null);
    }
}
