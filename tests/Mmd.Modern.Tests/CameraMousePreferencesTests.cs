using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Mmd.Modern.App;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class CameraMousePreferencesTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mda-camera-preferences-" + Guid.NewGuid().ToString("N"));
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void CameraAxesPersistIndependentlyAndLegacyFilesDefaultToUninverted(bool x, bool y)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        var preferences = new EditorPreferences { InvertCameraMouseX = x, InvertCameraMouseY = y };
        preferences.Save(path);
        var loaded = EditorPreferences.Load(out var diagnostic, path);
        Assert.Null(diagnostic);
        Assert.Equal(x, loaded.InvertCameraMouseX); Assert.Equal(y, loaded.InvertCameraMouseY);
        File.WriteAllText(path, "{\"schemaVersion\":1,\"themeIndex\":0,\"language\":\"zh-CN\"}");
        loaded = EditorPreferences.Load(out diagnostic, path);
        Assert.Null(diagnostic); Assert.False(loaded.InvertCameraMouseX); Assert.False(loaded.InvertCameraMouseY);
    }

    [Fact]
    public async Task PreferencesApplyReturnsDetachedAxesAndCancelPreservesTheSource() => await session.Dispatch(async () =>
    {
        var source = new EditorPreferences { InvertCameraMouseX = true, InvertCameraMouseY = false };
        var owner = new Window(); owner.Show();
        try
        {
            var dialog = new EditorPreferencesWindow(source);
            var result = dialog.ShowDialog<EditorPreferences?>(owner);
            Assert.True(dialog.FindControl<CheckBox>("PreferencesInvertCameraMouseXBox")!.IsChecked);
            Assert.False(dialog.FindControl<CheckBox>("PreferencesInvertCameraMouseYBox")!.IsChecked);
            dialog.FindControl<CheckBox>("PreferencesInvertCameraMouseXBox")!.IsChecked = false;
            dialog.FindControl<CheckBox>("PreferencesInvertCameraMouseYBox")!.IsChecked = true;
            dialog.FindControl<Button>("ApplyPreferencesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(result.IsCompletedSuccessfully);
            var applied = await result;
            Assert.False(applied!.InvertCameraMouseX); Assert.True(applied.InvertCameraMouseY);
            Assert.True(source.InvertCameraMouseX); Assert.False(source.InvertCameraMouseY);

            var cancelled = new EditorPreferencesWindow(source);
            var cancelledResult = cancelled.ShowDialog<EditorPreferences?>(owner);
            cancelled.FindControl<CheckBox>("PreferencesInvertCameraMouseYBox")!.IsChecked = true;
            cancelled.Close(null); Dispatcher.UIThread.RunJobs();
            Assert.True(cancelledResult.IsCompletedSuccessfully); Assert.Null(await cancelledResult);
            Assert.False(source.InvertCameraMouseY);
            return true;
        }
        finally { owner.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ApplyingLocalAxesUpdatesExistingViewportWithoutDirtyingProject() => await session.Dispatch(() =>
    {
        var window = new EditorWindow(false);
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!; viewport.IsVisible = false;
        window.DataContext = new EditorShellViewModel(); window.Show();
        try
        {
            var shell = (EditorShellViewModel)window.DataContext;
            var bones = viewport.BoneController; var lights = viewport.LightController;
            typeof(EditorWindow).GetMethod("ApplyPreferences", Hidden)!.Invoke(window,
                [new EditorPreferences { InvertCameraMouseX = true, InvertCameraMouseY = true }]);
            Assert.True(viewport.InvertCameraMouseX); Assert.True(viewport.InvertCameraMouseY);
            Assert.False(shell.Workspace.IsDirty);
            Assert.Same(bones, viewport.BoneController); Assert.Same(lights, viewport.LightController);
            typeof(EditorWindow).GetMethod("ApplyPreferences", Hidden)!.Invoke(window, [new EditorPreferences()]);
            Assert.False(viewport.InvertCameraMouseX); Assert.False(viewport.InvertCameraMouseY);
            return true;
        }
        finally
        {
            typeof(EditorWindow).GetField("_closingConfirmed", Hidden)!.SetValue(window, true);
            window.Close(); Dispatcher.UIThread.RunJobs();
        }
    }, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        try { await Task.Run(session.Dispose).ConfigureAwait(false); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
