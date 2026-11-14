using Mmd.Modern.Formats;

namespace Mmd.Modern.Application.MotionEditing;

public enum KeyframeTrackKind { Bone, Morph, Ik }

public enum BoneInterpolationChannel { X, Y, Z, Rotation }

/// <summary>VMD Bezier control bytes, retained without floating-point quantization.</summary>
public readonly record struct VmdBezierControl
{
    public byte X1 { get; }
    public byte Y1 { get; }
    public byte X2 { get; }
    public byte Y2 { get; }
    public VmdBezierControl(int x1, int y1, int x2, int y2)
    {
        if (x1 is < 0 or > 127 || y1 is < 0 or > 127 || x2 is < 0 or > 127 || y2 is < 0 or > 127)
            throw new ArgumentOutOfRangeException(nameof(x1), "VMD Bezier controls must be in [0,127].");
        X1 = (byte)x1; Y1 = (byte)y1; X2 = (byte)x2; Y2 = (byte)y2;
    }
}

/// <summary>An address selects every duplicate record at a track's frame.</summary>
public readonly record struct KeyframeAddress(KeyframeTrackKind Kind, string Name, uint Frame)
{
    public static KeyframeAddress Bone(string name, uint frame) => new(KeyframeTrackKind.Bone, name, frame);
    public static KeyframeAddress Morph(string name, uint frame) => new(KeyframeTrackKind.Morph, name, frame);
    public static KeyframeAddress Ik(uint frame) => new(KeyframeTrackKind.Ik, string.Empty, frame);
}

public sealed record KeyframeConflict(KeyframeAddress Address, int ExistingRecordCount);

/// <summary>Detached character-track records in source order. The earliest selected frame anchors paste.</summary>
public sealed class MotionClipboard
{
    internal VmdMotion Content { get; }
    public IReadOnlyList<VmdBoneKeyframe> BoneKeyframes => Content.BoneKeyframes;
    public IReadOnlyList<VmdMorphKeyframe> MorphKeyframes => Content.MorphKeyframes;
    public IReadOnlyList<VmdIkKeyframe> IkKeyframes => Content.IkKeyframes;
    public uint? SourceStartFrame { get; }
    public bool IsEmpty => SourceStartFrame is null;

    internal MotionClipboard(VmdMotion detachedContent)
    {
        Content = detachedContent;
        SourceStartFrame = BoneKeyframes.Select(k => (uint?)k.Frame)
            .Concat(MorphKeyframes.Select(k => (uint?)k.Frame))
            .Concat(IkKeyframes.Select(k => (uint?)k.Frame)).Min();
    }
}

/// <summary>Preflight result bound to one editor revision. A UI supplies its own confirmation dialog.</summary>
public sealed class MotionEditPlan
{
    internal Guid Owner { get; }
    internal VmdMotion Candidate { get; }
    internal bool Changes { get; }
    public ulong Revision { get; }
    public IReadOnlyList<KeyframeConflict> Conflicts { get; }
    internal MotionEditPlan(Guid owner, ulong revision, VmdMotion candidate,
        IEnumerable<KeyframeConflict> conflicts, bool changes)
    {
        Owner = owner; Revision = revision; Candidate = candidate; Changes = changes;
        Conflicts = Array.AsReadOnly(conflicts.ToArray());
    }
}

/// <summary>Headless character-track transactions with explicit overwrite and exact undo.</summary>
public sealed class MotionEditor
{
    private readonly Guid owner = Guid.NewGuid();
    private readonly int undoLimit;
    private readonly List<(VmdMotion Before, VmdMotion After)> undo = [];
    private readonly Stack<(VmdMotion Before, VmdMotion After)> redo = [];
    private VmdMotion current;
    public ulong Revision { get; private set; }
    public bool CanUndo => undo.Count != 0;
    public bool CanRedo => redo.Count != 0;

    public MotionEditor(VmdMotion source, int undoLimit = 1000)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (undoLimit < 1) throw new ArgumentOutOfRangeException(nameof(undoLimit));
        this.undoLimit = undoLimit;
        current = Snapshot(source);
    }

    public VmdMotion ExportMotion() => Snapshot(current);

    /// <summary>Edits one channel on selected destination keys, which own their incoming segment curves.</summary>
    public MotionEditPlan PrepareSetInterpolation(IEnumerable<KeyframeAddress> selection,
        BoneInterpolationChannel channel, VmdBezierControl curve)
    {
        if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        var selected = ValidateSelection(selection);
        if (selected.Any(a => a.Kind != KeyframeTrackKind.Bone))
            throw new ArgumentException("Only bone tracks have editable VMD bone interpolation.", nameof(selection));
        bool changes = false;
        var keys = current.BoneKeyframes.Select(key =>
        {
            if (!selected.Contains(KeyframeAddress.Bone(key.BoneName, key.Frame))) return key;
            byte[] bytes = key.Interpolation.ToArray();
            byte[] values = [curve.X1, curve.Y1, curve.X2, curve.Y2];
            // Canonical VMD repeats progressively shifted 16-byte matrices. Original MMD reads
            // channel c from block c, not exclusively from the first matrix (see keyframe-rea).
            for (int coordinate = 0; coordinate < 4; coordinate++)
                for (int block = 0; block < 4; block++)
                {
                    int canonical = 4 * coordinate + (int)channel;
                    if (canonical < block) continue;
                    int offset = 16 * block + canonical - block;
                    // Original MMD stores its per-bone physics marker here, overlapping Z/R.x1.
                    if (offset is 2 or 3) continue;
                    if (bytes[offset] != values[coordinate]) changes = true;
                    bytes[offset] = values[coordinate];
                }
            return new VmdBoneKeyframe { BoneName = key.BoneName, OriginalName = key.OriginalName, Frame = key.Frame,
                Translation = key.Translation, Rotation = key.Rotation, Interpolation = bytes };
        }).ToArray();
        var candidate = new VmdMotion
        {
            ModelName = current.ModelName, OriginalModelName = current.OriginalModelName, BoneKeyframes = Array.AsReadOnly(keys),
            MorphKeyframes = current.MorphKeyframes, IkKeyframes = current.IkKeyframes,
            CameraKeyframes = current.CameraKeyframes, LightKeyframes = current.LightKeyframes,
            SelfShadowKeyframes = current.SelfShadowKeyframes, Diagnostics = current.Diagnostics
        };
        return new(owner, Revision, candidate, [], changes);
    }

    /// <summary>Copies every record at each selected address; repeated addresses do not multiply records.</summary>
    public MotionClipboard CopySelection(IEnumerable<KeyframeAddress> selection)
    {
        var selected = ValidateSelection(selection);
        return new(Snapshot(new VmdMotion
        {
            BoneKeyframes = current.BoneKeyframes.Where(k => selected.Contains(KeyframeAddress.Bone(k.BoneName, k.Frame))).ToArray(),
            MorphKeyframes = current.MorphKeyframes.Where(k => selected.Contains(KeyframeAddress.Morph(k.MorphName, k.Frame))).ToArray(),
            IkKeyframes = current.IkKeyframes.Where(k => selected.Contains(KeyframeAddress.Ik(k.Frame))).ToArray()
        }));
    }

    /// <summary>Places the clipboard's earliest frame at targetFrame, preserving relative timing across tracks.</summary>
    public MotionEditPlan PreparePaste(MotionClipboard clipboard, uint targetFrame)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        if (clipboard.SourceStartFrame is not { } first)
            return new(owner, Revision, current, [], false);
        long delta = (long)targetFrame - first;
        var content = clipboard.Content;
        // Shift validates every record before a plan can be applied, so overflow cannot partially paste.
        return PrepareInsert(new VmdMotion
        {
            BoneKeyframes = content.BoneKeyframes.Select(k => Copy(k, Shift(k.Frame, delta))).ToArray(),
            MorphKeyframes = content.MorphKeyframes.Select(k => Copy(k, Shift(k.Frame, delta))).ToArray(),
            IkKeyframes = content.IkKeyframes.Select(k => Copy(k, Shift(k.Frame, delta))).ToArray()
        });
    }

    /// <summary>Registers supplied records at their absolute frames. Model name and diagnostics are retained from this editor.</summary>
    public MotionEditPlan PrepareRegister(VmdMotion records)
    {
        ArgumentNullException.ThrowIfNull(records);
        ValidateRegistration(records);
        return PrepareInsert(Snapshot(records));
    }

    private static void ValidateRegistration(VmdMotion records)
    {
        if (records.BoneKeyframes is null || records.MorphKeyframes is null || records.IkKeyframes is null)
            throw new ArgumentException("Registration track lists cannot be null.", nameof(records));
        foreach (var key in records.BoneKeyframes)
        {
            if (key is null || key.BoneName is null)
                throw new ArgumentException("Registered bone records and names cannot be null.", nameof(records));
            var t = key.Translation; var q = key.Rotation;
            if (!float.IsFinite(t.X) || !float.IsFinite(t.Y) || !float.IsFinite(t.Z)
                || !float.IsFinite(q.X) || !float.IsFinite(q.Y) || !float.IsFinite(q.Z) || !float.IsFinite(q.W))
                throw new ArgumentException("Registered bone translation and rotation must be finite.", nameof(records));
            // Match VmdReader: finite raw components are retained, even when their norm is not one.
            if (q.X == 0 && q.Y == 0 && q.Z == 0 && q.W == 0)
                throw new ArgumentException("Registered bone rotation cannot be a zero quaternion.", nameof(records));
        }
        foreach (var key in records.MorphKeyframes)
        {
            if (key is null || key.MorphName is null || !float.IsFinite(key.Weight))
                throw new ArgumentException("Registered morph records require a name and finite weight.", nameof(records));
        }
        foreach (var key in records.IkKeyframes)
        {
            if (key is null || key.Switches.Any(s => s.BoneName is null))
                throw new ArgumentException("Registered IK records and switch names cannot be null.", nameof(records));
        }
    }

    private MotionEditPlan PrepareInsert(VmdMotion records)
    {
        records = InheritOriginalNames(records);
        var targets = Addresses(records).ToHashSet();
        if (targets.Count == 0) return new(owner, Revision, current, [], false);
        var conflicts = Addresses(current).Where(targets.Contains).GroupBy(a => a)
            .Select(g => new KeyframeConflict(g.Key, g.Count()));
        // Retain unaffected source order, then append incoming records in their own order, including duplicates.
        var candidate = new VmdMotion
        {
            ModelName = current.ModelName, OriginalModelName = current.OriginalModelName,
            CameraKeyframes = current.CameraKeyframes, LightKeyframes = current.LightKeyframes,
            SelfShadowKeyframes = current.SelfShadowKeyframes,
            BoneKeyframes = Array.AsReadOnly(current.BoneKeyframes.Where(k => !targets.Contains(KeyframeAddress.Bone(k.BoneName, k.Frame)))
                .Concat(records.BoneKeyframes).ToArray()),
            MorphKeyframes = Array.AsReadOnly(current.MorphKeyframes.Where(k => !targets.Contains(KeyframeAddress.Morph(k.MorphName, k.Frame)))
                .Concat(records.MorphKeyframes).ToArray()),
            IkKeyframes = Array.AsReadOnly(current.IkKeyframes.Where(k => !targets.Contains(KeyframeAddress.Ik(k.Frame)))
                .Concat(records.IkKeyframes).ToArray()),
            Diagnostics = current.Diagnostics
        };
        return new(owner, Revision, candidate, conflicts, true);
    }

    private VmdMotion InheritOriginalNames(VmdMotion records)
    {
        static Dictionary<string, VmdEncodedName[]> Names(IEnumerable<(string Name, VmdEncodedName? Raw)> values) => values
            .Where(item => item.Raw is not null).GroupBy(item => item.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Raw!)
                .DistinctBy(raw => Convert.ToHexString(raw.Bytes.ToArray())).ToArray(), StringComparer.Ordinal);
        var bones = Names(current.BoneKeyframes.Select(key => (key.BoneName, key.OriginalName)));
        var morphs = Names(current.MorphKeyframes.Select(key => (key.MorphName, key.OriginalName)));
        var ik = Names(current.IkKeyframes.SelectMany(key => key.Switches).Select(item => (item.BoneName, item.OriginalName)));
        static VmdEncodedName? Resolve(string name, VmdEncodedName? explicitName, Dictionary<string, VmdEncodedName[]> known)
        {
            if (explicitName is not null)
            {
                if (explicitName.Value != name) throw new ArgumentException($"Original name metadata is stale for '{name}'.");
                return explicitName;
            }
            if (!known.TryGetValue(name, out var candidates)) return null;
            if (candidates.Length != 1 || candidates[0].Value != name)
                throw new ArgumentException($"Cannot inherit ambiguous original name bytes for '{name}'; supply explicit name metadata.");
            return candidates[0];
        }
        return new()
        {
            BoneKeyframes = records.BoneKeyframes.Select(key => new VmdBoneKeyframe
            { BoneName = key.BoneName, OriginalName = Resolve(key.BoneName, key.OriginalName, bones), Frame = key.Frame,
                Translation = key.Translation, Rotation = key.Rotation, Interpolation = key.Interpolation }).ToArray(),
            MorphKeyframes = records.MorphKeyframes.Select(key => new VmdMorphKeyframe
            { MorphName = key.MorphName, OriginalName = Resolve(key.MorphName, key.OriginalName, morphs), Frame = key.Frame, Weight = key.Weight }).ToArray(),
            IkKeyframes = records.IkKeyframes.Select(key => new VmdIkKeyframe
            { Frame = key.Frame, Visible = key.Visible, Switches = key.Switches.Select(item => new VmdIkSwitch(item.BoneName, item.Enabled)
                { OriginalName = Resolve(item.BoneName, item.OriginalName, ik) }).ToArray() }).ToArray()
        };
    }

    public MotionEditPlan PrepareMove(IEnumerable<KeyframeAddress> selection, long frameDelta)
    {
        var selected = ValidateSelection(selection);
        var destinations = selected.ToDictionary(a => a, a => a with { Frame = Shift(a.Frame, frameDelta) });
        var existing = Addresses(current).Where(a => !selected.Contains(a)).GroupBy(a => a).ToDictionary(g => g.Key, g => g.Count());
        var conflicts = destinations.Values.Distinct().Where(existing.ContainsKey)
            .Select(a => new KeyframeConflict(a, existing[a])).ToArray();
        if (selected.Count == 0 || frameDelta == 0)
            return new(owner, Revision, current, [], false);
        var targets = destinations.Values.ToHashSet();
        bool Keep(KeyframeAddress a) => selected.Contains(a) || !targets.Contains(a);
        uint Frame(KeyframeAddress a) => destinations.TryGetValue(a, out var target) ? target.Frame : a.Frame;
        var candidate = new VmdMotion
        {
            ModelName = current.ModelName, OriginalModelName = current.OriginalModelName,
            CameraKeyframes = current.CameraKeyframes, LightKeyframes = current.LightKeyframes,
            SelfShadowKeyframes = current.SelfShadowKeyframes,
            BoneKeyframes = Array.AsReadOnly(current.BoneKeyframes.Where(k => Keep(KeyframeAddress.Bone(k.BoneName, k.Frame)))
                .Select(k => Copy(k, Frame(KeyframeAddress.Bone(k.BoneName, k.Frame)))).ToArray()),
            MorphKeyframes = Array.AsReadOnly(current.MorphKeyframes.Where(k => Keep(KeyframeAddress.Morph(k.MorphName, k.Frame)))
                .Select(k => new VmdMorphKeyframe { MorphName = k.MorphName, OriginalName = k.OriginalName, Frame = Frame(KeyframeAddress.Morph(k.MorphName, k.Frame)), Weight = k.Weight }).ToArray()),
            IkKeyframes = Array.AsReadOnly(current.IkKeyframes.Where(k => Keep(KeyframeAddress.Ik(k.Frame)))
                .Select(k => new VmdIkKeyframe { Frame = Frame(KeyframeAddress.Ik(k.Frame)), Visible = k.Visible, Switches = k.Switches }).ToArray()),
            Diagnostics = current.Diagnostics
        };
        return new(owner, Revision, candidate, conflicts, true);
    }

    public MotionEditPlan PrepareDelete(IEnumerable<KeyframeAddress> selection)
    {
        var selected = ValidateSelection(selection);
        var candidate = new VmdMotion
        {
            ModelName = current.ModelName, OriginalModelName = current.OriginalModelName,
            CameraKeyframes = current.CameraKeyframes, LightKeyframes = current.LightKeyframes,
            SelfShadowKeyframes = current.SelfShadowKeyframes,
            BoneKeyframes = Array.AsReadOnly(current.BoneKeyframes.Where(k => !selected.Contains(KeyframeAddress.Bone(k.BoneName, k.Frame))).ToArray()),
            MorphKeyframes = Array.AsReadOnly(current.MorphKeyframes.Where(k => !selected.Contains(KeyframeAddress.Morph(k.MorphName, k.Frame))).ToArray()),
            IkKeyframes = Array.AsReadOnly(current.IkKeyframes.Where(k => !selected.Contains(KeyframeAddress.Ik(k.Frame))).ToArray()),
            Diagnostics = current.Diagnostics
        };
        return new(owner, Revision, candidate, [], selected.Count != 0);
    }

    public void Apply(MotionEditPlan plan, bool confirmOverwrite = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Owner != owner || plan.Revision != Revision)
            throw new InvalidOperationException("The edit plan belongs to a different editor or an expired revision; run preflight again.");
        if (plan.Conflicts.Count != 0 && !confirmOverwrite)
            throw new InvalidOperationException("Destination keyframes exist; explicit overwrite confirmation is required.");
        if (!plan.Changes) return;
        ulong next = checked(Revision + 1);
        undo.Add((current, plan.Candidate));
        if (undo.Count > undoLimit) undo.RemoveAt(0);
        redo.Clear(); current = plan.Candidate; Revision = next;
    }

    public bool Undo()
    {
        if (!CanUndo) return false;
        ulong next = checked(Revision + 1);
        var change = undo[^1]; redo.Push(change); undo.RemoveAt(undo.Count - 1);
        current = change.Before; Revision = next; return true;
    }

    public bool Redo()
    {
        if (!CanRedo) return false;
        ulong next = checked(Revision + 1);
        var change = redo.Pop(); undo.Add(change);
        current = change.After; Revision = next; return true;
    }

    private HashSet<KeyframeAddress> ValidateSelection(IEnumerable<KeyframeAddress> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var result = selection.ToHashSet();
        var existing = Addresses(current).ToHashSet();
        foreach (var address in result)
        {
            if (!Enum.IsDefined(address.Kind) || address.Name is null || (address.Kind == KeyframeTrackKind.Ik && address.Name.Length != 0))
                throw new ArgumentException("Invalid keyframe address.", nameof(selection));
            if (!existing.Contains(address))
                throw new ArgumentException($"Keyframe does not exist: {address}.", nameof(selection));
        }
        return result;
    }

    private static uint Shift(uint frame, long delta)
    {
        // Bounds before addition prevent Int64 wraparound even for adversarial deltas.
        if (delta < -(long)frame || delta > (long)uint.MaxValue - frame)
            throw new ArgumentOutOfRangeException(nameof(delta), "Moved keyframe is outside the VMD frame range.");
        return (uint)(frame + delta);
    }

    private static IEnumerable<KeyframeAddress> Addresses(VmdMotion motion) =>
        motion.BoneKeyframes.Select(k => KeyframeAddress.Bone(k.BoneName, k.Frame))
            .Concat(motion.MorphKeyframes.Select(k => KeyframeAddress.Morph(k.MorphName, k.Frame)))
            .Concat(motion.IkKeyframes.Select(k => KeyframeAddress.Ik(k.Frame)));

    private static VmdBoneKeyframe Copy(VmdBoneKeyframe key, uint frame) => new()
    { BoneName = key.BoneName, OriginalName = key.OriginalName, Frame = frame, Translation = key.Translation, Rotation = key.Rotation, Interpolation = key.Interpolation };

    private static VmdMorphKeyframe Copy(VmdMorphKeyframe key, uint frame) => new()
    { MorphName = key.MorphName, OriginalName = key.OriginalName, Frame = frame, Weight = key.Weight };

    private static VmdIkKeyframe Copy(VmdIkKeyframe key, uint frame) => new()
    { Frame = frame, Visible = key.Visible, Switches = key.Switches };

    private static VmdMotion Snapshot(VmdMotion motion) => new()
    {
        ModelName = motion.ModelName, OriginalModelName = motion.OriginalModelName,
        BoneKeyframes = Array.AsReadOnly(motion.BoneKeyframes.Select(k => Copy(k, k.Frame)).ToArray()),
        MorphKeyframes = Array.AsReadOnly(motion.MorphKeyframes.Select(k => Copy(k, k.Frame)).ToArray()),
        IkKeyframes = Array.AsReadOnly(motion.IkKeyframes.Select(k => Copy(k, k.Frame)).ToArray()),
        CameraKeyframes = Array.AsReadOnly(motion.CameraKeyframes.Select(k => new VmdCameraKeyframe
        { Frame = k.Frame, Distance = k.Distance, Interest = k.Interest, Rotation = k.Rotation,
            Interpolation = k.Interpolation, ViewAngle = k.ViewAngle, Perspective = k.Perspective }).ToArray()),
        LightKeyframes = Array.AsReadOnly(motion.LightKeyframes.Select(k => new VmdLightKeyframe
        { Frame = k.Frame, Color = k.Color, Direction = k.Direction }).ToArray()),
        SelfShadowKeyframes = Array.AsReadOnly(motion.SelfShadowKeyframes.Select(k => new VmdSelfShadowKeyframe
        { Frame = k.Frame, Mode = k.Mode, Distance = k.Distance }).ToArray()),
        Diagnostics = Array.AsReadOnly(motion.Diagnostics.ToArray())
    };
}
