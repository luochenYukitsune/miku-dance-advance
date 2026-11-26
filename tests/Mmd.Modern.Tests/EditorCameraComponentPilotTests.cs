using System.Numerics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorCameraComponentPilotTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);

    [Fact]
    public void TranslateBodyAndAimAtTargetPreservePmmCameraConvention()
    {
        var initial = new PmmCameraPose(new Vector3(0, 10, 0), new Vector3(0.15f, 0.35f, 0.1f), -45f, 35f, true);
        var beforeCamera = initial.CreateCamera();
        var delta = new Vector3(4.5f, -2.0f, 3.25f);

        var moved = EditorCameraViewportInteraction.TranslateBody(initial, delta);
        var movedCamera = moved.CreateCamera();
        Assert.True(Vector3.Distance(beforeCamera.Eye + delta, movedCamera.Eye) < 1e-3f);
        Assert.Equal(initial.Angle, moved.Angle);
        Assert.Equal(initial.Distance, moved.Distance);
        Assert.Equal(initial.Fov, moved.Fov);

        var newTarget = new Vector3(12f, 18f, 6f);
        var aimed = EditorCameraViewportInteraction.AimAtTarget(initial, newTarget);
        var aimedCamera = aimed.CreateCamera();
        Assert.True(Vector3.Distance(beforeCamera.Eye, aimedCamera.Eye) < 1e-3f);
        var expectedForward = Vector3.Normalize(newTarget - beforeCamera.Eye);
        var actualForward = Vector3.Normalize(aimedCamera.Target - aimedCamera.Eye);
        Assert.True(Vector3.Dot(expectedForward, actualForward) > 0.9999f);
        Assert.Equal(initial.Angle.Z, aimed.Angle.Z);

        // Straight-up and straight-down targets must keep Eye exact without horizontal drift.
        foreach (float verticalSign in new[] { 1f, -1f })
        {
            var verticalTarget = beforeCamera.Eye + new Vector3(0f, verticalSign * 15f, 0f);
            var verticalAimed = EditorCameraViewportInteraction.AimAtTarget(initial, verticalTarget);
            var verticalCamera = verticalAimed.CreateCamera();
            Assert.True(Vector3.Distance(beforeCamera.Eye, verticalCamera.Eye) < 1e-3f);
            Assert.True(Vector3.Dot(new Vector3(0f, verticalSign, 0f), Vector3.Normalize(verticalCamera.Target - verticalCamera.Eye)) > 0.9999f);
        }
    }

    [Fact]
    public void CreateOverlayAndHitTestSelectCameraBodyAndGizmoAxes()
    {
        var scenePose = new PmmCameraPose(new Vector3(0, 10, 0), Vector3.Zero, -45f, 30f, true);
        var sceneCamera = scenePose.CreateCamera();
        var obsPose = EditorCameraViewportInteraction.CreateObservationCameraAround(scenePose);
        var viewCamera = obsPose.CreateCamera();
        const int width = 1280, height = 720;

        var unselectedOverlay = EditorCameraViewportInteraction.CreateOverlay(sceneCamera, scenePose, viewCamera, width, height, selected: false);
        var selectedOverlay = EditorCameraViewportInteraction.CreateOverlay(sceneCamera, scenePose, viewCamera, width, height, selected: true);
        Assert.NotEmpty(unselectedOverlay.Lines);
        Assert.True(selectedOverlay.Lines.Count > unselectedOverlay.Lines.Count);

        Assert.True(LightViewportInteraction.TryProject(sceneCamera.Eye, viewCamera, width, height, out var eyePx));
        var bodyHit = EditorCameraViewportInteraction.HitTest(eyePx, sceneCamera, scenePose, viewCamera, width, height, selected: false);
        Assert.NotNull(bodyHit);
        Assert.Equal(CameraGizmoPart.Body, bodyHit.Part);

        var lookAt = EditorCameraViewportInteraction.ResolveLookAtWorld(scenePose, sceneCamera);
        Assert.True(LightViewportInteraction.TryProject(lookAt, viewCamera, width, height, out var targetPx));
        var targetHit = EditorCameraViewportInteraction.HitTest(targetPx, sceneCamera, scenePose, viewCamera, width, height, selected: true);
        Assert.NotNull(targetHit);
        Assert.Equal(CameraGizmoPart.Target, targetHit.Part);
    }

    [Fact]
    public void CameraControllerClickSelectsDoubleClickPilotsAndDragUpdatesDraft()
    {
        var scenePose = new PmmCameraPose(new Vector3(0, 10, 0), Vector3.Zero, -45f, 30f, true);
        var inspector = new EditorSceneInspectorViewModel(scenePose);
        var obsPose = EditorCameraViewportInteraction.CreateObservationCameraAround(scenePose);
        var viewCamera = obsPose.CreateCamera();
        const int width = 1280, height = 720;

        bool selected = false;
        int selectCalls = 0;
        int pilotCalls = 0;
        var controller = new EditorCameraViewportController(
            inspector,
            _ => null,
            isSelected: () => selected,
            selectCamera: () => { selected = true; selectCalls++; },
            enterPilotMode: () => pilotCalls++,
            redraw: () => { });

        var emptyScene = new OriginalFrameScene(new(SceneSnapshot.CreateEmpty(viewCamera), []));
        var rawFrame = new PreviewFrame(width, height, 0, emptyScene, obsPose, null, null) { ResolvedCamera = viewCamera };
        var decorated = controller.Decorate(rawFrame, viewCamera);
        Assert.NotNull(decorated.Scene.EditorOverlay);
        Assert.NotEmpty(decorated.Scene.EditorOverlay!.Lines);
        controller.FramePresented(decorated);

        Assert.True(LightViewportInteraction.TryProject(scenePose.CreateCamera().Eye, viewCamera, width, height, out var eyePx));
        Assert.True(controller.TryBeginDrag(eyePx, viewCamera, width, height, isDoubleClick: false));
        Assert.Equal(1, selectCalls);
        Assert.Equal(0, pilotCalls);
        Assert.True(controller.IsDragging);

        controller.MoveDrag(eyePx + new Vector2(40, -15));
        Assert.True(inspector.HasCameraDraft);
        Assert.NotEqual(scenePose, inspector.CameraPose);

        controller.CancelDrag();
        Assert.False(controller.IsDragging);
        Assert.Equal(scenePose, inspector.CameraPose);
        Assert.False(inspector.HasCameraDraft);

        // When a draft already existed before dragging, CancelDrag restores the draft and keeps HasCameraDraft true.
        var priorDraft = scenePose with { LookAt = scenePose.LookAt + new Vector3(1f, 0f, 0f) };
        inspector.SetCameraDraft(priorDraft);
        Assert.True(inspector.HasCameraDraft);
        // Stale snapshot (before re-decorating/presenting priorDraft) must reject TryBeginDrag.
        Assert.False(controller.TryBeginDrag(eyePx, viewCamera, width, height, isDoubleClick: false));

        var draftDecorated = controller.Decorate(rawFrame, viewCamera);
        controller.FramePresented(draftDecorated);
        Assert.True(LightViewportInteraction.TryProject(priorDraft.CreateCamera().Eye, viewCamera, width, height, out var priorEyePx));
        // Even if the live viewCamera moved before the next frame presented, TryBeginDrag uses the presented snapshot camera.
        var shiftedLiveCamera = (obsPose with { LookAt = obsPose.LookAt + new Vector3(20f, 0f, 0f) }).CreateCamera();
        Assert.True(controller.TryBeginDrag(priorEyePx, shiftedLiveCamera, width, height, isDoubleClick: false));
        controller.MoveDrag(priorEyePx + new Vector2(30, -10));
        Assert.NotEqual(priorDraft, inspector.CameraPose);
        controller.CancelDrag();
        Assert.Equal(priorDraft, inspector.CameraPose);
        Assert.True(inspector.HasCameraDraft);

        // Double-click enters pilot mode immediately without starting a drag.
        Assert.True(controller.TryBeginDrag(priorEyePx, viewCamera, width, height, isDoubleClick: true));
        Assert.Equal(1, pilotCalls);
        Assert.False(controller.IsDragging);

        // When piloting, gizmo overlay is hidden and cannot be picked.
        controller.IsPiloting = true;
        var pilotingFrame = controller.Decorate(rawFrame, viewCamera);
        Assert.Null(pilotingFrame.Scene.EditorOverlay);
        Assert.False(controller.TryBeginDrag(priorEyePx, viewCamera, width, height, isDoubleClick: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecoratePreservesMainLightAndCameraPresentationCorrelationInEitherOrder(bool cameraFirst)
    {
        var scenePose = new PmmCameraPose(new Vector3(0, 10, 0), Vector3.Zero, -45f, 30f, true);
        var inspector = new EditorSceneInspectorViewModel(scenePose);
        var obsPose = EditorCameraViewportInteraction.CreateObservationCameraAround(scenePose);
        var viewCamera = obsPose.CreateCamera();
        const int width = 800, height = 600;

        var mainLight = new EditorMainLightViewport(inspector, () => true, () => { });
        var cameraCtrl = new EditorCameraViewportController(
            inspector, _ => null, () => false, () => { }, () => { }, () => { });

        var rawFrame = new PreviewFrame(width, height, 0,
            new(new(SceneSnapshot.CreateEmpty(viewCamera), [])), obsPose, null, null) { ResolvedCamera = viewCamera };

        var decorated = cameraFirst
            ? mainLight.Decorate(cameraCtrl.Decorate(rawFrame, viewCamera), viewCamera)
            : cameraCtrl.Decorate(mainLight.Decorate(rawFrame, viewCamera), viewCamera);

        mainLight.FramePresented(decorated);
        cameraCtrl.FramePresented(decorated);

        var mainLightSource = mainLight.Decorate(rawFrame, viewCamera).Scene.EditorOverlay!.Lines
            .First(line => line.Shape == ViewportOverlayShape.Circle).Start * new Vector2(width, height);
        Assert.True(mainLight.TryBegin(mainLightSource, viewCamera, width, height));
        mainLight.Cancel();

        Assert.True(LightViewportInteraction.TryProject(scenePose.CreateCamera().Eye, viewCamera, width, height, out var eyePx));
        Assert.True(cameraCtrl.TryBeginDrag(eyePx, viewCamera, width, height));
        cameraCtrl.CancelDrag();
    }

    [Fact]
    public async Task EditorWindowCameraPilotToggleBannerOutlineDoubleClickAndEscExit() => await session.Dispatch(() =>
    {
        string previousLanguage = EditorLocalization.CurrentLanguage;
        var window = new EditorWindow(false);
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.IsSuspended = true;
        viewport.IsVisible = false;
        var shell = new EditorShellViewModel();
        window.DataContext = shell;
        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var pilotToggle = window.FindControl<ToggleButton>("ViewportCameraPilotToggle")!;
            var sceneInspectorView = window.FindControl<EditorSceneInspector>("SceneInspectorView")!;
            var helpersToggle = sceneInspectorView.FindControl<ToggleButton>("CameraHelpersToggle")!;
            var banner = window.FindControl<Border>("CameraPilotBanner")!;
            var inspectorToggle = sceneInspectorView.FindControl<ToggleButton>("CameraInspectorPilotToggle")!;
            var outlineCamera = window.FindControl<ListBoxItem>("OutlineCamera")!;

            // Starts in UE5-style Free View so the 3D camera actor is visible & selectable.
            Assert.False(viewport.IsPilotingCamera);
            Assert.False(pilotToggle.IsChecked);
            Assert.False(banner.IsVisible);
            Assert.True(helpersToggle.IsEnabled);
            Assert.True(helpersToggle.IsChecked);

            // Toggle Pilot Camera ON -> enters MMD camera control mode & selects camera.
            pilotToggle.IsChecked = true;
            pilotToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(viewport.IsPilotingCamera);
            Assert.Equal(1, shell.SceneSelectionIndex);
            Assert.True(banner.IsVisible);
            Assert.False(helpersToggle.IsEnabled);
            Assert.True(inspectorToggle.IsChecked);

            // Language switches keep inspector pilot toggle localized.
            foreach (string lang in new[] { "en", "ja", "zh-CN" })
            {
                EditorLocalization.SetLanguage(lang);
                Assert.Equal(EditorLocalization.Text("Mda.Text.CameraPilot.Exit"), inspectorToggle.Content);
            }

            // While piloting, switching inspector context away from camera still routes viewport Enter/K to camera keyframing.
            int cameraRegisterCalls = 0;
            shell.SceneInspector.CanMutateProject = () => true;
            shell.SceneInspector.CommitSceneMotionWithCameraParent = (_, _, camera) => { if (camera.HasValue) cameraRegisterCalls++; };
            shell.SceneSelectionIndex = -1;
            Assert.False(shell.IsCameraInspector);
            var executeKey = typeof(EditorWindow).GetMethod("ExecuteEditorKey",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            Assert.True((bool)executeKey.Invoke(window, [Key.K, KeyModifiers.None, true])!);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, cameraRegisterCalls);

            // Pressing Esc from viewport exits Pilot Mode back to Free View.
            Assert.True((bool)executeKey.Invoke(window, [Key.Escape, KeyModifiers.None, true])!);
            Dispatcher.UIThread.RunJobs();
            Assert.False(viewport.IsPilotingCamera);
            Assert.False(banner.IsVisible);
            Assert.True(helpersToggle.IsEnabled);
            Assert.NotNull(viewport.FreeEditorCameraPose);

            // Double-clicking OutlineCamera enters Pilot Mode.
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            outlineCamera.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent,
                new PointerPressedEventArgs(outlineCamera, pointer, window, default, 2,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                    KeyModifiers.None)));
            Dispatcher.UIThread.RunJobs();
            Assert.True(viewport.IsPilotingCamera);
            Assert.True(banner.IsVisible);

            // Clicking banner Exit button returns to Free View.
            window.FindControl<Button>("CameraPilotBannerExitButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(viewport.IsPilotingCamera);
            Assert.False(banner.IsVisible);
            return true;
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            EditorLocalization.SetLanguage(previousLanguage);
        }
    }, TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => new(Task.Run(session.Dispose));
}
