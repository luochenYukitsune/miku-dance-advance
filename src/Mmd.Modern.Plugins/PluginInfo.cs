using Mmd.Modern.Plugins.Contracts;

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
    string? Diagnostic = null,
    IReadOnlyList<string>? Capabilities = null,
    string? Description = null,
    string? Author = null,
    IReadOnlyList<string>? ProvidedStyles = null,
    IReadOnlyDictionary<string, string>? LocalizedNames = null,
    IReadOnlyDictionary<string, string>? LocalizedDescriptions = null)
{
    public string GetDisplayName(string? language) =>
        new LocalizedText(Name, LocalizedNames).Resolve(language);

    public string? GetDescription(string? language)
    {
        if (LocalizedDescriptions is { Count: > 0 })
        {
            var resolved = new LocalizedText(Description ?? string.Empty, LocalizedDescriptions).Resolve(language);
            if (Description is null && resolved.Length == 0)
                return null;
            return resolved;
        }
        return Description;
    }
}

public sealed record PluginOperationResult(bool Succeeded, string? Diagnostic = null);
