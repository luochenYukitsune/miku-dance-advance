using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
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
        && sharedRigidBoneFrame is null && outsideParents is null
        && frozenBoneWorlds is null && frozenOutsideParents is null && frozenSoftVertices is null;

    // Called only after the ordered boundary refreshed this stopped attachment. A changed
    // parent invalidates sampledFrame/sampledPose; a bones-only completion cannot satisfy
    // this proof. Keep the existing complete local scene and its frozen anchor together.
    internal bool HasCompleteRigidFrozenAttachmentPose(double frame, bool ik, bool morphs,
        IReadOnlyDictionary<int, Matrix4x4> parents)
        => !disposed && !PhysicsEnabled && model.SoftBodies.Count == 0
        && frame == DurationFrames && sampledFrame == frame && sampledIk == ik && sampledMorphs == morphs
        && sampledPose is not null && sharedRigidBoneFrame is null && BoneWorldMatrices.Count == model.Bones.Count
        && parents.Count != 0 && outsideParents is not null && parents.Count == outsideParents.Count
        && parents.All(pair => outsideParents.TryGetValue(pair.Key, out var previous) && previous == pair.Value);

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
            if ((next?.Count ?? 0) == (previous?.Count ?? 0)
                && (next is null || next.All(pair => previous!.TryGetValue(pair.Key, out var value) && value == pair.Value))) return false;
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

    /// <summary>Prepares all morph channels and animation-before-physics before the world can mutate.</summary>
    internal SharedPreparedPhysicsSample BeginSharedPhysicsSample(double frame, bool enableIk, bool enableMorphs,
        IReadOnlySet<int>? disabledBoneIndices = null, bool frozen = false,
        IReadOnlyDictionary<int, BoneLocalTransform>? localBoneOverrides = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ValidateFrame(frame); frame = Math.Min(frame, DurationFrames);
        if (physics is not null) throw new InvalidOperationException("An independent solver is already attached.");
        disabledBoneIndices ??= new HashSet<int>();
        if (disabledBoneIndices.Any(index => (uint)index >= (uint)model.Bones.Count)) throw new ArgumentException("Invalid disabled physics bone.", nameof(disabledBoneIndices));
        var prepared = PrepareMorphs(frame, enableMorphs);
        var driven = model.RigidBodies.Where(body => body.BoneIndex >= 0 && body.Operation != Formats.PmxRigidBodyOperation.Static
                && !disabledBoneIndices.Contains(body.BoneIndex)).Select(body => body.BoneIndex).Distinct().ToArray();
        var options = Evaluation(frame, enableIk, enableMorphs) with
            { PhysicsDrivenBoneIndices = driven, LocalBoneOverrides = localBoneOverrides };
        bool canDeferPreview = !frozen && localBoneOverrides is null && outsideParents is null
            && frame < DurationFrames && model.RigidBodies.Count != 0 && model.SoftBodies.Count == 0 && !SoftBodiesEnabled;
        if (frozen)
        {
            if (BoneWorldMatrices.Count != model.Bones.Count) throw new InvalidOperationException("A frozen actor needs a previously completed pose.");
            return new(this, frame, enableIk, enableMorphs, prepared, null, BoneWorldMatrices.ToArray(), driven, options.Morphs, false);
        }
        var evaluation = animator.BeginPhysicsSample(frame, options);
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
        public CharacterBodyImpulse[] Impulses { get; } = prepared.Channels.Impulses.Select(value =>
            new CharacterBodyImpulse(value.RigidBodyIndex, value.Local, value.TranslationVelocity, value.RotationTorque)).ToArray();
        /// <summary>Unpublished rigid intermediate boundary. Retain the completed bone snapshot for
        /// outside parents while deferring the rigid skin, scene and outline until the final boundary.</summary>
        public void CompleteRigidBoneWorlds(Matrix4x4[] worlds)
            => CompleteRigidBoneWorldsCore(worlds, deferPreview: false);

        // Only a non-frozen, unattached physical leaf may remain incomplete across preview
        // advances. The caller proves leaf status and scene-wide rigid-only status.
        public void CompleteRigidBoneWorldsAndDefer(Matrix4x4[] worlds)
        {
            if (!canDeferPreview || evaluation is null)
                throw new InvalidOperationException("Deferred preview requires an active unattached rigid physical sample before its end.");
            CompleteRigidBoneWorldsCore(worlds, deferPreview: true);
        }

        private void CompleteRigidBoneWorldsCore(Matrix4x4[] worlds, bool deferPreview)
        {
            if (completed) throw new InvalidOperationException("A shared animation sample can only be completed once.");
            if (owner.SoftBodiesEnabled)
                throw new InvalidOperationException("Deferred skinning requires a rigid-only sample.");
            completed = true;
            // Frozen actors retain their already completed pose, including attachment rebasing.
            // Re-running animation/IK would replace the immutable physical anchor with authored data.
            var finalWorlds = evaluation is null ? worlds.ToArray() : evaluation.CompleteBoneWorldMatrices(worlds, driven);
            owner.BoneWorldMatrices = Array.AsReadOnly(finalWorlds);
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
            Matrix4x4[]? rigidWorlds = null, double? softElapsed = null)
        {
            if (completed) throw new InvalidOperationException("A shared animation sample can only be completed once.");
            completed = true;
            var sample = evaluation is null ? owner.animator.SkinWorldPose(worlds, morphPose) : evaluation.Complete(worlds, driven);
            var anchorWorlds = owner.frozenBoneWorlds; var anchorParents = owner.frozenOutsideParents;
            var anchorVertices = owner.frozenSoftVertices;
            var previousRigids = owner.sharedSoftRigidWorlds;
            owner.sharedSoftRigidWorlds = rigidWorlds;
            try
            {
                var result = evaluation is null && owner.softBodies is not null
                    ? owner.StoreVertices(sample, frame, ik, morphs, prepared, owner.RetainFrozenSoftVertices(sample))
                    : owner.Store(sample, frame, ik, morphs, prepared, softElapsed);
                if (evaluation is null)
                { owner.frozenBoneWorlds = anchorWorlds; owner.frozenOutsideParents = anchorParents; owner.frozenSoftVertices = anchorVertices; }
                owner.sharedRigidBoneFrame = null;
                owner.deferredRigidPose = null;
                return result;
            }
            finally { owner.sharedSoftRigidWorlds = previousRigids; }
        }
        public void Dispose() => evaluation?.Dispose();
    }
}
