using System.Numerics;
using System.Text;
using System.Text.Json;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

internal static class D3D12StylePresetJson
{
    private const string Format = "mmd-modern-d3d12-style-preset";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static D3D12StylePresetResult Read(ReadOnlyMemory<byte> bytes, string expectedId)
    {
        if (bytes.Length is < 1 or > D3D12StylePresetStore.MaxPresetBytes)
            throw new InvalidDataException("Preset exceeds the supported byte budget.");
        _ = Utf8.GetCharCount(bytes.Span);
        using var document = JsonDocument.Parse(bytes, new()
        { MaxDepth = 8, AllowDuplicateProperties = false, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A preset must be a JSON object.");
        if (Text(root, "format") != Format) throw new InvalidDataException("Unknown preset format.");
        string id = Text(root, "stableId");
        D3D12StylePresetDefinition.ValidateIdentifier(id, "stableId");
        if (!string.Equals(id, expectedId, StringComparison.Ordinal))
            throw new InvalidDataException("Preset identity must exactly match its file name.");
        JsonElement schema = Required(root, "schemaVersion");
        if (schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) || version < 1)
            throw new InvalidDataException("Preset schemaVersion must be a positive integer.");
        if (version != D3D12StylePresetDefinition.CurrentSchemaVersion)
            return new(id, D3D12StylePresetStatus.IncompatibleSchema, null, $"Preset schema {version} is not supported; its file was left unchanged.");
        Fields(root, "format", "schemaVersion", "stableId", "name", "pipelineId", "characterOutlineStyle", "characterToonResponse");
        var definition = new D3D12StylePresetDefinition(id, Text(root, "name"), Text(root, "pipelineId"),
            Outline(Required(root, "characterOutlineStyle")), Toon(Required(root, "characterToonResponse")));
        return D3D12StylePresetResult.Resolve(definition);
    }

    private static CharacterOutlineStyle? Outline(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        Fields(value, "width", "color", "enabled");
        float width = Number(value, "width");
        JsonElement color = Required(value, "color"), enabled = Required(value, "enabled");
        Vector4? rgba = null;
        if (color.ValueKind != JsonValueKind.Null)
        {
            if (color.ValueKind != JsonValueKind.Array || color.GetArrayLength() != 4)
                throw new InvalidDataException("Outline color must be null or an RGBA array of four numbers.");
            rgba = new Vector4(Single(color[0]), Single(color[1]), Single(color[2]), Single(color[3]));
        }
        if (enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Outline enabled must be a boolean.");
        return new(width, rgba, enabled.GetBoolean());
    }

    private static CharacterToonResponse? Toon(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        Fields(value, "threshold", "transitionWidth");
        return new(Number(value, "threshold"), Number(value, "transitionWidth"));
    }

    private static void Fields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Preset parameter groups must be objects.");
        foreach (var property in value.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal)) throw new InvalidDataException($"Unknown preset field '{property.Name}'.");
        foreach (string name in names) _ = Required(value, name);
    }

    private static JsonElement Required(JsonElement value, string name)
        => value.TryGetProperty(name, out var field) ? field : throw new InvalidDataException($"Missing preset field '{name}'.");
    private static string Text(JsonElement value, string name)
    {
        var field = Required(value, name);
        return field.ValueKind == JsonValueKind.String ? field.GetString()! : throw new InvalidDataException($"Preset field '{name}' must be a string.");
    }
    private static float Number(JsonElement value, string name) => Single(Required(value, name));
    private static float Single(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float result) && float.IsFinite(result)
            ? result : throw new InvalidDataException("Preset numeric parameters must be finite single-precision numbers.");

    internal static byte[] Write(D3D12StylePresetDefinition definition)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new() { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteNumber("schemaVersion", definition.SchemaVersion);
            writer.WriteString("stableId", definition.StableId);
            writer.WriteString("name", definition.Name);
            writer.WriteString("pipelineId", definition.PipelineId);
            writer.WritePropertyName("characterOutlineStyle");
            if (definition.CharacterOutlineStyle is not { } outline) writer.WriteNullValue();
            else
            {
                writer.WriteStartObject(); writer.WriteNumber("width", outline.Width);
                writer.WritePropertyName("color");
                if (outline.Color is not { } color) writer.WriteNullValue();
                else
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(color.X); writer.WriteNumberValue(color.Y);
                    writer.WriteNumberValue(color.Z); writer.WriteNumberValue(color.W); writer.WriteEndArray();
                }
                writer.WriteBoolean("enabled", outline.Enabled); writer.WriteEndObject();
            }
            writer.WritePropertyName("characterToonResponse");
            if (definition.CharacterToonResponse is not { } toon) writer.WriteNullValue();
            else
            {
                writer.WriteStartObject(); writer.WriteNumber("threshold", toon.Threshold);
                writer.WriteNumber("transitionWidth", toon.TransitionWidth); writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        byte[] bytes = output.ToArray();
        if (bytes.Length > D3D12StylePresetStore.MaxPresetBytes) throw new InvalidDataException("Preset exceeds the supported byte budget.");
        return bytes;
    }
}
