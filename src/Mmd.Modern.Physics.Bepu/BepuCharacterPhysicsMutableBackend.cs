using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;

namespace Mmd.Modern.Physics.Bepu;

public sealed partial class BepuCharacterPhysicsBackend
{
    private sealed class BodySlot(PhysicsBodyDefinition definition, BodyHandle handle, TypedIndex shape, BodyInertia inertia, bool kinematic)
    {
        public PhysicsBodyDefinition Definition { get; } = definition;
        public BodyHandle Handle { get; } = handle;
        public TypedIndex Shape { get; } = shape;
        public BodyInertia AuthoredInertia { get; } = inertia;
        public bool Kinematic { get; set; } = kinematic;
    }

    private sealed class JointSlot(PhysicsJointDefinition definition, Matrix4x4 frameA, Matrix4x4 frameB)
    {
        public PhysicsJointDefinition Definition { get; } = definition;
        public Matrix4x4 FrameA { get; } = frameA;
        public Matrix4x4 FrameB { get; } = frameB;
        public List<ConstraintHandle> Handles { get; } = [];
    }

    private void RequireWorld()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (simulation is null) throw new InvalidOperationException("Physics is not initialized.");
    }

    private BodySlot GetSlot(int index)
    {
        RequireWorld();
        if ((uint)index >= bodies.Count || bodies[index] is not { } slot) throw new ArgumentOutOfRangeException(nameof(index));
        return slot;
    }

    private JointSlot GetJoint(int index)
    {
        RequireWorld();
        if ((uint)index >= joints.Count || joints[index] is not { } slot) throw new ArgumentOutOfRangeException(nameof(index));
        return slot;
    }

    public int AddBody(PhysicsBodyDefinition body, Matrix4x4 worldTransform)
    {
        RequireWorld(); ArgumentNullException.ThrowIfNull(body);
        // Bone mapping belongs to the caller's actor; the solver needs only a valid authored body.
        if (body.BoneIndex < -1) throw new ArgumentException("Invalid bone index.", nameof(body));
        _ = new CharacterPhysicsDefinition([], [body with { BoneIndex = -1 }], []);
        var pose = Pose(worldTransform);
        var (shape, inertia) = AddShape(body);
        bool kinematic = body.Mode == PhysicsBodyMode.Kinematic || body.Mass == 0;
        var description = kinematic ? BodyDescription.CreateKinematic(pose, shape, -1f)
            : BodyDescription.CreateDynamic(pose, inertia, shape, -1f);
        BodyHandle handle;
        try { handle = simulation!.Bodies.Add(description); }
        catch { simulation!.Shapes.RemoveAndDispose(shape, pool); throw; }
        int index = bodies.Count;
        bodies.Add(new BodySlot(body, handle, shape, inertia, kinematic));
        materials.Add(handle.Value, body);
        if (body.LinearDamping is < 0 or > 1 || body.AngularDamping is < 0 or > 1)
            diagnostics.Add($"Body {index} ({body.Name}): raw damping {body.LinearDamping}/{body.AngularDamping} preserved; simulation clamps each value to [0,1], as Bullet setDamping does.");
        if (body.Restitution != 0) diagnostics.Add($"Body {index} ({body.Name}): restitution {body.Restitution} preserved in data but unsupported by the default Bepu contact solver.");
        if (body.Friction < 0) diagnostics.Add($"Body {index} ({body.Name}): raw negative friction {body.Friction} preserved; modern contact friction clamps it to zero.");
        return index;
    }

    public int AddJoint(PhysicsJointDefinition joint, Matrix4x4 localFrameA, Matrix4x4 localFrameB)
    {
        RequireWorld(); ArgumentNullException.ThrowIfNull(joint);
        if (joint.Type != 0) throw new NotSupportedException($"Bepu does not support PMX joint type {joint.Type}.");
        _ = GetSlot(joint.BodyA); _ = GetSlot(joint.BodyB);
        if (joint.Name is null || joint.BodyA == joint.BodyB || !Finite(joint.Position) || !Finite(joint.Rotation)
            || !Finite(joint.LinearLower) || !Finite(joint.LinearUpper) || !Finite(joint.AngularLower) || !Finite(joint.AngularUpper)
            || !Finite(joint.LinearSpring) || !Finite(joint.AngularSpring))
            throw new ArgumentException("Invalid physics joint.", nameof(joint));
        PhysicsMath.ValidateRigid(localFrameA, nameof(localFrameA)); PhysicsMath.ValidateRigid(localFrameB, nameof(localFrameB));
        var slot = new JointSlot(joint, localFrameA, localFrameB);
        int index = joints.Count;
        try { BuildJoint(index, slot); }
        catch { RemoveSolverConstraints(slot); throw; }
        joints.Add(slot);
        if (Vector3.Min(joint.LinearSpring, Vector3.Zero) != Vector3.Zero || Vector3.Min(joint.AngularSpring, Vector3.Zero) != Vector3.Zero)
            diagnostics.Add($"Joint {index} ({joint.Name}): raw negative spring coefficients preserved but not simulated by the modern backend.");
        diagnostics.Add($"Joint {index} ({joint.Name}): linear axes mapped to Bepu limits/servos; angular axes use independent twist approximations; spring stiffness mapped to bounded frequency, not Bullet Generic6DofSpring.");
        return index;
    }

    private void RemoveSolverConstraints(JointSlot slot)
    {
        foreach (var handle in slot.Handles)
            if (simulation!.Solver.ConstraintExists(handle)) simulation.Solver.Remove(handle);
        slot.Handles.Clear();
    }

    public void RemoveJoint(int jointIndex)
    {
        var slot = GetJoint(jointIndex);
        RemoveSolverConstraints(slot); joints[jointIndex] = null;
    }

    public void RemoveBody(int bodyIndex)
    {
        var slot = GetSlot(bodyIndex);
        if (joints.Any(j => j is not null && (j.Definition.BodyA == bodyIndex || j.Definition.BodyB == bodyIndex)))
            throw new InvalidOperationException("Remove referencing joints before removing this body.");
        simulation!.Bodies.Remove(slot.Handle);
        materials.Remove(slot.Handle.Value);
        simulation.Shapes.RemoveAndDispose(slot.Shape, pool);
        bodies[bodyIndex] = null;
    }

    public void ResetBody(int bodyIndex, Matrix4x4 worldTransform)
    {
        var body = GetBody(bodyIndex); var target = Pose(worldTransform);
        body.Awake = true;
        // Bepu 2.4 Solver.Remove removes contact PairCache references too. Keep authored joint
        // constraints and all unrelated contact pairs, body handles and accumulated momentum.
        for (int i = body.Constraints.Count - 1; i >= 0; i--)
        {
            var handle = body.Constraints[i].ConnectingConstraintHandle;
            if (NarrowPhase.IsContactConstraintType(simulation!.Solver.HandleToConstraint[handle.Value].TypeId))
                simulation.Solver.Remove(handle);
        }
        body.Pose = target; body.Velocity = default; body.UpdateBounds();
    }

    public void SetBodyKinematic(int bodyIndex, bool kinematic)
    {
        var slot = GetSlot(bodyIndex);
        bool desired = kinematic || slot.Definition.Mode == PhysicsBodyMode.Kinematic || slot.Definition.Mass == 0;
        if (slot.Kinematic == desired) return;
        // Official SetLocalInertia handles broadphase mobility and removes all-kinematic solver
        // constraints. Logical joints outlive those handles and are rebuilt when dynamic again.
        var affected = joints.Select((joint, index) => (joint, index))
            .Where(x => x.joint is not null && (x.joint.Definition.BodyA == bodyIndex || x.joint.Definition.BodyB == bodyIndex)).ToArray();
        foreach (var item in affected) RemoveSolverConstraints(item.joint!);
        var body = GetBody(bodyIndex);
        body.SetLocalInertia(desired ? default : slot.AuthoredInertia);
        body.Velocity = default; slot.Kinematic = desired;
        foreach (var item in affected) BuildJoint(item.index, item.joint!);
    }
}
