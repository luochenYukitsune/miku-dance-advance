using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

public sealed partial class PmmProjectPlayback
{
    private readonly bool useSharedPhysics;
    private readonly bool requiresControlledPhysics;
    private readonly PmmPhysicsControlTracks physicsControls;
    private readonly Dictionary<(int Model, int PmmBone), int> sharedBoneMapping = [];
    private SharedCharacterPhysicsSession? pmmSharedWorld;
    private CharacterAnimationController.SharedPhysicsStatus? pmmSharedStatus;
    private readonly Dictionary<int, SharedCharacterPhysicsSession> pmmIndependentWorlds = [];
    private readonly Dictionary<int, CharacterAnimationController.SharedPhysicsStatus> pmmIndependentStatuses = [];
    private double pmmSharedAccumulator, pmmSharedSampleFrame;
    private PmmGravityNoise pmmGravityNoise = new();
    public bool UsesSharedPhysicsWorld => useSharedPhysics;
    public long SharedPhysicsStepCount => pmmSharedWorld?.StepCount ?? 0;
    private static Guid SharedActorId(int index) => new(index, 0, 0, new byte[8]);

    private bool RequiresPmmPhysicsControls(PmmDocument document)
    {
        if (document.Models.Any(model => model.BoneStates.Any(state => state.PhysicsDisabled)
            || model.InitialBoneKeyframes.Concat(model.BoneKeyframes).Any(key => key.PhysicsDisabled))) return true;
        if (document.Version == 1) return false;
        return document.Gravity.Keyframes.Count != 0 || physicsControls.SavedGravity.NoiseEnabled
            || physicsControls.GravityAt(0).NoiseEnabled
            || physicsControls.SavedGravity.WorldGravity != physicsControls.GravityAt(0).WorldGravity;
    }

    // The same boundary scheduler drives one shared world or one single-actor world per model.
    // Check object identity before initializing a new world: reusing an injected backend would
    // erase the first actor and turn independent physics into an accidental shared solver.
    private bool EnsurePmmPhysicsWorlds(double frame, bool saved)
    {
        if (pmmSharedStatus is not null) return false;
        var allocated = new HashSet<ICharacterPhysicsBackend>(ReferenceEqualityComparer.Instance);
        SharedCharacterPhysicsSession CreateWorld()
        {
            var backend = physicsFactory(SharedGravityAt(frame, saved), Plan.EffectivePhysics.GroundEnabled ? 0 : null)
                ?? throw new InvalidOperationException("PMM physics factory returned no backend.");
            if (retiredCoupledWorlds.Any(world => world.UsesBackend(backend)))
                throw new InvalidOperationException("A candidate PMM coupled world requires a newly owned backend.");
            if (!allocated.Add(backend)) throw new InvalidOperationException("Independent PMM physics requires a distinct backend instance for each model.");
            if (backend is not ICharacterPhysicsMutableBackend mutable)
            {
                backend.Dispose();
                throw new NotSupportedException("PMM dynamic controls require an incremental ICharacterPhysicsMutableBackend (gravity and body-mode updates).");
            }
            return new(mutable);
        }
        try
        {
            if (useSharedPhysics)
            {
                pmmSharedWorld = CreateWorld(); pmmSharedStatus = new(pmmSharedWorld.BackendName, pmmSharedWorld.Diagnostics);
            }
            else
            {
                foreach (var pair in entries.Where(pair => pair.Value.Source.Model.RigidBodies.Count > 0
                    || coupledSoftBodies && pair.Value.Source.Model.SoftBodies.Count > 0))
                {
                    var world = CreateWorld(); pmmIndependentWorlds.Add(pair.Key, world);
                    pmmIndependentStatuses.Add(pair.Key, new(world.BackendName, world.Diagnostics));
                }
                pmmSharedStatus = new("Independent PMM physics worlds", []);
            }
            pmmSharedSampleFrame = frame; pmmSharedAccumulator = 0;
            return true;
        }
        catch { DisposePmmPhysicsWorlds(); throw; }
    }

    private SharedCharacterPhysicsSession PmmWorld(int id) => useSharedPhysics ? pmmSharedWorld! : pmmIndependentWorlds[id];
    private CharacterAnimationController.SharedPhysicsStatus PmmStatus(int id) => useSharedPhysics ? pmmSharedStatus! : pmmIndependentStatuses[id];
    private IEnumerable<CharacterAnimationController.SharedPhysicsStatus> PmmStatuses()
        => useSharedPhysics ? [pmmSharedStatus!] : pmmIndependentStatuses.Values;
    private void DisposePmmPhysicsWorlds()
    {
        pmmSharedWorld?.Dispose(); pmmSharedWorld = null;
        foreach (var world in pmmIndependentWorlds.Values) world.Dispose();
        pmmIndependentWorlds.Clear(); pmmIndependentStatuses.Clear(); pmmSharedStatus = null;
    }

    private Vector3 SharedGravityAt(double frame, bool saved)
        => Plan.Document.Version == 1 ? Plan.EffectivePhysics.Gravity
            : (saved ? physicsControls!.SavedGravity : physicsControls!.GravityAt(frame)).WorldGravity;
    private PmmGravityPose SharedGravityPose(double frame, bool saved)
        => Plan.Document.Version == 1 ? new(9.8f,-Vector3.UnitY,false,0,Plan.EffectivePhysics.Gravity)
            : saved ? physicsControls!.SavedGravity : physicsControls!.GravityAt(frame);

    private void PrevalidateSharedPhysicsBindings()
    {
        foreach (var entry in entries.Values)
        {
            var source = entry.Source.Source;
            var keyBones = PmmMotionAdapter.ResolveTracks(source.InitialBoneKeyframes, source.BoneKeyframes, source.BoneNames.Count, "physics bone")
                .Where(pair => pair.Key.PhysicsDisabled).Select(pair => pair.Track);
            var savedBones = source.BoneStates.Select((state, index) => (state, index)).Where(pair => pair.state.PhysicsDisabled).Select(pair => pair.index);
            foreach (int bone in keyBones.Concat(savedBones).Distinct()) sharedBoneMapping.Add((source.Index, bone), MapBone(entry.Source, bone));
        }
    }

    private void EvaluatePmmShared(double frame, double elapsed, bool reset, bool ik, bool morphs, bool saved)
    {
        if (coupledSoftBodies && !buildingCoupledGraph && (reset || pmmSharedStatus is null
            || entries.Values.Any(entry => entry.Controller is null || entry.Saved != saved)))
        { RebuildCoupledPmmGraph(frame, ik, morphs, saved); return; }
        if (!reset && frame < CurrentFrame) throw new ArgumentOutOfRangeException(nameof(frame), "Use Seek to move a physical timeline backwards.");
        if (!reset && pmmSharedStatus is not null && (pmmSharedStatus.IsFaulted || PmmStatuses().Any(status => status.IsFaulted)))
            throw new InvalidOperationException("PMM physics failed; use Seek or Reset to recover every world before advancing.");
        if (!saved) elapsed = Math.Min(elapsed, Math.Max(0, (frame - CurrentFrame) / SceneAnimationSession.FramesPerSecond));
        bool created = EnsurePmmPhysicsWorlds(frame, saved);
        PreparePmmControllers(frame, saved, ik, morphs);
        int executed = 0;
        try
        {
            if (reset || created)
            {
                EvaluatePmmSharedBoundary(frame, frame, true, false, ik, morphs, saved);
                pmmSharedAccumulator = 0; pmmSharedSampleFrame = frame;
                pmmSharedStatus!.DiscardedSeconds = 0;
                foreach (var status in PmmStatuses()) status.DiscardedSeconds = 0;
            }
            else
            {
                double available = pmmSharedAccumulator + elapsed;
                int requested = (int)Math.Floor((available + 1e-12) / CharacterPhysicsSession.FixedStepSeconds);
                int steps = Math.Min(CharacterPhysicsSession.MaximumSubsteps, requested);
                for (int i = 0; i < steps; i++)
                {
                    double target = Math.Min(frame, pmmSharedSampleFrame + .25);
                    EvaluatePmmSharedBoundary(target, pmmSharedSampleFrame, false, true, ik, morphs, saved);
                    pmmSharedSampleFrame = target; executed++;
                }
                pmmSharedAccumulator = Math.Max(0, available - requested * CharacterPhysicsSession.FixedStepSeconds);
                if (requested > steps)
                {
                    double discarded = (requested - steps) * CharacterPhysicsSession.FixedStepSeconds;
                    pmmSharedSampleFrame = Math.Min(frame, pmmSharedSampleFrame + discarded * SceneAnimationSession.FramesPerSecond);
                    if (!useSharedPhysics) pmmSharedStatus!.DiscardedSeconds += discarded;
                    foreach (var status in PmmStatuses()) status.DiscardedSeconds += discarded;
                }
                if (frame != pmmSharedSampleFrame || requested == 0) EvaluatePmmSharedBoundary(frame, pmmSharedSampleFrame, false, false, ik, morphs, saved);
            }
            PublishPmmShared(frame, saved);
            pmmSharedStatus!.IsFaulted = false; pmmSharedStatus.LastStepCount = executed;
            foreach (var status in PmmStatuses()) { status.LastStepCount = executed; status.IsFaulted = false; }
        }
        catch { pmmSharedStatus!.IsFaulted = true; foreach (var status in PmmStatuses()) status.IsFaulted = true; throw; }
    }

    private Dictionary<int, Matrix4x4> BuildPmmBoundaryOutsideParents(Entry entry, Dictionary<int, PmmOutsideParent> actorBindings,
        Func<Entry, int, IReadOnlyList<Matrix4x4>> resolveParentWorlds)
    {
        var outside = new Dictionary<int, Matrix4x4>(actorBindings.Count);
        foreach (var pair in actorBindings)
        {
            int sourceBone = MapBone(entry.Source, pair.Key);
            if (pair.Value.ModelIndex == -2)
            {
                if (sourceBone >= 0) outside.Add(sourceBone, Matrix4x4.CreateTranslation(entry.Source.Model.Bones[sourceBone].Position));
                continue;
            }
            var parent = entries[pair.Value.ModelIndex];
            int targetBone = MapBone(parent.Source, pair.Value.BoneIndex);
            var parentWorlds = resolveParentWorlds(parent, targetBone);
            outside.Add(sourceBone, parentWorlds[targetBone]);
        }
        return outside;
    }

    private void EvaluatePmmSharedBoundary(double frame, double priorFrame, bool reset, bool step, bool ik, bool morphs, bool saved)
    {
        uint integer = (uint)Math.Floor(frame);
        var bindings = entries.ToDictionary(pair => pair.Key, pair => ParentsAt(pair.Value.Source.Source, integer, saved));
        var order = new List<int>(); var visiting = new HashSet<int>(); var done = new HashSet<int>();
        void Visit(int id)
        {
            if (done.Contains(id)) return;
            if (!visiting.Add(id)) throw new InvalidDataException("PMM outside-parent model dependency cycle.");
            foreach (var parent in bindings[id].Values.Where(p => p.ModelIndex >= 0)) Visit(parent.ModelIndex);
            visiting.Remove(id); done.Add(id); order.Add(id);
        }
        foreach (int id in entries.OrderBy(pair => pair.Value.Source.Source.TransformOrderIndex).Select(pair => pair.Key)) Visit(id);
        if (coupledSoftBodies)
        {
            var preview = new Dictionary<int, Matrix4x4[]>();
            foreach (int id in order)
            {
                var entry = entries[id]; var controller = entry.Controller!;
                var outside = BuildPmmBoundaryOutsideParents(entry, bindings[id], (parent, _) => preview[parent.Source.Source.Index]);
                var disabled = physicsControls.DisabledBonesAt(id, frame, saved).Select(bone => sharedBoneMapping[(id, bone)]).ToHashSet();
                preview.Add(id, controller.PreviewCoupledBoundary(saved ? 0 : Math.Min(frame, controller.DurationFrames), ik, morphs,
                    outside, disabled, !reset && !saved && priorFrame >= controller.DurationFrames,
                    TrackWindowsAt(id, entry.Source, saved ? 0 : frame, saved)));
            }
        }
        var pending = new List<(int Id, CharacterAnimationController.SharedPreparedPhysicsSample Pose, SharedPhysicsActorSample Input)>();
        var pendingById = new Dictionary<int, (CharacterAnimationController.SharedPreparedPhysicsSample Pose, SharedPhysicsActorSample Input)>();
        var softOnly = new List<(int Id, CharacterAnimationController.SharedPreparedPhysicsSample Pose, bool Frozen)>();
        var softOnlyById = new Dictionary<int, (CharacterAnimationController.SharedPreparedPhysicsSample Pose, bool Frozen)>();
        var preparedWorlds = new Dictionary<int, Matrix4x4[]>();
        var physicalDisabled = new Dictionary<int, IReadOnlySet<int>>();
        var resetAfterPhysicsWorlds = new Dictionary<int, Matrix4x4[]>();
        var phase1Outside = new Dictionary<int, Dictionary<int, Matrix4x4>>();
        var nonPhysicalAttached = new Dictionary<int, (double Local, bool SampleFrozen)>();
        try
        {
            foreach (int id in order)
            {
                var entry = entries[id]; var controller = entry.Controller!;
                var outside = BuildPmmBoundaryOutsideParents(entry, bindings[id], (parent, targetBone) =>
                {
                    int parentId = parent.Source.Source.Index;
                    var parentController = parent.Controller!;
                    if (physicalDisabled.TryGetValue(parentId, out var parentDisabled)
                        && parentController.IsBoneAffectedByAfterPhysics(targetBone))
                    {
                        if (reset)
                        {
                            if (!resetAfterPhysicsWorlds.TryGetValue(parentId, out var sampled))
                            {
                                sampled = parentController.SampleResetPostAfterPhysicsBoneWorlds(
                                    saved ? 0 : Math.Min(frame, parentController.DurationFrames), ik, morphs, parentDisabled);
                                resetAfterPhysicsWorlds[parentId] = sampled;
                            }
                            return sampled;
                        }
                        if (parentController.BoneWorldMatrices.Count == parent.Source.Model.Bones.Count)
                            return parentController.BoneWorldMatrices;
                    }
                    return preparedWorlds.TryGetValue(parentId, out var prepared) ? prepared : parentController.BoneWorldMatrices;
                });
                phase1Outside[id] = outside;
                bool trackWindowChanged = controller.SetOutsideParentTrackWindows(TrackWindowsAt(id, entry.Source, saved ? 0 : frame, saved));
                bool frozen = !reset && !saved && priorFrame >= controller.DurationFrames;
                bool fullEvalFrozen = frozen && controller.RequiresFullEvaluationForFrozenOutsideParents(outside, trackWindowChanged);
                bool sampleFrozen = frozen && !fullEvalFrozen;
                if (fullEvalFrozen) controller.SetOutsideParents(outside);
                else if (frozen) controller.RefreshSharedFrozenOutsideParents(outside);
                else controller.SetOutsideParents(outside);
                double local = saved ? 0 : Math.Min(frame, controller.DurationFrames);
                bool hasBindings = bindings[id].Values.Any(p => p.ModelIndex >= 0);
                if (!(controller.SupportsPhysics || coupledSoftBodies && controller.SupportsSoftBodies))
                {
                    if (controller.SoftBodiesEnabled)
                    {
                        controller.AttachSharedPhysics(pmmSharedStatus!);
                        var softPreparation = controller.BeginSharedPhysicsSample(local, ik, morphs, frozen: sampleFrozen);
                        softOnly.Add((id, softPreparation, frozen));
                        softOnlyById[id] = (softPreparation, frozen);
                        preparedWorlds[id] = softPreparation.BeforePhysicsBoneWorlds;
                    }
                    else
                    {
                        var pose = controller.SampleAt(local, ik, morphs); entry.Frame = PmmSharedLocalFrame(entry, pose);
                        preparedWorlds[id] = controller.BoneWorldMatrices.ToArray();
                        if (hasBindings) nonPhysicalAttached[id] = (local, sampleFrozen);
                    }
                    continue;
                }
                var disabled = physicsControls!.DisabledBonesAt(id, frame, saved).Select(bone => sharedBoneMapping[(id, bone)]).ToHashSet();
                physicalDisabled[id] = disabled;
                var preparation = controller.BeginSharedPhysicsSample(local, ik, morphs, disabled, sampleFrozen);
                preparedWorlds[id] = preparation.BeforePhysicsBoneWorlds;
                var input = new SharedPhysicsActorSample(SharedActorId(id), preparation.BeforePhysicsBoneWorlds)
                    { DisabledBoneIndices = disabled, Frozen = frozen, Impulses = preparation.Impulses };
                pending.Add((id, preparation, input));
                pendingById[id] = (preparation, input);
                var world = PmmWorld(id);
                if (!world.ContainsActor(input.ActorId))
                    world.AddActor(input.ActorId, PmxCharacterPhysicsAdapter.Convert(entry.Source.Model).Definition, Matrix4x4.Identity, input.BeforePhysicsBoneWorlds);
                controller.AttachSharedPhysics(PmmStatus(id));
            }
            var gravityPose=SharedGravityPose(frame,saved);
            if (reset) pmmGravityNoise.Reset(gravityPose);
            var gravity = reset ? pmmGravityNoise.Current : pmmGravityNoise.Advance(gravityPose, step ? CharacterPhysicsSession.FixedStepSeconds : 0);
            foreach (var entry in entries.Values)
                if (entry.Controller!.SoftBodiesEnabled) entry.Controller.SetSoftBodyGravity(gravity);
            var results = new Dictionary<Guid, Matrix4x4[]>();
            // A group's samples can only reach its own world. Noise is sampled once for the scene,
            // not once per actor; separate worlds therefore receive the identical acceleration.
            (SharedCharacterPhysicsSession World, SharedPhysicsActorSample[] Samples)[] batches = useSharedPhysics
                ? [(World: pmmSharedWorld!, Samples: pending.Select(item => item.Input).ToArray())]
                : pending.GroupBy(item => PmmWorld(item.Id))
                    .Select(group => (World: group.Key, Samples: group.Select(item => item.Input).ToArray())).ToArray();
            foreach (var batch in batches)
            {
                var world = batch.World; var samples = batch.Samples;
                var actors = pending.Where(item => ReferenceEquals(PmmWorld(item.Id), world)).ToArray();
                var targets = actors.ToDictionary(item => item.Id, item => entries[item.Id].Controller!.PrepareCoupledTargets(item.Pose));
                if (reset) world.ResetAll(samples);
                foreach (var item in actors)
                {
                    var entry = entries[item.Id];
                    if (coupledSoftBodies && entry.Controller!.SupportsSoftBodies && reset)
                        entry.Controller.ResetSharedCoupledSoftBodies(world, item.Input.ActorId, Matrix4x4.Identity, item.Pose);
                    var pins = reset ? entry.Controller!.PrepareCoupledTargets(item.Pose) : targets[item.Id];
                    entry.Controller!.PublishCoupledTargets(pins, item.Input.Frozen, step || reset);
                }
                world.SetGravity(gravity);
                foreach (var result in step ? world.Step(samples) : world.Reflect(samples)) results.Add(result.Key, result.Value);
            }
            var updatedInPhase3 = new HashSet<int>();
            foreach (int id in order)
            {
                var entry = entries[id]; var controller = entry.Controller!;
                bool hasPhase3Parent = bindings[id].Values.Any(p => p.ModelIndex >= 0
                    && (updatedInPhase3.Contains(p.ModelIndex)
                        || (physicalDisabled.ContainsKey(p.ModelIndex)
                            && entries[p.ModelIndex].Controller!.IsBoneAffectedByAfterPhysics(MapBone(entries[p.ModelIndex].Source, p.BoneIndex)))));
                Dictionary<int, Matrix4x4>? afterOutside = null;
                if (hasPhase3Parent)
                {
                    var postOutside = BuildPmmBoundaryOutsideParents(entry, bindings[id], (parent, _) => parent.Controller!.BoneWorldMatrices);
                    if (!CharacterAnimationController.OutsideParentsEqual(phase1Outside[id], postOutside))
                        afterOutside = postOutside;
                }
                if (pendingById.TryGetValue(id, out var item))
                {
                    entry.Frame = PmmSharedLocalFrame(entry, item.Pose.Complete(results[item.Input.ActorId],
                        PmmSoftRigidWorlds(id), reset ? null : step && !item.Input.Frozen ? CharacterPhysicsSession.FixedStepSeconds : 0, afterOutside));
                    if (afterOutside is not null) updatedInPhase3.Add(id);
                }
                else if (softOnlyById.TryGetValue(id, out var softItem))
                {
                    entry.Frame = PmmSharedLocalFrame(entry, softItem.Pose.Complete(softItem.Pose.BeforePhysicsBoneWorlds, [],
                        reset ? null : step && !softItem.Frozen ? CharacterPhysicsSession.FixedStepSeconds : 0, afterOutside));
                    if (afterOutside is not null) updatedInPhase3.Add(id);
                }
                else if (afterOutside is not null && nonPhysicalAttached.TryGetValue(id, out var info))
                {
                    bool fullAfter = info.SampleFrozen
                        && controller.RequiresFullEvaluationForFrozenOutsideParents(afterOutside, trackWindowChanged: false);
                    if (info.SampleFrozen && !fullAfter) controller.RefreshSharedFrozenOutsideParents(afterOutside);
                    else controller.SetOutsideParents(afterOutside);
                    entry.Frame = PmmSharedLocalFrame(entry, controller.SampleAt(info.Local, ik, morphs));
                    updatedInPhase3.Add(id);
                }
            }
        }
        finally { foreach (var item in pending) item.Pose.Dispose(); foreach (var item in softOnly) item.Pose.Dispose(); }
    }

    private OriginalFrameScene PmmSharedLocalFrame(Entry entry, (OriginalStyleScene Surface, OriginalOutlineData Outline) pose)
        => new(pose.Surface, pose.Outline, entry.Rest.OutlineOptions, materials: PmxOriginalFrameAdapter.FromEvaluatedMaterials(entry.Source.Model, pose.Surface, pose.Outline)
            .Select(flags => flags with { CastSelfShadow = flags.CastSelfShadow && (Plan.Document.Version == 1 || entry.Source.Source.SelfShadowEnabled), ReceiveSelfShadow = flags.ReceiveSelfShadow && (Plan.Document.Version == 1 || entry.Source.Source.SelfShadowEnabled) }));

    private void PublishPmmShared(double frame, bool saved)
    {
        uint integer = (uint)Math.Floor(frame);
        int[] shown = entries.Where(pair => saved ? pair.Value.Source.Source.Visible : pair.Value.Source.VisibleAt(integer))
            .OrderBy(pair => pair.Value.Source.Source.DrawOrderIndex).Select(pair => pair.Key).ToArray();
        var light = saved ? Tracks.SavedLight : Tracks.LightAt(frame); var camera = ResolveCamera(saved ? Tracks.SavedCamera : Tracks.CameraAt(frame));
        _ = camera.ViewProjection(Plan.OutputWidth, Plan.OutputHeight);
        var frames = shown.Select(id => entries[id].Frame).ToArray();
        var candidate = frames.Length == 0 ? EmptyFrame() : combined is not null && shown.SequenceEqual(visible)
            ? ProjectSceneAssembler.WithVertices(combined, frames) : ProjectSceneAssembler.Combine(frames, light.Color, -light.Direction);
        combined = new(new(candidate.Surface.Snapshot.WithLighting(-light.Direction), candidate.Surface.Materials, candidate.Surface.Profile with { LightAmbient = light.Color, LightSpecular = light.Color }),
            candidate.Outline, candidate.OutlineOptions, materials: candidate.Materials);
        visible = shown; currentCamera = camera; CurrentFrame = frame; IsSavedPose = saved;
    }
}
