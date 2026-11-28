using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Opt-in screen-space constant-pixel-width depth rim light.
/// Disabled by default to preserve original MMD rendering.</summary>
public sealed record D3D12DepthRimOptions(
    bool Enabled = false,
    float Strength = 0.35f,
    float WidthPixels = 1.75f,
    float DepthThreshold = 0.08f,
    float LightBias = 0.65f,
    Vector3? Color = null)
{
    public static D3D12DepthRimOptions Off { get; } = new();

    // Validate must be called before consuming this property, including disabled requests.
    public bool IsActive => Enabled && Strength > 0f;

    public void Validate()
    {
        if (!float.IsFinite(Strength) || Strength is < 0f or > 2f)
            throw new ArgumentOutOfRangeException(nameof(Strength), "Depth rim strength must be finite and between 0 and 2.");
        if (!float.IsFinite(WidthPixels) || WidthPixels is < 0.5f or > 8f)
            throw new ArgumentOutOfRangeException(nameof(WidthPixels), "Depth rim width must be finite and between 0.5 and 8 pixels.");
        if (!float.IsFinite(DepthThreshold) || DepthThreshold is < 0.001f or > 5f)
            throw new ArgumentOutOfRangeException(nameof(DepthThreshold), "Depth rim threshold must be finite and between 0.001 and 5.");
        if (!float.IsFinite(LightBias) || LightBias is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(LightBias), "Depth rim light bias must be finite and between 0 and 1.");
        if (Color is { } color
            && (!float.IsFinite(color.X) || !float.IsFinite(color.Y) || !float.IsFinite(color.Z)
                || color.X is < 0f or > 2f || color.Y is < 0f or > 2f || color.Z is < 0f or > 2f))
            throw new ArgumentOutOfRangeException(nameof(Color), "Depth rim color components must be finite and between 0 and 2.");
    }

    internal void ValidateOutput(FrameAlphaOutput alphaOutput, float effectiveBackgroundAlpha)
    {
        Validate();
        if (IsActive && alphaOutput != FrameAlphaOutput.Straight && effectiveBackgroundAlpha != 1f)
            throw new NotSupportedException("Modern Depth Rim requires opaque output or explicit straight-alpha coverage; legacy maximum-alpha blending is not compositable.");
    }

    internal void ValidateFrame(OriginalFrameScene frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateOutput(frame.Options?.AlphaOutput ?? FrameAlphaOutput.LegacyMax,
            frame.Options?.BackgroundColor?.W ?? 1f);
    }
}
