using Mmd.Modern.Formats;

namespace Mmd.Modern.Application.MotionEditing;

public enum SceneTrackKind { Camera, Light, SelfShadow }
public enum SceneCameraChannel { X, Y, Z, Rotation, Distance, FieldOfView }
public readonly record struct SceneKeyframeAddress(SceneTrackKind Kind, uint Frame);
public sealed record SceneTrackConflict(SceneKeyframeAddress Address, int ExistingRecordCount);

/// <summary>A detached preflight bound to one editor revision; UI owns confirmation prompts.</summary>
public sealed class SceneTrackEditPlan
{
    internal Guid Owner { get; }
    internal VmdMotion Candidate { get; }
    internal bool Changes { get; }
    public ulong Revision { get; }
    public IReadOnlyList<SceneTrackConflict> Conflicts { get; }
    internal SceneTrackEditPlan(Guid owner, ulong revision, VmdMotion candidate, IEnumerable<SceneTrackConflict> conflicts, bool changes)
    { Owner = owner; Revision = revision; Candidate = candidate; Conflicts = Array.AsReadOnly(conflicts.ToArray()); Changes = changes; }
}

public sealed class SceneTrackClipboard
{
    internal VmdMotion Content { get; }
    public uint? SourceStartFrame { get; }
    public bool IsEmpty => SourceStartFrame is null;
    internal SceneTrackClipboard(VmdMotion content)
    { Content = SceneTrackRecords.Snapshot(content); SourceStartFrame = SceneTrackRecords.Keys(Content).Select(k => (uint?)SceneTrackRecords.Address(k).Frame).Min(); }
}

/// <summary>Scene-only transactions preserve all character records, raw names and original order. Duplicate addresses select every record.</summary>
public sealed class SceneTrackEditor
{
    readonly Guid owner = Guid.NewGuid();
    readonly int undoLimit;
    readonly List<(VmdMotion Before, VmdMotion After)> undo = [];
    readonly Stack<(VmdMotion Before, VmdMotion After)> redo = [];
    VmdMotion current;
    public ulong Revision { get; private set; }
    public bool CanUndo => undo.Count != 0;
    public bool CanRedo => redo.Count != 0;

    public SceneTrackEditor(VmdMotion source, int undoLimit = 1000)
    {
        SceneTrackRecords.Validate(source);
        if (undoLimit < 1) throw new ArgumentOutOfRangeException(nameof(undoLimit));
        current = SceneTrackRecords.Snapshot(source); this.undoLimit = undoLimit;
    }

    public VmdMotion ExportMotion() => SceneTrackRecords.Snapshot(current);

    public SceneTrackClipboard CopySelection(IEnumerable<SceneKeyframeAddress> selection)
    {
        var selected = Selection(selection);
        return new(SceneTrackRecords.WithKeys(new(), SceneTrackRecords.Keys(current).Where(k => selected.Contains(SceneTrackRecords.Address(k)))));
    }

    public SceneTrackEditPlan PreparePaste(SceneTrackClipboard clipboard, uint targetFrame)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        if (clipboard.SourceStartFrame is not { } first) return Plan(current, [], false);
        long delta = (long)targetFrame - first;
        return Insert(SceneTrackRecords.Keys(clipboard.Content).Select(k => SceneTrackRecords.Copy(k, Shift(SceneTrackRecords.Address(k).Frame, delta))), []);
    }

    /// <summary>Registers scene records; character records are rejected, preventing accidental partial registration.</summary>
    public SceneTrackEditPlan PrepareRegister(VmdMotion records)
    {
        SceneTrackRecords.Validate(records);
        if (records.BoneKeyframes is null || records.MorphKeyframes is null || records.IkKeyframes is null
            || records.BoneKeyframes.Count != 0 || records.MorphKeyframes.Count != 0 || records.IkKeyframes.Count != 0)
            throw new ArgumentException("Scene registration accepts only camera, light and self-shadow records.", nameof(records));
        return Insert(SceneTrackRecords.Keys(SceneTrackRecords.Snapshot(records)), []);
    }

    public SceneTrackEditPlan PrepareMove(IEnumerable<SceneKeyframeAddress> selection, long frameDelta)
    {
        var selected = Selection(selection);
        if (frameDelta == 0 || selected.Count == 0) return Plan(current, [], false);
        var shifted = SceneTrackRecords.Keys(current).Where(k => selected.Contains(SceneTrackRecords.Address(k)))
            .Select(k => SceneTrackRecords.Copy(k, Shift(SceneTrackRecords.Address(k).Frame, frameDelta))).ToArray();
        return Insert(shifted, selected);
    }

    public SceneTrackEditPlan PrepareDelete(IEnumerable<SceneKeyframeAddress> selection)
    {
        var selected = Selection(selection);
        return Plan(SceneTrackRecords.WithKeys(current, SceneTrackRecords.Keys(current).Where(k => !selected.Contains(SceneTrackRecords.Address(k)))), [], selected.Count != 0);
    }

    /// <summary>VMD camera keys own outgoing curves. Stored order is x1,x2,y1,y2 for each channel.</summary>
    public SceneTrackEditPlan PrepareSetCameraInterpolation(IEnumerable<uint> frames, SceneCameraChannel channel, VmdBezierControl curve)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        var selected = Selection(frames.Select(f => new SceneKeyframeAddress(SceneTrackKind.Camera, f)));
        bool changes = false;
        var records = SceneTrackRecords.Keys(current).Select(key =>
        {
            if (key is not VmdCameraKeyframe camera || !selected.Contains(SceneTrackRecords.Address(key))) return key;
            byte[] bytes = camera.Interpolation.ToArray();
            byte[] values = [curve.X1, curve.X2, curve.Y1, curve.Y2];
            int index = (int)channel * 4;
            for (int i = 0; i < 4; i++) { changes |= bytes[index + i] != values[i]; bytes[index + i] = values[i]; }
            return new VmdCameraKeyframe { Frame = camera.Frame, Distance = camera.Distance, Interest = camera.Interest,
                Rotation = camera.Rotation, ViewAngle = camera.ViewAngle, Perspective = camera.Perspective, Interpolation = bytes };
        }).ToArray();
        return Plan(SceneTrackRecords.WithKeys(current, records), [], changes);
    }

    public void Apply(SceneTrackEditPlan plan, bool confirmOverwrite = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Owner != owner || plan.Revision != Revision)
            throw new InvalidOperationException("The edit plan belongs to a different editor or an expired revision; run preflight again.");
        if (plan.Conflicts.Count != 0 && !confirmOverwrite)
            throw new InvalidOperationException("Destination keyframes exist; explicit overwrite confirmation is required.");
        if (!plan.Changes) return;
        ulong next = checked(Revision + 1);
        undo.Add((current, plan.Candidate)); if (undo.Count > undoLimit) undo.RemoveAt(0);
        redo.Clear(); current = plan.Candidate; Revision = next;
    }

    public bool Undo()
    {
        if (!CanUndo) return false;
        ulong next = checked(Revision + 1); var change = undo[^1]; undo.RemoveAt(undo.Count - 1);
        redo.Push(change); current = change.Before; Revision = next; return true;
    }

    public bool Redo()
    {
        if (!CanRedo) return false;
        ulong next = checked(Revision + 1); var change = redo.Pop(); undo.Add(change);
        current = change.After; Revision = next; return true;
    }

    SceneTrackEditPlan Insert(IEnumerable<object> records, HashSet<SceneKeyframeAddress> remove)
    {
        object[] incoming = records.ToArray();
        var targets = incoming.Select(SceneTrackRecords.Address).ToHashSet();
        var remaining = SceneTrackRecords.Keys(current).Where(k => !remove.Contains(SceneTrackRecords.Address(k))).ToArray();
        var conflicts = remaining.GroupBy(SceneTrackRecords.Address).Where(g => targets.Contains(g.Key))
            .OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.Frame).Select(g => new SceneTrackConflict(g.Key, g.Count()));
        var candidate = SceneTrackRecords.WithKeys(current, remaining.Where(k => !targets.Contains(SceneTrackRecords.Address(k))).Concat(incoming));
        return Plan(candidate, conflicts, incoming.Length != 0);
    }

    SceneTrackEditPlan Plan(VmdMotion candidate, IEnumerable<SceneTrackConflict> conflicts, bool changes) => new(owner, Revision, candidate, conflicts, changes);

    HashSet<SceneKeyframeAddress> Selection(IEnumerable<SceneKeyframeAddress> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var selected = selection.ToHashSet(); var existing = SceneTrackRecords.Keys(current).Select(SceneTrackRecords.Address).ToHashSet();
        foreach (var key in selected)
            if (!Enum.IsDefined(key.Kind) || !existing.Contains(key)) throw new ArgumentException($"Scene keyframe does not exist: {key}.", nameof(selection));
        return selected;
    }

    static uint Shift(uint frame, long delta)
    {
        // Check before addition, including long.MinValue/MaxValue, so arithmetic cannot wrap.
        if (delta < -(long)frame || delta > (long)uint.MaxValue - frame) throw new ArgumentOutOfRangeException(nameof(delta));
        return (uint)(frame + delta);
    }
}
