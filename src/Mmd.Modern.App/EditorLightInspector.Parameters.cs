using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Core;

namespace Mmd.Modern.App;

public partial class EditorLightInspector
{
    private sealed record ParameterEdit(EditorLightParameterSlider Slider, EditorNativeLightItem Light,
        EditorRecoveryNativeLightDraft Before, SceneLightingSnapshot Source, Guid? Document);
    private ParameterEdit? parameterEdit;
    private bool updatingParameter;
    private sealed record PendingLightInput(NativeSceneLight Committed, EditorRecoveryNativeLightDraft Draft,
        string? Hex, ImmutableSortedDictionary<string, string> Numeric);
    private sealed record PendingGroupInput(SceneLightGroup Committed, EditorRecoveryLightGroupDraft Draft);
    private sealed record PendingLightingInput(Guid? Document, Dictionary<Guid, PendingLightInput> Lights,
        Dictionary<Guid, PendingGroupInput> Groups, decimal? BatchIntensity,
        SceneLightingSettings CommittedSettings, EditorRecoveryGlobalLightingDraft Globals);
    // The existing document/history owns the key. Closing a document or releasing its history
    // releases these unfinished inputs too; the inspector never retains committed document trees.
    private readonly ConditionalWeakTable<SceneLightingSnapshot, PendingLightingInput> pendingInputs = new();

    private void BeginParameterEdit(object? sender, CancelEventArgs args)
    {
        if (sender is not EditorLightParameterSlider slider || !modernParameters || refreshing || recoveryRestoreDepth != 0
            || EditStartingRequested?.Invoke() == false || !TryCommit() || ViewModel is not { ShowSingleLight: true, SelectedLight: { } light })
        { args.Cancel = true; return; }
        parameterEdit = new(slider, light, light.CaptureRecoveryDraft(), source, sourceDocumentId);
    }
    private void PreviewParameterEdit(object? sender, EventArgs args)
    {
        if (updatingParameter || parameterEdit is not { } edit || !ReferenceEquals(edit.Slider, sender)) return;
        if (!ReferenceEquals(edit.Source, source) || edit.Document != sourceDocumentId || ViewModel?.SelectedLight?.Id != edit.Light.Id)
        { CancelActiveParameterEdit(); return; }
        updatingParameter = true;
        try
        {
            decimal value = (decimal)edit.Slider.Value;
            switch (edit.Slider.Tag)
            {
                case "brightness": edit.Light.Intensity = EditorLightParameterScale.BrightnessValue(edit.Slider.Value); break;
                case "distance": edit.Light.Range = EditorLightParameterScale.DistanceValue(edit.Slider.Value); break;
                case "width": edit.Light.BeamWidthDegrees = value; break;
                case "softness": edit.Light.EdgeSoftnessPercent = value; break;
                case "areaWidth": edit.Light.AreaWidth = EditorLightParameterScale.DistanceValue(edit.Slider.Value); break;
                case "areaHeight": edit.Light.AreaHeight = EditorLightParameterScale.DistanceValue(edit.Slider.Value); break;
                default: return;
            }
            if (ViewModel.TryCreate(out var draft) && draft is not null && PreviewRequested?.Invoke(draft) == false)
                CancelActiveParameterEdit();
        }
        finally { updatingParameter = false; }
    }
    private void CompleteParameterEdit(object? sender, EventArgs args)
    {
        if (parameterEdit is not { } edit || !ReferenceEquals(edit.Slider, sender)) return;
        parameterEdit = null;
        try { TryCommit(); }
        finally { PreviewRequested?.Invoke(null); }
    }
    private void CancelParameterEdit(object? sender, EventArgs args)
    {
        if (parameterEdit is { } edit && ReferenceEquals(edit.Slider, sender)) CancelActiveParameterEdit();
    }
    private void CancelActiveParameterEdit()
    {
        CancelActiveGroupParameterEdit();
        CancelActiveGlobalParameterEdit();
        if (parameterEdit is not { } edit) return;
        parameterEdit = null;
        edit.Slider.CancelEdit();
        edit.Light.RestoreInput(edit.Before);
        PreviewRequested?.Invoke(null);
    }

    private PendingLightingInput? SavePendingInputs()
    {
        if (ViewModel is not { } model) return null;
        Dictionary<Guid, PendingLightInput> saved = [];
        foreach (var light in model.Lights)
        {
            if (source.Lights.FirstOrDefault(item => item.Id == light.Id) is not { } committed) continue;
            string? hex = rawHexText.GetValueOrDefault(light.Id);
            var numeric = rawNumericText.GetValueOrDefault(light.Id) ?? ImmutableSortedDictionary<string, string>.Empty;
            bool pending = hex is not null || numeric.Count > 0;
            if (!pending)
            {
                try { pending = light.Build() != committed; }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException) { pending = true; }
            }
            if (!pending) continue;
            saved[light.Id] = new(committed, light.CaptureRecoveryDraft(), rawHexText.GetValueOrDefault(light.Id),
                numeric);
        }
        Dictionary<Guid, PendingGroupInput> groups = [];
        foreach (var group in model.Groups)
        {
            if (source.Groups.FirstOrDefault(item => item.Id == group.Id) is not { } committed) continue;
            var draft = group.CaptureDraft() with { NumericText = rawGroupNumericText.GetValueOrDefault(group.Id) };
            bool pending = draft.NumericText is not null;
            if (!pending)
            {
                try { pending = group.Build() != committed; }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException) { pending = true; }
            }
            if (pending) groups.Add(group.Id, new(committed, draft));
        }
        var globals = model.CaptureGlobalDraft() with { NumericText = rawGlobalNumericText };
        bool pendingGlobals = globals.NumericText.Count > 0;
        try { pendingGlobals |= model.BuildGlobalSettings() != source.Settings; }
        catch (ArgumentException) { pendingGlobals = true; }
        var result = new PendingLightingInput(sourceDocumentId, saved, groups, model.BatchIntensity, source.Settings, globals);
        pendingInputs.Remove(source);
        if (saved.Count > 0 || groups.Count > 0 || model.BatchIntensity is not null || pendingGlobals) pendingInputs.Add(source, result);
        return result;
    }
    private void RestorePendingInputs(EditorLightingViewModel model, PendingLightingInput? current)
    {
        if (current is null && !pendingInputs.TryGetValue(source, out current)) return;
        if (current.Document != sourceDocumentId) return;
        foreach (var light in model.Lights)
        {
            if (!current.Lights.TryGetValue(light.Id, out var pending)) continue;
            light.RestorePendingInput(pending.Draft, pending.Committed);
            if (pending.Hex is { } hex) rawHexText[light.Id] = hex;
            if (pending.Numeric.Count > 0) rawNumericText[light.Id] = pending.Numeric;
        }
        foreach (var group in model.Groups)
        {
            if (!current.Groups.TryGetValue(group.Id, out var pending)) continue;
            group.RestorePending(pending.Draft, pending.Committed);
            if (pending.Draft.NumericText is { } text) rawGroupNumericText[group.Id] = text;
        }
        model.BatchIntensity = current.BatchIntensity;
        model.RestorePendingGlobals(current.Globals, current.CommittedSettings);
        rawGlobalNumericText = current.Globals.NumericText;
    }
    private static void RevealExplicitSelection(EditorLightingViewModel model, Guid? id, bool selectionChanged,
        IEnumerable<Guid>? selectedIds = null)
    {
        // Search may legitimately hide the existing selected row. Only an explicit request for
        // another light reveals it; typing in the search field never clears the query itself.
        var requested = (selectedIds ?? (id is { } selected ? [selected] : Array.Empty<Guid>())).ToHashSet();
        if (selectionChanged && model.Lights.Any(light => requested.Contains(light.Id)
            && !model.BrowserRows.OfType<EditorNativeLightItem>().Any(visible => visible.Id == light.Id)))
            model.SearchText = "";
    }
}
