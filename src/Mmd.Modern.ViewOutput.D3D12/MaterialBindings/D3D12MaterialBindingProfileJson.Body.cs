using System.Text.Json;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

public static partial class D3D12MaterialBindingProfileJson
{
    private static D3D12EndfieldPaletteInputMode ReceivedLightMode(JsonElement value, int schema)
    {
        var mode = Enumeration<D3D12EndfieldPaletteInputMode>(value, "receivedLightInputMode");
        if (schema < 12 && mode == D3D12EndfieldPaletteInputMode.RendererDiffuse)
            throw new InvalidDataException("RendererDiffuse requires material profile schema 12.");
        return mode;
    }

    private static D3D12EndfieldHighlightRangeBinding EndfieldHighlightRange(JsonElement value)
    {
        Fields(value, "overallBrightness", "highlightBrightness", "directionalShadow");
        return new(Number(value, "overallBrightness"), Number(value, "highlightBrightness"),
            EndfieldDirectionalShadow(Required(value, "directionalShadow")));
    }

    private static void WriteEndfieldHighlightRange(Utf8JsonWriter w, D3D12EndfieldHighlightRangeBinding value)
    {
        w.WriteNumber("overallBrightness", value.OverallBrightness); w.WriteNumber("highlightBrightness", value.HighlightBrightness);
        WriteOptional(w, "directionalShadow", value.DirectionalShadow, (d, directional) =>
        {
            WriteVector(d, "worldDirection", directional.WorldDirection);
            d.WriteNumber("width", directional.Width); d.WriteNumber("normalStrength", directional.NormalStrength);
        });
    }

    private static D3D12MaterialBindingProfileStarMcBody StarMcBody(JsonElement value)
    {
        Fields(value, "pipelineId", "normal", "detail", "mask", "matcap", "matcapEncoding", "uvChannel",
            "normalStrength", "detailStrength", "metallicSaturation", "highlightCoefficient", "highlightStrength",
            "rotationRadians", "shadowCoefficient", "shadowSoftness", "shadowColorStrength", "warmBandStrength",
            "shadowColor", "rimColor", "rimStrength", "rimSize", "rimDirectionRadians", "generatorRevision");
        var shadow = Required(value, "shadowColor"); var rim = Required(value, "rimColor");
        return new(Text(value, "pipelineId"), Texture(Required(value, "normal")), Texture(Required(value, "detail")),
            Texture(Required(value, "mask")), Texture(Required(value, "matcap")),
            Enumeration<D3D12MatcapColorEncoding>(value, "matcapEncoding"), Integer(value, "uvChannel"),
            Number(value, "normalStrength"), Number(value, "detailStrength"), Number(value, "metallicSaturation"),
            Number(value, "highlightCoefficient"), Number(value, "highlightStrength"), Number(value, "rotationRadians"),
            Number(value, "shadowCoefficient"), Number(value, "shadowSoftness"), Number(value, "shadowColorStrength"),
            Number(value, "warmBandStrength"), shadow.ValueKind == JsonValueKind.Null ? null : Vector(shadow),
            rim.ValueKind == JsonValueKind.Null ? null : Vector(rim), Number(value, "rimStrength"),
            Number(value, "rimSize"), Number(value, "rimDirectionRadians"), Text(value, "generatorRevision"));
    }

    private static void WriteStarMcBody(Utf8JsonWriter w, D3D12MaterialBindingProfileStarMcBody value)
    {
        w.WriteString("pipelineId", value.PipelineId); WriteTexture(w, "normal", value.Normal); WriteTexture(w, "detail", value.Detail);
        WriteTexture(w, "mask", value.Mask); WriteTexture(w, "matcap", value.Matcap); w.WriteString("matcapEncoding", value.MatcapEncoding.ToString());
        w.WriteNumber("uvChannel", value.UvChannel); w.WriteNumber("normalStrength", value.NormalStrength);
        w.WriteNumber("detailStrength", value.DetailStrength); w.WriteNumber("metallicSaturation", value.MetallicSaturation);
        w.WriteNumber("highlightCoefficient", value.HighlightCoefficient); w.WriteNumber("highlightStrength", value.HighlightStrength);
        w.WriteNumber("rotationRadians", value.RotationRadians); w.WriteNumber("shadowCoefficient", value.ShadowCoefficient);
        w.WriteNumber("shadowSoftness", value.ShadowSoftness); w.WriteNumber("shadowColorStrength", value.ShadowColorStrength);
        w.WriteNumber("warmBandStrength", value.WarmBandStrength);
        if (value.ShadowColor is { } shadow) WriteVector(w, "shadowColor", shadow); else w.WriteNull("shadowColor");
        if (value.RimColor is { } rim) WriteVector(w, "rimColor", rim); else w.WriteNull("rimColor");
        w.WriteNumber("rimStrength", value.RimStrength); w.WriteNumber("rimSize", value.RimSize);
        w.WriteNumber("rimDirectionRadians", value.RimDirectionRadians); w.WriteString("generatorRevision", value.GeneratorRevision);
    }
}
