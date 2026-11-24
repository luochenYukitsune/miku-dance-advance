using System.Collections.Immutable;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Core;

namespace Mmd.Modern.App;

public partial class EditorLightInspector
{
    private sealed record ParameterEdit(Control Control, EditorNativeLightItem Light,
        EditorRecoveryNativeLightDraft Before, SceneLightingSnapshot Source, Guid? Document);
    private sealed record NumericEdit(EditorNativeLightItem Light,
        EditorRecoveryNativeLightDraft Before, SceneLightingSnapshot Source, Guid? Document);
    private ParameterEdit? parameterEdit;
    private NumericEdit? numericEdit;
    private (Guid Id, EditorRecoveryNativeLightDraft Draft)? focusedNumericDraft;
    private EditorNativeLightItem? observedPreviewLight;
    private (EditorNumericUpDown Input, EditorNativeLightItem Light)? pendingNumericBinding;
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
        if (sender is not Control control || !modernParameters || refreshing || recoveryRestoreDepth != 0
            || EditStartingRequested?.Invoke() == false || !TryCommit() || ViewModel is not { ShowSingleLight: true, SelectedLight: { } light })
        { args.Cancel = true; return; }
        if (Equals(control.Tag, "brightness")) light.EnsureBrightnessSliderRange();
        parameterEdit = new(control, light, light.CaptureRecoveryDraft(), source, sourceDocumentId);
    }
    private void PreviewParameterEdit(object? sender, EventArgs args)
    {
        if (sender is not EditorLightParameterSlider slider || updatingParameter || parameterEdit is not { } edit
            || !ReferenceEquals(edit.Control, sender)) return;
        if (!ReferenceEquals(edit.Source, source) || edit.Document != sourceDocumentId || ViewModel?.SelectedLight?.Id != edit.Light.Id)
        { CancelActiveParameterEdit(); return; }
        updatingParameter = true;
        try
        {
            decimal value = (decimal)slider.Value;
            switch (slider.Tag)
            {
                case "brightness": edit.Light.Intensity = EditorLightParameterScale.BrightnessValue(slider.Value); break;
                case "distance": edit.Light.Range = EditorLightParameterScale.DistanceValue(slider.Value); break;
                case "width": edit.Light.BeamWidthDegrees = value; break;
                case "softness": edit.Light.EdgeSoftnessPercent = value; break;
                case "areaWidth": edit.Light.AreaWidth = EditorLightParameterScale.DistanceValue(slider.Value); break;
                case "areaHeight": edit.Light.AreaHeight = EditorLightParameterScale.DistanceValue(slider.Value); break;
                default: return;
            }
            if (ViewModel.TryCreate(out var draft) && draft is not null && PreviewRequested?.Invoke(draft) == false)
                CancelActiveParameterEdit();
        }
        finally { updatingParameter = false; }
    }
    private void CompleteParameterEdit(object? sender, EventArgs args)
    {
        if (parameterEdit is not { } edit || !ReferenceEquals(edit.Control, sender)) return;
        parameterEdit = null;
        try { TryCommit(); }
        finally { PreviewRequested?.Invoke(null); }
    }
    private void CancelParameterEdit(object? sender, EventArgs args)
    {
        if (parameterEdit is { } edit && ReferenceEquals(edit.Control, sender)) CancelActiveParameterEdit();
    }
    private void OnInspectorGotFocus(object? sender, Avalonia.Input.GotFocusEventArgs args)
    {
        if (modernParameters) EditStartingRequested?.Invoke();
        if (refreshing || recoveryRestoreDepth != 0 || numericEdit is not null) return;
        if (args.Source is not Control control) return;
        var numeric = control as EditorNumericUpDown ?? control.FindAncestorOfType<EditorNumericUpDown>();
        if (numeric?.DataContext is EditorNativeLightItem light)
            focusedNumericDraft = (light.Id, light.CaptureRecoveryDraft());
    }

    private void OnInspectorNumericSpin(object? sender, SpinEventArgs args)
    {
        if (refreshing || recoveryRestoreDepth != 0 || !modernParameters) return;
        if (args.Source is not Control control) return;
        var numeric = control as EditorNumericUpDown ?? control.FindAncestorOfType<EditorNumericUpDown>();
        if (numeric is null || numeric.IsKeyboardFocusWithin) return;
        if (numeric.DataContext is EditorNativeLightItem light && numericEdit is null)
            focusedNumericDraft = (light.Id, light.CaptureRecoveryDraft());
        numeric.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus();
    }

    private void ResetLightBrightness(object? sender, RoutedEventArgs args)
    {
        if (!modernParameters || refreshing || committing || recoveryRestoreDepth != 0
            || ViewModel is not { ShowSingleLight: true, SelectedLight: { } light }
            || sender is not Button button || !ReferenceEquals(button.DataContext, light)
            || EditStartingRequested?.Invoke() == false) return;

        var committed = source.Lights.Single(item => item.Id == light.Id);
        float brightness = EditorLightPlacement.DefaultIntensity(committed.Kind);
        // The shortcut also repairs malformed brightness text. Other unfinished inputs stay local.
        committing = true;
        try
        {
            if (committed.Intensity != brightness)
            {
                var snapshot = source.WithLights(source.Lights.Select(item => item.Id == light.Id
                    ? item with { Intensity = brightness } : item));
                if (CommitRequested?.Invoke(new(snapshot, light.Id)) != true) return;
                source = snapshot;
            }
            light.Intensity = (decimal)brightness;
            var input = this.GetVisualDescendants().OfType<EditorNumericUpDown>().Single(control =>
                control.Name == "InlineLightIntensity" && ReferenceEquals(control.DataContext, light));
            input.SetCurrentValue(NumericUpDown.TextProperty, ((decimal)brightness).ToString(input.FormatString, input.NumberFormat));
            if (rawNumericText.TryGetValue(light.Id, out var fields))
                rawNumericText[light.Id] = fields.Remove("InlineLightIntensity");
        }
        finally { committing = false; }
        TryReadLightingDraft(out _);
    }

    private void AimLightAtCenter(object? sender, RoutedEventArgs args) => AimSelectedLight(atCenter: true);
    private void AimLightFromCamera(object? sender, RoutedEventArgs args) => AimSelectedLight(atCenter: false);

    private void AimSelectedLight(bool atCenter)
    {
        if (refreshing || committing || recoveryRestoreDepth != 0
            || ViewModel is not { ShowSingleLight: true, SelectedLight: { } light } model
            || modernParameters && EditStartingRequested?.Invoke() == false
            || !TryReadLightingDraft(out var draft) || draft is null) return;
        try
        {
            var local = draft.Snapshot.Lights.Single(item => item.Id == light.Id);
            var parentWorld = local.Parent is { } parent
                ? resolveParentTransform?.Invoke(parent) ?? throw new InvalidOperationException()
                : Matrix4x4.Identity;
            light.AimFromView(local, parentWorld, CreationCameraProvider?.Invoke() ?? model.CreationCamera, atCenter);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        {
            model.SetValidationMessage("Mda.Text.Context.Lighting.AimFailed");
            return;
        }
        TryCommit();
    }

    private void FlipLightHemisphere(object? sender, RoutedEventArgs args)
    {
        if (refreshing || recoveryRestoreDepth != 0 || ViewModel is not { ShowSingleLight: true, SelectedLight: { } light }) return;
        if (modernParameters && EditStartingRequested?.Invoke() == false) return;
        light.FlipHemisphere();
        TryCommit();
    }

    private void SyncObservedPreviewLight()
    {
        var current = ViewModel?.ShowSingleLight == true ? ViewModel.SelectedLight : null;
        if (ReferenceEquals(observedPreviewLight, current)) return;
        if (observedPreviewLight is not null) observedPreviewLight.PropertyChanged -= OnObservedLightPropertyChanged;
        observedPreviewLight = current;
        if (observedPreviewLight is not null) observedPreviewLight.PropertyChanged += OnObservedLightPropertyChanged;
        pendingNumericBinding = null;
    }

    private void OnObservedLightPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (pendingNumericBinding is not { } pending || !ReferenceEquals(pending.Light, sender)) return;
        pendingNumericBinding = null;
        PublishNumericPreview(pending.Input, pending.Light);
    }

    private void FlushPendingNumericBinding()
    {
        if (pendingNumericBinding is not { } pending) return;
        pendingNumericBinding = null;
        PublishNumericPreview(pending.Input, pending.Light);
    }

    private void PreviewNumericInput(object? sender, NumericUpDownValueChangedEventArgs args)
    {
        if (refreshing || updatingParameter || recoveryRestoreDepth != 0 || !modernParameters) return;
        if (sender is not EditorNumericUpDown input || !input.IsInputValid || !args.NewValue.HasValue) return;
        if (!input.IsKeyboardFocusWithin) return;
        if (ViewModel is not { ShowSingleLight: true, SelectedLight: { } light } || !ReferenceEquals(input.DataContext, light)) return;
        if (parameterEdit is not null || groupParameterEdit is not null) return;
        SyncObservedPreviewLight();
        pendingNumericBinding = (input, light);
        Dispatcher.UIThread.Post(FlushPendingNumericBinding, DispatcherPriority.Send);
    }

    private void PublishNumericPreview(EditorNumericUpDown input, EditorNativeLightItem light)
    {
        if (refreshing || updatingParameter || recoveryRestoreDepth != 0 || !modernParameters) return;
        if (!input.IsKeyboardFocusWithin || !input.IsInputValid) return;
        if (ViewModel is not { ShowSingleLight: true, SelectedLight: { } current } || !ReferenceEquals(current, light)
            || !ReferenceEquals(input.DataContext, light))
            return;
        if (parameterEdit is not null || groupParameterEdit is not null) return;
        updatingParameter = true;
        try
        {
            if (!ViewModel.TryCreate(out var draft) || draft is null) return;
            if (numericEdit is null)
            {
                if (source.Lights.SequenceEqual(draft.Snapshot.Lights)
                    && source.Groups.SequenceEqual(draft.Snapshot.Groups)
                    && source.Settings == draft.Snapshot.Settings)
                    return;
                if (EditStartingRequested?.Invoke() == false) return;
                var before = focusedNumericDraft is { } captured && captured.Id == light.Id
                    ? captured.Draft
                    : source.Lights.FirstOrDefault(item => item.Id == light.Id) is { } committed
                        ? new EditorNativeLightItem(committed).CaptureRecoveryDraft()
                        : light.CaptureRecoveryDraft();
                numericEdit = new(light, before, source, sourceDocumentId);
            }
            else if (!ReferenceEquals(numericEdit.Source, source) || numericEdit.Document != sourceDocumentId
                || numericEdit.Light.Id != light.Id)
            {
                CancelActiveParameterEdit();
                return;
            }
            if (PreviewRequested?.Invoke(draft) == false)
                CancelActiveParameterEdit();
        }
        finally { updatingParameter = false; }
    }

    private void CancelActiveParameterEdit()
    {
        pendingNumericBinding = null;
        CancelActiveGroupParameterEdit();
        CancelActiveGlobalParameterEdit();
        if (numericEdit is { } activeNumeric)
        {
            numericEdit = null;
            focusedNumericDraft = null;
            updatingParameter = true;
            try { activeNumeric.Light.RestoreInput(activeNumeric.Before); }
            finally { updatingParameter = false; }
            PreviewRequested?.Invoke(null);
        }
        if (parameterEdit is not { } edit) return;
        parameterEdit = null;
        if (edit.Control is EditorLightParameterSlider slider) slider.CancelEdit();
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
