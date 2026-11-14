namespace Mmd.Modern.Rendering.D3D12;

public enum D3D12SpatialUpscalingMode { Off, Fsr1 }
public enum D3D12Fsr1QualityPreset { UltraQuality, Quality, Balanced, Performance }
public readonly record struct D3D12SpatialUpscalingDimensions(int RenderWidth, int RenderHeight, int DisplayWidth, int DisplayHeight);

/// <summary>Spatial reconstruction in the existing display color domain. RCAS sharpness is in stops: 0 is strongest, 2 is weakest.</summary>
public sealed record D3D12SpatialUpscalingOptions
{
    public D3D12SpatialUpscalingMode Mode { get; init; }
    public D3D12Fsr1QualityPreset QualityPreset { get; init; } = D3D12Fsr1QualityPreset.Quality;
    public bool EnableRcas { get; init; } = true;
    public float RcasSharpness { get; init; } = .2f;
    public static D3D12SpatialUpscalingOptions Off { get; } = new();

    public D3D12SpatialUpscalingDimensions GetDimensions(int displayWidth, int displayHeight)
    {
        if (displayWidth is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(displayWidth));
        if (displayHeight is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(displayHeight));
        if (!Enum.IsDefined(Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
        if (!Enum.IsDefined(QualityPreset)) throw new ArgumentOutOfRangeException(nameof(QualityPreset));
        if (!float.IsFinite(RcasSharpness) || RcasSharpness is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(RcasSharpness));
        double scale = Mode == D3D12SpatialUpscalingMode.Off ? 1 : QualityPreset switch
        {
            D3D12Fsr1QualityPreset.UltraQuality => 1.3,
            D3D12Fsr1QualityPreset.Quality => 1.5,
            D3D12Fsr1QualityPreset.Balanced => 1.7,
            D3D12Fsr1QualityPreset.Performance => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(QualityPreset))
        };
        return new(Math.Max(1, (int)Math.Floor(displayWidth / scale)), Math.Max(1, (int)Math.Floor(displayHeight / scale)), displayWidth, displayHeight);
    }
}
