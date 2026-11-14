using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Physics;
using System.Numerics;

namespace Mmd.Modern.Application;

public sealed partial class SceneAnimationSession
{
    /// <summary>Rigid PMM/shared graphs use the established ordered physical boundary on a warm owner.
    /// Independent unparented actors can update only the edited controller. Soft graphs and ordinary
    /// independent cross-parent graphs retain complete detached evaluation.</summary>
    internal bool SupportsInteractiveBones => entries.Any(entry => entry.Motion is not null)
        && (pmmControls is not null || sharedPhysicsFactory is not null || outsideBindings.Length == 0)
        && entries.All(entry => entry.Motion?.Model.SoftBodies.Count is not > 0
            && entry.Controller?.SoftBodiesEnabled != true);
    private double interactiveAccumulator;

    /// <summary>Evaluates one independent actor at its existing local frame while letting the owned
    /// rigid solver follow its raw draft target. Ordinary playback's clock/frozen-actor rules and
    /// explicit seeks retain their existing behavior.</summary>
    internal OriginalFrameScene PreviewInteractiveBones(Guid objectId,
        IReadOnlyDictionary<int, BoneLocalTransform> overrides, double elapsedSeconds,
        CancellationToken cancellationToken = default)
    {
        RequireHealthyWithoutPreviewCompletion(); ArgumentNullException.ThrowIfNull(overrides);
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0 || elapsedSeconds > 86400)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        if (!SupportsInteractiveBones)
            throw new NotSupportedException("Interactive bone evaluation requires rigid actors with PMM/shared physics or independent actors without outside parents; use a complete detached pose preview for soft or independent cross-parent graphs.");
        int index = Index(objectId);
        var entry = entries[index];
        var controller = entry.Controller
            ?? throw new InvalidOperationException("Interactive bone evaluation requires an explicitly bound model and motion.");
        var motion = entry.Motion!;
        // Validate before any native body or frozen attachment anchor can change. The request owns
        // this normalized copy, so caller mutation cannot change the input during a physical boundary.
        var captured = new Dictionary<int, BoneLocalTransform>(overrides.Count);
        foreach (var (bone, value) in overrides)
        {
            if ((uint)bone >= (uint)motion.Model.Bones.Count) throw new ArgumentOutOfRangeException(nameof(overrides));
            if (!float.IsFinite(value.Translation.X) || !float.IsFinite(value.Translation.Y) || !float.IsFinite(value.Translation.Z)
                || !float.IsFinite(value.Rotation.X) || !float.IsFinite(value.Rotation.Y) || !float.IsFinite(value.Rotation.Z)
                || !float.IsFinite(value.Rotation.W) || !float.IsFinite(value.Rotation.LengthSquared()) || value.Rotation.LengthSquared() <= 1e-12f)
                throw new ArgumentException("Interactive bone transforms must be finite with nonzero rotations.", nameof(overrides));
            captured.Add(bone, value with { Rotation = Quaternion.Normalize(value.Rotation) });
        }
        CompletePreviewLeaves(cancellationToken); entry = entries[index]; controller = entry.Controller!; motion = entry.Motion!;
        if (pmmControls is not null || sharedPhysicsFactory is not null)
            return PreviewInteractiveBoundary(objectId, captured, elapsedSeconds, cancellationToken);
        var next = (Entry[])entries.Clone();
        try
        {
            var pose = controller.PreviewInteractiveBones(Math.Min(CurrentFrame, controller.DurationFrames),
                elapsedSeconds, captured, motion.IkEnabled, motion.MorphsEnabled);
            next[index] = entry with { LocalFrame = SharedLocalFrame(entry, pose) };
            OriginalFrameScene composed;
            using (PoseEvaluationMetrics.Measure("interactive.compose")) composed = Compose(next, published);
            var nextStates = CaptureStates(next, CurrentFrame);
            cancellationToken.ThrowIfCancellationRequested();
            entries = next; published = composed; states = nextStates;
            return published;
        }
        catch (Exception error)
        {
            // The solver/controller may already have advanced. Retain the last complete published
            // frame, fault this detached owner and let the worker replace it before another request.
            if (error is OperationCanceledException) { MarkFault(error); throw; }
            var failure = new SceneActorAnimationException(objectId, "interactive bone preview", error);
            MarkFault(failure); throw failure;
        }
    }

    private OriginalFrameScene PreviewInteractiveBoundary(Guid objectId,
        IReadOnlyDictionary<int, BoneLocalTransform> overrides, double elapsedSeconds, CancellationToken token)
    {
        var next = (Entry[])entries.Clone();
        double available = interactiveAccumulator + elapsedSeconds;
        int requested = (int)Math.Floor((available + 1e-12) / CharacterPhysicsSession.FixedStepSeconds);
        int steps = Math.Min(CharacterPhysicsSession.MaximumSubsteps, requested);
        try
        {
            // This plan cannot outlive the request: a seek, PMM parent switch or graph replacement
            // must validate and compile its own topology before another solver boundary can run.
            SharedBoundaryGraph graph;
            using (PoseEvaluationMetrics.Measure("interactive.graph-plan")) graph = PrepareSharedBoundaryGraph(next, CurrentFrame);
            IReadOnlySet<Guid>? active = null;
            if (sharedPhysicsFactory is null)
            {
                // Independent PMM worlds do not interact. Reevaluate the edited actor and every active
                // outside-parent descendant, retaining all unrelated controllers and native body history.
                var affected = new HashSet<Guid> { objectId };
                bool changed;
                do
                {
                    changed = false;
                    foreach (var parent in graph.Parents)
                        if (affected.Contains(parent.ParentObjectId)) changed |= affected.Add(parent.ObjectId);
                } while (changed);
                active = affected;
            }
            // Every sample uses the captured timeline clock. Shared worlds include every actor so
            // collision contacts, frozen colliders and kinematic targets remain a coherent batch.
            for (int step = 0; step < steps; step++)
                EvaluateSharedBoundary(next, sharedWorld!, CurrentFrame, CurrentFrame, reset: false, step: true, token,
                    interactiveObjectId: objectId, localBoneOverrides: overrides, activeActors: active,
                    deferRigidSkinning: step + 1 < steps, boundaryGraph: graph);
            if (steps == 0)
                EvaluateSharedBoundary(next, sharedWorld!, CurrentFrame, CurrentFrame, reset: false, step: false, token,
                    interactiveObjectId: objectId, localBoneOverrides: overrides, activeActors: active, boundaryGraph: graph);
            OriginalFrameScene composed;
            using (PoseEvaluationMetrics.Measure("interactive.compose")) composed = Compose(next, published);
            var nextStates = CaptureStates(next, CurrentFrame);
            token.ThrowIfCancellationRequested();
            interactiveAccumulator = Math.Max(0, available - requested * CharacterPhysicsSession.FixedStepSeconds);
            if (sharedStatus is not null) { sharedStatus.LastStepCount = steps; sharedStatus.IsFaulted = false; }
            foreach (var status in controlledStatuses) { status.LastStepCount = steps; status.IsFaulted = false; }
            entries = next; published = composed; states = nextStates;
            return published;
        }
        catch (Exception error)
        {
            // A cancelled boundary can already have changed native body history; the detached owner
            // must be retired while the live source and last published snapshot remain untouched.
            if (sharedStatus is not null) sharedStatus.IsFaulted = true;
            foreach (var status in controlledStatuses) status.IsFaulted = true;
            if (error is OperationCanceledException) { MarkFault(error); throw; }
            var failure = new SceneActorAnimationException(objectId, "interactive PMM/shared bone preview", error);
            MarkFault(failure); throw failure;
        }
    }
}
