using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.FFmpeg;
using Mmd.Modern.ViewOutput.Images;

namespace Mmd.Modern.App;

/// <summary>Exports a detached project snapshot through the existing offline pipeline. Call on the
/// workspace's UI owner while editing is blocked; the live workspace is never advanced or disposed.</summary>
public static partial class EditorExportService
{
    public static Task<ExportResult> ExportAsync(EditorProjectWorkspace workspace, OutputSettingsDraft draft,
        string? ffmpegPath, string? ffprobePath, IProgress<ExportTaskProgress>? progress = null,
        CancellationToken token = default)
        => ExportCoreAsync(workspace, draft, ffmpegPath, ffprobePath, progress, null, token, null);

    internal static Task<ExportResult> ExportWithResourcesAsync(EditorProjectWorkspace workspace, OutputSettingsDraft draft,
        string? ffmpegPath, string? ffprobePath, IProgress<ExportTaskProgress>? progress,
        IProgress<ViewRendererDeviceInfo>? devices, CancellationToken token, ExportDiagnosticSession? diagnostics = null)
        => ExportCoreAsync(workspace, draft, ffmpegPath, ffprobePath, progress, devices, token, diagnostics);

    private static async Task<ExportResult> ExportCoreAsync(EditorProjectWorkspace workspace, OutputSettingsDraft draft,
        string? ffmpegPath, string? ffprobePath, IProgress<ExportTaskProgress>? progress,
        IProgress<ViewRendererDeviceInfo>? devices, CancellationToken token, ExportDiagnosticSession? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(draft);
        bool ownsDiagnostics = diagnostics is null;
        diagnostics ??= ExportDiagnosticSession.Start(workspace, draft);
        EditorRenderCleanupLedger.ExportLease? cleanupLease = null;
        Exception? failure = null;
        try
        {
            diagnostics.ReportPhase("request.validate");
            draft.Validate(); token.ThrowIfCancellationRequested();
            // Await on the caller's UI context: project binding below still belongs to that owner.
            // A retained native owner must be destroyed before any new detached session is created.
            cleanupLease = await workspace.RenderCleanup.EnterExportAsync(token);
            var source = workspace.Session ?? throw new InvalidOperationException(EditorLocalization.Text("请先载入模型。"));
            if (draft.Request.Plan.EndFrame > source.DurationFrames)
                throw new ArgumentOutOfRangeException(nameof(draft), EditorLocalization.Text("输出区间超出工程的最后关键帧。"));
            // Resolve tools before preparing the solver graph. Never install tools or search PATH.
            FfmpegToolchain? tools = draft.MediaKind == OutputMediaKind.Video
                ? new(RequireToolPath(ffmpegPath, "FFmpeg"), RequireToolPath(ffprobePath, "FFprobe")) : null;
            var binding = workspace.CaptureRenderBinding();
            var effectiveRequest = binding.PrepareExport(draft.Request);
            diagnostics.ReportEffectiveRequest(effectiveRequest);
            diagnostics.ReportPhase("snapshot.freeze");
            var snapshot = source.CapturePosePreviewRequestWithCancellation(new Dictionary<Guid, Mmd.Modern.Formats.VmdMotion>(), token);
            // Freeze the immutable selected track on its UI owner. PCM parsing and the private
            // range crop stay on the export worker; PNG outputs never consume audio.
            var audioTrack = draft.MediaKind == OutputMediaKind.Video
                ? EditorSceneAudioInputs.CaptureEnabled(source.Media) : null;
            // The owner suspends editing/preview capture while an independent solver graph is prepared.
            // Immutable model/texture assets can be shared; authored motions, physics and playback clocks cannot.
            // This also permits rendering a valid PMM motion whose names do not fit VMD serialization fields.
            var result = await Task.Run(async () =>
            {
                var videoOptions = draft.VideoOptions is null ? null
                    : draft.VideoOptions with { AudioInput = audioTrack is null ? null : new SceneAudioExportInput(audioTrack) };
                diagnostics.CaptureFileIdentities();
                diagnostics.ReportPhase("session.create");
                var coordinator = ExportCoordinator.ForProject(cancellation =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    diagnostics.ReportPhase("session.create");
                    return ValueTask.FromResult(snapshot.CreateSession(EditorProjectWorkspace.CreatePhysicsProvider(), cancellation));
                }, async (request, cancellation) =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    diagnostics.ReportPhase("renderer.create");
                    IViewRenderer renderer;
                    try { renderer = workspace.RenderCleanup.Track(binding.CreateCapture(request, draft.BackgroundColor)); }
                    catch (Exception error)
                    {
                        if (error is IEditorRenderInitializationCleanup cleanup) workspace.RenderCleanup.Retain(cleanup);
                        throw;
                    }
                    try
                    {
                        diagnostics.ReportRenderer(renderer.Capabilities.BackendId);
                        if (renderer is IViewRendererDeviceInfoProvider provider)
                        {
                            var device = provider.DeviceInfo;
                            diagnostics.ReportDevice(device);
                            devices?.Report(device);
                        }
                        return renderer;
                    }
                    catch (Exception error)
                    {
                        try { await renderer.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception cleanup) { try { error.Data["ViewOutputRendererNotificationCleanupError"] = cleanup; } catch { } }
                        throw;
                    }
                });
                IFrameSink sink = draft.MediaKind switch
                {
                    OutputMediaKind.PngImage => new PngFrameSink(draft.Destination),
                    OutputMediaKind.PngSequence => new PngSequenceSink(draft.Destination),
                    OutputMediaKind.Video => new FfmpegFrameSink(tools!, videoOptions!),
                    _ => throw new ArgumentOutOfRangeException(nameof(draft))
                };
                var recordedProgress = new ExportDiagnosticProgress(diagnostics, progress);
                diagnostics.ReportPhase("export.run");
                try
                {
                    return await coordinator.RunAsync(effectiveRequest, new ExportDiagnosticSink(sink, diagnostics), recordedProgress, token).ConfigureAwait(false);
                }
                finally { diagnostics.CaptureFinalNativeModules(); }
            }, token).ConfigureAwait(false);
            return result;
        }
        catch (Exception error)
        {
            failure = error;
            diagnostics.RecordFailure(error);
            throw;
        }
        finally
        {
            try
            {
                if (cleanupLease is not null)
                    await cleanupLease.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanup)
            {
                // Preserve the export/creation failure. The workspace keeps the actual owner,
                // independently of this diagnostic reference, for a later bounded retry.
                if (failure is not null)
                { try { failure.Data["EditorRenderCleanupRetryError"] = cleanup; } catch { } }
                else
                {
                    failure = cleanup;
                    if (diagnostics.OutputPublished)
                    {
                        try { cleanup.Data[ExportCoordinator.OutputPublishedDataKey] = true; } catch { }
                    }
                    diagnostics.RecordFailure(cleanup);
                    throw;
                }
            }
            finally
            {
                if (ownsDiagnostics)
                {
                    var finalState = failure is null ? ExportTaskState.Completed
                        : !diagnostics.OutputPublished && failure is OperationCanceledException && token.IsCancellationRequested
                            ? ExportTaskState.Cancelled : ExportTaskState.Failed;
                    diagnostics.Finish(finalState, failure);
                    diagnostics.Dispose();
                }
            }
        }
    }

    // Observe coordinator progress synchronously before forwarding to a possibly queued UI Progress<T>.
    private sealed class ExportDiagnosticProgress(ExportDiagnosticSession diagnostics, IProgress<ExportTaskProgress>? target)
        : IProgress<ExportTaskProgress>
    {
        public void Report(ExportTaskProgress value)
        { diagnostics.ReportProgress(value); target?.Report(value); }
    }

    private sealed class ExportDiagnosticSink(IFrameSink inner, ExportDiagnosticSession diagnostics) : IFrameSink
    {
        public ValueTask BeginAsync(ExportRequest request, CancellationToken token = default)
        { diagnostics.ReportPhase("sink.begin"); return inner.BeginAsync(request, token); }
        public ValueTask WriteAsync(Mmd.Modern.Application.OfflineFrameSample sample, FrameBufferLease frame, CancellationToken token = default)
            => inner.WriteAsync(sample, frame, token);
        public async ValueTask CompleteAsync(CancellationToken token = default)
        {
            diagnostics.ReportPhase("sink.complete");
            await inner.CompleteAsync(token).ConfigureAwait(false);
            diagnostics.RecordPublication();
        }
        public ValueTask AbortAsync(Exception? error, CancellationToken token = default)
        { diagnostics.ReportPhase("sink.abort"); return inner.AbortAsync(error, token); }
        public ValueTask DisposeAsync()
        { diagnostics.ReportPhase("sink.dispose"); return inner.DisposeAsync(); }
    }

    private static string RequireToolPath(string? path, string name)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            string bundled = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", name.ToLowerInvariant() + ".exe");
            if (File.Exists(bundled)) path = bundled;
        }
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException(EditorLocalization.Format("请在设置中选择 {0} 的完整可执行文件路径。", name));
        return path;
    }

}
