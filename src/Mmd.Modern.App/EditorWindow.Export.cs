using Avalonia.Controls;
using Avalonia.Threading;
using Mmd.Modern.ViewOutput;

namespace Mmd.Modern.App;

public partial class EditorWindow
{
    internal string? ExportDiagnosticLogRoot { get; set; }
    internal IEditorPowerPlatform ExportPowerPlatform { get; set; } = EditorWindowsPowerPlatform.Instance;

    private async Task RunExportWithStatusAsync(EditorShellViewModel shell, OutputSettingsDraft draft)
    {
        shell.SynchronizePendingScrub();
        using var diagnostics = ExportDiagnosticSession.Start(shell.Workspace, draft, ExportDiagnosticLogRoot);
        var status = new ExportRenderStatusViewModel(draft.Destination, draft.Request.Plan.FrameCount);
        status.SetDiagnostics(diagnostics);
        var window = new ExportRenderStatusWindow(status)
        {
            RequestedThemeVariant = ActualThemeVariant
        };
        var gpu = new ExportGpuResourceProvider();
        await using var monitor = new RenderResourceMonitor(gpu.Sample);
        CancellationTokenSource? activeCancellation = null;
        Exception? failure = null;
        Exception? cancellationError = null;
        bool cancelled = false;
        void RequestCancellation(object? sender, EventArgs args)
        {
            diagnostics.ReportPhase("cancellation.requested");
            if (shell.RequestOperationCancellation()) activeCancellation?.Cancel();
        }
        async Task StopMonitorAsync()
        {
            try { await monitor.StopAsync(); }
            catch (Exception error) { diagnostics.RecordSecondaryError(error); }
        }
        status.CancellationRequested += RequestCancellation;
        monitor.SnapshotAvailable += snapshot => Dispatcher.UIThread.Post(() => status.ApplyResourceSnapshot(snapshot));
        var rendering = new Progress<ViewRendererDeviceInfo>(identity =>
        {
            gpu.SetDevice(identity);
            status.ReportStage(ExportRenderStage.Rendering);
        });
        var progress = new Progress<ExportTaskProgress>(value =>
        {
            status.ReportProgress(value);
            if (value.State == ExportTaskState.Running)
            {
                shell.OperationStatus = EditorLocalization.Format("正在导出：{0} / {1} 帧", value.CompletedFrames, value.TotalFrames);
                if (value.TotalFrames > 0 && value.CompletedFrames >= value.TotalFrames)
                    status.ReportStage(ExportRenderStage.Finalizing);
            }
        });
        window.Show(this);
        try
        {
            await ExecuteCoreAsync(EditorLocalization.Text("正在导出…"), async token =>
            {
                activeCancellation = _operationCancellation;
                if (status.IsCancellationRequested) activeCancellation?.Cancel();
                // Entered and disposed on the UI thread, which owns the export's await continuations.
                using var power = EditorExportPowerScope.Enter(ExportPowerPlatform);
                monitor.Start();
                try
                {
                    token.ThrowIfCancellationRequested();
                    await shell.ReleaseAudioPlaybackAsync(token);
                    diagnostics.ReportPhase("preview.release");
                    status.ReportStage(ExportRenderStage.ReleasingPreview);
                    await this.FindControl<EditorViewportHost>("SceneViewport")!.SuspendAndReleaseResourcesAsync(token);
                    status.ReportStage(ExportRenderStage.PreparingRenderer);
                    await EditorExportService.ExportWithResourcesAsync(shell.Workspace, draft, _preferences.FfmpegPath,
                        _preferences.FfprobePath, progress, rendering, token, diagnostics);
                    shell.OperationStatus = EditorLocalization.Text("导出完成：") + draft.Destination;
                }
                catch (OperationCanceledException error) { cancelled = true; cancellationError = error; throw; }
                catch (Exception error) { failure = error; throw; }
                finally
                {
                    activeCancellation = null;
                    await StopMonitorAsync();
                    if (monitor.LatestSnapshot is { } finalResources) status.ApplyResourceSnapshot(finalResources);
                    status.ReportStage(ExportRenderStage.RestoringPreview);
                    diagnostics.ReportPhase("preview.restore");
                }
            }, null, error => failure = error);
        }
        catch (Exception error)
        {
            if (failure is null) failure = error;
            else if (!ReferenceEquals(failure, error)) diagnostics.RecordSecondaryError(error);
            shell.ReportActivityFailure(failure);
        }
        finally
        {
            activeCancellation = null;
            await StopMonitorAsync();
            if (monitor.LatestSnapshot is { } finalResources)
            {
                status.ApplyResourceSnapshot(finalResources);
                diagnostics.ReportResources(finalResources);
            }
            status.CancellationRequested -= RequestCancellation;
            diagnostics.Finish(failure is not null ? ExportTaskState.Failed : cancelled ? ExportTaskState.Cancelled : ExportTaskState.Completed,
                failure ?? cancellationError);
            status.SetDiagnostics(diagnostics);
            if (failure is not null) status.MarkFailed(failure);
            else if (cancelled) status.MarkCancelled();
            else status.MarkCompleted();
        }
    }
}
