using System.Numerics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Explicit Endfield radial-hair increment with an authored linear HDR palette.
/// Numerical Green uses UV0 linear-repeat mip-zero sampling independently of coverage.
/// This adapts the source radial scalar only; it does not implement complete HA/HB,
/// MatCap, rim ramps or secondary emission. The immutable raw radial seed stream is required.</summary>
public sealed record D3D12EndfieldHairBinding(
    int CombinedTextureIndex,
    Vector3 PaletteColor,
    float Brightness = 0f)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(CombinedTextureIndex);
        if (!Finite(PaletteColor.X) || !Finite(PaletteColor.Y) || !Finite(PaletteColor.Z))
            throw new ArgumentOutOfRangeException(nameof(PaletteColor), "Radial hair palette must be finite and each component in [0,16].");
        if (!float.IsFinite(Brightness) || Brightness is < -10f or > 10f)
            throw new ArgumentOutOfRangeException(nameof(Brightness), "Radial hair brightness must be finite and in [-10,10].");
    }

    private static bool Finite(float value) => float.IsFinite(value) && value is >= 0 and <= 16;

    internal static bool ConsumedEquivalent(D3D12EndfieldHairBinding? a,
        D3D12EndfieldHairBinding? b, bool compareTextureIndices = true)
        => ReferenceEquals(a, b) || a is not null && b is not null
            && (!compareTextureIndices || a.CombinedTextureIndex == b.CombinedTextureIndex)
            && a.PaletteColor == b.PaletteColor && a.Brightness == b.Brightness;
}
