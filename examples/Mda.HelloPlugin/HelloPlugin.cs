using Mmd.Modern.Plugins.Contracts;

namespace Mda.HelloPlugin;

/// <summary>A minimal plugin that uses only the neutral host contracts.</summary>
public sealed class HelloPlugin : IMdaPlugin
{
    private HostInfoCommand? _command;

    public ValueTask StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (_command is not null)
            throw new InvalidOperationException("The plugin is already running.");

        // Registration is owned by the host. Only immutable startup information is retained.
        var command = new HostInfoCommand(context.Host);
        context.RegisterExtension("editor.commands", "hello", command);
        _command = command;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        // The host withdraws registrations; release the plugin's own reference as well.
        _command = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _command = null;
        return ValueTask.CompletedTask;
    }

    private sealed record HostInfoCommand(PluginHostInfo Host) : IPluginCommand
    {
        public string Title => "插件信息";

        public string Description => "查看此插件使用的宿主版本与接口版本。";

        public ValueTask<PluginCommandResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new PluginCommandResult(
                $"MDA 宿主版本：{Host.ApplicationVersion}\n插件接口版本：{Host.ApiVersion}"));
        }
    }
}
