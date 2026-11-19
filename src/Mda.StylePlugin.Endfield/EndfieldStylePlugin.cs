using Mmd.Modern.Plugins.Contracts;

namespace Mda.StylePlugin.Endfield;

/// <summary>Host entry point registering the Arknights: Endfield stylized rendering pipeline.</summary>
public sealed class EndfieldStylePlugin : IMdaPlugin
{
    private StyleProvider? _provider;

    public ValueTask StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (_provider is not null)
            throw new InvalidOperationException("The plugin is already running.");

        var provider = new StyleProvider();
        context.RegisterExtension(PluginCapabilities.RenderingStyles, EndfieldStyleDefinition.CanonicalStyleId, provider);
        _provider = provider;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _provider = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _provider = null;
        return ValueTask.CompletedTask;
    }

    private sealed class StyleProvider : IRenderingStyleProvider
    {
        private static readonly IReadOnlyList<IRenderingStyleDefinition> Styles =
            [new EndfieldStyleDefinition()];

        public IReadOnlyList<IRenderingStyleDefinition> GetStyles() => Styles;
    }
}
