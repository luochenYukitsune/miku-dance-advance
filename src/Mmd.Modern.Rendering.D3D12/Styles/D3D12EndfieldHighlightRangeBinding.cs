namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Opt-in numerical P.r/P.a and directional-q highlight range. The same
/// evaluated packed-RG normal supplies q; this changes surface color, never coverage,
/// reflected rays or GI emission. Null retains the historical uniform baseline.</summary>
public sealed record D3D12EndfieldHighlightRangeBinding(
    float OverallBrightness,
    float HighlightBrightness,
    D3D12EndfieldDirectionalShadowBinding DirectionalShadow)
{
    public void Validate()
    {
        if (!float.IsFinite(OverallBrightness) || OverallBrightness is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(OverallBrightness), "Overall brightness must be finite in [0,4].");
        if (!float.IsFinite(HighlightBrightness) || HighlightBrightness is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(HighlightBrightness), "Highlight brightness must be finite in [0,4].");
        ArgumentNullException.ThrowIfNull(DirectionalShadow);
        DirectionalShadow.Validate();
    }
}
