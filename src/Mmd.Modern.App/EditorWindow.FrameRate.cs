using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Mmd.Modern.App;

public partial class EditorWindow
{
    private readonly EditorPreviewFrameRateMonitor previewFrameRate = new();
    private readonly DispatcherTimer previewFrameRateTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly long previewFrameRateClock = Stopwatch.GetTimestamp();
    private long previewPresentationGeneration = -1;
    private long lastMeasuredPresentCount;

    private TimeSpan PreviewFrameRateNow => Stopwatch.GetElapsedTime(previewFrameRateClock);

    private void AttachPreviewFrameRate(EditorViewportHost viewport)
    {
        previewFrameRateTimer.Tick += PreviewFrameRateTick;
        PropertyChanged += PreviewFrameRateVisibilityChanged;
        viewport.PropertyChanged += PreviewFrameRateVisibilityChanged;
        RefreshPreviewFrameRate();
    }

    private void DetachPreviewFrameRate(EditorShellViewModel shell, EditorViewportHost viewport)
    {
        previewFrameRateTimer.Stop();
        previewFrameRateTimer.Tick -= PreviewFrameRateTick;
        PropertyChanged -= PreviewFrameRateVisibilityChanged;
        viewport.PropertyChanged -= PreviewFrameRateVisibilityChanged;
        previewFrameRate.SetPlaybackActive(false, PreviewFrameRateNow);
        previewPresentationGeneration = -1;
        lastMeasuredPresentCount = 0;
        shell.SetPreviewFrameRate(previewFrameRate.Snapshot);
    }

    private void PreviewFrameRateVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == IsVisibleProperty || args.Property == WindowStateProperty)
            RefreshPreviewFrameRate();
    }

    private void PreviewFrameRateTick(object? sender, EventArgs args)
    {
        RefreshPreviewFrameRate();
        _shell?.SetPreviewFrameRate(previewFrameRate.Sample(PreviewFrameRateNow));
    }

    // This observes completed Present notifications only. Sampling never requests a frame,
    // advances the source clock, changes quality, or edits an export setting.
    private void MeasurePreviewFramePresented(EditorViewportPresentationStatistics statistics)
    {
        if (_shell is null) return;
        var now = PreviewFrameRateNow;
        RefreshPreviewFrameRate(now);
        var viewport = this.FindControl<EditorViewportHost>("SceneViewport")!;
        if (previewPresentationGeneration != viewport.PresentationGeneration
            || statistics.PresentCount < lastMeasuredPresentCount)
        {
            previewFrameRate.SetMeasurementEnabled(false, now);
            previewPresentationGeneration = viewport.PresentationGeneration;
            lastMeasuredPresentCount = 0;
            RefreshPreviewFrameRate(now);
        }
        if (statistics.PresentCount <= lastMeasuredPresentCount) return;
        lastMeasuredPresentCount = statistics.PresentCount;
        previewFrameRate.RecordPresented(now);
    }

    private void RefreshPreviewFrameRate(TimeSpan? timestamp = null)
    {
        if (_shell is not { } shell) return;
        var viewport = this.FindControl<EditorViewportHost>("SceneViewport")!;
        var now = timestamp ?? PreviewFrameRateNow;
        previewFrameRate.SetPlaybackActive(shell.IsPlaying, now);
        bool enabled = IsVisible && WindowState != WindowState.Minimized && viewport.IsEffectivelyVisible
            && !shell.IsBusy && !shell.IsPreparingPreview && !shell.PosePreview.IsPreparingPreview
            && !viewport.IsPreparingFrame && !viewport.IsSuspended && !viewport.HasRendererError;
        previewFrameRate.SetMeasurementEnabled(enabled, now);
        if (shell.IsPlaying && IsVisible && WindowState != WindowState.Minimized)
        {
            if (!previewFrameRateTimer.IsEnabled) previewFrameRateTimer.Start();
        }
        else previewFrameRateTimer.Stop();
        shell.SetPreviewFrameRate(previewFrameRate.Snapshot);
    }

    private void DismissLowPreviewFrameRate(object? sender, RoutedEventArgs args)
    {
        previewFrameRate.DismissWarning();
        _shell?.SetPreviewFrameRate(previewFrameRate.Snapshot);
    }
}
