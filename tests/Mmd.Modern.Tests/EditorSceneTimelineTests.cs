using BindingFlags = System.Reflection.BindingFlags;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.App.Controls;
using Mmd.Modern.Application;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorSceneTimelineTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);

    [Fact]
    public async Task CameraAndMainLightRegisterAndNavigateTheirOwnTracksAcrossUndoAndModelSelection() => await session.Dispatch(() =>
    {
        var window = new EditorWindow(false);
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.IsSuspended = true; viewport.IsVisible = false;
        var shell = new EditorShellViewModel(); window.DataContext = shell;
        try
        {
            window.Show();
            var model = MorphApplicationTests.Model([]);
            var path = Path.Combine(AppContext.BaseDirectory, "timeline-test.pmx");
            var id = shell.Workspace.AddModel(path, EditorProjectWorkspace.CreateModelAsset(model, path), ModelRole.Character);
            shell.RefreshWorkspace(id); shell.Browser.SelectedBoneIndex = 0;
            shell.SceneSelectionIndex = 1;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var timeline = window.FindControl<EditorTimeline>("SceneFrameTimeline")!;
            var modelTimeline = window.FindControl<EditorTimeline>("FrameTimeline")!;
            Assert.True(timeline.IsEffectivelyVisible); Assert.False(modelTimeline.IsEffectivelyVisible);
            Assert.Equal(SceneTrackKind.Camera, timeline.SceneTrack!.Kind);
            var initialMotion = shell.Workspace.Session!.CaptureAuthoredContent().SceneMotion!.Motion;
            var initialCamera = initialMotion.CameraKeyframes.Select(key => key.Frame).Distinct().Order().ToArray();
            var initialLight = initialMotion.LightKeyframes.Select(key => key.Frame).Distinct().Order().ToArray();
            Assert.Equal(initialCamera, timeline.SceneTrack.Frames);
            shell.CurrentFrame = 7;
            var cameraButton = window.FindControl<Button>("TimelineRegisterCamera")!;
            Assert.True(cameraButton.IsEffectivelyVisible);
            Assert.Same(shell.SceneInspector.RegisterCameraKeyframeCommand, cameraButton.Command);
            cameraButton.Command!.Execute(null);
            Assert.True(shell.IsCameraInspector); Assert.Equal(initialCamera.Append(7u), timeline.SceneTrack.Frames);
            shell.SceneSelectionIndex = 2; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(SceneTrackKind.Light, timeline.SceneTrack!.Kind); Assert.Equal(initialLight, timeline.SceneTrack.Frames);
            Assert.False(cameraButton.IsEffectivelyVisible); Assert.True(window.FindControl<Button>("TimelineRegisterMainLight")!.IsEffectivelyVisible);
            shell.CurrentFrame = 19;
            timeline.Focus();
            var shortcut = typeof(EditorWindow).GetMethod("ExecuteEditorKey", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.True((bool)shortcut.Invoke(window, [Key.Enter, KeyModifiers.None, false])!);
            Assert.Equal(initialLight.Append(19u), timeline.SceneTrack.Frames);
            Assert.True(shell.Workspace.Undo()); Assert.Equal(initialLight, timeline.SceneTrack!.Frames);
            Assert.True(shell.Workspace.Redo()); Assert.Equal(initialLight.Append(19u), timeline.SceneTrack!.Frames);
            shell.SceneSelectionIndex = 1; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(initialCamera.Append(7u), timeline.SceneTrack!.Frames);
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            var position = new Point(timeline.TrackHeaderWidth + (7 - timeline.FirstVisibleFrame) * timeline.PixelsPerFrame + 2,
                EditorTimeline.RulerHeight + EditorTimeline.TrackRowHeight / 2);
            timeline.RaiseEvent(new PointerPressedEventArgs(timeline, pointer, window,
                timeline.TranslatePoint(position, window)!.Value, 1, new PointerPointProperties(RawInputModifiers.LeftMouseButton,
                    PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
            Assert.Equal(7, shell.CurrentFrame); Assert.Empty(shell.Animation.SelectedKeyframes);
            shell.RefreshWorkspace(id);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(modelTimeline.IsEffectivelyVisible); Assert.False(timeline.IsEffectivelyVisible);
            Assert.Equal(0, shell.Animation.SelectedBoneIndex);
            return true;
        }
        finally
        {
            typeof(EditorWindow).GetField("_closingConfirmed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
            window.Close(); Dispatcher.UIThread.RunJobs();
        }
    }, TestContext.Current.CancellationToken);

    [Fact]
    public void NativeLightsHaveNoVmdMainLightRow()
    {
        var inspector = new EditorSceneInspectorViewModel { SceneSelectionIndex = 2 };
        Assert.Equal(SceneTrackKind.Light, inspector.ActiveTimelineTrack!.Kind);
        inspector.SetPrimaryLightTrackActive(false);
        Assert.Null(inspector.ActiveTimelineTrack); Assert.False(inspector.ShowsPrimaryLightTrack);
        inspector.SceneSelectionIndex = 1;
        Assert.Equal(SceneTrackKind.Camera, inspector.ActiveTimelineTrack!.Kind);
    }

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
