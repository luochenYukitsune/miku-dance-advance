// Phase ordering also references babylon-mmd (MIT), mmdRuntime.ts beforePhysics/afterPhysics,
// commit ccb750db2998ed1067017eaf67f3fc987cfba6f8. The fixed clock and frozen-actor contract are project-specific.
using System.Numerics;
using System.Runtime.ExceptionServices;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

public sealed partial class SceneAnimationSession
{
    private readonly record struct BoundaryClock(double Frame, double Prior, bool Reset, bool Step)
    {
        public double? SoftElapsed(bool frozen) => Reset ? null : Step && !frozen ? CharacterPhysicsSession.FixedStepSeconds : 0;
    }

    private readonly record struct PendingPhysicsPose(int Index, CharacterAnimationController.SharedPreparedPhysicsSample Pose,
        SharedPhysicsActorSample Physics, bool DeferPreview);

    private sealed class BoundaryPoses : IDisposable
    {
        public readonly List<PendingPhysicsPose> Physical = [];
        public readonly int[] PhysicalSlots;
        public List<(CharacterAnimationController.SharedPreparedPhysicsSample Pose, bool Frozen)>? SoftOnly { get; private set; }
        public int[]? SoftSlots { get; private set; }
        public readonly IReadOnlyList<Matrix4x4>?[] PreparedWorlds;
        public readonly IReadOnlySet<int>?[] DisabledBones;
        public readonly Matrix4x4[]?[]? ResetParentWorlds;
        public readonly Dictionary<int, Matrix4x4>?[] BeforeParents;
        public readonly bool[] HasBeforeParents;
        public readonly NonPhysicalAttachedState?[]? NonPhysicalAttachments;

        public BoundaryPoses(int count, bool reset, bool hasParents)
        {
            PhysicalSlots = EmptySlots(count);
            PreparedWorlds = new IReadOnlyList<Matrix4x4>?[count];
            DisabledBones = new IReadOnlySet<int>?[count];
            ResetParentWorlds = reset ? new Matrix4x4[]?[count] : null;
            BeforeParents = new Dictionary<int, Matrix4x4>?[count];
            HasBeforeParents = new bool[count];
            NonPhysicalAttachments = hasParents ? new NonPhysicalAttachedState?[count] : null;
        }

        private static int[] EmptySlots(int count)
        {
            var slots = new int[count];
            Array.Fill(slots, -1);
            return slots;
        }

        public void AddPhysical(int index, Guid objectId, CharacterAnimationController.SharedPreparedPhysicsSample pose,
            IReadOnlySet<int> disabled, bool frozen, bool applyImpulses, bool deferPreview)
        {
            var sample = new SharedPhysicsActorSample(objectId, pose.BeforePhysicsBoneWorlds)
            {
                Frozen = frozen, Impulses = applyImpulses ? pose.Impulses : [], DisabledBoneIndices = disabled,
            };
            PhysicalSlots[index] = Physical.Count;
            DisabledBones[index] = disabled;
            PreparedWorlds[index] = pose.BeforePhysicsBoneWorlds;
            Physical.Add(new(index, pose, sample, deferPreview));
        }

        public void AddSoft(int index, CharacterAnimationController.SharedPreparedPhysicsSample pose, bool frozen)
        {
            SoftOnly ??= [];
            SoftSlots ??= EmptySlots(PhysicalSlots.Length);
            SoftSlots[index] = SoftOnly.Count;
            PreparedWorlds[index] = pose.BeforePhysicsBoneWorlds;
            SoftOnly.Add((pose, frozen));
        }

        public void Dispose()
        {
            foreach (var item in Physical) item.Pose.Dispose();
            if (SoftOnly is not null)
                foreach (var item in SoftOnly) item.Pose.Dispose();
        }
    }

    private void PrepareSharedBoundarySerial(Entry[] source, SharedCharacterPhysicsSession? world,
        SharedBoundaryTopology topology, SharedBoundaryGraph? boundaryGraph, bool[]? physicalAtBoundary,
        HashSet<Guid>? requiredParents, BoundaryClock clock, bool initializing,
        bool deferRigidSkinning, BoundaryPoses poses, CancellationToken token, Guid? interactiveObjectId,
        IReadOnlyDictionary<int, BoneLocalTransform>? localBoneOverrides, IReadOnlySet<Guid>? activeActors)
    {
        var (frame, prior, reset, _) = clock;
        foreach (int index in topology.TopologicalOrder)
        {
            token.ThrowIfCancellationRequested(); var entry = source[index]; var controller = entry.Controller;
            if (controller is null) continue;
            if (activeActors is not null && !activeActors.Contains(entry.Object.Id)) continue;
            var motion = entry.Motion!; double local = Math.Min(frame, controller.DurationFrames);
            bool edited = interactiveObjectId == entry.Object.Id;
            bool frozen = !initializing && !reset && !edited && prior >= controller.DurationFrames;
            var overrides = edited ? localBoneOverrides : null;
            var bindings = topology.BindingsByIndex[index];
            var parentIndices = topology.ParentActorIndicesByBinding[index];
            var groundParents = boundaryGraph is not null ? boundaryGraph.GroundByIndex[index] : ControlGroundParents(entry.Object.Id, frame);
            var trackWindows = boundaryGraph is not null ? boundaryGraph.TrackWindowsByIndex[index] : ControlTrackWindows(entry.Object.Id, frame);
            bool hasBindings = bindings.Length != 0;
            bool attached = hasBindings || groundParents.Length != 0;
            bool trackWindowChanged = pmmControls is not null && controller.SetOutsideParentTrackWindows(trackWindows);
            bool physical = physicalAtBoundary?[index] ?? (ActorWorld(entry, world)?.ContainsActor(entry.Object.Id) == true);
            if (!physical && deferRigidSkinning && requiredParents is not null && frozen && !initializing && !reset
                && !interactiveObjectId.HasValue && !requiredParents.Contains(entry.Object.Id)
                && controller.CanDeferFrozenAttachmentBoundary(bindings, groundParents, trackWindowChanged))
            {
                continue;
            }
            if (interactiveObjectId.HasValue && frozen && !attached && !physical && !trackWindowChanged
                && controller.CanRetainCompleteRigidInteractivePose(local, motion.IkEnabled, motion.MorphsEnabled))
            {
                // Retain the immutable local scene of stopped rooms/props. Descendants still
                // receive its completed bones in dependency order, and every physical world
                // keeps its normal step. Editing this actor or attaching it takes the full path.
                poses.PreparedWorlds[index] = controller.BoneWorldMatrices;
                continue;
            }
            Dictionary<int, Matrix4x4>? parents = attached
                ? BuildBoundaryOutsideParents(source, entry, bindings, parentIndices, groundParents,
                    (parentIdx, parentBone) =>
                    {
                        var parentDisabled = poses.DisabledBones[parentIdx];
                        Matrix4x4[]? resetWorlds = null;
                        if (reset && parentDisabled is not null
                            && source[parentIdx].Controller!.IsBoneAffectedByAfterPhysics(parentBone))
                            resetWorlds = EnsureResetAfterPhysicsParentWorlds(source, parentIdx,
                                poses.DisabledBones, poses.ResetParentWorlds!, frame,
                                interactiveObjectId, localBoneOverrides);
                        return ResolvePhase1ParentBoneWorlds(source[parentIdx], parentBone,
                            poses.PreparedWorlds[parentIdx], parentDisabled, resetWorlds);
                    })
                : null;
            if (attached || pmmControls is not null)
            {
                poses.BeforeParents[index] = parents;
                poses.HasBeforeParents[index] = true;
            }
            // A frozen attachment can move during a fixed-clock gesture. Its immutable anchor
            // must rebase on every boundary, but the matching skin belongs to completion below.
            bool deferFrozenSkin = interactiveObjectId.HasValue || deferRigidSkinning || controller.HasUnpublishedRigidBonePose;
            var frozenFrame = ApplyPhase1OutsideParents(entry, local, frozen, attached, physical, deferFrozenSkin,
                trackWindowChanged, parents, out bool fullEvalFrozen, out bool sampleFrozen, out bool publishedFrozenDisplay);
            if (frozenFrame is not null)
                source[index] = entry = entry with { LocalFrame = frozenFrame };
            if (interactiveObjectId.HasValue && sampleFrozen && attached && !physical && !edited && !trackWindowChanged
                && controller.HasCompleteRigidFrozenAttachmentPose(local, motion.IkEnabled, motion.MorphsEnabled, parents!))
            {
                // Refresh above proved that the current parent matrices still match a
                // complete rigid display. Retain its scene, but keep supplying its solved
                // bones to descendants in the same dependency order and step every world.
                if (hasBindings) poses.NonPhysicalAttachments![index] = new(local, sampleFrozen, deferFrozenSkin, overrides);
                poses.PreparedWorlds[index] = controller.BoneWorldMatrices;
                continue;
            }
            if (!physical)
            {
                if (CanDeferVisualLeaf(entry, requiredParents, frame, frozen, attached, reset))
                {
                    // No solver, soft state, attachment or consumer can observe these
                    // intermediate bones. The final displayed boundary evaluates them
                    // normally. Never skip crossing an actor's end: later frozen parent
                    // rebasing requires a successfully completed tail, including PMM
                    // slots first activated after that local animation has ended.
                    if (previewWorking is not null) previewWorking[entry.Object.Id] = local;
                    continue;
                }
                if (!coupledSoftBodies && !initializing && !reset && !interactiveObjectId.HasValue
                    && !trackWindowChanged && !fullEvalFrozen
                    && controller.TryRetainCompleteRigidPlaybackPose(local, motion.IkEnabled, motion.MorphsEnabled))
                {
                    // Every control/parent boundary above still ran. A complete constant
                    // local result also supplies the exact stable bones to descendants;
                    // retain both products while advancing the controller's cache clock.
                    if (hasBindings) poses.NonPhysicalAttachments![index] = new(local, sampleFrozen, deferFrozenSkin, overrides);
                    if (!deferRigidSkinning) previewWorking?.Remove(entry.Object.Id);
                    poses.PreparedWorlds[index] = controller.BoneWorldMatrices;
                    continue;
                }
                if (controller.SoftBodiesEnabled && hasBindings && !interactiveObjectId.HasValue)
                {
                    var softPose = controller.BeginSharedPhysicsSample(local, motion.IkEnabled, motion.MorphsEnabled, frozen: sampleFrozen);
                    try
                    {
                        poses.AddSoft(index, softPose, frozen);
                    }
                    catch { softPose.Dispose(); throw; }
                    continue;
                }
                if (interactiveObjectId.HasValue)
                {
                    // The warm interactive route excludes soft bodies and keeps authored animation
                    // at one clock. A nonphysical PMM parent still needs its raw local draft here.
                    CharacterAnimationController.SharedPreparedPhysicsSample pose;
                    using (PoseEvaluationMetrics.Measure("boundary.nonphysical.interactive-prepare", entry.Object.Id))
                        pose = controller.BeginSharedPhysicsSample(local, motion.IkEnabled, motion.MorphsEnabled,
                            frozen: sampleFrozen, localBoneOverrides: overrides);
                    using (pose)
                    {
                        if (deferRigidSkinning)
                        {
                            using var completionMeasure = PoseEvaluationMetrics.Measure("boundary.nonphysical.interactive-complete-bones", entry.Object.Id);
                            pose.CompleteRigidBoneWorlds(pose.BeforePhysicsBoneWorlds);
                        }
                        else
                        {
                            using var completionMeasure = PoseEvaluationMetrics.Measure("boundary.nonphysical.interactive-complete-full", entry.Object.Id);
                            source[index] = entry with { LocalFrame = SharedLocalFrame(entry, pose.Complete(pose.BeforePhysicsBoneWorlds)) };
                        }
                    }
                }
                else if (controller.SoftBodiesEnabled)
                {
                    using var softPose = controller.BeginSharedPhysicsSample(local, motion.IkEnabled, motion.MorphsEnabled, frozen: sampleFrozen);
                    source[index] = entry with { LocalFrame = SharedLocalFrame(entry,
                        softPose.Complete(softPose.BeforePhysicsBoneWorlds, [], clock.SoftElapsed(frozen))) };
                }
                else if (SampleNonPhysicalRigidBoundary(entry, local, sampleFrozen, reset, attached, publishedFrozenDisplay, deferRigidSkinning) is { } rigidFrame)
                    source[index] = entry with { LocalFrame = rigidFrame };
                // Controllers replace their read-only palette on completion; they never mutate
                // an earlier palette. Keep that stable snapshot without copying every parent.
                if (hasBindings) poses.NonPhysicalAttachments![index] = new(local, sampleFrozen, deferFrozenSkin, overrides);
                if (!deferRigidSkinning) previewWorking?.Remove(entry.Object.Id);
                poses.PreparedWorlds[index] = controller.BoneWorldMatrices;
                continue;
            }
            var disabled = boundaryGraph?.DisabledByIndex[index] ?? (pmmControls is null ? EmptyDisabledBones : ControlDisabled(entry.Object.Id, frame));
            CharacterAnimationController.SharedPreparedPhysicsSample preparation;
            using (PoseEvaluationMetrics.Measure("boundary.physical.prepare", entry.Object.Id))
                preparation = controller.BeginSharedPhysicsSample(local, motion.IkEnabled, motion.MorphsEnabled, disabled, sampleFrozen, overrides);
            try
            {
                // Only this actor's terminal skin is delayed. The existing middle-boundary
                // flag also affects nonphysical parents, so it must not be widened here.
                // Children consume its before-physics bones above; rigid worlds do not read its skin.
                bool deferPreview = previewWorking is not null && !deferRigidSkinning && !initializing && !reset
                    && !interactiveObjectId.HasValue && !frozen && !attached && local < controller.DurationFrames
                    && requiredParents is not null
                    && AppContext.TryGetSwitch(PhysicalPreviewSwitch, out bool enabled) && enabled;
                poses.AddPhysical(index, entry.Object.Id, preparation, disabled, frozen, !initializing && !interactiveObjectId.HasValue, deferPreview);
            }
            catch { preparation.Dispose(); throw; }
        }
    }

    private static bool CanDeferVisualLeaf(Entry entry, HashSet<Guid>? requiredParents,
        double frame, bool frozen, bool attached, bool reset)
        => requiredParents is not null && !frozen && !attached && !reset
            && frame < entry.Controller!.DurationFrames && entry.Motion!.Model.SoftBodies.Count == 0
            && !requiredParents.Contains(entry.Object.Id);

    private static OriginalFrameScene? SampleNonPhysicalRigidBoundary(Entry entry, double local, bool frozen,
        bool reset, bool attached, bool publishedFrozenDisplay, bool deferRigidSkinning)
    {
        var controller = entry.Controller!;
        var motion = entry.Motion!;
        if (frozen && controller.HasUnpublishedRigidBonePose)
        {
            // Preserve the rebased bones if a deferred middle boundary reached the actor's end.
            if (deferRigidSkinning) return null;
            using var measure = PoseEvaluationMetrics.Measure("boundary.nonphysical.frozen-complete-full", entry.Object.Id);
            using var pose = controller.BeginSharedPhysicsSample(local, motion.IkEnabled, motion.MorphsEnabled, frozen: true);
            return SharedLocalFrame(entry, pose.Complete(pose.BeforePhysicsBoneWorlds));
        }
        if (publishedFrozenDisplay || frozen && !reset && !attached) return null;
        if (deferRigidSkinning)
        {
            using var measure = PoseEvaluationMetrics.Measure("boundary.nonphysical.bones-only", entry.Object.Id);
            controller.SampleRigidInitializationBoneWorlds(local, motion.IkEnabled, motion.MorphsEnabled);
            return null;
        }
        using var sample = PoseEvaluationMetrics.Measure("boundary.nonphysical.full-sample", entry.Object.Id);
        return SharedLocalFrame(entry, controller.SampleAt(local, motion.IkEnabled, motion.MorphsEnabled));
    }

    private Matrix4x4[]?[] SolveBoundaryWorlds(Entry[] source, SharedCharacterPhysicsSession? world,
        List<PendingPhysicsPose> pending, BoundaryClock clock, bool canParallelBoundary, int maxDegree)
    {
        var (frame, prior, reset, step) = clock;
        var resultsByEntryIndex = new Matrix4x4[]?[source.Length];
        if (pending.Count == 0) return resultsByEntryIndex;
        if (controlledWorlds.Count == 0)
        {
            var batchWorld = ActorWorld(source[pending[0].Index], world)!;
            var samples = new SharedPhysicsActorSample[pending.Count];
            Vector3[]?[]? targets = coupledSoftBodies ? new Vector3[]?[pending.Count] : null;
            for (int p = 0; p < pending.Count; p++)
            {
                samples[p] = pending[p].Physics;
                if (targets is not null)
                    targets[p] = source[pending[p].Index].Controller!.PrepareCoupledTargets(pending[p].Pose);
            }
            if (reset) batchWorld.ResetAll(samples);
            if (targets is not null)
            {
                for (int p = 0; p < pending.Count; p++)
                {
                    var item = pending[p];
                    var entry = source[item.Index];
                    if (entry.Controller!.SupportsSoftBodies && reset)
                        entry.Controller.ResetSharedCoupledSoftBodies(batchWorld, entry.Object.Id, entry.Object.World, item.Pose);
                    var pins = reset ? entry.Controller.PrepareCoupledTargets(item.Pose) : targets[p];
                    entry.Controller.PublishCoupledTargets(pins, item.Physics.Frozen, step || reset);
                }
            }
            if (currentControlGravity is { } gravity) batchWorld.SetGravity(gravity);
            try
            {
                using (PoseEvaluationMetrics.Measure(step ? "boundary.world.step" : "boundary.world.reflect"))
                {
                    var stepped = step ? batchWorld.Step(samples) : batchWorld.Reflect(samples);
                    for (int p = 0; p < pending.Count; p++)
                    {
                        int entryIndex = pending[p].Index;
                        resultsByEntryIndex[entryIndex] = stepped[source[entryIndex].Object.Id];
                    }
                }
            }
            catch (Exception error)
            {
                EnrichPhysicsStepException(error, source, -1, pending, frame, prior);
                throw;
            }
        }
        else if (canParallelBoundary && pending.Count >= 2)
        {
            RunBoundaryWorkers(pending.Count, maxDegree, p =>
            {
                var item = pending[p];
                var entry = source[item.Index];
                var actorWorld = controlledWorlds[entry.Object.Id];
                var samples = new[] { item.Physics };
                try
                {
                    if (reset) actorWorld.ResetAll(samples);
                    if (currentControlGravity is { } gravity) actorWorld.SetGravity(gravity);
                    var stepped = step ? actorWorld.Step(samples) : actorWorld.Reflect(samples);
                    resultsByEntryIndex[item.Index] = stepped[entry.Object.Id];
                }
                catch (Exception error)
                {
                    EnrichPhysicsStepException(error, source, item.Index, null, frame, prior);
                    throw;
                }
            });
        }
        else
        {
            for (int p = 0; p < pending.Count; p++)
            {
                var item = pending[p];
                var entry = source[item.Index];
                var actorWorld = ActorWorld(entry, world)!;
                var samples = new[] { item.Physics };
                var targets = coupledSoftBodies ? entry.Controller!.PrepareCoupledTargets(item.Pose) : null;
                if (reset) actorWorld.ResetAll(samples);
                if (coupledSoftBodies && entry.Controller!.SupportsSoftBodies)
                {
                    if (reset)
                        entry.Controller.ResetSharedCoupledSoftBodies(actorWorld, entry.Object.Id, entry.Object.World, item.Pose);
                    var pins = reset ? entry.Controller.PrepareCoupledTargets(item.Pose) : targets;
                    entry.Controller.PublishCoupledTargets(pins, item.Physics.Frozen, step || reset);
                }
                if (currentControlGravity is { } gravity) actorWorld.SetGravity(gravity);
                try
                {
                    using (PoseEvaluationMetrics.Measure(step ? "boundary.world.step" : "boundary.world.reflect"))
                    {
                        var stepped = step ? actorWorld.Step(samples) : actorWorld.Reflect(samples);
                        resultsByEntryIndex[item.Index] = stepped[entry.Object.Id];
                    }
                }
                catch (Exception error)
                {
                    EnrichPhysicsStepException(error, source, item.Index, null, frame, prior);
                    throw;
                }
            }
        }
        return resultsByEntryIndex;
    }

    private void CompleteUnattachedPoses(Entry[] source, SharedCharacterPhysicsSession? world,
        List<PendingPhysicsPose> pending, Matrix4x4[]?[] resultsByEntryIndex, BoundaryClock clock,
        bool deferRigidSkinning, bool canParallelBoundary, int maxDegree)
    {
        OriginalFrameScene? Complete(int slot)
        {
            var item = pending[slot];
            return CompletePendingSample(source[item.Index], world, item, resultsByEntryIndex[item.Index]!, clock, deferRigidSkinning);
        }

        if (canParallelBoundary && pending.Count >= 2)
        {
            var frames = new OriginalFrameScene?[pending.Count];
            RunBoundaryWorkers(pending.Count, maxDegree, p => frames[p] = Complete(p),
                p => CommitPendingSampleCompletion(source, pending[p], frames[p], clock.Frame, deferRigidSkinning));
        }
        else
        {
            for (int p = 0; p < pending.Count; p++)
                CommitPendingSampleCompletion(source, pending[p], Complete(p), clock.Frame, deferRigidSkinning);
        }
    }

    private static void RunBoundaryWorkers(int count, int maxDegree, Action<int> work, Action<int>? commit = null)
    {
        if (count == 1)
        {
            work(0);
            commit?.Invoke(0);
            return;
        }

        var errors = new ExceptionDispatchInfo?[count];
        Parallel.For(0, count, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(maxDegree, count),
            TaskScheduler = TaskScheduler.Default,
        }, index =>
        {
            try { work(index); }
            catch (Exception error) { errors[index] = ExceptionDispatchInfo.Capture(error); }
        });
        // Later workers may finish first. Publish frames and rethrow failures in actor order.
        for (int index = 0; index < count; index++)
        {
            errors[index]?.Throw();
            commit?.Invoke(index);
        }
    }

    private void CompleteAttachedPosesSerial(Entry[] source, SharedCharacterPhysicsSession? world,
        SharedBoundaryTopology topology, SharedBoundaryGraph? boundaryGraph, BoundaryClock clock, bool deferRigidSkinning,
        BoundaryPoses poses, Matrix4x4[]?[] resultsByEntryIndex, Guid? interactiveObjectId)
    {
        var updatedInPhase3 = new bool[source.Length];
        foreach (int index in topology.TopologicalOrder)
        {
            var entry = source[index];
            if (entry.Controller is null) continue;
            int p = poses.PhysicalSlots[index];
            int s = poses.SoftSlots is null ? -1 : poses.SoftSlots[index];
            bool hasNonPhysical = poses.NonPhysicalAttachments is not null && poses.NonPhysicalAttachments[index].HasValue;
            if (p < 0 && s < 0 && !hasNonPhysical) continue;
            var afterParents = HasUpdatedParent(source, topology, poses, updatedInPhase3, index)
                ? ResolveUpdatedParents(source, topology, boundaryGraph, poses, index, clock.Frame) : null;
            if (p >= 0)
            {
                var item = poses.Physical[p];
                var boneWorlds = resultsByEntryIndex[index]!;
                var completedFrame = CompletePendingSample(entry, world, item, boneWorlds, clock, deferRigidSkinning, afterParents);
                CommitPendingSampleCompletion(source, item, completedFrame, clock.Frame, deferRigidSkinning);
            }
            else if (s >= 0)
            {
                var softItem = poses.SoftOnly![s];
                source[index] = entry with { LocalFrame = SharedLocalFrame(entry,
                    softItem.Pose.Complete(softItem.Pose.BeforePhysicsBoneWorlds, [], clock.SoftElapsed(softItem.Frozen), afterParents)) };
            }
            else if (afterParents is not null && hasNonPhysical)
            {
                var info = poses.NonPhysicalAttachments![index]!.Value;
                var localFrame = EvaluateNonPhysicalAfterPhysics(entry, info, afterParents, interactiveObjectId, deferRigidSkinning);
                if (localFrame is not null) source[index] = entry with { LocalFrame = localFrame };
                if (!deferRigidSkinning) previewWorking?.Remove(entry.Object.Id);
            }
            if (afterParents is not null) updatedInPhase3[index] = true;
        }
    }

    private static bool HasUpdatedParent(Entry[] source, SharedBoundaryTopology topology, BoundaryPoses poses,
        bool[] updatedInPhase3, int index)
    {
        var bindings = topology.BindingsByIndex[index];
        var parentIndices = topology.ParentActorIndicesByBinding[index];
        for (int b = 0; b < bindings.Length; b++)
        {
            int parent = parentIndices[b];
            if (updatedInPhase3[parent] || poses.DisabledBones[parent] is not null
                && source[parent].Controller!.IsBoneAffectedByAfterPhysics(bindings[b].ParentBoneIndex))
                return true;
        }
        return false;
    }

    private Dictionary<int, Matrix4x4>? ResolveUpdatedParents(Entry[] source, SharedBoundaryTopology topology,
        SharedBoundaryGraph? boundaryGraph, BoundaryPoses poses, int index, double frame)
    {
        var entry = source[index];
        var ground = boundaryGraph is not null ? boundaryGraph.GroundByIndex[index] : ControlGroundParents(entry.Object.Id, frame);
        var parents = BuildBoundaryOutsideParents(source, entry, topology.BindingsByIndex[index], topology.ParentActorIndicesByBinding[index],
            ground, (parent, _) => source[parent].Controller!.BoneWorldMatrices);
        return poses.HasBeforeParents[index] && CharacterAnimationController.OutsideParentsEqual(poses.BeforeParents[index], parents)
            ? null : parents;
    }
}
