using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

public sealed partial class CharacterAnimationController
{
    private PmxSoftBodyPlayback? softBodies;
    private ISoftBodyBackend? softBackend;
    private string? softBackendName;
    // Only populated while a shared sample is being completed; coordinates remain model-local.
    private Matrix4x4[]? sharedSoftRigidWorlds;
    public bool SupportsSoftBodies => model.SoftBodies.Count != 0;
    public bool SoftBodiesEnabled => softBodies is not null || UsesCoupledSoftBodies;
    internal bool UsesSoftBodyBackend(ISoftBodyBackend backend) => ReferenceEquals(softBackend, backend);
    internal bool SharesSoftBodyBackendWith(SceneAnimationSession other) => softBackend is not null && other.UsesSoftPhysicsBackend(softBackend);
    internal void SetSoftBodyGravity(Vector3 gravity)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (UsesCoupledSoftBodies) return; // Acceleration belongs to the one numerical owner.
        (softBodies ?? throw new InvalidOperationException("Enable the soft solver before setting gravity.")).SetGravity(gravity);
    }
    /// <summary>Transfers ownership. Prepare a reset candidate before replacing an existing soft solver.
    /// This independent soft world receives actual rigid transforms but cannot push impulses back into the rigid solver.</summary>
    public void EnableSoftBodies(ISoftBodyBackend backend, double frame, bool enableIk = true, bool enableMorphs = true)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ArgumentNullException.ThrowIfNull(backend);
        if (UsesSoftBodyBackend(backend)) throw new InvalidOperationException("A replacement soft solver requires a newly owned backend.");
        PmxSoftBodyPlayback? candidate = null;
        bool transferred = false;
        try
        {
            ValidateFrame(frame);
            if (!SupportsSoftBodies) throw new NotSupportedException("Model contains no PMX soft bodies.");
            if (UsesCoupledSoftBodies) throw new InvalidOperationException("Disable coupled soft content before attaching a standalone soft world.");
            if (sharedPhysicsStatus is not null) throw new NotSupportedException("Soft bodies must be explicitly synchronized after shared rigid steps.");
            frame = Math.Min(frame, DurationFrames);
            var prepared = PrepareMorphs(frame, enableMorphs);
            if (physics is not null && (physics.CurrentFrame != frame || sampledFrame != frame
                || sampledIk != enableIk || sampledMorphs != enableMorphs || sampledPose is null))
                throw new InvalidOperationException("Sample or reset rigid physics at the requested frame and options before enabling soft bodies.");
            var pose = physics is not null ? sampledPose! : animator.Sample(frame, Evaluation(frame, enableIk, enableMorphs));
            transferred = true; candidate = new(model, backend);
            candidate.Reset(pose.Vertices, SoftRigidWorlds(pose.BoneWorldMatrices));
            var previous = softBodies; softBodies = candidate; candidate = null;
            try { Store(pose, frame, enableIk, enableMorphs, prepared); }
            catch { softBodies.Dispose(); softBodies = previous; throw; }
            softBackend = backend;
            softBackendName = backend.GetType().Name;
            previous?.Dispose();
        }
        catch { candidate?.Dispose(); if (!transferred) backend.Dispose(); throw; }
    }
    public void DisableSoftBodies()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        sharedCoupledSoftBodies?.Dispose(); sharedCoupledSoftBodies = null; physics?.DisableCoupledSoftBodies();
        var old = softBodies; softBodies = null; softBackend = null; softBackendName = null; sampledFrame = null; old?.Dispose();
    }
    private Matrix4x4[] SoftRigidWorlds(IReadOnlyList<Matrix4x4> boneWorlds)
    {
        if (sharedSoftRigidWorlds is not null) return sharedSoftRigidWorlds;
        if (physics is not null) return physics.GetBodyWorldTransforms();
        var adapted = PmxCharacterPhysicsAdapter.Convert(model).Definition;
        return adapted.Bodies.Select((body, index) => body.BoneIndex < 0 ? adapted.BodyRestWorldTransforms[index]
            : adapted.BodyBoneOffsets[index] * boneWorlds[body.BoneIndex]).ToArray();
    }
    private SceneVertex[] ApplySoftBodies(SceneVertex[] skinned, IReadOnlyList<Matrix4x4> boneWorlds, double? elapsed)
    {
        if (CoupledSoftBodies is { } coupled) return coupled.Apply(skinned);
        if (softBodies is null) return skinned;
        var rigidWorlds = SoftRigidWorlds(boneWorlds);
        if (elapsed.HasValue) return softBodies.Advance(elapsed.Value, skinned, rigidWorlds);
        softBodies.Reset(skinned, rigidWorlds); return softBodies.Apply(skinned);
    }
}
