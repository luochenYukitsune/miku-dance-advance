using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;

namespace Mmd.Modern.App;

internal sealed record EditorRecoveryChoice(EditorRecoveryCheckpoint Checkpoint, bool Dismiss);

/// <summary>Choosing a copy never overwrites the project that produced it.</summary>
internal sealed class EditorRecoveryWindow : Window
{
    internal EditorRecoveryWindow(Window owner, IReadOnlyList<EditorRecoverySessionInfo> sessions)
    {
        Title = Text("Title"); Width = 700; Height = 520; MinWidth = 520; MinHeight = 400;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        RequestedThemeVariant = owner.ActualThemeVariant;
        FontFamily = owner.FontFamily; FontSize = 14; UseLayoutRounding = true;
        EditorDialogTheme.Apply(this, owner);
        var copies = sessions.SelectMany(session => session.Checkpoints)
            .OrderByDescending(copy => copy.CreatedUtc).ToArray();
        var list = new ListBox { Name = "RecoveryCopies", Classes = { "recoveryCopies" } };
        foreach (var copy in copies)
        {
            list.Items.Add(new ListBoxItem
            {
                Theme = new ControlTheme(typeof(ListBoxItem)),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel
                {
                    Spacing = 4, Margin = new Thickness(8), Children =
                    {
                        // The display name usually already ends with the extension; only add it when it does not.
                        new TextBlock { Text = copy.DisplayName.EndsWith(copy.ProjectFormat.Extension, StringComparison.OrdinalIgnoreCase)
                                ? copy.DisplayName : copy.DisplayName + " (" + copy.ProjectFormat.Extension + ")",
                            FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = EditorLocalization.Format("Mda.Text.Recovery.Checkpoint",
                            copy.CreatedUtc.ToLocalTime().ToString("g"), (copy.PackageSizeBytes / (1024d * 1024d)).ToString("0.0")), FontSize = 12 },
                        new TextBlock { Text = EditorLocalization.Format("Mda.Text.Recovery.Original", copy.OriginalProjectPath ?? Text("Unnamed")),
                            TextWrapping = TextWrapping.Wrap, FontSize = 12 }
                    }
                }
            });
        }
        var restore = new Button { Name = "RestoreRecoveryButton", Content = Text("Restore"), IsDefault = true, MinWidth = 104, Classes = { "recoveryAction" } };
        var later = new Button { Name = "DeferRecoveryButton", Content = Text("Later"), IsCancel = true, MinWidth = 80, Classes = { "recoveryAction" } };
        var dismiss = new Button { Name = "DismissRecoveryButton", Content = Text("Dismiss"), MinWidth = 140, Classes = { "recoveryAction" } };
        void UpdateSelection()
        {
            restore.IsEnabled = dismiss.IsEnabled = list.SelectedIndex >= 0 && list.SelectedIndex < copies.Length;
        }
        list.SelectionChanged += (_, _) => UpdateSelection();
        restore.Click += (_, _) => { if (restore.IsEnabled) Close(new EditorRecoveryChoice(copies[list.SelectedIndex], false)); };
        dismiss.Click += (_, _) => { if (dismiss.IsEnabled) Close(new EditorRecoveryChoice(copies[list.SelectedIndex], true)); };
        later.Click += (_, _) => Close(null);
        var body = new Grid { Margin = new Thickness(24), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        body.Children.Add(new TextBlock { Text = copies.Length == 0 ? Text("Empty") : Text("Prompt"), TextWrapping = TextWrapping.Wrap,
            LineHeight = 22, Margin = new Thickness(0, 0, 0, 16) });
        Grid.SetRow(list, 1); body.Children.Add(list);
        var footer = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(0, 20, 0, 0) };
        dismiss.HorizontalAlignment = HorizontalAlignment.Left;
        footer.Children.Add(dismiss);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0), Children = { later, restore } };
        Grid.SetRow(actions, 1); footer.Children.Add(actions);
        Grid.SetRow(footer, 2); body.Children.Add(footer); Content = body;
        if (copies.Length > 0) list.SelectedIndex = 0;
        UpdateSelection();
        Opened += (_, _) => list.Focus();
    }

    private static string Text(string suffix) => EditorLocalization.Text("Mda.Text.Recovery." + suffix);
}
