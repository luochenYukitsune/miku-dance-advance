using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

public partial class OutputSettingsWindow : Window
{
    private readonly bool canExport;
    private readonly Func<CancellationToken, Task<EditorPreviewCapabilities>> queryGiCapabilities;
    private readonly CancellationTokenSource giQueryCancellation = new();
    private Task<bool>? giCapabilityQuery;
    private bool closed;
    public OutputSettingsWindow() : this(new OutputSettingsViewModel()) { }

    public OutputSettingsWindow(OutputSettingsViewModel viewModel, bool canExport = false,
        Func<CancellationToken, Task<EditorPreviewCapabilities>>? queryGiCapabilities = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        this.canExport = canExport;
        this.queryGiCapabilities = queryGiCapabilities
            ?? (token => Task.Run(() => EditorOutputCapabilities.QueryInstalledGi(token), token));
        var startButton = this.FindControl<Button>("StartExportButton")!;
        var applyButton = this.FindControl<Button>("ApplyOutputSettingsButton")!;
        startButton.IsEnabled = canExport;
        if (canExport)
        {
            applyButton.Classes.Remove("primary");
            applyButton.IsDefault = false;
            if (!startButton.Classes.Contains("primary")) startButton.Classes.Add("primary");
            startButton.IsDefault = true;
        }
        else
        {
            if (!applyButton.Classes.Contains("primary")) applyButton.Classes.Add("primary");
            applyButton.IsDefault = true;
            startButton.Classes.Remove("primary");
            startButton.IsDefault = false;
        }
        RefreshLocalization();
        Observe(this, static window => window.RefreshLocalization());
        Opened += async (_, _) =>
        {
            RequestedThemeVariant = Owner?.ActualThemeVariant ?? ThemeVariant.Dark;
            if (this.FindControl<TabControl>("OutputTabs")!.SelectedIndex == 1) await EnsureGiCapabilitiesAsync();
        };
        this.FindControl<TabControl>("OutputTabs")!.SelectionChanged += async (_, _) =>
        {
            if (IsVisible && this.FindControl<TabControl>("OutputTabs")!.SelectedIndex == 1) await EnsureGiCapabilitiesAsync();
        };
        Closed += (_, _) => { closed = true; giQueryCancellation.Cancel(); giQueryCancellation.Dispose(); };
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            Close(null); args.Handled = true;
        };
    }

    public OutputSettingsViewModel ViewModel => (OutputSettingsViewModel)DataContext!;
    public bool ExportRequested { get; private set; }

    private void RefreshLocalization()
    {
        string availability = Text(canExport ? "使用当前工程快照导出" : "载入模型后可导出");
        this.FindControl<TextBlock>("ExportAvailability")!.Text = availability;
        // A disabled export button explains itself on hover instead of looking merely broken.
        var start = this.FindControl<Button>("StartExportButton")!;
        ToolTip.SetTip(start, availability); ToolTip.SetShowOnDisabled(start, true);
    }

    private Task<bool> EnsureGiCapabilitiesAsync() => ViewModel.GiCapabilitiesReady
        ? Task.FromResult(true) : giCapabilityQuery ??= LoadGiCapabilitiesAsync();

    private async Task<bool> LoadGiCapabilitiesAsync()
    {
        try
        {
            var capabilities = await queryGiCapabilities(giQueryCancellation.Token);
            if (closed || giQueryCancellation.IsCancellationRequested) return false;
            ViewModel.ApplyGiCapabilities(capabilities); return true;
        }
        catch (OperationCanceledException) when (closed || giQueryCancellation.IsCancellationRequested) { return false; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (closed) return false;
            ViewModel.ApplyGiCapabilities(new() { GiUnavailableReason = "GI：设备查询未完成，当前未开放。" });
            return true;
        }
    }

    private async void StartExport(object? sender, RoutedEventArgs args)
    {
        if (this.GetVisualDescendants().OfType<Control>()
            .Any(control => control.IsEffectivelyEnabled && DataValidationErrors.GetHasErrors(control)))
        { ViewModel.SetValidationMessage("请先修正标出的输入值，再导出。"); return; }
        if (ViewModel.IsGiEnabled && !await EnsureGiCapabilitiesAsync()) return;
        if (closed) return;
        if (!ViewModel.TryCreate(out var draft)) return;
        ExportRequested = true;
        Close(draft);
    }

    private async void ApplyConfiguration(object? sender, RoutedEventArgs args)
    {
        if (this.GetVisualDescendants().OfType<Control>()
            .Any(control => control.IsEffectivelyEnabled && DataValidationErrors.GetHasErrors(control)))
        {
            ViewModel.SetValidationMessage("请先修正标出的输入值，再应用配置。");
            return;
        }
        if (ViewModel.IsGiEnabled && !await EnsureGiCapabilitiesAsync()) return;
        if (closed) return;
        if (ViewModel.TryCreate(out var draft)) Close(draft);
    }

    private void CancelConfiguration(object? sender, RoutedEventArgs args) => Close(null);

    private async void BrowseDestination(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button button) return;
        button.IsEnabled = false;
        try
        {
            if (ViewModel.IsSequence)
            {
                if (!StorageProvider.CanPickFolder) { ViewModel.SetValidationMessage("请在路径栏填写新序列文件夹。"); return; }
                var folders = await StorageProvider.OpenFolderPickerAsync(new()
                { Title = Text("选择序列的上级文件夹"), AllowMultiple = false });
                if (folders.FirstOrDefault()?.TryGetLocalPath() is { } parent)
                    ViewModel.Destination = Path.Combine(parent, "MDA-frames");
            }
            else
            {
                if (!StorageProvider.CanSave) { ViewModel.SetValidationMessage("请在路径栏填写输出文件。"); return; }
                string extension = ViewModel.RequiredExtension;
                var file = await StorageProvider.SaveFilePickerAsync(new()
                {
                    Title = Text("选择输出文件"), SuggestedFileName = Path.GetFileName(ViewModel.Destination),
                    DefaultExtension = extension.TrimStart('.'), ShowOverwritePrompt = true,
                    FileTypeChoices = [new FilePickerFileType(Text(ViewModel.IsVideo ? "视频" : "PNG 图片"))
                    { Patterns = ["*" + extension] }]
                });
                if (file?.TryGetLocalPath() is { } path) ViewModel.Destination = path;
            }
        }
        catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException)
        { ViewModel.SetValidationMessage("暂时无法打开文件选择器，请直接填写输出路径。"); }
        finally { button.IsEnabled = true; }
    }
}
