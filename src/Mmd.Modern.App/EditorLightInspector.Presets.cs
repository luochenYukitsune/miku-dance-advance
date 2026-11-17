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
    private void AddLightPreset(object? sender, RoutedEventArgs args) => ApplyLightingPreset(SceneLightingPresetApplyMode.Add);
    private void ReplaceLightPreset(object? sender, RoutedEventArgs args) => ApplyLightingPreset(SceneLightingPresetApplyMode.ReplaceSelected);

    internal bool ApplyLightingPreset(SceneLightingPresetApplyMode mode)
    {
        if (!modernParameters || presetBrowsing || refreshing || committing || recoveryRestoreDepth != 0
            || ViewModel is not { } model || EditStartingRequested?.Invoke() == false) return false;
        try
        {
            CancelActiveParameterEdit();
            if (!TryReadLightingDraft(out var draft) || draft is null
                || this.FindControl<ComboBox>("LightPresetChoice")?.SelectedItem is not EditorLightPresetChoice choice) return false;
            var result = choice.Preset.Apply(draft.Snapshot, mode, draft.SelectedLightIds, draft.SelectedGroupIds);
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
        var choice = new EditorLightPresetChoice(null, preset);
        var control = this.FindControl<ComboBox>("LightPresetChoice")!;
        control.ItemsSource = control.Items.Cast<EditorLightPresetChoice>().Where(item => item.Builtin is not null).Append(choice).ToArray();
        control.SelectedItem = choice;
    }

    private async void LoadLightPreset(object? sender, RoutedEventArgs args)
    {
        if (!modernParameters || presetBrowsing || TopLevel.GetTopLevel(this) is not { StorageProvider: { CanOpen: true } storage }) return;
        SetPresetBrowsing(true);
        try
        {
            var files = await storage.OpenFilePickerAsync(new()
            {
                Title = Text("Mda.Text.Context.Lighting.Presets.Load"), AllowMultiple = false,
                FileTypeFilter = [PresetFileType()],
            });
            if (files.FirstOrDefault() is not { } file) return;
            var path = file.TryGetLocalPath() ?? throw new ArgumentException("A local preset file is required.");
            LoadLightingPresetFile(path);
            ViewModel?.SetValidationMessage("Mda.Text.Context.Lighting.Presets.Loaded");
        }
        catch (Exception error) when (IsPresetFileError(error))
        { ViewModel?.SetValidationMessage("Mda.Text.Context.Lighting.Presets.LoadFailed"); }
        finally { SetPresetBrowsing(false); }
    }

    private async void SaveLightPreset(object? sender, RoutedEventArgs args)
    {
        if (presetBrowsing || TopLevel.GetTopLevel(this) is not { StorageProvider: { CanSave: true } storage }
            || !TryCaptureLightingPreset(out var preset) || preset is null) return;
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
