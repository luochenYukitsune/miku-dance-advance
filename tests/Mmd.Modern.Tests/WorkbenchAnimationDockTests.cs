using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class WorkbenchAnimationDockTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FullHeightTimelineCompactTabsAndOutlineFiltersWorkIndependently() => await session.Dispatch(() =>
    {
        var (window, shell, _) = Editor();
        try
        {
            var timeline = window.FindControl<Border>("TimelinePanel")!;
            var view = window.FindControl<Grid>("WorkbenchGrid")!;
            var dock = window.FindControl<Grid>("AnimationDock")!;
            Assert.InRange(timeline.Bounds.Width, 300, 340);
            Assert.Equal(view.Bounds.Height, timeline.Bounds.Height, 1);
            Assert.InRange(dock.Bounds.Height, 150, 170);
            Assert.True(window.FindControl<Grid>("CurveDockPanel")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<EditorMorphDock>("MorphDockPanel")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<Grid>("StandardSelectionInspector")!.IsEffectivelyVisible);
            int page = shell.RightInspectorPageIndex;
            var filter = window.FindControl<ComboBox>("OutlineFilter")!;
            filter.SelectedIndex = 2;
            Assert.False(window.FindControl<EditorModelBrowser>("ModelBrowserSurface")!.IsVisible);
            Assert.False(window.FindControl<ListBoxItem>("OutlineCamera")!.IsVisible);
            Assert.True(window.FindControl<ListBoxItem>("OutlineTrackLight")!.IsEffectivelyVisible);
            filter.SelectedIndex = 1;
            Assert.True(window.FindControl<EditorModelBrowser>("ModelBrowserSurface")!.IsVisible);
            Assert.False(window.FindControl<ListBox>("SceneObjects")!.IsVisible);
            Assert.Equal(page, shell.RightInspectorPageIndex);

            Click(window, window.FindControl<ToggleButton>("OutlineToggle")!);
            Assert.False(window.FindControl<ScrollViewer>("OutlineScroll")!.IsVisible);
            Click(window, window.FindControl<ToggleButton>("OutlineToggle")!);
            Assert.True(window.FindControl<ScrollViewer>("OutlineScroll")!.IsVisible);
            Click(window, window.FindControl<ToggleButton>("AnimationDockToggle")!);
            Assert.False(dock.IsVisible);
            Assert.True(window.FindControl<Button>("PlayAnimationButton")!.IsEffectivelyVisible);
            Click(window, window.FindControl<ToggleButton>("AnimationDockToggle")!);

            window.Width = 1100;
            Flush(window);
            var tabs = window.FindControl<TabControl>("AnimationDockTabs")!;
            Assert.True(tabs.IsEffectivelyVisible);
            tabs.SelectedIndex = 1;
            Assert.True(window.FindControl<EditorMorphDock>("MorphDockPanel")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Grid>("CurveDockPanel")!.IsVisible);
            shell.SceneSelectionIndex = 1; Flush(window);
            Assert.False(window.FindControl<TabItem>("MorphDockTab")!.IsVisible);
            Assert.True(window.FindControl<Grid>("StandardSelectionInspector")!.IsEffectivelyVisible);
        }
        finally { Close(window, shell); }
        return true;
    }, Token);

    [Theory]
    [InlineData("zh-CN")] [InlineData("en")] [InlineData("ja")]
    public async Task MorphSelectorsAndRegistrationFitBothDockLayouts(string language) => await session.Dispatch(() =>
    {
        var (window, shell, _) = Editor();
        string previous = EditorLocalization.CurrentLanguage;
        try
        {
            EditorLocalization.SetLanguage(language);
            shell.Animation.SelectTimelineKeys([KeyframeAddress.Bone("root", 3)]);
            var dock = window.FindControl<EditorMorphDock>("MorphDockPanel")!;
            foreach (int width in new[] { 1480, 1100 })
            {
                window.Width = width; window.Height = width == 1480 ? 940 : 720; Flush(window);
                if (width == 1100) window.FindControl<TabControl>("AnimationDockTabs")!.SelectedIndex = 1;
                Flush(window);
                Assert.Equal(width, window.ClientSize.Width);
                var rail = window.FindControl<Border>("RightRailPanel")!;
                var railEdge = rail.TranslatePoint(new Point(rail.Bounds.Width, rail.Bounds.Height), window)!.Value;
                var play = window.FindControl<Button>("PlayAnimationButton")!;
                var playEdge = play.TranslatePoint(new Point(play.Bounds.Width, play.Bounds.Height), window)!.Value;
                Assert.InRange(railEdge.X, window.ClientSize.Width - 1, window.ClientSize.Width + 1);
                Assert.InRange(railEdge.Y, 0, window.ClientSize.Height + 1);
                Assert.InRange(playEdge.Y, 0, window.ClientSize.Height + 1);
                var add = window.FindControl<Button>("AddViewportLightButton")!;
                var guides = window.FindControl<ToggleButton>("WorldGuidesToggle")!;
                var addBounds = new Rect(add.TranslatePoint(default, window)!.Value, add.Bounds.Size);
                var guideBounds = new Rect(guides.TranslatePoint(default, window)!.Value, guides.Bounds.Size);
                Assert.False(addBounds.Intersects(guideBounds));
                var combos = dock.GetVisualDescendants().OfType<ComboBox>().ToArray();
                Assert.Equal(4, combos.Length);
                foreach (var selector in combos)
                {
                    var button = dock.GetVisualDescendants().OfType<Button>().Single(item =>
                        item.DataContext == selector.DataContext && item.Classes.Contains("action"));
                    Assert.True(button.IsEffectivelyVisible);
                    Assert.True(selector.Bounds.Width > 35);
                    var top = selector.TranslatePoint(default, dock)!.Value;
                    var bottom = button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), dock)!.Value;
                    Assert.True(top.X >= 0 && bottom.X <= dock.Bounds.Width + 1 && bottom.Y <= dock.Bounds.Height + 1,
                        $"{language} at {width}: {top} to {bottom} within {dock.Bounds.Size}");
                }
                if (Environment.GetEnvironmentVariable("MDA_WORKBENCH_LAYOUT_EVIDENCE") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    using var frame = HeadlessTestHost.CaptureLayoutFrame(window);
                    Assert.NotNull(frame); frame.Save(Path.Combine(directory, $"animation-{language}-{width}.png"));
                }
            }
        }
        finally { EditorLocalization.SetLanguage(previous); Close(window, shell); }
        return true;
    }, Token);

    [Theory]
    [InlineData("zh-CN")] [InlineData("en")] [InlineData("ja")]
    public async Task BoneAndCameraFieldsFitTheValuesDockWithoutScrolling(string language) => await session.Dispatch(() =>
    {
        var (window, shell, _) = Editor();
        string previous = EditorLocalization.CurrentLanguage;
        try
        {
            EditorLocalization.SetLanguage(language);
            var dock = window.FindControl<Grid>("StandardSelectionInspector")!;
            foreach (int width in new[] { 1480, 1100 })
            {
                shell.SceneSelectionIndex = -1;
                shell.Animation.SelectTimelineKeys([KeyframeAddress.Bone("root", 3)]);
                window.Width = width; window.Height = width == 1480 ? 940 : 720; Flush(window);
                if (width == 1100) window.FindControl<TabControl>("AnimationDockTabs")!.SelectedIndex = 2;
                Flush(window);
                CheckControls("BonePositionX", "BonePositionZ", "BoneRotationZ", "CopyPoseButton", "MirrorPoseButton",
                    "RegisterKeyframeButton", "ResetAnimationPoseButton");
                var keyTools = dock.GetVisualDescendants().OfType<ToggleButton>().Single(control => control.Name == "SelectedKeyToolsToggle");
                Click(window, keyTools);
                CheckControls("MoveAnimationKeysFrame", "MoveAnimationKeysButton");
                Click(window, keyTools);
                Capture("pose");

                shell.SceneSelectionIndex = 1; Flush(window);
                CheckControls("CameraCenterX", "CameraCenterZ", "CameraRotationZ", "CameraDistance", "CameraFov",
                    "CameraProjection", "CameraFovSlider", "RegisterCameraKeyframeButton");
                var projection = dock.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "CameraProjection");
                Assert.Equal(shell.SceneInspector.CameraProjectionIndex, projection.SelectedIndex);
                Assert.Equal(EditorLocalization.Text("Mda.Text.17e14283a497"), Assert.IsType<ComboBoxItem>(projection.SelectedItem).Content);
                projection.SelectedIndex = 1; Flush(window);
                Assert.Equal(1, shell.SceneInspector.CameraProjectionIndex);
                Assert.True(shell.SceneInspector.HasCameraDraft);
                Assert.Equal(EditorLocalization.Text("Mda.Text.3759f1efa160"), Assert.IsType<ComboBoxItem>(projection.SelectedItem).Content);
                Capture("camera");
                shell.SceneInspector.RevertDraftCommand.Execute(null);

                void CheckControls(params string[] names)
                {
                    foreach (string name in names)
                    {
                        var control = dock.GetVisualDescendants().OfType<Control>().Single(child => child.Name == name);
                        Assert.True(control.IsEffectivelyVisible, name);
                        var first = control.TranslatePoint(default, dock)!.Value;
                        var last = control.TranslatePoint(new(control.Bounds.Width, control.Bounds.Height), dock)!.Value;
                        Assert.True(first.X >= -1 && first.Y >= -1 && last.X <= dock.Bounds.Width + 1 && last.Y <= dock.Bounds.Height + 1,
                            $"{name}, {language} at {width}: {first} to {last} within {dock.Bounds.Size}");
                    }
                }
                void Capture(string context)
                {
                    if (Environment.GetEnvironmentVariable("MDA_WORKBENCH_LAYOUT_EVIDENCE") is not { Length: > 0 } directory) return;
                    Directory.CreateDirectory(directory);
                    using var bitmap = HeadlessTestHost.CaptureLayoutFrame(window);
                    bitmap.Save(Path.Combine(directory, $"{context}-{language}-{width}.png"));
                }
            }
        }
        finally { EditorLocalization.SetLanguage(previous); Close(window, shell); }
        return true;
    }, Token);

    [Fact]
    public async Task OutlineFilterFollowsLanguageAndOnlyTheTrackedLightHasAnimationControls() => await session.Dispatch(() =>
    {
        var (window, shell, _) = Editor();
        string previous = EditorLocalization.CurrentLanguage;
        try
        {
            var filter = window.FindControl<ComboBox>("OutlineFilter")!;
            filter.SelectedIndex = 1;
            foreach (var language in new[] { "en", "ja", "zh-CN" })
            {
                EditorLocalization.SetLanguage(language); Flush(window);
                Assert.Equal(1, filter.SelectedIndex);
                Assert.Equal(EditorLocalization.Text("Mda.Text.Layout.Models"), filter.SelectionBoxItem);
                Assert.Equal(EditorLocalization.Text("Mda.Text.Layout.All"), filter.Items[0]);
            }
            shell.SceneSelectionIndex = 2;
            shell.SceneInspector.SetPrimaryLightTrackActive(false); Flush(window);
            Assert.False(window.FindControl<Grid>("AnimationDock")!.IsVisible);
            Assert.True(window.FindControl<Button>("PlayAnimationButton")!.IsEffectivelyVisible);
            shell.SceneInspector.SetPrimaryLightTrackActive(true); Flush(window);
            Assert.True(window.FindControl<Grid>("StandardSelectionInspector")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Grid>("CurveDockPanel")!.IsVisible);
            Assert.False(window.FindControl<EditorMorphDock>("MorphDockPanel")!.IsVisible);
        }
        finally { EditorLocalization.SetLanguage(previous); Close(window, shell); }
        return true;
    }, Token);

    [Fact]
    public async Task FourMorphGroupsUseSampledAndDraftWeightsAndRegisterOnlyTheirSelectedMorph() => await session.Dispatch(() =>
    {
        var (window, shell, actor) = Editor();
        try
        {
            var dock = window.FindControl<EditorMorphDock>("MorphDockPanel")!;
            shell.CurrentFrame = 5; Flush(window);
            Assert.Equal(new[] { "brow", "eyes", "mouth", "other" }, dock.Groups.Select(group => group.Selected?.Name));
            Assert.Equal(.5m, dock.Groups[1].Weight);
            var only = dock.FindControl<CheckBox>("NonzeroMorphs")!;
            Click(window, only);
            Assert.Empty(dock.Groups[0].Choices);
            Assert.Equal("eyes", Assert.Single(dock.Groups[1].Choices).Name);
            Click(window, only);
            var sliders = dock.GetVisualDescendants().OfType<EditorValueSlider>().ToArray();
            Assert.Equal(4, sliders.Length); Assert.True(sliders[2].Focus());
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyRelease(Key.Right, RawInputModifiers.None, PhysicalKey.None, null); Flush(window);
            Assert.Equal("mouth", shell.Animation.SelectionName);
            Assert.True(shell.Animation.SelectedHasDraft);
            Assert.DoesNotContain(shell.Workspace.Motions.Single(m => m.ObjectId == actor).Motion.MorphKeyframes, key => key.MorphName == "mouth");
            Click(window, only);
            Assert.Equal("mouth", Assert.Single(dock.Groups[2].Choices).Name);
            var register = dock.GetVisualDescendants().OfType<Button>().Single(button => button.DataContext == dock.Groups[2] && button.Classes.Contains("action"));
            Click(window, register);
            var keys = shell.Workspace.Motions.Single(m => m.ObjectId == actor).Motion.MorphKeyframes;
            Assert.Equal(.01f, Assert.Single(keys, key => key.Frame == 5 && key.MorphName == "mouth").Weight);
            Assert.DoesNotContain(keys, key => key.Frame == 5 && key.MorphName != "mouth");
            shell.CurrentFrame = 0; Flush(window);
            Assert.Empty(dock.Groups[1].Choices);
            shell.CurrentFrame = 10; Flush(window);
            Assert.Equal(1m, dock.Groups[1].Weight);
        }
        finally { Close(window, shell); }
        return true;
    }, Token);

    [Fact]
    public async Task FilteringDoesNotRetargetAnActiveMorphDragWhenItCrossesZero() => await session.Dispatch(() =>
    {
        var (window, shell, _) = Editor();
        try
        {
            var dock = window.FindControl<EditorMorphDock>("MorphDockPanel")!;
            shell.CurrentFrame = 5; Flush(window);
            var eyes = dock.Groups[1];
            var other = eyes.Choices.Single(choice => choice.Name == "blink");
            eyes.Selected = other; eyes.Weight = .25m;
            eyes.Selected = eyes.Choices.Single(choice => choice.Name == "eyes");
            Click(window, dock.FindControl<CheckBox>("NonzeroMorphs")!);
            Assert.Equal(2, eyes.Choices.Count);
            var slider = dock.GetVisualDescendants().OfType<EditorValueSlider>().Single(control => control.DataContext == eyes);
            Point At(double fraction) => slider.TranslatePoint(new Point(slider.Bounds.Width * fraction, slider.Bounds.Height / 2), window)!.Value;
            window.MouseDown(At(.5), MouseButton.Left);
            window.MouseMove(At(0)); Flush(window);
            Assert.Equal(0m, eyes.Weight);
            Assert.Equal("eyes", eyes.Selected?.Name);
            window.MouseMove(At(.8)); window.MouseUp(At(.8), MouseButton.Left); Flush(window);
            Assert.True(eyes.Weight > .5m);
            eyes.Selected = other;
            Assert.Equal(.25m, eyes.Weight);
        }
        finally { Close(window, shell); }
        return true;
    }, Token);

    [Fact]
    public async Task InlineCurveKeyboardEditCommitsToSelectedKeyAndUndoRestoresOriginal() => await session.Dispatch(() =>
    {
        var (window, shell, actor) = Editor();
        try
        {
            shell.Animation.SelectTimelineKeys([KeyframeAddress.Bone("root", 3)]); Flush(window);
            var graph = window.FindControl<EditorInterpolationCurve>("InlineCurveGraph")!;
            Assert.NotNull(graph.Channel);
            int originalX = graph.Channel!.GetCurve().X1;
            Assert.True(graph.Focus());
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyRelease(Key.Right, RawInputModifiers.None, PhysicalKey.None, null); Flush(window);
            var motion = shell.Workspace.Motions.Single(m => m.ObjectId == actor).Motion;
            Assert.Equal(originalX + 1, new EditorInterpolationViewModel(motion, [KeyframeAddress.Bone("root", 3)]).SelectedChannel.GetCurve().X1);
            Assert.True(shell.Workspace.Undo()); Flush(window);
            Assert.Equal(originalX, graph.Channel!.GetCurve().X1);
            shell.SceneSelectionIndex = 1; Flush(window);
            Assert.Null(graph.Channel);
        }
        finally { Close(window, shell); }
        return true;
    }, Token);

    private static (EditorWindow, EditorShellViewModel, Guid) Editor()
    {
        var model = MorphApplicationTests.Model(new[] { "brow", "eyes", "mouth", "other", "base", "blink" }.Select((name, index) =>
            new PmxMorph { Name = name, EnglishName = "", Type = PmxMorphType.Position, ControlPanel = index == 5 ? (byte)2 : (byte)((index + 1) % 5),
                PositionOffsets = [new(0, Vector3.UnitY)] }).ToArray());
        var shell = new EditorShellViewModel();
        var path = Path.Combine(Path.GetTempPath(), "dock-fixture.pmx");
        var id = shell.Workspace.AddModel(path, EditorProjectWorkspace.CreateModelAsset(model, path, Token), ModelRole.Prop);
        shell.Workspace.CommitMotion(id, new VmdMotion { BoneKeyframes = [new() { BoneName = "root", Frame = 3 }],
            MorphKeyframes = [MorphApplicationTests.Key("eyes", 0, 0), MorphApplicationTests.Key("eyes", 10, 1)] });
        shell.RefreshWorkspace(id);
        var window = new EditorWindow(false) { Width = 1480, Height = 940 };
        window.FindControl<EditorViewportHost>("SceneViewport")!.IsVisible = false;
        window.DataContext = shell; window.Show(); Flush(window);
        return (window, shell, id);
    }

    private static void Click(Window window, Control control)
    {
        Assert.True(control.IsEffectivelyEnabled);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Flush(window);
    }

    private static void Flush(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Close(EditorWindow window, EditorShellViewModel shell)
    { window.DataContext = null; window.Close(); shell.Dispose(); Dispatcher.UIThread.RunJobs(); }
    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
