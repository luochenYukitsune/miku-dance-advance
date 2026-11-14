using System.Runtime.Loader;
using Mmd.Modern.PluginFixture.PrivateDependency;
using Mmd.Modern.Plugins.Contracts;

namespace Mmd.Modern.PluginFixture;

/// <summary>A real CPU-only plugin used by the loader's integration tests.</summary>
public class CommandPlugin : IMdaPlugin
{
    private readonly string _directory;

    public CommandPlugin()
    {
        _directory = Path.GetDirectoryName(GetType().Assembly.Location)
            ?? throw new InvalidOperationException("The fixture must be loaded from its plugin directory.");
        Mark("constructed.txt", GetType().Name);
    }

    public virtual ValueTask StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requiredMarker = Path.Combine(_directory, "require-start-marker.txt");
        if (File.Exists(requiredMarker))
        {
            var dependencyMarker = File.ReadAllText(requiredMarker);
            if (!File.Exists(dependencyMarker))
                throw new InvalidOperationException("The dependency has not started before its consumer.");
            Mark("dependency-observed.txt", File.ReadAllText(dependencyMarker));
        }

        // Calling a type from the private assembly is intentional. Merely resolving the
        // plugin's entry type would not prove private dependency resolution works.
        Mark("started.txt", PrivateMessage.Format(context.PluginId));
        context.RegisterExtension("editor.commands", "probe", new FixtureCommand(context.PluginId));
        return ValueTask.CompletedTask;
    }

    public virtual ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Mark("stopped.txt", GetType().Name);
        return ValueTask.CompletedTask;
    }

    public virtual ValueTask DisposeAsync()
    {
        Mark("disposed.txt", GetType().Name);
        return ValueTask.CompletedTask;
    }

    protected void Mark(string fileName, string contents) => File.WriteAllText(Path.Combine(_directory, fileName), contents);

    private sealed class FixtureCommand(string pluginId) : IPluginCommand
    {
        public string Title => "CPU fixture probe";

        public string Description => "Reports private dependency resolution and collectible load context identity.";

        public ValueTask<PluginCommandResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pluginContext = AssemblyLoadContext.GetLoadContext(typeof(CommandPlugin).Assembly);
            var message = $"{PrivateMessage.Format(pluginId)}|collectible={pluginContext?.IsCollectible}"
                + $"|privateSameContext={ReferenceEquals(pluginContext, PrivateMessage.LoadContext)}";
            return ValueTask.FromResult(new PluginCommandResult(message));
        }
    }
}

public sealed class FailingStartPlugin : CommandPlugin
{
    public override async ValueTask StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        await base.StartAsync(context, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("intentional-start-failure");
    }
}

public sealed class FailingStopPlugin : CommandPlugin
{
    public override async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("intentional-stop-failure");
    }
}
