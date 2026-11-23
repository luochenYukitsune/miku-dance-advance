using System.Numerics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorLightingWorkspaceTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);

    [Theory]
    [InlineData(0x51, Key.Q)]
    [InlineData(0x57, Key.W)]
    [InlineData(0x45, Key.E)]
    public void NativeViewportForwardsTransformModeKeys(int virtualKey, Key expected)
    {
        Assert.True(EditorViewportHost.TryMapEditorKey(virtualKey, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task OutlineFiltersAreIndependentOfTheLightingInspector() => await session.Dispatch(() =>
    {
        var (window, shell) = Editor();
        try
        {
            shell.ShowInspectorPage(EditorInspectorPage.Lighting);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<TabControl>("InspectorPageTabs")!.IsEffectivelyVisible);
            var filter = window.FindControl<ComboBox>("OutlineFilter")!;
            Assert.Equal(0, filter.SelectedIndex);
            Assert.True(window.FindControl<ListBox>("WorkspaceLightList")!.IsEffectivelyVisible);
            filter.SelectedIndex = 1;
            Assert.Equal(EditorInspectorPage.Lighting, shell.InspectorPage);
            Assert.True(window.FindControl<EditorModelBrowser>("ModelBrowserSurface")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<ListBox>("WorkspaceLightList")!.IsEffectivelyVisible);
            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task LightingTabHidesBoneGeometryAndPickingAndPreservesThePoseDraft() => await session.Dispatch(() =>
    {
        var (window, shell) = Editor();
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd");
            using var input = File.OpenRead(path);
            var asset = EditorProjectWorkspace.CreateModelAsset(PmdPmxAdapter.Convert(PmdReader.Read(input)), path);
            var objectId = shell.Workspace.AddModel(path, asset, ModelRole.Character);
            shell.RefreshWorkspace(objectId);
            shell.Browser.SelectedBoneIndex = 0;
            shell.Animation.PositionX = 2.5m;
            var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
            var bones = viewport.BoneController!;
            bones.ShowBones = true;
            bones.ShowSelectionBounds = true;
            bones.IsManipulationEnabled = true;
            bones.EditingSpace = BoneEditingSpace.Global;
            var requests = bones.SnapshotRequests([]);
            Assert.NotEmpty(requests);
            var snapshot = shell.Workspace.Session!.CaptureRenderSnapshot(requests);
            var camera = viewport.CurrentCamera;
            var original = new PreviewFrame(800, 600, snapshot.SourceFrame, snapshot.Scene, snapshot.Camera,
                snapshot.Light, snapshot.SelfShadow) { ResolvedCamera = camera, SceneInputs = snapshot.SceneInputs };
            var withBones = viewport.FrameDecorator!(original);
            var center = withBones.Scene.EditorOverlay!.Lines.First(line =>
                (line.Shape is ViewportOverlayShape.Circle or ViewportOverlayShape.Square) && line.Color.X > .9f);
            int x = (int)Math.Round(center.Start.X * 800), y = (int)Math.Round(center.Start.Y * 600);
            bones.FramePresented(withBones);
            var lateAnimationFrame = viewport.FrameDecorator(original);
            var tabs = window.FindControl<TabControl>("InspectorPageTabs")!;
            tabs.SelectedIndex = (int)EditorInspectorPage.Lighting;
            Assert.True(shell.IsLightingPage);
            Assert.Empty(bones.SnapshotRequests([]));
            Assert.Empty(viewport.FrameDecorator(original).Scene.EditorOverlay?.Lines ?? []);
            Assert.False(bones.TryBeginDrag(x, y, 800, 600, camera));

            tabs.SelectedIndex = (int)EditorInspectorPage.Style;
            Assert.True(shell.IsStylePage);
            bones.FramePresented(lateAnimationFrame);
            Assert.False(bones.TryBeginDrag(x, y, 800, 600, camera));
            Assert.True(bones.ShowBones && bones.ShowSelectionBounds && bones.IsManipulationEnabled);
            Assert.Equal(BoneEditingSpace.Global, bones.EditingSpace);
            Assert.Equal(0, shell.Browser.SelectedBoneIndex);
            Assert.Equal(2.5m, shell.Animation.PositionX);
            Assert.True(shell.Animation.HasDraft);
            Assert.NotEmpty(bones.SnapshotRequests([]));
            var restored = viewport.FrameDecorator(original);
            Assert.Equal(withBones.Scene.EditorOverlay.Lines, restored.Scene.EditorOverlay!.Lines.ToArray());
            bones.FramePresented(restored);
            Assert.True(bones.TryBeginDrag(x, y, 800, 600, camera));
            bones.EndDrag();
            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task MainLightSectionControlsViewportEditingAndSharesWorkspaceHistory() => await session.Dispatch(() =>
    {
        var (window, shell) = Editor();
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd");
            using var input = File.OpenRead(path);
            var asset = EditorProjectWorkspace.CreateModelAsset(PmdPmxAdapter.Convert(PmdReader.Read(input)), path);
            shell.Workspace.AddModel(path, asset, ModelRole.Character);
            shell.ShowInspectorPage(EditorInspectorPage.Lighting);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
            var section = window.FindControl<Expander>("MainLightSection")!;
            Assert.True(section.IsExpanded);
            Assert.NotNull(viewport.MainLightViewport);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var controller = Assert.IsType<EditorMainLightViewport>(viewport.MainLightViewport);
            var snapshot = shell.Workspace.Session!.CaptureRenderSnapshot();
            var camera = viewport.CurrentCamera;
            var image = controller.Decorate(new(800, 600, snapshot.SourceFrame, snapshot.Scene,
                snapshot.Camera, snapshot.Light, snapshot.SelfShadow), camera);
            var source = image.Scene.EditorOverlay!.Lines.Last(line => line.Shape == ViewportOverlayShape.Circle).Start * new Vector2(800, 600);
            controller.FramePresented(image);
            Assert.True(controller.TryBegin(source, camera, 800, 600));
            controller.End(source + new Vector2(-28, 17));
            var direction = shell.SceneInspector.LightPose.Direction;
            Assert.True(shell.SceneInspector.HasLightDraft);
            shell.SceneInspector.RegisterLightKeyframeCommand.Execute(null);
            Assert.False(shell.SceneInspector.HasLightDraft);
            Assert.True(shell.Workspace.Undo());
            Assert.NotEqual(direction, shell.SceneInspector.LightPose.Direction);
            Assert.True(shell.Workspace.Redo());
            Assert.Equal(direction, shell.SceneInspector.LightPose.Direction);
            // Collapsing parameters or returning to a style preset keeps the lamp draggable.
            shell.ShowInspectorPage(EditorInspectorPage.Style);
            Assert.NotNull(viewport.MainLightViewport);
            var helpers = window.FindControl<Avalonia.Controls.Primitives.ToggleButton>("LightHelpersToggle")!;
            helpers.IsChecked = false;
            helpers.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Null(viewport.MainLightViewport);
            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task SearchingAndSelectingALightSharesInspectorDraftWithoutEditingHistory() => await session.Dispatch(() =>
    {
        var (window, shell) = Editor();
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd");
            using var input = File.OpenRead(path);
            var asset = EditorProjectWorkspace.CreateModelAsset(PmdPmxAdapter.Convert(PmdReader.Read(input)), path);
            shell.Workspace.AddModel(path, asset, ModelRole.Character);
            var key = new NativeSceneLight(Guid.NewGuid(), "Face fill", SceneLightKind.Spot, Vector3.One, Quaternion.Identity, Vector3.One);
            var rim = key with { Id = Guid.NewGuid(), Name = "Back rim" };
            shell.Workspace.EditLighting(new(new SceneLightingSnapshot([key, rim])));
            shell.ShowInspectorPage(EditorInspectorPage.Lighting);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            var list = window.FindControl<ListBox>("WorkspaceLightList");
            Assert.NotNull(list); Assert.Equal(2, list.ItemCount);
            CaptureLayout(window, "lighting-workspace");
            var before = shell.Workspace.Session!.Lighting;
            list.SelectedItem = inspector.ViewModel!.Lights.Single(light => light.Id == rim.Id);
            Assert.Equal(rim.Id, inspector.ViewModel.SelectedLight!.Id);
            var search = window.FindControl<TextBox>("LightSearchInput");
            Assert.NotNull(search); search.Text = "RIM";
            Assert.Equal(1, list.ItemCount);
            Assert.Equal(rim.Id, Assert.IsType<EditorNativeLightItem>(list.Items[0]).Id);
            Assert.Same(before, shell.Workspace.Session.Lighting);
            Assert.True(shell.Workspace.Undo()); // Selection/search added no transaction after the lighting edit.
            Assert.Empty(shell.Workspace.Session.Lighting.Lights);
            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task OutlineChoosesTheClickedLampAndViewportSelectionKeepsTheStylePage() => await session.Dispatch(() =>
    {
        var (window, shell) = Editor();
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd");
            using var input = File.OpenRead(path);
            var model = PmdPmxAdapter.Convert(PmdReader.Read(input));
            shell.Workspace.AddModel(path, EditorProjectWorkspace.CreateModelAsset(model, path), ModelRole.Character);
            var a = new NativeSceneLight(Guid.NewGuid(), "First", SceneLightKind.Point, Vector3.Zero, Quaternion.Identity, Vector3.One);
            var b = a with { Id = Guid.NewGuid(), Name = "Second" };
            shell.Workspace.EditLighting(new(new([a, b])));
            var view = window.FindControl<EditorViewportHost>("SceneViewport")!;
            var list = window.FindControl<ListBox>("WorkspaceLightList")!;
            Assert.True(shell.IsStylePage);
            list.SelectedItem = list.Items.OfType<EditorNativeLightItem>().Single(light => light.Id == b.Id);
            Assert.Equal(b.Id, view.LightController!.SelectedLightId);
            Assert.True(shell.IsLightingPage);
            shell.ShowInspectorPage(EditorInspectorPage.Style);
            view.LightController.Select(a.Id);
            Assert.True(shell.IsStylePage);
            var toggle = window.FindControl<Avalonia.Controls.Primitives.ToggleButton>("ViewportLightEnabled")!;
            toggle.IsChecked = false;
            toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.False(shell.Workspace.Session!.Lighting.Lights.Single(light => light.Id == a.Id).Enabled);
            Assert.True(shell.Workspace.Session.Lighting.Lights.Single(light => light.Id == b.Id).Enabled);
            Assert.True(shell.Workspace.Undo());
            Assert.True(shell.Workspace.Session.Lighting.Lights.Single(light => light.Id == a.Id).Enabled);
            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task AdditionalLightHistoryRetainsRendererBindingButModelPurposeStillRebinds() => await session.Dispatch(() =>
    {
        var (window, shell) = Editor();
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd");
            using var input = File.OpenRead(path);
            var asset = EditorProjectWorkspace.CreateModelAsset(PmdPmxAdapter.Convert(PmdReader.Read(input)), path);
            var id = shell.Workspace.AddModel(path, asset, ModelRole.Character);
            var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
            var bindingField = typeof(EditorViewportHost).GetField("renderBinding", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var revisionField = typeof(EditorViewportHost).GetField("revision", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var binding = bindingField.GetValue(viewport);
            var revision = revisionField.GetValue(viewport);
            long contentRevision = shell.ContentRevision;
            var light = new NativeSceneLight(Guid.NewGuid(), "fill", SceneLightKind.Point, Vector3.One, Quaternion.Identity, Vector3.One);
            shell.Workspace.EditLighting(new(new SceneLightingSnapshot([light])));
            Assert.True(shell.ContentRevision > contentRevision);
            Assert.Same(binding, bindingField.GetValue(viewport));
            Assert.Equal(revision, revisionField.GetValue(viewport));
            Assert.Equal(light, Assert.Single(shell.Workspace.Session!.CaptureRenderSnapshot().Scene.Lighting!.Lights));
            Assert.True(shell.Workspace.Undo());
            Assert.Same(binding, bindingField.GetValue(viewport));
            Assert.Empty(shell.Workspace.Session.CaptureRenderSnapshot().Scene.Lighting!.Lights);
            Assert.True(shell.Workspace.Redo());
            Assert.Same(binding, bindingField.GetValue(viewport));
            Assert.Equal(revision, revisionField.GetValue(viewport));
            shell.Workspace.Edit(edit => edit.Lighting.SetModelRole("scene-object:" + id.ToString("D"), ModelRole.Environment));
            Assert.NotSame(binding, bindingField.GetValue(viewport));
            Assert.NotEqual(revision, revisionField.GetValue(viewport));
            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ExplicitViewportSelectionRevealsALightOutsideTheSearchFilter() => await session.Dispatch(() =>
    {
        var (window, shell) = Editor();
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd");
            using var input = File.OpenRead(path);
            var asset = EditorProjectWorkspace.CreateModelAsset(PmdPmxAdapter.Convert(PmdReader.Read(input)), path);
            shell.Workspace.AddModel(path, asset, ModelRole.Character);
            var key = new NativeSceneLight(Guid.NewGuid(), "Face fill", SceneLightKind.Point, Vector3.One, Quaternion.Identity, Vector3.One);
            var rim = key with { Id = Guid.NewGuid(), Name = "Back rim" };
            shell.Workspace.EditLighting(new(new SceneLightingSnapshot([key, rim])));
            shell.ShowInspectorPage(EditorInspectorPage.Lighting);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            var list = window.FindControl<ListBox>("WorkspaceLightList")!;
            list.SelectedItem = inspector.ViewModel!.Lights.Single(light => light.Id == rim.Id);
            window.FindControl<TextBox>("LightSearchInput")!.Text = "RIM";
            var controller = (EditorLightViewportController)typeof(EditorWindow).GetField("lightController", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            controller.Select(key.Id); // Same explicit selection path as a viewport pick; no GPU is created.
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(key.Id, inspector.ViewModel!.SelectedLight?.Id);
            Assert.Equal(key.Id, Assert.IsType<EditorNativeLightItem>(list.SelectedItem).Id);
            Assert.Contains(list.Items.Cast<EditorNativeLightItem>(), light => light.Id == key.Id);
            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ModernLightUndoShortcutsPreservePendingInputWithoutCommittingOrBlockingHistory() => await session.Dispatch(() =>
    {
        var (window, shell) = Editor();
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd");
            using var input = File.OpenRead(path);
            var asset = EditorProjectWorkspace.CreateModelAsset(PmdPmxAdapter.Convert(PmdReader.Read(input)), path);
            shell.Workspace.AddModel(path, asset, ModelRole.Character);
            var light = new NativeSceneLight(Guid.NewGuid(), "fill", SceneLightKind.Point, Vector3.One, Quaternion.Identity, Vector3.One);
            shell.Workspace.EditLighting(new(new SceneLightingSnapshot([light])));
            shell.Workspace.EditLighting(new(new SceneLightingSnapshot([light with { Intensity = 2 }])));
            shell.ShowInspectorPage(EditorInspectorPage.Lighting);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            inspector.ViewModel!.SelectedLight!.Intensity = 3m; // Valid but unfinished numeric input.
            var shortcut = typeof(EditorWindow).GetMethod("ExecuteEditorKey", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.True((bool)shortcut.Invoke(window, [Key.Z, KeyModifiers.Control, true])!);
            Assert.Equal(1f, Assert.Single(shell.Workspace.Session!.Lighting.Lights).Intensity);
            Assert.Equal(3m, inspector.ViewModel!.SelectedLight!.Intensity);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var hex = inspector.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "LightHexColor");
            hex.Text = "#12";
            Assert.True((bool)shortcut.Invoke(window, [Key.Y, KeyModifiers.Control, true])!);
            Assert.Equal(2f, Assert.Single(shell.Workspace.Session.Lighting.Lights).Intensity);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal("#12", inspector.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "LightHexColor").Text);
            Assert.False(inspector.TryCommit());
            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    private static (EditorWindow, EditorShellViewModel) Editor()
    {
        var window = new EditorWindow(false);
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.IsSuspended = true; viewport.IsVisible = false;
        var shell = new EditorShellViewModel(); window.DataContext = shell; window.Show();
        return (window, shell);
    }
    private static void Close(EditorWindow window)
    {
        typeof(EditorWindow).GetField("_closingConfirmed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
        window.Close(); Dispatcher.UIThread.RunJobs();
    }
    private static void CaptureLayout(EditorWindow window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("MDA_LIGHTING_LAYOUT_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        // CPU Skia checks the real Avalonia layout; the native GPU viewport stays suspended.
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        using var frame = HeadlessTestHost.CaptureLayoutFrame(window);
        Assert.NotNull(frame); frame.Save(Path.Combine(directory, name + ".png"));
        double width = window.Width, height = window.Height;
        try
        {
            window.Width = 1100; window.Height = 720;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            using var minimum = HeadlessTestHost.CaptureLayoutFrame(window);
            Assert.NotNull(minimum); minimum.Save(Path.Combine(directory, name + "-minimum.png"));
        }
        finally { window.Width = width; window.Height = height; window.UpdateLayout(); }
    }
    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
