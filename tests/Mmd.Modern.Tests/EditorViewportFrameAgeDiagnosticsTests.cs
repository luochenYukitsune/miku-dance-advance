using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using Avalonia.Headless;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EditorFrameAgeDiagnosticsCollection
{
    public const string Name = "Editor frame age diagnostics headless";
}

[Collection(EditorFrameAgeDiagnosticsCollection.Name)]
public sealed class EditorViewportFrameAgeDiagnosticsTests : IAsyncDisposable
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type Host = typeof(EditorViewportHost);
    // Reuse the drawing-free headless builder, never Show an EditorWindow or create an HWND.
    private HeadlessUnitTestSession? session;
    private HeadlessUnitTestSession Headless =>
        session ??= HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);

    public EditorViewportFrameAgeDiagnosticsTests() => InteractionDiagnostics.Stop();

    // Resolve the new API only at test execution so this test file compiles against the old
    // product first. The RED result is an explicit missing-contract assertion, not a C# error.
    public delegate void SessionRecord(long expectedSessionId, string stage, long correlationId,
        long relatedId, long value, string? detail, long elapsedTicks, long allocatedBytes);

    [Fact]
    public void SessionPinnedRecordingNeverRelabelsOldWorkAsANewSession()
    {
        var record = Recorder();
        InteractionDiagnostics.Start(new() { Capacity = 16, CaptureSlowWindows = false });
        long first = InteractionDiagnostics.SessionId;
        record(first, "old.accepted", 11, 12, 13, "before", 14, 15);
        Assert.Single(InteractionDiagnostics.Snapshot().Events);
        InteractionDiagnostics.Start(new() { Capacity = 16, CaptureSlowWindows = false });
        long second = InteractionDiagnostics.SessionId;
        Assert.NotEqual(first, second);
        record(first, "old.rejected", 11, 0, 0, null, 0, 0);
        record(0, "zero.rejected", 11, 0, 0, null, 0, 0);
        Assert.Empty(InteractionDiagnostics.Snapshot().Events);
        record(second, "new.accepted", 21, 22, 23, "after", 24, 25);
        var entry = Assert.Single(InteractionDiagnostics.Snapshot().Events);
        Assert.Equal("new.accepted", entry.Stage);
        Assert.Equal(21, entry.CorrelationId);
        Assert.Equal(22, entry.RelatedId);
        Assert.Equal(23, entry.Value);
        Assert.Equal(24, entry.ElapsedTicks);
        Assert.Equal(25, entry.AllocatedBytes);
        InteractionDiagnostics.Stop();
        record(second, "stopped.rejected", 0, 0, 0, null, 0, 0);
        Assert.Single(InteractionDiagnostics.Snapshot().Events);
    }

    [Fact]
    public void DisabledSessionPinnedRecordingAllocatesNothing()
    {
        var record = Recorder();
        _ = MeasureDisabledRecording(record); // Warm the exact delegate and measurement boundary.
        Assert.Equal(0, MeasureDisabledRecording(record));
        Assert.False(InteractionDiagnostics.IsEnabled);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static long MeasureDisabledRecording(SessionRecord record)
    {
        // Keep assertion/type initialization outside the allocation window. The contract
        // remains exactly zero, without permitting a per-call allocation or a tolerance.
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; ++i) record(1, "disabled", i, 0, 0, null, 0, 0);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public async Task BusyQueueReplacesPendingWithoutAdmittingBeforeDequeue() => await Headless.Dispatch(() =>
    {
        using var viewport = new EditorViewportHost();
        InteractionDiagnostics.Start(new() { Capacity = 32, CaptureSlowWindows = false });
        Set(viewport, "accepting", true); Set(viewport, "workerRunning", true); Set(viewport, "revision", 7L);
        var held = Work(17.25, InteractionDiagnostics.SessionId, 801);
        var middle = Work(18.5, InteractionDiagnostics.SessionId, 802);
        var latest = Work(19.75, InteractionDiagnostics.SessionId, 803);
        Invoke(viewport, "QueueWork", held);
        Invoke(viewport, "QueueWork", middle);
        Invoke(viewport, "QueueWork", latest);
        Assert.Same(latest, Field(viewport, "pending"));
        Assert.Equal(17.25, ((PreviewFrame)Property(held, "Frame")!).SourceFrame);
        Assert.Equal(801L, Property(held, "FrameId"));
        Assert.True((bool)Field(viewport, "workerRunning")!);
        Assert.True(((Task)Field(viewport, "workerTask")!).IsCompleted);
        Assert.False(viewport.HasChildWindow);
        Assert.Null(Field(viewport, "renderer"));
        var entries = InteractionDiagnostics.Snapshot().Events;
        Assert.DoesNotContain(entries, e => e.Stage == "render.admitted"); // Admission occurs only when the worker takes the work.
        Assert.Equal(new long[] { 801, 802 }, entries.Where(e => e.Stage == "render.superseded").Select(e => e.RelatedId).ToArray());
        Assert.All(entries, entry => Assert.Contains("sourceFrame=", entry.Detail!));
        // This executes the real admission/replacement method with the worker held busy. It
        // does not execute RenderPendingAsync, Present, scan-out, or a simulated GPU.
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task AgeMetadataKeepsFractionalSourceAndOriginalStageDurationWithInvariantBoundedDetail() => await Headless.Dispatch(() =>
    {
        using var viewport = new EditorViewportHost();
        InteractionDiagnostics.Start(new() { Capacity = 16, CaptureSlowWindows = false });
        long stamp = Stopwatch.GetTimestamp();
        long owner = InteractionDiagnostics.SessionId;
        SetTimeline(viewport, owner, 42.5, stamp - 20, 777);
        var work = Work(40.25, owner, 900, stamp - 100, stamp - 50, stamp - 40, 41.5);
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Invoke(viewport, "RecordWorkStage", "render.present-return", work, 7L, 123456L, 9L);
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
        var entry = Assert.Single(InteractionDiagnostics.Snapshot().Events);
        Assert.Equal("render.present-return", entry.Stage);
        Assert.Equal(900, entry.RelatedId);
        Assert.Equal(123456, entry.ElapsedTicks); // Still PresentAsync duration, not frame age.
        Assert.Equal(7, entry.Value); Assert.Equal(9, entry.AllocatedBytes);
        Assert.NotNull(entry.Detail); Assert.InRange(entry.Detail.Length, 1, 512);
        var detail = Detail(entry);
        Assert.Equal("40.25", detail["sourceFrame"]);
        Assert.Equal("41.5", detail["timelineAtCapture"]);
        Assert.Equal("42.5", detail["latestObservedTimelineFrame"]);
        Assert.Equal(stamp - 20, Long(detail, "timelineObservedAt"));
        Assert.Equal(777, Long(detail, "timelineInputId"));
        Assert.Equal(888, Long(detail, "captureInputId"));
        Assert.Equal(stamp - 100, Long(detail, "captureStartedAt"));
        Assert.Equal(stamp - 50, Long(detail, "captureCompletedAt"));
        Assert.Equal(stamp - 40, Long(detail, "queuedAt"));
        long observed = Long(detail, "stageObservedAt");
        Assert.True(observed >= stamp);
        Assert.Equal(observed - (stamp - 50), Long(detail, "captureAgeTicks"));
        Assert.Equal(observed - (stamp - 40), Long(detail, "queueAgeTicks"));
        Assert.False(viewport.HasChildWindow); Assert.Null(Field(viewport, "renderer"));
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task OldWorkAndOldTimelineObservationCannotLeakAcrossDiagnosticSessions() => await Headless.Dispatch(() =>
    {
        using var viewport = new EditorViewportHost();
        InteractionDiagnostics.Start(new() { Capacity = 16, CaptureSlowWindows = false });
        long first = InteractionDiagnostics.SessionId;
        var old = Work(3.5, first, 1);
        SetTimeline(viewport, first, 4.5, Stopwatch.GetTimestamp(), 17);
        InteractionDiagnostics.Start(new() { Capacity = 16, CaptureSlowWindows = false });
        Invoke(viewport, "RecordWorkStage", "render.present-return", old, 0L, 11L, 0L);
        Assert.Empty(InteractionDiagnostics.Snapshot().Events);
        var current = Work(5.5, InteractionDiagnostics.SessionId, 2);
        Invoke(viewport, "RecordWorkStage", "render.present-return", current, 0L, 22L, 0L);
        var entry = Assert.Single(InteractionDiagnostics.Snapshot().Events);
        Assert.Equal(2, entry.RelatedId);
        Assert.Equal("n/a", Detail(entry)["latestObservedTimelineFrame"]);
        Assert.Equal(0, Long(Detail(entry), "timelineObservedAt"));
        Assert.Equal(0, Long(Detail(entry), "timelineInputId"));
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task SameSessionTimelineFromAnotherViewportRevisionIsUnknownWithoutDroppingTheWorkEvent() => await Headless.Dispatch(() =>
    {
        using var viewport = new EditorViewportHost();
        InteractionDiagnostics.Start(new() { Capacity = 16, CaptureSlowWindows = false });
        long owner = InteractionDiagnostics.SessionId;
        SetTimeline(viewport, owner, 99.5, Stopwatch.GetTimestamp(), 901, revision: 8);
        var work = Work(3.5, owner, 4);
        Invoke(viewport, "RecordWorkStage", "render.present-return", work, 0L, 11L, 0L);
        var entry = Assert.Single(InteractionDiagnostics.Snapshot().Events);
        Assert.Equal(4, entry.RelatedId);
        Assert.Equal(11, entry.ElapsedTicks);
        var detail = Detail(entry);
        Assert.Equal("3.5", detail["sourceFrame"]);
        Assert.Equal("n/a", detail["latestObservedTimelineFrame"]);
        Assert.Equal(0, Long(detail, "timelineObservedAt"));
        Assert.Equal(0, Long(detail, "timelineInputId"));
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task NoChildWindowStillPreventsCaptureAndWorkerAdmission() => await Headless.Dispatch(() =>
    {
        using var viewport = new EditorViewportHost();
        InteractionDiagnostics.Start(new() { Capacity = 16, CaptureSlowWindows = false });
        viewport.RequestRender();
        Assert.False(viewport.HasChildWindow);
        Assert.False((bool)Field(viewport, "capturePosted")!);
        Assert.Null(Field(viewport, "pending")); Assert.Null(Field(viewport, "renderer"));
        Assert.True(((Task)Field(viewport, "workerTask")!).IsCompleted);
        Assert.Empty(InteractionDiagnostics.Snapshot().Events);
        var observation = Field(viewport, "latestDiagnosticTimeline")!;
        Assert.Equal(InteractionDiagnostics.SessionId, Property(observation, "SessionId"));
        Assert.Equal(Field(viewport, "revision"), Property(observation, "Revision"));
        Assert.True((long)Property(observation, "ObservedAt")! > 0);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ExtremeFiniteMetadataRetainsEveryKeyWithinTheExistingRingLimit() => await Headless.Dispatch(() =>
    {
        using var viewport = new EditorViewportHost();
        InteractionDiagnostics.Start(new() { Capacity = 16, CaptureSlowWindows = false });
        long owner = InteractionDiagnostics.SessionId;
        SetTimeline(viewport, owner, double.MaxValue, long.MaxValue, long.MaxValue);
        var work = Work(double.Epsilon, owner, 3, long.MaxValue, long.MaxValue, long.MaxValue, double.MaxValue);
        Invoke(viewport, "RecordWorkStage", "render.present-return", work, 0L, 1L, 0L);
        var entry = Assert.Single(InteractionDiagnostics.Snapshot().Events);
        Assert.NotNull(entry.Detail); Assert.InRange(entry.Detail.Length, 1, 512);
        var detail = Detail(entry);
        string[] keys = ["sourceFrame", "timelineAtCapture", "latestObservedTimelineFrame", "timelineObservedAt",
            "timelineInputId", "captureInputId", "captureStartedAt", "captureCompletedAt", "queuedAt",
            "stageObservedAt", "captureAgeTicks", "queueAgeTicks"];
        Assert.Equal(keys.Order(), detail.Keys.Order());
        Assert.Equal(double.Epsilon.ToString("R", CultureInfo.InvariantCulture), detail["sourceFrame"]);
        _ = Long(detail, "queueAgeTicks");
        return true;
    }, TestContext.Current.CancellationToken);

    private static object Work(double source, long owner, long frameId, long started = 0,
        long completed = 0, long queued = 0, double? timelineAtCapture = null)
    {
        var camera = new SceneCamera(new(0, 1, -3), Vector3.Zero, Vector3.UnitY, 4, .1f, 100);
        var scene = new OriginalFrameScene(new(SceneSnapshot.CreateEmpty(camera), []));
        var frame = new PreviewFrame(100, 80, source, scene, null, null, null) { ResolvedCamera = camera };
        var quality = new ViewQualityOptions();
        var bindings = EditorModernRenderEngine.Instance.EmptyBinding;
        var candidate = new EditorPoseFrameCandidate(0, 100, 80, 7, quality, bindings, frame, 0,
            new object(), null, null, false, false, false);
        var capture = NewNested("DiagnosticFrameCapture", started, completed, timelineAtCapture, 888L);
        return NewNested("RenderWork", (nint)0, 100, 80, 7L, quality, bindings, frame, 0L,
            candidate, 123L, frameId, queued, owner, capture);
    }

    private static void SetTimeline(EditorViewportHost viewport, long owner, double? frame, long observed, long input, long revision = 7)
        => Set(viewport, "latestDiagnosticTimeline", NewNested("DiagnosticTimelineObservation", owner, revision, frame, observed, input));
    private static SessionRecord Recorder()
    {
        var method = typeof(InteractionDiagnostics).GetMethod("RecordForSession", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        return method.CreateDelegate<SessionRecord>();
    }
    private static object NewNested(string name, params object?[] args)
    {
        var type = Host.GetNestedType(name, BindingFlags.NonPublic);
        Assert.NotNull(type);
        var constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(c => c.GetParameters().Length == args.Length);
        Assert.NotNull(constructor);
        return constructor.Invoke(args);
    }
    private static object? Field(EditorViewportHost viewport, string name)
    {
        var field = Host.GetField(name, Hidden); Assert.NotNull(field);
        return field.GetValue(viewport);
    }
    private static void Set(EditorViewportHost viewport, string name, object value)
    {
        var field = Host.GetField(name, Hidden); Assert.NotNull(field);
        field.SetValue(viewport, value);
    }
    private static object? Property(object value, string name)
    {
        var property = value.GetType().GetProperty(name); Assert.NotNull(property);
        return property.GetValue(value);
    }
    private static object? Invoke(EditorViewportHost viewport, string name, params object?[] args)
    {
        var method = Host.GetMethod(name, Hidden); Assert.NotNull(method);
        return method.Invoke(viewport, args);
    }
    private static Dictionary<string, string> Detail(InteractionDiagnosticEvent entry)
        => entry.Detail!.Split(';').Select(part => part.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1]);
    private static long Long(Dictionary<string, string> detail, string key) => long.Parse(detail[key], CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        InteractionDiagnostics.Stop();
        // A failed Dispatch may inline the test continuation on the session worker. Never
        // synchronously wait for that worker from its own continuation during teardown.
        if (session is { } current) await Task.Run(current.Dispose).ConfigureAwait(false);
    }
}
