using System.Numerics;
using System.Windows.Input;
using Mmd.Modern.Animation;
using Mmd.Modern.Application;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;

namespace Mmd.Modern.App;

public sealed partial class EditorAnimationViewModel
{
    private IReadOnlyList<int> selectedBoneIndices = [];
    private (string Name, VpdLocalBoneTransform Pose)[] copiedPose = [];
    private bool playing;

    public IReadOnlyList<int> SelectedBoneIndices => selectedBoneIndices;
    public ICommand CopyPoseCommand { get; }
    public ICommand RegisterPoseCommand { get; }
    public ICommand PasteMirroredPoseCommand { get; }
    public IReadOnlyList<int> DraftFrames => actorDrafts.Keys.Where(key => key.Actor == objectId)
        .Select(key => key.Frame).Distinct().Order().ToArray();
    public IReadOnlyList<KeyframeAddress> TimelineDrafts
    {
        get
        {
            var addresses = new List<KeyframeAddress>();
            foreach (var ((actor, frame), pending) in actorDrafts)
            {
                if (actor != objectId) continue;
                addresses.AddRange(pending.Bones.Keys.Select(index => KeyframeAddress.Bone(pending.Model.Bones[index].Name, (uint)frame)));
                addresses.AddRange(pending.Morphs.Keys.Select(index => KeyframeAddress.Morph(pending.Model.Morphs[index].Name, (uint)frame)));
            }
            return addresses.AsReadOnly();
        }
    }

    internal event EventHandler? DraftContentEdited;
    internal Action<bool>? RegistrationHistory { get; private set; }

    // The workspace stores this patch with the exact motion transaction. Later edits to other
    // actors, frames and channels survive Undo/Redo, including a newer draft on the same channel.
    private Action<bool>? CreateRegistrationHistory(Guid actor, bool registerBones)
    {
        var key = (actor, CurrentFrame);
        if (!actorDrafts.TryGetValue(key, out var source)) return null;
        var bones = registerBones ? new Dictionary<int, BoneDraft>(source.Bones) : [];
        var morphs = new Dictionary<int, decimal?>();
        if (!registerBones && morphIndex is { } morph && source.Morphs.TryGetValue(morph, out var weight)) morphs.Add(morph, weight);
        if (bones.Count == 0 && morphs.Count == 0) return null;
        return restore =>
        {
            if (!actorDrafts.TryGetValue(key, out var pending))
            {
                if (!restore) return;
                actorDrafts.Add(key, pending = new(source.Model, source.Source));
            }
            if (!ReferenceEquals(pending.Model, source.Model)) return;
            foreach (var (index, draft) in bones)
            {
                if (restore) pending.Bones.TryAdd(index, draft);
                else if (pending.Bones.GetValueOrDefault(index) == draft) pending.Bones.Remove(index);
            }
            foreach (var (index, draft) in morphs)
            {
                if (restore) pending.Morphs.TryAdd(index, draft);
                else if (pending.Morphs.GetValueOrDefault(index) == draft) pending.Morphs.Remove(index);
            }
            if (pending.IsEmpty) actorDrafts.Remove(key);
            NotifyDraftAddresses(); LoadPose(); Changed(nameof(HasInvalidDrafts)); PoseDraftChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    private void NotifyDraftAddresses()
    {
        Changed(nameof(TimelineDrafts));
        Changed(nameof(DraftFrames));
    }

    internal void NotifyDraftContentEdited()
    {
        NotifyDraftAddresses();
        DraftContentEdited?.Invoke(this, EventArgs.Empty);
    }

    internal void SetPlayback(bool value)
    {
        if (playing == value) return;
        playing = value;
        LoadPose();
    }

    internal void SynchronizeDraftActors(IReadOnlyList<SceneActorMotion> actors)
    {
        if (actorDrafts.Count == 0) return;
        var available = actors.ToDictionary(actor => actor.ObjectId);
        bool removed = false;
        foreach (var (key, pending) in actorDrafts.ToArray())
        {
            if (!available.TryGetValue(key.Actor, out var actor) || !ReferenceEquals(actor.Model, pending.Model))
                removed |= actorDrafts.Remove(key);
            else pending.Source = actor.Motion;
        }
        if (removed) { NotifyDraftAddresses(); LoadPose(); }
    }

    /// <summary>The last selected bone is the active numeric/viewport channel.</summary>
    public void SelectBones(IReadOnlyList<int> indices)
    {
        ArgumentNullException.ThrowIfNull(indices);
        var next = indices.Where(index => model is not null && (uint)index < (uint)model.Bones.Count).Distinct().ToArray();
        SelectBone(next.Length == 0 ? null : next[^1]);
        if (next.SequenceEqual(selectedBoneIndices)) return;
        selectedBoneIndices = Array.AsReadOnly(next);
        contextRevision++;
        Changed(nameof(SelectedBoneIndices)); RefreshCommands();
    }

    private bool HasInvalidBoneRegistration => objectId is { } id && actorDrafts.TryGetValue((id, CurrentFrame), out var pending)
        && pending.Bones.Any(pair => !pair.Value.IsValid || detachment?.BoneNames.Contains(pending.Model.Bones[pair.Key].Name) == true);
    private bool CanRegisterBones => HasActor && !playing && !IsBusy && animationContextActive
        && CanMutateProject?.Invoke() != false && ValidateInput?.Invoke() != false
        && !HasInvalidBoneRegistration && (actorDrafts.GetValueOrDefault((objectId!.Value, CurrentFrame))?.Bones.Count > 0
            || selectedBoneIndices.Count > 0 && !IsSelectedUnowned && DraftIsValid);
    private bool CanCopyPose => !playing && !IsBusy && animationContextActive && selectedBoneIndices.Count > 0
        && !HasInvalidBoneRegistration && CanMutateProject?.Invoke() != false;
    private bool CanPastePose => !playing && !IsBusy && animationContextActive && HasActor && copiedPose.Length != 0
        && CanMutateProject?.Invoke() != false;

    private VpdLocalBoneTransform LocalBonePose(int index)
    {
        if (actorDrafts.TryGetValue((objectId!.Value, CurrentFrame), out var pending)
            && pending.Bones.TryGetValue(index, out var draft)) return new(draft.Translation, draft.Rotation);
        if (index == boneIndex) return new(DraftTranslation, DraftRotation);
        var name = model!.Bones[index].Name;
        if (FindExactBoneKey(name, (uint)CurrentFrame) is { } exact) return new(exact.Translation, exact.Rotation);
        var clip = MmdBoneMotionAdapter.Convert(new() { BoneKeyframes = EffectiveMotion.BoneKeyframes.Where(key => key.BoneName == name).ToArray() });
        var sampler = new CharacterAnimator(new CharacterRig([new RigBone(name, Vector3.Zero, -1)], [], []), clip);
        var matrix = sampler.Sample(CurrentFrame).BoneWorldMatrices[0];
        return new(matrix.Translation, Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(matrix)));
    }

    public void CopyPose()
    {
        if (!CanCopyPose) return;
        copiedPose = selectedBoneIndices.Select(index => (model!.Bones[index].Name, LocalBonePose(index))).ToArray();
        RefreshCommands();
    }

    public void PasteMirroredPose()
    {
        if (!CanPastePose || objectId is not { } id || model is null) return;
        var byName = model.Bones.Select((bone, index) => (bone.Name, Index: index)).ToLookup(pair => pair.Name, StringComparer.Ordinal);
        var changes = new Dictionary<int, BoneDraft>();
        foreach (var (name, pose) in copiedPose)
        {
            string mirrored = name.Contains('左') ? name.Replace('左', '右') : name.Replace('右', '左');
            var matches = byName[mirrored].ToArray();
            if (matches.Length != 1 || detachment?.BoneNames.Contains(mirrored) == true) continue;
            var translation = new Vector3(-pose.Translation.X, pose.Translation.Y, pose.Translation.Z);
            var rotation = new Quaternion(pose.Rotation.X, -pose.Rotation.Y, -pose.Rotation.Z, pose.Rotation.W);
            var degrees = ToDegrees(Quaternion.Normalize(rotation));
            changes[matches[0].Index] = new(ToDecimal(translation.X), ToDecimal(translation.Y), ToDecimal(translation.Z),
                ToDecimal(degrees.X), ToDecimal(degrees.Y), ToDecimal(degrees.Z), rotation, false);
        }
        if (changes.Count == 0) return;
        if (!actorDrafts.TryGetValue((id, CurrentFrame), out var pending)) actorDrafts.Add((id, CurrentFrame), pending = new(model, sourceMotion));
        foreach (var (index, draft) in changes) pending.Bones[index] = draft;
        LoadPose(); Changed(nameof(HasInvalidDrafts)); NotifyDraftContentEdited(); PoseDraftChanged?.Invoke(this, EventArgs.Empty);
    }
}
