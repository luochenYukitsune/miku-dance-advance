using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

public sealed partial class CharacterAnimationController
{
    private SharedPhysicsStatus? sharedPhysicsStatus;
    // Only an unpublished rigid boundary may own this bone-only clock. The final complete sample
    // replaces its stale skin before SceneAnimationSession publishes; failures retire that owner.
    private double? sharedRigidBoneFrame;
    internal bool HasUnpublishedRigidBonePose => sharedRigidBoneFrame.HasValue;
    private sealed record DeferredRigidPose(double Frame, bool Ik, bool Morphs, PreparedMorphs Prepared,
        Matrix4x4[] Worlds, IReadOnlyList<Matrix4x4> PublishedWorldView);
    private DeferredRigidPose? deferredRigidPose;
    internal bool HasDeferredRigidPose => deferredRigidPose is not null;
    internal double? DeferredRigidPoseFrame => deferredRigidPose?.Frame;

    // The scene must flush before changing controls/attachments or discard the entire
    // controller on seek/reset/fault. Discarding this record is not a physics replay.
    internal void DiscardDeferredRigidPose() => deferredRigidPose = null;

    internal (OriginalStyleScene Surface, OriginalOutlineData Outline) CompleteDeferredRigidPose(double expectedFrame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var deferred = deferredRigidPose ?? throw new InvalidOperationException("No deferred physical pose is available.");
        if (expectedFrame != deferred.Frame || sharedRigidBoneFrame != deferred.Frame || sampledFrame is not null
            || outsideParents is not null || !ReferenceEquals(BoneWorldMatrices, deferred.PublishedWorldView))
            throw new InvalidOperationException("The deferred physical pose no longer matches the current boundary.");
        // This does not evaluate animation, replay impulses, or touch the solver. Store owns
        // all normal morph, visibility, scene and cache publication, just as full completion.
        var sample = animator.SkinCompletedPhysicsWorlds(deferred.Worlds, deferred.Prepared.Pose);
        var result = Store(sample, deferred.Frame, deferred.Ik, deferred.Morphs, deferred.Prepared);
        deferredRigidPose = null;
        return result;
    }
    internal sealed class SharedPhysicsStatus(string backendName, IReadOnlyList<string> diagnostics)
    {
        public string BackendName { get; } = backendName + " (shared world)";
        public IReadOnlyList<string> Diagnostics { get; } = diagnostics;
        public int LastStepCount { get; set; }
        public double DiscardedSeconds { get; set; }
        public bool IsFaulted { get; set; }
    }
    internal void AttachSharedPhysics(SharedPhysicsStatus status)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ArgumentNullException.ThrowIfNull(status);
        if (physics is not null) throw new InvalidOperationException("Disable independent physics before joining a shared world.");
        sharedPhysicsStatus = status;
    }
    internal void DetachSharedPhysics() => sharedPhysicsStatus = null;

    // A stopped, unattached, nonphysical actor cannot change during another actor's gesture.
    // Keep only a fully completed pose: partial bone work and old attachment anchors must still
    // pass through the established boundary so a stale display cannot be mistaken for a full sample.
    internal bool CanRetainCompleteRigidInteractivePose(double frame, bool ik, bool morphs)
        => !disposed && physics is null && !SoftBodiesEnabled && model.SoftBodies.Count == 0
        && frame == DurationFrames && sampledFrame == frame && sampledIk == ik && sampledMorphs == morphs
        && sampledPose is not null && BoneWorldMatrices.Count == model.Bones.Count
        && sharedRigidBoneFrame is null && outsideParents is null && outsideParentTrackWindows is null
        && frozenBoneWorlds is null && frozenOutsideParents is null && frozenSoftVertices is null;

    // Called only after the ordered boundary refreshed this stopped attachment. A changed
    // parent invalidates sampledFrame/sampledPose; a bones-only completion cannot satisfy
    // this proof. Keep the existing complete local scene and its frozen anchor together.
    internal bool HasCompleteRigidFrozenAttachmentPose(double frame, bool ik, bool morphs,
        IReadOnlyDictionary<int, Matrix4x4> parents)
        => !disposed && !PhysicsEnabled && model.SoftBodies.Count == 0
        && frame == DurationFrames && sampledFrame == frame && sampledIk == ik && sampledMorphs == morphs
        && sampledPose is not null && sharedRigidBoneFrame is null && BoneWorldMatrices.Count == model.Bones.Count
        && parents.Count != 0 && OutsideParentsEqual(outsideParents, parents);

    /// <summary>PMM can change outside-parent slots on a stopped actor. Rebase its completed physical
    /// pose through old/new attachment frames, then keep that pose as the new frozen anchor.</summary>
    internal bool RefreshSharedFrozenOutsideParents(IReadOnlyDictionary<int, Matrix4x4> parents, bool deferSkinning = false)
    {
        var next = CopyOutsideParents(parents);
        var previous = outsideParents;
        if (deferSkinning)
        {
            if (SoftBodiesEnabled || (sampledFrame != DurationFrames && sharedRigidBoneFrame != DurationFrames)
                || BoneWorldMatrices.Count != model.Bones.Count)
                throw new InvalidOperationException("Deferred frozen attachment refresh requires a completed rigid final pose.");
            if (OutsideParentsEqual(previous, next)) return false;
            var anchorWorlds = frozenBoneWorlds ?? BoneWorldMatrices.ToArray();
            var anchorParents = frozenBoneWorlds is not null ? frozenOutsideParents : outsideParents;
            BoneWorldMatrices = Array.AsReadOnly(FrozenWorlds(next));
            outsideParents = next; frozenBoneWorlds = anchorWorlds; frozenOutsideParents = anchorParents;
            sharedRigidBoneFrame = DurationFrames; sampledFrame = null; sampledPose = null;
            return false;
        }
        if ((next?.Count ?? 0) == (previous?.Count ?? 0)
            && (next is null || next.Keys.All(key => previous!.ContainsKey(key))))
        {
            var before=sampled.Surface;
            if (next is not null) RefreshFrozenOutsideParents(next);
            return !ReferenceEquals(before,sampled.Surface);
        }
        if (sampledFrame != DurationFrames || BoneWorldMatrices.Count != model.Bones.Count)
            throw new InvalidOperationException("Changing frozen attachment slots needs a completed final pose.");
        RefreshFrozenPose(next);
        return true;
    }

    private HashSet<int>? afterPhysicsIkAffectedBones;
    private HashSet<int> AfterPhysicsIkAffectedBones => afterPhysicsIkAffectedBones ??= BuildAfterPhysicsIkAffectedBones();
    private HashSet<int> BuildAfterPhysicsIkAffectedBones()
    {
        var set = new HashSet<int>();
        for (int i = 0; i < model.Bones.Count; i++)
        {
            var bone = model.Bones[i];
            if (!bone.Flags.HasFlag(PmxBoneFlags.Ik) || !bone.Flags.HasFlag(PmxBoneFlags.DeformAfterPhysics)) continue;
            if ((uint)bone.IkTargetBoneIndex < (uint)model.Bones.Count) set.Add(bone.IkTargetBoneIndex);
            foreach (var link in bone.IkLinks)
                if ((uint)link.BoneIndex < (uint)model.Bones.Count) set.Add(link.BoneIndex);
        }
        return set;
    }

    internal bool IsBoneAffectedByAfterPhysics(int boneIndex)
    {
        if ((uint)boneIndex >= (uint)model.Bones.Count) return false;
        var visited = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(boneIndex);
        while (stack.Count > 0)
        {
            int current = stack.Pop();
            if (!visited.Add(current)) continue;
            var bone = model.Bones[current];
            if (bone.Flags.HasFlag(PmxBoneFlags.DeformAfterPhysics)) return true;
            if ((uint)bone.ParentIndex < (uint)model.Bones.Count) stack.Push(bone.ParentIndex);
            if ((bone.Flags & (PmxBoneFlags.AppendRotate | PmxBoneFlags.AppendTranslate)) != 0
                && (uint)bone.AppendBoneIndex < (uint)model.Bones.Count)
                stack.Push(bone.AppendBoneIndex);
        }
        return AfterPhysicsIkAffectedBones.Count > 0 && AfterPhysicsIkAffectedBones.Overlaps(visited);
    }

    internal Matrix4x4[] SampleResetPostAfterPhysicsBoneWorlds(double frame, bool enableIk, bool enableMorphs,
        IReadOnlySet<int>? disabledBoneIndices = null, IReadOnlyDictionary<int, BoneLocalTransform>? localBoneOverrides = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ValidateFrame(frame); frame = Math.Min(frame, DurationFrames);
        int[] driven = FilterDrivenBones(disabledBoneIndices);
        _ = PrepareMorphs(frame, enableMorphs);
        var options = Evaluation(frame, enableIk, enableMorphs) with
            { PhysicsDrivenBoneIndices = driven, LocalBoneOverrides = localBoneOverrides };
        return animator.SampleBoneWorldMatrices(frame, options);
    }

    private int[] FilterDrivenBones(IReadOnlySet<int>? disabledBoneIndices)
    {
        if (disabledBoneIndices is null || disabledBoneIndices.Count == 0) return defaultDrivenBoneIndices;
        foreach (int index in disabledBoneIndices)
            if ((uint)index >= (uint)model.Bones.Count)
                throw new ArgumentException("Invalid disabled physics bone.", nameof(disabledBoneIndices));
        var filtered = new List<int>(defaultDrivenBoneIndices.Length);
        for (int i = 0; i < defaultDrivenBoneIndices.Length; i++)
        {
            int bone = defaultDrivenBoneIndices[i];
            if (!disabledBoneIndices.Contains(bone)) filtered.Add(bone);
        }
        return filtered.Count == 0 ? [] : filtered.ToArray();
    }

    private static readonly IReadOnlySet<int> EmptyIntSet = new HashSet<int>();

    /// <summary>Prepares all morph channels and animation-before-physics before the world can mutate.</summary>
    internal SharedPreparedPhysicsSample BeginSharedPhysicsSample(double frame, bool enableIk, bool enableMorphs,
        IReadOnlySet<int>? disabledBoneIndices = null, bool frozen = false,
        IReadOnlyDictionary<int, BoneLocalTransform>? localBoneOverrides = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ValidateFrame(frame); frame = Math.Min(frame, DurationFrames);
        if (physics is not null) throw new InvalidOperationException("An independent solver is already attached.");
        disabledBoneIndices ??= EmptyIntSet;
        int[] driven = FilterDrivenBones(disabledBoneIndices);
        PreparedMorphs prepared;
        using (PoseEvaluationMetrics.Measure("actor.prepare.morphs"))
            prepared = PrepareMorphs(frame, enableMorphs);
        var options = Evaluation(frame, enableIk, enableMorphs) with
            { PhysicsDrivenBoneIndices = driven, LocalBoneOverrides = localBoneOverrides };
        bool canDeferPreview = !frozen && localBoneOverrides is null && outsideParents is null
            && frame < DurationFrames && model.RigidBodies.Count != 0 && model.SoftBodies.Count == 0 && !SoftBodiesEnabled;
        if (frozen)
        {
            if (BoneWorldMatrices.Count != model.Bones.Count) throw new InvalidOperationException("A frozen actor needs a previously completed pose.");
            return new(this, frame, enableIk, enableMorphs, prepared, null, BoneWorldMatrices.ToArray(), driven, options.Morphs, false);
        }
        CharacterPhysicsSample evaluation;
        using (PoseEvaluationMetrics.Measure("actor.prepare.bones"))
            evaluation = animator.BeginPhysicsSample(frame, options);
        try { return new(this, frame, enableIk, enableMorphs, prepared, evaluation, evaluation.BeforePhysicsBoneWorldMatrices.ToArray(), driven, options.Morphs, canDeferPreview); }
        catch { evaluation.Dispose(); throw; }
    }

    internal sealed class SharedPreparedPhysicsSample(CharacterAnimationController owner, double frame, bool ik, bool morphs,
        PreparedMorphs prepared, CharacterPhysicsSample? evaluation, Matrix4x4[] before, int[] driven, MorphPose? morphPose,
        bool canDeferPreview) : IDisposable
    {
        private bool completed;
        public Matrix4x4[] BeforePhysicsBoneWorlds { get; } = before;
        public SceneVertex[] BeforePhysicsVertices() => owner.animator.SkinWorldPose(BeforePhysicsBoneWorlds, morphPose).Vertices;
        public CharacterBodyImpulse[] Impulses { get; } = CreateBodyImpulses(prepared.Channels.Impulses);
        /// <summary>Unpublished rigid intermediate boundary. Retain the completed bone snapshot for
        /// outside parents while deferring the rigid skin, scene and outline until the final boundary.</summary>
        public void CompleteRigidBoneWorlds(Matrix4x4[] worlds, IReadOnlyDictionary<int, Matrix4x4>? afterPhysicsOutsideParents = null)
            => CompleteRigidBoneWorldsCore(worlds, deferPreview: false, afterPhysicsOutsideParents);

        // Only a non-frozen, unattached physical leaf may remain incomplete across preview
        // advances. The caller proves leaf status and scene-wide rigid-only status.
        public void CompleteRigidBoneWorldsAndDefer(Matrix4x4[] worlds, IReadOnlyDictionary<int, Matrix4x4>? afterPhysicsOutsideParents = null)
        {
            if (!canDeferPreview || evaluation is null)
                throw new InvalidOperationException("Deferred preview requires an active unattached rigid physical sample before its end.");
            CompleteRigidBoneWorldsCore(worlds, deferPreview: true, afterPhysicsOutsideParents);
        }

        private void CompleteRigidBoneWorldsCore(Matrix4x4[] worlds, bool deferPreview,
            IReadOnlyDictionary<int, Matrix4x4>? afterPhysicsOutsideParents = null)
        {
            if (completed) throw new InvalidOperationException("A shared animation sample can only be completed once.");
            if (owner.SoftBodiesEnabled)
                throw new InvalidOperationException("Deferred skinning requires a rigid-only sample.");
            completed = true;
            Dictionary<int, Matrix4x4>? nextParents = null;
            bool updatedParents = false;
            if (afterPhysicsOutsideParents is not null)
            {
                nextParents = owner.CopyOutsideParents(afterPhysicsOutsideParents);
                updatedParents = !OutsideParentsEqual(owner.outsideParents, nextParents);
            }
            if (evaluation is null && updatedParents)
            {
                var anchorWorlds = owner.frozenBoneWorlds ?? owner.BoneWorldMatrices.ToArray();
                var anchorParents = owner.frozenBoneWorlds is not null ? owner.frozenOutsideParents : owner.outsideParents;
                owner.BoneWorldMatrices = Array.AsReadOnly(owner.FrozenWorlds(nextParents));
                owner.outsideParents = nextParents; owner.frozenBoneWorlds = anchorWorlds; owner.frozenOutsideParents = anchorParents;
                owner.sampledFrame = null; owner.sampledPose = null; owner.sampledMorphsPreparation = null;
                owner.sharedRigidBoneFrame = frame;
                owner.deferredRigidPose = deferPreview
                    ? new(frame, ik, morphs, prepared, owner.BoneWorldMatrices.ToArray(), owner.BoneWorldMatrices) : null;
                return;
            }
            // Frozen actors retain their already completed pose, including attachment rebasing.
            // Re-running animation/IK would replace the immutable physical anchor with authored data.
            var finalWorlds = evaluation is null ? worlds.ToArray()
                : evaluation.CompleteBoneWorldMatrices(worlds, driven, updatedParents ? nextParents : null);
            owner.BoneWorldMatrices = Array.AsReadOnly(finalWorlds);
            if (updatedParents) owner.outsideParents = nextParents;
            // A partial completion has no matching skin cache. No caller may reuse the previous
            // sampled scene at this clock if the surrounding initialization fails or is interrupted.
            owner.sampledFrame = null; owner.sampledPose = null; owner.sampledMorphsPreparation = null;
            owner.sharedRigidBoneFrame = frame;
            owner.deferredRigidPose = deferPreview
                ? new(frame, ik, morphs, prepared, finalWorlds, owner.BoneWorldMatrices) : null;
            if (evaluation is not null)
            { owner.frozenBoneWorlds = null; owner.frozenOutsideParents = null; owner.frozenSoftVertices = null; }
        }
        public (OriginalStyleScene Surface, OriginalOutlineData Outline) Complete(Matrix4x4[] worlds,
            Matrix4x4[]? rigidWorlds = null, double? softElapsed = null,
            IReadOnlyDictionary<int, Matrix4x4>? afterPhysicsOutsideParents = null)
        {
            if (completed) throw new InvalidOperationException("A shared animation sample can only be completed once.");
            completed = true;
            Dictionary<int, Matrix4x4>? nextParents = null;
            bool updatedParents = false;
            if (afterPhysicsOutsideParents is not null)
            {
                nextParents = owner.CopyOutsideParents(afterPhysicsOutsideParents);
                updatedParents = !OutsideParentsEqual(owner.outsideParents, nextParents);
            }
            if (evaluation is null && updatedParents)
            {
                if (owner.SoftBodiesEnabled && nextParents is not null)
                {
                    var softPrevious = owner.sharedSoftRigidWorlds;
                    owner.sharedSoftRigidWorlds = rigidWorlds;
                    try
                    {
                        var softResult = owner.RefreshSharedFrozenOutsideParents(nextParents, deferSkinning: false)
                            ? owner.sampled
                            : owner.StoreVertices(
                                owner.animator.SkinWorldPose(owner.BoneWorldMatrices.ToArray(), morphPose),
                                frame, ik, morphs, prepared,
                                owner.RetainFrozenSoftVertices(owner.animator.SkinWorldPose(owner.BoneWorldMatrices.ToArray(), morphPose)));
                        owner.sharedRigidBoneFrame = null; owner.deferredRigidPose = null;
                        return softResult;
                    }
                    finally { owner.sharedSoftRigidWorlds = softPrevious; }
                }
                if (nextParents is not null)
                {
                    var anchorWorlds = owner.frozenBoneWorlds ?? owner.BoneWorldMatrices.ToArray();
                    var anchorParents = owner.frozenBoneWorlds is not null ? owner.frozenOutsideParents : owner.outsideParents;
                    var frozenSample = owner.animator.SkinWorldPose(owner.FrozenWorlds(nextParents), morphPose);
                    var frozenResult = owner.Store(frozenSample, frame, ik, morphs, prepared, softElapsed);
                    owner.outsideParents = nextParents; owner.frozenBoneWorlds = anchorWorlds; owner.frozenOutsideParents = anchorParents;
                    owner.sharedRigidBoneFrame = null; owner.deferredRigidPose = null;
                    return frozenResult;
                }
            }
            var sample = evaluation is null ? owner.animator.SkinWorldPose(worlds, morphPose)
                : evaluation.Complete(worlds, driven, updatedParents ? nextParents : null);
            var savedAnchorWorlds = owner.frozenBoneWorlds; var savedAnchorParents = owner.frozenOutsideParents;
            var savedAnchorVertices = owner.frozenSoftVertices;
            var previousRigids = owner.sharedSoftRigidWorlds;
            owner.sharedSoftRigidWorlds = rigidWorlds;
            try
            {
                var result = evaluation is null && owner.softBodies is not null
                    ? owner.StoreVertices(sample, frame, ik, morphs, prepared, owner.RetainFrozenSoftVertices(sample))
                    : owner.Store(sample, frame, ik, morphs, prepared, softElapsed);
                if (evaluation is null)
                { owner.frozenBoneWorlds = savedAnchorWorlds; owner.frozenOutsideParents = savedAnchorParents; owner.frozenSoftVertices = savedAnchorVertices; }
                else if (updatedParents) owner.outsideParents = nextParents;
                owner.sharedRigidBoneFrame = null;
                owner.deferredRigidPose = null;
                return result;
            }
            finally { owner.sharedSoftRigidWorlds = previousRigids; }
        }
        public void Dispose() => evaluation?.Dispose();
    }
}
