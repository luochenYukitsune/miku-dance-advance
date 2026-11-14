using System.Numerics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Opt-in source-facing multiplication of explicit M by itself. Blend zero is neutral,
/// including grazing angles: the defined zero-power response is one.</summary>
public sealed record D3D12EndfieldFacingBaseBinding(float Blend = 0)
{
    public void Validate()
    {
        if (!float.IsFinite(Blend) || Blend < 0 || Blend > 1)
            throw new ArgumentOutOfRangeException(nameof(Blend), "Blend must be finite in [0,1].");
    }
}

/// <summary>Explicit normalized MDA world-space direction, source width and independent
/// packed-normal strength. This does not claim source animated-attribute or scene-light parity.</summary>
public sealed record D3D12EndfieldDirectionalShadowBinding(
    Vector3 WorldDirection, float Width = .3f, float NormalStrength = 2)
{
    public void Validate()
    {
        float length = WorldDirection.Length();
        if (!float.IsFinite(length) || MathF.Abs(length - 1) > 1e-4f)
            throw new ArgumentOutOfRangeException(nameof(WorldDirection), "Direction must be finite and normalized within 0.0001.");
        if (!float.IsFinite(Width) || Width < -4 || Width > 4)
            throw new ArgumentOutOfRangeException(nameof(Width), "Width must be finite in [-4,4].");
        if (!float.IsFinite(NormalStrength) || NormalStrength < 0 || NormalStrength > 16)
            throw new ArgumentOutOfRangeException(nameof(NormalStrength), "Normal strength must be finite in [0,16].");
    }
}
