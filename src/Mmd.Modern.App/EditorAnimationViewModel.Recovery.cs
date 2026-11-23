using System.Collections.Immutable;
using Mmd.Modern.Application;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;

namespace Mmd.Modern.App;

public sealed partial class EditorAnimationViewModel
{
    internal EditorRecoveryAnimationState CaptureRecoveryState(IReadOnlyList<SceneActorMotion> actors)
    {
        var available = actors.ToDictionary(actor => actor.ObjectId);
        foreach (var ((id, _), pending) in actorDrafts)
            if (!available.TryGetValue(id, out var cachedActor) || !ReferenceEquals(pending.Source, cachedActor.Motion)
                || !StringComparer.Ordinal.Equals(EditorRecoverySessionState.ModelStructureSignature(pending.Model),
                    EditorRecoverySessionState.ModelStructureSignature(cachedActor.Model)))
                throw EditorRecoverySessionState.Invalid("Recovery drafts no longer match the project actors.");
        if (objectId is { } selected && (!available.TryGetValue(selected, out var currentActor) || model is null
            || !StringComparer.Ordinal.Equals(EditorRecoverySessionState.ModelStructureSignature(model),
                EditorRecoverySessionState.ModelStructureSignature(currentActor.Model))))
            throw EditorRecoverySessionState.Invalid("Recovery selection no longer matches the project actor.");

        return new()
        {
            CurrentFrame = currentFrame,
            MoveTargetFrame = moveTargetFrame,
            ObjectId = objectId,
            SelectedBoneIndex = boneIndex,
            SelectedMorphIndex = morphIndex,
            TimelineTrackMode = timelineTrackMode,
            AnimationContextActive = animationContextActive,
            SelectedKeyframes = selectedKeyframes.ToImmutableArray(),
            TimelineRangeStartText = timelineRangeStartText,
            TimelineRangeEndText = timelineRangeEndText,
            Actors = actors.OrderBy(actor => actor.ObjectId).Select(CaptureDraftActor).ToImmutableArray()
        };
    }

    internal EditorRecoveryActorState CaptureDraftActor(SceneActorMotion actor)
    {
        var frames = actorDrafts.Where(pair => pair.Key.Actor == actor.ObjectId).OrderBy(pair => pair.Key.Frame).ToArray();
        return new()
        {
            ObjectId = actor.ObjectId,
            StructureSignature = EditorRecoverySessionState.ModelStructureSignature(actor.Model),
            BoneCount = actor.Model.Bones.Count,
            MorphCount = actor.Model.Morphs.Count,
            Bones = frames.SelectMany(pair => pair.Value.Bones.OrderBy(value => value.Key).Select(value =>
                new EditorRecoveryBoneDraft(value.Key, value.Value.X, value.Value.Y, value.Value.Z,
                    value.Value.Pitch, value.Value.Yaw, value.Value.Roll, value.Value.SampledRotation,
                    value.Value.RotationEdited) { Frame = pair.Key.Frame })).ToImmutableArray(),
            Morphs = frames.SelectMany(pair => pair.Value.Morphs.OrderBy(value => value.Key).Select(value =>
                new EditorRecoveryMorphDraft(value.Key, value.Value) { Frame = pair.Key.Frame })).ToImmutableArray()
        };
    }

    private static Dictionary<(Guid Actor, int Frame), ActorDrafts> ReadDrafts(IEnumerable<EditorRecoveryActorState> snapshots,
        IReadOnlyDictionary<Guid, SceneActorMotion> available, int defaultFrame)
    {
        var restored = new Dictionary<(Guid Actor, int Frame), ActorDrafts>();
        foreach (var snapshot in snapshots)
        {
            var actor = available[snapshot.ObjectId];
            ActorDrafts At(int? frame)
            {
                var key = (actor.ObjectId, frame ?? defaultFrame);
                if (!restored.TryGetValue(key, out var pending)) restored.Add(key, pending = new(actor.Model, actor.Motion));
                return pending;
            }
            foreach (var bone in snapshot.Bones)
                At(bone.Frame).Bones.Add(bone.Index, new(bone.X, bone.Y, bone.Z, bone.Pitch, bone.Yaw, bone.Roll,
                    bone.SampledRotation, bone.RotationEdited));
            foreach (var morph in snapshot.Morphs) At(morph.Frame).Morphs.Add(morph.Index, morph.Weight);
        }
        return restored;
    }

    internal void RestoreNativeDrafts(IEnumerable<EditorRecoveryActorState> snapshots, IReadOnlyList<SceneActorMotion> actors)
    {
        var next = ReadDrafts(snapshots, actors.ToDictionary(actor => actor.ObjectId), 0);
        actorDrafts.Clear();
        foreach (var pair in next) actorDrafts.Add(pair.Key, pair.Value);
        copiedPose = [];
        contextRevision++;
        NotifyDraftAddresses(); LoadPose();
    }

    internal Action PrepareRecoveryRestore(EditorRecoveryAnimationState saved, IReadOnlyList<SceneActorMotion> actors)
    {
        var available = actors.ToDictionary(actor => actor.ObjectId);
        var restored = ReadDrafts(saved.Actors, available, saved.CurrentFrame);
        var selected = saved.ObjectId is { } id ? available[id] : null;
        var restoredMotion = new MotionEditor(selected?.Motion ?? new VmdMotion()).ExportMotion();
        // Keep the current host's scene label when the stable actor identity is retained.
        string displayName = selected is not null && objectId == selected.ObjectId
            ? actorDisplayName : selected?.Model.Name ?? string.Empty;
        return () =>
        {
            objectId = selected?.ObjectId;
            model = selected?.Model;
            actorDisplayName = displayName;
            sourceMotion = selected?.Motion;
            detachment = selected?.Detachment;
            motion = restoredMotion;
            boneIndex = saved.SelectedBoneIndex;
            selectedBoneIndices = boneIndex is { } selectedBone ? [selectedBone] : [];
            morphIndex = saved.SelectedMorphIndex;
            currentFrame = saved.CurrentFrame;
            moveTargetFrame = saved.MoveTargetFrame;
            timelineTrackMode = saved.TimelineTrackMode;
            animationContextActive = saved.AnimationContextActive;
            selectedKeyframes = saved.SelectedKeyframes;
            timelineRangeStartText = saved.TimelineRangeStartText;
            timelineRangeEndText = saved.TimelineRangeEndText;
            timelineRangeValidationKey = null;
            actorDrafts.Clear();
            foreach (var pair in restored) actorDrafts.Add(pair.Key, pair.Value);
            contextRevision++;
            RebuildTracks(); RebuildSampler(); LoadPose();
            foreach (string name in new[] { nameof(CurrentFrame), nameof(MoveTargetFrame), nameof(TimelineTrackMode),
                nameof(IsTimelineFollowSelection), nameof(IsTimelineAllTracks), nameof(IsTimelineKeyedTracks), nameof(TimelineRangeStartText),
                nameof(TimelineRangeEndText), nameof(TimelineRangeValidationMessage), nameof(HasInvalidDrafts) }) Changed(name);
            NotifyContext(); NotifyHiddenSelection();
            PoseDraftChanged?.Invoke(this, EventArgs.Empty);
        };
    }
}
