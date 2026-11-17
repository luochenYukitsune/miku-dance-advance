using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Core;

namespace Mmd.Modern.App;

/// <summary>GUI recovery owns its JSON representation; the core selection stays immutable and format-neutral.</summary>
internal sealed class EditorRecoveryLightReceiversConverter : JsonConverter<SceneLightReceivers>
{
    public override SceneLightReceivers Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("Mode", out var mode) || mode.ValueKind != JsonValueKind.Number || !mode.TryGetInt32(out int number)
            || !root.TryGetProperty("ObjectIds", out var ids) || ids.ValueKind != JsonValueKind.Array
            || root.EnumerateObject().Any(property => property.Name is not ("Mode" or "ObjectIds")))
            throw new JsonException("Invalid light receiver selection.");
        return new((SceneLightReceiverMode)number, ids.EnumerateArray().Take(1025).Select(item =>
            item.ValueKind == JsonValueKind.String && item.TryGetGuid(out var id) ? id : throw new JsonException("Invalid receiver model ID.")));
    }
    public override void Write(Utf8JsonWriter writer, SceneLightReceivers value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteNumber("Mode", (int)value.Mode); writer.WriteStartArray("ObjectIds");
        foreach (var id in value.ObjectIds) writer.WriteStringValue(id);
        writer.WriteEndArray(); writer.WriteEndObject();
    }
}
