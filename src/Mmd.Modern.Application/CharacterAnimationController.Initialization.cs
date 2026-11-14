using Mmd.Modern.Core;

namespace Mmd.Modern.Application;

public sealed partial class CharacterAnimationController
{
    /// <summary>Initializes a fixed independent rigid pose without rebuilding its skin at every solver boundary.</summary>
    internal (OriginalStyleScene Surface, OriginalOutlineData Outline) StabilizeRigidPhysicsAtCurrentFrame(
        int substeps, bool enableIk, bool enableMorphs, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (physics is null || SoftBodiesEnabled || sharedPhysicsStatus is not null)
            throw new InvalidOperationException("Fixed rigid initialization requires an independent solver without soft bodies.");
        if (outsideParents?.Count > 0 || frozenBoneWorlds is not null)
            throw new InvalidOperationException("Fixed rigid initialization requires an unattached, unfrozen actor.");
        if (physics.CurrentFrame is not { } frame)
            throw new InvalidOperationException("Sample or reset the actor before pose initialization.");
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = PrepareMorphs(frame, enableMorphs);
        var evaluation = Evaluation(frame, enableIk, enableMorphs);
        var pose = physics.StabilizeAt(frame, substeps, evaluation, cancellationToken);
        return Store(pose, frame, enableIk, enableMorphs, prepared);
    }
}
