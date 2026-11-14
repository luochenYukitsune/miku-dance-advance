using System.Numerics;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class BulletParallelWorldTests
{
    [Fact]
    public async Task EightIndependentCoupledSoftRigidWorldsStepConcurrently()
    {
        const int count = 8;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var boundary = new Barrier(count);
        // Dedicated workers ensure all eight worlds can enter the native solver together;
        // a thread-pool queue must not turn this regression into sequential stepping.
        var workers = Enumerable.Range(0, count).Select(index => Task.Factory.StartNew(
            () => RunWorld(index, boundary, cancellation), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        var results = await Task.WhenAll(workers);
        Assert.Equal(count, results.Length);
        for (int index = 0; index < results.Length; index++)
        {
            var result = results[index];
            float x = index * 10;
            Assert.InRange(result.RigidPosition.X, x - .0001f, x + .0001f);
            Assert.True(result.RigidPosition.Y < 5);
            Assert.Equal(new Vector3(x, 3, 0), result.ClothPositions[0]);
            Assert.True(result.ClothPositions[3].Y < 2.999f);
            Assert.All(result.ClothPositions, point => Assert.True(Finite(point)));
            Assert.True(Finite(result.RigidPosition));
            // Each owner has a distinct gravity; another world's state must not leak in.
            if (index > 0) Assert.True(result.RigidPosition.Y < results[index - 1].RigidPosition.Y - 1);
        }
    }

    private static WorldResult RunWorld(int index, Barrier boundary, CancellationTokenSource cancellation)
    {
        try
        {
            float x = index * 10;
            Vector3[] targets = [new(x, 3, 0), new(x + 1, 3, 0), new(x, 3, 1), new(x + 1, 3, 1)];
            var definition = new CharacterPhysicsDefinition([], [new PhysicsBodyDefinition(
                "falling", -1, PhysicsShapeKind.Sphere, new(.1f), new(x, 6, 3), Vector3.Zero,
                1, 0, 0, 0, .3f, 0, 0, PhysicsBodyMode.Dynamic)], []);
            using var owner = new BulletCharacterPhysicsBackend(new(0, -4 - index, 0), null, coupledSoftBodies: true);
            owner.Initialize(definition, definition.BodyRestWorldTransforms.ToArray());
            using var binding = owner.CreateSoftBodyBinding([]);
            binding.Initialize([new SoftBodyDefinition("cloth", SoftBodyShape.TriMesh, [0, 1, 2, 3], targets,
                [0, 1, 2, 1, 3, 2], [0], [], new())], targets);
            for (int step = 0; step < 360; step++)
            {
                Assert.True(boundary.SignalAndWait(TimeSpan.FromSeconds(30), cancellation.Token),
                    "All eight independent worlds must reach the next simulation boundary.");
                binding.SetTargets(targets);
                owner.Step(1f / 120);
            }
            var sample = Assert.Single(binding.Sample());
            Assert.Equal(new[] { 0, 1, 2, 3 }, sample.VertexIndices);
            return new(owner.GetBodyWorldTransform(0).Translation, sample.Positions);
        }
        catch
        {
            // A managed failure must release the other workers from their rendezvous.
            cancellation.Cancel();
            throw;
        }
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private sealed record WorldResult(Vector3 RigidPosition, Vector3[] ClothPositions);
}
