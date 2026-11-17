using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Core;

namespace Mmd.Modern.App;

// Recovery options intentionally omit read-only convenience properties. These two immutable
// project values must still be written explicitly and read without defaulting missing data.
internal sealed class EditorRecoveryLightingSettingsConverter : JsonConverter<SceneLightingSettings>
{
    public override SceneLightingSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Global lighting must be an object.");
        float? environment = null, exposure = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Invalid global lighting field.");
            string? name = reader.GetString();
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetSingle(out float value))
                throw new JsonException("Invalid global lighting number.");
            if (name == nameof(SceneLightingSettings.EnvironmentStrength) && environment is null) environment = value;
            else if (name == nameof(SceneLightingSettings.ExposureEv) && exposure is null) exposure = value;
            else throw new JsonException("Unknown or repeated global lighting field.");
        }
        if (reader.TokenType != JsonTokenType.EndObject || environment is null || exposure is null)
            throw new JsonException("Global lighting fields are incomplete.");
        try { return new(environment.Value, exposure.Value); }
        catch (ArgumentException error) { throw new JsonException("Global lighting values are invalid.", error); }
    }
    public override void Write(Utf8JsonWriter writer, SceneLightingSettings value, JsonSerializerOptions options)
    {
        SceneLightingSettings.Validate(value);
        writer.WriteStartObject();
        writer.WriteNumber(nameof(SceneLightingSettings.EnvironmentStrength), value.EnvironmentStrength);
        writer.WriteNumber(nameof(SceneLightingSettings.ExposureEv), value.ExposureEv);
        writer.WriteEndObject();
    }
}
