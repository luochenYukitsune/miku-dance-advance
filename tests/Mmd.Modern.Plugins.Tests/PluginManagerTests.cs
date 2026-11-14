using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Plugins.Contracts;
using Xunit;

namespace Mmd.Modern.Plugins.Tests;

public sealed class PluginManagerTests
{
    private const string CommandEntry = "Mmd.Modern.PluginFixture.CommandPlugin";
    private const string FailingStartEntry = "Mmd.Modern.PluginFixture.FailingStartPlugin";
    private const string FailingStopEntry = "Mmd.Modern.PluginFixture.FailingStopPlugin";
    private const string Commands = "editor.commands";

    [Fact(Timeout = 15_000)]
    public async Task Discovery_reads_manifests_without_executing_plugin_code()
    {
        var workspace = new FixtureWorkspace();
        var directory = workspace.AddPlugin("probe");
        await using var manager = CreateManager();

        Assert.True((await manager.RefreshAsync([workspace.Root], TestContext.Current.CancellationToken)).Succeeded);

        var plugin = Assert.Single(manager.Plugins);
        Assert.Equal("probe", plugin.Id);
        Assert.Equal(PluginState.Disabled, plugin.State);
        Assert.Equal(Path.GetFullPath(directory), Path.GetFullPath(plugin.Directory));
        Assert.False(File.Exists(Path.Combine(directory, "constructed.txt")));
        Assert.Empty(manager.Extensions.GetExtensions<IPluginCommand>(Commands));
    }

    [Fact(Timeout = 15_000)]
    public async Task Enable_loads_actual_collectible_plugin_and_private_dependency_then_disable_unregisters_it()
    {
        var workspace = new FixtureWorkspace();
        var directory = workspace.AddPlugin("probe");
        await using var manager = CreateManager();
        Assert.True((await manager.RefreshAsync([workspace.Root], TestContext.Current.CancellationToken)).Succeeded);

        Assert.True((await manager.EnableAsync("probe", TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(PluginState.Active, Find(manager, "probe").State);
        Assert.Equal("private-dependency:probe|collectible=True|privateSameContext=True", await ExecuteOnlyCommand(manager));
        Assert.Equal("CommandPlugin", File.ReadAllText(Path.Combine(directory, "constructed.txt")));

        Assert.True((await manager.DisableAsync("probe", TestContext.Current.CancellationToken)).Succeeded);

        Assert.Equal(PluginState.Disabled, Find(manager, "probe").State);
        Assert.Empty(manager.Extensions.GetExtensions<IPluginCommand>(Commands));
        Assert.True(File.Exists(Path.Combine(directory, "stopped.txt")));
        Assert.True(File.Exists(Path.Combine(directory, "disposed.txt")));
    }

    [Fact(Timeout = 15_000)]
    public async Task Dependencies_start_before_consumers_and_cannot_be_disabled_while_consumed()
    {
        var workspace = new FixtureWorkspace();
        var dependency = workspace.AddPlugin("base", version: "1.5.0");
        var consumer = workspace.AddPlugin("consumer", dependencies: [new Dependency("base", "1.0.0", "1.5.0")]);
        File.WriteAllText(Path.Combine(consumer, "require-start-marker.txt"), Path.Combine(dependency, "started.txt"));
        await using var manager = CreateManager();
        Assert.True((await manager.RefreshAsync([workspace.Root], TestContext.Current.CancellationToken)).Succeeded);

        Assert.True((await manager.EnableAsync("consumer", TestContext.Current.CancellationToken)).Succeeded);

        Assert.Equal(PluginState.Active, Find(manager, "base").State);
        Assert.Equal("private-dependency:base", File.ReadAllText(Path.Combine(consumer, "dependency-observed.txt")));
        Assert.Equal(2, manager.Extensions.GetExtensions<IPluginCommand>(Commands).Count);
        Assert.False((await manager.DisableAsync("base", TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(PluginState.Active, Find(manager, "base").State);
        Assert.False(File.Exists(Path.Combine(dependency, "stopped.txt")));

        Assert.True((await manager.DisableAsync("consumer", TestContext.Current.CancellationToken)).Succeeded);
        Assert.True((await manager.DisableAsync("base", TestContext.Current.CancellationToken)).Succeeded);
        Assert.Empty(manager.Extensions.GetExtensions<IPluginCommand>(Commands));
    }

    [Fact(Timeout = 15_000)]
    public async Task Start_failure_withdraws_staged_extensions_and_rolls_back_only_new_dependencies()
    {
        var workspace = new FixtureWorkspace();
        var existing = workspace.AddPlugin("existing");
        var fresh = workspace.AddPlugin("fresh");
        var failing = workspace.AddPlugin("failing", entryType: FailingStartEntry,
            dependencies: [new Dependency("fresh", "1.0.0"), new Dependency("existing", "1.0.0")]);
        await using var manager = CreateManager();
        Assert.True((await manager.RefreshAsync([workspace.Root], TestContext.Current.CancellationToken)).Succeeded);
        Assert.True((await manager.EnableAsync("existing", TestContext.Current.CancellationToken)).Succeeded);

        var result = await manager.EnableAsync("failing", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("intentional-start-failure", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(PluginState.Active, Find(manager, "existing").State);
        Assert.Equal(PluginState.Disabled, Find(manager, "fresh").State);
        Assert.Equal(PluginState.Failed, Find(manager, "failing").State);
        Assert.Equal("existing", Assert.Single(manager.Extensions.GetExtensions<IPluginCommand>(Commands)).PluginId);
        Assert.False(File.Exists(Path.Combine(existing, "stopped.txt")));
        Assert.False(File.Exists(Path.Combine(existing, "disposed.txt")));
        Assert.True(File.Exists(Path.Combine(fresh, "stopped.txt")));
        Assert.True(File.Exists(Path.Combine(fresh, "disposed.txt")));
        Assert.True(File.Exists(Path.Combine(failing, "started.txt")));
        Assert.True(File.Exists(Path.Combine(failing, "stopped.txt")));
        Assert.True(File.Exists(Path.Combine(failing, "disposed.txt")));
    }

    [Theory(Timeout = 15_000)]
    [InlineData("api")]
    [InlineData("host-version")]
    [InlineData("dependency-version")]
    [InlineData("cycle")]
    [InlineData("capability")]
    public async Task Incompatible_manifests_and_dependency_graphs_are_rejected_before_plugin_execution(string reason)
    {
        var workspace = new FixtureWorkspace();
        string directory;
        switch (reason)
        {
            case "api":
                directory = workspace.AddPlugin("invalid", apiVersion: 2);
                break;
            case "host-version":
                directory = workspace.AddPlugin("invalid", minimumHostVersion: "99.0.0");
                break;
            case "dependency-version":
                workspace.AddPlugin("base", version: "1.5.0");
                directory = workspace.AddPlugin("invalid", dependencies: [new Dependency("base", "1.0.0", "1.4.0")]);
                break;
            case "cycle":
                workspace.AddPlugin("base", dependencies: [new Dependency("invalid", "1.0.0")]);
                directory = workspace.AddPlugin("invalid", dependencies: [new Dependency("base", "1.0.0")]);
                break;
            case "capability":
                directory = workspace.AddPlugin("invalid", capabilities: ["unavailable.capability"]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(reason));
        }
        await using var manager = CreateManager();

        Assert.True((await manager.RefreshAsync([workspace.Root], TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(PluginState.Incompatible, Find(manager, "invalid").State);
        Assert.False((await manager.EnableAsync("invalid", TestContext.Current.CancellationToken)).Succeeded);
        Assert.False(File.Exists(Path.Combine(directory, "constructed.txt")));
        Assert.All(Directory.EnumerateDirectories(workspace.Root), pluginDirectory =>
            Assert.False(File.Exists(Path.Combine(pluginDirectory, "constructed.txt"))));
        Assert.Empty(manager.Extensions.GetExtensions<IPluginCommand>(Commands));
    }

    [Fact(Timeout = 15_000)]
    public async Task Duplicate_plugin_ids_are_visible_as_incompatible_and_neither_copy_executes()
    {
        var workspace = new FixtureWorkspace();
        var first = workspace.AddPlugin("duplicate", directoryName: "first");
        var second = workspace.AddPlugin("duplicate", directoryName: "second");
        await using var manager = CreateManager();

        Assert.True((await manager.RefreshAsync([workspace.Root], TestContext.Current.CancellationToken)).Succeeded);

        Assert.Equal(2, manager.Plugins.Count);
        Assert.All(manager.Plugins, plugin => Assert.Equal(PluginState.Incompatible, plugin.State));
        Assert.False((await manager.EnableAsync("duplicate", TestContext.Current.CancellationToken)).Succeeded);
        Assert.False(File.Exists(Path.Combine(first, "constructed.txt")));
        Assert.False(File.Exists(Path.Combine(second, "constructed.txt")));
        Assert.Empty(manager.Extensions.GetExtensions<IPluginCommand>(Commands));
    }

    [Fact(Timeout = 15_000)]
    public async Task Stop_failure_is_reported_but_dispose_and_registration_cleanup_still_run()
    {
        var workspace = new FixtureWorkspace();
        var directory = workspace.AddPlugin("failing", entryType: FailingStopEntry);
        await using var manager = CreateManager();
        Assert.True((await manager.RefreshAsync([workspace.Root], TestContext.Current.CancellationToken)).Succeeded);
        Assert.True((await manager.EnableAsync("failing", TestContext.Current.CancellationToken)).Succeeded);

        var result = await manager.DisableAsync("failing", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("intentional-stop-failure", result.Diagnostic, StringComparison.Ordinal);
        Assert.NotEqual(PluginState.Active, Find(manager, "failing").State);
        Assert.Empty(manager.Extensions.GetExtensions<IPluginCommand>(Commands));
        Assert.True(File.Exists(Path.Combine(directory, "stopped.txt")));
        Assert.True(File.Exists(Path.Combine(directory, "disposed.txt")));
        // Cleanup is observable; immediate AssemblyLoadContext collection is not a
        // lifecycle promise, so this test deliberately does not force GC or assert it.
    }

    private static PluginManager CreateManager() => new(
        new PluginHostInfo(new Version(0, 1, 22), 1, "zh-CN"),
        [new PluginCapability(Commands, typeof(IPluginCommand))]);

    private static PluginInfo Find(PluginManager manager, string id) => Assert.Single(manager.Plugins, plugin => plugin.Id == id);

    private static async Task<string> ExecuteOnlyCommand(PluginManager manager)
    {
        var extension = Assert.Single(manager.Extensions.GetExtensions<IPluginCommand>(Commands));
        Assert.Equal("probe", extension.RegistrationId);
        return (await extension.Value.ExecuteAsync(TestContext.Current.CancellationToken)).Message;
    }

    private sealed record Dependency(string Id, string MinimumVersion, string? MaximumVersion = null);

    private sealed class FixtureWorkspace
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public FixtureWorkspace([CallerMemberName] string testName = "")
        {
            Root = Path.Combine("G:/MDA-Work/plugins/20261113/loader675", $"{testName}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string AddPlugin(string id, string entryType = CommandEntry, string version = "1.0.0", int apiVersion = 1,
            string minimumHostVersion = "0.1.0", Dependency[]? dependencies = null, string[]? capabilities = null,
            string? directoryName = null)
        {
            var source = Path.Combine(AppContext.BaseDirectory, "PluginFixtures");
            Assert.True(File.Exists(Path.Combine(source, "Mmd.Modern.PluginFixture.dll")), "The actual compiled plugin fixture is missing.");
            Assert.True(File.Exists(Path.Combine(source, "Mmd.Modern.PluginFixture.PrivateDependency.dll")), "The compiled private dependency is missing.");
            var directory = Path.Combine(Root, directoryName ?? id);
            Directory.CreateDirectory(directory);
            foreach (var file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            File.WriteAllText(Path.Combine(directory, "mda-plugin.json"), JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                Id = id,
                Name = $"CPU fixture {id}",
                Version = version,
                EntryAssembly = "Mmd.Modern.PluginFixture.dll",
                EntryType = entryType,
                ApiVersion = apiVersion,
                MinimumHostVersion = minimumHostVersion,
                Dependencies = dependencies ?? [],
                Capabilities = capabilities ?? [Commands],
            }, JsonOptions));
            return directory;
        }
    }
}
