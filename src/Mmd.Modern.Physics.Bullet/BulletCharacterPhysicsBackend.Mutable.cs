using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Mmd.Modern.Physics.Bullet;

public sealed partial class BulletCharacterPhysicsBackend
{
    private static readonly Lazy<bool> batchedCapabilitySupported = new(DetectBatchedSupport);
    private readonly Dictionary<int, PhysicsBodyDefinition> mutableBodies = [];
    private readonly Dictionary<int, (int A, int B)> mutableJoints = [];
    private readonly HashSet<int> temporaryKinematic = [];
    private WorldHandle? bodyModeCacheWorld;
    private bool bodyModeCacheUncertain;

    private static bool DetectBatchedSupport()
    {
        try
        {
            if ((Native.Features() & 4) == 0) return false;
            IntPtr module = NativeLibrary.Load("mmd_bullet", typeof(BulletCharacterPhysicsBackend).Assembly, null);
            try
            {
                return NativeLibrary.TryGetExport(module, "mmd_bullet_set_kinematic_targets_batch", out _)
                    && NativeLibrary.TryGetExport(module, "mmd_bullet_correct_joints_and_get_poses", out _);
            }
            finally { NativeLibrary.Free(module); }
        }
        catch (EntryPointNotFoundException) { return false; }
        catch (DllNotFoundException) { return false; }
    }

    public void ValidateJointPullSupport()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (mmdJointPullCorrection) RequireJointPullCapability();
    }

    /// <summary>Original MMD C3460 safeguard; false constructor option retains Saba reflection.
    /// No reset, pair-cache purge, force clear or rotation conversion is performed.</summary>
    public void CorrectJointOverextension(int bodyA, int bodyB, float maximumDistance)
    {
        var current = GetWorld(bodyA); _ = GetWorld(bodyB);
        if (bodyA == bodyB || !float.IsFinite(maximumDistance) || maximumDistance < 0)
            throw new ArgumentException("Pull correction requires distinct bodies and a finite nonnegative distance.");
        if (!mmdJointPullCorrection) return;
        RequireJointPullCapability();
        Check(Native.CorrectJointOverextension(current, bodyA, bodyB, maximumDistance), "correct joint overextension");
    }

    private void RequireJointPullCapability()
    {
        if (jointPullCapabilityChecked) return;
        try
        {
            if ((Native.Features() & 2) == 0)
                throw new NotSupportedException("Bullet native library lacks the MMD joint pull safeguard capability (bit 2).");
            // Check both advertised support and the actual export before publishing a new world.
            IntPtr module = NativeLibrary.Load("mmd_bullet", typeof(BulletCharacterPhysicsBackend).Assembly, null);
            try { _ = NativeLibrary.GetExport(module, "mmd_bullet_correct_joint_overextension"); }
            finally { NativeLibrary.Free(module); }
            jointPullCapabilityChecked = true;
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new NotSupportedException("Bullet native library lacks the MMD joint pull safeguard capability.", exception);
        }
    }

    public int AddBody(PhysicsBodyDefinition body, Matrix4x4 worldTransform)
    {
        var current = GetWorld(); ArgumentNullException.ThrowIfNull(body);
        // The definition owns the authoritative shape/finite/range validation. Bone association is
        // application metadata and may reference an actor-local table absent from this shared world.
        _ = new CharacterPhysicsDefinition([], [body with { BoneIndex = -1 }], []);
        var value = new NativeBody
        {
            Shape = (int)body.Shape, Mode = (int)body.Mode, CollisionGroup = body.CollisionGroup, CollisionMask = body.CollisionMask,
            Pose = Pose(worldTransform, nameof(worldTransform)), Size = new(body.Size), Mass = body.Mass,
            LinearDamping = body.LinearDamping, AngularDamping = body.AngularDamping, Restitution = body.Restitution, Friction = body.Friction
        };
        Check(Native.AddBodySlot(current, in value, out int index), "add incremental body");
        mutableBodies.Add(index, body); return index;
    }

    public int AddJoint(PhysicsJointDefinition joint, Matrix4x4 localFrameA, Matrix4x4 localFrameB)
    {
        ArgumentNullException.ThrowIfNull(joint);
        var current = GetWorld(joint.BodyA); _ = GetWorld(joint.BodyB);
        if (joint.Type > 5) throw new NotSupportedException($"PMX joint type {joint.Type} is not supported.");
        _ = new CharacterPhysicsDefinition([], [mutableBodies[joint.BodyA] with { BoneIndex = -1 }, mutableBodies[joint.BodyB] with { BoneIndex = -1 }],
            [joint with { BodyA = 0, BodyB = 1 }]);
        if (joint.BodyA == joint.BodyB) throw new ArgumentException("A joint needs two distinct bodies.", nameof(joint));
        var value = new NativeJoint
        {
            BodyA = joint.BodyA, BodyB = joint.BodyB, Type = joint.Type,
            FrameA = Pose(localFrameA, nameof(localFrameA)), FrameB = Pose(localFrameB, nameof(localFrameB)),
            LinearLower = new(joint.LinearLower), LinearUpper = new(joint.LinearUpper), AngularLower = new(joint.AngularLower), AngularUpper = new(joint.AngularUpper),
            LinearSpring = new(joint.LinearSpring), AngularSpring = new(joint.AngularSpring)
        };
        ValidateJointPullSupport();
        Check(Native.AddJointSlot(current, in value, out int index), "add incremental joint");
        mutableJoints.Add(index, (joint.BodyA, joint.BodyB)); return index;
    }

    public void RemoveJoint(int jointIndex)
    {
        var current = GetWorld();
        if (!mutableJoints.ContainsKey(jointIndex)) throw new ArgumentOutOfRangeException(nameof(jointIndex));
        Check(Native.RemoveJoint(current, jointIndex), "remove joint"); mutableJoints.Remove(jointIndex);
    }
    public void RemoveBody(int bodyIndex)
    {
        var current = GetWorld(bodyIndex);
        if (mutableJoints.Values.Any(joint => joint.A == bodyIndex || joint.B == bodyIndex))
            throw new InvalidOperationException("Remove referencing joints before removing a body.");
        if (softBindings.Any(binding => binding.ReferencesSlot(bodyIndex)))
            throw new InvalidOperationException("Dispose referencing soft bindings before removing a body.");
        Check(Native.RemoveBody(current, bodyIndex), "remove body"); mutableBodies.Remove(bodyIndex); temporaryKinematic.Remove(bodyIndex);
    }
    public void ResetBody(int bodyIndex, Matrix4x4 worldTransform)
    {
        var current = GetWorld(bodyIndex); var value = Pose(worldTransform, nameof(worldTransform));
        Check(Native.ResetBody(current, bodyIndex, in value), "reset body");
    }
    public void SetBodyKinematic(int bodyIndex, bool kinematic)
    {
        var current = GetWorld(bodyIndex);
        if (!ReferenceEquals(bodyModeCacheWorld, current))
        {
            bodyModeCacheWorld = current;
            bodyModeCacheUncertain = false;
        }
        // Mirrors native temporary_kinematic, including the authored-kinematic no-op.
        // Initialize/AddBody start false; ResetBody preserves it and removal drops it.
        bool temporary = kinematic && mutableBodies[bodyIndex].Mode != PhysicsBodyMode.Kinematic;
        if (!bodyModeCacheUncertain && temporary == temporaryKinematic.Contains(bodyIndex)) return;
        try { Check(Native.SetBodyKinematic(current, bodyIndex, kinematic ? 1 : 0), "change body mode"); }
        catch
        {
            // Native may have changed its mode before a later operation failed. Keep
            // calling it for this world rather than treating the old mirror as known.
            bodyModeCacheUncertain = true;
            throw;
        }
        if (temporary) temporaryKinematic.Add(bodyIndex);
        else temporaryKinematic.Remove(bodyIndex);
    }

    public unsafe void ApplyKinematicTargetsBatch(ReadOnlySpan<PhysicsKinematicTargetBatchItem> targets, float stepSeconds)
    {
        var current = GetWorld();
        if (!ReferenceEquals(bodyModeCacheWorld, current))
        {
            bodyModeCacheWorld = current;
            bodyModeCacheUncertain = false;
        }
        if (!SupportsBatchedExecution)
        {
            for (int i = 0; i < targets.Length; i++)
            {
                var item = targets[i];
                SetBodyKinematic(item.BodyIndex, item.Kinematic);
                if (item.Kinematic) SetKinematicTarget(item.BodyIndex, item.WorldTransform, stepSeconds);
            }
            return;
        }
        if (targets.IsEmpty) return;

        NativeKinematicTargetItem[]? rented = null;
        Span<NativeKinematicTargetItem> nativeItems = targets.Length <= 128
            ? stackalloc NativeKinematicTargetItem[targets.Length]
            : (rented = ArrayPool<NativeKinematicTargetItem>.Shared.Rent(targets.Length)).AsSpan(0, targets.Length);
        try
        {
            bool stepValidated = false;
            for (int i = 0; i < targets.Length; i++)
            {
                var item = targets[i];
                if (!mutableBodies.ContainsKey(item.BodyIndex))
                    throw new ArgumentOutOfRangeException(nameof(targets));
                if (item.Kinematic)
                {
                    if (!stepValidated) { ValidateStep(stepSeconds); stepValidated = true; }
                    nativeItems[i] = new NativeKinematicTargetItem
                    {
                        BodyIndex = item.BodyIndex,
                        IsKinematic = 1,
                        Target = Pose(item.WorldTransform, nameof(targets))
                    };
                }
                else
                {
                    nativeItems[i] = new NativeKinematicTargetItem { BodyIndex = item.BodyIndex, IsKinematic = 0 };
                }
            }

            int writeCount = 0;
            for (int i = 0; i < targets.Length; i++)
            {
                var entry = nativeItems[i];
                bool kinematic = entry.IsKinematic != 0;
                bool temporary = kinematic && mutableBodies[entry.BodyIndex].Mode != PhysicsBodyMode.Kinematic;
                bool modeChanged = temporary != temporaryKinematic.Contains(entry.BodyIndex);
                if (bodyModeCacheUncertain || modeChanged || kinematic)
                {
                    nativeItems[writeCount++] = entry;
                    if (temporary) temporaryKinematic.Add(entry.BodyIndex);
                    else temporaryKinematic.Remove(entry.BodyIndex);
                }
            }
            if (writeCount == 0) return;

            fixed (NativeKinematicTargetItem* ptr = nativeItems)
            {
                int status = Native.SetKinematicTargetsBatch(current, ptr, writeCount);
                if (status != 0)
                {
                    bodyModeCacheUncertain = true;
                    throw Failure(status, "apply kinematic targets batch", LastError());
                }
            }
        }
        finally
        {
            if (rented is not null) ArrayPool<NativeKinematicTargetItem>.Shared.Return(rented);
        }
    }

    public unsafe void CorrectJointsAndGetBodyTransforms(ReadOnlySpan<PhysicsJointPullBatchItem> joints,
        ReadOnlySpan<int> bodyIndices, Span<Matrix4x4> worldTransforms)
    {
        var current = GetWorld();
        if (bodyIndices.Length != worldTransforms.Length)
            throw new ArgumentException("Transform buffer length mismatch.", nameof(worldTransforms));
        for (int i = 0; i < joints.Length; i++)
        {
            var joint = joints[i];
            if (!mutableBodies.ContainsKey(joint.BodyA) || !mutableBodies.ContainsKey(joint.BodyB))
                throw new ArgumentOutOfRangeException(nameof(joints));
            if (joint.BodyA == joint.BodyB || !float.IsFinite(joint.MaximumDistance) || joint.MaximumDistance < 0)
                throw new ArgumentException("Pull correction requires distinct bodies and a finite nonnegative distance.");
        }
        for (int i = 0; i < bodyIndices.Length; i++)
            if (!mutableBodies.ContainsKey(bodyIndices[i]))
                throw new ArgumentOutOfRangeException(nameof(bodyIndices));
        if (mmdJointPullCorrection && joints.Length > 0) RequireJointPullCapability();
        if (!SupportsBatchedExecution)
        {
            if (mmdJointPullCorrection)
                for (int i = 0; i < joints.Length; i++)
                    CorrectJointOverextension(joints[i].BodyA, joints[i].BodyB, joints[i].MaximumDistance);
            for (int i = 0; i < bodyIndices.Length; i++)
                worldTransforms[i] = GetBodyWorldTransform(bodyIndices[i]);
            return;
        }
        int activeJointCount = mmdJointPullCorrection ? joints.Length : 0;
        if (activeJointCount == 0 && bodyIndices.Length == 0) return;

        NativeJointPullItem[]? rentedJoints = null;
        NativePose[]? rentedPoses = null;
        Span<NativeJointPullItem> nativeJoints = activeJointCount <= 128
            ? stackalloc NativeJointPullItem[activeJointCount]
            : (rentedJoints = ArrayPool<NativeJointPullItem>.Shared.Rent(activeJointCount)).AsSpan(0, activeJointCount);
        Span<NativePose> nativePoses = bodyIndices.Length <= 128
            ? stackalloc NativePose[bodyIndices.Length]
            : (rentedPoses = ArrayPool<NativePose>.Shared.Rent(bodyIndices.Length)).AsSpan(0, bodyIndices.Length);
        try
        {
            for (int i = 0; i < activeJointCount; i++)
            {
                var joint = joints[i];
                nativeJoints[i] = new NativeJointPullItem
                {
                    BodyA = joint.BodyA,
                    BodyB = joint.BodyB,
                    MaximumDistance = joint.MaximumDistance
                };
            }
            fixed (NativeJointPullItem* jointsPtr = nativeJoints)
            fixed (int* bodiesPtr = bodyIndices)
            fixed (NativePose* posesPtr = nativePoses)
            {
                Check(Native.CorrectJointsAndGetPoses(current, jointsPtr, activeJointCount, bodiesPtr, bodyIndices.Length, posesPtr),
                    "correct joints and get poses");
            }
            for (int i = 0; i < bodyIndices.Length; i++)
                worldTransforms[i] = MatrixFromPose(in nativePoses[i], bodyIndices[i]);
        }
        finally
        {
            if (rentedJoints is not null) ArrayPool<NativeJointPullItem>.Shared.Return(rentedJoints);
            if (rentedPoses is not null) ArrayPool<NativePose>.Shared.Return(rentedPoses);
        }
    }

    private static partial class Native
    {
        [DllImport(Library, EntryPoint = "mmd_bullet_correct_joint_overextension", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int CorrectJointOverextension(WorldHandle world, int bodyA, int bodyB, float maximumDistance);
        [DllImport(Library, EntryPoint = "mmd_bullet_add_body_slot", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int AddBodySlot(WorldHandle world, in NativeBody body, out int index);
        [DllImport(Library, EntryPoint = "mmd_bullet_add_joint_slot", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int AddJointSlot(WorldHandle world, in NativeJoint joint, out int index);
        [DllImport(Library, EntryPoint = "mmd_bullet_remove_joint", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int RemoveJoint(WorldHandle world, int index);
        [DllImport(Library, EntryPoint = "mmd_bullet_remove_body", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int RemoveBody(WorldHandle world, int index);
        [DllImport(Library, EntryPoint = "mmd_bullet_reset_body", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int ResetBody(WorldHandle world, int index, in NativePose pose);
        [DllImport(Library, EntryPoint = "mmd_bullet_set_body_kinematic", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int SetBodyKinematic(WorldHandle world, int index, int enabled);
        [DllImport(Library, EntryPoint = "mmd_bullet_set_kinematic_targets_batch", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static unsafe extern int SetKinematicTargetsBatch(WorldHandle world, NativeKinematicTargetItem* items, int count);
        [DllImport(Library, EntryPoint = "mmd_bullet_correct_joints_and_get_poses", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static unsafe extern int CorrectJointsAndGetPoses(WorldHandle world, NativeJointPullItem* joints, int jointCount, int* bodyIndices, int bodyCount, NativePose* posesOut);
    }
}
