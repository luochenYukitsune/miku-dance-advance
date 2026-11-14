using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Physics.Bepu;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class PhysicsTests
{
    [Fact]
    public void SphereFallsAndRestsOnActualContactGround()
    {
        using var world = new BepuPhysicsBackend();
        world.AddGround(0);
        var body = world.AddDynamicSphere(new Vector3(0, 5, 0), 0.5f, 1);
        for (int i = 0; i < 30; i++) world.Step(1f / 120);
        Assert.InRange(world.GetPosition(body).Y, 4.5f, 4.9f);
        for (int i = 0; i < 570; i++) world.Step(1f / 120);
        Assert.InRange(world.GetPosition(body).Y, 0.47f, 0.53f);
    }

    [Fact]
    public void ResetInvalidatesHandlesAndAllowsFreshSimulation()
    {
        using var world = new BepuPhysicsBackend();
        var old = world.AddDynamicSphere(Vector3.UnitY, .5f, 1);
        world.Reset();
        Assert.Throws<ArgumentException>(() => world.GetPosition(old));
        var fresh = world.AddDynamicSphere(new Vector3(0, 3, 0), .5f, 1);
        Assert.NotEqual(old, fresh);
        Assert.Equal(3, world.GetPosition(fresh).Y);
    }

    [Fact]
    public void DisposalIsIdempotentAndRejectsFurtherAccess()
    {
        var world = new BepuPhysicsBackend();
        world.Dispose(); world.Dispose();
        Assert.Throws<ObjectDisposedException>(() => world.Step(1f / 60));
        Assert.Throws<ObjectDisposedException>(() => world.Reset());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    public void InvalidTimeStepIsRejected(float time)
    {
        using var world = new BepuPhysicsBackend();
        Assert.Throws<ArgumentOutOfRangeException>(() => world.Step(time));
    }
}
