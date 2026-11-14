using System.Numerics;
using System.Reflection;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmSharedPhysicsTests
{
    [Fact]
    public void TwoPmmActorsAdvanceOneWorldAt120HzAndReportWorldStepsOnce()
    {
        var backend = new TrackingSharedBackend(); int allocated = 0;
        using var playback = Playback(backend, allocated: () => allocated++);
        Assert.True(playback.UsesSharedPhysicsWorld); Assert.Equal(1, allocated);
        playback.Seek(0); playback.Advance(.25, 1d / 120);
        Assert.Equal(1, playback.SharedPhysicsStepCount); Assert.Equal(1, playback.PhysicsLastStepCount);
        Assert.Equal(1, backend.Steps); Assert.Equal(2, backend.ActiveCount);
        playback.Advance(.5, 1d / 120);
        Assert.Equal(2, playback.SharedPhysicsStepCount); Assert.Equal(1, allocated);
    }

    [Fact]
    public void BoneDisableMakesBodyKinematicAndAnimationAuthoritativeThenRestoresDynamics()
    {
        var backend = new TrackingSharedBackend();
        using var playback = Playback(backend, switches: true);
        playback.Seek(0); int initializations = backend.Initializations; playback.Advance(1, 1d / 30);
        Assert.True(backend.Bodies[0].Kinematic);
        Assert.InRange(MathF.Abs(playback.Frame.Surface.Snapshot.Vertices[0].Position.X - 1), 0, 1e-4f);
        Assert.InRange(MathF.Abs(playback.Frame.Surface.Snapshot.Vertices[0].Position.Y), 0, 1e-4f);
        Assert.False(backend.Bodies[1].Kinematic);
        playback.Advance(2, 1d / 30);
        Assert.False(backend.Bodies[0].Kinematic);
        Assert.True(playback.Frame.Surface.Snapshot.Vertices[0].Position.Y < 0);
        Assert.Equal(initializations, backend.Initializations); // Neither switch rebuilds the world.
    }

    [Fact]
    public void PhysicsDisableResolvesBoneNamesInsteadOfAssumingPmmAndPmxIndicesAgree()
    {
        var backend = new TrackingSharedBackend();
        using var playback = Playback(backend, switches: true, precedingPmxBone: true);
        playback.Seek(0); playback.Advance(1, 1d / 30);
        Assert.True(backend.Bodies[0].Kinematic);
        Assert.InRange(MathF.Abs(playback.Frame.Surface.Snapshot.Vertices[0].Position.X - 1), 0, 1e-4f);
        playback.Advance(2, 1d / 30);
        Assert.False(backend.Bodies[0].Kinematic);
        Assert.True(playback.Frame.Surface.Snapshot.Vertices[0].Position.Y < 0);
    }

    [Fact]
    public void GravityKeysExtendDurationAndChangeExistingWorldWithoutResettingIt()
    {
        var backend = new TrackingSharedBackend();
        var gravity = new PmmGravity
        {
            Acceleration = 3, Direction = Vector3.UnitZ,
            InitialKeyframe = new() { Acceleration = 1, Direction = -Vector3.UnitY },
            Keyframes = [new() { Frame = 1, Acceleration = 2, Direction = Vector3.UnitX },
                new() { Frame = 40, Acceleration = 4, Direction = Vector3.UnitY }]
        };
        using var playback = Playback(backend, gravity: gravity);
        Assert.Equal(new(0, 0, 30), backend.Gravity); Assert.Equal(40u, playback.DurationFrames);
        playback.Seek(0); int initializations = backend.Initializations; Assert.Equal(new(0, -10, 0), backend.Gravity);
        playback.Advance(.75, 3d / 120); var body = backend.Bodies[0].Transform;
        playback.Advance(1, 1d / 120);
        Assert.Equal(new(20, 0, 0), backend.Gravity);
        Assert.Equal(initializations, backend.Initializations);
        Assert.True(backend.Bodies[0].Transform.M41 > body.M41);
        Assert.True(backend.Bodies[0].Transform.M42 < body.M42); // Existing downward velocity survives gravity change.
    }

    [Fact]
    public void PmmShortActorFreezesAndSeekReplaysBothActors()
    {
        var backend = new TrackingSharedBackend(); using var playback = Playback(backend, shortEnd: 1);
        playback.Seek(0); playback.Advance(1, 1d / 30);
        var stopped = playback.Frame.Surface.Snapshot.Vertices[0].Position;
        playback.Advance(2, 1d / 30); playback.Advance(3, 1d / 30);
        Assert.Equal(stopped, playback.Frame.Surface.Snapshot.Vertices[0].Position);
        Assert.True(playback.Frame.Surface.Snapshot.Vertices[3].Position.Y < stopped.Y);
        var expected = playback.Frame.Surface.Snapshot.Vertices.ToArray();
        playback.Seek(0);
        for (int i = 1; i <= 3; i++) playback.Advance(i, 1d / 30);
        Assert.Equal(expected, playback.Frame.Surface.Snapshot.Vertices.ToArray());
    }

    [Fact]
    public void PmmFrozenChildTracksNonphysicalParentAtTheCurrentBoundary()
    {
        var backend = new TrackingSharedBackend();
        using var playback = Playback(backend, shortEnd: 1, outsideParent: true,
            gravity: new() { InitialKeyframe = new() { Direction = -Vector3.UnitY } });
        playback.Seek(0);
        for (int i = 1; i <= 3; i++) playback.Advance(i, 1d / 30);
        Assert.InRange(MathF.Abs(playback.Frame.Surface.Snapshot.Vertices[0].Position.X - 3), 0, 1e-4f);
        Assert.InRange(MathF.Abs(playback.Frame.Surface.Snapshot.Vertices[3].Position.X - 3), 0, 1e-4f);
    }

    [Fact]
    public void BackwardsAdvanceRequiresSeekAndDoesNotStepTheWorld()
    {
        var backend = new TrackingSharedBackend(); using var playback = Playback(backend);
        playback.Seek(0); playback.Advance(1, 1d / 30); int before = backend.Steps;
        var snapshot = playback.Frame;
        Assert.Throws<ArgumentOutOfRangeException>(() => playback.Advance(.5, 1d / 120));
        Assert.Equal(before, backend.Steps); Assert.Same(snapshot, playback.Frame);
        playback.Seek(0); Assert.Equal(0, playback.SharedPhysicsStepCount);
        playback.Advance(.25, 1d / 120); Assert.Equal(1, playback.SharedPhysicsStepCount);
    }

    [Fact]
    public void SeekAppliesDisabledAndRestoredBodyModesImmediatelyWithoutSimulating()
    {
        var backend = new TrackingSharedBackend(); using var playback = Playback(backend, switches: true);
        int steps = backend.Steps;
        playback.Seek(1);
        Assert.True(backend.Bodies[0].Kinematic); Assert.False(backend.Bodies[1].Kinematic);
        Assert.Equal(steps, backend.Steps); Assert.Equal(0, playback.SharedPhysicsStepCount);
        Assert.Equal(0, playback.PhysicsLastStepCount);
        Assert.InRange(MathF.Abs(playback.Frame.Surface.Snapshot.Vertices[0].Position.X - 1), 0, 1e-4f);
        playback.Seek(2);
        Assert.False(backend.Bodies[0].Kinematic);
        Assert.Equal(steps, backend.Steps); Assert.Equal(0, playback.SharedPhysicsStepCount);
        Assert.Equal(0, playback.PhysicsLastStepCount);
    }

    [Fact]
    public void UnchangedTimelineFrameDoesNotAccumulateSolverTime()
    {
        var backend = new TrackingSharedBackend(); using var playback = Playback(backend);
        playback.Seek(0); var vertices = playback.Frame.Surface.Snapshot.Vertices.ToArray();
        var bodies = backend.Bodies.Select(body => body.Transform).ToArray();
        playback.Advance(0, 1d / 30);
        Assert.Equal(0, backend.Steps); Assert.Equal(0, playback.SharedPhysicsStepCount);
        Assert.Equal(0, playback.PhysicsLastStepCount);
        Assert.Equal(vertices, playback.Frame.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(bodies, backend.Bodies.Select(body => body.Transform).ToArray());
        playback.Advance(.25, 1d / 120);
        Assert.Equal(1, backend.Steps); Assert.Equal(1, playback.SharedPhysicsStepCount);
    }

    [Fact]
    public void ExcessiveElapsedIsRejectedBeforeWorldOrPublishedStateChanges()
    {
        var backend = new TrackingSharedBackend(); using var playback = Playback(backend);
        playback.Seek(0); var published = playback.Frame;
        var bodies = backend.Bodies.Select(body => body.Transform).ToArray(); int initializes = backend.Initializations;
        foreach (double elapsed in new[] { 86400.01, double.MaxValue })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => playback.Advance(.25, elapsed));
            Assert.Same(published, playback.Frame); Assert.Equal(0, playback.CurrentFrame);
            Assert.Equal(0, backend.Steps); Assert.Equal(initializes, backend.Initializations);
            Assert.Equal(bodies, backend.Bodies.Select(body => body.Transform).ToArray());
        }
        playback.Advance(.25, 1d / 120); Assert.Equal(1, backend.Steps);
    }

    [Fact]
    public void GravityNoiseStepsAtPhysicalBoundariesAndSeekRestartsTheSameStream()
    {
        var backend = new TrackingSharedBackend();
        var gravity = new PmmGravity { InitialKeyframe = new() { Acceleration = 9.8f,
            Direction = -Vector3.UnitY, NoiseEnabled = true, Noise = 2 } };
        using var playback = Playback(backend, gravity: gravity);
        playback.Seek(0);
        var initial = backend.Gravity;
        // Original gate is strictly > 0.5 seconds in float32; the host advances at 120 Hz.
        for (int i = 1; i <= 64; i++) playback.Advance(i * .25, 1d / 120);
        var noisy = backend.Gravity;
        var vertices = playback.Frame.Surface.Snapshot.Vertices.ToArray();
        Assert.NotEqual(initial, noisy);
        Assert.NotEqual(0, noisy.X);
        playback.Advance(16, 0); Assert.Equal(noisy, backend.Gravity);
        playback.Seek(0); Assert.Equal(initial, backend.Gravity);
        for (int i = 1; i <= 64; i++) playback.Advance(i * .25, 1d / 120);
        Assert.Equal(noisy, backend.Gravity);
        Assert.Equal(vertices, playback.Frame.Surface.Snapshot.Vertices.ToArray());
    }

    internal static PmmProjectPlayback Playback(TrackingSharedBackend backend, bool switches = false,
        int shortEnd = 30, PmmGravity? gravity = null, bool outsideParent = false, Action? allocated = null, bool precedingPmxBone = false,
        bool sharedPhysicsWorld = true, Func<ICharacterPhysicsBackend>? factory = null, Action<PmmDocument>? configure = null)
    {
        var child = SharedPhysicsFixture.Model(outsideParent ? PmxRigidBodyOperation.DynamicAndBoneMerge : PmxRigidBodyOperation.Dynamic,
            precedingBone: precedingPmxBone);
        var parent = outsideParent ? SharedPhysicsFixture.Model(physics: false) : child;
        PmmModel Actor(byte index, int end)
        {
            bool toggle = switches && index == 0;
            var keys = toggle ? new[]
            {
                new PmmBoneKeyframe { RecordIndex = 1, ObjectIndex = 1, PreviousIndex = 0, NextIndex = 2,
                    Frame = 1, Translation = Vector3.UnitX, Rotation = Quaternion.Identity, Interpolation = Curves(), PhysicsDisabled = true },
                new PmmBoneKeyframe { RecordIndex = 2, ObjectIndex = 2, PreviousIndex = 1, NextIndex = 3,
                    Frame = 2, Translation = new(2, 0, 0), Rotation = Quaternion.Identity, Interpolation = Curves() },
                new PmmBoneKeyframe { RecordIndex = 3, ObjectIndex = 3, PreviousIndex = 2,
                    Frame = end, Translation = new(end, 0, 0), Rotation = Quaternion.Identity, Interpolation = Curves() }
            } : [new PmmBoneKeyframe { RecordIndex = 1, ObjectIndex = 1, Frame = end, Rotation = Quaternion.Identity,
                Translation = outsideParent && index == 1 ? new(end, 0, 0) : Vector3.Zero, Interpolation = Curves() }];
            return new()
            {
                Index = index, DrawOrderIndex = index, TransformOrderIndex = index, Visible = true, LastFrame = end,
                BoneNames = [new(0, "root", [])],
                InitialBoneKeyframes = [new() { NextIndex = 1, Rotation = Quaternion.Identity, Interpolation = Curves() }],
                BoneKeyframes = keys, BoneStates = [new(0, Vector3.Zero, Quaternion.Identity, false, false, 0)],
                InitialModelKeyframe = new() { Visible = true, OutsideParents = outsideParent && index == 0 ? [new(0, 1, 0)] : [] },
                OutsideParentSubjectBoneIndices = outsideParent && index == 0 ? [-1] : [],
                OutsideParentStates = outsideParent && index == 0 ? [new(0, 0, 0, new(0, 1, 0))] : []
            };
        }
        var document = new PmmDocument
        {
            Models = [Actor(0, shortEnd), Actor(1, 30)], OutputWidth = 1280, OutputHeight = 720, CameraFov = 30,
            Settings = new() { PhysicsSimulationMode = 1, CameraLookAtModelIndex = -1 },
            Gravity = gravity ?? new() { InitialKeyframe = new() { Acceleration = 1, Direction = -Vector3.UnitY } },
            Camera = new() { Position = new(0, 0, -10), Perspective = true,
                InitialKeyframe = new() { Distance = -10, Fov = 30, Perspective = true, ParentModelIndex = -1,
                    Interpolation = Enumerable.Range(0, 6).SelectMany(_ => new byte[] { 20, 20, 107, 107 }).ToArray() } },
            Light = new() { Color = Vector3.One, Direction = -Vector3.UnitY,
                InitialKeyframe = new() { Color = Vector3.One, Direction = -Vector3.UnitY } }
        };
        configure?.Invoke(document);
        var prepared = document.Models.Select(source =>
        {
            var model = source.Index == 0 ? child : parent;
            return new PreparedPmmModel(source, "synthetic.pmx", model,
                PmmMotionAdapter.Convert(source, model), PmmMotionAdapter.ConvertSavedPose(source, model));
        }).ToList();
        var plan = (PmmProjectImportPlan)Activator.CreateInstance(typeof(PmmProjectImportPlan), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [document, "synthetic.pmm", prepared, new List<PmmUnresolvedAsset>(), new List<string>()], null)!;
        return new(plan, new Dictionary<int, OriginalFrameScene> { [0] = SharedPhysicsFixture.Rest(child), [1] = SharedPhysicsFixture.Rest(parent) },
            (_, _) => { allocated?.Invoke(); return factory?.Invoke() ?? backend; }, sharedPhysicsWorld: sharedPhysicsWorld);
    }
    private static byte[] Curves() => Enumerable.Range(0, 4).SelectMany(_ => new byte[] { 20, 20, 107, 107 }).ToArray();
}
