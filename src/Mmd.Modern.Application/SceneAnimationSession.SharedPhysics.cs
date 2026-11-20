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
    private static readonly HashSet<Guid> EmptyGuidSet = new();
    private static readonly IReadOnlySet<int> EmptyDisabledBones = new HashSet<int>();

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
    private SharedBoundaryTopology? cachedBoundaryTopology;

    private sealed record SharedBoundaryTopology(
        SceneOutsideParentBinding[] Parents,
        Guid[] ActorIds,
        PmxModel?[] ActorModels,
        IReadOnlyList<PmxBone>?[] ActorBoneLists,
        int[] ActorBoneCounts,
        Dictionary<Guid, int> ObjectIndexById,
        int[] TopologicalOrder,
        int[][] Levels,
        SceneOutsideParentBinding[][] BindingsByIndex,
        int[][] ParentActorIndicesByBinding,
        HashSet<Guid> ParentObjectIds)
    {
        internal bool Matches(Entry[] source, SceneOutsideParentBinding[] activeParents)
        {
            if (!ReferenceEquals(Parents, activeParents) && (Parents.Length != 0 || activeParents.Length != 0))
                return false;
            if (ActorIds.Length != source.Length) return false;
            for (int i = 0; i < source.Length; i++)
            {
                var entry = source[i];
                var model = entry.Motion?.Model;
                if (ActorIds[i] != entry.Object.Id
                    || !ReferenceEquals(ActorModels[i], model)
                    || !ReferenceEquals(ActorBoneLists[i], model?.Bones)
                    || ActorBoneCounts[i] != (model?.Bones.Count ?? -1))
                    return false;
            }
            return true;
        }
    }

    private sealed record SharedBoundaryGraph(
        SharedBoundaryTopology Topology,
        IReadOnlySet<int>?[] DisabledByIndex,
        ScenePmmGroundOutsideParent[][] GroundByIndex,
        IReadOnlyDictionary<int, OutsideParentTrackWindow>?[] TrackWindowsByIndex)
    {
        internal SceneOutsideParentBinding[] Parents => Topology.Parents;
        internal int[] Order => Topology.TopologicalOrder;
    }

    private readonly record struct NonPhysicalAttachedState(double Local, bool SampleFrozen,
        bool DeferFrozenSkin, IReadOnlyDictionary<int, BoneLocalTransform>? Overrides);

    private SharedBoundaryTopology GetSharedBoundaryTopology(Entry[] source, SceneOutsideParentBinding[] activeParents)
    {
        var cached = Volatile.Read(ref cachedBoundaryTopology);
        if (cached is not null && cached.Matches(source, activeParents))
            return cached;
        var built = BuildSharedBoundaryTopology(source, activeParents);
        Volatile.Write(ref cachedBoundaryTopology, built);
        return built;
    }

    private static SharedBoundaryTopology BuildSharedBoundaryTopology(Entry[] source, SceneOutsideParentBinding[] activeParents)
    {
        if (activeParents.Length > 100_000)
            throw new ArgumentException("Too many outside-parent bindings.", "bindings");
        int count = source.Length;
        var actorIds = new Guid[count];
        var actorModels = new PmxModel?[count];
        var actorBoneLists = new IReadOnlyList<PmxBone>?[count];
        var actorBoneCounts = new int[count];
        var indices = new Dictionary<Guid, int>(count);
        for (int i = 0; i < count; i++)
        {
            var entry = source[i];
            Guid id = entry.Object.Id;
            var model = entry.Motion?.Model;
            actorIds[i] = id;
            actorModels[i] = model;
            actorBoneLists[i] = model?.Bones;
            actorBoneCounts[i] = model?.Bones.Count ?? -1;
            indices.Add(id, i);
        }

        if (activeParents.Length == 0)
        {
            var order = new int[count];
            for (int i = 0; i < count; i++) order[i] = i;
            var emptyBindings = new SceneOutsideParentBinding[count][];
            var emptyParentIndices = new int[count][];
            for (int i = 0; i < count; i++)
            {
                emptyBindings[i] = [];
                emptyParentIndices[i] = [];
            }
            int[][] levels = count == 0 ? [] : [order];
            return new(activeParents, actorIds, actorModels, actorBoneLists, actorBoneCounts,
                indices, order, levels, emptyBindings, emptyParentIndices, EmptyGuidSet);
        }

        var slots = new HashSet<(Guid, int)>(activeParents.Length);
        var parentObjectIds = new HashSet<Guid>();
        var groupedBindings = new List<SceneOutsideParentBinding>?[count];
        var groupedParentIndices = new List<int>?[count];
        var distinctParents = new List<int>?[count];
        for (int b = 0; b < activeParents.Length; b++)
        {
            var binding = activeParents[b];
            if (binding is null || !indices.TryGetValue(binding.ObjectId, out int child)
                || !indices.TryGetValue(binding.ParentObjectId, out int parent)
                || child == parent || !slots.Add((binding.ObjectId, binding.BoneIndex)))
                throw new ArgumentException("Outside-parent slots require distinct known models and unique subjects.", "bindings");
            var childModel = source[child].Motion?.Model;
            var parentModel = source[parent].Motion?.Model;
            if (childModel is null || parentModel is null || binding.BoneIndex < -1 || binding.BoneIndex >= childModel.Bones.Count
                || binding.ParentBoneIndex < 0 || binding.ParentBoneIndex >= parentModel.Bones.Count)
                throw new ArgumentException("Outside parents require bound models and valid bone indices.", "bindings");
            ValidateAffine(binding.Offset);
            parentObjectIds.Add(binding.ParentObjectId);
            (groupedBindings[child] ??= []).Add(binding);
            (groupedParentIndices[child] ??= []).Add(parent);
            var distinct = distinctParents[child] ??= [];
            if (!distinct.Contains(parent)) distinct.Add(parent);
        }

        var visitState = new byte[count];
        var topoOrder = new int[count];
        int topoCount = 0;
        void Visit(int index)
        {
            if (visitState[index] == 2) return;
            if (visitState[index] == 1)
                throw new ArgumentException("Outside-parent model dependency cycle.", "bindings");
            visitState[index] = 1;
            if (distinctParents[index] is { } parents)
                for (int p = 0; p < parents.Count; p++) Visit(parents[p]);
            visitState[index] = 2;
            topoOrder[topoCount++] = index;
        }
        for (int i = 0; i < count; i++) Visit(i);

        var depths = new int[count];
        int maxDepth = 0;
        for (int k = 0; k < topoOrder.Length; k++)
        {
            int idx = topoOrder[k];
            if (distinctParents[idx] is not { } parents) continue;
            int depth = 0;
            for (int p = 0; p < parents.Count; p++)
            {
                int candidate = depths[parents[p]] + 1;
                if (candidate > depth) depth = candidate;
            }
            depths[idx] = depth;
            if (depth > maxDepth) maxDepth = depth;
        }

        var levelLists = new List<int>[maxDepth + 1];
        for (int i = 0; i < count; i++) (levelLists[depths[i]] ??= []).Add(i);
        var topoLevels = new int[maxDepth + 1][];
        for (int l = 0; l < topoLevels.Length; l++) topoLevels[l] = levelLists[l]!.ToArray();

        var bindingsByIndex = new SceneOutsideParentBinding[count][];
        var parentIndicesByBinding = new int[count][];
        for (int i = 0; i < count; i++)
        {
            bindingsByIndex[i] = groupedBindings[i]?.ToArray() ?? [];
            parentIndicesByBinding[i] = groupedParentIndices[i]?.ToArray() ?? [];
        }

        return new(activeParents, actorIds, actorModels, actorBoneLists, actorBoneCounts,
            indices, topoOrder, topoLevels, bindingsByIndex, parentIndicesByBinding, parentObjectIds);
    }

    private SharedBoundaryGraph PrepareSharedBoundaryGraph(Entry[] source, double frame)
    {
        var parents = ControlParents(frame);
        var topology = GetSharedBoundaryTopology(source, parents);
        // Control tracks and the authored clock are fixed for this request. Only physical actors
        // need these sets; effective parent matrices remain live boundary inputs below.
        var disabled = new IReadOnlySet<int>?[source.Length];
        var ground = new ScenePmmGroundOutsideParent[source.Length][];
        var trackWindows = new IReadOnlyDictionary<int, OutsideParentTrackWindow>?[source.Length];
        for (int index = 0; index < source.Length; index++)
        {
            Guid id = source[index].Object.Id;
            ground[index] = ControlGroundParents(id, frame);
            trackWindows[index] = ControlTrackWindows(id, frame);
            if (source[index].Controller?.PhysicsEnabled == true)
                disabled[index] = ControlDisabled(id, frame);
        }
        return new(topology, disabled, ground, trackWindows);
    }

    private Dictionary<int, Matrix4x4>? BuildBoundaryOutsideParents(Entry[] source, Entry entry,
        SceneOutsideParentBinding[] bindings, int[] parentIndices, ScenePmmGroundOutsideParent[] groundParents,
        Func<int, int, IReadOnlyList<Matrix4x4>> resolveParentWorlds)
    {
        if (bindings.Length == 0 && groundParents.Length == 0) return null;
        Matrix4x4.Invert(entry.Object.World, out var inverseWorld);
        var parents = new Dictionary<int, Matrix4x4>(bindings.Length + groundParents.Length);
        for (int b = 0; b < bindings.Length; b++)
        {
            var binding = bindings[b];
            int parentIndex = parentIndices[b];
            var parentEntry = source[parentIndex];
            var worlds = resolveParentWorlds(parentIndex, binding.ParentBoneIndex);
            parents.Add(binding.BoneIndex, AffineProduct(binding.Offset * worlds[binding.ParentBoneIndex] * parentEntry.Object.World * inverseWorld));
        }
        for (int g = 0; g < groundParents.Length; g++)
        {
            int bone = groundParents[g].BoneIndex;
            parents.Add(bone, GroundOutsideParentTransform(entry.Motion!.Model, bone, inverseWorld));
        }
        return parents;
    }

    private static IReadOnlyList<Matrix4x4> ResolvePhase1ParentBoneWorlds(Entry parentEntry, int parentBoneIndex,
        IReadOnlyList<Matrix4x4>? prepared, IReadOnlySet<int>? parentDisabled, Matrix4x4[]? resetAfterPhysicsWorlds)
    {
        var parentController = parentEntry.Controller!;
        if (parentDisabled is not null && parentController.IsBoneAffectedByAfterPhysics(parentBoneIndex))
        {
            if (resetAfterPhysicsWorlds is not null) return resetAfterPhysicsWorlds;
            if (parentController.BoneWorldMatrices.Count == parentEntry.Motion!.Model.Bones.Count)
                return parentController.BoneWorldMatrices;
        }
        return prepared ?? parentController.BoneWorldMatrices;
    }

    private static Matrix4x4[]? EnsureResetAfterPhysicsParentWorlds(Entry[] source, int parentIndex,
        IReadOnlySet<int>?[] physicalDisabledByIndex, Matrix4x4[]?[] resetAfterPhysicsWorldsByIndex,
        double frame, Guid? interactiveObjectId, IReadOnlyDictionary<int, BoneLocalTransform>? localBoneOverrides)
    {
        if (resetAfterPhysicsWorldsByIndex[parentIndex] is { } existing) return existing;
        var parentEntry = source[parentIndex];
        var parentController = parentEntry.Controller!;
        var parentMotion = parentEntry.Motion!;
        var parentOverrides = interactiveObjectId == parentEntry.Object.Id ? localBoneOverrides : null;
        var sampled = parentController.SampleResetPostAfterPhysicsBoneWorlds(
            Math.Min(frame, parentController.DurationFrames),
            parentMotion.IkEnabled, parentMotion.MorphsEnabled, physicalDisabledByIndex[parentIndex]!, parentOverrides);
        resetAfterPhysicsWorldsByIndex[parentIndex] = sampled;
        return sampled;
    }

    private OriginalFrameScene? EvaluateNonPhysicalAfterPhysics(Entry entry, NonPhysicalAttachedState info,
        Dictionary<int, Matrix4x4> afterParents, Guid? interactiveObjectId, bool deferRigidSkinning)
    {
        var controller = entry.Controller!; var motion = entry.Motion!;
        bool fullFrozenAfter = info.SampleFrozen
            && controller.RequiresFullEvaluationForFrozenOutsideParents(afterParents, trackWindowChanged: false);
        if (info.SampleFrozen && !fullFrozenAfter)
        {
            if (info.DeferFrozenSkin)
            {
                controller.RefreshSharedFrozenOutsideParents(afterParents, deferSkinning: true);
                if (!deferRigidSkinning)
                {
                    using var frozenPose = controller.BeginSharedPhysicsSample(info.Local, motion.IkEnabled, motion.MorphsEnabled,
                        frozen: true, localBoneOverrides: info.Overrides);
                    return SharedLocalFrame(entry, frozenPose.Complete(frozenPose.BeforePhysicsBoneWorlds));
                }
                return null;
            }
            if (pmmControls is not null)
            {
                if (controller.RefreshSharedFrozenOutsideParents(afterParents, deferSkinning: false))
                    return SharedLocalFrame(entry, controller.SampleAt(info.Local, motion.IkEnabled, motion.MorphsEnabled));
                return null;
            }
            return SharedLocalFrame(entry, controller.RefreshFrozenOutsideParents(afterParents));
        }
        controller.SetOutsideParents(afterParents);
        if (interactiveObjectId.HasValue)
        {
            using var pose = controller.BeginSharedPhysicsSample(info.Local, motion.IkEnabled, motion.MorphsEnabled,
                frozen: false, localBoneOverrides: info.Overrides);
            if (deferRigidSkinning) { pose.CompleteRigidBoneWorlds(pose.BeforePhysicsBoneWorlds); return null; }
            return SharedLocalFrame(entry, pose.Complete(pose.BeforePhysicsBoneWorlds));
        }
        if (deferRigidSkinning)
        {
            controller.SampleRigidInitializationBoneWorlds(info.Local, motion.IkEnabled, motion.MorphsEnabled);
            return null;
        }
        return SharedLocalFrame(entry, controller.SampleAt(info.Local, motion.IkEnabled, motion.MorphsEnabled));
    }

    private static OriginalFrameScene? LocalFrameIfPoseChanged(Entry entry, (OriginalStyleScene Surface, OriginalOutlineData Outline) pose)
        => ReferenceEquals(pose.Surface, entry.LocalFrame.Surface) && ReferenceEquals(pose.Outline, entry.LocalFrame.Outline)
            ? null
            : SharedLocalFrame(entry, pose);

    private static void EnrichPhysicsStepException(Exception error, Entry[] source,
        int singleEntryIndex, IReadOnlyList<(int Index, CharacterAnimationController.SharedPreparedPhysicsSample Pose, SharedPhysicsActorSample Physics, bool DeferPreview)>? batchItems,
        double frame, double prior)
    {
        try
        {
            error.Data["Animation.SourceFrame"] = frame;
            error.Data["Animation.PriorSourceFrame"] = prior;
            if (error.Data["Physics.ActorId"] is Guid id)
            {
                Entry? matched = null;
                if (singleEntryIndex >= 0)
                {
                    if (source[singleEntryIndex].Object.Id == id) matched = source[singleEntryIndex];
                }
                else if (batchItems is not null)
                {
                    for (int i = 0; i < batchItems.Count; i++)
                    {
                        var candidate = source[batchItems[i].Index];
                        if (candidate.Object.Id == id) { matched = candidate; break; }
                    }
                }
                if (matched is { } entry)
                {
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
        }
        catch { }
    }

    private void EvaluateSharedBoundary(Entry[] source, SharedCharacterPhysicsSession world, double frame, double prior,
        bool reset, bool step, CancellationToken token, bool initializing = false,
        Guid? interactiveObjectId = null, IReadOnlyDictionary<int, BoneLocalTransform>? localBoneOverrides = null,
        IReadOnlySet<Guid>? activeActors = null, bool deferRigidSkinning = false, SharedBoundaryGraph? boundaryGraph = null)
    {
        if (deferRigidSkinning && (reset || coupledSoftBodies
            || source.Any(entry => entry.Controller?.SoftBodiesEnabled == true)))
            throw new InvalidOperationException("Deferred rigid skinning requires a rigid-only non-reset boundary.");
        int actorCount = source.Length;
        var pending = new List<(int Index, CharacterAnimationController.SharedPreparedPhysicsSample Pose, SharedPhysicsActorSample Physics, bool DeferPreview)>();
        var pendingByIndex = new int[actorCount];
        Array.Fill(pendingByIndex, -1);
        List<(int Index, CharacterAnimationController.SharedPreparedPhysicsSample Pose, bool Frozen)>? softOnly = null;
        int[]? softOnlyByIndex = null;
        var preparedWorldsByIndex = new IReadOnlyList<Matrix4x4>?[actorCount];
        var physicalDisabledByIndex = new IReadOnlySet<int>?[actorCount];
        Matrix4x4[]?[]? resetAfterPhysicsWorldsByIndex = reset ? new Matrix4x4[]?[actorCount] : null;
        var phase1OutsideParentsByIndex = new Dictionary<int, Matrix4x4>?[actorCount];
        var hasPhase1OutsideParents = new bool[actorCount];
        NonPhysicalAttachedState?[]? nonPhysicalAttached = null;
        try
        {
            var activeParents = boundaryGraph?.Parents ?? ControlParents(frame);
            var topology = boundaryGraph?.Topology ?? GetSharedBoundaryTopology(source, activeParents);
            if (activeParents.Length > 0) nonPhysicalAttached = new NonPhysicalAttachedState?[actorCount];
            // A pure visual leaf supplies no input to this unpublished physical boundary.
            // Recompute this set at every clock: PMM can attach/detach actors mid-advance.
            var requiredParents = (deferRigidSkinning || previewWorking is not null) && !initializing && !interactiveObjectId.HasValue
                ? topology.ParentObjectIds : null;
            if (coupledSoftBodies)
            {
                var preview = new Dictionary<Guid, Matrix4x4[]>(actorCount);
                foreach (int index in topology.TopologicalOrder)
                {
                    var entry = source[index]; if (entry.Controller is not { } controller) continue;
                    var bindings = topology.BindingsByIndex[index];
                    var parentIndices = topology.ParentActorIndicesByBinding[index];
                    var parents = BuildBoundaryOutsideParents(source, entry, bindings, parentIndices,
                        ControlGroundParents(entry.Object.Id, frame), (parentIdx, _) => preview[source[parentIdx].Object.Id]);
                    preview.Add(entry.Object.Id, controller.PreviewCoupledBoundary(Math.Min(frame, controller.DurationFrames),
                        entry.Motion!.IkEnabled, entry.Motion.MorphsEnabled, parents, ControlDisabled(entry.Object.Id, frame),
                        !initializing && !reset && prior >= controller.DurationFrames, ControlTrackWindows(entry.Object.Id, frame)));
                }
            }
            if (!initializing) ApplyControlGravity(source, frame, reset, step);
            IReadOnlyList<Guid>? worldActorIds = pmmControls is null && world is not null ? world.ActorIds : null;
            int maxDegree = Math.Clamp(Environment.ProcessorCount, 1, 16);
            bool canParallelBoundary = !token.CanBeCanceled
                && !coupledSoftBodies
                && !interactiveObjectId.HasValue
                && activeActors is null
                && maxDegree >= 2
                && !PoseEvaluationEventSource.Log.IsEnabled()
                && !AnySoftBodyController(source)
                && AreControllersParallelSafe(source);
            int[] boundaryOrder = topology.TopologicalOrder;

            if (canParallelBoundary)
            {
                PrepareSharedBoundaryLevelsParallel(source, world, topology, boundaryGraph, worldActorIds,
                    requiredParents, frame, prior, reset, initializing, deferRigidSkinning, maxDegree,
                    pending, pendingByIndex, preparedWorldsByIndex, physicalDisabledByIndex,
                    resetAfterPhysicsWorldsByIndex, phase1OutsideParentsByIndex, hasPhase1OutsideParents, nonPhysicalAttached);
            }
            else
            {
                for (int k = 0; k < boundaryOrder.Length; k++)
                {
                    int index = boundaryOrder[k];
                    token.ThrowIfCancellationRequested(); var entry = source[index]; var controller = entry.Controller;
                    if (controller is null) continue;
                    if (activeActors is not null && !activeActors.Contains(entry.Object.Id)) continue;
                    var motion = entry.Motion!; double local = Math.Min(frame, controller.DurationFrames);
                    bool edited = interactiveObjectId == entry.Object.Id;
                    bool frozen = !initializing && !reset && !edited && prior >= controller.DurationFrames;
                    var overrides = edited ? localBoneOverrides : null;
                    var bindings = topology.BindingsByIndex[index];
                    var parentIndices = topology.ParentActorIndicesByBinding[index];
                    var groundParents = boundaryGraph is not null ? boundaryGraph.GroundByIndex[index] : ControlGroundParents(entry.Object.Id, frame);
                    var trackWindows = boundaryGraph is not null ? boundaryGraph.TrackWindowsByIndex[index] : ControlTrackWindows(entry.Object.Id, frame);
                    bool hasBindings = bindings.Length != 0;
                    bool attached = hasBindings || groundParents.Length != 0;
                    bool trackWindowChanged = pmmControls is not null && controller.SetOutsideParentTrackWindows(trackWindows);
                    var actorWorld = ActorWorld(entry, world);
                    bool physical = pmmControls is null
                        ? (worldActorIds is not null && ContainsGuid(worldActorIds, entry.Object.Id))
                        : (actorWorld is not null && actorWorld.ActorIds.Contains(entry.Object.Id));
                    if (!physical && deferRigidSkinning && requiredParents is not null && frozen && !initializing && !reset
                        && !interactiveObjectId.HasValue && !requiredParents.Contains(entry.Object.Id)
                        && controller.CanDeferFrozenAttachmentBoundary(bindings, groundParents, trackWindowChanged))
                    {
                        continue;
                    }
                    if (interactiveObjectId.HasValue && frozen && !attached && !physical && !trackWindowChanged
                        && controller.CanRetainCompleteRigidInteractivePose(local, motion.IkEnabled, motion.MorphsEnabled))
                    {
                        // Retain the immutable local scene of stopped rooms/props. Descendants still
                        // receive its completed bones in dependency order, and every physical world
                        // keeps its normal step. Editing this actor or attaching it takes the full path.
                        preparedWorldsByIndex[index] = controller.BoneWorldMatrices;
                        continue;
                    }
                    Dictionary<int, Matrix4x4>? parents = attached
                        ? BuildBoundaryOutsideParents(source, entry, bindings, parentIndices, groundParents,
                            (parentIdx, parentBone) =>
                            {
                                var parentDisabled = physicalDisabledByIndex[parentIdx];
                                Matrix4x4[]? resetWorlds = null;
                                if (reset && parentDisabled is not null
                                    && source[parentIdx].Controller!.IsBoneAffectedByAfterPhysics(parentBone))
                                    resetWorlds = EnsureResetAfterPhysicsParentWorlds(source, parentIdx,
                                        physicalDisabledByIndex, resetAfterPhysicsWorldsByIndex!, frame,
                                        interactiveObjectId, localBoneOverrides);
                                return ResolvePhase1ParentBoneWorlds(source[parentIdx], parentBone,
                                    preparedWorldsByIndex[parentIdx], parentDisabled, resetWorlds);
                            })
                        : null;
                    if (attached || pmmControls is not null)
                    {
                        phase1OutsideParentsByIndex[index] = parents;
                        hasPhase1OutsideParents[index] = true;
                    }
                    // A frozen attachment can move during a fixed-clock gesture. Its immutable anchor
                    // must rebase on every boundary, but the matching skin belongs to completion below.
                    bool deferFrozenSkin = interactiveObjectId.HasValue || deferRigidSkinning || controller.HasUnpublishedRigidBonePose;
                    var frozenFrame = ApplyPhase1OutsideParents(entry, local, frozen, attached, physical, deferFrozenSkin,
                        trackWindowChanged, parents, out bool fullEvalFrozen, out bool sampleFrozen, out bool publishedFrozenDisplay);
                    if (frozenFrame is not null)
                        source[index] = entry = entry with { LocalFrame = frozenFrame };
                    if (interactiveObjectId.HasValue && sampleFrozen && attached && !physical && !edited && !trackWindowChanged
                        && controller.HasCompleteRigidFrozenAttachmentPose(local, motion.IkEnabled, motion.MorphsEnabled, parents!))
                    {
                        // Refresh above proved that the current parent matrices still match a
                        // complete rigid display. Retain its scene, but keep supplying its solved
                        // bones to descendants in the same dependency order and step every world.
                        if (hasBindings) nonPhysicalAttached![index] = new(local, sampleFrozen, deferFrozenSkin, overrides);
                        preparedWorldsByIndex[index] = controller.BoneWorldMatrices;
                        continue;
                    }
                    if (!physical)
                    {
                        if (requiredParents is not null && !frozen && !attached && !reset
                            && frame < controller.DurationFrames && motion.Model.SoftBodies.Count == 0
                            && !requiredParents.Contains(entry.Object.Id))
                        {
                            // No solver, soft state, attachment or consumer can observe these
                            // intermediate bones. The final displayed boundary evaluates them
                            // normally. Never skip crossing an actor's end: later frozen parent
                            // rebasing requires a successfully completed tail, including PMM
                            // slots first activated after that local animation has ended.
                            if (previewWorking is not null) previewWorking[entry.Object.Id] = local;
                            continue;
                        }
                        if (!coupledSoftBodies && !initializing && !reset && !interactiveObjectId.HasValue
                            && !trackWindowChanged && !fullEvalFrozen
                            && controller.TryRetainCompleteRigidPlaybackPose(local, motion.IkEnabled, motion.MorphsEnabled))
                        {
                            // Every control/parent boundary above still ran. A complete constant
                            // local result also supplies the exact stable bones to descendants;
                            // retain both products while advancing the controller's cache clock.
                            if (hasBindings) nonPhysicalAttached![index] = new(local, sampleFrozen, deferFrozenSkin, overrides);
                            if (!deferRigidSkinning) previewWorking?.Remove(entry.Object.Id);
                            preparedWorldsByIndex[index] = controller.BoneWorldMatrices;
                            continue;
                        }
                        if (controller.SoftBodiesEnabled && hasBindings && !interactiveObjectId.HasValue)
                        {
                            var softPose = controller.BeginSharedPhysicsSample(local, motion.IkEnabled, motion.MorphsEnabled, frozen: sampleFrozen);
                            try
                            {
                                softOnly ??= [];
                                if (softOnlyByIndex is null) { softOnlyByIndex = new int[actorCount]; Array.Fill(softOnlyByIndex, -1); }
                                softOnlyByIndex[index] = softOnly.Count;
                                softOnly.Add((index, softPose, frozen));
                                preparedWorldsByIndex[index] = softPose.BeforePhysicsBoneWorlds;
                            }
                            catch { softPose.Dispose(); throw; }
                            continue;
                        }
                        if (interactiveObjectId.HasValue)
                        {
                            // The warm interactive route excludes soft bodies and keeps authored animation
                            // at one clock. A nonphysical PMM parent still needs its raw local draft here.
                            CharacterAnimationController.SharedPreparedPhysicsSample pose;
                            using (PoseEvaluationMetrics.Measure("boundary.nonphysical.interactive-prepare", entry.Object.Id))
                                pose = controller.BeginSharedPhysicsSample(local, motion.IkEnabled, motion.MorphsEnabled,
                                    frozen: sampleFrozen, localBoneOverrides: overrides);
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
                            using var softPose = controller.BeginSharedPhysicsSample(local, motion.IkEnabled, motion.MorphsEnabled, frozen: sampleFrozen);
                            double? softElapsed = reset ? null : step && !frozen ? CharacterPhysicsSession.FixedStepSeconds : 0;
                            source[index] = entry with { LocalFrame = SharedLocalFrame(entry,
                                softPose.Complete(softPose.BeforePhysicsBoneWorlds, [], softElapsed)) };
                        }
                        else if (sampleFrozen && controller.HasUnpublishedRigidBonePose)
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
                        else if (!publishedFrozenDisplay && (!sampleFrozen || reset || attached))
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
                        if (hasBindings) nonPhysicalAttached![index] = new(local, sampleFrozen, deferFrozenSkin, overrides);
                        if (!deferRigidSkinning) previewWorking?.Remove(entry.Object.Id);
                        preparedWorldsByIndex[index] = controller.BoneWorldMatrices;
                        continue;
                    }
                    var disabled = boundaryGraph?.DisabledByIndex[index] ?? (pmmControls is null ? EmptyDisabledBones : ControlDisabled(entry.Object.Id, frame));
                    physicalDisabledByIndex[index] = disabled;
                    CharacterAnimationController.SharedPreparedPhysicsSample preparation;
                    using (PoseEvaluationMetrics.Measure("boundary.physical.prepare", entry.Object.Id))
                        preparation = controller.BeginSharedPhysicsSample(local, motion.IkEnabled, motion.MorphsEnabled, disabled, sampleFrozen, overrides);
                    preparedWorldsByIndex[index] = preparation.BeforePhysicsBoneWorlds;
                    // Only this actor's terminal skin is delayed. The existing middle-boundary
                    // flag also affects nonphysical parents, so it must not be widened here.
                    // Children consume its before-physics bones above; rigid worlds do not read its skin.
                    bool deferPreview = previewWorking is not null && !deferRigidSkinning && !initializing && !reset
                        && !interactiveObjectId.HasValue && !frozen && !attached && local < controller.DurationFrames
                        && requiredParents is not null
                        && AppContext.TryGetSwitch(PhysicalPreviewSwitch, out bool enabled) && enabled;
                    pendingByIndex[index] = pending.Count;
                    pending.Add((index, preparation, new(entry.Object.Id, preparation.BeforePhysicsBoneWorlds)
                    { Frozen = frozen, Impulses = initializing || interactiveObjectId.HasValue ? [] : preparation.Impulses, DisabledBoneIndices = disabled }, deferPreview));
                }
            }

            var resultsByEntryIndex = new Matrix4x4[]?[actorCount];
            if (pending.Count > 0)
            {
                if (controlledWorlds.Count == 0)
                {
                    var batchWorld = ActorWorld(source[pending[0].Index], world)!;
                    var samples = new SharedPhysicsActorSample[pending.Count];
                    Vector3[]?[]? targets = coupledSoftBodies ? new Vector3[]?[pending.Count] : null;
                    for (int p = 0; p < pending.Count; p++)
                    {
                        samples[p] = pending[p].Physics;
                        if (targets is not null)
                            targets[p] = source[pending[p].Index].Controller!.PrepareCoupledTargets(pending[p].Pose);
                    }
                    if (reset) batchWorld.ResetAll(samples);
                    if (targets is not null)
                    {
                        for (int p = 0; p < pending.Count; p++)
                        {
                            var item = pending[p];
                            var entry = source[item.Index];
                            if (entry.Controller!.SupportsSoftBodies && reset)
                                entry.Controller.ResetSharedCoupledSoftBodies(batchWorld, entry.Object.Id, entry.Object.World, item.Pose);
                            var pins = reset ? entry.Controller.PrepareCoupledTargets(item.Pose) : targets[p];
                            entry.Controller.PublishCoupledTargets(pins, item.Physics.Frozen, step || reset);
                        }
                    }
                    if (currentControlGravity is { } gravity) batchWorld.SetGravity(gravity);
                    try
                    {
                        using (PoseEvaluationMetrics.Measure(step ? "boundary.world.step" : "boundary.world.reflect"))
                        {
                            var stepped = step ? batchWorld.Step(samples) : batchWorld.Reflect(samples);
                            for (int p = 0; p < pending.Count; p++)
                            {
                                int entryIndex = pending[p].Index;
                                resultsByEntryIndex[entryIndex] = stepped[source[entryIndex].Object.Id];
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        EnrichPhysicsStepException(error, source, -1, pending, frame, prior);
                        throw;
                    }
                }
                else if (canParallelBoundary && pending.Count >= 2)
                {
                    int degree = Math.Min(pending.Count, maxDegree);
                    var stepErrors = new ExceptionDispatchInfo?[pending.Count];
                    Parallel.For(0, pending.Count, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = degree,
                        TaskScheduler = TaskScheduler.Default,
                    }, p =>
                    {
                        var item = pending[p];
                        var entry = source[item.Index];
                        var actorWorld = controlledWorlds[entry.Object.Id];
                        var samples = new[] { item.Physics };
                        try
                        {
                            if (reset) actorWorld.ResetAll(samples);
                            if (currentControlGravity is { } gravity) actorWorld.SetGravity(gravity);
                            var stepped = step ? actorWorld.Step(samples) : actorWorld.Reflect(samples);
                            resultsByEntryIndex[item.Index] = stepped[entry.Object.Id];
                        }
                        catch (Exception error)
                        {
                            EnrichPhysicsStepException(error, source, item.Index, null, frame, prior);
                            stepErrors[p] = ExceptionDispatchInfo.Capture(error);
                        }
                    });
                    for (int p = 0; p < pending.Count; p++) stepErrors[p]?.Throw();
                }
                else
                {
                    for (int p = 0; p < pending.Count; p++)
                    {
                        var item = pending[p];
                        var entry = source[item.Index];
                        var actorWorld = ActorWorld(entry, world)!;
                        var samples = new[] { item.Physics };
                        var targets = coupledSoftBodies ? entry.Controller!.PrepareCoupledTargets(item.Pose) : null;
                        if (reset) actorWorld.ResetAll(samples);
                        if (coupledSoftBodies && entry.Controller!.SupportsSoftBodies)
                        {
                            if (reset)
                                entry.Controller.ResetSharedCoupledSoftBodies(actorWorld, entry.Object.Id, entry.Object.World, item.Pose);
                            var pins = reset ? entry.Controller.PrepareCoupledTargets(item.Pose) : targets;
                            entry.Controller.PublishCoupledTargets(pins, item.Physics.Frozen, step || reset);
                        }
                        if (currentControlGravity is { } gravity) actorWorld.SetGravity(gravity);
                        try
                        {
                            using (PoseEvaluationMetrics.Measure(step ? "boundary.world.step" : "boundary.world.reflect"))
                            {
                                var stepped = step ? actorWorld.Step(samples) : actorWorld.Reflect(samples);
                                resultsByEntryIndex[item.Index] = stepped[entry.Object.Id];
                            }
                        }
                        catch (Exception error)
                        {
                            EnrichPhysicsStepException(error, source, item.Index, null, frame, prior);
                            throw;
                        }
                    }
                }
            }

            if (activeParents.Length == 0 && (softOnly is null || softOnly.Count == 0))
            {
                if (canParallelBoundary && pending.Count >= 2)
                {
                    int degree = Math.Min(pending.Count, maxDegree);
                    var completeFrames = new OriginalFrameScene?[pending.Count];
                    var completeErrors = new ExceptionDispatchInfo?[pending.Count];
                    Parallel.For(0, pending.Count, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = degree,
                        TaskScheduler = TaskScheduler.Default,
                    }, p =>
                    {
                        var item = pending[p];
                        var entry = source[item.Index];
                        var boneWorlds = resultsByEntryIndex[item.Index]!;
                        try
                        {
                            completeFrames[p] = CompletePendingSample(entry, world, item, boneWorlds, reset, step, deferRigidSkinning);
                        }
                        catch (Exception error)
                        {
                            completeErrors[p] = ExceptionDispatchInfo.Capture(error);
                        }
                    });
                    for (int p = 0; p < pending.Count; p++)
                    {
                        completeErrors[p]?.Throw();
                        CommitPendingSampleCompletion(source, pending[p], completeFrames[p], frame, deferRigidSkinning);
                    }
                }
                else
                {
                    for (int p = 0; p < pending.Count; p++)
                    {
                        var item = pending[p];
                        var entry = source[item.Index];
                        var boneWorlds = resultsByEntryIndex[item.Index]!;
                        var completedFrame = CompletePendingSample(entry, world, item, boneWorlds, reset, step, deferRigidSkinning);
                        CommitPendingSampleCompletion(source, item, completedFrame, frame, deferRigidSkinning);
                    }
                }
            }
            else if (canParallelBoundary)
            {
                CompletePhase3LevelsParallel(source, world, topology, boundaryGraph, frame, reset, step, deferRigidSkinning,
                    maxDegree, pending, pendingByIndex, physicalDisabledByIndex, phase1OutsideParentsByIndex,
                    hasPhase1OutsideParents, nonPhysicalAttached, resultsByEntryIndex);
            }
            else
            {
                var updatedInPhase3 = new bool[actorCount];
                for (int k = 0; k < boundaryOrder.Length; k++)
                {
                    int index = boundaryOrder[k];
                    var entry = source[index];
                    if (entry.Controller is null) continue;
                    int p = pendingByIndex[index];
                    int s = softOnlyByIndex is null ? -1 : softOnlyByIndex[index];
                    bool hasNonPhysical = nonPhysicalAttached is not null && nonPhysicalAttached[index].HasValue;
                    if (p < 0 && s < 0 && !hasNonPhysical) continue;
                    var bindings = topology.BindingsByIndex[index];
                    var parentIndices = topology.ParentActorIndicesByBinding[index];
                    bool hasPhase3Parent = false;
                    for (int b = 0; b < bindings.Length; b++)
                    {
                        int parentIdx = parentIndices[b];
                        if (updatedInPhase3[parentIdx]) { hasPhase3Parent = true; break; }
                        if (physicalDisabledByIndex[parentIdx] is not null
                            && source[parentIdx].Controller!.IsBoneAffectedByAfterPhysics(bindings[b].ParentBoneIndex))
                        {
                            hasPhase3Parent = true;
                            break;
                        }
                    }
                    Dictionary<int, Matrix4x4>? afterParents = null;
                    if (hasPhase3Parent)
                    {
                        var groundParents = boundaryGraph is not null ? boundaryGraph.GroundByIndex[index] : ControlGroundParents(entry.Object.Id, frame);
                        var postParents = BuildBoundaryOutsideParents(source, entry, bindings, parentIndices, groundParents,
                            (parentIdx, _) => source[parentIdx].Controller!.BoneWorldMatrices);
                        if (!hasPhase1OutsideParents[index]
                            || !CharacterAnimationController.OutsideParentsEqual(phase1OutsideParentsByIndex[index], postParents))
                            afterParents = postParents;
                    }
                    if (p >= 0)
                    {
                        var item = pending[p];
                        var boneWorlds = resultsByEntryIndex[index]!;
                        var completedFrame = CompletePendingSample(entry, world, item, boneWorlds, reset, step, deferRigidSkinning, afterParents);
                        CommitPendingSampleCompletion(source, item, completedFrame, frame, deferRigidSkinning);
                        if (afterParents is not null) updatedInPhase3[index] = true;
                    }
                    else if (s >= 0)
                    {
                        var softItem = softOnly![s];
                        double? softElapsed = reset ? null : step && !softItem.Frozen ? CharacterPhysicsSession.FixedStepSeconds : 0;
                        source[index] = entry with { LocalFrame = SharedLocalFrame(entry,
                            softItem.Pose.Complete(softItem.Pose.BeforePhysicsBoneWorlds, [], softElapsed, afterParents)) };
                        if (afterParents is not null) updatedInPhase3[index] = true;
                    }
                    else if (afterParents is not null && hasNonPhysical)
                    {
                        var info = nonPhysicalAttached![index]!.Value;
                        var localFrame = EvaluateNonPhysicalAfterPhysics(entry, info, afterParents, interactiveObjectId, deferRigidSkinning);
                        if (localFrame is not null) source[index] = entry with { LocalFrame = localFrame };
                        if (!deferRigidSkinning) previewWorking?.Remove(entry.Object.Id);
                        updatedInPhase3[index] = true;
                    }
                }
            }
        }
        finally
        {
            foreach (var item in pending) item.Pose.Dispose();
            if (softOnly is not null)
                foreach (var item in softOnly) item.Pose.Dispose();
        }
    }

    private OriginalFrameScene? ApplyPhase1OutsideParents(Entry entry, double local, bool frozen, bool attached,
        bool physical, bool deferFrozenSkin, bool trackWindowChanged, Dictionary<int, Matrix4x4>? parents,
        out bool fullEvalFrozen, out bool sampleFrozen, out bool publishedFrozenDisplay)
    {
        var controller = entry.Controller!;
        var motion = entry.Motion!;
        fullEvalFrozen = frozen && (attached || pmmControls is not null)
            && controller.RequiresFullEvaluationForFrozenOutsideParents(parents, trackWindowChanged);
        sampleFrozen = frozen && !fullEvalFrozen;
        publishedFrozenDisplay = false;
        if (fullEvalFrozen)
        {
            controller.SetOutsideParents(parents);
            return null;
        }
        if (pmmControls is not null && frozen)
        {
            bool updated = controller.RefreshSharedFrozenOutsideParents(parents ?? [], deferFrozenSkin);
            if (updated && !physical)
            {
                publishedFrozenDisplay = true;
                return LocalFrameIfPoseChanged(entry, controller.SampleAt(local, motion.IkEnabled, motion.MorphsEnabled));
            }
            return null;
        }
        if (frozen && attached)
        {
            if (deferFrozenSkin) controller.RefreshSharedFrozenOutsideParents(parents!, deferSkinning: true);
            else
            {
                var refreshed = controller.RefreshFrozenOutsideParents(parents!);
                if (!physical)
                {
                    publishedFrozenDisplay = true;
                    return LocalFrameIfPoseChanged(entry, refreshed);
                }
            }
            return null;
        }
        if (attached || pmmControls is not null)
            controller.SetOutsideParents(parents);
        return null;
    }

    private OriginalFrameScene? CompletePendingSample(Entry entry, SharedCharacterPhysicsSession? world,
        (int Index, CharacterAnimationController.SharedPreparedPhysicsSample Pose, SharedPhysicsActorSample Physics, bool DeferPreview) item,
        Matrix4x4[] boneWorlds, bool reset, bool step, bool deferRigidSkinning, Dictionary<int, Matrix4x4>? afterParents = null)
    {
        if (deferRigidSkinning)
        {
            using var completionMeasure = PoseEvaluationMetrics.Measure("boundary.physical.complete-bones", entry.Object.Id);
            item.Pose.CompleteRigidBoneWorlds(boneWorlds, afterParents);
            return null;
        }
        if (item.DeferPreview)
        {
            using var completionMeasure = PoseEvaluationMetrics.Measure("boundary.physical.complete-deferred-bones", entry.Object.Id);
            item.Pose.CompleteRigidBoneWorldsAndDefer(boneWorlds, afterParents);
            return null;
        }
        double? softElapsed = reset ? null : step && !item.Physics.Frozen ? CharacterPhysicsSession.FixedStepSeconds : 0;
        using var fullCompletionMeasure = PoseEvaluationMetrics.Measure("boundary.physical.complete-full", entry.Object.Id);
        var softRigidWorlds = entry.Controller!.SoftBodiesEnabled ? SharedSoftRigidWorlds(entry, ActorWorld(entry, world)!) : [];
        return SharedLocalFrame(entry, item.Pose.Complete(boneWorlds, softRigidWorlds, softElapsed, afterParents));
    }

    private void CommitPendingSampleCompletion(Entry[] source,
        (int Index, CharacterAnimationController.SharedPreparedPhysicsSample Pose, SharedPhysicsActorSample Physics, bool DeferPreview) item,
        OriginalFrameScene? completedFrame, double frame, bool deferRigidSkinning)
    {
        if (deferRigidSkinning) return;
        var entry = source[item.Index];
        if (item.DeferPreview)
        {
            previewWorking![entry.Object.Id] = Math.Min(frame, entry.Controller!.DurationFrames);
            return;
        }
        source[item.Index] = entry with { LocalFrame = completedFrame! };
        previewWorking?.Remove(entry.Object.Id);
    }

    private enum Phase1LevelWorkKind { NonPhysicalWork, PhysicalPrepare }

    private readonly record struct Phase1LevelWorkItem(
        int EntryIndex,
        Phase1LevelWorkKind Kind,
        double LocalFrame,
        bool Frozen,
        SceneOutsideParentBinding[] Bindings,
        int[] ParentIndices,
        ScenePmmGroundOutsideParent[] GroundParents,
        bool TrackWindowChanged,
        bool Physical,
        IReadOnlySet<int>? Disabled);

    private readonly record struct Phase3LevelWorkItem(
        int EntryIndex,
        int PendingSlot,
        bool HasPhase3Parent);

    private static bool AnySoftBodyController(Entry[] source)
    {
        for (int i = 0; i < source.Length; i++)
            if (source[i].Controller?.SoftBodiesEnabled == true) return true;
        return false;
    }

    private static bool AreControllersParallelSafe(Entry[] source)
    {
        if (source.Length < 2) return false;
        var controllers = new HashSet<CharacterAnimationController>(source.Length, ReferenceEqualityComparer.Instance);
        var models = new HashSet<PmxModel>(ReferenceEqualityComparer.Instance);
        int activeCount = 0;
        for (int i = 0; i < source.Length; i++)
        {
            var entry = source[i];
            if (entry.Controller is not { } controller) continue;
            activeCount++;
            if (!controllers.Add(controller)
                || entry.Motion is not { } motion
                || (models.Add(motion.Model) && !StandardPreviewModel(motion.Model)))
                return false;
        }
        return activeCount >= 2;
    }

    private static bool ContainsGuid(IReadOnlyList<Guid> ids, Guid id)
    {
        for (int i = 0; i < ids.Count; i++)
            if (ids[i] == id) return true;
        return false;
    }

    private void PrepareSharedBoundaryLevelsParallel(Entry[] source, SharedCharacterPhysicsSession? world,
        SharedBoundaryTopology topology, SharedBoundaryGraph? boundaryGraph, IReadOnlyList<Guid>? worldActorIds,
        HashSet<Guid>? requiredParents, double frame, double prior, bool reset, bool initializing,
        bool deferRigidSkinning, int maxDegree,
        List<(int Index, CharacterAnimationController.SharedPreparedPhysicsSample Pose, SharedPhysicsActorSample Physics, bool DeferPreview)> pending,
        int[] pendingByIndex, IReadOnlyList<Matrix4x4>?[] preparedWorldsByIndex, IReadOnlySet<int>?[] physicalDisabledByIndex,
        Matrix4x4[]?[]? resetAfterPhysicsWorldsByIndex, Dictionary<int, Matrix4x4>?[] phase1OutsideParentsByIndex,
        bool[] hasPhase1OutsideParents, NonPhysicalAttachedState?[]? nonPhysicalAttached)
    {
        bool physicalPreviewSwitchEnabled = previewWorking is not null && !deferRigidSkinning && !initializing && !reset
            && requiredParents is not null && AppContext.TryGetSwitch(PhysicalPreviewSwitch, out bool enabled) && enabled;
        var levelWork = new List<Phase1LevelWorkItem>();
        for (int levelIndex = 0; levelIndex < topology.Levels.Length; levelIndex++)
        {
            int[] level = topology.Levels[levelIndex];
            levelWork.Clear();
            for (int k = 0; k < level.Length; k++)
            {
                int index = level[k];
                var entry = source[index];
                var controller = entry.Controller;
                if (controller is null) continue;
                var motion = entry.Motion!;
                double local = Math.Min(frame, controller.DurationFrames);
                bool frozen = !initializing && !reset && prior >= controller.DurationFrames;
                var bindings = topology.BindingsByIndex[index];
                var parentIndices = topology.ParentActorIndicesByBinding[index];
                var groundParents = boundaryGraph is not null ? boundaryGraph.GroundByIndex[index] : ControlGroundParents(entry.Object.Id, frame);
                var trackWindows = boundaryGraph is not null ? boundaryGraph.TrackWindowsByIndex[index] : ControlTrackWindows(entry.Object.Id, frame);
                bool hasBindings = bindings.Length != 0;
                bool attached = hasBindings || groundParents.Length != 0;
                bool trackWindowChanged = pmmControls is not null && controller.SetOutsideParentTrackWindows(trackWindows);
                var actorWorld = ActorWorld(entry, world);
                bool physical = pmmControls is null
                    ? (worldActorIds is not null && ContainsGuid(worldActorIds, entry.Object.Id))
                    : (actorWorld is not null && actorWorld.ActorIds.Contains(entry.Object.Id));

                if (!physical && deferRigidSkinning && requiredParents is not null && frozen && !initializing && !reset
                    && !requiredParents.Contains(entry.Object.Id)
                    && controller.CanDeferFrozenAttachmentBoundary(bindings, groundParents, trackWindowChanged))
                {
                    continue;
                }

                if (!physical)
                {
                    if (requiredParents is not null && !frozen && !attached && !reset
                        && frame < controller.DurationFrames && motion.Model.SoftBodies.Count == 0
                        && !requiredParents.Contains(entry.Object.Id))
                    {
                        if (pmmControls is not null) controller.SetOutsideParents(null);
                        if (previewWorking is not null) previewWorking[entry.Object.Id] = local;
                        continue;
                    }
                    if (frozen && !reset && !attached && !trackWindowChanged
                        && (!controller.HasUnpublishedRigidBonePose || deferRigidSkinning)
                        && (pmmControls is null || !controller.HasAnyOutsideParents))
                    {
                        if (pmmControls is not null)
                        {
                            phase1OutsideParentsByIndex[index] = null;
                            hasPhase1OutsideParents[index] = true;
                        }
                        if (!deferRigidSkinning) previewWorking?.Remove(entry.Object.Id);
                        preparedWorldsByIndex[index] = controller.BoneWorldMatrices;
                        continue;
                    }
                }

                // Warm lazy after-physics bone maps and reset post-physics parent palettes on the
                // calling thread before launching level workers that may share the same parent actor.
                for (int b = 0; b < bindings.Length; b++)
                {
                    int parentIdx = parentIndices[b];
                    if (physicalDisabledByIndex[parentIdx] is not null
                        && source[parentIdx].Controller!.IsBoneAffectedByAfterPhysics(bindings[b].ParentBoneIndex)
                        && reset)
                    {
                        _ = EnsureResetAfterPhysicsParentWorlds(source, parentIdx,
                            physicalDisabledByIndex, resetAfterPhysicsWorldsByIndex!, frame, null, null);
                    }
                }

                var disabled = physical
                    ? (boundaryGraph?.DisabledByIndex[index] ?? (pmmControls is null ? EmptyDisabledBones : ControlDisabled(entry.Object.Id, frame)))
                    : null;
                levelWork.Add(new(index, physical ? Phase1LevelWorkKind.PhysicalPrepare : Phase1LevelWorkKind.NonPhysicalWork,
                    local, frozen, bindings, parentIndices, groundParents, trackWindowChanged, physical, disabled));
            }

            int workCount = levelWork.Count;
            if (workCount == 0) continue;
            var localFrames = new OriginalFrameScene?[workCount];
            var preparations = new CharacterAnimationController.SharedPreparedPhysicsSample?[workCount];
            var phase1ParentsOut = new Dictionary<int, Matrix4x4>?[workCount];
            var hasPhase1ParentsOut = new bool[workCount];
            var nonPhysicalAttachedOut = new NonPhysicalAttachedState?[workCount];
            var errors = new ExceptionDispatchInfo?[workCount];
            try
            {
                void ExecuteWorkItem(int w)
                {
                    var item = levelWork[w];
                    var entry = source[item.EntryIndex];
                    var controller = entry.Controller!;
                    var motion = entry.Motion!;
                    try
                    {
                        bool hasBindings = item.Bindings.Length != 0;
                        bool attached = hasBindings || item.GroundParents.Length != 0;
                        Dictionary<int, Matrix4x4>? parents = attached
                            ? BuildBoundaryOutsideParents(source, entry, item.Bindings, item.ParentIndices, item.GroundParents,
                                (parentIdx, parentBone) => ResolvePhase1ParentBoneWorlds(source[parentIdx], parentBone,
                                    preparedWorldsByIndex[parentIdx], physicalDisabledByIndex[parentIdx],
                                    resetAfterPhysicsWorldsByIndex?[parentIdx]))
                            : null;
                        if (attached || pmmControls is not null)
                        {
                            phase1ParentsOut[w] = parents;
                            hasPhase1ParentsOut[w] = true;
                        }
                        bool deferFrozenSkin = deferRigidSkinning || controller.HasUnpublishedRigidBonePose;
                        if (ApplyPhase1OutsideParents(entry, item.LocalFrame, item.Frozen, attached, item.Physical, deferFrozenSkin,
                            item.TrackWindowChanged, parents, out bool fullEvalFrozen, out bool sampleFrozen, out bool publishedFrozenDisplay) is { } frozenFrame)
                        {
                            localFrames[w] = frozenFrame;
                        }

                        if (item.Kind == Phase1LevelWorkKind.NonPhysicalWork)
                        {
                            if (!initializing && !reset && !item.TrackWindowChanged && !fullEvalFrozen
                                && controller.TryRetainCompleteRigidPlaybackPose(item.LocalFrame, motion.IkEnabled, motion.MorphsEnabled))
                            {
                                if (hasBindings) nonPhysicalAttachedOut[w] = new(item.LocalFrame, sampleFrozen, deferFrozenSkin, null);
                                return;
                            }
                            if (sampleFrozen && controller.HasUnpublishedRigidBonePose)
                            {
                                if (!deferRigidSkinning)
                                {
                                    using var frozenPose = controller.BeginSharedPhysicsSample(item.LocalFrame, motion.IkEnabled, motion.MorphsEnabled, frozen: true);
                                    localFrames[w] = SharedLocalFrame(entry, frozenPose.Complete(frozenPose.BeforePhysicsBoneWorlds));
                                }
                            }
                            else if (!publishedFrozenDisplay && (!sampleFrozen || reset || attached))
                            {
                                if (deferRigidSkinning)
                                    controller.SampleRigidInitializationBoneWorlds(item.LocalFrame, motion.IkEnabled, motion.MorphsEnabled);
                                else
                                    localFrames[w] = SharedLocalFrame(entry, controller.SampleAt(item.LocalFrame, motion.IkEnabled, motion.MorphsEnabled));
                            }
                            if (hasBindings) nonPhysicalAttachedOut[w] = new(item.LocalFrame, sampleFrozen, deferFrozenSkin, null);
                        }
                        else
                        {
                            preparations[w] = controller.BeginSharedPhysicsSample(item.LocalFrame, motion.IkEnabled, motion.MorphsEnabled,
                                item.Disabled, sampleFrozen, null);
                        }
                    }
                    catch (Exception error)
                    {
                        errors[w] = ExceptionDispatchInfo.Capture(error);
                    }
                }

                if (workCount == 1)
                    ExecuteWorkItem(0);
                else
                    Parallel.For(0, workCount, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Min(workCount, maxDegree),
                        TaskScheduler = TaskScheduler.Default,
                    }, ExecuteWorkItem);

                for (int w = 0; w < workCount; w++)
                {
                    errors[w]?.Throw();
                    var item = levelWork[w];
                    int index = item.EntryIndex;
                    var entry = source[index];
                    var controller = entry.Controller!;
                    if (hasPhase1ParentsOut[w])
                    {
                        phase1OutsideParentsByIndex[index] = phase1ParentsOut[w];
                        hasPhase1OutsideParents[index] = true;
                    }
                    if (item.Kind == Phase1LevelWorkKind.NonPhysicalWork)
                    {
                        if (localFrames[w] is { } localFrame)
                            source[index] = entry with { LocalFrame = localFrame };
                        if (nonPhysicalAttachedOut[w] is { } attachedState)
                            nonPhysicalAttached![index] = attachedState;
                        if (!deferRigidSkinning) previewWorking?.Remove(entry.Object.Id);
                        preparedWorldsByIndex[index] = controller.BoneWorldMatrices;
                    }
                    else
                    {
                        var preparation = preparations[w]!;
                        preparations[w] = null;
                        physicalDisabledByIndex[index] = item.Disabled;
                        preparedWorldsByIndex[index] = preparation.BeforePhysicsBoneWorlds;
                        bool deferPreview = physicalPreviewSwitchEnabled && !item.Frozen
                            && item.Bindings.Length == 0 && item.GroundParents.Length == 0
                            && item.LocalFrame < controller.DurationFrames;
                        pendingByIndex[index] = pending.Count;
                        pending.Add((index, preparation, new(entry.Object.Id, preparation.BeforePhysicsBoneWorlds)
                        { Frozen = item.Frozen, Impulses = initializing ? [] : preparation.Impulses, DisabledBoneIndices = item.Disabled! }, deferPreview));
                    }
                }
            }
            finally
            {
                for (int w = 0; w < preparations.Length; w++) preparations[w]?.Dispose();
            }
        }
    }

    private void CompletePhase3LevelsParallel(Entry[] source, SharedCharacterPhysicsSession? world, SharedBoundaryTopology topology,
        SharedBoundaryGraph? boundaryGraph, double frame, bool reset, bool step, bool deferRigidSkinning, int maxDegree,
        List<(int Index, CharacterAnimationController.SharedPreparedPhysicsSample Pose, SharedPhysicsActorSample Physics, bool DeferPreview)> pending,
        int[] pendingByIndex, IReadOnlySet<int>?[] physicalDisabledByIndex,
        Dictionary<int, Matrix4x4>?[] phase1OutsideParentsByIndex, bool[] hasPhase1OutsideParents,
        NonPhysicalAttachedState?[]? nonPhysicalAttached, Matrix4x4[]?[] resultsByEntryIndex)
    {
        var updatedInPhase3 = new bool[source.Length];
        var levelWork = new List<Phase3LevelWorkItem>();
        for (int levelIndex = 0; levelIndex < topology.Levels.Length; levelIndex++)
        {
            int[] level = topology.Levels[levelIndex];
            levelWork.Clear();
            for (int k = 0; k < level.Length; k++)
            {
                int index = level[k];
                var entry = source[index];
                if (entry.Controller is null) continue;
                int p = pendingByIndex[index];
                bool hasNonPhysical = nonPhysicalAttached is not null && nonPhysicalAttached[index].HasValue;
                if (p < 0 && !hasNonPhysical) continue;
                var bindings = topology.BindingsByIndex[index];
                var parentIndices = topology.ParentActorIndicesByBinding[index];
                bool hasPhase3Parent = false;
                for (int b = 0; b < bindings.Length; b++)
                {
                    int parentIdx = parentIndices[b];
                    if (updatedInPhase3[parentIdx]) { hasPhase3Parent = true; break; }
                    if (physicalDisabledByIndex[parentIdx] is not null
                        && source[parentIdx].Controller!.IsBoneAffectedByAfterPhysics(bindings[b].ParentBoneIndex))
                    {
                        hasPhase3Parent = true;
                        break;
                    }
                }
                if (p < 0 && !hasPhase3Parent) continue;
                levelWork.Add(new(index, p, hasPhase3Parent));
            }

            int workCount = levelWork.Count;
            if (workCount == 0) continue;
            var completeFrames = new OriginalFrameScene?[workCount];
            var updatedOut = new bool[workCount];
            var errors = new ExceptionDispatchInfo?[workCount];

            void ExecuteWorkItem(int w)
            {
                var item = levelWork[w];
                int index = item.EntryIndex;
                var entry = source[index];
                try
                {
                    Dictionary<int, Matrix4x4>? afterParents = null;
                    if (item.HasPhase3Parent)
                    {
                        var bindings = topology.BindingsByIndex[index];
                        var parentIndices = topology.ParentActorIndicesByBinding[index];
                        var groundParents = boundaryGraph is not null ? boundaryGraph.GroundByIndex[index] : ControlGroundParents(entry.Object.Id, frame);
                        var postParents = BuildBoundaryOutsideParents(source, entry, bindings, parentIndices, groundParents,
                            (parentIdx, _) => source[parentIdx].Controller!.BoneWorldMatrices);
                        if (!hasPhase1OutsideParents[index]
                            || !CharacterAnimationController.OutsideParentsEqual(phase1OutsideParentsByIndex[index], postParents))
                            afterParents = postParents;
                    }
                    if (item.PendingSlot >= 0)
                    {
                        var pendingItem = pending[item.PendingSlot];
                        var boneWorlds = resultsByEntryIndex[index]!;
                        completeFrames[w] = CompletePendingSample(entry, world, pendingItem, boneWorlds, reset, step, deferRigidSkinning, afterParents);
                        updatedOut[w] = afterParents is not null;
                    }
                    else if (afterParents is not null)
                    {
                        var info = nonPhysicalAttached![index]!.Value;
                        completeFrames[w] = EvaluateNonPhysicalAfterPhysics(entry, info, afterParents, null, deferRigidSkinning);
                        updatedOut[w] = true;
                    }
                }
                catch (Exception error)
                {
                    errors[w] = ExceptionDispatchInfo.Capture(error);
                }
            }

            if (workCount == 1)
                ExecuteWorkItem(0);
            else
                Parallel.For(0, workCount, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(workCount, maxDegree),
                    TaskScheduler = TaskScheduler.Default,
                }, ExecuteWorkItem);

            for (int w = 0; w < workCount; w++)
            {
                errors[w]?.Throw();
                var item = levelWork[w];
                int index = item.EntryIndex;
                if (item.PendingSlot >= 0)
                {
                    CommitPendingSampleCompletion(source, pending[item.PendingSlot], completeFrames[w], frame, deferRigidSkinning);
                }
                else if (updatedOut[w])
                {
                    if (completeFrames[w] is { } localFrame)
                        source[index] = source[index] with { LocalFrame = localFrame };
                    if (!deferRigidSkinning) previewWorking?.Remove(source[index].Object.Id);
                }
                if (updatedOut[w]) updatedInPhase3[index] = true;
            }
        }
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
