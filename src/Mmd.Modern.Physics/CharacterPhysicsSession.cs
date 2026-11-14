// Ported from saba (https://github.com/benikabocha/saba) @ 29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332, MIT License, Copyright (c) 2016-2017 benikabocha
// PMXModel.cpp reset/physics reflection and MMDPhysics.cpp motion-state semantics.
using System.Numerics;

namespace Mmd.Modern.Physics;

/// <summary>Stateful experimental physics timeline. Call Reset on seek; replay forward explicitly for a physical seek.</summary>
public sealed class CharacterPhysicsSession : IDisposable
{
    public const double FixedStepSeconds = 1.0 / 120;
    public const int MaximumSubsteps = 10;
    private readonly CharacterPhysicsDefinition definition;
    private readonly ICharacterPhysicsBackend backend;
    private readonly int[] drivenBodies;
    private double accumulator;
    private Vector3? gravity;
    private bool disposed;
    private readonly List<ICharacterSoftBodyBinding> softBindings = [];
    public int LastStepCount { get; private set; }
    public double DiscardedSeconds { get; private set; }
    public string BackendName => backend.Name;
    public IReadOnlyList<string> Diagnostics => backend.Diagnostics;

    /// <summary>Attaches actor-local soft content to this session's numerical owner. No extra clock is created.</summary>
    public ICharacterSoftBodyBinding CreateSoftBodyBinding()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (backend is not ICharacterSoftBodyProvider provider)
            throw new NotSupportedException($"Physics backend '{backend.Name}' does not provide a coupled rigid-soft world.");
        var binding = provider.CreateSoftBodyBinding(Enumerable.Range(0, definition.Bodies.Count).ToArray());
        softBindings.Add(binding); return binding;
    }

    private void ReleaseSoftBindings()
    {
        foreach (var binding in softBindings) binding.Dispose();
        softBindings.Clear();
    }

    public CharacterPhysicsSession(CharacterPhysicsDefinition definition, ICharacterPhysicsBackend backend,
        IReadOnlyList<Matrix4x4>? initialBoneWorlds = null)
    {
        this.definition = definition ?? throw new ArgumentNullException(nameof(definition));
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        drivenBodies = Enumerable.Repeat(-1, definition.Bones.Count).ToArray();
        for (int i = 0; i < definition.Bodies.Count; i++)
        {
            var body = definition.Bodies[i];
            if (body.BoneIndex >= 0 && body.Mode != PhysicsBodyMode.Kinematic) drivenBodies[body.BoneIndex] = i;
        }
        try { Reset(initialBoneWorlds?.ToArray() ?? definition.Bones.Select(bone => bone.RestWorld).ToArray()); }
        catch { backend.Dispose(); throw; }
    }

    public void Reset(ReadOnlySpan<Matrix4x4> animatedBoneWorlds)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ValidatePose(animatedBoneWorlds);
        var transforms = new Matrix4x4[definition.Bodies.Count];
        for (int i = 0; i < transforms.Length; i++) transforms[i] = BodyTarget(i, animatedBoneWorlds);
        // Recreate the solver: no previous contact pairs, warm starts, impulses, velocity, or fractional step survive.
        backend.Initialize(definition, transforms);
        // Initialize is transactional in the owner; only retire the old borrowed views after it succeeds.
        ReleaseSoftBindings();
        if (gravity is Vector3 acceleration) ((ICharacterGravityBackend)backend).SetGravity(acceleration);
        accumulator = 0; LastStepCount = 0; DiscardedSeconds = 0;
    }

    /// <summary>Changes acceleration without resetting elapsed time, contacts, poses or momentum.</summary>
    public void SetGravity(Vector3 gravity)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!PhysicsMath.Finite(gravity)) throw new ArgumentOutOfRangeException(nameof(gravity), "Gravity must be finite.");
        if (backend is not ICharacterGravityBackend configurable)
            throw new NotSupportedException($"Physics backend '{backend.Name}' does not support changing gravity.");
        configurable.SetGravity(gravity);
        this.gravity = gravity;
    }

    /// <summary>Input is immutable. Returns a fresh array after physics and hierarchy propagation.</summary>
    public Matrix4x4[] Advance(double elapsedSeconds, ReadOnlySpan<Matrix4x4> animatedBoneWorlds,
        ReadOnlySpan<CharacterBodyImpulse> impulses = default, Action? beforeStep = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0 || elapsedSeconds > 86400) throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        ValidatePose(animatedBoneWorlds);
        ValidateImpulses(impulses);
        accumulator += elapsedSeconds;
        int requested = (int)Math.Floor((accumulator + 1e-12) / FixedStepSeconds);
        accumulator = Math.Max(0, accumulator - requested * FixedStepSeconds);
        LastStepCount = Math.Min(MaximumSubsteps, requested);
        DiscardedSeconds += (requested - LastStepCount) * FixedStepSeconds;
        var starts = new Matrix4x4[definition.Bodies.Count]; var targets = new Matrix4x4[definition.Bodies.Count];
        for (int i = 0; i < starts.Length; i++)
            if (definition.Bodies[i].Mode == PhysicsBodyMode.Kinematic)
            { starts[i] = backend.GetBodyWorldTransform(i); targets[i] = BodyTarget(i, animatedBoneWorlds); }
        var worldImpulses = new CharacterBodyImpulse[impulses.Length];
        for (int step = 0; step < LastStepCount; step++)
        {
            // Resolve the complete sample before mutating this step. A local impulse follows the
            // body's simulated orientation at this boundary, not the animated bone's rotation.
            for (int j = 0; j < impulses.Length; j++)
            {
                var impulse = impulses[j]; var config = definition.Bodies[impulse.BodyIndex];
                if (config.Mode == PhysicsBodyMode.Kinematic || config.Mass == 0) continue;
                if (impulse.Local)
                {
                    var world = backend.GetBodyWorldTransform(impulse.BodyIndex);
                    PhysicsMath.ValidateRigid(world, nameof(backend));
                    impulse = impulse with { Local = false, Linear = Vector3.TransformNormal(impulse.Linear, world),
                        Angular = Vector3.TransformNormal(impulse.Angular, world) };
                    if (!PhysicsMath.Finite(impulse.Linear) || !PhysicsMath.Finite(impulse.Angular))
                        throw new ArgumentException("Local impulse conversion produced nonfinite momentum.", nameof(impulses));
                }
                worldImpulses[j] = impulse;
            }
            float fraction = (step + 1f) / LastStepCount;
            beforeStep?.Invoke();
            for (int i = 0; i < starts.Length; i++)
                if (definition.Bodies[i].Mode == PhysicsBodyMode.Kinematic)
                    backend.SetKinematicTarget(i, Interpolate(starts[i], targets[i], fraction), (float)FixedStepSeconds);
            foreach (var impulse in worldImpulses)
                if (impulse.Linear != Vector3.Zero || impulse.Angular != Vector3.Zero)
                    ((ICharacterImpulseBackend)backend).ApplyImpulse(impulse.BodyIndex, impulse.Linear, impulse.Angular);
            backend.Step((float)FixedStepSeconds);
        }
        return Reflect(animatedBoneWorlds);
    }

    /// <summary>Validates the entire sampled command list without changing clock or solver state.</summary>
    public void ValidateImpulses(ReadOnlySpan<CharacterBodyImpulse> impulses)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        bool requiresCapability = false;
        foreach (var impulse in impulses)
        {
            if ((uint)impulse.BodyIndex >= (uint)definition.Bodies.Count
                || !PhysicsMath.Finite(impulse.Linear) || !PhysicsMath.Finite(impulse.Angular))
                throw new ArgumentException("Impulse requires a valid body index and finite momentum.", nameof(impulses));
            var body = definition.Bodies[impulse.BodyIndex];
            requiresCapability |= body.Mode != PhysicsBodyMode.Kinematic && body.Mass > 0
                && (impulse.Linear != Vector3.Zero || impulse.Angular != Vector3.Zero);
        }
        if (requiresCapability && backend is not ICharacterImpulseBackend)
            throw new NotSupportedException($"Physics backend '{backend.Name}' does not support impulse morph execution.");
    }

    public Matrix4x4 GetBodyWorldTransform(int bodyIndex)
    { ObjectDisposedException.ThrowIf(disposed, this); return backend.GetBodyWorldTransform(bodyIndex); }

    private Matrix4x4 BodyTarget(int index, ReadOnlySpan<Matrix4x4> input)
    {
        int bone = definition.Bodies[index].BoneIndex;
        if (bone < 0) bone = input.Length == 0 ? -1 : 0;
        return bone < 0 ? definition.BodyRestWorldTransforms[index] : definition.BodyBoneOffsets[index] * input[bone];
    }

    /// <summary>Reflects an already advanced shared solver without stepping it. Disabled bones keep their animated pose.</summary>
    public Matrix4x4[] ReflectPose(ReadOnlySpan<Matrix4x4> input, IReadOnlySet<int>? disabledBoneIndices = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ValidatePose(input);
        if (disabledBoneIndices is not null && disabledBoneIndices.Any(index => (uint)index >= (uint)definition.Bones.Count))
            throw new ArgumentException("Disabled physics bone is outside the definition.", nameof(disabledBoneIndices));
        return Reflect(input, disabledBoneIndices);
    }

    // The shared owner already validated the complete sample before touching the solver.
    internal Matrix4x4[] ReflectValidatedSharedPose(ReadOnlySpan<Matrix4x4> input,
        IReadOnlySet<int> disabledBoneIndices, bool frozen)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!frozen) return Reflect(input, disabledBoneIndices);

        // Keep the already completed frozen pose. Converting its kinematic colliders
        // back to bones would feed offset/placement roundoff into the next tick.
        for (int body = 0; body < definition.Bodies.Count; body++)
            PhysicsMath.ValidateRigid(backend.GetBodyWorldTransform(body), nameof(backend));
        var result = input.ToArray();
        for (int bone = 0; bone < result.Length; bone++) result[bone] = CanonicalAffine(result[bone]);
        return result;
    }

    private Matrix4x4[] Reflect(ReadOnlySpan<Matrix4x4> input, IReadOnlySet<int>? disabledBoneIndices = null)
    {
        if (backend is ICharacterPhysicsJointPullBackend pull)
            for (int i = 0; i < definition.Joints.Count; i++)
            {
                var joint = definition.Joints[i];
                if (joint.Type > 5) continue;
                pull.CorrectJointOverextension(joint.BodyA, joint.BodyB, definition.JointMaximumDistances[i]);
            }
        var result = input.ToArray();
        // Topological reflection preserves dynamic child world poses even when PMX bodies are not sorted by bone.
        // Last PMX body wins if several dynamic bodies drive the same bone.
        foreach (int bone in definition.BoneOrder)
        {
            int parent = definition.Bones[bone].ParentIndex;
            if (parent < 0 || result[parent] == CanonicalAffine(input[parent]))
            {
                // Nothing changed this bone's effective parent. Preserve the sampled pose
                // instead of feeding inverse/product roundoff back into every frozen tick.
                // Only the already validated affine column needs exact canonicalization.
                result[bone] = CanonicalAffine(input[bone]);
            }
            else
            {
                Matrix4x4.Invert(input[parent], out var inverseParent);
                var local = CanonicalAffine(input[bone] * CanonicalAffine(inverseParent));
                result[bone] = CanonicalAffine(local * result[parent]);
            }
            int bodyIndex = drivenBodies[bone];
            if (bodyIndex < 0 || disabledBoneIndices?.Contains(bone) == true) continue;
            var bodyWorld = backend.GetBodyWorldTransform(bodyIndex);
            // Do not let affine correction conceal a corrupt backend pose.
            PhysicsMath.ValidateRigid(bodyWorld, nameof(backend));
            var physical = CanonicalAffine(definition.InverseBodyBoneOffsets[bodyIndex] * bodyWorld);
            if (definition.Bodies[bodyIndex].Mode == PhysicsBodyMode.DynamicAligned) physical.Translation = result[bone].Translation;
            result[bone] = physical;
        }
        return result;
    }

    private static Matrix4x4 CanonicalAffine(Matrix4x4 value)
    {
        value.M14 = value.M24 = value.M34 = 0; value.M44 = 1;
        return value;
    }

    private void ValidatePose(ReadOnlySpan<Matrix4x4> input)
    {
        if (input.Length != definition.Bones.Count) throw new ArgumentException("Physics pose bone count differs from the definition.", nameof(input));
        foreach (var transform in input) PhysicsMath.ValidateRigid(transform, nameof(input));
    }

    private static Matrix4x4 Interpolate(Matrix4x4 start, Matrix4x4 target, float fraction)
    {
        var rotation = Quaternion.Slerp(Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(start)),
            Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(target)), fraction);
        return Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(Vector3.Lerp(start.Translation, target.Translation, fraction));
    }

    public void Dispose() { if (disposed) return; disposed = true; try { ReleaseSoftBindings(); } finally { backend.Dispose(); } }
}
