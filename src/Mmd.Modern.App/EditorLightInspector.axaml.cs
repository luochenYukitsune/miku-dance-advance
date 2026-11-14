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
    private readonly HashSet<EditorNumericUpDown> restoredNumericControls = [];
    public EditorLightInspector()
    {
        InitializeComponent();
        LayoutUpdated += (_, _) =>
        {
            ApplyRecoveryNumericText();
            CaptureRawControlText();
            if (ViewModel?.SelectedLight is { } light && !ReferenceEquals(renderedColorSource, light)) RefreshColor();
        };
        KeyDown += (_, args) =>
        {
            if (recoveryRestoreDepth != 0) return;
            if (args.Key != Key.Enter || args.KeyModifiers != KeyModifiers.None) return;
            if (args.Source is TextBox { Name: "LightHexColor" } hex) ApplyHex(hex);
            else TryCommit();
            args.Handled = true;
        };
    }
    public Func<EditorLightingDraft, bool>? CommitRequested { get; set; }
    public Action<Guid?>? SelectionRequested { get; set; }
    public EditorLightingViewModel? ViewModel => DataContext as EditorLightingViewModel;

    public void SetLighting(SceneLightingSnapshot snapshot, Guid? selectedId, Guid? documentId = null)
    {
        if (refreshing || committing || recoveryRestoreDepth != 0) return;
        refreshing = true;
        try
        {
            if (!ReferenceEquals(source, snapshot) || sourceDocumentId != documentId || ViewModel is null)
            {
                if (ViewModel is { } old) old.PropertyChanged -= SelectionChanged;
                source = snapshot;
                sourceDocumentId = documentId;
                ClearRawInput();
                var model = new EditorLightingViewModel(snapshot, selectedId);
                // A light context may be open with no light selected; do not silently select a row.
                model.SelectedLight = model.Lights.FirstOrDefault(light => light.Id == selectedId);
                DataContext = model;
                model.PropertyChanged += SelectionChanged;
            }
            else if (ViewModel is { } current)
                current.SelectedLight = current.Lights.FirstOrDefault(light => light.Id == selectedId);
            this.FindControl<TextBlock>("EmptyLightPrompt")!.IsVisible = ViewModel?.HasSelectedLight != true;
        }
        finally { refreshing = false; }
    }

    internal EditorRecoveryNativeLightingState CaptureRecoveryState(SceneLightingSnapshot committed, EditorInspectorPage page,
        Guid? documentId = null)
    {
        // A hidden inspector can still belong to an earlier document. Only its matching committed
        // binding owns these drafts; historical item originals are deliberately not that binding.
        bool matching = sourceDocumentId == documentId && source.Lights.SequenceEqual(committed.Lights) && ViewModel is { };
        var model = matching ? ViewModel! : new EditorLightingViewModel(committed);
        if (matching) CaptureRawControlText();
        var state = new EditorRecoveryNativeLightingState
        {
            InspectorPage = page, SelectedLightId = matching ? model.SelectedLight?.Id : null,
            CommittedLights = committed.Lights.ToImmutableArray(),
            Lights = model.Lights.Select(light => light.CaptureRecoveryDraft() with
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
        state.ValidateAgainst(committed);
        // Construct every item before changing the live inspector. No Build or commit is performed.
        var items = state.Lights.Select(EditorNativeLightItem.RestoreRecoveryDraft).ToArray();
        var model = new EditorLightingViewModel(SceneLightingSnapshot.Empty);
        foreach (var item in items) model.Lights.Add(item);
        model.SelectedLight = model.Lights.FirstOrDefault(light => light.Id == state.SelectedLightId);
        refreshing = true;
        try
        {
            if (ViewModel is { } previous) previous.PropertyChanged -= SelectionChanged;
            source = committed;
            sourceDocumentId = documentId;
            ClearRawInput();
            foreach (var light in state.Lights)
            {
                if (light.HexText is { } hex) rawHexText.Add(light.Original.Id, hex);
                if (light.NumericText.Count != 0) rawNumericText.Add(light.Original.Id, light.NumericText);
            }
            DataContext = model;
            model.PropertyChanged += SelectionChanged;
            this.FindControl<TextBlock>("EmptyLightPrompt")!.IsVisible = !model.HasSelectedLight;
            UpdateLayout();
            RefreshColor();
            ApplyRecoveryNumericText();
        }
        finally { refreshing = false; }
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
        rawHexText.Clear(); rawNumericText.Clear(); restoredNumericControls.Clear();
        renderedColorSource = null; renderedHexText = "";
    }

    private void CaptureRawControlText()
    {
        if (refreshing || ViewModel is null) return;
        foreach (var input in this.GetVisualDescendants().OfType<EditorNumericUpDown>())
        {
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
            if (input.DataContext is not EditorNativeLightItem light || input.Name is not { } name
                || !EditorRecoveryNativeLightDraft.NumericNames.Contains(name) || !restoredNumericControls.Add(input)) continue;
            if (rawNumericText.TryGetValue(light.Id, out var values) && values.TryGetValue(name, out string? text))
                input.SetCurrentValue(NumericUpDown.TextProperty, text);
        }
    }

    public bool TryCommit()
    {
        if (refreshing || committing || recoveryRestoreDepth != 0 || ViewModel is not { } model) return true;
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
        if (EditorNumericUpDown.HasInvalidInputs(this))
        { model.SetValidationMessage("请先修正标出的灯光输入值。"); return false; }
        if (!model.TryCreate(out var draft) || draft is null) return false;
        if (source.Lights.SequenceEqual(draft.Snapshot.Lights)) return true;
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

    private void SelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (refreshing || recoveryRestoreDepth != 0 || args.PropertyName != nameof(EditorLightingViewModel.SelectedLight)) return;
        CaptureRawControlText();
        SelectionRequested?.Invoke(ViewModel?.SelectedLight?.Id);
        this.FindControl<TextBlock>("EmptyLightPrompt")!.IsVisible = ViewModel?.HasSelectedLight != true;
        Dispatcher.UIThread.Post(RefreshColor);
    }
    private void CommitInput(object? sender, RoutedEventArgs args) { if (!refreshing) TryCommit(); }
    private void CommitToggle(object? sender, RoutedEventArgs args) { if (!refreshing) TryCommit(); }
    private void CommitHexColor(object? sender, RoutedEventArgs args) { if (!refreshing && recoveryRestoreDepth == 0 && sender is TextBox input) ApplyHex(input); }
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
