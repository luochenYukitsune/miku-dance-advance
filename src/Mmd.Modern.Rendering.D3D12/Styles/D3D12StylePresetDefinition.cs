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
    public bool IsAvailable => Status == D3D12StylePresetStatus.Available;

    internal D3D12StylePresetResult(string? stableId, D3D12StylePresetStatus status,
        D3D12StylePresetDefinition? definition, string diagnostic)
    { StableId = stableId; Status = status; Definition = definition; Diagnostic = diagnostic; }

    internal static D3D12StylePresetResult Resolve(D3D12StylePresetDefinition definition)
    {
        var status = definition.SchemaVersion != D3D12StylePresetDefinition.CurrentSchemaVersion
            ? D3D12StylePresetStatus.IncompatibleSchema : definition.PipelineId switch
            {
                "mmd" or "genshin" or "star-rail" or "wuthering-waves" or "endfield" => D3D12StylePresetStatus.Available,
                _ => D3D12StylePresetStatus.UnsupportedPipeline,
            };
        string diagnostic = status switch
        {
            D3D12StylePresetStatus.Available when definition.PipelineId == "genshin"
                => "Genshin ordinary PMX basic body surface is character-only. Explicit soft-face numerical shape is available through a separate RGB/GA binding. Dedicated face/hair color palettes, body LightMap and ramp paths are not implemented.",
            D3D12StylePresetStatus.Available when definition.PipelineId == "star-rail"
                => "Star Rail ordinary PMX basic body surface is character-only: one-sided diffuse ramp and bounded authored specular with a fixed 0.45-0.55 band. Explicit gray FaceSDF is available through a separate binding. Dedicated face/hair color palettes, body LightMap, MatCap and ramp-texture paths are not implemented.",
            D3D12StylePresetStatus.Available when definition.PipelineId == "wuthering-waves"
                => "Wuthering Waves ordinary PMX basic body surface is character-only: raw-NdotL front band and bounded authored specular with a fixed squared-lobe 0.10-0.30 gate and extra authored-lobe attenuation. Explicit gray FaceSDF is available through a separate binding. Dedicated face/hair color palettes, body LightMap, encoded RG normals, AO/HM and emission paths are not implemented.",
            D3D12StylePresetStatus.Available when definition.PipelineId == "endfield"
                => "Endfield ordinary PMX basic body surface is character-only: a three-knot palette with fixed dark/mid/lit weights 0/0.5/1 within the authored dark/lit colors and unchanged authored Blinn specular. Explicit RGB/cm_M numerical face shape is available through a separate binding. Reference ramp stops, complete face/hair palettes, body LightMap, PMap, MatCap and ramp-texture paths are not implemented.",
            D3D12StylePresetStatus.Available => "",
            D3D12StylePresetStatus.IncompatibleSchema => $"Preset schema {definition.SchemaVersion} is not supported.",
            D3D12StylePresetStatus.PipelineUnavailable => $"Pipeline '{definition.PipelineId}' is planned and has not been implemented.",
            _ => $"Pipeline '{definition.PipelineId}' is not supported by this renderer.",
        };
        return new(definition.StableId, status, definition, diagnostic);
    }
}

/// <summary>A detached, bounded catalog snapshot. Reading it never opens a device or changes scene materials.
/// The five built-in IDs are reserved; local files cannot replace them.</summary>
public sealed class D3D12StylePresetCatalog
{
    private readonly ReadOnlyCollection<D3D12StylePresetResult> entries;
    private readonly IReadOnlyDictionary<string, D3D12StylePresetResult> byId;
    private readonly D3D12StylePresetResult? loadFailure;
    public static D3D12StylePresetCatalog BuiltIn { get; } = new([]);

    internal D3D12StylePresetCatalog(IEnumerable<D3D12StylePresetResult> local)
    {
        var all = new List<D3D12StylePresetResult>();
        foreach (var (id, name) in new[] { ("mmd", "MMD"), ("genshin", "Genshin"), ("star-rail", "Star Rail"),
            ("wuthering-waves", "Wuthering Waves"), ("endfield", "Endfield") })
            // The default MMD family inherits original material shading and edge data.
            // Only explicitly selected alternative families supply modern toon/outline parameters.
            all.Add(D3D12StylePresetResult.Resolve(new(id, name, id,
                id == "mmd" ? null : new CharacterOutlineStyle(),
                id == "mmd" ? null : new CharacterToonResponse())));
        all.AddRange(local);
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

    internal static bool IsBuiltIn(string id) => id is "mmd" or "genshin" or "star-rail" or "wuthering-waves" or "endfield";
}
