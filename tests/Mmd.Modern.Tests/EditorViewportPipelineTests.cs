using System.Numerics;
using System.Reflection;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EditorViewportPipelineSession : IAsyncDisposable
{
    private HeadlessUnitTestSession? session;
    private IDisposable? locatorScope;
    public HeadlessUnitTestSession Headless
    {
        get
        {
            if (session is not null) return session;

            // PerAssembly keeps one dispatcher for the pipeline's multiple Dispatch calls,
            // but Avalonia does not scope its shared application. Keep its MediaContext and
            // clock out of the locator inherited by later PerTest sessions on other threads.
            // The pinned 11.3.22 runtime exposes this method, but its reference
            // assembly marks it internal. Fail explicitly if that contract changes.
            var enterScope = typeof(AvaloniaLocator).GetMethod("EnterScope",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            if (enterScope is null || !typeof(IDisposable).IsAssignableFrom(enterScope.ReturnType))
                throw new InvalidOperationException("Avalonia 11.3.22 locator scope contract is unavailable.");
            locatorScope = enterScope.Invoke(null, null) as IDisposable
                ?? throw new InvalidOperationException("Avalonia locator scope was not returned.");
            try
            {
                return session = HeadlessTestHost.Start(
                    QualityLoadingAppBuilder.BuildAvaloniaApp, AvaloniaTestIsolationLevel.PerAssembly);
            }
            catch
            {
                locatorScope.Dispose();
                locatorScope = null;
                throw;
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (session is { } current) await Task.Run(current.Dispose).ConfigureAwait(false);
        }
        finally
        {
            locatorScope?.Dispose();
        }
    }
}

// Exercises the actual dispatcher/worker path with no HWND, D3D device or native presentation.
[Collection(EditorFrameAgeDiagnosticsCollection.Name)]
public sealed class EditorViewportPipelineTests(EditorViewportPipelineSession fixture) : IClassFixture<EditorViewportPipelineSession>
{
    private HeadlessUnitTestSession Headless => fixture.Headless;
    // The persistent UI thread has a different execution context. Capture each test's
    // token before dispatch rather than reading a previous test's AsyncLocal there.
    private readonly CancellationToken Token = TestContext.Current.CancellationToken;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RecoveryBarrierDefersBasePoseUntilLatestInputWithoutBlockingLaterOrdinaryFrames(bool useRecoveryPredicate)
    {
        var renderer = new FakeRenderer { GateFramePermits = true };
        var f = await Create(_ => renderer);
        var captured = NewSignal();
        try
        {
            Assert.Equal(0, await Next(renderer.PermitWaits));
            bool pending = true;
            await Ui(() =>
            {
                var provider = f.Host.SnapshotProvider!;
                f.Host.SnapshotProvider = requests => { var snapshot = provider(requests); captured.TrySetResult(); return snapshot; };
                // A failed Complete worker has already cleared IsPreparingPreview. Recovery's
                // predicate must still retain the image until a latest valid input is available.
                f.Host.PoseInputPending = useRecoveryPredicate ? () => false : () => pending;
                f.Host.RequireCurrentPoseInputForNextFrame(useRecoveryPredicate ? () => pending : null);
                f.Request(7);
                return true;
            });
            renderer.ReleasePermit(0);
            await Pump(captured.Task);
            await Ui(() =>
            {
                Assert.Equal(1, f.Captures);
                Assert.False((bool)typeof(EditorViewportHost).GetField("workerRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Host)!);
                Assert.False(renderer.Frames.Reader.TryRead(out _));
                Assert.False(f.Callbacks.Reader.TryRead(out _));
                Assert.False(f.Errors.Reader.TryRead(out _));
                pending = false; f.Request(8); // The pose publication's existing RequestRender wake-up.
                return true;
            });
            Assert.Equal(8, (await Next(renderer.Frames)).SourceFrame);
            renderer.Release(0); await Next(f.Callbacks);
            await Ui(() => { pending = true; f.Request(9); return true; });
            Assert.Equal(1, await Next(renderer.PermitWaits));
            renderer.ReleasePermit(1);
            Assert.Equal(9, (await Next(renderer.Frames)).SourceFrame); // Recovery's one-shot barrier is consumed.
            renderer.Release(1); await Next(f.Callbacks);
        }
        finally { await Cleanup(f, renderer); }
    }

    [Fact]
    public async Task FramePermitIsObtainedBeforeFirstCaptureAndEveryFollowingCapture()
    {
        var renderer = new FakeRenderer { GateFramePermits = true };
        var f = await Create(_ => renderer);
        try
        {
            Assert.Equal(0, await Next(renderer.PermitWaits));
            await Ui(() => { f.Request(1); f.Request(2); f.Request(3); return true; });
            Assert.Equal(0, await Ui(() => f.Captures));
            renderer.ReleasePermit(0);
            Assert.Equal(3, (await Next(renderer.Frames)).SourceFrame);
            renderer.Release(0);
            Assert.Equal(1, (await Next(f.Callbacks)).PresentCount);
            await Ui(() => { f.Request(4); return true; });
            Assert.Equal(1, await Next(renderer.PermitWaits));
            await Ui(() => { f.Request(5); f.Request(6); return true; });
            Assert.Equal(1, await Ui(() => f.Captures));
            renderer.ReleasePermit(1);
            Assert.Equal(6, (await Next(renderer.Frames)).SourceFrame);
            Assert.Equal(2, await Ui(() => f.Captures));
            renderer.Release(1);
            Assert.Equal(2, (await Next(f.Callbacks)).PresentCount);
        }
        finally { await Cleanup(f, renderer); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaybackRecordingAheadSurvivesTicksAndCancelsOnDiscontinuity(bool suspend)
    {
        var renderer = new FakeRenderer { GateFramePermits = true, SupportsRecordingAhead = true };
        var f = await Create(_ => renderer, host =>
        {
            host.Quality = new(new(LightingMode.HardwareRayTracing));
            host.SetPlaybackActive(true);
        });
        try
        {
            Assert.Equal(0, await Next(renderer.PermitWaits));
            Assert.Equal(0, await Ui(() => f.Captures));
            Assert.False(renderer.AheadFrames.Reader.TryRead(out _));
            renderer.ReleasePermit(0);
            Assert.Equal(0, (await Next(renderer.Frames)).SourceFrame);
            renderer.Release(0);
            Assert.Equal(1, (await Next(f.Callbacks)).PresentCount);

            await Ui(() => { f.Request(1); return true; });
            Assert.Equal(1, (await Next(renderer.AheadFrames)).SourceFrame);
            await Ui(() => { f.Request(2); return true; });
            Assert.False(renderer.Frames.Reader.TryRead(out _));
            Assert.False(f.Callbacks.Reader.TryRead(out _));
            renderer.ReleaseAhead(0);
            Assert.Equal(1, (await Next(renderer.Frames)).SourceFrame);
            renderer.Release(1);
            Assert.Equal(2, (await Next(f.Callbacks)).PresentCount);
            Assert.Equal(2, (await Next(renderer.AheadFrames)).SourceFrame);

            int captures = await Ui(() =>
            {
                if (suspend) f.Host.IsSuspended = true;
                else f.Host.InvalidateTemporalHistory();
                f.Request(8);
                if (suspend) f.Host.IsSuspended = false;
                return f.Captures;
            });
            // The abandoned recording's gate is still closed. Cancellation must let the
            // replacement return to the strict permit-before-capture path.
            Assert.Equal(1, await Next(renderer.PermitWaits));
            Assert.Equal(captures, await Ui(() => f.Captures));
            Assert.False(renderer.Frames.Reader.TryRead(out _));
            Assert.False(f.Callbacks.Reader.TryRead(out _));
            renderer.ReleasePermit(1);
            Assert.Equal(8, (await Next(renderer.Frames)).SourceFrame);
            Assert.Equal(3, (await Next(f.Callbacks)).PresentCount);
            Assert.False(f.Errors.Reader.TryRead(out _));
        }
        finally { await Cleanup(f, renderer); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedPermitIsCancelledBeforeUiDisposalOrResourceRelease(bool releaseResources)
    {
        var renderer = new FakeRenderer { GateFramePermits = true };
        var f = await Create(_ => renderer);
        try
        {
            await Next(renderer.PermitWaits);
            if (releaseResources)
            {
                Task? release = null;
                await Ui(() => { f.Host.IsSuspended = true; release = f.Host.SuspendAndReleaseResourcesAsync(Token); return true; });
                await Pump(release!);
            }
            else await Ui(() => { f.Host.Dispose(); return true; });
            Assert.Equal(1, renderer.Disposals);
            Assert.Equal(0, await Ui(() => f.Captures));
            Assert.False(f.Errors.Reader.TryRead(out _));
        }
        finally { await Cleanup(f, renderer); }
    }

    [Fact]
    public async Task RestartCancelsOldPermitAndCapturesLatestOnlyAfterNewPermit()
    {
        var old = new FakeRenderer { GateFramePermits = true };
        var fresh = new FakeRenderer { GateFramePermits = true };
        int count = 0;
        var f = await Create(_ => Interlocked.Increment(ref count) == 1 ? old : fresh);
        try
        {
            await Next(old.PermitWaits);
            await Ui(() => { f.Host.RestartRenderer(); f.Request(8); return true; });
            await Next(fresh.PermitWaits);
            Assert.Equal(1, old.Disposals);
            Assert.Equal(0, await Ui(() => f.Captures));
            fresh.ReleasePermit(0);
            Assert.Equal(8, (await Next(fresh.Frames)).SourceFrame);
            fresh.Release(0);
            await Next(f.Callbacks);
            Assert.False(f.Errors.Reader.TryRead(out _));
        }
        finally { await Cleanup(f, old, fresh); }
    }

    [Fact]
    public async Task BusyPresentCapturesOnlyLatestDemandAndDeliversBothCallbacks()
    {
        var renderer = new FakeRenderer();
        var f = await Create(_ => renderer);
        try
        {
            var a = await Next(renderer.Frames);
            Assert.Equal(0, a.SourceFrame);
            int before = await Ui(() => f.Captures);
            Assert.Equal(1, before); // Initialization discovers requests before this first capture.
            await Ui(() => { f.Request(1); f.Request(2); f.Request(3); return true; });
            Assert.Equal(before, await Ui(() => f.Captures));
            renderer.Release(0);
            var d = await Next(renderer.Frames);
            Assert.Equal(3, d.SourceFrame);
            Assert.Equal(2, await Ui(() => f.Captures));
            Assert.Equal(1, (await Next(f.Callbacks)).PresentCount);
            renderer.Release(1);
            Assert.Equal(2, (await Next(f.Callbacks)).PresentCount);
            await Ui(() => { Assert.False(f.Host.HasChildWindow); return true; });
        }
        finally { await Cleanup(f, renderer); }
        Assert.Equal(1, renderer.Disposals);
    }

    [Fact]
    public async Task RestartWhilePresentIsBlockedCreatesNewBackendForLatestFrame()
    {
        var old = new FakeRenderer(); var fresh = new FakeRenderer { CounterBase = 1000 }; int count = 0;
        var f = await Create(_ => Interlocked.Increment(ref count) == 1 ? old : fresh);
        try
        {
            Assert.Equal(0, (await Next(old.Frames)).SourceFrame);
            await Ui(() => { f.Host.RestartRenderer(); f.Request(2); f.Request(4); return true; });
            Assert.Equal(1, await Ui(() => f.Captures));
            old.Release(0);
            Assert.Equal(4, (await Next(fresh.Frames)).SourceFrame);
            Assert.Equal(1, old.Disposals);
            fresh.Release(0);
            Assert.Equal(1001, (await Next(f.Callbacks)).PresentCount);
            Assert.False(f.Callbacks.Reader.TryRead(out _));
            await Ui(() => { Assert.False(f.Host.HasChildWindow); return true; });
        }
        finally { await Cleanup(f, old, fresh); }
        Assert.Equal(1, fresh.Disposals);
    }

    [Fact]
    public async Task SuspendReleaseAndDisposeFinishWithoutUiCallbacks()
    {
        var old = new FakeRenderer(); var fresh = new FakeRenderer(); int count = 0;
        var f = await Create(_ => Interlocked.Increment(ref count) == 1 ? old : fresh);
        try
        {
            await Next(old.Frames);
            Task? release = null;
            await Ui(() => { f.Host.IsSuspended = true; f.Request(1); release = f.Host.SuspendAndReleaseResourcesAsync(Token); return true; });
            Assert.False(release!.IsCompleted);
            old.Release(0);
            await Pump(release);
            Assert.Equal(1, old.Disposals);
            await Ui(() => { f.Host.RequestRender(); return true; });
            Assert.Equal(1, await Ui(() => f.Captures));
            Assert.False(f.Callbacks.Reader.TryRead(out _));

            await Ui(() => { f.Host.IsSuspended = false; f.Request(2); return true; });
            Assert.Equal(2, (await Next(fresh.Frames)).SourceFrame);
            var entered = NewSignal();
            var disposing = Ui(() => { entered.TrySetResult(); f.Host.Dispose(); return true; });
            await entered.Task.WaitAsync(Timeout, Token);
            // Disposal has been invoked on the UI. Its precise entry into the join is
            // intentionally not inferred from this signal; completion needs no UI callback.
            fresh.Release(0);
            await disposing.WaitAsync(Timeout, Token);
            await Ui(() => { Dispatcher.UIThread.RunJobs(); Assert.False(f.Host.HasChildWindow); return true; });
            Assert.False(f.Callbacks.Reader.TryRead(out _));
            Assert.Equal(1, fresh.Disposals);
        }
        finally { await Cleanup(f, old, fresh); }
    }

    [Theory]
    [InlineData("factory")]
    [InlineData("permit")]
    [InlineData("present")]
    public async Task BackendRebuildsWithoutNewInputAndCanAddAndEditLights(string failure)
    {
        var failed = new FakeRenderer { FailPresent = failure == "present", FailPermit = failure == "permit" };
        var recovered = new FakeRenderer();
        int creations = 0;
        var f = await Create(_ =>
        {
            if (Interlocked.Increment(ref creations) != 1) return recovered;
            if (failure == "factory") throw new InvalidOperationException("factory failure");
            return failed;
        });
        try
        {
            Assert.Equal(0, (await Next(recovered.Frames)).SourceFrame);
            Assert.Equal(failure == "factory" ? 0 : 1, failed.Disposals);
            recovered.Release(0);
            await Next(f.Callbacks);
            var lamp = new NativeSceneLight(Guid.NewGuid(), "fill", SceneLightKind.Point,
                Vector3.Zero, Quaternion.Identity, Vector3.One);
            await Ui(() => { f.Project.Edit(edit => edit.Lighting.Add(lamp), Token); f.Host.RequestRender(); return true; });
            Assert.Equal(lamp, Assert.Single((await Next(recovered.Frames)).Scene.Lighting!.Lights));
            recovered.Release(1);
            await Next(f.Callbacks);
            var brighter = lamp with { Intensity = 3 };
            await Ui(() => { f.Project.Edit(edit => edit.Lighting.Update(brighter), Token); f.Host.RequestRender(); return true; });
            Assert.Equal(brighter, Assert.Single((await Next(recovered.Frames)).Scene.Lighting!.Lights));
            await Next(f.Callbacks);
            Assert.False(await Ui(() => f.Host.HasRendererError));
            Assert.False(f.Errors.Reader.TryRead(out _));
            Assert.Equal(2, Volatile.Read(ref creations));
        }
        finally { await Cleanup(f, failed, recovered); }
    }

    [Fact]
    public async Task RepeatedFactoryFailureStopsUntilExplicitRetry()
    {
        var recovered = new FakeRenderer();
        int creations = 0;
        var f = await Create(_ => Interlocked.Increment(ref creations) <= 2
            ? throw new InvalidOperationException("factory failure") : recovered);
        try
        {
            Assert.Equal("factory failure", (await Next(f.Errors)).Message);
            await Ui(() =>
            {
                f.Host.RequestRender();
                Dispatcher.UIThread.RunJobs();
                Assert.True(f.Host.HasRendererError);
                Assert.Equal(2, Volatile.Read(ref creations));
                f.Host.RestartRenderer();
                return true;
            });
            Assert.Equal(0, (await Next(recovered.Frames)).SourceFrame);
            recovered.Release(0);
            await Next(f.Callbacks);
            Assert.False(await Ui(() => f.Host.HasRendererError));
            Assert.Equal(3, Volatile.Read(ref creations));
        }
        finally { await Cleanup(f, recovered); }
    }

    [Fact]
    public async Task SuccessfulPresentationRearmsRecoveryForALaterBackendFailure()
    {
        var first = new FakeRenderer { FailPresent = true };
        var second = new FakeRenderer();
        var third = new FakeRenderer();
        var backends = new[] { first, second, third };
        int creations = 0;
        var f = await Create(_ => backends[Interlocked.Increment(ref creations) - 1]);
        try
        {
            await Next(second.Frames);
            second.Release(0);
            await Next(f.Callbacks);
            await Ui(() => { f.Request(5); return true; });
            Assert.Equal(5, (await Next(second.Frames)).SourceFrame);
            second.Fail(1);
            Assert.Equal(5, (await Next(third.Frames)).SourceFrame);
            third.Release(0);
            await Next(f.Callbacks);
            Assert.False(f.Errors.Reader.TryRead(out _));
            Assert.Equal(3, Volatile.Read(ref creations));
        }
        finally { await Cleanup(f, backends); }
    }

    [Fact]
    public async Task ClearingMainLightDraftAfterCaptureFailureRestoresTheSampledLight()
    {
        var first = new FakeRenderer();
        var recovered = new FakeRenderer();
        var draft = new PmmLightPose(new(.5f), Vector3.UnitX);
        int creations = 0;
        var f = await Create(_ => Interlocked.Increment(ref creations) == 1 ? first : recovered,
            host => host.FrameDecorator = frame => frame.Light == draft
                ? throw new InvalidOperationException("bad light draft") : frame);
        try
        {
            var original = await Next(first.Frames);
            first.Release(0);
            await Next(f.Callbacks);
            await Ui(() => { f.Host.SetLightPose(draft); return true; });
            Assert.Equal("bad light draft", (await Next(f.Errors)).Message);
            Assert.True(await Ui(() => f.Host.HasRendererError));
            await Ui(() => { f.Host.SetLightPose(null); return true; });
            Assert.Equal(original.Light, (await Next(recovered.Frames)).Light);
            recovered.Release(0);
            await Next(f.Callbacks);
            Assert.False(await Ui(() => f.Host.HasRendererError));
            Assert.Equal(2, Volatile.Read(ref creations));
        }
        finally { await Cleanup(f, first, recovered); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UndoOfFailedLightingPresentsTheCorrectedFrame(bool undoWhilePresenting)
    {
        var first = new FakeRenderer();
        var repeatedFailure = new FakeRenderer { FailPresent = true };
        var recovered = new FakeRenderer();
        int creations = 0;
        var f = await Create(_ => Interlocked.Increment(ref creations) switch
        {
            1 => first,
            2 when !undoWhilePresenting => repeatedFailure,
            _ => recovered
        });
        try
        {
            await Next(first.Frames);
            first.Release(0);
            await Next(f.Callbacks);
            var lamp = new NativeSceneLight(Guid.NewGuid(), "fill", SceneLightKind.Point,
                Vector3.Zero, Quaternion.Identity, Vector3.One);
            await Ui(() =>
            {
                f.Project.Edit(edit => edit.Lighting.Add(lamp), Token);
                f.Host.RequestRender();
                return true;
            });
            Assert.Equal(lamp, Assert.Single((await Next(first.Frames)).Scene.Lighting!.Lights));
            async Task Undo() => Assert.True(await Ui(() =>
            {
                var changed = f.Project.Undo(Token);
                f.Host.RequestRender();
                return changed;
            }));
            if (undoWhilePresenting) await Undo();
            first.Fail(1);
            if (!undoWhilePresenting)
            {
                Assert.Equal("present failure", (await Next(f.Errors)).Message);
                await Ui(() =>
                {
                    f.Host.RequestRender();
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(f.Host.HasRendererError);
                    Assert.Equal(2, Volatile.Read(ref creations));
                    return true;
                });
                await Undo();
            }
            Assert.Empty((await Next(recovered.Frames)).Scene.Lighting!.Lights);
            recovered.Release(0);
            await Next(f.Callbacks);
            Assert.False(await Ui(() => f.Host.HasRendererError));
            Assert.False(f.Errors.Reader.TryRead(out _));
            Assert.Equal(undoWhilePresenting ? 2 : 3, Volatile.Read(ref creations));
        }
        finally { await Cleanup(f, first, repeatedFailure, recovered); }
    }

    private async Task<Fixture> Create(Func<ViewRenderRequest, IEditorViewportRenderer> factory,
        Action<EditorViewportHost>? configure = null) => await Ui(() =>
    {
        var camera = new SceneCamera(new(0, 1, -3), Vector3.Zero, Vector3.UnitY, 4, .1f, 100);
        var layout = new SceneCompositionEditor();
        var vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }
            .Select(position => new SceneVertex(position, Vector3.UnitZ, Vector2.Zero)).ToArray();
        var mesh = new SceneSnapshot(vertices, [0u, 1u, 2u],
            [new SceneMaterial("triangle", 0, 3, Vector4.One, Vector3.Zero, new(.3f), 1)], [], camera: camera);
        var outline = new OriginalOutlineData(vertices.Select(vertex =>
            new OriginalOutlineVertex(vertex.Position, vertex.Normal, OriginalOutlineVertexMode.Expand, 1, 0)).ToArray(),
            [new(1, Vector4.One)]);
        layout.Add("triangle", "triangle.pmx", ModelRole.Character, new OriginalFrameScene(new(mesh, [new(new(.5f))]),
            outline, new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, 10), 30, 1)));
        var project = new SceneProjectSession(layout, new(), [], new StarMcBodyViewProfileTests.NoPhysics(), cancellationToken: Token);
        var host = new EditorViewportHost(() => (100, 80), factory);
        var f = new Fixture(host, project, Token);
        host.SnapshotProvider = requests => { f.Captures++; return project.CaptureRenderSnapshot(requests); };
        host.FramePresented += value => f.Callbacks.Writer.TryWrite(value);
        host.Error += value => f.Errors.Writer.TryWrite(value);
        configure?.Invoke(host);
        host.Project = project; // Restarts and requests initialization on the real dispatcher.
        host.RequestRender();
        return f;
    });

    private async Task Cleanup(Fixture f, params FakeRenderer[] renderers)
    {
        foreach (var renderer in renderers) renderer.ReleaseAll();
        await Ui(() => { f.Host.Dispose(); f.Project.Dispose(); return true; });
    }

    private Task<T> Ui<T>(Func<T> action) => Headless.Dispatch(action, Token).WaitAsync(Timeout, Token);
    // Async Dispatch enters Avalonia's real DispatcherFrame until the operation completes.
    // PerAssembly above keeps the same dispatcher alive between the individual UI actions.
    private Task<T> Next<T>(Channel<T> channel) => Headless.Dispatch(
        async () => await channel.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token), Token)
        .WaitAsync(Timeout + TimeSpan.FromSeconds(1), Token);
    private Task<bool> Pump(Task task) => Headless.Dispatch(async () =>
    {
        await task.WaitAsync(Timeout, Token);
        return true;
    }, Token).WaitAsync(Timeout + TimeSpan.FromSeconds(1), Token);
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Fixture(EditorViewportHost host, SceneProjectSession project, CancellationToken token)
    {
        public EditorViewportHost Host { get; } = host;
        public SceneProjectSession Project { get; } = project;
        public int Captures;
        public Channel<EditorViewportPresentationStatistics> Callbacks { get; } = Channel.CreateUnbounded<EditorViewportPresentationStatistics>();
        public Channel<Exception> Errors { get; } = Channel.CreateUnbounded<Exception>();
        public void Request(int frame) { Project.Seek(frame, token); Host.RequestRender(); }
    }

    private sealed class FakeRenderer : IEditorViewportRenderer
    {
        private readonly TaskCompletionSource[] gates = [NewSignal(), NewSignal()];
        private readonly TaskCompletionSource[] permits = [NewSignal(), NewSignal()];
        private readonly TaskCompletionSource[] aheadGates = [NewSignal(), NewSignal()];
        private int permitWaits;
        private int aheadRecordings;
        private int presents, disposals;
        public bool FailPresent { get; init; }
        public bool FailPermit { get; init; }
        public bool GateFramePermits { get; init; }
        public bool SupportsRecordingAhead { get; init; }
        public Channel<int> PermitWaits { get; } = Channel.CreateUnbounded<int>();
        public long CounterBase { get; init; }
        public int Disposals => Volatile.Read(ref disposals);
        public Channel<PreviewFrame> Frames { get; } = Channel.CreateUnbounded<PreviewFrame>();
        public Channel<PreviewFrame> AheadFrames { get; } = Channel.CreateUnbounded<PreviewFrame>();
        public string AdapterName => "headless fake";
        public bool IsWarp => false;
        public ViewRendererCapabilities Capabilities { get; } = new("fake", 4096, 4096, false, true);
        public IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests => [];
        public EditorViewportPresentationStatistics Statistics => new(CounterBase + Volatile.Read(ref presents), 0, 0, 0, 100, 80, 2, null);
        public async ValueTask WaitForNextFrameAsync(CancellationToken cancellationToken = default)
        {
            if (FailPermit) throw new InvalidOperationException("frame permit failure");
            if (!GateFramePermits) return;
            int index = Interlocked.Increment(ref permitWaits) - 1;
            PermitWaits.Writer.TryWrite(index);
            if (index < permits.Length) await permits[index].Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        public async ValueTask PresentAsync(PreviewFrame frame, CancellationToken cancellationToken = default)
        {
            int index = Interlocked.Increment(ref presents) - 1;
            Frames.Writer.TryWrite(frame);
            if (FailPresent) throw new InvalidOperationException("present failure");
            if (index < gates.Length) await gates[index].Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        public async ValueTask PresentAheadAsync(PreviewFrame frame, CancellationToken cancellationToken)
        {
            int index = Interlocked.Increment(ref aheadRecordings) - 1;
            AheadFrames.Writer.TryWrite(frame);
            await aheadGates[index].Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await PresentAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask UpdateQualityAsync(ViewQualityOptions quality, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { Interlocked.Increment(ref disposals); return ValueTask.CompletedTask; }
        public void Fail(int index) => gates[index].TrySetException(new InvalidOperationException("present failure"));
        public void Release(int index) => gates[index].TrySetResult();
        public void ReleasePermit(int index) => permits[index].TrySetResult();
        public void ReleaseAhead(int index) => aheadGates[index].TrySetResult();
        public void ReleaseAll() { foreach (var gate in gates.Concat(permits).Concat(aheadGates)) gate.TrySetResult(); }
    }

}
