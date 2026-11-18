using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Threading.Channels;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorPlaybackPacingTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession dispatcher = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string[] PanelProperties = [nameof(EditorAnimationViewModel.PositionX), nameof(EditorAnimationViewModel.PositionY),
        nameof(EditorAnimationViewModel.PositionZ), nameof(EditorAnimationViewModel.RotationX), nameof(EditorAnimationViewModel.RotationY),
        nameof(EditorAnimationViewModel.RotationZ), nameof(EditorAnimationViewModel.CurrentFrame), nameof(EditorAnimationViewModel.FrameSummary)];

    [Fact]
    public async Task PlaybackAdvancesByElapsedTimeNotByPulseOrTickCount() => await dispatcher.Dispatch(() =>
    {
        using var shell = Shell(out _);
        long now = 1_000_000, second = Stopwatch.Frequency;
        shell.PlaybackClock = () => now;
        var session = shell.Workspace.Session!;
        Play(shell);
        now += second / 100;
        for (int index = 0; index < 5; index++) shell.AdvancePlaybackForFrame();
        Assert.Equal(.3, session.CurrentFrame, 9);
        now += second;
        shell.AdvancePlaybackForFrame();
        Assert.Equal(30.3, session.CurrentFrame, 9);
        Assert.Equal(30, shell.CurrentFrame);
        // A timer tick after a frame permit already advanced is not a second clock; the following
        // uncovered interval advances by all elapsed time since the permit.
        now += second / 10; Tick(shell);
        Assert.Equal(30.3, session.CurrentFrame, 9);
        now += second / 10; Tick(shell);
        Assert.Equal(36.3, session.CurrentFrame, 9);
        Assert.Equal(36, shell.CurrentFrame);
        shell.StopPlayback();
        now += second; shell.AdvancePlaybackForFrame(); Tick(shell);
        Assert.Equal(36.3, session.CurrentFrame, 9);

        // Same real time through many small permits reaches the same source time.
        using var fine = Shell(out _);
        long fineNow = 0; fine.PlaybackClock = () => fineNow;
        Play(fine);
        for (int index = 0; index < 100; index++) { fineNow += second / 100; fine.AdvancePlaybackForFrame(); }
        Assert.Equal(30, fine.Workspace.Session!.CurrentFrame, 9);
        return true;
    }, Token);

    [Fact]
    public async Task ThrottledPanelsShowExactlyTheSeekedValuesWhenPaused() => await dispatcher.Dispatch(() =>
    {
        using var shell = Shell(out var motion);
        long now = 0, second = Stopwatch.Frequency;
        shell.PlaybackClock = () => now;
        var shown = new Dictionary<string, object?>();
        int positionNotifications = 0, commandNotifications = 0;
        void Bind(object? sender, PropertyChangedEventArgs args)
        {
            foreach (var name in PanelProperties.Where(name => args.PropertyName is null || args.PropertyName == name))
                shown[name] = typeof(EditorAnimationViewModel).GetProperty(name)!.GetValue(shell.Animation);
            if (args.PropertyName == nameof(EditorAnimationViewModel.PositionX)) positionNotifications++;
        }
        void CommandChanged(object? sender, EventArgs args) => commandNotifications++;
        shell.Animation.PropertyChanged += Bind;
        shell.Animation.RegisterKeyframeCommand.CanExecuteChanged += CommandChanged;
        Play(shell);
        int frameChanges = 0, frame = shell.CurrentFrame;
        for (int index = 0; index < 125; index++)
        {
            now += second / 60; shell.AdvancePlaybackForFrame();
            if (shell.CurrentFrame != frame) { frameChanges++; frame = shell.CurrentFrame; }
            Assert.Equal(shell.CurrentFrame, shell.Animation.CurrentFrame); // The model itself is never stale.
        }
        Assert.InRange(positionNotifications, 1, frameChanges / 2);
        Assert.Equal(positionNotifications, commandNotifications);
        shell.StopPlayback();
        Assert.NotEqual(0, shell.Workspace.Session!.CurrentFrame % 1);
        foreach (var name in PanelProperties)
            Assert.Equal(typeof(EditorAnimationViewModel).GetProperty(name)!.GetValue(shell.Animation), shown[name]);

        using var seeked = Shell(out _, motion);
        seeked.CurrentFrame = shell.CurrentFrame;
        foreach (var name in PanelProperties)
            Assert.Equal(typeof(EditorAnimationViewModel).GetProperty(name)!.GetValue(seeked.Animation), shown[name]);
        Assert.Equal(seeked.SceneInspector.CurrentFrame, shell.SceneInspector.CurrentFrame);
        shell.Animation.PropertyChanged -= Bind;
        shell.Animation.RegisterKeyframeCommand.CanExecuteChanged -= CommandChanged;
        return true;
    }, Token);

    [Fact]
    public async Task ExactKeyIndexFollowsCommittedMotionEdits() => await dispatcher.Dispatch(() =>
    {
        using var shell = Shell(out var motion);
        var id = shell.Animation.ObjectId!.Value;
        shell.CurrentFrame = 10;
        Assert.Equal(Quaternion.CreateFromYawPitchRoll(.2f, 0, 0), shell.Animation.FindExactBoneKey("root", 10)!.Rotation);
        Assert.Equal(Quaternion.CreateFromYawPitchRoll(.5f, 0, 0), shell.Animation.FindExactBoneKey("root", 20)!.Rotation);
        Assert.Null(shell.Animation.FindExactBoneKey("root", 11));
        var edited = new VmdMotion { ModelName = motion.ModelName, BoneKeyframes = motion.BoneKeyframes
            .Select(key => key.Frame != 10 ? key : new VmdBoneKeyframe { BoneName = "root", Frame = 10, Rotation = Quaternion.CreateFromYawPitchRoll(-.7f, .1f, 0) }).ToArray() };
        shell.Workspace.CommitMotion(id, edited);
        Assert.Equal(Quaternion.CreateFromYawPitchRoll(-.7f, .1f, 0), shell.Animation.FindExactBoneKey("root", 10)!.Rotation);
        using var fresh = Shell(out _, edited);
        fresh.CurrentFrame = 10;
        foreach (var name in PanelProperties)
            Assert.Equal(typeof(EditorAnimationViewModel).GetProperty(name)!.GetValue(fresh.Animation),
                typeof(EditorAnimationViewModel).GetProperty(name)!.GetValue(shell.Animation));
        Assert.True(shell.Workspace.Undo());
        Assert.Equal(Quaternion.CreateFromYawPitchRoll(.2f, 0, 0), shell.Animation.FindExactBoneKey("root", 10)!.Rotation);
        return true;
    }, Token);

    [Fact]
    public async Task ViewportFramePermitsPacePlaybackWithoutUiTimerAndNonPacingRenderersDoNot() => await dispatcher.Dispatch(async () =>
    {
        using var shell = Shell(out _);
        long now = 1_000_000, second = Stopwatch.Frequency;
        shell.PlaybackClock = () => now;
        var renderer = new PacingFakeRenderer { PacesPresentation = true };
        using var host = new EditorViewportHost(() => (64, 64), _ => renderer);
        var presented = Channel.CreateUnbounded<double>();
        host.FramePresented += stats => presented.Writer.TryWrite(renderer.LastSourceFrame);
        host.Project = shell.Workspace.Session;
        host.SnapshotProvider = requests => shell.Workspace.Session!.CaptureRenderSnapshot(requests);
        host.PlaybackAdvance = shell.AdvancePlaybackForFrame;
        shell.ViewStateChanged += (_, _) => host.RequestRender();
        shell.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(EditorShellViewModel.IsPlaying)) host.SetPlaybackActive(shell.IsPlaying);
        };

        // Initial paused frame (permit 0 -> present 0).
        host.RequestRender();
        Assert.Equal(0, await renderer.PermitWaits.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        renderer.ReleasePermit(0);
        Assert.Equal(0d, await presented.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token), 9);

        // Start playback without UI timer: SetPlaybackActive(true) immediately requests permit 1.
        Play(shell);
        Assert.Equal(1, await renderer.PermitWaits.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Equal(0d, shell.Workspace.Session!.CurrentFrame, 9);

        long step1 = second / 60;
        now += step1;
        renderer.ReleasePermit(1);
        double expected1 = (step1 / (double)second) * 30d;
        Assert.Equal(expected1, await presented.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token), 9);

        // FramePresented automatically schedules permit 2 -> advance -> capture -> present.
        Assert.Equal(2, await renderer.PermitWaits.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        long step2 = second / 30;
        now += step2;
        renderer.ReleasePermit(2);
        double expected2 = expected1 + (step2 / (double)second) * 30d;
        Assert.Equal(expected2, await presented.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token), 9);

        // End of timeline stops playback after presenting the final frame.
        Assert.Equal(3, await renderer.PermitWaits.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        now += 15 * second;
        renderer.ReleasePermit(3);
        Assert.Equal(300d, await presented.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token), 9);
        Assert.False(shell.IsPlaying);
        await Task.Delay(50, Token);
        Assert.False(renderer.PermitWaits.Reader.TryRead(out _));

        // A non-pacing renderer never calls PlaybackAdvance.
        using var unpacedShell = Shell(out _);
        long unpacedNow = 0; unpacedShell.PlaybackClock = () => unpacedNow;
        var unpacedRenderer = new PacingFakeRenderer { PacesPresentation = false };
        using var unpacedHost = new EditorViewportHost(() => (64, 64), _ => unpacedRenderer);
        var unpacedPresented = Channel.CreateUnbounded<double>();
        unpacedHost.FramePresented += _ => unpacedPresented.Writer.TryWrite(unpacedRenderer.LastSourceFrame);
        unpacedHost.Project = unpacedShell.Workspace.Session;
        unpacedHost.SnapshotProvider = requests => unpacedShell.Workspace.Session!.CaptureRenderSnapshot(requests);
        unpacedHost.PlaybackAdvance = unpacedShell.AdvancePlaybackForFrame;
        unpacedHost.RequestRender();
        Assert.Equal(0, await unpacedRenderer.PermitWaits.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        unpacedRenderer.ReleasePermit(0);
        Assert.Equal(0d, await unpacedPresented.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token), 9);
        Play(unpacedShell);
        unpacedHost.SetPlaybackActive(true);
        unpacedNow += second;
        unpacedHost.RequestRender();
        Assert.Equal(1, await unpacedRenderer.PermitWaits.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        unpacedRenderer.ReleasePermit(1);
        Assert.Equal(0d, await unpacedPresented.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token), 9);
        Assert.Equal(0d, unpacedShell.Workspace.Session!.CurrentFrame, 9);
        return true;
    }, Token);

    [Fact]
    public async Task LightingParentContextSkipsRebuildWhenContentRevisionIsUnchanged() => await dispatcher.Dispatch(() =>
    {
        var window = new EditorWindow(false);
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.IsSuspended = true; viewport.IsVisible = false;
        using var shell = Shell(out _);
        var light = new NativeSceneLight(Guid.NewGuid(), "key", SceneLightKind.Point, new(1, 2, 3), Quaternion.Identity, Vector3.One);
        shell.Workspace.EditLighting(new(new([light]), light.Id));
        window.DataContext = shell;
        window.Show();
        try
        {
            shell.ShowInspectorPage(EditorInspectorPage.Lighting);
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            var initialParents = inspector.ParentModels;
            Assert.Single(initialParents);

            shell.CurrentFrame = 10;
            shell.CurrentFrame = 20;
            Assert.Same(initialParents, inspector.ParentModels);

            var second = MorphApplicationTests.Model([]);
            string secondPath = Path.Combine(Path.GetTempPath(), "mda-lighting-cache-second.pmx");
            shell.Workspace.AddModel(secondPath, EditorProjectWorkspace.CreateModelAsset(second, secondPath, Token), ModelRole.Prop);
            Assert.NotSame(initialParents, inspector.ParentModels);
            Assert.Equal(2, inspector.ParentModels.Count);
            return true;
        }
        finally
        {
            typeof(EditorWindow).GetField("_closingConfirmed", Instance)!.SetValue(window, true);
            window.Close(); Dispatcher.UIThread.RunJobs();
        }
    }, Token);

    private sealed class PacingFakeRenderer : IEditorViewportRenderer
    {
        private readonly List<TaskCompletionSource> permits = [];
        private readonly object gate = new();
        private long presents;
        public bool PacesPresentation { get; init; }
        public double LastSourceFrame { get; private set; }
        public Channel<int> PermitWaits { get; } = Channel.CreateUnbounded<int>();
        public string AdapterName => "fake-pacing";
        public bool IsWarp => false;
        public ViewRendererCapabilities Capabilities { get; } = new("fake", 4096, 4096, false, true);
        public IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests => [];
        public EditorViewportPresentationStatistics Statistics => new(Volatile.Read(ref presents), 0, 0, 0, 64, 64, 2, null);
        public ValueTask WaitForNextFrameAsync(CancellationToken cancellationToken = default)
        {
            TaskCompletionSource signal;
            int index;
            lock (gate)
            {
                index = permits.Count;
                signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                permits.Add(signal);
            }
            PermitWaits.Writer.TryWrite(index);
            return new(signal.Task.WaitAsync(cancellationToken));
        }
        public void ReleasePermit(int index)
        {
            lock (gate) permits[index].TrySetResult();
        }
        public ValueTask PresentAsync(PreviewFrame frame, CancellationToken cancellationToken = default)
        {
            LastSourceFrame = frame.SourceFrame;
            Interlocked.Increment(ref presents);
            return ValueTask.CompletedTask;
        }
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask UpdateQualityAsync(ViewQualityOptions quality, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync()
        {
            lock (gate) foreach (var signal in permits) signal.TrySetCanceled();
            return ValueTask.CompletedTask;
        }
    }

    private static EditorShellViewModel Shell(out VmdMotion motion, VmdMotion? source = null)
    {
        var shell = new EditorShellViewModel();
        var model = MorphApplicationTests.Model([]);
        string path = Path.Combine(Path.GetTempPath(), "mda-playback-pacing-fixture.pmx");
        var id = shell.Workspace.AddModel(path, EditorProjectWorkspace.CreateModelAsset(model, path, Token), ModelRole.Prop);
        motion = source ?? new VmdMotion
        {
            ModelName = model.Name,
            BoneKeyframes = [new() { BoneName = "root" },
                new() { BoneName = "root", Frame = 10, Rotation = Quaternion.CreateFromYawPitchRoll(.2f, 0, 0) },
                new() { BoneName = "root", Frame = 20, Rotation = Quaternion.CreateFromYawPitchRoll(.4f, 0, 0) },
                // Duplicate records: the later one is the exact key, as in file order.
                new() { BoneName = "root", Frame = 20, Rotation = Quaternion.CreateFromYawPitchRoll(.5f, 0, 0) },
                new() { BoneName = "root", Frame = 300, Translation = new(30, 1, -2), Rotation = Quaternion.CreateFromYawPitchRoll(1, .3f, .2f) }]
        };
        shell.Workspace.CommitMotion(id, motion);
        shell.Browser.SelectedModel = shell.Browser.Models.Single();
        shell.Browser.SelectedBoneIndex = 0;
        Assert.True(shell.Animation.IsBoneSelected);
        return shell;
    }

    private static void Play(EditorShellViewModel shell)
    {
        shell.PlayCommand.Execute(null);
        ((DispatcherTimer)typeof(EditorShellViewModel).GetField("playbackTimer", Instance)!.GetValue(shell)!).Stop();
        Assert.True(shell.IsPlaying);
    }

    private static void Tick(EditorShellViewModel shell)
        => typeof(EditorShellViewModel).GetMethod("PlaybackTick", Instance)!.Invoke(shell, [null, EventArgs.Empty]);

    public async ValueTask DisposeAsync() => await Task.Run(dispatcher.Dispose).ConfigureAwait(false);
}
