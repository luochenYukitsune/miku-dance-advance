using Mmd.Modern.Core;
using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

/// <summary>Detached scrub-seek inputs captured on the owner thread before background preparation.
/// Background execution reads only this immutable snapshot and never touches live session fields.</summary>
public sealed class SceneSeekRequest
{
    private readonly SceneProjectSession owner;
    private readonly object capturedContent;
    private readonly long capturedStateEpoch;
    private readonly Func<CancellationToken, SceneAnimationSession> prepare;

    internal SceneSeekRequest(
        SceneProjectSession owner,
        object capturedContent,
        long capturedStateEpoch,
        double targetFrame,
        Func<CancellationToken, SceneAnimationSession> prepare)
    {
        this.owner = owner;
        this.capturedContent = capturedContent;
        this.capturedStateEpoch = capturedStateEpoch;
        TargetFrame = targetFrame;
        this.prepare = prepare;
    }

    public double TargetFrame { get; }

    /// <summary>Call only on the owner thread. Returns false if an edit, Undo/Redo, explicit
    /// synchronous seek, playback advance or disposal occurred after capture.</summary>
    public bool IsCurrent(SceneProjectSession session)
        => owner.IsSeekRequestCurrent(session, capturedContent, capturedStateEpoch);

    /// <summary>Builds and stabilizes an independent candidate session with full physics warmup on a worker.</summary>
    public ScenePreparedSeek Prepare(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = prepare(cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            try { candidate.Dispose(); } catch { }
            cancellationToken.ThrowIfCancellationRequested();
        }
        return new ScenePreparedSeek(this, candidate);
    }
}

/// <summary>Worker-prepared candidate waiting for owner-thread publication or deterministic disposal.</summary>
public sealed class ScenePreparedSeek : IDisposable
{
    private SceneAnimationSession? candidate;

    internal ScenePreparedSeek(SceneSeekRequest request, SceneAnimationSession candidate)
    {
        Request = request;
        this.candidate = candidate;
    }

    public SceneSeekRequest Request { get; }

    internal SceneAnimationSession? TakeCandidate()
        => Interlocked.Exchange(ref candidate, null);

    public void Dispose()
    {
        Interlocked.Exchange(ref candidate, null)?.Dispose();
    }
}

public sealed partial class SceneProjectSession
{
    private long stateEpoch;

    internal bool IsSeekRequestCurrent(SceneProjectSession session, object capturedContent, long capturedStateEpoch)
        => ReferenceEquals(session, this) && !disposed && !editing
            && ReferenceEquals(content, capturedContent) && stateEpoch == capturedStateEpoch;

    /// <summary>True when Seek performs multi-substep physics warmup rather than an immediate in-place seek.</summary>
    public bool RequiresPhysicsPreRoll
    {
        get
        {
            RequireAlive();
            return physics.PoseInitializationSubsteps > 0;
        }
    }

    /// <summary>Evaluates bones, IK, and morphs at an integer frame without running physics warmup
    /// or rebuilding the solver graph. Callers must perform a full Seek before playback or settling.</summary>
    public OriginalFrameScene SeekWithoutPhysicsPreRoll(double frame, CancellationToken cancellationToken = default)
    {
        RequireIdle();
        ValidateSeekFrame(frame);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return animation.Seek(frame, cancellationToken);
        }
        finally
        {
            stateEpoch++;
        }
    }

    /// <summary>Captures immutable state on the owner thread for detached background Seek preparation.
    /// Returns null when PoseInitializationSubsteps is zero so callers retain in-place synchronous Seek.</summary>
    public SceneSeekRequest? TryCaptureSeekRequest(double frame)
    {
        RequireIdle();
        ValidateSeekFrame(frame);
        int initializationSubsteps = physics.PoseInitializationSubsteps;
        if (initializationSubsteps == 0) return null;
        var capturedContent = content;
        var capturedEpoch = stateEpoch;
        var initialTextureSource = animation.PublishedTextureSource;
        var physicsProvider = physics;
        bool capturedDeferSkinning = deferVertexSkinning;
        return new SceneSeekRequest(
            this,
            capturedContent,
            capturedEpoch,
            frame,
            token => PrepareDetachedCandidate(capturedContent, physicsProvider, frame, initializationSubsteps, initialTextureSource, capturedDeferSkinning, token));
    }

    /// <summary>Publishes a worker-prepared candidate on the owner thread when the request is still current.
    /// Always disposes rejected or superseded candidates; returns false when stale or disposed.</summary>
    public bool TryPublishSeek(ScenePreparedSeek prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (disposed)
        {
            prepared.Dispose();
            return false;
        }
        RequireIdle();
        if (!prepared.Request.IsCurrent(this))
        {
            prepared.Dispose();
            return false;
        }
        var candidate = prepared.TakeCandidate();
        if (candidate is null) return false;
        try
        {
            if (candidate.SharesPhysicsBackendWith(animation))
            {
                candidate.Dispose();
                throw new InvalidOperationException("A project candidate requires a fresh physics backend; its factory returned an active solver.");
            }
            Publish(content, candidate);
            return true;
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
    }

    private static SceneAnimationSession PrepareDetachedCandidate(
        Content snapshot,
        IProjectPhysicsBackendProvider physicsProvider,
        double frame,
        int initializationSubsteps,
        SceneSnapshot? initialTextureSource,
        bool deferVertexSkinning,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ICharacterPhysicsBackend Independent(EditableSceneObject item)
            => physicsProvider.CreateIndependent(snapshot.Settings.Physics, item);
        ICharacterPhysicsMutableBackend Shared()
            => physicsProvider.CreateShared(snapshot.Settings.Physics);
        ISoftBodyBackend Soft(EditableSceneObject item)
            => physicsProvider.CreateSoftBody(snapshot.Settings.Physics, item);

        SceneAnimationSession candidate;
        using (PoseEvaluationMetrics.Measure("project.prepare.constructor"))
            candidate = new SceneAnimationSession(snapshot.Layout, snapshot.Motions, Independent,
                snapshot.Lighting.Snapshot, token, frame, snapshot.Parents, snapshot.SceneMotion,
                snapshot.Settings.Physics.SharedWorld ? Shared : null,
                snapshot.Settings.Physics.CoupledSoftBodies ? null : Soft,
                sharedWorldGravity: snapshot.Settings.Physics.SharedWorld ? snapshot.Settings.Physics.Gravity : null,
                pmmControls: snapshot.PmmControls,
                coupledSoftBodies: snapshot.Settings.Physics.CoupledSoftBodies,
                modelRoles: snapshot.Lighting.ModelRoles,
                deferInitialComposition: initializationSubsteps > 0,
                initialTextureSource: initialTextureSource,
                deferVertexSkinning: deferVertexSkinning);
        try
        {
            using var measure = PoseEvaluationMetrics.Measure("project.prepare.stabilize");
            candidate.StabilizePhysicsAtCurrentFrame(initializationSubsteps, token);
            return candidate;
        }
        catch
        {
            try { candidate.Dispose(); }
            catch { /* Worker never touches owner cleanupDiagnostics; disposal stays isolated. */ }
            throw;
        }
    }
}
