using System.Numerics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>
/// Built-in textureless stylized cel-shading parameters.
/// Deepens shadow along albedo hue, optionally adds a warm terminator band, a lit-side rim,
/// and saturation/contrast/brightness color grading.
/// </summary>
public sealed record D3D12StylizedShading(
    float ShadowStrength, float ShadowValue, float ShadowHue, Vector3 ShadowTint, float FaceFlatten,
    Vector3 WarmColor, float WarmStrength, float RimEdge, float RimStrength,
    float Saturation, float Contrast, float Brightness,
    float OutlineValue = .5f, float OutlineSaturation = 1.3f)
{
    /// <summary>x shadow strength (0 off), y shadow value, z albedo hue deepening, w face flatten.</summary>
    internal Vector4 Shade => new(ShadowStrength, ShadowValue, ShadowHue, FaceFlatten);
    /// <summary>xyz shadow tint, w warm terminator band strength.</summary>
    internal Vector4 Tint => new(ShadowTint, WarmStrength);
    /// <summary>xyz warm terminator color, w rim Fresnel threshold.</summary>
    internal Vector4 Warm => new(WarmColor, RimEdge);
    /// <summary>x saturation, y contrast, z brightness, w rim strength.</summary>
    internal Vector4 Grade => new(Saturation, Contrast, Brightness, RimStrength);

    public void Validate()
    {
        static void Range(float value, float min, float max, string name)
        {
            if (!float.IsFinite(value) || value < min || value > max)
                throw new ArgumentOutOfRangeException(name, $"Stylized shading {name} must be finite and in [{min},{max}].");
        }
        Range(ShadowStrength, 0, 1, nameof(ShadowStrength)); Range(ShadowValue, 0, 1, nameof(ShadowValue));
        Range(ShadowHue, 0, 1, nameof(ShadowHue)); Range(FaceFlatten, 0, 1, nameof(FaceFlatten));
        Range(ShadowTint.X, 0, 1, nameof(ShadowTint)); Range(ShadowTint.Y, 0, 1, nameof(ShadowTint)); Range(ShadowTint.Z, 0, 1, nameof(ShadowTint));
        Range(WarmColor.X, 0, 1, nameof(WarmColor)); Range(WarmColor.Y, 0, 1, nameof(WarmColor)); Range(WarmColor.Z, 0, 1, nameof(WarmColor));
        Range(WarmStrength, 0, 1, nameof(WarmStrength)); Range(RimEdge, 0, 1, nameof(RimEdge)); Range(RimStrength, 0, 1, nameof(RimStrength));
        Range(OutlineValue, .05f, 1, nameof(OutlineValue)); Range(OutlineSaturation, 0, 2, nameof(OutlineSaturation));
        Range(Saturation, 0, 2, nameof(Saturation)); Range(Contrast, -.5f, .5f, nameof(Contrast)); Range(Brightness, .5f, 2, nameof(Brightness));
    }
}

/// <summary>Adapts a base stylized shading profile to a character material role.</summary>
public static class D3D12StylizedShadingCatalog
{
    public static D3D12StylizedShading? ResolveForRole(D3D12StylizedShading? baseShading, D3D12CharacterMaterialRole role)
    {
        if (baseShading is null) return null;
        return role switch
        {
            D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair => baseShading,
            // Face suppresses most normal-based shading, disables rim light, and halves warm band.
            D3D12CharacterMaterialRole.Face => baseShading with { FaceFlatten = .85f, RimStrength = 0, WarmStrength = baseShading.WarmStrength * .5f },
            // Eye keeps authored shading/specular and applies color grading only.
            D3D12CharacterMaterialRole.Eye => baseShading with { ShadowStrength = 0, RimStrength = 0, WarmStrength = 0 },
            _ => null,
        };
    }
}
