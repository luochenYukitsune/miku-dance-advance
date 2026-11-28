using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Opt-in multi-level mip-pyramid bloom on display-domain scene color.
/// Disabled by default to preserve original MMD rendering.</summary>
public sealed record D3D12BloomOptions(
    bool Enabled = false,
    float Threshold = 0.80f,
    float SoftKnee = 0.25f,
    float Intensity = 0.22f,
    float Scatter = 0.65f)
{
    public static D3D12BloomOptions Off { get; } = new();

    // Validate must be called before consuming this property, including disabled requests.
    public bool IsActive => Enabled && Intensity > 0f;

    public void Validate()
    {
        if (!float.IsFinite(Threshold) || Threshold is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(Threshold), "Bloom threshold must be finite and between 0 and 1.");
        if (!float.IsFinite(SoftKnee) || SoftKnee is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(SoftKnee), "Bloom soft knee must be finite and between 0 and 1.");
        if (!float.IsFinite(Intensity) || Intensity is < 0f or > 2f)
            throw new ArgumentOutOfRangeException(nameof(Intensity), "Bloom intensity must be finite and between 0 and 2.");
        if (!float.IsFinite(Scatter) || Scatter is < 0.1f or > 0.95f)
            throw new ArgumentOutOfRangeException(nameof(Scatter), "Bloom scatter must be finite and between 0.1 and 0.95.");
    }

    internal void ValidateOutput(FrameAlphaOutput alphaOutput, float effectiveBackgroundAlpha)
    {
        Validate();
        if (IsActive && alphaOutput != FrameAlphaOutput.Straight && effectiveBackgroundAlpha != 1f)
            throw new NotSupportedException("Modern Bloom requires opaque output or explicit straight-alpha coverage; legacy maximum-alpha blending is not compositable.");
    }

    internal void ValidateFrame(OriginalFrameScene frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateOutput(frame.Options?.AlphaOutput ?? FrameAlphaOutput.LegacyMax,
            frame.Options?.BackgroundColor?.W ?? 1f);
    }
}

/// <summary>Opt-in display-domain tone curve and subtle color grading pass.
/// Disabled by default to preserve existing colors; when enabled, the default Shoulder (0.20) gently
/// compresses highlights—set Shoulder = 0 (with Contrast = 0, Saturation = 1, null tints) for a neutral pass.</summary>
public sealed record D3D12ToneCurveOptions(
    bool Enabled = false,
    float Contrast = 0f,
    float Shoulder = 0.20f,
    float Saturation = 1f,
    Vector3? ShadowTint = null,
    Vector3? HighlightTint = null)
{
    public static D3D12ToneCurveOptions Off { get; } = new();

    public bool IsActive => Enabled
        && (Contrast != 0f
            || Shoulder != 0f
            || Saturation != 1f
            || (ShadowTint is { } s && s != Vector3.One)
            || (HighlightTint is { } h && h != Vector3.One));

    public void Validate()
    {
        if (!float.IsFinite(Contrast) || Contrast is < -0.5f or > 0.5f)
            throw new ArgumentOutOfRangeException(nameof(Contrast), "Tone curve contrast must be finite and between -0.5 and 0.5.");
        if (!float.IsFinite(Shoulder) || Shoulder is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(Shoulder), "Tone curve shoulder must be finite and between 0 and 1.");
        if (!float.IsFinite(Saturation) || Saturation is < 0f or > 2f)
            throw new ArgumentOutOfRangeException(nameof(Saturation), "Tone curve saturation must be finite and between 0 and 2.");
        ValidateTint(ShadowTint, nameof(ShadowTint));
        ValidateTint(HighlightTint, nameof(HighlightTint));
    }

    private static void ValidateTint(Vector3? tint, string name)
    {
        if (tint is not { } value) return;
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)
            || value.X is < 0.5f or > 1.5f || value.Y is < 0.5f or > 1.5f || value.Z is < 0.5f or > 1.5f)
            throw new ArgumentOutOfRangeException(name, $"Tone curve {name} components must be finite and between 0.5 and 1.5.");
    }
}
