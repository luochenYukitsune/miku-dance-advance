using Mmd.Modern.Core;
using Mmd.Modern.Physics;
using System.Numerics;

namespace Mmd.Modern.Application;

/// <summary>Temporary, detached edit context. Retaining it cannot mutate the committed project.</summary>
public sealed partial class SceneProjectEdit
{
    internal PmaRenderingStyleState RenderingStyleState { get; set; } = PmaRenderingStyleState.Default;
    public SceneCompositionEditor Composition { get; }
    public SceneLightingEditor Lighting { get; }
    public List<SceneActorMotion> Motions { get; }
    public List<SceneOutsideParentBinding> OutsideParents { get; }
    public SceneMotionAsset? SceneMotion { get; set; }
    public ProjectSettings Settings { get; set; }
    public List<SceneMediaTrack> Media { get; }
    public List<SceneProjectAccessory> Accessories { get; }
    public ScenePmmControlState? PmmControls { get; set; }
    internal SceneProjectEdit(SceneCompositionEditor composition, SceneLightingEditor lighting,
        IEnumerable<SceneActorMotion> motions, IEnumerable<SceneOutsideParentBinding> parents,
        SceneMotionAsset? sceneMotion, ProjectSettings settings, IEnumerable<SceneMediaTrack> media, ScenePmmControlState? pmmControls = null,
        IEnumerable<SceneProjectAccessory>? accessories = null)
    {
        Composition = composition; Lighting = lighting; Motions = motions.ToList();
        OutsideParents = parents.ToList(); SceneMotion = sceneMotion; Settings = settings; Media = media.ToList();
        PmmControls = pmmControls;
        Accessories = accessories?.ToList() ?? [];
    }
    public void RemoveObject(Guid id)
    {
        Composition.Remove(id); Motions.RemoveAll(m => m.ObjectId == id);
        OutsideParents.RemoveAll(p => p.ObjectId == id || p.ParentObjectId == id);
        RenderingStyleState = RenderingStyleState.RemoveObject(id);
    }
}

/// <summary>Single-owner project transaction boundary. Layout, lighting, animation, media and settings
/// are prepared as one candidate before publication. Animation-affecting content edits and undo restart
/// solvers at the current clock; style-only and additional-light-only transactions retain them.
/// Solver histories are never serialized. No GUI or GPU dependency.</summary>
public sealed partial class SceneProjectSession : IDisposable
{
    private sealed record Content(SceneCompositionEditor Composition, SceneLightingEditor Lighting,
        SceneActorMotion[] Motions, SceneOutsideParentBinding[] Parents, SceneMotionAsset? SceneMotion,
        ProjectSettings Settings, SceneMediaTrack[] Media, ScenePmmControlState? PmmControls, PmaRenderingStyleState RenderingStyles,
        ValidatedSceneLayout Layout, SceneProjectAccessory[] Accessories)
    {
        // Style-only and additional-light-only copies keep this marker. Prepared candidates receive a fresh marker.
        public object AnimationIdentity { get; init; } = new();
    }
    private readonly IProjectPhysicsBackendProvider physics;
    private readonly Stack<Content> undo = new(), redo = new();
    private readonly List<string> cleanupDiagnostics = [];
    private Content content;
    private SceneAnimationSession animation;
    private bool disposed, editing;

    public SceneProjectSession(SceneCompositionEditor composition, SceneLightingEditor lighting,
        IEnumerable<SceneActorMotion> motions, IProjectPhysicsBackendProvider physics,
        ProjectSettings? settings = null, IEnumerable<SceneOutsideParentBinding>? outsideParents = null,
        SceneMotionAsset? sceneMotion = null, IEnumerable<SceneMediaTrack>? media = null,
        double initialFrame = 0, CancellationToken cancellationToken = default, ScenePmmControlState? pmmControls = null,
        IEnumerable<SceneProjectAccessory>? accessories = null)
    {
        this.physics = physics ?? throw new ArgumentNullException(nameof(physics));
        content = Freeze(new(composition, lighting, motions, outsideParents ?? [], sceneMotion, settings ?? new(), media ?? [], pmmControls, accessories));
        animation = Prepare(content, initialFrame, cancellationToken);
    }
    private SceneProjectSession(PreparedSceneAnimationProject prepared, IProjectPhysicsBackendProvider physics,
        CancellationToken cancellationToken)
    {
        this.physics = physics;
        var state = prepared.Animation.CaptureSaveSnapshot();
        content = Freeze(new(prepared.Composition, prepared.Lighting, state.Motions, state.OutsideParents,
            state.SceneMotion, prepared.Settings, prepared.Media, state.PmmControls, prepared.Accessories));
        animation = prepared.Animation;
        animation.StabilizePhysicsAtCurrentFrame(physics.PoseInitializationSubsteps, cancellationToken);
    }
    public OriginalFrameScene Frame { get { RequireAlive(); return animation.Frame; } }
    private bool deferVertexSkinning;
    /// <summary>Host selection for a renderer that skins on the GPU; retained by every later prepared candidate.
    /// See SceneAnimationSession.DeferVertexSkinningToRenderer. Never persisted with the project.</summary>
    public bool DeferVertexSkinningToRenderer
    {
        get { RequireAlive(); return deferVertexSkinning; }
        set { RequireAlive(); deferVertexSkinning = value; animation.DeferVertexSkinningToRenderer = value; }
    }
    public IReadOnlyList<EditableSceneObject> Objects { get { RequireAlive(); return content.Composition.Objects; } }
    public IReadOnlyList<SceneActorPlaybackState> Actors { get { RequireAlive(); return animation.Actors; } }
    public SceneLightingSnapshot Lighting { get { RequireAlive(); return content.Lighting.Snapshot; } }
    /// <summary>Read-only effective purpose for a real scene instance. Shares the rendering resolver and
    /// never opens a transaction, rebuilds physics or creates an Undo entry.</summary>
    public ModelPurpose GetModelPurpose(Guid objectId)
    {
        RequireAlive();
        var item = content.Composition.Objects.FirstOrDefault(value => value.Id == objectId)
            ?? throw new ArgumentException("Unknown scene object.", nameof(objectId));
        return SceneModelShadingPolicy.ResolvePurpose(item, content.Lighting.ModelRoles);
    }
    public ProjectSettings Settings { get { RequireAlive(); return content.Settings; } }
    public IReadOnlyList<SceneMediaTrack> Media { get { RequireAlive(); return Array.AsReadOnly(content.Media); } }
    public IReadOnlyList<SceneOutsideParentBinding> OutsideParents { get { RequireAlive(); return Array.AsReadOnly(content.Parents); } }
    public ScenePmmControlState? PmmControls { get { RequireAlive(); return content.PmmControls; } }
    public double CurrentFrame { get { RequireAlive(); return animation.CurrentFrame; } }
    public uint DurationFrames { get { RequireAlive(); return animation.DurationFrames; } }
    public bool CanUndo { get { RequireAlive(); return undo.Count != 0; } }
    public bool CanRedo { get { RequireAlive(); return redo.Count != 0; } }
    public IReadOnlyList<string> CleanupDiagnostics => Array.AsReadOnly(cleanupDiagnostics.ToArray());
    /// <summary>Samples the single animation clock exactly as an IEEE-754 rational; no independent media clock can drift.
    /// Actual media decoding and output device latency remain the host's responsibility.</summary>
    public IReadOnlyList<SceneMediaSample> SampleMedia(bool isPlaying)
    {
        RequireIdle();
        var clock = new SceneMediaClock(new(content.Settings.FpsNumerator, content.Settings.FpsDenominator));
        long bits = BitConverter.DoubleToInt64Bits(animation.CurrentFrame);
        int exponent = (int)((bits >> 52) & 2047);
        BigInteger mantissa = bits & 0x000fffffffffffffL;
        if (exponent != 0) mantissa += BigInteger.One << 52;
        int shift = exponent == 0 ? -1074 : exponent - 1023 - 52;
        clock.Seek(shift >= 0 ? new(mantissa << shift, 1) : new(mantissa, BigInteger.One << -shift));
        if (isPlaying) clock.Play();
        return Array.AsReadOnly(content.Media.Select(clock.Sample).ToArray());
    }

    public void Edit(Action<SceneProjectEdit> edit, CancellationToken cancellationToken = default)
        => EditChecked(edit, null, cancellationToken);

    /// <summary>Prepares and validates a candidate before the shared transaction publishes it.
    /// A failed postcondition releases only the candidate; live solvers and both history stacks survive.</summary>
    private void EditChecked(Action<SceneProjectEdit> edit, Action<SceneAnimationSession>? validateCandidate,
        CancellationToken cancellationToken)
    {
        RequireIdle(); ArgumentNullException.ThrowIfNull(edit); cancellationToken.ThrowIfCancellationRequested();
        editing = true;
        try
        {
            var detached = ToEdit(content); edit(detached);
            DetachLightsFromRemovedParents(detached);
            if (validateCandidate is null && TryFreezeLightingOnly(detached, out var lightingOnly))
            {
                animation.UpdateLighting(lightingOnly.Lighting.Snapshot, cancellationToken);
                undo.Push(content); redo.Clear(); content = lightingOnly;
                return;
            }
            var next = Freeze(detached);
            var candidate = Prepare(next, animation.CurrentFrame, cancellationToken);
            try
            {
                if (validateCandidate is not null)
                {
                    validateCandidate(candidate);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            catch
            {
                try { candidate.Dispose(); }
                catch (Exception error) { cleanupDiagnostics.Add("Rejected project candidate cleanup failed: " + error.Message); }
                cleanupDiagnostics.AddRange(candidate.CleanupDiagnostics);
                throw;
            }
            undo.Push(content); redo.Clear(); Publish(next, candidate);
        }
        finally { editing = false; }
    }
    public bool Undo(CancellationToken cancellationToken = default) => Restore(undo, redo, cancellationToken);
    public bool Redo(CancellationToken cancellationToken = default) => Restore(redo, undo, cancellationToken);
    private bool Restore(Stack<Content> source, Stack<Content> destination, CancellationToken token)
    {
        RequireIdle(); token.ThrowIfCancellationRequested(); if (!source.TryPeek(out var next)) return false;
        if (ReferenceEquals(next.AnimationIdentity, content.AnimationIdentity))
        {
            animation.UpdateLighting(next.Lighting.Snapshot, token);
            source.Pop(); destination.Push(content); content = next; return true;
        }
        var candidate = Prepare(next, animation.CurrentFrame, token);
        source.Pop(); destination.Push(content); Publish(next, candidate); return true;
    }
    private void Publish(Content next, SceneAnimationSession candidate)
    {
        var old = animation; content = next; animation = candidate;
        try { old.Dispose(); }
        catch (Exception error) { cleanupDiagnostics.Add("Previous project cleanup failed: " + error.Message); }
        cleanupDiagnostics.AddRange(old.CleanupDiagnostics);
    }
    public OriginalFrameScene Seek(double frame, CancellationToken cancellationToken = default)
    {
        RequireIdle();
        if (!double.IsFinite(frame) || frame < 0 || frame > uint.MaxValue || frame != Math.Floor(frame))
            throw new ArgumentOutOfRangeException(nameof(frame), "Seek requires a nonnegative integer VMD frame.");
        if (physics.PoseInitializationSubsteps == 0) return animation.Seek(frame, cancellationToken);
        // A fresh graph makes the initialized pose independent of playback and prior seeks. Preparing
        // can fail or be cancelled without changing the live solver, clock or project history.
        Publish(content, Prepare(content, frame, cancellationToken, animation.PublishedTextureSource));
        return animation.Frame;
    }
    public OriginalFrameScene Advance(double elapsedSeconds, CancellationToken cancellationToken = default)
    { RequireIdle(); return animation.Advance(elapsedSeconds, cancellationToken); }
    public void AdvancePreview(double elapsedSeconds, CancellationToken cancellationToken = default)
    { RequireIdle(); animation.AdvancePreview(elapsedSeconds, cancellationToken); }
    public void Save(string path, SceneProjectSaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        RequireIdle(); cancellationToken.ThrowIfCancellationRequested();
        RequireAccessoryPersistenceSupport();
        if (content.RenderingStyles.NeedsPersistence) throw new NotSupportedException("Rendering style selections require PMA. Save as .pma to retain this metadata.");
        SceneAnimationProjectStore.Save(path, content.Composition, content.Lighting, animation,
            content.Settings, content.Media, options ?? new(), cancellationToken);
    }
    public static SceneProjectSession Load(string path, Func<string, CancellationToken, SceneAnimationModelAsset> loadModel,
        IProjectPhysicsBackendProvider physics, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(physics);
        var prepared = SceneAnimationProjectStore.Load(path, loadModel, physics, cancellationToken);
        try { return new(prepared, physics, cancellationToken); }
        catch { prepared.Dispose(); throw; }
    }
    private SceneAnimationSession Prepare(Content next, double frame, CancellationToken token, SceneSnapshot? initialTextureSource = null)
    {
        token.ThrowIfCancellationRequested();
        ICharacterPhysicsBackend Independent(EditableSceneObject item)
        {
            var backend = physics.CreateIndependent(next.Settings.Physics, item);
            if (animation?.UsesPhysicsBackend(backend) == true)
                throw new InvalidOperationException("A project candidate requires a fresh physics backend; its factory returned an active solver.");
            return backend;
        }
        ICharacterPhysicsMutableBackend Shared()
        {
            var backend = physics.CreateShared(next.Settings.Physics);
            if (animation?.UsesPhysicsBackend(backend) == true)
                throw new InvalidOperationException("A project candidate requires a fresh shared backend; its factory returned an active solver.");
            return backend;
        }
        ISoftBodyBackend Soft(EditableSceneObject item)
        {
            var backend = physics.CreateSoftBody(next.Settings.Physics, item);
            if (animation?.UsesSoftPhysicsBackend(backend) == true)
                throw new InvalidOperationException("A project candidate requires a fresh soft backend; its factory returned an active solver.");
            return backend;
        }
        // Capture one host policy for both deferred construction and immediate initialization.
        // A custom provider's property need not return the same value on a second read.
        int initializationSubsteps = physics.PoseInitializationSubsteps;
        SceneAnimationSession candidate;
        using (PoseEvaluationMetrics.Measure("project.prepare.constructor"))
        candidate = new SceneAnimationSession(next.Layout, next.Motions, Independent,
            next.Lighting.Snapshot, token, frame, next.Parents, next.SceneMotion,
            next.Settings.Physics.SharedWorld ? Shared : null, next.Settings.Physics.CoupledSoftBodies ? null : Soft,
            sharedWorldGravity: next.Settings.Physics.SharedWorld ? next.Settings.Physics.Gravity : null, pmmControls: next.PmmControls,
            coupledSoftBodies: next.Settings.Physics.CoupledSoftBodies, modelRoles: next.Lighting.ModelRoles,
            deferInitialComposition: initializationSubsteps > 0, initialTextureSource: initialTextureSource,
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
            catch (Exception error) { cleanupDiagnostics.Add("Project initialization cleanup failed: " + error.Message); }
            cleanupDiagnostics.AddRange(candidate.CleanupDiagnostics);
            throw;
        }
    }
    private static SceneProjectEdit ToEdit(Content state) => new(Clone(state.Composition), Clone(state.Lighting),
        state.Motions, state.Parents, state.SceneMotion, state.Settings, state.Media, state.PmmControls, state.Accessories) { RenderingStyleState = state.RenderingStyles };
    private static Content Freeze(SceneProjectEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit.Settings); edit.Settings.Validate();
        // Clone again after the callback: the caller may retain the detached edit object.
        var layout = Clone(edit.Composition); var lights = Clone(edit.Lighting);
        var validatedLayout = ValidatedSceneLayout.Create(layout);
        if (edit.Motions.Any(m => m is null) || edit.Motions.Select(m => m.ObjectId).Distinct().Count() != edit.Motions.Count)
            throw new ArgumentException("Project motion bindings must have unique identities.");
        var ids = layout.Objects.Select(o => o.Id).ToHashSet();
        edit.RenderingStyleState.ValidateObjects(ids);
        if (edit.Motions.Any(m => !ids.Contains(m.ObjectId))) throw new ArgumentException("Motion belongs to a removed scene object.");
        if (edit.Media.Any(m => m is null) || edit.Media.Select(m => m.Id).Distinct().Count() != edit.Media.Count)
            throw new ArgumentException("Media tracks must have unique identities.");
        if (edit.Media.Count > 128 || edit.Media.DistinctBy(m => m.Asset.Sha256).Sum(m => (long)m.Asset.Length) > SceneAnimationProjectStore.MaxMediaBytes)
            throw new InvalidDataException("Project media exceeds the save contract budget.");
        // Validate resource/time contracts even if a media decoder has not been attached by the host.
        var clock = new SceneMediaClock(new OfflineFrameRate(edit.Settings.FpsNumerator, edit.Settings.FpsDenominator));
        foreach (var track in edit.Media) _ = clock.Sample(track);
        edit.PmmControls?.ValidateActors(edit.Motions.ToDictionary(m => m.ObjectId, m => m.Model));
        var accessories = FreezeAccessories(edit.Accessories, ids, edit.PmmControls);
        return new(layout, lights, edit.Motions.ToArray(), edit.OutsideParents.ToArray(), edit.SceneMotion, edit.Settings, edit.Media.ToArray(), edit.PmmControls, edit.RenderingStyleState, validatedLayout, accessories);
    }
    private static SceneCompositionEditor Clone(SceneCompositionEditor source)
    { ArgumentNullException.ThrowIfNull(source); return SceneCompositionEditor.FromObjects(source.Objects, source.SelectedId); }
    private static SceneLightingEditor Clone(SceneLightingEditor source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.IsEditing) throw new InvalidOperationException("Commit or cancel the light gesture before editing the project.");
        var copy = new SceneLightingEditor();
        if (!copy.TryLoadSidecar(source.SerializeSidecar(), out string? error)) throw new InvalidDataException(error);
        return copy;
    }
    private void RequireAlive() => ObjectDisposedException.ThrowIf(disposed, this);
    private void RequireIdle()
    { RequireAlive(); if (editing) throw new InvalidOperationException("A project transaction cannot be nested or evaluated while preparing."); }
    internal void RequireDisposalAllowed()
    { if (editing) throw new InvalidOperationException("Cannot dispose the owner inside its edit transaction."); }
    public void Dispose()
    {
        if (disposed) return;
        RequireDisposalAllowed();
        disposed = true; animation.Dispose(); undo.Clear(); redo.Clear();
    }
}
