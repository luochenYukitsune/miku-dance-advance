using System.Numerics;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class SharedPhysicsPreparationBufferTests
{
    [Fact]
    public void ActorMembershipChecksTrackChangesWithoutAllocatingSnapshots()
    {
        using var world = new SharedCharacterPhysicsSession(new RecordingBackend());
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        AddPair(world, first, second);
        var snapshot = world.ActorIds;
        world.RemoveActor(second);
        Assert.Contains(second, snapshot);
        Assert.True(world.ContainsActor(first));
        Assert.False(world.ContainsActor(second));

        long start = GC.GetAllocatedBytesForCurrentThread();
        int found = 0;
        for (int i = 0; i < 2048; i++)
            if (world.ContainsActor((i & 1) == 0 ? first : second)) found++;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(1024, found);
        Assert.Equal(0L, allocated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmSharedStepsDoNotAllocateBodyOrImpulseSizedPreparationBuffers(bool withImpulses)
    {
        const int bodyCount = 2048, repetitions = 16;
        var backend = new RecordingBackend { RecordCalls = false };
        using var world = new SharedCharacterPhysicsSession(backend);
        var actor = Guid.NewGuid();
        var definition = new CharacterPhysicsDefinition([], Enumerable.Repeat(Body(PhysicsBodyMode.Dynamic, -1), bodyCount), []);
        world.AddActor(actor, definition, Matrix4x4.Identity, []);
        SharedPhysicsActorSample[] samples = [new(actor, [])
        {
            Impulses = withImpulses
                ? Enumerable.Range(0, bodyCount).Select(index => new CharacterBodyImpulse(index, false, Vector3.UnitX, Vector3.UnitY)).ToArray()
                : []
        }];
        for (int step = 0; step < repetitions; step++) world.Step(samples);

        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int step = 0; step < repetitions; step++) world.Step(samples);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

        Assert.True(allocated < 64 * 1024, $"{repetitions} warmed shared steps allocated {allocated} bytes for {bodyCount} bodies (impulses: {withImpulses}).");
        Assert.Equal(repetitions * 2, world.StepCount);
        Assert.Equal(withImpulses ? bodyCount * repetitions * 2 : 0, backend.ImpulseCalls);
        Assert.False(world.IsFaulted);
    }

    [Fact]
    public void LatePreflightFailureDoesNotMutateOrFaultAndLaterStepsCannotReplayPreparedCommands()
    {
        var backend = new RecordingBackend();
        using var world = new SharedCharacterPhysicsSession(backend);
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        AddPair(world, first, second);
        backend.Calls.Clear();
        backend.OverrideReadSlot = 2;
        backend.OverrideRead = Matrix4x4.CreateScale(float.MaxValue, 1, 1);
        SharedPhysicsActorSample[] rejected = [FirstSample(first, 3, 7),
            new(second, [Matrix4x4.Identity]) { Impulses = [new(0, true, new(2, 0, 0), Vector3.Zero)] }];

        var error = Assert.Throws<ArgumentException>(() => world.Step(rejected));

        Assert.Equal("samples", error.ParamName);
        Assert.Contains("Impulse conversion overflowed", error.Message);
        Assert.Empty(backend.Calls);
        Assert.Equal(0, world.StepCount);
        Assert.False(world.IsFaulted);
        backend.OverrideReadSlot = null;

        var firstResult = world.Step([FirstSample(first, 7, 11), new(second, [Matrix4x4.Identity])]);
        Assert.Equal(new[] { K(0, true), T(0, 7), K(1, false), K(2, false), I(1, 11), S() }, backend.Calls);
        Assert.Equal(Matrix4x4.CreateTranslation(7, 0, 0), firstResult[first][0]);
        backend.Calls.Clear();

        var secondResult = world.Step([FirstSample(first, 9), new(second, [Matrix4x4.Identity]) { Frozen = true }]);

        Assert.Equal(new[] { K(0, true), T(0, 9), K(1, false), K(2, true), T(2, 0), S() }, backend.Calls);
        Assert.Equal(Matrix4x4.CreateTranslation(9, 0, 0), secondResult[first][0]);
        Assert.Equal(Matrix4x4.CreateTranslation(7, 0, 0), firstResult[first][0]);
        Assert.Equal(Matrix4x4.CreateTranslation(3, 0, 0), rejected[0].BeforePhysicsBoneWorlds[0]);
        Assert.Equal(2, world.StepCount);
        Assert.False(world.IsFaulted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NestedBackendCallbackUsesIndependentPreparationDuringPreflightAndConsumption(bool duringPreflight)
    {
        var backend = new RecordingBackend();
        using var world = new SharedCharacterPhysicsSession(backend);
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        AddPair(world, first, second);
        backend.Calls.Clear();
        IReadOnlyDictionary<Guid, Matrix4x4[]>? nestedResult = null;
        Action nested = () => nestedResult = world.Step([FirstSample(first, 8, 13), new(second, [Matrix4x4.Identity])]);
        if (duringPreflight) backend.BeforeRead = nested;
        else backend.BeforeKinematic = nested;

        var result = world.Step([FirstSample(first, 3, 2),
            new(second, [Matrix4x4.Identity]) { Impulses = [new(0, true, new(5, 0, 0), Vector3.Zero)] }]);

        Assert.Equal(new[]
        {
            K(0, true), T(0, 8), K(1, false), K(2, false), I(1, 13), S(),
            K(0, true), T(0, 3), K(1, false), K(2, false), I(1, 2), I(2, 5), S()
        }, backend.Calls);
        Assert.NotNull(nestedResult);
        Assert.Equal(Matrix4x4.CreateTranslation(8, 0, 0), nestedResult[first][0]);
        Assert.Equal(Matrix4x4.CreateTranslation(3, 0, 0), result[first][0]);
        Assert.Equal(2, world.StepCount);
        Assert.False(world.IsFaulted);

        backend.Calls.Clear();
        world.Step([FirstSample(first, 10), new(second, [Matrix4x4.Identity])]);
        Assert.Equal(new[] { K(0, true), T(0, 10), K(1, false), K(2, false), S() }, backend.Calls);
    }

    [Fact]
    public void BackendMutationFailureStillFaultsAndResetAllowsFreshPreparation()
    {
        var backend = new RecordingBackend();
        using var world = new SharedCharacterPhysicsSession(backend);
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        AddPair(world, first, second);
        backend.Calls.Clear();
        backend.FailNextImpulse = true;

        Assert.Throws<IOException>(() => world.Step([FirstSample(first, 3, 7), new(second, [Matrix4x4.Identity])]));

        Assert.True(world.IsFaulted);
        Assert.Equal(0, world.StepCount);
        var failedCalls = backend.Calls.ToArray();
        Assert.Throws<InvalidOperationException>(() => world.Step([FirstSample(first, 5), new(second, [Matrix4x4.Identity])]));
        Assert.Equal(failedCalls, backend.Calls);

        world.ResetAll([FirstSample(first, 0), new(second, [Matrix4x4.Identity])]);
        backend.Calls.Clear();
        var result = world.Step([FirstSample(first, 6), new(second, [Matrix4x4.Identity])]);

        Assert.False(world.IsFaulted);
        Assert.Equal(1, world.StepCount);
        Assert.Equal(new[] { K(0, true), T(0, 6), K(1, false), K(2, false), S() }, backend.Calls);
        Assert.Equal(Matrix4x4.CreateTranslation(6, 0, 0), result[first][0]);
    }

    private static void AddPair(SharedCharacterPhysicsSession world, Guid first, Guid second)
    {
        world.AddActor(first, new([new(-1, Matrix4x4.Identity), new(-1, Matrix4x4.Identity)],
            [Body(PhysicsBodyMode.Kinematic, 0), Body(PhysicsBodyMode.Dynamic, 1)], []),
            Matrix4x4.Identity, [Matrix4x4.Identity, Matrix4x4.Identity]);
        world.AddActor(second, new([new(-1, Matrix4x4.Identity)], [Body(PhysicsBodyMode.Dynamic, 0)], []),
            Matrix4x4.Identity, [Matrix4x4.Identity]);
    }

    private static SharedPhysicsActorSample FirstSample(Guid actor, float x, float? impulse = null)
        => new(actor, [Matrix4x4.CreateTranslation(x, 0, 0), Matrix4x4.Identity])
        { Impulses = impulse is { } value ? [new(1, false, new(value, 0, 0), Vector3.Zero)] : [] };

    private static PhysicsBodyDefinition Body(PhysicsBodyMode mode, int bone)
        => new("body", bone, PhysicsShapeKind.Sphere, new(.5f), Vector3.Zero, Vector3.Zero,
            1, 0, 0, 0, 0, 0, ushort.MaxValue, mode);

    private static BackendCall K(int slot, bool value) => new("kinematic", slot, Kinematic: value);
    private static BackendCall T(int slot, float x) => new("target", slot,
        Transform: Matrix4x4.CreateTranslation(x, 0, 0), Seconds: (float)CharacterPhysicsSession.FixedStepSeconds);
    private static BackendCall I(int slot, float x) => new("impulse", slot, Linear: new(x, 0, 0));
    private static BackendCall S() => new("step", Seconds: (float)CharacterPhysicsSession.FixedStepSeconds);

    private readonly record struct BackendCall(string Kind, int Slot = -1, bool Kinematic = false,
        Matrix4x4 Transform = default, Vector3 Linear = default, Vector3 Angular = default, float Seconds = 0);

    // Records the owner's exact mutation order without implementing a numerical solver.
    private sealed class RecordingBackend : ICharacterPhysicsMutableBackend
    {
        private readonly Dictionary<int, Matrix4x4> bodies = [];
        private int nextBody;
        public string Name => "shared preparation recorder";
        public IReadOnlyList<string> Diagnostics => [];
        public List<BackendCall> Calls { get; } = [];
        public bool RecordCalls { get; set; } = true;
        public int ImpulseCalls { get; private set; }
        public bool FailNextImpulse { get; set; }
        public int? OverrideReadSlot { get; set; }
        public Matrix4x4 OverrideRead { get; set; }
        public Action? BeforeRead { get; set; }
        public Action? BeforeKinematic { get; set; }

        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms)
        {
            bodies.Clear(); nextBody = 0;
            for (int index = 0; index < definition.Bodies.Count; index++) AddBody(definition.Bodies[index], transforms[index]);
        }
        public int AddBody(PhysicsBodyDefinition body, Matrix4x4 transform)
        { int slot = nextBody++; bodies.Add(slot, transform); return slot; }
        public int AddJoint(PhysicsJointDefinition joint, Matrix4x4 frameA, Matrix4x4 frameB) => throw new NotSupportedException();
        public void RemoveJoint(int index) => throw new NotSupportedException();
        public void RemoveBody(int index) => bodies.Remove(index);
        public void ResetBody(int index, Matrix4x4 transform) => bodies[index] = transform;
        public void SetGravity(Vector3 gravity) { }
        public void SetBodyKinematic(int index, bool kinematic)
        {
            var callback = BeforeKinematic; BeforeKinematic = null; callback?.Invoke();
            if (RecordCalls) Calls.Add(K(index, kinematic));
        }
        public void SetKinematicTarget(int index, Matrix4x4 transform, float seconds)
        {
            bodies[index] = transform;
            if (RecordCalls) Calls.Add(new("target", index, Transform: transform, Seconds: seconds));
        }
        public Matrix4x4 GetBodyWorldTransform(int index)
        {
            var callback = BeforeRead; BeforeRead = null; callback?.Invoke();
            return OverrideReadSlot == index ? OverrideRead : bodies[index];
        }
        public void ApplyImpulse(int index, Vector3 linear, Vector3 angular)
        {
            if (FailNextImpulse) { FailNextImpulse = false; throw new IOException("Injected impulse mutation failure."); }
            ImpulseCalls++;
            if (RecordCalls) Calls.Add(new("impulse", index, Linear: linear, Angular: angular));
        }
        public void Step(float seconds)
        { if (RecordCalls) Calls.Add(new("step", Seconds: seconds)); }
        public void Dispose() => bodies.Clear();
    }
}
