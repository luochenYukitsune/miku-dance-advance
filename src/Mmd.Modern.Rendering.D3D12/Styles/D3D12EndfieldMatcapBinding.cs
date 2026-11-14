using System.Numerics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Authored MatCap RGB transfer function. N/P maps always remain numerical linear data.</summary>
public enum D3D12MatcapColorEncoding { LinearRgb, SrgbRgb }

/// <summary>Explicit packed-RG tangent normal, numerical P.r/P.a source gate and Straight-image
/// MatCap palette adapter. All three images use mip-zero bilinear-repeat sampling. Source image
/// alpha premultiplies the sampled linear color only, never material coverage. Geometry is derived
/// from the same captured evaluated pose/UVs; it is not a rest tangent or radial seed stream.
/// Optional coupling requires an explicitly shared C0 with the selected rim/radial adapters.</summary>
public sealed record D3D12EndfieldMatcapBinding(
    int NormalCombinedTextureIndex,
    int PCombinedTextureIndex,
    int MatcapCombinedTextureIndex,
    D3D12MatcapColorEncoding MatcapEncoding,
    int UvChannel,
    Vector3 BaseColor,
    Vector3 HighlightColor,
    float NormalStrength = 2,
    float RotationRadians = 0,
    float HighlightCoefficient = 0,
    float ShadowHardness = .5f,
    Vector3? LightControlDirection = null,
    float BaselineStrength = 1,
    bool CoupleEndfieldPalette = false,
    D3D12EndfieldMatcapGeometry? Geometry = null,
    D3D12EndfieldPaletteBinding? Palette = null,
    D3D12EndfieldHighlightRangeBinding? HighlightRange = null)
{
    public Vector3 EffectiveLightControlDirection => Vector3.Normalize(LightControlDirection ?? Vector3.UnitZ);

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(NormalCombinedTextureIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(PCombinedTextureIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(MatcapCombinedTextureIndex);
        if (!Enum.IsDefined(MatcapEncoding)) throw new ArgumentOutOfRangeException(nameof(MatcapEncoding));
        if (UvChannel is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(UvChannel));
        Palette?.Validate();
        HighlightRange?.Validate();
        if (Palette is not null && !CoupleEndfieldPalette)
            throw new ArgumentException("The downstream palette requires explicit shared Endfield palette coupling.", nameof(Palette));
        Color(BaseColor, nameof(BaseColor)); Color(HighlightColor, nameof(HighlightColor));
        Scalar(NormalStrength, 32, nameof(NormalStrength));
        Scalar(RotationRadians, 65536, nameof(RotationRadians));
        Scalar(HighlightCoefficient, 32, nameof(HighlightCoefficient));
        Scalar(ShadowHardness, 16, nameof(ShadowHardness));
        Scalar(BaselineStrength, 32, nameof(BaselineStrength));
        if (LightControlDirection is { } direction && (!Finite(direction)
            || direction.LengthSquared() is <= 0 or > 1e20f))
            throw new ArgumentOutOfRangeException(nameof(LightControlDirection), "Explicit world light-control direction must be finite, nonzero and normalizable.");
    }

    internal static bool ConsumedEquivalent(D3D12EndfieldMatcapBinding? a,
        D3D12EndfieldMatcapBinding? b, bool compareTextureIndices = true)
        => ReferenceEquals(a, b) || a is not null && b is not null
            && (!compareTextureIndices || a.NormalCombinedTextureIndex == b.NormalCombinedTextureIndex
                && a.PCombinedTextureIndex == b.PCombinedTextureIndex && a.MatcapCombinedTextureIndex == b.MatcapCombinedTextureIndex)
            && a.MatcapEncoding == b.MatcapEncoding && a.UvChannel == b.UvChannel
            && a.BaseColor == b.BaseColor && a.HighlightColor == b.HighlightColor
            && a.NormalStrength == b.NormalStrength && a.RotationRadians == b.RotationRadians
            && a.HighlightCoefficient == b.HighlightCoefficient && a.ShadowHardness == b.ShadowHardness
            && a.EffectiveLightControlDirection == b.EffectiveLightControlDirection
            && a.BaselineStrength == b.BaselineStrength && a.CoupleEndfieldPalette == b.CoupleEndfieldPalette
            && a.HighlightRange == b.HighlightRange
            && D3D12EndfieldPaletteBinding.ConsumedEquivalent(a.Palette, b.Palette, compareTextureIndices);

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static void Color(Vector3 value, string name)
    {
        if (!Finite(value) || value.X is < 0 or > 16 || value.Y is < 0 or > 16 || value.Z is < 0 or > 16)
            throw new ArgumentOutOfRangeException(name, "Explicit MatCap palette channels must be finite and in [0,16].");
    }
    private static void Scalar(float value, float bound, string name)
    {
        if (!float.IsFinite(value) || value < -bound || value > bound)
            throw new ArgumentOutOfRangeException(name, $"Explicit MatCap scalar must be finite and in [-{bound},{bound}].");
    }
}
