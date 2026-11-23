using System.Numerics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorLightParameterTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);

    [Fact]
    public void BeamControlsMapFullAngleAndSoftnessWithoutChangingUntouchedFloats()
    {
        var light = Light() with { Intensity = 1000, Range = 10000, InnerConeRadians = .001234f, OuterConeRadians = .004567f };
        var item = new EditorNativeLightItem(light);
        Assert.Equal(light, item.Build());
        item.BeamWidthDegrees = 80;
        Assert.InRange(item.Build().OuterConeRadians, .69813f, .69814f);
        Assert.InRange((float)item.EdgeSoftnessPercent!.Value, 72.97f, 72.99f);
        item.EdgeSoftnessPercent = 25;
        var changed = item.Build();
        Assert.InRange(changed.InnerConeRadians, .52359f, .52360f);
        Assert.Equal(light.Intensity, changed.Intensity); Assert.Equal(light.Range, changed.Range);
    }

    [Fact]
    public void DefaultCreatedLightsHaveUsableSliderPositionsAndBrightnessCanReachZero()
    {
        foreach (var kind in Enum.GetValues<SceneLightKind>())
        {
            var light = EditorLightPlacement.Create(Guid.NewGuid(), "created", kind, EditorLightPlacement.DefaultCamera);
            var item = new EditorNativeLightItem(light);
            Assert.InRange(item.BrightnessSliderPosition, 1, 7);
            Assert.InRange(item.DistanceSliderPosition, -1, 4);
            Assert.InRange(Math.Abs((float)EditorLightParameterScale.BrightnessValue(item.BrightnessSliderPosition) - light.Intensity), 0, .001f);
            Assert.InRange(Math.Abs((float)EditorLightParameterScale.DistanceValue(item.DistanceSliderPosition) - light.Range), 0, .001f);
            Assert.Equal(light, item.Build());
        }
        Assert.Equal(0m, EditorLightParameterScale.BrightnessValue(0));
    }

    [Fact]
    public void HdrBrightnessKeepsSliderHeadroomWithoutMovingTheTrackDuringADrag()
    {
        var item = new EditorNativeLightItem(Light() with { Intensity = 90 });
        Assert.True(item.BrightnessSliderMaximum > EditorLightParameterScale.BrightnessPosition(100));
        double end = item.BrightnessSliderMaximum;
        item.Intensity = EditorLightParameterScale.BrightnessValue(end);
        Assert.Equal(end, item.BrightnessSliderMaximum);
        item.EnsureBrightnessSliderRange();
        Assert.True(item.BrightnessSliderMaximum > end);
        item.Intensity = decimal.MaxValue;
        Assert.Equal(decimal.MaxValue, EditorLightParameterScale.BrightnessValue(item.BrightnessSliderMaximum));
    }

    [Theory]
    [InlineData(.00001f)]
    [InlineData(.001f)]
    [InlineData(.7f)]
    [InlineData(1.5707963f)]
    public void ZeroSoftnessAuthorsAValidNearHardEdgeWithoutRewritingHistoricalCones(float outer)
    {
        var light = Light() with { OuterConeRadians = outer, InnerConeRadians = MathF.BitDecrement(outer) };
        var item = new EditorNativeLightItem(light);
        Assert.Equal(light, item.Build());
        item.EdgeSoftnessPercent = 0;
        var hard = item.Build();
        Assert.True(hard.InnerConeRadians < hard.OuterConeRadians);
        Assert.Equal(light.OuterConeRadians, hard.OuterConeRadians);
        Assert.InRange((float)item.EdgeSoftnessPercent!.Value, 0, .00011f);
        Assert.Equal("0", item.EdgeSoftnessPercent.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task SliderTicksPreviewAndReleaseMakesOneUndoWhileEscapeMakesNone() => await session.Dispatch(() =>
    {
        var (window, shell, light) = Editor();
        try
        {
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            var slider = inspector.GetVisualDescendants().OfType<EditorLightParameterSlider>().Single(control => control.Name == "LightBrightnessSlider");
            var controller = (EditorLightViewportController)typeof(EditorWindow).GetField("lightController", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var original = shell.Workspace.Session!.Lighting;
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            slider.Value = EditorLightParameterScale.BrightnessPosition(3); slider.Value = EditorLightParameterScale.BrightnessPosition(4); slider.Value = EditorLightParameterScale.BrightnessPosition(5);
            Assert.Same(original, shell.Workspace.Session.Lighting);
            Assert.Equal(5f, Assert.Single(controller.PreviewLighting.Lights).Intensity);
            var snapshot = shell.Workspace.Session.CaptureRenderSnapshot();
            var frame = new PreviewFrame(800, 600, 0, snapshot.Scene, snapshot.Camera, snapshot.Light, snapshot.SelfShadow);
            Assert.Equal(5f, Assert.Single(controller.DecorateFrame(frame).Scene.Lighting!.Lights).Intensity);
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Right });
            Assert.Equal(5f, Assert.Single(shell.Workspace.Session.Lighting.Lights).Intensity);
            Assert.True(shell.Workspace.Undo());
            Assert.Equal(light, Assert.Single(shell.Workspace.Session.Lighting.Lights));
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            slider = inspector.GetVisualDescendants().OfType<EditorLightParameterSlider>().Single(control => control.Name == "LightBrightnessSlider");
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            slider.Value = EditorLightParameterScale.BrightnessPosition(6);
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.Equal(light, Assert.Single(shell.Workspace.Session.Lighting.Lights));
            Assert.Equal(light, Assert.Single(controller.PreviewLighting.Lights));
            Assert.True(shell.Workspace.Undo()); // Escape adds no entry; this removes the original added light.
            Assert.Empty(shell.Workspace.Session.Lighting.Lights);
            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task EscapeSuppressesHeldKeyRepeatsUntilReleaseAndNextPressCanEdit() => await session.Dispatch(() =>
    {
        var (window, shell, light) = Editor();
        try
        {
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            var slider = inspector.GetVisualDescendants().OfType<EditorLightParameterSlider>().Single(control => control.Name == "LightBrightnessSlider");
            var controller = (EditorLightViewportController)typeof(EditorWindow).GetField("lightController", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var committed = shell.Workspace.Session!.Lighting;
            int started = 0; slider.EditStarted += (_, _) => started++;
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            Assert.Equal(1, started);
            Assert.NotEqual(light.Intensity, Assert.Single(controller.PreviewLighting.Lights).Intensity);
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            double canceledValue = slider.Value;
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Right });
            Assert.Equal(1, started);
            Assert.Equal(canceledValue, slider.Value);
            Assert.Same(committed, shell.Workspace.Session.Lighting);
            Assert.Equal(light, Assert.Single(controller.PreviewLighting.Lights));
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Right });
            Assert.Equal(2, started);
            Assert.NotEqual(light.Intensity, Assert.Single(shell.Workspace.Session.Lighting.Lights).Intensity);
            Assert.True(shell.Workspace.Undo());
            Assert.Equal(light, Assert.Single(shell.Workspace.Session.Lighting.Lights));
            Assert.True(shell.Workspace.Undo()); // The canceled/repeated segment created no additional entry.
            Assert.Empty(shell.Workspace.Session.Lighting.Lights);
            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task LoadingOutOfSliderRangeDoesNotRewriteProjectAndUnfinishedHexSurvivesUndo() => await session.Dispatch(() =>
    {
        var (window, shell, light) = Editor();
        try
        {
            var large = light with { Intensity = 1e9f, Range = 1e7f };
            shell.Workspace.EditLighting(new(new SceneLightingSnapshot([large]), light.Id));
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            Assert.True(inspector.TryCommit());
            Assert.Equal(large, Assert.Single(shell.Workspace.Session!.Lighting.Lights));
            var hex = inspector.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "LightHexColor");
            hex.Text = "#12";
            Assert.True(shell.Workspace.Undo());
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            hex = inspector.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "LightHexColor");
            Assert.Equal("#12", hex.Text);
            Assert.False(inspector.TryCommit());
            Assert.Equal(light, Assert.Single(shell.Workspace.Session.Lighting.Lights));
            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(SceneLightKind.Directional)]
    [InlineData(SceneLightKind.Spot)]
    public async Task ResetBrightnessRepairsTextAndOnlyChangesIntensityInOneUndo(SceneLightKind kind) => await session.Dispatch(() =>
    {
        var (window, shell, light) = Editor();
        try
        {
            var before = light with { Intensity = 0, Kind = kind };
            shell.Workspace.EditLighting(new(new([before]), before.Id));
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            var brightness = inspector.GetVisualDescendants().OfType<EditorNumericUpDown>().Single(c => c.Name == "InlineLightIntensity");
            var reset = inspector.GetVisualDescendants().OfType<Button>().Single(c => c.Name == "ResetLightBrightnessButton");
            Assert.True(reset.IsEffectivelyVisible);
            brightness.Text = "unfinished";
            inspector.ViewModel!.SelectedLight!.Name = "pending name";
            reset.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            float expected = EditorLightPlacement.Create(Guid.NewGuid(), "new", kind, EditorLightPlacement.DefaultCamera).Intensity;
            var updated = shell.Workspace.Session!.Lighting;
            Assert.Equal(before with { Intensity = expected }, Assert.Single(updated.Lights));
            Assert.True(brightness.IsInputValid);
            Assert.Equal("pending name", inspector.ViewModel.SelectedLight.Name);
            reset.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Same(updated, shell.Workspace.Session.Lighting);
            Assert.True(shell.Workspace.Undo());
            Assert.Equal(before, Assert.Single(shell.Workspace.Session.Lighting.Lights));
            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task InvalidHexExplainsBlockedEditAndEscapeRestoresTheColorWithoutHistory() => await session.Dispatch(() =>
    {
        var (window, shell, light) = Editor();
        try
        {
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            var hex = inspector.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "LightHexColor");
            var source = shell.Workspace.Session!.Lighting;
            hex.Focus();
            hex.Text = "#12";
            Assert.False(inspector.TryCommit());
            var feedback = window.FindControl<TextBlock>("LightBrowserValidation")!;
            Assert.True(feedback.IsVisible);
            Assert.Contains("#FFFFFF", feedback.Text);

            hex.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.Equal("#FFFFFF", hex.Text);
            Assert.True(inspector.TryCommit());
            Assert.False(feedback.IsVisible);
            Assert.Same(source, shell.Workspace.Session.Lighting);
            Assert.Equal(light, Assert.Single(source.Lights));
            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task DraftsStayWithTheirDocumentAndDeletedLightsDoNotReviveOldInput() => await session.Dispatch(() =>
    {
        var light = Light(); var first = new SceneLightingSnapshot([light]);
        var second = new SceneLightingSnapshot([light with { Intensity = 10 }]);
        Guid firstDocument = Guid.NewGuid(), secondDocument = Guid.NewGuid();
        var inspector = new EditorLightInspector(); inspector.SetLighting(first, light.Id, firstDocument);
        var window = new Window { Width = 500, Height = 1000, Content = inspector };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var input = inspector.GetVisualDescendants().OfType<EditorNumericUpDown>().Single(control => control.Name == "InlineLightIntensity");
            input.Text = "unfinished";
            var hex = inspector.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "LightHexColor"); hex.Text = "#12";
            inspector.SetLighting(second, light.Id, secondDocument); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(10m, inspector.ViewModel!.SelectedLight!.Intensity);
            Assert.Equal("#FFFFFF", inspector.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "LightHexColor").Text);
            inspector.SetLighting(first, light.Id, firstDocument); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal("unfinished", inspector.GetVisualDescendants().OfType<EditorNumericUpDown>().Single(control => control.Name == "InlineLightIntensity").Text);
            Assert.Equal("#12", inspector.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "LightHexColor").Text);
            inspector.SetLighting(SceneLightingSnapshot.Empty, null, firstDocument); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            inspector.SetLighting(first, light.Id, firstDocument); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(inspector.GetVisualDescendants().OfType<EditorNumericUpDown>().Single(control => control.Name == "InlineLightIntensity").IsInputValid);
            Assert.Equal("#FFFFFF", inspector.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "LightHexColor").Text);
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task FocusedNumericInputPreviewsLiveWithoutSpuriousInitialInteractionAndCommitsOnce() => await session.Dispatch(() =>
    {
        var (window, shell, light) = Editor();
        try
        {
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            var controller = (EditorLightViewportController)typeof(EditorWindow).GetField("lightController", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            Assert.False(controller.IsInteracting);

            var advanced = inspector.GetVisualDescendants().OfType<Expander>().Single(control => control.Name == "InlineLightAdvanced");
            Assert.False(advanced.IsExpanded);
            var posX = inspector.GetVisualDescendants().OfType<EditorNumericUpDown>().Single(control => control.Name == "InlineLightPositionX");
            Assert.True(posX.IsEffectivelyVisible);

            posX.GetVisualDescendants().OfType<TextBox>().First().Focus(); Dispatcher.UIThread.RunJobs();
            Assert.False(controller.IsInteracting);
            var committedBefore = shell.Workspace.Session!.Lighting;

            posX.Value = 5m; Dispatcher.UIThread.RunJobs();
            Assert.Same(committedBefore, shell.Workspace.Session.Lighting);
            Assert.True(controller.IsInteracting);
            Assert.Equal(5f, Assert.Single(controller.PreviewLighting.Lights).Position.X);

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(controller.IsInteracting);
            Assert.Equal(light.Position.X, Assert.Single(shell.Workspace.Session.Lighting.Lights).Position.X);

            posX.GetVisualDescendants().OfType<TextBox>().First().Focus(); Dispatcher.UIThread.RunJobs();
            posX.Value = 7m; Dispatcher.UIThread.RunJobs();
            Assert.True(controller.IsInteracting);
            Assert.True(inspector.TryCommit());
            Dispatcher.UIThread.RunJobs();
            Assert.False(controller.IsInteracting);
            var after = Assert.Single(shell.Workspace.Session.Lighting.Lights);
            Assert.Equal(7f, after.Position.X);
            Assert.Equal(light.Rotation, after.Rotation);

            Assert.True(shell.Workspace.Undo());
            Assert.Equal(light, Assert.Single(shell.Workspace.Session.Lighting.Lights));
            Assert.True(shell.Workspace.Undo());
            Assert.Empty(shell.Workspace.Session.Lighting.Lights);
            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task DirectionPadDragPreviewsAttachedLightLocallyAndReleasesOneEdit() => await session.Dispatch(() =>
    {
        var (window, shell, original) = Editor();
        try
        {
            var project = shell.Workspace.Session!;
            var actor = project.Objects[0].Id;
            var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .7f);
            project.Edit(edit => edit.Composition.SetTransform(actor, new(3, 0, 0), rotation, 2), TestContext.Current.CancellationToken);
            var before = original with { Parent = new(actor) };
            shell.Workspace.EditLighting(new(new([before]), before.Id));
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            var pad = inspector.GetVisualDescendants().OfType<EditorLightDirectionPad>().Single();
            pad.BringIntoView(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var center = pad.TranslatePoint(new(pad.Bounds.Width / 2, pad.Bounds.Height / 2), window)!.Value;
            var end = center + new Avalonia.Vector(18, -12);
            var controller = (EditorLightViewportController)typeof(EditorWindow).GetField("lightController", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            window.MouseDown(center, MouseButton.Left); window.MouseMove(end);
            Assert.True(controller.IsInteracting);
            var preview = Assert.Single(controller.PreviewLighting.Lights);
            Assert.True(preview.Direction.X < 0 && preview.Direction.Y < 0);
            Assert.Equal(before, Assert.Single(project.Lighting.Lights));
            window.MouseUp(end, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Assert.False(controller.IsInteracting);
            Assert.Equal(preview, Assert.Single(project.Lighting.Lights));
            var world = Assert.Single(project.CaptureRenderSnapshot().Scene.Lighting!.Lights);
            Assert.InRange(Vector3.Distance(Vector3.Transform(preview.Direction, rotation), world.Direction), 0, .0001f);
            window.MouseDown(center, MouseButton.Left);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            window.MouseUp(center, MouseButton.Left);
            Assert.False(controller.IsInteracting);
            Assert.Equal(preview, Assert.Single(project.Lighting.Lights));
            Assert.True(shell.Workspace.Undo());
            Assert.Equal(before, Assert.Single(project.Lighting.Lights));
            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task TargetInputsAndQuickAimWorkAndSurviveRecovery() => await session.Dispatch(() =>
    {
        var (window, shell, _) = Editor();
        try
        {
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            var targetX = inspector.GetVisualDescendants().OfType<EditorNumericUpDown>().Single(control => control.Name == "InlineLightTargetX");
            targetX.GetVisualDescendants().OfType<TextBox>().First().Focus(); Dispatcher.UIThread.RunJobs();
            targetX.Value = 4m; Dispatcher.UIThread.RunJobs();
            Assert.True(inspector.TryCommit());
            var aimed = Assert.Single(shell.Workspace.Session!.Lighting.Lights);
            Assert.InRange(aimed.Target.X, 3.99f, 4.01f);

            var recovery = inspector.CaptureRecoveryState(shell.Workspace.Session.Lighting, EditorInspectorPage.Lighting, Guid.NewGuid());
            Assert.InRange((double)(recovery.Lights[0].TargetX ?? 0m), 3.99, 4.01);

            var centerBtn = inspector.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "InlineLightAimCenterButton");
            centerBtn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var atCenter = Assert.Single(shell.Workspace.Session.Lighting.Lights);
            var expectedLookAt = inspector.CreationCameraProvider?.Invoke().LookAt ?? EditorLightPlacement.DefaultCamera.LookAt;
            Assert.True(Vector3.Distance(atCenter.Target, expectedLookAt) < 0.05f);

            var cameraBtn = inspector.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "InlineLightAimCameraButton");
            cameraBtn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var fromCam = Assert.Single(shell.Workspace.Session.Lighting.Lights);
            var cam = (inspector.CreationCameraProvider?.Invoke() ?? EditorLightPlacement.DefaultCamera).CreateCamera();
            Assert.InRange(Vector3.Dot(Vector3.Normalize(fromCam.Direction), Vector3.Normalize(cam.Target - cam.Eye)), 0.999f, 1.001f);

            float prevZ = fromCam.Direction.Z;
            var flipBtn = inspector.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "InlineLightFlipHemisphereButton");
            flipBtn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var flipped = Assert.Single(shell.Workspace.Session.Lighting.Lights);
            Assert.True(MathF.Sign(flipped.Direction.Z) != MathF.Sign(prevZ) || MathF.Abs(prevZ) < 1e-4f);

            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    private static NativeSceneLight Light() => new(Guid.NewGuid(), "fill", SceneLightKind.Spot, Vector3.One, Quaternion.Identity, Vector3.One);
    private static (EditorWindow, EditorShellViewModel, NativeSceneLight) Editor()
    {
        var window = new EditorWindow(false);
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.IsSuspended = true; viewport.IsVisible = false;
        var shell = new EditorShellViewModel(); window.DataContext = shell; window.Show();
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd");
        using var input = File.OpenRead(path);
        var asset = EditorProjectWorkspace.CreateModelAsset(Mmd.Modern.Formats.PmdPmxAdapter.Convert(PmdReader.Read(input)), path);
        shell.Workspace.AddModel(path, asset, ModelRole.Character);
        var light = Light(); shell.Workspace.EditLighting(new(new SceneLightingSnapshot([light]), light.Id));
        shell.ShowInspectorPage(EditorInspectorPage.Lighting);
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        return (window, shell, light);
    }
    private static void Close(EditorWindow window)
    {
        typeof(EditorWindow).GetField("_closingConfirmed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
        window.Close(); Dispatcher.UIThread.RunJobs();
    }
    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
