using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Mmd.Modern.Plugins.Contracts;
using Xunit;

namespace Mmd.Modern.Plugins.Tests;

public sealed class PluginActiveDiscoveryTests
{
    private const string Commands = "editor.commands";

    [Fact(Timeout = 15_000)]
    public async Task Preserving_refresh_discovers_new_disabled_plugin_without_replacing_active_runtime()
    {
        var workspace = new FixtureWorkspace();
        var activeDirectory = workspace.AddPlugin("active");
        await using var manager = CreateManager();
        Assert.True((await manager.RefreshAsync([workspace.Root], TestContext.Current.CancellationToken)).Succeeded);
        Assert.True((await manager.EnableAsync("active", TestContext.Current.CancellationToken)).Succeeded);
        var command = Assert.Single(manager.Extensions.GetExtensions<IPluginCommand>(Commands)).Value;
        var loadContext = AssemblyLoadContext.GetLoadContext(command.GetType().Assembly);
        Assert.NotNull(loadContext);
        Assert.True(loadContext.IsCollectible);
        var freshDirectory = workspace.AddPlugin("fresh");

        var result = await manager.RefreshAsync([workspace.Root], preserveActive: true,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal(2, manager.Plugins.Count);
        Assert.Equal(PluginState.Active, Assert.Single(manager.Plugins, p => p.Id == "active").State);
        Assert.Equal(PluginState.Disabled, Assert.Single(manager.Plugins, p => p.Id == "fresh").State);
        var retained = Assert.Single(manager.Extensions.GetExtensions<IPluginCommand>(Commands));
        Assert.Equal("active", retained.PluginId);
        Assert.Same(command, retained.Value);
        Assert.Same(loadContext, AssemblyLoadContext.GetLoadContext(retained.Value.GetType().Assembly));
        Assert.Equal("private-dependency:active|collectible=True|privateSameContext=True",
            (await retained.Value.ExecuteAsync(TestContext.Current.CancellationToken)).Message);
        Assert.False(File.Exists(Path.Combine(activeDirectory, "stopped.txt")));
        Assert.False(File.Exists(Path.Combine(activeDirectory, "disposed.txt")));
        Assert.False(File.Exists(Path.Combine(freshDirectory, "constructed.txt")));
    }

    [Theory(Timeout = 15_000)]
    [InlineData("changed")]
    [InlineData("invalid")]
    [InlineData("missing")]
    public async Task Active_manifest_drift_or_missing_directory_rejects_entire_refresh(string change)
    {
        var workspace = new FixtureWorkspace();
        var directory = workspace.AddPlugin("active");
        await using var manager = CreateManager();
        Assert.True((await manager.RefreshAsync([workspace.Root], TestContext.Current.CancellationToken)).Succeeded);
        Assert.True((await manager.EnableAsync("active", TestContext.Current.CancellationToken)).Succeeded);
        var snapshot = manager.Plugins;
        var command = Assert.Single(manager.Extensions.GetExtensions<IPluginCommand>(Commands)).Value;
        var freshDirectory = workspace.AddPlugin("fresh");
        string[] roots = [workspace.Root];
        switch (change)
        {
            case "changed":
                // A valid new identity still cannot replace a running plugin implicitly.
                FixtureWorkspace.WriteManifest(directory, "replacement");
                break;
            case "invalid":
                File.WriteAllText(Path.Combine(directory, "mda-plugin.json"), "{");
                break;
            case "missing":
                // Excluding the old root models a missing active directory without moving
                // a live, mapped DLL on Windows or altering the plugin's cleanup markers.
                var emptyRoot = Path.Combine(workspace.Root, "empty-root");
                Directory.CreateDirectory(emptyRoot);
                roots = [emptyRoot];
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change));
        }

        var result = await manager.RefreshAsync(roots, preserveActive: true, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("active", result.Diagnostic, StringComparison.Ordinal);
        Assert.Same(snapshot, manager.Plugins);
        Assert.Equal(PluginState.Active, Assert.Single(manager.Plugins).State);
        Assert.Same(command, Assert.Single(manager.Extensions.GetExtensions<IPluginCommand>(Commands)).Value);
        Assert.Equal("private-dependency:active|collectible=True|privateSameContext=True",
            (await command.ExecuteAsync(TestContext.Current.CancellationToken)).Message);
        Assert.False(File.Exists(Path.Combine(directory, "stopped.txt")));
        Assert.False(File.Exists(Path.Combine(directory, "disposed.txt")));
        Assert.False(File.Exists(Path.Combine(freshDirectory, "constructed.txt")));
    }

    [Fact(Timeout = 15_000)]
    public async Task Duplicate_id_involving_active_plugin_rejects_refresh_without_mutating_registration_or_state()
    {
        var workspace = new FixtureWorkspace();
        var directory = workspace.AddPlugin("active");
        await using var manager = CreateManager();
        Assert.True((await manager.RefreshAsync([workspace.Root], TestContext.Current.CancellationToken)).Succeeded);
        Assert.True((await manager.EnableAsync("active", TestContext.Current.CancellationToken)).Succeeded);
        var snapshot = manager.Plugins;
        var command = Assert.Single(manager.Extensions.GetExtensions<IPluginCommand>(Commands)).Value;
        var duplicate = workspace.AddPlugin("active", "duplicate");

        var result = await manager.RefreshAsync([workspace.Root], preserveActive: true,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("Duplicate", result.Diagnostic, StringComparison.Ordinal);
        Assert.Same(snapshot, manager.Plugins);
        Assert.Equal(PluginState.Active, Assert.Single(manager.Plugins).State);
        Assert.Same(command, Assert.Single(manager.Extensions.GetExtensions<IPluginCommand>(Commands)).Value);
        Assert.False(File.Exists(Path.Combine(directory, "stopped.txt")));
        Assert.False(File.Exists(Path.Combine(duplicate, "constructed.txt")));
        // The original entry must also remain addressable by the lifecycle graph.
        Assert.True((await manager.DisableAsync("active", TestContext.Current.CancellationToken)).Succeeded);
        Assert.Empty(manager.Extensions.GetExtensions<IPluginCommand>(Commands));
        Assert.True(File.Exists(Path.Combine(directory, "stopped.txt")));
    }

    private static PluginManager CreateManager() => new(
        new PluginHostInfo(new Version(0, 1, 22), 1, "zh-CN"),
        [new PluginCapability(Commands, typeof(IPluginCommand))]);

    private sealed class FixtureWorkspace
    {
        public FixtureWorkspace([CallerMemberName] string testName = "")
        {
            Root = Path.Combine(Path.GetTempPath(), "MDA", "Tests", "Plugins", $"{testName}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string AddPlugin(string id, string? directoryName = null)
        {
            var source = Path.Combine(AppContext.BaseDirectory, "PluginFixtures");
            Assert.True(File.Exists(Path.Combine(source, "Mmd.Modern.PluginFixture.dll")), "The actual compiled plugin fixture is missing.");
            Assert.True(File.Exists(Path.Combine(source, "Mmd.Modern.PluginFixture.PrivateDependency.dll")), "The compiled private dependency is missing.");
            var directory = Path.Combine(Root, directoryName ?? id);
            Directory.CreateDirectory(directory);
            foreach (var file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            WriteManifest(directory, id);
            return directory;
        }

        internal static void WriteManifest(string directory, string id) =>
            File.WriteAllText(Path.Combine(directory, "mda-plugin.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                id,
                name = $"CPU fixture {id}",
                version = "1.0.0",
                entryAssembly = "Mmd.Modern.PluginFixture.dll",
                entryType = "Mmd.Modern.PluginFixture.CommandPlugin",
                apiVersion = 1,
                minimumHostVersion = "0.1.0",
                dependencies = Array.Empty<object>(),
                capabilities = new[] { Commands },
            }));
    }
}
