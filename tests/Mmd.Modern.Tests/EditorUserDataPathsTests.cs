using Avalonia.Headless;
using Mmd.Modern.App;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorUserDataPathsTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mda-user-data-" + Guid.NewGuid().ToString("N"));
    public EditorUserDataPathsTests() => session.Dispatch(() => EditorLocalization.Text("Mda.Text.UserData.Unavailable"),
        TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    [Fact]
    public void UnconfiguredPathsKeepTheLegacyLayoutAndDoNotCreateIt()
    {
        var paths = new EditorUserDataPaths(Path.Combine(directory, "legacy"));
        paths.Initialize();
        Assert.Equal(paths.RootDirectory, paths.ConfigDirectory);
        Assert.Equal(Path.Combine(paths.RootDirectory, "ProjectCache"), paths.ProjectCacheDirectory);
        Assert.Equal(Path.Combine(paths.RootDirectory, "Updates"), paths.UpdateDirectory);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void MigrationCopiesOnlyKnownSettingsAndPluginsAndKeepsRecoveryDiscoverable()
    {
        string legacy = Path.Combine(directory, "legacy"), root = NewRoot("personal");
        var before = new EditorPreferences { Language = "ja", ThemeIndex = 1, RecoveryDirectory = Path.Combine(legacy, "Recovery") };
        before.Save(Path.Combine(legacy, "editor-preferences.json"));
        File.WriteAllText(Path.Combine(legacy, "editor-preview.json"), "preview bytes");
        File.WriteAllText(Path.Combine(legacy, "editor-output.json"), "output bytes");
        Directory.CreateDirectory(Path.Combine(legacy, "Updates"));
        File.WriteAllText(Path.Combine(legacy, "Updates", "skipped-version.txt"), "1.2.3");
        Directory.CreateDirectory(Path.Combine(legacy, "Plugins", "hello"));
        File.WriteAllText(Path.Combine(legacy, "Plugins", "hello", "plugin.dll"), "plugin bytes");
        File.WriteAllText(Path.Combine(legacy, "Plugins", "enabled.json"), "[\"hello\"]");
        Directory.CreateDirectory(before.RecoveryDirectory);
        File.WriteAllText(Path.Combine(before.RecoveryDirectory, "large.pma"), "recovery stays");
        File.WriteAllText(Path.Combine(legacy, "unrelated.pma"), "project stays");
        var paths = new EditorUserDataPaths(legacy, root);
        paths.Initialize();
        var after = EditorPreferences.Load(out var diagnostic, Path.Combine(paths.ConfigDirectory, "editor-preferences.json"));
        Assert.Null(diagnostic);
        Assert.Equal(before.Language, after.Language); Assert.Equal(before.ThemeIndex, after.ThemeIndex);
        Assert.Equal(paths.RecoveryDirectory, after.RecoveryDirectory);
        Assert.Contains(before.RecoveryDirectory, after.RecoverySearchDirectories);
        Assert.Equal("preview bytes", File.ReadAllText(Path.Combine(paths.ConfigDirectory, "editor-preview.json")));
        Assert.Equal("output bytes", File.ReadAllText(Path.Combine(paths.ConfigDirectory, "editor-output.json")));
        Assert.Equal("1.2.3", File.ReadAllText(Path.Combine(paths.UpdateDirectory, "skipped-version.txt")));
        Assert.Equal("plugin bytes", File.ReadAllText(Path.Combine(paths.PluginDirectory, "hello", "plugin.dll")));
        Assert.Equal("[\"hello\"]", File.ReadAllText(Path.Combine(paths.PluginDirectory, "enabled.json")));
        Assert.False(File.Exists(Path.Combine(root, "unrelated.pma")));
        Assert.False(File.Exists(Path.Combine(paths.RecoveryDirectory, "large.pma")));
        Assert.True(File.Exists(Path.Combine(before.RecoveryDirectory, "large.pma")));
        Assert.True(File.Exists(Path.Combine(legacy, "editor-preferences.json")));
        Assert.Contains("Initialized=1", File.ReadAllText(paths.MarkerPath));
        foreach (string path in new[] { paths.ConfigDirectory, paths.PluginDirectory, paths.RecoveryDirectory,
            paths.ProjectCacheDirectory, paths.UpdateDirectory, paths.ExportLogDirectory }) Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void PreviousConfiguredRootKeepsCustomRecoveryAndHistory()
    {
        string legacy = Path.Combine(directory, "legacy"), previous = NewRoot("previous"), root = NewRoot("next");
        Directory.CreateDirectory(Path.Combine(previous, "Config"));
        var before = new EditorPreferences { RecoveryDirectory = Path.Combine(directory, "custom-recovery"),
            RecoverySearchDirectories = [Path.Combine(directory, "earlier-recovery")] };
        before.Save(Path.Combine(previous, "Config", "editor-preferences.json"));
        new EditorUserDataPaths(legacy, root, previous).Initialize();
        var after = EditorPreferences.Load(out var diagnostic, Path.Combine(root, "Config", "editor-preferences.json"));
        Assert.Null(diagnostic);
        Assert.Equal(before.RecoveryDirectory, after.RecoveryDirectory);
        Assert.Contains(before.RecoverySearchDirectories[0], after.RecoverySearchDirectories);
        Assert.Contains(Path.Combine(previous, "Recovery"), after.RecoverySearchDirectories);
    }

    [Fact]
    public void OldSchemaWithoutRecoveryFieldUsesTheSourceDefaultInsteadOfTheProcessDefault()
    {
        string legacy = Path.Combine(directory, "legacy"), previous = NewRoot("previous"), root = NewRoot("next");
        Directory.CreateDirectory(Path.Combine(previous, "Config"));
        File.WriteAllText(Path.Combine(previous, "Config", "editor-preferences.json"), """
            {"schemaVersion":1,"language":"en","invertCameraMouseX":true}
            """);
        new EditorUserDataPaths(legacy, root, previous).Initialize();
        var after = EditorPreferences.Load(out var diagnostic, Path.Combine(root, "Config", "editor-preferences.json"));
        Assert.Null(diagnostic);
        Assert.Equal("en", after.Language); Assert.True(after.InvertCameraMouseX);
        Assert.Equal(Path.Combine(root, "Recovery"), after.RecoveryDirectory);
        Assert.Contains(Path.Combine(previous, "Recovery"), after.RecoverySearchDirectories);
    }

    [Fact]
    public void SelectingTheLegacyRootStillMovesItsConfigurationIntoConfig()
    {
        string root = NewRoot("legacy");
        new EditorPreferences { RecoveryDirectory = Path.Combine(root, "Recovery"), Language = "ja" }
            .Save(Path.Combine(root, "editor-preferences.json"));
        var paths = new EditorUserDataPaths(root, root);
        paths.Initialize();
        var after = EditorPreferences.Load(out var diagnostic, Path.Combine(paths.ConfigDirectory, "editor-preferences.json"));
        Assert.Null(diagnostic); Assert.Equal("ja", after.Language);
        Assert.Equal(paths.RecoveryDirectory, after.RecoveryDirectory);
        Assert.Empty(after.RecoverySearchDirectories);
        Assert.True(File.Exists(Path.Combine(root, "editor-preferences.json")));
    }

    [Fact]
    public void InitializedLegacyRootMigratesCurrentConfigInsteadOfStaleFlatSettings()
    {
        string legacy = NewRoot("legacy"), root = NewRoot("personal");
        new EditorPreferences { Language = "ja", ThemeIndex = 1, RecoveryDirectory = Path.Combine(legacy, "Recovery") }
            .Save(Path.Combine(legacy, "editor-preferences.json"));
        File.WriteAllText(Path.Combine(legacy, "editor-preview.json"), "stale preview");
        File.WriteAllText(Path.Combine(legacy, "editor-output.json"), "stale output");
        var before = new EditorPreferences { Language = "en", ThemeIndex = 0, InvertCameraMouseX = true,
            RecoveryDirectory = Path.Combine(directory, "custom-recovery"),
            RecoverySearchDirectories = [Path.Combine(directory, "earlier-recovery")] };
        before.Save(Path.Combine(legacy, "Config", "editor-preferences.json"));
        File.WriteAllText(Path.Combine(legacy, "Config", "editor-preview.json"), "current preview");
        File.WriteAllText(Path.Combine(legacy, "Config", "editor-output.json"), "current output");
        Directory.CreateDirectory(Path.Combine(legacy, "Config", "Updates"));
        File.WriteAllText(Path.Combine(legacy, "Config", "Updates", "skipped-version.txt"), "1.2.3");
        Directory.CreateDirectory(Path.Combine(legacy, "Plugins", "hello"));
        File.WriteAllText(Path.Combine(legacy, "Plugins", "hello", "plugin.dll"), "plugin bytes");
        File.WriteAllText(Path.Combine(legacy, "Plugins", "enabled.json"), "[\"hello\"]");
        Directory.CreateDirectory(Path.Combine(legacy, "Recovery"));
        File.WriteAllText(Path.Combine(legacy, "Recovery", "large.pma"), "recovery stays");
        File.WriteAllText(Path.Combine(legacy, EditorUserDataPaths.MarkerFileName), EditorUserDataPaths.InitialMarker + "Initialized=1\r\n");
        var sourceFiles = Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);

        var paths = new EditorUserDataPaths(legacy, root, legacy);
        paths.Initialize();

        var after = EditorPreferences.Load(out var diagnostic, Path.Combine(paths.ConfigDirectory, "editor-preferences.json"));
        Assert.Null(diagnostic);
        Assert.Equal(before.Language, after.Language); Assert.Equal(before.ThemeIndex, after.ThemeIndex);
        Assert.True(after.InvertCameraMouseX);
        Assert.Equal(before.RecoveryDirectory, after.RecoveryDirectory);
        Assert.Contains(before.RecoverySearchDirectories[0], after.RecoverySearchDirectories);
        Assert.Contains(Path.Combine(legacy, "Recovery"), after.RecoverySearchDirectories);
        Assert.Equal("current preview", File.ReadAllText(Path.Combine(paths.ConfigDirectory, "editor-preview.json")));
        Assert.Equal("current output", File.ReadAllText(Path.Combine(paths.ConfigDirectory, "editor-output.json")));
        Assert.Equal("1.2.3", File.ReadAllText(Path.Combine(paths.UpdateDirectory, "skipped-version.txt")));
        Assert.Equal("plugin bytes", File.ReadAllText(Path.Combine(paths.PluginDirectory, "hello", "plugin.dll")));
        Assert.Equal("[\"hello\"]", File.ReadAllText(Path.Combine(paths.PluginDirectory, "enabled.json")));
        Assert.False(File.Exists(Path.Combine(paths.RecoveryDirectory, "large.pma")));
        Assert.Equal(sourceFiles.Count, Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories).Count());
        foreach (var source in sourceFiles) Assert.Equal(source.Value, File.ReadAllBytes(source.Key));
    }

    [Fact]
    public void TargetConflictFailsWithoutOverwritingAndCanRetryAfterItIsResolved()
    {
        string legacy = Path.Combine(directory, "legacy"), root = NewRoot("personal");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "editor-preview.json"), "preview");
        File.WriteAllText(Path.Combine(legacy, "editor-output.json"), "output");
        Directory.CreateDirectory(Path.Combine(root, "Config"));
        string conflict = Path.Combine(root, "Config", "editor-output.json");
        File.WriteAllText(conflict, "existing output");
        var paths = new EditorUserDataPaths(legacy, root);
        Assert.ThrowsAny<IOException>(paths.Initialize);
        Assert.Equal("existing output", File.ReadAllText(conflict));
        Assert.DoesNotContain("Initialized=1", File.ReadAllText(paths.MarkerPath));
        File.Delete(conflict);
        paths.Initialize();
        Assert.Equal("output", File.ReadAllText(conflict));
        Assert.Contains("Initialized=1", File.ReadAllText(paths.MarkerPath));
    }

    [Fact]
    public void InitializedRootDoesNotReimportOldFiles()
    {
        string legacy = Path.Combine(directory, "legacy"), root = NewRoot("personal");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "editor-output.json"), "original");
        var paths = new EditorUserDataPaths(legacy, root);
        paths.Initialize();
        File.WriteAllText(Path.Combine(legacy, "editor-output.json"), "old root changed");
        new EditorUserDataPaths(legacy, root).Initialize();
        Assert.Equal("original", File.ReadAllText(Path.Combine(root, "Config", "editor-output.json")));
    }

    [Fact]
    public void MissingOrForeignMarkerAndBlockedDirectoryFailInsteadOfUsingLegacySettings()
    {
        string legacy = Path.Combine(directory, "legacy"), root = Path.Combine(directory, "personal");
        var paths = new EditorUserDataPaths(legacy, root);
        Assert.ThrowsAny<IOException>(paths.Initialize);
        Directory.CreateDirectory(root);
        File.WriteAllText(paths.MarkerPath, "[MDA]\nAppId=other\nLayoutVersion=1\n");
        Assert.ThrowsAny<IOException>(paths.Initialize);
        File.WriteAllText(paths.MarkerPath, EditorUserDataPaths.InitialMarker);
        File.WriteAllText(Path.Combine(root, "Config"), "blocked");
        Assert.ThrowsAny<IOException>(paths.Initialize);
        Assert.False(Directory.Exists(legacy));
    }

    [Fact]
    public void InvalidOldPreferencesFailWithoutPublishingAnInitializedRoot()
    {
        string legacy = Path.Combine(directory, "legacy"), root = NewRoot("personal");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "editor-preferences.json"), "broken json");
        var paths = new EditorUserDataPaths(legacy, root);
        Assert.ThrowsAny<IOException>(paths.Initialize);
        Assert.DoesNotContain("Initialized=1", File.ReadAllText(paths.MarkerPath));
        Assert.False(File.Exists(Path.Combine(root, "Config", "editor-preferences.json")));
    }

    [Fact]
    public void PluginDirectoryLinkIsRejectedWithoutCopyingOutsideFiles()
    {
        string legacy = Path.Combine(directory, "legacy"), root = NewRoot("personal"), outside = Path.Combine(directory, "outside");
        Directory.CreateDirectory(Path.Combine(legacy, "Plugins"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "private.txt"), "must stay");
        string link = Path.Combine(legacy, "Plugins", "linked");
        if (OperatingSystem.IsWindows())
        {
            // Junction creation needs no symbolic-link privilege on an ordinary Windows token.
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "/d", "/c", "mklink", "/J", link, outside }) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            Assert.True(process.WaitForExit(10000));
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, outside);
        try
        {
            var paths = new EditorUserDataPaths(legacy, root);
            Assert.ThrowsAny<IOException>(paths.Initialize);
            Assert.False(File.Exists(Path.Combine(root, "Plugins", "linked", "private.txt")));
            Assert.DoesNotContain("Initialized=1", File.ReadAllText(paths.MarkerPath));
        }
        finally { Directory.Delete(link); }
    }

    private string NewRoot(string name)
    {
        string root = Path.Combine(directory, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, EditorUserDataPaths.MarkerFileName), EditorUserDataPaths.InitialMarker);
        return root;
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Run(session.Dispose).ConfigureAwait(false);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
