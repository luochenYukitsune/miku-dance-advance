using System.Numerics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Explicit Endfield geometry/camera rim mask and MDA palette-delta adapter.
/// C0=<see cref="BaseColor"/>, B=<see cref="RimColor"/>, R=recovered rim scalar:
/// delta=Overlay(C0,B,saturate(R))*(RangeStrength+RadialStrength+R)-C0*(RangeStrength+RadialStrength).
/// This signed delta is added once without scaling accumulated PMX highlights/GI or changing coverage.
/// The caller supplies the palette and both baseline strengths; no upstream HA/HB palette,
/// MatCap, wave or dynamic radial coupling is inferred. Existing EndfieldHair remains independent.
/// No texture, UV, rest seed or head bone is needed. Zero EdgeStrength is exact Off.
/// Validation bounds are MDA numerical domains, not recovered source UI clamps.</summary>
public sealed record D3D12EndfieldRimBinding(
    Vector3 BaseColor,
    Vector3 RimColor,
    float EdgeSize=.2f,
    float EdgeStrength=0f,
    float DirectionRadians=5.5f,
    float RangeStrength=1f,
    float RadialStrength=0f)
{
    public void Validate()
    {
        Color(BaseColor,nameof(BaseColor));Color(RimColor,nameof(RimColor));
        Scalar(EdgeSize,16,nameof(EdgeSize));Scalar(EdgeStrength,32,nameof(EdgeStrength));
        Scalar(DirectionRadians,65536,nameof(DirectionRadians));
        Scalar(RangeStrength,32,nameof(RangeStrength));Scalar(RadialStrength,32,nameof(RadialStrength));
    }

    private static void Color(Vector3 value,string name)
    {
        if(!float.IsFinite(value.X)||!float.IsFinite(value.Y)||!float.IsFinite(value.Z)
            ||value.X is <0 or >16||value.Y is <0 or >16||value.Z is <0 or >16)
            throw new ArgumentOutOfRangeException(name,"Explicit rim palette channels must be finite and in [0,16].");
    }
    private static void Scalar(float value,float bound,string name)
    {
        if(!float.IsFinite(value)||value < -bound||value > bound)
            throw new ArgumentOutOfRangeException(name,$"Explicit rim scalar must be finite and in [-{bound},{bound}].");
    }
}
