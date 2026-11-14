using System.Numerics;
using System.Reflection;
using Avalonia.Headless;
using Mmd.Modern.App;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorPresentationBoundaryTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));
    private const BindingFlags InternalStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly CancellationToken token = TestContext.Current.CancellationToken;

    [Fact]
    public void SharedPresentationReportContainsNoBackendTypes()
    {
        var report = typeof(EditorViewportHost).GetProperty(nameof(EditorViewportHost.Statistics))!.PropertyType;
        Assert.Equal("EditorViewportPresentationStatistics", report.Name);
        Assert.Equal(report, typeof(IEditorViewportRenderer).GetProperty("Statistics")!.PropertyType);
        Assert.Equal(typeof(Action<>).MakeGenericType(report),
            typeof(EditorViewportHost).GetEvent(nameof(EditorViewportHost.FramePresented))!.EventHandlerType);
        AssertNeutral(report, []);
    }

    [Fact]
    public void D3D12AdapterPreservesPresentationQualityTimingAndDiagnosticValues()
    {
        var frame = new D3D12FrameStatistics(41, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31,
            37, 43, 2, 53, "device", "commands")
        {
            RecordCpuMilliseconds = 11.5, GeometryCacheMilliseconds = 2.75, RecordAllocatedBytes = 12345,
            LastSlotWaitMilliseconds = .75, SubmitCpuMilliseconds = 1.25,
            SpatialSamples = 4, Antialiasing = FrameAntialiasing.Smaa1X, SmaaQuality = SmaaQuality.Ultra,
            UpscalingInputWidth = 640, UpscalingInputHeight = 360,
            TemporalUpscaling = new("provider", 1, false, 2, 3, Vector2.Zero, 640, 360, 1280, 720),
            SceneRecordCpu = new()
            {
                SurfaceBoundsBuild = new(2.25, 12, 3), OutlineBoundsBuild = new(3.75, 24, 4)
            }
        };
        var native = new D3D12PresentationStatistics(61, 67, 71, 73, 1280, 720, 2, frame)
        {
            RequestedSpatialSamples = 16, EffectiveSpatialSamples = 4,
            PreviewSamplingAdjustmentReason = "memory budget",
            Timing = new(2.5, 3.5, 6) { InterPresentMilliseconds = 10 }
        };
        var report = Convert(native);
        foreach (string property in new[] { "PresentCount", "ExplicitCaptureCount", "ResizeCount", "IdleWaitCount",
            "Width", "Height", "BackbufferCount", "RequestedSpatialSamples", "EffectiveSpatialSamples",
            "PreviewSamplingAdjustmentReason" })
            Assert.Equal(Read(native, property), Read(report, property));
        var timing = Read(report, "Timing")!;
        Assert.Equal(2.5, Read(timing, "RecordAndSubmitMilliseconds"));
        Assert.Equal(3.5, Read(timing, "PresentWaitMilliseconds"));
        Assert.Equal(6d, Read(timing, "TotalMilliseconds"));
        Assert.Equal(10d, Read(timing, "InterPresentMilliseconds"));
        var mappedFrame = Read(report, "Frame")!;
        foreach (string property in new[] { "FrameId", "SubmissionCount", "SubmittedFence", "CompletedFence", "PendingFrames",
            "RecordCpuMilliseconds", "GeometryCacheMilliseconds", "RecordAllocatedBytes", "LastSlotWaitMilliseconds", "SubmitCpuMilliseconds",
            "SpatialSamples", "Antialiasing", "SmaaQuality", "UpscalingInputWidth", "UpscalingInputHeight" })
            Assert.Equal(Read(frame, property), Read(mappedFrame, property));
        var temporal = Read(mappedFrame, "TemporalUpscaling")!;
        Assert.Equal(640, Read(temporal, "RenderWidth"));
        Assert.Equal(360, Read(temporal, "RenderHeight"));
        var scene = Read(mappedFrame, "SceneRecordCpu")!;
        Assert.Equal(2.25, Read(Read(scene, "SurfaceBoundsBuild")!, "CpuMilliseconds"));
        Assert.Equal(3.75, Read(Read(scene, "OutlineBoundsBuild")!, "CpuMilliseconds"));

        var empty = Convert(new(0, 0, 0, 0, 100, 80, 2, null));
        Assert.Null(Read(empty, "Frame"));
        Assert.Null(Read(empty, "Timing"));
        Assert.Equal(1, Read(empty, "RequestedSpatialSamples"));
        Assert.Equal(1, Read(empty, "EffectiveSpatialSamples"));
    }

    [Fact]
    public async Task StartupWaitSubscribesBeforeRequestAndWaitsForAnActualPresentation()
        => await session.Dispatch(async () =>
        {
            using var viewport = new EditorViewportHost();
            bool requested = false;
            var waiting = WaitForFirstPresentation(viewport, () => requested = true, token);
            Assert.True(requested);
            Assert.False(waiting.IsCompleted);
            Publish(viewport, "FramePresented", Convert(new(1, 0, 0, 0, 100, 80, 2, null)));
            await waiting.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Null(Subscription(viewport, "FramePresented"));
            Assert.Null(Subscription(viewport, "Error"));

            // A backend may present synchronously during the request. Subscription must
            // precede that request so startup cannot miss its first completed frame.
            await WaitForFirstPresentation(viewport,
                () => Publish(viewport, "FramePresented", Convert(new(2, 0, 0, 0, 100, 80, 2, null))), token);
            return true;
        }, token);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupWaitPropagatesFailureOrCancellationAndDetaches(bool cancel)
        => await session.Dispatch(async () =>
        {
            using var viewport = new EditorViewportHost();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var waiting = WaitForFirstPresentation(viewport, () => { }, cancellation.Token);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            }
            else
            {
                var error = new InvalidOperationException("backend presentation failed");
                Publish(viewport, "Error", error);
                Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => waiting));
            }
            Assert.Null(Subscription(viewport, "FramePresented"));
            Assert.Null(Subscription(viewport, "Error"));
            return true;
        }, token);

    [Fact]
    public async Task StartupWaitSkipsAnAlreadyCancelledRequestAndDetachesWhenRequestThrows()
        => await session.Dispatch(async () =>
        {
            using var viewport = new EditorViewportHost();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            bool requested = false;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                WaitForFirstPresentation(viewport, () => requested = true, cancellation.Token));
            Assert.False(requested);
            Assert.Null(Subscription(viewport, "FramePresented"));
            Assert.Null(Subscription(viewport, "Error"));

            var error = new InvalidOperationException("request failed");
            Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WaitForFirstPresentation(viewport, () => throw error, token)));
            Assert.Null(Subscription(viewport, "FramePresented"));
            Assert.Null(Subscription(viewport, "Error"));
            return true;
        }, token);

    private static void AssertNeutral(Type type, HashSet<Type> visited)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        Assert.DoesNotContain("D3D12", type.FullName ?? type.Name);
        Assert.NotEqual(typeof(object), type);
        if (!visited.Add(type) || type.Namespace != typeof(EditorViewportHost).Namespace) return;
        foreach (var property in type.GetProperties()) AssertNeutral(property.PropertyType, visited);
    }

    private static object Convert(D3D12PresentationStatistics native)
    {
        var conversion = typeof(EditorViewportRenderer).GetMethod("ToPresentationStatistics", InternalStatic);
        Assert.NotNull(conversion);
        return conversion.Invoke(null, [native])!;
    }

    private static Task WaitForFirstPresentation(EditorViewportHost viewport, Action request, CancellationToken token)
    {
        var wait = typeof(EditorStartupApplication).GetMethod("AwaitFirstPresentationAsync", InternalStatic,
            binder: null, [typeof(EditorViewportHost), typeof(Action), typeof(CancellationToken)], modifiers: null);
        Assert.NotNull(wait);
        return (Task)wait.Invoke(null, [viewport, request, token])!;
    }

    private static object? Read(object value, string name)
    {
        var property = value.GetType().GetProperty(name);
        Assert.NotNull(property);
        return property.GetValue(value);
    }

    private static Delegate? Subscription(EditorViewportHost viewport, string name)
        => (Delegate?)typeof(EditorViewportHost).GetField(name, Instance)!.GetValue(viewport);
    private static void Publish(EditorViewportHost viewport, string name, object value)
        => Subscription(viewport, name)!.DynamicInvoke(value);

    public ValueTask DisposeAsync()
    { session.Dispose(); return ValueTask.CompletedTask; }
}
