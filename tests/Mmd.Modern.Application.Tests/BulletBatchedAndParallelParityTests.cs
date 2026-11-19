using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class BulletBatchedAndParallelParityTests
{
    private const float Dt = (float)CharacterPhysicsSession.FixedStepSeconds;

    [Fact]
    public void BulletCapabilitiesExposeConcurrentWorldsAndBatchedAbiBit4()
    {
        using var backend = new BulletCharacterPhysicsBackend(new(0, -98, 0), groundY: 0);
        Assert.True(backend.SupportsConcurrentIndependentWorlds);
        Assert.True(backend.SupportsBatchedExecution);
        Assert.Equal(7, NativeFeatures());
    }

    [Fact]
    public void SharedSessionBatchedAndUnbatchedProduceBitIdenticalTrajectories()
    {
        var defA = BuildMultiShapeDefinition(boneCount: 5, includeJoints: true);
        var defB = BuildMultiShapeDefinition(boneCount: 4, includeJoints: true);

        using var batchedBackend = new BulletCharacterPhysicsBackend(new(0, -98, 0), groundY: 0);
        using var rawUnbatched = new BulletCharacterPhysicsBackend(new(0, -98, 0), groundY: 0);
        using var unbatchedBackend = new UnbatchedMutableJointPullProxy(rawUnbatched);

        using var batchedWorld = new SharedCharacterPhysicsSession(batchedBackend);
        using var unbatchedWorld = new SharedCharacterPhysicsSession(unbatchedBackend);

        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var placeA = Matrix4x4.Identity;
        var placeB = Matrix4x4.CreateRotationY(0.35f) * Matrix4x4.CreateTranslation(2.4f, 0, -0.8f);

        var initA = BuildAnimatedBones(defA, 0);
        var initB = BuildAnimatedBones(defB, 0);

        batchedWorld.AddActor(idA, defA, placeA, initA);
        batchedWorld.AddActor(idB, defB, placeB, initB);
        unbatchedWorld.AddActor(idA, defA, placeA, initA);
        unbatchedWorld.AddActor(idB, defB, placeB, initB);

        var disabled = new HashSet<int> { 2 };
        for (int step = 1; step <= 90; step++)
        {
            var bonesA = BuildAnimatedBones(defA, step);
            var bonesB = BuildAnimatedBones(defB, step);

            // Deliberately stretch body 3 on step 20 to trigger MMD joint pull (> maxDist + 2).
            if (step == 20)
                bonesA[0] = Matrix4x4.CreateTranslation(0, 12f, 0) * bonesA[0];

            var sampleA = new SharedPhysicsActorSample(idA, bonesA)
            {
                Frozen = step is >= 45 and <= 55,
                DisabledBoneIndices = step is >= 25 and <= 35 ? disabled : new HashSet<int>(),
                Impulses = step == 10 ? [new CharacterBodyImpulse(1, Local: true, new(1.2f, 0.5f, -0.3f), new(0, 0.2f, 0))] : []
            };
            var sampleB = new SharedPhysicsActorSample(idB, bonesB)
            {
                Impulses = step == 15 ? [new CharacterBodyImpulse(1, Local: false, new(-0.8f, 0.4f, 0.6f), Vector3.Zero)] : []
            };

            var actual = batchedWorld.Step([sampleA, sampleB]);
            var expected = unbatchedWorld.Step([sampleA, sampleB]);

            AssertExactPoseArrays(expected[idA], actual[idA], step, "ActorA");
            AssertExactPoseArrays(expected[idB], actual[idB], step, "ActorB");
        }

        // Also check standalone Reflect parity (frozen and active).
        var reflectSampleA = new SharedPhysicsActorSample(idA, BuildAnimatedBones(defA, 91)) { DisabledBoneIndices = disabled };
        var reflectSampleB = new SharedPhysicsActorSample(idB, BuildAnimatedBones(defB, 91)) { Frozen = true };
        var reflectActual = batchedWorld.Reflect([reflectSampleA, reflectSampleB]);
        var reflectExpected = unbatchedWorld.Reflect([reflectSampleA, reflectSampleB]);
        AssertExactPoseArrays(reflectExpected[idA], reflectActual[idA], 91, "ReflectA");
        AssertExactPoseArrays(reflectExpected[idB], reflectActual[idB], 91, "ReflectB");
    }

    [Fact]
    public void SingleSessionBatchedAndUnbatchedProduceBitIdenticalTrajectories()
    {
        var def = BuildMultiShapeDefinition(boneCount: 5, includeJoints: true);
        var init = BuildAnimatedBones(def, 0);

        using var batchedBackend = new BulletCharacterPhysicsBackend(new(0, -98, 0), groundY: 0);
        using var rawUnbatched = new BulletCharacterPhysicsBackend(new(0, -98, 0), groundY: 0);
        using var unbatchedProxy = new UnbatchedSingleSessionProxy(rawUnbatched);

        using var batchedSession = new CharacterPhysicsSession(def, batchedBackend, init);
        using var unbatchedSession = new CharacterPhysicsSession(def, unbatchedProxy, init);

        var disabled = new HashSet<int> { 3 };
        for (int frame = 1; frame <= 30; frame++)
        {
            var anim = BuildAnimatedBones(def, frame);
            CharacterBodyImpulse[] impulses = frame == 4
                ? [new CharacterBodyImpulse(1, Local: true, new(0.9f, 0.3f, 0.1f), new(0.1f, 0, 0)),
                   new CharacterBodyImpulse(0, Local: false, new(99, 99, 99), Vector3.Zero)]
                : [];
            var actual = batchedSession.Advance(CharacterPhysicsSession.FixedStepSeconds * 2, anim, impulses);
            var expected = unbatchedSession.Advance(CharacterPhysicsSession.FixedStepSeconds * 2, anim, impulses);
            AssertExactPoseArrays(expected, actual, frame, "SingleAdvance");

            var reflectedActual = batchedSession.ReflectPose(anim, disabled);
            var reflectedExpected = unbatchedSession.ReflectPose(anim, disabled);
            AssertExactPoseArrays(reflectedExpected, reflectedActual, frame, "SingleReflectPose");
        }
    }

    [Fact]
    public void ParallelIslandAndParallelPairDispatchAreDeterministicAcrossRuns()
    {
        static Matrix4x4[][] RunScene()
        {
            // 10 independent islands (20 dynamic/kinematic bodies clustered in tight overlapping pairs + ground + elevated)
            // Triggers both ParallelCollisionDispatcher (lanes >= 2) and ParallelIslandSolver (multiple independent islands).
            var bones = new List<PhysicsBoneDefinition>();
            var bodies = new List<PhysicsBodyDefinition>();
            var joints = new List<PhysicsJointDefinition>();
            for (int island = 0; island < 10; island++)
            {
                float x = (island % 5) * 0.45f;
                float z = (island / 5) * 0.45f;
                float baseY = island < 5 ? 0.35f : 12.0f; // bottom row contacts ground; top row hits ground early-out

                int rootBone = bones.Count;
                bones.Add(new(-1, Matrix4x4.CreateTranslation(x, baseY + 0.5f, z)));
                bones.Add(new(rootBone, Matrix4x4.CreateTranslation(x, baseY, z)));
                bones.Add(new(rootBone + 1, Matrix4x4.CreateTranslation(x + 0.15f, baseY - 0.15f, z)));

                int b0 = bodies.Count;
                bodies.Add(new($"k_{island}", rootBone, PhysicsShapeKind.Sphere, new(0.2f), new(x, baseY + 0.5f, z), Vector3.Zero, 0, 0.05f, 0.05f, 0.1f, 0.5f, 0, ushort.MaxValue, PhysicsBodyMode.Kinematic));
                var shape = (island % 3) switch
                {
                    0 => PhysicsShapeKind.Capsule,
                    1 => PhysicsShapeKind.Box,
                    _ => PhysicsShapeKind.Sphere
                };
                var size = shape == PhysicsShapeKind.Box ? new Vector3(0.22f, 0.22f, 0.22f)
                    : shape == PhysicsShapeKind.Capsule ? new Vector3(0.18f, 0.28f, 0)
                    : new Vector3(0.22f, 0, 0);
                bodies.Add(new($"d1_{island}", rootBone + 1, shape, size, new(x, baseY, z), new(0.1f, 0.2f, 0.05f), 1.0f, 0.05f, 0.05f, 0.2f, 0.5f, 0, ushort.MaxValue, PhysicsBodyMode.Dynamic));
                bodies.Add(new($"d2_{island}", rootBone + 2, PhysicsShapeKind.Capsule, new(0.18f, 0.24f, 0), new(x + 0.15f, baseY - 0.15f, z), Vector3.Zero, 1.0f, 0.05f, 0.05f, 0.2f, 0.5f, 0, ushort.MaxValue, PhysicsBodyMode.Dynamic));

                joints.Add(new($"j1_{island}", b0, b0 + 1, 0, new(x, baseY + 0.25f, z), Vector3.Zero,
                    new(-0.02f), new(0.02f), new(-0.5f), new(0.5f), new(10f), new(10f)));
                joints.Add(new($"j2_{island}", b0 + 1, b0 + 2, 0, new(x + 0.08f, baseY - 0.08f, z), Vector3.Zero,
                    new(-0.02f), new(0.02f), new(-0.4f), new(0.4f), new(8f), new(8f)));
            }

            var def = new CharacterPhysicsDefinition(bones, bodies, joints);
            var init = bones.Select(b => b.RestWorld).ToArray();
            using var backend = new BulletCharacterPhysicsBackend(new(0, -98, 0), groundY: 0);
            using var session = new CharacterPhysicsSession(def, backend, init);

            var trace = new Matrix4x4[40][];
            for (int step = 0; step < trace.Length; step++)
            {
                var anim = new Matrix4x4[init.Length];
                var shift = Matrix4x4.CreateTranslation(MathF.Sin(step * 0.12f) * 0.2f, 0, MathF.Cos(step * 0.12f) * 0.1f);
                for (int i = 0; i < anim.Length; i++)
                    anim[i] = init[i] * shift;
                trace[step] = session.Advance(CharacterPhysicsSession.FixedStepSeconds, anim);
            }
            return trace;
        }

        var run1 = RunScene();
        var run2 = RunScene();
        Assert.Equal(run1.Length, run2.Length);
        for (int step = 0; step < run1.Length; step++)
        {
            for (int bone = 0; bone < run1[step].Length; bone++)
                PhysicsMath.ValidateRigid(run1[step][bone], nameof(run1));
            AssertExactPoseArrays(run1[step], run2[step], step, "ParallelDeterminism");
        }
    }

    [Fact]
    public void ConcurrentIndependentBulletWorldsProduceBitIdenticalTrajectories()
    {
        var def = BuildMultiShapeDefinition(boneCount: 4, includeJoints: true);
        var init = BuildAnimatedBones(def, 0);

        Matrix4x4[] RunSingleWorld(bool coupledSoft)
        {
            using var backend = new BulletCharacterPhysicsBackend(new(0, -98, 0), groundY: 0, coupledSoftBodies: coupledSoft);
            using var world = new SharedCharacterPhysicsSession(backend);
            var actorId = Guid.NewGuid();
            world.AddActor(actorId, def, Matrix4x4.Identity, init);
            Matrix4x4[] last = [];
            for (int step = 1; step <= 80; step++)
            {
                var sample = new SharedPhysicsActorSample(actorId, BuildAnimatedBones(def, step))
                {
                    Impulses = step == 5 ? [new CharacterBodyImpulse(1, false, new(0.5f, 1.0f, -0.25f), new(0, 0.1f, 0))] : []
                };
                last = world.Step([sample])[actorId];
            }
            return last;
        }

        var rigidRef = RunSingleWorld(coupledSoft: false);
        var coupledRef = RunSingleWorld(coupledSoft: true);

        const int threadCount = 6;
        using var barrier = new Barrier(threadCount);
        var results = new Matrix4x4[threadCount][];
        var errors = new Exception?[threadCount];
        var threads = new Thread[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            int workerIndex = t;
            bool coupled = workerIndex >= 4;
            threads[t] = new Thread(() =>
            {
                try
                {
                    using var backend = new BulletCharacterPhysicsBackend(new(0, -98, 0), groundY: 0, coupledSoftBodies: coupled);
                    using var world = new SharedCharacterPhysicsSession(backend);
                    var actorId = Guid.NewGuid();
                    world.AddActor(actorId, def, Matrix4x4.Identity, init);
                    barrier.SignalAndWait();
                    Matrix4x4[] last = [];
                    for (int step = 1; step <= 80; step++)
                    {
                        var sample = new SharedPhysicsActorSample(actorId, BuildAnimatedBones(def, step))
                        {
                            Impulses = step == 5 ? [new CharacterBodyImpulse(1, false, new(0.5f, 1.0f, -0.25f), new(0, 0.1f, 0))] : []
                        };
                        last = world.Step([sample])[actorId];
                    }
                    results[workerIndex] = last;
                }
                catch (Exception ex) { errors[workerIndex] = ex; }
            })
            { IsBackground = true };
            threads[t].Start();
        }
        foreach (var thread in threads) thread.Join();

        for (int t = 0; t < threadCount; t++)
        {
            Assert.Null(errors[t]);
            AssertExactPoseArrays(t >= 4 ? coupledRef : rigidRef, results[t], t, $"ConcurrentThread{t}");
        }
    }

    [Fact]
    public void SharedSessionWarmStepsAvoidValidationLinqAndBatchAllocations()
    {
        var def = BuildMultiShapeDefinition(boneCount: 16, includeJoints: true);
        var init = BuildAnimatedBones(def, 0);
        using var backend = new BulletCharacterPhysicsBackend(new(0, -98, 0), groundY: 0);
        using var world = new SharedCharacterPhysicsSession(backend);

        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var samples = new SharedPhysicsActorSample[ids.Length];
        for (int a = 0; a < ids.Length; a++)
        {
            world.AddActor(ids[a], def, Matrix4x4.CreateTranslation(a * 4f, 0, 0), init);
            samples[a] = new SharedPhysicsActorSample(ids[a], init);
        }

        for (int i = 0; i < 8; i++) _ = world.Step(samples);

        long before = GC.GetAllocatedBytesForCurrentThread();
        const int measuredSteps = 16;
        for (int i = 0; i < measuredSteps; i++) _ = world.Step(samples);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Expected per-step allocations are ONLY the Dictionary<Guid, Matrix4x4[]> return container and the
        // 3 returned Matrix4x4[] bone arrays (~16 * 64 = 1024 bytes each -> ~3.4 KB/step, ~55 KB over 16 steps).
        // Any per-body/per-joint heap buffers or LINQ closures would exceed this budget easily.
        Assert.True(allocated < 96 * 1024, $"16 warmed batched shared steps allocated {allocated} bytes.");
    }

    [Fact]
    public void BatchedBackendValidatesArgumentsAndMaintainsTemporaryKinematicCache()
    {
        using var backend = new BulletCharacterPhysicsBackend(Vector3.Zero, groundY: null);
        Assert.Throws<InvalidOperationException>(() => backend.ApplyKinematicTargetsBatch([], Dt));
        Assert.Throws<InvalidOperationException>(() => backend.CorrectJointsAndGetBodyTransforms([], [], []));

        var def = new CharacterPhysicsDefinition(
            [new(-1, Matrix4x4.Identity), new(0, Matrix4x4.CreateTranslation(0, -1, 0))],
            [
                new("k", 0, PhysicsShapeKind.Sphere, new(0.2f), Vector3.Zero, Vector3.Zero, 0, 0, 0, 0, 0.5f, 0, ushort.MaxValue, PhysicsBodyMode.Kinematic),
                new("d", 1, PhysicsShapeKind.Sphere, new(0.2f), new(0, -1, 0), Vector3.Zero, 1, 0, 0, 0, 0.5f, 0, ushort.MaxValue, PhysicsBodyMode.Dynamic)
            ],
            [new("j", 0, 1, 0, new(0, -0.5f, 0), Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero)]);
        backend.Initialize(def, [Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, -1, 0)]);

        // Validation failures must not mutate temporaryKinematic state.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            backend.ApplyKinematicTargetsBatch([new(1, true, Matrix4x4.Identity), new(99, false, Matrix4x4.Identity)], Dt));
        Assert.Throws<ArgumentException>(() =>
            backend.ApplyKinematicTargetsBatch([new(1, true, Matrix4x4.CreateScale(2))], Dt));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            backend.ApplyKinematicTargetsBatch([new(0, true, Matrix4x4.Identity)], -1f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            backend.ApplyKinematicTargetsBatch([new(0, true, Matrix4x4.Identity)], float.NaN));
        Assert.Throws<ArgumentException>(() =>
            backend.SetKinematicTarget(1, Matrix4x4.Identity, Dt));

        // Duplicate body index in one batch (true then false) leaves slot dynamic.
        var targetPose = Matrix4x4.CreateTranslation(3, 2, 1);
        backend.ApplyKinematicTargetsBatch([
            new(1, true, targetPose),
            new(1, false, Matrix4x4.Identity)
        ], Dt);
        Assert.Throws<ArgumentException>(() => backend.SetKinematicTarget(1, Matrix4x4.Identity, Dt));

        // Switch body 1 to temporary kinematic, step, and verify readback.
        backend.ApplyKinematicTargetsBatch([new(0, true, Matrix4x4.Identity), new(1, true, targetPose)], Dt);
        backend.Step(Dt);
        var readback = new Matrix4x4[2];
        backend.CorrectJointsAndGetBodyTransforms([], [0, 1], readback);
        Assert.InRange(Vector3.Distance(targetPose.Translation, readback[1].Translation), 0, 1e-5f);

        // Joint pull validation checks.
        Assert.Throws<ArgumentException>(() =>
            backend.CorrectJointsAndGetBodyTransforms([], [0], new Matrix4x4[2]));
        Assert.Throws<ArgumentException>(() =>
            backend.CorrectJointsAndGetBodyTransforms([new(0, 0, 1)], [], []));
        Assert.Throws<ArgumentException>(() =>
            backend.CorrectJointsAndGetBodyTransforms([new(0, 1, -0.1f)], [], []));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            backend.CorrectJointsAndGetBodyTransforms([new(0, 5, 1f)], [], []));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            backend.CorrectJointsAndGetBodyTransforms([], [5], new Matrix4x4[1]));

        // With mmdJointPullCorrection disabled, valid pull items are ignored without pulling.
        using var noPullBackend = new BulletCharacterPhysicsBackend(Vector3.Zero, groundY: null, mmdJointPullCorrection: false);
        noPullBackend.Initialize(def, [Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, -10, 0)]);
        var noPullOut = new Matrix4x4[1];
        noPullBackend.CorrectJointsAndGetBodyTransforms([new(0, 1, 0.5f)], [1], noPullOut);
        Assert.InRange(Vector3.Distance(new(0, -10, 0), noPullOut[0].Translation), 0, 1e-5f);
        Assert.Throws<ArgumentException>(() =>
            noPullBackend.CorrectJointsAndGetBodyTransforms([new(0, 1, float.NaN)], [1], noPullOut));
    }

    private static CharacterPhysicsDefinition BuildMultiShapeDefinition(int boneCount, bool includeJoints)
    {
        var bones = new List<PhysicsBoneDefinition>(boneCount);
        for (int i = 0; i < boneCount; i++)
            bones.Add(new PhysicsBoneDefinition(i - 1, Matrix4x4.CreateTranslation(0, 3.0f - i * 0.4f, 0)));

        var bodies = new List<PhysicsBodyDefinition>
        {
            new("rootK", 0, PhysicsShapeKind.Sphere, new(0.2f), new(0, 3.0f, 0), Vector3.Zero, 0, 0.05f, 0.05f, 0.1f, 0.5f, 0, ushort.MaxValue, PhysicsBodyMode.Kinematic),
            new("dynBox", 1, PhysicsShapeKind.Box, new(0.18f, 0.22f, 0.16f), new(0, 2.6f, 0), new(0.1f, -0.2f, 0.05f), 1.2f, 0.05f, 0.05f, 0.2f, 0.5f, 1, ushort.MaxValue, PhysicsBodyMode.Dynamic),
            new("alignedCap", 2, PhysicsShapeKind.Capsule, new(0.15f, 0.3f, 0), new(0, 2.2f, 0), new(0, 0.15f, 0.1f), 0.9f, 0.05f, 0.05f, 0.15f, 0.6f, 1, ushort.MaxValue, PhysicsBodyMode.DynamicAligned),
            new("dynSphere", 3, PhysicsShapeKind.Sphere, new(0.18f), new(0, 1.8f, 0), Vector3.Zero, 0.8f, 0.05f, 0.05f, 0.1f, 0.5f, 1, ushort.MaxValue, PhysicsBodyMode.Dynamic),
            new("unboundCap", -1, PhysicsShapeKind.Capsule, new(0.14f, 0.2f, 0), new(0.3f, 1.5f, 0), Vector3.Zero, 0.5f, 0.05f, 0.05f, 0.1f, 0.5f, 2, ushort.MaxValue, PhysicsBodyMode.Dynamic)
        };

        var joints = new List<PhysicsJointDefinition>();
        if (includeJoints)
        {
            joints.Add(new("j0", 0, 1, 0, new(0, 2.8f, 0), Vector3.Zero, new(-0.02f), new(0.02f), new(-0.4f), new(0.4f), new(12f), new(8f)));
            joints.Add(new("j1", 1, 2, 0, new(0, 2.4f, 0), Vector3.Zero, new(-0.02f), new(0.02f), new(-0.5f), new(0.5f), new(10f), new(6f)));
            joints.Add(new("j2", 2, 3, 2, new(0, 2.0f, 0), Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero));
        }

        return new CharacterPhysicsDefinition(bones, bodies, joints);
    }

    private static Matrix4x4[] BuildAnimatedBones(CharacterPhysicsDefinition definition, int step)
    {
        float t = step * 0.05f;
        var motion = Matrix4x4.CreateRotationY(MathF.Sin(t) * 0.25f) * Matrix4x4.CreateTranslation(MathF.Sin(t) * 0.4f, 0, MathF.Cos(t) * 0.2f);
        var result = new Matrix4x4[definition.Bones.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = definition.Bones[i].RestWorld * motion;
        return result;
    }

    private static void AssertExactPoseArrays(Matrix4x4[] expected, Matrix4x4[] actual, int step, string label)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(expected[i] == actual[i], $"{label} step {step} bone {i} differed: expected {expected[i]}, actual {actual[i]}.");
    }

    [DllImport("mmd_bullet", EntryPoint = "mmd_bullet_features", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int NativeFeatures();

    private sealed class UnbatchedMutableJointPullProxy(BulletCharacterPhysicsBackend inner)
        : ICharacterPhysicsMutableBackend, ICharacterPhysicsJointPullBackend
    {
        public string Name => inner.Name;
        public IReadOnlyList<string> Diagnostics => inner.Diagnostics;
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> bodyWorldTransforms) => inner.Initialize(definition, bodyWorldTransforms);
        public void SetKinematicTarget(int bodyIndex, Matrix4x4 worldTransform, float stepSeconds) => inner.SetKinematicTarget(bodyIndex, worldTransform, stepSeconds);
        public Matrix4x4 GetBodyWorldTransform(int bodyIndex) => inner.GetBodyWorldTransform(bodyIndex);
        public void Step(float stepSeconds) => inner.Step(stepSeconds);
        public int AddBody(PhysicsBodyDefinition body, Matrix4x4 worldTransform) => inner.AddBody(body, worldTransform);
        public int AddJoint(PhysicsJointDefinition joint, Matrix4x4 localFrameA, Matrix4x4 localFrameB) => inner.AddJoint(joint, localFrameA, localFrameB);
        public void RemoveJoint(int jointIndex) => inner.RemoveJoint(jointIndex);
        public void RemoveBody(int bodyIndex) => inner.RemoveBody(bodyIndex);
        public void ResetBody(int bodyIndex, Matrix4x4 worldTransform) => inner.ResetBody(bodyIndex, worldTransform);
        public void SetBodyKinematic(int bodyIndex, bool kinematic) => inner.SetBodyKinematic(bodyIndex, kinematic);
        public void ApplyImpulse(int bodyIndex, Vector3 linearImpulse, Vector3 angularImpulse) => inner.ApplyImpulse(bodyIndex, linearImpulse, angularImpulse);
        public void SetGravity(Vector3 gravity) => inner.SetGravity(gravity);
        public void ValidateJointPullSupport() => inner.ValidateJointPullSupport();
        public void CorrectJointOverextension(int bodyA, int bodyB, float maximumDistance) => inner.CorrectJointOverextension(bodyA, bodyB, maximumDistance);
        public void Dispose() => inner.Dispose();
    }

    private sealed class UnbatchedSingleSessionProxy(BulletCharacterPhysicsBackend inner)
        : ICharacterPhysicsBackend, ICharacterImpulseBackend, ICharacterGravityBackend, ICharacterPhysicsJointPullBackend
    {
        public string Name => inner.Name;
        public IReadOnlyList<string> Diagnostics => inner.Diagnostics;
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> bodyWorldTransforms) => inner.Initialize(definition, bodyWorldTransforms);
        public void SetKinematicTarget(int bodyIndex, Matrix4x4 worldTransform, float stepSeconds) => inner.SetKinematicTarget(bodyIndex, worldTransform, stepSeconds);
        public Matrix4x4 GetBodyWorldTransform(int bodyIndex) => inner.GetBodyWorldTransform(bodyIndex);
        public void Step(float stepSeconds) => inner.Step(stepSeconds);
        public void ApplyImpulse(int bodyIndex, Vector3 linearImpulse, Vector3 angularImpulse) => inner.ApplyImpulse(bodyIndex, linearImpulse, angularImpulse);
        public void SetGravity(Vector3 gravity) => inner.SetGravity(gravity);
        public void ValidateJointPullSupport() => inner.ValidateJointPullSupport();
        public void CorrectJointOverextension(int bodyA, int bodyB, float maximumDistance) => inner.CorrectJointOverextension(bodyA, bodyB, maximumDistance);
        public void Dispose() => inner.Dispose();
    }
}
