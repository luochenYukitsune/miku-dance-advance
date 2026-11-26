using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorOutlineContextMenuTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RightClickSelectsModelBeforeOpeningAndBlankAreaOffersImport() => await session.Dispatch(() =>
    {
        var window = CreateWindow(out var shell);
        try
        {
            var browser = window.FindControl<EditorModelBrowser>("ModelBrowserSurface")!;
            var area = browser.FindControl<Grid>("ModelListArea")!;
            var empty = OpenMenu(window, area);
            Assert.False(Assert.IsType<MenuItem>(Assert.Single(empty.Items)).IsEnabled);
            empty.Close();

            var first = AddModel(shell, "first");
            var second = AddModel(shell, "second");
            shell.RefreshWorkspace(second);
            Flush(window);
            var list = browser.FindControl<ListBox>("ModelList")!;
            var row = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0));
            var menu = OpenMenu(window, row);
            Assert.Equal(first, shell.Browser.SelectedModel!.ObjectId);
            Assert.True(FindItem(menu, "Mda.Text.6135d4159e89").IsEffectivelyEnabled);
            menu.Close();
            shell.SceneSelectionIndex = 1;
            Click(window, row, MouseButton.Left);
            Assert.True(shell.IsAnimationInspector);
            return true;
        }
        finally { Close(window, shell); }
    }, Token);

    [Fact]
    public async Task LightMenuKeepsSelectedRowsAndCommitsOnlyItsTargetsIncludingGroupMasks() => await session.Dispatch(() =>
    {
        var window = CreateWindow(out var shell);
        try
        {
            AddModel(shell, "actor");
            var group = new SceneLightGroup(Guid.NewGuid(), "rig");
            var first = new NativeSceneLight(Guid.NewGuid(), "key", SceneLightKind.Point, Vector3.One, Quaternion.Identity, Vector3.One, GroupId: group.Id);
            var second = first with { Id = Guid.NewGuid(), Name = "fill" };
            var third = first with { Id = Guid.NewGuid(), Name = "solo", GroupId = null };
            shell.Workspace.EditLighting(new(new([first, second, third], [group])));
            shell.ShowInspectorPage(EditorInspectorPage.Lighting);
            Flush(window);
            var list = window.FindControl<ListBox>("WorkspaceLightList")!;
            var view = window.FindControl<EditorLightInspector>("NativeLightInspector")!.ViewModel!;
            view.SelectMany([first.Id, second.Id], first.Id, preserveGroups: false);
            Flush(window);
            var menu = OpenMenu(window, Row(view.Lights.Single(light => light.Id == second.Id)));
            Assert.Equal(new[] { first.Id, second.Id }, view.SelectedLightIds);
            Choose(menu, "Mda.Text.Context.Lighting.DisableSelection");
            Assert.Equal(new[] { false, false, true }, shell.Workspace.Session!.Lighting.Lights.Select(light => light.Enabled));
            Assert.True(shell.Workspace.Undo());
            Flush(window);
            menu = OpenMenu(window, Row(Current().Lights.Single(light => light.Id == third.Id)));
            Assert.Equal(third.Id, Assert.Single(Current().SelectedLightIds));
            Choose(menu, "Mda.Text.2f9daa828907");
            Assert.Equal(new[] { first.Id, second.Id }, shell.Workspace.Session.Lighting.Lights.Select(light => light.Id));
            menu = OpenMenu(window, Row(Current().Groups.Single()));
            Assert.Equal(group.Id, Assert.Single(Current().SelectedGroupIds));
            Choose(menu, "Mda.Text.Context.Lighting.DisableSelection");
            Assert.False(shell.Workspace.Session.Lighting.Groups.Single().Enabled);
            Assert.All(shell.Workspace.Session.Lighting.Lights, light => Assert.True(light.Enabled));
            return true;

            EditorLightingViewModel Current() => window.FindControl<EditorLightInspector>("NativeLightInspector")!.ViewModel!;
            ListBoxItem Row(object target)
            {
                list.ScrollIntoView(target);
                Flush(window);
                return Assert.IsType<ListBoxItem>(list.ContainerFromItem(target));
            }
        }
        finally { Close(window, shell); }
    }, Token);

    [Fact]
    public async Task EmptyLightListAddsRealLightsAndBothPresetMenusHonorAvailableCapacity() => await session.Dispatch(() =>
    {
        var window = CreateWindow(out var shell);
        try
        {
            AddModel(shell, "rig");
            shell.ShowInspectorPage(EditorInspectorPage.Lighting);
            Flush(window);
            var menu = OpenMenu(window, window.FindControl<Grid>("LightListArea")!);
            Assert.True(FindItem(menu, "Mda.Text.Layout.ThreePoint").IsEffectivelyEnabled);
            Choose(menu, "Mda.Text.164901accbd4");
            var point = Assert.Single(shell.Workspace.Session!.Lighting.Lights);
            Assert.Equal(SceneLightKind.Point, point.Kind);
            var lights = Enumerable.Range(0, SceneLightingSnapshot.MaxActiveLights - 2)
                .Select(i => point with { Id = Guid.NewGuid(), Name = i.ToString() }).ToArray();
            shell.Workspace.EditLighting(new(new(lights)));
            Flush(window);
            var search = window.FindControl<TextBox>("LightSearchInput")!;
            search.Text = "no such light";
            Flush(window);
            menu = OpenMenu(window, window.FindControl<Grid>("LightListArea")!);
            Assert.False(FindItem(menu, "Mda.Text.Layout.ThreePoint").IsEffectivelyEnabled);
            Assert.True(FindItem(menu, "Mda.Text.Context.Lighting.Presets.IndoorWindow").IsEffectivelyEnabled);
            menu.Close();
            var add = window.FindControl<Button>("AddBrowserLightButton")!;
            var flyout = Assert.IsType<MenuFlyout>(add.Flyout);
            flyout.ShowAt(add);
            Flush(window);
            Assert.False(flyout.Items.OfType<MenuItem>().Single(item => item.Name == "AddThreePointLightGroup").IsEnabled);
            Assert.True(flyout.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "IndoorWindow")).IsEnabled);
            flyout.Hide();
            return true;
        }
        finally { Close(window, shell); }
    }, Token);

    [Fact]
    public async Task MorphGroupAndLeafMenusSelectTargetAndZeroWeightWithUndo() => await session.Dispatch(() =>
    {
        var window = CreateWindow(out var shell);
        try
        {
            var id = AddModel(shell, "actor");
            shell.Workspace.CommitMotion(id, new VmdMotion { MorphKeyframes = [MorphApplicationTests.Key("smile", 0, .7f)] });
            shell.RefreshWorkspace(id);
            shell.Browser.IsHierarchyExpanded = true;
            shell.Browser.HierarchyTab = 1;
            shell.Animation.ConfirmOverwriteAsync = _ => Task.FromResult(true);
            var browser = window.FindControl<EditorModelBrowser>("ModelBrowserSurface")!;
            var tree = browser.FindControl<TreeView>("MorphTree")!;
            Flush(window);
            var group = shell.Browser.MorphGroups.Single();
            var groupContainer = (TreeViewItem)tree.ContainerFromIndex(0)!;
            var menu = OpenMenu(window, groupContainer);
            Assert.True(shell.Browser.SelectedMorphNode!.IsGroup);
            Choose(menu, group.IsExpanded ? "Mda.Text.Menu.Collapse" : "Mda.Text.Menu.Expand");
            group.IsExpanded = true;
            Flush(window);
            var leaf = tree.GetVisualDescendants().OfType<TreeViewItem>().Single(item => item.DataContext is EditorElementNode { DisplayName: "smile" });
            menu = OpenMenu(window, leaf);
            Assert.Equal("smile", shell.Animation.SelectionName);
            Choose(menu, "Mda.Text.Menu.ZeroMorph");
            Assert.Equal(0m, shell.Animation.MorphWeight);
            Assert.Equal(0f, shell.Workspace.Motions.Single().Motion!.MorphKeyframes.Single().Weight);
            Assert.True(shell.Workspace.Undo());
            Assert.Equal(.7f, shell.Workspace.Motions.Single().Motion!.MorphKeyframes.Single().Weight);
            return true;
        }
        finally { Close(window, shell); }
    }, Token);

    [Fact]
    public async Task BoneAndFixedObjectMenusFollowTheClickedRow() => await session.Dispatch(() =>
    {
        var window = CreateWindow(out var shell);
        try
        {
            var model = AddModel(shell, "rig");
            shell.RefreshWorkspace(model);
            shell.Browser.IsHierarchyExpanded = true;
            var browser = window.FindControl<EditorModelBrowser>("ModelBrowserSurface")!;
            Flush(window);
            var bone = browser.FindControl<TreeView>("BoneTree")!.GetVisualDescendants().OfType<TreeViewItem>()
                .Single(row => row.DataContext is EditorElementNode { DisplayName: "root" });
            var menu = OpenMenu(window, bone);
            Assert.True(shell.Animation.IsBoneSelected);
            Assert.Equal(0, shell.Animation.SelectedBoneIndex);
            Assert.True(FindItem(menu, "Mda.Text.Layout.CopyPose").IsEffectivelyEnabled);
            menu.Close();
            shell.Browser.IsHierarchyExpanded = false;
            Flush(window);
            menu = OpenMenu(window, window.FindControl<ListBoxItem>("OutlineWorld")!);
            Assert.True(shell.IsWorldInspector);
            var guides = shell.ShowWorldGuides;
            Choose(menu, "Mda.Text.Ux.View.ShowWorldGuides");
            Assert.Equal(!guides, shell.ShowWorldGuides);
            menu = OpenMenu(window, window.FindControl<ListBoxItem>("OutlineCamera")!);
            Assert.True(shell.IsCameraInspector);
            Assert.True(FindItem(menu, "Mda.Text.a619bd96a115").IsEffectivelyEnabled);
            Assert.False(FindItem(menu, "Mda.Text.70403791a994").IsEffectivelyEnabled);
            menu.Close();
            shell.SceneInspector.AimLight("left");
            menu = OpenMenu(window, window.FindControl<ListBoxItem>("OutlineTrackLight")!);
            Assert.True(shell.SceneInspector.ShowsPrimaryLightTrack);
            Assert.True(FindItem(menu, "Mda.Text.e4b84a93325e").IsEffectivelyEnabled);
            Choose(menu, "Mda.Text.Menu.DefaultMainLight");
            Assert.Equal(new Vector3(-.5f, -1f, .5f), shell.SceneInspector.LightPose.Direction);
            return true;
        }
        finally { Close(window, shell); }
    }, Token);

    [Fact]
    public void BatchActionsRespectGroupMasksAndBothLightCapacityLimits()
    {
        var group = new SceneLightGroup(Guid.NewGuid(), "off", Enabled: false);
        var basis = new NativeSceneLight(Guid.NewGuid(), "light", SceneLightKind.Point, Vector3.One, Quaternion.Identity, Vector3.One);
        var lights = Enumerable.Range(0, SceneLightingSnapshot.MaxLights).Select(i => basis with
        {
            Id = Guid.NewGuid(), Name = i.ToString(), Enabled = i <= SceneLightingSnapshot.MaxActiveLights,
            GroupId = i == SceneLightingSnapshot.MaxActiveLights ? group.Id : null
        }).ToArray();
        var model = new EditorLightingViewModel(new(lights, [group]));
        model.SelectGroupIds([group.Id]);
        Assert.False(model.CanEnableSelection); // The group's enabled child would exceed the active budget.
        Assert.False(model.CanDisableSelection);
        Assert.False(model.CanDuplicateSelection); // Authored capacity includes disabled lights.
        model.Lights[0].Enabled = false;
        Assert.True(model.CanEnableSelection);
        Assert.True(model.SetSelectedEnabled(true));
        Assert.True(model.CanDisableSelection);
        Assert.True(model.Lights[SceneLightingSnapshot.MaxActiveLights].Enabled);
    }

    private static EditorWindow CreateWindow(out EditorShellViewModel shell)
    {
        shell = new EditorShellViewModel();
        var window = new EditorWindow(false) { Width = 1480, Height = 940 };
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.IsSuspended = true; viewport.IsVisible = false;
        window.DataContext = shell;
        window.Show(); Flush(window);
        return window;
    }

    private static Guid AddModel(EditorShellViewModel shell, string name)
    {
        var path = Path.Combine(Path.GetTempPath(), name + ".pmx");
        var model = MorphApplicationTests.Model([new PmxMorph { Name = "smile", EnglishName = "", Type = PmxMorphType.Position,
            ControlPanel = 3, PositionOffsets = [new(0, Vector3.UnitY)] }]);
        return shell.Workspace.AddModel(path, EditorProjectWorkspace.CreateModelAsset(model, path, Token), ModelRole.Character);
    }

    private static MenuItem FindItem(ContextMenu menu, string key) =>
        Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, EditorLocalization.Text(key)));

    private static void Choose(ContextMenu menu, string key)
    {
        var item = FindItem(menu, key);
        Assert.True(item.IsEffectivelyEnabled, key);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close(); Dispatcher.UIThread.RunJobs();
    }

    private static ContextMenu OpenMenu(EditorWindow window, Control target)
    {
        ContextMenu? opened = null;
        using var capture = ContextMenu.OpenedEvent.AddClassHandler<ContextMenu>((menu, _) => opened = menu);
        Click(window, target, MouseButton.Right);
        return Assert.IsType<ContextMenu>(opened);
    }

    private static void Click(EditorWindow window, Control target, MouseButton button)
    {
        target.BringIntoView(new Rect(0, 0, target.Bounds.Width, 24));
        Flush(window);
        var point = target.TranslatePoint(new(Math.Min(35, target.Bounds.Width / 2), Math.Min(12, target.Bounds.Height / 2)), window)!.Value;
        window.MouseMove(point); window.MouseDown(point, button); window.MouseUp(point, button); Flush(window);
    }

    private static void Flush(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Close(EditorWindow window, EditorShellViewModel shell)
    { window.DataContext = null; window.Close(); shell.Dispose(); Dispatcher.UIThread.RunJobs(); }
    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
