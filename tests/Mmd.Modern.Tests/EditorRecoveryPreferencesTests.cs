using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorRecoveryPreferencesTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mda-recovery-preferences-" + Guid.NewGuid().ToString("N"));

    public EditorRecoveryPreferencesTests()
    {
        // Plain storage-validation tests also use translated diagnostics. Load the
        // real catalog inside the existing headless application's asset scope first.
        session.Dispatch(() => EditorLocalization.Text("Mda.Text.RecoveryPreferences.InvalidBudget"),
            TestContext.Current.CancellationToken).GetAwaiter().GetResult();
    }

    [Fact]
    public void LegacySchemaOneKeepsExistingSettingsAndUsesRecoveryDefaults()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        File.WriteAllText(path, """
            {"schemaVersion":1,"themeIndex":1,"language":"ja","showWorldGuides":false,"invertCameraMouseX":true,"pmxMaxBytes":12345}
            """);
        var preferences = EditorPreferences.Load(out var diagnostic, path);
        Assert.Null(diagnostic);
        Assert.Equal(1, preferences.SchemaVersion);
        Assert.Equal(1, preferences.ThemeIndex);
        Assert.Equal("ja", preferences.Language);
        Assert.False(preferences.ShowWorldGuides);
        Assert.True(preferences.InvertCameraMouseX);
        Assert.Equal(12345L, preferences.PmxMaxBytes);
        Assert.True(preferences.AutoSaveEnabled);
        Assert.Equal(3, preferences.AutoSaveIntervalMinutes);
        Assert.Equal(5, preferences.RecoveryMaxVersions);
        Assert.Equal(5 * EditorPreferences.Gibibyte, preferences.RecoveryMaxTotalBytes);
        Assert.Equal(Path.Combine(EditorPreferences.DirectoryPath, "Recovery"), preferences.RecoveryDirectory);
        Assert.Empty(preferences.RecoverySearchDirectories);
    }

    [Fact]
    public void DirectoryChangesPersistAllPriorRootsAndDeduplicateEquivalentPaths()
    {
        string first = Path.Combine(directory, "A"), second = Path.Combine(directory, "B"), third = Path.Combine(directory, "C");
        var a = new EditorPreferences { RecoveryDirectory = first };
        var b = (a with { RecoveryDirectory = second }).WithRecoveryDirectoryHistory(a);
        var c = (b with { RecoveryDirectory = third }).WithRecoveryDirectoryHistory(b);
        var back = (c with { RecoveryDirectory = first + Path.DirectorySeparatorChar }).WithRecoveryDirectoryHistory(c);
        Assert.Equal(new[] { first }, b.RecoverySearchDirectories);
        Assert.Equal(new[] { second, first }, c.RecoverySearchDirectories);
        Assert.Equal(new[] { third, second }, back.RecoverySearchDirectories);
        Assert.Empty(a.RecoverySearchDirectories);
        string path = Path.Combine(directory, "history.json");
        back.Save(path);
        var loaded = EditorPreferences.Load(out var diagnostic, path);
        Assert.Null(diagnostic);
        Assert.Equal(back.RecoverySearchDirectories, loaded.RecoverySearchDirectories);
        Assert.False(Directory.Exists(first)); Assert.False(Directory.Exists(second)); Assert.False(Directory.Exists(third));
    }

    [Fact]
    public void InvalidOrOversizedDirectoryHistoryCannotReplaceSavedPreferences()
    {
        string path = Path.Combine(directory, "history.json");
        var preferences = new EditorPreferences { RecoveryDirectory = Path.Combine(directory, "current") };
        preferences.Save(path); byte[] before = File.ReadAllBytes(path);
        var relative = preferences with { RecoverySearchDirectories = ["relative"] };
        Assert.ThrowsAny<ArgumentException>(() => relative.Save(path));
        var excessive = preferences with
        {
            RecoverySearchDirectories = Enumerable.Range(0, 129).Select(i => Path.Combine(directory, "old" + i)).ToImmutableArray()
        };
        Assert.ThrowsAny<ArgumentException>(() => excessive.Save(path));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false, 1, 1, 1L)]
    [InlineData(true, 60, 20, 107374182400L)]
    public void RecoverySettingsRoundTripWithoutLosingExactByteBudgets(bool enabled, int minutes, int versions, long bytes)
    {
        string path = Path.Combine(directory, "preferences.json");
        var preferences = new EditorPreferences
        {
            AutoSaveEnabled = enabled,
            AutoSaveIntervalMinutes = minutes,
            RecoveryMaxVersions = versions,
            RecoveryMaxTotalBytes = bytes,
            RecoveryDirectory = Path.Combine(directory, "恢复 copies", "未命名工程")
        };
        preferences.Save(path);
        var loaded = EditorPreferences.Load(out var diagnostic, path);
        Assert.Null(diagnostic);
        Assert.Equal(preferences, loaded);
        Assert.False(Directory.Exists(preferences.RecoveryDirectory));
    }

    [Theory]
    [InlineData("autoSaveIntervalMinutes", "0")]
    [InlineData("autoSaveIntervalMinutes", "61")]
    [InlineData("recoveryMaxVersions", "0")]
    [InlineData("recoveryMaxVersions", "21")]
    [InlineData("recoveryMaxTotalBytes", "0")]
    [InlineData("recoveryMaxTotalBytes", "107374182401")]
    [InlineData("recoveryDirectory", "null")]
    [InlineData("recoveryDirectory", "\"\"")]
    [InlineData("recoveryDirectory", "\"relative/recovery\"")]
    [InlineData("unknownRecoveryOption", "true")]
    public void InvalidOrUnknownRecoveryValuesLeaveTheStoredFileUntouched(string property, string value)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        string json = "{\"schemaVersion\":1,\"" + property + "\":" + value + "}";
        File.WriteAllText(path, json);
        var loaded = EditorPreferences.Load(out var diagnostic, path);
        Assert.NotNull(diagnostic);
        Assert.Equal(new EditorPreferences(), loaded);
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void DuplicateRecoveryPropertyRemainsRejected()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        const string json = "{\"schemaVersion\":1,\"autoSaveEnabled\":true,\"autoSaveEnabled\":false}";
        File.WriteAllText(path, json);
        _ = EditorPreferences.Load(out var diagnostic, path);
        Assert.NotNull(diagnostic);
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative/recovery")]
    [InlineData("\\\\server\\share\\Recovery")]
    [InlineData("//server/share/Recovery")]
    [InlineData("\\\\?\\C:\\Recovery")]
    public void RecoveryDirectoryMustBeAnAbsoluteLocalDirectory(string path)
        => Assert.ThrowsAny<ArgumentException>(() => (new EditorPreferences { RecoveryDirectory = path }).Validate());

    [Fact]
    public void RecoveryDirectoryCannotNameAnExistingFile()
    {
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "a-file.pma");
        File.WriteAllText(file, "original");
        Assert.ThrowsAny<ArgumentException>(() => (new EditorPreferences { RecoveryDirectory = file }).Validate());
        Assert.Equal("original", File.ReadAllText(file));
    }

    [Theory]
    [InlineData(13421772800L, "12")]
    [InlineData(1L, "0")]
    public async Task CapacityInputsDisplayWholeGibibytesWithoutChangingUneditedByteLimits(long pmxBytes, string pmxText) => await session.Dispatch(async () =>
    {
        var source = new EditorPreferences
        {
            RecoveryDirectory = Path.Combine(directory, "capacity"),
            PmxMaxBytes = pmxBytes,
            PmaPackageMaxBytes = 50 * EditorPreferences.Gibibyte + 321,
            PmaExpandedMaxBytes = 50 * EditorPreferences.Gibibyte + 987,
            PmaEntryMaxBytes = 10 * EditorPreferences.Gibibyte + 789
        };
        var owner = new Window(); owner.Show();
        var dialog = new EditorPreferencesWindow(source);
        try
        {
            var result = dialog.ShowDialog<EditorPreferences?>(owner);
            dialog.FindControl<TabControl>("PreferencesTabs")!.SelectedIndex = 2;
            foreach (var (name, text, bytes) in new[]
            {
                ("PreferencesPmxBudgetBox", pmxText, source.PmxMaxBytes),
                ("PreferencesPmaPackageBudgetBox", "50", source.PmaPackageMaxBytes),
                ("PreferencesPmaExpandedBudgetBox", "50", source.PmaExpandedMaxBytes),
                ("PreferencesPmaEntryBudgetBox", "10", source.PmaEntryMaxBytes)
            })
            {
                var input = dialog.FindControl<EditorNumericUpDown>(name)!;
                Assert.Equal(text, input.Text);
                Assert.Equal((decimal)bytes / EditorPreferences.Gibibyte, input.Value);
                Assert.True(input.IsInputValid);
                input.Focus(); dialog.FindControl<Button>("ApplyPreferencesButton")!.Focus();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal((decimal)bytes / EditorPreferences.Gibibyte, input.Value);
            }
            dialog.FindControl<Button>("ApplyPreferencesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(result.IsCompletedSuccessfully);
            Assert.Equal(source, await result);

            var edited = new EditorPreferencesWindow(source);
            var editedResult = edited.ShowDialog<EditorPreferences?>(owner);
            edited.FindControl<EditorNumericUpDown>("PreferencesPmxBudgetBox")!.Text = "25";
            edited.FindControl<Button>("ApplyPreferencesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(editedResult.IsCompletedSuccessfully);
            Assert.Equal(source with { PmxMaxBytes = 25 * EditorPreferences.Gibibyte }, await editedResult);
            return true;
        }
        finally { foreach (var child in owner.OwnedWindows.ToArray()) child.Close(null); owner.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(1L)]
    [InlineData(123456789L)]
    public async Task ApplyReturnsDetachedRecoverySettingsAndCancelPreservesEverySourceValue(long exactBudget) => await session.Dispatch(async () =>
    {
        var source = new EditorPreferences { RecoveryDirectory = Path.Combine(directory, "before"), RecoveryMaxTotalBytes = exactBudget };
        var owner = new Window(); owner.Show();
        try
        {
            var dialog = new EditorPreferencesWindow(source);
            var result = dialog.ShowDialog<EditorPreferences?>(owner);
            dialog.FindControl<CheckBox>("RecoveryEnabledBox")!.IsChecked = false;
            dialog.FindControl<NumericUpDown>("RecoveryIntervalBox")!.Value = 7;
            dialog.FindControl<NumericUpDown>("RecoveryVersionsBox")!.Value = 9;
            dialog.FindControl<TextBox>("RecoveryDirectoryBox")!.Text = Path.Combine(directory, "after");
            dialog.FindControl<Button>("ApplyPreferencesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(result.IsCompletedSuccessfully);
            var applied = await result;
            Assert.NotNull(applied);
            Assert.False(applied.AutoSaveEnabled);
            Assert.Equal(7, applied.AutoSaveIntervalMinutes);
            Assert.Equal(9, applied.RecoveryMaxVersions);
            Assert.Equal(source.RecoveryMaxTotalBytes, applied.RecoveryMaxTotalBytes);
            Assert.Equal(Path.Combine(directory, "after"), applied.RecoveryDirectory);
            Assert.Equal(new EditorPreferences { RecoveryDirectory = Path.Combine(directory, "before"), RecoveryMaxTotalBytes = exactBudget }, source);

            var cancelled = new EditorPreferencesWindow(source);
            var cancelledResult = cancelled.ShowDialog<EditorPreferences?>(owner);
            cancelled.FindControl<NumericUpDown>("RecoveryBudgetBox")!.Value = 12.5m;
            cancelled.FindControl<TextBox>("RecoveryDirectoryBox")!.Text = Path.Combine(directory, "cancelled");
            cancelled.Close(null); Dispatcher.UIThread.RunJobs();
            Assert.True(cancelledResult.IsCompletedSuccessfully);
            Assert.Null(await cancelledResult);
            Assert.Equal(exactBudget, source.RecoveryMaxTotalBytes);
            Assert.Equal(Path.Combine(directory, "before"), source.RecoveryDirectory);
            return true;
        }
        finally { owner.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("RecoveryIntervalBox", "abc")]
    [InlineData("RecoveryIntervalBox", "1.5")]
    [InlineData("RecoveryVersionsBox", "2.5")]
    [InlineData("RecoveryBudgetBox", "101")]
    public async Task InvalidRecoveryInputCannotApplyTheLastValidValue(string controlName, string text) => await session.Dispatch(() =>
    {
        var owner = new Window(); owner.Show();
        var dialog = new EditorPreferencesWindow(new EditorPreferences { RecoveryDirectory = Path.Combine(directory, "valid") });
        try
        {
            var result = dialog.ShowDialog<EditorPreferences?>(owner);
            dialog.FindControl<TabControl>("PreferencesTabs")!.SelectedItem = dialog.FindControl<TabItem>("RecoveryTab");
            var input = dialog.FindControl<EditorNumericUpDown>(controlName)!;
            input.Text = text;
            dialog.FindControl<Button>("ApplyPreferencesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(result.IsCompleted);
            Assert.Equal(text, input.Text);
            Assert.True(dialog.FindControl<TextBlock>("PreferencesValidationMessage")!.IsVisible);
            return true;
        }
        finally { dialog.Close(null); owner.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task SwitchingTabsCannotHideAnInvalidRecoveryInputFromApply() => await session.Dispatch(() =>
    {
        var owner = new Window(); owner.Show();
        var dialog = new EditorPreferencesWindow(new EditorPreferences { RecoveryDirectory = Path.Combine(directory, "valid") });
        try
        {
            var result = dialog.ShowDialog<EditorPreferences?>(owner);
            var tabs = dialog.FindControl<TabControl>("PreferencesTabs")!;
            tabs.SelectedItem = dialog.FindControl<TabItem>("RecoveryTab");
            dialog.FindControl<EditorNumericUpDown>("RecoveryIntervalBox")!.Text = "invalid";
            tabs.SelectedIndex = 0;
            dialog.FindControl<Button>("ApplyPreferencesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(result.IsCompleted);
            Assert.Equal("invalid", dialog.FindControl<EditorNumericUpDown>("RecoveryIntervalBox")!.Text);
            Assert.True(dialog.FindControl<TextBlock>("PreferencesValidationMessage")!.IsVisible);
            return true;
        }
        finally { dialog.Close(null); owner.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task InvalidRecoveryDirectoryStaysEditableAndDoesNotCloseDialog() => await session.Dispatch(() =>
    {
        var source = new EditorPreferences { RecoveryDirectory = Path.Combine(directory, "valid") };
        var owner = new Window(); owner.Show();
        var dialog = new EditorPreferencesWindow(source);
        try
        {
            var result = dialog.ShowDialog<EditorPreferences?>(owner);
            dialog.FindControl<TextBox>("RecoveryDirectoryBox")!.Text = "relative/recovery";
            dialog.FindControl<Button>("ApplyPreferencesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(result.IsCompleted);
            Assert.Equal("relative/recovery", dialog.FindControl<TextBox>("RecoveryDirectoryBox")!.Text);
            Assert.True(dialog.FindControl<TextBlock>("PreferencesValidationMessage")!.IsVisible);
            Assert.Equal(Path.Combine(directory, "valid"), source.RecoveryDirectory);
            return true;
        }
        finally { dialog.Close(null); owner.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        try { await Task.Run(session.Dispose).ConfigureAwait(false); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
