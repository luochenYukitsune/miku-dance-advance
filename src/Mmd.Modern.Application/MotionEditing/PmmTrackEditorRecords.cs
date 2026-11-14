// PMM record layout and opacity packing: nanoem MIT ext/document.c/document_p.h
// @30acffaa29f5d2eb9e997d69418f2e4b97b5894f. No nanoem editor-host code.
using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application.MotionEditing;

internal static class PmmTrackEditorRecords
{
    internal static PmmDocument Snapshot(PmmDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        byte[] bytes = PmmWriter.ToBytes(source);
        return PmmReader.Read(new MemoryStream(bytes, writable: false), new()
        {
            AnsiCodePage = source.AnsiCodePage == 0 ? 936 : source.AnsiCodePage,
            V1ModelMetadataResolver = reference => source.Models.FirstOrDefault(model => model.Index == reference.StoredModelIndex) is { } model
                ? new(model.BoneNames.Select(name => name.Value), model.MorphNames.Select(name => name.Value), model.ConstraintBoneIndices) : null,
        });
    }

    internal static IEnumerable<PmmTrackRecord> Keys(PmmDocument source)
    {
        if (source.Version == 2)
            foreach (var key in new[] { source.Gravity.InitialKeyframe }.Concat(source.Gravity.Keyframes)) yield return new(new(PmmTrackKind.Gravity, 0, "", key.Frame), key);
        foreach (var model in source.Models)
        {
            foreach (var key in new[] { model.InitialModelKeyframe }.Concat(model.ModelKeyframes)) yield return new(new(PmmTrackKind.Model, model.Index, "", key.Frame), key);
            foreach (var (key, track) in PmmMotionAdapter.ResolveTracks(model.InitialBoneKeyframes, model.BoneKeyframes, model.BoneNames.Count, "edited bone"))
                yield return new(new(PmmTrackKind.Bone, model.Index, model.BoneNames[track].Value, key.Frame), key);
        }
        foreach (var accessory in source.Accessories)
            foreach (var key in new[] { accessory.InitialKeyframe }.Concat(accessory.Keyframes)) yield return new(new(PmmTrackKind.Accessory, accessory.Index, "", key.Frame), key);
    }
    internal static PmmModel Model(PmmDocument source, int index) => source.Models.SingleOrDefault(model => model.Index == index)
        ?? throw new ArgumentException($"PMM stored model index {index} does not exist.");
    internal static int Bone(PmmModel model, string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        int[] matches = model.BoneNames.Select((bone, index) => (bone, index)).Where(pair => pair.bone.Value == name).Select(pair => pair.index).ToArray();
        return matches.Length == 1 ? matches[0] : throw new ArgumentException($"PMM bone '{name}' must resolve to one exact name in model #{model.Index}.");
    }
    internal static PmmTrackRecord ValidateAndCopy(PmmDocument source, PmmTrackRecord record)
    {
        ArgumentNullException.ThrowIfNull(record); ArgumentNullException.ThrowIfNull(record.Value);
        var address = record.Address;
        if (!Enum.IsDefined(address.Kind) || address.Frame < 0 || address.BoneName is null) throw new ArgumentException("Invalid PMM track address.");
        if (address.Kind != PmmTrackKind.Bone && address.BoneName.Length != 0) throw new ArgumentException("Only bone addresses carry a bone name.");
        bool valid = address.Kind switch
        {
            PmmTrackKind.Gravity => address.OwnerIndex == 0 && record.Value is PmmGravityKeyframe && source.Version == 2,
            PmmTrackKind.Model => record.Value is PmmModelKeyframe && Model(source, address.OwnerIndex) is not null,
            PmmTrackKind.Bone => record.Value is PmmBoneKeyframe && Bone(Model(source, address.OwnerIndex), address.BoneName) >= 0,
            PmmTrackKind.Accessory => record.Value is PmmAccessoryKeyframe && source.Accessories.Any(accessory => accessory.Index == address.OwnerIndex),
            _ => false,
        };
        if (!valid) throw new ArgumentException("PMM key type, owner or version does not match its address.");
        if (record.Value is PmmBoneKeyframe bone && (!Finite(bone.Translation) || !Finite(bone.Rotation) || LengthSquared(bone.Rotation) == 0))
            throw new InvalidDataException("PMM bone keys require finite transforms and a nonzero quaternion.");
        if (record.Value is PmmBoneKeyframe controls && (controls.Interpolation is null || controls.Interpolation.Count != 16 || controls.Interpolation.Any(value => value > 127)))
            throw new InvalidDataException("PMM bone keys require sixteen Bezier control bytes in [0,127].");
        if (record.Value is PmmGravityKeyframe gravity) _ = PmmPhysicsSettings.ToGravity(gravity.Acceleration, gravity.Direction);
        if (record.Value is PmmModelKeyframe modelKey && modelKey.OutsideParents.Any(parent => parent.ModelIndex >= 0 && parent.BoneIndex < 0))
            throw new InvalidDataException("An attached outside parent requires a valid target bone for playback.");
        if (record.Value is PmmAccessoryKeyframe accessoryKey && accessoryKey.OpacityAndVisibility > 201)
            throw new InvalidDataException("PMM accessory opacity must be between 0 and 100 percent.");
        return Copy(record);
    }
    internal static PmmTrackRecord Copy(PmmTrackRecord record) => Copy(record, record.Address.Frame);
    internal static PmmTrackRecord Copy(PmmTrackRecord record, int frame) => new(record.Address with { Frame = frame }, CloneKey(record.Value, frame));
    static PmmKeyframe CloneKey(PmmKeyframe value, int frame) => value switch
    {
        PmmGravityKeyframe key => new PmmGravityKeyframe { Frame = frame, NoiseEnabled = key.NoiseEnabled, Noise = key.Noise, Acceleration = key.Acceleration, Direction = key.Direction, Selected = key.Selected },
        PmmBoneKeyframe key => new PmmBoneKeyframe { Frame = frame, Translation = key.Translation, Rotation = key.Rotation, Interpolation = key.Interpolation.ToArray(), PhysicsDisabled = key.PhysicsDisabled, Selected = key.Selected },
        PmmModelKeyframe key => new PmmModelKeyframe { Frame = frame, Visible = key.Visible, ConstraintEnabled = key.ConstraintEnabled.ToArray(), OutsideParents = key.OutsideParents.Select(parent => parent with { }).ToArray(), Selected = key.Selected },
        PmmAccessoryKeyframe key => new PmmAccessoryKeyframe { Frame = frame, OpacityAndVisibility = key.OpacityAndVisibility, ParentModelIndex = key.ParentModelIndex, ParentBoneIndex = key.ParentBoneIndex, Translation = key.Translation, Rotation = key.Rotation, Scale = key.Scale, ShadowEnabled = key.ShadowEnabled, Selected = key.Selected },
        _ => throw new ArgumentException("Unsupported PMM special-track key type."),
    };

    internal static PmmDocument Rebuild(PmmDocument source, IEnumerable<PmmTrackRecord> records, IReadOnlySet<PmmTrackAddress> changed)
    {
        var keys = records.ToArray();
        var models = source.Models.Select(model =>
        {
            bool bonesChanged = changed.Any(address => address.Kind == PmmTrackKind.Bone && address.OwnerIndex == model.Index);
            bool modelChanged = changed.Any(address => address.Kind == PmmTrackKind.Model && address.OwnerIndex == model.Index);
            if (!bonesChanged && !modelChanged) return model;
            PmmBoneKeyframe[] initialBones = model.InitialBoneKeyframes.ToArray(), extraBones = model.BoneKeyframes.ToArray();
            PmmModelKeyframe initialModel = model.InitialModelKeyframe; PmmModelKeyframe[] extraModel = model.ModelKeyframes.ToArray();
            var boneStates = model.BoneStates.ToArray(); var constraintStates = model.ConstraintStates.ToArray();
            var outsideStates = model.OutsideParentStates.ToArray(); bool visible = model.Visible;
            if (bonesChanged)
            {
                var boneTracks = model.BoneNames.Select(name => keys.Where(record => record.Address.Kind == PmmTrackKind.Bone && record.Address.OwnerIndex == model.Index && record.Address.BoneName == name.Value)
                    .Select(record => (PmmBoneKeyframe)record.Value).OrderBy(key => key.Frame).ToArray()).ToArray();
                (initialBones, extraBones) = Relink(boneTracks);
                for (int index = 0; index < model.BoneNames.Count; index++)
                {
                    if (!changed.Any(address => address.Kind == PmmTrackKind.Bone && address.OwnerIndex == model.Index && address.BoneName == model.BoneNames[index].Value)) continue;
                    var (translation, rotation) = BoneAt(boneTracks[index], source.CurrentFrame);
                    boneStates[index] = boneStates[index] with { Translation = translation, Rotation = rotation, PhysicsDisabled = At(boneTracks[index], source.CurrentFrame).PhysicsDisabled, Dirty = true };
                }
            }
            if (modelChanged)
            {
                var modelKeys = keys.Where(record => record.Address.Kind == PmmTrackKind.Model && record.Address.OwnerIndex == model.Index).Select(record => (PmmModelKeyframe)record.Value).OrderBy(key => key.Frame).ToArray();
                var (initial, extra) = Relink<PmmModelKeyframe>([modelKeys]); initialModel = initial[0]; extraModel = extra;
                var state = At(modelKeys, source.CurrentFrame); visible = state.Visible;
                if (state.ConstraintEnabled.Count != constraintStates.Length || state.OutsideParents.Count != outsideStates.Length)
                    throw new InvalidDataException("PMM model key IK/outside-parent counts must match its model tables.");
                constraintStates = constraintStates.Select((old, index) => old with { Enabled = state.ConstraintEnabled[index] }).ToArray();
                outsideStates = outsideStates.Select((old, index) =>
                {
                    var parent = state.OutsideParents[index];
                    int begin = modelKeys.Where(key => key.Frame <= source.CurrentFrame).OrderByDescending(key => key.Frame).TakeWhile(key => SameParent(key.OutsideParents[index], parent)).LastOrDefault()?.Frame ?? modelKeys[0].Frame;
                    int end = modelKeys.FirstOrDefault(key => key.Frame > source.CurrentFrame && !SameParent(key.OutsideParents[index], parent)) is { } next ? next.Frame - 1 : int.MaxValue;
                    return old with { BeginFrame = begin, EndFrame = end, Parent = parent };
                }).ToArray();
            }
            // LastFrame describes the resulting model tracks, not an ever-growing historical maximum.
            int lastFrame = initialBones.Concat(extraBones).Select(key => key.Frame)
                .Concat(model.InitialMorphKeyframes.Concat(model.MorphKeyframes).Select(key => key.Frame))
                .Concat(new[] { initialModel }.Concat(extraModel).Select(key => key.Frame)).DefaultIfEmpty(0).Max();
            return CopyModel(model, initialBones, extraBones, initialModel, extraModel, boneStates, constraintStates, outsideStates, visible, lastFrame);
        }).ToArray();
        var gravity = source.Gravity;
        if (changed.Any(address => address.Kind == PmmTrackKind.Gravity))
        {
            var gravityKeys = keys.Where(record => record.Address.Kind == PmmTrackKind.Gravity).Select(record => (PmmGravityKeyframe)record.Value).OrderBy(key => key.Frame).ToArray();
            var (initial, extra) = Relink<PmmGravityKeyframe>([gravityKeys]);
            var state = new PmmPhysicsControlTracks(new PmmDocument { Gravity = new() { InitialKeyframe = initial[0], Keyframes = extra } }).GravityAt(source.CurrentFrame);
            gravity = new() { Offset = gravity.Offset, Acceleration = state.Acceleration, Direction = state.Direction, Noise = state.Noise, NoiseEnabled = state.NoiseEnabled, InitialKeyframe = initial[0], Keyframes = extra };
        }
        var accessories = source.Accessories.Select(accessory =>
        {
            if (!changed.Any(address => address.Kind == PmmTrackKind.Accessory && address.OwnerIndex == accessory.Index)) return accessory;
            var accessoryKeys = keys.Where(record => record.Address.Kind == PmmTrackKind.Accessory && record.Address.OwnerIndex == accessory.Index).Select(record => (PmmAccessoryKeyframe)record.Value).OrderBy(key => key.Frame).ToArray();
            var (initial, extra) = Relink<PmmAccessoryKeyframe>([accessoryKeys]); var state = PmmTrackSampling.AccessoryAt(accessoryKeys, source.CurrentFrame);
            return new PmmAccessory { Offset = accessory.Offset, Index = accessory.Index, Name = accessory.Name, Path = accessory.Path, DrawOrderIndex = accessory.DrawOrderIndex,
                InitialKeyframe = initial[0], Keyframes = extra, OpacityAndVisibility = PmmTrackSampling.PackOpacity(state.Opacity, state.Visible), ParentModelIndex = state.ParentModelIndex, ParentBoneIndex = state.ParentBoneIndex,
                Translation = state.Translation, Rotation = state.Rotation, Scale = state.Scale, ShadowEnabled = state.ShadowEnabled, AddBlendEnabled = accessory.AddBlendEnabled };
        }).ToArray();
        var result = CopyDocument(source, models, gravity, accessories);
        ValidateOutsideParentCycles(result);
        return Snapshot(result); // Existing writer + strict reader own format validation; publish only after the complete candidate succeeds.
    }

    internal static PmmDocument AddOutsideParentSlot(PmmDocument source, int modelIndex, int subject)
    {
        var models = source.Models.Select(model =>
        {
            if (model.Index != modelIndex) return model;
            PmmModelKeyframe Append(PmmModelKeyframe key)
            {
                var copy = (PmmModelKeyframe)CloneKey(key, key.Frame);
                copy.ObjectIndex = key.ObjectIndex; copy.RecordIndex = key.RecordIndex; copy.PreviousIndex = key.PreviousIndex; copy.NextIndex = key.NextIndex;
                return new() { Frame = copy.Frame, ObjectIndex = copy.ObjectIndex, RecordIndex = copy.RecordIndex, PreviousIndex = copy.PreviousIndex, NextIndex = copy.NextIndex,
                    Visible = copy.Visible, ConstraintEnabled = copy.ConstraintEnabled, OutsideParents = copy.OutsideParents.Append(new(0, -1, -1)).ToArray(), Selected = copy.Selected };
            }
            return CopyModel(model, model.InitialBoneKeyframes, model.BoneKeyframes, Append(model.InitialModelKeyframe), model.ModelKeyframes.Select(Append).ToArray(), model.BoneStates,
                model.ConstraintStates, model.OutsideParentStates.Append(new(0, 0, int.MaxValue, new(0, -1, -1))).ToArray(), model.Visible, model.LastFrame,
                model.OutsideParentSubjectBoneIndices.Append(subject).ToArray());
        }).ToArray();
        return CopyDocument(source, models, source.Gravity, source.Accessories);
    }
    internal static PmmModelKeyframe ModelAt(PmmModel model, double frame) => At(new[] { model.InitialModelKeyframe }.Concat(model.ModelKeyframes).OrderBy(key => key.Frame).ToArray(), frame);
    static bool SameParent(PmmOutsideParent a, PmmOutsideParent b) => a.ModelIndex == b.ModelIndex && a.BoneIndex == b.BoneIndex;
    static void ValidateOutsideParentCycles(PmmDocument source)
    {
        int[] frames = source.Models.SelectMany(model => new[] { model.InitialModelKeyframe }.Concat(model.ModelKeyframes)).Select(key => key.Frame).Distinct().ToArray();
        foreach (int frame in frames)
        {
            var edges = new Dictionary<(int Model, int Bone), (int Model, int Bone)>();
            var modelEdges = new Dictionary<int, HashSet<int>>();
            foreach (var model in source.Models)
            {
                var state = ModelAt(model, frame);
                if (state.OutsideParents.Count != model.OutsideParentSubjectBoneIndices.Count) throw new InvalidDataException("PMM outside-parent slot count mismatch.");
                for (int slot = 0; slot < state.OutsideParents.Count; slot++)
                {
                    var parent = state.OutsideParents[slot];
                    if (parent.ModelIndex >= 0 && parent.BoneIndex >= 0)
                    {
                        edges.Add((model.Index, model.OutsideParentSubjectBoneIndices[slot]), (parent.ModelIndex, parent.BoneIndex));
                        if (!modelEdges.TryGetValue(model.Index, out var parents)) modelEdges[model.Index] = parents = [];
                        parents.Add(parent.ModelIndex);
                    }
                }
            }
            foreach (var start in edges.Keys)
            {
                var visited = new HashSet<(int Model, int Bone)>(); var at = start;
                while (edges.TryGetValue(at, out var next))
                { if (!visited.Add(at)) throw new InvalidDataException("PMM outside-parent binding contains a cycle."); at = next; }
            }
            // Playback evaluates whole models in dependency order. Different subject/target bones
            // can still form a model cycle even when the direct bone-edge graph is acyclic.
            var visiting = new HashSet<int>(); var complete = new HashSet<int>();
            void Visit(int model)
            {
                if (complete.Contains(model)) return;
                if (!visiting.Add(model)) throw new InvalidDataException("PMM outside-parent model dependency cycle.");
                if (modelEdges.TryGetValue(model, out var parents)) foreach (int parent in parents) Visit(parent);
                visiting.Remove(model); complete.Add(model);
            }
            foreach (int model in modelEdges.Keys) Visit(model);
        }
    }

    static (T[] Initial, T[] Extra) Relink<T>(T[][] tracks) where T : PmmKeyframe
    {
        var initial = new T[tracks.Length]; var extra = new List<T>(); var copies = new T[tracks.Length][];
        for (int track = 0; track < tracks.Length; track++)
        {
            if (tracks[track].Length == 0) throw new InvalidOperationException("A PMM track must retain its initial key.");
            copies[track] = tracks[track].Select(key => (T)CloneKey(key, key.Frame)).ToArray();
            for (int index = 0; index < copies[track].Length; index++)
            {
                var key = copies[track][index];
                key.RecordIndex = key.ObjectIndex = index == 0 ? track : tracks.Length + extra.Count;
                if (index == 0) initial[track] = key; else extra.Add(key);
            }
        }
        foreach (var track in copies) for (int index = 0; index < track.Length; index++)
        { track[index].PreviousIndex = index == 0 ? 0 : track[index - 1].ObjectIndex; track[index].NextIndex = index == track.Length - 1 ? 0 : track[index + 1].ObjectIndex; }
        return (initial, extra.ToArray());
    }
    internal static T At<T>(IReadOnlyList<T> keys, double frame) where T : PmmKeyframe
    {
        int low = 0, high = keys.Count;
        while (low < high) { int middle = low + (high - low) / 2; if (keys[middle].Frame <= frame) low = middle + 1; else high = middle; }
        return keys[Math.Max(0, low - 1)];
    }
    static (Vector3 Translation, Quaternion Rotation) BoneAt(PmmBoneKeyframe[] keys, int frame)
    {
        var a = At(keys, frame); int index = Array.IndexOf(keys, a);
        if (frame <= a.Frame || index == keys.Length - 1) return (a.Translation, a.Rotation);
        var b = keys[index + 1]; double t = (frame - (double)a.Frame) / (b.Frame - (double)a.Frame);
        double Curve(int channel)
        { int start = channel * 4; var c = b.Interpolation; return new BezierCurve(c[start] / 127f, c[start + 1] / 127f, c[start + 2] / 127f, c[start + 3] / 127f).Evaluate(t); }
        float L(float from, float to, double weight) => (float)((1 - weight) * from + weight * to);
        return (new(L(a.Translation.X, b.Translation.X, Curve(0)), L(a.Translation.Y, b.Translation.Y, Curve(1)), L(a.Translation.Z, b.Translation.Z, Curve(2))),
            Quaternion.Slerp(Normalize(a.Rotation), Normalize(b.Rotation), (float)Curve(3)));
    }
    static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    static bool Finite(Quaternion value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
    static double LengthSquared(Quaternion value) => (double)value.X * value.X + (double)value.Y * value.Y + (double)value.Z * value.Z + (double)value.W * value.W;
    static Quaternion Normalize(Quaternion value)
    { double length = Math.Sqrt(LengthSquared(value)); return new((float)(value.X / length), (float)(value.Y / length), (float)(value.Z / length), (float)(value.W / length)); }

    static PmmModel CopyModel(PmmModel source, IReadOnlyList<PmmBoneKeyframe> initialBones, IReadOnlyList<PmmBoneKeyframe> extraBones,
        PmmModelKeyframe initialModel, IReadOnlyList<PmmModelKeyframe> extraModel, IReadOnlyList<PmmBoneState> boneStates,
        IReadOnlyList<PmmConstraintState> constraints, IReadOnlyList<PmmOutsideParentState> outsideStates, bool visible, int lastFrame, IReadOnlyList<int>? outsideSubjects = null) => new()
    {
        Offset = source.Offset, Index = source.Index, Name = source.Name, EnglishName = source.EnglishName, Path = source.Path, FixedTrackCount = source.FixedTrackCount,
        BoneNames = source.BoneNames, MorphNames = source.MorphNames, ConstraintBoneIndices = source.ConstraintBoneIndices,
        OutsideParentSubjectBoneIndices = outsideSubjects ?? source.OutsideParentSubjectBoneIndices,
        DrawOrderIndex = source.DrawOrderIndex, Visible = visible, SelectedBoneIndex = source.SelectedBoneIndex, SelectedMorphIndices = source.SelectedMorphIndices,
        ExpansionStates = source.ExpansionStates, VerticalScroll = source.VerticalScroll, LastFrame = lastFrame,
        InitialBoneKeyframes = initialBones, BoneKeyframes = extraBones, InitialMorphKeyframes = source.InitialMorphKeyframes, MorphKeyframes = source.MorphKeyframes,
        InitialModelKeyframe = initialModel, ModelKeyframes = extraModel, BoneStates = boneStates, MorphStates = source.MorphStates,
        ConstraintStates = constraints, OutsideParentStates = outsideStates, BlendEnabled = source.BlendEnabled, EdgeWidth = source.EdgeWidth,
        SelfShadowEnabled = source.SelfShadowEnabled, TransformOrderIndex = source.TransformOrderIndex,
    };
    static PmmDocument CopyDocument(PmmDocument source, IReadOnlyList<PmmModel> models, PmmGravity gravity, IReadOnlyList<PmmAccessory> accessories) => new()
    {
        Version = source.Version, AnsiCodePage = source.AnsiCodePage, HeaderBytes = source.HeaderBytes, OutputWidth = source.OutputWidth, OutputHeight = source.OutputHeight,
        TimelineWidth = source.TimelineWidth, CameraFov = source.CameraFov, PanelStates = source.PanelStates, SelectedModelIndex = source.SelectedModelIndex, Models = models,
        V1ModelNames = source.V1ModelNames, Camera = source.Camera, Light = source.Light, SelectedAccessoryIndex = source.SelectedAccessoryIndex,
        AccessoryHorizontalScroll = source.AccessoryHorizontalScroll, AccessoryNames = source.AccessoryNames, Accessories = accessories, CurrentFrame = source.CurrentFrame,
        Settings = source.Settings, Gravity = gravity, SelfShadow = source.SelfShadow, ParsedLength = source.ParsedLength, BytesConsumed = source.BytesConsumed,
        UnknownTail = source.UnknownTail, Diagnostics = source.Diagnostics,
    };
}

