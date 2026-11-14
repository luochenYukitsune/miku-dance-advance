using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.ViewOutput;

namespace Mmd.Modern.App;

/// <summary>Formal viewport light gestures. Borrows the workspace; transient previews never touch its history.</summary>
public sealed class EditorLightViewportController
{
    private readonly EditorProjectWorkspace workspace;
    private readonly Action<EditorLightingDraft> commit;
    private readonly Action requestRender;
    private SceneLightingEditor editor = new();
    private SceneProjectSession? sourceSession;
    private SceneLightingSnapshot? sourceSnapshot;
    private bool enabled = true, rotateHandle;
    private CapturedLights? captured;
    // Other decorators may replace PreviewFrame/EditorOverlay while retaining the emitted
    // primitive objects. Tag a light primitive so presentation still identifies this geometry.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ViewportOverlayLine, CapturedLights> awaitingPresentation = new();
    private Drag? drag;
    private sealed record CapturedLights(SceneProjectSession Session, SceneLightingSnapshot Source,
        SceneLightingSnapshot Lights, Guid? Selected, bool Rotate, SceneCamera Camera, int Width, int Height, float Size);
    private sealed record Drag(NativeSceneLight Light, Vector2 Start, SceneCamera Camera, int Width, int Height, LightDragAxis Axis, bool Rotate);

    public EditorLightViewportController(EditorProjectWorkspace workspace, Action<EditorLightingDraft> commit, Action requestRender)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.commit = commit ?? throw new ArgumentNullException(nameof(commit));
        this.requestRender = requestRender ?? throw new ArgumentNullException(nameof(requestRender));
    }
    public event EventHandler? Changed;
    /// <summary>The window commits pending light input before a gesture. Unregistered animation poses
    /// remain independent; native-light edits and their history do not discard or register them.</summary>
    public Func<bool>? CanBeginEdit { get; set; }
    public bool IsEnabled
    {
        get => enabled;
        set { if (enabled == value) return; CancelDrag(); enabled = value; captured = null; Notify(); }
    }
    public bool RotateHandle
    {
        get => rotateHandle;
        set { if (rotateHandle == value) return; CancelDrag(); rotateHandle = value; Notify(); }
    }
    public bool IsDragging => drag is not null;
    internal object PresentationIdentity => new PresentationKey(new(workspace.Session), new(sourceSnapshot),
        enabled, rotateHandle, editor.SelectedId, new(drag));
    private readonly record struct PresentationKey(EditorPresentationReference Session, EditorPresentationReference Source,
        bool Enabled, bool Rotate, Guid? Selected, EditorPresentationReference Gesture);
    public Guid? SelectedLightId => editor.SelectedId;
    public SceneLightingSnapshot PreviewLighting { get { Synchronize(); return editor.Snapshot; } }

    public void Select(Guid? id)
    {
        CancelDrag(); Synchronize(); editor.Select(id); Notify();
    }

    /// <summary>Run after the host resolves its manual camera. Picking waits for FramePresented.</summary>
    public PreviewFrame DecorateFrame(PreviewFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame); Synchronize();
        if (!IsEnabled || workspace.Session is not { } session || frame.Width <= 0 || frame.Height <= 0)
        { captured = null; return frame; }
        var camera = frame.ResolvedCamera ?? frame.Camera?.CreateCamera() ?? frame.Scene.Surface.Snapshot.Camera;
        var lights = editor.Snapshot;
        float size = GizmoSize(lights, editor.SelectedId, camera, frame.Width, frame.Height);
        var handles = LightViewportInteraction.CreateOverlay(lights, editor.SelectedId, camera, frame.Width, frame.Height, size, rotateHandle);
        var existing = frame.Scene.EditorOverlay?.Lines ?? [];
        var overlay = new ViewportOverlaySnapshot(existing.Take(ViewportOverlaySnapshot.MaxLines - handles.Lines.Count).Concat(handles.Lines));
        if (handles.Lines.Count > 0 && sourceSnapshot is { } source)
            awaitingPresentation.Add(handles.Lines[0], new(session, source, lights, editor.SelectedId,
                rotateHandle, camera, frame.Width, frame.Height, size));
        var scene = frame.Scene;
        return frame with { Scene = new(scene.Surface, scene.Outline, scene.OutlineOptions, scene.Options,
            scene.Materials, drag is null ? scene.Lighting : lights, overlay) };
    }

    /// <summary>Call only after successful native presentation. Dropped frames cannot move hit targets.</summary>
    public void FramePresented(PreviewFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!enabled) return;
        foreach (var line in frame.Scene.EditorOverlay?.Lines ?? [])
            if (awaitingPresentation.TryGetValue(line, out var pose))
            {
                awaitingPresentation.Remove(line);
                if (SourceCurrent(pose) && frame.Width == pose.Width && frame.Height == pose.Height) captured = pose;
                return;
            }
    }

    /// <summary>Call before orbit handling. Icons move directly; only the selected light's displayed
    /// axes/rings can transform it. A draft guard consumes a hit without changing the selection.</summary>
    public bool TryBeginDrag(int x, int y, int width, int height, SceneCamera camera, bool rotate = false)
    {
        ArgumentNullException.ThrowIfNull(camera);
        Synchronize();
        if (!enabled || workspace.Session is null || drag is not null || captured is not { } pose
            || width <= 0 || height <= 0 || width != pose.Width || height != pose.Height) return false;
        if (camera != pose.Camera || !pose.Lights.Lights.SequenceEqual(editor.Snapshot.Lights))
        { requestRender(); return false; }
        bool rotating = rotate || rotateHandle;
        var selected = pose.Selected == editor.SelectedId && pose.Rotate == rotating ? editor.SelectedId : null;
        var hit = LightViewportInteraction.HitTest(pose.Lights, selected, new(x, y), pose.Camera, width, height, pose.Size, rotating);
        if (hit is null) return false;
        if (CanBeginEdit?.Invoke() == false) return true;
        // A host guard may resolve another draft synchronously. Never carry its old hit
        // across the resulting project edit; the next presented frame will supply it.
        Synchronize();
        if (!SourceCurrent(pose)) { requestRender(); return true; }
        editor.Select(hit.LightId);
        var light = editor.Lights.Single(item => item.Id == hit.LightId);
        editor.BeginEdit(); drag = new(light, new(x, y), pose.Camera, width, height, hit.Axis,
            rotating && hit.Axis != LightDragAxis.Free); Notify(); return true;
    }

    public bool MoveDrag(int x, int y)
    {
        Synchronize();
        if (drag is not { } gesture) return false;
        var delta = new Vector2(x, y) - gesture.Start;
        var light = delta == Vector2.Zero ? gesture.Light : gesture.Rotate
            ? gesture.Light with { Rotation = LightViewportInteraction.RotateOnAxis(gesture.Light.Rotation, gesture.Light.Position,
                gesture.Start, new(x, y), gesture.Camera, gesture.Width, gesture.Height, gesture.Axis) }
            : gesture.Light with { Position = LightViewportInteraction.Translate(gesture.Light.Position, delta, gesture.Camera, gesture.Width, gesture.Height, gesture.Axis) };
        editor.Update(light); Notify(); return true;
    }

    /// <summary>Mouse release commits exactly once if content changed. Capture loss and Escape must call CancelDrag.</summary>
    public bool EndDrag()
    {
        Synchronize();
        if (drag is null) return false;
        drag = null; editor.CommitEdit();
        var result = new EditorLightingDraft(editor.Snapshot, editor.SelectedId);
        bool changed = sourceSnapshot is not null && !sourceSnapshot.Lights.SequenceEqual(result.Snapshot.Lights);
        try { if (changed) commit(result); }
        finally { sourceSnapshot = null; Synchronize(); Notify(); }
        return changed;
    }

    public void CancelDrag()
    {
        if (drag is null) return;
        drag = null; if (editor.IsEditing) editor.CancelEdit(); Notify();
    }

    /// <summary>Also call on project/Undo changes; an in-progress gesture cannot publish across an outside edit.</summary>
    public void Refresh() { Synchronize(); captured = null; Notify(); }
    private void Synchronize()
    {
        var session = workspace.Session;
        var snapshot = session?.Lighting ?? SceneLightingSnapshot.Empty;
        if (ReferenceEquals(session, sourceSession) && ReferenceEquals(snapshot, sourceSnapshot)) return;
        Guid? selected = editor.SelectedId;
        if (editor.IsEditing) editor.CancelEdit(); drag = null; captured = null;
        editor = new(); foreach (var light in snapshot.Lights) editor.Add(light);
        if (snapshot.Lights.Any(light => light.Id == selected)) editor.Select(selected); else editor.Select(null);
        sourceSession = session; sourceSnapshot = snapshot;
    }
    private bool SourceCurrent(CapturedLights pose) => ReferenceEquals(workspace.Session, pose.Session)
        && ReferenceEquals(workspace.Session?.Lighting, pose.Source);
    private static float GizmoSize(SceneLightingSnapshot lights, Guid? selected, SceneCamera camera, int width, int height)
    {
        var position = lights.Lights.FirstOrDefault(light => light.Id == selected)?.Position ?? camera.Target;
        var shifted = LightViewportInteraction.Translate(position, new(70, 0), camera, width, height);
        return Math.Clamp(Vector3.Distance(position, shifted), .001f, 100000f);
    }
    private void Notify() { Changed?.Invoke(this, EventArgs.Empty); requestRender(); }
}
