using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;

namespace Mmd.Modern.Physics.Bepu;

/// <summary>Experimental modern solver. Its joint/margin/material behavior is not Bullet compatibility.</summary>
public sealed partial class BepuCharacterPhysicsBackend : ICharacterPhysicsMutableBackend
{
    private readonly BufferPool pool = new();
    private Vector3 gravity;
    private readonly float? groundY;
    private readonly Dictionary<int, PhysicsBodyDefinition> materials = [];
    private readonly List<string> diagnostics = [];
    private Simulation? simulation;
    private readonly List<BodySlot?> bodies = [];
    private readonly List<JointSlot?> joints = [];
    private bool disposed;
    public string Name => "Bepu 2.4.0 Modern (experimental; not Bullet)";
    public IReadOnlyList<string> Diagnostics => diagnostics.AsReadOnly();
    public bool SupportsConcurrentIndependentWorlds => true;

    public BepuCharacterPhysicsBackend(Vector3? gravity = null, float? groundY = 0)
    {
        this.gravity = gravity ?? new Vector3(0, -98, 0); this.groundY = groundY;
        if (!Finite(this.gravity) || (groundY.HasValue && !float.IsFinite(groundY.Value))) throw new ArgumentOutOfRangeException(nameof(gravity));
    }

    public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> bodyWorldTransforms)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ArgumentNullException.ThrowIfNull(definition);
        if (bodyWorldTransforms.Length != definition.Bodies.Count) throw new ArgumentException("Body transform count mismatch.", nameof(bodyWorldTransforms));
        foreach (var transform in bodyWorldTransforms) PhysicsMath.ValidateRigid(transform, nameof(bodyWorldTransforms));
        simulation?.Dispose(); pool.Clear(); materials.Clear(); diagnostics.Clear();
        bodies.Clear(); joints.Clear();
        simulation = Simulation.Create(pool, new Contacts(materials), new Gravity(this), new SolveDescription(8, 1));
        diagnostics.Add("Experimental Bepu modern backend: contacts, damping, joints and spring tuning differ from Bullet; no MMD compatibility claim.");
        if (groundY is float floor)
            simulation.Statics.Add(new StaticDescription(new Vector3(0, floor - .5f, 0), simulation.Shapes.Add(new Box(50000, 1, 50000))));
        for (int i = 0; i < definition.Bodies.Count; i++) AddBody(definition.Bodies[i], bodyWorldTransforms[i]);
        for (int i = 0; i < definition.Joints.Count; i++)
        {
            var joint = definition.Joints[i];
            // Preserve the existing tolerant whole-model load; incremental calls reject unsupported types.
            if (joint.Type != 0)
            {
                diagnostics.Add($"Joint {i} ({joint.Name}): PMX type {joint.Type} is unsupported and was not simulated.");
                joints.Add(null);
            }
            else AddJoint(joint, definition.JointLocalFramesA[i], definition.JointLocalFramesB[i]);
        }
    }

    private (TypedIndex Shape, BodyInertia Inertia) AddShape(PhysicsBodyDefinition body)
    {
        float mass = Math.Max(body.Mass, 1e-6f);
        switch (body.Shape)
        {
            case PhysicsShapeKind.Sphere:
                var sphere = new Sphere(body.Size.X); return (simulation!.Shapes.Add(sphere), sphere.ComputeInertia(mass));
            case PhysicsShapeKind.Box:
                var box = new Box(body.Size.X * 2, body.Size.Y * 2, body.Size.Z * 2); return (simulation!.Shapes.Add(box), box.ComputeInertia(mass));
            case PhysicsShapeKind.Capsule:
                var capsule = new Capsule(body.Size.X, body.Size.Y); return (simulation!.Shapes.Add(capsule), capsule.ComputeInertia(mass));
            default: throw new ArgumentException("Unknown shape.", nameof(body));
        }
    }

    private void BuildJoint(int index, JointSlot slot)
    {
        var joint = slot.Definition;
        var a = GetSlot(joint.BodyA).Handle; var b = GetSlot(joint.BodyB).Handle;
        if (simulation!.Bodies.GetBodyReference(a).Kinematic && simulation.Bodies.GetBodyReference(b).Kinematic) return;
        var frameA = slot.FrameA; var frameB = slot.FrameB;
        var hard = new SpringSettings(60, 1);
        for (int axis = 0; axis < 3; axis++)
        {
            var unit = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
            var localAxis = Vector3.Normalize(Vector3.TransformNormal(unit, frameA));
            float lower = Component(joint.LinearLower, axis), upper = Component(joint.LinearUpper, axis);
            if (lower == upper)
                slot.Handles.Add(simulation.Solver.Add(a, b, new LinearAxisServo { LocalOffsetA = frameA.Translation, LocalOffsetB = frameB.Translation,
                    LocalPlaneNormal = localAxis, TargetOffset = lower, ServoSettings = ServoSettings.Default, SpringSettings = hard }));
            else if (lower < upper)
                slot.Handles.Add(simulation.Solver.Add(a, b, new LinearAxisLimit { LocalOffsetA = frameA.Translation, LocalOffsetB = frameB.Translation,
                    LocalAxis = localAxis, MinimumOffset = lower, MaximumOffset = upper, SpringSettings = hard }));
            float stiffness = Component(joint.LinearSpring, axis);
            // A locked axis already has its authored target; a zero-target spring would fight it.
            if (stiffness > 0 && lower != upper)
                slot.Handles.Add(simulation.Solver.Add(a, b, new LinearAxisServo { LocalOffsetA = frameA.Translation, LocalOffsetB = frameB.Translation,
                    LocalPlaneNormal = localAxis, TargetOffset = 0, ServoSettings = ServoSettings.Default, SpringSettings = Spring(stiffness, joint) }));
            // Bepu has no Bullet Generic6Dof Euler constraint. Independent twist constraints approximate it.
            var axisBasis = axis == 0 ? Matrix4x4.CreateRotationY(MathF.PI / 2) : axis == 1 ? Matrix4x4.CreateRotationX(-MathF.PI / 2) : Matrix4x4.Identity;
            var basisA = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(axisBasis * frameA));
            var basisB = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(axisBasis * frameB));
            lower = Component(joint.AngularLower, axis); upper = Component(joint.AngularUpper, axis);
            if (lower == upper)
                slot.Handles.Add(simulation.Solver.Add(a, b, new TwistServo { LocalBasisA = basisA, LocalBasisB = basisB, TargetAngle = lower,
                    ServoSettings = ServoSettings.Default, SpringSettings = hard }));
            else if (lower < upper && upper - lower < MathF.Tau)
                slot.Handles.Add(simulation.Solver.Add(a, b, new TwistLimit { LocalBasisA = basisA, LocalBasisB = basisB,
                    MinimumAngle = lower, MaximumAngle = upper, SpringSettings = hard }));
            stiffness = Component(joint.AngularSpring, axis);
            if (stiffness > 0 && lower != upper)
                slot.Handles.Add(simulation.Solver.Add(a, b, new TwistServo { LocalBasisA = basisA, LocalBasisB = basisB, TargetAngle = 0,
                    ServoSettings = ServoSettings.Default, SpringSettings = Spring(stiffness, joint) }));
        }
    }

    private SpringSettings Spring(float stiffness, PhysicsJointDefinition joint)
    {
        float invMass = InverseMass(GetSlot(joint.BodyA).Definition) + InverseMass(GetSlot(joint.BodyB).Definition);
        return new SpringSettings(Math.Clamp(MathF.Sqrt(stiffness * Math.Max(invMass, 1e-6f)) / MathF.Tau, .01f, 60), 1);
    }
    private static float InverseMass(PhysicsBodyDefinition body) => body.Mode == PhysicsBodyMode.Kinematic || body.Mass == 0 ? 0 : 1 / body.Mass;
    private static float Component(Vector3 value, int axis) => axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;

    public void SetKinematicTarget(int bodyIndex, Matrix4x4 worldTransform, float stepSeconds)
    {
        var body = GetBody(bodyIndex);
        if (!GetSlot(bodyIndex).Kinematic) throw new ArgumentException("Body is not kinematic.", nameof(bodyIndex));
        if (!float.IsFinite(stepSeconds) || stepSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(stepSeconds));
        var target = Pose(worldTransform);
        body.Velocity.Linear = (target.Position - body.Pose.Position) / stepSeconds;
        var delta = Quaternion.Normalize(target.Orientation * Quaternion.Conjugate(body.Pose.Orientation));
        if (delta.W < 0) delta = new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);
        float sine = new Vector3(delta.X, delta.Y, delta.Z).Length();
        // The quaternion logarithm tends to 2 * xyz near identity. Preserve tiny
        // authored rotations instead of turning a representable target into a dead zone.
        body.Velocity.Angular = sine < 1e-8f ? new Vector3(delta.X, delta.Y, delta.Z) * (2 / stepSeconds)
            : new Vector3(delta.X, delta.Y, delta.Z) * (2 * MathF.Atan2(sine, Math.Clamp(delta.W, -1, 1)) / (sine * stepSeconds));
        body.Awake = true;
    }

    public Matrix4x4 GetBodyWorldTransform(int bodyIndex)
    { var body = GetBody(bodyIndex); return Matrix4x4.CreateFromQuaternion(body.Pose.Orientation) * Matrix4x4.CreateTranslation(body.Pose.Position); }

    public void ApplyImpulse(int bodyIndex, Vector3 linearImpulse, Vector3 angularImpulse)
    {
        var body = GetBody(bodyIndex);
        if (!Finite(linearImpulse) || !Finite(angularImpulse)) throw new ArgumentException("Impulse momentum must be finite.");
        if (GetSlot(bodyIndex).Kinematic) return;
        body.Awake = true;
        body.ApplyLinearImpulse(linearImpulse);
        body.ApplyAngularImpulse(angularImpulse);
    }

    public void SetGravity(Vector3 gravity)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!Finite(gravity)) throw new ArgumentOutOfRangeException(nameof(gravity), "Gravity must be finite.");
        if (this.gravity == gravity) return;
        this.gravity = gravity;
        if (simulation is null) return;
        foreach (var slot in bodies)
            if (slot is not null && !slot.Kinematic)
            {
                var body = simulation.Bodies.GetBodyReference(slot.Handle);
                body.Awake = true;
            }
    }

    public void Step(float stepSeconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (simulation is null) throw new InvalidOperationException("Physics is not initialized.");
        if (!float.IsFinite(stepSeconds) || stepSeconds <= 0 || stepSeconds > 1f / 15) throw new ArgumentOutOfRangeException(nameof(stepSeconds));
        foreach (var slot in bodies)
        {
            if (slot is null || slot.Kinematic) continue;
            var config = slot.Definition;
            var body = simulation.Bodies.GetBodyReference(slot.Handle);
            body.Velocity.Linear *= MathF.Pow(1 - Math.Clamp(config.LinearDamping, 0, 1), stepSeconds);
            body.Velocity.Angular *= MathF.Pow(1 - Math.Clamp(config.AngularDamping, 0, 1), stepSeconds);
        }
        simulation.Timestep(stepSeconds);
    }

    private BodyReference GetBody(int index)
    { var slot = GetSlot(index); return simulation!.Bodies.GetBodyReference(slot.Handle); }
    private static RigidPose Pose(Matrix4x4 transform)
    {
        PhysicsMath.ValidateRigid(transform, nameof(transform));
        Matrix4x4.Decompose(transform, out _, out var rotation, out var position);
        return new RigidPose(position, Quaternion.Normalize(rotation));
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    public void Dispose() { if (disposed) return; disposed = true; simulation?.Dispose(); simulation = null; pool.Clear(); materials.Clear(); bodies.Clear(); joints.Clear(); }

    private struct Contacts(Dictionary<int, PhysicsBodyDefinition> materials) : INarrowPhaseCallbacks
    {
        public void Initialize(Simulation owner) { }
        public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
        {
            if (a.Mobility != CollidableMobility.Dynamic && b.Mobility != CollidableMobility.Dynamic) return false;
            if (a.Mobility == CollidableMobility.Static || b.Mobility == CollidableMobility.Static) return true; // Saba ground bypasses group masks.
            return PhysicsMath.ShouldCollide(materials[a.BodyHandle.Value], materials[b.BodyHandle.Value]);
        }
        public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;
        public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterial)
            where TManifold : unmanaged, IContactManifold<TManifold>
        {
            float frictionA = pair.A.Mobility == CollidableMobility.Static ? .5f : materials[pair.A.BodyHandle.Value].Friction;
            float frictionB = pair.B.Mobility == CollidableMobility.Static ? .5f : materials[pair.B.BodyHandle.Value].Friction;
            pairMaterial = new PairMaterialProperties { FrictionCoefficient = Math.Min(10, Math.Max(0, frictionA) * Math.Max(0, frictionB)),
                MaximumRecoveryVelocity = 10, SpringSettings = new SpringSettings(60, 1) };
            return true;
        }
        public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold) => true;
        public void Dispose() { }
    }
    private struct Gravity(BepuCharacterPhysicsBackend owner) : IPoseIntegratorCallbacks
    {
        private Vector3Wide acceleration;
        public AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
        public bool AllowSubstepsForUnconstrainedBodies => false;
        public bool IntegrateVelocityForKinematics => false;
        public void Initialize(Simulation owner) { }
        public void PrepareForIntegration(float dt) => acceleration = Vector3Wide.Broadcast(owner.gravity * dt);
        public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation, BodyInertiaWide localInertia,
            Vector<int> integrationMask, int workerIndex, Vector<float> dt, ref BodyVelocityWide velocity) => velocity.Linear += acceleration;
    }
}
