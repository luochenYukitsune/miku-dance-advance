// Phase ordering also references babylon-mmd (MIT), mmdRuntime.ts beforePhysics/afterPhysics,
// commit ccb750db2998ed1067017eaf67f3fc987cfba6f8. The fixed clock and frozen-actor contract are project-specific.
using System.Numerics;
using System.Runtime.ExceptionServices;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

public sealed partial class SceneAnimationSession
{
    private readonly Func<ICharacterPhysicsMutableBackend>? sharedPhysicsFactory;
    private readonly Vector3? sharedWorldGravity;
    private SharedCharacterPhysicsSession? sharedWorld;
    private double sharedAccumulator, sharedSampleFrame;
    public bool UsesSharedPhysicsWorld => sharedPhysicsFactory is not null && sharedWorld is not null;
    public long SharedPhysicsStepCount => UsesSharedPhysicsWorld ? sharedWorld!.StepCount : 0;
    private CharacterAnimationController.SharedPhysicsStatus? sharedStatus;
    // Only leaves skipped at a successful preview boundary are recorded. A later boundary
    // replaces their clock, so repeated unpublished advances never build a backlog.
    private Dictionary<Guid, double> previewLeaves = [];
    private Dictionary<Guid, double>? previewWorking;
    // Per-session observation seam for deterministic scheduler tests; never set by production.
    private Action<Guid>? PreviewLeafObserverForTesting = null;

    private const string ParallelPreviewSwitch = "Mmd.Modern.Application.ExperimentalParallelPreviewLeaves";
    private const string ParallelPreviewDegree = "Mmd.Modern.Application.ExperimentalParallelPreviewLeaves.Degree";
    private const string PhysicalPreviewSwitch = "Mmd.Modern.Application.ExperimentalPhysicalPreviewLeaves";

    // Arrays and exact List<T> instances have known read behavior. In particular, an arbitrary
    // IReadOnlyList (including a read-only wrapper over user code) may run side effects on read.
    private static bool StandardList<T>(IReadOnlyList<T>? list)
        => list is T[] || list?.GetType() == typeof(List<T>);

    private static bool StandardPreviewModel(PmxModel model)
    {
        if (!StandardList(model.SoftBodies) || !StandardList(model.Bones)
            || !StandardList(model.Morphs) || !StandardList(model.Materials)) return false;
        // Rig and morph adapters snapshot these definitions at construction. Check them as well
        // so unknown collections retain the original fully ordered evaluation route.
        foreach (var bone in model.Bones)
            if (bone is null || !StandardList(bone.IkLinks)) return false;
        foreach (var morph in model.Morphs)
            if (morph is null || !StandardList(morph.GroupOffsets) || !StandardList(morph.PositionOffsets)
                || !StandardList(morph.BoneOffsets) || !StandardList(morph.UvOffsets)
                || !StandardList(morph.MaterialOffsets) || !StandardList(morph.FlipOffsets)
                || !StandardList(morph.ImpulseOffsets)) return false;
        return true;
    }

    private bool CanCompletePreviewLeavesInParallel(CancellationToken token, Entry[] source)
    {
        if (token.CanBeCanceled || previewLeaves.Count < 2
            || !AppContext.TryGetSwitch(ParallelPreviewSwitch, out bool enabled) || !enabled) return false;
        var controllers = new HashSet<CharacterAnimationController>();
        var models = new HashSet<PmxModel>();
        foreach (var (id, _) in previewLeaves)
        {
            var entry = source[Index(id)];
            if (entry.Controller is not { } controller || entry.Motion is not { } motion
                || controller.HasDeferredRigidPose || !controllers.Add(controller)) return false;
            if (models.Add(motion.Model) && !StandardPreviewModel(motion.Model)) return false;
        }
        return true;
    }

    private void CompletePreviewLeavesParallel(Entry[] next)
    {
        var work = previewLeaves.Select(item => (Index: Index(item.Key), Id: item.Key, Frame: item.Value)).ToArray();
        var poses = new (OriginalStyleScene Surface, OriginalOutlineData Outline)?[work.Length];
        var errors = new ExceptionDispatchInfo?[work.Length];
        int maximum = Math.Min(16, Math.Max(1, Environment.ProcessorCount));
        int degree = AppContext.GetData(ParallelPreviewDegree) is int requested ? Math.Clamp(requested, 1, maximum) : maximum;
        Parallel.For(0, work.Length, new ParallelOptions { MaxDegreeOfParallelism = degree, TaskScheduler = TaskScheduler.Default }, i =>
        {
            try
            {
                var item = work[i]; var entry = next[item.Index]; var motion = entry.Motion!;
                PreviewLeafObserverForTesting?.Invoke(item.Id);
                bool retained;
                using (PoseEvaluationMetrics.Measure("preview.visual.retain", item.Id))
                    retained = entry.Controller!.TryRetainCompleteRigidPlaybackPose(item.Frame, motion.IkEnabled, motion.MorphsEnabled);
                if (!retained)
                    using (PoseEvaluationMetrics.Measure("preview.visual.sample", item.Id))
                        poses[i] = entry.Controller.SampleAt(item.Frame, motion.IkEnabled, motion.MorphsEnabled);
            }
            catch (Exception error) { errors[i] = ExceptionDispatchInfo.Capture(error); }
        });
        // Join every worker before inspecting failures, then preserve the original enumeration
        // order, including failures from the per-entry frame/material merge.
        for (int i = 0; i < work.Length; i++)
        {
            errors[i]?.Throw();
            if (poses[i] is { } pose)
            {
                int index = work[i].Index; var entry = next[index];
                next[index] = entry with { LocalFrame = SharedLocalFrame(entry, pose) };
            }
        }
    }

    private void CompletePreviewLeaves(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (previewLeaves.Count == 0) return;
        // Total wall time includes the child sampling, composition, and state-capture phases.
        using var totalMeasure = PoseEvaluationMetrics.Measure("preview.complete.total");
        var next = (Entry[])entries.Clone(); bool samplingStarted = false;
        try
        {
            if (CanCompletePreviewLeavesInParallel(token, next))
            {
                samplingStarted = true;
                CompletePreviewLeavesParallel(next);
            }
            else foreach (var (id, frame) in previewLeaves)
            {
                token.ThrowIfCancellationRequested();
                int index = Index(id); var entry = next[index]; var motion = entry.Motion!;
                samplingStarted = true;
                PreviewLeafObserverForTesting?.Invoke(id);
                if (entry.Controller!.HasDeferredRigidPose)
                {
                    using var completionMeasure = PoseEvaluationMetrics.Measure("preview.physical.complete", id);
                    var pose = entry.Controller.CompleteDeferredRigidPose(frame);
                    next[index] = entry with { LocalFrame = SharedLocalFrame(entry, pose) };
                }
                else
                {
                    bool retained;
                    using (PoseEvaluationMetrics.Measure("preview.visual.retain", id))
                        retained = entry.Controller.TryRetainCompleteRigidPlaybackPose(frame, motion.IkEnabled, motion.MorphsEnabled);
                    if (!retained)
                    {
                        (OriginalStyleScene Surface, OriginalOutlineData Outline) pose;
                        using (PoseEvaluationMetrics.Measure("preview.visual.sample", id))
                            pose = entry.Controller.SampleAt(frame, motion.IkEnabled, motion.MorphsEnabled);
                        next[index] = entry with { LocalFrame = SharedLocalFrame(entry, pose) };
                    }
                }
                token.ThrowIfCancellationRequested();
            }
            token.ThrowIfCancellationRequested();
            OriginalFrameScene composed;
            using (PoseEvaluationMetrics.Measure("preview.compose"))
                composed = Compose(next, published);
            IReadOnlyList<SceneActorPlaybackState> nextStates;
            using (PoseEvaluationMetrics.Measure("preview.capture-states"))
                nextStates = CaptureStates(next, CurrentFrame);
            token.ThrowIfCancellationRequested();
            entries = next; published = composed; states = nextStates; previewLeaves.Clear();
        }
        catch (Exception error)
        {
            if (error is not OperationCanceledException || samplingStarted) MarkFault(error);
            throw;
        }
    }

    private SharedCharacterPhysicsSession CreateSharedWorld(Entry[] source, double frame, ICharacterPhysicsMutableBackend? supplied = null)
    {
        var backend = supplied ?? sharedPhysicsFactory!() ?? throw new InvalidOperationException("Shared physics factory returned no backend.");
        if (UsesPhysicsBackend(backend))
            throw new InvalidOperationException("A candidate shared world requires a newly owned backend; the active world cannot be reused.");
        var candidate = new SharedCharacterPhysicsSession(backend);
        var status = new CharacterAnimationController.SharedPhysicsStatus(candidate.BackendName, candidate.Diagnostics);
        try
        {
            foreach (var entry in source)
            {
                if (entry.Controller is not { } controller || entry.Motion is not { PhysicsEnabled: true } motion
                    || !(controller.SupportsPhysics || coupledSoftBodies && controller.SupportsSoftBodies)) continue;
                PhysicsMath.ValidateRigid(entry.Object.World, nameof(source));
                using var pending = controller.BeginSharedPhysicsSample(Math.Min(frame, controller.DurationFrames), motion.IkEnabled, motion.MorphsEnabled, ControlDisabled(entry.Object.Id, frame));
                var definition = PmxCharacterPhysicsAdapter.Convert(motion.Model).Definition;
                candidate.AddActor(entry.Object.Id, definition, entry.Object.World, pending.BeforePhysicsBoneWorlds);
                if (coupledSoftBodies && controller.SupportsSoftBodies)
                    controller.ResetSharedCoupledSoftBodies(candidate, entry.Object.Id, entry.Object.World, pending);
                if (sharedWorldGravity is { } gravity) candidate.SetGravity(gravity);
                var pose = pending.Complete(pending.BeforePhysicsBoneWorlds, SharedSoftRigidWorlds(entry, candidate));
                controller.AttachSharedPhysics(status);
                int index = Array.IndexOf(source, entry);
                source[index] = entry with { LocalFrame = SharedLocalFrame(entry, pose) };
            }
            sharedStatus = status; sharedAccumulator = 0; sharedSampleFrame = frame;
            return candidate;
        }
        catch { foreach (var entry in source) entry.Controller?.DetachSharedPhysics(); candidate.Dispose(); throw; }
    }

    private static OriginalFrameScene SharedLocalFrame(Entry entry, (OriginalStyleScene Surface, OriginalOutlineData Outline) pose)
        => new(pose.Surface, pose.Outline, entry.Object.Source.OutlineOptions,
            materials: PmxOriginalFrameAdapter.FromEvaluatedMaterials(entry.Motion!.Model, pose.Surface, pose.Outline));

    private static Matrix4x4[] SharedSoftRigidWorlds(Entry entry, SharedCharacterPhysicsSession world)
    {
        if (entry.Controller is not { SoftBodiesEnabled: true }) return [];
        Matrix4x4.Invert(entry.Object.World, out var inversePlacement);
        return Enumerable.Range(0, entry.Motion!.Model.RigidBodies.Count)
            .Select(index => AffineProduct(world.GetBodyWorldTransform(entry.Object.Id, index) * inversePlacement)).ToArray();
    }

    private OriginalFrameScene EvaluateShared(double frame, double elapsed, bool reset, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var world = sharedWorld!;
        var next = (Entry[])entries.Clone(); int stepCount = 0;
        try
        {
            if (reset)
            {
                EvaluateSharedBoundary(next, world, frame, sharedSampleFrame, reset: true, step: false, token);
                sharedAccumulator = 0; sharedSampleFrame = frame; sharedStatus!.DiscardedSeconds = 0;
            }
            else
            {
                double available = sharedAccumulator + elapsed;
                int requested = (int)Math.Floor((available + 1e-12) / CharacterPhysicsSession.FixedStepSeconds);
                int steps = Math.Min(CharacterPhysicsSession.MaximumSubsteps, requested);
                bool deferPlaybackSkins = !coupledSoftBodies
                    && next.All(entry => entry.Controller?.SoftBodiesEnabled != true && entry.Motion?.Model.SoftBodies.Count is not > 0);
                for (int i = 0; i < steps; i++)
                {
                    double target = sharedSampleFrame + CharacterPhysicsSession.FixedStepSeconds * FramesPerSecond;
                    // Keep every 120Hz bone/control/physics boundary. Only the final displayed
                    // boundary needs vertices and outlines; exact remainder sampling below may
                    // follow the last physical step. Predict with the same discard arithmetic.
                    double finalSample = target + (requested - steps) * CharacterPhysicsSession.FixedStepSeconds * FramesPerSecond;
                    bool finalReflection = i + 1 == steps && (frame != finalSample || CrossesUncompletedActorEnd(next, target, finalSample));
                    EvaluateSharedBoundary(next, world, target, sharedSampleFrame, reset: false, step: true, token,
                        deferRigidSkinning: deferPlaybackSkins && (i + 1 < steps || finalReflection));
                    sharedSampleFrame = target; stepCount++;
                }
                double solvedSampleFrame = sharedSampleFrame;
                sharedAccumulator = Math.Max(0, available - requested * CharacterPhysicsSession.FixedStepSeconds);
                if (requested > steps)
                {
                    double discarded = (requested - steps) * CharacterPhysicsSession.FixedStepSeconds;
                    sharedSampleFrame += discarded * FramesPerSecond; sharedStatus!.DiscardedSeconds += discarded;
                }
                // The timeline can end between physical ticks. Update animation at that exact time,
                // while holding the last completed physical state; never invent an extra solver step.
                // A capped catch-up may jump over an actor's last authored frame. Complete that
                // end pose with the held solver state before a later tick treats it as frozen;
                // no discarded physical step is reintroduced and no failed owner is recovered.
                bool crossedEnd = CrossesUncompletedActorEnd(next, solvedSampleFrame, sharedSampleFrame);
                // Capped catch-up advances the shared timeline past discarded physical ticks.
                // Reflect authored animation at the displayed clock with the held solved state,
                // even when that clock equals the adjusted sharedSampleFrame. Deferred visual
                // leaves otherwise retain the final physical tick's earlier animation frame.
                if (frame != sharedSampleFrame || requested == 0 || requested > steps || crossedEnd)
                    EvaluateSharedBoundary(next, world, frame, crossedEnd ? solvedSampleFrame : sharedSampleFrame,
                        reset: false, step: false, token);
            }
            // A partially sampled preview is not a publishable frame. Retain the last complete
            // frame for fault diagnostics; the first actual reader completes and composes it.
            bool incomplete = previewWorking is { Count: > 0 };
            var composed = incomplete ? published : Compose(next, published);
            var nextStates = incomplete ? states : CaptureStates(next, frame);
            token.ThrowIfCancellationRequested();
            sharedStatus!.LastStepCount = stepCount; sharedStatus.IsFaulted = false;
            foreach (var status in controlledStatuses) { status.LastStepCount = stepCount; status.IsFaulted = false; status.DiscardedSeconds = sharedStatus.DiscardedSeconds; }
            entries = next; published = composed; states = nextStates; CurrentFrame = frame;
            if (reset) previewLeaves.Clear();
            else if (previewWorking is not null) previewLeaves = previewWorking;
            ClearFault();
            return published;
        }
        catch (Exception error) { MarkFault(error); sharedStatus!.IsFaulted = true; foreach (var status in controlledStatuses) status.IsFaulted = true; throw; }
    }

    private static bool CrossesUncompletedActorEnd(Entry[] source, double solvedFrame, double skippedFrame)
        => skippedFrame > solvedFrame && source.Any(entry => entry.Controller is { } controller
            && controller.DurationFrames > solvedFrame && controller.DurationFrames <= skippedFrame);

    // A fixed-clock gesture changes only each entry's completed pose. Its validated attachment
    // topology and actor indices therefore belong to the request, while effective parent matrices
    // and frozen anchors must still be calculated at every physical boundary.
    private sealed record SharedBoundaryGraph(SceneOutsideParentBinding[] Parents, int[] Order,
        Dictionary<Guid, int> Indices, SceneOutsideParentBinding[][] BindingsByIndex,
        IReadOnlySet<int>?[] DisabledByIndex);

    private SharedBoundaryGraph PrepareSharedBoundaryGraph(Entry[] source, double frame)
    {
        var parents = ControlParents(frame);
        var order = ValidateOutsideParents(parents, source);
        var indices = source.Select((entry, index) => (entry.Object.Id, index))
            .ToDictionary(value => value.Id, value => value.index);
        var grouped = new List<SceneOutsideParentBinding>?[source.Length];
        foreach (var binding in parents)
            (grouped[indices[binding.ObjectId]] ??= []).Add(binding);
        var bindings = new SceneOutsideParentBinding[source.Length][];
        for (int index = 0; index < bindings.Length; index++) bindings[index] = grouped[index]?.ToArray() ?? [];
        // Control tracks and the authored clock are fixed for this request. Only physical actors
        // need these sets; effective parent matrices remain live boundary inputs below.
        var disabled = new IReadOnlySet<int>?[source.Length];
        for (int index = 0; index < source.Length; index++)
            if (source[index].Controller?.PhysicsEnabled == true)
                disabled[index] = ControlDisabled(source[index].Object.Id, frame);
        return new(parents, order, indices, bindings, disabled);
    }

    private void EvaluateSharedBoundary(Entry[] source, SharedCharacterPhysicsSession world, double frame, double prior,
        bool reset, bool step, CancellationToken token, bool initializing = false,
        Guid? interactiveObjectId = null, IReadOnlyDictionary<int, BoneLocalTransform>? localBoneOverrides = null,
        IReadOnlySet<Guid>? activeActors = null, bool deferRigidSkinning = false, SharedBoundaryGraph? boundaryGraph = null)
    {
        if (deferRigidSkinning && (reset || coupledSoftBodies
            || source.Any(entry => entry.Controller?.SoftBodiesEnabled == true)))
            throw new InvalidOperationException("Deferred rigid skinning requires a rigid-only non-reset boundary.");
        var pending = new List<(int Index, CharacterAnimationController.SharedPreparedPhysicsSample Pose, SharedPhysicsActorSample Physics, bool DeferPreview)>();
        var preparedWorlds = new Dictionary<Guid, IReadOnlyList<Matrix4x4>>();
        try
        {
            var activeParents = boundaryGraph?.Parents ?? ControlParents(frame);
            // A pure visual leaf supplies no input to this unpublished physical boundary.
            // Recompute this set at every clock: PMM can attach/detach actors mid-advance.
            var requiredParents = (deferRigidSkinning || previewWorking is not null) && !initializing && !interactiveObjectId.HasValue
                ? activeParents.Select(binding => binding.ParentObjectId).ToHashSet() : null;
            if (coupledSoftBodies)
            {
                var preview = new Dictionary<Guid, Matrix4x4[]>();
                foreach (int index in ValidateOutsideParents(activeParents, source))
                {
                    var entry = source[index]; if (entry.Controller is not { } controller) continue;
                    Matrix4x4.Invert(entry.Object.World, out var inverse);
                    var parents = new Dictionary<int, Matrix4x4>();
                    foreach (var binding in activeParents.Where(binding => binding.ObjectId == entry.Object.Id))
                    {
                        var parent = source.Single(value => value.Object.Id == binding.ParentObjectId);
                        parents.Add(binding.BoneIndex, AffineProduct(binding.Offset * preview[binding.ParentObjectId][binding.ParentBoneIndex] * parent.Object.World * inverse));
                    }
                    preview.Add(entry.Object.Id, controller.PreviewCoupledBoundary(Math.Min(frame, controller.DurationFrames),
                        entry.Motion!.IkEnabled, entry.Motion.MorphsEnabled, parents, ControlDisabled(entry.Object.Id, frame), !initializing && !reset && prior >= controller.DurationFrames));
                }
            }
            if (!initializing) ApplyControlGravity(source, frame, reset, step);
            foreach (int index in boundaryGraph?.Order ?? ValidateOutsideParents(activeParents, source))
            {
                token.ThrowIfCancellationRequested(); var entry = source[index]; var controller = entry.Controller;
                if (controller is null) continue;
                if (activeActors is not null && !activeActors.Contains(entry.Object.Id)) continue;
                var motion = entry.Motion!; double local = Math.Min(frame, controller.DurationFrames);
                bool edited = interactiveObjectId == entry.Object.Id;
                bool frozen = !initializing && !reset && !edited && prior >= controller.DurationFrames;
                var overrides = edited ? localBoneOverrides : null;
                bool attached = boundaryGraph is null ? activeParents.Any(binding => binding.ObjectId == entry.Object.Id)
                    : boundaryGraph.BindingsByIndex[index].Length != 0;
                var actorWorld = ActorWorld(entry, world);
                bool physical = actorWorld is not null && actorWorld.ActorIds.Contains(entry.Object.Id);
                if (interactiveObjectId.HasValue && frozen && !attached && !physical
                    && controller.CanRetainCompleteRigidInteractivePose(local, motion.IkEnabled, motion.MorphsEnabled))
                {
                    // Retain the immutable local scene of stopped rooms/props. Descendants still
                    // receive its completed bones in dependency order, and every physical world
                    // keeps its normal step. Editing this actor or attaching it takes the full path.
                    preparedWorlds[entry.Object.Id] = controller.BoneWorldMatrices;
                    continue;
                }
                Dictionary<int, Matrix4x4>? parents = null;
                if (attached)
                {
                    Matrix4x4.Invert(entry.Object.World, out var inverseWorld);
                    parents = [];
                    IEnumerable<SceneOutsideParentBinding> bindings = boundaryGraph is null
                        ? activeParents.Where(binding => binding.ObjectId == entry.Object.Id) : boundaryGraph.BindingsByIndex[index];
                    foreach (var binding in bindings)
                    {
                        var parentEntry = boundaryGraph is null ? source.Single(value => value.Object.Id == binding.ParentObjectId)
                            : source[boundaryGraph.Indices[binding.ParentObjectId]];
                        var worlds = preparedWorlds.TryGetValue(binding.ParentObjectId, out var prepared) ? prepared : parentEntry.Controller!.BoneWorldMatrices;
                        parents.Add(binding.BoneIndex, AffineProduct(binding.Offset * worlds[binding.ParentBoneIndex] * parentEntry.Object.World * inverseWorld));
                    }
                }
                // A frozen attachment can move during a fixed-clock gesture. Its immutable anchor
                // must rebase on every boundary, but the matching skin belongs to completion below.
                bool deferFrozenSkin = interactiveObjectId.HasValue || deferRigidSkinning || controller.HasUnpublishedRigidBonePose;
                if (pmmControls is not null && frozen)
                {
                    bool updated=controller.RefreshSharedFrozenOutsideParents(parents ?? [], deferFrozenSkin);
                    // Detaching the last PMM slot can complete a new frozen display even
                    // though the actor is now unattached. Publish that cached result too;
                    // leaving LocalFrame behind would show the old attachment's geometry.
                    if(updated&&!physical)
                        source[index]=entry=entry with {LocalFrame=SharedLocalFrame(entry,controller.SampleAt(local,motion.IkEnabled,motion.MorphsEnabled))};
                }
                else if (frozen && attached)
                {
                    if (deferFrozenSkin) controller.RefreshSharedFrozenOutsideParents(parents!, deferSkinning: true);
                    else controller.RefreshFrozenOutsideParents(parents!);
                }
                else if (attached || pmmControls is not null) controller.SetOutsideParents(parents);
                if (interactiveObjectId.HasValue && frozen && attached && !physical && !edited
                    && controller.HasCompleteRigidFrozenAttachmentPose(local, motion.IkEnabled, motion.MorphsEnabled, parents!))
                {
                    // Refresh above proved that the current parent matrices still match a
                    // complete rigid display. Retain its scene, but keep supplying its solved
                    // bones to descendants in the same dependency order and step every world.
                    preparedWorlds[entry.Object.Id] = controller.BoneWorldMatrices;
                    continue;
                }
                if (!physical)
                {
                    if(requiredParents is not null&&!frozen&&!attached&&!reset
                        &&frame<controller.DurationFrames&&motion.Model.SoftBodies.Count==0
                        &&!requiredParents.Contains(entry.Object.Id))
                    {
                        // No solver, soft state, attachment or consumer can observe these
                        // intermediate bones. The final displayed boundary evaluates them
                        // normally. Never skip crossing an actor's end: later frozen parent
                        // rebasing requires a successfully completed tail, including PMM
                        // slots first activated after that local animation has ended.
                        if (previewWorking is not null) previewWorking[entry.Object.Id] = local;
                        continue;
                    }
                    if(!coupledSoftBodies&&!initializing&&!reset&&!interactiveObjectId.HasValue
                        &&controller.TryRetainCompleteRigidPlaybackPose(local,motion.IkEnabled,motion.MorphsEnabled))
                    {
                        // Every control/parent boundary above still ran. A complete constant
                        // local result also supplies the exact stable bones to descendants;
                        // retain both products while advancing the controller's cache clock.
                        if (!deferRigidSkinning) previewWorking?.Remove(entry.Object.Id);
                        preparedWorlds[entry.Object.Id]=controller.BoneWorldMatrices;
                        continue;
                    }
                    if (interactiveObjectId.HasValue)
                    {
                        // The warm interactive route excludes soft bodies and keeps authored animation
                        // at one clock. A nonphysical PMM parent still needs its raw local draft here.
                        CharacterAnimationController.SharedPreparedPhysicsSample pose;
                        using (PoseEvaluationMetrics.Measure("boundary.nonphysical.interactive-prepare", entry.Object.Id))
                            pose = controller.BeginSharedPhysicsSample(local, motion.IkEnabled, motion.MorphsEnabled,
                                frozen: frozen, localBoneOverrides: overrides);
                        using (pose)
                        {
                            if (deferRigidSkinning)
                            {
                                using var completionMeasure = PoseEvaluationMetrics.Measure("boundary.nonphysical.interactive-complete-bones", entry.Object.Id);
                                pose.CompleteRigidBoneWorlds(pose.BeforePhysicsBoneWorlds);
                            }
                            else
                            {
                                using var completionMeasure = PoseEvaluationMetrics.Measure("boundary.nonphysical.interactive-complete-full", entry.Object.Id);
                                source[index] = entry with { LocalFrame = SharedLocalFrame(entry, pose.Complete(pose.BeforePhysicsBoneWorlds)) };
                            }
                        }
                    }
                    else if (controller.SoftBodiesEnabled)
                    {
                        using var softPose = controller.BeginSharedPhysicsSample(local, motion.IkEnabled, motion.MorphsEnabled, frozen: frozen);
                        double? softElapsed = reset ? null : step && !frozen ? CharacterPhysicsSession.FixedStepSeconds : 0;
                        source[index] = entry with { LocalFrame = SharedLocalFrame(entry,
                            softPose.Complete(softPose.BeforePhysicsBoneWorlds, [], softElapsed)) };
                    }
                    else if (frozen && controller.HasUnpublishedRigidBonePose)
                    {
                        // The stopped actor may have reached its end in a bone-only middle
                        // boundary or changed/dropped a PMM parent slot. Preserve its rebased
                        // frozen bones; resampling authored animation would lose that anchor.
                        if (!deferRigidSkinning)
                        {
                            using var completionMeasure = PoseEvaluationMetrics.Measure("boundary.nonphysical.frozen-complete-full", entry.Object.Id);
                            using var frozenPose = controller.BeginSharedPhysicsSample(local, motion.IkEnabled, motion.MorphsEnabled, frozen: true);
                            source[index] = entry with { LocalFrame = SharedLocalFrame(entry, frozenPose.Complete(frozenPose.BeforePhysicsBoneWorlds)) };
                        }
                    }
                    else if (!frozen || reset || attached)
                    {
                        if (deferRigidSkinning)
                        {
                            using var sampleMeasure = PoseEvaluationMetrics.Measure("boundary.nonphysical.bones-only", entry.Object.Id);
                            controller.SampleRigidInitializationBoneWorlds(local, motion.IkEnabled, motion.MorphsEnabled);
                        }
                        else
                        {
                            using var sampleMeasure = PoseEvaluationMetrics.Measure("boundary.nonphysical.full-sample", entry.Object.Id);
                            source[index] = entry with { LocalFrame = SharedLocalFrame(entry, controller.SampleAt(local, motion.IkEnabled, motion.MorphsEnabled)) };
                        }
                    }
                    // Controllers replace their read-only palette on completion; they never mutate
                    // an earlier palette. Keep that stable snapshot without copying every parent.
                    if (!deferRigidSkinning) previewWorking?.Remove(entry.Object.Id);
                    preparedWorlds[entry.Object.Id] = controller.BoneWorldMatrices;
                    continue;
                }
                var disabled = boundaryGraph?.DisabledByIndex[index] ?? ControlDisabled(entry.Object.Id, frame);
                CharacterAnimationController.SharedPreparedPhysicsSample preparation;
                using (PoseEvaluationMetrics.Measure("boundary.physical.prepare", entry.Object.Id))
                    preparation = controller.BeginSharedPhysicsSample(local, motion.IkEnabled, motion.MorphsEnabled, disabled, frozen, overrides);
                preparedWorlds[entry.Object.Id] = preparation.BeforePhysicsBoneWorlds;
                // Only this actor's terminal skin is delayed. The existing middle-boundary
                // flag also affects nonphysical parents, so it must not be widened here.
                // Children consume its before-physics bones above; rigid worlds do not read its skin.
                bool deferPreview = previewWorking is not null && !deferRigidSkinning && !initializing && !reset
                    && !interactiveObjectId.HasValue && !frozen && !attached && local < controller.DurationFrames
                    && requiredParents is not null
                    && AppContext.TryGetSwitch(PhysicalPreviewSwitch, out bool enabled) && enabled;
                pending.Add((index, preparation, new(entry.Object.Id, preparation.BeforePhysicsBoneWorlds)
                { Frozen = frozen, Impulses = initializing || interactiveObjectId.HasValue ? [] : preparation.Impulses, DisabledBoneIndices = disabled }, deferPreview));
            }
            var results = new Dictionary<Guid, Matrix4x4[]>();
            foreach (var batch in pending.GroupBy(item => ActorWorld(source[item.Index], world)!))
            {
                var samples = batch.Select(value => value.Physics).ToArray();
                // Validate every pin target in this world before changing body modes, impulses or time.
                var targets = batch.Select(item => (item, targets: source[item.Index].Controller!.PrepareCoupledTargets(item.Pose))).ToArray();
                if (reset) batch.Key.ResetAll(samples);
                foreach (var command in targets)
                {
                    var entry = source[command.item.Index];
                    if (coupledSoftBodies && entry.Controller!.SupportsSoftBodies && reset)
                        entry.Controller.ResetSharedCoupledSoftBodies(batch.Key, entry.Object.Id, entry.Object.World, command.item.Pose);
                    var pins = reset ? entry.Controller!.PrepareCoupledTargets(command.item.Pose) : command.targets;
                    entry.Controller!.PublishCoupledTargets(pins, command.item.Physics.Frozen, step || reset);
                }
                if (currentControlGravity is { } gravity) batch.Key.SetGravity(gravity);
                try
                {
                    using (PoseEvaluationMetrics.Measure(step ? "boundary.world.step" : "boundary.world.reflect"))
                        foreach (var result in step ? batch.Key.Step(samples) : batch.Key.Reflect(samples)) results.Add(result.Key, result.Value);
                }
                catch (Exception error)
                {
                    try
                    {
                        error.Data["Animation.SourceFrame"] = frame;
                        error.Data["Animation.PriorSourceFrame"] = prior;
                        if (error.Data["Physics.ActorId"] is Guid id)
                        {
                            var entry = batch.Select(item => source[item.Index]).First(item => item.Object.Id == id);
                            string name = entry.Object.Name;
                            error.Data["Animation.ActorName"] = name.Length <= 4096 ? name : name[..4096];
                            if (error.Data["Physics.BoneIndex"] is int bone && entry.Motion is { } motion
                                && (uint)bone < (uint)motion.Model.Bones.Count)
                            {
                                string boneName = motion.Model.Bones[bone].Name;
                                error.Data["Animation.BoneName"] = boneName.Length <= 4096 ? boneName : boneName[..4096];
                            }
                        }
                    }
                    catch { }
                    throw;
                }
            }
            foreach (var item in pending)
            {
                var entry = source[item.Index];
                if (deferRigidSkinning)
                {
                    using var completionMeasure = PoseEvaluationMetrics.Measure("boundary.physical.complete-bones", entry.Object.Id);
                    item.Pose.CompleteRigidBoneWorlds(results[entry.Object.Id]);
                    continue;
                }
                if (item.DeferPreview)
                {
                    using var completionMeasure = PoseEvaluationMetrics.Measure("boundary.physical.complete-deferred-bones", entry.Object.Id);
                    item.Pose.CompleteRigidBoneWorldsAndDefer(results[entry.Object.Id]);
                    previewWorking![entry.Object.Id] = Math.Min(frame, entry.Controller!.DurationFrames);
                    continue;
                }
                double? softElapsed = reset ? null : step && !item.Physics.Frozen ? CharacterPhysicsSession.FixedStepSeconds : 0;
                using var fullCompletionMeasure = PoseEvaluationMetrics.Measure("boundary.physical.complete-full", entry.Object.Id);
                source[item.Index] = entry with { LocalFrame = SharedLocalFrame(entry,
                    item.Pose.Complete(results[entry.Object.Id], SharedSoftRigidWorlds(entry, ActorWorld(entry, world)!), softElapsed)) };
                previewWorking?.Remove(entry.Object.Id);
            }
        }
        finally { foreach (var item in pending) item.Pose.Dispose(); }
    }

    private void BindSharedMotion(int index, SceneActorMotion motion, CancellationToken token)
    {
        // Soft candidate resets can fail. Rebuild a complete candidate graph before publishing,
        // rather than replacing live rigid membership and then attempting a fallible soft reset.
        if (coupledSoftBodies || outsideBindings.Length != 0 || entries.Any(entry => entry.Controller?.SoftBodiesEnabled == true)
            || motion.PhysicsEnabled && motion.Model.SoftBodies.Count != 0)
        {
            var motions = entries.Where(entry => entry.Motion is not null && entry.Object.Id != motion.ObjectId).Select(entry => entry.Motion!).Append(motion).ToArray();
            ReplaceGraph(outsideBindings, motions, token); return;
        }
        var previous = entries[index]; var candidate = PrepareEntry(previous.Object, motion, CurrentFrame, token);
        CharacterAnimationController.SharedPreparedPhysicsSample? initial = null;
        try
        {
            bool has = sharedWorld!.ActorIds.Contains(motion.ObjectId);
            if (motion.PhysicsEnabled && candidate.Controller!.SupportsPhysics)
            {
                initial = candidate.Controller.BeginSharedPhysicsSample(Math.Min(CurrentFrame, candidate.Controller.DurationFrames), motion.IkEnabled, motion.MorphsEnabled);
                candidate = candidate with { LocalFrame = SharedLocalFrame(candidate, initial.Complete(initial.BeforePhysicsBoneWorlds)) };
            }
            var next = (Entry[])entries.Clone(); next[index] = candidate;
            var prospective = Compose(next, null); // All topology/material failures precede solver membership changes.
            if (initial is not null)
            {
                var definition = PmxCharacterPhysicsAdapter.Convert(motion.Model).Definition;
                if (has) sharedWorld.ReplaceActor(motion.ObjectId, definition, candidate.Object.World, initial.BeforePhysicsBoneWorlds);
                else sharedWorld.AddActor(motion.ObjectId, definition, candidate.Object.World, initial.BeforePhysicsBoneWorlds);
                candidate.Controller!.AttachSharedPhysics(sharedStatus!);
            }
            else if (has) sharedWorld.RemoveActor(motion.ObjectId);
            entries = next; published = prospective; states = CaptureStates(next, CurrentFrame); DurationFrames = Duration(next);
            if (!ReferenceEquals(previous.Motion?.Model, motion.Model)) { outsideUndo.Clear(); outsideRedo.Clear(); }
        }
        catch (Exception error)
        {
            if (sharedWorld!.IsFaulted) { MarkFault(error); sharedStatus!.IsFaulted = true; }
            Release(candidate); throw;
        }
        finally { initial?.Dispose(); }
        Release(previous);
    }
}
