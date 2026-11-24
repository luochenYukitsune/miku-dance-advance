using System.ComponentModel;
using System.Globalization;
using Mmd.Modern.ViewOutput;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

public enum ExportRenderStage
{
    Preparing, ReleasingPreview, PreparingRenderer, Rendering, Finalizing, RestoringPreview
}

/// <summary>
/// One export's retained status. The owner reports the terminal state only after renderer,
/// encoder and preview cleanup has finished. Members are updated on the UI thread.
/// </summary>
public sealed class ExportRenderStatusViewModel : INotifyPropertyChanged
{
    private readonly TimeProvider clock;
    private readonly long startedAt;
    private readonly Queue<(long Timestamp, long Frames)> rateSamples = new();
    private TimeSpan elapsed;
    private string stageKey = "Mda.Text.ExportStatus.Preparing";
    private bool rendering;
    private long completedFrames, totalFrames;
    private ExportTaskState state = ExportTaskState.Running;
    private RenderResourceSnapshot? resources;
    private ulong? peakVideoMemory, peakWorkingSet;

    public ExportRenderStatusViewModel(string destination, long totalFrames = 0, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (totalFrames < 0) throw new ArgumentOutOfRangeException(nameof(totalFrames));
        Destination = destination;
        this.totalFrames = totalFrames;
        this.clock = clock ?? TimeProvider.System;
        startedAt = this.clock.GetTimestamp();
        Observe(this, static model => model.Changed());
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? CancellationRequested;
    public string Destination { get; }
    public ExportTaskState State => state;
    public bool IsRunning => state == ExportTaskState.Running;
    public bool CanClose => !IsRunning;
    public bool CanCancel => IsRunning && !IsCancellationRequested;
    public bool IsCancellationRequested { get; private set; }
    public bool HasError => ErrorText.Length > 0;
    public string ErrorText { get; private set; } = "";
    public bool HasDiagnostics => DiagnosticsText.Length > 0;
    public string DiagnosticsText { get; private set; } = "";
    public string DiagnosticLogPath { get; private set; } = "";
    public string CopyableDetails => HasDiagnostics ? DiagnosticsText : ErrorText;
    public long CompletedFrames => completedFrames;
    public long TotalFrames => totalFrames;
    public double ProgressPercent => totalFrames > 0 ? completedFrames * 100d / totalFrames : 0;
    public bool IsProgressIndeterminate => totalFrames <= 0 && IsRunning;
    public string StageText => Text(stageKey);
    public string FrameText => totalFrames > 0
        ? Format("Mda.Text.ExportStatus.Frames", completedFrames, totalFrames)
        : Format("Mda.Text.ExportStatus.FramesUnknown", completedFrames);
    public string ProgressText => totalFrames > 0 ? Percent(ProgressPercent) : Unavailable;
    public TimeSpan Elapsed => elapsed;
    public string ElapsedText => Duration(elapsed);
    public TimeSpan? EstimatedRemaining
    {
        get
        {
            if (!IsRunning || !rendering || IsCancellationRequested || totalFrames <= completedFrames || rateSamples.Count < 4)
                return null;
            var first = rateSamples.Peek();
            var last = rateSamples.Last();
            double seconds = clock.GetElapsedTime(first.Timestamp, last.Timestamp).TotalSeconds;
            long frames = last.Frames - first.Frames;
            if (seconds < 2 || frames < 3) return null;
            double remaining = (totalFrames - completedFrames) * seconds / frames;
            return double.IsFinite(remaining) && remaining >= 0 && remaining <= TimeSpan.MaxValue.TotalSeconds
                ? TimeSpan.FromSeconds(remaining) : null;
        }
    }
    public string RemainingText => EstimatedRemaining is { } remaining ? Duration(remaining) : Unavailable;
    public string FooterText => Text(IsRunning
        ? IsCancellationRequested ? "Mda.Text.ExportStatus.StoppingHint" : "Mda.Text.ExportStatus.RunningHint"
        : "Mda.Text.ExportStatus.RetainedHint");
    public string CancelText => Text(IsCancellationRequested ? "Mda.Text.ExportStatus.Cancelling" : "Mda.Text.ExportStatus.Cancel");

    public string CpuText => ValidPercent(resources?.ProcessCpuPercent) is { } value ? Percent(value) : Unavailable;
    public string WorkingSetText => resources?.ProcessWorkingSetBytes is { } current ? Bytes(current) : Unavailable;
    public string PeakWorkingSetText => peakWorkingSet is { } value ? Bytes(value) : Unavailable;
    public string AdapterText => resources?.AdapterName is { } name && !string.IsNullOrWhiteSpace(name) ? name : Unavailable;
    public string GpuText => ValidPercent(resources?.GpuUtilizationPercent) is { } value ? Percent(value) : Unavailable;
    public string VideoMemoryText => resources?.ProcessLocalVideoMemoryUsageBytes is { } value ? Bytes(value) : Unavailable;
    public string VideoMemoryBudgetText => resources?.LocalVideoMemoryBudgetBytes is { } value ? Bytes(value) : Unavailable;
    public string PeakVideoMemoryText => peakVideoMemory is { } value ? Bytes(value) : Unavailable;
    public string SampleTimeText => resources is { } value ? SampleTime(value.SampledAtUtc) : Unavailable;
    public string VideoMemorySampleTimeText => resources?.VideoMemorySampledAtUtc is { } value ? SampleTime(value) : Unavailable;
    public string? CpuUnavailableReason => resources?.CpuUnavailableReason;
    public string? MemoryUnavailableReason => resources?.MemoryUnavailableReason;
    public string? GpuUnavailableReason => resources?.GpuUnavailableReason;
    public string? VideoMemoryUnavailableReason => resources?.VideoMemoryUnavailableReason;

    public void ReportStage(ExportRenderStage stage)
    {
        if (!Enum.IsDefined(stage)) throw new ArgumentOutOfRangeException(nameof(stage));
        if (!IsRunning) return;
        rendering = stage == ExportRenderStage.Rendering;
        if (rendering && rateSamples.Count == 0) rateSamples.Enqueue((clock.GetTimestamp(), completedFrames));
        stageKey = "Mda.Text.ExportStatus." + stage;
        RefreshElapsed();
    }

    /// <summary>Reports an explicit service stage or strategy description; never infers GPU activity.</summary>
    public void ReportStage(string localizationKeyOrText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localizationKeyOrText);
        if (!IsRunning) return;
        stageKey = localizationKeyOrText;
        Changed();
    }

    /// <summary>Coordinator state is not a cleanup completion signal.</summary>
    public void ReportProgress(ExportTaskProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (!IsRunning) return;
        if (progress.TotalFrames > 0) totalFrames = progress.TotalFrames;
        long next = Math.Max(0, progress.CompletedFrames);
        if (totalFrames > 0) next = Math.Min(next, totalFrames);
        if (next > completedFrames)
        {
            completedFrames = next;
            // Completed output is evidence of rendering independently of optional adapter metrics.
            // Late queued progress must not move cleanup or cancellation back into Rendering.
            if (progress.State == ExportTaskState.Running && !rendering && !IsCancellationRequested
                && stageKey is "Mda.Text.ExportStatus.Preparing" or "Mda.Text.ExportStatus.PreparingRenderer")
            {
                rendering = true;
                stageKey = "Mda.Text.ExportStatus.Rendering";
            }
            if (rendering && !IsCancellationRequested)
            {
                long now = clock.GetTimestamp();
                rateSamples.Enqueue((now, completedFrames));
                while (rateSamples.Count > 4 && clock.GetElapsedTime(rateSamples.Peek().Timestamp, now).TotalSeconds > 30)
                    rateSamples.Dequeue();
            }
        }
        RefreshElapsed();
    }

    public void ApplyResourceSnapshot(RenderResourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!IsRunning || resources is { } previous && snapshot.SampledAtUtc < previous.SampledAtUtc) return;
        if (resources?.AdapterLuid is { } oldAdapter && snapshot.AdapterLuid is { } newAdapter && oldAdapter != newAdapter)
            peakVideoMemory = null;
        resources = snapshot;
        peakWorkingSet = Maximum(peakWorkingSet, snapshot.ProcessWorkingSetBytes, snapshot.ProcessPeakWorkingSetBytes);
        peakVideoMemory = Maximum(peakVideoMemory, snapshot.ProcessLocalVideoMemoryUsageBytes, snapshot.PeakProcessLocalVideoMemoryUsageBytes);
        Changed();
    }

    public bool RequestCancellation()
    {
        if (!CanCancel) return false;
        IsCancellationRequested = true;
        rendering = false;
        stageKey = "Mda.Text.ExportStatus.Cancelling";
        RefreshElapsed();
        CancellationRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void RefreshElapsed()
    {
        if (!IsRunning) return;
        elapsed = clock.GetElapsedTime(startedAt, clock.GetTimestamp());
        Changed();
    }

    public void MarkCompleted() => Finish(ExportTaskState.Completed, "Mda.Text.ExportStatus.Completed");
    public void MarkCancelled() => Finish(ExportTaskState.Cancelled, "Mda.Text.ExportStatus.Cancelled");
    public void MarkFailed(Exception error) => MarkFailed(error, outputPublished: false);
    internal void MarkFailed(Exception error, bool outputPublished)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (!IsRunning) return;
        ErrorText = error.ToString();
        if (!HasDiagnostics)
        {
            DiagnosticsText = ExportDiagnosticException.Readable(error);
            DiagnosticLogPath = Text("Mda.Text.ExportStatus.LogUnavailable");
        }
        if (outputPublished && totalFrames > 0) completedFrames = totalFrames;
        Finish(ExportTaskState.Failed, outputPublished ? "Mda.Text.ExportStatus.FailedAfterPublication" : "Mda.Text.ExportStatus.Failed");
    }

    internal void SetDiagnostics(ExportDiagnosticSession diagnostics)
    {
        DiagnosticsText = diagnostics.ReadableSummary;
        DiagnosticLogPath = diagnostics.SummaryPath ?? diagnostics.JsonLinesPath
            ?? Text("Mda.Text.ExportStatus.LogUnavailable");
        Changed();
    }

    private void Finish(ExportTaskState finalState, string finalStage)
    {
        if (!IsRunning) return;
        elapsed = clock.GetElapsedTime(startedAt, clock.GetTimestamp());
        state = finalState;
        stageKey = finalStage;
        rendering = false;
        if (finalState == ExportTaskState.Completed && totalFrames > 0) completedFrames = totalFrames;
        Changed();
    }

    private static string Unavailable => Text("Mda.Text.ExportStatus.Unavailable");
    private static CultureInfo Culture => CultureInfo.GetCultureInfo(CurrentLanguage);
    private static string Percent(double value) => value.ToString("0.0", Culture) + "%";
    private static double? ValidPercent(double? value) => value is { } number && double.IsFinite(number) && number is >= 0 and <= 100 ? number : null;
    private static string Bytes(ulong value) => value >= 1024UL * 1024 * 1024
        ? ((double)value / (1024 * 1024 * 1024)).ToString("0.00", Culture) + " GiB"
        : ((double)value / (1024 * 1024)).ToString("0.0", Culture) + " MiB";
    private static string SampleTime(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", Culture);
    private static string Duration(TimeSpan value) => value.TotalDays >= 1
        ? value.ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture)
        : value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    private static ulong? Maximum(params ulong?[] values)
    {
        ulong? maximum = null;
        foreach (var value in values) if (value.HasValue && (!maximum.HasValue || value.Value > maximum.Value)) maximum = value;
        return maximum;
    }
    private void Changed() => PropertyChanged?.Invoke(this, new(null));
}
