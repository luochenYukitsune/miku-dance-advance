using System.Numerics;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SoftNearAnchorTests
{
    // No constraints/contact/gravity isolates the documented post-step velocity addition.
    // Real production constraints remain covered by the existing normal-anchor tests.
    private static SoftBodyDefinition Rope(bool near, bool pin = false) => new("near rope", SoftBodyShape.Rope,
        [4, 8], [new(2, 3, 0), new(3, 3, 0)], [], pin ? [4] : [], [new(0, 4, near)],
        new() { CollisionMask = 0, Iterations = [0, 0, 0, 0] });
    private static PhysicsBodyDefinition Collider(PhysicsBodyMode mode = PhysicsBodyMode.Kinematic) =>
        new("proxy", -1, PhysicsShapeKind.Sphere, new(.1f), new(0, 3, 0), Vector3.Zero,
            1, 0, 0, 0, .5f, 0, 0, mode);
    private static Vector3[] Targets()
    {
        var result = new Vector3[9]; result[4] = new(2, 3, 0); result[8] = new(3, 3, 0); return result;
    }
    private static readonly Matrix4x4[] Rest = [Matrix4x4.CreateTranslation(0, 3, 0)];

    [Fact]
    public void DefinitionPreservesNearFlagWithoutChoosingBackendCapability()
        => Assert.True(Assert.Single(Rope(true).Anchors).NearMode);

    [Fact]
    public void KinematicNearAddsVelocityAfterStepAndUsesSparseNodeMapping()
    {
        var targets = Targets();
        using var near = new SoftBodyBulletBackend(Vector3.Zero, null);
        using var ordinary = new SoftBodyBulletBackend(Vector3.Zero, null);
        near.Initialize([Rope(true)], [Collider()]); ordinary.Initialize([Rope(false)], [Collider()]);
        near.Reset(targets, Rest); ordinary.Reset(targets, Rest);
        Assert.Equal(ordinary.Sample()[0].Positions, near.Sample()[0].Positions);
        near.Step(1f / 120, targets, Rest); ordinary.Step(1f / 120, targets, Rest);
        Assert.Equal(ordinary.Sample()[0].Positions, near.Sample()[0].Positions);
        // Sampling does not apply Near a second time.
        Assert.Equal(near.Sample()[0].Positions, near.Sample()[0].Positions);
        near.Step(1f / 120, targets, Rest); ordinary.Step(1f / 120, targets, Rest);
        var actual = near.Sample()[0]; var baseline = ordinary.Sample()[0];
        Assert.Equal(new[] { 4, 8 }, actual.VertexIndices);
        Assert.InRange(MathF.Abs(actual.Positions[0].X - (2 - 2f / 120)), 0, 1e-6f);
        Assert.True(actual.Positions[0].X < baseline.Positions[0].X - .01f);
        Assert.Equal(baseline.Positions[1], actual.Positions[1]);
    }

    [Fact]
    public void ResetRecreatesNearPairsAndClearsPostStepVelocity()
    {
        var targets = Targets();
        using var backend = new SoftBodyBulletBackend(Vector3.Zero, null);
        backend.Initialize([Rope(true)], [Collider()]); backend.Reset(targets, Rest);
        for (int i = 0; i < 6; i++) backend.Step(1f / 120, targets, Rest);
        var expected = backend.Sample()[0].Positions;
        backend.Reset(targets, Rest);
        Assert.Equal(new[] { targets[4], targets[8] }, backend.Sample()[0].Positions);
        for (int i = 0; i < 6; i++) backend.Step(1f / 120, targets, Rest);
        Assert.Equal(expected, backend.Sample()[0].Positions);
    }

    [Theory]
    [InlineData(PhysicsBodyMode.Dynamic)]
    [InlineData(PhysicsBodyMode.DynamicAligned)]
    public void PhysicalNearRejectsBeforeReplacingWorkingWorld(PhysicsBodyMode mode)
    {
        var targets = Targets();
        using var backend = new SoftBodyBulletBackend(Vector3.Zero, null);
        backend.Initialize([Rope(false)], [Collider()]); backend.Reset(targets, Rest);
        var original = backend.Sample()[0].Positions;
        var error = Assert.Throws<NotSupportedException>(() => backend.Initialize([Rope(true)], [Collider(mode)]));
        Assert.Contains("reverse", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, backend.Sample()[0].Positions);
        backend.Step(1f / 120, targets, Rest);
        Assert.Equal(original, backend.Sample()[0].Positions);
    }

    [Fact]
    public void PinnedNearNodeKeepsAuthoredTargetAcrossSteps()
    {
        var targets = Targets();
        using var backend = new SoftBodyBulletBackend(Vector3.Zero, null);
        backend.Initialize([Rope(true, true)], [Collider()]); backend.Reset(targets, Rest);
        for (int i = 0; i < 5; i++) backend.Step(1f / 120, targets, Rest);
        Assert.Equal(targets[4], backend.Sample()[0].Positions[0]);
        targets[4] += Vector3.UnitY;
        backend.Step(1f / 120, targets, Rest);
        Assert.Equal(targets[4], backend.Sample()[0].Positions[0]);
    }
}
