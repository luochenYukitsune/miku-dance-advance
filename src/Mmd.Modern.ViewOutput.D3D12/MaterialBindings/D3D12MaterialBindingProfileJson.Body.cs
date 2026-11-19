using System.Collections.ObjectModel;
using System.Numerics;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

public static partial class D3D12MaterialBindingProfileJson
{
    private static readonly string[] ValidMatcapEncodings = ["LinearRgb", "SrgbRgb", "Srgb", "LinearBgr", "SrgbBgr"];
    private static readonly string[] ValidFaceEncodings = ["GrayRed", "GenshinSoftFace", "EndfieldRgbCmM"];
    private static readonly string[] ValidAtlasEncodings = ["LinearRgb", "Srgb"];
    private static readonly string[] ValidMetallicLitModes = ["PrimaryResponse", "Uniform"];
    private static readonly string[] ValidHairHighlightChannels = ["Blue", "Alpha"];
    private static readonly string[] ValidPaletteInputModes = ["Uniform", "Texture", "RendererDiffuse"];

    private static D3D12MaterialBindingProfileRow DecodeLegacyRow(JsonElement row, int schema)
    {
        string[] baseFields =
        [
            "localMaterialIndex", "role", "faceNormalShadeStrength", "faceGeometricShadowStrength",
            "eyeGeometricShadowStrength", "pmxMaterialTreatment", "face", "genshinHair", "starMcHair"
        ];
        Fields(
            row,
            schema == 1 ? baseFields
            : schema == 2 ? [..baseFields, "endfieldRim"]
            : schema == 3 ? [..baseFields, "endfieldRim", "genshinHairAtlas"]
            : schema == 4 ? [..baseFields, "endfieldRim", "genshinHairAtlas", "endfieldHair"]
            : schema == 5 ? [..baseFields, "endfieldRim", "genshinHairAtlas", "endfieldHair", "endfieldMatcap"]
            : schema < 12 ? [..baseFields, "endfieldRim", "genshinHairAtlas", "endfieldHair", "endfieldMatcap", "genshinMetallic"]
            : [..baseFields, "endfieldRim", "genshinHairAtlas", "endfieldHair", "endfieldMatcap", "genshinMetallic", "starMcBody"]);

        int localMaterialIndex = Integer(row, "localMaterialIndex");
        var role = Enumeration<D3D12CharacterMaterialRole>(row, "role");
        float faceNormalShadeStrength = Number(row, "faceNormalShadeStrength");
        float faceGeometricShadowStrength = Number(row, "faceGeometricShadowStrength");
        float eyeGeometricShadowStrength = Number(row, "eyeGeometricShadowStrength");
        var treatment = Optional(row, "pmxMaterialTreatment", value => Treatment(value, schema));

        var pluginBindings = new List<StyleMaterialBindingData>();
        var rawGuards = new List<D3D12MaterialBindingProfileTexture>();

        var face = Optional(row, "face", value => ParseLegacyFace(value, rawGuards));
        var genshinHair = Optional(row, "genshinHair", value => ParseLegacyGenshinHair(value, rawGuards));
        var starMcHair = Optional(row, "starMcHair", value => ParseLegacyStarMcHair(value, rawGuards));
        var endfieldRim = schema == 1 ? null : Optional(row, "endfieldRim", ParseLegacyEndfieldRim);
        var genshinHairAtlas = schema < 3 ? null : Optional(row, "genshinHairAtlas", value => ParseLegacyGenshinHairAtlas(value, rawGuards));
        var endfieldHair = schema < 4 ? null : Optional(row, "endfieldHair", value => ParseLegacyEndfieldHair(value, rawGuards));
        var endfieldMatcap = schema < 5 ? null : Optional(row, "endfieldMatcap", value => ParseLegacyEndfieldMatcap(value, schema, rawGuards));
        var genshinMetallic = schema < 6 ? null : Optional(row, "genshinMetallic", value => ParseLegacyGenshinMetallic(value, schema, rawGuards));
        var starMcBody = schema < 12 ? null : Optional(row, "starMcBody", value => ParseLegacyStarMcBody(value, rawGuards));

        int dedicated =
            (face is null ? 0 : 1)
            + (genshinHair is null && genshinHairAtlas is null && genshinMetallic is null ? 0 : 1)
            + (starMcHair is null ? 0 : 1)
            + (endfieldRim is null && endfieldHair is null && endfieldMatcap is null ? 0 : 1)
            + (starMcBody is null ? 0 : 1);

        if (dedicated > 1 || (dedicated != 0 && treatment is not null))
            throw new ArgumentException("A material must not combine dedicated inputs or ordinary treatment with dedicated inputs.");
        if (face is not null && role != D3D12CharacterMaterialRole.Face)
            throw new ArgumentException("FaceSDF requires an explicit Face role.");
        if ((genshinHair is not null || starMcHair is not null || endfieldHair is not null) && role != D3D12CharacterMaterialRole.Hair)
            throw new ArgumentException("Dedicated hair requires an explicit Hair role.");
        if (genshinHairAtlas is not null && role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair))
            throw new ArgumentException("Generated atlas adaptation requires explicit Body/Hair; Face and Eye are protected.");
        if (starMcBody is not null && role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair))
            throw new ArgumentException("Star/MC Body adaptation requires an explicit Body/Hair role.");
        if (endfieldRim is not null && role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair))
            throw new ArgumentException("Endfield rim requires an explicit Body/Hair role.");

        if (genshinMetallic is not null)
        {
            if (role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair))
                throw new ArgumentException("Genshin metallic requires an explicit Body/Hair role.");
            if ((genshinHair is not null && genshinHair.Lightmap != genshinMetallic.Lightmap)
                || (genshinHairAtlas is not null && genshinHairAtlas.Lightmap != genshinMetallic.Lightmap))
                throw new ArgumentException("Co-bound metallic, R/B and atlas require the identical guarded UV0 lightmap.");
        }

        if (endfieldMatcap is not null)
        {
            if (role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair))
                throw new ArgumentException("Endfield MatCap requires an explicit Body/Hair role.");
            ValidateLegacyEndfieldPalette(
                endfieldMatcap.BaseColor,
                endfieldMatcap.CoupleEndfieldPalette,
                endfieldMatcap.BaselineStrength,
                endfieldRim,
                endfieldHair?.PaletteColor);
        }

        if (face is not null) pluginBindings.Add(face.Binding);
        if (genshinHair is not null) pluginBindings.Add(genshinHair.Binding);
        if (genshinHairAtlas is not null) pluginBindings.Add(genshinHairAtlas.Binding);
        if (genshinMetallic is not null) pluginBindings.Add(genshinMetallic.Binding);
        if (starMcHair is not null) pluginBindings.Add(starMcHair);
        if (starMcBody is not null) pluginBindings.Add(starMcBody);
        if (endfieldRim is not null) pluginBindings.Add(endfieldRim.Binding);
        if (endfieldHair is not null) pluginBindings.Add(endfieldHair.Binding);
        if (endfieldMatcap is not null) pluginBindings.Add(endfieldMatcap.Binding);

        var dedupedGuards = rawGuards.Distinct().ToArray();
        return new D3D12MaterialBindingProfileRow(
            localMaterialIndex,
            role,
            faceNormalShadeStrength,
            faceGeometricShadowStrength,
            eyeGeometricShadowStrength,
            treatment,
            pluginBindings,
            dedupedGuards,
            explicitRole: true);
    }

    private sealed record ParsedFace(StyleMaterialBindingData Binding);
    private sealed record ParsedGenshinHair(D3D12MaterialBindingProfileTexture Lightmap, StyleMaterialBindingData Binding);
    private sealed record ParsedGenshinAtlas(D3D12MaterialBindingProfileTexture Lightmap, StyleMaterialBindingData Binding);
    private sealed record ParsedGenshinMetallic(D3D12MaterialBindingProfileTexture Lightmap, StyleMaterialBindingData Binding);
    private sealed record ParsedEndfieldRim(Vector3 BaseColor, float RangeStrength, float RadialStrength, StyleMaterialBindingData Binding);
    private sealed record ParsedEndfieldHair(Vector3 PaletteColor, StyleMaterialBindingData Binding);
    private sealed record ParsedEndfieldMatcap(Vector3 BaseColor, bool CoupleEndfieldPalette, float BaselineStrength, StyleMaterialBindingData Binding);

    private static ParsedFace ParseLegacyFace(JsonElement value, List<D3D12MaterialBindingProfileTexture> guards)
    {
        Fields(value, "pipelineId", "primary", "secondary", "headBasis", "headWorldForward", "headWorldRight", "transition", "offset", "encoding");
        string pipelineId = Text(value, "pipelineId");
        var primary = ImageTexture(Required(value, "primary"), guards, "FaceSDF primary");
        var secondary = Optional(value, "secondary", tex => ImageTexture(tex, guards, "FaceSDF secondary"));
        if (Text(value, "headBasis") != "FixedWorld")
            throw new InvalidDataException("Only FixedWorld head axes can be persisted without a guarded skeleton layout.");
        var forward = Vector(Required(value, "headWorldForward"));
        var right = Vector(Required(value, "headWorldRight"));
        float transition = Number(value, "transition");
        float offset = Number(value, "offset");
        string encoding = StringEnum(value, "encoding", ValidFaceEncodings);

        bool compatible = encoding switch
        {
            "GrayRed" => pipelineId is "star-rail" or "wuthering-waves",
            "GenshinSoftFace" => pipelineId == "genshin",
            "EndfieldRgbCmM" => pipelineId == "endfield",
            _ => false,
        };
        if (!compatible)
            throw new ArgumentException("Face encoding must match its explicitly selected pipeline.");

        if (!IsOrthonormalAxes(forward, right))
            throw new ArgumentException("FaceSDF FixedWorld head axes must be finite unit vectors that are orthogonal.");
        if (!float.IsFinite(transition) || transition <= 0f)
            throw new ArgumentOutOfRangeException(nameof(transition), "FaceSDF transition must be finite and positive.");
        if (!float.IsFinite(offset))
            throw new ArgumentOutOfRangeException(nameof(offset), "FaceSDF offset must be finite.");

        var slots = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["primary"] = primary.SourceSceneTextureOrdinal
        };
        if (secondary is not null)
            slots["secondary"] = secondary.SourceSceneTextureOrdinal;

        var numbers = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["transition"] = transition,
            ["offset"] = offset
        };
        var strings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["encoding"] = encoding
        };

        return new ParsedFace(new StyleMaterialBindingData(
            pipelineId,
            "face-sdf",
            new ReadOnlyDictionary<string, int>(slots),
            new ReadOnlyDictionary<string, double>(numbers),
            StringValues: new ReadOnlyDictionary<string, string>(strings),
            BoneBinding: new StyleBoneAxisBinding(StyleBoneTrackingMode.FixedWorld, FixedWorldForward: forward, FixedWorldRight: right)));
    }

    private static ParsedGenshinHair ParseLegacyGenshinHair(JsonElement value, List<D3D12MaterialBindingProfileTexture> guards)
    {
        Fields(value, "texture", "globalSpecularScale", "shininess", "multiplier", "intensity");
        var texture = TextureGuard(Required(value, "texture"), guards);
        float globalScale = NonNegativeNumber(value, "globalSpecularScale");
        float shininess = PositiveNumber(value, "shininess");
        float multiplier = NonNegativeNumber(value, "multiplier");
        float intensity = UnitNumber(value, "intensity");

        return new ParsedGenshinHair(texture, new StyleMaterialBindingData(
            "genshin",
            "hair",
            SingleSlot("texture", texture.SourceSceneTextureOrdinal),
            Dict(
                ("globalSpecularScale", globalScale),
                ("shininess", shininess),
                ("multiplier", multiplier),
                ("intensity", intensity))));
    }

    private static ParsedGenshinAtlas ParseLegacyGenshinHairAtlas(JsonElement value, List<D3D12MaterialBindingProfileTexture> guards)
    {
        Fields(value, "lightmap", "atlas", "atlasEncoding", "explicitRowId", "dayNight", "shadowPosition", "shadowRampWidth",
            "vertexWidth", "vertexAo", "useLightmapAo", "useVertexColorAo");
        var lightmap = TextureGuard(Required(value, "lightmap"), guards);
        var atlas = TextureGuard(Required(value, "atlas"), guards);
        string atlasEncoding = StringEnum(value, "atlasEncoding", ValidAtlasEncodings);
        float explicitRowId = Number(value, "explicitRowId");
        float dayNight = Number(value, "dayNight");
        float shadowPosition = Number(value, "shadowPosition");
        float shadowRampWidth = Number(value, "shadowRampWidth");
        float vertexWidth = Number(value, "vertexWidth");
        float vertexAo = Number(value, "vertexAo");
        float useLightmapAo = Number(value, "useLightmapAo");
        float useVertexColorAo = Number(value, "useVertexColorAo");

        if (explicitRowId is < 1f or > 255f)
            throw new ArgumentOutOfRangeException(nameof(explicitRowId));
        if (shadowRampWidth <= 0f || shadowRampWidth > 1f)
            throw new ArgumentOutOfRangeException(nameof(shadowRampWidth));

        var slots = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["lightmap"] = lightmap.SourceSceneTextureOrdinal,
            ["atlas"] = atlas.SourceSceneTextureOrdinal
        };
        var numbers = Dict(
            ("explicitRowId", explicitRowId),
            ("dayNight", dayNight),
            ("shadowPosition", shadowPosition),
            ("shadowRampWidth", shadowRampWidth),
            ("vertexWidth", vertexWidth),
            ("vertexAo", vertexAo),
            ("useLightmapAo", useLightmapAo),
            ("useVertexColorAo", useVertexColorAo));

        return new ParsedGenshinAtlas(lightmap, new StyleMaterialBindingData(
            "genshin",
            "hair-atlas",
            new ReadOnlyDictionary<string, int>(slots),
            numbers,
            StringValues: SingleString("atlasEncoding", atlasEncoding)));
    }

    private static ParsedGenshinMetallic ParseLegacyGenshinMetallic(JsonElement value, int schema, List<D3D12MaterialBindingProfileTexture> guards)
    {
        string[] fields =
        [
            "lightmap", "matcap", "matcapEncoding", "darkColor", "lightColor", "shadowMultiplyColor",
            "enableMetallics", "tileScale", "brightness", "litMode", "uniformLitFactor"
        ];
        Fields(value, schema < 7 ? fields : [..fields, "specular"]);

        var lightmap = ImageTexture(Required(value, "lightmap"), guards, "Genshin metallic lightmap");
        var matcap = ImageTexture(Required(value, "matcap"), guards, "Genshin metallic matcap");
        string matcapEncoding = StringEnum(value, "matcapEncoding", ValidMatcapEncodings);
        var darkColor = RangeColor(value, "darkColor", -16f, 16f);
        var lightColor = RangeColor(value, "lightColor", -16f, 16f);
        var shadowMultiplyColor = RangeColor(value, "shadowMultiplyColor", -16f, 16f);
        float enableMetallics = RangeNumber(value, "enableMetallics", -32f, 32f);
        float tileScale = RangeNumber(value, "tileScale", -32f, 32f);
        float brightness = RangeNumber(value, "brightness", -32f, 32f);
        string litMode = StringEnum(value, "litMode", ValidMetallicLitModes);
        float uniformLitFactor = RangeNumber(value, "uniformLitFactor", -32f, 32f);

        var slots = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["lightmap"] = lightmap.SourceSceneTextureOrdinal,
            ["matcap"] = matcap.SourceSceneTextureOrdinal
        };
        var numbers = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["enableMetallics"] = enableMetallics,
            ["tileScale"] = tileScale,
            ["brightness"] = brightness,
            ["uniformLitFactor"] = uniformLitFactor
        };
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal)
        {
            ["darkColor"] = Vec(darkColor),
            ["lightColor"] = Vec(lightColor),
            ["shadowMultiplyColor"] = Vec(shadowMultiplyColor)
        };
        var strings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["matcapEncoding"] = matcapEncoding,
            ["litMode"] = litMode
        };
        Dictionary<string, bool>? bools = null;

        if (schema >= 7 && OptionalElement(value, "specular") is JsonElement spec)
        {
            Fields(spec, "color", "sharpColor", "shininess", "scale", "attenuation", "sharpOffset", "ramp", "rampMix");
            var color = RangeColor(spec, "color", -16f, 16f);
            var sharpColor = RangeColor(spec, "sharpColor", -16f, 16f);
            float shininess = RangeNumber(spec, "shininess", 0f, 256f);
            float scale = RangeNumber(spec, "scale", 0f, 32f);
            float attenuation = RangeNumber(spec, "attenuation", 0f, 32f);
            float sharpOffset = RangeNumber(spec, "sharpOffset", -32f, 32f);
            var ramp = Optional(spec, "ramp", tex => ImageTexture(tex, guards, "Genshin metallic specular ramp"));
            float rampMix = RangeNumber(spec, "rampMix", -32f, 32f);
            if (rampMix > 0f && ramp is null)
                throw new ArgumentException("An active metallic specular ramp requires an explicit numerical RGB image index.", "ramp");

            vectors["specularColor"] = Vec(color);
            vectors["specularSharpColor"] = Vec(sharpColor);
            numbers["specularShininess"] = shininess;
            numbers["specularScale"] = scale;
            numbers["specularAttenuation"] = attenuation;
            numbers["specularSharpOffset"] = sharpOffset;
            numbers["specularRampMix"] = rampMix;
            bools = new Dictionary<string, bool>(StringComparer.Ordinal) { ["hasSpecular"] = true };
            if (ramp is not null)
                slots["specularRamp"] = ramp.SourceSceneTextureOrdinal;
        }

        return new ParsedGenshinMetallic(lightmap, new StyleMaterialBindingData(
            "genshin",
            "metallic",
            new ReadOnlyDictionary<string, int>(slots),
            new ReadOnlyDictionary<string, double>(numbers),
            bools is null ? null : new ReadOnlyDictionary<string, bool>(bools),
            new ReadOnlyDictionary<string, float[]>(vectors),
            new ReadOnlyDictionary<string, string>(strings)));
    }

    private static StyleMaterialBindingData ParseLegacyStarMcHair(JsonElement value, List<D3D12MaterialBindingProfileTexture> guards)
    {
        Fields(value, "pipelineId", "highlight", "shadow", "highlightChannel", "highlightCoefficient",
            "highlightBrightness", "shadowCoefficient", "shadowSoftness", "intensity");
        string pipelineId = Text(value, "pipelineId");
        if (pipelineId is not ("star-rail" or "wuthering-waves"))
            throw new ArgumentException("Star/MC hair requires an explicit supported pipeline.");
        var highlight = TextureGuard(Required(value, "highlight"), guards);
        var shadow = TextureGuard(Required(value, "shadow"), guards);
        string channel = StringEnum(value, "highlightChannel", ValidHairHighlightChannels);
        if (pipelineId == "star-rail" && channel != "Blue")
            throw new ArgumentException("Star Rail hair consumes Blue; Alpha is an explicit MC-only selection.");

        float highlightCoefficient = Number(value, "highlightCoefficient");
        float highlightBrightness = NonNegativeNumber(value, "highlightBrightness");
        float shadowCoefficient = Number(value, "shadowCoefficient");
        float shadowSoftness = PositiveNumber(value, "shadowSoftness");
        float intensity = UnitNumber(value, "intensity");

        var slots = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["highlight"] = highlight.SourceSceneTextureOrdinal,
            ["shadow"] = shadow.SourceSceneTextureOrdinal
        };
        return new StyleMaterialBindingData(
            pipelineId,
            "hair",
            new ReadOnlyDictionary<string, int>(slots),
            Dict(
                ("highlightCoefficient", highlightCoefficient),
                ("highlightBrightness", highlightBrightness),
                ("shadowCoefficient", shadowCoefficient),
                ("shadowSoftness", shadowSoftness),
                ("intensity", intensity)),
            StringValues: SingleString("highlightChannel", channel));
    }

    private static StyleMaterialBindingData ParseLegacyStarMcBody(JsonElement value, List<D3D12MaterialBindingProfileTexture> guards)
    {
        Fields(value, "pipelineId", "normal", "detail", "mask", "matcap", "matcapEncoding", "uvChannel",
            "normalStrength", "detailStrength", "metallicSaturation", "highlightCoefficient", "highlightStrength",
            "rotationRadians", "shadowCoefficient", "shadowSoftness", "shadowColorStrength", "warmBandStrength",
            "shadowColor", "rimColor", "rimStrength", "rimSize", "rimDirectionRadians", "generatorRevision");

        string pipelineId = Text(value, "pipelineId");
        if (pipelineId is not ("star-rail" or "wuthering-waves"))
            throw new ArgumentException("Body inputs require an explicit Star or MC pipeline.");
        var normal = ImageTexture(Required(value, "normal"), guards, "Star/MC body normal");
        var detail = ImageTexture(Required(value, "detail"), guards, "Star/MC body detail");
        var mask = ImageTexture(Required(value, "mask"), guards, "Star/MC body mask");
        var matcap = ImageTexture(Required(value, "matcap"), guards, "Star/MC body matcap");
        string encoding = StringEnum(value, "matcapEncoding", ValidMatcapEncodings);
        int uvChannel = Integer(value, "uvChannel");
        if (uvChannel is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(uvChannel));
        if (Text(value, "generatorRevision") != "mikk-v1")
            throw new ArgumentException("Body inputs require the supported mikk-v1 tangent generator.");

        float normalStrength = NonNegativeNumber(value, "normalStrength");
        float detailStrength = NonNegativeNumber(value, "detailStrength");
        float metallicSaturation = NonNegativeNumber(value, "metallicSaturation");
        float highlightCoefficient = Number(value, "highlightCoefficient");
        float highlightStrength = NonNegativeNumber(value, "highlightStrength");
        float rotationRadians = Number(value, "rotationRadians");
        float shadowCoefficient = Number(value, "shadowCoefficient");
        float shadowSoftness = PositiveNumber(value, "shadowSoftness");
        float shadowColorStrength = UnitNumber(value, "shadowColorStrength");
        float warmBandStrength = NonNegativeNumber(value, "warmBandStrength");
        float rimStrength = NonNegativeNumber(value, "rimStrength");
        float rimSize = PositiveNumber(value, "rimSize");
        float rimDirectionRadians = Number(value, "rimDirectionRadians");

        var shadowField = Required(value, "shadowColor");
        var rimField = Required(value, "rimColor");
        Vector3? shadowColor = shadowField.ValueKind == JsonValueKind.Null ? null : NonNegativeVec(shadowField);
        Vector3? rimColor = rimField.ValueKind == JsonValueKind.Null ? null : NonNegativeVec(rimField);

        var slots = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["normal"] = normal.SourceSceneTextureOrdinal,
            ["detail"] = detail.SourceSceneTextureOrdinal,
            ["mask"] = mask.SourceSceneTextureOrdinal,
            ["matcap"] = matcap.SourceSceneTextureOrdinal
        };
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
        if (shadowColor.HasValue) vectors["shadowColor"] = Vec(shadowColor.Value);
        if (rimColor.HasValue) vectors["rimColor"] = Vec(rimColor.Value);

        var strings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["matcapEncoding"] = encoding,
            ["generatorRevision"] = "mikk-v1"
        };

        return new StyleMaterialBindingData(
            pipelineId,
            "body",
            new ReadOnlyDictionary<string, int>(slots),
            Dict(
                ("uvChannel", uvChannel),
                ("normalStrength", normalStrength),
                ("detailStrength", detailStrength),
                ("metallicSaturation", metallicSaturation),
                ("highlightCoefficient", highlightCoefficient),
                ("highlightStrength", highlightStrength),
                ("rotationRadians", rotationRadians),
                ("shadowCoefficient", shadowCoefficient),
                ("shadowSoftness", shadowSoftness),
                ("shadowColorStrength", shadowColorStrength),
                ("warmBandStrength", warmBandStrength),
                ("rimStrength", rimStrength),
                ("rimSize", rimSize),
                ("rimDirectionRadians", rimDirectionRadians)),
            VectorValues: vectors.Count == 0 ? null : new ReadOnlyDictionary<string, float[]>(vectors),
            StringValues: new ReadOnlyDictionary<string, string>(strings));
    }

    private static ParsedEndfieldRim ParseLegacyEndfieldRim(JsonElement value)
    {
        Fields(value, "baseColor", "rimColor", "edgeSize", "edgeStrength", "directionRadians", "rangeStrength", "radialStrength");
        var baseColor = RangeColor(value, "baseColor", 0f, 16f);
        var rimColor = RangeColor(value, "rimColor", 0f, 16f);
        float edgeSize = RangeNumber(value, "edgeSize", -16f, 16f);
        float edgeStrength = RangeNumber(value, "edgeStrength", -32f, 32f);
        float directionRadians = RangeNumber(value, "directionRadians", -65536f, 65536f);
        float rangeStrength = RangeNumber(value, "rangeStrength", -32f, 32f);
        float radialStrength = RangeNumber(value, "radialStrength", -32f, 32f);

        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal)
        {
            ["baseColor"] = Vec(baseColor),
            ["rimColor"] = Vec(rimColor)
        };
        return new ParsedEndfieldRim(
            baseColor,
            rangeStrength,
            radialStrength,
            new StyleMaterialBindingData(
                "endfield",
                "rim",
                NumberValues: Dict(
                    ("edgeSize", edgeSize),
                    ("edgeStrength", edgeStrength),
                    ("directionRadians", directionRadians),
                    ("rangeStrength", rangeStrength),
                    ("radialStrength", radialStrength)),
                VectorValues: new ReadOnlyDictionary<string, float[]>(vectors)));
    }

    private static ParsedEndfieldHair ParseLegacyEndfieldHair(JsonElement value, List<D3D12MaterialBindingProfileTexture> guards)
    {
        Fields(value, "texture", "paletteColor", "brightness");
        var texture = TextureGuard(Required(value, "texture"), guards);
        var paletteColor = NonNegativeColor(value, "paletteColor");
        float brightness = Number(value, "brightness");

        return new ParsedEndfieldHair(
            paletteColor,
            new StyleMaterialBindingData(
                "endfield",
                "hair",
                SingleSlot("texture", texture.SourceSceneTextureOrdinal),
                Dict(("brightness", brightness)),
                VectorValues: new ReadOnlyDictionary<string, float[]>(
                    new Dictionary<string, float[]>(StringComparer.Ordinal) { ["paletteColor"] = Vec(paletteColor) })));
    }

    private static ParsedEndfieldMatcap ParseLegacyEndfieldMatcap(JsonElement value, int schema, List<D3D12MaterialBindingProfileTexture> guards)
    {
        string[] fields =
        [
            "normal", "p", "matcap", "matcapEncoding", "uvChannel", "baseColor", "highlightColor",
            "normalStrength", "rotationRadians", "highlightCoefficient", "shadowHardness", "lightControlDirection",
            "baselineStrength", "coupleEndfieldPalette", "generatorRevision"
        ];
        Fields(value, schema < 8 ? fields : schema < 12 ? [..fields, "palette"] : [..fields, "palette", "highlightRange"]);

        var normal = ImageTexture(Required(value, "normal"), guards, "Endfield MatCap normal");
        var p = ImageTexture(Required(value, "p"), guards, "Endfield MatCap P");
        var matcap = ImageTexture(Required(value, "matcap"), guards, "Endfield MatCap color");
        string encoding = StringEnum(value, "matcapEncoding", ValidMatcapEncodings);
        int uvChannel = Integer(value, "uvChannel");
        if (uvChannel is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(uvChannel));
        if (Text(value, "generatorRevision") != "mikk-v1")
            throw new ArgumentException("MatCap requires the supported mikk-v1 tangent generator revision.");

        var baseColor = NonNegativeColor(value, "baseColor");
        var highlightColor = NonNegativeColor(value, "highlightColor");
        float normalStrength = NonNegativeNumber(value, "normalStrength");
        float rotationRadians = Number(value, "rotationRadians");
        float highlightCoefficient = Number(value, "highlightCoefficient");
        float shadowHardness = PositiveNumber(value, "shadowHardness");
        var dirElem = Required(value, "lightControlDirection");
        Vector3? lightControlDirection = dirElem.ValueKind == JsonValueKind.Null ? null : Vector(dirElem);
        if (lightControlDirection is { } ldir && ldir.LengthSquared() < 1e-6f)
            throw new ArgumentException("LightControlDirection must be non-zero when provided.");
        float baselineStrength = NonNegativeNumber(value, "baselineStrength");
        bool couplePalette = Boolean(value, "coupleEndfieldPalette");

        var slots = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["normal"] = normal.SourceSceneTextureOrdinal,
            ["p"] = p.SourceSceneTextureOrdinal,
            ["matcap"] = matcap.SourceSceneTextureOrdinal
        };
        var numbers = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["uvChannel"] = uvChannel,
            ["normalStrength"] = normalStrength,
            ["rotationRadians"] = rotationRadians,
            ["highlightCoefficient"] = highlightCoefficient,
            ["shadowHardness"] = shadowHardness,
            ["baselineStrength"] = baselineStrength
        };
        var bools = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["coupleEndfieldPalette"] = couplePalette
        };
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal)
        {
            ["baseColor"] = Vec(baseColor),
            ["highlightColor"] = Vec(highlightColor)
        };
        var strings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["matcapEncoding"] = encoding,
            ["generatorRevision"] = "mikk-v1"
        };
        if (lightControlDirection.HasValue)
            vectors["lightControlDirection"] = Vec(lightControlDirection.Value);

        if (schema >= 8 && OptionalElement(value, "palette") is JsonElement paletteElem)
            ParseLegacyEndfieldPalette(paletteElem, schema, guards, slots, numbers, bools, vectors, strings);

        if (schema >= 12 && OptionalElement(value, "highlightRange") is JsonElement rangeElem)
            ParseLegacyHighlightRange(rangeElem, numbers, bools, vectors);

        return new ParsedEndfieldMatcap(
            baseColor,
            couplePalette,
            baselineStrength,
            new StyleMaterialBindingData(
                "endfield",
                "matcap",
                new ReadOnlyDictionary<string, int>(slots),
                new ReadOnlyDictionary<string, double>(numbers),
                new ReadOnlyDictionary<string, bool>(bools),
                new ReadOnlyDictionary<string, float[]>(vectors),
                new ReadOnlyDictionary<string, string>(strings)));
    }

    private static void ParseLegacyEndfieldPalette(
        JsonElement value,
        int schema,
        List<D3D12MaterialBindingProfileTexture> guards,
        Dictionary<string, int> slots,
        Dictionary<string, double> numbers,
        Dictionary<string, bool> bools,
        Dictionary<string, float[]> vectors,
        Dictionary<string, string> strings)
    {
        string[] fields =
        [
            "receivedLightLinearRgb", "emissionStrength", "uniformEmissionBlue", "lightInfluence", "density",
            "emissionInputMode", "emissionTexture", "receivedLightInputMode", "receivedLightTexture"
        ];
        Fields(value, schema < 9 ? fields : [..fields, "baseImage"]);

        bools["hasPalette"] = true;
        vectors["paletteReceivedLightLinearRgb"] = Vec(NonNegativeColor(value, "receivedLightLinearRgb"));
        numbers["paletteEmissionStrength"] = NonNegativeNumber(value, "emissionStrength");
        numbers["paletteUniformEmissionBlue"] = UnitNumber(value, "uniformEmissionBlue");
        numbers["paletteLightInfluence"] = NonNegativeNumber(value, "lightInfluence");
        numbers["paletteDensity"] = NonNegativeNumber(value, "density");

        string emissionMode = StringEnum(value, "emissionInputMode", ValidPaletteInputModes);
        string receivedMode = StringEnum(value, "receivedLightInputMode", ValidPaletteInputModes);
        if (schema < 12 && receivedMode == "RendererDiffuse")
            throw new InvalidDataException("RendererDiffuse requires material profile schema 12.");
        strings["paletteEmissionInputMode"] = emissionMode;
        strings["paletteReceivedLightInputMode"] = receivedMode;

        var emTex = Optional(value, "emissionTexture", tex => ImageTexture(tex, guards, "Endfield palette emission"));
        if (emTex is not null)
            slots["paletteEmission"] = emTex.SourceSceneTextureOrdinal;
        else if (emissionMode == "Texture")
            throw new ArgumentException("Texture emission mode requires an explicit emission texture.");

        var rlTex = Optional(value, "receivedLightTexture", tex => ImageTexture(tex, guards, "Endfield palette received light"));
        if (rlTex is not null)
            slots["paletteReceivedLight"] = rlTex.SourceSceneTextureOrdinal;
        else if (receivedMode == "Texture")
            throw new ArgumentException("Texture received-light mode requires an explicit received-light texture.");

        if (schema >= 9 && OptionalElement(value, "baseImage") is JsonElement baseElem)
        {
            string[] baseFields =
            [
                "baseImageLinearRgb", "modelColor", "shadowColor", "colorMultiplier", "shadowFactor", "warmTint",
                "contrast", "saturation", "detailBlue", "shadowStrength", "baseInputMode", "baseTexture", "baseEncoding",
                "detailInputMode", "detailTexture", "shadowInputMode", "shadowTexture"
            ];
            Fields(baseElem, schema < 10 ? baseFields : [..baseFields, "facingBase", "directionalShadow"]);

            bools["hasBaseImage"] = true;
            vectors["baseImageLinearRgb"] = Vec(NonNegativeColor(baseElem, "baseImageLinearRgb"));
            vectors["baseModelColor"] = Vec(NonNegativeColor(baseElem, "modelColor"));
            vectors["baseShadowColor"] = Vec(NonNegativeColor(baseElem, "shadowColor"));
            vectors["baseColorMultiplier"] = Vec(NonNegativeColor(baseElem, "colorMultiplier"));
            numbers["baseShadowFactor"] = NonNegativeNumber(baseElem, "shadowFactor");
            numbers["baseWarmTint"] = Number(baseElem, "warmTint");
            numbers["baseContrast"] = NonNegativeNumber(baseElem, "contrast");
            numbers["baseSaturation"] = NonNegativeNumber(baseElem, "saturation");
            numbers["baseDetailBlue"] = UnitNumber(baseElem, "detailBlue");
            numbers["baseShadowStrength"] = NonNegativeNumber(baseElem, "shadowStrength");
            string baseMode = StringEnum(baseElem, "baseInputMode", ValidPaletteInputModes);
            strings["baseInputMode"] = baseMode;
            strings["baseEncoding"] = StringEnum(baseElem, "baseEncoding", ValidMatcapEncodings);
            string detailMode = StringEnum(baseElem, "detailInputMode", ValidPaletteInputModes);
            strings["detailInputMode"] = detailMode;
            string shadowMode = StringEnum(baseElem, "shadowInputMode", ValidPaletteInputModes);
            strings["shadowInputMode"] = shadowMode;

            var bTex = Optional(baseElem, "baseTexture", tex => ImageTexture(tex, guards, "Endfield Cpre base"));
            if (bTex is not null)
                slots["baseImage"] = bTex.SourceSceneTextureOrdinal;
            else if (baseMode == "Texture")
                throw new ArgumentException("Texture Base input mode requires an explicit Base texture.");

            var dTex = Optional(baseElem, "detailTexture", tex => ImageTexture(tex, guards, "Endfield Cpre detail"));
            if (dTex is not null)
                slots["baseImageDetail"] = dTex.SourceSceneTextureOrdinal;
            else if (detailMode == "Texture")
                throw new ArgumentException("Texture Detail input mode requires an explicit Detail texture.");

            var sTex = Optional(baseElem, "shadowTexture", tex => ImageTexture(tex, guards, "Endfield Cpre shadow"));
            if (sTex is not null)
                slots["baseImageShadow"] = sTex.SourceSceneTextureOrdinal;
            else if (shadowMode == "Texture")
                throw new ArgumentException("Texture Shadow input mode requires an explicit Shadow texture.");

            if (schema >= 10 && OptionalElement(baseElem, "facingBase") is JsonElement facingElem)
            {
                Fields(facingElem, "blend");
                bools["hasFacingBase"] = true;
                numbers["facingBaseBlend"] = UnitNumber(facingElem, "blend");
            }
            if (schema >= 10 && OptionalElement(baseElem, "directionalShadow") is JsonElement dirElem)
            {
                Fields(dirElem, "worldDirection", "width", "normalStrength");
                bools["hasBaseDirectionalShadow"] = true;
                var worldDir = Vector(Required(dirElem, "worldDirection"));
                if (worldDir.LengthSquared() < 1e-6f)
                    throw new ArgumentException("Directional shadow worldDirection must be non-zero.");
                vectors["baseDirectionalShadowWorldDirection"] = Vec(worldDir);
                numbers["baseDirectionalShadowWidth"] = NonNegativeNumber(dirElem, "width");
                numbers["baseDirectionalShadowNormalStrength"] = NonNegativeNumber(dirElem, "normalStrength");
            }
        }
    }

    private static void ParseLegacyHighlightRange(
        JsonElement value,
        Dictionary<string, double> numbers,
        Dictionary<string, bool> bools,
        Dictionary<string, float[]> vectors)
    {
        Fields(value, "overallBrightness", "highlightBrightness", "directionalShadow");
        bools["hasHighlightRange"] = true;
        numbers["highlightRangeOverallBrightness"] = NonNegativeNumber(value, "overallBrightness");
        numbers["highlightRangeHighlightBrightness"] = NonNegativeNumber(value, "highlightBrightness");
        if (OptionalElement(value, "directionalShadow") is JsonElement dirElem)
        {
            Fields(dirElem, "worldDirection", "width", "normalStrength");
            bools["hasHighlightRangeDirectionalShadow"] = true;
            var worldDir = Vector(Required(dirElem, "worldDirection"));
            if (worldDir.LengthSquared() < 1e-6f)
                throw new ArgumentException("Highlight range directional shadow worldDirection must be non-zero.");
            vectors["highlightRangeDirectionalShadowWorldDirection"] = Vec(worldDir);
            numbers["highlightRangeDirectionalShadowWidth"] = NonNegativeNumber(dirElem, "width");
            numbers["highlightRangeDirectionalShadowNormalStrength"] = NonNegativeNumber(dirElem, "normalStrength");
        }
    }

    private static void ValidateLegacyEndfieldPalette(
        Vector3 baseColor,
        bool couple,
        float baselineStrength,
        ParsedEndfieldRim? rim,
        Vector3? radialColor)
    {
        if (rim is null && radialColor is null)
            return;
        if (!couple)
            throw new ArgumentException("Same-material MatCap plus rim/radial hair requires explicit CoupleEndfieldPalette.");
        if ((rim is not null && rim.BaseColor != baseColor) || (radialColor is { } color && color != baseColor))
            throw new ArgumentException("Coupled Endfield MatCap, rim and radial hair require an explicitly identical shared BaseColor.");
        if (rim is not null && baselineStrength != rim.RangeStrength + rim.RadialStrength)
            throw new ArgumentException("Coupled MatCap BaselineStrength must explicitly equal rim RangeStrength + RadialStrength.");
        if (radialColor is not null && rim is { RadialStrength: not 0f })
            throw new ArgumentException("Coupled evaluated radial hair requires rim RadialStrength=0 to avoid adding P twice.");
    }

    private static JsonElement? OptionalElement(JsonElement value, string name)
    {
        var field = Required(value, name);
        return field.ValueKind == JsonValueKind.Null ? null : field;
    }

    private static D3D12MaterialBindingProfileTexture TextureGuard(JsonElement elem, List<D3D12MaterialBindingProfileTexture> guards)
    {
        var guard = Texture(elem);
        guard.Validate();
        guards.Add(guard);
        return guard;
    }

    private static D3D12MaterialBindingProfileTexture ImageTexture(JsonElement elem, List<D3D12MaterialBindingProfileTexture> guards, string context)
    {
        var guard = TextureGuard(elem, guards);
        if (guard.Kind != SceneTextureKind.Image)
            throw new ArgumentException($"{context} requires explicit original Image resources.");
        return guard;
    }

    private static string StringEnum(JsonElement value, string name, string[] allowed)
    {
        string text = Text(value, name);
        foreach (string item in allowed)
        {
            if (string.Equals(text, item, StringComparison.Ordinal))
                return item;
        }
        throw new InvalidDataException($"Unknown enum name '{text}' for '{name}'.");
    }

    private static bool IsOrthonormalAxes(Vector3 forward, Vector3 right)
    {
        float fl = forward.Length();
        float rl = right.Length();
        if (!float.IsFinite(fl) || !float.IsFinite(rl) || fl < 1e-4f || rl < 1e-4f)
            return false;
        return MathF.Abs(fl - 1f) <= 0.05f
            && MathF.Abs(rl - 1f) <= 0.05f
            && MathF.Abs(Vector3.Dot(Vector3.Normalize(forward), Vector3.Normalize(right))) <= 0.05f;
    }

    private static float PositiveNumber(JsonElement value, string name)
    {
        float v = Number(value, name);
        if (v <= 0f) throw new ArgumentOutOfRangeException(name, $"'{name}' must be positive.");
        return v;
    }

    private static float NonNegativeNumber(JsonElement value, string name)
    {
        float v = Number(value, name);
        if (v < 0f) throw new ArgumentOutOfRangeException(name, $"'{name}' must be non-negative.");
        return v;
    }

    private static float UnitNumber(JsonElement value, string name)
    {
        float v = Number(value, name);
        if (v is < 0f or > 1f) throw new ArgumentOutOfRangeException(name, $"'{name}' must be in [0,1].");
        return v;
    }

    private static float RangeNumber(JsonElement value, string name, float min, float max)
    {
        float v = Number(value, name);
        if (!float.IsFinite(v) || v < min || v > max)
            throw new ArgumentOutOfRangeException(name, $"'{name}' must be finite and in [{min},{max}].");
        return v;
    }

    private static Vector3 RangeColor(JsonElement value, string name, float min, float max)
    {
        var v = Vector(Required(value, name));
        if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)
            || v.X < min || v.X > max || v.Y < min || v.Y > max || v.Z < min || v.Z > max)
            throw new ArgumentOutOfRangeException(name, $"'{name}' color components must be finite and in [{min},{max}].");
        return v;
    }

    private static Vector3 NonNegativeColor(JsonElement value, string name) => NonNegativeVec(Required(value, name));

    private static Vector3 NonNegativeVec(JsonElement value)
    {
        var v = Vector(value);
        if (v.X < 0f || v.Y < 0f || v.Z < 0f)
            throw new ArgumentOutOfRangeException(nameof(value), "Color components must be non-negative.");
        return v;
    }

    private static float[] Vec(Vector3 v) => [v.X, v.Y, v.Z];

    private static IReadOnlyDictionary<string, int> SingleSlot(string key, int slot)
        => new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal) { [key] = slot });

    private static IReadOnlyDictionary<string, string> SingleString(string key, string value)
        => new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value });

    private static IReadOnlyDictionary<string, double> Dict(params (string Key, double Value)[] entries)
    {
        var dict = new Dictionary<string, double>(entries.Length, StringComparer.Ordinal);
        foreach (var (key, value) in entries)
            dict.Add(key, value);
        return new ReadOnlyDictionary<string, double>(dict);
    }
}
