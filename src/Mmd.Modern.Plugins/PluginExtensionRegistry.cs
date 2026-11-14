using Mmd.Modern.Plugins.Contracts;

namespace Mmd.Modern.Plugins;

internal sealed record ExtensionRegistration(string PluginId, string CapabilityId, string RegistrationId, object Value);

internal sealed class PluginExtensionRegistry : IPluginExtensionRegistry
{
    private readonly object _sync = new();
    private readonly List<ExtensionRegistration> _registrations = [];

    public IReadOnlyList<PluginExtension<T>> GetExtensions<T>(string capabilityId) where T : class
    {
        ArgumentNullException.ThrowIfNull(capabilityId);
        lock (_sync)
        {
            return Array.AsReadOnly(_registrations
                .Where(r => r.CapabilityId == capabilityId && r.Value is T)
                .Select(r => new PluginExtension<T>(r.PluginId, r.RegistrationId, (T)r.Value)).ToArray());
        }
    }

    internal void Commit(IReadOnlyList<ExtensionRegistration> registrations)
    {
        lock (_sync)
            _registrations.AddRange(registrations);
    }

    internal void Remove(string pluginId)
    {
        lock (_sync)
            _registrations.RemoveAll(r => r.PluginId == pluginId);
    }
}

internal sealed class PluginStartContext(
    PluginManifest manifest,
    PluginHostInfo host,
    IReadOnlyDictionary<string, PluginCapability> capabilities) : IPluginContext
{
    private readonly object _sync = new();
    private readonly List<ExtensionRegistration> _registrations = [];
    private bool _closed;

    public string PluginId => manifest.Id;
    public PluginHostInfo Host => host;

    public void RegisterExtension(string capabilityId, string registrationId, object extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        PluginManifest.RequireIdentifier(capabilityId, "capability id");
        PluginManifest.RequireIdentifier(registrationId, "registration id");
        lock (_sync)
        {
            if (_closed)
                throw new InvalidOperationException("Extensions may only be registered during StartAsync.");
            if (!manifest.Capabilities.Contains(capabilityId) || !capabilities.TryGetValue(capabilityId, out var capability))
                throw new InvalidOperationException($"The plugin did not declare a supported capability '{capabilityId}'.");
            if (!capability.ContractType.IsInstanceOfType(extension))
                throw new InvalidOperationException($"The extension for '{capabilityId}' does not implement its shared contract.");
            if (_registrations.Count >= 256)
                throw new InvalidOperationException("A plugin may register at most 256 extensions.");
            if (_registrations.Any(r => r.CapabilityId == capabilityId && r.RegistrationId == registrationId))
                throw new InvalidOperationException($"Duplicate extension registration '{capabilityId}/{registrationId}'.");
            _registrations.Add(new ExtensionRegistration(PluginId, capabilityId, registrationId, extension));
        }
    }

    internal IReadOnlyList<ExtensionRegistration> Close(bool keepRegistrations)
    {
        lock (_sync)
        {
            _closed = true;
            var result = keepRegistrations ? _registrations.ToArray() : [];
            _registrations.Clear();
            return result;
        }
    }
}
