// Fixed-pose initialization follows the bounded solver warmup pattern in saba's VMDAnimation::SyncPhysics
// (third_party/saba/src/Saba/Model/MMD/VMDAnimation.cpp) and three.js r170 MMDPhysics.warmup/MMDAnimationHelper.
// This host policy holds the exact target pose; it does not reproduce saba's blended transition or replay motion.
using Mmd.Modern.Core;
using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

public sealed partial class SceneAnimationSession
{
    /// <summary>Settles newly reset solvers at the current, clamped actor poses. Every substep is 1/120
    /// second without advancing motion, scene controls, gravity noise or the timeline. Impulse morphs
    /// are sampled for the displayed pose but are not repeatedly applied to the solver.</summary>
    public OriginalFrameScene StabilizePhysicsAtCurrentFrame(int substeps, CancellationToken cancellationToken = default)
    {
        RequireHealthyWithoutPreviewCompletion(); cancellationToken.ThrowIfCancellationRequested();
        if (substeps is < 0 or > 240) throw new ArgumentOutOfRangeException(nameof(substeps));
        CompletePreviewLeaves(cancellationToken);
        if (substeps == 0 || !entries.Any(entry => entry.Controller?.PhysicsEnabled == true)) return published;
        var next = (Entry[])entries.Clone();
        using var aggregation = PoseEvaluationMetrics.AggregateBoundaries();
        bool shared = pmmControls is not null || sharedWorld is not null;
        // Saba's SyncPhysics retains node-before/physics/node-after at every warm boundary and
        // leaves vertex deformation for presentation. Soft pins and collision surfaces still need
        // the established full path; only a completely rigid graph may defer its intermediate skins.
        bool deferSharedRigidSkinning = shared && !coupledSoftBodies
            && entries.All(entry => entry.Controller?.SoftBodiesEnabled != true);
        try
        {
            SharedBoundaryGraph? graph = null;
            if (deferSharedRigidSkinning)
            {
                // The clock and parent slots cannot change within this initialization call. Reuse
                // the existing request-local plan, while every boundary still reads new solved
                // parent palettes. A later initialization/seek constructs and validates its own plan.
                using var graphMeasure = PoseEvaluationMetrics.Measure("initialization.graph-plan");
                graph = PrepareSharedBoundaryGraph(next, CurrentFrame);
            }
            var independentOrder = !shared && outsideBindings.Length != 0
                ? ValidateOutsideParents(outsideBindings, next) : null;
            if (!shared && outsideBindings.Length == 0 && entries.All(entry => entry.Controller?.SoftBodiesEnabled != true))
            {
                // These graphs cannot exchange solver state or change fixed-pose animation inputs.
                // Preserve all 1/120 second reflection boundaries, but publish each actor's skin once.
                for (int index = 0; index < next.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = next[index];
                    if (entry.Controller is not { PhysicsEnabled: true } controller || entry.Motion is not { } motion) continue;
                    try
                    {
                        var pose = controller.StabilizeRigidPhysicsAtCurrentFrame(substeps,
                            motion.IkEnabled, motion.MorphsEnabled, cancellationToken);
                        next[index] = entry with { LocalFrame = SharedLocalFrame(entry, pose) };
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    { throw new SceneActorAnimationException(entry.Object.Id, "pose initialization", error); }
                }
            }
            else for (int step = 0; step < substeps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (shared)
                {
                    using var boundaryMeasure = PoseEvaluationMetrics.Measure("initialization.boundary");
                    EvaluateSharedBoundary(next, sharedWorld!, CurrentFrame, CurrentFrame,
                        reset: false, step: true, cancellationToken, initializing: true,
                        deferRigidSkinning: deferSharedRigidSkinning && step + 1 < substeps, boundaryGraph: graph);
                    continue;
                }
                foreach (int index in independentOrder ?? ValidateOutsideParents(outsideBindings, next))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = next[index];
                    if (entry.Controller is not { } controller || entry.Motion is not { } motion) continue;
                    try
                    {
                        if (outsideBindings.Any(binding => binding.ObjectId == entry.Object.Id))
                            controller.SetOutsideParents(EffectiveOutsideParents(entry.Object.Id, next, outsideBindings));
                        double local = Math.Min(CurrentFrame, controller.DurationFrames);
                        var pose = controller.PhysicsEnabled
                            ? controller.Advance(local, CharacterPhysicsSession.FixedStepSeconds,
                                motion.IkEnabled, motion.MorphsEnabled, applyImpulses: false)
                            : controller.SampleAt(local, motion.IkEnabled, motion.MorphsEnabled);
                        next[index] = entry with { LocalFrame = SharedLocalFrame(entry, pose) };
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    { throw new SceneActorAnimationException(entry.Object.Id, "pose initialization", error); }
                }
            }
            OriginalFrameScene composed;
            using (PoseEvaluationMetrics.Measure("initialization.compose"))
                composed = Compose(next, published);
            var nextStates = CaptureStates(next, CurrentFrame);
            cancellationToken.ThrowIfCancellationRequested();
            if (sharedStatus is { } sharedState) { sharedState.LastStepCount = substeps; sharedState.IsFaulted = false; }
            foreach (var status in controlledStatuses) { status.LastStepCount = substeps; status.IsFaulted = false; }
            entries = next; published = composed; states = nextStates;
            return published;
        }
        catch (Exception error)
        {
            // Solvers may have stepped already; a failed direct initialization requires a fresh seek.
            // SceneProjectSession only invokes this on an unpublished candidate and disposes failures.
            MarkFault(error);
            if (sharedStatus is { } sharedState) sharedState.IsFaulted = true;
            foreach (var status in controlledStatuses) status.IsFaulted = true;
            throw;
        }
    }
}
