using System.Numerics;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmIndependentControlsTests
{
    [Fact]
    public void DisabledBonesUseNamesAndNeverAffectTheOtherWorld()
    {
        var worlds = Worlds();
        using var playback = Playback(worlds, switches: true, precedingBone: true);
        playback.Seek(0); playback.Advance(1, 1d / 30);
        Assert.All(worlds, world => Assert.Equal(1, world.ActiveCount));
        Assert.True(worlds[0].Bodies[0].Kinematic); Assert.False(worlds[1].Bodies[0].Kinematic);
        Assert.InRange(MathF.Abs(playback.Frame.Surface.Snapshot.Vertices[0].Position.X - 1), 0, 1e-4f);
        Assert.True(playback.Frame.Surface.Snapshot.Vertices[3].Position.Y < 0);
        playback.Advance(2, 1d / 30);
        Assert.False(worlds[0].Bodies[0].Kinematic);
        Assert.True(playback.Frame.Surface.Snapshot.Vertices[0].Position.Y < 0);
        Assert.False(playback.UsesSharedPhysicsWorld); Assert.Equal(0, playback.SharedPhysicsStepCount);
        Assert.Equal(8, playback.PhysicsLastStepCount);
    }

    [Fact]
    public void GravityUsesSavedStateThenTimelineAndExtendsDuration()
    {
        var worlds = Worlds();
        var gravity = new PmmGravity { Acceleration = 3, Direction = Vector3.UnitZ,
            InitialKeyframe = new() { Acceleration = 1, Direction = -Vector3.UnitY },
            Keyframes = [new() { Frame = 40, Acceleration = 4, Direction = Vector3.UnitX }] };
        using var playback = Playback(worlds, gravity: gravity);
        Assert.Equal(40u, playback.DurationFrames);
        Assert.All(worlds, world => Assert.Equal(new(0, 0, 30), world.Gravity));
        playback.Seek(0);
        Assert.All(worlds, world => Assert.Equal(new(0, -10, 0), world.Gravity));
        var initializations = worlds.Select(w => w.Initializations).ToArray();
        playback.Advance(1, 1d / 30);
        Assert.All(worlds, world => Assert.True(world.Gravity.X > 0));
        Assert.Equal(initializations, worlds.Select(w => w.Initializations));
    }

    [Fact]
    public void BoneSwitchesBetweenHostFramesMatchFixedStepReplay()
    {
        var coarseWorlds = Worlds(); var fineWorlds = Worlds();
        using var coarse = Playback(coarseWorlds, switches: true);
        using var fine = Playback(fineWorlds, switches: true);
        coarse.Seek(0); fine.Seek(0); coarse.Advance(2, 2d / 30);
        for (int i = 1; i <= 8; i++) fine.Advance(i * .25, 1d / 120);
        Assert.Equal(fine.Frame.Surface.Snapshot.Vertices.ToArray(), coarse.Frame.Surface.Snapshot.Vertices.ToArray());
        Assert.True(coarse.Frame.Surface.Snapshot.Vertices[0].Position.X > 1);
    }

    [Fact]
    public void NoiseAdvancesOncePerPhysicalBoundaryAndSeekReplaysAllWorlds()
    {
        var worlds = Worlds();
        using var playback = Playback(worlds, gravity: Noise());
        playback.Seek(0); var initial = worlds[0].Gravity;
        for (int i = 1; i <= 64; i++) playback.Advance(i * .25, 1d / 120);
        var noisy = worlds[0].Gravity; var expected = playback.Frame.Surface.Snapshot.Vertices.ToArray();
        Assert.NotEqual(initial, noisy); Assert.Equal(noisy, worlds[1].Gravity);
        playback.Advance(16, 1d / 30);
        Assert.Equal(noisy, worlds[0].Gravity); Assert.Equal(0, playback.PhysicsLastStepCount);
        playback.Seek(0); Assert.Equal(initial, worlds[0].Gravity);
        for (int i = 1; i <= 64; i++) playback.Advance(i * .25, 1d / 120);
        Assert.Equal(noisy, worlds[0].Gravity);
        Assert.Equal(expected, playback.Frame.Surface.Snapshot.Vertices.ToArray());
    }

    [Fact]
    public void ShortActorsFreezeEvenWhenGravityKeysExtendScenePastLastActor()
    {
        var worlds = Worlds();
        using var playback = Playback(worlds, shortEnd: 1,
            gravity: new() { InitialKeyframe = new() { Acceleration = 1, Direction = -Vector3.UnitY },
                Keyframes = [new() { Frame = 40, Acceleration = 2, Direction = Vector3.UnitX }] });
        playback.Seek(0); playback.Advance(1, 1d / 30);
        var shortActor = playback.Frame.Surface.Snapshot.Vertices[0].Position;
        for (int i = 2; i <= 30; i++) playback.Advance(i, 1d / 30);
        var allActors = playback.Frame.Surface.Snapshot.Vertices.ToArray();
        Assert.Equal(shortActor, allActors[0].Position);
        Assert.NotEqual(shortActor, allActors[3].Position);
        for (int i = 31; i <= 40; i++) playback.Advance(i, 1d / 30);
        Assert.Equal(allActors, playback.Frame.Surface.Snapshot.Vertices.ToArray());
        Assert.All(worlds, world => Assert.True(world.Bodies[0].Kinematic));
    }

    [Fact]
    public void FrozenChildStillFollowsCurrentNonphysicalParent()
    {
        var worlds = Worlds();
        using var playback = Playback(worlds, shortEnd: 1, outsideParent: true);
        playback.Seek(0);
        for (int i = 1; i <= 3; i++) playback.Advance(i, 1d / 30);
        Assert.InRange(MathF.Abs(playback.Frame.Surface.Snapshot.Vertices[0].Position.X - 3), 0, 1e-4f);
    }

    [Fact]
    public void SeekUpdatesDisabledModeWithoutStepping()
    {
        var worlds = Worlds(); using var playback = Playback(worlds, switches: true);
        playback.Seek(1); Assert.True(worlds[0].Bodies[0].Kinematic);
        Assert.All(worlds, world => Assert.Equal(0, world.Steps));
        playback.Seek(2); Assert.False(worlds[0].Bodies[0].Kinematic);
        Assert.Equal(0, playback.PhysicsLastStepCount);
    }

    [Fact]
    public void BackwardAndMalformedTimeFailBeforePublishedOrSolverMutation()
    {
        var worlds = Worlds(); using var playback = Playback(worlds);
        playback.Seek(0); playback.Advance(1, 1d / 30);
        var published = playback.Frame; var poses = worlds.Select(w => w.Bodies[0].Transform).ToArray();
        var steps = worlds.Select(w => w.Steps).ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => playback.Advance(.5, 1d / 120));
        foreach (double elapsed in new[] { -1d, double.NaN, double.PositiveInfinity, 86400.01 })
            Assert.Throws<ArgumentOutOfRangeException>(() => playback.Advance(2, elapsed));
        Assert.Same(published, playback.Frame); Assert.Equal(1, playback.CurrentFrame);
        Assert.Equal(poses, worlds.Select(w => w.Bodies[0].Transform)); Assert.Equal(steps, worlds.Select(w => w.Steps));
    }

    [Fact]
    public void MalformedControlsFailBeforeAllocatingAnyWorld()
    {
        int allocated = 0;
        Assert.Throws<InvalidDataException>(() => PmmSharedPhysicsTests.Playback(new(), sharedPhysicsWorld: false,
            allocated: () => allocated++, gravity: new() { InitialKeyframe = new() { Acceleration = float.NaN } }));
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void MissingMutableCapabilityFailsExplicitlyForControlledDocuments()
    {
        Assert.Throws<NotSupportedException>(() => PmmSharedPhysicsTests.Playback(new(), sharedPhysicsWorld: false,
            switches: true, factory: () => new LegacyBackend()));
    }

    [Fact]
    public void FaultInSecondWorldBlocksEveryWorldUntilSeekRecovery()
    {
        var first = new FaultBackend(); var second = new FaultBackend(); int next = 0;
        using var playback = PmmSharedPhysicsTests.Playback(new(), sharedPhysicsWorld: false,
            factory: () => next++ == 0 ? first : second);
        playback.Seek(0); var published = playback.Frame; second.FailNextStep = true;
        Assert.Throws<InvalidOperationException>(() => playback.Advance(.25, 1d / 120));
        Assert.Equal(1, first.Inner.Steps); Assert.Equal(0, second.Inner.Steps);
        Assert.Same(published, playback.Frame); Assert.Equal(0, playback.CurrentFrame);
        Assert.Throws<InvalidOperationException>(() => playback.Advance(.5, 1d / 120));
        Assert.Equal(1, first.Inner.Steps); Assert.Equal(0, second.Inner.Steps);
        Assert.Same(published, playback.Frame);
        playback.Seek(0); playback.Advance(.25, 1d / 120);
        Assert.Equal(2, first.Inner.Steps); Assert.Equal(1, second.Inner.Steps);
    }

    [Fact]
    public void MissingCapabilityOnReenablePreservesDisabledPlayback()
    {
        int next = 0;
        using var playback = PmmSharedPhysicsTests.Playback(new(), sharedPhysicsWorld: false,
            factory: () => next++ < 2 ? new TrackingSharedBackend() : new LegacyBackend());
        playback.Seek(1); playback.SetPhysics(false);
        var published = playback.Frame; var position = playback.GetBoneWorldTransform(0, 0);
        Assert.Throws<NotSupportedException>(() => playback.SetPhysics(true));
        Assert.False(playback.PhysicsEnabled); Assert.Same(published, playback.Frame);
        Assert.Equal(1, playback.CurrentFrame); Assert.Equal(position, playback.GetBoneWorldTransform(0, 0));
    }

    [Fact]
    public void ControlledToggleResetAndDisposalOwnEachBackendExactlyOnce()
    {
        var worlds = new List<FaultBackend>();
        using var playback = PmmSharedPhysicsTests.Playback(new(), sharedPhysicsWorld: false, switches: true,
            factory: () => { var world = new FaultBackend(); worlds.Add(world); return world; });
        playback.Seek(0); playback.Advance(1, 1d / 30);
        Assert.True(worlds[0].Inner.Bodies[0].Kinematic); Assert.Equal(2, worlds.Count);
        playback.Reset(); Assert.Equal(2, worlds.Count); Assert.Equal(0, playback.PhysicsLastStepCount);
        Assert.True(worlds[0].Inner.Bodies[0].Kinematic);
        playback.SetPhysics(false); Assert.False(playback.PhysicsEnabled);
        Assert.All(worlds, world => Assert.Equal(1, world.DisposeCount));
        playback.SetPhysics(true); Assert.True(playback.PhysicsEnabled); Assert.Equal(4, worlds.Count);
        Assert.True(worlds[2].Inner.Bodies[0].Kinematic);
        Assert.All(worlds.Skip(2), world => Assert.Equal(0, world.DisposeCount));
        playback.Advance(2, 1d / 30); Assert.False(worlds[2].Inner.Bodies[0].Kinematic);
        playback.Dispose(); playback.Dispose();
        Assert.All(worlds, world => Assert.Equal(1, world.DisposeCount));
    }

    [Fact]
    public void NoControlDocumentsRetainLegacyBackendCompatibility()
    {
        int allocated = 0;
        using var playback = PmmSharedPhysicsTests.Playback(new(), sharedPhysicsWorld: false,
            gravity: new() { Acceleration = 1, Direction = -Vector3.UnitY,
                InitialKeyframe = new() { Acceleration = 1, Direction = -Vector3.UnitY } },
            allocated: () => allocated++, factory: () => new LegacyBackend());
        playback.Seek(0); playback.Advance(1, 1d / 30);
        Assert.Equal(4, allocated); // Saved-pose controllers are replaced when entering the timeline.
    }

    [Fact]
    public void ReusingFactoryBackendIsRejectedBeforeSecondWorldInitialization()
    {
        var backend = new TrackingSharedBackend();
        Assert.Throws<InvalidOperationException>(() => PmmSharedPhysicsTests.Playback(backend, sharedPhysicsWorld: false));
        Assert.Equal(1, backend.Initializations);
    }

    [Fact]
    public void NoControlLegacyFactoryReuseIsRejectedBeforeSecondInitialization()
    {
        var backend = new LegacyBackend(); int calls = 0, beforeSecondActor = 0;
        Assert.Throws<InvalidOperationException>(() => PmmSharedPhysicsTests.Playback(new(), sharedPhysicsWorld: false,
            gravity: new() { Acceleration = 1, Direction = -Vector3.UnitY,
                InitialKeyframe = new() { Acceleration = 1, Direction = -Vector3.UnitY } }, factory: () =>
                { if (++calls == 2) beforeSecondActor = backend.Initializations; return backend; }));
        Assert.Equal(2, calls); Assert.True(beforeSecondActor > 0);
        Assert.Equal(beforeSecondActor, backend.Initializations);
    }

    [Fact]
    public void RealBulletIndependentWorldsNeverContactOverlappingOtherActors()
    {
        int allocated = 0;
        using var playback = PmmSharedPhysicsTests.Playback(new(), sharedPhysicsWorld: false, switches: true,
            factory: () => { allocated++; return new BulletCharacterPhysicsBackend(Vector3.Zero, null); },
            gravity: new() { Acceleration = 0, InitialKeyframe = new() { Acceleration = 0 } });
        playback.Seek(0);
        for (int i = 1; i <= 3; i++) playback.Advance(i * .25, 1d / 120);
        Assert.Equal(2, allocated);
        Assert.Equal(playback.Frame.Surface.Snapshot.Vertices[0].Position,
            playback.Frame.Surface.Snapshot.Vertices[3].Position);
    }

    private static TrackingSharedBackend[] Worlds() => [new(), new()];
    private static PmmGravity Noise() => new() { InitialKeyframe = new() { Acceleration = 9.8f,
        Direction = -Vector3.UnitY, NoiseEnabled = true, Noise = 2 } };
    private static PmmProjectPlayback Playback(TrackingSharedBackend[] worlds, bool switches = false,
        bool precedingBone = false, int shortEnd = 30, PmmGravity? gravity = null, bool outsideParent = false)
    {
        int next = 0;
        return PmmSharedPhysicsTests.Playback(worlds[0], switches, shortEnd, gravity, outsideParent,
            precedingPmxBone: precedingBone, sharedPhysicsWorld: false, factory: () => worlds[next++]);
    }

    private sealed class LegacyBackend : ICharacterPhysicsBackend
    {
        private Matrix4x4[] poses = [];
        public int Initializations { get; private set; }
        public string Name => "legacy no-control backend";
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms)
        { Initializations++; poses = transforms.ToArray(); }
        public void SetKinematicTarget(int index, Matrix4x4 transform, float seconds) => poses[index] = transform;
        public Matrix4x4 GetBodyWorldTransform(int index) => poses[index];
        public void Step(float seconds) { }
        public void Dispose() { }
    }

    private sealed class FaultBackend : ICharacterPhysicsMutableBackend
    {
        internal TrackingSharedBackend Inner { get; } = new();
        internal bool FailNextStep;
        internal int DisposeCount;
        public string Name => Inner.Name;
        public IReadOnlyList<string> Diagnostics => Inner.Diagnostics;
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms) => Inner.Initialize(definition, transforms);
        public int AddBody(PhysicsBodyDefinition definition, Matrix4x4 transform) => Inner.AddBody(definition, transform);
        public int AddJoint(PhysicsJointDefinition definition, Matrix4x4 a, Matrix4x4 b) => Inner.AddJoint(definition, a, b);
        public void RemoveBody(int index) => Inner.RemoveBody(index);
        public void RemoveJoint(int index) => Inner.RemoveJoint(index);
        public void ResetBody(int index, Matrix4x4 transform) => Inner.ResetBody(index, transform);
        public void SetBodyKinematic(int index, bool enabled) => Inner.SetBodyKinematic(index, enabled);
        public void SetKinematicTarget(int index, Matrix4x4 transform, float seconds) => Inner.SetKinematicTarget(index, transform, seconds);
        public Matrix4x4 GetBodyWorldTransform(int index) => Inner.GetBodyWorldTransform(index);
        public void SetGravity(Vector3 gravity) => Inner.SetGravity(gravity);
        public void ApplyImpulse(int index, Vector3 linear, Vector3 angular) => Inner.ApplyImpulse(index, linear, angular);
        public void Step(float seconds)
        {
            if (FailNextStep) { FailNextStep = false; throw new InvalidOperationException("Injected second-world failure."); }
            Inner.Step(seconds);
        }
        public void Dispose() { DisposeCount++; Inner.Dispose(); }
    }
}
