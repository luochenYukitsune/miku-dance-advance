namespace Mmd.Modern.ViewOutput;

/// <summary>Implemented spatial fallback choices only. FSR1 does not provide temporal reconstruction.</summary>
public enum ViewUpscalingMode { Off, Fsr1 }
public enum ViewFsr1QualityPreset { UltraQuality, Quality, Balanced, Performance }

/// <summary>Per-view spatial fallback request. Render and output dimensions are resolved by the backend.
/// RCAS sharpness uses stops: zero is strongest, two is weakest. Latest FSR and DLSS require separate integration.</summary>
public sealed record ViewUpscalingOptions
{
    public ViewUpscalingMode Mode { get; init; }
    public ViewFsr1QualityPreset QualityPreset { get; init; } = ViewFsr1QualityPreset.Quality;
    public bool EnableRcas { get; init; } = true;
    public float RcasSharpness { get; init; } = .2f;
    public static ViewUpscalingOptions Off { get; } = new();

    public void Validate()
    {
        if (!Enum.IsDefined(Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
        if (!Enum.IsDefined(QualityPreset)) throw new ArgumentOutOfRangeException(nameof(QualityPreset));
        if (!float.IsFinite(RcasSharpness) || RcasSharpness is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(RcasSharpness));
    }
}
