namespace Mmd.Modern.Core;

/// <summary>Depth-based screen-space ambient occlusion (GTAO/SSAO) settings.
/// Disabled by default to preserve original MMD shading. Mutually exclusive with Ray GI occlusion.</summary>
public sealed record ScreenSpaceAoOptions(
    bool Enabled = false,
    float Strength = 0.4f,
    float Radius = 1.5f,
    int SampleCount = 8)
{
    public static ScreenSpaceAoOptions Off { get; } = new();
    public bool IsActive => Enabled && Strength > 0f;

    public void Validate()
    {
        if (!float.IsFinite(Strength) || Strength is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(Strength), "SSAO strength must be finite and between 0 and 1.");
        if (!float.IsFinite(Radius) || Radius is < 0.1f or > 8f)
            throw new ArgumentOutOfRangeException(nameof(Radius), "SSAO radius must be finite and between 0.1 and 8.");
        if (SampleCount is not (4 or 8 or 16))
            throw new ArgumentOutOfRangeException(nameof(SampleCount), "SSAO sample count must be 4, 8, or 16.");
    }
}
