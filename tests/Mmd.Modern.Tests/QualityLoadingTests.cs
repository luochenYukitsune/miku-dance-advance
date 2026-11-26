using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class QualityLoadingCollection
{
    public const string Name = "Quality loading headless application";
}

public static class QualityLoadingAppBuilder
{
    // The production interface embeds its fonts. The drawing stub cannot load font
    // streams, so use the real CPU Skia backend while the native viewport stays isolated.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<global::Mmd.Modern.App.App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

[Collection(QualityLoadingCollection.Name)]
public sealed class QualityLoadingTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ChangedQualityKeepsRealLoadingActiveWithoutRestartingTheRenderer() => await session.Dispatch(() =>
    {
        var (window, shell, viewport) = CreateEditor();
        try
        {
            typeof(EditorViewportHost).GetMethod("SetPreparingFrame", Instance)!.Invoke(viewport, [false]);
            Assert.False(shell.IsLoading);
            var revision = Field(viewport, "revision");
            var history = Field(viewport, "historyGeneration");
            var quality = viewport.Quality with { Diffusion = new(true, .45f, .002f) };
            viewport.Quality = quality;
            var token = Field(viewport, "preparationToken");
            Assert.True(viewport.IsPreparingFrame);
            Assert.True(shell.IsPreparingPreview);
            Assert.True(shell.IsLoading);
            Assert.True(window.FindControl<LoadingDots>("BusyLoadingDots")!.IsActive);
            Assert.False(shell.IsBusy);
            Assert.True(shell.CanEditWorkspace);
            Assert.Equal(revision, Field(viewport, "revision"));
            Assert.Equal(history, Field(viewport, "historyGeneration"));

            viewport.Quality = quality;
            Assert.Equal(token, Field(viewport, "preparationToken"));
            viewport.Quality = quality with { Diffusion = new(true, .3f, .002f) };
            Assert.Equal((long)token! + 1, Field(viewport, "preparationToken"));
            Assert.True(shell.IsLoading);
            Assert.Equal(revision, Field(viewport, "revision"));
            Assert.Equal(history, Field(viewport, "historyGeneration"));
        }
        finally { CloseEditor(window); }
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ExportResourceReleaseWaitsForTheWorkerWithoutBlockingTheDispatcherOrCancellingCleanup() => await session.Dispatch(async () =>
    {
        var (window, _, viewport) = CreateEditor();
        var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => viewport.SuspendAndReleaseResourcesAsync(TestContext.Current.CancellationToken));
            var quality = viewport.Quality;
            var pose = viewport.ManualCameraPose;
            viewport.IsSuspended = true;
            typeof(EditorViewportHost).GetField("workerTask", Instance)!.SetValue(viewport, worker.Task);
            using var cancellation = new CancellationTokenSource();
            var release = viewport.SuspendAndReleaseResourcesAsync(cancellation.Token);
            Assert.False(release.IsCompleted);
            bool heartbeat = false;
            await Dispatcher.UIThread.InvokeAsync(() => heartbeat = true, DispatcherPriority.Background);
            Assert.True(heartbeat);
            cancellation.Cancel();
            Assert.False(release.IsCompleted);
            worker.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => release);
            Assert.True(viewport.IsSuspended);
            Assert.Equal(quality, viewport.Quality);
            Assert.Equal(pose, viewport.ManualCameraPose);
            Assert.False(viewport.HasChildWindow);
            Assert.Null(Field(viewport, "renderer"));
        }
        finally { worker.TrySetResult(); CloseEditor(window); }
        return true;
    }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(false, "success")]
    [InlineData(true, "success")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "failure")]
    [InlineData(true, "failure")]
    public async Task ExportOperationRestoresPriorSuspensionAfterEveryOutcome(bool wasSuspended, string outcome) => await session.Dispatch(async () =>
    {
        var (window, shell, viewport) = CreateEditor();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            viewport.IsSuspended = wasSuspended;
            var quality = viewport.Quality;
            typeof(EditorShellViewModel).GetProperty(nameof(EditorShellViewModel.IsPlaying))!.SetValue(shell, true);
            Func<CancellationToken, Task> export = async token =>
            {
                await viewport.SuspendAndReleaseResourcesAsync(token);
                started.SetResult();
                await finish.Task.WaitAsync(token);
            };
            var operation = (Task)typeof(EditorWindow).GetMethod("ExecuteAsync", Instance)!.Invoke(window, ["Export test", export, null])!;
            await started.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.True(viewport.IsSuspended);
            Assert.True(shell.IsBusy);
            Assert.True(shell.IsLoading);
            Assert.False(shell.IsPlaying);
            Assert.False(shell.CanEditWorkspace);
            Assert.Equal(quality, viewport.Quality);
            bool heartbeat = false;
            await Dispatcher.UIThread.InvokeAsync(() => heartbeat = true, DispatcherPriority.Background);
            Assert.True(heartbeat);
            if (outcome == "cancel")
                ((CancellationTokenSource)typeof(EditorWindow).GetField("_operationCancellation", Instance)!.GetValue(window)!).Cancel();
            else if (outcome == "failure")
            {
                finish.SetException(new InvalidOperationException("Export test failure"));
                // The operation continuation creates the actual error dialog. Wait for that
                // visible window explicitly instead of racing it with one posted Close call.
                var dialog = await WaitForOwnedDialogAsync(window);
                dialog.Close();
            }
            else finish.SetResult();
            await operation.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(wasSuspended, viewport.IsSuspended);
            Assert.False(shell.IsBusy);
            Assert.False(shell.IsPlaying);
            Assert.Equal(quality, viewport.Quality);
            Assert.Equal(outcome switch
            {
                "cancel" => EditorActivityState.Cancelled,
                "failure" => EditorActivityState.Failed,
                _ => EditorActivityState.Busy // The requested preview frame has not reached a native viewport in this headless test.
            }, shell.ActivityState);
            Assert.True(viewport.IsPreparingFrame);
        }
        finally
        {
            finish.TrySetResult();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            CloseEditor(window);
        }
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ReportedWorkerFailureDoesNotPoisonLaterNativeTeardown() => await session.Dispatch(async () =>
    {
        var (window, _, viewport) = CreateEditor();
        try
        {
            viewport.IsSuspended = true;
            var failure = new InvalidOperationException("Prior worker failure");
            typeof(EditorViewportHost).GetField("workerTask", Instance)!.SetValue(viewport, Task.FromException(failure));
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => viewport.SuspendAndReleaseResourcesAsync(TestContext.Current.CancellationToken)));
            Assert.Equal(TaskStatus.RanToCompletion, ((Task)Field(viewport, "workerTask")!).Status);
            await viewport.SuspendAndReleaseResourcesAsync(TestContext.Current.CancellationToken);
        }
        finally { CloseEditor(window); }
        return true;
    }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(null)]
    [InlineData(OutputMediaKind.PngImage)]
    [InlineData(OutputMediaKind.Video)]
    public async Task OutputEntryOpensItsRequestedFormatWithoutSuspendingPreview(OutputMediaKind? kind) => await session.Dispatch(async () =>
    {
        var (window, _, viewport) = CreateEditor();
        try
        {
            var revision = Field(viewport, "revision");
            if (kind is { } requested)
            {
                var entry = window.GetVisualDescendants().OfType<Menu>().SelectMany(menu => menu.Items.OfType<MenuItem>())
                    .SelectMany(top => top.Items.OfType<MenuItem>()).Single(item => Equals(item.Tag, requested));
                entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            else
                window.FindControl<Button>("OutputSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var dialog = Assert.IsType<OutputSettingsWindow>(await WaitForOwnedDialogAsync(window));
            Assert.True(dialog.IsVisible);
            Assert.Equal(kind ?? OutputMediaKind.Video, (OutputMediaKind)dialog.ViewModel.MediaKindIndex);
            Assert.False(viewport.IsSuspended);
            Assert.Equal(revision, Field(viewport, "revision"));
            dialog.Close(null);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.False(viewport.IsSuspended);
        }
        finally { CloseEditor(window); }
        return true;
    }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("zh-CN", "预览")]
    [InlineData("en", "Preview")]
    [InlineData("ja", "プレビュー")]
    public async Task AdmittedSamplingFeedbackBindsToTheStatusAndRuntimeAndClearsOnRecoveryOrDetach(string language, string expectedWord) => await session.Dispatch(() =>
    {
        var (window, shell, viewport) = CreateEditor();
        string previousLanguage = EditorLocalization.CurrentLanguage;
        try
        {
            EditorLocalization.SetLanguage(language);
            var quality = viewport.Quality;
            var status = window.FindControl<TextBlock>("PreviewSamplingStatus")!;
            var publish = typeof(EditorShellViewModel).GetMethod("SetPreviewStatistics", Instance)!;
            var adjusted = new EditorViewportPresentationStatistics(1, 0, 0, 0, 1280, 720, 2, null)
            {
                RequestedSpatialSamples = 16, EffectiveSpatialSamples = 4,
                PreviewSamplingAdjustmentReason = "Preview test memory budget"
            };
            publish.Invoke(shell, [adjusted]);
            Assert.True(status.IsVisible);
            Assert.True(shell.HasPreviewSamplingAdjustment);
            Assert.Contains(expectedWord, status.Text);
            Assert.Contains("4", status.Text);
            Assert.Contains("16", status.Text);
            Assert.DoesNotContain("Mda.Text.", status.Text);
            Assert.DoesNotContain("Mda.Text.", ToolTip.GetTip(status)?.ToString() ?? "");
            var runtime = new EditorPreviewRuntimeInfo("Test adapter", false, 1280, 720, adjusted);
            Assert.Equal(status.Text, runtime.SamplingSummary);
            Assert.Equal(quality, viewport.Quality);

            publish.Invoke(shell, [adjusted with { EffectiveSpatialSamples = 16, PreviewSamplingAdjustmentReason = null }]);
            Assert.False(status.IsVisible);
            Assert.False(shell.HasPreviewSamplingAdjustment);
            publish.Invoke(shell, [adjusted]);
            Assert.True(status.IsVisible);
            window.DataContext = null;
            Assert.False(shell.HasPreviewSamplingAdjustment);
            Assert.Equal("", shell.PreviewSamplingSummary);
            window.DataContext = shell;
            Assert.False(status.IsVisible);
        }
        finally { EditorLocalization.SetLanguage(previousLanguage); CloseEditor(window); }
        return true;
    }, TestContext.Current.CancellationToken);

    private static (EditorWindow Window, EditorShellViewModel Shell, EditorViewportHost Viewport) CreateEditor()
    {
        var window = new EditorWindow(false);
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.IsVisible = false; // Exercise formal bindings and operations without creating an HWND or using the GPU.
        var shell = new EditorShellViewModel();
        window.DataContext = shell;
        window.Show();
        return (window, shell, viewport);
    }

    private static object? Field(EditorViewportHost viewport, string name) => typeof(EditorViewportHost).GetField(name, Instance)!.GetValue(viewport);
    private static async Task<Window> WaitForOwnedDialogAsync(EditorWindow owner)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TestTimeout);
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (owner.OwnedWindows.FirstOrDefault(window => window.IsVisible) is { } dialog) return dialog;
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private static void CloseEditor(EditorWindow window)
    {
        foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
        window.Close();
        // Drain the final detach/layout work while this test application's font manager
        // and render services are still alive, before HeadlessUnitTestSession resets them.
        Dispatcher.UIThread.RunJobs();
    }
    // Dispatch completion can inline the test continuation on the session's own worker.
    // Join that worker elsewhere instead of making synchronous disposal wait for itself.
    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
