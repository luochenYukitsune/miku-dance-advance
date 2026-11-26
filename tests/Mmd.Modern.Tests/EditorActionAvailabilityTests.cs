using System.Numerics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorActionAvailabilityTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);

    [Fact]
    public async Task FileModelAndSceneActionsFollowTheDocumentSelectionAndBusyState() => await session.Dispatch(async () =>
    {
        using var shell = new EditorShellViewModel();
        var window = ShowEditor(shell);
        try
        {
            var actions = MenuActions(window);
            // Import pose, export motion, export pose, model properties, rig settings, save PMM.
            var modelActions = new[] { "ae7f218be0f9", "23d7044e6320", "409eb92cb153", "814c34f6f2d1", "df725de98db0", "9837932b374e" }
                .Select(key => actions.Single(item => Equals(item.Header, EditorLocalization.Text("Mda.Text." + key)))).ToArray();
            Assert.All(modelActions, item => Assert.False(item.IsEffectivelyEnabled));
            Assert.False(shell.SceneInspector.CanRegisterLight);
            Assert.False(shell.SceneInspector.CanRegisterCamera);
            AddModel(shell);
            Assert.All(modelActions, item => Assert.True(item.IsEffectivelyEnabled));
            Assert.True(shell.SceneInspector.CanRegisterLight);
            Assert.True(shell.SceneInspector.CanRegisterCamera);
            var settings = window.FindControl<Button>("SettingsButton")!;
            var output = window.FindControl<Button>("OutputSettingsButton")!;
            await shell.RunOperationAsync("test", () =>
            {
                Assert.False(settings.IsEffectivelyEnabled);
                Assert.False(output.IsEffectivelyEnabled);
                Assert.False(shell.SceneInspector.RegisterCameraKeyframeCommand.CanExecute(null));
                Assert.False(shell.SceneInspector.RegisterLightKeyframeCommand.CanExecute(null));
                return Task.CompletedTask;
            });
            Assert.True(settings.IsEffectivelyEnabled);
            Assert.True(output.IsEffectivelyEnabled);
        }
        finally { CloseEditor(window); }
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task OutlineAndInspectorTabsSwitchLightCameraAndAnimationWithoutABackButton() => await session.Dispatch(() =>
    {
        using var shell = new EditorShellViewModel();
        var window = ShowEditor(shell);
        try
        {
            AddModel(shell);
            var outline = window.FindControl<ListBox>("SceneObjects")!;
            var tabs = window.FindControl<TabControl>("InspectorPageTabs")!;
            var title = Assert.IsType<Grid>(window.FindControl<TextBlock>("InspectorTitle")!.Parent);
            var actions = title.Children.OfType<Button>().ToArray();
            Assert.Collection(actions, revert => Assert.Same(shell.SceneInspector.RevertDraftCommand, revert.Command),
                register => Assert.Same(shell.SceneInspector.RegisterCameraKeyframeCommand, register.Command));
            Assert.Equal(3, title.ColumnDefinitions.Count);

            outline.SelectedItem = window.FindControl<ListBoxItem>("OutlineTrackLight");
            Flush(window);
            Assert.True(shell.IsLightingPage && shell.IsLightInspector);
            Assert.All(actions, item => Assert.False(item.IsEffectivelyVisible));

            tabs.SelectedIndex = (int)EditorInspectorPage.Style;
            Flush(window);
            Assert.True(shell.IsAnimationInspector);
            Assert.True(window.FindControl<EditorAnimationInspector>("AnimationInspector")!.IsEffectivelyVisible);

            outline.SelectedItem = window.FindControl<ListBoxItem>("OutlineCamera");
            shell.CurrentFrame = 9;
            Flush(window);
            Assert.True(shell.IsCameraInspector);
            Assert.All(actions, item => Assert.True(item.IsEffectivelyVisible));
            var curveMenu = MenuActions(window).Single(item => Equals(item.Header, EditorLocalization.Text("Mda.Text.d20383c88bb0")));
            var curveButton = Assert.Single(window.FindControl<Grid>("CurveDockPanel")!.GetVisualDescendants().OfType<Button>(),
                button => Equals(button.Content, "…"));
            Assert.False(curveMenu.IsEffectivelyEnabled);
            Assert.False(curveButton.IsEffectivelyEnabled);
            shell.SceneInspector.RegisterCameraKeyframeCommand.Execute(null);
            Flush(window);
            Assert.True(curveButton.IsEffectivelyEnabled);
            Assert.True(curveMenu.IsEffectivelyEnabled);
            window.FindControl<MenuItem>("ShowLeftRailMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Flush(window);
            Assert.False(window.FindControl<Grid>("AnimationDock")!.IsVisible);
            Assert.True(curveMenu.IsEffectivelyEnabled);
            outline.SelectedItem = window.FindControl<ListBoxItem>("OutlineTrackLight");
            Flush(window);
            Assert.True(shell.IsLightingPage && shell.IsLightInspector);
            Assert.False(curveMenu.IsEffectivelyEnabled);
        }
        finally { CloseEditor(window); }
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task TransportAvailabilityIncludesScrollOffsetAndAuthoredEndFrame() => await session.Dispatch(() =>
    {
        using var shell = new EditorShellViewModel();
        var window = ShowEditor(shell);
        try
        {
            var previous = window.FindControl<Button>("PreviousFrameButton")!;
            var start = window.FindControl<Button>("GoToStartButton")!;
            Assert.False(previous.IsEffectivelyEnabled || start.IsEffectivelyEnabled);
            shell.FirstVisibleFrame = 24;
            Assert.True(start.IsEffectivelyEnabled);
            start.Command!.Execute(null);
            Assert.Equal(0d, shell.FirstVisibleFrame);
            Assert.False(start.IsEffectivelyEnabled);
            shell.CurrentFrame = int.MaxValue;
            Assert.False(window.FindControl<Button>("NextFrameButton")!.IsEffectivelyEnabled);
            Assert.True(previous.IsEffectivelyEnabled);
            shell.CurrentFrame = 0;
            var actor = AddModel(shell);
            shell.Workspace.CommitMotion(actor, new VmdMotion { BoneKeyframes = [new() { BoneName = "root", Frame = 12 }] });
            var end = window.FindControl<Button>("GoToEndButton")!;
            Assert.True(end.IsEffectivelyEnabled);
            end.Command!.Execute(null);
            Assert.Equal(12, shell.CurrentFrame);
            Assert.False(end.IsEffectivelyEnabled);
        }
        finally { CloseEditor(window); }
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task NativeShortcutsIgnoreStaleTextFocusAndOrdinaryTextKeysStayInTheInput() => await session.Dispatch(() =>
    {
        using var shell = new EditorShellViewModel();
        var window = ShowEditor(shell);
        try
        {
            var actor = AddModel(shell);
            shell.Workspace.CommitMotion(actor, new VmdMotion { BoneKeyframes = [new() { BoneName = "root", Frame = 12 }] });
            shell.Browser.SelectedBoneIndex = 0;
            shell.Animation.SelectTimelineKeys([KeyframeAddress.Bone("root", 12)]);
            shell.CurrentFrame = 0;
            Flush(window);
            Assert.True(window.FindControl<TextBox>("TimelineRangeStartInput")!.Focus());
            bool Press(Key key, bool native, KeyModifiers modifiers = KeyModifiers.None) =>
                PressEditorKey(window, key, native, modifiers);

            Assert.False(Press(Key.Right, false));
            Assert.False(Press(Key.Space, false));
            Assert.False(Press(Key.C, false, KeyModifiers.Control));
            Assert.False(Press(Key.Delete, false));
            Assert.True(Press(Key.Right, true));
            Assert.Equal(1, shell.CurrentFrame);
            Assert.True(Press(Key.Space, true));
            Assert.True(shell.IsPlaying);
            Assert.True(Press(Key.Space, true));
            Assert.False(shell.IsPlaying);
            Assert.True(Press(Key.C, true, KeyModifiers.Control));
            Assert.True(Press(Key.V, true, KeyModifiers.Control));
            Assert.Contains(shell.Workspace.Motions.Single().Motion.BoneKeyframes, key => key.Frame == 1);
            Assert.True(Press(Key.Delete, true));
            Assert.DoesNotContain(shell.Workspace.Motions.Single().Motion.BoneKeyframes, key => key.Frame == 1);
        }
        finally { CloseEditor(window); }
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ViewportMenuAndDeleteFollowPickedLightGroupCameraAndBone() => await session.Dispatch(() =>
    {
        using var shell = new EditorShellViewModel();
        var window = ShowEditor(shell);
        try
        {
            AddModel(shell);
            var light = new NativeSceneLight(Guid.NewGuid(), "fill", SceneLightKind.Point, Vector3.One, Quaternion.Identity, Vector3.One);
            var group = new SceneLightGroup(Guid.NewGuid(), "empty");
            shell.Workspace.EditLighting(new(new([light], [group])));
            var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!.LightController!;
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            var tabs = window.FindControl<TabControl>("InspectorPageTabs")!;
            bool HasItem(string key) => window.ViewportMenuItems().OfType<MenuItem>()
                .Any(item => Equals(item.Header, EditorLocalization.Text(key)));
            const string deleteLight = "Mda.Text.2f9daa828907";

            tabs.SelectedIndex = (int)EditorInspectorPage.Style;
            viewport.Select(light.Id);
            Flush(window);
            Assert.True(shell.IsStylePage);
            Assert.True(HasItem(deleteLight));
            window.FindControl<ListBox>("SceneObjects")!.SelectedItem = window.FindControl<ListBoxItem>("OutlineCamera");
            Flush(window);
            Assert.True(HasItem("Mda.Text.a619bd96a115"));
            Assert.False(HasItem(deleteLight));
            Assert.False(PressEditorKey(window, Key.Delete, true));
            Assert.Equal(light.Id, Assert.Single(shell.Workspace.Session!.Lighting.Lights).Id);

            viewport.Select(light.Id);
            Assert.True(HasItem(deleteLight));
            Assert.True(PressEditorKey(window, Key.Delete, true));
            Assert.Empty(shell.Workspace.Session.Lighting.Lights);
            Assert.True(shell.Workspace.Undo());

            tabs.SelectedIndex = (int)EditorInspectorPage.Lighting;
            Flush(window);
            window.FindControl<ListBox>("WorkspaceLightList")!.SelectedItem = Assert.Single(inspector.ViewModel!.Groups);
            Flush(window);
            Assert.Equal(group.Id, Assert.Single(inspector.ViewModel.SelectedGroupIds));
            Assert.Empty(viewport.SelectedLightIds);
            Assert.True(HasItem(deleteLight));
            var delete = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete };
            window.FindControl<EditorViewportHost>("SceneViewport")!.RaiseEvent(delete);
            Assert.True(delete.Handled);
            Assert.Empty(shell.Workspace.Session.Lighting.Groups);

            viewport.Select(light.Id);
            tabs.SelectedIndex = (int)EditorInspectorPage.Animation;
            shell.Browser.SelectedBoneIndex = 0;
            window.FindControl<Button>("BoneMoveMode")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Flush(window);
            Assert.Equal(light.Id, viewport.SelectedLightId);
            Assert.True(HasItem("Mda.Text.Menu.ResetBonePose"));
            Assert.False(HasItem(deleteLight));
            Assert.False(PressEditorKey(window, Key.Delete, true));
            Assert.Equal(light.Id, Assert.Single(shell.Workspace.Session.Lighting.Lights).Id);
        }
        finally { CloseEditor(window); }
        return true;
    }, TestContext.Current.CancellationToken);

    private static bool PressEditorKey(EditorWindow window, Key key, bool native, KeyModifiers modifiers = KeyModifiers.None) =>
        (bool)typeof(EditorWindow).GetMethod("ExecuteEditorKey", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, [key, modifiers, native])!;

    private static MenuItem[] MenuActions(EditorWindow window) => window.GetVisualDescendants().OfType<Menu>()
        .SelectMany(menu => menu.Items.OfType<MenuItem>()).SelectMany(top => top.Items.OfType<MenuItem>()).ToArray();

    private static Guid AddModel(EditorShellViewModel shell)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "menu-test.pmx");
        return shell.Workspace.AddModel(path, EditorProjectWorkspace.CreateModelAsset(MorphApplicationTests.Model([]), path), ModelRole.Character);
    }

    private static EditorWindow ShowEditor(EditorShellViewModel shell)
    {
        var window = new EditorWindow(false) { Width = 1480, Height = 940 };
        window.FindControl<EditorViewportHost>("SceneViewport")!.IsVisible = false;
        window.DataContext = shell;
        window.Show(); Flush(window);
        return window;
    }

    private static void Flush(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void CloseEditor(EditorWindow window) { window.DataContext = null; window.Close(); Dispatcher.UIThread.RunJobs(); }
    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
