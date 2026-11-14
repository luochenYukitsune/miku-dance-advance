using System.Numerics;
using System.Text;
using System.Text.Json;
using Mmd.Modern.Core;
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
            string[] fields = ["format", "schemaVersion", "stableId", "name", "layoutVersion", "layoutFingerprint", "materials"];
            Fields(root, schema < 13 ? fields : [..fields, "outlineWidths"]);
            var materials = Required(root, "materials");
            if (materials.ValueKind != JsonValueKind.Array || materials.GetArrayLength() > D3D12MaterialBindingProfile.MaxMaterials)
                throw new InvalidDataException("Profile materials must be a bounded array.");
            return new(id, Text(root, "name"), Text(root, "layoutFingerprint"),
                materials.EnumerateArray().Select(row => Row(row, schema)), Integer(root, "layoutVersion"),
                schema < 13 ? null : Outlines(Required(root, "outlineWidths")));
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        { throw new InvalidDataException("Invalid material binding profile: " + error.Message, error); }
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

    private static D3D12MaterialBindingProfileRow Row(JsonElement row, int schema)
    {
        string[] fields = ["localMaterialIndex", "role", "faceNormalShadeStrength", "faceGeometricShadowStrength",
            "eyeGeometricShadowStrength", "pmxMaterialTreatment", "face", "genshinHair", "starMcHair"];
        Fields(row, schema == 1 ? fields : schema == 2 ? [..fields, "endfieldRim"]
            : schema == 3 ? [..fields, "endfieldRim", "genshinHairAtlas"]
            : schema == 4 ? [..fields, "endfieldRim", "genshinHairAtlas", "endfieldHair"]
            : schema == 5 ? [..fields, "endfieldRim", "genshinHairAtlas", "endfieldHair", "endfieldMatcap"]
            : schema < 12 ? [..fields, "endfieldRim", "genshinHairAtlas", "endfieldHair", "endfieldMatcap", "genshinMetallic"]
            : [..fields, "endfieldRim", "genshinHairAtlas", "endfieldHair", "endfieldMatcap", "genshinMetallic", "starMcBody"]);
        return new(Integer(row, "localMaterialIndex"), Enumeration<D3D12CharacterMaterialRole>(row, "role"),
            Number(row, "faceNormalShadeStrength"), Number(row, "faceGeometricShadowStrength"), Number(row, "eyeGeometricShadowStrength"),
            Optional(row, "pmxMaterialTreatment", value => Treatment(value, schema)), Optional(row, "face", Face),
            Optional(row, "genshinHair", GenshinHair), Optional(row, "starMcHair", StarMcHair),
            schema == 1 ? null : Optional(row, "endfieldRim", Rim),
            schema < 3 ? null : Optional(row, "genshinHairAtlas", GenshinHairAtlas),
            schema < 4 ? null : Optional(row, "endfieldHair", EndfieldHair),
            schema < 5 ? null : Optional(row, "endfieldMatcap", value => EndfieldMatcap(value, schema)),
            schema < 6 ? null : Optional(row, "genshinMetallic", value => GenshinMetallic(value, schema)),
            schema < 12 ? null : Optional(row, "starMcBody", StarMcBody));
    }

    private static D3D12MaterialBindingProfileGenshinMetallic GenshinMetallic(JsonElement value, int schema)
    {
        string[] fields = ["lightmap", "matcap", "matcapEncoding", "darkColor", "lightColor", "shadowMultiplyColor",
            "enableMetallics", "tileScale", "brightness", "litMode", "uniformLitFactor"];
        Fields(value, schema < 7 ? fields : [..fields, "specular"]);
        return new(Texture(Required(value, "lightmap")), Texture(Required(value, "matcap")),
            Enumeration<D3D12MatcapColorEncoding>(value, "matcapEncoding"), Vector(Required(value, "darkColor")),
            Vector(Required(value, "lightColor")), Vector(Required(value, "shadowMultiplyColor")),
            Number(value, "enableMetallics"), Number(value, "tileScale"), Number(value, "brightness"),
            Enumeration<D3D12GenshinMetallicLitMode>(value, "litMode"), Number(value, "uniformLitFactor"),
            schema < 7 ? null : Optional(value, "specular", GenshinMetallicSpecular));
    }

    private static D3D12MaterialBindingProfileGenshinMetallicSpecular GenshinMetallicSpecular(JsonElement value)
    {
        Fields(value, "color", "sharpColor", "shininess", "scale", "attenuation", "sharpOffset", "ramp", "rampMix");
        return new(Vector(Required(value, "color")), Vector(Required(value, "sharpColor")),
            Number(value, "shininess"), Number(value, "scale"), Number(value, "attenuation"),
            Number(value, "sharpOffset"), Optional(value, "ramp", Texture), Number(value, "rampMix"));
    }

    private static D3D12MaterialBindingProfileEndfieldMatcap EndfieldMatcap(JsonElement value, int schema)
    {
        string[] fields = ["normal", "p", "matcap", "matcapEncoding", "uvChannel", "baseColor", "highlightColor",
            "normalStrength", "rotationRadians", "highlightCoefficient", "shadowHardness", "lightControlDirection",
            "baselineStrength", "coupleEndfieldPalette", "generatorRevision"];
        Fields(value, schema < 8 ? fields : schema < 12 ? [..fields, "palette"] : [..fields, "palette", "highlightRange"]);
        var direction = Required(value, "lightControlDirection");
        return new(Texture(Required(value, "normal")), Texture(Required(value, "p")), Texture(Required(value, "matcap")),
            Enumeration<D3D12MatcapColorEncoding>(value, "matcapEncoding"), Integer(value, "uvChannel"),
            Vector(Required(value, "baseColor")), Vector(Required(value, "highlightColor")),
            Number(value, "normalStrength"), Number(value, "rotationRadians"), Number(value, "highlightCoefficient"),
            Number(value, "shadowHardness"), direction.ValueKind == JsonValueKind.Null ? null : Vector(direction),
            Number(value, "baselineStrength"), Boolean(value, "coupleEndfieldPalette"), Text(value, "generatorRevision"),
            schema < 8 ? null : Optional(value, "palette", v => EndfieldPalette(v, schema)),
            schema < 12 ? null : Optional(value, "highlightRange", EndfieldHighlightRange));
    }

    private static D3D12MaterialBindingProfileEndfieldPalette EndfieldPalette(JsonElement value, int schema)
    {
        string[] fields = ["receivedLightLinearRgb", "emissionStrength", "uniformEmissionBlue", "lightInfluence", "density",
            "emissionInputMode", "emissionTexture", "receivedLightInputMode", "receivedLightTexture"];
        Fields(value, schema < 9 ? fields : [..fields, "baseImage"]);
        return new(Vector(Required(value, "receivedLightLinearRgb")), Number(value, "emissionStrength"),
            Number(value, "uniformEmissionBlue"), Number(value, "lightInfluence"), Number(value, "density"),
            Enumeration<D3D12EndfieldPaletteInputMode>(value, "emissionInputMode"), Optional(value, "emissionTexture", Texture),
            ReceivedLightMode(value, schema), Optional(value, "receivedLightTexture", Texture),
            schema < 9 ? null : Optional(value, "baseImage", v => EndfieldBaseImage(v, schema)));
    }

    private static D3D12MaterialBindingProfileEndfieldBaseImage EndfieldBaseImage(JsonElement value, int schema)
    {
        string[] fields = ["baseImageLinearRgb", "modelColor", "shadowColor", "colorMultiplier", "shadowFactor", "warmTint",
            "contrast", "saturation", "detailBlue", "shadowStrength", "baseInputMode", "baseTexture", "baseEncoding",
            "detailInputMode", "detailTexture", "shadowInputMode", "shadowTexture"];
        Fields(value, schema < 10 ? fields : [..fields, "facingBase", "directionalShadow"]);
        return new(Vector(Required(value, "baseImageLinearRgb")), Vector(Required(value, "modelColor")),
            Vector(Required(value, "shadowColor")), Vector(Required(value, "colorMultiplier")),
            Number(value, "shadowFactor"), Number(value, "warmTint"), Number(value, "contrast"),
            Number(value, "saturation"), Number(value, "detailBlue"), Number(value, "shadowStrength"),
            Enumeration<D3D12EndfieldPaletteInputMode>(value, "baseInputMode"), Optional(value, "baseTexture", Texture),
            Enumeration<D3D12MatcapColorEncoding>(value, "baseEncoding"),
            Enumeration<D3D12EndfieldPaletteInputMode>(value, "detailInputMode"), Optional(value, "detailTexture", Texture),
            Enumeration<D3D12EndfieldPaletteInputMode>(value, "shadowInputMode"), Optional(value, "shadowTexture", Texture),
            schema < 10 ? null : Optional(value, "facingBase", EndfieldFacingBase),
            schema < 10 ? null : Optional(value, "directionalShadow", EndfieldDirectionalShadow));
    }

    private static D3D12EndfieldFacingBaseBinding EndfieldFacingBase(JsonElement value)
    {
        Fields(value, "blend");
        return new(Number(value, "blend"));
    }

    private static D3D12EndfieldDirectionalShadowBinding EndfieldDirectionalShadow(JsonElement value)
    {
        Fields(value, "worldDirection", "width", "normalStrength");
        return new(Vector(Required(value, "worldDirection")), Number(value, "width"), Number(value, "normalStrength"));
    }

    private static D3D12MaterialBindingProfileEndfieldHair EndfieldHair(JsonElement value)
    {
        Fields(value, "texture", "paletteColor", "brightness");
        return new(Texture(Required(value, "texture")), Vector(Required(value, "paletteColor")), Number(value, "brightness"));
    }

    private static D3D12EndfieldRimBinding Rim(JsonElement value)
    {
        Fields(value, "baseColor", "rimColor", "edgeSize", "edgeStrength", "directionRadians", "rangeStrength", "radialStrength");
        return new(Vector(Required(value, "baseColor")), Vector(Required(value, "rimColor")),
            Number(value, "edgeSize"), Number(value, "edgeStrength"), Number(value, "directionRadians"),
            Number(value, "rangeStrength"), Number(value, "radialStrength"));
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
    private static D3D12MaterialBindingProfileFace Face(JsonElement value)
    {
        Fields(value, "pipelineId", "primary", "secondary", "headBasis", "headWorldForward", "headWorldRight", "transition", "offset", "encoding");
        if (Text(value, "headBasis") != "FixedWorld")
            throw new InvalidDataException("Only FixedWorld head axes can be persisted without a guarded skeleton layout.");
        return new(Text(value, "pipelineId"), Texture(Required(value, "primary")), Optional(value, "secondary", Texture),
            Vector(Required(value, "headWorldForward")), Vector(Required(value, "headWorldRight")),
            Number(value, "transition"), Number(value, "offset"), Enumeration<D3D12FaceSdfEncoding>(value, "encoding"));
    }
    private static D3D12MaterialBindingProfileGenshinHair GenshinHair(JsonElement value)
    {
        Fields(value, "texture", "globalSpecularScale", "shininess", "multiplier", "intensity");
        return new(Texture(Required(value, "texture")), Number(value, "globalSpecularScale"), Number(value, "shininess"), Number(value, "multiplier"), Number(value, "intensity"));
    }
    private static D3D12MaterialBindingProfileGenshinHairAtlas GenshinHairAtlas(JsonElement value)
    {
        Fields(value, "lightmap", "atlas", "atlasEncoding", "explicitRowId", "dayNight", "shadowPosition", "shadowRampWidth",
            "vertexWidth", "vertexAo", "useLightmapAo", "useVertexColorAo");
        return new(Texture(Required(value, "lightmap")), Texture(Required(value, "atlas")),
            Enumeration<D3D12GenshinAtlasColorEncoding>(value, "atlasEncoding"), Number(value, "explicitRowId"), Number(value, "dayNight"),
            Number(value, "shadowPosition"), Number(value, "shadowRampWidth"), Number(value, "vertexWidth"), Number(value, "vertexAo"),
            Number(value, "useLightmapAo"), Number(value, "useVertexColorAo"));
    }
    private static D3D12MaterialBindingProfileStarMcHair StarMcHair(JsonElement value)
    {
        Fields(value, "pipelineId", "highlight", "shadow", "highlightChannel", "highlightCoefficient", "highlightBrightness", "shadowCoefficient", "shadowSoftness", "intensity");
        return new(Text(value, "pipelineId"), Texture(Required(value, "highlight")), Texture(Required(value, "shadow")),
            Enumeration<D3D12HairHighlightChannel>(value, "highlightChannel"), Number(value, "highlightCoefficient"), Number(value, "highlightBrightness"),
            Number(value, "shadowCoefficient"), Number(value, "shadowSoftness"), Number(value, "intensity"));
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
            throw new InvalidDataException("Head directions must contain exactly three finite numbers.");
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
    private static bool Boolean(JsonElement value, string name)
    {
        var field = Required(value, name);
        return field.ValueKind is JsonValueKind.True or JsonValueKind.False ? field.GetBoolean()
            : throw new InvalidDataException($"Profile field '{name}' must be a boolean.");
    }
    private static float Single(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float number) && float.IsFinite(number)
            ? number : throw new InvalidDataException("Profile numbers must be finite single-precision values.");

    public static byte[] Encode(D3D12MaterialBindingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new() { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteString("format", Format); writer.WriteNumber("schemaVersion", profile.SchemaVersion);
            writer.WriteString("stableId", profile.StableId); writer.WriteString("name", profile.Name);
            writer.WriteNumber("layoutVersion", profile.LayoutVersion); writer.WriteString("layoutFingerprint", profile.LayoutFingerprint);
            writer.WriteStartArray("materials");
            foreach (var row in profile.Materials)
            {
                writer.WriteStartObject(); writer.WriteNumber("localMaterialIndex", row.LocalMaterialIndex); writer.WriteString("role", row.Role.ToString());
                writer.WriteNumber("faceNormalShadeStrength", row.FaceNormalShadeStrength); writer.WriteNumber("faceGeometricShadowStrength", row.FaceGeometricShadowStrength);
                writer.WriteNumber("eyeGeometricShadowStrength", row.EyeGeometricShadowStrength);
                WriteOptional(writer, "pmxMaterialTreatment", row.PmxMaterialTreatment, (w, treatment) =>
                {
                    w.WriteNumber("saturation", treatment.Saturation); w.WriteNumber("transitionWidthScale", treatment.TransitionWidthScale);
                    if (treatment.ValueContrast != 0f) w.WriteNumber("valueContrast", treatment.ValueContrast);
                    if (treatment.ShadeGamma != 1f) w.WriteNumber("shadeGamma", treatment.ShadeGamma);
                    if (treatment.HairHighlightStrength != 0f) w.WriteNumber("hairHighlightStrength", treatment.HairHighlightStrength);
                    if (treatment.HairHighlightWidth != .12f) w.WriteNumber("hairHighlightWidth", treatment.HairHighlightWidth);
                });
                WriteOptional(writer, "face", row.Face, WriteFace);
                WriteOptional(writer, "genshinHair", row.GenshinHair, (w, hair) =>
                {
                    WriteTexture(w, "texture", hair.Texture); w.WriteNumber("globalSpecularScale", hair.GlobalSpecularScale);
                    w.WriteNumber("shininess", hair.Shininess); w.WriteNumber("multiplier", hair.Multiplier); w.WriteNumber("intensity", hair.Intensity);
                });
                WriteOptional(writer, "starMcHair", row.StarMcHair, (w, hair) =>
                {
                    w.WriteString("pipelineId", hair.PipelineId); WriteTexture(w, "highlight", hair.Highlight); WriteTexture(w, "shadow", hair.Shadow);
                    w.WriteString("highlightChannel", hair.HighlightChannel.ToString()); w.WriteNumber("highlightCoefficient", hair.HighlightCoefficient);
                    w.WriteNumber("highlightBrightness", hair.HighlightBrightness); w.WriteNumber("shadowCoefficient", hair.ShadowCoefficient);
                    w.WriteNumber("shadowSoftness", hair.ShadowSoftness); w.WriteNumber("intensity", hair.Intensity);
                });
                if (profile.SchemaVersion >= 2)
                    WriteOptional(writer, "endfieldRim", row.EndfieldRim, (w, rim) =>
                    {
                        WriteVector(w, "baseColor", rim.BaseColor); WriteVector(w, "rimColor", rim.RimColor);
                        w.WriteNumber("edgeSize", rim.EdgeSize); w.WriteNumber("edgeStrength", rim.EdgeStrength);
                        w.WriteNumber("directionRadians", rim.DirectionRadians); w.WriteNumber("rangeStrength", rim.RangeStrength);
                        w.WriteNumber("radialStrength", rim.RadialStrength);
                    });
                if (profile.SchemaVersion >= 3)
                    WriteOptional(writer, "genshinHairAtlas", row.GenshinHairAtlas, (w, atlas) =>
                    {
                        WriteTexture(w, "lightmap", atlas.Lightmap); WriteTexture(w, "atlas", atlas.Atlas);
                        w.WriteString("atlasEncoding", atlas.AtlasEncoding.ToString()); w.WriteNumber("explicitRowId", atlas.ExplicitRowId);
                        w.WriteNumber("dayNight", atlas.DayNight); w.WriteNumber("shadowPosition", atlas.ShadowPosition);
                        w.WriteNumber("shadowRampWidth", atlas.ShadowRampWidth); w.WriteNumber("vertexWidth", atlas.VertexWidth);
                        w.WriteNumber("vertexAo", atlas.VertexAo); w.WriteNumber("useLightmapAo", atlas.UseLightmapAo);
                        w.WriteNumber("useVertexColorAo", atlas.UseVertexColorAo);
                    });
                if (profile.SchemaVersion >= 4)
                    WriteOptional(writer, "endfieldHair", row.EndfieldHair, (w, hair) =>
                    {
                        WriteTexture(w, "texture", hair.Texture); WriteVector(w, "paletteColor", hair.PaletteColor);
                        w.WriteNumber("brightness", hair.Brightness);
                    });
                if (profile.SchemaVersion >= 5)
                    WriteOptional(writer, "endfieldMatcap", row.EndfieldMatcap, (w, matcap) =>
                    {
                        WriteTexture(w, "normal", matcap.Normal); WriteTexture(w, "p", matcap.P); WriteTexture(w, "matcap", matcap.Matcap);
                        w.WriteString("matcapEncoding", matcap.MatcapEncoding.ToString()); w.WriteNumber("uvChannel", matcap.UvChannel);
                        WriteVector(w, "baseColor", matcap.BaseColor); WriteVector(w, "highlightColor", matcap.HighlightColor);
                        w.WriteNumber("normalStrength", matcap.NormalStrength); w.WriteNumber("rotationRadians", matcap.RotationRadians);
                        w.WriteNumber("highlightCoefficient", matcap.HighlightCoefficient); w.WriteNumber("shadowHardness", matcap.ShadowHardness);
                        if (matcap.LightControlDirection is { } direction) WriteVector(w, "lightControlDirection", direction);
                        else w.WriteNull("lightControlDirection");
                        w.WriteNumber("baselineStrength", matcap.BaselineStrength); w.WriteBoolean("coupleEndfieldPalette", matcap.CoupleEndfieldPalette);
                        w.WriteString("generatorRevision", matcap.GeneratorRevision);
                        if (profile.SchemaVersion >= 12)
                            WriteOptional(w, "highlightRange", matcap.HighlightRange, WriteEndfieldHighlightRange);
                        if (profile.SchemaVersion >= 8)
                            WriteOptional(w, "palette", matcap.Palette, (p, palette) =>
                            {
                                WriteVector(p, "receivedLightLinearRgb", palette.ReceivedLightLinearRgb);
                                p.WriteNumber("emissionStrength", palette.EmissionStrength); p.WriteNumber("uniformEmissionBlue", palette.UniformEmissionBlue);
                                p.WriteNumber("lightInfluence", palette.LightInfluence); p.WriteNumber("density", palette.Density);
                                p.WriteString("emissionInputMode", palette.EmissionInputMode.ToString());
                                if (palette.EmissionTexture is { } emission) WriteTexture(p, "emissionTexture", emission); else p.WriteNull("emissionTexture");
                                p.WriteString("receivedLightInputMode", palette.ReceivedLightInputMode.ToString());
                                if (palette.ReceivedLightTexture is { } light) WriteTexture(p, "receivedLightTexture", light); else p.WriteNull("receivedLightTexture");
                                if (profile.SchemaVersion >= 9)
                                    WriteOptional(p, "baseImage", palette.BaseImage, (b, basis) =>
                                    {
                                        WriteVector(b, "baseImageLinearRgb", basis.BaseImageLinearRgb); WriteVector(b, "modelColor", basis.ModelColor);
                                        WriteVector(b, "shadowColor", basis.ShadowColor); WriteVector(b, "colorMultiplier", basis.ColorMultiplier);
                                        b.WriteNumber("shadowFactor", basis.ShadowFactor); b.WriteNumber("warmTint", basis.WarmTint);
                                        b.WriteNumber("contrast", basis.Contrast); b.WriteNumber("saturation", basis.Saturation);
                                        b.WriteNumber("detailBlue", basis.DetailBlue); b.WriteNumber("shadowStrength", basis.ShadowStrength);
                                        b.WriteString("baseInputMode", basis.BaseInputMode.ToString());
                                        if (basis.BaseTexture is { } image) WriteTexture(b, "baseTexture", image); else b.WriteNull("baseTexture");
                                        b.WriteString("baseEncoding", basis.BaseEncoding.ToString());
                                        b.WriteString("detailInputMode", basis.DetailInputMode.ToString());
                                        if (basis.DetailTexture is { } detail) WriteTexture(b, "detailTexture", detail); else b.WriteNull("detailTexture");
                                        b.WriteString("shadowInputMode", basis.ShadowInputMode.ToString());
                                        if (basis.ShadowTexture is { } shadow) WriteTexture(b, "shadowTexture", shadow); else b.WriteNull("shadowTexture");
                                        if (profile.SchemaVersion >= 10)
                                        {
                                            WriteOptional(b, "facingBase", basis.FacingBase,
                                                (f, facing) => f.WriteNumber("blend", facing.Blend));
                                            WriteOptional(b, "directionalShadow", basis.DirectionalShadow, (d, directional) =>
                                            {
                                                WriteVector(d, "worldDirection", directional.WorldDirection);
                                                d.WriteNumber("width", directional.Width); d.WriteNumber("normalStrength", directional.NormalStrength);
                                            });
                                        }
                                    });
                            });
                    });
                if (profile.SchemaVersion >= 6)
                    WriteOptional(writer, "genshinMetallic", row.GenshinMetallic, (w, metallic) =>
                    {
                        WriteTexture(w, "lightmap", metallic.Lightmap); WriteTexture(w, "matcap", metallic.Matcap);
                        w.WriteString("matcapEncoding", metallic.MatcapEncoding.ToString());
                        WriteVector(w, "darkColor", metallic.DarkColor); WriteVector(w, "lightColor", metallic.LightColor);
                        WriteVector(w, "shadowMultiplyColor", metallic.ShadowMultiplyColor);
                        w.WriteNumber("enableMetallics", metallic.EnableMetallics); w.WriteNumber("tileScale", metallic.TileScale);
                        w.WriteNumber("brightness", metallic.Brightness); w.WriteString("litMode", metallic.LitMode.ToString());
                        w.WriteNumber("uniformLitFactor", metallic.UniformLitFactor);
                        if (profile.SchemaVersion >= 7)
                            WriteOptional(w, "specular", metallic.Specular, (s, specular) =>
                            {
                                WriteVector(s, "color", specular.Color); WriteVector(s, "sharpColor", specular.SharpColor);
                                s.WriteNumber("shininess", specular.Shininess); s.WriteNumber("scale", specular.Scale);
                                s.WriteNumber("attenuation", specular.Attenuation); s.WriteNumber("sharpOffset", specular.SharpOffset);
                                if (specular.Ramp is { } ramp) WriteTexture(s, "ramp", ramp); else s.WriteNull("ramp");
                                s.WriteNumber("rampMix", specular.RampMix);
                            });
                    });
                if (profile.SchemaVersion >= 12) WriteOptional(writer, "starMcBody", row.StarMcBody, WriteStarMcBody);
                writer.WriteEndObject();
                if (writer.BytesCommitted + writer.BytesPending > MaxProfileBytes)
                    throw new InvalidDataException("Profile exceeds the supported byte budget.");
            }
            writer.WriteEndArray();
            if (profile.SchemaVersion >= 13)
            {
                writer.WriteStartArray("outlineWidths");
                foreach (var outline in profile.OutlineWidths)
                {
                    writer.WriteStartObject(); writer.WriteNumber("localMaterialIndex", outline.LocalMaterialIndex);
                    writer.WriteNumber("scale", outline.Scale); writer.WriteEndObject();
                    if (writer.BytesCommitted + writer.BytesPending > MaxProfileBytes)
                        throw new InvalidDataException("Profile exceeds the supported byte budget.");
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        if (output.Length > MaxProfileBytes) throw new InvalidDataException("Profile exceeds the supported byte budget.");
        return output.ToArray();
    }
    private static void WriteFace(Utf8JsonWriter writer, D3D12MaterialBindingProfileFace face)
    {
        writer.WriteString("pipelineId", face.PipelineId); WriteTexture(writer, "primary", face.Primary);
        if (face.Secondary is null) writer.WriteNull("secondary"); else WriteTexture(writer, "secondary", face.Secondary);
        writer.WriteString("headBasis", "FixedWorld"); WriteVector(writer, "headWorldForward", face.HeadWorldForward);
        WriteVector(writer, "headWorldRight", face.HeadWorldRight); writer.WriteNumber("transition", face.Transition);
        writer.WriteNumber("offset", face.Offset); writer.WriteString("encoding", face.Encoding.ToString());
    }
    private static void WriteOptional<T>(Utf8JsonWriter writer, string name, T? value, Action<Utf8JsonWriter, T> write) where T : class
    {
        if (value is null) { writer.WriteNull(name); return; }
        writer.WriteStartObject(name); write(writer, value); writer.WriteEndObject();
    }
    private static void WriteTexture(Utf8JsonWriter writer, string name, D3D12MaterialBindingProfileTexture texture)
    {
        writer.WriteStartObject(name); writer.WriteNumber("sourceSceneTextureOrdinal", texture.SourceSceneTextureOrdinal);
        writer.WriteNumber("width", texture.Width); writer.WriteNumber("height", texture.Height);
        writer.WriteString("kind", texture.Kind.ToString()); writer.WriteString("rgbaSha256", texture.RgbaSha256); writer.WriteEndObject();
    }
    private static void WriteVector(Utf8JsonWriter writer, string name, Vector3 vector)
    { writer.WriteStartArray(name); writer.WriteNumberValue(vector.X); writer.WriteNumberValue(vector.Y); writer.WriteNumberValue(vector.Z); writer.WriteEndArray(); }
}
