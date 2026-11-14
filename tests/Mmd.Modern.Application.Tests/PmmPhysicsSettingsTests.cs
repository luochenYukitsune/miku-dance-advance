using System.Numerics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmPhysicsSettingsTests
{
    [Fact]
    public void DefaultAccelerationUsesOriginalWorldUnits()
    {
        Assert.Equal(new Vector3(0, -98, 0), PmmPhysicsSettings.ToGravity(9.8f, -Vector3.UnitY));
    }

    [Fact]
    public void DirectionMagnitudeDoesNotChangeAcceleration()
    {
        Assert.Equal(new Vector3(6, -8, 0), PmmPhysicsSettings.ToGravity(1, new(3, -4, 0)));
        Assert.Equal(PmmPhysicsSettings.ToGravity(9.8f, new(3, -4, 0)),
            PmmPhysicsSettings.ToGravity(9.8f, new(30, -40, 0)));
    }

    [Fact]
    public void NegativeAccelerationReversesTheNormalizedDirection()
    {
        Assert.Equal(new Vector3(-6, 8, 0), PmmPhysicsSettings.ToGravity(-1, new(3, -4, 0)));
    }

    [Fact]
    public void ZeroAccelerationOrDirectionProducesZeroGravity()
    {
        Assert.Equal(Vector3.Zero, PmmPhysicsSettings.ToGravity(0, new(float.MaxValue, 1, -2)));
        Assert.Equal(Vector3.Zero, PmmPhysicsSettings.ToGravity(9.8f, Vector3.Zero));
        Assert.Equal(Vector3.Zero, PmmPhysicsSettings.ToGravity(float.MaxValue, Vector3.Zero));
    }

    [Fact]
    public void FiniteDirectionIsNormalizedWithoutOverflowOrUnderflow()
    {
        Assert.Equal(new Vector3(0, -98, 0), PmmPhysicsSettings.ToGravity(9.8f, new(0, -float.MaxValue, 0)));
        Assert.Equal(new Vector3(0, -98, 0), PmmPhysicsSettings.ToGravity(9.8f, new(0, -float.Epsilon, 0)));
        var gravity = PmmPhysicsSettings.ToGravity(9.8f, new(float.MaxValue, -float.MaxValue, float.MaxValue));
        Assert.True(float.IsFinite(gravity.X) && float.IsFinite(gravity.Y) && float.IsFinite(gravity.Z));
        Assert.InRange(gravity.Length(), 97.9999f, 98.0001f);
        Assert.Equal(gravity.X, -gravity.Y);
        Assert.Equal(gravity.X, gravity.Z);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonfiniteAccelerationIsRejectedEvenForZeroDirection(float acceleration)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PmmPhysicsSettings.ToGravity(acceleration, Vector3.Zero));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonfiniteDirectionIsRejectedEvenForZeroAcceleration(float component)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PmmPhysicsSettings.ToGravity(0, new(component, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => PmmPhysicsSettings.ToGravity(0, new(0, component, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => PmmPhysicsSettings.ToGravity(0, new(0, 0, component)));
    }

    [Theory]
    [InlineData(float.MaxValue)]
    [InlineData(float.MinValue)]
    public void UnrepresentableWorldGravityIsRejected(float acceleration)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PmmPhysicsSettings.ToGravity(acceleration, Vector3.UnitX));
    }
}
