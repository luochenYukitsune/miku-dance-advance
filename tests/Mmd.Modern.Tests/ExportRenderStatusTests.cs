using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class ExportRenderStatusTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CompletedFramesStartRenderingAndEtaWithoutOptionalDeviceMetrics() => await Run(() =>
    {
        var clock = new ManualClock();
        var model = new ExportRenderStatusViewModel("output.mp4", 30, clock);
        model.ReportStage(ExportRenderStage.PreparingRenderer);
        for (int frame = 1; frame <= 4; frame++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            model.ReportProgress(Progress(frame, 30));
        }
        Assert.Equal(EditorLocalization.Text("Mda.Text.ExportStatus.Rendering"), model.StageText);
        Assert.Equal(TimeSpan.FromSeconds(26), model.EstimatedRemaining);
        Assert.Equal(EditorLocalization.Text("Mda.Text.ExportStatus.Unavailable"), model.GpuText);
        model.ReportStage(ExportRenderStage.RestoringPreview);
        model.ReportProgress(Progress(5, 30));
        Assert.Equal(EditorLocalization.Text("Mda.Text.ExportStatus.RestoringPreview"), model.StageText);
        Assert.Null(model.EstimatedRemaining);
    });

    [Fact]
    public async Task MissingMetricsRemainUnavailableAndRealZeroRemainsZero() => await Run(() =>
    {
        var model = new ExportRenderStatusViewModel("output.mp4", 30);
        string unavailable = EditorLocalization.Text("Mda.Text.ExportStatus.Unavailable");
        Assert.Equal(unavailable, model.CpuText);
        Assert.Equal(unavailable, model.GpuText);
        Assert.Equal(unavailable, model.WorkingSetText);
        Assert.Equal(unavailable, model.VideoMemoryText);
        Assert.Equal(unavailable, model.VideoMemoryBudgetText);
        Assert.Equal(unavailable, model.PeakVideoMemoryText);
        model.ApplyResourceSnapshot(new()
        {
            SampledAtUtc = DateTimeOffset.UtcNow, ProcessCpuPercent = 0, GpuUtilizationPercent = 0,
            ProcessWorkingSetBytes = 0, ProcessLocalVideoMemoryUsageBytes = 0, LocalVideoMemoryBudgetBytes = 0
        });
        Assert.Equal("0.0%", model.CpuText);
        Assert.Equal("0.0%", model.GpuText);
        Assert.Equal("0.0 MiB", model.WorkingSetText);
        Assert.Equal("0.0 MiB", model.VideoMemoryText);
        model.ApplyResourceSnapshot(new() { SampledAtUtc = DateTimeOffset.UtcNow, ProcessCpuPercent = double.NaN, GpuUtilizationPercent = 101 });
        Assert.Equal(unavailable, model.CpuText);
        Assert.Equal(unavailable, model.GpuText);
    });

    [Fact]
    public async Task EtaRequiresFrameSamplesAndRenderingTimeAndIsHiddenDuringCleanup() => await Run(() =>
    {
        var clock = new ManualClock();
        var model = new ExportRenderStatusViewModel("output.mp4", 30, clock);
        clock.Advance(TimeSpan.FromSeconds(5));
        model.ReportStage(ExportRenderStage.Rendering);
        Assert.Null(model.EstimatedRemaining);
        for (int frame = 1; frame <= 3; frame++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            model.ReportProgress(Progress(frame, 30));
            if (frame < 3) Assert.Null(model.EstimatedRemaining);
        }
        Assert.Equal(TimeSpan.FromSeconds(27), model.EstimatedRemaining);
        Assert.Equal(TimeSpan.FromSeconds(8), model.Elapsed);
        model.ReportStage(ExportRenderStage.RestoringPreview);
        Assert.Null(model.EstimatedRemaining);
        Assert.True(model.IsRunning);
    });

    [Fact]
    public async Task CoordinatorTerminalProgressDoesNotPermitCloseBeforeOwnerCleanup() => await Run(() =>
    {
        var clock = new ManualClock();
        var model = new ExportRenderStatusViewModel("output.mp4", 10, clock);
        clock.Advance(TimeSpan.FromSeconds(4));
        model.ReportProgress(Progress(10, 10) with { State = ExportTaskState.Completed });
        Assert.True(model.IsRunning);
        Assert.False(model.CanClose);
        Assert.Equal(100, model.ProgressPercent);
        model.ReportStage(ExportRenderStage.RestoringPreview);
        clock.Advance(TimeSpan.FromSeconds(2));
        model.MarkCompleted();
        Assert.Equal(ExportTaskState.Completed, model.State);
        Assert.True(model.CanClose);
        Assert.False(model.CanCancel);
        Assert.Equal(TimeSpan.FromSeconds(6), model.Elapsed);
        clock.Advance(TimeSpan.FromHours(1));
        model.RefreshElapsed();
        model.ReportProgress(Progress(1, 10));
        Assert.Equal(TimeSpan.FromSeconds(6), model.Elapsed);
        Assert.Equal(10, model.CompletedFrames);
    });

    [Fact]
    public async Task CancellationIsRequestedOnceAndKeepsActualFrameCountUntilCleanupEnds() => await Run(() =>
    {
        var model = new ExportRenderStatusViewModel("output.mp4", 100);
        model.ReportProgress(Progress(8, 100));
        int requests = 0;
        model.CancellationRequested += (_, _) => requests++;
        Assert.True(model.RequestCancellation());
        Assert.False(model.RequestCancellation());
        Assert.True(model.IsRunning);
        Assert.False(model.CanClose);
        Assert.False(model.CanCancel);
        model.ReportStage(ExportRenderStage.RestoringPreview);
        model.MarkCancelled();
        Assert.Equal(1, requests);
        Assert.Equal(ExportTaskState.Cancelled, model.State);
        Assert.Equal(8, model.CompletedFrames);
        Assert.Equal(8, model.ProgressPercent);
        Assert.True(model.CanClose);
    });

    [Fact]
    public async Task PeaksAndFailureDetailsAreRetainedAndLateMetricsCannotReplaceThem() => await Run(() =>
    {
        var model = new ExportRenderStatusViewModel("output.mp4", 10);
        var sample = new RenderResourceSnapshot
        {
            SampledAtUtc = new(2026, 11, 3, 1, 2, 3, TimeSpan.Zero), AdapterName = "Selected GPU", AdapterLuid = 42,
            ProcessWorkingSetBytes = 1024UL * 1024, ProcessPeakWorkingSetBytes = 2UL * 1024 * 1024,
            ProcessLocalVideoMemoryUsageBytes = 3UL * 1024 * 1024,
            PeakProcessLocalVideoMemoryUsageBytes = 4UL * 1024 * 1024,
            LocalVideoMemoryBudgetBytes = 8UL * 1024 * 1024,
            VideoMemorySampledAtUtc = new(2026, 11, 3, 1, 2, 2, TimeSpan.Zero)
        };
        model.ApplyResourceSnapshot(sample);
        model.ApplyResourceSnapshot(sample with { SampledAtUtc = sample.SampledAtUtc.AddSeconds(1), ProcessWorkingSetBytes = null, ProcessLocalVideoMemoryUsageBytes = null });
        Assert.Equal("2.0 MiB", model.PeakWorkingSetText);
        Assert.Equal("4.0 MiB", model.PeakVideoMemoryText);
        Assert.NotEqual(model.SampleTimeText, model.VideoMemorySampleTimeText);
        var error = new InvalidOperationException("Encoder cleanup failed", new IOException("Disk unavailable"));
        model.MarkFailed(error);
        Assert.Equal(ExportTaskState.Failed, model.State);
        Assert.Equal(error.ToString(), model.ErrorText);
        Assert.True(model.HasError);
        Assert.True(model.CanClose);
        model.ApplyResourceSnapshot(sample with { SampledAtUtc = sample.SampledAtUtc.AddDays(1), PeakProcessLocalVideoMemoryUsageBytes = ulong.MaxValue });
        Assert.Equal("4.0 MiB", model.PeakVideoMemoryText);
        model.MarkCompleted();
        Assert.Equal(ExportTaskState.Failed, model.State);
    });

    [Fact]
    public async Task OlderResourceSamplesCannotReplaceNewerAdapterOrMeasurements() => await Run(() =>
    {
        var model = new ExportRenderStatusViewModel("output.mp4");
        var sample = new RenderResourceSnapshot { SampledAtUtc = DateTimeOffset.UtcNow, AdapterName = "Selected GPU", AdapterLuid = 7, GpuUtilizationPercent = 30 };
        model.ApplyResourceSnapshot(sample);
        model.ApplyResourceSnapshot(sample with { SampledAtUtc = sample.SampledAtUtc.AddSeconds(-1), AdapterName = "Old GPU", GpuUtilizationPercent = 99 });
        Assert.Equal("Selected GPU", model.AdapterText);
        Assert.Equal("30.0%", model.GpuText);
    });

    [Fact]
    public async Task RunningWindowCloseRequestsCancellationAndRetainsWindowUntilManualCloseAfterCleanup() => await Run(() =>
    {
        var model = new ExportRenderStatusViewModel("output.mp4", 30);
        var window = new ExportRenderStatusWindow(model);
        int requests = 0;
        model.CancellationRequested += (_, _) => requests++;
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var dots = window.FindControl<LoadingDots>("RenderLoadingDots")!;
            Assert.True(dots.IsActive); Assert.True(dots.IsVisible);
            Assert.False(window.FindControl<Button>("CloseStatusButton")!.IsEnabled);
            window.Close(); window.Close();
            Assert.True(window.IsVisible);
            Assert.Equal(1, requests);
            Assert.True(dots.IsActive);
            Assert.False(window.FindControl<Button>("CancelRenderButton")!.IsEnabled);
            model.MarkCancelled();
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsVisible);
            Assert.False(dots.IsActive); Assert.False(dots.IsVisible);
            Assert.True(window.FindControl<Button>("CloseStatusButton")!.IsEnabled);
            var timer = (DispatcherTimer)typeof(ExportRenderStatusWindow).GetField("elapsedTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            Assert.False(timer.IsEnabled);
            window.Close();
            Assert.False(window.IsVisible);
        }
        finally { model.MarkCancelled(); window.Close(); Dispatcher.UIThread.RunJobs(); }
    });

    [Theory]
    [InlineData("zh-CN", false)]
    [InlineData("en", true)]
    [InlineData("ja", false)]
    public async Task WindowBindsLocalizedRetainedFailureWithoutLosingUserPath(string language, bool light) => await Run(() =>
    {
        EditorLocalization.SetLanguage(language);
        var path = "D:/用户/カメラ/Long scene name/output.mp4";
        var model = new ExportRenderStatusViewModel(path, 600);
        var window = new ExportRenderStatusWindow(model) { RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        window.Show();
        try
        {
            model.MarkFailed(new IOException("Actual encoder failure"));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(EditorLocalization.Text("Mda.Text.ExportStatus.Title"), window.Title);
            Assert.Equal(EditorLocalization.Text("Mda.Text.ExportStatus.Failed"), window.FindControl<TextBlock>("StageText")!.Text);
            Assert.Equal(path, model.Destination);
            Assert.True(window.FindControl<Button>("CopyErrorButton")!.IsVisible);
            Assert.Contains("Actual encoder failure", window.FindControl<SelectableTextBlock>("ErrorDetails")!.Text);
            Assert.DoesNotContain("Mda.Text.", model.FooterText);
            Assert.DoesNotContain("Mda.Text.", model.RemainingText);
            Assert.True(window.FindControl<Button>("CloseStatusButton")!.IsEnabled);
        }
        finally { window.Close(); EditorLocalization.SetLanguage("zh-CN"); Dispatcher.UIThread.RunJobs(); }
    });

    private async Task Run(Action action) => await session.Dispatch(() =>
    {
        EditorLocalization.SetLanguage("zh-CN");
        action();
        return true;
    }, Token).WaitAsync(TimeSpan.FromSeconds(15), Token);

    private static ExportTaskProgress Progress(long completed, long total) => new(ExportTaskState.Running, completed, total, 0, new OfflineFrameTime(0, 1));

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
