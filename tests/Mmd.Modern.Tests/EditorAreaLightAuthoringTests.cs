using System.Numerics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorAreaLightAuthoringTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static NativeSceneLight Area() => new(Guid.NewGuid(), "柔光箱", SceneLightKind.Area,
        new(2, 3, 4), Quaternion.Identity, Vector3.One, AreaSize: new(8.123456f, 12.654321f));

    [Fact]
    public void TypeChangesRetainExactAreaSizeAndOldLightsStayNullUntilAreaIsChosen()
    {
        var light = Area(); var item = new EditorNativeLightItem(light);
        Assert.Equal(light, item.Build()); Assert.True(item.HasAreaSize);
        item.KindIndex = (int)SceneLightKind.Point; Assert.False(item.HasAreaSize);
        Assert.Equal(light.AreaSize, item.Build().AreaSize);
        item.KindIndex = (int)SceneLightKind.Area; Assert.Equal(light, item.Build());
        item.AreaWidth = 0; Assert.Throws<EditorLocalizedArgumentException>(() => item.Build());
        item.AreaWidth = null; Assert.Throws<EditorLocalizedArgumentException>(() => item.Build());
        var old = light with { Kind = SceneLightKind.Point, AreaSize = null }; item = new(old);
        Assert.Equal(old, item.Build()); item.KindIndex = (int)SceneLightKind.Area;
        Assert.Equal(new Vector2(5), item.Build().AreaSize);
        item.KindIndex = (int)SceneLightKind.Spot; Assert.Equal(new Vector2(5), item.Build().AreaSize);
    }

    [Fact]
    public async Task SliderPausesPlaybackPreviewsBothDimensionsAndCreatesOneUndoWhileEscapeCancels() => await session.Dispatch(() =>
    {
        var (window, shell, light) = Editor();
        try
        {
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            var slider = Find<EditorLightParameterSlider>(inspector, "LightAreaWidthSlider");
            var controller = (EditorLightViewportController)typeof(EditorWindow).GetField("lightController", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            double frame = shell.Workspace.Session!.CurrentFrame;
            shell.PlayCommand.Execute(null); Assert.True(shell.IsPlaying);
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            Assert.False(shell.IsPlaying); Assert.Equal(frame, shell.Workspace.Session.CurrentFrame);
            slider.Value = EditorLightParameterScale.DistancePosition(15); slider.Value = EditorLightParameterScale.DistancePosition(18);
            Assert.Equal(light.AreaSize, Assert.Single(shell.Workspace.Session.Lighting.Lights).AreaSize);
            Assert.Equal(new Vector2(18, light.AreaSize!.Value.Y), Assert.Single(controller.PreviewLighting.Lights).AreaSize);
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Right });
            Assert.Equal(new Vector2(18, light.AreaSize.Value.Y), Assert.Single(shell.Workspace.Session.Lighting.Lights).AreaSize);
            Assert.True(shell.Workspace.Undo()); Assert.Equal(light, Assert.Single(shell.Workspace.Session.Lighting.Lights));
            Pump(window);
            slider = Find<EditorLightParameterSlider>(inspector, "LightAreaHeightSlider");
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            slider.Value = EditorLightParameterScale.DistancePosition(25);
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.Equal(light, Assert.Single(shell.Workspace.Session.Lighting.Lights));
            Assert.Equal(light, Assert.Single(controller.PreviewLighting.Lights));
            Assert.True(shell.Workspace.Undo()); Assert.Empty(shell.Workspace.Session.Lighting.Lights);
            return true;
        }
        finally { Close(window); }
    }, Token);

    [Fact]
    public async Task RecoveryAndOutsideEditsPreserveUnfinishedDimensionsAndOnlyAreaShowsControls() => await session.Dispatch(() =>
    {
        var light = Area(); var snapshot = new SceneLightingSnapshot([light]); Guid document = Guid.NewGuid();
        var inspector = new EditorLightInspector(); inspector.UseWorkspaceBrowser(true); inspector.SetLighting(snapshot, light.Id, document);
        var window = new Window { Width = 500, Height = 1100, Content = inspector };
        try
        {
            window.Show(); Pump(window);
            Find<EditorNumericUpDown>(inspector, "InlineLightAreaWidth").Text = "unfinished width";
            inspector.ViewModel!.SelectedLight!.AreaHeight = 27;
            var state = inspector.CaptureRecoveryState(snapshot, EditorInspectorPage.Lighting, document);
            Assert.Equal("unfinished width", state.Lights[0].NumericText["InlineLightAreaWidth"]);
            Assert.Equal(27m, state.Lights[0].AreaHeight);
            var envelope = EditorRecoverySessionState.Capture(new(), new(), []) with { NativeLighting = state };
            state = EditorRecoverySessionState.FromJsonBytes(envelope.ToJsonBytes()).NativeLighting!;
            Assert.Equal(light, Assert.Single(state.CommittedLights));
            Assert.Equal(light, Assert.Single(state.Lights).Original);
            using (inspector.SuspendRecoveryEdits()) inspector.RestoreRecoveryState(state, snapshot, document);
            Pump(window);
            Assert.Equal(27m, inspector.ViewModel!.SelectedLight!.AreaHeight);
            Assert.Equal("unfinished width", Find<EditorNumericUpDown>(inspector, "InlineLightAreaWidth").Text);
            var changed = new SceneLightingSnapshot([light with { Intensity = 2 }]);
            inspector.SetLighting(changed, light.Id, document); Pump(window);
            Assert.Equal(27m, inspector.ViewModel!.SelectedLight!.AreaHeight);
            Assert.Equal(2m, inspector.ViewModel.SelectedLight.Intensity);
            Assert.Equal("unfinished width", Find<EditorNumericUpDown>(inspector, "InlineLightAreaWidth").Text);
            Assert.False(inspector.TryCommit());
            inspector.SetLighting(new([light with { Kind = SceneLightKind.Point }]), light.Id, Guid.NewGuid()); Pump(window);
            Assert.False(Find<StackPanel>(inspector, "AreaLightDimensions").IsEffectivelyVisible);
            Assert.Equal(light.AreaSize, inspector.ViewModel!.SelectedLight!.Build().AreaSize);
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, Token);

    [Fact]
    public async Task KindChoiceUsesFourthTypePausesAndRetainsDimensionsAcrossUndoAndLocalization() => await session.Dispatch(() =>
    {
        var (window, shell, light) = Editor();
        try
        {
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            shell.PlayCommand.Execute(null); Assert.True(shell.IsPlaying);
            Find<ComboBox>(inspector, "InlineLightKind").SelectedIndex = (int)SceneLightKind.Point;
            Assert.False(shell.IsPlaying);
            Assert.Equal(SceneLightKind.Point, Assert.Single(shell.Workspace.Session!.Lighting.Lights).Kind);
            Assert.Equal(light.AreaSize, Assert.Single(shell.Workspace.Session.Lighting.Lights).AreaSize);
            Assert.True(shell.Workspace.Undo()); Pump(window);
            Assert.Equal(light, Assert.Single(shell.Workspace.Session.Lighting.Lights));
            var choices = inspector.ViewModel!.SelectedLight!.KindChoices;
            foreach (string language in new[] { "zh-CN", "en", "ja" })
            {
                EditorLocalization.SetLanguage(language); Pump(window);
                Assert.Equal(4, inspector.ViewModel!.SelectedLight!.KindChoices.Length);
                Assert.Same(choices, inspector.ViewModel.SelectedLight.KindChoices);
                Assert.Equal(3, Find<ComboBox>(inspector, "InlineLightKind").SelectedIndex);
                Assert.Equal(EditorLocalization.Text("Mda.Text.Context.Lighting.Area"), choices[3].Label);
                Assert.Equal(light, Assert.Single(shell.Workspace.Session.Lighting.Lights));
            }
            EditorLocalization.SetLanguage("zh-CN"); Pump(window);
            if (Environment.GetEnvironmentVariable("MDA_LIGHTING_LAYOUT_EVIDENCE") is { Length: > 0 } evidence)
            {
                Directory.CreateDirectory(evidence);
                window.Width = 1480; window.Height = 940; Pump(window);
                using var normal = window.CaptureRenderedFrame(); Assert.NotNull(normal); normal.Save(Path.Combine(evidence, "lighting-area.png"));
                window.Width = 1100; window.Height = 720; Pump(window);
                using var minimum = window.CaptureRenderedFrame(); Assert.NotNull(minimum); minimum.Save(Path.Combine(evidence, "lighting-area-minimum.png"));
            }
            return true;
        }
        finally { EditorLocalization.SetLanguage("zh-CN"); Close(window); }
    }, Token);

    private static (EditorWindow, EditorShellViewModel, NativeSceneLight) Editor()
    {
        var window = new EditorWindow(false); var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.IsSuspended = true; viewport.IsVisible = false;
        var shell = new EditorShellViewModel(); window.DataContext = shell; window.Show();
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd");
        using var input = File.OpenRead(path);
        var model = Mmd.Modern.Formats.PmdPmxAdapter.Convert(PmdReader.Read(input));
        shell.Workspace.AddModel(path, EditorProjectWorkspace.CreateModelAsset(model, path), ModelRole.Character);
        var light = Area(); shell.Workspace.EditLighting(new(new([light]), light.Id));
        shell.ShowInspectorPage(EditorInspectorPage.Lighting); Pump(window);
        return (window, shell, light);
    }
    private static T Find<T>(EditorLightInspector inspector, string name) where T : Control
        => inspector.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pump(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Close(EditorWindow window)
    {
        typeof(EditorWindow).GetField("_closingConfirmed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
        window.Close(); Dispatcher.UIThread.RunJobs();
    }
    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
