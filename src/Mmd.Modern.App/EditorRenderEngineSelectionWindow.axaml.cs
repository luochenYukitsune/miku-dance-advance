using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Styling;

namespace Mmd.Modern.App;

/// <summary>Only an explicit confirmation returns an engine ID. Cancel and window close return null.</summary>
internal sealed partial class EditorRenderEngineSelectionWindow : Window
{
    private readonly ListBox choicesList;
    private readonly Button confirmButton;
    private bool synchronizingSelection;

    internal EditorRenderEngineSelectionWindow(IEnumerable<EditorRenderEngineChoice> choices, string defaultChoiceId = "modern")
    {
        SelectionModel = new(choices, defaultChoiceId);
        InitializeComponent();
        choicesList = this.FindControl<ListBox>("RenderEngineChoices")!;
        confirmButton = this.FindControl<Button>("ConfirmRenderEngineButton")!;
        choicesList.SelectionChanged += (_, _) => UpdateSelection();
        RefreshChoices();
        EditorLocalization.Observe(this, static window => window.RefreshChoices());
        Opened += (_, _) =>
        {
            if (Owner is { } owner)
            {
                RequestedThemeVariant = owner.ActualThemeVariant;
                FontFamily = owner.FontFamily;
                Resources["SelectionFont"] = owner.FontFamily;
            }
            if (choicesList.SelectedItem is ListBoxItem selected) selected.Focus();
            else this.FindControl<Button>("CancelRenderEngineButton")!.Focus();
        };
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            Close(null); args.Handled = true;
        };
    }

    internal EditorRenderEngineSelectionModel SelectionModel { get; }
    internal string? Result { get; private set; }

    private void RefreshChoices()
    {
        synchronizingSelection = true;
        try
        {
            choicesList.Items.Clear();
            for (int index = 0; index < SelectionModel.Choices.Count; index++)
            {
                var choice = SelectionModel.Choices[index];
                string name = EditorLocalization.Text(choice.DisplayName), purpose = EditorLocalization.Text(choice.Purpose);
                string? unavailable = choice.IsAvailable ? null : EditorLocalization.Format("Mda.Text.RenderEngineSelection.Unavailable",
                    EditorLocalization.Text(choice.UnavailableReason!));
                var content = new StackPanel { Spacing = 4, Children =
                {
                    new TextBlock { Text = name, Classes = { "engineName" } },
                    new TextBlock { Text = purpose, Classes = { "muted" } }
                } };
                if (unavailable is not null) content.Children.Add(new TextBlock
                {
                    Name = "RenderEngineUnavailable" + index, Text = unavailable, Classes = { "caption" }
                });
                var item = new ListBoxItem
                {
                    // Keep this window's quiet selection template. The global control theme
                    // otherwise adds a second bright accent fill inside the custom row.
                    Theme = new ControlTheme(typeof(ListBoxItem)),
                    Name = "RenderEngineChoice" + index, Tag = choice.Id, Content = content,
                    IsEnabled = choice.IsAvailable, HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                AutomationProperties.SetName(item, name);
                AutomationProperties.SetHelpText(item, unavailable is null ? purpose : purpose + " " + unavailable);
                choicesList.Items.Add(item);
                if (StringComparer.Ordinal.Equals(SelectionModel.SelectedChoice?.Id, choice.Id)) choicesList.SelectedItem = item;
            }
            this.FindControl<TextBlock>("RenderEngineSelectionEmpty")!.IsVisible = SelectionModel.Choices.Count == 0;
            confirmButton.IsEnabled = SelectionModel.CanConfirm;
        }
        finally { synchronizingSelection = false; }
    }

    private void UpdateSelection()
    {
        if (synchronizingSelection) return;
        string? selectedId = (choicesList.SelectedItem as ListBoxItem)?.Tag as string;
        if (!SelectionModel.TrySelect(selectedId))
        {
            synchronizingSelection = true;
            try
            {
                choicesList.SelectedItem = choicesList.Items.OfType<ListBoxItem>().FirstOrDefault(item =>
                    StringComparer.Ordinal.Equals(item.Tag as string, SelectionModel.SelectedChoice?.Id));
            }
            finally { synchronizingSelection = false; }
        }
        confirmButton.IsEnabled = SelectionModel.CanConfirm;
    }

    private void ConfirmSelection(object? sender, RoutedEventArgs args)
    {
        if (!SelectionModel.TryConfirm(out string? id)) return;
        Result = id;
        Close(id);
    }

    private void CancelSelection(object? sender, RoutedEventArgs args) => Close(null);
}
