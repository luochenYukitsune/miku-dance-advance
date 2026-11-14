using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>Inputs captured by a project owner before detached background pose evaluation.
/// The creation delegate reads only frozen content; source validity is checked by the original owner.</summary>
public sealed class ScenePosePreviewRequest
{
    private readonly Func<IProjectPhysicsBackendProvider, CancellationToken, SceneProjectSession> create;
    private readonly Func<SceneProjectSession, bool> isCurrent;
    private readonly object sourceContent;
    private readonly object sourceAnimation;
    private readonly object sourceFrame;
    private readonly double sourceClock;

    internal ScenePosePreviewRequest(Func<IProjectPhysicsBackendProvider, CancellationToken, SceneProjectSession> create,
        Func<SceneProjectSession, bool> isCurrent, bool supportsInteractiveBones,
        object sourceContent, object sourceAnimation, object sourceFrame, double sourceClock)
    {
        this.create = create; this.isCurrent = isCurrent;
        SupportsInteractiveBones = supportsInteractiveBones;
        this.sourceContent = sourceContent; this.sourceAnimation = sourceAnimation;
        this.sourceFrame = sourceFrame; this.sourceClock = sourceClock;
    }

    /// <summary>Whether this captured graph supports a fixed-frame interactive bone preview.
    /// Rigid PMM/shared graphs preserve their physical boundaries and active outside parents;
    /// soft bodies and independent non-PMM cross-parent graphs require complete evaluation.</summary>
    public bool SupportsInteractiveBones { get; }

    /// <summary>Compares only captured immutable identities and the clock, without reading either
    /// live owner. Workers may use this to retain an owner across requests for the same source pose.
    /// The original owner must still check IsCurrent before publishing a completed snapshot.</summary>
    public bool HasSameSource(ScenePosePreviewRequest other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ReferenceEquals(sourceContent, other.sourceContent)
            && ReferenceEquals(sourceAnimation, other.sourceAnimation)
            && ReferenceEquals(sourceFrame, other.sourceFrame) && sourceClock == other.sourceClock;
    }

    /// <summary>Call only on the source owner's thread, before publishing a completed snapshot.
    /// Seek to the same clock, committed edits, Undo/Redo and disposal all invalidate this request.</summary>
    public bool IsCurrent(SceneProjectSession source) => isCurrent(source);

    /// <summary>Creates a worker-owned solver graph without reading the source owner. The caller must
    /// supply a factory that creates fresh solvers and does not borrow mutable state or solvers from
    /// another owner. Its pose-initialization policy is preserved. Dispose on the evaluation worker.</summary>
    public SceneProjectSession CreateSession(IProjectPhysicsBackendProvider freshPhysics,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(freshPhysics);
        cancellationToken.ThrowIfCancellationRequested();
        return create(freshPhysics, cancellationToken);
    }
}

public sealed partial class SceneProjectSession
{
    /// <summary>Capture on the source owner's thread, completing any deferred visual leaves.
    /// No solvers are created and no physical clock is advanced here.
    /// A detached request can subsequently create and evaluate a graph on one background worker.</summary>
    public ScenePosePreviewRequest CapturePosePreviewRequest(IReadOnlyDictionary<Guid, VmdMotion> draftMotions)
        => CapturePosePreviewRequestWithCancellation(draftMotions, default);

    /// <summary>Captures a detached request with cancellation during deferred visual completion.</summary>
    public ScenePosePreviewRequest CapturePosePreviewRequestWithCancellation(IReadOnlyDictionary<Guid, VmdMotion> draftMotions,
        CancellationToken cancellationToken)
    {
        RequireIdle();
        if (animation.IsFaulted) throw new InvalidOperationException("Recover the project before previewing an edited pose.", animation.FaultCause);
        cancellationToken.ThrowIfCancellationRequested();
        var next = PosePreviewContent(content, SnapshotPosePreviewMotions(draftMotions));
        var sourceContent = content;
        var sourceAnimation = animation;
        var sourceFrame = animation.GetFrame(cancellationToken);
        double sourceClock = animation.CurrentFrame;
        return new((provider, token) => new SceneProjectSession(next, provider, sourceClock, token)
            { isDetachedInteractivePoseOwner = true },
            source => ReferenceEquals(source, this) && !disposed && !editing
                && ReferenceEquals(content, sourceContent) && ReferenceEquals(animation, sourceAnimation)
                && ReferenceEquals(animation.Frame, sourceFrame) && animation.CurrentFrame == sourceClock
                && !animation.IsFaulted,
            animation.SupportsInteractiveBones, sourceContent, sourceAnimation, sourceFrame, sourceClock);
    }
}
