using System.Numerics;
using System.Runtime.InteropServices;

namespace Mmd.Modern.Physics.Bullet;

public sealed partial class BulletCharacterPhysicsBackend
{
    private readonly Dictionary<int, PhysicsBodyDefinition> mutableBodies = [];
    private readonly Dictionary<int, (int A, int B)> mutableJoints = [];
    private readonly HashSet<int> temporaryKinematic = [];
    private WorldHandle? bodyModeCacheWorld;
    private bool bodyModeCacheUncertain;

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
    }
}
