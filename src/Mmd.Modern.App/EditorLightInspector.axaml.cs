using System.ComponentModel;
using System.Collections.Immutable;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Core;
using Avalonia;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>Immediate project-light editing. Numeric text remains local until a valid edit ends.</summary>
public partial class EditorLightInspector : UserControl
{
    private SceneLightingSnapshot source = SceneLightingSnapshot.Empty;
    private Guid? sourceDocumentId;
    private bool refreshing, committing;
    private int recoveryRestoreDepth;
    private EditorNativeLightItem? renderedColorSource;
    private string renderedHexText = "";
    private readonly Dictionary<Guid, string> rawHexText = [];
    private readonly Dictionary<Guid, ImmutableSortedDictionary<string, string>> rawNumericText = [];
    private readonly Dictionary<Guid, string> rawGroupNumericText = [];
    private readonly HashSet<EditorNumericUpDown> restoredNumericControls = [];
    public EditorLightInspector()
    {
        InitializeComponent();
        InitializePresets();
        LayoutUpdated += (_, _) =>
        {
            ApplyRecoveryNumericText();
            CaptureRawControlText();
            if (ViewModel?.SelectedLight is { } light && !ReferenceEquals(renderedColorSource, light)) RefreshColor();
        };
        KeyDown += (_, args) =>
        {
            if (recoveryRestoreDepth != 0) return;
            if (args.Key == Key.Escape && numericEdit is not null)
            {
                CancelActiveParameterEdit();
                args.Handled = true;
                return;
            }
            if (args.Key != Key.Enter || args.KeyModifiers != KeyModifiers.None) return;
            if (args.Source is TextBox { Name: "LightHexColor" } hex) ApplyHex(hex);
            else if (args.Source is Control input && input.GetVisualAncestors().OfType<EditorNumericUpDown>()
                .Any(number => number.Name is { } name && EditorRecoveryGlobalLightingDraft.NumericNames.Contains(name))) TryCommitGlobalSettings();
            else
            {
                bool hadNumeric = numericEdit is not null;
                numericEdit = null;
                focusedNumericDraft = null;
                try { TryCommit(); }
                finally { if (hadNumeric) PreviewRequested?.Invoke(null); }
            }
            args.Handled = true;
        };
        AddHandler(GotFocusEvent, OnInspectorGotFocus, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        AddHandler(Spinner.SpinEvent, OnInspectorNumericSpin, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    }
    public Func<EditorLightingDraft, bool>? CommitRequested { get; set; }
    public Action<Guid?>? SelectionRequested { get; set; }
    public Action<IReadOnlyList<Guid>, Guid?>? SelectionSetRequested { get; set; }
    public Func<bool>? EditStartingRequested { get; set; }
    public Func<EditorLightingDraft?, bool>? PreviewRequested { get; set; }
    public Func<Mmd.Modern.Application.PmmCameraPose>? CreationCameraProvider { get; set; }
    public EditorLightingViewModel? ViewModel => DataContext as EditorLightingViewModel;
    private bool modernParameters;
    internal void UseWorkspaceBrowser(bool value)
    {
        modernParameters = value;
        this.FindControl<ComboBox>("LightChoice")!.IsVisible = !value;
        if (ViewModel is { } model)
        {
            model.IsModernWorkspace = value;
            foreach (var item in model.Lights) item.UseModernControls = value;
        }
        RefreshParentControls();
    }

    public void SetLighting(SceneLightingSnapshot snapshot, Guid? selectedId, Guid? documentId = null,
        IEnumerable<Guid>? selectedIds = null)
    {
        if (refreshing || committing || recoveryRestoreDepth != 0) return;
        bool replacingSource = !ReferenceEquals(source, snapshot) || sourceDocumentId != documentId || ViewModel is null;
        var requestedIds = snapshot.Lights.Where(light => (selectedIds ?? (selectedId is { } id ? [id] : Array.Empty<Guid>())).Contains(light.Id))
            .Select(light => light.Id).ToArray();
        bool selectionChanged = ViewModel?.SelectedLight?.Id != selectedId || ViewModel?.SelectedLightIds.SequenceEqual(requestedIds) != true;
        var retainedGroups = sourceDocumentId == documentId && ViewModel is { } previousModel
            && previousModel.SelectedLightIds.Where(id => snapshot.Lights.Any(light => light.Id == id)).SequenceEqual(requestedIds)
            ? previousModel.SelectedGroupIds.Where(id => snapshot.Groups.Any(group => group.Id == id)).ToArray() : [];
        if (replacingSource && retainedGroups.Length > 0 && ViewModel is { } previousSelection)
        {
            // A selected group remains the selected row through membership/history changes. Its
            // current children replace its old children; genuinely explicit lamp rows stay explicit.
            var explicitIds = previousSelection.SelectedRows.OfType<EditorNativeLightItem>().Select(light => light.Id).ToHashSet();
            requestedIds = snapshot.Lights.Where(light => explicitIds.Contains(light.Id)
                || light.GroupId is { } group && retainedGroups.Contains(group)).Select(light => light.Id).ToArray();
            if (selectedId is not { } primary || !requestedIds.Contains(primary)) selectedId = requestedIds.Length > 0 ? requestedIds[0] : null;
        }
        if (replacingSource || selectionChanged && (groupParameterEdit is not null || parameterEdit is not null || padEdit is not null || numericEdit is not null || globalParameterEdit is not null)
            || parameterEdit is { } edit && edit.Light.Id != selectedId
            || padEdit is { } activePad && activePad.Light.Id != selectedId
            || numericEdit is { } activeNumeric && activeNumeric.Light.Id != selectedId) CancelActiveParameterEdit();
        using (Mmd.Modern.Application.InteractionDiagnostics.Measure("lighting.inspector-capture-raw")) CaptureRawControlText();
        var pending = replacingSource ? SavePendingInputs() : null;
        Guid? previousDocument = sourceDocumentId;
        refreshing = true;
        try
        {
            if (!ReferenceEquals(source, snapshot) || sourceDocumentId != documentId || ViewModel is null)
            {
                if (ViewModel is { } old) old.PropertyChanged -= SelectionChanged;
                string search = sourceDocumentId == documentId ? (ViewModel?.SearchText ?? "") : "";
                source = snapshot;
                sourceDocumentId = documentId;
                ClearRawInput();
                using var measureModel = Mmd.Modern.Application.InteractionDiagnostics.Measure("lighting.inspector-new-model");
                var model = new EditorLightingViewModel(snapshot, selectedId, requestedIds, retainedGroups)
                {
                    SearchText = search
                };
                RestorePendingInputs(model, previousDocument == documentId ? pending : null);
                model.IsModernWorkspace = modernParameters;
                foreach (var item in model.Lights) item.UseModernControls = modernParameters;
                RevealExplicitSelection(model, selectedId, selectionChanged, requestedIds);
                // A light context may be open with no light selected; do not silently select a row.
                model.RestoreSelection(requestedIds, selectedId, retainedGroups);
                using (Mmd.Modern.Application.InteractionDiagnostics.Measure("lighting.inspector-datacontext")) DataContext = model;
                model.PropertyChanged += SelectionChanged;
            }
            else if (ViewModel is { } current)
            {
                RevealExplicitSelection(current, selectedId, selectionChanged, requestedIds);
                current.RestoreSelection(requestedIds, selectedId, retainedGroups);
            }
            this.FindControl<TextBlock>("EmptyLightPrompt")!.IsVisible = ViewModel?.HasSelection != true;
        }
        finally { refreshing = false; }
        SyncObservedPreviewLight();
        RefreshParentControls();
    }

    internal EditorRecoveryNativeLightingState CaptureRecoveryState(SceneLightingSnapshot committed, EditorInspectorPage page,
        Guid? documentId = null)
    {
        // A hidden inspector can still belong to an earlier document. Only its matching committed
        // binding owns these drafts; historical item originals are deliberately not that binding.
        bool matching = sourceDocumentId == documentId && source.Lights.SequenceEqual(committed.Lights)
            && source.Groups.SequenceEqual(committed.Groups) && source.Settings == committed.Settings && ViewModel is { };
        var model = matching ? ViewModel! : new EditorLightingViewModel(committed);
        if (matching) CaptureRawControlText();
        var state = new EditorRecoveryNativeLightingState
        {
            InspectorPage = page, SelectedLightId = matching ? model.SelectedLight?.Id : null,
            SelectedLightIds = matching ? model.SelectedLightIds.ToImmutableArray() : [],
            SelectedGroupIds = matching ? model.SelectedGroupIds.ToImmutableArray() : [],
            CommittedLights = committed.Lights.ToImmutableArray(),
            CommittedGroups = committed.Groups.ToImmutableArray(),
            CommittedSettings = committed.Settings,
            GlobalDraft = matching && globalParameterEdit is { } globalEdit ? globalEdit.Before
                : model.CaptureGlobalDraft() with { NumericText = matching ? rawGlobalNumericText : ImmutableSortedDictionary<string, string>.Empty },
            GroupDrafts = model.Groups.Select(group => (matching && groupParameterEdit is { } groupEdit
                && groupEdit.Groups.TryGetValue(group.Id, out var beforeGroup) ? beforeGroup : group.CaptureDraft()) with
            { NumericText = matching ? rawGroupNumericText.GetValueOrDefault(group.Id) : null }).ToImmutableArray(),
            Lights = model.Lights.Select(light => (matching && parameterEdit is { } editing && editing.Light.Id == light.Id
                ? editing.Before
                : matching && padEdit is { } activePad && activePad.Light.Id == light.Id
                    ? activePad.Before
                    : matching && numericEdit is { } activeNumeric && activeNumeric.Light.Id == light.Id
                        ? activeNumeric.Before
                        : matching && groupParameterEdit is { } batchEdit && batchEdit.Lights.TryGetValue(light.Id, out var beforeLight)
                            ? beforeLight : light.CaptureRecoveryDraft()) with
            {
                HexText = matching && rawHexText.TryGetValue(light.Id, out string? hex) ? hex : null,
                NumericText = matching && rawNumericText.TryGetValue(light.Id, out var numeric)
                    ? numeric : ImmutableSortedDictionary<string, string>.Empty
            }).ToImmutableArray()
        };
        state.ValidateAgainst(committed);
        return state;
    }

    internal void RestoreRecoveryState(EditorRecoveryNativeLightingState state, SceneLightingSnapshot committed, Guid? documentId = null)
    {
        CancelActiveParameterEdit();
        state.ValidateAgainst(committed);
        // Construct every item before changing the live inspector. No Build or commit is performed.
        var items = state.Lights.Select(EditorNativeLightItem.RestoreRecoveryDraft).ToArray();
        var model = new EditorLightingViewModel(new SceneLightingSnapshot([], committed.Groups, committed.Settings));
        if (state.GlobalDraft is { } globals) model.RestoreGlobalDraft(globals);
        model.IsModernWorkspace = modernParameters;
        model.Groups.Clear();
        foreach (var draft in state.GroupDrafts) model.Groups.Add(EditorLightGroupItem.RestoreDraft(draft));
        foreach (var item in items)
        {
            item.GroupId = committed.Lights.Single(light => light.Id == item.Id).GroupId;
            model.Lights.Add(item);
        }
        foreach (var item in items) item.UseModernControls = modernParameters;
        model.RestoreSelection(state.SelectedLightIds.Length == 0 && state.SelectedLightId is { } legacyId ? [legacyId] : state.SelectedLightIds,
            state.SelectedLightId, state.SelectedGroupIds);
        refreshing = true;
        try
        {
            if (ViewModel is { } previous) previous.PropertyChanged -= SelectionChanged;
            source = committed;
            sourceDocumentId = documentId;
            ClearRawInput();
            rawGlobalNumericText = state.GlobalDraft?.NumericText ?? ImmutableSortedDictionary<string, string>.Empty;
            foreach (var light in state.Lights)
            {
                if (light.HexText is { } hex) rawHexText.Add(light.Original.Id, hex);
                if (light.NumericText.Count != 0) rawNumericText.Add(light.Original.Id, light.NumericText);
            }
            foreach (var group in state.GroupDrafts)
                if (group.NumericText is { } text) rawGroupNumericText.Add(group.Original.Id, text);
            DataContext = model;
            model.PropertyChanged += SelectionChanged;
            this.FindControl<TextBlock>("EmptyLightPrompt")!.IsVisible = !model.HasSelection;
            UpdateLayout();
            RefreshColor();
            ApplyRecoveryNumericText();
        }
        finally { refreshing = false; }
        SyncObservedPreviewLight();
        RefreshParentControls();
    }

    internal IDisposable SuspendRecoveryEdits()
    {
        recoveryRestoreDepth++;
        return new RecoveryEditSuspension(this);
    }

    private sealed class RecoveryEditSuspension(EditorLightInspector owner) : IDisposable
    {
        private EditorLightInspector? target = owner;
        public void Dispose()
        {
            if (target is not { } current) return;
            target = null;
            current.recoveryRestoreDepth--;
        }
    }

    private void ClearRawInput()
    {
        rawHexText.Clear(); rawNumericText.Clear(); rawGroupNumericText.Clear(); restoredNumericControls.Clear();
        rawGlobalNumericText = ImmutableSortedDictionary<string, string>.Empty;
        renderedColorSource = null; renderedHexText = "";
    }

    private void CaptureRawControlText()
    {
        if (refreshing || ViewModel is null) return;
        foreach (var input in this.GetVisualDescendants().OfType<EditorNumericUpDown>())
        {
            if (input.Name is { } globalName && EditorRecoveryGlobalLightingDraft.NumericNames.Contains(globalName))
            {
                // Collapsed expander content can exist before its NumericUpDown template has
                // initialized Text. This is not a user-authored empty string.
                if (input.Text is null) continue;
                if (!restoredNumericControls.Contains(input) && rawGlobalNumericText.ContainsKey(globalName)) continue;
                rawGlobalNumericText = input.IsInputValid ? rawGlobalNumericText.Remove(globalName)
                    : rawGlobalNumericText.SetItem(globalName, input.Text ?? "");
                continue;
            }
            if (input.DataContext is EditorLightGroupItem group && input.Name == "LightGroupIntensityInput")
            {
                if (!restoredNumericControls.Contains(input) && rawGroupNumericText.ContainsKey(group.Id)) continue;
                if (input.IsInputValid) rawGroupNumericText.Remove(group.Id);
                else rawGroupNumericText[group.Id] = input.Text ?? "";
                continue;
            }
            if (input.DataContext is not EditorNativeLightItem light || input.Name is not { } name
                || !EditorRecoveryNativeLightDraft.NumericNames.Contains(name)) continue;
            if (!restoredNumericControls.Contains(input) && rawNumericText.TryGetValue(light.Id, out var pending)
                && pending.ContainsKey(name)) continue;
            var values = rawNumericText.GetValueOrDefault(light.Id) ?? ImmutableSortedDictionary<string, string>.Empty;
            rawNumericText[light.Id] = input.IsInputValid ? values.Remove(name) : values.SetItem(name, input.Text ?? "");
        }
        if (renderedColorSource is not { } selected) return;
        var hex = this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(input => input.Name == "LightHexColor"
            && ReferenceEquals(input.DataContext, selected));
        if (hex is null) return;
        if (string.Equals(hex.Text, renderedHexText, StringComparison.OrdinalIgnoreCase)) rawHexText.Remove(selected.Id);
        else rawHexText[selected.Id] = hex.Text ?? "";
    }

    private void ApplyRecoveryNumericText()
    {
        foreach (var input in this.GetVisualDescendants().OfType<EditorNumericUpDown>())
        {
            if (input.Name is { } globalName && EditorRecoveryGlobalLightingDraft.NumericNames.Contains(globalName))
            {
                if (restoredNumericControls.Add(input) && rawGlobalNumericText.TryGetValue(globalName, out string? globalText))
                    input.SetCurrentValue(NumericUpDown.TextProperty, globalText);
                continue;
            }
            if (input.DataContext is EditorLightGroupItem group && input.Name == "LightGroupIntensityInput")
            {
                if (restoredNumericControls.Add(input) && rawGroupNumericText.TryGetValue(group.Id, out string? groupText))
                    input.SetCurrentValue(NumericUpDown.TextProperty, groupText);
                continue;
            }
            if (input.DataContext is not EditorNativeLightItem light || input.Name is not { } name
                || !EditorRecoveryNativeLightDraft.NumericNames.Contains(name) || !restoredNumericControls.Add(input)) continue;
            if (rawNumericText.TryGetValue(light.Id, out var values) && values.TryGetValue(name, out string? text))
                input.SetCurrentValue(NumericUpDown.TextProperty, text);
        }
    }

    public bool TryCommit()
    {
        if (refreshing || committing || recoveryRestoreDepth != 0 || ViewModel is null) return true;
        pendingNumericBinding = null;
        bool hadNumeric = numericEdit is not null;
        numericEdit = null;
        focusedNumericDraft = null;
        CancelActiveParameterEdit();
        try
        {
            if (!TryReadLightingDraft(out var draft) || draft is null) return false;
            if (source.Lights.SequenceEqual(draft.Snapshot.Lights) && source.Groups.SequenceEqual(draft.Snapshot.Groups)
                && source.Settings == draft.Snapshot.Settings) return true;
            committing = true;
            try
            {
                if (CommitRequested?.Invoke(draft) != true) return false;
                source = draft.Snapshot;
                RefreshColor();
                return true;
            }
            finally { committing = false; }
        }
        finally
        {
            if (hadNumeric) PreviewRequested?.Invoke(null);
        }
    }

    // Parent changes read and validate the same complete draft, then publish the local transform
    // and pending parameters together. Reading it must never create a preliminary Undo entry.
    private bool TryReadLightingDraft(out EditorLightingDraft? draft)
    {
        draft = null;
        if (ViewModel is not { } model) return false;
        if (!TryReadGlobalSettings(out _)) return false;
        if (model.SelectedLight is { } selected)
        {
            var hex = this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(input =>
                input.Name == "LightHexColor" && ReferenceEquals(input.DataContext, selected));
            if (hex is { Text: null } && !ReferenceEquals(renderedColorSource, selected)) RefreshColor();
            if (hex is not null && ReferenceEquals(renderedColorSource, selected)
                && !string.Equals(hex.Text, renderedHexText, StringComparison.OrdinalIgnoreCase)
                && !ReadHex(hex, selected)) return false;
            rawHexText.Remove(selected.Id);
            if (hex is not null && string.Equals(hex.Text, renderedHexText, StringComparison.OrdinalIgnoreCase))
                DataValidationErrors.ClearErrors(hex);
        }
        if (HasInvalidCommitInputs())
        { model.SetValidationMessage("请先修正标出的灯光输入值。"); return false; }
        return model.TryCreate(out draft) && draft is not null;
    }

    private void SelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (refreshing || recoveryRestoreDepth != 0 || args.PropertyName != nameof(EditorLightingViewModel.SelectedLightIds)) return;
        if (groupParameterEdit is not null) CancelActiveGroupParameterEdit();
        if (parameterEdit is { } editing && (ViewModel?.ShowSingleLight != true || ViewModel.SelectedLight?.Id != editing.Light.Id)
            || padEdit is { } activePad && (ViewModel?.ShowSingleLight != true || ViewModel.SelectedLight?.Id != activePad.Light.Id)
            || numericEdit is { } activeNumeric && (ViewModel?.ShowSingleLight != true || ViewModel.SelectedLight?.Id != activeNumeric.Light.Id)) CancelActiveParameterEdit();
        CaptureRawControlText();
        if (!performingSelectionAction)
        {
            if (SelectionSetRequested is { } selectMany) selectMany(ViewModel?.SelectedLightIds ?? [], ViewModel?.SelectedLight?.Id);
            else SelectionRequested?.Invoke(ViewModel?.SelectedLight?.Id);
        }
        SyncObservedPreviewLight();
        this.FindControl<TextBlock>("EmptyLightPrompt")!.IsVisible = ViewModel?.HasSelection != true;
        RefreshParentControls();
        Dispatcher.UIThread.Post(RefreshColor);
    }
    private void SelectLegacyLight(object? sender, SelectionChangedEventArgs args)
    {
        // Modern selection belongs to the multi-row browser/viewport. Replacing ItemsSource on
        // the hidden legacy selector must never clear the new model's group selection.
        if (modernParameters || refreshing || recoveryRestoreDepth != 0 || sender is not ComboBox choice || ViewModel is not { } model) return;
        if (ReferenceEquals(model.SelectedLight, choice.SelectedItem)) return;
        model.SelectedLight = choice.SelectedItem as EditorNativeLightItem;
    }
    private void CommitInput(object? sender, RoutedEventArgs args)
    {
        if (refreshing || parameterEdit is not null || padEdit is not null || groupParameterEdit is not null) return;
        bool hadNumeric = numericEdit is not null;
        numericEdit = null;
        focusedNumericDraft = null;
        try { TryCommit(); }
        finally { if (hadNumeric) PreviewRequested?.Invoke(null); }
    }
    private void CommitToggle(object? sender, RoutedEventArgs args) { if (!refreshing) CommitSwitch(sender); }
    internal bool CommitSwitch(object? sender)
    {
        if (TryCommit()) return true;
        // A rejected capacity change leaves the authored project untouched. Reflect that
        // result in the switch while preserving every independent unfinished numeric field.
        if (sender is CheckBox toggle)
        {
            if (toggle.DataContext is EditorNativeLightItem item && source.Lights.FirstOrDefault(light => light.Id == item.Id) is { } before)
            {
                if (Equals(toggle.Tag, "enabled")) { item.Enabled = before.Enabled; toggle.SetCurrentValue(CheckBox.IsCheckedProperty, before.Enabled); }
                else if (Equals(toggle.Tag, "shadows")) { item.CastShadows = before.CastShadows; toggle.SetCurrentValue(CheckBox.IsCheckedProperty, before.CastShadows); }
            }
            else if (toggle.DataContext is EditorLightGroupItem group && source.Groups.FirstOrDefault(value => value.Id == group.Id) is { } beforeGroup)
            { group.Enabled = beforeGroup.Enabled; toggle.SetCurrentValue(CheckBox.IsCheckedProperty, beforeGroup.Enabled); }
        }
        return false;
    }
    private void CommitHexColor(object? sender, RoutedEventArgs args) { if (!refreshing && parameterEdit is null && recoveryRestoreDepth == 0 && sender is TextBox input) ApplyHex(input); }
    private void ApplyHex(TextBox input)
    {
        if (ViewModel?.SelectedLight is not { } light) return;
        if (ReferenceEquals(renderedColorSource, light)
            && !string.Equals(input.Text, renderedHexText, StringComparison.OrdinalIgnoreCase) && !ReadHex(input, light)) return;
        rawHexText.Remove(light.Id);
        DataValidationErrors.ClearErrors(input);
        TryCommit();
    }
    private static bool ReadHex(TextBox input, EditorNativeLightItem light)
    {
        string value = (input.Text ?? "").Trim().TrimStart('#');
        if (value.Length != 6 || !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            DataValidationErrors.SetError(input, new FormatException(Text("请填写六位十六进制颜色，例如 #FFFFFF。")));
            return false;
        }
        DataValidationErrors.ClearErrors(input);
        light.Red = ((rgb >> 16) & 255) / 255m;
        light.Green = ((rgb >> 8) & 255) / 255m;
        light.Blue = (rgb & 255) / 255m;
        return true;
    }
    private void ChooseColor(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string value } || ViewModel?.SelectedLight is not { } light) return;
        var color = Color.Parse(value);
        rawHexText.Remove(light.Id);
        light.Red = color.R / 255m; light.Green = color.G / 255m; light.Blue = color.B / 255m;
        foreach (var input in this.GetVisualDescendants().OfType<TextBox>().Where(input => input.Name == "LightHexColor"))
            DataValidationErrors.ClearErrors(input);
        RefreshColor();
        TryCommit();
    }
    private void RefreshColor()
    {
        if (ViewModel?.SelectedLight is not { } light) return;
        static byte Channel(decimal? channel) => (byte)decimal.Round(Math.Clamp(channel ?? 0, 0, 1) * 255);
        var color = Color.FromRgb(Channel(light.Red), Channel(light.Green), Channel(light.Blue));
        foreach (var swatch in this.GetVisualDescendants().OfType<Border>().Where(control => control.Name == "LightColorSwatch" && ReferenceEquals(control.DataContext, light)))
            swatch.Background = new SolidColorBrush(color);
        foreach (var input in this.GetVisualDescendants().OfType<TextBox>().Where(control => control.Name == "LightHexColor" && ReferenceEquals(control.DataContext, light)))
        {
            renderedHexText = ColorHex(light);
            input.Text = rawHexText.GetValueOrDefault(light.Id) ?? renderedHexText;
            renderedColorSource = light;
        }
    }
    private static string ColorHex(EditorNativeLightItem light)
    {
        static byte Channel(decimal? channel) => (byte)decimal.Round(Math.Clamp(channel ?? 0, 0, 1) * 255);
        return $"#{Channel(light.Red):X2}{Channel(light.Green):X2}{Channel(light.Blue):X2}";
    }
}
