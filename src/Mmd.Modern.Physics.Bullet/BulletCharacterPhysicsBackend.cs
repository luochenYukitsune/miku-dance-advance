using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mmd.Modern.Physics.Bullet;

/// <summary>Owns one Bullet 2.75 world in raw PMX coordinates, using the float32 C ABI.</summary>
public sealed partial class BulletCharacterPhysicsBackend : ICharacterPhysicsBatchedBackend, ICharacterSoftBodyProvider, ICharacterPhysicsJointPullBackend
{
    private Vector3 gravity;
    private readonly float? groundY;
    private WorldHandle? world;
    private CharacterPhysicsDefinition? definition;
    private IReadOnlyList<string> diagnostics = Array.Empty<string>();
    private bool disposed;
    private readonly bool mmdJointPullCorrection;
    private bool jointPullCapabilityChecked;

    public string Name => "Bullet 2.75 Compatibility (float32)";
    public IReadOnlyList<string> Diagnostics => diagnostics;
    public bool SupportsConcurrentIndependentWorlds => true;
    public bool SupportsBatchedExecution => batchedCapabilitySupported.Value;

    public BulletCharacterPhysicsBackend(Vector3? gravity = null, float? groundY = 0, bool coupledSoftBodies = false,
        bool mmdJointPullCorrection = true)
    {
        this.gravity = gravity ?? new Vector3(0, -98, 0);
        this.groundY = groundY;
        this.coupledSoftBodies = coupledSoftBodies;
        this.mmdJointPullCorrection = mmdJointPullCorrection;
        if (!Finite(this.gravity)) throw new ArgumentOutOfRangeException(nameof(gravity));
        if (groundY.HasValue && !float.IsFinite(groundY.Value)) throw new ArgumentOutOfRangeException(nameof(groundY));
    }

    public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> bodyWorldTransforms)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(definition);
        if (bodyWorldTransforms.Length != definition.Bodies.Count)
            throw new ArgumentException("Body transform count mismatch.", nameof(bodyWorldTransforms));

        // Complete managed validation before allocating or replacing a native world.
        var bodies = new NativeBody[definition.Bodies.Count];
        for (int i = 0; i < bodies.Length; i++)
        {
            var body = definition.Bodies[i];
            bodies[i] = new NativeBody
            {
                Shape = body.Shape switch
                {
                    PhysicsShapeKind.Sphere => 0, PhysicsShapeKind.Box => 1, PhysicsShapeKind.Capsule => 2,
                    _ => throw new ArgumentException($"Unknown physics shape on body {i}.", nameof(definition))
                },
                Mode = body.Mode switch
                {
                    PhysicsBodyMode.Kinematic => 0, PhysicsBodyMode.Dynamic => 1, PhysicsBodyMode.DynamicAligned => 2,
                    _ => throw new ArgumentException($"Unknown physics mode on body {i}.", nameof(definition))
                },
                CollisionGroup = body.CollisionGroup, CollisionMask = body.CollisionMask,
                Pose = Pose(bodyWorldTransforms[i], nameof(bodyWorldTransforms)), Size = new(body.Size),
                Mass = body.Mass, LinearDamping = body.LinearDamping, AngularDamping = body.AngularDamping,
                Restitution = body.Restitution, Friction = body.Friction
            };
        }
        var joints = new NativeJoint[definition.Joints.Count];
        for (int i = 0; i < joints.Length; i++)
        {
            var joint = definition.Joints[i];
            joints[i] = new NativeJoint
            {
                BodyA = joint.BodyA, BodyB = joint.BodyB, Type = joint.Type,
                FrameA = Pose(definition.JointLocalFramesA[i], nameof(definition)),
                FrameB = Pose(definition.JointLocalFramesB[i], nameof(definition)),
                LinearLower = new(joint.LinearLower), LinearUpper = new(joint.LinearUpper),
                AngularLower = new(joint.AngularLower), AngularUpper = new(joint.AngularUpper),
                LinearSpring = new(joint.LinearSpring), AngularSpring = new(joint.AngularSpring)
            };
        }

        Check(Native.GetAbi(out var abi), "query ABI");
        if (abi.AbiVersion != 4 || abi.BulletVersion != 275 || abi.ScalarBytes != sizeof(float)
            || abi.PoseBytes != Marshal.SizeOf<NativePose>() || abi.BodyBytes != Marshal.SizeOf<NativeBody>()
            || abi.JointBytes != Marshal.SizeOf<NativeJoint>())
            throw new InvalidOperationException($"Incompatible Bullet ABI: version {abi.AbiVersion}, Bullet {abi.BulletVersion}, "
                + $"scalar/pose/body/joint bytes {abi.ScalarBytes}/{abi.PoseBytes}/{abi.BodyBytes}/{abi.JointBytes}.");

        if (coupledSoftBodies) RequireCoupledCapability();
        if (mmdJointPullCorrection && definition.Joints.Any(joint => joint.Type <= 5)) RequireJointPullCapability();
        IntPtr pointer;
        int status = coupledSoftBodies
            ? Native.CreateCoupled(new(gravity), groundY.HasValue ? 1 : 0, groundY ?? 0, out pointer)
            : Native.Create(new(gravity), groundY.HasValue ? 1 : 0, groundY ?? 0, out pointer);
        WorldHandle? candidate = new(pointer);
        try
        {
            Check(status, "create world");
            if (candidate.IsInvalid) throw new InvalidOperationException("Bullet returned a null world after successful creation.");
            for (int i = 0; i < bodies.Length; i++) Check(Native.AddBody(candidate, in bodies[i]), $"add body {i}");
            var messages = new List<string>();
            for (int i = 0; i < joints.Length; i++)
            {
                status = Native.AddJoint(candidate, in joints[i]);
                if (status == 2)
                {
                    string error = LastError();
                    // A missing implementation of a supported type must fail initialization.
                    if (joints[i].Type <= 5) throw Failure(status, $"add joint {i}", error);
                    messages.Add($"Joint {i} ({definition.Joints[i].Name}): PMX type {joints[i].Type} is unsupported and was not simulated. {error}");
                }
                else Check(status, $"add joint {i}");
            }
            var publishedDiagnostics = messages.AsReadOnly();
            var previous = world;
            InvalidateSoftBindings();
            world = candidate;
            this.definition = definition;
            mutableBodies.Clear(); temporaryKinematic.Clear(); mutableJoints.Clear();
            for (int i = 0; i < definition.Bodies.Count; i++) mutableBodies.Add(i, definition.Bodies[i]);
            int slot = 0;
            foreach (var joint in definition.Joints.Where(joint => joint.Type <= 5))
                mutableJoints.Add(slot++, (joint.BodyA, joint.BodyB));
            diagnostics = publishedDiagnostics;
            candidate = null;
            previous?.Dispose();
        }
        finally { candidate?.Dispose(); }
    }

    public void SetKinematicTarget(int bodyIndex, Matrix4x4 worldTransform, float stepSeconds)
    {
        var current = GetWorld(bodyIndex);
        if (mutableBodies[bodyIndex].Mode != PhysicsBodyMode.Kinematic && !temporaryKinematic.Contains(bodyIndex))
            throw new ArgumentException("Body is not kinematic.", nameof(bodyIndex));
        ValidateStep(stepSeconds);
        var target = Pose(worldTransform, nameof(worldTransform));
        int status = Native.SetKinematicTarget(current, bodyIndex, in target);
        if (status != 0) throw Failure(status, $"set kinematic target for body {bodyIndex}", LastError());
    }

    public Matrix4x4 GetBodyWorldTransform(int bodyIndex)
    {
        var current = GetWorld(bodyIndex);
        int status = Native.GetBodyPose(current, bodyIndex, out var pose);
        if (status != 0) throw Failure(status, $"get pose for body {bodyIndex}", LastError());
        return MatrixFromPose(in pose, bodyIndex);
    }

    private static Matrix4x4 MatrixFromPose(in NativePose pose, int bodyIndex)
    {
        var rotation = new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw);
        if (!Finite(pose.Position.Value) || !float.IsFinite(rotation.LengthSquared()) || rotation.LengthSquared() <= 0)
            throw new InvalidOperationException($"Bullet returned a nonfinite or invalid pose for body {bodyIndex}.");
        return Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation)) * Matrix4x4.CreateTranslation(pose.Position.Value);
    }

    public void ApplyImpulse(int bodyIndex, Vector3 linearImpulse, Vector3 angularImpulse)
    {
        var current = GetWorld(bodyIndex);
        if (!Finite(linearImpulse) || !Finite(angularImpulse)) throw new ArgumentException("Impulse momentum must be finite.");
        var config = mutableBodies[bodyIndex];
        if (config.Mode == PhysicsBodyMode.Kinematic || temporaryKinematic.Contains(bodyIndex) || config.Mass == 0) return;
        Check(Native.ApplyImpulse(current, bodyIndex, new(linearImpulse), new(angularImpulse)), $"apply impulse to body {bodyIndex}");
    }

    public void Step(float stepSeconds)
    {
        var current = GetWorld();
        ValidateStep(stepSeconds);
        Check(Native.Step(current, stepSeconds), "step world");
    }

    public void SetGravity(Vector3 gravity)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!Finite(gravity)) throw new ArgumentOutOfRangeException(nameof(gravity), "Gravity must be finite.");
        if (this.gravity == gravity) return;
        if (world is not null) Check(Native.SetGravity(world, new(gravity)), "set gravity");
        this.gravity = gravity;
    }

    private WorldHandle GetWorld()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return world ?? throw new InvalidOperationException("Physics is not initialized.");
    }

    private WorldHandle GetWorld(int bodyIndex)
    {
        var current = GetWorld();
        if (!mutableBodies.ContainsKey(bodyIndex)) throw new ArgumentOutOfRangeException(nameof(bodyIndex));
        return current;
    }

    private static void ValidateStep(float stepSeconds)
    {
        if (!float.IsFinite(stepSeconds) || stepSeconds <= 0 || stepSeconds > 1f / 15)
            throw new ArgumentOutOfRangeException(nameof(stepSeconds));
    }

    private static NativePose Pose(Matrix4x4 transform, string parameterName)
    {
        PhysicsMath.ValidateRigid(transform, parameterName);
        Matrix4x4.Decompose(transform, out _, out var rotation, out var position);
        rotation = Quaternion.Normalize(rotation);
        return new NativePose { Position = new(position), Qx = rotation.X, Qy = rotation.Y, Qz = rotation.Z, Qw = rotation.W };
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static string LastError() => Marshal.PtrToStringUTF8(Native.LastError()) is { Length: > 0 } error ? error : "No native error detail.";
    private static InvalidOperationException Failure(int status, string operation, string error) =>
        new($"Bullet failed to {operation} (status {status}): {error}");
    private static void Check(int status, string operation)
    {
        if (status != 0) throw Failure(status, operation, LastError());
    }

    public void Dispose()
    {
        if (disposed) return;
        InvalidateSoftBindings();
        disposed = true;
        world?.Dispose();
        world = null;
        definition = null;
    }

    private sealed class WorldHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public WorldHandle(IntPtr pointer) : base(true) => SetHandle(pointer);
        protected override bool ReleaseHandle() { Native.Destroy(handle); return true; }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct NativeVector(Vector3 value)
    {
        public float X = value.X, Y = value.Y, Z = value.Z;
        public readonly Vector3 Value => new(X, Y, Z);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct NativePose
    {
        public NativeVector Position;
        public float Qx, Qy, Qz, Qw;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct NativeBody
    {
        public int Shape, Mode, CollisionGroup, CollisionMask;
        public NativePose Pose;
        public NativeVector Size;
        public float Mass, LinearDamping, AngularDamping, Restitution, Friction;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct NativeJoint
    {
        public int BodyA, BodyB, Type;
        public NativePose FrameA, FrameB;
        public NativeVector LinearLower, LinearUpper, AngularLower, AngularUpper, LinearSpring, AngularSpring;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct NativeAbi
    {
        public int AbiVersion, BulletVersion, ScalarBytes, PoseBytes, BodyBytes, JointBytes;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct NativeKinematicTargetItem
    {
        public int BodyIndex, IsKinematic;
        public NativePose Target;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct NativeJointPullItem
    {
        public int BodyA, BodyB;
        public float MaximumDistance;
    }

    private static partial class Native
    {
        private const string Library = "mmd_bullet";
        [DllImport(Library, EntryPoint = "mmd_bullet_get_abi", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int GetAbi(out NativeAbi result);
        [DllImport(Library, EntryPoint = "mmd_bullet_last_error", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern IntPtr LastError();
        [DllImport(Library, EntryPoint = "mmd_bullet_create", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int Create(NativeVector gravity, int hasGround, float groundY, out IntPtr result);
        [DllImport(Library, EntryPoint = "mmd_bullet_destroy", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern void Destroy(IntPtr context);
        [DllImport(Library, EntryPoint = "mmd_bullet_add_body", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int AddBody(WorldHandle context, in NativeBody description);
        [DllImport(Library, EntryPoint = "mmd_bullet_add_joint", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int AddJoint(WorldHandle context, in NativeJoint description);
        [DllImport(Library, EntryPoint = "mmd_bullet_set_kinematic_target", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int SetKinematicTarget(WorldHandle context, int index, in NativePose target);
        [DllImport(Library, EntryPoint = "mmd_bullet_get_body_pose", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int GetBodyPose(WorldHandle context, int index, out NativePose result);
        [DllImport(Library, EntryPoint = "mmd_bullet_apply_impulse", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int ApplyImpulse(WorldHandle context, int index, NativeVector linear, NativeVector angular);
        [DllImport(Library, EntryPoint = "mmd_bullet_set_gravity", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int SetGravity(WorldHandle context, NativeVector gravity);
        [DllImport(Library, EntryPoint = "mmd_bullet_step", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int Step(WorldHandle context, float seconds);
    }
}
