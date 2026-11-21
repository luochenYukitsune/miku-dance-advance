using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Mmd.Modern.Physics;

/// <summary>One complete actor's animation-before-physics pose in actor model space.</summary>
public sealed record SharedPhysicsActorSample(Guid ActorId, Matrix4x4[] BeforePhysicsBoneWorlds)
{
    public bool Frozen { get; init; }
    public IReadOnlySet<int> DisabledBoneIndices { get; init; } = new HashSet<int>();
    public CharacterBodyImpulse[] Impulses { get; init; } = [];
}

/// <summary>Owns exactly one numerical world. Membership changes preserve unrelated actors and momentum.
/// The application controls the fixed-step timeline; each Step is exactly 1/120 second for all actors.</summary>
public sealed class SharedCharacterPhysicsSession : IDisposable
{
    private readonly ICharacterPhysicsMutableBackend backend;
    private readonly Dictionary<Guid, Actor> actors = [];
    private const int MaximumRetainedPreparationEntries = 16_384;
    private readonly List<(int Slot, Vector3 Linear, Vector3 Angular)> stepCommands = [];
    private readonly List<PhysicsKinematicTargetBatchItem> stepTargets = [];
    private readonly List<PhysicsJointPullBatchItem> reflectJoints = [];
    private readonly List<int> reflectBodySlots = [];
    private readonly List<int> reflectActorReadCounts = [];
    private Matrix4x4[]? reflectWorldTransforms;
    private readonly HashSet<Guid> validationSeenActors = [];
    private int preparationBuffersInUse;
    private int reflectionBuffersInUse;
    private int validationBuffersInUse;
    private bool disposed;
    public long StepCount { get; private set; }
    public string BackendName => backend.Name;
    public IReadOnlyList<string> Diagnostics => backend.Diagnostics;
    public IReadOnlyList<Guid> ActorIds => Array.AsReadOnly(actors.Keys.ToArray());
    public bool ContainsActor(Guid actorId) => actors.ContainsKey(actorId);
    public bool IsFaulted { get; private set; }
    /// <summary>Identity-only ownership check, before a replacement factory result can initialize a world.</summary>
    public bool UsesBackend(ICharacterPhysicsBackend candidate) => ReferenceEquals(backend, candidate);

    public SharedCharacterPhysicsSession(ICharacterPhysicsMutableBackend backend)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        try { backend.Initialize(new([], [], []), []); }
        catch { backend.Dispose(); throw; }
    }

    public void AddActor(Guid actorId, CharacterPhysicsDefinition definition, Matrix4x4 placement, ReadOnlySpan<Matrix4x4> animatedBoneWorlds)
    {
        RequireHealthy(); ArgumentNullException.ThrowIfNull(definition);
        if (actors.ContainsKey(actorId)) throw new ArgumentException("Actor identity is already present.", nameof(actorId));
        PhysicsMath.ValidateRigid(placement, nameof(placement));
        if (animatedBoneWorlds.Length != definition.Bones.Count) throw new ArgumentException("Actor bone count mismatch.", nameof(animatedBoneWorlds));
        foreach (var value in animatedBoneWorlds) PhysicsMath.ValidateRigid(value, nameof(animatedBoneWorlds));
        if (definition.Joints.Any(joint => joint.Type <= 5) && backend is ICharacterPhysicsJointPullBackend pull)
            pull.ValidateJointPullSupport();
        var mapped = new ActorBackend(backend, placement);
        CharacterPhysicsSession? session = null;
        try
        {
            session = new(definition, mapped, animatedBoneWorlds.ToArray());
            actors.Add(actorId, new(definition, mapped, session)
                { PreviousRootWorld = animatedBoneWorlds.Length == 0 ? null : animatedBoneWorlds[0] });
        }
        catch
        {
            // Native allocation/rollback failures can leave an unknown membership state. A whole
            // ResetAll is the documented recovery boundary; never continue an uncertain world.
            IsFaulted = true;
            try { session?.Dispose(); if (session is null) mapped.Dispose(); } catch { }
            throw;
        }
    }

    public void RemoveActor(Guid actorId)
    {
        RequireHealthy(); var actor = Find(actorId);
        try { actor.Session.Dispose(); actors.Remove(actorId); }
        catch { IsFaulted = true; throw; }
    }
    public void ReplaceActor(Guid actorId, CharacterPhysicsDefinition definition, Matrix4x4 placement, ReadOnlySpan<Matrix4x4> animatedBoneWorlds)
    {
        RequireHealthy(); var previous = Find(actorId); var temporaryId = Guid.NewGuid();
        AddActor(temporaryId, definition, placement, animatedBoneWorlds);
        var candidate = actors[temporaryId];
        try { previous.Session.Dispose(); }
        catch
        {
            IsFaulted = true; actors.Remove(temporaryId);
            try { candidate.Session.Dispose(); } catch { }
            throw;
        }
        actors.Remove(temporaryId); actors[actorId] = candidate;
    }
    public void ResetActor(Guid actorId, ReadOnlySpan<Matrix4x4> animatedBoneWorlds)
    {
        RequireHealthy(); var actor = Find(actorId);
        if (animatedBoneWorlds.Length != actor.Definition.Bones.Count) throw new ArgumentException("Actor bone count mismatch.", nameof(animatedBoneWorlds));
        foreach (var pose in animatedBoneWorlds) PhysicsMath.ValidateRigid(pose, nameof(animatedBoneWorlds));
        try
        {
            actor.Session.Reset(animatedBoneWorlds);
            actor.PreviousRootWorld = animatedBoneWorlds.Length == 0 ? null : animatedBoneWorlds[0];
        }
        catch { IsFaulted = true; throw; }
    }
    /// <summary>Resets every actor as one recovery boundary. This is a seek, not continuity preservation.</summary>
    public void ResetAll(IReadOnlyList<SharedPhysicsActorSample> samples)
    {
        RequireAlive(); ValidateSamples(samples);
        var rebuilt = new Dictionary<Guid, Actor>();
        try
        {
            // Seek intentionally resets every solver history and is also capable of recovering from
            // a failed partial membership mutation. Abandon stale slots before disposing old views.
            backend.Initialize(new([], [], []), []);
            foreach (var actor in actors.Values) { actor.Backend.Abandon(); actor.Session.Dispose(); }
            foreach (var sample in samples)
            {
                var previous = actors[sample.ActorId]; var mapped = new ActorBackend(backend, previous.Backend.Placement);
                var session = new CharacterPhysicsSession(previous.Definition, mapped, sample.BeforePhysicsBoneWorlds);
                rebuilt.Add(sample.ActorId, new(previous.Definition, mapped, session)
                    { PreviousRootWorld = sample.BeforePhysicsBoneWorlds.Length == 0 ? null : sample.BeforePhysicsBoneWorlds[0] });
                for (int body = 0; body < previous.Definition.Bodies.Count; body++)
                {
                    var config = previous.Definition.Bodies[body];
                    backend.SetBodyKinematic(mapped.Slot(body), sample.Frozen || config.Mode == PhysicsBodyMode.Kinematic
                        || config.BoneIndex >= 0 && sample.DisabledBoneIndices.Contains(config.BoneIndex));
                }
            }
            actors.Clear(); foreach (var pair in rebuilt) actors.Add(pair.Key, pair.Value);
            IsFaulted = false; StepCount = 0;
        }
        catch { foreach (var actor in rebuilt.Values) { try { actor.Session.Dispose(); } catch { } } IsFaulted = true; throw; }
    }
    public void SetGravity(Vector3 gravity) { RequireHealthy(); backend.SetGravity(gravity); }
    /// <summary>Maps actor-local anchor indices into stable owner slots. Targets and samples stay in world space.</summary>
    public ICharacterSoftBodyBinding CreateSoftBodyBinding(Guid actorId)
    { RequireHealthy(); return Find(actorId).Session.CreateSoftBodyBinding(); }
    /// <summary>Returns scene-world body transform; actor bone output remains in model space.</summary>
    public Matrix4x4 GetBodyWorldTransform(Guid actorId, int localBodyIndex)
    { RequireHealthy(); var actor = Find(actorId); return backend.GetBodyWorldTransform(actor.Backend.Slot(localBodyIndex)); }

    public IReadOnlyDictionary<Guid, Matrix4x4[]> Reflect(IReadOnlyList<SharedPhysicsActorSample> samples)
    {
        RequireHealthy(); ValidateSamples(samples);
        return ReflectValidated(samples);
    }

    private IReadOnlyDictionary<Guid, Matrix4x4[]> ReflectValidated(IReadOnlyList<SharedPhysicsActorSample> samples)
    {
        try
        {
            // Reflection can apply joint-pull corrections before a later backend read fails.
            // The same recovery boundary as Step must cover this public entry point too.
            var result = new Dictionary<Guid, Matrix4x4[]>(samples.Count);
            if (backend is ICharacterPhysicsBatchedBackend { SupportsBatchedExecution: true } batched)
            {
                bool reuse = Interlocked.CompareExchange(ref reflectionBuffersInUse, 1, 0) == 0;
                var joints = reuse ? reflectJoints : new List<PhysicsJointPullBatchItem>();
                var bodySlots = reuse ? reflectBodySlots : new List<int>();
                var actorReadCounts = reuse ? reflectActorReadCounts : new List<int>();
                Matrix4x4[]? rentedTransforms = null;
                try
                {
                    bool hasJointPull = backend is ICharacterPhysicsJointPullBackend;
                    for (int i = 0; i < samples.Count; i++)
                    {
                        var sample = samples[i];
                        var actor = actors[sample.ActorId];
                        if (!sample.Frozen)
                        {
                            if (hasJointPull)
                            {
                                var actorJoints = actor.Definition.Joints;
                                var maxDistances = actor.Definition.JointMaximumDistances;
                                var slotMap = actor.Backend.BodySlots;
                                for (int j = 0; j < actorJoints.Count; j++)
                                {
                                    var joint = actorJoints[j];
                                    if (joint.Type > 5) continue;
                                    joints.Add(new PhysicsJointPullBatchItem(slotMap[joint.BodyA], slotMap[joint.BodyB], maxDistances[j]));
                                }
                            }
                            actor.Session.CollectReflectedBodyIndices(sample.DisabledBoneIndices, actor.Backend.BodySlots, bodySlots, out int readCount);
                            actorReadCounts.Add(readCount);
                        }
                        else
                        {
                            var slotMap = actor.Backend.BodySlots;
                            for (int b = 0; b < slotMap.Length; b++) bodySlots.Add(slotMap[b]);
                            actorReadCounts.Add(slotMap.Length);
                        }
                    }

                    int totalReads = bodySlots.Count;
                    Span<Matrix4x4> worldTransformsSpan = Span<Matrix4x4>.Empty;
                    if (totalReads > 0)
                    {
                        if (reuse)
                        {
                            if (reflectWorldTransforms is null || reflectWorldTransforms.Length < totalReads)
                                reflectWorldTransforms = new Matrix4x4[totalReads];
                            worldTransformsSpan = reflectWorldTransforms.AsSpan(0, totalReads);
                        }
                        else
                        {
                            rentedTransforms = ArrayPool<Matrix4x4>.Shared.Rent(totalReads);
                            worldTransformsSpan = rentedTransforms.AsSpan(0, totalReads);
                        }
                    }

                    batched.CorrectJointsAndGetBodyTransforms(CollectionsMarshal.AsSpan(joints), CollectionsMarshal.AsSpan(bodySlots), worldTransformsSpan);

                    int offset = 0;
                    for (int i = 0; i < samples.Count; i++)
                    {
                        var sample = samples[i];
                        var actor = actors[sample.ActorId];
                        int count = actorReadCounts[i];
                        var slice = worldTransformsSpan.Slice(offset, count);
                        offset += count;
                        actor.Backend.ConvertWorldTransformsInPlace(slice);
                        var reflected = actor.Session.ReflectValidatedSharedPose(sample.BeforePhysicsBoneWorlds,
                            sample.DisabledBoneIndices, sample.Frozen, slice);
                        result.Add(sample.ActorId, reflected);
                        actor.PreviousRootWorld = reflected.Length == 0 ? null : reflected[0];
                    }
                    return result;
                }
                finally
                {
                    if (rentedTransforms is not null)
                        ArrayPool<Matrix4x4>.Shared.Return(rentedTransforms, clearArray: true);
                    if (reuse)
                    {
                        joints.Clear();
                        bodySlots.Clear();
                        actorReadCounts.Clear();
                        if (joints.Capacity > MaximumRetainedPreparationEntries) joints.Capacity = 0;
                        if (bodySlots.Capacity > MaximumRetainedPreparationEntries) bodySlots.Capacity = 0;
                        if (actorReadCounts.Capacity > MaximumRetainedPreparationEntries) actorReadCounts.Capacity = 0;
                        if (reflectWorldTransforms is { Length: > MaximumRetainedPreparationEntries })
                            reflectWorldTransforms = null;
                        else if (reflectWorldTransforms is not null)
                            Array.Clear(reflectWorldTransforms);
                        Volatile.Write(ref reflectionBuffersInUse, 0);
                    }
                }
            }

            for (int i = 0; i < samples.Count; i++)
            {
                var sample = samples[i];
                var actor = actors[sample.ActorId];
                var reflected = actor.Session.ReflectValidatedSharedPose(sample.BeforePhysicsBoneWorlds,
                    sample.DisabledBoneIndices, sample.Frozen);
                result.Add(sample.ActorId, reflected);
                actor.PreviousRootWorld = reflected.Length == 0 ? null : reflected[0];
            }
            return result;
        }
        catch { IsFaulted = true; throw; }
    }

    public IReadOnlyDictionary<Guid, Matrix4x4[]> Step(IReadOnlyList<SharedPhysicsActorSample> samples)
    {
        RequireHealthy(); ValidateSamples(samples);
        // Custom backends may call Step again while reading or consuming the prepared work.
        // An overlapping call keeps its own lists; this lease does not synchronize the solver.
        bool reuse = Interlocked.CompareExchange(ref preparationBuffersInUse, 1, 0) == 0;
        var commands = reuse ? stepCommands : new List<(int Slot, Vector3 Linear, Vector3 Angular)>();
        var targets = reuse ? stepTargets : new List<PhysicsKinematicTargetBatchItem>();
        try { return StepPrepared(samples, commands, targets); }
        finally
        {
            if (reuse)
            {
                // Entries contain only value types. Clear the count after success and preflight
                // failure alike, and drop unusually large arrays without copying them to shrink.
                commands.Clear(); targets.Clear();
                if (commands.Capacity > MaximumRetainedPreparationEntries) commands.Capacity = 0;
                if (targets.Capacity > MaximumRetainedPreparationEntries) targets.Capacity = 0;
                Volatile.Write(ref preparationBuffersInUse, 0);
            }
        }
    }

    private IReadOnlyDictionary<Guid, Matrix4x4[]> StepPrepared(IReadOnlyList<SharedPhysicsActorSample> samples,
        List<(int Slot, Vector3 Linear, Vector3 Angular)> commands,
        List<PhysicsKinematicTargetBatchItem> targets)
    {
        // Resolve local impulse orientation and every target before mutating any actor.
        for (int s = 0; s < samples.Count; s++)
        {
            var sample = samples[s];
            var actor = actors[sample.ActorId]; var definition = actor.Definition;
            for (int i = 0; i < definition.Bodies.Count; i++)
            {
                var body = definition.Bodies[i];
                bool disabled = body.BoneIndex >= 0 && sample.DisabledBoneIndices.Count > 0 && sample.DisabledBoneIndices.Contains(body.BoneIndex);
                int bone = body.BoneIndex >= 0 ? body.BoneIndex : definition.Bones.Count == 0 ? -1 : 0;
                var target = bone < 0 ? definition.BodyRestWorldTransforms[i] : definition.BodyBoneOffsets[i] * sample.BeforePhysicsBoneWorlds[bone];
                target = Affine(target * actor.Backend.Placement);
                if (sample.Frozen && body.BoneIndex < 0 && body.Mode != PhysicsBodyMode.Kinematic)
                {
                    // An unbound dynamic collider has no reflected bone from which its final pose
                    // can be reconstructed. Freeze the simulated pose, applying only a moving root
                    // attachment's delta; returning to its authored rest transform would teleport it.
                    target = backend.GetBodyWorldTransform(actor.Backend.Slot(i));
                    if (sample.BeforePhysicsBoneWorlds.Length != 0 && actor.PreviousRootWorld is { } previousRoot
                        && Affine(previousRoot) != Affine(sample.BeforePhysicsBoneWorlds[0]))
                    {
                        Matrix4x4.Invert(actor.Backend.Placement, out var inversePlacement);
                        Matrix4x4.Invert(previousRoot, out var inverseRoot);
                        target = Affine(target * inversePlacement * inverseRoot * sample.BeforePhysicsBoneWorlds[0] * actor.Backend.Placement);
                    }
                }
                PhysicsMath.ValidateRigid(target, nameof(samples));
                targets.Add(new PhysicsKinematicTargetBatchItem(actor.Backend.Slot(i), sample.Frozen || disabled || body.Mode == PhysicsBodyMode.Kinematic, target));
            }
            var sampleImpulses = sample.Impulses;
            for (int j = 0; j < sampleImpulses.Length; j++)
            {
                var impulse = sampleImpulses[j];
                var body = definition.Bodies[impulse.BodyIndex];
                if (sample.Frozen || body.Mode == PhysicsBodyMode.Kinematic || body.Mass == 0 || (body.BoneIndex >= 0 && sample.DisabledBoneIndices.Count > 0 && sample.DisabledBoneIndices.Contains(body.BoneIndex))) continue;
                var rotation = impulse.Local ? backend.GetBodyWorldTransform(actor.Backend.Slot(impulse.BodyIndex)) : actor.Backend.Placement;
                var linear = Vector3.TransformNormal(impulse.Linear, rotation); var angular = Vector3.TransformNormal(impulse.Angular, rotation);
                if (!PhysicsMath.Finite(linear) || !PhysicsMath.Finite(angular)) throw new ArgumentException("Impulse conversion overflowed.", nameof(samples));
                commands.Add((actor.Backend.Slot(impulse.BodyIndex), linear, angular));
            }
        }
        try
        {
            if (backend is ICharacterPhysicsBatchedBackend { SupportsBatchedExecution: true } batched)
            {
                batched.ApplyKinematicTargetsBatch(CollectionsMarshal.AsSpan(targets), (float)CharacterPhysicsSession.FixedStepSeconds);
            }
            else
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    var target = targets[i];
                    backend.SetBodyKinematic(target.BodyIndex, target.Kinematic);
                    if (target.Kinematic) backend.SetKinematicTarget(target.BodyIndex, target.WorldTransform, (float)CharacterPhysicsSession.FixedStepSeconds);
                }
            }
            for (int i = 0; i < commands.Count; i++)
            {
                var command = commands[i];
                backend.ApplyImpulse(command.Slot, command.Linear, command.Angular);
            }
            backend.Step((float)CharacterPhysicsSession.FixedStepSeconds); StepCount++;
            return ReflectValidated(samples);
        }
        catch { IsFaulted = true; throw; }
    }

    private void ValidateSamples(IReadOnlyList<SharedPhysicsActorSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count != actors.Count)
            throw new ArgumentException("A shared step requires exactly one sample for every actor.", nameof(samples));

        bool reuse = Interlocked.CompareExchange(ref validationBuffersInUse, 1, 0) == 0;
        var seenActors = reuse ? validationSeenActors : new HashSet<Guid>(samples.Count);
        int maxSeen = 0;
        try
        {
            for (int i = 0; i < samples.Count; i++)
            {
                var sample = samples[i];
                if (sample is null || !seenActors.Add(sample.ActorId))
                    throw new ArgumentException("A shared step requires exactly one sample for every actor.", nameof(samples));
            }
            maxSeen = seenActors.Count;
        }
        finally
        {
            seenActors.Clear();
            if (reuse)
            {
                if (maxSeen > MaximumRetainedPreparationEntries) seenActors.TrimExcess();
                Volatile.Write(ref validationBuffersInUse, 0);
            }
        }

        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            var actor = Find(sample.ActorId);
            ArgumentNullException.ThrowIfNull(sample.BeforePhysicsBoneWorlds); ArgumentNullException.ThrowIfNull(sample.DisabledBoneIndices); ArgumentNullException.ThrowIfNull(sample.Impulses);
            if (sample.BeforePhysicsBoneWorlds.Length != actor.Definition.Bones.Count)
                throw new ArgumentException("Actor sample has invalid bones.", nameof(samples));
            if (sample.DisabledBoneIndices.Count > 0)
            {
                if (sample.DisabledBoneIndices is HashSet<int> disabledSet)
                {
                    foreach (int bone in disabledSet)
                        if ((uint)bone >= (uint)actor.Definition.Bones.Count)
                            throw new ArgumentException("Actor sample has invalid bones.", nameof(samples));
                }
                else
                {
                    foreach (int bone in sample.DisabledBoneIndices)
                        if ((uint)bone >= (uint)actor.Definition.Bones.Count)
                            throw new ArgumentException("Actor sample has invalid bones.", nameof(samples));
                }
            }
            for (int bone = 0; bone < sample.BeforePhysicsBoneWorlds.Length; bone++)
            {
                var pose = sample.BeforePhysicsBoneWorlds[bone];
                try { PhysicsMath.ValidateRigid(pose, nameof(samples)); }
                catch (ArgumentException error)
                {
                    // Observe the exact rejected sample before stack unwinding discards it.
                    // These bounded values do not retain the actor, solver, or sample array.
                    try
                    {
                        error.Data["Physics.ActorId"] = sample.ActorId;
                        error.Data["Physics.BoneIndex"] = bone;
                        error.Data["Physics.Frozen"] = sample.Frozen;
                        error.Data["Physics.CompletedWorldSteps"] = StepCount;
                        error.Data["Physics.Backend"] = BackendName;
                        error.Data["Physics.RejectedBoneWorld"] = pose;
                        error.Data["Physics.RejectedMatrixSpace"] = "actor-model row-vector";
                    }
                    catch { }
                    throw;
                }
            }
            var impulses = sample.Impulses;
            for (int j = 0; j < impulses.Length; j++)
            {
                var impulse = impulses[j];
                if ((uint)impulse.BodyIndex >= (uint)actor.Definition.Bodies.Count || !PhysicsMath.Finite(impulse.Linear) || !PhysicsMath.Finite(impulse.Angular))
                    throw new ArgumentException("Actor impulses require valid body indices and finite momentum.", nameof(samples));
            }
        }
    }
    private Actor Find(Guid id) => actors.TryGetValue(id, out var actor) ? actor : throw new ArgumentException("Unknown physics actor.", nameof(id));
    private sealed record Actor(CharacterPhysicsDefinition Definition, ActorBackend Backend, CharacterPhysicsSession Session)
    { public Matrix4x4? PreviousRootWorld { get; set; } }
    private static Matrix4x4 Affine(Matrix4x4 value) { value.M14 = value.M24 = value.M34 = 0; value.M44 = 1; return value; }
    private void RequireAlive() => ObjectDisposedException.ThrowIf(disposed, this);
    private void RequireHealthy() { RequireAlive(); if (IsFaulted) throw new InvalidOperationException("Shared world failed; reset all actors before continuing."); }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        try { foreach (var actor in actors.Values) actor.Session.Dispose(); actors.Clear(); }
        finally { backend.Dispose(); }
    }

    private sealed class ActorBackend(ICharacterPhysicsMutableBackend owner, Matrix4x4 placement) : ICharacterPhysicsBackend, ICharacterImpulseBackend, ICharacterSoftBodyProvider, ICharacterPhysicsJointPullBackend
    {
        private int[] bodySlots = [], jointSlots = [];
        private readonly List<ICharacterSoftBodyBinding> bindings = [];
        private bool released;
        public Matrix4x4 Placement { get; } = placement;
        private readonly Matrix4x4 inversePlacement = Matrix4x4.Invert(placement, out var inverse) ? inverse : throw new ArgumentException("Invalid placement.");
        public string Name => owner.Name;
        public IReadOnlyList<string> Diagnostics => owner.Diagnostics;
        public ReadOnlySpan<int> BodySlots => bodySlots;
        public int Slot(int localIndex) => (uint)localIndex < (uint)bodySlots.Length ? bodySlots[localIndex] : throw new ArgumentOutOfRangeException(nameof(localIndex));
        public void ConvertWorldTransformsInPlace(Span<Matrix4x4> transforms)
        {
            for (int i = 0; i < transforms.Length; i++)
            {
                var world = transforms[i];
                PhysicsMath.ValidateRigid(world, nameof(owner));
                transforms[i] = Affine(world * inversePlacement);
            }
        }
        public ICharacterSoftBodyBinding CreateSoftBodyBinding(IReadOnlyList<int> slots)
        {
            ObjectDisposedException.ThrowIf(released, this);
            if (owner is not ICharacterSoftBodyProvider provider)
                throw new NotSupportedException($"Physics backend '{owner.Name}' does not provide a coupled rigid-soft world.");
            var binding = provider.CreateSoftBodyBinding(slots.Select(Slot).ToArray());
            bindings.Add(binding); return binding;
        }
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms)
        {
            ObjectDisposedException.ThrowIf(released, this);
            var candidates = new List<int>(); var joints = new List<int>();
            try
            {
                for (int i = 0; i < definition.Bodies.Count; i++) candidates.Add(owner.AddBody(definition.Bodies[i], Affine(transforms[i] * Placement)));
                for (int i = 0; i < definition.Joints.Count; i++)
                {
                    var joint = definition.Joints[i];
                    joints.Add(owner.AddJoint(joint with { BodyA = candidates[joint.BodyA], BodyB = candidates[joint.BodyB] }, definition.JointLocalFramesA[i], definition.JointLocalFramesB[i]));
                }
            }
            catch { foreach (int joint in joints.AsEnumerable().Reverse()) owner.RemoveJoint(joint); foreach (int body in candidates.AsEnumerable().Reverse()) owner.RemoveBody(body); throw; }
            foreach (var binding in bindings) binding.Dispose(); bindings.Clear();
            foreach (int joint in jointSlots) owner.RemoveJoint(joint);
            foreach (int body in bodySlots) owner.RemoveBody(body);
            bodySlots = candidates.ToArray(); jointSlots = joints.ToArray();
        }
        public void SetKinematicTarget(int index, Matrix4x4 transform, float seconds) => owner.SetKinematicTarget(Slot(index), Affine(transform * Placement), seconds);
        public Matrix4x4 GetBodyWorldTransform(int index)
        {
            var world = owner.GetBodyWorldTransform(Slot(index));
            // Canonicalization may remove affine roundoff after placement conversion, but must
            // never turn a corrupt solver matrix into a valid model-space rigid pose.
            PhysicsMath.ValidateRigid(world, nameof(owner));
            return Affine(world * inversePlacement);
        }
        public void ApplyImpulse(int index, Vector3 linear, Vector3 angular) => owner.ApplyImpulse(Slot(index), Vector3.TransformNormal(linear, Placement), Vector3.TransformNormal(angular, Placement));
        public void CorrectJointOverextension(int bodyA, int bodyB, float maximumDistance)
        {
            if (owner is ICharacterPhysicsJointPullBackend pull)
                pull.CorrectJointOverextension(Slot(bodyA), Slot(bodyB), maximumDistance);
        }
        public void ValidateJointPullSupport()
        {
            if (owner is ICharacterPhysicsJointPullBackend pull) pull.ValidateJointPullSupport();
        }
        public void Step(float seconds) => throw new InvalidOperationException("Shared actors must be stepped together by their world owner.");
        public void Dispose()
        {
            if (released) return; released = true;
            foreach (var binding in bindings) binding.Dispose(); bindings.Clear();
            foreach (int joint in jointSlots) owner.RemoveJoint(joint);
            foreach (int body in bodySlots) owner.RemoveBody(body);
            bodySlots = []; jointSlots = [];
        }
        public void Abandon() { released = true; bodySlots = []; jointSlots = []; }
    }
}
