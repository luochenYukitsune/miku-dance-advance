using Mmd.Modern.Animation;
using Mmd.Modern.Core;

namespace Mmd.Modern.Application;

public sealed partial class SceneProjectSession
{
    // Only the detached-request factory grants this permission. The live project and ordinary
    // synchronous previews cannot bypass their transaction or complete-pose initialization paths.
    private bool isDetachedInteractivePoseOwner;

    /// <summary>Updates raw local bone drafts on a worker-owned preview without moving the animation
    /// clock, committing keys or resetting its owned rigid solvers. Supply the complete set of
    /// drafts for this actor on each call; omitted bones use the captured authored motion again.
    /// This temporary pose must not be used for project persistence or final output. Final evaluation
    /// still uses a fresh detached request with the normal complete initialization policy.</summary>
    public OriginalFrameScene PreviewInteractiveBones(Guid objectId,
        IReadOnlyDictionary<int, BoneLocalTransform> overrides, double elapsedSeconds,
        CancellationToken cancellationToken = default)
    {
        RequireIdle();
        if (!isDetachedInteractivePoseOwner)
            throw new InvalidOperationException("Interactive bone evaluation requires an owner created from a detached pose-preview request.");
        return animation.PreviewInteractiveBones(objectId, overrides, elapsedSeconds, cancellationToken);
    }
}
