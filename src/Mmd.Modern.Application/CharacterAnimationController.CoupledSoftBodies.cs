using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

public sealed partial class CharacterAnimationController
{
    private PmxCoupledSoftBodyPlayback? sharedCoupledSoftBodies;
    private PmxCoupledSoftBodyPlayback? CoupledSoftBodies => physics?.CoupledSoftBodies ?? sharedCoupledSoftBodies;
    public bool UsesCoupledSoftBodies => CoupledSoftBodies is not null;

    /// <summary>Explicitly selects a coupled numerical owner. Prepares a complete replacement before publication;
    /// pin targets use pre-physics skinning, unlike the independent one-way soft backend.</summary>
    public void EnableCoupledPhysicsAt(ICharacterPhysicsBackend backend, double frame, bool enableIk = true, bool enableMorphs = true)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ArgumentNullException.ThrowIfNull(backend);
        if (ReferenceEquals(physicsBackend, backend)) throw new InvalidOperationException("A replacement coupled world requires a newly owned backend.");
        CharacterPhysicsPlayback? candidate = null; bool transferred = false;
        try
        {
            ValidateFrame(frame);
            if (!SupportsSoftBodies) throw new NotSupportedException("Model contains no PMX soft bodies.");
            if (backend is not ICharacterSoftBodyProvider) throw new NotSupportedException("The selected backend does not provide a coupled rigid-soft world.");
            if (sharedPhysicsStatus is not null) throw new InvalidOperationException("Detach shared physics before replacing an independent world.");
            frame = Math.Min(frame, DurationFrames);
            var prepared = PrepareMorphs(frame, enableMorphs); var adapted = PmxCharacterPhysicsAdapter.Convert(model);
            transferred = true; candidate = new(animator, adapted.Definition, backend); candidate.EnableCoupledSoftBodies(model);
            var pose = candidate.ResetAt(frame, Evaluation(frame, enableIk, enableMorphs));
            var scene = CreateScene(pose, prepared, candidate.CoupledSoftBodies!.Apply(pose.Vertices));
            var previous = physics; var previousSoft = softBodies;
            physics = candidate; candidate = null; physicsBackend = backend; physicsDiagnostics = adapted.Diagnostics;
            softBodies = null; softBackend = null; softBackendName = null;
            sampled = scene; sampledFrame = frame; sampledIk = enableIk; sampledMorphs = enableMorphs;
            sampledPose = pose; BoneWorldMatrices = Array.AsReadOnly(pose.BoneWorldMatrices); MorphChannels = prepared.Channels;
            sampledMorphsPreparation = prepared;
            IsVisible = visibility.Evaluate(frame); frozenBoneWorlds = null; frozenOutsideParents = null; frozenSoftVertices = null;
            try { previous?.Dispose(); } catch (Exception error) { physicsCleanupDiagnostics.Add("Retired coupled world cleanup failed: " + error.Message); }
            try { previousSoft?.Dispose(); } catch (Exception error) { physicsCleanupDiagnostics.Add("Retired standalone soft cleanup failed: " + error.Message); }
        }
        catch { candidate?.Dispose(); if (!transferred) backend.Dispose(); throw; }
    }

    internal void ResetSharedCoupledSoftBodies(SharedCharacterPhysicsSession world, Guid actorId, Matrix4x4 placement,
        SharedPreparedPhysicsSample prepared)
    {
        var candidate = new PmxCoupledSoftBodyPlayback(model, world.CreateSoftBodyBinding(actorId), prepared.BeforePhysicsVertices(), placement);
        var previous = sharedCoupledSoftBodies; sharedCoupledSoftBodies = candidate; previous?.Dispose();
    }

    internal Vector3[]? PrepareCoupledTargets(SharedPreparedPhysicsSample prepared)
        => sharedCoupledSoftBodies?.PrepareTargets(prepared.BeforePhysicsVertices());
    internal void PublishCoupledTargets(Vector3[]? targets, bool frozen, bool updateTargets = true)
    { if (targets is not null) sharedCoupledSoftBodies!.PublishTargets(targets, frozen, updateTargets); }

    private Matrix4x4? CoupledFrozenDelta(Matrix4x4[] nextWorlds)
    {
        if (CoupledSoftBodies is null || CoupledSoftBodies.SupportsFrozenCollisionSurface) return null;
        var oldMaps = animator.CalculateVertexSkinTransforms(BoneWorldMatrices.ToArray());
        var nextMaps = animator.CalculateVertexSkinTransforms(nextWorlds); Matrix4x4? common = null;
        foreach (int vertex in SoftVertexIndices)
        {
            if (!Matrix4x4.Invert(oldMaps[vertex], out var inverse))
                throw new NotSupportedException("Coupled frozen soft nodes have a singular skin map; seek before refreshing.");
            var delta = inverse * nextMaps[vertex]; delta.M14 = delta.M24 = delta.M34 = 0; delta.M44 = 1;
            try { PhysicsMath.ValidateRigid(delta, nameof(nextWorlds)); }
            catch (ArgumentException error) { throw new NotSupportedException("Coupled frozen soft history requires a uniform rigid skin rebase.", error); }
            if (common is { } first && !EquivalentRigid(first, delta))
                throw new NotSupportedException("Coupled frozen soft history cannot yet rebase different nodes by different skin maps; seek before refreshing.");
            common ??= delta;
        }
        return common;
    }

    private static bool EquivalentRigid(Matrix4x4 first, Matrix4x4 second)
        => Vector3.Distance(first.Translation, second.Translation) < 1e-4f
            && Vector3.Distance(Vector3.TransformNormal(Vector3.UnitX, first), Vector3.TransformNormal(Vector3.UnitX, second)) < 1e-4f
            && Vector3.Distance(Vector3.TransformNormal(Vector3.UnitY, first), Vector3.TransformNormal(Vector3.UnitY, second)) < 1e-4f
            && Vector3.Distance(Vector3.TransformNormal(Vector3.UnitZ, first), Vector3.TransformNormal(Vector3.UnitZ, second)) < 1e-4f;

    // Preflight the complete dependency graph before any outside-parent setter, gravity, node, body-mode or clock mutation.
    internal Matrix4x4[] PreviewCoupledBoundary(double frame, bool ik, bool morphs, IReadOnlyDictionary<int, Matrix4x4>? parents,
        IReadOnlySet<int> disabled, bool frozen, IReadOnlyDictionary<int, OutsideParentTrackWindow>? trackWindows = null)
    {
        var next = CopyOutsideParents(parents);
        bool fullEvalFrozen = frozen && RequiresFullEvaluationForFrozenOutsideParents(next, !TrackWindowsEqual(outsideParentTrackWindows, trackWindows));
        if (frozen && !fullEvalFrozen)
        {
            var worlds = FrozenWorlds(next);
            if (CoupledSoftBodies is { SupportsFrozenCollisionSurface: true } frozenSoft)
            {
                var prepared = PrepareMorphs(DurationFrames, sampledMorphs);
                var pose = animator.SkinWorldPose(worlds, Evaluation(DurationFrames, sampledIk, sampledMorphs).Morphs);
                var baseline = frozenSoftVertices ?? sampled.Surface.Snapshot.Vertices.ToArray();
                var vertices = ReprojectFrozenSoftVertices(frozenBoneWorlds ?? BoneWorldMatrices.ToArray(), pose, baseline);
                _ = frozenSoft.PrepareFrozenSurface(vertices);
                _ = CreateScene(pose, prepared, vertices);
            }
            else _ = CoupledFrozenDelta(worlds);
            return worlds;
        }
        var driven = model.RigidBodies.Where(body => body.BoneIndex >= 0 && body.Operation != Formats.PmxRigidBodyOperation.Static
            && !disabled.Contains(body.BoneIndex)).Select(body => body.BoneIndex).Distinct().ToArray();
        var options = Evaluation(frame, ik, morphs) with
            { OutsideParents = next, PhysicsDrivenBoneIndices = driven, OutsideParentTrackWindows = trackWindows };
        _ = PrepareMorphs(frame, morphs);
        using var sample = animator.BeginPhysicsSample(frame, options);
        var before = sample.BeforePhysicsBoneWorldMatrices.ToArray();
        if (CoupledSoftBodies is { } soft) _ = soft.PrepareTargets(animator.SkinWorldPose(before, options.Morphs).Vertices);
        return before;
    }
}
