namespace Mmd.Modern.ViewOutput;

/// <summary>Native post-filter settings owned by one view/output configuration, independent of project data.
/// This first mode follows Diffusion7's MIX1/BCOMP0 white-tint response. Disabled by default.</summary>
public sealed record ViewDiffusionOptions(bool Enabled = false, float Strength = .7f, float Extent = .002f)
{
    public static ViewDiffusionOptions Off { get; } = new();
    public void Validate()
    {
        // Validate stored settings even while disabled; enabling later must never revive invalid data.
        if (!float.IsFinite(Strength) || Strength is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(Strength));
        if (!float.IsFinite(Extent) || Extent is < 0 or > .01f)
            throw new ArgumentOutOfRangeException(nameof(Extent));
    }
}
