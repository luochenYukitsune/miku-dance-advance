using System.Collections.ObjectModel;
using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Application;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;

namespace Mmd.Modern.App;

/// <summary>A detached registration preflight. The host confirms every conflict before committing
/// all returned motions in one workspace transaction.</summary>
public sealed class EditorAnimationDraftMotionPlan
{
    private readonly VmdMotion source;
    private readonly VmdMotion records;
    public Guid ObjectId { get; }
    public MotionEditPlan Plan { get; }

    internal EditorAnimationDraftMotionPlan(Guid objectId, VmdMotion source, VmdMotion records)
    {
        ObjectId = objectId;
        this.source = new MotionEditor(source).ExportMotion();
        this.records = new MotionEditor(records).ExportMotion();
        Plan = new MotionEditor(this.source).PrepareRegister(this.records);
    }

    /// <summary>Materializes a fresh candidate without modifying the actor, drafts or workspace Undo.
    /// Existing keys require the host's explicit confirmation.</summary>
    public VmdMotion CreateMotion(bool confirmOverwrite = false)
    {
        var staged = new MotionEditor(source);
        staged.Apply(staged.PrepareRegister(records), confirmOverwrite);
        return staged.ExportMotion();
    }
}

public sealed partial class EditorAnimationViewModel
{
    /// <summary>Includes incomplete numeric input on every cached model and channel.</summary>
    public bool HasInvalidDrafts => actorDrafts.Values.Any(draft =>
        draft.Bones.Values.Any(bone => !bone.IsValid) || draft.Morphs.Values.Any(weight => !weight.HasValue));

    /// <summary>Restores PMM's saved editor values at the current global frame. These are unregistered
    /// local channels, kept separate from authored motion and project Undo even for unselected actors.</summary>
    public void RestoreSavedPoses(IEnumerable<SceneActorMotion> actors,
        IReadOnlyDictionary<Guid, VmdMotion> poses, int frame)
    {
        ArgumentNullException.ThrowIfNull(actors);
        ArgumentNullException.ThrowIfNull(poses);
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        var restored = new Dictionary<Guid, ActorDrafts>();
        foreach (var actor in actors)
        {
            if (!poses.TryGetValue(actor.ObjectId, out var pose)) continue;
            var pending = new ActorDrafts(actor.Model, actor.Motion);
            var bones = FirstNamedIndices(actor.Model.Bones.Select(bone => bone.Name));
            var morphs = FirstNamedIndices(actor.Model.Morphs.Select(morph => morph.Name));
            foreach (var key in pose.BoneKeyframes)
            {
                if (!bones.TryGetValue(key.BoneName, out int index)
                    || !FiniteTranslation(key.Translation) || !TryNormalizeSavedRotation(key.Rotation, out var normalized)) continue;
                var degrees = ToDegrees(normalized);
                pending.Bones[index] = new(ToDecimal(key.Translation.X), ToDecimal(key.Translation.Y), ToDecimal(key.Translation.Z),
                    ToDecimal(degrees.X), ToDecimal(degrees.Y), ToDecimal(degrees.Z), key.Rotation, false);
            }
            foreach (var key in pose.MorphKeyframes)
                if (morphs.TryGetValue(key.MorphName, out int index) && float.IsFinite(key.Weight))
                    pending.Morphs[index] = ToDecimal(key.Weight);
            if (!pending.IsEmpty) restored.Add(actor.ObjectId, pending);
        }
        // Build the entire replacement first, so invalid enumeration cannot partially restore a document.
        actorDrafts.Clear();
        foreach (var pair in restored) actorDrafts.Add(pair.Key, pair.Value);
        currentFrame = frame;
        contextRevision++;
        Changed(nameof(CurrentFrame));
        Changed(nameof(HasInvalidDrafts));
        LoadPose();
        PoseDraftChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Prepares complete cached channels for one atomic host save. The host must confirm
    /// each Plan.Conflicts before CreateMotion(true), then commit the candidates together.</summary>
    public IReadOnlyList<EditorAnimationDraftMotionPlan> PrepareDraftMotions(IEnumerable<SceneActorMotion> actors)
    {
        ArgumentNullException.ThrowIfNull(actors);
        if (HasInvalidDrafts) throw new InvalidOperationException("请先完成所有未登记姿势的数字输入，再保存工程。");
        var available = actors.ToDictionary(actor => actor.ObjectId);
        var result = new List<EditorAnimationDraftMotionPlan>();
        uint frame = checked((uint)CurrentFrame);
        foreach (var (id, pending) in actorDrafts.OrderBy(pair => pair.Key))
        {
            if (pending.IsEmpty) continue;
            if (!available.TryGetValue(id, out var actor) || !ReferenceEquals(actor.Model, pending.Model)
                || !ReferenceEquals(actor.Motion, pending.Source))
                throw new InvalidOperationException("未登记姿势对应的模型或动作已变化，请重新检查编辑对象。");
            var bones = new List<VmdBoneKeyframe>();
            foreach (var (index, draft) in pending.Bones.OrderBy(pair => pair.Key))
            {
                if ((uint)index >= (uint)actor.Model.Bones.Count)
                    throw new InvalidOperationException("未登记姿势的骨骼已不在当前模型中。");
                var bone = actor.Model.Bones[index];
                var template = actor.Motion.BoneKeyframes.LastOrDefault(key => key.BoneName == bone.Name && key.Frame == frame);
                bones.Add(CreateDraftKey(bone, frame, draft.Translation, draft.Rotation, template));
            }
            var morphs = new List<VmdMorphKeyframe>();
            foreach (var (index, weight) in pending.Morphs.OrderBy(pair => pair.Key))
            {
                if ((uint)index >= (uint)actor.Model.Morphs.Count)
                    throw new InvalidOperationException("未登记姿势的表情已不在当前模型中。");
                morphs.Add(new() { MorphName = actor.Model.Morphs[index].Name, Frame = frame, Weight = (float)weight!.Value });
            }
            result.Add(new(id, actor.Motion, new() { BoneKeyframes = bones.AsReadOnly(), MorphKeyframes = morphs.AsReadOnly() }));
        }
        return result.AsReadOnly();
    }

    /// <summary>Samples authored local animation deltas, then overlays complete drafts at their global
    /// frame. No PMX parent, IK, append, bone morph, external parent or physics result is baked.</summary>
    public (VpdLocalBoneTransform[] Bones, IReadOnlyDictionary<int, float> MorphWeights)
        CaptureLocalPose(SceneActorMotion actor, int frame)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        var clip = MmdBoneMotionAdapter.Convert(actor.Motion);
        var tracksByName = clip.Tracks.ToDictionary(track => track.BoneName, StringComparer.Ordinal);
        var exactRotations = actor.Motion.BoneKeyframes.Where(key => key.Frame == (uint)frame)
            .GroupBy(key => key.BoneName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Rotation, StringComparer.Ordinal);
        var localAnimation = new VpdLocalBoneTransform[actor.Model.Bones.Count];
        for (int index = 0; index < localAnimation.Length; index++)
        {
            string name = actor.Model.Bones[index].Name;
            localAnimation[index] = new(Vector3.Zero, Quaternion.Identity);
            if (!tracksByName.TryGetValue(name, out var track)) continue;
            // This one-bone, zero-rest rig exposes only the existing evaluator's authored VMD curves.
            var sampler = new CharacterAnimator(new CharacterRig([new RigBone(name, Vector3.Zero, -1)], [], []),
                new MotionClip([track], clip.CurveOwnership));
            var local = sampler.Sample(frame).BoneWorldMatrices[0];
            var rotation = exactRotations.TryGetValue(name, out var exact) ? exact
                : Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(local));
            localAnimation[index] = new(local.Translation, rotation);
        }
        var morphIndices = FirstNamedIndices(actor.Model.Morphs.Select(morph => morph.Name));
        var morphWeights = new Dictionary<int, float>(new MmdMorphMotionAdapter(morphIndices, actor.Motion).Evaluate(frame));
        if (frame == CurrentFrame && actorDrafts.TryGetValue(actor.ObjectId, out var pending)
            && ReferenceEquals(pending.Model, actor.Model) && ReferenceEquals(pending.Source, actor.Motion))
        {
            foreach (var (index, draft) in pending.Bones)
                if (draft.IsValid && (uint)index < (uint)localAnimation.Length)
                    localAnimation[index] = new(draft.Translation, draft.Rotation);
            foreach (var (index, weight) in pending.Morphs)
                if (weight.HasValue && (uint)index < (uint)actor.Model.Morphs.Count)
                    morphWeights[index] = (float)weight.Value;
        }
        return (localAnimation, new ReadOnlyDictionary<int, float>(morphWeights));
    }

    private static Dictionary<string, int> FirstNamedIndices(IEnumerable<string> names)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        int index = 0;
        foreach (string name in names) result.TryAdd(name, index++);
        return result;
    }

    private static bool FiniteTranslation(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool TryNormalizeSavedRotation(Quaternion value, out Quaternion normalized)
    {
        double length = Math.Sqrt((double)value.X * value.X + (double)value.Y * value.Y
            + (double)value.Z * value.Z + (double)value.W * value.W);
        normalized = Quaternion.Identity;
        if (!double.IsFinite(length) || length == 0) return false;
        normalized = new((float)(value.X / length), (float)(value.Y / length),
            (float)(value.Z / length), (float)(value.W / length));
        return true;
    }
}
