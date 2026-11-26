using System.Numerics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorDraftNumberHistoryTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Reset = "Mda.Text.Menu.ResetValue", Paste = "Mda.Text.Menu.PasteNumber";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MorphResetIsOneDraftUndoAtItsOriginalFrameAndLeavesOtherChannelsAlone(bool useDock) => await session.Dispatch(() =>
    {
        var (window, shell, actor) = Editor();
        try
        {
            shell.Browser.HierarchyTab = 1;
            shell.Browser.SelectedMorphIndex = 0;
            Flush(window);
            Control field = useDock
                ? window.GetVisualDescendants().OfType<EditorValueSlider>().Single(item => item.DataContext is EditorMorphDockGroup { Selected.Index: 0 })
                : window.GetVisualDescendants().OfType<EditorNumericUpDown>().Single(item => item.Name == "MorphWeight");
            Assert.False(shell.Animation.SelectedHasDraft);
            Choose(Open(field), Reset);
            Assert.Equal(0m, shell.Animation.MorphWeight);
            Assert.True(shell.Animation.SelectedHasDraft);
            var authored = shell.Workspace.Motions.Single(motion => motion.ObjectId == actor).Motion!;
            Assert.Equal(.75f, Assert.Single(authored.MorphKeyframes).Weight);

            shell.CurrentFrame = 8;
            shell.Browser.SelectedMorphIndex = 1;
            shell.Animation.MorphWeight = .45m;
            shell.UndoCommand.Execute(null);
            Assert.Equal(8, shell.CurrentFrame);
            Assert.Equal(.45m, shell.Animation.MorphWeight);
            Assert.DoesNotContain(0, shell.Animation.DraftFrames);
            shell.RedoCommand.Execute(null);
            shell.CurrentFrame = 0; shell.Browser.SelectedMorphIndex = 0;
            Assert.Equal(0m, shell.Animation.MorphWeight);
            Assert.Contains(8, shell.Animation.DraftFrames);
            Assert.Same(authored, shell.Workspace.Motions.Single().Motion);
            return true;
        }
        finally { Close(window, shell); }
    }, Token);

    [Fact]
    public async Task UndoOfInactiveMorphUpdatesNonzeroFilterAndKeepsActiveMorph() => await session.Dispatch(() =>
    {
        var (window, shell, _) = Editor();
        try
        {
            shell.Browser.HierarchyTab = 1;
            shell.Browser.SelectedMorphIndex = 1;
            shell.Animation.MorphWeight = .4m;
            shell.Browser.SelectedMorphIndex = 0;
            var dock = window.FindControl<EditorMorphDock>("MorphDockPanel")!;
            var filter = dock.FindControl<CheckBox>("NonzeroMorphs")!;
            filter.IsChecked = true;
            filter.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Flush(window);
            var eyes = dock.Groups[1];
            Assert.Equal("blink", Assert.Single(eyes.Choices).Name);
            var slider = dock.GetVisualDescendants().OfType<EditorValueSlider>().Single(item => item.DataContext == eyes);
            Choose(Open(slider), Reset);
            shell.Browser.SelectedMorphIndex = 1;
            Flush(window);
            Assert.Empty(eyes.Choices);
            shell.UndoCommand.Execute(null);
            Flush(window);
            Assert.Equal("blink", Assert.Single(eyes.Choices).Name);
            Assert.Equal(.75m, eyes.Weight);
            Assert.Equal(1, shell.Animation.SelectedMorphIndex);
            Assert.Equal(.4m, shell.Animation.MorphWeight);
            shell.RedoCommand.Execute(null);
            Flush(window);
            Assert.Empty(eyes.Choices);
            Assert.Equal(1, shell.Browser.SelectedMorphIndex);
            Assert.Equal("smile", dock.Groups[2].Selected!.Name);
            return true;
        }
        finally { Close(window, shell); }
    }, Token);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScenePasteUndoRestoresDraftFlagAndKeepsTheOtherTrack(bool camera) => await session.Dispatch(async () =>
    {
        var (window, shell, _) = Editor();
        try
        {
            shell.SceneSelectionIndex = camera ? 1 : 2;
            var scene = shell.SceneInspector;
            if (camera) scene.LightIntensity = 1.6m;
            else scene.CameraFov = 43;
            var authored = shell.Workspace.Session!.CaptureAuthoredContent().SceneMotion;
            var beforeCamera = scene.CameraPose;
            var beforeLight = scene.LightPose;
            Flush(window);
            string name = camera ? "CameraFovSlider" : "LightIntensity";
            var field = window.GetVisualDescendants().OfType<Control>().Single(control => control.Name == name);
            await window.Clipboard!.SetTextAsync(camera ? "52" : "0.4");
            Choose(Open(field), Paste);
            Assert.Equal(camera ? 52m : .4m, camera ? scene.CameraFov : scene.LightIntensity);
            Assert.True(scene.HasCameraDraft && scene.HasLightDraft);
            shell.UndoCommand.Execute(null);
            Assert.Equal(!camera, scene.HasCameraDraft);
            Assert.Equal(camera, scene.HasLightDraft);
            Assert.Equal(beforeCamera, scene.CameraPose);
            Assert.Equal(beforeLight, scene.LightPose);
            shell.RedoCommand.Execute(null);
            Assert.Equal(camera ? 52m : .4m, camera ? scene.CameraFov : scene.LightIntensity);
            Assert.True(scene.HasCameraDraft && scene.HasLightDraft);
            Assert.Same(authored, shell.Workspace.Session.CaptureAuthoredContent().SceneMotion);
            if (camera) scene.CameraCenterX = 7; else scene.LightColorR = .3m;
            shell.UndoCommand.Execute(null);
            Assert.Equal(camera ? 30m : 1m, camera ? scene.CameraFov : scene.LightIntensity);
            Assert.Equal(camera ? 7m : .3m, camera ? scene.CameraCenterX : scene.LightColorR);
            Assert.True(camera ? scene.HasCameraDraft : scene.HasLightDraft);
            shell.RedoCommand.Execute(null);

            var stale = Open(field);
            shell.CurrentFrame = 4;
            if (camera) scene.CameraFov = 72; else scene.LightIntensity = .7m;
            Choose(stale, Reset);
            Assert.Equal(camera ? 72m : .7m, camera ? scene.CameraFov : scene.LightIntensity);
            return true;
        }
        finally { Close(window, shell); }
    }, Token);

    private static (EditorWindow, EditorShellViewModel, Guid) Editor()
    {
        var shell = new EditorShellViewModel();
        var model = MorphApplicationTests.Model(new[] { "blink", "smile" }.Select((name, index) =>
            new PmxMorph { Name = name, EnglishName = "", Type = PmxMorphType.Position, ControlPanel = (byte)(index + 2),
                PositionOffsets = [new(0, Vector3.UnitY)] }).ToArray());
        var path = Path.Combine(Path.GetTempPath(), "draft-number-model.pmx");
        var id = shell.Workspace.AddModel(path, EditorProjectWorkspace.CreateModelAsset(model, path, Token), ModelRole.Character);
        shell.Workspace.CommitMotion(id, new VmdMotion { MorphKeyframes = [MorphApplicationTests.Key("blink", 0, .75f)] });
        shell.RefreshWorkspace(id);
        var window = new EditorWindow(false) { Width = 1480, Height = 940, DataContext = shell };
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.IsSuspended = true; viewport.IsVisible = false;
        window.Show(); Flush(window);
        return (window, shell, id);
    }

    private static ContextMenu Open(Control field)
    {
        ContextMenu? opened = null;
        using var subscription = ContextMenu.OpenedEvent.AddClassHandler<ContextMenu>((value, _) => opened = value);
        field.RaiseEvent(new ContextRequestedEventArgs());
        Dispatcher.UIThread.RunJobs();
        return Assert.IsType<ContextMenu>(opened);
    }

    private static void Choose(ContextMenu menu, string key)
    {
        var item = menu.Items.OfType<MenuItem>().Single(value => Equals(value.Header, EditorLocalization.Text(key)));
        Assert.True(item.IsEffectivelyEnabled, key);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close(); Dispatcher.UIThread.RunJobs();
    }

    private static void Flush(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static void Close(EditorWindow window, EditorShellViewModel shell)
    { window.DataContext = null; window.Close(); shell.Dispose(); Dispatcher.UIThread.RunJobs(); }
    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
