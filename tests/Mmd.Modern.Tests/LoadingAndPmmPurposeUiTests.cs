using System.Reflection;
using System.Numerics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.Physics;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class LoadingAndPmmPurposeUiTests : IAsyncDisposable
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);

    [Fact]
    public async Task ActualBusyFeedbackDrawsBeforeItsClockAndWhenInputOrMotionIsDisabled() => await session.Dispatch(() =>
    {
        var dots = new LoadingDots { IsActive = true, ActivationDelay = TimeSpan.FromSeconds(10) };
        var parent = new StackPanel { Children = { dots } };
        var window = new Window { Content = parent };
        window.Show(); dots.Arrange(new Rect(0, 0, 70, 12));
        try
        {
            LoadingClockTestDriver.AssertStopped(dots); // The clock is delayed, not the activity feedback.
            AssertDraws(dots);
            parent.IsEnabled = false;
            LoadingClockTestDriver.AssertStopped(dots); AssertDraws(dots);
            parent.IsEnabled = true; dots.AnimationsEnabled = false;
            LoadingClockTestDriver.AssertStopped(dots); AssertDraws(dots);
            dots.IsActive = false;
            Assert.Empty(Draw(dots).Children); LoadingClockTestDriver.AssertStopped(dots);
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task SustainedFormalOperationKeepsDotsVisibleAndAdvancesTheNativeAnimationClock() => await session.Dispatch(async () =>
    {
        var window = new EditorWindow(false);
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.IsVisible = false;
        var shell = new EditorShellViewModel(); window.DataContext = shell; window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
        var dots = window.FindControl<LoadingDots>("BusyLoadingDots")!;
        var activationChanges = new List<TimeSpan>();
        dots.PropertyChanged += (_, change) =>
        {
            if (change.Property == LoadingDots.ActivationDelayProperty) activationChanges.Add(dots.ActivationDelay);
        };
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The window wrapper requests the post-operation frame; shell-only work does not.
        var operation = (Task)typeof(EditorWindow).GetMethod("ExecuteAsync", Hidden)!
            .Invoke(window, ["Actual pending work", new Func<CancellationToken, Task>(_ => finish.Task), null])!;
        try
        {
            dots.Arrange(new Rect(0, 0, 70, 12));
            Assert.True(shell.IsBusy); Assert.True(dots.IsActive);
            Assert.Equal("Actual pending work", shell.LoadingStatus); AssertDraws(dots);
            var diagnostics = new List<string> { ClockDiagnostic(dots, "initial-busy") };
            // The test platform still uses Avalonia's real render timer and animation clock.
            // It does not replace progress with a test-driven property assignment.
            var samples = new List<double>();
            for (int index = 0; index < 10; index++)
            {
                // RunJobs executes queued work but explicitly ignores pending platform
                // events. Enter the platform loop so DispatcherTimer's OS due-time event
                // can promote the 120ms activation callback, as it does in a real window.
                LoadingClockTestDriver.Pump(100);
                Assert.True(shell.IsBusy); Assert.True(dots.IsActive); AssertDraws(dots);
                Assert.Equal(TimeSpan.FromMilliseconds(120), dots.ActivationDelay);
                samples.Add(dots.GetValue(LoadingClockTestDriver.Progress));
                diagnostics.Add(ClockDiagnostic(dots, "sample-" + index));
            }
            string clockEvidence = string.Join(Environment.NewLine, diagnostics);
            LoadingClockTestDriver.AssertRunning(dots, "Native loading clock did not start:" + Environment.NewLine + clockEvidence);
            Assert.True(samples.Distinct().Count() > 2, "A real pending operation must advance the Avalonia animation clock. "
                + string.Join(",", samples) + Environment.NewLine + clockEvidence);
            Assert.Empty(activationChanges); // Starting motion must not withdraw the Window's delay setter.
            finish.SetResult(); await operation;
            Assert.False(shell.IsBusy);
            Assert.True(viewport.IsPreparingFrame); Assert.True(shell.IsLoading);
            LoadingClockTestDriver.AssertRunning(dots);
            // This headless fixture omits native presentation. Complete both production
            // callbacks in order: preparation alone must not clear the window's frame wait.
            typeof(EditorViewportHost).GetMethod("SetPreparingFrame", Hidden)!.Invoke(viewport, [false]);
            Assert.True(shell.IsLoading);
            typeof(EditorWindow).GetMethod("LoadingFramePresented", Hidden)!.Invoke(window,
                [new EditorViewportPresentationStatistics(1, 0, 0, 0, 1, 1, 1, null)]);
            Dispatcher.UIThread.RunJobs();
            Assert.False(shell.IsLoading);
            LoadingClockTestDriver.AssertStopped(dots);
            return true;
        }
        finally
        {
            finish.TrySetResult(); await operation;
            typeof(EditorWindow).GetField("_closingConfirmed", Hidden)!.SetValue(window, true);
            window.Close(); Dispatcher.UIThread.RunJobs();
        }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ChangingCycleOrInheritedDelayRestartsOnlyTheClockWithoutInvalidatingParentSetters() => await session.Dispatch(() =>
    {
        var dots = new LoadingDots { IsActive = true };
        var inheritedDelay = new Setter(LoadingDots.ActivationDelayProperty, TimeSpan.FromMilliseconds(30));
        var window = new Window { Content = dots };
        window.Styles.Add(new Style(selector => selector.OfType<LoadingDots>()) { Setters = { inheritedDelay } });
        window.Show();
        var activationChanges = new List<TimeSpan>();
        dots.PropertyChanged += (_, change) =>
        {
            if (change.Property == LoadingDots.ActivationDelayProperty) activationChanges.Add(dots.ActivationDelay);
        };
        try
        {
            LoadingClockTestDriver.Pump(150); LoadingClockTestDriver.AssertRunning(dots);
            dots.CycleDuration = TimeSpan.FromMilliseconds(600);
            LoadingClockTestDriver.Pump(90); LoadingClockTestDriver.AssertRunning(dots);
            double first = dots.GetValue(LoadingClockTestDriver.Progress);
            LoadingClockTestDriver.Pump(90);
            double second = dots.GetValue(LoadingClockTestDriver.Progress);
            Assert.InRange(second - first, 0.04, 0.4);
            Assert.Empty(activationChanges);
            Assert.Equal(TimeSpan.FromMilliseconds(30), dots.ActivationDelay);

            // Change the effective delay as a real property update, then remove the
            // local value to restore the parent's setter. Both must start a new wait.
            dots.ActivationDelay = TimeSpan.FromMilliseconds(300);
            Assert.False(dots.IsAnimating(LoadingClockTestDriver.Progress));
            LoadingClockTestDriver.Pump(90);
            Assert.False(dots.IsAnimating(LoadingClockTestDriver.Progress));
            AssertDraws(dots);
            LoadingClockTestDriver.Pump(320); LoadingClockTestDriver.AssertRunning(dots);
            dots.ClearValue(LoadingDots.ActivationDelayProperty);
            Assert.Equal(TimeSpan.FromMilliseconds(30), dots.ActivationDelay);
            Assert.False(dots.IsAnimating(LoadingClockTestDriver.Progress));
            LoadingClockTestDriver.Pump(120); LoadingClockTestDriver.AssertRunning(dots);
            Assert.Equal(new[] { TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(30) }, activationChanges);
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task PmmPurposeChoicesCarryExplicitIndoorAndLegacyModesAcrossLocalizationAndConfirmation() => await session.Dispatch(() =>
    {
        using var playback = ResolvedOutputCameraFixture.Playback();
        var original = playback.Plan;
        var dialog = new EditorPmmImportWindow(original); dialog.Show();
        var previousLanguage = EditorLocalization.CurrentLanguage;
        try
        {
            var choices = dialog.FindControl<ComboBox>("PmmBatchRoleBox")!;
            Assert.Equal(5, choices.ItemCount);
            var role = Assert.Single(dialog.FindControl<StackPanel>("PmmModelRows")!.GetVisualDescendants().OfType<ComboBox>(),
                item => item.Name == "PmmRole_0");
            Assert.Equal(-1, role.SelectedIndex); Assert.True(original.HasUnconfirmedRoles);
            choices.SelectedIndex = 1;
            typeof(EditorPmmImportWindow).GetMethod("ApplyBatchRole", Hidden)!.Invoke(dialog, [null, new RoutedEventArgs()]);
            Assert.Equal(new(ModelRole.Environment, true), CurrentPurposes(dialog)[0]);
            EditorLocalization.SetLanguage("en");
            Assert.Equal(1, role.SelectedIndex);
            Assert.Equal("Indoor Stage", role.SelectedItem);
            Assert.Equal(new(ModelRole.Environment, true), CurrentPurposes(dialog)[0]);
            role.SelectedIndex = 2;
            Assert.Equal(new(ModelRole.Environment, false), CurrentPurposes(dialog)[0]);
            role.SelectedIndex = 4;
            Assert.Equal(new(ModelRole.Unspecified), CurrentPurposes(dialog)[0]);
            role.SelectedIndex = 1;
            Assert.True(dialog.FindControl<Button>("PmmConfirmImportButton")!.IsEnabled);
            typeof(EditorPmmImportWindow).GetMethod("ConfirmImport", Hidden)!.Invoke(dialog, [null, new RoutedEventArgs()]);
            Assert.Equal(new(ModelRole.Environment, true), dialog.ImportOptions.ModelPurposes[0]);
            Assert.Empty(original.ModelPurposes);
            Assert.Same(original.Document, dialog.Plan.Document);
            Assert.Same(original.Models[0].Motion, dialog.Plan.Models[0].Motion);
            return true;
        }
        finally { EditorLocalization.SetLanguage(previousLanguage); dialog.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(null, 2)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task OpeningExistingPmmPurposePreservesItsExactNullableCompatibilityChoice(bool? indoor, int choice) => await session.Dispatch(() =>
    {
        using var playback = ResolvedOutputCameraFixture.Playback();
        var purpose = new ModelPurpose(ModelRole.Environment, indoor);
        var plan = playback.Plan.ConfirmRoles(new Dictionary<int, ModelPurpose> { [0] = purpose });
        var dialog = new EditorPmmImportWindow(plan); dialog.Show();
        try
        {
            var role = Assert.Single(dialog.FindControl<StackPanel>("PmmModelRows")!.GetVisualDescendants().OfType<ComboBox>(),
                item => item.Name == "PmmRole_0");
            Assert.Equal(choice, role.SelectedIndex);
            Assert.Equal(purpose, CurrentPurposes(dialog)[0]);
            // Explicitly applying the same visible legacy option resolves an old nullable
            // compatibility tag to false, even though the combo index itself does not change.
            dialog.FindControl<ComboBox>("PmmBatchRoleBox")!.SelectedIndex = 2;
            typeof(EditorPmmImportWindow).GetMethod("ApplyBatchRole", Hidden)!.Invoke(dialog, [null, new RoutedEventArgs()]);
            Assert.Equal(new(ModelRole.Environment, false), CurrentPurposes(dialog)[0]);
            role.SelectedIndex = 1; role.SelectedIndex = 2;
            Assert.Equal(new(ModelRole.Environment, false), CurrentPurposes(dialog)[0]);
            return true;
        }
        finally { dialog.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitIndoorPmmUsesOneSharedStarterLightAndPreservesAuthoredCameraMotionAndGravity(bool indoor)
    {
        // The starter light's display name comes from the embedded Avalonia catalog.
        await this.session.Dispatch(() => EditorLocalization.Text("Mda.Text.Context.Room.BasicLightName"), TestContext.Current.CancellationToken);
        using var playback = ResolvedOutputCameraFixture.Playback();
        var original = playback.Plan;
        var source = original.Models[0].Source;
        var instances = Enumerable.Range(0, 6).Select(index => new PmmModel
        {
            Index = (byte)index, Name = source.Name, Path = source.Path, Visible = true,
            DrawOrderIndex = (byte)index, SelectedMorphIndices = source.SelectedMorphIndices,
            BoneNames = source.BoneNames, InitialBoneKeyframes = source.InitialBoneKeyframes,
            BoneKeyframes = source.BoneKeyframes, BoneStates = source.BoneStates,
            InitialModelKeyframe = source.InitialModelKeyframe, SelfShadowEnabled = source.SelfShadowEnabled
        }).ToArray();
        var document = new PmmDocument
        {
            AnsiCodePage = original.Document.AnsiCodePage, Models = instances,
            OutputWidth = original.OutputWidth, OutputHeight = original.OutputHeight,
            CameraFov = original.Document.CameraFov, Camera = original.Document.Camera, Light = original.Document.Light,
            PanelStates = original.Document.PanelStates,
            Settings = original.Document.Settings, Gravity = original.Document.Gravity, SelfShadow = original.Document.SelfShadow,
            CurrentFrame = original.CurrentFrame
        };
        var prepared = instances.Select(instance => original.Models[0] with { Source = instance }).ToList();
        var plan = (PmmProjectImportPlan)Activator.CreateInstance(typeof(PmmProjectImportPlan), Hidden, null,
            [document, "indoor-instances.pmm", prepared, new List<PmmUnresolvedAsset>(), new List<string>()], null)!;
        plan = plan.ConfirmRoles(instances.ToDictionary(instance => (int)instance.Index,
            _ => new ModelPurpose(ModelRole.Environment, indoor)));
        using var candidate = EditorProjectInterchange.CreatePmmCandidate(plan,
            (model, token) => EditorProjectWorkspace.CreateModelAsset(model.Model, model.Path, token), new NoPhysics(),
            token: TestContext.Current.CancellationToken);
        var session = candidate.Session;
        Assert.Equal(indoor ? 1 : 0, session.Lighting.Lights.Count);
        Assert.All(session.Objects, item => Assert.Equal(new(ModelRole.Environment, indoor), session.GetModelPurpose(item.Id)));
        Assert.Equal(new PmmSceneTracks(document).SavedCamera, candidate.SavedCamera);
        Assert.Equal(new PmmSceneTracks(document).SavedLight, candidate.SavedLight);
        Assert.Equal(plan.EffectivePhysics.Gravity, session.Settings.Physics.Gravity);
        Assert.Same(document, plan.Document);
        Assert.All(plan.Models, model => Assert.Same(original.Models[0].Motion, model.Motion));
        var authored = session.CaptureAuthoredContent();
        Assert.Equal(6, authored.Motions.Count);
        Assert.Equal(original.Models[0].Motion.BoneKeyframes.Count, authored.Motions[0].Motion.BoneKeyframes.Count);
        Assert.Equal(document.Camera.InitialKeyframe.ParentModelIndex, session.CameraPose!.Value.ParentModelIndex);
        if (indoor)
        {
            var light = Assert.Single(session.Lighting.Lights);
            Assert.True(light.Enabled); Assert.Equal(SceneLightKind.Point, light.Kind);
            session.Edit(edit => edit.Lighting.Remove(light.Id), TestContext.Current.CancellationToken);
            Assert.Empty(session.Lighting.Lights);
            Assert.True(session.Undo(TestContext.Current.CancellationToken));
            Assert.Equal(light, Assert.Single(session.Lighting.Lights));
        }
        Assert.Empty(original.ModelPurposes);
    }

    private static Dictionary<int, ModelPurpose> CurrentPurposes(EditorPmmImportWindow dialog) =>
        (Dictionary<int, ModelPurpose>)typeof(EditorPmmImportWindow).GetMethod("CurrentPurposes", Hidden)!.Invoke(dialog, null)!;

    private static DrawingGroup Draw(LoadingDots dots)
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open()) dots.Render(context);
        return drawing;
    }
    private static void AssertDraws(LoadingDots dots) => Assert.NotEmpty(Draw(dots).Children);

    private static string ClockDiagnostic(LoadingDots dots, string phase)
    {
        object? Field(string name) => typeof(LoadingDots).GetField(name, Hidden)!.GetValue(dots);
        var dispatcher = Dispatcher.UIThread;
        var implementation = typeof(Dispatcher).GetField("_impl", Hidden)!.GetValue(dispatcher)!;
        var timers = ((IEnumerable<DispatcherTimer>)typeof(Dispatcher).GetMethod("SnapshotTimersForUnitTests",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!).Select(timer => new
            {
                timer.Interval, timer.IsEnabled, tag = timer.Tag?.ToString(),
                dueTime = typeof(DispatcherTimer).GetProperty("DueTimeInMs", Hidden)!.GetValue(timer),
                sameDispatcher = ReferenceEquals(timer.Dispatcher, dispatcher)
            }).ToArray();
        var progress = (StyledProperty<double>)typeof(LoadingDots).GetField("AnimationProgressProperty",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        object Describe(Visual value) => new
        {
            type = value.GetType().Name, name = (value as StyledElement)?.Name, value.IsVisible,
            enabled = (value as Control)?.IsEnabled, effectiveEnabled = (value as Control)?.IsEffectivelyEnabled,
            windowState = (value as Window)?.WindowState.ToString()
        };
        return JsonSerializer.Serialize(new
        {
            phase, dots.IsActive, dots.IsVisible, dots.IsEnabled, dots.IsEffectivelyEnabled, dots.AnimationsEnabled,
            dots.ActivationDelay, attached = Field("_attached"), activationReady = Field("_activationReady"),
            activationTimer = Field("_activationTimer") is not null, surfaceMotionAllowed = Field("_surfaceMotionAllowed"),
            hasAnimationStyle = Field("_animationStyle") is not null, styles = dots.Styles.Count,
            animationRunning = Field("_animationRunning"), isAnimating = dots.IsAnimating(progress),
            progress = dots.GetValue(progress), bounds = dots.Bounds.ToString(), classes = dots.Classes.ToArray(),
            dispatcher = new { implementation = implementation.GetType().FullName, dispatcher.SupportsRunLoops,
                now = implementation.GetType().GetProperty("Now")!.GetValue(implementation), timers },
            surface = TopLevel.GetTopLevel(dots) is { } surface ? Describe(surface) : null,
            observedAncestors = ((IEnumerable<Visual>)Field("_ancestors")!).Select(Describe).ToArray(),
            actualAncestors = dots.GetVisualAncestors().Select(Describe).ToArray()
        });
    }

    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item)
            => throw new InvalidOperationException("The imported test instances contain no physics bodies.");
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings)
            => throw new InvalidOperationException("The imported test instances contain no physics bodies.");
    }

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
