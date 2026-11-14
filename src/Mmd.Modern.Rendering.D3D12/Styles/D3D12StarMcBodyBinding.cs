using System.Numerics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Explicit Body inputs for the independent Star/MC adapter. Numerical maps use
/// mip-zero bilinear repeat: normal.RG (MC only), detail.G (MC only), mask.R shadow and
/// mask.B highlight. MatCap RGB is color, alpha never changes coverage. Geometry is an
/// optional capture of the same evaluated pose, normals and selected effective UVs.
/// No plugin images or node implementation are incorporated.</summary>
public sealed record D3D12StarMcBodyBinding(
    int NormalCombinedTextureIndex, int DetailCombinedTextureIndex, int MaskCombinedTextureIndex,
    int MatcapCombinedTextureIndex, D3D12MatcapColorEncoding MatcapEncoding,
    int UvChannel = 0, float NormalStrength = 1, float DetailStrength = 1,
    float MetallicSaturation = 1, float HighlightCoefficient = 0, float HighlightStrength = 1,
    float RotationRadians = 0, float ShadowCoefficient = .5f, float ShadowSoftness = .1f,
    float ShadowColorStrength = 1, float WarmBandStrength = .25f, Vector3? ShadowColor = null,
    Vector3? RimColor = null, float RimStrength = 0, float RimSize = .75f,
    float RimDirectionRadians = 0, D3D12EndfieldMatcapGeometry? Geometry = null)
{
    public Vector3 EffectiveShadowColor => ShadowColor ?? new Vector3(.62f, .66f, .72f);
    public Vector3 EffectiveRimColor => RimColor ?? new Vector3(.85f, .9f, 1);

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(NormalCombinedTextureIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(DetailCombinedTextureIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(MaskCombinedTextureIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(MatcapCombinedTextureIndex);
        if (!Enum.IsDefined(MatcapEncoding)) throw new ArgumentOutOfRangeException(nameof(MatcapEncoding));
        if (UvChannel is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(UvChannel));
        Bound(NormalStrength, 0, 8, nameof(NormalStrength)); Bound(DetailStrength, 0, 1, nameof(DetailStrength));
        Bound(MetallicSaturation, 0, 4, nameof(MetallicSaturation));
        Bound(HighlightCoefficient, -1, 1, nameof(HighlightCoefficient)); Bound(HighlightStrength, 0, 1, nameof(HighlightStrength));
        Bound(RotationRadians, -65536, 65536, nameof(RotationRadians));
        Bound(ShadowCoefficient, 0, 1, nameof(ShadowCoefficient)); Bound(ShadowSoftness, .00001f, 1, nameof(ShadowSoftness));
        Bound(ShadowColorStrength, 0, 1, nameof(ShadowColorStrength)); Bound(WarmBandStrength, 0, 1, nameof(WarmBandStrength));
        Color(EffectiveShadowColor, nameof(ShadowColor)); Color(EffectiveRimColor, nameof(RimColor));
        Bound(RimStrength, 0, 1, nameof(RimStrength)); Bound(RimSize, 0, 4, nameof(RimSize));
        Bound(RimDirectionRadians, -65536, 65536, nameof(RimDirectionRadians));
    }

    // Shares only evaluated geometry/resource plumbing with the Endfield adapter.
    internal D3D12EndfieldMatcapBinding GeometryAdapter(bool wuthering) => new(
        NormalCombinedTextureIndex, MaskCombinedTextureIndex, MatcapCombinedTextureIndex, MatcapEncoding,
        UvChannel, Vector3.Zero, Vector3.Zero, NormalStrength: wuthering ? NormalStrength : 0,
        RotationRadians: RotationRadians, Geometry: Geometry);

    internal static bool ConsumedEquivalent(D3D12StarMcBodyBinding? a, D3D12StarMcBodyBinding? b, bool wuthering, bool compareIndices)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        // Geometry is compared by consumed appearance, texture slots by captured channels.
        return (!compareIndices || a.MaskCombinedTextureIndex == b.MaskCombinedTextureIndex
            && a.MatcapCombinedTextureIndex == b.MatcapCombinedTextureIndex
            && (!wuthering || a.NormalCombinedTextureIndex == b.NormalCombinedTextureIndex
                && a.DetailCombinedTextureIndex == b.DetailCombinedTextureIndex))
            && a.MatcapEncoding == b.MatcapEncoding && a.UvChannel == b.UvChannel
            && (!wuthering || a.NormalStrength == b.NormalStrength && a.DetailStrength == b.DetailStrength)
            && a.MetallicSaturation == b.MetallicSaturation && a.HighlightCoefficient == b.HighlightCoefficient
            && a.HighlightStrength == b.HighlightStrength && a.RotationRadians == b.RotationRadians
            && a.ShadowCoefficient == b.ShadowCoefficient && a.ShadowSoftness == b.ShadowSoftness
            && a.ShadowColorStrength == b.ShadowColorStrength && a.WarmBandStrength == b.WarmBandStrength
            && a.EffectiveShadowColor == b.EffectiveShadowColor && a.EffectiveRimColor == b.EffectiveRimColor
            && a.RimStrength == b.RimStrength && a.RimSize == b.RimSize && a.RimDirectionRadians == b.RimDirectionRadians;
    }
    private static void Bound(float x, float min, float max, string name)
    { if (!float.IsFinite(x) || x < min || x > max) throw new ArgumentOutOfRangeException(name); }
    private static void Color(Vector3 x, string name)
    { Bound(x.X, 0, 1, name); Bound(x.Y, 0, 1, name); Bound(x.Z, 0, 1, name); }
}
