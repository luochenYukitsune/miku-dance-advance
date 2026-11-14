using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using Mmd.Modern.Core;

namespace Mmd.Modern.Physics.Bepu;

public sealed class BepuPhysicsBackend : IPhysicsBackend
{
    private readonly BufferPool pool = new();
    private readonly Dictionary<PhysicsBodyId, BodyHandle> bodies = [];
    private Simulation simulation;
    private int nextId;
    private bool disposed;

    public BepuPhysicsBackend() => simulation = CreateSimulation();
    private Simulation CreateSimulation() => Simulation.Create(pool, new Contacts(), new Gravity(), new SolveDescription(8, 1));
    public PhysicsBodyId AddDynamicSphere(Vector3 position, float radius, float mass)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!float.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        if (!float.IsFinite(mass) || mass <= 0) throw new ArgumentOutOfRangeException(nameof(mass));
        if (!Finite(position)) throw new ArgumentOutOfRangeException(nameof(position));
        var sphere = new Sphere(radius);
        var handle = simulation.Bodies.Add(BodyDescription.CreateDynamic(position, sphere.ComputeInertia(mass), simulation.Shapes.Add(sphere), 0.01f));
        var id = new PhysicsBodyId(checked(++nextId));
        bodies.Add(id, handle);
        return id;
    }
    public void AddGround(float y)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!float.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        simulation.Statics.Add(new StaticDescription(new Vector3(0, y - 0.5f, 0), simulation.Shapes.Add(new Box(500, 1, 500))));
    }
    public void Step(float deltaTime)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!float.IsFinite(deltaTime) || deltaTime <= 0 || deltaTime > 1f / 15) throw new ArgumentOutOfRangeException(nameof(deltaTime));
        simulation.Timestep(deltaTime);
    }
    public Vector3 GetPosition(PhysicsBodyId body)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!bodies.TryGetValue(body, out var handle)) throw new ArgumentException("Unknown or reset body.", nameof(body));
        return simulation.Bodies.GetBodyReference(handle).Pose.Position;
    }
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        simulation.Dispose(); pool.Clear(); bodies.Clear(); simulation = CreateSimulation();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; simulation.Dispose(); pool.Clear(); bodies.Clear();
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private struct Contacts : INarrowPhaseCallbacks
    {
        public void Initialize(Simulation owner) { }
        public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
            => a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic;
        public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;
        public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterial)
            where TManifold : unmanaged, IContactManifold<TManifold>
        {
            pairMaterial = new PairMaterialProperties { FrictionCoefficient = 1, MaximumRecoveryVelocity = 2, SpringSettings = new SpringSettings(30, 1) };
            return true;
        }
        public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold) => true;
        public void Dispose() { }
    }
    private struct Gravity : IPoseIntegratorCallbacks
    {
        private Vector3Wide acceleration;
        public AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
        public bool AllowSubstepsForUnconstrainedBodies => false;
        public bool IntegrateVelocityForKinematics => false;
        public void Initialize(Simulation owner) { }
        public void PrepareForIntegration(float dt) => acceleration = Vector3Wide.Broadcast(new Vector3(0, -9.81f * dt, 0));
        public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation, BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex, Vector<float> dt, ref BodyVelocityWide velocity)
            => velocity.Linear += acceleration;
    }
}
