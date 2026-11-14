using System.Numerics;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class SharedPhysicsFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedActorRemovalFaultsWorldAndWholeResetRebuildsDisposedMember(bool failJointRemoval)
    {
        var backend = new FaultInjectingBackend();
        using var world = new SharedCharacterPhysicsSession(backend);
        var actor = Guid.NewGuid(); var other = Guid.NewGuid();
        world.AddActor(actor, Definition(withJoint: true), Matrix4x4.Identity, [Matrix4x4.Identity]);
        world.AddActor(other, Definition(), Matrix4x4.CreateTranslation(10, 0, 0), [Matrix4x4.Identity]);
        world.Step(Samples(actor, other));
        int initializations = backend.InitializationCount;
        backend.FailNextRemoveJoint = failJointRemoval;
        backend.FailNextRemoveBody = !failJointRemoval;

        Assert.Throws<IOException>(() => world.RemoveActor(actor));
        Assert.True(world.IsFaulted);
        Assert.Equal(2, world.ActorIds.Count);
        int steps = backend.StepCalls;
        Assert.Throws<InvalidOperationException>(() => world.Step(Samples(actor, other)));
        Assert.Equal(steps, backend.StepCalls);

        world.ResetAll(Samples(actor, other));

        Assert.False(world.IsFaulted);
        Assert.Equal(initializations + 1, backend.InitializationCount);
        Assert.Equal(0, world.StepCount);
        Assert.Equal(3, backend.LiveBodyCount);
        Assert.Equal(1, backend.LiveJointCount);
        Assert.Equal(Matrix4x4.CreateTranslation(10, 0, 0), world.GetBodyWorldTransform(other, 0));
        Assert.Equal(2, world.Step(Samples(actor, other)).Count);
        world.RemoveActor(actor);
        Assert.Single(world.ActorIds);
        Assert.Equal(1, backend.LiveBodyCount);
        Assert.Equal(0, backend.LiveJointCount);
    }

    [Fact]
    public void FailedAddRollbackCannotLeaveUnownedBodiesInAHealthyWorld()
    {
        var backend = new FaultInjectingBackend();
        using var world = new SharedCharacterPhysicsSession(backend);
        var retained = Guid.NewGuid();
        world.AddActor(retained, Definition(), Matrix4x4.Identity, [Matrix4x4.Identity]);
        backend.FailNextAddJoint = true;
        backend.FailNextRemoveBody = true;

        Assert.Throws<IOException>(() => world.AddActor(Guid.NewGuid(), Definition(withJoint: true),
            Matrix4x4.CreateTranslation(20, 0, 0), [Matrix4x4.Identity]));

        Assert.True(world.IsFaulted);
        Assert.Single(world.ActorIds);
        Assert.True(backend.LiveBodyCount > 1); // Deliberately interrupted cleanup left orphan solver bodies.
        Assert.Throws<InvalidOperationException>(() => world.Step([new(retained, [Matrix4x4.Identity])]));
        Assert.Equal(0, backend.StepCalls);

        world.ResetAll([new(retained, [Matrix4x4.Identity])]);

        Assert.False(world.IsFaulted);
        Assert.Equal(1, backend.LiveBodyCount);
        Assert.Equal(0, backend.LiveJointCount);
        Assert.Single(world.Step([new(retained, [Matrix4x4.Identity])]));
        Assert.Equal(1, backend.StepCalls);
    }

    private static SharedPhysicsActorSample[] Samples(Guid actor, Guid other) =>
        [new(actor, [Matrix4x4.Identity]), new(other, [Matrix4x4.Identity])];

    private static CharacterPhysicsDefinition Definition(bool withJoint = false)
    {
        var body = new PhysicsBodyDefinition("body", 0, PhysicsShapeKind.Sphere, new(.5f), Vector3.Zero,
            Vector3.Zero, 1, 0, 0, 0, 0, 0, ushort.MaxValue, PhysicsBodyMode.Dynamic);
        return new([new(-1, Matrix4x4.Identity)], withJoint ? [body, body] : [body],
            withJoint ? [new("joint", 0, 1, 0, Vector3.Zero, Vector3.Zero, Vector3.Zero,
                Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero)] : []);
    }

    // Faults occur at a deterministic backend boundary; this fake is not a physics solver/oracle.
    private sealed class FaultInjectingBackend : ICharacterPhysicsMutableBackend
    {
        private readonly Dictionary<int, Matrix4x4> bodies = [];
        private readonly Dictionary<int, (int A, int B)> joints = [];
        private int nextBody, nextJoint;
        public string Name => "fault-injecting mutable backend";
        public IReadOnlyList<string> Diagnostics => [];
        public bool FailNextRemoveBody { get; set; }
        public bool FailNextRemoveJoint { get; set; }
        public bool FailNextAddJoint { get; set; }
        public int InitializationCount { get; private set; }
        public int StepCalls { get; private set; }
        public int LiveBodyCount => bodies.Count;
        public int LiveJointCount => joints.Count;

        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms)
        {
            bodies.Clear(); joints.Clear(); nextBody = nextJoint = 0; InitializationCount++;
            for (int i = 0; i < definition.Bodies.Count; i++) AddBody(definition.Bodies[i], transforms[i]);
            for (int i = 0; i < definition.Joints.Count; i++)
                AddJoint(definition.Joints[i], definition.JointLocalFramesA[i], definition.JointLocalFramesB[i]);
        }
        public int AddBody(PhysicsBodyDefinition body, Matrix4x4 transform)
        { int index = nextBody++; bodies.Add(index, transform); return index; }
        public int AddJoint(PhysicsJointDefinition joint, Matrix4x4 frameA, Matrix4x4 frameB)
        {
            if (FailNextAddJoint) { FailNextAddJoint = false; throw new IOException("Injected joint allocation failure."); }
            int index = nextJoint++; joints.Add(index, (joint.BodyA, joint.BodyB)); return index;
        }
        public void RemoveJoint(int index)
        {
            if (FailNextRemoveJoint) { FailNextRemoveJoint = false; throw new IOException("Injected joint removal failure."); }
            if (!joints.Remove(index)) throw new ArgumentOutOfRangeException(nameof(index));
        }
        public void RemoveBody(int index)
        {
            if (FailNextRemoveBody) { FailNextRemoveBody = false; throw new IOException("Injected body removal failure."); }
            if (joints.Values.Any(joint => joint.A == index || joint.B == index))
                throw new InvalidOperationException("Remove referencing joints first.");
            if (!bodies.Remove(index)) throw new ArgumentOutOfRangeException(nameof(index));
        }
        public void SetKinematicTarget(int index, Matrix4x4 transform, float seconds) => ResetBody(index, transform);
        public Matrix4x4 GetBodyWorldTransform(int index) => bodies[index];
        public void ResetBody(int index, Matrix4x4 transform)
        { _ = bodies[index]; bodies[index] = transform; }
        public void SetBodyKinematic(int index, bool kinematic) => _ = bodies[index];
        public void ApplyImpulse(int index, Vector3 linear, Vector3 angular) => _ = bodies[index];
        public void SetGravity(Vector3 gravity) { }
        public void Step(float seconds) => StepCalls++;
        public void Dispose() { bodies.Clear(); joints.Clear(); }
    }
}
