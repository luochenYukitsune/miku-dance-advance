using System.Numerics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorLightPresetInspectorTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static NativeSceneLight Light() => new(Guid.NewGuid(), "Existing", SceneLightKind.Point,
        new(1, 10, 0), Quaternion.Identity, Vector3.One);

    [Fact]
    public async Task IncompleteInputAndCapacityRejectWithoutPublishingHalfARig() => await session.Dispatch(() =>
    {
        var light = Light(); var inspector = new EditorLightInspector(); inspector.UseWorkspaceBrowser(true);
        inspector.SetLighting(new([light]), light.Id, Guid.NewGuid()); var window = Host(inspector);
        int commits = 0; inspector.CommitRequested = _ => { commits++; return true; };
        try
        {
            var number = inspector.GetVisualDescendants().OfType<EditorNumericUpDown>().Single(value => value.Name == "InlineLightIntensity");
            number.Text = "unfinished";
            Assert.False(inspector.ApplyLightingPreset(SceneLightingPresetApplyMode.Add)); Assert.Equal(0, commits);
            Assert.Equal("unfinished", number.Text); Assert.Equal(light.Id, Assert.Single(inspector.ViewModel!.Lights).Id);
            var full = new SceneLightingSnapshot(Enumerable.Range(0, SceneLightingSnapshot.MaxActiveLights).Select(_ => Light()));
            inspector.SetLighting(full, full.Lights[0].Id, Guid.NewGuid()); Pump(window);
            Assert.False(inspector.ApplyLightingPreset(SceneLightingPresetApplyMode.Add)); Assert.Equal(0, commits);
            Assert.Equal(full.Lights.Select(value => value.Id), inspector.ViewModel!.Lights.Select(value => value.Id));
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task AddAndReplaceSelectedCommitOnceAndLegacyOrRejectedEditsKeepState() => await session.Dispatch(() =>
    {
        var light = Light(); var inspector = new EditorLightInspector(); inspector.UseWorkspaceBrowser(true);
        inspector.SetLighting(new([light]), light.Id, Guid.NewGuid());
        var window = Host(inspector); int commits = 0; EditorLightingDraft? accepted = null;
        inspector.CommitRequested = draft => { commits++; accepted = draft; return true; };
        try
        {
            Assert.False(inspector.FindControl<Expander>("LightPresetExpander")!.IsExpanded);
            Assert.True(inspector.ApplyLightingPreset(SceneLightingPresetApplyMode.Add));
            Assert.Equal(1, commits); Assert.Equal(4, accepted!.Snapshot.Lights.Count); Assert.Contains(light, accepted.Snapshot.Lights);
            var oldIds = accepted.SelectedLightIds.ToArray(); Assert.Equal(3, oldIds.Length);
            Assert.Single(inspector.ViewModel!.SelectedGroupIds);
            Assert.True(inspector.ApplyLightingPreset(SceneLightingPresetApplyMode.ReplaceSelected));
            Assert.Equal(2, commits); Assert.Equal(4, accepted.Snapshot.Lights.Count);
            Assert.All(oldIds, id => Assert.DoesNotContain(accepted.Snapshot.Lights, value => value.Id == id));
            Assert.Single(accepted.Snapshot.Groups); Assert.Contains(light, accepted.Snapshot.Lights);
            var before = inspector.ViewModel!.Lights.Select(value => value.Id).ToArray();
            inspector.CommitRequested = _ => false;
            Assert.False(inspector.ApplyLightingPreset(SceneLightingPresetApplyMode.Add));
            Assert.Equal(before, inspector.ViewModel.Lights.Select(value => value.Id));
            inspector.UseWorkspaceBrowser(false); Pump(window);
            Assert.False(inspector.FindControl<Expander>("LightPresetExpander")!.IsEffectivelyVisible);
            Assert.False(inspector.ApplyLightingPreset(SceneLightingPresetApplyMode.Add));
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task CaptureResolvesParentAndFilesLoadWithoutApplyingAndNamesFollowLanguage() => await session.Dispatch(() =>
    {
        EditorLocalization.SetLanguage("zh-CN");
        var parent = new SceneLightParent(Guid.NewGuid(), 0);
        var light = Light() with { Parent = parent, Receivers = new(SceneLightReceiverMode.Exclude, [parent.ObjectId]) };
        var inspector = new EditorLightInspector(); inspector.UseWorkspaceBrowser(true);
        inspector.ConfigureParentContext([new(parent.ObjectId, "Actor", ["Root"])], _ => Matrix4x4.CreateTranslation(20, 0, 0));
        inspector.SetLighting(new([light]), light.Id, Guid.NewGuid()); var window = Host(inspector);
        int commits = 0; inspector.CommitRequested = _ => { commits++; return true; };
        string directory = Path.Combine(Environment.GetEnvironmentVariable("MDA_LIGHTING_PRESET_EVIDENCE") ?? Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.True(inspector.TryCaptureLightingPreset(out var preset)); Assert.NotNull(preset);
            var captured = Assert.Single(preset.Lighting.Lights);
            Assert.Null(captured.Parent); Assert.Null(captured.Receivers); Assert.Equal(new(21, 10, 0), captured.Position);
            string path = Path.Combine(directory, "preset.json"); preset.Save(path); inspector.LoadLightingPresetFile(path);
            Assert.Equal(0, commits); Assert.Equal(light, inspector.ViewModel!.SelectedLight!.Build());
            Assert.Equal(preset.Name, Assert.IsType<EditorLightPresetChoice>(inspector.FindControl<ComboBox>("LightPresetChoice")!.SelectedItem).Label);
            inspector.FindControl<TextBox>("LightPresetName")!.Text = "My custom name";
            EditorLocalization.SetLanguage("ja"); Pump(window);
            Assert.Equal("My custom name", inspector.FindControl<TextBox>("LightPresetName")!.Text);
            var choice = inspector.FindControl<ComboBox>("LightPresetChoice")!; choice.SelectedIndex = 0;
            Assert.Equal("人物ソフトライト", Assert.IsType<EditorLightPresetChoice>(choice.SelectedItem).Label);
            Assert.Equal("人物ソフトライト", inspector.FindControl<TextBox>("LightPresetName")!.Text);
            return true;
        }
        finally { EditorLocalization.SetLanguage("zh-CN"); window.Close(); Dispatcher.UIThread.RunJobs(); Directory.Delete(directory, true); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ActualWorkspacePresetButtonsSupportUndoRedoAndThreeLanguageMinimumLayout() => await session.Dispatch(() =>
    {
        var window = new EditorWindow(false); var shell = new EditorShellViewModel();
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!; viewport.IsSuspended = true; viewport.IsVisible = false;
        window.DataContext = shell; window.Show();
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd");
            using var input = File.OpenRead(path); var model = PmdPmxAdapter.Convert(PmdReader.Read(input));
            shell.Workspace.AddModel(path, EditorProjectWorkspace.CreateModelAsset(model, path), ModelRole.Prop);
            var original = Light(); shell.Workspace.EditLighting(new(new([original]), original.Id));
            shell.ShowInspectorPage(EditorInspectorPage.Lighting); Pump(window);
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            inspector.FindControl<Expander>("LightPresetExpander")!.IsExpanded = true; Pump(window);
            var menu = (MenuFlyout)window.FindControl<Button>("AddBrowserLightButton")!.Flyout!;
            menu.Items.OfType<MenuItem>().Single(item => item.Name == "AddThreePointLightGroup").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump(window); Assert.Equal(4, shell.Workspace.Session!.Lighting.Lights.Count);
            Assert.Single(inspector.ViewModel!.SelectedGroupIds); Assert.Equal(3, inspector.ViewModel.SelectedLightIds.Count);
            Assert.Equal(3, inspector.ViewModel.SelectedRows.OfType<EditorLightGroupItem>().SelectMany(group => inspector.ViewModel.Lights.Where(light => light.GroupId == group.Id)).Count());
            Assert.Equal(inspector.ViewModel.SelectionSummary, window.FindControl<TextBlock>("SelectedLightName")!.Text);
            Assert.True(inspector.FindControl<Button>("ReplaceLightPresetButton")!.IsEnabled);
            Assert.True(inspector.FindControl<Button>("SaveLightPresetButton")!.IsEnabled);
            Assert.Contains(window.FindControl<ListBox>("WorkspaceLightList")!.SelectedItems!.Cast<object>(), row => row is EditorLightGroupItem);
            foreach (string language in new[] { "zh-CN", "en", "ja" })
            {
                EditorLocalization.SetLanguage(language); window.Width = 1100; window.Height = 720; Pump(window);
                Assert.Single(inspector.ViewModel!.SelectedGroupIds); Assert.Equal(3, inspector.ViewModel.SelectedLightIds.Count);
                var expander = inspector.FindControl<Expander>("LightPresetExpander")!;
                Assert.True(expander.IsEffectivelyVisible); Assert.True(expander.Bounds.Width > 0);
                foreach (string name in new[] { "ReplaceLightPresetButton", "LoadLightPresetButton", "SaveLightPresetButton" })
                {
                    var button = inspector.FindControl<Button>(name)!;
                    Assert.True(button.Bounds.Width > 0); Assert.True(button.Bounds.Width <= expander.Bounds.Width);
                    Assert.DoesNotContain("Mda.Text.", button.Content?.ToString() ?? "");
                }
                if (Environment.GetEnvironmentVariable("MDA_LIGHTING_PRESET_EVIDENCE") is { Length: > 0 } evidence)
                {
                    Directory.CreateDirectory(evidence); using var image = HeadlessTestHost.CaptureLayoutFrame(window); Assert.NotNull(image);
                    image.Save(Path.Combine(evidence, "presets-" + language + "-minimum.png"));
                    window.Width = 1480; window.Height = 940; Pump(window);
                    using var normal = HeadlessTestHost.CaptureLayoutFrame(window); Assert.NotNull(normal);
                    normal.Save(Path.Combine(evidence, "presets-" + language + "-normal.png"));
                }
            }
            // Capture the actual Add result above. History selection is a separate existing policy:
            // Undo removes the rig and Redo restores its content without inventing a selection.
            Assert.True(shell.Workspace.Undo()); Assert.Equal(original, Assert.Single(shell.Workspace.Session.Lighting.Lights));
            Assert.True(shell.Workspace.Redo()); Assert.Equal(4, shell.Workspace.Session.Lighting.Lights.Count); Pump(window);
            return true;
        }
        finally
        {
            EditorLocalization.SetLanguage("zh-CN");
            typeof(EditorWindow).GetField("_closingConfirmed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
            window.Close(); Dispatcher.UIThread.RunJobs();
        }
    }, TestContext.Current.CancellationToken);

    private static Window Host(EditorLightInspector inspector)
    {
        var window = new Window { Width = 450, Height = 900, Content = new ScrollViewer { Content = inspector } };
        window.Show(); Pump(window); return window;
    }
    private static void Pump(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
