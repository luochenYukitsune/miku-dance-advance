using System.Numerics;
using System.Reflection;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorGlobalLightingInspectorTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);

    [Fact]
    public async Task EveryDetachedLightOperationKeepsGlobalSettings() => await session.Dispatch(() =>
    {
        var settings = new SceneLightingSettings(.31f, 1.25f);
        var light = Light();
        var model = new EditorLightingViewModel(new([light], settings: settings), light.Id);
        Assert.True(model.AddLight(SceneLightKind.Area));
        Assert.True(model.TryCreate(out var added)); Assert.Equal(settings, added!.Snapshot.Settings);
        model.RemoveSelectedLight();
        Assert.True(model.TryCreate(out var removed)); Assert.Equal(settings, removed!.Snapshot.Settings);
        model.SelectMany([light.Id], light.Id);
        Assert.True(model.DuplicateSelection());
        Assert.True(model.TryCreate(out var duplicated)); Assert.Equal(settings, duplicated!.Snapshot.Settings);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task GlobalSliderPublishesOnceAndEscapeRestoresWhilePendingLampTextSurvives() => await session.Dispatch(() =>
    {
        var (window, shell, light) = Editor();
        try
        {
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            inspector.FindControl<Expander>("GlobalEnvironmentExpander")!.IsExpanded = true; Pump(window);
            var input = inspector.GetVisualDescendants().OfType<EditorNumericUpDown>().Single(x => x.Name == "InlineLightIntensity");
            input.Text = "unfinished lamp"; Pump(window);
            var slider = inspector.FindControl<EditorLightParameterSlider>("GlobalEnvironmentSlider")!;
            var controller = (EditorLightViewportController)typeof(EditorWindow).GetField("lightController", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var before = shell.Workspace.Session!.Lighting;
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            slider.Value = .5; slider.Value = .75;
            Assert.Same(before, shell.Workspace.Session.Lighting);
            Assert.Equal(.75f, controller.PreviewLighting.Settings.EnvironmentStrength);
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Right }); Pump(window);
            Assert.Equal(.75f, shell.Workspace.Session.Lighting.Settings.EnvironmentStrength);
            Assert.Equal("unfinished lamp", inspector.GetVisualDescendants().OfType<EditorNumericUpDown>().Single(x => x.Name == "InlineLightIntensity").Text);
            slider = inspector.FindControl<EditorLightParameterSlider>("GlobalEnvironmentSlider")!;
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right }); slider.Value = 1.5;
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.Equal(.75f, controller.PreviewLighting.Settings.EnvironmentStrength);
            Assert.True(shell.Workspace.Undo()); Pump(window);
            Assert.Equal(SceneLightingSettings.Default, shell.Workspace.Session.Lighting.Settings);
            Assert.Equal(light, Assert.Single(shell.Workspace.Session.Lighting.Lights));
            Assert.True(shell.Workspace.Undo()); Assert.Empty(shell.Workspace.Session.Lighting.Lights);
            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task GlobalNumericCommitKeepsUnfinishedLampDraftAndRejectsInvalidGlobalInput() => await session.Dispatch(() =>
    {
        var light = Light(); var settings = new SceneLightingSettings(.31f, -.7f);
        var inspector = new EditorLightInspector(); inspector.UseWorkspaceBrowser(true);
        var source = new SceneLightingSnapshot([light], settings: settings); Guid document = Guid.NewGuid();
        inspector.SetLighting(source, light.Id, document);
        var window = new Window { Width = 360, Height = 720, Content = new ScrollViewer { Content = inspector } };
        try
        {
            window.Show(); inspector.FindControl<Expander>("GlobalExposureExpander")!.IsExpanded = true; Pump(window);
            inspector.ViewModel!.SelectedLight!.Range = null;
            int commits = 0; EditorLightingDraft? accepted = null;
            inspector.CommitRequested = draft => { commits++; accepted = draft; return true; };
            var input = inspector.FindControl<EditorNumericUpDown>("GlobalExposureInput")!;
            int starts = 0; inspector.EditStartingRequested = () => { starts++; return true; };
            input.RaiseEvent(new GotFocusEventArgs { RoutedEvent = InputElement.GotFocusEvent });
            Assert.True(starts > 0); Assert.Equal(0, commits);
            input.Value = 1.5m; input.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
            Assert.Equal(1, commits); Assert.Equal(new SceneLightingSettings(.31f, 1.5f), accepted!.Snapshot.Settings);
            Assert.Equal(light, Assert.Single(accepted.Snapshot.Lights)); Assert.Null(inspector.ViewModel.SelectedLight.Range);
            inspector.SetLighting(accepted.Snapshot, light.Id, document); Pump(window);
            Assert.Null(inspector.ViewModel!.SelectedLight!.Range);
            input = inspector.FindControl<EditorNumericUpDown>("GlobalExposureInput")!;
            input.Text = "9"; input.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
            Assert.False(input.IsInputValid); Assert.Equal(1, commits);
            inspector.FindControl<Expander>("GlobalExposureExpander")!.IsExpanded = false;
            Assert.False(inspector.TryCommit()); Assert.Equal(1, commits);
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task RecoveryRoundTripPreservesGlobalsAndUnfinishedTextAndRejectsMalformedSettings() => await session.Dispatch(() =>
    {
        var settings = new SceneLightingSettings(.31234567f, -1.25f);
        var source = new SceneLightingSnapshot([], settings: settings); Guid document = Guid.NewGuid();
        var inspector = new EditorLightInspector(); inspector.UseWorkspaceBrowser(true); inspector.SetLighting(source, null, document);
        var window = new Window { Width = 360, Height = 720, Content = inspector };
        try
        {
            window.Show(); inspector.FindControl<Expander>("GlobalEnvironmentExpander")!.IsExpanded = true; Pump(window);
            inspector.FindControl<EditorNumericUpDown>("GlobalEnvironmentInput")!.Text = "unfinished fill";
            inspector.ViewModel!.ExposureEv = null; Pump(window);
            var state = inspector.CaptureRecoveryState(source, EditorInspectorPage.Lighting, document);
            var envelope = EditorRecoverySessionState.Capture(new(), new(), []) with { NativeLighting = state };
            byte[] bytes = envelope.ToJsonBytes();
            var roundTrip = EditorRecoverySessionState.FromJsonBytes(bytes).NativeLighting!;
            Assert.Equal(settings, roundTrip.CommittedSettings); Assert.Equal(settings, roundTrip.GlobalDraft!.Original);
            Assert.Equal("unfinished fill", roundTrip.GlobalDraft.NumericText["GlobalEnvironmentInput"]);
            Assert.Null(roundTrip.GlobalDraft.ExposureEv);
            inspector.RestoreRecoveryState(roundTrip, source, document); Pump(window);
            Assert.Equal("unfinished fill", inspector.FindControl<EditorNumericUpDown>("GlobalEnvironmentInput")!.Text);
            Assert.Null(inspector.ViewModel!.ExposureEv); Assert.False(inspector.TryCommit());
            Assert.Throws<InvalidDataException>(() => (roundTrip with { GlobalDraft = roundTrip.GlobalDraft with
            { NumericText = ImmutableSortedDictionary<string, string>.Empty.Add("unexpected", "1") } }).Validate());
            foreach (string invalid in new[] { "{}", "null", "{\"EnvironmentStrength\":5,\"ExposureEv\":0}",
                "{\"EnvironmentStrength\":1,\"ExposureEv\":0,\"Extra\":1}", "{\"EnvironmentStrength\":1e100,\"ExposureEv\":0}" })
            {
                var json = JsonNode.Parse(bytes)!; json["NativeLighting"]!["CommittedSettings"] = JsonNode.Parse(invalid);
                Assert.Throws<InvalidDataException>(() => EditorRecoverySessionState.FromJsonBytes(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString())));
            }
            Assert.Throws<InvalidDataException>(() => roundTrip.ValidateAgainst(SceneLightingSnapshot.Empty));
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ThreeLanguagesKeepGlobalDraftsAndLegacyControlsCannotPublishThem() => await session.Dispatch(() =>
    {
        var settings = new SceneLightingSettings(.31f, .75f); var snapshot = new SceneLightingSnapshot([], settings: settings);
        var inspector = new EditorLightInspector(); inspector.UseWorkspaceBrowser(true); inspector.SetLighting(snapshot, null);
        var window = new Window { Width = 360, Height = 720, Content = inspector };
        try
        {
            window.Show(); Pump(window);
            var environment = inspector.FindControl<Expander>("GlobalEnvironmentExpander")!;
            var exposure = inspector.FindControl<Expander>("GlobalExposureExpander")!;
            Assert.False(environment.IsExpanded); Assert.False(exposure.IsExpanded);
            Assert.True(inspector.FindControl<Expander>("LightPresetExpander")!.IsEffectivelyVisible);
            environment.IsExpanded = true; exposure.IsExpanded = true; Pump(window);
            int commits = 0, qualityOpens = 0; inspector.CommitRequested = _ => { commits++; return true; };
            inspector.PreviewQualityRequested = () => qualityOpens++;
            inspector.FindControl<EditorNumericUpDown>("GlobalEnvironmentInput")!.Text = "unfinished fill";
            foreach (var (language, title) in new[] { ("zh-CN", "整体曝光"), ("en", "Overall exposure"), ("ja", "全体の露出") })
            {
                EditorLocalization.SetLanguage(language); Pump(window);
                Assert.Equal(title, exposure.Header);
                Assert.Equal("unfinished fill", inspector.FindControl<EditorNumericUpDown>("GlobalEnvironmentInput")!.Text);
                Assert.Equal(.75m, inspector.ViewModel!.ExposureEv);
                foreach (string name in new[] { "GlobalEnvironmentInput", "GlobalExposureInput", "GlobalPreviewQualityButton" })
                {
                    var control = inspector.FindControl<Control>(name)!;
                    Assert.True(control.Bounds.Width > 0 && control.Bounds.Width <= inspector.Bounds.Width);
                }
                if (Environment.GetEnvironmentVariable("MDA_GLOBAL_LIGHTING_EVIDENCE") is { Length: > 0 } evidence)
                {
                    Directory.CreateDirectory(evidence);
                    var number = inspector.FindControl<EditorNumericUpDown>("GlobalEnvironmentInput")!;
                    number.Text = "0.31"; Pump(window);
                    using var image = HeadlessTestHost.CaptureLayoutFrame(window); Assert.NotNull(image);
                    image.Save(Path.Combine(evidence, "globals-" + language + "-360.png"));
                    number.Text = "unfinished fill"; Pump(window);
                }
            }
            inspector.FindControl<Button>("GlobalPreviewQualityButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, qualityOpens); Assert.Equal(0, commits);
            inspector.UseWorkspaceBrowser(false); Pump(window);
            Assert.False(environment.IsEffectivelyVisible); Assert.False(exposure.IsEffectivelyVisible);
            var slider = inspector.FindControl<EditorLightParameterSlider>("GlobalExposureSlider")!;
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Right });
            Assert.Equal(.75m, inspector.ViewModel!.ExposureEv); Assert.Equal(0, commits);
            return true;
        }
        finally { EditorLocalization.SetLanguage("zh-CN"); window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ActualEditorGlobalControlsUseEditorStylesAndRemainWithinRightRail() => await session.Dispatch(() =>
    {
        var (window, shell, _) = Editor();
        try
        {
            window.Width=1480;window.Height=940;Pump(window);
            var inspector=window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            var environment=inspector.FindControl<Expander>("GlobalEnvironmentExpander")!;
            var exposure=inspector.FindControl<Expander>("GlobalExposureExpander")!;
            environment.IsExpanded=true;exposure.IsExpanded=true;Pump(window);
            var scroll=environment.GetVisualAncestors().OfType<ScrollViewer>().First();
            if(scroll.Content is Control content && environment.TranslatePoint(default,content) is { } location)
                scroll.Offset=new Avalonia.Vector(0,location.Y);
            Pump(window);
            var number=inspector.FindControl<EditorNumericUpDown>("GlobalEnvironmentInput")!;
            number.Text="0.31";Pump(window);
            Assert.True(number.IsInputValid);
            Assert.InRange(number.Bounds.Width,60,140);
            if(Environment.GetEnvironmentVariable("MDA_GLOBAL_LIGHTING_EVIDENCE") is {Length:>0} evidence)
            {
                Directory.CreateDirectory(evidence);
                using var picture=HeadlessTestHost.CaptureLayoutFrame(window);Assert.NotNull(picture);
                picture.Save(Path.Combine(evidence,"globals-actual-editor.png"));
            }
            return true;
        }
        finally { Close(window); }
    },TestContext.Current.CancellationToken);

    private static NativeSceneLight Light() => new(Guid.NewGuid(), "fill", SceneLightKind.Spot, Vector3.One, Quaternion.Identity, Vector3.One);
    private static (EditorWindow, EditorShellViewModel, NativeSceneLight) Editor()
    {
        var window = new EditorWindow(false); var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.IsSuspended = true; viewport.IsVisible = false;
        var shell = new EditorShellViewModel(); window.DataContext = shell; window.Show();
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd");
        using var input = File.OpenRead(path);
        var asset = EditorProjectWorkspace.CreateModelAsset(Mmd.Modern.Formats.PmdPmxAdapter.Convert(PmdReader.Read(input)), path);
        shell.Workspace.AddModel(path, asset, ModelRole.Character);
        var light = Light(); shell.Workspace.EditLighting(new(new SceneLightingSnapshot([light]), light.Id));
        shell.ShowInspectorPage(EditorInspectorPage.Lighting); Pump(window);
        return (window, shell, light);
    }
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static void Close(EditorWindow window)
    {
        typeof(EditorWindow).GetField("_closingConfirmed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
        window.Close(); Dispatcher.UIThread.RunJobs();
    }
    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
