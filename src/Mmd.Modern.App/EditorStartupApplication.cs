using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Mmd.Modern.App;

internal enum EditorStartupPhase
{
    WaitingForStartupFrame,
    LoadingPreferences,
    ChoosingRenderEngine,
    CreatingEditor,
    CreatingWorkspace,
    AttachingWorkspace,
    InitializingViewport,
    Ready,
    Failed,
    Cancelled
}

/// <summary>
/// Owns only startup. The desktop takes ownership of the editor after its first real GPU present.
/// Each UI construction stage yields to the startup window's render loop; GPU creation already
/// runs on EditorViewportHost's serialized worker. No animation duration gates editor readiness.
/// </summary>
internal sealed partial class EditorStartupApplication
{
    private readonly IClassicDesktopStyleApplicationLifetime desktop;
    private readonly ShutdownMode originalShutdownMode;
    private readonly CancellationTokenSource cancellation = new();
    private readonly CancellationToken token;
    private readonly EditorStartupWindow startup;
    private readonly EditorStartupConfiguration? configuration;
    private EditorWindow? editor;
    private EditorShellViewModel? workspace;
    private bool workspaceAttached, completed, closed, lifetimeExited;

    private EditorStartupApplication(IClassicDesktopStyleApplicationLifetime desktop, EditorStartupConfiguration? configuration = null)
    {
        configuration?.Validate();
        this.configuration = configuration;
        this.desktop = desktop;
        token = cancellation.Token;
        originalShutdownMode = desktop.ShutdownMode;
        startup = new EditorStartupWindow();
        startup.Opened += OnStartupOpened;
        startup.Closed += OnStartupClosed;
        desktop.Exit += OnLifetimeExit;
        // A closed startup window must never strand the prepared editor off screen.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.MainWindow = startup;
    }

    internal static EditorStartupApplication? Current { get; private set; }
    internal EditorStartupPhase Phase { get; private set; } = EditorStartupPhase.WaitingForStartupFrame;
    internal Exception? LastError { get; private set; }
    internal EditorStartupWindow StartupWindow => startup;
    internal EditorWindow? EditorWindow => editor;
    internal event EventHandler? Changed;

    internal static void Start(IClassicDesktopStyleApplicationLifetime desktop, EditorStartupConfiguration? configuration = null) =>
        Current = new EditorStartupApplication(desktop, configuration);

    private async void OnStartupOpened(object? sender, EventArgs args)
    {
        startup.Opened -= OnStartupOpened;
        EditorPluginService? preparedPlugins = null;
        try
        {
            await YieldToStartupFrameAsync();
            // Controlled hosts/tests supply their own storage and never initialize real user data.
            if (configuration is null) await Task.Run(EditorUserDataPaths.InitializeProduction, token);
            SetPhase(EditorStartupPhase.LoadingPreferences);
            token.ThrowIfCancellationRequested();
            var preferences = configuration?.Preferences ?? await Task.Run(EditorPreferences.Load, token);
            token.ThrowIfCancellationRequested();
            startup.ApplyPreferences(preferences);
            EditorLocalization.SetLanguage(preferences.Language);
            await YieldToStartupFrameAsync();

            if (!await ReviewStartupUpdateAsync()) return;

            // Explicit test/embedding configurations never scan the user's plugin folders.
            // Ownership transfers only after the editor and its first frame are ready.
            preparedPlugins = configuration?.Plugins ?? (configuration is null ? EditorPluginService.CreateDefault() : null);
            if (preparedPlugins is not null) await preparedPlugins.InitializeAsync(token);

            var openRequest = await Task.Run(() => EditorFileOpenRequest.Prepare(desktop.Args ?? [], preferences, token), token);

            SetPhase(EditorStartupPhase.ChoosingRenderEngine);
            var renderEngine = await ChooseRenderEngineAsync(configuration, ShowRenderEngineSelectionAsync, token,
                selectClassicResources: cancellation => EditorClassicToonResourcePicker.ChooseForStartupAsync(startup, cancellation),
                requiredProjectFormat: openRequest?.ProjectFormat);
            if (renderEngine is null) { startup.Close(); return; }

            SetPhase(EditorStartupPhase.CreatingEditor);
            token.ThrowIfCancellationRequested();
            editor = configuration is null ? new EditorWindow()
                : new EditorWindow(configuration.PersistPreferences, preferences);
            editor.ShowActivated = false;
            editor.ShowInTaskbar = false;
            editor.WindowStartupLocation = WindowStartupLocation.Manual;
            editor.Position = new PixelPoint(-20000, -20000);
            editor.Closed += OnPreparedEditorClosed;
            await YieldToStartupFrameAsync();

            SetPhase(EditorStartupPhase.CreatingWorkspace);
            token.ThrowIfCancellationRequested();
            var camera = new Mmd.Modern.Application.PmmCameraPose(new(0, 10, 0), System.Numerics.Vector3.Zero, -45, 30, true);
            workspace = new EditorShellViewModel(camera, renderEngine);
            workspace.AttachPluginService(preparedPlugins);
            if (configuration is not null) workspace.Workspace.PackageCacheDirectory = configuration.PackageCacheDirectory;
            await YieldToStartupFrameAsync();

            SetPhase(EditorStartupPhase.AttachingWorkspace);
            token.ThrowIfCancellationRequested();
            // AttachShell takes ownership before it applies preferences and viewport bindings.
            workspaceAttached = true;
            editor.DataContext = workspace;
            await YieldToStartupFrameAsync();

            var viewport = editor.FindControl<EditorViewportHost>("SceneViewport")
                ?? throw new InvalidOperationException("The editor viewport is unavailable.");
            SetPhase(EditorStartupPhase.InitializingViewport);
            token.ThrowIfCancellationRequested();
            await AwaitFirstPresentationAsync(viewport);
            token.ThrowIfCancellationRequested();

            if (preparedPlugins is not null)
            {
                editor.AttachPlugins(preparedPlugins);
                preparedPlugins = null;
            }

            PositionOnStartupScreen();
            desktop.MainWindow = editor;
            desktop.ShutdownMode = originalShutdownMode;
            editor.ShowInTaskbar = true;
            editor.ShowActivated = true;
            editor.Closed -= OnPreparedEditorClosed;
            startup.Closed -= OnStartupClosed;
            completed = true;
            startup.StopAnimation();
            SetPhase(EditorStartupPhase.Ready);
            editor.Activate();
            startup.Close();
            // Recovery choices belong to the visible, fully initialized editor. Errors here
            // retain the editor and its copies rather than re-entering startup failure cleanup.
            await editor.ReviewRecoveryAsync();
            if (openRequest is not null) await editor.OpenAssociatedFileAsync(openRequest);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (closed || lifetimeExited) return;
            LastError = error;
            try { ClosePreparedEditor(); }
            catch (Exception cleanupError) { error.Data["StartupCleanupError"] = cleanupError; }
            SetPhase(EditorStartupPhase.Failed);
            if (closed || lifetimeExited) return;
            startup.ShowFailure(error);
            startup.Activate();
        }
        finally
        {
            if (preparedPlugins is not null)
            {
                try { await preparedPlugins.DisposeAsync(); }
                catch (Exception error) { LastError ??= error; }
            }
        }
    }

    private async Task YieldToStartupFrameAsync()
    {
        token.ThrowIfCancellationRequested();
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => frame.TrySetCanceled(token));
        startup.RequestAnimationFrame(_ => frame.TrySetResult());
        await frame.Task;
        // The animation callback precedes painting. Run the next construction stage below
        // render priority so the already-visible startup surface can be committed first.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        token.ThrowIfCancellationRequested();
    }

    private Task AwaitFirstPresentationAsync(EditorViewportHost viewport)
        => AwaitFirstPresentationAsync(viewport, () =>
        {
            // NativeControlHost needs a shown HWND. Preparing beyond the desktop avoids
            // exposing the white native child before the renderer has supplied its first frame.
            editor!.Show();
            viewport.RequestRender();
        }, token);

    internal static async Task AwaitFirstPresentationAsync(EditorViewportHost viewport, Action requestPresentation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(requestPresentation);
        cancellationToken.ThrowIfCancellationRequested();
        var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPresented(EditorViewportPresentationStatistics _) => presented.TrySetResult();
        void OnError(Exception error) => presented.TrySetException(error);
        viewport.FramePresented += OnPresented;
        viewport.Error += OnError;
        try
        {
            using var registration = cancellationToken.Register(() => presented.TrySetCanceled(cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            requestPresentation();
            await presented.Task;
        }
        finally
        {
            viewport.FramePresented -= OnPresented;
            viewport.Error -= OnError;
        }
    }

    private void PositionOnStartupScreen()
    {
        var screen = startup.Screens.ScreenFromWindow(startup) ?? startup.Screens.Primary;
        if (screen is null || editor is null) return;
        var area = screen.WorkingArea;
        var size = editor.FrameSize ?? editor.ClientSize;
        int width = (int)Math.Round(size.Width * screen.Scaling);
        int height = (int)Math.Round(size.Height * screen.Scaling);
        editor.Position = new PixelPoint(area.X + Math.Max(0, (area.Width - width) / 2),
            area.Y + Math.Max(0, (area.Height - height) / 2));
        editor.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private void OnPreparedEditorClosed(object? sender, EventArgs args)
    {
        workspace = null;
        editor = null;
        if (!closed && !completed && !lifetimeExited) startup.Close();
    }

    private void OnStartupClosed(object? sender, EventArgs args)
    {
        if (completed || closed) return;
        closed = true;
        cancellation.Cancel();
        if (Phase != EditorStartupPhase.Failed) SetPhase(EditorStartupPhase.Cancelled);
        try { ClosePreparedEditor(); }
        catch (Exception error) { LastError ??= error; }
        finally
        {
            // If a platform shutdown is already closing windows, Exit runs before this
            // queued callback. Avoid recursively shutting down an exiting lifetime.
            Dispatcher.UIThread.Post(() =>
            {
                if (!lifetimeExited) desktop.Shutdown(LastError is null ? 0 : 1);
            }, DispatcherPriority.Background);
        }
    }

    private void ClosePreparedEditor()
    {
        var prepared = editor;
        editor = null;
        if (prepared is not null)
        {
            prepared.Closed -= OnPreparedEditorClosed;
            prepared.Close(); // EditorWindow releases its attached workspace and native renderer.
        }
        if (!workspaceAttached) workspace?.Dispose();
        workspace = null;
    }

    private void OnLifetimeExit(object? sender, ControlledApplicationLifetimeExitEventArgs args)
    {
        lifetimeExited = true;
        cancellation.Cancel();
        desktop.Exit -= OnLifetimeExit;
        startup.Opened -= OnStartupOpened;
        startup.Closed -= OnStartupClosed;
        cancellation.Dispose();
        if (ReferenceEquals(Current, this)) Current = null;
    }

    private void SetPhase(EditorStartupPhase phase)
    {
        Phase = phase;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
