using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

public sealed partial class SceneProjectSession
{
    private SceneProjectSession? posePreviewSource;
    private Content? posePreviewSourceContent;
    private SceneAnimationSession? posePreviewSourceAnimation;
    private Mmd.Modern.Core.OriginalFrameScene? posePreviewSourceFrame;
    private double posePreviewSourceClock;
    private IReadOnlyDictionary<Guid, VmdMotion> posePreviewMotions = new Dictionary<Guid, VmdMotion>();

    /// <summary>Creates a disposable, detached editing preview at the current clock. The supplied motion
    /// contains the unregistered draft. Immutable model/texture assets are shared; every solver is freshly
    /// owned. Pending visual leaves in the source are completed without advancing its physical clock.
    /// This performs no file I/O. The host must dispose
    /// the result when the draft ends and must use the live owner for project persistence and export.</summary>
    public SceneProjectSession CreatePosePreview(Guid objectId, VmdMotion draftMotion,
        CancellationToken cancellationToken = default)
        => CreatePosePreview(new Dictionary<Guid, VmdMotion> { [objectId] = draftMotion }, cancellationToken);

    /// <summary>Creates one coherent detached graph for simultaneous unregistered poses on several actors.</summary>
    public SceneProjectSession CreatePosePreview(IReadOnlyDictionary<Guid, VmdMotion> draftMotions,
        CancellationToken cancellationToken = default)
    {
        RequireIdle();
        if (animation.IsFaulted) throw new InvalidOperationException("Recover the project before previewing an edited pose.", animation.FaultCause);
        cancellationToken.ThrowIfCancellationRequested();
        var overrides = SnapshotPosePreviewMotions(draftMotions);
        var next = PosePreviewContent(content, overrides);
        var sourceFrame = animation.GetFrame(cancellationToken);
        double sourceClock = animation.CurrentFrame;
        var preview = new SceneProjectSession(next, new PosePreviewPhysicsProvider(this, physics),
            sourceClock, cancellationToken)
        {
            posePreviewSource = this,
            posePreviewSourceContent = content,
            posePreviewSourceAnimation = animation,
            posePreviewSourceFrame = sourceFrame,
            posePreviewSourceClock = sourceClock,
            posePreviewMotions = overrides
        };
        return preview;
    }

    private SceneProjectSession(Content initial, IProjectPhysicsBackendProvider physics, double frame, CancellationToken token)
    {
        this.physics = physics;
        content = initial;
        animation = Prepare(initial, frame, token);
    }

    /// <summary>A preview becomes stale after any committed content change, Undo/Redo, seek or playback.
    /// Even a seek to the same frame can replace its physical pose and must invalidate the preview.</summary>
    public bool CanReusePosePreview(SceneProjectSession source)
        => CanReusePosePreviewWithCancellation(source, default);

    private bool CanReusePosePreviewWithCancellation(SceneProjectSession source, CancellationToken token)
    {
        RequireIdle(); ArgumentNullException.ThrowIfNull(source); source.RequireIdle();
        token.ThrowIfCancellationRequested();
        return ReferenceEquals(posePreviewSource, source)
            && ReferenceEquals(posePreviewSourceContent, source.content)
            && ReferenceEquals(posePreviewSourceAnimation, source.animation)
            && !source.animation.IsFaulted
            && ReferenceEquals(posePreviewSourceFrame, source.animation.GetFrame(token))
            && posePreviewSourceClock == source.animation.CurrentFrame;
    }

    /// <summary>Updates only a detached editing owner; a live project cannot use this path to bypass Undo.
    /// Independent actors without outside-parent or PMM controls reuse the other detached controllers.
    /// Shared/attached/controlled graphs are rebuilt coherently at the same clock. No owner is advanced.</summary>
    public void UpdatePosePreviewMotion(Guid objectId, VmdMotion draftMotion, CancellationToken cancellationToken = default)
    {
        var overrides = new Dictionary<Guid, VmdMotion>(posePreviewMotions) { [objectId] = draftMotion };
        UpdatePosePreviewMotions(overrides, cancellationToken);
    }

    /// <summary>Atomically replaces the complete set of transient overrides. Omitting an actor restores its
    /// committed motion. A failed or cancelled replacement retains every previously published draft.</summary>
    public void UpdatePosePreviewMotions(IReadOnlyDictionary<Guid, VmdMotion> draftMotions,
        CancellationToken cancellationToken = default)
    {
        RequireIdle();
        var source = posePreviewSource ?? throw new InvalidOperationException("Only a detached pose preview can replace transient motion.");
        cancellationToken.ThrowIfCancellationRequested();
        var overrides = SnapshotPosePreviewMotions(draftMotions);
        var next = PosePreviewContent(source.content, overrides);
        if (!CanReusePosePreviewWithCancellation(source, cancellationToken)) throw new InvalidOperationException("The editing preview is stale; recreate it from the live project.");
        var affected = posePreviewMotions.Keys.Concat(overrides.Keys).Distinct().Where(id =>
            !posePreviewMotions.TryGetValue(id, out var previous) || !overrides.TryGetValue(id, out var replacement)
            || !SamePoseMotion(previous, replacement)).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (affected.Length == 0) { content = next; posePreviewMotions = overrides; return; }
        if (physics.PoseInitializationSubsteps == 0 && affected.Length == 1 && !next.Settings.Physics.SharedWorld
            && next.Parents.Length == 0 && next.PmmControls is null)
        {
            var objectId = affected[0];
            animation.BindMotion(next.Motions.Single(binding => binding.ObjectId == objectId), cancellationToken);
            content = next;
        }
        else Publish(next, Prepare(next, animation.CurrentFrame, cancellationToken));
        posePreviewMotions = overrides;
    }

    private static IReadOnlyDictionary<Guid, VmdMotion> SnapshotPosePreviewMotions(IReadOnlyDictionary<Guid, VmdMotion> motions)
    {
        ArgumentNullException.ThrowIfNull(motions);
        var result = new Dictionary<Guid, VmdMotion>();
        foreach (var (id, motion) in motions)
        { ArgumentNullException.ThrowIfNull(motion); result.Add(id, new MotionEditor(motion).ExportMotion()); }
        return result;
    }

    private static bool SamePoseMotion(VmdMotion a, VmdMotion b)
        => a.BoneKeyframes.Count == b.BoneKeyframes.Count && a.BoneKeyframes.Zip(b.BoneKeyframes).All(pair =>
            pair.First.BoneName == pair.Second.BoneName && pair.First.Frame == pair.Second.Frame
            && pair.First.Translation == pair.Second.Translation && pair.First.Rotation == pair.Second.Rotation
            && pair.First.Interpolation.SequenceEqual(pair.Second.Interpolation))
        && a.MorphKeyframes.Count == b.MorphKeyframes.Count && a.MorphKeyframes.Zip(b.MorphKeyframes).All(pair =>
            pair.First.MorphName == pair.Second.MorphName && pair.First.Frame == pair.Second.Frame && pair.First.Weight == pair.Second.Weight)
        && a.IkKeyframes.Count == b.IkKeyframes.Count && a.IkKeyframes.Zip(b.IkKeyframes).All(pair =>
            pair.First.Frame == pair.Second.Frame && pair.First.Visible == pair.Second.Visible && pair.First.Switches.SequenceEqual(pair.Second.Switches));

    private static Content PosePreviewContent(Content baseline, IReadOnlyDictionary<Guid, VmdMotion> motions)
    {
        // The committed content already owns immutable layout, lighting and model assets.
        // A transient pose changes only motion bindings: cloning both editors and forcing
        // Composition.Frame here used to rebuild the entire scene on the UI thread.
        if (motions.Count == 0) return baseline;
        var bindings = (SceneActorMotion[])baseline.Motions.Clone();
        foreach (var (objectId, motion) in motions)
        {
            int index = Array.FindIndex(bindings, binding => binding.ObjectId == objectId);
            if (index < 0) throw new ArgumentException("Pose editing requires an explicitly bound model and motion.", nameof(motions));
            // Snapshot externally supplied containers before any solver is created. The original binding,
            // resource identity and imported asset bytes remain untouched in the live project.
            bindings[index] = bindings[index] with { Motion = motion, Asset = null };
        }
        return baseline with { Motions = bindings, AnimationIdentity = new() };
    }

    // A defective custom provider must not hand a preview one of the live owner's solvers. Reject aliases
    // before a candidate initializes or disposes them; Prepare also checks against the previous preview.
    private sealed class PosePreviewPhysicsProvider(SceneProjectSession source, IProjectPhysicsBackendProvider provider)
        : IProjectPhysicsBackendProvider
    {
        public int PoseInitializationSubsteps => provider.PoseInitializationSubsteps;

        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item)
        {
            var backend = provider.CreateIndependent(settings, item);
            if (source.animation.UsesPhysicsBackend(backend))
                throw new InvalidOperationException("An editing preview requires a fresh independent physics backend.");
            return backend;
        }

        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings)
        {
            var backend = provider.CreateShared(settings);
            if (source.animation.UsesPhysicsBackend(backend))
                throw new InvalidOperationException("An editing preview requires a fresh shared physics backend.");
            return backend;
        }

        public ISoftBodyBackend CreateSoftBody(ProjectPhysicsSettings settings, EditableSceneObject item)
        {
            var backend = provider.CreateSoftBody(settings, item);
            if (source.animation.UsesSoftPhysicsBackend(backend))
                throw new InvalidOperationException("An editing preview requires a fresh soft-body backend.");
            return backend;
        }
    }
}
