using System.Numerics;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SoftClusterGroundTests
{
    private static SoftBodyDefinition Falling(SoftBodyShape shape, float y = 2, float x = 0,
        float z = 0, ushort mask = ushort.MaxValue, bool pin = false) => new("cluster plane", shape,
        [0, 1, 2], [new(x - .3f, y, z), new(x + .3f, y, z), new(x, y, z + .6f)],
        shape == SoftBodyShape.TriMesh ? [0, 1, 2] : [], pin ? [0] : [], [],
        new() { Flags = 2, ClusterCount = 1, CollisionMask = mask });

    [Theory]
    [InlineData(SoftBodyShape.TriMesh, 0f, 0f, 0f)]
    [InlineData(SoftBodyShape.TriMesh, 2f, 100000f, -100000f)]
    [InlineData(SoftBodyShape.TriMesh, -3f, -100000f, 100000f)]
    [InlineData(SoftBodyShape.Rope, 0f, 100000f, 100000f)]
    public void ClusterFallsOntoInfiniteAnalyticPlaneAtAuthoredMargin(SoftBodyShape shape,
        float ground, float x, float z)
    {
        var d = Falling(shape, ground + 2, x, z);
        using var backend = new SoftBodyBulletBackend(new(0, -9.8f, 0), ground);
        backend.Initialize([d], []);
        var targets = d.Positions.ToArray();
        backend.Reset(targets, []);
        for (int i = 0; i < 360; i++) backend.Step(1f / 120, targets, []);
        var sample = Assert.Single(backend.Sample());
        Assert.All(sample.Positions, p => Assert.InRange(MathF.Abs(p.Y - ground - d.Settings.Margin), 0, .002f));
        Assert.All(sample.Normals, n => Assert.True(float.IsFinite(n.LengthSquared())));
    }

    [Fact]
    public void ClusterGroundHonorsCollisionMaskAndNoGroundOption()
    {
        var d = Falling(SoftBodyShape.TriMesh, mask: 0);
        using var masked = new SoftBodyBulletBackend(new(0, -9.8f, 0), 0);
        using var absent = new SoftBodyBulletBackend(new(0, -9.8f, 0), null);
        masked.Initialize([d], []); absent.Initialize([d], []);
        var targets = d.Positions.ToArray();
        masked.Reset(targets, []); absent.Reset(targets, []);
        for (int i = 0; i < 120; i++)
        {
            masked.Step(1f / 120, targets, []); absent.Step(1f / 120, targets, []);
        }
        Assert.Equal(absent.Sample()[0].Positions, masked.Sample()[0].Positions);
        Assert.All(masked.Sample()[0].Positions, p => Assert.True(p.Y < -.5f));
    }

    [Fact]
    public void ClusterGroundKeepsPinsAndResetClearsContactHistory()
    {
        var d = Falling(SoftBodyShape.TriMesh, pin: true);
        using var backend = new SoftBodyBulletBackend(new(0, -9.8f, 0), 0);
        backend.Initialize([d], []);
        var targets = d.Positions.ToArray();
        backend.Reset(targets, []);
        for (int i = 0; i < 120; i++) backend.Step(1f / 120, targets, []);
        var expected = backend.Sample()[0].Positions;
        Assert.Equal(targets[0], expected[0]);
        Assert.True(expected[2].Y < targets[2].Y - .1f);
        backend.Reset(targets, []);
        Assert.Equal(targets, backend.Sample()[0].Positions);
        for (int i = 0; i < 120; i++) backend.Step(1f / 120, targets, []);
        Assert.Equal(expected, backend.Sample()[0].Positions);
    }
}
