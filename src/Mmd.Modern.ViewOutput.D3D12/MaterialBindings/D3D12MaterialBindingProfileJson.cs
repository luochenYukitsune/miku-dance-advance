using System.Collections.ObjectModel;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>Versioned, bounded model-mapping payload. All fields and resource selectors are explicit;
/// no renderer record reflection, paths, blobs, frame slots or selected-bone matrices are serialized.</summary>
public static partial class D3D12MaterialBindingProfileJson
{
    public const int MaxProfileBytes = 256 * 1024;
    private const string Format = "mda-d3d12-material-binding-profile";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static D3D12MaterialBindingProfile Decode(ReadOnlyMemory<byte> bytes, string? expectedStableId = null)
    {
        try
        {
            if (bytes.Length is < 1 or > MaxProfileBytes) throw new InvalidDataException("Profile exceeds the supported byte budget.");
            _ = Utf8.GetCharCount(bytes.Span);
            using var document = JsonDocument.Parse(bytes, new()
            { MaxDepth = 12, AllowDuplicateProperties = false, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "format") != Format)
                throw new InvalidDataException("Unknown material binding profile format.");
            string id = Text(root, "stableId");
            D3D12MaterialBindingProfile.ValidateIdentifier(id);
            if (expectedStableId is not null && !string.Equals(id, expectedStableId, StringComparison.Ordinal))
                throw new InvalidDataException("Profile identity must exactly match its file name.");
            int schema = Integer(root, "schemaVersion");
            if (schema < 1) throw new InvalidDataException("Profile schema must be positive.");
            if (schema > D3D12MaterialBindingProfile.CurrentSchemaVersion)
                throw new NotSupportedException($"Material binding profile schema {schema} is not supported.");

            if (schema == D3D12MaterialBindingProfile.CurrentSchemaVersion)
            {
                string[] requiredRoot = ["format", "schemaVersion", "stableId", "name", "layoutVersion", "layoutFingerprint", "materials", "outlineWidths"];
                string[] optionalRoot = ["pipelineId", "guessMaterialRoles"];
                Fields(root, requiredRoot, optionalRoot);

                string? pipelineId = null;
                if (root.TryGetProperty("pipelineId", out var pipeProp) && pipeProp.ValueKind != JsonValueKind.Null)
                    pipelineId = pipeProp.ValueKind == JsonValueKind.String
                        ? pipeProp.GetString()!
                        : throw new InvalidDataException("Profile field 'pipelineId' must be a string or null.");

                bool guessMaterialRoles = root.TryGetProperty("guessMaterialRoles", out var guessProp)
                    ? ParseBoolElement(guessProp, "guessMaterialRoles")
                    : true;

                var materials = Required(root, "materials");
                if (materials.ValueKind != JsonValueKind.Array || materials.GetArrayLength() > D3D12MaterialBindingProfile.MaxMaterials)
                    throw new InvalidDataException("Profile materials must be a bounded array.");

                return new(
                    id,
                    Text(root, "name"),
                    Text(root, "layoutFingerprint"),
                    materials.EnumerateArray().Select(DecodeRowV14),
                    Integer(root, "layoutVersion"),
                    Outlines(Required(root, "outlineWidths")),
                    pipelineId,
                    guessMaterialRoles);
            }

            string[] legacyFields = ["format", "schemaVersion", "stableId", "name", "layoutVersion", "layoutFingerprint", "materials"];
            Fields(root, schema < 13 ? legacyFields : [..legacyFields, "outlineWidths"]);
            var legacyMaterials = Required(root, "materials");
            if (legacyMaterials.ValueKind != JsonValueKind.Array || legacyMaterials.GetArrayLength() > D3D12MaterialBindingProfile.MaxMaterials)
                throw new InvalidDataException("Profile materials must be a bounded array.");
            return new(
                id,
                Text(root, "name"),
                Text(root, "layoutFingerprint"),
                legacyMaterials.EnumerateArray().Select(row => DecodeLegacyRow(row, schema)),
                Integer(root, "layoutVersion"),
                schema < 13 ? null : Outlines(Required(root, "outlineWidths")));
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        { throw new InvalidDataException("Invalid material binding profile: " + error.Message, error); }
    }

    private static D3D12MaterialBindingProfileRow DecodeRowV14(JsonElement row)
    {
        Fields(
            row,
            ["localMaterialIndex", "role", "faceNormalShadeStrength", "faceGeometricShadowStrength",
             "eyeGeometricShadowStrength", "pmxMaterialTreatment", "pluginBindings"],
            ["explicitRole", "textureGuards"]);

        bool explicitRole = row.TryGetProperty("explicitRole", out var explicitProp)
            ? ParseBoolElement(explicitProp, "explicitRole")
            : true;

        var bindingsElement = Required(row, "pluginBindings");
        if (bindingsElement.ValueKind != JsonValueKind.Array || bindingsElement.GetArrayLength() > D3D12MaterialBindingProfile.MaxMaterials)
            throw new InvalidDataException("Profile 'pluginBindings' must be a bounded array.");
        var pluginBindings = bindingsElement.EnumerateArray().Select(DecodePluginBinding).ToArray();

        D3D12MaterialBindingProfileTexture[]? textureGuards = null;
        if (row.TryGetProperty("textureGuards", out var guardsElement) && guardsElement.ValueKind != JsonValueKind.Null)
        {
            if (guardsElement.ValueKind != JsonValueKind.Array || guardsElement.GetArrayLength() > D3D12MaterialBindingProfile.MaxMaterials)
                throw new InvalidDataException("Profile 'textureGuards' must be a bounded array.");
            textureGuards = guardsElement.EnumerateArray().Select(Texture).ToArray();
        }

        return new(
            Integer(row, "localMaterialIndex"),
            Enumeration<D3D12CharacterMaterialRole>(row, "role"),
            Number(row, "faceNormalShadeStrength"),
            Number(row, "faceGeometricShadowStrength"),
            Number(row, "eyeGeometricShadowStrength"),
            Optional(row, "pmxMaterialTreatment", static value => Treatment(value, 14)),
            pluginBindings,
            textureGuards,
            explicitRole);
    }

    private static StyleMaterialBindingData DecodePluginBinding(JsonElement value)
    {
        Fields(
            value,
            ["styleId", "modeId"],
            ["textureSlots", "numberValues", "boolValues", "vectorValues", "stringValues", "boneBinding"]);

        string styleId = Text(value, "styleId");
        string modeId = Text(value, "modeId");

        IReadOnlyDictionary<string, int>? textureSlots = null;
        if (value.TryGetProperty("textureSlots", out var texProp) && texProp.ValueKind != JsonValueKind.Null)
        {
            if (texProp.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Binding 'textureSlots' must be an object.");
            var dict = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in texProp.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out int slot))
                    throw new InvalidDataException($"Texture slot '{p.Name}' must be an integer.");
                dict.Add(p.Name, slot);
            }
            textureSlots = new ReadOnlyDictionary<string, int>(dict);
        }

        IReadOnlyDictionary<string, double>? numberValues = null;
        if (value.TryGetProperty("numberValues", out var numProp) && numProp.ValueKind != JsonValueKind.Null)
        {
            if (numProp.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Binding 'numberValues' must be an object.");
            var dict = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var p in numProp.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetDouble(out double d) || !double.IsFinite(d) || Math.Abs(d) > float.MaxValue)
                    throw new InvalidDataException($"Numeric parameter '{p.Name}' must be a finite number.");
                dict.Add(p.Name, d);
            }
            numberValues = new ReadOnlyDictionary<string, double>(dict);
        }

        IReadOnlyDictionary<string, bool>? boolValues = null;
        if (value.TryGetProperty("boolValues", out var boolProp) && boolProp.ValueKind != JsonValueKind.Null)
        {
            if (boolProp.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Binding 'boolValues' must be an object.");
            var dict = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var p in boolProp.EnumerateObject())
                dict.Add(p.Name, ParseBoolElement(p.Value, p.Name));
            boolValues = new ReadOnlyDictionary<string, bool>(dict);
        }

        IReadOnlyDictionary<string, float[]>? vectorValues = null;
        if (value.TryGetProperty("vectorValues", out var vecProp) && vecProp.ValueKind != JsonValueKind.Null)
        {
            if (vecProp.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Binding 'vectorValues' must be an object.");
            var dict = new Dictionary<string, float[]>(StringComparer.Ordinal);
            foreach (var p in vecProp.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Array || p.Value.GetArrayLength() is < 1 or > 16)
                    throw new InvalidDataException($"Vector parameter '{p.Name}' must be an array of 1..16 numbers.");
                var arr = new float[p.Value.GetArrayLength()];
                int idx = 0;
                foreach (var elem in p.Value.EnumerateArray())
                    arr[idx++] = Single(elem);
                dict.Add(p.Name, arr);
            }
            vectorValues = new ReadOnlyDictionary<string, float[]>(dict);
        }

        IReadOnlyDictionary<string, string>? stringValues = null;
        if (value.TryGetProperty("stringValues", out var strProp) && strProp.ValueKind != JsonValueKind.Null)
        {
            if (strProp.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Binding 'stringValues' must be an object.");
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in strProp.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException($"String parameter '{p.Name}' must be a string.");
                dict.Add(p.Name, p.Value.GetString()!);
            }
            stringValues = new ReadOnlyDictionary<string, string>(dict);
        }

        StyleBoneAxisBinding? boneBinding = null;
        if (value.TryGetProperty("boneBinding", out var boneProp) && boneProp.ValueKind != JsonValueKind.Null)
            boneBinding = DecodeBoneBinding(boneProp);

        return D3D12StylePluginAdapter.DetachBindingData(new StyleMaterialBindingData(
            styleId, modeId, textureSlots, numberValues, boolValues, vectorValues, stringValues, boneBinding));
    }

    private static StyleBoneAxisBinding DecodeBoneBinding(JsonElement value)
    {
        Fields(value, ["mode"], ["boneName", "localForward", "localUp", "fixedWorldForward", "fixedWorldRight"]);
        var mode = Enumeration<StyleBoneTrackingMode>(value, "mode");
        string? boneName = null;
        if (value.TryGetProperty("boneName", out var bnProp) && bnProp.ValueKind != JsonValueKind.Null)
        {
            if (bnProp.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("BoneBinding 'boneName' must be a string or null.");
            boneName = bnProp.GetString();
        }
        Vector3? OptVec(string name) => value.TryGetProperty(name, out var prop) && prop.ValueKind != JsonValueKind.Null
            ? Vector(prop)
            : null;
        return new StyleBoneAxisBinding(
            mode,
            boneName,
            OptVec("localForward"),
            OptVec("localUp"),
            OptVec("fixedWorldForward"),
            OptVec("fixedWorldRight"));
    }

    private static IEnumerable<D3D12MaterialBindingProfileOutline> Outlines(JsonElement outlines)
    {
        if (outlines.ValueKind != JsonValueKind.Array || outlines.GetArrayLength() > D3D12MaterialBindingProfile.MaxMaterials)
            throw new InvalidDataException("Profile outline widths must be a bounded array.");
        foreach (var outline in outlines.EnumerateArray())
        {
            Fields(outline, "localMaterialIndex", "scale");
            yield return new(Integer(outline, "localMaterialIndex"), Number(outline, "scale"));
        }
    }

    private static D3D12PmxMaterialTreatment Treatment(JsonElement value, int schema)
    {
        if (schema < 11) Fields(value, "saturation", "transitionWidthScale");
        else Fields(value, ["saturation", "transitionWidthScale"],
            ["valueContrast", "shadeGamma", "hairHighlightStrength", "hairHighlightWidth"]);
        return new(Number(value, "saturation"), Number(value, "transitionWidthScale"),
            schema >= 11 && value.TryGetProperty("valueContrast", out var contrast) ? Single(contrast) : 0f,
            schema >= 11 && value.TryGetProperty("shadeGamma", out var gamma) ? Single(gamma) : 1f,
            schema >= 11 && value.TryGetProperty("hairHighlightStrength", out var strength) ? Single(strength) : 0f,
            schema >= 11 && value.TryGetProperty("hairHighlightWidth", out var width) ? Single(width) : .12f);
    }

    private static D3D12MaterialBindingProfileTexture Texture(JsonElement value)
    {
        Fields(value, "sourceSceneTextureOrdinal", "width", "height", "kind", "rgbaSha256");
        return new(Integer(value, "sourceSceneTextureOrdinal"), Integer(value, "width"), Integer(value, "height"),
            Enumeration<SceneTextureKind>(value, "kind"), Text(value, "rgbaSha256"));
    }

    private static T? Optional<T>(JsonElement value, string name, Func<JsonElement, T> parse) where T : class
    { var field = Required(value, name); return field.ValueKind == JsonValueKind.Null ? null : parse(field); }

    private static Vector3 Vector(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3)
            throw new InvalidDataException("Vector fields must contain exactly three finite numbers.");
        return new(Single(value[0]), Single(value[1]), Single(value[2]));
    }

    private static T Enumeration<T>(JsonElement value, string name) where T : struct, Enum
    {
        string text = Text(value, name);
        return Enum.TryParse<T>(text, ignoreCase: false, out var result) && Enum.IsDefined(result) && result.ToString() == text
            ? result : throw new InvalidDataException($"Unknown enum name for '{name}'.");
    }

    private static void Fields(JsonElement value, params string[] names)
        => Fields(value, names, []);

    private static void Fields(JsonElement value, string[] names, string[] optionalNames)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Profile parameter groups must be objects.");
        foreach (var property in value.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) && !optionalNames.Contains(property.Name, StringComparer.Ordinal))
                throw new InvalidDataException($"Unknown material profile field '{property.Name}'.");
        foreach (string name in names) _ = Required(value, name);
    }

    private static JsonElement Required(JsonElement value, string name)
        => value.TryGetProperty(name, out var field) ? field : throw new InvalidDataException($"Missing material profile field '{name}'.");

    private static string Text(JsonElement value, string name)
    {
        var field = Required(value, name);
        return field.ValueKind == JsonValueKind.String ? field.GetString()! : throw new InvalidDataException($"Profile field '{name}' must be a string.");
    }

    private static int Integer(JsonElement value, string name)
    {
        var field = Required(value, name);
        return field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out int number)
            ? number : throw new InvalidDataException($"Profile field '{name}' must be an integer.");
    }

    private static float Number(JsonElement value, string name) => Single(Required(value, name));

    private static bool Boolean(JsonElement value, string name) => ParseBoolElement(Required(value, name), name);

    private static bool ParseBoolElement(JsonElement field, string name)
        => field.ValueKind is JsonValueKind.True or JsonValueKind.False ? field.GetBoolean()
            : throw new InvalidDataException($"Profile field '{name}' must be a boolean.");

    private static float Single(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float number) && float.IsFinite(number)
            ? number : throw new InvalidDataException("Profile numbers must be finite single-precision values.");

    public static byte[] Encode(D3D12MaterialBindingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new() { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteNumber("schemaVersion", D3D12MaterialBindingProfile.CurrentSchemaVersion);
            writer.WriteString("stableId", profile.StableId);
            writer.WriteString("name", profile.Name);
            writer.WriteNumber("layoutVersion", profile.LayoutVersion);
            writer.WriteString("layoutFingerprint", profile.LayoutFingerprint);
            if (profile.PipelineId is not null)
                writer.WriteString("pipelineId", profile.PipelineId);
            else
                writer.WriteNull("pipelineId");
            writer.WriteBoolean("guessMaterialRoles", profile.GuessMaterialRoles);

            writer.WriteStartArray("materials");
            foreach (var row in profile.Materials)
            {
                writer.WriteStartObject();
                writer.WriteNumber("localMaterialIndex", row.LocalMaterialIndex);
                writer.WriteString("role", row.Role.ToString());
                if (!row.ExplicitRole)
                    writer.WriteBoolean("explicitRole", false);
                writer.WriteNumber("faceNormalShadeStrength", row.FaceNormalShadeStrength);
                writer.WriteNumber("faceGeometricShadowStrength", row.FaceGeometricShadowStrength);
                writer.WriteNumber("eyeGeometricShadowStrength", row.EyeGeometricShadowStrength);
                WriteOptional(writer, "pmxMaterialTreatment", row.PmxMaterialTreatment, static (w, treatment) =>
                {
                    w.WriteNumber("saturation", treatment.Saturation);
                    w.WriteNumber("transitionWidthScale", treatment.TransitionWidthScale);
                    if (treatment.ValueContrast != 0f) w.WriteNumber("valueContrast", treatment.ValueContrast);
                    if (treatment.ShadeGamma != 1f) w.WriteNumber("shadeGamma", treatment.ShadeGamma);
                    if (treatment.HairHighlightStrength != 0f) w.WriteNumber("hairHighlightStrength", treatment.HairHighlightStrength);
                    if (treatment.HairHighlightWidth != .12f) w.WriteNumber("hairHighlightWidth", treatment.HairHighlightWidth);
                });

                writer.WriteStartArray("pluginBindings");
                foreach (var binding in row.PluginBindings)
                    WritePluginBinding(writer, binding);
                writer.WriteEndArray();

                if (row.TextureGuards.Count > 0)
                {
                    writer.WriteStartArray("textureGuards");
                    foreach (var guard in row.TextureGuards)
                        WriteTextureObject(writer, guard);
                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
                if (writer.BytesCommitted + writer.BytesPending > MaxProfileBytes)
                    throw new InvalidDataException("Profile exceeds the supported byte budget.");
            }
            writer.WriteEndArray();

            writer.WriteStartArray("outlineWidths");
            foreach (var outline in profile.OutlineWidths)
            {
                writer.WriteStartObject();
                writer.WriteNumber("localMaterialIndex", outline.LocalMaterialIndex);
                writer.WriteNumber("scale", outline.Scale);
                writer.WriteEndObject();
                if (writer.BytesCommitted + writer.BytesPending > MaxProfileBytes)
                    throw new InvalidDataException("Profile exceeds the supported byte budget.");
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        if (output.Length > MaxProfileBytes) throw new InvalidDataException("Profile exceeds the supported byte budget.");
        return output.ToArray();
    }

    private static void WritePluginBinding(Utf8JsonWriter writer, StyleMaterialBindingData binding)
    {
        writer.WriteStartObject();
        writer.WriteString("styleId", binding.StyleId);
        writer.WriteString("modeId", binding.ModeId);

        if (binding.TextureSlots is { Count: > 0 } textureSlots)
        {
            writer.WriteStartObject("textureSlots");
            foreach (var (key, value) in textureSlots.OrderBy(static p => p.Key, StringComparer.Ordinal))
                writer.WriteNumber(key, value);
            writer.WriteEndObject();
        }

        if (binding.NumberValues is { Count: > 0 } numberValues)
        {
            writer.WriteStartObject("numberValues");
            foreach (var (key, value) in numberValues.OrderBy(static p => p.Key, StringComparer.Ordinal))
                writer.WriteNumber(key, value);
            writer.WriteEndObject();
        }

        if (binding.BoolValues is { Count: > 0 } boolValues)
        {
            writer.WriteStartObject("boolValues");
            foreach (var (key, value) in boolValues.OrderBy(static p => p.Key, StringComparer.Ordinal))
                writer.WriteBoolean(key, value);
            writer.WriteEndObject();
        }

        if (binding.VectorValues is { Count: > 0 } vectorValues)
        {
            writer.WriteStartObject("vectorValues");
            foreach (var (key, value) in vectorValues.OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                writer.WriteStartArray(key);
                foreach (float elem in value)
                    writer.WriteNumberValue(elem);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }

        if (binding.StringValues is { Count: > 0 } stringValues)
        {
            writer.WriteStartObject("stringValues");
            foreach (var (key, value) in stringValues.OrderBy(static p => p.Key, StringComparer.Ordinal))
                writer.WriteString(key, value);
            writer.WriteEndObject();
        }

        if (binding.BoneBinding is { } bone)
        {
            writer.WriteStartObject("boneBinding");
            writer.WriteString("mode", bone.Mode.ToString());
            if (bone.BoneName is not null) writer.WriteString("boneName", bone.BoneName);
            if (bone.LocalForward is { } lf) WriteVector(writer, "localForward", lf);
            if (bone.LocalUp is { } lu) WriteVector(writer, "localUp", lu);
            if (bone.FixedWorldForward is { } fwf) WriteVector(writer, "fixedWorldForward", fwf);
            if (bone.FixedWorldRight is { } fwr) WriteVector(writer, "fixedWorldRight", fwr);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteOptional<T>(Utf8JsonWriter writer, string name, T? value, Action<Utf8JsonWriter, T> write) where T : class
    {
        if (value is null) { writer.WriteNull(name); return; }
        writer.WriteStartObject(name); write(writer, value); writer.WriteEndObject();
    }

    private static void WriteTextureObject(Utf8JsonWriter writer, D3D12MaterialBindingProfileTexture texture)
    {
        writer.WriteStartObject();
        writer.WriteNumber("sourceSceneTextureOrdinal", texture.SourceSceneTextureOrdinal);
        writer.WriteNumber("width", texture.Width);
        writer.WriteNumber("height", texture.Height);
        writer.WriteString("kind", texture.Kind.ToString());
        writer.WriteString("rgbaSha256", texture.RgbaSha256);
        writer.WriteEndObject();
    }

    private static void WriteVector(Utf8JsonWriter writer, string name, Vector3 vector)
    {
        writer.WriteStartArray(name);
        writer.WriteNumberValue(vector.X);
        writer.WriteNumberValue(vector.Y);
        writer.WriteNumberValue(vector.Z);
        writer.WriteEndArray();
    }
}
