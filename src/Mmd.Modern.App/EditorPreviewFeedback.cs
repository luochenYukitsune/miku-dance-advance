using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.App;

/// <summary>Displays the last completed preview's admitted quality without changing authored settings.</summary>
internal static class EditorPreviewFeedback
{
    internal static bool IsAdjusted(EditorViewportPresentationStatistics? statistics) => statistics is
        { EffectiveSpatialSamples: > 0 } && statistics.EffectiveSpatialSamples < statistics.RequestedSpatialSamples;

    internal static string SamplingSummary(EditorViewportPresentationStatistics statistics) => IsAdjusted(statistics)
        ? EditorLocalization.Format("Mda.Text.Preview.SamplingAdjusted", statistics.EffectiveSpatialSamples, statistics.RequestedSpatialSamples)
        : EditorLocalization.Format("Mda.Text.Preview.SamplingActual", statistics.EffectiveSpatialSamples);

    internal static string BudgetSummary(D3D12GiMemoryBudgetExceededException error) => EditorLocalization.Format(
        "Mda.Text.Preview.GiBudgetExceeded", error.RequiredBytes / (1024d * 1024), error.AvailableBytes / (1024d * 1024));
}

public sealed partial class EditorShellViewModel
{
    private EditorViewportPresentationStatistics? previewStatistics;
    private EditorPreviewFrameRateSnapshot previewFrameRate;
    public bool HasPreviewSamplingAdjustment => EditorPreviewFeedback.IsAdjusted(previewStatistics);
    public string PreviewSamplingSummary => previewStatistics is { } statistics
        ? EditorPreviewFeedback.SamplingSummary(statistics) : "";
    public string PreviewSamplingHint => EditorLocalization.Text("Mda.Text.Preview.SamplingAdjustedHint");
    public bool HasPreviewFrameRate => previewFrameRate.IsMeasuring;
    public string PreviewFrameRateSummary => previewFrameRate.FramesPerSecond is { } fps
        ? EditorLocalization.Format("Mda.Text.Preview.FrameRate", fps)
        : EditorLocalization.Text("Mda.Text.Preview.FrameRateMeasuring");
    public string PreviewFrameRateHint => EditorLocalization.Text("Mda.Text.Preview.FrameRateHint");
    public bool HasLowPreviewFrameRateWarning => previewFrameRate.IsLowFrameRateWarningVisible;

    internal void SetPreviewFrameRate(EditorPreviewFrameRateSnapshot value)
    {
        if (previewFrameRate == value) return;
        previewFrameRate = value;
        OnPropertyChanged(nameof(HasPreviewFrameRate));
        OnPropertyChanged(nameof(PreviewFrameRateSummary));
        OnPropertyChanged(nameof(HasLowPreviewFrameRateWarning));
    }

    internal void SetPreviewStatistics(EditorViewportPresentationStatistics? statistics)
    {
        if (previewStatistics == statistics) return;
        previewStatistics = statistics;
        OnPropertyChanged(nameof(HasPreviewSamplingAdjustment));
        OnPropertyChanged(nameof(PreviewSamplingSummary));
    }
}
