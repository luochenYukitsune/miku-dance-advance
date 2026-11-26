using System.Numerics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

public sealed record EditorLightPresetChoice(SceneLightingBuiltinPreset? Builtin, SceneLightingPreset Preset)
{
    public string Label => Builtin is { } builtin ? Text("Mda.Text.Context.Lighting.Presets." + builtin) : Preset.Name;
}

public partial class EditorLightInspector
{
    private bool presetBrowsing;
    internal Func<EditorSceneInspectorViewModel?>? TrackLightProvider { get; set; }
    private void InitializePresets()
    {
        RefreshPresetLanguage();
        Observe(this, static inspector => inspector.RefreshPresetLanguage());
    }

    private void RefreshPresetLanguage()
    {
        var control = this.FindControl<ComboBox>("LightPresetChoice")!;
        var previous = control.SelectedItem as EditorLightPresetChoice;
        string? previousName = this.FindControl<TextBox>("LightPresetName")!.Text;
        var choices = Enum.GetValues<SceneLightingBuiltinPreset>().Select(kind =>
        {
            var source = SceneLightingPreset.CreateBuiltin(kind).Lighting;
            string name = Text("Mda.Text.Context.Lighting.Presets." + kind);
            var localized = new SceneLightingSnapshot(source.Lights.Select(light => light with
            { Name = Text("Mda.Text.Context.Lighting.Presets.BuiltinLight." + light.Name.Replace(" ", "")) }),
                source.Groups.Select(group => group with { Name = name }), source.Settings);
            return new EditorLightPresetChoice(kind, SceneLightingPreset.Capture(name, localized, [], localized.Groups.Select(group => group.Id)));
        }).ToList();
        if (previous is { Builtin: null }) choices.Add(previous);
        control.ItemsSource = choices;
        control.SelectedItem = previous?.Builtin is { } builtin ? choices.Single(choice => choice.Builtin == builtin)
            : previous is not null ? previous : choices[0];
        // User-authored names are content, including a name typed over a built-in label.
        if (previousName is not null && previous is not null && previousName != previous.Preset.Name)
            this.FindControl<TextBox>("LightPresetName")!.Text = previousName;
    }

    private void SelectLightPreset(object? sender, SelectionChangedEventArgs args)
    {
        if (this.FindControl<ComboBox>("LightPresetChoice")?.SelectedItem is EditorLightPresetChoice choice)
            this.FindControl<TextBox>("LightPresetName")!.Text = choice.Label;
    }
    private SceneLightingPreset BuiltinPreset(SceneLightingBuiltinPreset kind) =>
        this.FindControl<ComboBox>("LightPresetChoice")!.Items.OfType<EditorLightPresetChoice>().Single(option => option.Builtin == kind).Preset;

    internal bool CanAddBuiltinLighting(SceneLightingBuiltinPreset kind)
    {
        if (!modernParameters || ViewModel is not { } model) return false;
        var preset = BuiltinPreset(kind).Lighting;
        return model.Lights.Count + preset.Lights.Count <= SceneLightingSnapshot.MaxLights
            && model.EnabledLightCount + preset.EffectiveLights.Count(light => light.Enabled) <= SceneLightingSnapshot.MaxActiveLights;
    }

    internal bool AddBuiltinLighting(SceneLightingBuiltinPreset kind) =>
        ApplyLightingPreset(SceneLightingPresetApplyMode.Add, BuiltinPreset(kind));
    private void ReplaceLightPreset(object? sender, RoutedEventArgs args) => ApplyLightingPreset(SceneLightingPresetApplyMode.ReplaceSelected);

    internal bool ApplyLightingPreset(SceneLightingPresetApplyMode mode, SceneLightingPreset? supplied = null)
    {
        if (!modernParameters || presetBrowsing || refreshing || committing || recoveryRestoreDepth != 0
            || ViewModel is not { } model || EditStartingRequested?.Invoke() == false) return false;
        try
        {
            CancelActiveParameterEdit();
            var preset = supplied ?? (this.FindControl<ComboBox>("LightPresetChoice")?.SelectedItem as EditorLightPresetChoice)?.Preset;
            if (!TryReadLightingDraft(out var draft) || draft is null || preset is null) return false;
            var result = preset.Apply(draft.Snapshot, mode, draft.SelectedLightIds, draft.SelectedGroupIds);
            var candidate = new EditorLightingDraft(result.Snapshot, result.AddedLightIds[0], result.AddedLightIds, result.AddedGroupIds);
            committing = true;
            bool accepted;
            try { accepted = CommitRequested?.Invoke(candidate) == true; }
            finally { committing = false; }
            if (!accepted) { model.SetValidationMessage("Mda.Text.Context.Lighting.Presets.ApplyFailed"); return false; }
            source = draft.Snapshot;
            performingSelectionAction = true;
            try
            {
                SetLighting(result.Snapshot, candidate.SelectedLightId, sourceDocumentId, candidate.SelectedLightIds);
                ViewModel?.RestoreSelection(candidate.SelectedLightIds, candidate.SelectedLightId, candidate.SelectedGroupIds);
            }
            finally { performingSelectionAction = false; }
            ViewModel?.SetValidationMessage("");
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        { model.SetValidationMessage("Mda.Text.Context.Lighting.Presets.ApplyFailed"); return false; }
    }

    internal bool TryCaptureLightingPreset(out SceneLightingPreset? preset)
    {
        preset = null;
        if (!modernParameters || refreshing || committing || recoveryRestoreDepth != 0
            || ViewModel is not { HasSelectedLight: true } model || EditStartingRequested?.Invoke() == false) return false;
        try
        {
            CancelActiveParameterEdit();
            if (!TryReadLightingDraft(out var draft) || draft is null) return false;
            preset = SceneLightingPreset.Capture(this.FindControl<TextBox>("LightPresetName")!.Text ?? "", draft.Snapshot,
                draft.SelectedLightIds, draft.SelectedGroupIds, resolveParentTransform);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        { model.SetValidationMessage("Mda.Text.Context.Lighting.Presets.CaptureFailed"); return false; }
    }

    internal void LoadLightingPresetFile(string path)
    {
        var preset = SceneLightingPreset.Load(path); // Validate before changing the selected reusable preset.
        SelectLoadedPreset(preset);
    }

    private void SelectLoadedPreset(SceneLightingPreset preset)
    {
        var choice = new EditorLightPresetChoice(null, preset);
        var control = this.FindControl<ComboBox>("LightPresetChoice")!;
        control.ItemsSource = control.Items.Cast<EditorLightPresetChoice>().Where(item => item.Builtin is not null).Append(choice).ToArray();
        control.SelectedItem = choice;
    }

    private async Task<SceneLightingPreset?> PickPresetAsync()
    {
        if (!modernParameters || presetBrowsing || TopLevel.GetTopLevel(this) is not { StorageProvider: { CanOpen: true } storage }) return null;
        SetPresetBrowsing(true);
        try
        {
            var files = await storage.OpenFilePickerAsync(new()
            {
                Title = Text("Mda.Text.Context.Lighting.Presets.Load"), AllowMultiple = false, FileTypeFilter = [PresetFileType()]
            });
            return files.FirstOrDefault()?.TryGetLocalPath() is { } path ? SceneLightingPreset.Load(path) : null;
        }
        catch (Exception error) when (IsPresetFileError(error))
        { ViewModel?.SetValidationMessage("Mda.Text.Context.Lighting.Presets.LoadFailed"); return null; }
        finally { SetPresetBrowsing(false); }
    }

    internal async Task<bool> ImportLightGroupAsync()
    {
        var document = sourceDocumentId;
        return await PickPresetAsync() is { } preset && sourceDocumentId == document
            && ApplyLightingPreset(SceneLightingPresetApplyMode.Add, preset);
    }

    private async void ApplyTrackPreset(object? sender, RoutedEventArgs args)
    {
        if (TrackLightProvider?.Invoke() is not { CanEdit: true } model) return;
        var document = sourceDocumentId;
        long revision = model.PoseRevision;
        if (await PickPresetAsync() is not { } preset || document != sourceDocumentId || model.PoseRevision != revision
            || !ReferenceEquals(model, TrackLightProvider?.Invoke()) || EditStartingRequested?.Invoke() == false) return;
        if (preset.Lighting.EffectiveLights is not [{ Kind: SceneLightKind.Directional } value])
        { ViewModel?.SetValidationMessage("Mda.Text.Layout.DirectionalRequired"); return; }
        model.SetLightDraft(new(value.Color * (value.Enabled ? value.Intensity : 0), value.Direction));
    }

    private async void SaveTrackPreset(object? sender, RoutedEventArgs args)
    {
        if (TrackLightProvider?.Invoke() is not { CanEdit: true, HasLightValidation: false } model
            || EditStartingRequested?.Invoke() == false) return;
        var pose = model.LightPose;
        var direction = Vector3.Normalize(pose.Direction);
        var up = Math.Abs(direction.Y) > .99f ? Vector3.UnitZ : Vector3.UnitY;
        var rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(
            Matrix4x4.CreateWorld(Vector3.Zero, direction, up)));
        var value = new NativeSceneLight(Guid.NewGuid(), Text("Mda.Text.Layout.TrackLight"), SceneLightKind.Directional,
            Vector3.Zero, rotation, pose.Color);
        await SavePresetAsync(SceneLightingPreset.Capture(value.Name, new([value]), [value.Id]));
    }

    private async void ApplySingleLightPreset(object? sender, RoutedEventArgs args)
    {
        var document = sourceDocumentId;
        if (ViewModel is not { ShowSingleLight: true, SelectedLight.Id: var id }
            || await PickPresetAsync() is not { } preset || sourceDocumentId != document
            || ViewModel is not { ShowSingleLight: true, SelectedLight: { } light } || light.Id != id) return;
        ApplySingleLightPreset(preset);
    }

    internal bool ApplySingleLightPreset(SceneLightingPreset preset)
    {
        var document = sourceDocumentId;
        if (ViewModel is not { ShowSingleLight: true, SelectedLight: { } light } model
            || EditStartingRequested?.Invoke() == false) return false;
        var id = light.Id;
        pendingNumericBinding = null;
        bool hadNumeric = numericEdit is not null;
        numericEdit = null;
        focusedNumericDraft = null;
        CancelActiveParameterEdit();
        try
        {
            if (!TryReadLightingDraft(out var draft) || draft is null) return false;
            if (preset.Lighting.Lights.Count != 1) { model.SetValidationMessage("Mda.Text.Layout.SingleLightRequired"); return false; }
            var snapshot = preset.ApplyToLight(draft.Snapshot, id);
            committing = true;
            bool accepted;
            try { accepted = CommitRequested?.Invoke(new(snapshot, id)) == true; }
            finally { committing = false; }
            if (accepted)
            {
                rawHexText.Remove(id);
                rawNumericText.Remove(id);
                if (renderedColorSource?.Id == id) { renderedColorSource = null; renderedHexText = ""; }
                source = draft.Snapshot;
                SetLighting(snapshot, id, document);
                return true;
            }
            model.SetValidationMessage("Mda.Text.Context.Lighting.Presets.ApplyFailed");
            return false;
        }
        catch (Exception error) when (IsPresetFileError(error))
        { model.SetValidationMessage("Mda.Text.Context.Lighting.Presets.ApplyFailed"); return false; }
        finally
        {
            if (hadNumeric) PreviewRequested?.Invoke(null);
        }
    }

    private async void SaveSingleLightPreset(object? sender, RoutedEventArgs args)
    {
        if (ViewModel is not { ShowSingleLight: true, SelectedLight: { } light } || EditStartingRequested?.Invoke() == false
            || !TryReadLightingDraft(out var draft) || draft is null) return;
        try
        {
            await SavePresetAsync(SceneLightingPreset.Capture(light.Name, draft.Snapshot, [light.Id], resolveParent: resolveParentTransform));
        }
        catch (Exception error) when (IsPresetFileError(error))
        { ViewModel?.SetValidationMessage("Mda.Text.Context.Lighting.Presets.CaptureFailed"); }
    }

    private async void LoadLightPreset(object? sender, RoutedEventArgs args)
    {
        var document = sourceDocumentId;
        if (await PickPresetAsync() is not { } preset || document != sourceDocumentId) return;
        SelectLoadedPreset(preset);
        ViewModel?.SetValidationMessage("Mda.Text.Context.Lighting.Presets.Loaded");
    }

    private async void SaveLightPreset(object? sender, RoutedEventArgs args)
    {
        if (TryCaptureLightingPreset(out var preset) && preset is not null) await SavePresetAsync(preset);
    }

    private async Task SavePresetAsync(SceneLightingPreset preset)
    {
        if (presetBrowsing || TopLevel.GetTopLevel(this) is not { StorageProvider: { CanSave: true } storage }) return;
        // Capture once before the picker. Saving never exports another document's later selection.
        SetPresetBrowsing(true);
        try
        {
            var file = await storage.SaveFilePickerAsync(new()
            {
                Title = Text("Mda.Text.Context.Lighting.Presets.SaveSelected"), SuggestedFileName = "lighting-preset.json",
                DefaultExtension = "json", ShowOverwritePrompt = true, FileTypeChoices = [PresetFileType()],
            });
            if (file is null) return;
            var path = file.TryGetLocalPath() ?? throw new ArgumentException("A local preset file is required.");
            preset.Save(path);
            ViewModel?.SetValidationMessage("Mda.Text.Context.Lighting.Presets.Saved");
        }
        catch (Exception error) when (IsPresetFileError(error))
        { ViewModel?.SetValidationMessage("Mda.Text.Context.Lighting.Presets.SaveFailed"); }
        finally { SetPresetBrowsing(false); }
    }
    private static bool IsPresetFileError(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException;
    private static FilePickerFileType PresetFileType() => new(Text("Mda.Text.Context.Lighting.Presets.FileType")) { Patterns = ["*.json"] };
    private void SetPresetBrowsing(bool value)
    {
        presetBrowsing = value;
        this.FindControl<StackPanel>("LightPresetTools")!.IsEnabled = !value;
    }
}
