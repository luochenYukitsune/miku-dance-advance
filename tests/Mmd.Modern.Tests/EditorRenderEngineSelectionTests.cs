using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using System.Text.Json;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EditorRenderEngineSelectionModelTests
{
    [Fact]
    public void ModernIsTheDefaultRegardlessOfCatalogOrder()
    {
        var selection = new EditorRenderEngineSelectionModel([
            new("compatibility", "Compatibility", "Older effects", true),
            new("modern", "Modern", "Ordinary editing", true)]);

        Assert.Equal("modern", selection.SelectedChoice!.Id);
        Assert.True(selection.TryConfirm(out string? id));
        Assert.Equal("modern", id);
    }

    [Fact]
    public void UnavailableOrUnknownChoicesCannotReplaceAValidSelection()
    {
        var selection = new EditorRenderEngineSelectionModel([
            new("modern", "Modern", "Ordinary editing", true),
            new("compatibility", "Compatibility", "Older effects", false, "Component is not installed.")]);

        Assert.False(selection.TrySelect("compatibility"));
        Assert.False(selection.TrySelect("unknown"));
        Assert.Equal("modern", selection.SelectedChoice!.Id);
        Assert.True(selection.TryConfirm(out string? id));
        Assert.Equal("modern", id);
    }

    [Fact]
    public void AnAvailableChoiceCanBeSelectedAndConfirmed()
    {
        var selection = new EditorRenderEngineSelectionModel([
            new("modern", "Modern", "Ordinary editing", true),
            new("compatibility", "Compatibility", "Older effects", true)]);

        Assert.True(selection.TrySelect("compatibility"));
        Assert.True(selection.TryConfirm(out string? id));
        Assert.Equal("compatibility", id);
    }

    [Fact]
    public void MissingDefaultFallsBackOnlyToAnAvailableChoice()
    {
        var selection = new EditorRenderEngineSelectionModel([
            new("unavailable", "Unavailable", "Unavailable engine", false, "Missing component."),
            new("available", "Available", "Available engine", true)]);
        Assert.Equal("available", selection.SelectedChoice!.Id);
    }

    [Fact]
    public void AnEmptyOrUnavailableCatalogHasNoConfirmableSelection()
    {
        foreach (var choices in new EditorRenderEngineChoice[][] { [], [new("modern", "Modern", "Ordinary editing", false, "Missing component.")] })
        {
            var selection = new EditorRenderEngineSelectionModel(choices);
            Assert.False(selection.CanConfirm);
            Assert.Null(selection.SelectedChoice);
            Assert.False(selection.TryConfirm(out string? id));
            Assert.Null(id);
        }
    }

    [Fact]
    public void CatalogIsCopiedAndRejectsAmbiguousOrIncompleteChoices()
    {
        var source = new[] { new EditorRenderEngineChoice("modern", "Modern", "Ordinary editing", true) };
        var selection = new EditorRenderEngineSelectionModel(source);
        source[0] = new("changed", "Changed", "Changed", true);
        Assert.Equal("modern", selection.Choices[0].Id);
        Assert.Throws<ArgumentException>(() => new EditorRenderEngineSelectionModel([
            new("modern", "Modern", "Ordinary editing", true), new("modern", "Other", "Other", true)]));
        Assert.Throws<ArgumentException>(() => new EditorRenderEngineSelectionModel([new("", "Name", "Purpose", true)]));
        Assert.Throws<ArgumentException>(() => new EditorRenderEngineSelectionModel([new("modern", "", "Purpose", true)]));
        Assert.Throws<ArgumentException>(() => new EditorRenderEngineSelectionModel([new("modern", "Name", "", true)]));
        Assert.Throws<ArgumentException>(() => new EditorRenderEngineSelectionModel([new("modern", "Name", "Purpose", false)]));
    }
}

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorRenderEngineSelectionWindowTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("zh-CN")] [InlineData("en")] [InlineData("ja")]
    public async Task LocalizationCatalogsDoNotContainEncodingReplacementText(string language) => await session.Dispatch(() =>
    {
        using var input = AssetLoader.Open(new Uri($"avares://Mmd.Modern.App/Assets/Localization/{language}.json"));
        using var catalog = JsonDocument.Parse(input);
        foreach (var entry in catalog.RootElement.EnumerateObject())
        {
            string text = entry.Value.GetString()!;
            Assert.False(text.Contains("??", StringComparison.Ordinal) || text.Contains('\uFFFD'),
                $"{language}: {entry.Name} contains encoding replacement text.");
        }
        return true;
    }, Token);

    [Theory]
    [InlineData("zh-CN", "原版 MMD 资源",
        "导入 PMM 或新建工程需要读取本机原版 MMD 的色阶贴图。请选择原版 MMD 文件夹；这些资源不随 MDA 提供。已包含完整资源的 PMC 工程可直接打开。",
        "选择原版 MMD 文件夹", "仅打开资源完整的 PMC", "取消")]
    [InlineData("ja", "オリジナル MMD のリソース",
        "PMM の読み込みや新規プロジェクトの作成には、この PC にあるオリジナル MMD のトゥーンテクスチャが必要です。MMD のフォルダーを選択してください。これらのリソースは MDA に同梱されていません。必要なリソースをすべて含む PMC は、そのまま開けます。",
        "MMD フォルダーを選択", "PMC のみ開く", "キャンセル")]
    public async Task ActualClassicStartupResourceDialogRendersLocalizedTextAndOpensPortableOnly(
        string language, string title, string prompt, string chooseLocal, string portableOnly, string cancel)
        => await session.Dispatch(async () =>
    {
        string languageBefore = EditorLocalization.CurrentLanguage;
        EditorLocalization.SetLanguage(language);
        var owner = new Window { Width = 640, Height = 480, RequestedThemeVariant = ThemeVariant.Dark };
        owner.Show();
        try
        {
            // Exercise the real startup picker and common dialog, without selecting a native folder or creating a renderer.
            var operation = EditorClassicToonResourcePicker.ChooseForStartupAsync(owner, Token);
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(owner.OwnedWindows);
            dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(title, dialog.Title);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == prompt && text.IsEffectivelyVisible);
            var buttons = dialog.GetVisualDescendants().OfType<Button>().ToArray();
            Assert.Equal(new[] { chooseLocal, portableOnly, cancel }, buttons.Select(button => button.Content as string).ToArray());
            foreach (string text in new[] { dialog.Title!, prompt }.Concat(buttons.Select(button => (string)button.Content!)))
            {
                Assert.DoesNotContain("??", text);
                Assert.DoesNotContain('\uFFFD', text);
                Assert.DoesNotContain("Mda.Text.", text);
            }
            Assert.All(buttons, button => Assert.True(button.IsEffectivelyVisible && button.IsEffectivelyEnabled));

            // The headless platform uses the existing real CPU Skia backend; retain its actual rendered frame as optional evidence.
            using var frame = dialog.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.True(frame.PixelSize.Width > 0 && frame.PixelSize.Height > 0);
            if (Environment.GetEnvironmentVariable("MDA_TEST_SCREENSHOT_DIRECTORY") is { Length: > 0 } directory)
            {
                Assert.True(Path.IsPathFullyQualified(directory), "MDA_TEST_SCREENSHOT_DIRECTORY must be an absolute directory.");
                Directory.CreateDirectory(directory);
                frame.Save(Path.Combine(directory, $"classic-startup-resources-{language}.png"));
            }

            buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var result = await operation;
            Assert.True(result.Proceed);
            Assert.Null(result.Resources);
            Assert.Empty(owner.OwnedWindows);
        }
        finally
        {
            foreach (var child in owner.OwnedWindows.ToArray()) child.Close(2);
            owner.Close();
            EditorLocalization.SetLanguage(languageBefore);
            Dispatcher.UIThread.RunJobs();
        }
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(20), Token);

    [Fact]
    public async Task ActualConfirmReturnsTheSelectedStableId() => await session.Dispatch(async () =>
    {
        var owner = new Window(); owner.Show();
        var window = (EditorRenderEngineSelectionWindow)EditorRenderEngineSelectionUiProbe.CreateWindow("en", true, compatibilityAvailable: true);
        try
        {
            var result = window.ShowDialog<string?>(owner); window.UpdateLayout();
            var list = window.FindControl<ListBox>("RenderEngineChoices")!;
            list.SelectedIndex = 1;
            window.FindControl<Button>("ConfirmRenderEngineButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("compatibility", await result);
            Assert.Equal("compatibility", window.Result);
        }
        finally { window.Close(); owner.Close(); }
        return true;
    }, Token);

    [Fact]
    public async Task DisabledChoiceCannotBeConfirmedEvenWhenSelectionIsAssignedProgrammatically() => await session.Dispatch(async () =>
    {
        var owner = new Window(); owner.Show();
        var window = new EditorRenderEngineSelectionWindow([
            new("compatibility", "Compatibility", "Older effects", false, "Missing component.")]);
        try
        {
            var result = window.ShowDialog<string?>(owner); window.UpdateLayout();
            var list = window.FindControl<ListBox>("RenderEngineChoices")!;
            Assert.False(((ListBoxItem)list.Items[0]!).IsEnabled);
            list.SelectedIndex = 0;
            var confirm = window.FindControl<Button>("ConfirmRenderEngineButton")!;
            Assert.False(confirm.IsEnabled); Assert.Equal(-1, list.SelectedIndex);
            confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(result.IsCompleted); Assert.Null(window.Result);
            window.Close(); Assert.Null(await result);
        }
        finally { window.Close(); owner.Close(); }
        return true;
    }, Token);

    [Theory]
    [InlineData("cancel")]
    [InlineData("escape")]
    [InlineData("close")]
    public async Task CancelEscapeAndWindowCloseReturnNull(string action) => await session.Dispatch(async () =>
    {
        var owner = new Window(); owner.Show();
        var window = (EditorRenderEngineSelectionWindow)EditorRenderEngineSelectionUiProbe.CreateWindow("en", false);
        try
        {
            var result = window.ShowDialog<string?>(owner); window.UpdateLayout();
            if (action == "cancel") window.FindControl<Button>("CancelRenderEngineButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else if (action == "escape") Press(window, Key.Escape);
            else window.Close();
            Assert.Null(await result); Assert.Null(window.Result);
        }
        finally { window.Close(); owner.Close(); }
        return true;
    }, Token);

    [Fact]
    public async Task KeyboardSkipsUnavailableChoiceAndEnterConfirmsTheNextAvailableChoice() => await session.Dispatch(async () =>
    {
        var owner = new Window(); owner.Show();
        var window = new EditorRenderEngineSelectionWindow([
            new("modern", "Modern", "Ordinary editing", true),
            new("unavailable", "Unavailable", "Older effects", false, "Missing component."),
            new("other", "Other", "Another available engine", true)]);
        try
        {
            var result = window.ShowDialog<string?>(owner); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var list = window.FindControl<ListBox>("RenderEngineChoices")!;
            Assert.True(((ListBoxItem)list.Items[0]!).Focus());
            Press(window, Key.Down);
            Assert.Equal("other", window.SelectionModel.SelectedChoice!.Id);
            Press(window, Key.Enter);
            Assert.Equal("other", await result);
        }
        finally { window.Close(); owner.Close(); }
        return true;
    }, Token);

    [Theory]
    [InlineData("zh-CN", false)] [InlineData("zh-CN", true)]
    [InlineData("en", false)] [InlineData("en", true)]
    [InlineData("ja", false)] [InlineData("ja", true)]
    public async Task ThreeLanguagesKeepActionsAndUnavailableReasonVisibleAtMinimumSize(string language, bool dark) => await session.Dispatch(() =>
    {
        var window = EditorRenderEngineSelectionUiProbe.CreateWindow(language, dark);
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.DoesNotContain("Mda.Text.", window.Title!);
            foreach (var text in window.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible))
                Assert.DoesNotContain("Mda.Text.", text.Text ?? "");
            foreach (string name in new[] { "ConfirmRenderEngineButton", "CancelRenderEngineButton", "RenderEngineSelectionPrompt", "RenderEngineUnavailable1" })
            {
                var control = window.GetVisualDescendants().OfType<Control>().Single(control => control.Name == name);
                var position = control.TranslatePoint(default, window)!.Value;
                Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0, name + " has no space");
                Assert.True(position.X >= 0 && position.Y >= 0 && position.X + control.Bounds.Width <= window.Bounds.Width + .1
                    && position.Y + control.Bounds.Height <= window.Bounds.Height + .1, name + " spills beyond the window");
            }
            Assert.True(window.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "RenderEngineUnavailable1").IsEffectivelyVisible);
        }
        finally { window.Close(); }
        return true;
    }, Token);

    private static void Press(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        if (window.IsVisible) window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}

/// <summary>UI-only fixture: call on an initialized Avalonia UI thread; it creates no editor or graphics device.</summary>
public static class EditorRenderEngineSelectionUiProbe
{
    // A real Skia harness may Show this returned window and capture it using its existing render backend.
    // This helper deliberately neither initializes a backend nor shows/renders the window itself.
    public static Window CreateWindow(string language, bool dark, double width = 480, double height = 420, bool compatibilityAvailable = false)
    {
        EditorLocalization.SetLanguage(language);
        return new EditorRenderEngineSelectionWindow([
            new("modern", "Mda.Text.RenderEngineSelection.ModernName", "Mda.Text.RenderEngineSelection.ModernPurpose", true),
            new("compatibility", "Mda.Text.RenderEngineSelection.CompatibilityName", "Mda.Text.RenderEngineSelection.CompatibilityPurpose",
                compatibilityAvailable, compatibilityAvailable ? null : "Mda.Text.RenderEngineSelection.ComponentNotInstalled")])
        {
            Width = width, Height = height, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };
    }
}
