using Avalonia.Controls;

namespace Mmd.Modern.App;

/// <summary>Separate visible preparation from exclusive edits: background poses never disable dragging.</summary>
public partial class EditorWindow
{
    private bool _waitingForOperationFrame;

    private void AttachLoadingState(EditorShellViewModel shell)
    {
        var viewport = this.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.PreparationChanged += LoadingPreparationChanged;
        viewport.FramePresented += LoadingFramePresented;
        viewport.Error += LoadingFrameFailed;
        viewport.LightingFrameRetrying += shell.BeginPreviewAttempt;
        shell.PosePreview.PreparationChanged += LoadingPreparationChanged;
        AttachPreviewFrameRate(viewport);
        RefreshLoadingState();
    }

    private void DetachLoadingState(EditorShellViewModel shell)
    {
        var viewport = this.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.PreparationChanged -= LoadingPreparationChanged;
        viewport.FramePresented -= LoadingFramePresented;
        viewport.Error -= LoadingFrameFailed;
        viewport.LightingFrameRetrying -= shell.BeginPreviewAttempt;
        shell.PosePreview.PreparationChanged -= LoadingPreparationChanged;
        DetachPreviewFrameRate(shell, viewport);
        _waitingForOperationFrame = false;
        shell.SetPreviewLoading(false);
        shell.SetPreviewStatistics(null);
    }

    private void LoadingPreparationChanged(object? sender, EventArgs args)
    {
        var viewport = this.FindControl<EditorViewportHost>("SceneViewport")!;
        if (_shell is { } active && !viewport.HasRendererError
            && ReferenceEquals(sender, active.PosePreview) && active.PosePreview.IsPreparingPreview)
            active.BeginPreviewAttempt();
        // CPU completion only publishes a snapshot. Keep feedback until that snapshot's
        // requested frame reaches the native viewport, even with an existing renderer.
        if (_shell is { } shell && ReferenceEquals(sender, shell.PosePreview) && !shell.PosePreview.IsPreparingPreview)
            viewport.PrepareNextFrame();
        RefreshLoadingState();
    }
    private void LoadingFramePresented(EditorViewportPresentationStatistics statistics)
    {
        _shell?.SetPreviewStatistics(statistics);
        var viewport = this.FindControl<EditorViewportHost>("SceneViewport")!;
        if (!viewport.IsPreparingFrame && _shell?.IsBusy != true && !viewport.IsSuspended)
            _waitingForOperationFrame = false;
        RefreshLoadingState();
        MeasurePreviewFramePresented(statistics);
    }
    private void LoadingFrameFailed(Exception error)
    { _waitingForOperationFrame = false; RefreshLoadingState(); }

    private void RefreshLoadingState()
    {
        if (_shell is not { } shell) return;
        var viewport = this.FindControl<EditorViewportHost>("SceneViewport")!;
        if (viewport.HasRendererError) _waitingForOperationFrame = false;
        shell.SetPreviewLoading(_waitingForOperationFrame || viewport.IsPreparingFrame || shell.PosePreview.IsPreparingPreview);
        RefreshPreviewFrameRate();
    }
}
