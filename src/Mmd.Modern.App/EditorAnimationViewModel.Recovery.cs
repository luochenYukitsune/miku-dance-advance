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
        foreach (var (id, pending) in actorDrafts)
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
            Actors = actors.OrderBy(actor => actor.ObjectId).Select(actor =>
            {
                actorDrafts.TryGetValue(actor.ObjectId, out var pending);
                return new EditorRecoveryActorState
                {
                    ObjectId = actor.ObjectId,
                    StructureSignature = EditorRecoverySessionState.ModelStructureSignature(actor.Model),
                    BoneCount = actor.Model.Bones.Count,
                    MorphCount = actor.Model.Morphs.Count,
                    Bones = pending is null ? [] : pending.Bones.OrderBy(pair => pair.Key).Select(pair =>
                        new EditorRecoveryBoneDraft(pair.Key, pair.Value.X, pair.Value.Y, pair.Value.Z,
                            pair.Value.Pitch, pair.Value.Yaw, pair.Value.Roll,
                            pair.Value.SampledRotation, pair.Value.RotationEdited)).ToImmutableArray(),
                    Morphs = pending is null ? [] : pending.Morphs.OrderBy(pair => pair.Key)
                        .Select(pair => new EditorRecoveryMorphDraft(pair.Key, pair.Value)).ToImmutableArray()
                };
            }).ToImmutableArray()
        };
    }

    internal Action PrepareRecoveryRestore(EditorRecoveryAnimationState saved, IReadOnlyList<SceneActorMotion> actors)
    {
        var available = actors.ToDictionary(actor => actor.ObjectId);
        var restored = new Dictionary<Guid, ActorDrafts>();
        foreach (var snapshot in saved.Actors)
        {
            var actor = available[snapshot.ObjectId];
            var pending = new ActorDrafts(actor.Model, actor.Motion);
            foreach (var bone in snapshot.Bones)
                pending.Bones.Add(bone.Index, new(bone.X, bone.Y, bone.Z, bone.Pitch, bone.Yaw, bone.Roll,
                    bone.SampledRotation, bone.RotationEdited));
            foreach (var morph in snapshot.Morphs) pending.Morphs.Add(morph.Index, morph.Weight);
            if (!pending.IsEmpty) restored.Add(actor.ObjectId, pending);
        }
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
                nameof(IsTimelineFollowSelection), nameof(IsTimelineAllTracks), nameof(TimelineRangeStartText),
                nameof(TimelineRangeEndText), nameof(TimelineRangeValidationMessage), nameof(HasInvalidDrafts) }) Changed(name);
            NotifyContext(); NotifyHiddenSelection();
            PoseDraftChanged?.Invoke(this, EventArgs.Empty);
        };
    }
}
