using System.Numerics;
using System.Reflection;
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
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorLightingGroupInspectorTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);

    [Fact]
    public async Task LegacyInspectorHidesAndGuardsModernGroupOperationsWhileModernModeEnablesThem() => await session.Dispatch(() =>
    {
        var light = new NativeSceneLight(Guid.NewGuid(), "key", SceneLightKind.Point, Vector3.One, Quaternion.Identity, Vector3.One);
        var inspector = new EditorLightInspector(); inspector.SetLighting(new([light]), light.Id, Guid.NewGuid());
        var window = new Window { Content = inspector, Width = 500, Height = 1000 };
        int commits = 0; inspector.CommitRequested = _ => { commits++; return true; };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            inspector.UseWorkspaceBrowser(false);
            Assert.True(inspector.FindControl<ComboBox>("LightChoice")!.IsEffectivelyVisible);
            Assert.False(inspector.FindControl<StackPanel>("ModernLightSelectionTools")!.IsEffectivelyVisible);
            inspector.FindControl<Button>("GroupSelectedLightsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            inspector.FindControl<Button>("DuplicateSelectedLightsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(inspector.ViewModel!.Lights); Assert.Empty(inspector.ViewModel.Groups); Assert.Equal(0, commits);
            inspector.UseWorkspaceBrowser(true);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.False(inspector.FindControl<ComboBox>("LightChoice")!.IsEffectivelyVisible);
            Assert.True(inspector.FindControl<StackPanel>("ModernLightSelectionTools")!.IsEffectivelyVisible);
            inspector.FindControl<Button>("GroupSelectedLightsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(inspector.ViewModel.Groups); Assert.Equal(1, commits);
            return true;
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task SelectedGroupFollowsMembershipHistoryAndRepeatedRefreshWhileExplicitViewportSelectionExitsIt() => await session.Dispatch(() =>
    {
        var (window, shell, group, first, second) = Editor();
        try
        {
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            window.FindControl<ListBox>("WorkspaceLightList")!.SelectedItem = inspector.ViewModel!.Groups[0];
            var third = first with { Id = Guid.NewGuid(), Name = "third" };
            var movedOutsideGroup = first with { GroupId = null };
            shell.Workspace.EditLighting(new(new([first, second, third], [group])));
            AssertGroup(first.Id, second.Id, third.Id);
            // Moving a member out of the group keeps that lamp in the project, but the selected
            // group row must stop selecting it. Its newly added child remains included.
            shell.Workspace.EditLighting(new(new([movedOutsideGroup, second, third], [group])));
            AssertGroup(second.Id, third.Id);
            Assert.True(shell.Workspace.Undo()); AssertGroup(first.Id, second.Id, third.Id);
            Assert.True(shell.Workspace.Redo()); AssertGroup(second.Id, third.Id);
            Assert.True(shell.Workspace.Undo()); AssertGroup(first.Id, second.Id, third.Id);
            Assert.True(shell.Workspace.Undo()); AssertGroup(first.Id, second.Id);
            Controller(window).Select(first.Id);
            RefreshTwice();
            Assert.Empty(inspector.ViewModel!.SelectedGroupIds);
            Assert.Equal(first.Id, Assert.Single(inspector.ViewModel.SelectedLightIds));
            Assert.True(inspector.ViewModel.ShowSingleLight);
            return true;

            void RefreshTwice()
            {
                var refresh = typeof(EditorWindow).GetMethod("RefreshLightingInspector", BindingFlags.NonPublic | BindingFlags.Instance)!;
                refresh.Invoke(window, null); refresh.Invoke(window, null);
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            }
            void AssertGroup(params Guid[] ids)
            {
                RefreshTwice();
                Assert.Equal(group.Id, Assert.Single(inspector.ViewModel!.SelectedGroupIds));
                Assert.Same(inspector.ViewModel.Groups[0], Assert.Single(inspector.ViewModel.SelectedRows));
                Assert.Equal(ids, inspector.ViewModel.SelectedLightIds);
                Assert.Equal(ids, Controller(window).SelectedLightIds);
            }
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ActualListMultiSelectionAndGroupSearchKeepViewportAndInspectorInSyncWithoutHistory() => await session.Dispatch(() =>
    {
        var (window, shell, group, first, second) = Editor();
        try
        {
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            var list = window.FindControl<ListBox>("WorkspaceLightList")!;
            var controller = Controller(window);
            var before = shell.Workspace.Session!.Lighting;
            list.SelectedItems!.Clear();
            list.SelectedItems!.Add(inspector.ViewModel!.Lights[0]);
            list.SelectedItems.Add(inspector.ViewModel.Lights[1]);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { first.Id, second.Id }, controller.SelectedLightIds);
            Assert.Equal(2, list.SelectedItems.Count);
            Assert.False(inspector.ViewModel.ShowSingleLight);
            Assert.True(inspector.ViewModel.ShowBatchControls);
            list.SelectedItem = inspector.ViewModel.Groups[0];
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(group.Id, Assert.Single(inspector.ViewModel.SelectedGroupIds));
            Assert.Equal(new[] { first.Id, second.Id }, controller.SelectedLightIds);
            Assert.True(inspector.FindControl<ContentControl>("LightGroupProperties")!.IsEffectivelyVisible);
            CaptureLayout(window, "lighting-groups");
            Assert.All(inspector.GetVisualDescendants().OfType<TextBox>().Where(control => control.Name == "LightNameInput"), control => Assert.False(control.IsEffectivelyVisible));
            var search = window.FindControl<TextBox>("LightSearchInput")!;
            search.Text = "no matching rig";
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, list.ItemCount);
            Assert.Equal(group.Id, Assert.Single(inspector.ViewModel.SelectedGroupIds));
            Assert.Equal(new[] { first.Id, second.Id }, controller.SelectedLightIds);
            search.Text = "";
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, list.ItemCount);
            Assert.Same(inspector.ViewModel.Groups[0], Assert.Single(list.SelectedItems.Cast<object>()));
            Assert.Same(before, shell.Workspace.Session.Lighting);
            Assert.True(shell.Workspace.Undo());
            Assert.Empty(shell.Workspace.Session.Lighting.Lights);
            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task GroupSliderPreviewsAndReleaseCreatesOneUndoWhileEscapeRestoresMask() => await session.Dispatch(() =>
    {
        var (window, shell, group, first, second) = Editor();
        try
        {
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            var list = window.FindControl<ListBox>("WorkspaceLightList")!;
            list.SelectedItem = inspector.ViewModel!.Groups.Single();
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { first.Id, second.Id }, inspector.ViewModel.SelectedLightIds);
            Assert.True(inspector.ViewModel.HasSelectedGroup);
            var slider = Slider(inspector, "LightGroupBrightnessSlider");
            var controller = Controller(window);
            var before = shell.Workspace.Session!.Lighting;
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            slider.Value = EditorLightParameterScale.BrightnessPosition(2);
            slider.Value = EditorLightParameterScale.BrightnessPosition(3);
            Assert.Same(before, shell.Workspace.Session.Lighting);
            Assert.Equal(3f, Assert.Single(controller.PreviewLighting.Groups).Intensity);
            Assert.Equal(first.Intensity, controller.PreviewLighting.Lights[0].Intensity);
            Assert.Equal(second.Enabled, controller.PreviewLighting.Lights[1].Enabled);
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Right });
            Assert.Equal(3f, Assert.Single(shell.Workspace.Session.Lighting.Groups).Intensity);
            Assert.Equal(group.Id, Assert.Single(inspector.ViewModel.SelectedGroupIds));
            Assert.Same(inspector.ViewModel.Groups[0], Assert.Single(inspector.ViewModel.SelectedRows));
            Assert.True(shell.Workspace.Undo());
            Assert.Equal(group, Assert.Single(shell.Workspace.Session.Lighting.Groups));
            Assert.Equal(group.Id, Assert.Single(inspector.ViewModel!.SelectedGroupIds));
            Assert.Same(inspector.ViewModel.Groups[0], Assert.Single(inspector.ViewModel.SelectedRows));
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(group.Id, Assert.Single(inspector.ViewModel.SelectedGroupIds));
            slider = Slider(inspector, "LightGroupBrightnessSlider");
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            slider.Value = EditorLightParameterScale.BrightnessPosition(4);
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.Equal(group, Assert.Single(shell.Workspace.Session.Lighting.Groups));
            Assert.Equal(group, Assert.Single(controller.PreviewLighting.Groups));
            Assert.True(shell.Workspace.Undo()); // Esc adds no entry; this removes the original lighting edit.
            Assert.Empty(shell.Workspace.Session.Lighting.Groups);
            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task BatchRelativeSliderPreservesRatiosAndOptionalInvalidAbsoluteInputDoesNotBlockIt() => await session.Dispatch(() =>
    {
        var (window, shell, _, first, second) = Editor();
        try
        {
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            inspector.ViewModel!.SelectMany([first.Id, second.Id], second.Id);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            inspector.FindControl<Expander>("BatchLightAbsoluteExpander")!.IsExpanded = true;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var absolute = inspector.GetVisualDescendants().OfType<EditorNumericUpDown>().Single(control => control.Name == "BatchLightIntensityInput");
            absolute.Text = "unfinished";
            Assert.False(absolute.IsInputValid);
            Assert.True(inspector.TryCommit());
            var slider = Slider(inspector, "BatchLightBrightnessSlider");
            var original = shell.Workspace.Session!.Lighting;
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            slider.Value = Math.Log10(2);
            Assert.Same(original, shell.Workspace.Session.Lighting);
            var preview = Controller(window).PreviewLighting;
            Assert.Equal(first.Intensity * 2, preview.Lights[0].Intensity);
            Assert.Equal(second.Intensity * 2, preview.Lights[1].Intensity);
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Right });
            Assert.Equal(first.Intensity * 2, shell.Workspace.Session.Lighting.Lights[0].Intensity);
            Assert.Equal(second.Intensity * 2, shell.Workspace.Session.Lighting.Lights[1].Intensity);
            Assert.Equal(0, inspector.ViewModel.RelativeBrightnessPosition);
            Assert.True(shell.Workspace.Undo());
            Assert.Equal(first, shell.Workspace.Session.Lighting.Lights[0]);
            Assert.Equal(second, shell.Workspace.Session.Lighting.Lights[1]);
            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ChangingFromGroupToItsChildrenCancelsGestureEvenWithTheSamePrimaryAndLightIds() => await session.Dispatch(() =>
    {
        var (window, shell, group, first, second) = Editor();
        try
        {
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            inspector.ViewModel!.SelectGroupIds([group.Id]);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var slider = Slider(inspector, "LightGroupBrightnessSlider");
            var committed = shell.Workspace.Session!.Lighting;
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            slider.Value = EditorLightParameterScale.BrightnessPosition(3);
            Assert.Equal(3f, Controller(window).PreviewLighting.Groups[0].Intensity);
            var model = inspector.ViewModel!;
            model.SelectRows(model.Lights.Cast<object>(), model.Lights[0]);
            Assert.Same(committed, shell.Workspace.Session.Lighting);
            Assert.Equal(group, Assert.Single(Controller(window).PreviewLighting.Groups));
            Assert.Equal(new[] { first.Id, second.Id }, model.SelectedLightIds);
            Assert.Equal(first.Id, model.SelectedLight?.Id);
            Assert.Empty(model.SelectedGroupIds);
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Right });
            Assert.Same(committed, shell.Workspace.Session.Lighting);
            Assert.True(shell.Workspace.Undo());
            Assert.Empty(shell.Workspace.Session.Lighting.Lights);
            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingFromSingleToGroupOrMultipleLightsCancelsPreviewWithUnchangedPrimary(bool chooseGroup) => await session.Dispatch(() =>
    {
        var (window, shell, group, first, second) = Editor();
        try
        {
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            Controller(window).Select(first.Id);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(inspector.ViewModel!.ShowSingleLight);
            var slider = inspector.GetVisualDescendants().OfType<EditorLightParameterSlider>().Single(control => control.Name == "LightBrightnessSlider"
                && control.DataContext is EditorNativeLightItem light && light.Id == first.Id);
            var committed = shell.Workspace.Session!.Lighting;
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            slider.Value = EditorLightParameterScale.BrightnessPosition(7);
            Assert.Equal(7f, Controller(window).PreviewLighting.Lights[0].Intensity);
            if (chooseGroup) inspector.ViewModel.SelectGroupIds([group.Id]);
            else inspector.ViewModel.SelectMany([first.Id, second.Id], first.Id);
            Assert.Equal(first.Id, inspector.ViewModel.SelectedLight?.Id);
            Assert.Equal(new[] { first.Id, second.Id }, inspector.ViewModel.SelectedLightIds);
            Assert.Equal(chooseGroup, inspector.ViewModel.HasSelectedGroup);
            Assert.False(inspector.ViewModel.ShowSingleLight);
            Assert.Equal(committed.Lights, Controller(window).PreviewLighting.Lights);
            Assert.Same(committed, shell.Workspace.Session.Lighting);
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Right });
            Assert.Same(committed, shell.Workspace.Session.Lighting);
            Assert.True(shell.Workspace.Undo());
            Assert.Empty(shell.Workspace.Session.Lighting.Lights);
            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task GroupPendingNameAndInvalidNumberSurviveHistoryWithoutChangingGroupMembership() => await session.Dispatch(() =>
    {
        var (window, shell, group, first, second) = Editor();
        try
        {
            shell.Workspace.EditLighting(new(new([first, second], [group with { Intensity = 2 }]), first.Id, [first.Id, second.Id], [group.Id]));
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var inspector = window.FindControl<EditorLightInspector>("NativeLightInspector")!;
            inspector.ViewModel!.SelectGroupIds([group.Id]);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            inspector.ViewModel.Groups[0].Name = "Pending rig";
            Assert.Equal(group.Id, Assert.Single(inspector.ViewModel.SelectedGroupIds));
            var numeric = inspector.GetVisualDescendants().OfType<EditorNumericUpDown>().Single(control => control.Name == "LightGroupIntensityInput");
            numeric.Text = "unfinished";
            Assert.True(shell.Workspace.Undo());
            Assert.Equal(group.Id, Assert.Single(inspector.ViewModel!.SelectedGroupIds));
            Assert.Same(inspector.ViewModel.Groups[0], Assert.Single(inspector.ViewModel.SelectedRows));
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal("Pending rig", inspector.ViewModel!.Groups[0].Name);
            Assert.Equal("unfinished", inspector.GetVisualDescendants().OfType<EditorNumericUpDown>().Single(control => control.Name == "LightGroupIntensityInput").Text);
            Assert.Equal(group.Name, shell.Workspace.Session!.Lighting.Groups[0].Name);
            Assert.All(inspector.ViewModel.Lights, light => Assert.Equal(group.Id, light.GroupId));
            Assert.False(inspector.TryCommit());
            return true;
        }
        finally { Close(window); }
    }, TestContext.Current.CancellationToken);

    private static EditorLightParameterSlider Slider(EditorLightInspector inspector, string name) =>
        inspector.GetVisualDescendants().OfType<EditorLightParameterSlider>().Single(control => control.Name == name);
    private static EditorLightViewportController Controller(EditorWindow window) =>
        (EditorLightViewportController)typeof(EditorWindow).GetField("lightController", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
    private static (EditorWindow, EditorShellViewModel, SceneLightGroup, NativeSceneLight, NativeSceneLight) Editor()
    {
        var window = new EditorWindow(false);
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.IsSuspended = true; viewport.IsVisible = false;
        var shell = new EditorShellViewModel(); window.DataContext = shell; window.Show();
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd");
        using var input = File.OpenRead(path);
        var asset = EditorProjectWorkspace.CreateModelAsset(PmdPmxAdapter.Convert(PmdReader.Read(input)), path);
        shell.Workspace.AddModel(path, asset, ModelRole.Character);
        var group = new SceneLightGroup(Guid.NewGuid(), "Face rig");
        var first = new NativeSceneLight(Guid.NewGuid(), "key", SceneLightKind.Point, Vector3.One, Quaternion.Identity, Vector3.One, 2, GroupId: group.Id);
        var second = first with { Id = Guid.NewGuid(), Name = "fill", Intensity = 5, Enabled = false };
        shell.Workspace.EditLighting(new(new([first, second], [group])));
        shell.ShowInspectorPage(EditorInspectorPage.Lighting);
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        return (window, shell, group, first, second);
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
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame); frame.Save(Path.Combine(directory, name + ".png"));
        double width = window.Width, height = window.Height;
        try
        {
            window.Width = 1100; window.Height = 720;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            using var minimum = window.CaptureRenderedFrame();
            Assert.NotNull(minimum); minimum.Save(Path.Combine(directory, name + "-minimum.png"));
        }
        finally { window.Width = width; window.Height = height; window.UpdateLayout(); }
    }
    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
