using System.Numerics;
using Avalonia.Headless;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorRenderCleanupTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ViewportRetainsFailedInitializationCleanupUntilTheSameOwnerCanBeReleased()
        => await session.Dispatch(async () =>
        {
            var failure = new CleanupException(2);
            var engine = new TestEngine { PreviewFailure = failure };
            using var viewport = new EditorViewportHost(() => (4, 3), engine);
            var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            viewport.Error += error => reported.TrySetResult(error);
            viewport.RequestRender();
            Assert.Same(failure, await reported.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.Equal(1, failure.Disposals); // The failing creation is retried by FailRendererAsync.
            Assert.Equal(1, engine.PreviewCreates);
            viewport.IsSuspended = true;
            var retry = await Assert.ThrowsAsync<IOException>(() => viewport.SuspendAndReleaseResourcesAsync(Token));
            Assert.Same(failure.CleanupError, retry);
            Assert.Equal(2, failure.Disposals);
            Assert.Equal(1, engine.PreviewCreates);
            await viewport.SuspendAndReleaseResourcesAsync(Token);
            Assert.Equal(3, failure.Disposals);
            viewport.Dispose();
            Assert.Equal(3, failure.Disposals);
            return true;
        }, Token);

    [Fact]
    public async Task ViewportDisposeFailureKeepsTheRendererAndAllowsDisposeToRetry()
        => await session.Dispatch(async () =>
        {
            var engine = new TestEngine();
            engine.Preview.DisposeFailures = 1;
            using var viewport = new EditorViewportHost(() => (4, 3), engine);
            var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            viewport.FramePresented += _ => presented.TrySetResult();
            viewport.RequestRender();
            await presented.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            viewport.IsSuspended = true;
            var error = Assert.Throws<IOException>(viewport.Dispose);
            Assert.Equal("viewport destroy failed", error.Message);
            Assert.Equal(1, engine.Preview.Disposals);
            viewport.RestartRenderer();
            viewport.RequestRender(); // Failed final disposal must not admit another renderer.
            Assert.Equal(1, engine.PreviewCreates);
            viewport.Dispose();
            viewport.Dispose();
            Assert.Equal(2, engine.Preview.Disposals);
            return true;
        }, Token);

    [Fact]
    public async Task ExportRetainsCreationCleanupBlocksTheNextSessionAndWorkspaceDisposeRetriesIt()
        => await session.Dispatch(async () =>
        {
            using var files = new Fixture();
            var failure = new CleanupException(3);
            var engine = new TestEngine { CaptureFailure = failure };
            var workspace = files.Workspace(engine);
            try
            {
                var first = await Assert.ThrowsAsync<CleanupException>(() =>
                    EditorExportService.ExportAsync(workspace, files.Draft("first"), null, null, token: Token));
                Assert.Same(failure, first);
                Assert.Same(failure.CleanupError, first.Data["EditorRenderCleanupRetryError"]);
                Assert.Equal(1, failure.Disposals);
                Assert.Equal(1, engine.CaptureCreates);
                var live = workspace.Session;

                var blocked = await Assert.ThrowsAsync<IOException>(() =>
                    EditorExportService.ExportAsync(workspace, files.Draft("blocked"), null, null, token: Token));
                Assert.Same(failure.CleanupError, blocked);
                Assert.Equal(2, failure.Disposals);
                Assert.Equal(1, engine.CaptureCreates);
                Assert.Equal(1, engine.Bindings); // Admission fails before binding or detached session creation.
                Assert.Same(live, workspace.Session);

                Assert.Throws<IOException>(workspace.Dispose);
                Assert.Same(live, workspace.Session); // Workspace disposal remains retryable too.
                Assert.Equal(3, failure.Disposals);
                workspace.Dispose();
                Assert.Equal(4, failure.Disposals);
                Assert.Null(workspace.Session);
            }
            finally { workspace.Dispose(); }
            return true;
        }, Token);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportRetriesFailedRendererCleanupWithoutReplacingTheOriginalFailure(bool captureFails)
        => await session.Dispatch(async () =>
        {
            using var files = new Fixture();
            var old = new CaptureRenderer { DisposeFailures = 2, CaptureFails = captureFails };
            var fresh = new CaptureRenderer();
            var engine = new TestEngine { Capture = old };
            using var workspace = files.Workspace(engine);
            var failure = await Assert.ThrowsAsync<IOException>(() =>
                EditorExportService.ExportAsync(workspace, files.Draft("first"), null, null, token: Token));
            Assert.Equal(captureFails ? "capture failed" : "capture destroy failed", failure.Message);
            Assert.IsType<IOException>(failure.Data["EditorRenderCleanupRetryError"]);
            Assert.Equal(2, old.Disposals); // Coordinator cleanup, then ExportCore's bounded finally retry.
            Assert.Equal(1, engine.CaptureCreates);
            engine.Capture = fresh;
            var result = await EditorExportService.ExportAsync(workspace, files.Draft("next"), null, null, token: Token);
            Assert.Equal(1, result.CompletedFrames);
            Assert.Equal(3, old.Disposals); // The same retained owner is released before the next session.
            Assert.Equal(2, engine.CaptureCreates);
            Assert.Equal(1, fresh.Disposals);
            return true;
        }, Token);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicExportStillForwardsSelectedInputsPreflightAndOnlyAnActualDeviceProvider(bool deviceProvider)
        => await session.Dispatch(async () =>
        {
            using var files = new Fixture();
            var renderer = deviceProvider ? new DeviceCaptureRenderer() : new CaptureRenderer();
            var engine = new TestEngine { Capture = renderer };
            using var workspace = files.Workspace(engine);
            renderer.ObjectId = workspace.Session!.Objects.Single().Id;
            var devices = new Devices();
            await EditorExportService.ExportWithResourcesAsync(workspace, files.Draft("forwarded"), null, null,
                null, devices, Token);
            Assert.Equal(1, renderer.StylePreflights);
            Assert.Equal(1, renderer.InputPreflights);
            Assert.Equal(1, renderer.Captures);
            Assert.Equal(1, renderer.Disposals);
            Assert.Equal(deviceProvider ? 1 : 0, devices.Values.Count);
            if (deviceProvider) Assert.Same(DeviceCaptureRenderer.Identity, Assert.Single(devices.Values));
            return true;
        }, Token);

    [Fact]
    public async Task ActiveExportRejectsAnotherExportAndSynchronousWorkspaceDisposeWithoutBlockingTheUi()
        => await session.Dispatch(async () =>
        {
            using var files = new Fixture();
            var renderer = new CaptureRenderer { BlockCapture = true };
            var engine = new TestEngine { Capture = renderer };
            var workspace = files.Workspace(engine);
            Task<ExportResult>? exporting = null;
            try
            {
                exporting = EditorExportService.ExportAsync(workspace, files.Draft("active"), null, null, token: Token);
                await renderer.CaptureEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
                var live = workspace.Session;
                Assert.Throws<InvalidOperationException>(workspace.Dispose);
                Assert.Same(live, workspace.Session);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    EditorExportService.ExportAsync(workspace, files.Draft("concurrent"), null, null, token: Token))
                    .WaitAsync(TimeSpan.FromSeconds(10), Token);
                Assert.Equal(1, engine.Bindings);
                Assert.Equal(1, engine.CaptureCreates);
                renderer.CaptureRelease.TrySetResult();
                await exporting.WaitAsync(TimeSpan.FromSeconds(10), Token);
                workspace.Dispose();
                Assert.Null(workspace.Session);
            }
            finally
            {
                renderer.CaptureRelease.TrySetResult();
                if (exporting is not null) await exporting;
                workspace.Dispose();
            }
            return true;
        }, Token);

    [Fact]
    public async Task DisposableCreationExceptionsWithoutTheOwnershipMarkerAreOnlyReported()
        => await session.Dispatch(async () =>
        {
            using var files = new Fixture();
            var failure = new OrdinaryDisposableException();
            var engine = new TestEngine { CaptureFailure = failure, PreviewFailure = failure };
            using var workspace = files.Workspace(engine);
            using var viewport = new EditorViewportHost(() => (4, 3), engine);
            var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            viewport.Error += error => reported.TrySetResult(error);
            viewport.RequestRender();
            Assert.Same(failure, await reported.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.Same(failure, await Assert.ThrowsAsync<OrdinaryDisposableException>(() =>
                EditorExportService.ExportAsync(workspace, files.Draft("ordinary"), null, null, token: Token)));
            viewport.Dispose();
            workspace.Dispose();
            Assert.Equal(0, failure.Disposals);
            return true;
        }, Token);

    private sealed class TestEngine : IEditorRenderEngine, IEditorRenderBinding
    {
        public IEditorProjectPersistence Persistence => new EditorModernProjectPersistence(this);
        public string Id => "test.cleanup";
        public IEditorRenderEngine Engine => this;
        public IEditorRenderBinding EmptyBinding => this;
        internal int Bindings, PreviewCreates, CaptureCreates;
        internal Exception? PreviewFailure, CaptureFailure;
        internal ViewportRenderer Preview { get; } = new();
        internal CaptureRenderer Capture { get; set; } = new();
        public IEditorRenderBinding Bind(EditorProjectWorkspace workspace) { Bindings++; return this; }
        public ExportRequest PrepareExport(ExportRequest request) => request;
        public IEditorViewportRenderer CreatePreview(nint window, ViewRenderRequest request)
        { PreviewCreates++; if (PreviewFailure is not null) throw PreviewFailure; return Preview; }
        public IViewRenderer CreateCapture(ViewRenderRequest request, Vector4 background)
        { CaptureCreates++; if (CaptureFailure is not null) throw CaptureFailure; return Capture; }
    }

    private sealed class ViewportRenderer : IEditorViewportRenderer
    {
        internal int Disposals, DisposeFailures, Presents;
        public string AdapterName => "CPU fixture";
        public bool IsWarp => false;
        public EditorViewportPresentationStatistics Statistics => new(Presents, 0, 0, 0, 4, 3, 1, null);
        public IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests => [];
        public ViewRendererCapabilities Capabilities => new("test.cleanup", 4096, 4096, false, true);
        public ValueTask PresentAsync(PreviewFrame frame, CancellationToken cancellationToken = default)
        { Presents++; return ValueTask.CompletedTask; }
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask UpdateQualityAsync(ViewQualityOptions quality, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync()
        { Disposals++; if (DisposeFailures-- > 0) throw new IOException("viewport destroy failed"); return ValueTask.CompletedTask; }
    }

    private class CaptureRenderer : IViewRenderer, IViewSceneInputRequests, IViewSceneInputPreflight, IViewRenderingStylePreflight
    {
        internal int Disposals, DisposeFailures, Captures, StylePreflights, InputPreflights;
        internal bool CaptureFails, BlockCapture;
        internal Guid ObjectId;
        internal TaskCompletionSource CaptureEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CaptureRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ViewRendererCapabilities Capabilities => new("test.cleanup", 4096, 4096, true, false);
        public IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests
            => ObjectId == Guid.Empty ? [] : [new(ObjectId, [0], includeSourceGeometry: true)];
        public async ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame, CancellationToken cancellationToken = default)
        {
            Captures++; CaptureEntered.TrySetResult();
            if (BlockCapture) await CaptureRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (CaptureFails) throw new IOException("capture failed");
            return new FrameBufferLease(frame.Width, frame.Height, frame.Width * 4,
                new byte[frame.Width * frame.Height * 4]);
        }
        public void ValidateSceneInputs(PreviewFrame frame)
        {
            InputPreflights++;
            if (ObjectId == Guid.Empty) return;
            var input = Assert.Single(frame.SceneInputs).Value;
            Assert.Equal(ObjectId, input.ObjectId);
            Assert.Single(input.BoneWorldMatrices);
            Assert.NotNull(input.SourceGeometry);
        }
        public void ValidateRenderingStyles(OriginalFrameScene scene, IReadOnlyList<SceneRenderingStyleBinding> bindings)
        { StylePreflights++; Assert.Single(bindings); }
        public ValueTask DisposeAsync()
        { Disposals++; if (DisposeFailures-- > 0) throw new IOException("capture destroy failed"); return ValueTask.CompletedTask; }
    }

    private sealed class DeviceCaptureRenderer : CaptureRenderer, IViewRendererDeviceInfoProvider
    {
        internal static ViewRendererDeviceInfo Identity { get; } = new("test.cleanup", "CPU metadata fixture", 177, false);
        public ViewRendererDeviceInfo DeviceInfo => Identity;
    }

    private sealed class CleanupException(int failures) : IOException("initialization failed"), IEditorRenderInitializationCleanup
    {
        internal int Disposals;
        internal IOException CleanupError { get; } = new("initialization destroy failed");
        public ValueTask DisposeAsync()
        { Disposals++; if (Disposals <= failures) throw CleanupError; return ValueTask.CompletedTask; }
    }

    private sealed class OrdinaryDisposableException : IOException, IAsyncDisposable
    {
        internal int Disposals;
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class Devices : IProgress<ViewRendererDeviceInfo>
    {
        internal List<ViewRendererDeviceInfo> Values { get; } = [];
        public void Report(ViewRendererDeviceInfo value) => Values.Add(value);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "mda-render-cleanup-" + Guid.NewGuid().ToString("N"));
        internal Fixture() => Directory.CreateDirectory(directory);
        internal EditorProjectWorkspace Workspace(TestEngine engine)
        {
            var workspace = new EditorProjectWorkspace(engine);
            string path = Path.Combine(directory, "fixture.pmx");
            workspace.AddModel(path, EditorProjectWorkspace.CreateModelAsset(MorphApplicationTests.Model([]), path, Token), ModelRole.Character);
            return workspace;
        }
        internal OutputSettingsDraft Draft(string name)
        {
            var request = new ExportRequest(new OfflineFramePlan(4, 3, new(30), 0, 0));
            var preset = new ViewOutputPresetDefinition("test.cleanup", "Cleanup fixture", new(4, 3), new(4, 3, new(30)));
            return new(preset, request, OutputMediaKind.PngImage, Path.Combine(directory, name + ".png"), false, Vector4.One, null);
        }
        public void Dispose()
        {
            string target = Path.GetFullPath(directory);
            string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(target).StartsWith("mda-render-cleanup-", StringComparison.Ordinal)
                || (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Unexpected render cleanup fixture path.");
            Directory.Delete(target, recursive: true);
        }
    }

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
