namespace Mmd.Modern.ViewOutput;

/// <summary>Only providers integrated by an installed renderer are selectable. Spatial FSR1 is separate.</summary>
public enum ViewTemporalUpscalingMode { Off = 0, Fsr = 1, XeSs = 2, Dlss = 3 }
public enum ViewTemporalQualityPreset { NativeAa, Quality, Balanced, Performance, UltraPerformance }
public enum ViewXeSsQuality { UltraPerformance, Performance, Balanced, Quality, UltraQuality, UltraQualityPlus, NativeAa }
public enum ViewDlssQuality { Performance, Balanced, Quality, UltraPerformance, UltraQuality, NativeAa }

/// <summary>Independent per-view temporal reconstruction. Native library installation belongs to the backend.
/// Frame time is the first-frame/paused fallback; exports replace it with their exact output interval.</summary>
public sealed record ViewTemporalUpscalingOptions
{
    public ViewTemporalUpscalingMode Mode { get; init; }
    public ViewTemporalQualityPreset QualityPreset { get; init; } = ViewTemporalQualityPreset.NativeAa;
    /// <summary>XeSS has its own official quality levels; its SDK queries the actual input dimensions.</summary>
    public ViewXeSsQuality XeSsQuality { get; init; } = ViewXeSsQuality.NativeAa;
    /// <summary>NGX quality availability and input dimensions are verified by the selected backend on its actual device.</summary>
    public ViewDlssQuality DlssQuality { get; init; } = ViewDlssQuality.NativeAa;
    public float FrameTimeMilliseconds { get; init; } = 1000f / 60;
    public static ViewTemporalUpscalingOptions Off { get; } = new();

    public void Validate()
    {
        if (!Enum.IsDefined(Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
        if (!Enum.IsDefined(QualityPreset)) throw new ArgumentOutOfRangeException(nameof(QualityPreset));
        if (!Enum.IsDefined(XeSsQuality)) throw new ArgumentOutOfRangeException(nameof(XeSsQuality));
        if (!Enum.IsDefined(DlssQuality)) throw new ArgumentOutOfRangeException(nameof(DlssQuality));
        if (!float.IsFinite(FrameTimeMilliseconds) || FrameTimeMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(FrameTimeMilliseconds));
    }
}

/// <summary>Host discontinuity markers, carried without native resources. Increment generation after seek/restart,
/// and camera key after a camera cut. Zero elapsed time retains the configured positive fallback.</summary>
public sealed record ViewTemporalFrameState
{
    public float? FrameTimeMilliseconds { get; init; }
    public bool ResetHistory { get; init; }
    public long HistoryGeneration { get; init; }
    public long CameraCutKey { get; init; }
    public void Validate()
    {
        if (FrameTimeMilliseconds is { } time && (!float.IsFinite(time) || time <= 0))
            throw new ArgumentOutOfRangeException(nameof(FrameTimeMilliseconds));
    }
}
