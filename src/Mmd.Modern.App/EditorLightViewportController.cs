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
    private readonly bool modernLightingWorkspace;
    private SceneLightingEditor editor = new();
    private SceneProjectSession? sourceSession;
    private SceneLightingSnapshot? sourceSnapshot;
    private bool enabled = true, rotateHandle, helpersVisible = true;
    private CapturedLights? captured;
    // Other decorators may replace PreviewFrame/EditorOverlay while retaining the emitted
    // primitive objects. Tag a light primitive so presentation still identifies this geometry.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ViewportOverlayLine, CapturedLights> awaitingPresentation = new();
    private Drag? drag;
    private Vector2 lastDragPointer;
    private SceneLightingSnapshot? parameterPreview;
    private IReadOnlyList<Guid> selectedIds = Array.Empty<Guid>();
    private object selectionIdentity = new();
    private sealed record CapturedLights(SceneProjectSession Session, SceneLightingSnapshot Source,
        SceneLightingSnapshot Lights, double SourceFrame, Guid? Selected, IReadOnlyList<Guid> SelectedIds, bool Rotate, SceneCamera Camera, int Width, int Height, float Size);
    private sealed record Drag(IReadOnlyList<NativeSceneLight> Lights, IReadOnlyList<NativeSceneLight> Authored,
        IReadOnlyDictionary<SceneLightParent, Matrix4x4> Parents, double SourceFrame, Vector3 Pivot, Vector3 TranslationAnchor, Vector2 Start,
        SceneCamera Camera, int Width, int Height, LightDragAxis Axis, bool Rotate, bool Target = false);

    public EditorLightViewportController(EditorProjectWorkspace workspace, Action<EditorLightingDraft> commit, Action requestRender,
        bool modernLightingWorkspace = true)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.commit = commit ?? throw new ArgumentNullException(nameof(commit));
        this.requestRender = requestRender ?? throw new ArgumentNullException(nameof(requestRender));
        this.modernLightingWorkspace = modernLightingWorkspace;
    }
    public event EventHandler? Changed;
    /// <summary>The window commits pending light input before a gesture. Unregistered animation poses
    /// remain independent; native-light edits and their history do not discard or register them.</summary>
    public Func<bool>? CanBeginEdit { get; set; }
    public bool IsEnabled
    {
        get => enabled;
        set { if (enabled == value) return; CancelDrag(); parameterPreview = null; enabled = value; captured = null; Notify(); }
    }
    public bool RotateHandle
    {
        get => rotateHandle;
        set { if (rotateHandle == value) return; CancelDrag(); parameterPreview = null; rotateHandle = value; Notify(); }
    }
    public bool IsDragging => drag is not null;
    /// <summary>View-local presentation only. Hidden helpers never disable the real lights.</summary>
    public bool HelpersVisible
    {
        get => helpersVisible;
        set { if (helpersVisible == value) return; CancelDrag(); helpersVisible = value; captured = null; Notify(); }
    }
    public bool IsInteracting { get { Synchronize(); return enabled && (drag is not null || parameterPreview is not null); } }
    /// <summary>Preview work borrows this neutral per-view override. Author preferences and
    /// independent capture/export requests continue to use their own complete quality settings.</summary>
    public ViewQualityOptions InteractionQuality(ViewQualityOptions quality)
    {
        ArgumentNullException.ThrowIfNull(quality);
        return modernLightingWorkspace && IsInteracting && quality.EffectiveGi.Enabled ? quality with { Gi = RayGiOptions.Off } : quality;
    }
    internal object PresentationIdentity => new PresentationKey(new(workspace.Session), new(sourceSnapshot),
        enabled, helpersVisible, rotateHandle, editor.SelectedId, new(selectionIdentity), new(drag), new(parameterPreview ?? (drag is null ? null : editor.Snapshot)));
    private readonly record struct PresentationKey(EditorPresentationReference Session, EditorPresentationReference Source,
        bool Enabled, bool Helpers, bool Rotate, Guid? Selected, EditorPresentationReference Selection,
        EditorPresentationReference Gesture, EditorPresentationReference Parameters);
    public Guid? SelectedLightId => editor.SelectedId;
    public IReadOnlyList<Guid> SelectedLightIds => selectedIds;
    internal SceneLightingSnapshot? TransientLighting => parameterPreview ?? (drag is null ? null : editor.Snapshot);
    public SceneLightingSnapshot PreviewLighting { get { Synchronize(); return parameterPreview ?? editor.Snapshot; } }

    /// <summary>Inspector gestures use the same renderer decorator as viewport gestures. This slot
    /// is transient and is invalidated with the owning session/committed snapshot.</summary>
    public bool SetParameterPreview(EditorLightingDraft? draft)
    {
        Synchronize();
        if (draft is not null && (workspace.Session is null || drag is not null
            || !draft.Snapshot.Lights.Select(light => light.Id).SequenceEqual(editor.Lights.Select(light => light.Id)))) return false;
        parameterPreview = draft?.Snapshot;
        // Keep hit-testing the displayed frame while a newer inspector preview is queued.
        requestRender();
        return true;
    }

    public void Select(Guid? id)
    {
        SelectMany(id is { } selected ? [selected] : [], id);
    }

    public void SelectMany(IEnumerable<Guid> ids, Guid? primary = null)
    {
        ArgumentNullException.ThrowIfNull(ids);
        Synchronize();
        var requested = ids.ToHashSet();
        if (requested.Any(id => editor.Lights.All(light => light.Id != id))
            || primary is { } selected && !requested.Contains(selected))
            throw new ArgumentException("The light selection must belong to the current scene.", nameof(ids));
        var selection = editor.Lights.Where(light => requested.Contains(light.Id)).Select(light => light.Id).ToArray();
        primary ??= selection.Length > 0 ? selection[0] : null;
        if (selectedIds.SequenceEqual(selection) && editor.SelectedId == primary) return;
        CancelDrag(); parameterPreview = null; editor.Select(primary);
        selectedIds = Array.AsReadOnly(selection); selectionIdentity = new(); captured = null; Notify();
    }

    /// <summary>Run after the host resolves its manual camera. Picking waits for FramePresented.</summary>
    public PreviewFrame DecorateFrame(PreviewFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame); Synchronize();
        if (!IsEnabled || workspace.Session is not { } session || frame.Width <= 0 || frame.Height <= 0)
        { captured = null; return frame; }
        var camera = frame.ResolvedCamera ?? frame.Camera?.CreateCamera() ?? frame.Scene.Surface.Snapshot.Camera;
        var authored = parameterPreview ?? editor.Snapshot;
        // Frame-owned matrices keep markers, lighting and picking on the same sampled pose,
        // including an unregistered pose preview. Never ask the live solver for a later pose here.
        var sampled = frame.Scene.Lighting;
        if (authored.Lights.Any(light => light.Parent is not null)
            && (sampled is null || !sampled.AuthoredLighting.Lights.SequenceEqual(sourceSnapshot!.Lights)
                || !sampled.AuthoredLighting.Groups.SequenceEqual(sourceSnapshot.Groups)))
        { captured = null; return frame; }
        var lights = authored.ResolveParents(parent => sampled!.ParentTransforms[parent]);
        var pivot = SelectionPivot(lights);
        float size = GizmoSize(pivot ?? camera.Target, camera, frame.Width, frame.Height);
        var handles = helpersVisible ? LightViewportInteraction.CreateOverlay(lights, editor.SelectedId, camera, frame.Width, frame.Height,
            size, rotateHandle, selectedIds, pivot, showDetails: modernLightingWorkspace) : new ViewportOverlaySnapshot([]);
        var existing = frame.Scene.EditorOverlay?.Lines ?? [];
        var overlay = new ViewportOverlaySnapshot(existing.Take(ViewportOverlaySnapshot.MaxLines - handles.Lines.Count).Concat(handles.Lines));
        if (handles.Lines.Count > 0 && sourceSnapshot is { } source)
            awaitingPresentation.Add(handles.Lines[0], new(session, source, lights, frame.SourceFrame, editor.SelectedId, selectedIds,
                rotateHandle, camera, frame.Width, frame.Height, size));
        var scene = frame.Scene;
        bool interacting = modernLightingWorkspace && (drag is not null || parameterPreview is not null);
        // Keep same-frame parent metadata and receiver filters intact. Shadow simplification
        // is a rendering copy only; picking/gesture identities above keep the original lights.
        var renderLights = interacting
            ? authored.WithLights(authored.Lights.Select(light => light with { CastShadows = false }))
                .ResolveParents(parent => sampled!.ParentTransforms[parent])
            : drag is null && parameterPreview is null ? scene.Lighting : lights;
        return frame with { Scene = new(scene.Surface, scene.Outline, scene.OutlineOptions, scene.Options,
            scene.Materials, renderLights, overlay) };
    }

    /// <summary>Call only after successful native presentation. Dropped frames cannot move hit targets.</summary>
    public void FramePresented(PreviewFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        captured = null;
        if (!enabled || !helpersVisible) return;
        foreach (var line in frame.Scene.EditorOverlay?.Lines ?? [])
            if (awaitingPresentation.TryGetValue(line, out var pose))
            {
                awaitingPresentation.Remove(line);
                if (SourceCurrent(pose) && frame.Width == pose.Width && frame.Height == pose.Height) captured = pose;
                return;
            }
    }

    /// <summary>Call before orbit handling. Light bodies follow the active move or rotation tool. Selected axes and rings constrain the gesture. A draft guard consumes a hit without changing the selection.</summary>
    public bool TryBeginDrag(int x, int y, int width, int height, SceneCamera camera, bool rotate = false)
    {
        ArgumentNullException.ThrowIfNull(camera);
        Synchronize();
        if (!enabled || !helpersVisible || workspace.Session is null || drag is not null || captured is not { } pose
            || width <= 0 || height <= 0 || width != pose.Width || height != pose.Height) return false;
        if (camera != pose.Camera || !SourceCurrent(pose))
        { requestRender(); return false; }
        bool rotating = rotate || rotateHandle;
        var selected = pose.Selected == editor.SelectedId && pose.SelectedIds.SequenceEqual(selectedIds)
            && pose.Rotate == rotating ? editor.SelectedId : null;
        var hit = LightViewportInteraction.HitTest(pose.Lights, selected, new(x, y), pose.Camera, width, height,
            pose.Size, rotating, SelectionPivot(pose.Lights), selected is null ? null : pose.SelectedIds);
        if (hit is null) return false;
        if (CanBeginEdit?.Invoke() == false) return true;
        // A host guard may resolve another draft synchronously. Never carry its old hit
        // across the resulting project edit; the next presented frame will supply it.
        Synchronize();
        // A focused numeric field can put the displayed marker at a draft position.
        // Let its hit reach the commit guard before comparing it with the document.
        if (!SourceCurrent(pose) || parameterPreview is not null
            || !pose.Lights.AuthoredLighting.Lights.SequenceEqual(editor.Snapshot.Lights)
            || !pose.Lights.AuthoredLighting.Groups.SequenceEqual(editor.Snapshot.Groups))
        { requestRender(); return true; }
        if (!selectedIds.Contains(hit.LightId)) Select(hit.LightId);
        var authored = editor.Lights.Where(light => selectedIds.Contains(light.Id)).ToArray();
        var originals = pose.Lights.Lights.Where(light => selectedIds.Contains(light.Id)).ToArray();
        // A first click can switch from a rotating spot to an unselected point light.
        if (originals is [{ Kind: SceneLightKind.Point }]) rotating = false;
        var pivot = SelectionPivot(pose.Lights)!.Value;
        var anchor = pivot;
        if (!rotating && hit.Axis != LightDragAxis.Free
            && LightViewportInteraction.TryProject(pivot, pose.Camera, width, height, out var center))
            anchor = LightViewportInteraction.Translate(pivot, new Vector2(x, y) - center,
                pose.Camera, width, height, hit.Axis);
        lastDragPointer = new(x, y);
        editor.BeginEdit(); drag = new(originals, authored, pose.Lights.ParentTransforms, pose.SourceFrame,
            pivot, anchor, new(x, y), pose.Camera, width, height, hit.Axis,
            rotating && !hit.Target, hit.Target); Notify(); return true;
    }

    public bool MoveDrag(int x, int y)
    {
        Synchronize();
        if (drag is not { } gesture) return false;
        var pointer = new Vector2(x, y);
        if (pointer == lastDragPointer) return true;
        var delta = pointer - gesture.Start;
        var rotation = delta == Vector2.Zero || !gesture.Rotate ? Quaternion.Identity
            : gesture.Axis == LightDragAxis.Free ? LightViewportInteraction.Rotate(Quaternion.Identity, delta, gesture.Camera)
            : LightViewportInteraction.RotateOnAxis(Quaternion.Identity, gesture.Pivot,
                gesture.Start, new(x, y), gesture.Camera, gesture.Width, gesture.Height, gesture.Axis);
        var translation = delta == Vector2.Zero || gesture.Rotate || gesture.Target ? Vector3.Zero
            : LightViewportInteraction.Translate(gesture.TranslationAnchor, delta, gesture.Camera, gesture.Width, gesture.Height, gesture.Axis) - gesture.TranslationAnchor;
        var replacements = new Dictionary<Guid, NativeSceneLight>();
        SceneLightingSnapshot next;
        try
        {
          foreach (var original in gesture.Lights)
          {
            var light = delta == Vector2.Zero ? original : gesture.Target
                ? SafeAimAt(original, LightViewportInteraction.Translate(original.Target, delta, gesture.Camera, gesture.Width, gesture.Height), original)
                : gesture.Rotate
                ? original with { Position = gesture.Lights.Count == 1 ? original.Position
                    : gesture.Pivot + Vector3.Transform(original.Position - gesture.Pivot, rotation),
                    Rotation = Quaternion.Normalize(rotation * original.Rotation) }
                : original with { Position = original.Position + translation };
            var authored = gesture.Authored.Single(source => source.Id == original.Id);
            // A zero-motion gesture preserves even the original quaternion sign and exact
            // local floats. Actual world-space transforms return through the captured parent.
            var local = delta == Vector2.Zero ? authored : authored.Parent is { } parent
                ? SceneLightTransform.ToLocal(light, parent, gesture.Parents[parent]) : light;
            if (gesture.Target || (gesture.Rotate && gesture.Lights.Count == 1))
                local = local with { Position = authored.Position };
            else if (!gesture.Rotate)
                local = local with { Rotation = authored.Rotation };
            replacements.Add(light.Id, local);
          }
          // Validate the whole selection before publishing any member. A coordinate overflow
          // cannot leave half a group at the new transform and half at the previous one.
          next = editor.Snapshot.WithLights(editor.Lights.Select(light => replacements.GetValueOrDefault(light.Id, light)));
        }
        catch (ArgumentException) { return false; }
        editor.ReplaceSnapshot(next);
        lastDragPointer = pointer;
        requestRender(); return true;
    }

    private static NativeSceneLight SafeAimAt(NativeSceneLight light, Vector3 candidateTarget, NativeSceneLight reference)
    {
        var delta = candidateTarget - light.Position;
        if (delta.LengthSquared() >= 0.05f * 0.05f) return SceneLightAim.AimAt(light, candidateTarget);
        if (delta.LengthSquared() >= 1e-10f)
            return SceneLightAim.AimAt(light, light.Position + Vector3.Normalize(delta) * 0.05f);
        var fallbackDir = reference.Target - reference.Position;
        var unit = fallbackDir.LengthSquared() >= 1e-10f ? Vector3.Normalize(fallbackDir) : Vector3.UnitZ;
        return SceneLightAim.AimAt(light, light.Position + unit * 0.05f);
    }

    /// <summary>Mouse release commits exactly once if content changed. Capture loss and Escape must call CancelDrag.</summary>
    public bool EndDrag()
    {
        Synchronize();
        if (drag is null) return false;
        drag = null; editor.CommitEdit();
        var result = new EditorLightingDraft(editor.Snapshot, editor.SelectedId, selectedIds);
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
        if (drag is { } moving && session?.CurrentFrame != moving.SourceFrame) CancelDrag();
        var snapshot = session?.Lighting ?? SceneLightingSnapshot.Empty;
        if (ReferenceEquals(session, sourceSession) && ReferenceEquals(snapshot, sourceSnapshot)) return;
        Guid? selected = editor.SelectedId;
        if (editor.IsEditing) editor.CancelEdit(); drag = null; parameterPreview = null; captured = null;
        editor = new(); editor.ReplaceSnapshot(snapshot);
        selectedIds = Array.AsReadOnly(snapshot.Lights.Where(light => selectedIds.Contains(light.Id)).Select(light => light.Id).ToArray());
        if (selected is { } primary && selectedIds.Contains(primary)) editor.Select(primary);
        else editor.Select(selectedIds.Count > 0 ? selectedIds[0] : null);
        selectionIdentity = new();
        sourceSession = session; sourceSnapshot = snapshot;
    }
    private bool SourceCurrent(CapturedLights pose) => ReferenceEquals(workspace.Session, pose.Session)
        && ReferenceEquals(workspace.Session?.Lighting, pose.Source)
        && (pose.Lights.ParentTransforms.Count == 0 || workspace.Session?.CurrentFrame == pose.SourceFrame);
    private Vector3? SelectionPivot(SceneLightingSnapshot lights)
    {
        var positions = lights.Lights.Where(light => selectedIds.Contains(light.Id)).Select(light => light.Position).ToArray();
        if (positions.Length == 0) return null;
        return new((float)(positions.Sum(point => (double)point.X) / positions.Length),
            (float)(positions.Sum(point => (double)point.Y) / positions.Length),
            (float)(positions.Sum(point => (double)point.Z) / positions.Length));
    }
    private static float GizmoSize(Vector3 position, SceneCamera camera, int width, int height)
    {
        var shifted = LightViewportInteraction.Translate(position, new(70, 0), camera, width, height);
        return Math.Clamp(Vector3.Distance(position, shifted), .001f, 100000f);
    }
    private void Notify() { Changed?.Invoke(this, EventArgs.Empty); requestRender(); }
}
