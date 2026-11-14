namespace Mmd.Modern.Plugins;

/// <summary>A capability that the host accepts through a shared neutral contract.</summary>
public sealed record PluginCapability(string Id, Type ContractType);

public enum PluginState
{
    Discovered,
    Disabled,
    Active,
    Failed,
    Incompatible
}

/// <summary>A snapshot containing no plugin instances or reflection objects.</summary>
public sealed record PluginInfo(
    string Id,
    string Name,
    Version Version,
    string Directory,
    PluginState State,
    string? Diagnostic = null);

public sealed record PluginOperationResult(bool Succeeded, string? Diagnostic = null);
