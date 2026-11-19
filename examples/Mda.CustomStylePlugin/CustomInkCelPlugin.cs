using Mmd.Modern.Plugins.Contracts;

namespace Mda.CustomStylePlugin;

/// <summary>
/// Example third-party plugin entry point registering a custom stylized rendering pipeline.
/// </summary>
public sealed class CustomInkCelPlugin : IMdaPlugin
{
    private StyleProvider? _provider;

    public ValueTask StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (_provider is not null)
            throw new InvalidOperationException("The plugin is already running.");

        var provider = new StyleProvider();
        context.RegisterExtension(PluginCapabilities.RenderingStyles, CustomInkCelStyleDefinition.CanonicalStyleId, provider);
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
            [new CustomInkCelStyleDefinition()];

        public IReadOnlyList<IRenderingStyleDefinition> GetStyles() => Styles;
    }
}
