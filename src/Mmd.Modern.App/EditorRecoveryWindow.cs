using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
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
        AddButtonContrastStyles();
        var copies = sessions.SelectMany(session => session.Checkpoints)
            .OrderByDescending(copy => copy.CreatedUtc).ToArray();
        var list = new ListBox { Name = "RecoveryCopies", Classes = { "recoveryCopies" } };
        foreach (var copy in copies)
        {
            var metaText = new TextBlock
            {
                Text = EditorLocalization.Format("Mda.Text.Recovery.Checkpoint",
                    copy.CreatedUtc.ToLocalTime().ToString("g"), (copy.PackageSizeBytes / (1024d * 1024d)).ToString("0.0")),
                FontSize = 12
            };
            metaText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("EditorMuted"));
            var originalText = new TextBlock
            {
                Text = EditorLocalization.Format("Mda.Text.Recovery.Original", copy.OriginalProjectPath ?? Text("Unnamed")),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12
            };
            originalText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("EditorMuted"));
            list.Items.Add(new ListBoxItem
            {
                Theme = new ControlTheme(typeof(ListBoxItem)),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel
                {
                    Spacing = 4, Margin = new Thickness(12, 10), Children =
                    {
                        // The display name usually already ends with the extension; only add it when it does not.
                        new TextBlock { Text = copy.DisplayName.EndsWith(copy.ProjectFormat.Extension, StringComparison.OrdinalIgnoreCase)
                                ? copy.DisplayName : copy.DisplayName + " (" + copy.ProjectFormat.Extension + ")",
                            FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                        metaText,
                        originalText
                    }
                }
            });
        }
        var restore = new Button { Name = "RestoreRecoveryButton", Content = Text("Restore"), IsDefault = true, MinWidth = 104, Classes = { "primary" } };
        var later = new Button { Name = "DeferRecoveryButton", Content = Text("Later"), IsCancel = true, MinWidth = 80, Classes = { "quiet" } };
        var dismiss = new Button { Name = "DismissRecoveryButton", Content = Text("Dismiss"), MinWidth = 140 };
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
            LineHeight = 22, Margin = new Thickness(0, 0, 0, 14) });
        var listSurface = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = list
        };
        listSurface.Bind(Border.BackgroundProperty, this.GetResourceObservable("EditorPanelSurface"));
        listSurface.Bind(Border.BorderBrushProperty, this.GetResourceObservable("EditorBorder"));
        Grid.SetRow(listSurface, 1); body.Children.Add(listSurface);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { later, restore } };
        var footerRow = new AdaptiveFooterPanel();
        footerRow.Children.Add(dismiss);
        footerRow.Children.Add(actions);
        var footer = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 16, 0, 0),
            Padding = new Thickness(0, 14, 0, 0),
            Child = footerRow
        };
        footer.Bind(Border.BorderBrushProperty, this.GetResourceObservable("EditorBorder"));
        Grid.SetRow(footer, 2); body.Children.Add(footer); Content = body;
        if (copies.Length > 0) list.SelectedIndex = 0;
        UpdateSelection();
        Opened += (_, _) => list.Focus();
    }

    private void AddButtonContrastStyles()
    {
        Styles.Add(new Style(x => x.OfType<Button>().PropertyEquals(Button.IsDefaultProperty, true).Descendant().OfType<TextBlock>())
        {
            Setters = { new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("MdaAccentText")) }
        });
        Styles.Add(new Style(x => x.OfType<Button>().Class("primary").Descendant().OfType<TextBlock>())
        {
            Setters = { new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("MdaAccentText")) }
        });
        Styles.Add(new Style(x => x.OfType<Button>().Class(":disabled").Descendant().OfType<TextBlock>())
        {
            Setters = { new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("EditorDisabledText")) }
        });
        Styles.Add(new Style(x => x.OfType<Button>().PropertyEquals(Button.IsDefaultProperty, true).Class(":disabled").Descendant().OfType<TextBlock>())
        {
            Setters = { new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("EditorDisabledText")) }
        });
        Styles.Add(new Style(x => x.OfType<Button>().Class("primary").Class(":disabled").Descendant().OfType<TextBlock>())
        {
            Setters = { new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("EditorDisabledText")) }
        });
    }

    private static string Text(string suffix) => EditorLocalization.Text("Mda.Text.Recovery." + suffix);

    private sealed class AdaptiveFooterPanel : Panel
    {
        private const double HorizontalGap = 16;
        private const double VerticalGap = 10;

        protected override Size MeasureOverride(Size availableSize)
        {
            if (Children.Count != 2) return base.MeasureOverride(availableSize);
            var left = Children[0];
            var right = Children[1];
            left.Measure(availableSize);
            right.Measure(availableSize);
            double singleWidth = left.DesiredSize.Width + HorizontalGap + right.DesiredSize.Width;
            if (double.IsInfinity(availableSize.Width) || singleWidth <= availableSize.Width + 0.5)
                return new Size(double.IsInfinity(availableSize.Width) ? singleWidth : availableSize.Width,
                    Math.Max(left.DesiredSize.Height, right.DesiredSize.Height));
            return new Size(availableSize.Width, left.DesiredSize.Height + VerticalGap + right.DesiredSize.Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (Children.Count != 2) return base.ArrangeOverride(finalSize);
            var left = Children[0];
            var right = Children[1];
            double singleWidth = left.DesiredSize.Width + HorizontalGap + right.DesiredSize.Width;
            if (singleWidth <= finalSize.Width + 0.5)
            {
                double rowHeight = Math.Max(left.DesiredSize.Height, right.DesiredSize.Height);
                left.Arrange(new Rect(0, (rowHeight - left.DesiredSize.Height) / 2, left.DesiredSize.Width, left.DesiredSize.Height));
                right.Arrange(new Rect(Math.Max(0, finalSize.Width - right.DesiredSize.Width),
                    (rowHeight - right.DesiredSize.Height) / 2, right.DesiredSize.Width, right.DesiredSize.Height));
            }
            else
            {
                double leftWidth = Math.Min(finalSize.Width, left.DesiredSize.Width);
                double rightWidth = Math.Min(finalSize.Width, right.DesiredSize.Width);
                left.Arrange(new Rect(0, 0, leftWidth, left.DesiredSize.Height));
                right.Arrange(new Rect(Math.Max(0, finalSize.Width - rightWidth),
                    left.DesiredSize.Height + VerticalGap, rightWidth, right.DesiredSize.Height));
            }
            return finalSize;
        }
    }
}
