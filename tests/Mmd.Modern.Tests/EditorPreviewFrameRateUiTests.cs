using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Mmd.Modern.App;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorPreviewFrameRateUiTests : IAsyncDisposable
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));

    [Theory]
    [InlineData("zh-CN", 1100, false)]
    [InlineData("en", 1280, false)]
    [InlineData("ja", 1100, true)]
    public async Task LocalizedNonmodalTipPreservesQualityAndFitsTheEditor(string language, int width, bool light)
        => await session.Dispatch(() =>
        {
            string previousLanguage = EditorLocalization.CurrentLanguage;
            var (window, shell, viewport) = CreateEditor();
            try
            {
                window.Width = width; window.Height = 800;
                shell.SelectedThemeIndex = light ? 1 : 0;
                EditorLocalization.SetLanguage(language);
                var quality = viewport.Quality;
                var output = shell.OutputSettings;
                int sourceFrame = shell.CurrentFrame;
                var focus = window.FindControl<Button>("ThemeToggle")!;
                focus.Focus();
                shell.SetPreviewFrameRate(new(true, 18.5, true));
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();

                var popup = window.FindControl<Popup>("LowPreviewFrameRatePopup")!;
                var status = window.FindControl<TextBlock>("PreviewFrameRateStatus")!;
                var message = window.FindControl<TextBlock>("LowPreviewFrameRateMessage")!;
                var settings = window.FindControl<Button>("LowPreviewFrameRateSettingsButton")!;
                var dismiss = window.FindControl<Button>("DismissLowPreviewFrameRateButton")!;
                Assert.True(popup.IsOpen);
                Assert.Same(window.FindControl<Border>("ProjectHeaderBar"), popup.PlacementTarget);
                Assert.Equal(PlacementMode.BottomEdgeAlignedRight, popup.Placement);
                Assert.False(popup.IsLightDismissEnabled);
                Assert.False(popup.ShouldUseOverlayLayer);
                Assert.False(popup.TakesFocusFromNativeControl);
                Assert.True(status.IsVisible);
                Assert.Contains("18.5", status.Text);
                Assert.Equal(EditorLocalization.Text("Mda.Text.Preview.LowFrameRate"), message.Text);
                Assert.DoesNotContain("Mda.Text.", settings.Content?.ToString() ?? "");
                Assert.Equal(light ? ThemeVariant.Light : ThemeVariant.Dark, window.ActualThemeVariant);
                Assert.Empty(window.OwnedWindows); // Popup does not open a modal editor dialog.
                Assert.True(focus.IsFocused);
                Assert.Equal(quality, viewport.Quality);
                Assert.Same(output, shell.OutputSettings);
                Assert.Equal(sourceFrame, shell.CurrentFrame);

                var card = window.FindControl<Border>("LowPreviewFrameRateReminder")!;
                Assert.InRange(card.Bounds.Width, 299, 301);
                Assert.Equal(2, Grid.GetColumn(dismiss));
                Assert.Equal(0, Grid.GetRow(dismiss));
                Assert.Contains("previewTipAction", settings.Classes);
                Assert.Contains("previewTipAction", dismiss.Classes);
                Assert.Equal(new Thickness(1), settings.BorderThickness);
                Assert.True(dismiss.Bounds.Width >= dismiss.DesiredSize.Width - .5);
                Assert.True(status.Bounds.Width > 0);
                var statusOrigin = status.TranslatePoint(default, window)!.Value;
                Assert.InRange(statusOrigin.X, 0, window.Bounds.Width - status.Bounds.Width);
                SaveScreenshots(window, card, language, width, light);

                string next = language == "en" ? "ja" : "en";
                EditorLocalization.SetLanguage(next);
                Assert.Equal(EditorLocalization.Text("Mda.Text.Preview.LowFrameRate"), message.Text);
                Assert.Equal(EditorLocalization.Format("Mda.Text.Preview.FrameRate", 18.5), status.Text);
                dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(popup.IsOpen);
                Assert.False(shell.HasLowPreviewFrameRateWarning);
                Assert.Equal(quality, viewport.Quality);
            }
            finally { CloseEditor(window); EditorLocalization.SetLanguage(previousLanguage); }
            return true;
        }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task CompletedFrameCallbacksDeduplicateResetGenerationAndStopTheHeartbeatOnDetach()
        => await session.Dispatch(() =>
        {
            var (window, shell, viewport) = CreateEditor();
            try
            {
                SetPlaying(shell, true);
                var timer = (DispatcherTimer)Field(window, "previewFrameRateTimer")!;
                Assert.True(timer.IsEnabled);
                var completed = (Action<EditorViewportPresentationStatistics>)typeof(EditorViewportHost)
                    .GetField("FramePresented", Hidden)!.GetValue(viewport)!;
                var statistics = new EditorViewportPresentationStatistics(1, 0, 0, 0, 1280, 720, 2, null);
                completed(statistics);
                Assert.Equal(1L, Field(window, "lastMeasuredPresentCount"));
                completed(statistics);
                Assert.Equal(1L, Field(window, "lastMeasuredPresentCount"));
                long generation = viewport.PresentationGeneration;
                typeof(EditorViewportHost).GetField("revision", Hidden)!.SetValue(viewport, generation + 1);
                completed(statistics); // Equal count from a new renderer still resets its baseline.
                Assert.Equal(generation + 1, Field(window, "previewPresentationGeneration"));
                completed(statistics with { PresentCount = 3 });
                completed(statistics with { PresentCount = 1 }); // A counter rollback also resets safely.
                Assert.Equal(1L, Field(window, "lastMeasuredPresentCount"));
                Assert.False(shell.HasPreviewFrameRate); // Invisible viewport never measures or warns.
                Assert.False(shell.HasLowPreviewFrameRateWarning);
                Assert.False(viewport.HasChildWindow);

                window.WindowState = WindowState.Minimized;
                Assert.False(timer.IsEnabled);
                window.WindowState = WindowState.Normal;
                Assert.True(timer.IsEnabled);
                SetPlaying(shell, false);
                Assert.False(timer.IsEnabled);
                SetPlaying(shell, true);
                window.DataContext = null;
                Assert.False(timer.IsEnabled);
                Assert.False(shell.HasPreviewFrameRate);
                Assert.False(shell.HasLowPreviewFrameRateWarning);
                Assert.Null(typeof(EditorViewportHost).GetField("FramePresented", Hidden)!.GetValue(viewport));
                SetPlaying(shell, false);
                window.DataContext = shell;
                SetPlaying(shell, true);
                window.Close();
                Assert.False(timer.IsEnabled);
            }
            finally { CloseEditor(window); }
            return true;
        }, TestContext.Current.CancellationToken);

    private static (EditorWindow Window, EditorShellViewModel Shell, EditorViewportHost Viewport) CreateEditor()
    {
        var window = new EditorWindow(false);
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.IsVisible = false; // Real CPU Skia layout; no native child or GPU renderer.
        var shell = new EditorShellViewModel();
        window.DataContext = shell;
        window.Show();
        return (window, shell, viewport);
    }

    private static object? Field(EditorWindow window, string name) => typeof(EditorWindow).GetField(name, Hidden)!.GetValue(window);
    private static void SetPlaying(EditorShellViewModel shell, bool value) => typeof(EditorShellViewModel)
        .GetProperty(nameof(EditorShellViewModel.IsPlaying))!.SetValue(shell, value);

    private static void SaveScreenshots(EditorWindow window, Border card, string language, int width, bool light)
    {
        if (Environment.GetEnvironmentVariable("MDA_FPS_UI_ARTIFACTS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        string stem = Path.Combine(directory, $"fps-{language}-{width}-{(light ? "light" : "dark")}");
        using var editorImage = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
        editorImage.Render(window); editorImage.Save(stem + "-editor.png");
        using var tipImage = new RenderTargetBitmap(new PixelSize((int)card.Bounds.Width, (int)Math.Ceiling(card.Bounds.Height)), new Vector(96, 96));
        tipImage.Render(card); tipImage.Save(stem + "-tip.png");
    }

    private static void CloseEditor(EditorWindow window)
    {
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
