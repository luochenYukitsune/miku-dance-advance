namespace Mmd.Modern.Plugins.Contracts;

/// <summary>Immutable information about the host supplied when a plugin starts.</summary>
/// <param name="ApplicationVersion">The running application's version.</param>
/// <param name="ApiVersion">The plugin contract API version supported by the host.</param>
/// <param name="Language">The host language at startup.</param>
public sealed record PluginHostInfo(Version ApplicationVersion, int ApiVersion, string Language);

/// <summary>The lifecycle of a plugin instance owned by the host.</summary>
/// <remarks>
/// Plugins run in the host process with its permissions. These contracts do not provide
/// a security sandbox or guarantee that a plugin assembly can be unloaded immediately.
/// </remarks>
public interface IMdaPlugin : IAsyncDisposable
{
    /// <summary>Starts the instance and registers the extensions it provides.</summary>
    ValueTask StartAsync(IPluginContext context, CancellationToken cancellationToken);

    /// <summary>Stops work and releases activity before the instance is disposed.</summary>
    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>Host services and immutable startup information available to a plugin.</summary>
public interface IPluginContext
{
    /// <summary>The identifier from the validated plugin manifest.</summary>
    string PluginId { get; }

    /// <summary>The host information captured for this instance.</summary>
    PluginHostInfo Host { get; }

    /// <summary>Registers an extension during <see cref="IMdaPlugin.StartAsync"/>.</summary>
    /// <remarks>
    /// The host validates the declared capability and registration identifier and owns
    /// the registration's lifetime. Registrations are withdrawn when the plugin stops
    /// or fails to start. Capability-specific interfaces define the extension's behavior.
    /// </remarks>
    void RegisterExtension(string capabilityId, string registrationId, object extension);
}

/// <summary>Read access to extensions currently registered by running plugins.</summary>
public interface IPluginExtensionRegistry
{
    /// <summary>Returns a snapshot of compatible registrations for a capability.</summary>
    /// <remarks>
    /// A snapshot does not keep a plugin running. Consumers must not invoke retained
    /// extensions after their plugin has stopped or retain them when unloading it.
    /// </remarks>
    IReadOnlyList<PluginExtension<T>> GetExtensions<T>(string capabilityId) where T : class;
}

/// <summary>An extension and its stable identity within a plugin.</summary>
public sealed record PluginExtension<T>(string PluginId, string RegistrationId, T Value) where T : class;

/// <summary>A host-invoked action exposed by a plugin without a GUI dependency.</summary>
public interface IPluginCommand
{
    string Title { get; }

    string? Description { get; }

    ValueTask<PluginCommandResult> ExecuteAsync(CancellationToken cancellationToken);
}

/// <summary>The user-facing result of a completed plugin command.</summary>
public sealed record PluginCommandResult(string Message);
