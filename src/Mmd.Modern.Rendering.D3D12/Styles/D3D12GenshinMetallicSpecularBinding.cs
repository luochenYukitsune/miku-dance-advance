using System.Numerics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Optional source-local metallic specular lobe attached to an explicit Genshin
/// metallic palette binding. Reuses its UV0 numerical lightmap, R gate and LitMode.
/// Authored RGB uses plain Gamma2.2 once; the optional independent ramp is numerical
/// RGB, Linear Repeat at LOD0 with Alpha ignored. Global scale and intensity are one.
/// Geometry normal and existing primary half direction are a bounded MDA adapter.</summary>
public sealed record D3D12GenshinMetallicSpecularBinding(
    Vector3 Color,
    Vector3 SharpColor,
    float Shininess = 16,
    float Scale = 1,
    float Attenuation = .5f,
    float SharpOffset = 1,
    int? RampCombinedTextureIndex = null,
    float RampMix = 0)
{
    internal bool ConsumesRamp => RampMix > 0;

    public void Validate()
    {
        ValidateColor(Color, nameof(Color)); ValidateColor(SharpColor, nameof(SharpColor));
        Scalar(Shininess, 0, 256, nameof(Shininess));
        Scalar(Scale, 0, 32, nameof(Scale)); Scalar(Attenuation, 0, 32, nameof(Attenuation));
        Scalar(SharpOffset, -32, 32, nameof(SharpOffset)); Scalar(RampMix, -32, 32, nameof(RampMix));
        if (RampCombinedTextureIndex is { } index) ArgumentOutOfRangeException.ThrowIfNegative(index, nameof(RampCombinedTextureIndex));
        if (ConsumesRamp && RampCombinedTextureIndex is null)
            throw new ArgumentException("An active metallic specular ramp requires an explicit numerical RGB image index.", nameof(RampCombinedTextureIndex));
    }

    internal static bool ConsumedEquivalent(D3D12GenshinMetallicSpecularBinding? a,
        D3D12GenshinMetallicSpecularBinding? b, bool compareTextureIndices = true)
        => ReferenceEquals(a, b) || a is not null && b is not null
            && a.Color == b.Color && a.SharpColor == b.SharpColor && a.Shininess == b.Shininess
            && a.Scale == b.Scale && a.Attenuation == b.Attenuation && a.SharpOffset == b.SharpOffset
            && a.RampMix == b.RampMix
            && (!compareTextureIndices || !a.ConsumesRamp || a.RampCombinedTextureIndex == b.RampCombinedTextureIndex);

    private static void ValidateColor(Vector3 value, string name)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)
            || value.X is < -16 or > 16 || value.Y is < -16 or > 16 || value.Z is < -16 or > 16)
            throw new ArgumentOutOfRangeException(name, "Source numeric RGB must be finite and in [-16,16].");
    }

    private static void Scalar(float value, float min, float max, string name)
    {
        if (!float.IsFinite(value) || value < min || value > max)
            throw new ArgumentOutOfRangeException(name, $"Explicit metallic specular scalar must be finite and in [{min},{max}].");
    }
}
