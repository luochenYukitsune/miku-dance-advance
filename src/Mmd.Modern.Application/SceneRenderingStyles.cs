using System.Collections.ObjectModel;
using System.Text.Json;

namespace Mmd.Modern.Application;

/// <summary>Convenience names for built-in preset references. These are not the complete set of valid project references.</summary>
public enum RenderingStyleId { Mmd, Genshin, StarRail, WutheringWaves, Endfield }

/// <summary>A stable, case-sensitive preset identity. The renderer owns preset definitions, parameters, installation and
/// capability resolution; a project can retain this reference even when the renderer or preset is absent.</summary>
public readonly record struct RenderingPresetReference
{
    public const int MaxLength = 128;
    public string StableId { get; }
    public RenderingPresetReference(string stableId)
    {
        Validate(stableId); StableId = stableId;
    }
    public static RenderingPresetReference BuiltIn(RenderingStyleId style) => new(SceneRenderingStyles.StableId(style));
    public static implicit operator RenderingPresetReference(RenderingStyleId style) => BuiltIn(style);
    public override string ToString() => StableId ?? string.Empty;
    internal void Validate()
    {
        if (StableId is null) throw new ArgumentException("An uninitialized rendering preset reference cannot be used in a project.");
        Validate(StableId);
    }
    private static void Validate(string stableId)
    {
        ArgumentNullException.ThrowIfNull(stableId);
        if (stableId.Length is < 1 or > MaxLength || stableId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw new ArgumentException("Rendering preset references must contain 1..128 ASCII letters, digits, '.', '_' or '-'.", nameof(stableId));
    }
}

/// <summary>Immutable preset references: a global default and explicit overrides keyed by scene instance identity,
/// independently of ModelRole. Preset definitions and parameters belong to the renderer and are never copied here.</summary>
public sealed class SceneRenderingStyles
{
    public const string ExtensionName = "org.mmd-modern.rendering-styles";
    public const int ExtensionVersion = 1;
    public RenderingPresetReference DefaultStyle { get; }
    public IReadOnlyDictionary<Guid, RenderingPresetReference> ModelOverrides { get; }
    internal bool IsDefault => DefaultStyle == RenderingPresetReference.BuiltIn(RenderingStyleId.Mmd) && ModelOverrides.Count == 0;
    internal SceneRenderingStyles(RenderingPresetReference defaultStyle, IEnumerable<KeyValuePair<Guid, RenderingPresetReference>> overrides)
    {
        defaultStyle.Validate();
        var copy = overrides.ToDictionary(p => p.Key, p => p.Value);
        if (copy.Count > PmaRenderingStyleState.MaxOverrides) throw new InvalidDataException("Rendering style overrides exceed their budget.");
        foreach (var pair in copy)
        {
            if (pair.Key == Guid.Empty) throw new ArgumentException("A rendering style override needs a nonempty object identity.");
            pair.Value.Validate();
        }
        DefaultStyle = defaultStyle; ModelOverrides = new ReadOnlyDictionary<Guid, RenderingPresetReference>(copy);
    }
    public static string StableId(RenderingStyleId style) => style switch
    {
        RenderingStyleId.Mmd => "mmd", RenderingStyleId.Genshin => "genshin", RenderingStyleId.StarRail => "star-rail",
        RenderingStyleId.WutheringWaves => "wuthering-waves", RenderingStyleId.Endfield => "endfield",
        _ => throw new ArgumentOutOfRangeException(nameof(style)),
    };
    internal void ValidateObjects(IReadOnlySet<Guid> ids)
    { if (ModelOverrides.Keys.Any(id => !ids.Contains(id))) throw new ArgumentException("Rendering style override refers to an unknown scene object."); }
    internal bool SameAs(SceneRenderingStyles other) => DefaultStyle == other.DefaultStyle && ModelOverrides.Count == other.ModelOverrides.Count
        && ModelOverrides.All(pair => other.ModelOverrides.TryGetValue(pair.Key, out var value) && value == pair.Value);
}

/// <summary>Detached candidate for one style-only project transaction. Remove an override to inherit the current global default.</summary>
public sealed class SceneRenderingStylesEdit
{
    public RenderingPresetReference DefaultStyle { get; set; }
    public Dictionary<Guid, RenderingPresetReference> ModelOverrides { get; }
    internal SceneRenderingStylesEdit(SceneRenderingStyles source)
    { DefaultStyle = source.DefaultStyle; ModelOverrides = source.ModelOverrides.ToDictionary(p => p.Key, p => p.Value); }
    internal SceneRenderingStyles Freeze(IReadOnlySet<Guid> ids)
    { var result = new SceneRenderingStyles(DefaultStyle, ModelOverrides); result.ValidateObjects(ids); return result; }
}

/// <summary>One reference-only PMA extension. Unknown preset IDs are normal selection metadata; unsupported future
/// schemas stay opaque and cannot masquerade as an understood selection or imply a renderer fallback.</summary>
internal sealed class PmaRenderingStyleState
{
    internal const int MaxOverrides = 4096;
    private const int MaxBytes = 1024 * 1024;
    private readonly SceneRenderingStyles? known;
    private readonly JsonElement? opaque;
    internal static PmaRenderingStyleState Default { get; } = new(new(RenderingStyleId.Mmd, []));
    internal IReadOnlyList<string> Diagnostics { get; }
    internal bool IsSupported => known is not null;
    internal bool NeedsPersistence => opaque.HasValue || !Known.IsDefault;
    internal SceneRenderingStyles Known => known ?? throw new NotSupportedException(string.Join(" ", Diagnostics));
    internal PmaRenderingStyleState(SceneRenderingStyles styles) { known = styles; Diagnostics = Array.Empty<string>(); }
    private PmaRenderingStyleState(JsonElement raw, IEnumerable<string> diagnostics)
    { opaque = raw.Clone(); Diagnostics = Array.AsReadOnly(diagnostics.ToArray()); }

    internal PmaRenderingStyleState RemoveObject(Guid id)
        => known is null || !known.ModelOverrides.ContainsKey(id) ? this : new(new(known.DefaultStyle, known.ModelOverrides.Where(p => p.Key != id)));
    internal void ValidateObjects(IReadOnlySet<Guid> ids)
    {
        known?.ValidateObjects(ids);
        // Unknown schemas/fields are opaque, but their understood identity references must remain valid.
        if (opaque is { } raw) _ = Parse(raw, ids);
    }
    internal JsonElement? Serialize()
    {
        if (opaque is { } raw) return raw.Clone();
        if (Known.IsDefault) return null;
        Known.DefaultStyle.Validate();
        foreach (var reference in Known.ModelOverrides.Values) reference.Validate();
        return JsonSerializer.SerializeToElement(new { version = SceneRenderingStyles.ExtensionVersion,
            defaultStyle = Known.DefaultStyle.StableId,
            modelOverrides = Known.ModelOverrides.OrderBy(p => p.Key).ToDictionary(p => p.Key.ToString("D"), p => p.Value.StableId) });
    }
    internal static PmaRenderingStyleState Parse(JsonElement raw, IReadOnlySet<Guid> ids)
    {
        if (raw.ValueKind != JsonValueKind.Object) throw new InvalidDataException("PMA rendering style extension must be an object.");
        string json = raw.GetRawText();
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new InvalidDataException("PMA rendering style extension exceeds its byte budget.");
        try
        {
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 16 });
            return ParseChecked(document.RootElement, ids);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid PMA rendering style extension JSON.", error); }
    }
    private static PmaRenderingStyleState ParseChecked(JsonElement raw, IReadOnlySet<Guid> ids)
    {
        void Invalid(string message) => throw new InvalidDataException("Invalid PMA rendering style extension: " + message);
        if (raw.ValueKind != JsonValueKind.Object) { Invalid("expected an object."); }
        var properties = raw.EnumerateObject().ToArray();
        if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length) Invalid("duplicate fields.");
        if (!raw.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number < 1)
            throw new InvalidDataException("Invalid PMA rendering style extension version.");
        bool supportedVersion = number == SceneRenderingStyles.ExtensionVersion;
        bool hasDefault = raw.TryGetProperty("defaultStyle", out var defaultStyle);
        bool hasOverrides = raw.TryGetProperty("modelOverrides", out var overrides);
        if ((supportedVersion && (!hasDefault || !hasOverrides)) || (hasOverrides && overrides.ValueKind != JsonValueKind.Object))
            throw new InvalidDataException("Invalid PMA rendering style extension fields.");
        var unknown = new List<string>();
        if (!supportedVersion) unknown.Add($"unsupported version {number}");
        RenderingPresetReference ReadReference(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("PMA rendering preset references must be strings.");
            try { return new(value.GetString()!); }
            catch (ArgumentException error) { throw new InvalidDataException("PMA rendering preset references must be bounded stable ASCII identifiers.", error); }
        }
        var defaultId = hasDefault ? ReadReference(defaultStyle) : default;
        var modelStyles = new Dictionary<Guid, RenderingPresetReference>();
        foreach (var pair in hasOverrides ? overrides.EnumerateObject() : Enumerable.Empty<JsonProperty>())
        {
            if (!Guid.TryParseExact(pair.Name, "D", out Guid id) || id == Guid.Empty || !ids.Contains(id) || !modelStyles.TryAdd(id, ReadReference(pair.Value)))
                throw new InvalidDataException("PMA rendering style override must reference a unique existing object identity.");
            if (modelStyles.Count > MaxOverrides) throw new InvalidDataException("PMA rendering style overrides exceed their budget.");
        }
        foreach (var property in properties)
            if (property.Name is not ("version" or "defaultStyle" or "modelOverrides")) unknown.Add("unknown field '" + property.Name + "'");
        return unknown.Count == 0 ? new(new(defaultId, modelStyles)) : new(raw,
            [$"{SceneRenderingStyles.ExtensionName}: {string.Join(", ", unknown)}; data is preserved, style queries and edits are unavailable."]);
    }
}
