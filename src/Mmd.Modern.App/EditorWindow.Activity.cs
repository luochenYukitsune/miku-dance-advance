using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Mmd.Modern.App;

public partial class EditorWindow
{
    private void RefreshActivityPresentation()
    {
        if (_shell is not { } shell) return;
        var state = shell.ActivityState;
        string icon = state switch
        {
            EditorActivityState.Busy or EditorActivityState.Cancelling => "dot-outline",
            EditorActivityState.Committing => "lock-simple",
            EditorActivityState.Completed => "check-circle",
            EditorActivityState.Cancelled => "prohibit",
            EditorActivityState.Failed => "warning-circle",
            _ => "circle"
        };
        string brushKey = state switch
        {
            EditorActivityState.Busy or EditorActivityState.Completed => "EditorLoadingAccent",
            EditorActivityState.Committing => "EditorSelection",
            EditorActivityState.Failed => "EditorErrorAccent",
            _ => "EditorMuted"
        };
        var statusIcon = this.FindControl<PathIcon>("OperationStatusIcon")!;
        if (this.TryFindResource("Phosphor." + icon, out var geometry) && geometry is Geometry data) statusIcon.Data = data;
        if (this.TryFindResource(brushKey, out var value) && value is IBrush brush) statusIcon.Foreground = brush;
        if (this.TryFindResource(state == EditorActivityState.Failed ? "EditorErrorAccent" : "EditorMuted", out var textValue)
            && textValue is IBrush textBrush) this.FindControl<TextBlock>("OperationStatusText")!.Foreground = textBrush;
        // LoadingDots observes the desktop preference while attached, including
        // settings changes during an operation and minimize/restore.
    }

    private async void ViewOperationDetails(object? sender, RoutedEventArgs args)
    {
        if (_shell is { HasActivityDetails: true } shell)
            await ShowActivityDetailsAsync(shell.ActivityFailureSummary, shell.ActivityDetails, this);
    }

    private async Task ShowErrorAsync(Exception error, Window? owner = null)
    {
        _shell?.ReportActivityFailure(error);
        string summary = _shell?.ActivityFailureSummary ?? EditorLocalization.Text("操作未完成。请查看技术详情。");
        await ShowActivityDetailsAsync(summary, error.ToString(), owner?.IsVisible == true ? owner : this);
    }

    // Reuses the editor's existing modal error outlet; technical exceptions stay off the status line.
    private static async Task ShowActivityDetailsAsync(string summary, string details, Window owner)
    {
        var dialog = new Window
        {
            Title = EditorLocalization.Text("操作失败"), Width = 520, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = owner.ActualThemeVariant, FontFamily = owner.FontFamily, FontSize = 12, UseLayoutRounding = true
        };
        EditorDialogTheme.Apply(dialog, owner);
        var copy = new Button { Name = "CopyOperationLogButton", Content = EditorLocalization.Text("Mda.Text.ExportStatus.CopyLog"), MinWidth = 76, Height = 26 };
        copy.Click += async (_, _) =>
        {
            try
            {
                if (dialog.Clipboard is not { } clipboard) throw new InvalidOperationException();
                await clipboard.SetTextAsync(details);
                copy.Content = EditorLocalization.Text("已复制");
            }
            catch
            {
                copy.Content = EditorLocalization.Text("复制失败");
                ToolTip.SetTip(copy, EditorLocalization.Text("未能复制详情。请在技术详情中选择并复制。"));
            }
        };
        var close = new Button { Content = EditorLocalization.Text("关闭"), MinWidth = 64, Height = 26, IsCancel = true, IsDefault = true };
        close.Click += (_, _) => dialog.Close();
        var technicalDetails = new TextBox
        {
            Name = "OperationErrorDetails", Text = details, IsReadOnly = true, AcceptsReturn = true,
            FontFamily = new FontFamily("Consolas, monospace"), FontSize = 11, MaxHeight = 160,
            TextWrapping = TextWrapping.NoWrap
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(technicalDetails, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(technicalDetails, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 16,
            Children =
            {
                new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 18 },
                new Expander { Header = EditorLocalization.Text("技术详情"), Content = technicalDetails, HorizontalAlignment = HorizontalAlignment.Stretch },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { copy, close } },
                new TextBlock { Name = "OperationLogAiHint", Text = EditorLocalization.Text("Mda.Text.ExportStatus.LogAiHint"),
                    TextWrapping = TextWrapping.Wrap, Foreground = owner.TryFindResource("EditorMuted", out var muted) && muted is IBrush brush ? brush : owner.Foreground,
                    FontSize = 12, LineHeight = 18 }
            }
        };
        dialog.Opened += (_, _) => close.Focus();
        await dialog.ShowDialog(owner);
    }
}
