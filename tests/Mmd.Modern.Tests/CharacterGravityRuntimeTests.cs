using System.Numerics;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bepu;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class CharacterGravityRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GravityChangePreservesMomentumAndResetKeepsLatestAcceleration(bool bullet)
    {
        using ICharacterPhysicsBackend backend = bullet ? new BulletCharacterPhysicsBackend(Vector3.Zero, null)
            : new BepuCharacterPhysicsBackend(Vector3.Zero, null);
        var definition = Definition();
        using var session = new CharacterPhysicsSession(definition, backend);
        session.Advance(CharacterPhysicsSession.FixedStepSeconds, [], [new(0, false, new(2, 0, 0), Vector3.Zero)]);
        var before = session.GetBodyWorldTransform(0).Translation;
        session.SetGravity(new(0, -120, 0));
        Assert.Equal(before, session.GetBodyWorldTransform(0).Translation);
        session.Advance(CharacterPhysicsSession.FixedStepSeconds, []);
        var after = session.GetBodyWorldTransform(0).Translation;
        Assert.InRange(after.X - before.X, .0082f, .0085f);
        Assert.InRange(after.Y, -.0085f, -.0082f);
        Assert.Equal(new Vector3(10, 0, 0), session.GetBodyWorldTransform(1).Translation);
        session.Reset([]);
        session.Advance(CharacterPhysicsSession.FixedStepSeconds, []);
        var reset = session.GetBodyWorldTransform(0).Translation;
        Assert.Equal(0, reset.X);
        Assert.InRange(reset.Y, -.0085f, -.0082f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidGravityIsAtomicAndBackendCanConfigureBeforeInitialize(bool bullet)
    {
        using ICharacterPhysicsBackend backend = bullet ? new BulletCharacterPhysicsBackend(Vector3.Zero, null)
            : new BepuCharacterPhysicsBackend(Vector3.Zero, null);
        var gravity = Assert.IsAssignableFrom<ICharacterGravityBackend>(backend);
        gravity.SetGravity(new(0, -120, 0));
        var definition = Definition();
        using var session = new CharacterPhysicsSession(definition, backend);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetGravity(new(float.NaN, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => gravity.SetGravity(new(0, float.PositiveInfinity, 0)));
        Assert.Equal(0, session.LastStepCount);
        session.Advance(CharacterPhysicsSession.FixedStepSeconds / 2, []);
        Assert.Equal(0, session.LastStepCount);
        session.Advance(CharacterPhysicsSession.FixedStepSeconds / 2, []);
        Assert.InRange(session.GetBodyWorldTransform(0).Translation.Y, -.0085f, -.0082f);
    }

    [Fact]
    public void LegacyCapabilityFailsBeforeAnySessionMutation()
    {
        var backend = new LegacyBackend();
        using var session = new CharacterPhysicsSession(Definition(), backend);
        Assert.Throws<NotSupportedException>(() => session.SetGravity(Vector3.Zero));
        Assert.Equal(1, backend.Initializations);
        Assert.Equal(0, backend.Steps);
        session.Advance(CharacterPhysicsSession.FixedStepSeconds / 2, []);
        Assert.Equal(0, backend.Steps);
    }

    [Fact]
    public void SessionReappliesLastGravityAfterBackendReinitialization()
    {
        var backend = new RecordingBackend();
        using var session = new CharacterPhysicsSession(Definition(), backend);
        session.SetGravity(Vector3.UnitZ);
        session.Reset([]);
        Assert.Equal(Vector3.UnitZ, backend.Acceleration);
        Assert.Equal(2, backend.Initializations);
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.SetGravity(Vector3.Zero));
    }

    private static CharacterPhysicsDefinition Definition() => new([], [Body(PhysicsBodyMode.Dynamic, 2),
        Body(PhysicsBodyMode.Kinematic, 2) with { Position = new(10, 0, 0) }], []);
    private static PhysicsBodyDefinition Body(PhysicsBodyMode mode, float mass) => new("test", -1, PhysicsShapeKind.Sphere,
        new(.5f), Vector3.Zero, Vector3.Zero, mass, 0, 0, 0, 0, 0, 0, mode);

    private class LegacyBackend : ICharacterPhysicsBackend
    {
        private Matrix4x4[] worlds = [];
        public int Initializations, Steps;
        public string Name => "legacy";
        public IReadOnlyList<string> Diagnostics => [];
        public virtual void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms)
        { worlds = transforms.ToArray(); Initializations++; }
        public void SetKinematicTarget(int index, Matrix4x4 transform, float seconds) => worlds[index] = transform;
        public Matrix4x4 GetBodyWorldTransform(int index) => worlds[index];
        public void Step(float seconds) => Steps++;
        public void Dispose() { }
    }

    private sealed class RecordingBackend : LegacyBackend, ICharacterGravityBackend
    {
        public Vector3 Acceleration;
        public override void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms)
        { base.Initialize(definition, transforms); Acceleration = Vector3.Zero; }
        public void SetGravity(Vector3 gravity) => Acceleration = gravity;
    }
}
