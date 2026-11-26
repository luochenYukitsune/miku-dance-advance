using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.ViewOutput;

namespace Mmd.Modern.App;

/// <summary>Editor-only solved-bone guides and raw local-animation gestures. The borrowed project,
/// source model and keyframe history are never changed by this controller.</summary>
public sealed partial class EditorBoneViewportController
{
    private readonly EditorShellViewModel shell;
    private readonly Action requestRender;
    private bool enabled = true, showBones = true, showSelectionBounds, rotateHandle, preferredRotateHandle, manipulate;
    private BoneEditingSpace editingSpace = BoneEditingSpace.Local;
    private CapturedPose? captured;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, CapturedPose> awaitingPresentation = new();
    private Drag? drag;
    private bool dragEdited;
    private Vector2 lastDragPoint;
    private PmxModel? visibilityModel;
    private HashSet<int> physicsDrivenBones = [];
    private HashSet<int> fixedAxisMarkerOnlyBones = [];
    private IReadOnlySet<int> hiddenPhysicsBones = new HashSet<int>();
    private sealed record CapturedPose(SceneProjectSession Session, long Revision, int Frame, EditorModelItem Model,
        PreviewFrame RenderFrame, SceneObjectRenderInputs Inputs, IReadOnlyList<BoneCenter> BoneCenters,
        IReadOnlyList<BoneSegment> BoneSegments, IReadOnlyList<BoneHandle> BoneHandles);
    private sealed record BoneCenter(int Index, Vector2 Point, Vector3 WorldPoint);
    private sealed record BoneSegment(int Index, Vector2 Start, Vector2 End, Vector3 WorldStart, Vector3 WorldEnd);
    private sealed record BoneHandle(int Index, LightDragAxis Axis, bool Rotate, BoneEditingSpace Space, Vector2? Corner = null);
    private sealed record Drag(CapturedPose Captured, EditorAnimationViewModel.ViewportBoneDraftState Before,
        BoneEditingBasis Basis, float ParentScale, Vector2 Start, SceneCamera Camera, int Width, int Height,
        LightDragAxis Axis, bool Rotate, bool Corner);

    public EditorBoneViewportController(EditorShellViewModel shell, Action requestRender)
    {
        this.shell = shell ?? throw new ArgumentNullException(nameof(shell));
        this.requestRender = requestRender ?? throw new ArgumentNullException(nameof(requestRender));
    }

    public event EventHandler? Changed;
    /// <summary>Resolve other pending editor inputs before changing the selected bone. A refused
    /// hit is consumed so it cannot accidentally orbit the camera.</summary>
    public Func<bool>? CanBeginInteraction { get; set; }
    public bool IsEnabled
    {
        get => enabled;
        set
        {
            if (enabled == value) return;
            CancelDrag();
            enabled = value;
            captured = null;
            awaitingPresentation.Clear();
            Notify();
        }
    }
    public bool ShowBones { get => showBones; set { if (showBones == value) return; CancelDrag(); showBones = value; captured = null; Notify(); } }
    public bool ShowSelectionBounds { get => showSelectionBounds; set { if (showSelectionBounds == value) return; showSelectionBounds = value; Notify(); } }
    public bool IsManipulationEnabled
    {
        get => manipulate;
        set { if (manipulate == value) return; CancelDrag(); manipulate = value; Notify(); }
    }
    public bool RotateHandle
    {
        get => rotateHandle;
        set
        {
            bool effective = ResolveRotateHandle(value);
            if (preferredRotateHandle == value && rotateHandle == effective) return;
            CancelDrag(); preferredRotateHandle = value; rotateHandle = effective; Notify();
        }
    }
    public BoneEditingSpace EditingSpace
    {
        get => editingSpace;
        set { if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); if (editingSpace == value) return; CancelDrag(); editingSpace = value; Notify(); }
    }
    public bool IsDragging => drag is not null;
    internal bool HasPointerGesture => IsDragging || pendingPick is not null;
    // Raw draft values are represented by the immutable snapshot identity. Selection and
    // gesture provenance must still differ even when their emitted pixels happen to match.
    internal object PresentationIdentity => new PresentationKey(new(shell.Workspace.Session), shell.ContentRevision,
        shell.CurrentFrame, new(shell.Browser.SelectedModel), shell.Animation.ObjectId, shell.Animation.SelectedBoneIndex, new(shell.Animation.SelectedBoneIndices),
        enabled, showBones, showSelectionBounds, manipulate, rotateHandle, editingSpace, new(drag));
    private readonly record struct PresentationKey(EditorPresentationReference Session, long Revision, int Frame,
        EditorPresentationReference Model, Guid? ObjectId, int? BoneIndex, EditorPresentationReference Selection, bool Enabled, bool ShowBones,
        bool ShowBounds, bool Manipulate, bool Rotate, BoneEditingSpace Space, EditorPresentationReference Gesture);

    /// <summary>Merge with the renderer's requests before the host captures its live or detached pose.
    /// Texture requests retain their required/optional distinction; the selected instance's real PMX
    /// table supplies every bone index, including bones omitted from PMX display groups.</summary>
    public IReadOnlyList<SceneObjectRenderRequest> SnapshotRequests(IEnumerable<SceneObjectRenderRequest> rendererRequests)
    {
        ArgumentNullException.ThrowIfNull(rendererRequests);
        var requests = rendererRequests.ToList();
        var selected = shell.Browser.SelectedModel;
        if (!enabled || (!showBones && !showSelectionBounds) || selected is null || shell.Workspace.Session is null
            || !shell.Workspace.Motions.Any(actor => actor.ObjectId == selected.ObjectId)) return requests;
        var existing = requests.FirstOrDefault(request => request.ObjectId == selected.ObjectId);
        requests.RemoveAll(request => request.ObjectId == selected.ObjectId);
        requests.Add(new(selected.ObjectId,
            (existing?.BoneIndices ?? []).Concat(Enumerable.Range(0, selected.PmxModel.Bones.Count)),
            existing?.LocalTextureIndices, existing?.OptionalLocalTextureIndices,
            showSelectionBounds || existing?.IncludeSourceGeometry == true));
        return requests.AsReadOnly();
    }

    /// <summary>The renderer composites these editor lines after the final scene filter. They never
    /// enter scene lighting, reconstruction history or the export/capture path.</summary>
    public PreviewFrame DecorateFrame(PreviewFrame frame, bool publishPicking = true, bool lightDragging = false)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Synchronize();
        var lastPresented = captured;
        var selected = shell.Browser.SelectedModel;
        if (lightDragging || !enabled || (!showBones && !showSelectionBounds) || selected is null || shell.Workspace.Session is not { } session
            || frame.Width <= 0 || frame.Height <= 0 || !frame.SceneInputs.TryGetValue(selected.ObjectId, out var inputs)
            || !inputs.Visible)
        { captured = null; return frame; }
        RefreshPhysicsVisibility(selected, session);
        var boneCenters = new List<BoneCenter>();
        var boneSegments = new List<BoneSegment>();
        var boneHandles = new List<BoneHandle>();
        captured = new(session, shell.ContentRevision, shell.CurrentFrame, selected, frame, inputs, boneCenters, boneSegments, boneHandles);
        var camera = frame.ResolvedCamera ?? frame.Camera?.CreateCamera() ?? frame.Scene.Surface.Snapshot.Camera;
        var existing = frame.Scene.EditorOverlay?.Lines ?? [];
        int lineBudget = Math.Max(0, ViewportOverlaySnapshot.MaxLines - existing.Count);
        var lines = new List<ViewportOverlayLine>();
        int? selectedBone = shell.Animation.ObjectId == selected.ObjectId ? shell.Animation.SelectedBoneIndex : null;
        var highlight = new Vector4(.95f, .12f, .18f, 1);
        if (showBones && selectedBone is { } active && inputs.BoneWorldMatrices.ContainsKey(active))
        {
            DrawBone(active, highlight);
            if (manipulate && TryCreateBasis(captured, active, camera, out var basis, out _))
            {
                float size = HandleSize(basis.Origin, camera, frame.Width, frame.Height);
                foreach (var axis in Axes)
                {
                    int beforeHandle = lines.Count;
                    if (rotateHandle) DrawRing(basis, axis, size, AxisColor(axis));
                    else WorldLine(basis.Origin, basis.Origin + BasisAxis(basis, axis) * size, AxisColor(axis));
                    if (lines.Count - beforeHandle == (rotateHandle ? RingSegments : 1))
                        boneHandles.Add(new(active, axis, rotateHandle, editingSpace));
                }
                if (shell.Animation.CanRotate && frame.Width >= 110 && frame.Height >= 44)
                {
                    foreach (var axis in Axes)
                    {
                        var center = CornerCenter(axis, frame.Width, frame.Height);
                        var strokes = CornerGlyph(axis);
                        if (lineBudget - lines.Count < strokes.Length + 1) break;
                        var uv = center / new Vector2(frame.Width, frame.Height);
                        lines.Add(new(uv, uv, AxisColor(axis), 1.5f, ViewportOverlayShape.Square, CornerRadius));
                        foreach (var (start, end) in strokes) Line(center + start, center + end, AxisColor(axis));
                        boneHandles.Add(new(active, axis, true, editingSpace, center));
                    }
                }
            }
        }
        if (showSelectionBounds && inputs.SourceGeometry is { } geometry) DrawBounds(geometry, new(.46f, .64f, .77f, 1));
        int priorityLineCount = lines.Count;
        if (showBones)
        {
            var selectedIndices = shell.Animation.SelectedBoneIndices.Count > 1 ? shell.Animation.SelectedBoneIndices.ToHashSet() : null;
            foreach (int index in Enumerable.Range(0, selected.PmxModel.Bones.Count)
                .Where(index => index != selectedBone && IsShown(selected.PmxModel, index)))
                DrawBone(index, selectedIndices?.Contains(index) == true ? highlight : new(.12f, .30f, .95f, 1));
        }

        var overlay = new ViewportOverlaySnapshot(existing.Concat(lines.Skip(priorityLineCount)).Concat(lines.Take(priorityLineCount)));
        var scene = frame.Scene;
        var decorated = frame with { Scene = new(scene.Surface, scene.Outline, scene.OutlineOptions, scene.Options,
            scene.Materials, scene.Lighting, overlay) };
        if (!publishPicking)
        {
            object presentationKey = lines.Count > priorityLineCount
                ? lines[priorityLineCount]
                : (lines.Count > 0 ? lines[0] : (object)decorated);
            awaitingPresentation.Add(presentationKey, captured);
            captured = lastPresented;
        }
        return decorated;

        bool Project(Vector3 world, out Vector2 pixel) => LightViewportInteraction.TryProject(world, camera, frame.Width, frame.Height, out pixel);
        void Line(Vector2 a, Vector2 b, Vector4 color)
        {
            if (lines.Count < lineBudget && Finite(a) && Finite(b))
                lines.Add(new(new(a.X / frame.Width, a.Y / frame.Height), new(b.X / frame.Width, b.Y / frame.Height), color));
        }
        void WorldLine(Vector3 a, Vector3 b, Vector4 color) { if (Project(a, out var start) && Project(b, out var end)) Line(start, end, color); }
        void DrawBone(int index, Vector4 color)
        {
            if (!inputs.BoneWorldMatrices.TryGetValue(index, out var world) || !Project(world.Translation, out var center)) return;
            var bone = selected.PmxModel.Bones[index];
            // PMX's display destination is independent of its deformation parent. A zero offset
            // has no displayed rod; connecting it back to the parent invents a different skeleton.
            if (!fixedAxisMarkerOnlyBones.Contains(index) && bone.Flags.HasFlag(PmxBoneFlags.TargetShowMode)
                && bone.LinkBoneIndex > 0 && inputs.BoneWorldMatrices.TryGetValue(bone.LinkBoneIndex, out var linked))
                BoneLine(linked.Translation);
            else if (!fixedAxisMarkerOnlyBones.Contains(index) && !bone.Flags.HasFlag(PmxBoneFlags.TargetShowMode)
                && bone.PositionOffset != Vector3.Zero)
                BoneLine(world.Translation + Vector3.TransformNormal(bone.PositionOffset, world));
            if (lines.Count < lineBudget && Finite(center))
            {
                var normalized = new Vector2(center.X / frame.Width, center.Y / frame.Height);
                lines.Add(new(normalized, normalized, color, Width: 1,
                    Shape: bone.Flags.HasFlag(PmxBoneFlags.AllowTranslate) ? ViewportOverlayShape.Square : ViewportOverlayShape.Circle, Radius: 5));
                boneCenters.Add(new(index, center, world.Translation));
            }

            void BoneLine(Vector3 end)
            {
                if (lines.Count >= lineBudget || !Project(end, out var pixel) || !Finite(center) || !Finite(pixel)
                    || Vector2.DistanceSquared(center, pixel) < 1) return;
                lines.Add(new(new(center.X / frame.Width, center.Y / frame.Height), new(pixel.X / frame.Width, pixel.Y / frame.Height),
                    color, Width: 1, Shape: ViewportOverlayShape.Wedge, Radius: 3.5f));
                boneSegments.Add(new(index, center, pixel, world.Translation, end));
            }
        }
        void DrawRing(BoneEditingBasis basis, LightDragAxis axis, float size, Vector4 color)
        {
            var (a, b) = RingAxes(basis, axis);
            for (int step = 0; step < RingSegments; step++)
            {
                float first = MathF.Tau * step / RingSegments, next = MathF.Tau * (step + 1) / RingSegments;
                WorldLine(basis.Origin + (a * MathF.Cos(first) + b * MathF.Sin(first)) * size,
                    basis.Origin + (a * MathF.Cos(next) + b * MathF.Sin(next)) * size, color);
            }
        }
        void DrawBounds(SceneSnapshot geometry, Vector4 color)
        {
            if (geometry.IsEmpty) return;
            var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
            for (int chunk = 0; chunk < geometry.VertexChunkCount; chunk++)
                foreach (var vertex in geometry.GetVertexChunk(chunk)) { min = Vector3.Min(min, vertex.Position); max = Vector3.Max(max, vertex.Position); }
            var corners = new Vector3[8];
            for (int i = 0; i < corners.Length; i++) corners[i] = Vector3.Transform(new((i & 1) == 0 ? min.X : max.X,
                (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z), geometry.World);
            for (int i = 0; i < corners.Length; i++)
                foreach (int bit in new[] { 1, 2, 4 }) if ((i & bit) == 0) WorldLine(corners[i], corners[i | bit], color);
        }
    }

    /// <summary>Publishes hit geometry only after the host successfully presents this exact frame.
    /// Dropped render work cannot replace the mouse targets belonging to the displayed image.</summary>
    public void FramePresented(PreviewFrame frame)
    {
        if (awaitingPresentation.TryGetValue(frame, out var pose))
        {
            awaitingPresentation.Remove(frame);
            if (Current(pose)) captured = pose;
            return;
        }
        foreach (var line in frame.Scene.EditorOverlay?.Lines ?? [])
        {
            if (awaitingPresentation.TryGetValue(line, out pose))
            {
                awaitingPresentation.Remove(line);
                if (Current(pose)) captured = pose;
                return;
            }
        }
    }

    /// <summary>An emitted bone center marker takes priority over a nearby handle. Centers use the
    /// nearest screen distance, then camera distance and source index, independent of PMX table draw order.
    /// Handles must be fully emitted in that frame and match the current mode and editing space.
    /// After centers and selected handles, only bone segments actually drawn in that frame can hit.
    /// A click may select without starting a gesture when the PMX channel disallows that edit.</summary>
    public bool TryBeginDrag(int x, int y, int width, int height, SceneCamera camera, bool rotate = false, bool extendSelection = false)
    {
        ArgumentNullException.ThrowIfNull(camera); Synchronize();
        if (!enabled || !showBones || shell.IsBusy || HasPointerGesture || captured is not { } pose
            || width != pose.RenderFrame.Width || height != pose.RenderFrame.Height || width <= 0 || height <= 0
            || pose.RenderFrame.SourceFrame != shell.CurrentFrame) return false;
        var displayedCamera = pose.RenderFrame.ResolvedCamera ?? pose.RenderFrame.Camera?.CreateCamera()
            ?? pose.RenderFrame.Scene.Surface.Snapshot.Camera;
        if (camera != displayedCamera) { lastPick = null; requestRender(); return false; }
        var mouse = new Vector2(x, y);
        bool rotating = ResolveRotateHandle(rotate || preferredRotateHandle);
        var axis = LightDragAxis.Free;
        bool corner = false;
        int? hit = null;
        // The corner controls are drawn on top of the model, even if a projected joint is beneath one.
        if (manipulate && !extendSelection)
        {
            foreach (var handle in pose.BoneHandles)
            {
                if (handle.Index != shell.Animation.SelectedBoneIndex || handle.Space != editingSpace || handle.Corner is not { } center) continue;
                var delta = Vector2.Abs(mouse - center);
                if (delta.X > CornerRadius || delta.Y > CornerRadius) continue;
                hit = handle.Index; axis = handle.Axis; corner = true; break;
            }
        }
        int[] candidates = hit is null ? CenterCandidates(pose, mouse, camera) : [];
        if (candidates.Length != 0) hit = candidates[0];
        if (manipulate && hit is null && !extendSelection && shell.Animation.SelectedBoneIndex is { } selected
            && TryCreateBasis(pose, selected, camera, out var selectedBasis, out _))
        {
            float size = HandleSize(selectedBasis.Origin, camera, width, height), distance = 7;
            foreach (var handle in pose.BoneHandles)
            {
                if (handle.Corner is not null || handle.Index != selected || handle.Rotate != rotating || handle.Space != editingSpace) continue;
                float test = HandleDistance(selectedBasis, handle.Axis, size, mouse, camera, width, height, rotating);
                if (test < distance) { distance = test; axis = handle.Axis; hit = selected; }
            }
        }
        if (hit is null)
        {
            candidates = SegmentCandidates(pose, mouse, camera);
            if (candidates.Length != 0) hit = candidates[0];
        }
        if (hit is not { } boneIndex) { lastPick = null; return false; }
        if (CanBeginInteraction?.Invoke() == false) return true;
        if (!Current(pose)) { requestRender(); return true; }
        shell.StopPlayback();
        if (candidates.Length > 1 && !extendSelection)
        {
            var cycle = new PickCycle(pose.Model.ObjectId, pose.Revision, pose.Frame, width, height, camera, mouse, candidates, boneIndex);
            int next = boneIndex;
            if (lastPick is { } previous && previous.Matches(cycle) && previous.Selected == shell.Animation.SelectedBoneIndex)
            {
                boneIndex = previous.Selected;
                next = candidates[(Array.IndexOf(candidates, boneIndex) + 1) % candidates.Length];
            }
            pendingPick = new(cycle with { Selected = boneIndex }, next);
        }
        lastPick = null;
        if (extendSelection)
        {
            var selection = shell.Animation.SelectedBoneIndices.ToList();
            if (!selection.Remove(boneIndex)) selection.Add(boneIndex);
            shell.Browser.HierarchyTab = 0;
            shell.Browser.SelectedBoneIndex = selection.Count == 0 ? null : selection[^1];
            shell.SceneSelectionIndex = -1;
            shell.Animation.SelectBones(selection);
            Notify(); return true;
        }
        if (!corner && axis == LightDragAxis.Free) SelectSingleBone(boneIndex);
        SynchronizeHandleMode();
        rotating = corner || ResolveRotateHandle(rotate || preferredRotateHandle);
        if (!manipulate) { Notify(); return true; }
        if (!pose.Model.PmxModel.Bones[boneIndex].Flags.HasFlag(PmxBoneFlags.AllowControl)
            || (rotating ? !shell.Animation.CanRotate : !shell.Animation.CanTranslate))
        {
            shell.OperationStatus = EditorLocalization.Text("此骨骼不支持当前变换，只能选择。请选择可操作的骨骼。");
            Notify(); return true;
        }
        if (!TryCreateBasis(pose, boneIndex, camera, out var basis, out float parentScale))
        {
            shell.OperationStatus = EditorLocalization.Text("无法建立此骨骼的编辑基底：外亲绑定或父级变换暂不支持视口拖动，请使用骨骼属性编辑。");
            Notify(); return true;
        }
        if (shell.Animation.CaptureViewportBoneDraft() is not { } before)
        {
            shell.OperationStatus = EditorLocalization.Text("当前骨骼草稿无效，无法开始拖动。请先修正骨骼属性中的输入。");
            Notify(); return true;
        }
        dragEdited = false; lastDragPoint = mouse;
        drag = new(pose, before, basis, parentScale, mouse, camera, width, height, axis, rotating, corner);
        Notify(); return true;
    }

    public bool MoveDrag(int x, int y)
    {
        using var measure = InteractionDiagnostics.Measure("bone.move", EditorInteractionTrace.CurrentInput);
        Synchronize();
        if (pendingPick is { } click)
        {
            if (Vector2.DistanceSquared(click.Cycle.Point, new(x, y)) <= ClickSlopSquared) return true;
            // Drag the bone that was selected on press. Only a stationary release advances the cycle.
            if (drag is null) { pendingPick = click with { Moved = true }; return true; }
            pendingPick = null;
        }
        if (drag is not { } gesture) return false;
        var mouse = new Vector2(x, y);
        if (mouse == lastDragPoint) return true;
        lastDragPoint = mouse;
        var delta = mouse - gesture.Start;
        var local = gesture.Before.LocalPose;
        if (gesture.Rotate)
        {
            Quaternion rotation;
            if (gesture.Axis == LightDragAxis.Free)
            {
                var right = InBasis(CameraRight(gesture.Camera), gesture.Basis);
                var up = InBasis(Vector3.Normalize(gesture.Camera.Up), gesture.Basis);
                rotation = Quaternion.CreateFromAxisAngle(up, delta.X * .01f) * Quaternion.CreateFromAxisAngle(right, delta.Y * .01f);
                local = gesture.Basis.RotateInBasis(local, rotation);
            }
            else
            {
                float angle = gesture.Corner ? (delta.X - delta.Y) * .01f : RotationAngle(gesture, new(x, y));
                rotation = Quaternion.CreateFromAxisAngle(UnitAxis(gesture.Axis), angle);
                local = gesture.Basis.RotateInBasis(local, rotation);
            }
        }
        else
        {
            Vector3 worldDelta;
            if (gesture.Axis == LightDragAxis.Free)
                worldDelta = LightViewportInteraction.Translate(gesture.Basis.Origin, delta, gesture.Camera, gesture.Width, gesture.Height) - gesture.Basis.Origin;
            else
            {
                var worldAxis = BasisAxis(gesture.Basis, gesture.Axis);
                float size = HandleSize(gesture.Basis.Origin, gesture.Camera, gesture.Width, gesture.Height);
                if (!LightViewportInteraction.TryProject(gesture.Basis.Origin, gesture.Camera, gesture.Width, gesture.Height, out var start)
                    || !LightViewportInteraction.TryProject(gesture.Basis.Origin + worldAxis * size, gesture.Camera, gesture.Width, gesture.Height, out var end)
                    || Vector2.DistanceSquared(start, end) < 4) return true;
                var screenAxis = end - start;
                worldDelta = worldAxis * (Vector2.Dot(delta, screenAxis) / screenAxis.LengthSquared() * size);
            }
            var scaledDelta = worldDelta / gesture.ParentScale;
            if (!float.IsFinite(scaledDelta.X) || !float.IsFinite(scaledDelta.Y) || !float.IsFinite(scaledDelta.Z)) return true;
            local = gesture.Basis.TranslateInWorld(local, scaledDelta);
        }
        if (delta == Vector2.Zero)
        {
            if (!dragEdited) return true;
            shell.Animation.RestoreViewportBoneDraft(gesture.Before);
            dragEdited = false;
        }
        else
        {
            shell.Animation.ApplyViewportBoneDraft(gesture.Before, local);
            dragEdited = true;
        }
        Notify(); return true;
    }

    /// <summary>Release keeps an unregistered pose. Existing Animation.RegisterKeyframeCommand owns
    /// overwrite confirmation, the project transaction and Undo; release never invokes it.</summary>
    public bool EndDrag(bool finishClick = true)
    {
        Synchronize();
        if (!HasPointerGesture) return false;
        drag = null;
        if (finishClick && pendingPick is { Moved: false } click && shell.Animation.SelectedBoneIndex == click.Cycle.Selected
            && shell.Browser.SelectedModel?.ObjectId == click.Cycle.Actor && shell.CurrentFrame == click.Cycle.Frame
            && shell.ContentRevision == click.Cycle.Revision)
        {
            pendingPick = null;
            if (click.Next != click.Cycle.Selected) SelectSingleBone(click.Next);
            lastPick = click.Cycle with { Selected = click.Next };
        }
        pendingPick = null;
        Notify(); return true;
    }
    public void CancelDrag()
    {
        pendingPick = null; lastPick = null;
        if (drag is not { } gesture) return;
        drag = null;
        if (dragEdited && SourceCurrent(gesture.Captured)) shell.Animation.RestoreViewportBoneDraft(gesture.Before);
        Notify();
    }
    public void Refresh() { Synchronize(); captured = null; Notify(); }
    public void Rotate(bool value) => RotateHandle = value;

    private void Synchronize()
    {
        if (drag is { } gesture && (!Current(gesture.Captured)
            || shell.Animation.ObjectId != gesture.Before.ObjectId || shell.Animation.SelectedBoneIndex != gesture.Before.BoneIndex)) CancelDrag();
        if (captured is { } pose && !Current(pose)) captured = null;
        SynchronizeHandleMode();
    }
    private bool ResolveRotateHandle(bool preferred)
    {
        if (shell.Browser.SelectedModel is not { } model || shell.Animation.ObjectId != model.ObjectId
            || shell.Animation.SelectedBoneIndex is not { } index || (uint)index >= (uint)model.PmxModel.Bones.Count) return preferred;
        var flags = model.PmxModel.Bones[index].Flags;
        bool translate = flags.HasFlag(PmxBoneFlags.AllowTranslate), rotate = flags.HasFlag(PmxBoneFlags.AllowRotate);
        return translate == rotate ? preferred : rotate;
    }
    private void SynchronizeHandleMode()
    {
        bool effective = ResolveRotateHandle(preferredRotateHandle);
        if (rotateHandle == effective) return;
        rotateHandle = effective;
        // Decorating a frame must not enter the UI setter, cancel a draft, or request another
        // frame. The window's Changed handler marshals its toolbar update to the UI thread.
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private bool SourceCurrent(CapturedPose pose) => ReferenceEquals(shell.Workspace.Session, pose.Session)
        && shell.ContentRevision == pose.Revision && shell.CurrentFrame == pose.Frame;
    private bool Current(CapturedPose pose) => SourceCurrent(pose) && ReferenceEquals(shell.Browser.SelectedModel, pose.Model);

    private bool TryCreateBasis(CapturedPose pose, int index, SceneCamera camera, out BoneEditingBasis basis, out float parentScale)
    {
        basis = null!; parentScale = 1;
        if ((uint)index >= (uint)pose.Model.PmxModel.Bones.Count || !pose.Inputs.BoneWorldMatrices.TryGetValue(index, out var world)) return false;
        var bone = pose.Model.PmxModel.Bones[index];
        var item = pose.Session.Objects.FirstOrDefault(item => item.Id == pose.Model.ObjectId);
        if (item is null) return false;
        // A direct outside-parent can replace the ordinary parent, including PMM dynamic bindings.
        // Those require an explicit effective-parent render input. Do not infer it from solved output.
        var pmmSample = pose.Session.PmmControls?.Sample(pose.Frame);
        var parents = pose.Session.OutsideParents.Concat(pmmSample?.OutsideParents ?? []);
        if (parents.Any(binding => binding.ObjectId == pose.Model.ObjectId
            && (binding.BoneIndex == index || bone.ParentIndex < 0 && binding.BoneIndex == -1))) return false;
        if (pmmSample?.GroundOutsideParents.Any(ground => ground.ObjectId == pose.Model.ObjectId && ground.BoneIndex == index) == true)
            return false;
        Matrix4x4 parent = item.World;
        if (bone.ParentIndex >= 0 && !pose.Inputs.BoneWorldMatrices.TryGetValue(bone.ParentIndex, out parent)) return false;
        if (!TryRigid(parent, out var parentRigid, out parentScale) || !TryRigid(world, out var boneRigid, out _)) return false;
        var view = camera.LeftHanded ? Matrix4x4.CreateLookAtLeftHanded(camera.Eye, camera.Target, camera.Up)
            : Matrix4x4.CreateLookAt(camera.Eye, camera.Target, camera.Up);
        if (!Matrix4x4.Invert(view, out var viewToWorld)) return false;
        try { basis = BoneEditingBasis.Create(bone, parentRigid, boneRigid, editingSpace, viewToWorld); return true; }
        catch (ArgumentException) { return false; }
    }
    private static bool TryRigid(Matrix4x4 matrix, out Matrix4x4 rigid, out float uniformScale)
    {
        rigid = default; uniformScale = 1;
        if (!Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var position) || !float.IsFinite(scale.X)
            || scale.X <= 0 || MathF.Abs(scale.X - scale.Y) > scale.X * 2e-4f || MathF.Abs(scale.X - scale.Z) > scale.X * 2e-4f) return false;
        uniformScale = scale.X; rigid = Matrix4x4.CreateFromQuaternion(rotation); rigid.Translation = position;
        // Decompose alone may accept a shear. Only a proper uniform scale may be factored out.
        float tolerance = uniformScale * 2e-4f;
        return MathF.Abs(matrix.M11 - rigid.M11 * uniformScale) <= tolerance
            && MathF.Abs(matrix.M12 - rigid.M12 * uniformScale) <= tolerance && MathF.Abs(matrix.M13 - rigid.M13 * uniformScale) <= tolerance
            && MathF.Abs(matrix.M21 - rigid.M21 * uniformScale) <= tolerance && MathF.Abs(matrix.M22 - rigid.M22 * uniformScale) <= tolerance
            && MathF.Abs(matrix.M23 - rigid.M23 * uniformScale) <= tolerance && MathF.Abs(matrix.M31 - rigid.M31 * uniformScale) <= tolerance
            && MathF.Abs(matrix.M32 - rigid.M32 * uniformScale) <= tolerance && MathF.Abs(matrix.M33 - rigid.M33 * uniformScale) <= tolerance;
    }
    private const int RingSegments = 24;
    private static readonly LightDragAxis[] Axes = [LightDragAxis.X, LightDragAxis.Y, LightDragAxis.Z];
    private void RefreshPhysicsVisibility(EditorModelItem selected, SceneProjectSession session)
    {
        var model = selected.PmxModel;
        if (!ReferenceEquals(visibilityModel, model))
        {
            visibilityModel = model;
            // A dynamic rigid body owns this bone's solved pose. It is not an ordinary manual
            // animation handle, even when the author left Visible/AllowControl flags set.
            physicsDrivenBones = model.RigidBodies
                .Where(body => body.Operation is PmxRigidBodyOperation.Dynamic or PmxRigidBodyOperation.DynamicAndBoneMerge)
                .Select(body => body.BoneIndex).Where(bone => bone >= 0 && bone < model.Bones.Count).ToHashSet();
            // Original PMX loading assigns fixed-axis bones a marker-only type, then
            // reclassifies IK controllers, targets and links. Translation alone does not override it.
            fixedAxisMarkerOnlyBones = FindFixedAxisMarkerOnlyBones(model.Bones);
        }
        bool simulated = shell.Workspace.Motions.FirstOrDefault(actor => actor.ObjectId == selected.ObjectId)?.PhysicsEnabled == true;
        if (!simulated) { hiddenPhysicsBones = new HashSet<int>(); return; }
        var sample = session.PmmControls?.Sample(shell.CurrentFrame);
        hiddenPhysicsBones = sample is not null && sample.DisabledBoneIndices.TryGetValue(selected.ObjectId, out var disabled)
            ? physicsDrivenBones.Except(disabled).ToHashSet() : physicsDrivenBones;
    }
    private bool IsShown(PmxModel model, int index)
    {
        var bone = model.Bones[index];
        // Authored visibility controls display; edit permissions are checked only after picking.
        return !hiddenPhysicsBones.Contains(index) && bone.Flags.HasFlag(PmxBoneFlags.Visible);
    }
    private static HashSet<int> FindFixedAxisMarkerOnlyBones(IReadOnlyList<PmxBone> bones)
    {
        var ikBones = new HashSet<int>();
        for (int index = 0; index < bones.Count; index++)
        {
            var bone = bones[index];
            if (!bone.Flags.HasFlag(PmxBoneFlags.Ik)) continue;
            ikBones.Add(index);
            ikBones.Add(bone.IkTargetBoneIndex);
            foreach (var link in bone.IkLinks) ikBones.Add(link.BoneIndex);
        }
        return Enumerable.Range(0, bones.Count)
            .Where(index => bones[index].Flags.HasFlag(PmxBoneFlags.FixedAxis) && !ikBones.Contains(index)).ToHashSet();
    }
    private static Vector3 UnitAxis(LightDragAxis axis) => axis switch { LightDragAxis.X => Vector3.UnitX, LightDragAxis.Y => Vector3.UnitY, LightDragAxis.Z => Vector3.UnitZ, _ => Vector3.Zero };
    private static Vector3 BasisAxis(BoneEditingBasis basis, LightDragAxis axis) => axis switch { LightDragAxis.X => basis.XAxis, LightDragAxis.Y => basis.YAxis, LightDragAxis.Z => basis.ZAxis, _ => Vector3.Zero };
    private static Vector3 InBasis(Vector3 world, BoneEditingBasis basis) => new(Vector3.Dot(world, basis.XAxis), Vector3.Dot(world, basis.YAxis), Vector3.Dot(world, basis.ZAxis));
    private static Vector4 AxisColor(LightDragAxis axis) => axis switch { LightDragAxis.X => new(.95f, .28f, .24f, 1), LightDragAxis.Y => new(.3f, .8f, .4f, 1), _ => new(.32f, .55f, 1, 1) };
    private static (Vector3 A, Vector3 B) RingAxes(BoneEditingBasis basis, LightDragAxis axis) => axis switch
    { LightDragAxis.X => (basis.YAxis, basis.ZAxis), LightDragAxis.Y => (basis.ZAxis, basis.XAxis), _ => (basis.XAxis, basis.YAxis) };
    private static Vector3 CameraRight(SceneCamera camera) => Vector3.Normalize(camera.LeftHanded
        ? Vector3.Cross(camera.Up, camera.Target - camera.Eye) : Vector3.Cross(camera.Target - camera.Eye, camera.Up));
    private static float HandleSize(Vector3 origin, SceneCamera camera, int width, int height)
    {
        var shifted = LightViewportInteraction.Translate(origin, new(70, 0), camera, width, height);
        return Math.Clamp(Vector3.Distance(origin, shifted), .001f, 100000f);
    }
    private static float HandleDistance(BoneEditingBasis basis, LightDragAxis axis, float size, Vector2 mouse, SceneCamera camera, int width, int height, bool rotate)
    {
        if (!rotate) return Segment(basis.Origin, basis.Origin + BasisAxis(basis, axis) * size);
        float nearest = float.PositiveInfinity; var (a, b) = RingAxes(basis, axis);
        for (int step = 0; step < RingSegments; step++)
        {
            float first = MathF.Tau * step / RingSegments, next = MathF.Tau * (step + 1) / RingSegments;
            nearest = Math.Min(nearest, Segment(basis.Origin + (a * MathF.Cos(first) + b * MathF.Sin(first)) * size,
                basis.Origin + (a * MathF.Cos(next) + b * MathF.Sin(next)) * size));
        }
        return nearest;
        float Segment(Vector3 a, Vector3 b)
        {
            if (!LightViewportInteraction.TryProject(a, camera, width, height, out var start)
                || !LightViewportInteraction.TryProject(b, camera, width, height, out var end)) return float.PositiveInfinity;
            var direction = end - start; float length = direction.LengthSquared();
            return Vector2.Distance(mouse, length < 1e-6f ? start : start + direction * Math.Clamp(Vector2.Dot(mouse - start, direction) / length, 0, 1));
        }
    }
    private static float RotationAngle(Drag gesture, Vector2 mouse)
    {
        var normal = BasisAxis(gesture.Basis, gesture.Axis);
        if (Matrix4x4.Invert(gesture.Camera.ViewProjection(gesture.Width, gesture.Height), out var inverse)
            && TryPlanePoint(gesture.Start, out var first) && TryPlanePoint(mouse, out var current))
        {
            first -= gesture.Basis.Origin; current -= gesture.Basis.Origin;
            if (first.LengthSquared() > 1e-10f && current.LengthSquared() > 1e-10f)
                return MathF.Atan2(Vector3.Dot(normal, Vector3.Cross(first, current)), Vector3.Dot(first, current));
        }
        var delta = mouse - gesture.Start; return (delta.X - delta.Y) * .01f;

        bool TryPlanePoint(Vector2 pixel, out Vector3 point)
        {
            point = default;
            float x = pixel.X * 2 / gesture.Width - 1, y = 1 - pixel.Y * 2 / gesture.Height;
            var near = Vector4.Transform(new Vector4(x, y, 0, 1), inverse);
            var far = Vector4.Transform(new Vector4(x, y, 1, 1), inverse);
            if (MathF.Abs(near.W) < 1e-8f || MathF.Abs(far.W) < 1e-8f) return false;
            var origin = new Vector3(near.X, near.Y, near.Z) / near.W;
            var direction = new Vector3(far.X, far.Y, far.Z) / far.W - origin;
            float denominator = Vector3.Dot(normal, direction);
            if (!float.IsFinite(denominator) || MathF.Abs(denominator) < 1e-7f) return false;
            point = origin + direction * (Vector3.Dot(normal, gesture.Basis.Origin - origin) / denominator);
            return float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
        }
    }
    private static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    private void Notify() { Changed?.Invoke(this, EventArgs.Empty); requestRender(); }
}

public sealed partial class EditorAnimationViewModel
{
    /// <summary>Opaque channel state captured before a viewport gesture; exact source quaternion and
    /// prior incomplete/complete draft values survive cancellation, while other channels are untouched.</summary>
    internal sealed record ViewportBoneDraftState(Guid ObjectId, PmxModel Model, VmdMotion? Source, int Frame,
        int BoneIndex, VpdLocalBoneTransform LocalPose, bool HadDraft, decimal? X, decimal? Y, decimal? Z,
        decimal? Pitch, decimal? Yaw, decimal? Roll, Quaternion SampledRotation, bool RotationEdited);

    internal ViewportBoneDraftState? CaptureViewportBoneDraft()
    {
        if (!IsBoneSelected || IsProjectBusy || !DraftIsValid) return null;
        return new(objectId!.Value, model!, sourceMotion, CurrentFrame, boneIndex!.Value,
            new(DraftTranslation, DraftRotation), hasDraft, positionX, positionY, positionZ,
            rotationX, rotationY, rotationZ, sampledRotation, rotationEdited);
    }

    internal void ApplyViewportBoneDraft(ViewportBoneDraftState before, VpdLocalBoneTransform pose)
    {
        using var measure = InteractionDiagnostics.Measure("bone.apply-draft", EditorInteractionTrace.CurrentInput);
        if (!ViewportContextMatches(before) || objectId != before.ObjectId || boneIndex != before.BoneIndex || IsProjectBusy) return;
        if (!FiniteTranslation(pose.Translation) || !TryNormalizeSavedRotation(pose.Rotation, out var normalized)) return;
        var degrees = ToDegrees(normalized);
        var draft = new BoneDraft(ToDecimal(pose.Translation.X), ToDecimal(pose.Translation.Y), ToDecimal(pose.Translation.Z),
            ToDecimal(degrees.X), ToDecimal(degrees.Y), ToDecimal(degrees.Z), pose.Rotation, false);
        if (!draft.IsValid) return;
        if (!actorDrafts.TryGetValue((before.ObjectId, before.Frame), out var pending)) actorDrafts.Add((before.ObjectId, before.Frame), pending = new(before.Model, before.Source));
        pending.Bones[before.BoneIndex] = draft;
        LoadPose(); Changed(nameof(HasInvalidDrafts)); NotifyDraftContentEdited(); PoseDraftChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void RestoreViewportBoneDraft(ViewportBoneDraftState before)
    {
        if (!ViewportContextMatches(before) || IsProjectBusy) return;
        if (before.HadDraft)
        {
            if (!actorDrafts.TryGetValue((before.ObjectId, before.Frame), out var pending)) actorDrafts.Add((before.ObjectId, before.Frame), pending = new(before.Model, before.Source));
            pending.Bones[before.BoneIndex] = new(before.X, before.Y, before.Z, before.Pitch, before.Yaw, before.Roll,
                before.SampledRotation, before.RotationEdited);
        }
        else if (actorDrafts.TryGetValue((before.ObjectId, before.Frame), out var pending))
        {
            pending.Bones.Remove(before.BoneIndex);
            if (pending.IsEmpty) actorDrafts.Remove((before.ObjectId, before.Frame));
        }
        LoadPose(); Changed(nameof(HasInvalidDrafts)); NotifyDraftContentEdited(); PoseDraftChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool ViewportContextMatches(ViewportBoneDraftState before) => CurrentFrame == before.Frame
        && (objectId == before.ObjectId ? ReferenceEquals(model, before.Model) && ReferenceEquals(sourceMotion, before.Source)
            : actorDrafts.TryGetValue((before.ObjectId, before.Frame), out var pending) && ReferenceEquals(pending.Model, before.Model) && ReferenceEquals(pending.Source, before.Source));
}
