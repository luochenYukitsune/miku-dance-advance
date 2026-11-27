namespace Mmd.Modern.ViewOutput;

/// <summary>Standalone RCAS contrast-adaptive sharpening decoupled from FSR1.
/// Sharpness uses RCAS stops: zero is strongest, two is weakest.</summary>
public sealed record ViewSharpeningOptions(bool Enabled = false, float Sharpness = 0.2f)
{
    public static ViewSharpeningOptions Off { get; } = new();
    public bool IsActive => Enabled;

    public void Validate()
    {
        if (!float.IsFinite(Sharpness) || Sharpness is < 0f or > 2f)
            throw new ArgumentOutOfRangeException(nameof(Sharpness), "RCAS sharpness must be finite and between 0 and 2.");
    }
}
