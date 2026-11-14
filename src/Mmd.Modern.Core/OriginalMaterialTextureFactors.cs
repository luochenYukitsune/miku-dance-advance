using System.Numerics;

namespace Mmd.Modern.Core;

/// <summary>Raw immutable texture-colour accumulators, independent of PMX and GPU resources.
/// These values are inputs to the selected surface equation; they do not directly define opacity.</summary>
public readonly record struct OriginalTextureFactor(Vector4 Multiply, Vector4 Add)
{
    public static OriginalTextureFactor Identity { get; } = new(Vector4.One, Vector4.Zero);
    public bool IsIdentity => this == Identity;
    public bool IsFinite => Finite(Multiply) && Finite(Add);
    private static bool Finite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y)
        && float.IsFinite(value.Z) && float.IsFinite(value.W);
}

/// <summary>Evaluated diffuse, sphere and toon texture factors in local material order.
/// Shader interpretation is separate from transporting the six original four-component vectors.</summary>
public readonly record struct OriginalMaterialTextureFactors(OriginalTextureFactor Diffuse,
    OriginalTextureFactor Sphere, OriginalTextureFactor Toon)
{
    public static OriginalMaterialTextureFactors Identity { get; } = new(
        OriginalTextureFactor.Identity, OriginalTextureFactor.Identity, OriginalTextureFactor.Identity);
    public bool IsIdentity => this == Identity;
    public bool IsFinite => Diffuse.IsFinite && Sphere.IsFinite && Toon.IsFinite;
}
