using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Mmd.Modern.Plugins.Contracts;
using Xunit;

namespace Mmd.Modern.Plugins.Tests;

public sealed class PluginPackageInstallTests
{
    [Theory(Timeout = 15_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_example_zip_installs_from_root_or_wrapper_and_starts_disabled(bool wrapper)
    {
        var workspace = new InstallWorkspace();
        var source = Path.Combine(AppContext.BaseDirectory, "ExamplePlugin");
        var archive = workspace.CreateArchive(source, wrapper ? "Mda.HelloPlugin-1.0/" : "");

        var result = await PluginPackageInstaller.InstallAsync(archive, workspace.Installation,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal("mda.hello", result.PluginId);
        Assert.Equal(Path.Combine(workspace.Installation, "mda.hello"), result.Directory);
        Assert.Equal(File.ReadAllBytes(Path.Combine(source, "Mda.HelloPlugin.dll")),
            File.ReadAllBytes(Path.Combine(result.Directory!, "Mda.HelloPlugin.dll")));
        Assert.False(File.Exists(Path.Combine(result.Directory!, Path.GetFileName(archive))));
        Assert.Equal("mda.hello", Path.GetFileName(Assert.Single(Directory.EnumerateDirectories(workspace.Installation))));
        await using var manager = CreateManager();
        Assert.True((await manager.RefreshAsync([workspace.Installation], TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(PluginState.Disabled, Assert.Single(manager.Plugins).State);
        Assert.Empty(manager.Extensions.GetExtensions<IPluginCommand>("editor.commands"));
        Assert.True((await manager.EnableAsync("mda.hello", TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal("hello", Assert.Single(manager.Extensions.GetExtensions<IPluginCommand>("editor.commands")).RegistrationId);
        Assert.True((await manager.DisableAsync("mda.hello", TestContext.Current.CancellationToken)).Succeeded);
        workspace.AssertNoStagingDirectories();
    }

    [Fact(Timeout = 15_000)]
    public async Task Installation_and_discovery_do_not_construct_plugin_code()
    {
        var workspace = new InstallWorkspace();
        var archive = workspace.CreateFixtureArchive();

        var result = await PluginPackageInstaller.InstallAsync(archive, workspace.Installation,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Diagnostic);
        await using var manager = CreateManager();
        Assert.True((await manager.RefreshAsync([workspace.Installation], TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(PluginState.Disabled, Assert.Single(manager.Plugins).State);
        Assert.False(File.Exists(Path.Combine(result.Directory!, "constructed.txt")));
        Assert.False(File.Exists(Path.Combine(result.Directory!, "started.txt")));
        Assert.Empty(manager.Extensions.GetExtensions<IPluginCommand>("editor.commands"));
        workspace.AssertNoStagingDirectories();
    }

    [Theory(Timeout = 15_000)]
    [InlineData("../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("file.txt:stream")]
    [InlineData("inner/../../outside.txt")]
    [InlineData("..\\outside.txt")]
    public async Task Unsafe_relative_paths_are_rejected_without_writing_outside_the_package(string entryName)
    {
        var workspace = new InstallWorkspace();
        var archive = workspace.CreateFixtureArchive(extraEntry: entryName);

        var result = await PluginPackageInstaller.InstallAsync(archive, workspace.Installation,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.False(Directory.Exists(workspace.Installation));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "outside.txt")));
        workspace.AssertNoStagingDirectories();
    }

    [Fact(Timeout = 15_000)]
    public async Task Duplicate_paths_and_symbolic_link_entries_are_rejected()
    {
        var workspace = new InstallWorkspace();
        var duplicate = workspace.CreateFixtureArchive(extraEntry: "asset.txt");
        using (var archive = ZipFile.Open(duplicate, ZipArchiveMode.Update))
            WriteEntry(archive, "ASSET.TXT", "duplicate");
        var linked = workspace.CreateFixtureArchive();
        using (var archive = ZipFile.Open(linked, ZipArchiveMode.Update))
        {
            var entry = archive.CreateEntry("linked.txt");
            entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var writer = new StreamWriter(entry.Open());
            writer.Write("../outside.txt");
        }

        Assert.False((await PluginPackageInstaller.InstallAsync(duplicate, workspace.Installation,
            TestContext.Current.CancellationToken)).Succeeded);
        Assert.False((await PluginPackageInstaller.InstallAsync(linked, workspace.Installation,
            TestContext.Current.CancellationToken)).Succeeded);
        Assert.False(Directory.Exists(workspace.Installation));
        workspace.AssertNoStagingDirectories();
    }

    [Fact(Timeout = 15_000)]
    public async Task Existing_plugin_is_preserved_and_a_second_install_is_rejected()
    {
        var workspace = new InstallWorkspace();
        var archive = workspace.CreateFixtureArchive();
        var first = await PluginPackageInstaller.InstallAsync(archive, workspace.Installation,
            TestContext.Current.CancellationToken);
        Assert.True(first.Succeeded, first.Diagnostic);
        var manifestPath = Path.Combine(first.Directory!, "mda-plugin.json");
        var originalManifest = File.ReadAllBytes(manifestPath);
        var sentinel = Path.Combine(first.Directory!, "keep.txt");
        File.WriteAllText(sentinel, "existing-plugin-owned-content");

        var second = await PluginPackageInstaller.InstallAsync(archive, workspace.Installation,
            TestContext.Current.CancellationToken);

        Assert.False(second.Succeeded);
        Assert.Contains("already exists", second.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalManifest, File.ReadAllBytes(manifestPath));
        Assert.Equal("existing-plugin-owned-content", File.ReadAllText(sentinel));
        Assert.Single(Directory.EnumerateDirectories(workspace.Installation));
        workspace.AssertNoStagingDirectories();
    }

    [Fact(Timeout = 15_000)]
    public async Task A_plugin_id_reserved_by_another_discovery_root_is_not_installed()
    {
        var workspace = new InstallWorkspace();
        var otherPlugin = Path.Combine(workspace.Root, "application-plugins", "install-probe");
        Directory.CreateDirectory(otherPlugin);
        var sentinel = Path.Combine(otherPlugin, "keep.txt");
        File.WriteAllText(sentinel, "separate-root-owned-content");
        var archive = workspace.CreateFixtureArchive();

        var result = await PluginPackageInstaller.InstallAsync(archive, workspace.Installation,
            ["invalid:unrelated", "install-probe"], TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("already exists", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(workspace.Installation));
        Assert.Equal("separate-root-owned-content", File.ReadAllText(sentinel));
        workspace.AssertNoStagingDirectories();
    }

    [Fact(Timeout = 15_000)]
    public async Task An_existing_manifest_id_is_preserved_even_when_its_folder_was_manually_renamed()
    {
        var workspace = new InstallWorkspace();
        var archive = workspace.CreateFixtureArchive();
        var first = await PluginPackageInstaller.InstallAsync(archive, workspace.Installation,
            TestContext.Current.CancellationToken);
        Assert.True(first.Succeeded, first.Diagnostic);
        var manualDirectory = Path.Combine(workspace.Installation, "manually-named-folder");
        Directory.Move(first.Directory!, manualDirectory);
        var manifestPath = Path.Combine(manualDirectory, "mda-plugin.json");
        var originalManifest = File.ReadAllBytes(manifestPath);
        var malformedDirectory = Path.Combine(workspace.Installation, "unrelated-invalid-folder");
        Directory.CreateDirectory(malformedDirectory);
        File.WriteAllText(Path.Combine(malformedDirectory, "mda-plugin.json"), "malformed manifest");

        var result = await PluginPackageInstaller.InstallAsync(archive, workspace.Installation,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("already exists", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(workspace.Installation, "install-probe")));
        Assert.Equal(originalManifest, File.ReadAllBytes(manifestPath));
        Assert.Equal("malformed manifest", File.ReadAllText(Path.Combine(malformedDirectory, "mda-plugin.json")));
        workspace.AssertNoStagingDirectories();
    }

    [Fact(Timeout = 15_000)]
    public async Task Invalid_manifest_cleans_extracted_staging_and_preserves_other_plugins()
    {
        var workspace = new InstallWorkspace();
        var existing = Path.Combine(workspace.Installation, "existing");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "keep.txt"), "unchanged");
        var archive = workspace.CreateFixtureArchive(schemaVersion: 2);

        var result = await PluginPackageInstaller.InstallAsync(archive, workspace.Installation,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("schemaVersion", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal("unchanged", File.ReadAllText(Path.Combine(existing, "keep.txt")));
        Assert.Equal(existing, Assert.Single(Directory.EnumerateDirectories(workspace.Installation)));
        workspace.AssertNoStagingDirectories();
    }

    [Theory(Timeout = 15_000)]
    [InlineData("readme.txt")]
    [InlineData("other/mda-plugin.json")]
    public async Task A_wrapper_cannot_hide_unrelated_siblings_or_a_second_manifest(string sibling)
    {
        var workspace = new InstallWorkspace();
        var archive = workspace.CreateArchive(Path.Combine(AppContext.BaseDirectory, "ExamplePlugin"), "wrapper/");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
            WriteEntry(zip, sibling, "outside package");

        var result = await PluginPackageInstaller.InstallAsync(archive, workspace.Installation,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.False(Directory.Exists(workspace.Installation));
        workspace.AssertNoStagingDirectories();
    }

    [Fact(Timeout = 15_000)]
    public async Task Entry_count_and_declared_expanded_entry_size_are_bounded_before_extraction()
    {
        var workspace = new InstallWorkspace();
        var manyEntries = Path.Combine(workspace.Root, "too-many.zip");
        using (var archive = ZipFile.Open(manyEntries, ZipArchiveMode.Create))
            for (var index = 0; index < 2049; index++)
                archive.CreateEntry($"entry-{index}.txt");
        var oversized = workspace.CreateFixtureArchive();
        using (var archive = ZipFile.Open(oversized, ZipArchiveMode.Update))
        {
            using var stream = archive.CreateEntry("oversized.bin", CompressionLevel.Fastest).Open();
            var buffer = new byte[1024 * 1024];
            for (var index = 0; index < 64; index++)
                stream.Write(buffer);
            stream.WriteByte(0);
        }

        var countResult = await PluginPackageInstaller.InstallAsync(manyEntries, workspace.Installation,
            TestContext.Current.CancellationToken);
        var sizeResult = await PluginPackageInstaller.InstallAsync(oversized, workspace.Installation,
            TestContext.Current.CancellationToken);

        Assert.False(countResult.Succeeded);
        Assert.Contains("2048", countResult.Diagnostic, StringComparison.Ordinal);
        Assert.False(sizeResult.Succeeded);
        Assert.Contains("64 MiB", sizeResult.Diagnostic, StringComparison.Ordinal);
        Assert.False(Directory.Exists(workspace.Installation));
        workspace.AssertNoStagingDirectories();
    }

    [Fact(Timeout = 15_000)]
    public async Task Requested_cancellation_does_not_install_or_leave_staging()
    {
        var workspace = new InstallWorkspace();
        var archive = workspace.CreateFixtureArchive();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PluginPackageInstaller.InstallAsync(archive, workspace.Installation, cancellation.Token));

        Assert.False(Directory.Exists(workspace.Installation));
        workspace.AssertNoStagingDirectories();
    }

    private static PluginManager CreateManager() => new(new(new Version(0, 1, 22), 1, "zh-CN"),
        [new("editor.commands", typeof(IPluginCommand))]);

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }

    private sealed class InstallWorkspace
    {
        internal InstallWorkspace([CallerMemberName] string testName = "")
        {
            Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MDA", "Tests", "Plugins", $"{testName}-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(Root);
            Installation = Path.Combine(Root, "plugins");
        }

        internal string Root { get; }
        internal string Installation { get; }

        internal string CreateArchive(string source, string prefix = "")
        {
            Assert.True(Directory.Exists(source), "The actual compiled plugin package is missing.");
            var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".zip");
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                archive.CreateEntryFromFile(file, prefix + Path.GetRelativePath(source, file).Replace('\\', '/'));
            return path;
        }

        internal string CreateFixtureArchive(int schemaVersion = 1, string? extraEntry = null)
        {
            var archivePath = CreateArchive(Path.Combine(AppContext.BaseDirectory, "PluginFixtures"));
            using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
            WriteEntry(archive, "mda-plugin.json", JsonSerializer.Serialize(new
            {
                schemaVersion,
                id = "install-probe",
                name = "Install fixture",
                version = "1.0.0",
                entryAssembly = "Mmd.Modern.PluginFixture.dll",
                entryType = "Mmd.Modern.PluginFixture.CommandPlugin",
                apiVersion = 1,
                minimumHostVersion = "0.1.0",
                capabilities = new[] { "editor.commands" },
            }));
            if (extraEntry is not null)
                WriteEntry(archive, extraEntry, "archive content");
            return archivePath;
        }

        internal void AssertNoStagingDirectories() =>
            Assert.Empty(Directory.EnumerateDirectories(Root, ".mda-plugin-install-*"));
    }
}
