using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.Plugins;
using Mmd.Modern.Plugins.Contracts;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorPluginTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static readonly string Root = MdaWorkRoot.Path("plugins/20261113/loader675");
    private readonly string directory = Path.Combine(Root, "editor-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ActualManagerControlsEnableRunAndDisableARealPluginAndPersistTheResult()
        => await session.Dispatch(async () =>
        {
            EditorLocalization.SetLanguage("zh-CN");
            await using var service = CreateService();
            await service.InitializeAsync(Token);
            var owner = new Window { Background = new SolidColorBrush(Color.Parse("#111417")), Foreground = Brushes.White };
            owner.Show();
            var window = new EditorPluginsWindow(owner, service);
            window.Show(owner);
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(PluginState.Disabled, Assert.Single(service.Manager.Plugins).State);
                Assert.False(File.Exists(Path.Combine(directory, "plugins", "probe", "constructed.txt")));
                Click(Find<Button>(window, "EnablePluginButton"));
                await UntilAsync(() => service.Manager.Plugins.Single().State == PluginState.Active
                    && File.Exists(Path.Combine(directory, "enabled.json"))
                    && Find<Button>(window, "ClosePluginsButton").IsEnabled);
                Assert.Single(service.Commands);
                Click(Find<Button>(window, "RunPluginCommandButton"));
                await UntilAsync(() => Find<TextBlock>(window, "PluginOperationResult").Text?.Contains("privateSameContext=True", StringComparison.Ordinal) == true
                    && Find<Button>(window, "ClosePluginsButton").IsEnabled);
                SaveScreenshot(window, Path.Combine(directory, "plugin-manager.png"));
                Click(Find<Button>(window, "DisablePluginButton"));
                await UntilAsync(() => service.Manager.Plugins.Single().State == PluginState.Disabled
                    && Find<Button>(window, "ClosePluginsButton").IsEnabled);
                Assert.Empty(service.Commands);
                using var preferences = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "enabled.json")));
                Assert.Empty(preferences.RootElement.GetProperty("enabled").EnumerateArray());
            }
            finally { window.Close(); owner.Close(); }
            return true;
        }, Token).WaitAsync(TimeSpan.FromSeconds(20), Token);

    [Fact]
    public async Task RestartRestoresEnablementAndEditorCloseStopsPluginsBeforeDisposingTheWorkspace()
        => await session.Dispatch(async () =>
        {
            await using (var first = CreateService())
            {
                await first.InitializeAsync(Token);
                Assert.True((await first.SetEnabledAsync("probe", true, Token)).Succeeded);
            }
            await using var restored = CreateService(copyFixture: false);
            await restored.InitializeAsync(Token);
            Assert.Single(restored.Commands);
            using var shell = new EditorShellViewModel();
            var editor = new EditorWindow(false, new EditorPreferences
            { AutoSaveEnabled = false, RecoveryDirectory = Path.Combine(directory, "recovery") });
            var viewport = editor.FindControl<EditorViewportHost>("SceneViewport")!;
            viewport.IsVisible = false; viewport.IsSuspended = true;
            editor.DataContext = shell;
            Assert.False(editor.FindControl<MenuItem>("PluginsMenuItem")!.IsEnabled);
            editor.AttachPlugins(restored);
            Assert.True(editor.FindControl<MenuItem>("PluginsMenuItem")!.IsEnabled);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            editor.Closed += (_, _) => closed.TrySetResult();
            editor.Show(); editor.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Null(editor.Plugins);
            Assert.Empty(restored.Commands);
            Assert.True(File.Exists(Path.Combine(directory, "plugins", "probe", "stopped.txt")));
            Assert.True(File.Exists(Path.Combine(directory, "plugins", "probe", "disposed.txt")));
            Assert.Null(viewport.AdapterName);
            return true;
        }, Token).WaitAsync(TimeSpan.FromSeconds(20), Token);

    [Fact]
    public async Task InvalidPreferencesAreDisplayedWithoutExecutingAnyPlugin()
        => await session.Dispatch(async () =>
        {
            await using var service = CreateService();
            File.WriteAllText(Path.Combine(directory, "enabled.json"), "{ invalid json");
            await service.InitializeAsync(Token);
            Assert.NotNull(service.Diagnostic);
            Assert.False(File.Exists(Path.Combine(directory, "plugins", "probe", "constructed.txt")));
            var owner = new Window();
            var window = new EditorPluginsWindow(owner, service);
            owner.Show(); window.Show(owner);
            try { Assert.Equal(service.Diagnostic, Find<TextBlock>(window, "PluginOperationResult").Text); }
            finally { window.Close(); owner.Close(); }
            return true;
        }, Token);

    private EditorPluginService CreateService(bool copyFixture = true)
    {
        string root = Path.Combine(directory, "plugins"), plugin = Path.Combine(root, "probe");
        Directory.CreateDirectory(plugin);
        if (copyFixture)
        {
            foreach (string path in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "PluginFixtures")))
                File.Copy(path, Path.Combine(plugin, Path.GetFileName(path)), true);
            File.WriteAllBytes(Path.Combine(plugin, "mda-plugin.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, id = "probe", name = "插件加载测试", version = "1.0.0", apiVersion = 1,
                minimumHostVersion = "0.1.0", entryAssembly = "Mmd.Modern.PluginFixture.dll",
                entryType = "Mmd.Modern.PluginFixture.CommandPlugin", capabilities = new[] { "editor.commands" }
            }));
        }
        return new(root, Path.Combine(directory, "enabled.json"), new(new(0, 1, 21), 1, "zh-CN"));
    }

    private static T Find<T>(Window window, string name) where T : Control
        => Assert.Single(window.GetVisualDescendants().OfType<T>(), control => control.Name == name);
    private static void Click(Button button)
    { Assert.True(button.IsEffectivelyEnabled); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
    private static async Task UntilAsync(Func<bool> ready)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!ready()) { await Task.Delay(10, timeout.Token); Dispatcher.UIThread.RunJobs(); }
    }
    private static void SaveScreenshot(Window window, string destination)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start(); try { Dispatcher.UIThread.PushFrame(frame); } finally { timer.Stop(); }
        window.UpdateLayout();
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
        bitmap.Render(window); bitmap.Save(destination);
    }
    public async ValueTask DisposeAsync()
    {
        await Task.Run(session.Dispose).ConfigureAwait(false);
        // Collectible contexts request unload; they do not promise synchronous file release.
        // Keep the owned fixture and its lifecycle markers as evidence until the test host exits.
    }
}
