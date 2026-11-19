using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Mmd.Modern.ViewOutput;

namespace Mmd.Modern.App;

/// <summary>Returns a detached ViewQualityOptions on Apply; Cancel and window close return null.</summary>
public partial class EditorPreviewSettingsWindow : Window
{
    private readonly Func<bool, CancellationToken, Task<EditorPreviewCapabilities>>? queryCapabilities;
    private CancellationTokenSource? probeCts;
    private Task<bool>? activeProbe;
    private int probeGeneration;
    private bool closed;

    public EditorPreviewSettingsWindow() : this(new EditorPreviewSettingsViewModel()) { }

    public EditorPreviewSettingsWindow(ViewQualityOptions current, EditorPreviewCapabilities? capabilities = null,
        EditorPreviewRuntimeInfo? runtime = null)
        : this(new EditorPreviewSettingsViewModel(current, capabilities, runtime)) { }

    public EditorPreviewSettingsWindow(EditorPreviewSettingsViewModel viewModel,
        Func<bool, CancellationToken, Task<EditorPreviewCapabilities>>? queryCapabilities = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        this.queryCapabilities = queryCapabilities;
        InitializeComponent(); DataContext = viewModel;
        if (this.queryCapabilities is not null)
            viewModel.CanRefreshCapabilities = true;
        Opened += (_, _) =>
        {
            RequestedThemeVariant = Owner?.ActualThemeVariant ?? ThemeVariant.Dark;
            if (Owner is { } owner)
            {
                FontFamily = owner.FontFamily; FontSize = owner.FontSize; Resources["OutputFont"] = owner.FontFamily;
            }
            if (this.queryCapabilities is not null && !ViewModel.CapabilitiesReady)
                _ = EnsureCapabilitiesAsync(forceRefresh: false);
        };
        Closed += (_, _) =>
        {
            closed = true;
            probeGeneration++;
            probeCts?.Cancel();
        };
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            Close(null); args.Handled = true;
        };
    }

    public EditorPreviewSettingsViewModel ViewModel => (EditorPreviewSettingsViewModel)DataContext!;

    private Task<bool> EnsureCapabilitiesAsync(bool forceRefresh)
    {
        if (queryCapabilities is null) return Task.FromResult(true);
        if (!forceRefresh)
        {
            if (ViewModel.CapabilitiesReady) return Task.FromResult(true);
            if (activeProbe is { IsCompleted: false } running) return running;
        }
        return activeProbe = LoadCapabilitiesAsync(forceRefresh);
    }

    private async Task<bool> LoadCapabilitiesAsync(bool forceRefresh)
    {
        if (closed || queryCapabilities is null) return false;
        var priorCts = probeCts;
        var cts = new CancellationTokenSource();
        probeCts = cts;
        priorCts?.Cancel();
        int generation = ++probeGeneration;
        if (forceRefresh || ViewModel.CapabilitiesReady)
            ViewModel.BeginCapabilitiesProbe();

        try
        {
            var capabilities = await queryCapabilities(forceRefresh, cts.Token);
            if (closed || cts.IsCancellationRequested || generation != probeGeneration) return false;
            ViewModel.ApplyCapabilities(capabilities);
            return true;
        }
        catch (OperationCanceledException) when (closed || cts.IsCancellationRequested || generation != probeGeneration)
        {
            return false;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (closed || generation != probeGeneration) return false;
            ViewModel.ApplyCapabilities(new() { DeviceUnavailableReason = "D3D12 设备检查未完成，可重试或检查显卡驱动。" });
            return true;
        }
        finally
        {
            cts.Dispose();
            if (ReferenceEquals(probeCts, cts))
                probeCts = null;
        }
    }

    private async void RefreshCapabilities(object? sender, RoutedEventArgs args)
    {
        if (closed || queryCapabilities is null) return;
        await EnsureCapabilitiesAsync(forceRefresh: true);
    }

    private async void ApplySettings(object? sender, RoutedEventArgs args)
    {
        if (this.GetVisualDescendants().OfType<Control>().Any(control =>
            control.IsEffectivelyEnabled && DataValidationErrors.GetHasErrors(control)))
        { ViewModel.SetValidationMessage("请先修正标出的输入值，再应用设置。"); return; }
        if (ViewModel.NeedsCapabilitiesForApply && !await EnsureCapabilitiesAsync(forceRefresh: false)) return;
        if (closed) return;
        if (ViewModel.TryCreate(out var result)) Close(result);
    }

    private void CancelSettings(object? sender, RoutedEventArgs args) => Close(null);
}
