namespace Mmd.Modern.Rendering.D3D12;

// Color texture RGB interpretation is explicit; Alpha never controls coverage.
public enum D3D12GenshinAtlasColorEncoding { LinearRgb, SrgbRgb }

/// <summary>Explicit-row, nonmetallic Hair palette. Source vertex-color width/AO
/// are supplied as already-converted finite scalars, not inferred from PMX colors.
/// This is the source disabled-catch subset plus an independent MDA visibility adapter.
/// MDA may explicitly reuse this palette for generated cloth Body materials; that
/// adaptation does not claim source-Hair equivalence. The optional original R/B
/// specular remains Hair-only and separate.</summary>
public sealed record D3D12GenshinHairShadowAtlasBinding(
    int LightmapCombinedTextureIndex,
    int AtlasCombinedTextureIndex,
    D3D12GenshinAtlasColorEncoding AtlasEncoding,
    float ExplicitRowId,
    float DayNight,
    float ShadowPosition,
    float ShadowRampWidth,
    float VertexWidth,
    float VertexAo,
    float UseLightmapAo,
    float UseVertexColorAo)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(LightmapCombinedTextureIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(AtlasCombinedTextureIndex);
        if (!Enum.IsDefined(AtlasEncoding)) throw new ArgumentOutOfRangeException(nameof(AtlasEncoding));
        Range(ExplicitRowId, 1, 5, nameof(ExplicitRowId));
        Range(DayNight, 0, 1, nameof(DayNight));
        Range(UseLightmapAo, 0, 1, nameof(UseLightmapAo));
        Range(UseVertexColorAo, 0, 1, nameof(UseVertexColorAo));
        // MDA's finite operational domain, not implicit Blender socket clamps.
        // Zero and signed values intentionally remain valid for safe-divide semantics.
        Range(ShadowPosition, -16, 16, nameof(ShadowPosition));
        Range(ShadowRampWidth, -16, 16, nameof(ShadowRampWidth));
        Range(VertexWidth, -16, 16, nameof(VertexWidth));
        Range(VertexAo, -16, 16, nameof(VertexAo));
    }

    internal static bool ConsumedEquivalent(D3D12GenshinHairShadowAtlasBinding? a,
        D3D12GenshinHairShadowAtlasBinding? b, bool compareTextureIndices = true)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return (!compareTextureIndices || a.LightmapCombinedTextureIndex == b.LightmapCombinedTextureIndex
            && a.AtlasCombinedTextureIndex == b.AtlasCombinedTextureIndex)
            && a.AtlasEncoding == b.AtlasEncoding && a.ExplicitRowId == b.ExplicitRowId
            && a.DayNight == b.DayNight && a.ShadowPosition == b.ShadowPosition
            && a.ShadowRampWidth == b.ShadowRampWidth && a.VertexWidth == b.VertexWidth
            && a.VertexAo == b.VertexAo && a.UseLightmapAo == b.UseLightmapAo
            && a.UseVertexColorAo == b.UseVertexColorAo;
    }

    private static void Range(float value, float minimum, float maximum, string name)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, $"Value must be finite in [{minimum},{maximum}].");
    }
}

