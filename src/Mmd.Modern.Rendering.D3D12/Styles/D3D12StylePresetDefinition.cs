using System.Collections.ObjectModel;
using System.Text;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Renderer-owned immutable parameters, independently of project references and GPU resources.
/// Null parameters retain original material behavior; automatic outline color remains null.</summary>
public sealed class D3D12StylePresetDefinition
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxIdentifierLength = 128;
    public const int MaxNameLength = 128;
    public string StableId { get; }
    public string Name { get; }
    public int SchemaVersion { get; }
    public string PipelineId { get; }
    public CharacterOutlineStyle? CharacterOutlineStyle { get; }
    public CharacterToonResponse? CharacterToonResponse { get; }

    public D3D12StylePresetDefinition(string stableId, string name, string pipelineId,
        CharacterOutlineStyle? characterOutlineStyle, CharacterToonResponse? characterToonResponse,
        int schemaVersion = CurrentSchemaVersion)
    {
        ValidateIdentifier(stableId, nameof(stableId));
        ValidateIdentifier(pipelineId, nameof(pipelineId));
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength || name.Any(char.IsControl))
            throw new ArgumentException("A preset name must contain 1 to 128 characters without control characters.", nameof(name));
        try { _ = new UTF8Encoding(false, true).GetByteCount(name); }
        catch (EncoderFallbackException error) { throw new ArgumentException("A preset name must be valid Unicode.", nameof(name), error); }
        if (schemaVersion < 1) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        if (characterOutlineStyle is not null) CharacterOutlineMath.Validate(characterOutlineStyle);
        if (characterToonResponse is not null) CharacterToonMath.Validate(characterToonResponse);
        StableId = stableId; Name = name; PipelineId = pipelineId; SchemaVersion = schemaVersion;
        CharacterOutlineStyle = characterOutlineStyle; CharacterToonResponse = characterToonResponse;
    }

    internal static void ValidateIdentifier(string id, string parameter)
    {
        if (id is null || id.Length is < 1 or > MaxIdentifierLength
            || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw new ArgumentException("A preset or pipeline ID must be 1 to 128 ASCII letters, digits, dots, underscores or hyphens.", parameter);
    }
}

/// <summary>Availability describes the actual implemented pipeline, never an automatic fallback.</summary>
public enum D3D12StylePresetStatus
{
    Available, Missing, IncompatibleSchema, UnsupportedPipeline, PipelineUnavailable, InvalidData, IoFailure, CapacityExceeded,
}

public sealed class D3D12StylePresetResult
{
    public string? StableId { get; }
    public D3D12StylePresetStatus Status { get; }
    public D3D12StylePresetDefinition? Definition { get; }
    public string Diagnostic { get; }
    public D3D12PluginStyleRegistration? PluginRegistration { get; }
    public D3D12StylizedShading? DefaultStylizedShading => PluginRegistration?.DefaultStylizedShading;
    public bool IsAvailable => Status == D3D12StylePresetStatus.Available;

    internal D3D12StylePresetResult(string? stableId, D3D12StylePresetStatus status,
        D3D12StylePresetDefinition? definition, string diagnostic,
        D3D12PluginStyleRegistration? pluginRegistration = null)
    {
        StableId = stableId; Status = status; Definition = definition; Diagnostic = diagnostic;
        PluginRegistration = pluginRegistration;
    }

    internal static D3D12StylePresetResult Resolve(
        D3D12StylePresetDefinition definition,
        IReadOnlyDictionary<string, D3D12PluginStyleRegistration>? plugins = null)
    {
        if (definition.SchemaVersion != D3D12StylePresetDefinition.CurrentSchemaVersion)
            return new(definition.StableId, D3D12StylePresetStatus.IncompatibleSchema, definition,
                $"Preset schema {definition.SchemaVersion} is not supported.");

        if (string.Equals(definition.PipelineId, D3D12SurfaceStyleMap.DefaultStyleId, StringComparison.Ordinal))
            return new(definition.StableId, D3D12StylePresetStatus.Available, definition, "");

        if (plugins is not null && plugins.TryGetValue(definition.PipelineId, out var plugin))
            return new(definition.StableId, D3D12StylePresetStatus.Available, definition,
                plugin.Diagnostic ?? "", plugin);

        return new(definition.StableId, D3D12StylePresetStatus.UnsupportedPipeline, definition,
            $"Pipeline '{definition.PipelineId}' is not supported by this renderer.");
    }
}

/// <summary>A detached, bounded catalog snapshot. Reading it never opens a device or changes scene materials.
/// The built-in "mmd" ID is reserved; local files cannot replace built-in or registered plugin IDs.</summary>
public sealed class D3D12StylePresetCatalog
{
    private readonly ReadOnlyCollection<D3D12StylePresetResult> entries;
    private readonly IReadOnlyDictionary<string, D3D12StylePresetResult> byId;
    private readonly D3D12StylePresetResult? loadFailure;
    public IReadOnlyDictionary<string, D3D12PluginStyleRegistration> Plugins { get; }
    public static D3D12StylePresetCatalog BuiltIn { get; } = new([]);

    public static D3D12StylePresetCatalog WithPlugins(
        IEnumerable<D3D12PluginStyleRegistration> plugins,
        IEnumerable<D3D12StylePresetResult>? local = null)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        return new(local ?? [], plugins);
    }

    internal D3D12StylePresetCatalog(
        IEnumerable<D3D12StylePresetResult> local,
        IEnumerable<D3D12PluginStyleRegistration>? plugins = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        Plugins = D3D12PluginStyleRegistration.BuildDictionary(plugins);

        var all = new List<D3D12StylePresetResult>
        {
            // The default MMD pipeline inherits original material shading and edge data.
            D3D12StylePresetResult.Resolve(new(D3D12SurfaceStyleMap.DefaultStyleId, "MMD",
                D3D12SurfaceStyleMap.DefaultStyleId, null, null))
        };

        foreach (var plugin in Plugins.Values)
        {
            all.Add(D3D12StylePresetResult.Resolve(
                new(plugin.StyleId, plugin.DisplayName, plugin.StyleId,
                    plugin.DefaultOutline, plugin.DefaultToonResponse),
                Plugins));
        }

        foreach (var entry in local)
        {
            if (entry is null) continue;
            if (entry.Definition is { } definition)
                all.Add(D3D12StylePresetResult.Resolve(definition, Plugins));
            else
                all.Add(entry);
        }

        entries = all.AsReadOnly();
        loadFailure = all.FirstOrDefault(entry => entry.StableId is null
            && entry.Status is D3D12StylePresetStatus.IoFailure or D3D12StylePresetStatus.CapacityExceeded);
        var index = new Dictionary<string, D3D12StylePresetResult>(StringComparer.Ordinal);
        foreach (var entry in all)
            if (entry.StableId is { } id) index.TryAdd(id, entry);
        byId = new ReadOnlyDictionary<string, D3D12StylePresetResult>(index);
    }

    public IReadOnlyList<D3D12StylePresetResult> List() => entries;

    public D3D12StylePresetResult Find(string stableId)
    {
        D3D12StylePresetDefinition.ValidateIdentifier(stableId, nameof(stableId));
        if (byId.TryGetValue(stableId, out var result)) return result;
        return loadFailure is { } failure ? new(stableId, failure.Status, null, failure.Diagnostic)
            : new(stableId, D3D12StylePresetStatus.Missing, null, $"Preset '{stableId}' is not installed.");
    }

    public D3D12PluginStyleRegistration? FindPlugin(string styleId)
    {
        D3D12StylePresetDefinition.ValidateIdentifier(styleId, nameof(styleId));
        return Plugins.TryGetValue(styleId, out var plugin) ? plugin : null;
    }

    internal static bool IsBuiltIn(string id) => string.Equals(id, D3D12SurfaceStyleMap.DefaultStyleId, StringComparison.Ordinal);
}
