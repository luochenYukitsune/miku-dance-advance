using System.Numerics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Application;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorLightReceiverInspectorTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);

    [Fact]
    public async Task ModeAndModelCheckboxesCommitFullSelectionAndSearchRetainsHiddenCheckedModels() => await session.Dispatch(() =>
    {
        var light = Light(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var inspector = Inspector(light, a, b); int commits = 0;
        NativeSceneLight accepted = light;
        inspector.CommitRequested = draft => { commits++; accepted = Assert.Single(draft.Snapshot.Lights); return true; };
        var window = Host(inspector);
        try
        {
            var modes = inspector.FindControl<ComboBox>("LightReceiverMode")!;
            Choose(SceneLightReceiverMode.Include); Assert.Empty(accepted.Receivers!.ObjectIds); Assert.Equal(1, commits);
            Check("Actor"); Assert.Equal(new[] { a }, accepted.Receivers.ObjectIds);
            inspector.FindControl<TextBox>("LightReceiverSearch")!.Text = "Room"; Pump();
            Check("Room"); Assert.Equal(2, accepted.Receivers.ObjectIds.Count);
            Choose(SceneLightReceiverMode.Exclude); Assert.Equal(2, accepted.Receivers.ObjectIds.Count);
            Assert.False(accepted.Receivers.Affects(a)); Assert.False(accepted.Receivers.Affects(b));
            Assert.True(inspector.TryCommit()); Assert.Equal(4, commits);
            Choose(SceneLightReceiverMode.All); Assert.Null(accepted.Receivers); Assert.Equal(5, commits);
            inspector.ViewModel!.SelectedLight!.Intensity = 2; Assert.True(inspector.TryCommit());
            Assert.Null(accepted.Receivers); Assert.Equal(2, accepted.Intensity);
            return true;
            void Pump() { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
            void Choose(SceneLightReceiverMode mode)
            { modes.SelectedItem = modes.Items.Cast<EditorLightReceiverModeChoice>().Single(choice => choice.Mode == mode); Pump(); }
            void Check(string name)
            {
                var box = inspector.FindControl<ItemsControl>("LightReceiverModels")!.GetVisualDescendants().OfType<CheckBox>()
                    .Single(box => box.DataContext is EditorLightReceiverChoice choice && choice.Name == name);
                box.SetCurrentValue(CheckBox.IsCheckedProperty, true); box.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            }
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task InvalidInputAndLegacyModeCannotPublishReceiverEdits() => await session.Dispatch(() =>
    {
        var light = Light(); var inspector = Inspector(light, Guid.NewGuid(), Guid.NewGuid());
        var window = Host(inspector); int commits = 0; inspector.CommitRequested = _ => { commits++; return true; };
        try
        {
            var input = inspector.GetVisualDescendants().OfType<EditorNumericUpDown>().Single(control => control.Name == "InlineLightIntensity");
            input.Text = "unfinished";
            var modes = inspector.FindControl<ComboBox>("LightReceiverMode")!;
            modes.SelectedItem = modes.Items.Cast<EditorLightReceiverModeChoice>().Single(choice => choice.Mode == SceneLightReceiverMode.Include);
            Assert.Equal(0, commits); Assert.Equal("unfinished", input.Text);
            Assert.Equal(SceneLightReceiverMode.All, Assert.IsType<EditorLightReceiverModeChoice>(modes.SelectedItem).Mode);
            inspector.UseWorkspaceBrowser(false);
            Assert.False(inspector.FindControl<StackPanel>("LightReceiverProperties")!.IsEffectivelyVisible);
            modes.SelectedItem = modes.Items.Cast<EditorLightReceiverModeChoice>().Single(choice => choice.Mode == SceneLightReceiverMode.Exclude);
            Assert.Equal(0, commits); Assert.Null(inspector.ViewModel!.SelectedLight!.Build().Receivers);
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ActualWorkspaceCommitsReceiverChoiceWithUndoAndMatchingMaterialOwners() => await session.Dispatch(() =>
    {
        var window = new EditorWindow(false); var shell = new EditorShellViewModel();
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!; viewport.IsSuspended = true; viewport.IsVisible = false;
        window.DataContext = shell; window.Show();
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd");
            using var input = File.OpenRead(path); var model = PmdPmxAdapter.Convert(PmdReader.Read(input));
            var asset = EditorProjectWorkspace.CreateModelAsset(model, path);
            shell.Workspace.AddModel(path, asset, ModelRole.Prop); shell.Workspace.AddModel(path, asset, ModelRole.Prop);
            var light = Light(); shell.Workspace.EditLighting(new(new([light]), light.Id));
            shell.ShowInspectorPage(EditorInspectorPage.Lighting); Pump();
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            inspector.FindControl<Expander>("LightReceiverExpander")!.IsExpanded = true; Pump();
            var mode = inspector.FindControl<ComboBox>("LightReceiverMode")!;
            mode.SelectedItem = mode.Items.Cast<EditorLightReceiverModeChoice>().Single(choice => choice.Mode == SceneLightReceiverMode.Include); Pump();
            var target = shell.Workspace.Session!.Objects[1].Id;
            var box = inspector.FindControl<ItemsControl>("LightReceiverModels")!.GetVisualDescendants().OfType<CheckBox>()
                .Single(box => box.DataContext is EditorLightReceiverChoice choice && choice.ObjectId == target);
            box.SetCurrentValue(CheckBox.IsCheckedProperty, true); box.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            var selected = Assert.Single(shell.Workspace.Session.Lighting.Lights);
            Assert.Equal(new[] { target }, selected.Receivers!.ObjectIds);
            Assert.Equal(1u, shell.Workspace.Session.Lighting.ReceiverMask(target));
            Assert.Equal(0u, shell.Workspace.Session.Lighting.ReceiverMask(shell.Workspace.Session.Objects[0].Id));
            Assert.Contains(shell.Workspace.Session.Frame.Materials, flags => flags.LightReceiverObjectId == target);
            Assert.True(shell.Workspace.Undo()); Assert.Empty(Assert.Single(shell.Workspace.Session.Lighting.Lights).Receivers!.ObjectIds);
            Assert.True(shell.Workspace.Redo()); Assert.Equal(selected, Assert.Single(shell.Workspace.Session.Lighting.Lights)); Pump();
            if (Environment.GetEnvironmentVariable("MDA_LIGHTING_LAYOUT_EVIDENCE") is { Length: > 0 } evidence)
            {
                Directory.CreateDirectory(evidence);
                using var normal = window.CaptureRenderedFrame(); Assert.NotNull(normal); normal.Save(Path.Combine(evidence, "lighting-receivers.png"));
                window.Width = 1100; window.Height = 720; Pump();
                using var minimum = window.CaptureRenderedFrame(); Assert.NotNull(minimum); minimum.Save(Path.Combine(evidence, "lighting-receivers-minimum.png"));
            }
            return true;
            void Pump() { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        }
        finally
        {
            typeof(EditorWindow).GetField("_closingConfirmed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
            window.Close(); Dispatcher.UIThread.RunJobs();
        }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task RecoveryJsonRetainsImmutableReceiverSelectionsAndPendingParameters() => await session.Dispatch(() =>
    {
        var id = Guid.NewGuid(); var light = Light() with { Receivers = new(SceneLightReceiverMode.Include, [id]), Parent = new(id, 0) };
        var inspector = Inspector(light, id, Guid.NewGuid());
        inspector.ViewModel!.SelectedLight!.Intensity = 7;
        var state = EditorRecoverySessionState.Capture(new(), new(), []) with
        { NativeLighting = inspector.CaptureRecoveryState(new([light]), EditorInspectorPage.Lighting) };
        var decoded = EditorRecoverySessionState.FromJsonBytes(state.ToJsonBytes());
        Assert.Equal(light, Assert.Single(decoded.NativeLighting!.CommittedLights));
        var target = new EditorLightInspector(); target.RestoreRecoveryState(decoded.NativeLighting, new([light]));
        Assert.Equal(7, target.ViewModel!.SelectedLight!.Intensity);
        Assert.Equal(light.Receivers, target.ViewModel.SelectedLight.Build().Receivers);
        return true;
    }, TestContext.Current.CancellationToken);

    private static NativeSceneLight Light() => new(Guid.NewGuid(), "fill", SceneLightKind.Point, Vector3.One, Quaternion.Identity, Vector3.One);
    private static EditorLightInspector Inspector(NativeSceneLight light, Guid a, Guid b)
    {
        var inspector = new EditorLightInspector(); inspector.UseWorkspaceBrowser(true);
        inspector.ConfigureParentContext([new(a, "Actor", []), new(b, "Room", [])], _ => Matrix4x4.Identity);
        inspector.SetLighting(new([light]), light.Id); return inspector;
    }
    private static Window Host(EditorLightInspector inspector)
    {
        var window = new Window { Width = 500, Height = 1100, Content = inspector };
        inspector.FindControl<Expander>("LightReceiverExpander")!.IsExpanded = true;
        window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); return window;
    }
    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
