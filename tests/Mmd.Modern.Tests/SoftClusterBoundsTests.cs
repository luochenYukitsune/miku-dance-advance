using System.Numerics;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class SoftClusterBoundsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedRepeatedFaceDefinitionFailsSafelyAtNativeClusterLimitAndOwnerCanBeReused(bool coupled)
    {
        Vector3[] positions = [new(0, 1, 0), new(1, 1, 0), new(0, 1, 1)];
        var oversized = new SoftBodyDefinition("oversized clusters", SoftBodyShape.TriMesh, [0, 1, 2], positions,
            Enumerable.Range(0, 65_536).SelectMany(_ => new[] { 0, 1, 2 }), [], [],
            new() { Flags = 2, ClusterCount = 0, CollisionMask = 0 });
        var valid = new SoftBodyDefinition("valid clusters", SoftBodyShape.TriMesh, [0, 1, 2], positions,
            [0, 1, 2], [], [], new() { Flags = 2, ClusterCount = 1, CollisionMask = 0 });
        Assert.Equal(65_536 * 3, oversized.TriangleIndices.Count);
        if (coupled)
        {
            using var owner = new BulletCharacterPhysicsBackend(Vector3.Zero, null, coupledSoftBodies: true);
            owner.Initialize(new([], [], []), []);
            using var binding = owner.CreateSoftBodyBinding([]);
            var error = Assert.Throws<InvalidOperationException>(() => binding.Initialize([oversized], positions));
            Assert.Contains("Cluster connectivity exceeds int32 storage", error.Message);
            binding.Initialize([valid], positions);
            Assert.Equal(positions, Assert.Single(binding.Sample()).Positions);
            owner.Step(1f / 120);
            Assert.All(Assert.Single(binding.Sample()).Positions, point => Assert.True(float.IsFinite(point.X + point.Y + point.Z)));
        }
        else
        {
            using var backend = new SoftBodyBulletBackend(Vector3.Zero, null);
            backend.Initialize([oversized], []);
            var error = Assert.Throws<InvalidOperationException>(() => backend.Reset(positions, []));
            Assert.Contains("Cluster connectivity exceeds int32 storage", error.Message);
            backend.Initialize([valid], []);
            backend.Reset(positions, []);
            Assert.Equal(positions, Assert.Single(backend.Sample()).Positions);
            backend.Step(1f / 120, positions, []);
            Assert.All(Assert.Single(backend.Sample()).Positions, point => Assert.True(float.IsFinite(point.X + point.Y + point.Z)));
        }
    }
}
