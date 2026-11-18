using Avalonia.Controls;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.Formats;
using Xunit;
using System.Reflection;
using System.Text;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Physics;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorClassicEffectsMenuTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);

    [Theory]
    [InlineData("zh-CN", false)] [InlineData("en", true)] [InlineData("ja", false)]
    public async Task ClassicMenuIsLocalizedAndModernDoesNotExposeIt(string language, bool light)
        => await session.Dispatch(() =>
    {
        string before = EditorLocalization.CurrentLanguage;
        string directory = Path.Combine(Path.GetTempPath(), "mda-mme-menu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        EditorLocalization.SetLanguage(language);
        try
        {
            string[] names = ["Mmd.Modern.D3D9.Compatibility.dll", "d3dx9_43.dll", "d3dcompiler_43.dll"];
            foreach (string name in names) File.WriteAllBytes(Path.Combine(directory, name), [1]);
            var engine = new EditorClassicRenderEngine(new(Path.Combine(directory, names[0]),
                Path.Combine(directory, names[1]), Path.Combine(directory, names[2])), directory);
            using var classic = new EditorShellViewModel(new PmmCameraPose(new(0, 10, 0), default, -45, 30, true), engine);
            using var modern = new EditorShellViewModel();
            var window = new EditorWindow(false, new EditorPreferences { Language = language }) { RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
            try
            {
                window.DataContext = modern;
                Assert.Equal(language, EditorLocalization.CurrentLanguage);
                Assert.False(window.FindControl<MenuItem>("ClassicEffectsMenu")!.IsVisible);
                window.DataContext = classic;
                var menu = window.FindControl<MenuItem>("ClassicEffectsMenu")!;
                Assert.True(menu.IsVisible);
                Assert.Equal("MMEffect", menu.Header);
                var entries = menu.Items.OfType<MenuItem>().ToArray();
                string[] keys = ["Assignment", "Toon", "ToonSlots", "Enable", "Reload", "AutoReload", "AutoSave", "Log", "About"];
                Assert.Equal(keys.Select(key => EditorLocalization.Text("Mda.Text.Mme." + key)), entries.Select(item => item.Header as string));
                Assert.All(entries, item =>
                {
                    Assert.DoesNotContain("Mda.Text.", (string)item.Header!);
                    Assert.DoesNotContain('\uFFFD', (string)item.Header!);
                });
                Assert.True(window.FindControl<MenuItem>("ClassicEffectsEnabled")!.IsChecked);
                Assert.False(window.FindControl<MenuItem>("ClassicEffectsAutoReload")!.IsChecked);
                Assert.False(window.FindControl<MenuItem>("ClassicMappingAutoSave")!.IsChecked);
                Assert.False(window.FindControl<MenuItem>("ClassicEffectAssignment")!.IsEnabled);
                Assert.False(window.FindControl<MenuItem>("ClassicEffectsReload")!.IsEnabled);
                Assert.False(window.FindControl<MenuItem>("ClassicToonSettings")!.IsEnabled);
                Assert.False(window.FindControl<MenuItem>("ClassicToonSlots")!.IsEnabled);
                window.Show();
                var workArea = window.Screens.Primary!.WorkingArea;
                window.Position = new PixelPoint(workArea.Right - (int)(window.Bounds.Width * window.RenderScaling), workArea.Y);
                var menus = window.GetVisualDescendants().OfType<Menu>().SelectMany(bar => bar.Items.OfType<MenuItem>()).ToArray();
                Assert.Contains(menu, menus);
                foreach (var top in menus)
                    AssertMenuTextFits(top);
                window.DataContext = modern;
                Assert.False(menu.IsVisible);
            }
            finally
            {
                window.DataContext = null;
                window.FindControl<EditorViewportHost>("SceneViewport")!.Dispose();
                window.Close(); Dispatcher.UIThread.RunJobs();
            }
        }
        finally { EditorLocalization.SetLanguage(before); Directory.Delete(directory, true); }
        return true;
    }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("zh-CN", false)] [InlineData("en", true)] [InlineData("ja", false)]
    public async Task AssignmentDraftCancelPreservesProjectAndAbsentSubsetVisibilityIsInherited(string language, bool light)
        => await session.Dispatch(() =>
    {
        string before = EditorLocalization.CurrentLanguage;
        EditorLocalization.SetLanguage(language);
        string path = Path.Combine(Path.GetTempPath(), "mda-mme-draft-model.pmx");
        var model = EditorRecoveryPmaIntegrationTests.Model();
        var asset = EditorProjectWorkspace.CreateModelAsset(model, path, TestContext.Current.CancellationToken);
        var composition = new SceneCompositionEditor();
        Guid id = composition.Add("Actor", path, ModelRole.Character, asset.Source).Id;
        using var project = new SceneProjectSession(composition, new(),
            [new(id, model, new VmdMotion { ModelName = model.Name }, PhysicsEnabled: false)], new NoPhysics());
        var document = EmmReader.Read(Encoding.UTF8.GetBytes($"[Info]\nVersion=3\n[Object]\nPmd0={path}\n[Effect]\nDefault=none\nPmd0.show=false\n"));
        var plan = EmmImportPlan.Create(document, Path.Combine(Path.GetTempPath(), "mda-draft.emm"), [new(id, true, path, 0)]);
        var emm = D3D9EmmPma.Capture(plan, []);
        // Detached metadata avoids opening a native renderer or effect inspector.
        var constructor = typeof(EditorClassicDocumentState).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(value => value.GetParameters()[0].ParameterType == typeof(SceneProjectSession));
        var metadata = (EditorClassicDocumentState)constructor.Invoke([project, emm, null, null, null, null, false]);
        using var workspace = new EditorProjectWorkspace();
        typeof(EditorProjectWorkspace).GetProperty("Session")!.SetValue(workspace, project);
        typeof(EditorProjectWorkspace).GetProperty("ProjectMetadata", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(workspace, metadata);
        var owner = new Window { RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        owner.Show();
        var dialog = new EditorClassicEffectsWindow(owner, workspace) { Width = 680, Height = 460 };
        try
        {
            dialog.Show(owner); dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(owner.ActualThemeVariant, dialog.ActualThemeVariant);
            var lists = dialog.GetVisualDescendants().OfType<ListBox>().ToArray();
            var rows = lists.Single(list => list.Items.Count == model.Materials.Count + 1);
            string Value(object row, string property) => (string)row.GetType().GetProperty(property)!.GetValue(row)!;
            Assert.Equal(EditorLocalization.Text("Mda.Text.Mme.Hidden"), Value(rows.Items[0]!, "Visibility"));
            Assert.Equal(EditorLocalization.Text("Mda.Text.Mme.Inherited"), Value(rows.Items[1]!, "Visibility"));
            var buttons = dialog.GetVisualDescendants().OfType<Button>().ToArray();
            buttons.Single(button => Equals(button.Content, EditorLocalization.Text("Mda.Text.Mme.NoEffect")))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotEqual(document.CopySourceBytes(), dialog.Draft.CopySourceBytes());
            Assert.Same(metadata, workspace.ProjectMetadata);
            Assert.Equal(document.CopySourceBytes(), emm.Document.CopySourceBytes());
            Assert.False(workspace.IsDirty); Assert.False(workspace.CanUndo);
            using var frame = dialog.CaptureRenderedFrame(); Assert.NotNull(frame);
            if (Environment.GetEnvironmentVariable("MDA_TEST_SCREENSHOT_DIRECTORY") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                frame.Save(Path.Combine(directory, $"mme-assignment-minimum-{language}-{(light ? "light" : "dark")}.png"));
            }
            buttons.Single(button => Equals(button.Content, EditorLocalization.Text("Mda.Text.Mme.Cancel")))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(metadata, workspace.ProjectMetadata);
            Assert.Equal(document.CopySourceBytes(), emm.Document.CopySourceBytes());
        }
        finally { dialog.Close(); owner.Close(); EditorLocalization.SetLanguage(before); Dispatcher.UIThread.RunJobs(); }
        return true;
    }, TestContext.Current.CancellationToken);

    private static void AssertMenuTextFits(MenuItem top)
    {
        top.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();
        var popup = top.GetVisualDescendants().OfType<Popup>().First();
        Assert.True(popup.IsOpen);
        var viewport = popup.Child!.GetVisualDescendants().OfType<ScrollViewer>().First();
        foreach (var entry in top.Items.OfType<MenuItem>())
        {
            var labels = entry.GetVisualDescendants().OfType<TextBlock>()
                .Where(label => !string.IsNullOrEmpty(label.Text)).ToArray();
            Assert.NotEmpty(labels);
            foreach (var label in labels)
            {
                var origin = label.TranslatePoint(default, viewport)!.Value;
                var width = label.TextLayout.Width;
                Assert.True(origin.X >= -1 && origin.X + width <= viewport.Bounds.Width + 1,
                    $"{label.Text}: x={origin.X}, width={width}, menu={viewport.Bounds.Width}");
            }
        }
        top.IsSubMenuOpen = false;
    }

    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject actor) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException();
    }

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
