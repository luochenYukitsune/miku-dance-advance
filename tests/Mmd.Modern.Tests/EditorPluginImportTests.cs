using System.IO.Compression;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.Plugins;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorPluginImportTests : IAsyncDisposable
{
    private static readonly string Root = MdaWorkRoot.Path("plugins/20261113/import681");
    private readonly string directory = Path.Combine(Root, "editor-" + Guid.NewGuid().ToString("N"));
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FileDropImportsDisabledThenActualControlsEnableAndRunWithoutRestartingExistingPlugin()
        => await session.Dispatch(async () =>
        {
            EditorLocalization.SetLanguage("zh-CN");
            string plugins = Path.Combine(directory, "plugins");
            CreatePlugin(Path.Combine(plugins, "existing"), "existing", "已启用的插件");
            string source = Path.Combine(directory, "package");
            CreatePlugin(source, "imported", "新导入的插件");
            string archive = Path.Combine(directory, "插件包.zip");
            ZipFile.CreateFromDirectory(source, archive);
            await using var service = new EditorPluginService(plugins, Path.Combine(directory, "enabled.json"), new(new(0, 1, 21), 1, "zh-CN"));
            await service.InitializeAsync(Token);
            Assert.True((await service.SetEnabledAsync("existing", true, Token)).Succeeded);
            var existingCommand = Assert.Single(service.Commands).Value;
            var owner = new Window { Background = new SolidColorBrush(Color.Parse("#111417")), Foreground = Brushes.White };
            owner.Show();
            var window = new EditorPluginsWindow(owner, service);
            window.Show(owner);
            try
            {
                Dispatcher.UIThread.RunJobs();
                var button = Find<Button>(window, "ImportPluginButton");
                Assert.Equal("导入插件…", button.Content);
                Assert.True(button.IsEffectivelyEnabled);
                // The headless platform's native picker returns an empty selection. Its
                // cancellation still exercises the real button without creating a window.
                Click(button);
                await UntilAsync(() => button.IsEffectivelyEnabled);
                using var file = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(archive));
                Assert.NotNull(file);
                var data = new DataTransfer(); data.Add(DataTransferItem.Create(DataFormat.File, file));
                window.DragDrop(new Point(100, 230), RawDragEventType.DragEnter, data, DragDropEffects.Copy);
                window.DragDrop(new Point(100, 230), RawDragEventType.Drop, data, DragDropEffects.Copy);
                await UntilAsync(() => service.Manager.Plugins.Count == 2 && button.IsEffectivelyEnabled);
                Assert.Same(existingCommand, service.Commands.Single().Value);
                Assert.False(File.Exists(Path.Combine(plugins, "existing", "stopped.txt")));
                Assert.Equal(PluginState.Disabled, service.Manager.Plugins.Single(plugin => plugin.Id == "imported").State);
                Assert.False(File.Exists(Path.Combine(plugins, "imported", "constructed.txt")));
                Assert.Equal("imported", ((PluginInfo)Find<ListBox>(window, "InstalledPlugins").SelectedItem!).Id);
                Assert.Contains("已导入", Find<TextBlock>(window, "PluginOperationResult").Text);
                SaveScreenshot(window, MdaWorkRoot.Path("plugins/20261113/hierarchy685/zh-normal-initial02.png"));
                foreach (string language in new[] { "zh-CN", "en", "ja" })
                {
                    window.Width = 680; window.Height = 560;
                    EditorLocalization.SetLanguage(language); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                    foreach (string controlName in new[] { "ImportPluginButton", "SelectedPluginName", "EnablePluginButton", "ClosePluginsButton" })
                    {
                        var control = Find<Control>(window, controlName);
                        var origin = control.TranslatePoint(default, window);
                        Assert.NotNull(origin);
                        Assert.True(new Rect(window.Bounds.Size).Contains(new Rect(origin.Value, control.Bounds.Size)), controlName + " must fit " + language);
                    }
                    SaveScreenshot(window, MdaWorkRoot.Path($"plugins/20261113/hierarchy685/{language}-minimum.png"));
                }
                EditorLocalization.SetLanguage("zh-CN");
                window.Width = 860; window.Height = 620;
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                SaveScreenshot(window, MdaWorkRoot.Path("plugins/20261113/hierarchy685/zh-normal-final02.png"));
                Click(Find<Button>(window, "EnablePluginButton"));
                await UntilAsync(() => service.Commands.Count == 2 && button.IsEffectivelyEnabled);
                var commands = Find<ComboBox>(window, "PluginCommands"); Assert.Equal(1, commands.ItemCount); commands.SelectedIndex = 0;
                Click(Find<Button>(window, "RunPluginCommandButton"));
                await UntilAsync(() => Find<TextBlock>(window, "PluginOperationResult").Text?.Contains("privateSameContext=True", StringComparison.Ordinal) == true);
                await window.ImportPackagesAsync([archive]);
                Assert.Equal(2, service.Manager.Plugins.Count);
                Assert.Contains("already exists", Find<TextBlock>(window, "PluginOperationResult").Text);
                Assert.Same(existingCommand, service.Commands.Single(command => command.PluginId == "existing").Value);
            }
            finally { window.Close(); owner.Close(); }
            return true;
        }, Token).WaitAsync(TimeSpan.FromSeconds(25), Token);

    [Fact]
    public async Task ImportCannotShadowAPluginInAnAdditionalRootOrExecuteItsCode()
    {
        string bundled = Path.Combine(directory, "bundled"), plugins = Path.Combine(directory, "plugins");
        CreatePlugin(Path.Combine(bundled, "arbitrary-folder"), "existing", "现有插件");
        string source = Path.Combine(directory, "package"); CreatePlugin(source, "existing", "同名插件");
        string archive = Path.Combine(directory, "duplicate.zip"); ZipFile.CreateFromDirectory(source, archive);
        await using var service = new EditorPluginService(plugins, Path.Combine(directory, "enabled.json"), new(new(0, 1, 21), 1, "zh-CN"), [bundled]);
        await service.InitializeAsync(Token);
        var result = await service.ImportAsync(archive, Token);
        Assert.False(result.Succeeded); Assert.Contains("already exists", result.Diagnostic);
        Assert.Equal(PluginState.Disabled, Assert.Single(service.Manager.Plugins).State);
        Assert.False(Directory.Exists(Path.Combine(plugins, "existing")));
        Assert.False(File.Exists(Path.Combine(bundled, "arbitrary-folder", "constructed.txt")));
    }

    private static void CreatePlugin(string target, string id, string name)
    {
        Directory.CreateDirectory(target);
        foreach (string path in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "PluginFixtures")))
            File.Copy(path, Path.Combine(target, Path.GetFileName(path)));
        File.WriteAllBytes(Path.Combine(target, "mda-plugin.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, id, name, version = "1.0.0", apiVersion = 1, minimumHostVersion = "0.1.0",
            entryAssembly = "Mmd.Modern.PluginFixture.dll", entryType = "Mmd.Modern.PluginFixture.CommandPlugin",
            capabilities = new[] { "editor.commands" }
        }));
    }
    private static T Find<T>(Window window, string name) where T : Control
        => Assert.Single(window.GetVisualDescendants().OfType<T>(), control => control.Name == name);
    private static void Click(Button button)
    { Assert.True(button.IsEffectivelyEnabled); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
    private static async Task UntilAsync(Func<bool> ready)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        while (!ready()) { await Task.Delay(10, timeout.Token); Dispatcher.UIThread.RunJobs(); }
    }
    // Interactive debugging aid only: nothing asserts on these files. Opt in with MMD_PLUGIN_UI_ARTIFACTS.
    private static void SaveScreenshot(Window window, string destination)
    {
        string? artifacts = Environment.GetEnvironmentVariable("MMD_PLUGIN_UI_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(artifacts)) return;
        destination = Path.Combine(Path.GetFullPath(artifacts), Path.GetFileName(destination));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start(); try { Dispatcher.UIThread.PushFrame(frame); } finally { timer.Stop(); }
        window.UpdateLayout();
        var button = Find<Button>(window, "ImportPluginButton");
        var point = button.TranslatePoint(default, window);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination + ".layout.json", JsonSerializer.Serialize(new
        {
            window = window.Bounds.ToString(), importBounds = button.Bounds.ToString(), importOrigin = point?.ToString(),
            button.IsVisible, button.Opacity, button.IsEffectivelyEnabled, content = button.Content?.ToString()
        }));
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
        bitmap.Render(window); bitmap.Save(destination);
    }
    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
