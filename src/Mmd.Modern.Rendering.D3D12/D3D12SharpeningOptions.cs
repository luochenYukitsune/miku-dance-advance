namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Standalone RCAS contrast-adaptive sharpening options, independent of FSR1 spatial upscaling.
/// Sharpness uses RCAS stops: zero is strongest, two is weakest.</summary>
public sealed record D3D12SharpeningOptions(bool Enabled = false, float Sharpness = 0.2f)
{
    public static D3D12SharpeningOptions Off { get; } = new();
    public bool IsActive => Enabled;

    public void Validate()
    {
        if (!float.IsFinite(Sharpness) || Sharpness is < 0f or > 2f)
            throw new ArgumentOutOfRangeException(nameof(Sharpness), "RCAS sharpness must be finite and between 0 and 2.");
    }
}
