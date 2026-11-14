using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Mmd.Modern.App;

/// <summary>Retains results until manually closed; closing an active task requests cancellation.</summary>
public sealed partial class ExportRenderStatusWindow : Window
{
    private readonly DispatcherTimer elapsedTimer;
    private bool closed;

    public ExportRenderStatusWindow() : this(new ExportRenderStatusViewModel("")) { }

    public ExportRenderStatusWindow(ExportRenderStatusViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        elapsedTimer.Tick += (_, _) => ViewModel.RefreshElapsed();
        viewModel.PropertyChanged += StatusChanged;
        Opened += (_, _) =>
        {
            RequestedThemeVariant = Owner?.ActualThemeVariant ?? RequestedThemeVariant ?? ThemeVariant.Dark;
            if (viewModel.IsRunning) elapsedTimer.Start();
        };
        Closing += (_, args) =>
        {
            if (!viewModel.IsRunning) return;
            args.Cancel = true;
            viewModel.RequestCancellation();
        };
        Closed += (_, _) =>
        {
            closed = true;
            elapsedTimer.Stop();
            viewModel.PropertyChanged -= StatusChanged;
        };
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            if (viewModel.IsRunning) viewModel.RequestCancellation(); else Close();
        };
    }

    public ExportRenderStatusViewModel ViewModel => (ExportRenderStatusViewModel)DataContext!;

    private void StatusChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (closed || ViewModel.IsRunning) return;
        elapsedTimer.Stop();
    }

    private void CancelExport(object? sender, RoutedEventArgs args) => ViewModel.RequestCancellation();
    private void CloseStatus(object? sender, RoutedEventArgs args)
    {
        if (ViewModel.CanClose) Close();
    }

    private async void CopyError(object? sender, RoutedEventArgs args)
    {
        if (ViewModel.CopyableDetails.Length == 0 || Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(ViewModel.CopyableDetails); }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            // The retained, selectable error remains available when the OS clipboard is busy.
        }
    }
}
