using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Opt-in native Diffusion7 adaptation: fixed MIX1/BCOMP0, white tint and Si10.
/// Extent is a fraction of output height per tap, not a fixed pixel radius.</summary>
public sealed record D3D12DiffusionOptions(bool Enabled = false, float Strength = .7f, float Extent = .002f)
{
    public static D3D12DiffusionOptions Off { get; } = new();
    // Validate must be called before consuming this property, including disabled requests.
    public bool IsActive => Enabled && Strength > 0;

    public void Validate()
    {
        if (!float.IsFinite(Strength) || Strength is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(Strength));
        if (!float.IsFinite(Extent) || Extent is < 0 or > .01f)
            throw new ArgumentOutOfRangeException(nameof(Extent));
    }

    internal Vector2 GetTapStepUv(int displayWidth, int displayHeight)
    {
        Validate();
        if (displayWidth is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(displayWidth));
        if (displayHeight is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(displayHeight));
        return new(Extent * displayHeight / displayWidth, Extent);
    }

    /// <summary>Call before any GPU recording or temporal owner recreation. Bypass keeps all old alpha paths.</summary>
    internal void ValidateOutput(FrameAlphaOutput alphaOutput, float effectiveBackgroundAlpha)
    {
        Validate();
        if (IsActive && alphaOutput != FrameAlphaOutput.Straight && effectiveBackgroundAlpha != 1)
            throw new NotSupportedException("Native Diffusion requires opaque output or explicit straight-alpha coverage; legacy maximum-alpha blending is not compositable.");
    }
    internal void ValidateFrame(OriginalFrameScene frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateOutput(frame.Options?.AlphaOutput ?? FrameAlphaOutput.LegacyMax,
            frame.Options?.BackgroundColor?.W ?? 1f);
    }
}
