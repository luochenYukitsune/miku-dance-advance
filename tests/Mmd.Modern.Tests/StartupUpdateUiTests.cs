using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.App.Updates;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class StartupUpdateUiTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));
    private const BindingFlags InternalInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(5);
    private const string PlainNotes = "# Release summary\n<b>literal HTML</b>\n[plain link](https://example.invalid/)\n修复模型编辑。";

    [Theory]
    [InlineData("zh-CN", false)] [InlineData("zh-CN", true)]
    [InlineData("en", false)] [InlineData("en", true)]
    [InlineData("ja", false)] [InlineData("ja", true)]
    public async Task OfferUsesTheRealStartupSurfaceAndSkipCompletesOnce(string language, bool light) =>
        await session.Dispatch(async () =>
        {
            string previousLanguage = EditorLocalization.CurrentLanguage;
            EditorLocalization.SetLanguage(language);
            var window = new EditorStartupWindow { RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
            window.Show();
            try
            {
                AssertOriginalSurface(window);
                Call(window, "SetUpdateMessage", EditorLocalization.Text("Mda.Text.Update.Checking"));
                Assert.Equal(EditorLocalization.Text("Mda.Text.Update.Checking"), Control<TextBlock>(window, "StartupStatus").Text);
                Task review = Review(window, new("0.1.9", PlainNotes), canUpgrade: true);
                Assert.False(review.IsCompleted);
                Assert.True(window.IsVisible);
                Assert.Empty(window.OwnedWindows);
                Assert.Equal(560d, window.Width);
                Assert.Equal(420d, window.Height);
                Assert.True(Control<Grid>(window, "StartupUpdateContent").IsVisible);
                Assert.False(Control<StackPanel>(window, "BrandContent").IsVisible);
                Assert.False(Control<StackPanel>(window, "FailureContent").IsVisible);
                Assert.False(Control<LoadingDots>(window, "StartupDots").IsActive);
                Assert.Equal(EditorLocalization.Text("Mda.Text.Update.Title"), Control<TextBlock>(window, "StartupUpdateTitle").Text);
                Assert.DoesNotContain("Mda.Text.", Control<TextBlock>(window, "StartupUpdateTitle").Text!);
                Assert.Equal(EditorLocalization.Format("Mda.Text.Update.Version", "0.1.9"), Control<TextBlock>(window, "StartupUpdateVersion").Text);
                Assert.Equal(PlainNotes, Control<SelectableTextBlock>(window, "StartupUpdateNotes").Text);
                Assert.Equal(ScrollBarVisibility.Auto, Control<ScrollViewer>(window, "StartupUpdateNotesScroll").VerticalScrollBarVisibility);
                Assert.Equal(ScrollBarVisibility.Disabled, Control<ScrollViewer>(window, "StartupUpdateNotesScroll").HorizontalScrollBarVisibility);
                Assert.Equal(EditorLocalization.Text("Mda.Text.Update.Skip"), Control<Button>(window, "StartupUpdateSkip").Content);
                Assert.Equal(EditorLocalization.Text("Mda.Text.Update.Upgrade"), Control<Button>(window, "StartupUpdateUpgrade").Content);
                Assert.True(Control<Button>(window, "StartupUpdateUpgrade").IsEnabled);
                Click(window, "StartupUpdateSkip");
                Click(window, "StartupUpdateUpgrade"); // A queued second click cannot replace the choice.
                Assert.Equal("SkipVersion", await Decision(review));
                Assert.False(Control<Button>(window, "StartupUpdateContinue").IsEnabled);
                Assert.False(Control<Button>(window, "StartupUpdateSkip").IsEnabled);
                Assert.False(Control<Button>(window, "StartupUpdateUpgrade").IsEnabled);
                Call(window, "ResetUpdateSurface");
                AssertOriginalSurface(window);
                return true;
            }
            finally { window.Close(); Dispatcher.UIThread.RunJobs(); EditorLocalization.SetLanguage(previousLanguage); }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task UpgradeIsOnlyAChoiceAndNeverClosesOrCreatesAnotherWindow() => await session.Dispatch(async () =>
    {
        var window = new EditorStartupWindow(); window.Show();
        try
        {
            Task review = Review(window, new("0.1.9", PlainNotes), canUpgrade: true);
            Click(window, "StartupUpdateUpgrade");
            Assert.Equal("Upgrade", await Decision(review));
            Assert.True(window.IsVisible);
            Assert.Empty(window.OwnedWindows);
            Assert.Equal(PlainNotes, Control<SelectableTextBlock>(window, "StartupUpdateNotes").Text);
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task MissingUpgradeHandlerExplainsTheUnavailableActionAndAllowsContinue() => await session.Dispatch(async () =>
    {
        var window = new EditorStartupWindow(); window.Show();
        try
        {
            Task review = Review(window, new("0.1.9", PlainNotes), canUpgrade: false);
            Assert.False(Control<Button>(window, "StartupUpdateUpgrade").IsEnabled);
            Assert.True(Control<TextBlock>(window, "StartupUpdateMessage").IsVisible);
            Assert.Equal(EditorLocalization.Text("Mda.Text.Update.UpgradeUnavailable"), Control<TextBlock>(window, "StartupUpdateMessage").Text);
            Click(window, "StartupUpdateUpgrade");
            Assert.False(review.IsCompleted);
            Click(window, "StartupUpdateContinue");
            Assert.Equal("Continue", await Decision(review));
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task BusyStateKeepsNotesAndRejectsActionsUntilTheOperationFinishes() => await session.Dispatch(async () =>
    {
        var window = new EditorStartupWindow(); window.Show();
        try
        {
            Task review = Review(window, new("0.1.9", PlainNotes), canUpgrade: true);
            Call(window, "SetUpdateBusy", true);
            Call(window, "SetUpdateMessage", "Installer prepared");
            Assert.True(Control<LoadingDots>(window, "StartupDots").IsActive);
            Assert.Equal("Installer prepared", Control<TextBlock>(window, "StartupUpdateMessage").Text);
            Assert.Equal(PlainNotes, Control<SelectableTextBlock>(window, "StartupUpdateNotes").Text);
            foreach (string action in new[] { "StartupUpdateContinue", "StartupUpdateSkip", "StartupUpdateUpgrade" })
            {
                Assert.False(Control<Button>(window, action).IsEnabled);
                Click(window, action);
            }
            Assert.False(review.IsCompleted);
            Call(window, "SetUpdateBusy", false);
            Assert.False(Control<LoadingDots>(window, "StartupDots").IsActive);
            Assert.True(Control<Button>(window, "StartupUpdateUpgrade").IsEnabled);
            Click(window, "StartupUpdateContinue");
            Assert.Equal("Continue", await Decision(review));
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task CancellingTheTokenCancelsOnlyThatOfferAndAllowsAnotherReview() => await session.Dispatch(async () =>
    {
        var window = new EditorStartupWindow(); window.Show();
        using var cancellation = new CancellationTokenSource();
        try
        {
            Task review = Review(window, new("0.1.9", PlainNotes), canUpgrade: true, cancellation.Token);
            await Task.Run(cancellation.Cancel, TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => review.WaitAsync(CompletionTimeout, TestContext.Current.CancellationToken));
            Assert.True(review.IsCanceled);
            Call(window, "ResetUpdateSurface");
            Task next = Review(window, new("0.1.10", PlainNotes), canUpgrade: true);
            Click(window, "StartupUpdateSkip");
            Assert.Equal("SkipVersion", await Decision(next));
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ClosingStartupCancelsThePendingChoice() => await session.Dispatch(async () =>
    {
        var window = new EditorStartupWindow(); window.Show();
        try
        {
            Task review = Review(window, new("0.1.9", PlainNotes), canUpgrade: true);
            window.Close();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => review.WaitAsync(CompletionTimeout, TestContext.Current.CancellationToken));
            Assert.True(review.IsCanceled);
            Assert.False(window.IsVisible);
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task StartupFailureReplacesTheOfferAndRestoresTheOriginalGeometry() => await session.Dispatch(async () =>
    {
        var window = new EditorStartupWindow(); window.Show();
        try
        {
            Task review = Review(window, new("0.1.9", PlainNotes), canUpgrade: true);
            Call(window, "ShowFailure", new InvalidOperationException("Startup failure"));
            Assert.Equal("Continue", await Decision(review));
            Assert.Equal(440d, window.Width);
            Assert.Equal(284d, window.Height);
            Assert.False(Control<Grid>(window, "StartupUpdateContent").IsVisible);
            Assert.False(Control<StackPanel>(window, "BrandContent").IsVisible);
            Assert.True(Control<StackPanel>(window, "FailureContent").IsVisible);
            Assert.False(Control<LoadingDots>(window, "StartupDots").IsActive);
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task LanguageChangesRefreshTheVersionAndMissingNotesWithoutReplacingTheOffer() => await session.Dispatch(async () =>
    {
        string previousLanguage = EditorLocalization.CurrentLanguage;
        var window = new EditorStartupWindow(); window.Show();
        try
        {
            Task review = Review(window, new("0.1.9", ""), canUpgrade: false);
            foreach (string language in new[] { "zh-CN", "en", "ja" })
            {
                EditorLocalization.SetLanguage(language);
                Assert.Equal(EditorLocalization.Text("Mda.Text.Update.Title"), Control<TextBlock>(window, "StartupUpdateTitle").Text);
                Assert.Equal(EditorLocalization.Format("Mda.Text.Update.Version", "0.1.9"), Control<TextBlock>(window, "StartupUpdateVersion").Text);
                Assert.Equal(EditorLocalization.Text("Mda.Text.Update.NotesUnavailable"), Control<SelectableTextBlock>(window, "StartupUpdateNotes").Text);
                Assert.Equal(EditorLocalization.Text("Mda.Text.Update.UpgradeUnavailable"), Control<TextBlock>(window, "StartupUpdateMessage").Text);
            }
            Call(window, "ResetUpdateSurface");
            Assert.Equal("Continue", await Decision(review));
            AssertOriginalSurface(window);
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); EditorLocalization.SetLanguage(previousLanguage); }
    }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallationRequiresAnExplicitChoiceAndCompletesOnlyOnce(bool install) => await session.Dispatch(async () =>
    {
        var window = new EditorStartupWindow(); window.Show();
        try
        {
            Task<bool> confirmation = ConfirmInstallation(window, new("0.1.9", PlainNotes));
            Assert.False(confirmation.IsCompleted);
            Assert.True(window.IsVisible);
            Assert.Empty(window.OwnedWindows);
            Assert.True(Control<Grid>(window, "StartupUpdateContent").IsVisible);
            Assert.Equal(PlainNotes, Control<SelectableTextBlock>(window, "StartupUpdateNotes").Text);
            Assert.False(Control<LoadingDots>(window, "StartupDots").IsActive);
            Assert.False(Control<Button>(window, "StartupUpdateSkip").IsVisible);
            Assert.False(Control<Button>(window, "StartupUpdateSkip").IsEnabled);
            Click(window, "StartupUpdateSkip"); // Even a stale routed event cannot bypass consent.
            Assert.False(confirmation.IsCompleted);
            Click(window, install ? "StartupUpdateUpgrade" : "StartupUpdateContinue");
            Click(window, install ? "StartupUpdateContinue" : "StartupUpdateUpgrade");
            Assert.Equal(install, await confirmation.WaitAsync(CompletionTimeout, TestContext.Current.CancellationToken));
            Assert.True(window.IsVisible); // The surface only returns consent; it never launches or exits.
            Assert.False(Control<Button>(window, "StartupUpdateUpgrade").IsEnabled);
            Assert.False(Control<Button>(window, "StartupUpdateContinue").IsEnabled);
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task CancellingInstallationPreventsAnAlreadyQueuedAcceptAndDoesNotCancelTheNextOffer() => await session.Dispatch(async () =>
    {
        var window = new EditorStartupWindow(); window.Show();
        using var cancellation = new CancellationTokenSource();
        try
        {
            Task<bool> confirmation = ConfirmInstallation(window, new("0.1.9", PlainNotes), cancellation.Token);
            cancellation.Cancel();
            Click(window, "StartupUpdateUpgrade"); // Cancellation cleanup has not run on the dispatcher yet.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => confirmation.WaitAsync(CompletionTimeout, TestContext.Current.CancellationToken));
            Assert.True(confirmation.IsCanceled);
            Task next = Review(window, new("0.1.10", PlainNotes), canUpgrade: true);
            Dispatcher.UIThread.RunJobs();
            Assert.False(next.IsCompleted);
            Click(window, "StartupUpdateContinue");
            Assert.Equal("Continue", await Decision(next));
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ClosingStartupCancelsInstallationConsent() => await session.Dispatch(async () =>
    {
        var window = new EditorStartupWindow(); window.Show();
        try
        {
            Task<bool> confirmation = ConfirmInstallation(window, new("0.1.9", PlainNotes));
            window.Close();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => confirmation.WaitAsync(CompletionTimeout, TestContext.Current.CancellationToken));
            Assert.True(confirmation.IsCanceled);
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("zh-CN", "准备安装更新", "更新包已下载并通过文件校验。点击“安装并退出”将关闭 MDA 并启动安装程序。", "安装并退出", "稍后安装")]
    [InlineData("en", "Ready to Install Update", "The update package has been downloaded and verified. Installing now will close MDA and launch the installer.", "Install and Exit", "Install Later")]
    [InlineData("ja", "アップデートのインストール準備完了", "アップデートファイルのダウンロードと検証が完了しました。「インストールして終了」をクリックすると MDA を終了し、インストーラーを起動します。", "インストールして終了", "後で")]
    public async Task InstallationCopyChangesLanguageAndResetRestoresAllOfferActions(string language, string title,
        string message, string install, string later) => await session.Dispatch(async () =>
    {
        string previousLanguage = EditorLocalization.CurrentLanguage;
        EditorLocalization.SetLanguage(language == "en" ? "ja" : "en");
        var window = new EditorStartupWindow(); window.Show();
        try
        {
            Task<bool> confirmation = ConfirmInstallation(window, new("0.1.9", ""));
            EditorLocalization.SetLanguage(language);
            Assert.Equal(title, Control<TextBlock>(window, "StartupUpdateTitle").Text);
            Assert.Equal(title, Control<TextBlock>(window, "StartupStatus").Text);
            Assert.Equal(message, Control<TextBlock>(window, "StartupUpdateMessage").Text);
            Assert.True(Control<TextBlock>(window, "StartupUpdateMessage").IsVisible);
            Assert.Equal(install, Control<Button>(window, "StartupUpdateUpgrade").Content);
            Assert.Equal(later, Control<Button>(window, "StartupUpdateContinue").Content);
            Assert.Equal(EditorLocalization.Text("Mda.Text.Update.NotesUnavailable"), Control<SelectableTextBlock>(window, "StartupUpdateNotes").Text);
            Assert.False(confirmation.IsCompleted);

            Call(window, "ResetUpdateSurface");
            Assert.False(await confirmation.WaitAsync(CompletionTimeout, TestContext.Current.CancellationToken));
            AssertOriginalSurface(window);
            AssertOfferLabels(window);
            Assert.True(Control<Button>(window, "StartupUpdateSkip").IsVisible);
            Task review = Review(window, new("0.1.10", PlainNotes), canUpgrade: false);
            foreach (string offerLanguage in new[] { "zh-CN", "en", "ja" })
            {
                EditorLocalization.SetLanguage(offerLanguage);
                AssertOfferLabels(window);
                Assert.True(Control<Button>(window, "StartupUpdateSkip").IsVisible);
                Assert.True(Control<Button>(window, "StartupUpdateSkip").IsEnabled);
                Assert.False(Control<Button>(window, "StartupUpdateUpgrade").IsEnabled);
                Assert.Equal(EditorLocalization.Text("Mda.Text.Update.UpgradeUnavailable"), Control<TextBlock>(window, "StartupUpdateMessage").Text);
            }
            Click(window, "StartupUpdateSkip");
            Assert.Equal("SkipVersion", await Decision(review));
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); EditorLocalization.SetLanguage(previousLanguage); }
    }, TestContext.Current.CancellationToken);

    private static Task Review(EditorStartupWindow window, StartupUpdateRelease release, bool canUpgrade, CancellationToken token = default) =>
        (Task)Call(window, "ReviewUpdateAsync", release, canUpgrade, token)!;

    private static Task<bool> ConfirmInstallation(EditorStartupWindow window, StartupUpdateRelease release, CancellationToken token = default) =>
        (Task<bool>)Call(window, "ConfirmUpdateInstallationAsync", release, token)!;

    private static object? Call(EditorStartupWindow window, string name, params object?[] args) =>
        typeof(EditorStartupWindow).GetMethod(name, InternalInstance)!.Invoke(window, args);

    private static async Task<string> Decision(Task task)
    {
        await task.WaitAsync(CompletionTimeout, TestContext.Current.CancellationToken);
        return task.GetType().GetProperty("Result")!.GetValue(task)!.ToString()!;
    }

    private static T Control<T>(EditorStartupWindow window, string name) where T : Control => window.FindControl<T>(name)!;
    private static void Click(EditorStartupWindow window, string name) => Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void AssertOriginalSurface(EditorStartupWindow window)
    {
        Assert.Equal(440d, window.Width);
        Assert.Equal(284d, window.Height);
        Assert.False(Control<Grid>(window, "StartupUpdateContent").IsVisible);
        Assert.True(Control<StackPanel>(window, "BrandContent").IsVisible);
        Assert.True(Control<Grid>(window, "NormalRail").IsVisible);
        Assert.False(Control<StackPanel>(window, "FailureContent").IsVisible);
        Assert.True(Control<LoadingDots>(window, "StartupDots").IsActive);
    }

    private static void AssertOfferLabels(EditorStartupWindow window)
    {
        Assert.Equal(EditorLocalization.Text("Mda.Text.Update.Title"), Control<TextBlock>(window, "StartupUpdateTitle").Text);
        Assert.Equal(EditorLocalization.Text("Mda.Text.Update.Continue"), Control<Button>(window, "StartupUpdateContinue").Content);
        Assert.Equal(EditorLocalization.Text("Mda.Text.Update.Skip"), Control<Button>(window, "StartupUpdateSkip").Content);
        Assert.Equal(EditorLocalization.Text("Mda.Text.Update.Upgrade"), Control<Button>(window, "StartupUpdateUpgrade").Content);
    }

    public async ValueTask DisposeAsync()
    {
        // A failed dispatched assertion can resume xUnit on the headless worker. Dispose
        // joins that worker, so move its join to another thread to avoid a self-join.
        await Task.Run(session.Dispose).ConfigureAwait(false);
    }
}
