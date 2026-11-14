using System.Numerics;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bepu;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class CharacterImpulseExecutionTests
{
    [Fact]
    public void FixedStepsApplyOnceEachAndLocalUsesCurrentStepOrientation()
    {
        var backend = new RecordingBackend();
        using var session = new CharacterPhysicsSession(Definition(), backend);
        CharacterBodyImpulse[] commands = [new(0, true, Vector3.UnitX, Vector3.UnitY), new(0, false, Vector3.UnitX, Vector3.UnitY)];
        session.Advance(2 * CharacterPhysicsSession.FixedStepSeconds, [], commands);
        Assert.Equal(2, backend.Steps);
        Assert.Equal(4, backend.Impulses.Count);
        Near(Vector3.UnitX, backend.Impulses[0].Linear);
        Near(Vector3.UnitY, backend.Impulses[2].Linear);
        Near(-Vector3.UnitX, backend.Impulses[2].Angular);
        Near(Vector3.UnitX, backend.Impulses[3].Linear);
    }

    [Fact]
    public void PausedFractionalSeekAndInactiveBodiesDoNotConsumeImpulses()
    {
        var backend = new RecordingBackend();
        using var session = new CharacterPhysicsSession(Definition(), backend);
        CharacterBodyImpulse[] commands = [new(0, false, Vector3.UnitX, Vector3.Zero)];
        session.Advance(CharacterPhysicsSession.FixedStepSeconds / 2, [], commands);
        session.Advance(0, [], commands);
        Assert.Empty(backend.Impulses);
        session.Reset([]);
        session.Advance(CharacterPhysicsSession.FixedStepSeconds / 2, [], commands);
        Assert.Empty(backend.Impulses);
        session.Advance(CharacterPhysicsSession.FixedStepSeconds / 2, [], commands);
        Assert.Single(backend.Impulses);
        session.Advance(CharacterPhysicsSession.FixedStepSeconds, [], [new(1, true, Vector3.One, Vector3.One), new(2, false, Vector3.One, Vector3.One)]);
        Assert.Single(backend.Impulses);
    }

    [Fact]
    public void AllCommandsValidateBeforeAnyClockOrSolverMutation()
    {
        var backend = new RecordingBackend();
        using var session = new CharacterPhysicsSession(Definition(), backend);
        Assert.Throws<ArgumentException>(() => session.Advance(CharacterPhysicsSession.FixedStepSeconds / 2, [],
            [new(0, false, Vector3.One, Vector3.Zero), new(99, false, Vector3.Zero, Vector3.Zero)]));
        Assert.Throws<ArgumentException>(() => session.Advance(1, [], [new(0, false, new(float.NaN), Vector3.Zero)]));
        Assert.Equal(0, backend.Steps); Assert.Empty(backend.Impulses);
        session.Advance(CharacterPhysicsSession.FixedStepSeconds / 2, [], [new(0, false, Vector3.One, Vector3.Zero)]);
        Assert.Equal(0, backend.Steps);
    }

    [Fact]
    public void OptionalCapabilityRejectsActiveCommandsWithoutAdvancingLegacyBackends()
    {
        var backend = new LegacyBackend();
        using var session = new CharacterPhysicsSession(Definition(), backend);
        Assert.Throws<NotSupportedException>(() => session.Advance(CharacterPhysicsSession.FixedStepSeconds / 2, [],
            [new(0, false, Vector3.One, Vector3.Zero)]));
        session.Advance(CharacterPhysicsSession.FixedStepSeconds / 2, [], []);
        Assert.Equal(0, backend.Inner.Steps);
        session.Advance(CharacterPhysicsSession.FixedStepSeconds / 2, [], [new(1, false, Vector3.One, Vector3.One)]);
        Assert.Equal(1, backend.Inner.Steps); // ignored kinematic commands need no capability
        Assert.Empty(backend.Inner.Impulses);
    }

    [Fact]
    public void DiscardedCatchUpTimeDoesNotApplyUnexecutedImpulses()
    {
        var backend = new RecordingBackend();
        using var session = new CharacterPhysicsSession(Definition(), backend);
        session.Advance(1, [], [new(0, false, Vector3.One, Vector3.Zero)]);
        Assert.Equal(CharacterPhysicsSession.MaximumSubsteps, backend.Impulses.Count);
        session.Advance(0, [], [new(0, false, Vector3.One, Vector3.Zero)]);
        Assert.Equal(CharacterPhysicsSession.MaximumSubsteps, backend.Impulses.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealBackendUsesMassAndInertiaAndResetRemovesMomentum(bool bullet)
    {
        using ICharacterPhysicsBackend backend = bullet ? new BulletCharacterPhysicsBackend(Vector3.Zero, null)
            : new BepuCharacterPhysicsBackend(Vector3.Zero, null);
        var definition = Definition();
        backend.Initialize(definition, definition.BodyRestWorldTransforms.ToArray());
        var impulses = Assert.IsAssignableFrom<ICharacterImpulseBackend>(backend);
        impulses.ApplyImpulse(0, new(2, 0, 0), new(0, 0, .2f));
        backend.Step(1f / 120);
        var pose = backend.GetBodyWorldTransform(0);
        Assert.InRange(pose.Translation.X, .0082f, .0085f); // mass 2 -> linear velocity 1
        Assert.True(Math.Abs(pose.M12) > .001f);
        backend.Initialize(definition, definition.BodyRestWorldTransforms.ToArray());
        backend.Step(1f / 120);
        Assert.Equal(Matrix4x4.Identity, backend.GetBodyWorldTransform(0));
        impulses.ApplyImpulse(1, Vector3.One, Vector3.One); // kinematic is explicitly ignored
        impulses.ApplyImpulse(2, Vector3.One, Vector3.One); // zero mass is explicitly ignored
        Assert.Throws<ArgumentOutOfRangeException>(() => impulses.ApplyImpulse(50, Vector3.Zero, Vector3.Zero));
        Assert.Throws<ArgumentException>(() => impulses.ApplyImpulse(0, new(float.PositiveInfinity), Vector3.Zero));
    }

    private static CharacterPhysicsDefinition Definition() => new([], [Body(PhysicsBodyMode.Dynamic, 2),
        Body(PhysicsBodyMode.Kinematic, 2) with { Position = new(10, 0, 0) },
        Body(PhysicsBodyMode.Dynamic, 0) with { Position = new(20, 0, 0) }], []);
    private static PhysicsBodyDefinition Body(PhysicsBodyMode mode, float mass) => new("test", -1, PhysicsShapeKind.Sphere,
        new(.5f), Vector3.Zero, Vector3.Zero, mass, 0, 0, 0, 0, 0, 0, mode);
    private static void Near(Vector3 expected, Vector3 actual) => Assert.True(Vector3.Distance(expected, actual) < 1e-5f, $"{expected} != {actual}");

    private sealed class RecordingBackend : ICharacterPhysicsBackend, ICharacterImpulseBackend
    {
        private Matrix4x4[] worlds = [];
        public int Steps;
        public List<(Vector3 Linear, Vector3 Angular)> Impulses { get; } = [];
        public string Name => "recording";
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms)
        { worlds = transforms.ToArray(); Steps = 0; Impulses.Clear(); }
        public void SetKinematicTarget(int index, Matrix4x4 transform, float seconds) => worlds[index] = transform;
        public Matrix4x4 GetBodyWorldTransform(int index) => worlds[index];
        public void ApplyImpulse(int index, Vector3 linear, Vector3 angular) => Impulses.Add((linear, angular));
        public void Step(float seconds) { Steps++; worlds[0] = Matrix4x4.CreateRotationZ(Steps * MathF.PI / 2); }
        public void Dispose() { }
    }

    private sealed class LegacyBackend : ICharacterPhysicsBackend
    {
        public RecordingBackend Inner { get; } = new();
        public string Name => "legacy without impulse capability";
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms) => Inner.Initialize(definition, transforms);
        public void SetKinematicTarget(int index, Matrix4x4 transform, float seconds) => Inner.SetKinematicTarget(index, transform, seconds);
        public Matrix4x4 GetBodyWorldTransform(int index) => Inner.GetBodyWorldTransform(index);
        public void Step(float seconds) => Inner.Step(seconds);
        public void Dispose() => Inner.Dispose();
    }
}
