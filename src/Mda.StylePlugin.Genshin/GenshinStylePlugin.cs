using Mmd.Modern.Plugins.Contracts;

namespace Mda.StylePlugin.Genshin;

/// <summary>MDA rendering style plugin entry point providing the Genshin Impact style family.</summary>
public sealed class GenshinStylePlugin : IMdaPlugin, IRenderingStyleProvider
{
    private static readonly IReadOnlyList<IRenderingStyleDefinition> Styles =
        [GenshinStyleDefinition.Instance];

    private bool _running;

    public ValueTask StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (_running)
            throw new InvalidOperationException("The Genshin rendering style plugin is already running.");

        context.RegisterExtension(PluginCapabilities.RenderingStyles, GenshinStyleDefinition.CanonicalStyleId, this);
        _running = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _running = false;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _running = false;
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<IRenderingStyleDefinition> GetStyles() => Styles;
}
