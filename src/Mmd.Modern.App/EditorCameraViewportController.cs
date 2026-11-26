using System.Numerics;
using System.Runtime.CompilerServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.ViewOutput;

namespace Mmd.Modern.App;

/// <summary>Viewport controller for the UE5-style selectable 3D scene camera component.</summary>
public sealed class EditorCameraViewportController
{
    private readonly EditorSceneInspectorViewModel sceneInspector;
    private readonly Func<PmmCameraPose, Matrix4x4?> resolveParentTransform;
    private readonly Func<bool> isSelected;
    private readonly Action selectCamera;
    private readonly Action enterPilotMode;
    private readonly Action redraw;

    private sealed record CapturedCamera(
        PmmCameraPose Pose,
        SceneCamera SceneCamera,
        SceneCamera ViewCamera,
        int Width,
        int Height,
        bool Selected);

    private sealed record DragState(
        PmmCameraPose InitialPose,
        SceneCamera InitialSceneCamera,
        Vector3 InitialAnchorWorld,
        Vector2 StartPointer,
        SceneCamera ViewCamera,
        int Width,
        int Height,
        CameraGizmoPart Part,
        bool HadDraft);

    private readonly ConditionalWeakTable<ViewportOverlayLine, CapturedCamera> awaiting = new();
    private CapturedCamera? presented;
    private DragState? drag;
    private PmmCameraPose? previewPose;
    private bool isPiloting;
    private bool helpersVisible = true;

    public EditorCameraViewportController(
        EditorSceneInspectorViewModel sceneInspector,
        Func<PmmCameraPose, Matrix4x4?> resolveParentTransform,
        Func<bool> isSelected,
        Action selectCamera,
        Action enterPilotMode,
        Action redraw)
    {
        this.sceneInspector = sceneInspector ?? throw new ArgumentNullException(nameof(sceneInspector));
        this.resolveParentTransform = resolveParentTransform ?? throw new ArgumentNullException(nameof(resolveParentTransform));
        this.isSelected = isSelected ?? throw new ArgumentNullException(nameof(isSelected));
        this.selectCamera = selectCamera ?? throw new ArgumentNullException(nameof(selectCamera));
        this.enterPilotMode = enterPilotMode ?? throw new ArgumentNullException(nameof(enterPilotMode));
        this.redraw = redraw ?? throw new ArgumentNullException(nameof(redraw));
    }

    public event EventHandler? Changed;

    public bool IsPiloting
    {
        get => isPiloting;
        set
        {
            if (isPiloting == value) return;
            CancelDrag();
            isPiloting = value;
            presented = null;
            Changed?.Invoke(this, EventArgs.Empty);
            redraw();
        }
    }

    public bool HelpersVisible
    {
        get => helpersVisible;
        set
        {
            if (helpersVisible == value) return;
            CancelDrag();
            helpersVisible = value;
            presented = null;
            Changed?.Invoke(this, EventArgs.Empty);
            redraw();
        }
    }

    public bool IsDragging => drag is not null;

    internal void ClearPresentation()
    {
        presented = null;
        awaiting.Clear();
    }

    public PreviewFrame Decorate(PreviewFrame frame, SceneCamera viewCamera)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(viewCamera);
        if (isPiloting || !helpersVisible) return frame;

        var pose = previewPose ?? sceneInspector.CameraPose;
        SceneCamera sceneCamera;
        try
        {
            var parent = pose.ParentModelIndex >= 0 ? resolveParentTransform(pose) : null;
            sceneCamera = pose.CreateCamera(parent);
        }
        catch (Exception)
        {
            return frame;
        }

        bool selected = isSelected();
        var activePart = drag?.Part ?? CameraGizmoPart.None;
        var gizmo = EditorCameraViewportInteraction.CreateOverlay(
            sceneCamera, pose, viewCamera, frame.Width, frame.Height, selected, activePart);
        if (gizmo.Lines.Count == 0) return frame;

        var scene = frame.Scene;
        var existing = scene.EditorOverlay?.Lines ?? [];
        int keepExisting = Math.Max(0, ViewportOverlaySnapshot.MaxLines - gizmo.Lines.Count);
        var merged = new ViewportOverlaySnapshot(existing.Take(keepExisting).Concat(gizmo.Lines));
        var decorated = frame with
        {
            Scene = new(scene.Surface, scene.Outline, scene.OutlineOptions, scene.Options,
                scene.Materials, scene.Lighting, merged)
        };
        awaiting.Add(gizmo.Lines[0], new CapturedCamera(pose, sceneCamera, viewCamera, frame.Width, frame.Height, selected));
        return decorated;
    }

    public void FramePresented(PreviewFrame frame)
    {
        presented = null;
        foreach (var line in frame.Scene.EditorOverlay?.Lines ?? [])
        {
            if (awaiting.TryGetValue(line, out var captured))
            {
                awaiting.Remove(line);
                presented = captured;
                return;
            }
        }
    }

    public bool TryBeginDrag(Vector2 pointer, SceneCamera viewCamera, int width, int height, bool isDoubleClick = false)
    {
        if (isPiloting || !helpersVisible || drag is not null || presented is not { } snapshot) return false;
        if (snapshot.Width != width || snapshot.Height != height) return false;
        if (snapshot.Pose != sceneInspector.CameraPose) return false;

        var hit = EditorCameraViewportInteraction.HitTest(
            pointer, snapshot.SceneCamera, snapshot.Pose, snapshot.ViewCamera, width, height, snapshot.Selected);
        if (hit is null) return false;

        selectCamera();

        if (isDoubleClick)
        {
            enterPilotMode();
            return true;
        }

        if (snapshot.Pose.ParentModelIndex >= 0)
        {
            redraw();
            return true;
        }

        var anchor = hit.Part == CameraGizmoPart.Target
            ? EditorCameraViewportInteraction.ResolveLookAtWorld(snapshot.Pose, snapshot.SceneCamera)
            : snapshot.SceneCamera.Eye;

        drag = new DragState(
            snapshot.Pose,
            snapshot.SceneCamera,
            anchor,
            pointer,
            snapshot.ViewCamera,
            width,
            height,
            hit.Part,
            sceneInspector.HasCameraDraft);
        previewPose = snapshot.Pose;
        redraw();
        return true;
    }

    public void MoveDrag(Vector2 pointer)
    {
        if (drag is not { } active) return;

        var axis = active.Part switch
        {
            CameraGizmoPart.AxisX => LightDragAxis.X,
            CameraGizmoPart.AxisY => LightDragAxis.Y,
            CameraGizmoPart.AxisZ => LightDragAxis.Z,
            _ => LightDragAxis.Free
        };

        var deltaPixels = pointer - active.StartPointer;
        var movedWorld = LightViewportInteraction.Translate(
            active.InitialAnchorWorld, deltaPixels, active.ViewCamera, active.Width, active.Height, axis);
        var worldDelta = movedWorld - active.InitialAnchorWorld;

        var updated = active.Part == CameraGizmoPart.Target
            ? EditorCameraViewportInteraction.AimAtTarget(active.InitialPose, movedWorld)
            : EditorCameraViewportInteraction.TranslateBody(active.InitialPose, worldDelta);

        if (previewPose == updated) return;
        previewPose = updated;
        sceneInspector.SetCameraDraft(updated);
        redraw();
    }

    public void EndDrag(Vector2 pointer)
    {
        if (drag is null) return;
        MoveDrag(pointer);
        drag = null;
        previewPose = null;
        redraw();
    }

    public void CancelDrag()
    {
        if (drag is not { } active) return;
        bool changed = previewPose.HasValue && previewPose.Value != active.InitialPose;
        drag = null;
        previewPose = null;
        if (changed)
        {
            if (active.HadDraft) sceneInspector.SetCameraDraft(active.InitialPose);
            else sceneInspector.RestoreCameraPoseWithoutDraft(active.InitialPose);
        }
        redraw();
    }
}
