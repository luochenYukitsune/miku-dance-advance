using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Mmd.Modern.App;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorAboutWindowTests : IDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RuntimeDetailsInheritTheOwnerThemeAndCloseFromOkOrEscape(bool escape) => await session.Dispatch(async () =>
    {
        string before = EditorLocalization.CurrentLanguage;
        EditorLocalization.SetLanguage("en");
        var owner = new Window { RequestedThemeVariant = ThemeVariant.Light }; owner.Show();
        var window = new EditorAboutWindow(new("0.9.1-preview", "123456789abc", ".NET test runtime"), AppContext.BaseDirectory);
        try
        {
            Task result = window.ShowDialog(owner); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeVariant.Light, window.ActualThemeVariant);
            Assert.Equal("Version 0.9.1-preview", window.FindControl<TextBlock>("AboutVersion")!.Text);
            Assert.Equal("Build: 123456789abc", window.FindControl<TextBlock>("AboutBuild")!.Text);
            Assert.Equal(".NET test runtime", window.FindControl<TextBlock>("AboutRuntime")!.Text);
            Assert.Contains("luochen Yukitsune", window.FindControl<TextBlock>("AboutAuthor")!.Text!);
            Assert.True(window.FindControl<ScrollViewer>("AboutOverview")!.IsVisible);
            Assert.False(window.FindControl<Grid>("AboutLicensesPane")!.IsVisible);

            EditorLocalization.SetLanguage("ja"); Dispatcher.UIThread.RunJobs();
            Assert.Equal(EditorLocalization.Format("Mda.Text.About.Version", "0.9.1-preview"),
                window.FindControl<TextBlock>("AboutVersion")!.Text);
            if (escape) window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            else window.FindControl<Button>("AboutOkButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await result;
            Assert.False(window.IsVisible);
        }
        finally { window.Close(); owner.Close(); EditorLocalization.SetLanguage(before); }
        return true;
    }, Token);

    [Theory]
    [InlineData("zh-CN")] [InlineData("en")] [InlineData("ja")]
    public async Task ShippedWordLicenseOpensTheOriginalWithoutReadingBinaryAsText(string language) => await session.Dispatch(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "mda-about-original-" + Guid.NewGuid().ToString("N"));
        string licenseDirectory = Path.Combine(directory, "tools", "upscaling", "licenses", "vc-runtime");
        Directory.CreateDirectory(licenseDirectory);
        string original = Path.Combine(licenseDirectory, "Visual-C-V14-Runtime-2026-License.docx");
        byte[] originalBytes = [0x50, 0x4b, 0x03, 0x04, 0xff, 0x00, 0xfe];
        File.WriteAllBytes(original, originalBytes);
        File.WriteAllBytes(Path.Combine(licenseDirectory, "LICENSE-other.docx"), originalBytes);
        File.WriteAllText(Path.Combine(directory, "THIRD_PARTY_NOTICES.md"), "Installation notices");
        var opened = new List<string>();
        bool failLaunch = false;
        string before = EditorLocalization.CurrentLanguage; EditorLocalization.SetLanguage(language);
        var window = new EditorAboutWindow(new("1.2.3", null, ".NET test runtime"), directory, path =>
        {
            if (failLaunch) throw new System.ComponentModel.Win32Exception("No document viewer");
            opened.Add(path);
        });
        try
        {
            window.Show(); window.UpdateLayout();
            var open = window.FindControl<Button>("AboutOpenOriginalButton")!;
            Assert.False(open.IsVisible);
            window.FindControl<Button>("AboutLicensesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var files = window.FindControl<ComboBox>("AboutLicenseFiles")!;
            var items = files.Items.OfType<ComboBoxItem>().ToArray();
            var target = Assert.Single(items, item => ((TextBlock)item.Content!).Text ==
                "tools/upscaling/licenses/vc-runtime/Visual-C-V14-Runtime-2026-License.docx");
            Assert.DoesNotContain(items, item => ((TextBlock)item.Content!).Text!.EndsWith("LICENSE-other.docx", StringComparison.OrdinalIgnoreCase));
            files.SelectedItem = target;
            Assert.True(open.IsVisible);
            Assert.Equal(EditorLocalization.Format("Mda.Text.About.OriginalDocument", original),
                window.FindControl<SelectableTextBlock>("AboutLicenseText")!.Text);
            Assert.Equal(EditorLocalization.Text("Mda.Text.About.OpenOriginal"), open.Content);
            Assert.Empty(opened);
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new[] { original }, opened);
            Assert.Equal(originalBytes, File.ReadAllBytes(original));
            failLaunch = true;
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(EditorLocalization.Format("Mda.Text.About.LicenseReadFailed", "No document viewer"),
                window.FindControl<SelectableTextBlock>("AboutLicenseText")!.Text);
            failLaunch = false;

            EditorLocalization.SetLanguage(language == "en" ? "ja" : "en");
            Assert.Equal(EditorLocalization.Format("Mda.Text.About.OriginalDocument", original),
                window.FindControl<SelectableTextBlock>("AboutLicenseText")!.Text);
            File.Delete(original);
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            string errorText = window.FindControl<SelectableTextBlock>("AboutLicenseText")!.Text!;
            Assert.StartsWith(EditorLocalization.Text("Mda.Text.About.LicenseReadFailed").Split("{0}")[0], errorText);
            Assert.Equal(new[] { original }, opened);

            files.SelectedItem = items.First(item => !ReferenceEquals(item, target));
            Assert.False(open.IsVisible);
            Assert.Equal("Installation notices", window.FindControl<SelectableTextBlock>("AboutLicenseText")!.Text);
            files.SelectedItem = target;
            Assert.StartsWith(EditorLocalization.Text("Mda.Text.About.LicenseReadFailed").Split("{0}")[0],
                window.FindControl<SelectableTextBlock>("AboutLicenseText")!.Text!);
        }
        finally { window.Close(); EditorLocalization.SetLanguage(before); Directory.Delete(directory, true); }
        return true;
    }, Token);

    [Fact]
    public async Task LocalLicenseReaderShowsTheCompleteFileAndHandlesAFileRemovedAfterIndexing() => await session.Dispatch(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "mda-about-notices-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "licenses", "example"));
        string license = Path.Combine(directory, "licenses", "example", "LICENSE.txt");
        string fullText = string.Join('\n', Enumerable.Range(0, 512).Select(index => "Full license line " + index)) + "\nEND OF TERMS";
        File.WriteAllText(license, fullText);
        File.WriteAllText(Path.Combine(directory, "THIRD_PARTY_NOTICES.md"), "Installation notices");
        File.WriteAllText(Path.Combine(directory, "licenses", "package-notices.json"), "{}");
        string before = EditorLocalization.CurrentLanguage; EditorLocalization.SetLanguage("en");
        var window = new EditorAboutWindow(new("1.2.3", null, ".NET test runtime"), directory);
        try
        {
            window.Show(); window.UpdateLayout();
            Assert.False(window.FindControl<TextBlock>("AboutBuild")!.IsVisible);
            window.FindControl<Button>("AboutLicensesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var files = window.FindControl<ComboBox>("AboutLicenseFiles")!;
            var items = files.Items.OfType<ComboBoxItem>().ToArray();
            var target = Assert.Single(items, item => ((TextBlock)item.Content!).Text == "licenses/example/LICENSE.txt");
            Assert.DoesNotContain(items, item => ((TextBlock)item.Content!).Text!.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
            files.SelectedItem = target;
            Assert.Equal(fullText, window.FindControl<SelectableTextBlock>("AboutLicenseText")!.Text);
            Assert.True(window.FindControl<Grid>("AboutLicensesPane")!.IsVisible);
            Assert.True(window.FindControl<Button>("AboutOkButton")!.IsEffectivelyVisible);
            File.Delete(license); files.SelectedItem = items.First(item => !ReferenceEquals(item, target)); files.SelectedItem = target;
            Assert.Contains("Could not read or open the license file:", window.FindControl<SelectableTextBlock>("AboutLicenseText")!.Text!);
            window.FindControl<Button>("AboutLicensesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.FindControl<ScrollViewer>("AboutOverview")!.IsVisible);
            Assert.False(window.FindControl<Grid>("AboutLicensesPane")!.IsVisible);
        }
        finally { window.Close(); EditorLocalization.SetLanguage(before); Directory.Delete(directory, true); }
        return true;
    }, Token);

    public void Dispose() => session.Dispose();
}
