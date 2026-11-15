using Mmd.Modern.Plugins.Contracts;
using Xunit;

namespace Mmd.Modern.Plugins.Tests;

public sealed class ExamplePluginTests
{
    [Fact]
    public async Task ActualExamplePackageLoadsWithoutItsOwnContractCopyAndReportsTheHostVersion()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MDA", "Tests", "Plugins", "sample-" + Guid.NewGuid().ToString("N"));
        string plugin = Path.Combine(directory, "mda.hello");
        Directory.CreateDirectory(plugin);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "ExamplePlugin")))
            File.Copy(file, Path.Combine(plugin, Path.GetFileName(file)));
        Assert.False(File.Exists(Path.Combine(plugin, "Mmd.Modern.Plugins.Contracts.dll")));
        await using var manager = new PluginManager(new(new(0, 1, 21), 1, "zh-CN"),
            [new("editor.commands", typeof(IPluginCommand))]);
        Assert.True((await manager.RefreshAsync([directory], TestContext.Current.CancellationToken)).Succeeded);
        var enabled = await manager.EnableAsync("mda.hello", TestContext.Current.CancellationToken);
        Assert.True(enabled.Succeeded, enabled.Diagnostic);
        var command = Assert.Single(manager.Extensions.GetExtensions<IPluginCommand>("editor.commands"));
        Assert.Equal("hello", command.RegistrationId);
        Assert.Equal("插件信息", command.Value.Title);
        Assert.Equal("MDA 宿主版本：0.1.21\n插件接口版本：1",
            (await command.Value.ExecuteAsync(TestContext.Current.CancellationToken)).Message);
        Assert.True((await manager.DisableAsync("mda.hello", TestContext.Current.CancellationToken)).Succeeded);
        Assert.Empty(manager.Extensions.GetExtensions<IPluginCommand>("editor.commands"));
    }
}
