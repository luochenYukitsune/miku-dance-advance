using Mmd.Modern.Formats;

namespace Mmd.Modern.Application.MotionEditing;

public enum PmmTrackKind { Gravity, Model, Bone, Accessory }
/// <summary>OwnerIndex is the stored PMM model/accessory Index, never its list position. BoneName is exact and case sensitive.</summary>
public readonly record struct PmmTrackAddress(PmmTrackKind Kind, int OwnerIndex, string BoneName, int Frame);
/// <summary>Address.Frame is authoritative; key IDs/links are rebuilt. Model records contain visibility, IK and outside-parent slots together.</summary>
public sealed record PmmTrackRecord(PmmTrackAddress Address, PmmKeyframe Value);
public sealed record PmmTrackConflict(PmmTrackAddress Address, int ExistingRecordCount);

public sealed class PmmTrackEditPlan
{
    internal Guid Owner { get; }
    internal PmmDocument Candidate { get; }
    internal bool Changes { get; }
    public ulong Revision { get; }
    public IReadOnlyList<PmmTrackConflict> Conflicts { get; }
    internal PmmTrackEditPlan(Guid owner, ulong revision, PmmDocument candidate, IEnumerable<PmmTrackConflict> conflicts, bool changes)
    { Owner = owner; Revision = revision; Candidate = candidate; Conflicts = Array.AsReadOnly(conflicts.ToArray()); Changes = changes; }
}

public sealed class PmmTrackClipboard
{
    internal IReadOnlyList<PmmTrackRecord> Records { get; }
    public int? SourceStartFrame { get; }
    public bool IsEmpty => SourceStartFrame is null;
    internal PmmTrackClipboard(IEnumerable<PmmTrackRecord> records)
    {
        Records = Array.AsReadOnly(records.Select(record => PmmTrackEditorRecords.Copy(record)).ToArray());
        SourceStartFrame = Records.Select(record => (int?)record.Address.Frame).Min();
    }
}

/// <summary>Detached PMM special-track transactions. Frame-zero anchors cannot be removed or moved; overwrite is explicit.
/// Full bone records make the accompanying pose/curve change visible when inserting a physics switch.
/// No claim is made that inserting a bone key preserves the original continuous Bezier trajectory.</summary>
public sealed class PmmTrackEditor
{
    readonly Guid owner = Guid.NewGuid();
    readonly int undoLimit;
    readonly List<(PmmDocument Before, PmmDocument After)> undo = [];
    readonly Stack<(PmmDocument Before, PmmDocument After)> redo = [];
    PmmDocument current;
    public ulong Revision { get; private set; }
    public bool CanUndo => undo.Count != 0;
    public bool CanRedo => redo.Count != 0;

    public PmmTrackEditor(PmmDocument source, int undoLimit = 1000)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.CurrentFrame < 0) throw new InvalidDataException("PMM saved editor frame cannot be negative.");
        if (undoLimit < 1) throw new ArgumentOutOfRangeException(nameof(undoLimit));
        current = PmmTrackEditorRecords.Snapshot(source); this.undoLimit = undoLimit;
    }
    public PmmDocument ExportDocument() => PmmTrackEditorRecords.Snapshot(current);
    public IReadOnlyList<PmmTrackRecord> ExportKeyframes() => Array.AsReadOnly(PmmTrackEditorRecords.Keys(current).Select(record => PmmTrackEditorRecords.Copy(record)).ToArray());
    public PmmTrackClipboard CopySelection(IEnumerable<PmmTrackAddress> selection)
    {
        var selected = Selection(selection);
        return new(PmmTrackEditorRecords.Keys(current).Where(record => selected.Contains(record.Address)));
    }
    public PmmTrackEditPlan PrepareRegister(IEnumerable<PmmTrackRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return Insert(records, []);
    }
    public PmmTrackEditPlan PreparePaste(PmmTrackClipboard clipboard, int targetFrame)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        if (targetFrame < 0) throw new ArgumentOutOfRangeException(nameof(targetFrame));
        if (clipboard.SourceStartFrame is not { } first) return Plan(current, [], false);
        return Insert(clipboard.Records.Select(record => PmmTrackEditorRecords.Copy(record, Shift(record.Address.Frame, (long)targetFrame - first))), []);
    }
    public PmmTrackEditPlan PrepareMove(IEnumerable<PmmTrackAddress> selection, long frameDelta)
    {
        var selected = Selection(selection);
        if (frameDelta == 0 || selected.Count == 0) return Plan(current, [], false);
        ProtectAnchors(selected);
        var moved = PmmTrackEditorRecords.Keys(current).Where(record => selected.Contains(record.Address))
            .Select(record => PmmTrackEditorRecords.Copy(record, Shift(record.Address.Frame, frameDelta)));
        return Insert(moved, selected);
    }
    public PmmTrackEditPlan PrepareDelete(IEnumerable<PmmTrackAddress> selection)
    {
        var selected = Selection(selection); ProtectAnchors(selected);
        if (selected.Count == 0) return Plan(current, [], false);
        return Plan(PmmTrackEditorRecords.Rebuild(current, PmmTrackEditorRecords.Keys(current).Where(record => !selected.Contains(record.Address)), selected), [], true);
    }

    /// <summary>Registers one full model state after changing an outside-parent slot. Missing subject slots are initialized detached at every existing key.
    /// This is raw PMM relative binding, not the session editor's keep-world binding operation.</summary>
    public PmmTrackEditPlan PrepareOutsideParent(int modelIndex, string subjectBoneName, int frame, int parentModelIndex, string? parentBoneName = null)
    {
        if (current.Version != 2) throw new NotSupportedException("PMM 0001 has no outside-parent tracks.");
        if (frame < 0) throw new ArgumentOutOfRangeException(nameof(frame));
        if (parentModelIndex < -2) throw new ArgumentOutOfRangeException(nameof(parentModelIndex));
        if (parentModelIndex >= 0 && parentBoneName is null)
            throw new ArgumentException("An attached outside parent requires an explicit target bone name.", nameof(parentBoneName));
        if (parentModelIndex == -2 && parentBoneName is not null)
            throw new ArgumentException("A ground outside parent does not take a target bone name.", nameof(parentBoneName));
        var model = PmmTrackEditorRecords.Model(current, modelIndex);
        int subject = PmmTrackEditorRecords.Bone(model, subjectBoneName);
        int parentBone = parentModelIndex >= 0
            ? PmmTrackEditorRecords.Bone(PmmTrackEditorRecords.Model(current, parentModelIndex), parentBoneName!)
            : -1;
        PmmDocument baseline = current;
        int slot = model.OutsideParentSubjectBoneIndices.ToList().IndexOf(subject);
        if (slot < 0)
        {
            baseline = PmmTrackEditorRecords.AddOutsideParentSlot(current, modelIndex, subject);
            slot = baseline.Models.Single(actor => actor.Index == modelIndex).OutsideParentSubjectBoneIndices.Count - 1;
        }
        var state = PmmTrackEditorRecords.ModelAt(baseline.Models.Single(actor => actor.Index == modelIndex), frame);
        var parents = state.OutsideParents.ToArray(); parents[slot] = new(0, parentModelIndex, parentBone);
        var key = new PmmModelKeyframe { Visible = state.Visible, ConstraintEnabled = state.ConstraintEnabled, OutsideParents = parents, Selected = state.Selected };
        var address = new PmmTrackAddress(PmmTrackKind.Model, modelIndex, "", frame);
        return Insert([new(address, key)], [], baseline);
    }

    public void Apply(PmmTrackEditPlan plan, bool confirmOverwrite = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Owner != owner || plan.Revision != Revision) throw new InvalidOperationException("The PMM edit plan belongs to another editor or an expired revision; run preflight again.");
        if (plan.Conflicts.Count != 0 && !confirmOverwrite) throw new InvalidOperationException("Destination PMM keys exist; explicit overwrite confirmation is required.");
        if (!plan.Changes) return;
        ulong revision = checked(Revision + 1);
        undo.Add((current, plan.Candidate)); if (undo.Count > undoLimit) undo.RemoveAt(0);
        redo.Clear(); current = plan.Candidate; Revision = revision;
    }
    public bool Undo()
    {
        if (!CanUndo) return false;
        ulong revision = checked(Revision + 1); var change = undo[^1]; undo.RemoveAt(undo.Count - 1);
        redo.Push(change); current = change.Before; Revision = revision; return true;
    }
    public bool Redo()
    {
        if (!CanRedo) return false;
        ulong revision = checked(Revision + 1); var change = redo.Pop(); undo.Add(change);
        current = change.After; Revision = revision; return true;
    }

    PmmTrackEditPlan Insert(IEnumerable<PmmTrackRecord> records, HashSet<PmmTrackAddress> remove, PmmDocument? baseline = null)
    {
        baseline ??= current;
        PmmTrackRecord[] incoming = records.Select(record => PmmTrackEditorRecords.ValidateAndCopy(baseline, record)).ToArray();
        var targets = incoming.Select(record => record.Address).ToHashSet();
        if (targets.Count != incoming.Length) throw new ArgumentException("Registration contains duplicate PMM addresses.", nameof(records));
        if (incoming.Length == 0) return Plan(current, [], false);
        var remaining = PmmTrackEditorRecords.Keys(baseline).Where(record => !remove.Contains(record.Address)).ToArray();
        var conflicts = remaining.GroupBy(record => record.Address).Where(group => targets.Contains(group.Key))
            .OrderBy(group => group.Key.Kind).ThenBy(group => group.Key.OwnerIndex).ThenBy(group => group.Key.BoneName, StringComparer.Ordinal).ThenBy(group => group.Key.Frame)
            .Select(group => new PmmTrackConflict(group.Key, group.Count())).ToArray();
        var changed = targets.Concat(remove).ToHashSet();
        var candidate = PmmTrackEditorRecords.Rebuild(baseline, remaining.Where(record => !targets.Contains(record.Address)).Concat(incoming), changed);
        return Plan(candidate, conflicts, true);
    }
    PmmTrackEditPlan Plan(PmmDocument candidate, IEnumerable<PmmTrackConflict> conflicts, bool changes) => new(owner, Revision, candidate, conflicts, changes);
    HashSet<PmmTrackAddress> Selection(IEnumerable<PmmTrackAddress> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var selected = selection.ToHashSet(); var existing = PmmTrackEditorRecords.Keys(current).Select(record => record.Address).ToHashSet();
        if (selected.Any(address => !existing.Contains(address))) throw new ArgumentException("A selected PMM key does not exist.", nameof(selection));
        return selected;
    }
    static void ProtectAnchors(IEnumerable<PmmTrackAddress> selection)
    { if (selection.Any(address => address.Frame == 0)) throw new InvalidOperationException("PMM frame-zero anchors cannot be removed or moved; overwrite their value instead."); }
    static int Shift(int frame, long delta)
    {
        if (delta < -(long)frame || delta > (long)int.MaxValue - frame) throw new ArgumentOutOfRangeException(nameof(delta));
        return (int)(frame + delta);
    }
}

