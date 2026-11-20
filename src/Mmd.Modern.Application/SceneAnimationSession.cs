using System.Collections.ObjectModel;
using System.Numerics;
using System.Runtime.ExceptionServices;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

public sealed record SceneActorMotion(Guid ObjectId, PmxModel Model, VmdMotion Motion,
    bool IkEnabled = true, bool MorphsEnabled = true, bool PhysicsEnabled = true, SceneMotionAsset? Asset = null)
{
    public SceneMotionDetachment? Detachment { get; init; }
    internal VmdMotion RuntimeMotion => Detachment?.Filter(Motion) ?? Motion;
}
public sealed record SceneActorPlaybackState(Guid ObjectId, bool HasMotion, uint DurationFrames, double LocalFrame,
    bool IkEnabled, bool MorphsEnabled, bool SupportsPhysics, bool PhysicsEnabled, string? PhysicsBackend, IReadOnlyList<string> Diagnostics)
{
    public bool Visible { get; init; } = true;
}
public sealed class SceneActorAnimationException(Guid objectId, string operation, Exception inner)
    : InvalidOperationException($"Scene actor {objectId} failed during {operation}: {inner.Message}", inner)
{
    public Guid ObjectId { get; } = objectId;
    public string Operation { get; } = operation;
}

/// <summary>Single-owner application session over fixed, ordered static sources. Owns controllers and their
/// independent model-space physics backends; publishes neutral frames. Source layouts are never modified.</summary>
public sealed partial class SceneAnimationSession : IDisposable
{
    public const double FramesPerSecond = 30;
    private sealed record Entry(EditableSceneObject Object, SceneActorMotion? Motion, CharacterAnimationController? Controller,
        ICharacterPhysicsBackend? Backend, OriginalFrameScene LocalFrame);
    private Entry[] entries = [];
    private readonly Func<EditableSceneObject, ICharacterPhysicsBackend> physicsFactory;
    private readonly Func<EditableSceneObject, ISoftBodyBackend>? softPhysicsFactory;
    private readonly bool coupledSoftBodies;
    private SceneLightingSnapshot lighting;
    private readonly IReadOnlyDictionary<string, ModelPurpose>? modelRoles;
    private OriginalFrameScene published = null!;
    private readonly ProjectSceneAssembler.CompositionCache compositionCache;
    private readonly Dictionary<Guid, (EditableSceneObject Object, OriginalFrameScene Local, OriginalFrameScene Placed)> placedFrames = [];
    private static readonly IReadOnlyList<string> EmptyPlaybackDiagnostics = Array.AsReadOnly(Array.Empty<string>());
    private static readonly IReadOnlyDictionary<SceneLightParent, Matrix4x4> EmptyLightParents = new Dictionary<SceneLightParent, Matrix4x4>();
    private readonly List<Guid> composeIds = [];
    private readonly List<OriginalFrameScene> composeSources = [];
    private readonly List<OriginalFrameScene> composePlaced = [];
    private IReadOnlyList<SceneActorPlaybackState> states = Array.Empty<SceneActorPlaybackState>();
    private bool disposed;
    private bool deferVertexSkinning;
    /// <summary>Selected by a host whose renderer skins on the GPU (the modern engine, for preview and export alike).
    /// Published actor ranges then carry deformation inputs instead of CPU-skinned vertices; vertex reads still
    /// evaluate the exact CPU reference. Soft-body models always keep CPU skinning. Takes effect from the next sample.</summary>
    public bool DeferVertexSkinningToRenderer
    {
        get => deferVertexSkinning;
        set
        {
            RequireAlive();
            deferVertexSkinning = value;
            foreach (var entry in entries) if (entry.Controller is { } controller) controller.DeferVertexSkinning = value;
            states = CaptureStates(entries, CurrentFrame);
        }
    }
    private readonly List<string> cleanupDiagnostics = [];
    /// <summary>Cleanup failures are reported separately; they cannot invalidate a successfully committed replacement.</summary>
    public IReadOnlyList<string> CleanupDiagnostics => Array.AsReadOnly(cleanupDiagnostics.ToArray());

    public SceneAnimationSession(IEnumerable<EditableSceneObject> objects, IEnumerable<SceneActorMotion> motions,
        Func<EditableSceneObject, ICharacterPhysicsBackend> physicsFactory, SceneLightingSnapshot? lighting = null,
        CancellationToken cancellationToken = default, double initialFrame = 0, IEnumerable<SceneOutsideParentBinding>? outsideParents = null,
        SceneMotionAsset? sceneMotion = null, Func<ICharacterPhysicsMutableBackend>? sharedPhysicsFactory = null,
        Func<EditableSceneObject, ISoftBodyBackend>? softPhysicsFactory = null,
        Vector3? sharedWorldGravity = null, ScenePmmControlState? pmmControls = null, bool coupledSoftBodies = false,
        IReadOnlyDictionary<string, ModelPurpose>? modelRoles = null)
        : this(objects, null, motions, physicsFactory, lighting, cancellationToken, initialFrame, outsideParents,
            sceneMotion, sharedPhysicsFactory, softPhysicsFactory, sharedWorldGravity, pmmControls, coupledSoftBodies, modelRoles, false, null, false)
    { }

    internal SceneAnimationSession(ValidatedSceneLayout layout, IEnumerable<SceneActorMotion> motions,
        Func<EditableSceneObject, ICharacterPhysicsBackend> physicsFactory, SceneLightingSnapshot? lighting = null,
        CancellationToken cancellationToken = default, double initialFrame = 0, IEnumerable<SceneOutsideParentBinding>? outsideParents = null,
        SceneMotionAsset? sceneMotion = null, Func<ICharacterPhysicsMutableBackend>? sharedPhysicsFactory = null,
        Func<EditableSceneObject, ISoftBodyBackend>? softPhysicsFactory = null,
        Vector3? sharedWorldGravity = null, ScenePmmControlState? pmmControls = null, bool coupledSoftBodies = false,
        IReadOnlyDictionary<string, ModelPurpose>? modelRoles = null, bool deferInitialComposition = false,
        SceneSnapshot? initialTextureSource = null, bool deferVertexSkinning = false)
        : this(null, layout ?? throw new ArgumentNullException(nameof(layout)), motions, physicsFactory, lighting,
            cancellationToken, initialFrame, outsideParents, sceneMotion, sharedPhysicsFactory, softPhysicsFactory,
            sharedWorldGravity, pmmControls, coupledSoftBodies, modelRoles, deferInitialComposition, initialTextureSource, deferVertexSkinning)
    { }

    private SceneAnimationSession(IEnumerable<EditableSceneObject>? objects, ValidatedSceneLayout? validatedLayout,
        IEnumerable<SceneActorMotion> motions, Func<EditableSceneObject, ICharacterPhysicsBackend> physicsFactory,
        SceneLightingSnapshot? lighting, CancellationToken cancellationToken, double initialFrame,
        IEnumerable<SceneOutsideParentBinding>? outsideParents, SceneMotionAsset? sceneMotion,
        Func<ICharacterPhysicsMutableBackend>? sharedPhysicsFactory, Func<EditableSceneObject, ISoftBodyBackend>? softPhysicsFactory,
        Vector3? sharedWorldGravity, ScenePmmControlState? pmmControls, bool coupledSoftBodies,
        IReadOnlyDictionary<string, ModelPurpose>? modelRoles, bool deferInitialComposition, SceneSnapshot? initialTextureSource,
        bool deferVertexSkinning)
    {
        this.deferVertexSkinning = deferVertexSkinning;
        if (validatedLayout is null) ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(motions);
        compositionCache = new(initialTextureSource);
        this.physicsFactory = physicsFactory ?? throw new ArgumentNullException(nameof(physicsFactory));
        this.sharedPhysicsFactory = sharedPhysicsFactory;
        this.softPhysicsFactory = softPhysicsFactory;
        this.coupledSoftBodies = coupledSoftBodies;
        if (coupledSoftBodies && softPhysicsFactory is not null)
            throw new ArgumentException("Select coupled soft content or an independent soft factory.", nameof(softPhysicsFactory));
        this.pmmControls = pmmControls;
        // Explicit opt-in for soft proxy worlds; omitted values preserve custom factory gravity.
        if (sharedWorldGravity is { } gravity && (!float.IsFinite(gravity.X) || !float.IsFinite(gravity.Y)
            || !float.IsFinite(gravity.Z) || sharedPhysicsFactory is null))
            throw new ArgumentException("Shared soft-world gravity must be finite and requires a shared rigid factory.", nameof(sharedWorldGravity));
        this.sharedWorldGravity = sharedWorldGravity;
        this.lighting = lighting ?? SceneLightingSnapshot.Empty;
        this.modelRoles = modelRoles is null ? null : new Dictionary<string, ModelPurpose>(modelRoles, StringComparer.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(initialFrame) || initialFrame < 0 || initialFrame > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(initialFrame));
        sceneTracks = PrepareSceneMotion(sceneMotion, initialFrame);
        this.sceneMotion = sceneMotion;
        // Only frozen project content can supply this proof. Public callers still validate the full
        // source layout before numerical backends exist; mutable binding.Model is checked below on every PrepareEntry.
        var layout = validatedLayout ?? ValidatedSceneLayout.Create(objects!);
        var staticObjects = layout.Objects;
        entries = new Entry[staticObjects.Length];
        for (int index = 0; index < staticObjects.Length; index++)
        {
            var item = staticObjects[index];
            entries[index] = new(item, null, null, null, item.Source);
        }
        var bindings = motions.ToArray();
        if (bindings.Any(binding => binding is null) || bindings.Select(binding => binding.ObjectId).Distinct().Count() != bindings.Length)
            throw new ArgumentException("Motion bindings must have unique object identities.", nameof(motions));
        foreach (var binding in bindings) _ = Index(binding.ObjectId);
        outsideBindings = outsideParents?.ToArray() ?? [];
        var validationEntries = entries.Select(entry => entry with { Motion = bindings.SingleOrDefault(binding => binding.ObjectId == entry.Object.Id) }).ToArray();
        ValidateLightParents(validationEntries, this.lighting);
        using (PoseEvaluationMetrics.Measure("session.constructor.control-validation"))
            ValidateControls(validationEntries);
        var initialParents = ControlParents(initialFrame);
        int[] order = ValidateOutsideParents(initialParents, validationEntries);
        PreparedController?[]? preparedControllers = null;
        try
        {
            preparedControllers = PrepareInitialControllers(validationEntries, order, cancellationToken);
            foreach (int index in order)
            {
                var binding = validationEntries[index].Motion;
                if (binding is null) continue;
                using var entryMeasure = PoseEvaluationMetrics.Measure("session.constructor.prepare-entry", binding.ObjectId);
                var entryParents = CombineWithGroundParents(entries[index].Object, binding.Model,
                    EffectiveOutsideParents(binding.ObjectId, entries, initialParents), ControlGroundParents(binding.ObjectId, initialFrame));
                entries[index] = PrepareEntry(entries[index].Object, binding, initialFrame, cancellationToken,
                    entryParents, entries, preparedControllers?[index]);
            }
            using (PoseEvaluationMetrics.Measure("session.constructor.create-worlds"))
                sharedWorld = CreateSessionWorlds(entries, initialFrame);
            if (pmmControls is not null)
                using (PoseEvaluationMetrics.Measure("session.constructor.pmm-reset"))
                    EvaluateSharedBoundary(entries, sharedWorld!, initialFrame, initialFrame, reset: true, step: false, cancellationToken);
            // Only the internal project Prepare path may defer this unpublished frame; it immediately
            // runs a positive stabilization budget before returning the candidate. Controller samples
            // and world resets above are still complete. Static/disabled graphs return early from
            // stabilization and therefore must retain the ordinary immediate composition here.
            if (!deferInitialComposition || !entries.Any(entry => entry.Controller?.PhysicsEnabled == true))
            {
                using (PoseEvaluationMetrics.Measure("session.constructor.compose"))
                    published = Compose(entries, null);
                states = CaptureStates(entries, initialFrame);
            }
            CurrentFrame = initialFrame;
            DurationFrames = Duration(entries);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch { Dispose(); throw; }
        finally { ReleasePreparedControllers(preparedControllers, order); }
    }
    public OriginalFrameScene Frame { get { RequireAlive(); if (!IsFaulted) CompletePreviewLeaves(); return published; } }
    // A seek only needs the last published textures. Reading Frame would also finish any pending pose.
    internal SceneSnapshot PublishedTextureSource => published.Surface.Snapshot;
    internal OriginalFrameScene GetFrame(CancellationToken token)
    { RequireHealthyWithoutPreviewCompletion(); CompletePreviewLeaves(token); return published; }
    public IReadOnlyList<SceneActorPlaybackState> Actors { get { RequireAlive(); if (!IsFaulted) CompletePreviewLeaves(); return states; } }
    /// <summary>Maximum imported motion length. Seek may inspect a later integer frame; actor poses still clamp.</summary>
    public uint DurationFrames { get; private set; }
    public double CurrentFrame { get; private set; }
    public bool IsFaulted { get; private set; }
    /// <summary>The first failed evaluation that may have changed the owned solver state.
    /// Retained until an explicit recovery succeeds, so later guards cannot hide the original cause.</summary>
    public Exception? FaultCause { get; private set; }

    private void MarkFault(Exception error) { FaultCause ??= error; IsFaulted = true; }
    private void ClearFault() { IsFaulted = false; FaultCause = null; }

    internal sealed record SaveSnapshot(IReadOnlyList<EditableSceneObject> Objects, IReadOnlyList<SceneActorMotion> Motions,
        SceneLightingSnapshot Lighting, double CurrentFrame, IReadOnlyList<SceneOutsideParentBinding> OutsideParents, SceneMotionAsset? SceneMotion, ScenePmmControlState? PmmControls,
        bool CoupledSoftBodies, bool SharedPhysicsWorld);
    internal SaveSnapshot CaptureSaveSnapshot()
    {
        RequireHealthyWithoutPreviewCompletion();
        return new(Array.AsReadOnly(entries.Select(entry => entry.Object).ToArray()),
            Array.AsReadOnly(entries.Where(entry => entry.Motion is not null).Select(entry => entry.Motion!).ToArray()), lighting, CurrentFrame, OutsideParents, sceneMotion, pmmControls,
            coupledSoftBodies, sharedPhysicsFactory is not null);
    }

    /// <summary>Absolute integer-frame seek; each bound actor restarts physical state at its clamped pose.</summary>
    public OriginalFrameScene Seek(double frame, CancellationToken cancellationToken = default)
    {
        RequireAlive();
        if (!double.IsFinite(frame) || frame < 0 || frame > uint.MaxValue || frame != Math.Floor(frame))
            throw new ArgumentOutOfRangeException(nameof(frame), "Seek requires a nonnegative integer VMD frame.");
        if (coupledSoftBodies) return SeekCoupledGraph(frame, cancellationToken);
        return Evaluate(frame, 0, true, cancellationToken);
    }

    /// <summary>One shared 30fps clock. Finished actors, including their physical pose, remain frozen.</summary>
    public OriginalFrameScene Advance(double elapsedSeconds, CancellationToken cancellationToken = default)
    {
        RequireHealthyWithoutPreviewCompletion(); cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0 || elapsedSeconds > 86400)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        CompletePreviewLeaves(cancellationToken);
        double target = Math.Max(CurrentFrame, Math.Min(DurationFrames, CurrentFrame + elapsedSeconds * FramesPerSecond));
        if (target == CurrentFrame) return published;
        // Reject unsupported soft catch-up before any actor can mutate its existing solver.
        if (entries.Any(entry => entry.Controller is { SoftBodiesEnabled: true } controller
            && (Math.Min(target, controller.DurationFrames) - Math.Min(CurrentFrame, controller.DurationFrames)) / FramesPerSecond > 10))
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds), "Soft-body playback supports at most ten seconds per advance; split larger clock advances.");
        return Evaluate(target, (target - CurrentFrame) / FramesPerSecond, false, cancellationToken);
    }

    // Several clock advances may precede a render capture. Keep the solver clock, but defer
    // only independent visual leaves until an actual consumer reads them.
    internal void AdvancePreview(double elapsedSeconds, CancellationToken cancellationToken = default)
    {
        RequireAlive();
        if (IsFaulted) throw new InvalidOperationException("The session failed during evaluation; perform a successful Seek before continuing.", FaultCause);
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0 || elapsedSeconds > 86400)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        double target = Math.Max(CurrentFrame, Math.Min(DurationFrames, CurrentFrame + elapsedSeconds * FramesPerSecond));
        if (target == CurrentFrame) return;
        // Only the existing rigid shared/PMM orchestration has the boundary guarantees needed
        // for this optimization. All other paths retain their established full evaluation.
        if ((pmmControls is null && sharedWorld is null) || coupledSoftBodies
            || entries.Any(entry => entry.Controller?.SoftBodiesEnabled == true || entry.Motion?.Model.SoftBodies.Count is > 0))
        {
            CompletePreviewLeaves(cancellationToken);
            _ = Advance(elapsedSeconds, cancellationToken);
            return;
        }
        previewWorking = new(previewLeaves);
        try { _ = EvaluateShared(target, (target - CurrentFrame) / FramesPerSecond, false, cancellationToken); }
        finally { previewWorking = null; }
    }

    /// <summary>Prepare, sample and compose a candidate before replacing the old actor. Others retain their solver state.</summary>
    public void BindMotion(SceneActorMotion motion, CancellationToken cancellationToken = default)
    {
        RequireProjectControlEdit();
        RequireHealthyWithoutPreviewCompletion(); ArgumentNullException.ThrowIfNull(motion); cancellationToken.ThrowIfCancellationRequested();
        int index = Index(motion.ObjectId); CompletePreviewLeaves(cancellationToken);
        if (sharedWorld is not null) { BindSharedMotion(index, motion, cancellationToken); return; }
        bool modelChanged = !ReferenceEquals(entries[index].Motion?.Model, motion.Model);
        if (outsideBindings.Length != 0)
        {
            var motions = entries.Where(entry => entry.Motion is not null && entry.Object.Id != motion.ObjectId).Select(entry => entry.Motion!).Append(motion).ToArray();
            ReplaceGraph(outsideBindings, motions, cancellationToken);
            if (modelChanged) { outsideUndo.Clear(); outsideRedo.Clear(); }
            return;
        }
        var previous = entries[index];
        var candidate = PrepareEntry(entries[index].Object, motion, CurrentFrame, cancellationToken);
        try
        {
            var next = (Entry[])entries.Clone(); next[index] = candidate;
            var frame = Compose(next, null); var nextStates = CaptureStates(next, CurrentFrame);
            cancellationToken.ThrowIfCancellationRequested();
            entries = next; published = frame; states = nextStates; DurationFrames = Duration(next);
        }
        catch { Release(candidate); throw; }
        if (modelChanged) { outsideUndo.Clear(); outsideRedo.Clear(); }
        Release(previous);
    }

    public void ClearMotion(Guid objectId)
    {
        RequireProjectControlEdit();
        RequireHealthyWithoutPreviewCompletion(); int index = Index(objectId); var previous = entries[index];
        if (outsideBindings.Any(binding => binding.ObjectId == objectId || binding.ParentObjectId == objectId))
            throw new InvalidOperationException("Remove outside-parent relationships before clearing this model's motion.");
        if (previous.Motion is null) return;
        CompletePreviewLeaves(); previous = entries[index];
        var next = (Entry[])entries.Clone(); next[index] = new(previous.Object, null, null, null, previous.Object.Source);
        var frame = Compose(next, null); var nextStates = CaptureStates(next, CurrentFrame);
        if (sharedWorld?.ActorIds.Contains(objectId) == true)
        {
            try { sharedWorld.RemoveActor(objectId); }
            catch (Exception error) { MarkFault(error); sharedStatus!.IsFaulted=true; throw; }
        }
        entries = next; published = frame; states = nextStates; DurationFrames = Duration(next);
        outsideUndo.Clear(); outsideRedo.Clear();
        Release(previous);
    }

    /// <summary>Reconfigures only the selected actor, restarting its physical pose at the current frame.</summary>
    public void SetActorOptions(Guid objectId, bool ik, bool morphs, bool physics, CancellationToken cancellationToken = default)
    {
        RequireHealthyWithoutPreviewCompletion(); cancellationToken.ThrowIfCancellationRequested();
        var binding = entries[Index(objectId)].Motion ?? throw new InvalidOperationException("Bind a motion before changing actor options.");
        if (binding.IkEnabled == ik && binding.MorphsEnabled == morphs && binding.PhysicsEnabled == physics) return;
        BindMotion(binding with { IkEnabled = ik, MorphsEnabled = morphs, PhysicsEnabled = physics }, cancellationToken);
    }

    private OriginalFrameScene Evaluate(double frame, double elapsed, bool reset, CancellationToken token)
    {
        if (pmmControls is not null || sharedWorld is not null) return EvaluateShared(frame, elapsed, reset, token);
        token.ThrowIfCancellationRequested();
        var next = (Entry[])entries.Clone(); bool changed = false;
        try
        {
            if (CanEvaluateInParallel(next, frame, reset, token, out var workIndices))
            {
                int degree = Math.Min(workIndices.Count, Math.Clamp(Environment.ProcessorCount, 1, 16));
                var localFrames = new OriginalFrameScene?[workIndices.Count];
                var errors = new ExceptionDispatchInfo?[workIndices.Count];
                Parallel.For(0, workIndices.Count, new ParallelOptions
                {
                    MaxDegreeOfParallelism = degree,
                    TaskScheduler = TaskScheduler.Default,
                }, w =>
                {
                    int i = workIndices[w];
                    var entry = next[i];
                    try
                    {
                        if (TryEvaluateEntry(entry, frame, elapsed, reset, parents: null, frozenAttachment: false, out var localFrame))
                            localFrames[w] = localFrame;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        errors[w] = ExceptionDispatchInfo.Capture(
                            new SceneActorAnimationException(entry.Object.Id, reset ? "seek" : "advance", exception));
                    }
                    catch (Exception exception)
                    {
                        errors[w] = ExceptionDispatchInfo.Capture(exception);
                    }
                });
                for (int w = 0; w < workIndices.Count; w++)
                {
                    errors[w]?.Throw();
                    if (localFrames[w] is { } localFrame)
                    {
                        int i = workIndices[w];
                        next[i] = next[i] with { LocalFrame = localFrame };
                        changed = true;
                    }
                }
            }
            else if (outsideBindings.Length == 0)
            {
                for (int i = 0; i < next.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var entry = next[i]; var controller = entry.Controller;
                    if (controller is null) continue;
                    double local = Math.Min(frame, controller.DurationFrames);
                    double prior = Math.Min(CurrentFrame, controller.DurationFrames);
                    if (!reset && local <= prior) continue;
                    try
                    {
                        if (TryEvaluateEntry(entry, frame, elapsed, reset, parents: null, frozenAttachment: false, out var localFrame))
                        {
                            next[i] = entry with { LocalFrame = localFrame! }; changed = true;
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    { throw new SceneActorAnimationException(entry.Object.Id, reset ? "seek" : "advance", exception); }
                }
            }
            else
            {
                foreach (int i in ValidateOutsideParents(outsideBindings, next))
                {
                    token.ThrowIfCancellationRequested(); var entry = next[i]; var controller = entry.Controller;
                    if (controller is null) continue;
                    double local = Math.Min(frame, controller.DurationFrames);
                    double prior = Math.Min(CurrentFrame, controller.DurationFrames);
                    bool attached = HasOutsideBinding(outsideBindings, entry.Object.Id);
                    if (!reset && local <= prior && !attached) continue;
                    try
                    {
                        var parents = attached ? EffectiveOutsideParents(entry.Object.Id, next, outsideBindings) : null;
                        bool frozenAttachment = !reset && attached && local <= prior
                            && !controller.RequiresFullEvaluationForFrozenOutsideParents(parents, trackWindowChanged: false);
                        if (TryEvaluateEntry(entry, frame, elapsed, reset, parents, frozenAttachment, out var localFrame))
                        {
                            next[i] = entry with { LocalFrame = localFrame! }; changed = true;
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    { throw new SceneActorAnimationException(entry.Object.Id, reset ? "seek" : "advance", exception); }
                }
            }
            var composed = changed ? Compose(next, published) : published;
            var nextStates = CaptureStates(next, frame);
            token.ThrowIfCancellationRequested();
            entries = next; published = composed; states = nextStates; CurrentFrame = frame; ClearFault();
            return published;
        }
        catch (Exception error) { MarkFault(error); throw; } // Controllers may already have advanced. Only a successful Seek can recover.
    }

    private bool CanEvaluateInParallel(Entry[] next, double frame, bool reset, CancellationToken token, out List<int> workIndices)
    {
        workIndices = [];
        if (token.CanBeCanceled || outsideBindings.Length != 0 || PoseEvaluationEventSource.Log.IsEnabled()) return false;
        int maxDegree = Math.Clamp(Environment.ProcessorCount, 1, 16);
        if (maxDegree < 2) return false;
        var controllers = new HashSet<CharacterAnimationController>(ReferenceEqualityComparer.Instance);
        var models = new HashSet<PmxModel>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < next.Length; i++)
        {
            var entry = next[i];
            if (entry.Controller is not { } controller) continue;
            if (controller.SoftBodiesEnabled) return false;
            double local = Math.Min(frame, controller.DurationFrames);
            double prior = Math.Min(CurrentFrame, controller.DurationFrames);
            if (!reset && local <= prior) continue;
            if (!controller.SupportsConcurrentIndependentPhysics || !controllers.Add(controller)
                || entry.Motion is not { } motion
                || (models.Add(motion.Model) && !StandardPreviewModel(motion.Model)))
                return false;
            workIndices.Add(i);
        }
        return workIndices.Count >= 2;
    }

    private static bool HasOutsideBinding(SceneOutsideParentBinding[] bindings, Guid objectId)
    {
        for (int i = 0; i < bindings.Length; i++)
            if (bindings[i].ObjectId == objectId) return true;
        return false;
    }

    private bool TryEvaluateEntry(Entry entry, double frame, double elapsed, bool reset,
        IReadOnlyDictionary<int, Matrix4x4>? parents, bool frozenAttachment, out OriginalFrameScene? localFrame)
    {
        var controller = entry.Controller!;
        double local = Math.Min(frame, controller.DurationFrames);
        double prior = Math.Min(CurrentFrame, controller.DurationFrames);
        if (parents is not null && !frozenAttachment) controller.SetOutsideParents(parents);
        var motion = entry.Motion!;
        var pose = reset ? controller.ResetPhysics((uint)local, motion.IkEnabled, motion.MorphsEnabled)
            : frozenAttachment ? controller.RefreshFrozenOutsideParents(parents!)
            : controller.Advance(local, Math.Min(elapsed, (local - prior) / FramesPerSecond), motion.IkEnabled, motion.MorphsEnabled);
        if (!ReferenceEquals(pose.Surface, entry.LocalFrame.Surface) || !ReferenceEquals(pose.Outline, entry.LocalFrame.Outline))
        {
            localFrame = new OriginalFrameScene(pose.Surface, pose.Outline, entry.Object.Source.OutlineOptions,
                materials: PmxOriginalFrameAdapter.FromEvaluatedMaterials(motion.Model, pose.Surface, pose.Outline));
            return true;
        }
        localFrame = null;
        return false;
    }

    private Entry PrepareEntry(EditableSceneObject item, SceneActorMotion binding, double frame, CancellationToken token,
        IReadOnlyDictionary<int, Matrix4x4>? parents = null, IReadOnlyList<Entry>? candidates = null,
        PreparedController? prepared = null)
    {
        token.ThrowIfCancellationRequested(); CharacterAnimationController? controller = null;
        try
        {
            if (prepared is null) ValidateControllerModel(item, binding);
            else prepared.ValidationFailure?.Throw();
            var model = binding.Model;
            Vector3? modelSoftGravity = null;
            if (sharedWorldGravity is { } worldGravity && binding.PhysicsEnabled && model.SoftBodies.Count != 0)
            {
                PhysicsMath.ValidateRigid(item.World, nameof(item));
                Matrix4x4.Invert(item.World, out var inversePlacement);
                var localGravity = Vector3.TransformNormal(worldGravity, inversePlacement);
                if (!float.IsFinite(localGravity.X) || !float.IsFinite(localGravity.Y) || !float.IsFinite(localGravity.Z))
                    throw new ArgumentException("Shared gravity cannot be represented as finite actor-local acceleration.", nameof(item));
                modelSoftGravity = localGravity;
            }
            if (prepared is null)
            {
                controller = ConstructController(item, binding);
                ControllerPreparedObserverForTesting?.Invoke(item.Id, controller);
            }
            else controller = prepared.TakeController();
            controller.SetOutsideParentTrackWindows(ControlTrackWindows(item.Id, frame));
            controller.SetOutsideParents(parents);
            controller.DeferVertexSkinning = deferVertexSkinning;
            ICharacterPhysicsBackend? backend = null; double local = Math.Min(frame, controller.DurationFrames);
            if (binding.PhysicsEnabled && (controller.SupportsPhysics || coupledSoftBodies && controller.SupportsSoftBodies) && sharedPhysicsFactory is null && pmmControls is null)
            {
                backend = physicsFactory(item) ?? throw new InvalidOperationException("Physics factory returned no backend.");
                if (entries.Any(entry => ReferenceEquals(entry.Backend, backend)) || candidates?.Any(entry => ReferenceEquals(entry.Backend, backend)) == true)
                    throw new InvalidOperationException("Each actor requires a newly owned physics backend; sharing an active world is invalid.");
                if (coupledSoftBodies && controller.SupportsSoftBodies)
                    controller.EnableCoupledPhysicsAt(backend, local, binding.IkEnabled, binding.MorphsEnabled);
                else controller.EnablePhysicsAt(backend, local, binding.IkEnabled, binding.MorphsEnabled);
            }
            (OriginalStyleScene Surface, OriginalOutlineData Outline) pose;
            using (PoseEvaluationMetrics.Measure("entry.initial-sample", binding.ObjectId))
                pose = controller.SampleAt(local, binding.IkEnabled, binding.MorphsEnabled);
            if(binding.PhysicsEnabled&&controller.SupportsSoftBodies&&!coupledSoftBodies)
            {
                var softBackend=softPhysicsFactory?.Invoke(item)??throw new NotSupportedException("PMX soft bodies require an explicit soft-physics backend factory.");
                if(entries.Any(entry=>entry.Controller?.UsesSoftBodyBackend(softBackend)==true)
                    ||candidates?.Any(entry=>entry.Controller?.UsesSoftBodyBackend(softBackend)==true)==true)
                    throw new InvalidOperationException("Each actor requires a newly owned soft backend; sharing an active soft world is invalid.");
                controller.EnableSoftBodies(softBackend,local,binding.IkEnabled,binding.MorphsEnabled);
                if (modelSoftGravity is { } gravity) controller.SetSoftBodyGravity(gravity);
                pose=controller.SampleAt(local,binding.IkEnabled,binding.MorphsEnabled);
            }
            var candidate = new Entry(item, binding, controller, backend,
                new(pose.Surface, pose.Outline, item.Source.OutlineOptions,
                    materials: PmxOriginalFrameAdapter.FromEvaluatedMaterials(model, pose.Surface, pose.Outline)));
            token.ThrowIfCancellationRequested();
            return candidate;
        }
        catch (OperationCanceledException) { Release(item.Id, controller); throw; }
        catch (Exception exception)
        { Release(item.Id, controller); throw new SceneActorAnimationException(item.Id, "bind", exception); }
    }

    private OriginalFrameScene Compose(Entry[] input, OriginalFrameScene? baseline)
    {
        var resolvedLighting = ResolveLighting(input, lighting);
        int visibleCount = 0;
        for (int i = 0; i < input.Length; i++)
            if (input[i].Controller?.IsVisible ?? true) visibleCount++;
        if (visibleCount == 0)
        {
            compositionCache.Clear(); placedFrames.Clear();
            var source = input[0].LocalFrame;
            return new(new(SceneSnapshot.CreateEmpty(source.Surface.Snapshot.Camera, source.Surface.Snapshot.LightDirection), [], source.Surface.Profile),
                OriginalOutlineData.Empty, source.OutlineOptions, lighting: resolvedLighting);
        }
        composeIds.Clear();
        composeSources.Clear();
        composePlaced.Clear();
        OriginalFrameScene combined;
        OriginalStyleScene first;
        try
        {
            for (int i = 0; i < input.Length; i++)
            {
                var entry = input[i];
                if (!(entry.Controller?.IsVisible ?? true)) continue;
                var placed = PlaceComposeEntry(entry);
                composeIds.Add(entry.Object.Id);
                composeSources.Add(entry.Object.Source);
                composePlaced.Add(placed);
            }
            first = composePlaced[0].Surface;
            // Published states are immutable and still describe the prior frame while controllers advance.
            // Compare identities: equal-sized visible subsets can have different geometry and textures.
            combined = compositionCache.Compose(baseline, composeIds, composeSources, composePlaced,
                first.Profile.LightAmbient, first.Snapshot.LightDirection);
        }
        finally
        {
            composeIds.Clear();
            composeSources.Clear();
            composePlaced.Clear();
        }
        if (ReferenceEquals(combined, baseline)
            && (ReferenceEquals(resolvedLighting, baseline!.Lighting)
                || resolvedLighting.Lights.SequenceEqual(baseline.Lighting?.Lights ?? [])
                    && resolvedLighting.Groups.SequenceEqual(baseline.Lighting?.Groups ?? [])
                    && resolvedLighting.Settings == (baseline.Lighting?.Settings ?? SceneLightingSettings.Default)
                    && resolvedLighting.AuthoredLighting.Lights.SequenceEqual(baseline.Lighting?.AuthoredLighting.Lights ?? [])
                    && resolvedLighting.ParentTransforms.SequenceEqual(baseline.Lighting?.ParentTransforms ?? EmptyLightParents))) return baseline!;
        return new(new(combined.Surface.Snapshot, combined.Surface.Materials, first.Profile), combined.Outline, combined.OutlineOptions,
            materials: combined.Materials, lighting: resolvedLighting);
    }

    private OriginalFrameScene PlaceComposeEntry(Entry entry)
    {
        // Included in the caller's composition wall time; actors remain individually attributable.
        using var placeMeasure = PoseEvaluationMetrics.Measure("composition.actor.place", entry.Object.Id);
        if (placedFrames.TryGetValue(entry.Object.Id, out var previous)
            && ReferenceEquals(previous.Object, entry.Object) && ReferenceEquals(previous.Local, entry.LocalFrame))
            return previous.Placed;
        // All posed routes, including shared/coupled physics, publish through this point.
        // Retain explicit source styles before applying role GI treatment or environment clearing.
        var local = entry.LocalFrame;
        var sourceFlags = entry.Object.Source.Materials;
        var localMaterials = local.Materials;
        bool needsSourceStyleUpdate = false;
        for (int m = 0; m < localMaterials.Count; m++)
        {
            var flags = localMaterials[m];
            var src = sourceFlags[m];
            if ((src.CharacterOutlineStyle is { } style && flags.CharacterOutlineStyle != style)
                || (src.CharacterToonResponse is { } response && flags.CharacterToonResponse != response)
                || (pmmControls is not null && ((flags.CastSelfShadow && !src.CastSelfShadow)
                    || (flags.ReceiveSelfShadow && !src.ReceiveSelfShadow))))
            {
                needsSourceStyleUpdate = true;
                break;
            }
        }
        if (needsSourceStyleUpdate)
        {
            var adjusted = new OriginalFrameMaterialFlags[localMaterials.Count];
            for (int m = 0; m < adjusted.Length; m++)
            {
                var flags = localMaterials[m];
                var src = sourceFlags[m];
                adjusted[m] = flags with
                {
                    CharacterOutlineStyle = src.CharacterOutlineStyle ?? flags.CharacterOutlineStyle,
                    CharacterToonResponse = src.CharacterToonResponse ?? flags.CharacterToonResponse,
                    // PMM's model-local self-shadow switch is captured in the static source.
                    // Animated material evaluation may disable a flag, but cannot re-enable that switch.
                    CastSelfShadow = flags.CastSelfShadow && (pmmControls is null || src.CastSelfShadow),
                    ReceiveSelfShadow = flags.ReceiveSelfShadow && (pmmControls is null || src.ReceiveSelfShadow),
                };
            }
            local = new(local.Surface, local.Outline, local.OutlineOptions, local.Options,
                adjusted, local.Lighting, local.EditorOverlay);
        }
        local = SceneModelShadingPolicy.Apply(local, SceneModelShadingPolicy.ResolvePurpose(entry.Object, modelRoles));
        var policyMaterials = local.Materials;
        bool needsReceiverId = false;
        for (int m = 0; m < policyMaterials.Count; m++)
        {
            if (policyMaterials[m].LightReceiverObjectId != entry.Object.Id)
            {
                needsReceiverId = true;
                break;
            }
        }
        if (needsReceiverId)
        {
            var receiverMaterials = new OriginalFrameMaterialFlags[policyMaterials.Count];
            for (int m = 0; m < receiverMaterials.Length; m++)
                receiverMaterials[m] = policyMaterials[m] with { LightReceiverObjectId = entry.Object.Id };
            local = new(local.Surface, local.Outline, local.OutlineOptions, local.Options,
                receiverMaterials, local.Lighting, local.EditorOverlay);
        }
        if (entry.Object.World != Matrix4x4.Identity)
        {
            var snapshot = local.Surface.Snapshot;
            var transformed = snapshot.WithWorld(snapshot.World * entry.Object.World);
            local = new OriginalFrameScene(new(transformed, local.Surface.Materials, local.Surface.Profile), local.Outline,
                local.OutlineOptions, materials: local.Materials);
        }
        placedFrames[entry.Object.Id] = (entry.Object, entry.LocalFrame, local);
        return local;
    }

    private IReadOnlyList<SceneActorPlaybackState> CaptureStates(Entry[] input, double frame)
    {
        var previous = states;
        if (input.Length == 0) return EmptyPlaybackStates;
        bool canReuseList = previous.Count == input.Length;
        bool allSame = canReuseList && previous is ReadOnlyCollection<SceneActorPlaybackState>;
        var result = new SceneActorPlaybackState[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            var entry = input[i];
            var controller = entry.Controller;
            var objectId = entry.Object.Id;
            bool hasMotion = entry.Motion is not null;
            uint duration = controller?.DurationFrames ?? 0;
            double localFrame = controller is null ? 0 : Math.Min(frame, controller.DurationFrames);
            bool ikEnabled = entry.Motion?.IkEnabled ?? false;
            bool morphsEnabled = entry.Motion?.MorphsEnabled ?? false;
            bool supportsPhysics = controller is not null && (controller.SupportsPhysics || controller.SupportsSoftBodies);
            bool physicsEnabled = controller?.PhysicsEnabled ?? false;
            string? backendName = controller?.PhysicsBackendName;
            bool visible = controller?.IsVisible ?? true;
            var currentDiags = controller?.Diagnostics ?? EmptyPlaybackDiagnostics;

            SceneActorPlaybackState? prior = canReuseList ? previous[i] : null;
            IReadOnlyList<string> diagnostics;
            if (prior is not null && (ReferenceEquals(prior.Diagnostics, currentDiags) || SameStrings(prior.Diagnostics, currentDiags)))
            {
                diagnostics = prior.Diagnostics;
            }
            else
            {
                diagnostics = currentDiags is ReadOnlyCollection<string> ro ? ro : Array.AsReadOnly(currentDiags.ToArray());
            }

            if (prior is not null
                && prior.ObjectId == objectId
                && prior.HasMotion == hasMotion
                && prior.DurationFrames == duration
                && BitConverter.DoubleToInt64Bits(prior.LocalFrame) == BitConverter.DoubleToInt64Bits(localFrame)
                && prior.IkEnabled == ikEnabled
                && prior.MorphsEnabled == morphsEnabled
                && prior.SupportsPhysics == supportsPhysics
                && prior.PhysicsEnabled == physicsEnabled
                && string.Equals(prior.PhysicsBackend, backendName, StringComparison.Ordinal)
                && prior.Visible == visible
                && ReferenceEquals(prior.Diagnostics, diagnostics))
            {
                result[i] = prior;
            }
            else
            {
                allSame = false;
                result[i] = new SceneActorPlaybackState(objectId, hasMotion, duration, localFrame,
                    ikEnabled, morphsEnabled, supportsPhysics, physicsEnabled, backendName, diagnostics)
                { Visible = visible };
            }
        }
        return allSame ? previous : Array.AsReadOnly(result);
    }

    private static readonly IReadOnlyList<SceneActorPlaybackState> EmptyPlaybackStates = Array.AsReadOnly(Array.Empty<SceneActorPlaybackState>());

    private static bool SameStrings(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }
    private uint Duration(Entry[] input) => Math.Max(Math.Max(input.Max(entry => Math.Max(entry.Controller?.DurationFrames ?? 0,
        entry.Motion?.Motion.BoneKeyframes.Select(k => k.Frame).Concat(entry.Motion.Motion.MorphKeyframes.Select(k => k.Frame))
            .Concat(entry.Motion.Motion.IkKeyframes.Select(k => k.Frame)).DefaultIfEmpty(0u).Max() ?? 0)), sceneTracks?.DurationFrames ?? 0), pmmControls?.DurationFrames ?? 0);
    private int Index(Guid id)
    { int index = Array.FindIndex(entries, entry => entry.Object.Id == id); return index >= 0 ? index : throw new ArgumentException("Unknown scene object.", nameof(id)); }
    private void RequireAlive() => ObjectDisposedException.ThrowIf(disposed, this);
    private void RequireHealthyWithoutPreviewCompletion()
    { RequireAlive(); if (IsFaulted) throw new InvalidOperationException("The session failed during evaluation; perform a successful Seek before continuing.", FaultCause); }
    private void RequireHealthy()
    { RequireHealthyWithoutPreviewCompletion(); CompletePreviewLeaves(); }
    public void Dispose()
    { if (disposed) return; disposed = true; compositionCache.Clear(); placedFrames.Clear(); foreach (var entry in entries) Release(entry); DisposeSessionWorlds(); }
    private void Release(Entry entry) => Release(entry.Object.Id, entry.Controller);
    private void Release(Guid id, CharacterAnimationController? controller)
    {
        try { controller?.Dispose(); }
        catch (Exception exception) { cleanupDiagnostics.Add($"Actor {id} resource cleanup failed: {exception.Message}"); }
    }
}
