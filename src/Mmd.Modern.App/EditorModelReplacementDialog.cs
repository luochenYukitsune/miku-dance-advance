using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Mmd.Modern.Application;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>Explicit review before any source or orphan binding changes. Authored names remain verbatim.</summary>
internal static class EditorModelReplacementDialog
{
    public static Task<bool?> ConfirmAsync(Window owner, string name, SceneModelReplacementPlan plan, bool keepsMapping)
    {
        var dialog = Create(owner, "ModelReplacementDialog", Text("Mda.Text.Context.ReplaceModel.Title"));
        var restore = new CheckBox
        {
            Name = "RestoreCompatibleUnownedCheck", IsVisible = plan.RecoverableCount > 0, IsChecked = false,
            Content = Text("Mda.Text.Context.ReplaceModel.RestoreCheck")
        };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Copy(Format("Mda.Text.Context.ReplaceModel.Files", name, System.IO.Path.GetFileName(plan.SourcePath)), owner, true));
        body.Children.Add(Copy(Format("Mda.Text.Context.ReplaceModel.Summary", plan.MatchedBoneCount, plan.MatchedMorphCount), owner));
        body.Children.Add(Copy(Text("Mda.Text.Context.ReplaceModel.KeepHelp"), owner));
        if (!keepsMapping) body.Children.Add(Copy(Text("Mda.Text.Context.ReplaceModel.MaterialWarning"), owner));
        body.Children.Add(NoticeList(plan.Notices, owner));
        body.Children.Add(restore);
        var cancel = Action("CancelModelReplacement", "取消"); cancel.IsCancel = true;
        var apply = Action("ConfirmModelReplacement", "Mda.Text.Context.ReplaceModel.Confirm"); apply.IsDefault = true;
        cancel.Click += (_, _) => dialog.Close(null);
        apply.Click += (_, _) => dialog.Close(restore.IsChecked == true);
        body.Children.Add(Actions(cancel, apply)); dialog.Content = body;
        dialog.Opened += (_, _) => cancel.Focus();
        return dialog.ShowDialog<bool?>(owner);
    }

    public static Task<bool> ConfirmRestorationAsync(Window owner, string source, string target, SceneOrphanRestorationPlan plan)
    {
        var dialog = Create(owner, "UnownedDataRestorationDialog", Text("Mda.Text.Context.ReplaceModel.Unowned"));
        var cancel = Action("KeepUnownedData", "Mda.Text.Context.ReplaceModel.KeepUnowned"); cancel.IsCancel = true;
        var apply = Action("ConfirmUnownedRestoration", "Mda.Text.Context.ReplaceModel.RestoreAction");
        apply.IsEnabled = plan.RecoverableCount > 0;
        cancel.Click += (_, _) => dialog.Close(false); apply.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel { Spacing = 12, Children =
        {
            Copy(Format("Mda.Text.Context.ReplaceModel.RestoreSummary", source, target, plan.RecoverableCount), owner, true),
            Copy(Text("Mda.Text.Context.ReplaceModel.RestoreHelp"), owner),
            NoticeList(plan.Notices, owner), Actions(cancel, apply)
        } };
        dialog.Opened += (_, _) => cancel.Focus();
        return dialog.ShowDialog<bool>(owner);
    }

    private static Window Create(Window owner, string name, string title) => new()
    {
        Name = name, Title = title, Width = 620, MaxHeight = 760, SizeToContent = SizeToContent.Height,
        Padding = new Thickness(22), CanResize = true, ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, RequestedThemeVariant = owner.ActualThemeVariant,
        Background = owner.Background, Foreground = owner.Foreground, FontFamily = owner.FontFamily,
        FontSize = 13, UseLayoutRounding = true
    };

    private static Control NoticeList(IReadOnlyList<SceneModelDataNotice> notices, Window owner)
    {
        if (notices.Count == 0) return Copy(Text("Mda.Text.Context.ReplaceModel.AllMatched"), owner);
        var rows = new StackPanel { Spacing = 7 };
        foreach (var item in notices)
        {
            var text = Format(item.CanRestore ? "Mda.Text.Context.ReplaceModel.RestorableRow" : "Mda.Text.Context.ReplaceModel.UnownedRow",
                Text("Mda.Text.Context.ReplaceModel.Kind." + item.Kind), item.Name);
            rows.Children.Add(Copy(text, owner));
        }
        return new ScrollViewer { Name = "ModelReplacementNoticeList", MaxHeight = 300, Content = rows,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }
    private static TextBlock Copy(string text, Window owner, bool strong = false) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Foreground = owner.Foreground, LineHeight = 21,
        FontWeight = strong ? FontWeight.SemiBold : FontWeight.Normal
    };
    private static Button Action(string name, string text)
    {
        var result = new Button { Name = name, Content = Text(text), MinWidth = 78, Padding = new Thickness(12, 6) };
        AutomationProperties.SetName(result, Text(text)); return result;
    }
    private static StackPanel Actions(params Control[] buttons)
    {
        var result = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var button in buttons) result.Children.Add(button); return result;
    }
}
