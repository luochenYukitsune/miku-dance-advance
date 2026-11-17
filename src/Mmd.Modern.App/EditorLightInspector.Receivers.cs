using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Mmd.Modern.Core;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

public sealed class EditorLightReceiverChoice(Guid objectId, string name) : INotifyPropertyChanged
{
    private bool isChecked;
    public Guid ObjectId { get; } = objectId;
    public string Name { get; } = name;
    public bool IsChecked { get => isChecked; set { if (isChecked == value) return; isChecked = value; PropertyChanged?.Invoke(this, new(nameof(IsChecked))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
public sealed record EditorLightReceiverModeChoice(SceneLightReceiverMode Mode)
{
    public string Label => Text("Mda.Text.Context.Lighting.Receivers" + Mode);
}

public partial class EditorLightInspector
{
    private bool refreshingReceivers;
    private Guid? receiverDocumentId;
    private readonly Dictionary<Guid, EditorLightReceiverChoice> receiverChoices = [];

    private void RefreshReceiverControls()
    {
        if (refreshingReceivers) return;
        refreshingReceivers = true;
        try
        {
            var panel = this.FindControl<StackPanel>("LightReceiverProperties")!;
            var mode = this.FindControl<ComboBox>("LightReceiverMode")!;
            var list = this.FindControl<ItemsControl>("LightReceiverModels")!;
            var search = this.FindControl<TextBox>("LightReceiverSearch")!;
            if (receiverDocumentId != sourceDocumentId) { receiverDocumentId = sourceDocumentId; search.Text = ""; }
            bool available = modernParameters && ViewModel is { ShowSingleLight: true } && resolveParentTransform is not null;
            panel.IsVisible = available; mode.IsEnabled = available;
            var selected = ViewModel?.SelectedLight;
            var authored = source.Lights.FirstOrDefault(light => light.Id == selected?.Id)?.Receivers;
            var current = authored?.Mode ?? SceneLightReceiverMode.All;
            mode.ItemsSource ??= Enum.GetValues<SceneLightReceiverMode>().Select(value => new EditorLightReceiverModeChoice(value)).ToArray();
            mode.SelectedItem = mode.Items.Cast<EditorLightReceiverModeChoice>().Single(choice => choice.Mode == current);
            this.FindControl<StackPanel>("LightReceiverSelection")!.IsVisible = current != SceneLightReceiverMode.All;
            foreach (var removed in receiverChoices.Keys.Except(parentModels.Select(model => model.ObjectId)).ToArray()) receiverChoices.Remove(removed);
            foreach (var model in parentModels)
            {
                if (!receiverChoices.TryGetValue(model.ObjectId, out var choice) || choice.Name != model.Name)
                    receiverChoices[model.ObjectId] = choice = new(model.ObjectId, model.Name);
                choice.IsChecked = authored?.ObjectIds.Contains(model.ObjectId) == true;
            }
            string query = search.Text?.Trim() ?? "";
            var visible = parentModels.Select(model => receiverChoices[model.ObjectId])
                .Where(choice => query.Length == 0 || choice.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            if (list.ItemsSource is not IEnumerable<EditorLightReceiverChoice> old || !old.SequenceEqual(visible)) list.ItemsSource = visible;
            this.FindControl<TextBlock>("LightReceiverCount")!.Text = Format("Mda.Text.Context.Lighting.ReceiversCount", authored?.ObjectIds.Count ?? 0);
        }
        finally { refreshingReceivers = false; }
    }
    private void SearchLightReceivers(object? sender, TextChangedEventArgs args) => RefreshReceiverControls();
    private void SelectLightReceiverMode(object? sender, SelectionChangedEventArgs args)
    {
        if (refreshingReceivers || sender is not ComboBox { SelectedItem: EditorLightReceiverModeChoice choice }) return;
        CommitReceiverSelection(choice.Mode);
    }
    private void SelectLightReceiver(object? sender, RoutedEventArgs args)
    {
        if (refreshingReceivers || this.FindControl<ComboBox>("LightReceiverMode")?.SelectedItem is not EditorLightReceiverModeChoice choice) return;
        CommitReceiverSelection(choice.Mode);
    }
    private void CommitReceiverSelection(SceneLightReceiverMode mode)
    {
        try
        {
            if (!modernParameters || refreshing || committing || recoveryRestoreDepth != 0
                || ViewModel is not { ShowSingleLight: true, SelectedLight: { } selected } model) return;
            if (EditStartingRequested?.Invoke() == false) return;
            CancelActiveParameterEdit();
            if (!TryReadLightingDraft(out var draft) || draft is null) return;
            var receivers = mode == SceneLightReceiverMode.All ? null
                : new SceneLightReceivers(mode, receiverChoices.Values.Where(choice => choice.IsChecked).Select(choice => choice.ObjectId));
            var snapshot = draft.Snapshot.WithLights(draft.Snapshot.Lights.Select(light => light.Id == selected.Id
                ? light with { Receivers = receivers } : light));
            if (source.Lights.SequenceEqual(snapshot.Lights) && source.Groups.SequenceEqual(snapshot.Groups)
                && source.Settings == snapshot.Settings) return;
            var candidate = new EditorLightingDraft(snapshot, draft.SelectedLightId, draft.SelectedLightIds, draft.SelectedGroupIds);
            committing = true; bool accepted;
            try { accepted = CommitRequested?.Invoke(candidate) == true; }
            finally { committing = false; }
            if (!accepted) { model.SetValidationMessage("Mda.Text.Context.Lighting.ReceiversFailed"); return; }
            source = draft.Snapshot;
            SetLighting(snapshot, candidate.SelectedLightId, sourceDocumentId, candidate.SelectedLightIds);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        { ViewModel?.SetValidationMessage("Mda.Text.Context.Lighting.ReceiversFailed"); }
        finally { RefreshReceiverControls(); }
    }
}
