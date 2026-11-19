// PMM links/interpolation: nanoem MIT ext/document.c + document_p.h
// @30acffaa29f5d2eb9e997d69418f2e4b97b5894f. No nanoem editor-host code.
using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>Exports edited named character tracks into an existing PMM 0002 project.</summary>
public static class PmmEditedMotionExporter
{
    /// <summary>
    /// Replaces only bone/morph tracks present in <paramref name="motion"/>. Missing tracks remain unchanged;
    /// duplicate edited frames use their last record. IK/visibility updates merge with existing model keys,
    /// retaining outside-parent transitions. Unrelated project fields and models are preserved.
    /// The model position addresses the document's Models list, not the model's stored Index byte.
    /// By default, edited saved editor channels refresh at CurrentFrame using raw pre-constraint local animation.
    /// Scene VMD sections and erasure of existing per-bone physics transition frames are explicitly rejected.
    /// This is not a complete SceneAnimationSession/layout exporter. It never writes to the input document.
    /// </summary>
    public static PmmDocument ReplaceModelMotion(PmmDocument source, int modelPosition, PmxModel model,
        VmdMotion motion, bool refreshSavedPose = true)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(motion);
        if (source.Models is null || (uint)modelPosition >= (uint)source.Models.Count) throw new ArgumentOutOfRangeException(nameof(modelPosition));
        if (motion.BoneKeyframes is null || motion.MorphKeyframes is null || motion.IkKeyframes is null || motion.CameraKeyframes is null || motion.LightKeyframes is null || motion.SelfShadowKeyframes is null)
            throw new InvalidDataException("Edited motion contains a null track collection.");
        if (motion.CameraKeyframes.Count != 0 || motion.LightKeyframes.Count != 0 || motion.SelfShadowKeyframes.Count != 0)
            throw new NotSupportedException("Character PMM export does not accept camera, light or self-shadow edits; existing project scene tracks are preserved.");
        if (refreshSavedPose && source.CurrentFrame < 0) throw new InvalidDataException("Saved editor pose requires a nonnegative current frame.");
        // Validate all source sections and their sparse-ID chains before constructing a candidate.
        _ = PmmWriter.ToBytes(source);
        var actor = source.Models[modelPosition];
        var boneNames = ResolveNames(actor.BoneNames, model.Bones.Select(b => b.Name), "bone");
        var morphNames = ResolveNames(actor.MorphNames, model.Morphs.Select(m => m.Name), "morph");
        var boneTracks = Tracks(actor.InitialBoneKeyframes, actor.BoneKeyframes);
        var morphTracks = Tracks(actor.InitialMorphKeyframes, actor.MorphKeyframes);
        var changedBones = new HashSet<int>(); var changedMorphs = new HashSet<int>();

        foreach (var key in motion.BoneKeyframes)
        {
            if (key is null) throw new InvalidDataException("Edited bone key is null.");
            _ = Resolve(boneNames, key.BoneName, "bone"); _ = Frame(key.Frame);
            if (!Finite(key.Translation) || !Finite(key.Rotation) || LengthSquared(key.Rotation) == 0)
                throw new InvalidDataException("Edited bone transform must be finite with a nonzero quaternion.");
            _ = BoneInterpolation(key.Interpolation);
        }
        foreach (var group in motion.BoneKeyframes.GroupBy(k => k.BoneName, StringComparer.Ordinal))
        {
            int track = Resolve(boneNames, group.Key, "bone"); changedBones.Add(track);
            var old = boneTracks[track];
            var oldByFrame = old.GroupBy(k => k.Frame).ToDictionary(g => g.Key, g => g.Last());
            // Match the character import policy: a replaced bone track starts at rest at frame zero,
            // not at the old PMM track's unrelated first pose. Preserve only its editor/physics metadata.
            var keys = new SortedDictionary<int, PmmBoneKeyframe> { [0] = new()
            {
                Frame = 0, Translation = Vector3.Zero, Rotation = Quaternion.Identity,
                Interpolation = new byte[] { 20, 20, 107, 107, 20, 20, 107, 107, 20, 20, 107, 107, 20, 20, 107, 107 },
                PhysicsDisabled = old[0].PhysicsDisabled, Selected = old[0].Frame == 0 && old[0].Selected,
            } };
            foreach (var key in group)
            {
                int frame = Frame(key.Frame); var metadata = At(old, frame);
                keys[frame] = new() { Frame = frame, Translation = key.Translation, Rotation = key.Rotation,
                    Interpolation = BoneInterpolation(key.Interpolation), PhysicsDisabled = metadata.PhysicsDisabled,
                    Selected = oldByFrame.TryGetValue(frame, out var match) && match.Selected };
            }
            var effective = oldByFrame.Values.OrderBy(k => k.Frame).ToArray();
            for (int i = 1; i < effective.Length; i++)
                if (effective[i].PhysicsDisabled != effective[i - 1].PhysicsDisabled && !keys.ContainsKey(effective[i].Frame))
                    throw new NotSupportedException($"Replacing bone '{group.Key}' would remove its physics switch at frame {effective[i].Frame}; retain a key at that frame.");
            boneTracks[track] = keys.Values.ToArray();
        }
        foreach (var key in motion.MorphKeyframes)
        {
            if (key is null) throw new InvalidDataException("Edited morph key is null.");
            _ = Resolve(morphNames, key.MorphName, "morph"); _ = Frame(key.Frame);
            if (!float.IsFinite(key.Weight)) throw new InvalidDataException("Edited morph weight must be finite.");
        }
        foreach (var group in motion.MorphKeyframes.GroupBy(k => k.MorphName, StringComparer.Ordinal))
        {
            int track = Resolve(morphNames, group.Key, "morph"); changedMorphs.Add(track);
            var old = morphTracks[track];
            var oldByFrame = old.GroupBy(k => k.Frame).ToDictionary(g => g.Key, g => g.Last());
            // Morph tracks clamp to their first edited value before their first key; they do not seed zero.
            var first = group.GroupBy(k => k.Frame).OrderBy(g => g.Key).First().Last();
            var keys = new SortedDictionary<int, PmmMorphKeyframe> { [0] = new()
            { Frame = 0, Weight = first.Weight, Selected = old[0].Frame == 0 && old[0].Selected } };
            foreach (var key in group)
            {
                int frame = Frame(key.Frame);
                keys[frame] = new() { Frame = frame, Weight = key.Weight, Selected = oldByFrame.TryGetValue(frame, out var match) && match.Selected };
            }
            morphTracks[track] = keys.Values.ToArray();
        }
        var modelTracks = Tracks<PmmModelKeyframe>([actor.InitialModelKeyframe], actor.ModelKeyframes);
        bool changedModel = motion.IkKeyframes.Count != 0;
        var changedConstraints = new HashSet<int>();
        if (changedModel)
        {
            var constraintNames = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var pair in boneNames)
            {
                int index = IndexOf(actor.ConstraintBoneIndices, pair.Value);
                if (index >= 0) constraintNames.Add(pair.Key, index);
            }
            foreach (var key in motion.IkKeyframes)
            {
                if (key is null) throw new InvalidDataException("Edited IK key is null.");
                _ = Frame(key.Frame);
                var unique = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in key.Switches)
                {
                    changedConstraints.Add(Resolve(constraintNames, item.BoneName, "IK constraint"));
                    if (!unique.Add(item.BoneName)) throw new InvalidDataException("Edited IK key contains a duplicate constraint name.");
                }
            }
            var old = modelTracks[0];
            var edits = motion.IkKeyframes.GroupBy(k => Frame(k.Frame)).ToDictionary(g => g.Key, g => g.Last());
            var frames = old.Select(k => k.Frame).Concat(edits.Keys).Distinct().Order().ToArray();
            var selectedFrames = old.Where(k => k.Selected).Select(k => k.Frame).ToHashSet();
            var switches = new Dictionary<int, bool>(); bool? visible = null;
            modelTracks[0] = frames.Select(frame =>
            {
                var metadata = At(old, frame); var enabled = metadata.ConstraintEnabled.ToArray();
                if (edits.TryGetValue(frame, out var edit))
                {
                    visible = edit.Visible;
                    foreach (var item in edit.Switches) switches[constraintNames[item.BoneName]] = item.Enabled;
                }
                foreach (var pair in switches) enabled[pair.Key] = pair.Value;
                return new PmmModelKeyframe { Frame = frame, Visible = visible ?? metadata.Visible,
                    ConstraintEnabled = enabled, OutsideParents = metadata.OutsideParents, Selected = selectedFrames.Contains(frame) };
            }).ToArray();
        }
        var (initialBones, extraBones) = Relink(boneTracks);
        var (initialMorphs, extraMorphs) = Relink(morphTracks);
        var (initialModel, extraModel) = Relink(modelTracks);
        IReadOnlyList<PmmBoneState> boneStates = actor.BoneStates;
        IReadOnlyList<PmmMorphState> morphStates = actor.MorphStates;
        IReadOnlyList<PmmConstraintState> constraintStates = actor.ConstraintStates;
        bool savedVisible = actor.Visible;
        if (refreshSavedPose)
        {
            boneStates = actor.BoneStates.Select((state, i) =>
            {
                if (!changedBones.Contains(i)) return state;
                var (translation, rotation) = BoneAt(boneTracks[i], source.CurrentFrame);
                return state with { Translation = translation, Rotation = rotation, Dirty = true, PhysicsDisabled = At(boneTracks[i], source.CurrentFrame).PhysicsDisabled };
            }).ToArray();
            morphStates = actor.MorphStates.Select((state, i) => changedMorphs.Contains(i) ? state with { Weight = MorphAt(morphTracks[i], source.CurrentFrame) } : state).ToArray();
            if (changedModel)
            {
                var state = At(modelTracks[0], source.CurrentFrame); savedVisible = state.Visible;
                constraintStates = actor.ConstraintStates.Select((old, i) => changedConstraints.Contains(i) ? old with { Enabled = state.ConstraintEnabled[i] } : old).ToArray();
            }
        }
        int lastFrame = Math.Max(actor.LastFrame, boneTracks.SelectMany(t => t).Select(k => k.Frame)
            .Concat(morphTracks.SelectMany(t => t).Select(k => k.Frame)).Concat(modelTracks[0].Select(k => k.Frame)).DefaultIfEmpty(0).Max());
        var replacement = CopyModel(actor, initialBones, extraBones, initialMorphs, extraMorphs, initialModel[0], extraModel,
            boneStates, morphStates, constraintStates, savedVisible, lastFrame);
        var models = source.Models.ToArray(); models[modelPosition] = replacement;
        var result = CopyDocument(source, models);
        _ = PmmWriter.ToBytes(result); // A fully serializable candidate; no external file has been touched.
        return result;
    }

    static Dictionary<string, int> ResolveNames(IReadOnlyList<PmmString> names, IEnumerable<string> pmxNames, string kind)
    {
        var counts = pmxNames.GroupBy(n => n, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < names.Count; i++)
        {
            var candidates = PmmAssetPathResolver.DecodeCandidates(names[i]).Where(counts.ContainsKey).Distinct(StringComparer.Ordinal).ToArray();
            if (candidates.Length > 1 || (candidates.Length == 1 && (counts[candidates[0]] != 1 || !result.TryAdd(candidates[0], i))))
                throw new InvalidDataException($"PMM {kind} '{names[i].Value}' has an ambiguous PMX binding.");
        }
        return result;
    }
    static int Resolve(Dictionary<string, int> names, string name, string kind)
        => name is not null && names.TryGetValue(name, out int value) ? value : throw new InvalidDataException($"Edited {kind} '{name}' has no unique exact PMM/PMX name binding.");
    static int IndexOf(IReadOnlyList<int> items, int value) { for (int i = 0; i < items.Count; i++) if (items[i] == value) return i; return -1; }
    static int Frame(uint frame) => frame <= int.MaxValue ? (int)frame : throw new InvalidDataException("PMM frame exceeds Int32 range.");
    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    static bool Finite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);
    static double LengthSquared(Quaternion q) => (double)q.X * q.X + (double)q.Y * q.Y + (double)q.Z * q.Z + (double)q.W * q.W;
    static Quaternion Normalize(Quaternion q) { double length = Math.Sqrt(LengthSquared(q)); return new((float)(q.X / length), (float)(q.Y / length), (float)(q.Z / length), (float)(q.W / length)); }
    static byte[] BoneInterpolation(IReadOnlyList<byte> raw)
    {
        if (raw is null || raw.Count != 64) throw new InvalidDataException("VMD bone requires 64 interpolation bytes.");
        var result = new byte[16];
        for (int c = 0; c < 4; c++) for (int i = 0; i < 4; i++)
        {
            // PMM stores 0..127; imported VMD bytes above that are clamped exactly as playback evaluates them.
            result[4 * c + i] = Math.Min(raw[16 * c + 4 * i], (byte)127);
        }
        return result;
    }
    static T[][] Tracks<T>(IReadOnlyList<T> initial, IReadOnlyList<T> extra) where T : PmmKeyframe
    {
        var byId = initial.Concat(extra).ToDictionary(k => k.ObjectIndex); var result = new T[initial.Count][];
        for (int i = 0; i < initial.Count; i++)
        {
            var track = new List<T>(); T key = initial[i];
            while (true)
            {
                track.Add(key); if (key.NextIndex == 0) break;
                T next = byId[key.NextIndex];
                if (next.Frame < key.Frame) throw new InvalidDataException("PMM track links run backwards in frame order.");
                key = next;
            }
            result[i] = track.ToArray();
        }
        return result;
    }
    static (T[] Initial, T[] Extra) Relink<T>(T[][] tracks) where T : PmmKeyframe
    {
        var initial = new T[tracks.Length]; var extra = new List<T>(); var clones = new T[tracks.Length][];
        for (int track = 0; track < tracks.Length; track++)
        {
            clones[track] = tracks[track].Select(k => (T)CopyKey(k)).ToArray();
            for (int i = 0; i < clones[track].Length; i++)
            {
                T key = clones[track][i]; key.RecordIndex = key.ObjectIndex = i == 0 ? track : tracks.Length + extra.Count;
                if (i == 0) initial[track] = key; else extra.Add(key);
            }
        }
        foreach (var track in clones) for (int i = 0; i < track.Length; i++)
        { track[i].PreviousIndex = i == 0 ? 0 : track[i - 1].ObjectIndex; track[i].NextIndex = i == track.Length - 1 ? 0 : track[i + 1].ObjectIndex; }
        return (initial, extra.ToArray());
    }
    static PmmKeyframe CopyKey(PmmKeyframe key) => key switch
    {
        PmmBoneKeyframe k => Copy(k), PmmMorphKeyframe k => Copy(k),
        PmmModelKeyframe k => new PmmModelKeyframe { Frame = k.Frame, Visible = k.Visible, ConstraintEnabled = k.ConstraintEnabled, OutsideParents = k.OutsideParents, Selected = k.Selected },
        _ => throw new InvalidOperationException("Unsupported PMM track type."),
    };
    static PmmBoneKeyframe Copy(PmmBoneKeyframe k) => new() { Frame = k.Frame, Translation = k.Translation, Rotation = k.Rotation, Interpolation = k.Interpolation, PhysicsDisabled = k.PhysicsDisabled, Selected = k.Selected };
    static PmmMorphKeyframe Copy(PmmMorphKeyframe k) => new() { Frame = k.Frame, Weight = k.Weight, Selected = k.Selected };
    static T At<T>(T[] keys, int frame) where T : PmmKeyframe
    {
        int low = 0, high = keys.Length;
        while (low < high) { int middle = low + (high - low) / 2; if (keys[middle].Frame <= frame) low = middle + 1; else high = middle; }
        return keys[Math.Max(0, low - 1)];
    }
    static (T A, T B, double T) Segment<T>(T[] keys, int frame) where T : PmmKeyframe
    {
        T a = At(keys, frame); int index = Array.IndexOf(keys, a);
        if (frame <= a.Frame || index == keys.Length - 1) return (a, a, 0);
        T b = keys[index + 1]; return (a, b, (frame - (double)a.Frame) / (b.Frame - (double)a.Frame));
    }
    static (Vector3 Translation, Quaternion Rotation) BoneAt(PmmBoneKeyframe[] keys, int frame)
    {
        var (a, b, t) = Segment(keys, frame);
        if (a == b) return (a.Translation, a.Rotation);
        double Curve(int c) { var raw = b.Interpolation; int i = c * 4; return BezierCurve.FromMmd(raw[i], raw[i + 1], raw[i + 2], raw[i + 3]).Evaluate(t); }
        float L(float x, float y, double weight) => (float)((1 - weight) * x + weight * y);
        return (new(L(a.Translation.X, b.Translation.X, Curve(0)), L(a.Translation.Y, b.Translation.Y, Curve(1)), L(a.Translation.Z, b.Translation.Z, Curve(2))),
            Quaternion.Slerp(Normalize(a.Rotation), Normalize(b.Rotation), (float)Curve(3)));
    }
    static float MorphAt(PmmMorphKeyframe[] keys, int frame) { var (a, b, t) = Segment(keys, frame); return (float)((1 - t) * a.Weight + t * b.Weight); }

    static PmmModel CopyModel(PmmModel s, PmmBoneKeyframe[] bones, PmmBoneKeyframe[] extraBones, PmmMorphKeyframe[] morphs,
        PmmMorphKeyframe[] extraMorphs, PmmModelKeyframe model, PmmModelKeyframe[] extraModel,
        IReadOnlyList<PmmBoneState> boneStates, IReadOnlyList<PmmMorphState> morphStates, IReadOnlyList<PmmConstraintState> constraintStates, bool visible, int lastFrame) => new()
    {
        Offset = s.Offset, Index = s.Index, Name = s.Name, EnglishName = s.EnglishName, Path = s.Path, FixedTrackCount = s.FixedTrackCount,
        BoneNames = s.BoneNames, MorphNames = s.MorphNames, ConstraintBoneIndices = s.ConstraintBoneIndices, OutsideParentSubjectBoneIndices = s.OutsideParentSubjectBoneIndices,
        DrawOrderIndex = s.DrawOrderIndex, Visible = visible, SelectedBoneIndex = s.SelectedBoneIndex, SelectedMorphIndices = s.SelectedMorphIndices,
        ExpansionStates = s.ExpansionStates, VerticalScroll = s.VerticalScroll, LastFrame = lastFrame,
        InitialBoneKeyframes = bones, BoneKeyframes = extraBones, InitialMorphKeyframes = morphs, MorphKeyframes = extraMorphs,
        InitialModelKeyframe = model, ModelKeyframes = extraModel, BoneStates = boneStates, MorphStates = morphStates,
        ConstraintStates = constraintStates, OutsideParentStates = s.OutsideParentStates, BlendEnabled = s.BlendEnabled, EdgeWidth = s.EdgeWidth,
        SelfShadowEnabled = s.SelfShadowEnabled, TransformOrderIndex = s.TransformOrderIndex,
    };
    static PmmDocument CopyDocument(PmmDocument s, IReadOnlyList<PmmModel> models) => new()
    {
        Version = s.Version, AnsiCodePage = s.AnsiCodePage, HeaderBytes = s.HeaderBytes, OutputWidth = s.OutputWidth, OutputHeight = s.OutputHeight,
        TimelineWidth = s.TimelineWidth, CameraFov = s.CameraFov, PanelStates = s.PanelStates, SelectedModelIndex = s.SelectedModelIndex, Models = models,
        Camera = s.Camera, Light = s.Light, SelectedAccessoryIndex = s.SelectedAccessoryIndex, AccessoryHorizontalScroll = s.AccessoryHorizontalScroll,
        AccessoryNames = s.AccessoryNames, Accessories = s.Accessories, CurrentFrame = s.CurrentFrame, Settings = s.Settings, Gravity = s.Gravity,
        SelfShadow = s.SelfShadow, ParsedLength = s.ParsedLength, BytesConsumed = s.BytesConsumed, UnknownTail = s.UnknownTail, Diagnostics = s.Diagnostics,
    };
}
