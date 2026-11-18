using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class ExportStatusIntegrationTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task ProductionWindowRetainsEveryOutcomeAfterCleanupAndRestoresLivePreview(string outcome)
        => await session.Dispatch(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "mda-export-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var engine = new TestEngine(outcome);
        var camera = new PmmCameraPose(new(0, 10, 0), Vector3.Zero, -45, 30, true).CreateCamera();
        using var shell = new EditorShellViewModel(camera, null, engine);
        var model = MorphApplicationTests.Model([]);
        string modelPath = Path.Combine(directory, "fixture.pmx");
        var id = shell.Workspace.AddModel(modelPath, EditorProjectWorkspace.CreateModelAsset(model, modelPath, Token), ModelRole.Prop);
        shell.Workspace.CommitMotion(id, new VmdMotion
        {
            ModelName = model.Name,
            BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 6, Translation = new(6, 0, 0) }]
        });
        shell.RefreshWorkspace();
        shell.Workspace.Session!.Seek(2, Token);
        var live = shell.Workspace.Session;
        var pose = live.Frame;
        var window = new EditorWindow(false) { ExportDiagnosticLogRoot = Path.Combine(directory, "diagnostics") };
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.IsVisible = false;
        window.DataContext = shell;
        window.Show();
        string output = Path.Combine(directory, "result.png");
        var quality = new ViewQualityOptions();
        var request = new ExportRequest(new OfflineFramePlan(4, 3, new(30), 3, 3), quality);
        var preset = new ViewOutputPresetDefinition("test.export-status", "Export status", new(4, 3), new(4, 3, new(30), quality));
        var draft = new OutputSettingsDraft(preset, request, OutputMediaKind.PngImage, output, false, Vector4.One, null);
        try
        {
            var export = (Task)typeof(EditorWindow).GetMethod("RunExportWithStatusAsync", Instance)!.Invoke(window, [shell, draft])!;
            var statusWindow = Assert.Single(window.OwnedWindows.OfType<ExportRenderStatusWindow>());
            await engine.Renderer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.True(shell.IsBusy);
            Assert.True(viewport.IsSuspended);
            Assert.False(shell.IsPlaying);
            Assert.False(shell.CanEditWorkspace);
            Assert.Same(live, shell.Workspace.Session);
            Assert.Same(pose, live.Frame);
            Assert.Equal(2, live.CurrentFrame);
            if (outcome == "cancel")
            {
                statusWindow.Close(); // This must request cancellation, not detach a running task.
                Assert.True(statusWindow.IsVisible);
                Assert.True(statusWindow.ViewModel.IsCancellationRequested);
            }
            else engine.Renderer.ReleaseCapture.TrySetResult();
            await engine.Renderer.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.True(statusWindow.ViewModel.IsRunning);
            Assert.False(statusWindow.ViewModel.CanClose);
            Assert.False(export.IsCompleted);
            bool heartbeat = false;
            await Dispatcher.UIThread.InvokeAsync(() => heartbeat = true, DispatcherPriority.Background);
            Assert.True(heartbeat);
            engine.Renderer.ReleaseDispose.TrySetResult();
            await export.WaitAsync(TimeSpan.FromSeconds(15), Token);
            Assert.True(statusWindow.IsVisible);
            Assert.True(statusWindow.ViewModel.CanClose);
            Assert.False(statusWindow.ViewModel.IsRunning);
            Assert.Equal(outcome switch
            {
                "success" => ExportTaskState.Completed,
                "cancel" => ExportTaskState.Cancelled,
                _ => ExportTaskState.Failed
            }, statusWindow.ViewModel.State);
            Assert.Equal(outcome == "success", File.Exists(output));
            Assert.Equal(outcome == "failure", statusWindow.ViewModel.HasError);
            Assert.True(statusWindow.ViewModel.HasDiagnostics);
            string summary = statusWindow.ViewModel.DiagnosticLogPath;
            Assert.True(File.Exists(summary));
            Assert.Equal(File.ReadAllText(summary), statusWindow.ViewModel.CopyableDetails);
            using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(summary)!, "final.json")));
            Assert.Equal(statusWindow.ViewModel.State.ToString(), report.RootElement.GetProperty("state").GetString());
            var metadata = report.RootElement.GetProperty("metadata");
            Assert.Equal(engine.Id, metadata.GetProperty("rendererBackendId").GetString());
            Assert.Equal(2, metadata.GetProperty("projectSourceFrameAtStart").GetDouble());
            Assert.Equal(JsonValueKind.Null, metadata.GetProperty("device").ValueKind);
            Assert.Equal(120, metadata.GetProperty("plan").GetProperty("simulationTicksPerSecond").GetInt32());
            Assert.NotEmpty(File.ReadAllLines(Path.Combine(Path.GetDirectoryName(summary)!, "events.jsonl")));
            if (outcome == "failure")
            {
                Assert.Contains("Test capture failure", statusWindow.ViewModel.CopyableDetails);
                var failed = report.RootElement.GetProperty("failureContext");
                Assert.Equal(0, failed.GetProperty("outputIndex").GetInt64());
                Assert.Equal("3", failed.GetProperty("sampledSourceFrame").GetProperty("numerator").GetString());
                Assert.Equal("renderer.capture", report.RootElement.GetProperty("phase").GetString());
            }
            Assert.Equal(1, engine.Renderer.Disposals);
            Assert.False(shell.IsBusy);
            Assert.False(viewport.IsSuspended);
            Assert.Same(live, shell.Workspace.Session);
            Assert.Same(pose, live.Frame);
            Assert.Equal(2, live.CurrentFrame);
            Assert.DoesNotContain(window.OwnedWindows, child => child != statusWindow && child.IsVisible);
            statusWindow.Close();
            Assert.False(statusWindow.IsVisible);
        }
        finally
        {
            engine.Renderer.ReleaseCapture.TrySetResult();
            engine.Renderer.ReleaseDispose.TrySetResult();
            foreach (var child in window.OwnedWindows.ToArray())
            {
                if (child is ExportRenderStatusWindow status && status.ViewModel.IsRunning) status.ViewModel.MarkCancelled();
                child.Close();
            }
            window.DataContext = null;
            window.Close();
            Dispatcher.UIThread.RunJobs();
            string target = Path.GetFullPath(directory);
            string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(target).StartsWith("mda-export-status-", StringComparison.Ordinal)
                || (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Unexpected export status fixture cleanup path.");
            Directory.Delete(target, recursive: true);
        }
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(40), Token);

    private sealed class TestEngine(string outcome) : IEditorRenderEngine, IEditorRenderBinding
    {
        public string Id => "test.export-status";
        public IEditorProjectPersistence Persistence => new EditorModernProjectPersistence(this);
        public IEditorRenderEngine Engine => this;
        public IEditorRenderBinding EmptyBinding => this;
        internal TestRenderer Renderer { get; } = new(outcome);
        public IEditorRenderBinding Bind(EditorProjectWorkspace workspace) => this;
        public ExportRequest PrepareExport(ExportRequest request) => request;
        public IViewRenderer CreateCapture(ViewRenderRequest request, Vector4 background) => Renderer;
        public IEditorViewportRenderer CreatePreview(nint window, ViewRenderRequest request)
            => throw new InvalidOperationException("The hidden fixture must not create preview GPU resources.");
    }

    private sealed class TestRenderer(string outcome) : IViewRenderer
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseCapture { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Disposals;
        public ViewRendererCapabilities Capabilities => new("test.export-status", 4096, 4096, true, false);
        public async ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await ReleaseCapture.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (outcome == "failure") throw new IOException("Test capture failure");
            byte[] pixels = new byte[checked(frame.Width * frame.Height * 4)];
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return new(frame.Width, frame.Height, frame.Width * 4, pixels);
        }
        public async ValueTask DisposeAsync()
        {
            Disposals++;
            DisposeEntered.TrySetResult();
            await ReleaseDispose.Task.ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
