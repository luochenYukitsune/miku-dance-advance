using System.Numerics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Explicit ordinary-PMX palette, shade and diffuse-transition treatment.
/// Identity values preserve the established surface response. This is an MDA adapter,
/// independent of source texture data and dedicated game-material bindings.</summary>
public sealed record D3D12PmxMaterialTreatment(float Saturation = 1f, float TransitionWidthScale = 1f,
    float ValueContrast = 0f, float ShadeGamma = 1f,
    float HairHighlightStrength = 0f, float HairHighlightWidth = .12f)
{
    public void Validate()
    {
        if (!float.IsFinite(Saturation) || Saturation is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(Saturation), "Ordinary-PMX saturation must be finite and in [0,2].");
        if (!float.IsFinite(TransitionWidthScale) || TransitionWidthScale is < .5f or > 1.5f)
            throw new ArgumentOutOfRangeException(nameof(TransitionWidthScale), "Ordinary-PMX transition width scale must be finite and in [.5,1.5].");
        if (!float.IsFinite(ValueContrast) || ValueContrast is < -.5f or > .5f)
            throw new ArgumentOutOfRangeException(nameof(ValueContrast), "Ordinary-PMX value contrast must be finite and in [-.5,.5].");
        if (!float.IsFinite(ShadeGamma) || ShadeGamma is < .5f or > 2f)
            throw new ArgumentOutOfRangeException(nameof(ShadeGamma), "Ordinary-PMX shade gamma must be finite and in [.5,2].");
        if (!float.IsFinite(HairHighlightStrength) || HairHighlightStrength is < 0 or > .4f)
            throw new ArgumentOutOfRangeException(nameof(HairHighlightStrength), "Ordinary-PMX hair highlight strength must be finite and in [0,.4].");
        if (!float.IsFinite(HairHighlightWidth) || HairHighlightWidth is < .02f or > .4f)
            throw new ArgumentOutOfRangeException(nameof(HairHighlightWidth), "Ordinary-PMX hair highlight width must be finite and in [.02,.4].");
    }

    /// <summary>Transforms each authored shade RGB channel in [0,1] while retaining
    /// signed/HDR artist coefficients and alpha exactly. Identity bypasses Pow.</summary>
    public Vector4 TransformShade(Vector4 shade) => ShadeGamma == 1f ? shade
        : new(TransformShadeChannel(shade.X), TransformShadeChannel(shade.Y), TransformShadeChannel(shade.Z), shade.W);

    private float TransformShadeChannel(float value) => value is >= 0 and <= 1 ? MathF.Pow(value, ShadeGamma) : value;

    internal static bool ConsumedEquivalent(D3D12PmxMaterialTreatment? a, D3D12PmxMaterialTreatment? b)
    {
        if (ReferenceEquals(a, b)) return true;
        return a is not null && b is not null
            && a.Saturation == b.Saturation && a.TransitionWidthScale == b.TransitionWidthScale
            && a.ValueContrast == b.ValueContrast && a.ShadeGamma == b.ShadeGamma
            && a.HairHighlightStrength == b.HairHighlightStrength
            && (a.HairHighlightStrength == 0 || a.HairHighlightWidth == b.HairHighlightWidth);
    }
}
