using System.Numerics;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmPhysicsControlTracksTests
{
    [Fact]
    public void SavedGravityAndTimelineRemainIndependent()
    {
        var tracks = new PmmPhysicsControlTracks(new PmmDocument { Gravity = new()
        { Acceleration = 2, Direction = Vector3.UnitX, InitialKeyframe = Gravity(0, 9.8f, -Vector3.UnitY) } });
        Assert.Equal(new Vector3(20, 0, 0), tracks.SavedGravity.WorldGravity);
        Assert.Equal(new Vector3(0, -98, 0), tracks.GravityAt(0).WorldGravity);
    }

    [Fact]
    public void GravityUsesOriginalLinearScalarsAndExactKeyBoundaries()
    {
        var tracks = Tracks(new PmmGravity { InitialKeyframe = Gravity(0, 1, Vector3.UnitX),
            Keyframes = [Gravity(10, 2, Vector3.UnitY), Gravity(20, 3, Vector3.UnitZ)] });
        Assert.Equal(1.5f, tracks.GravityAt(5).Acceleration);
        Assert.Equal(new Vector3(.5f, .5f, 0), tracks.GravityAt(5).Direction);
        Assert.Equal(new Vector3(0, 20, 0), tracks.GravityAt(10).WorldGravity);
        Assert.Equal(2.5f, tracks.GravityAt(15).Acceleration);
        Assert.Equal(new Vector3(0, .5f, .5f), tracks.GravityAt(15).Direction);
        Assert.Equal(new Vector3(0, 0, 30), tracks.GravityAt(double.MaxValue).WorldGravity);
        Assert.Equal(new Vector3(10, 0, 0), tracks.GravityAt(0).WorldGravity);
        Assert.Equal(20u, tracks.DurationFrames);
    }

    [Fact]
    public void NoiseIsRetainedWithExplicitStatefulRuntimeRequirement()
    {
        var tracks = Tracks(new PmmGravity { NoiseEnabled = true, Noise = 17,
            InitialKeyframe = new() { NoiseEnabled = true, Noise = 99, Acceleration = 1, Direction = Vector3.UnitX } });
        Assert.True(tracks.SavedGravity.NoiseEnabled);
        Assert.Equal(17, tracks.SavedGravity.Noise);
        Assert.True(tracks.GravityAt(0).NoiseEnabled);
        Assert.Equal(99, tracks.GravityAt(0).Noise);
        Assert.Contains(tracks.Diagnostics, message => message.Contains("noise", StringComparison.OrdinalIgnoreCase)
            && message.Contains("stateful", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new Vector3(10, 0, 0), tracks.GravityAt(0).WorldGravity);
    }

    [Fact]
    public void SparseBoneLinksKeepIndependentTracksAndSavedFlags()
    {
        var model = Model();
        var tracks = new PmmPhysicsControlTracks(new PmmDocument { Models = [model] });
        Assert.Equal(new[] { 1 }, tracks.DisabledBonesAt(7, 0, saved: true));
        Assert.Equal(new[] { 0 }, tracks.DisabledBonesAt(7, 0));
        Assert.Equal(new[] { 0 }, tracks.DisabledBonesAt(7, 9.999));
        Assert.Equal(new[] { 1 }, tracks.DisabledBonesAt(7, 10));
        Assert.Equal(new[] { 1 }, tracks.DisabledBonesAt(7, double.MaxValue));
        Assert.Equal(new[] { 0 }, tracks.DisabledBonesAt(7, 5));
        Assert.Equal(10u, tracks.DurationFrames);
    }

    [Fact]
    public void ConstructorSnapshotsMutableRecordsAndCollections()
    {
        var first = Gravity(0, 1, Vector3.UnitX);
        var extras = new List<PmmGravityKeyframe> { Gravity(5, 2, Vector3.UnitY) };
        var model = Model();
        var tracks = new PmmPhysicsControlTracks(new PmmDocument { Models = [model],
            Gravity = new() { InitialKeyframe = first, Keyframes = extras } });
        first.Frame = 99; extras[0].Frame = 999; extras.Clear();
        model.InitialBoneKeyframes[0].Frame = 500;
        model.BoneKeyframes[0].Frame = 600;
        Assert.Equal(new Vector3(0, 20, 0), tracks.GravityAt(5).WorldGravity);
        Assert.Equal(new[] { 1 }, tracks.DisabledBonesAt(7, 10));
        Assert.Equal(10u, tracks.DurationFrames);
        Assert.Throws<NotSupportedException>(() => ((IList<int>)tracks.DisabledBonesAt(7, 10))[0] = 0);
    }

    [Fact]
    public void LastDuplicateGravityRecordWinsWithoutSortingSource()
    {
        var extras = new[] { Gravity(20, 2, Vector3.UnitY), Gravity(0, 3, Vector3.UnitZ) };
        var tracks = Tracks(new PmmGravity { InitialKeyframe = Gravity(0, 1, Vector3.UnitX), Keyframes = extras });
        Assert.Equal(new Vector3(0, 0, 30), tracks.GravityAt(0).WorldGravity);
        Assert.Equal(new[] { 20, 0 }, extras.Select(k => k.Frame));
    }

    [Theory]
    [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(double.NegativeInfinity)]
    public void InvalidTimeFailsEvenForSavedOrUnknownActor(double frame)
    {
        var tracks = new PmmPhysicsControlTracks(new PmmDocument { Models = [Model()] });
        Assert.Throws<ArgumentOutOfRangeException>(() => tracks.GravityAt(frame));
        Assert.Throws<ArgumentOutOfRangeException>(() => tracks.DisabledBonesAt(7, frame, true));
    }

    [Fact]
    public void UnknownActorAndDuplicateActorAreRejected()
    {
        var tracks = new PmmPhysicsControlTracks(new PmmDocument());
        Assert.Throws<KeyNotFoundException>(() => tracks.DisabledBonesAt(7, 0));
        Assert.Throws<InvalidDataException>(() => new PmmPhysicsControlTracks(new PmmDocument { Models = [Model(), Model()] }));
    }

    [Theory]
    [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(float.NegativeInfinity)] [InlineData(float.MaxValue)]
    public void EveryGravityKeyIsValidatedBeforeDuplicateElimination(float acceleration)
    {
        Assert.Throws<InvalidDataException>(() => Tracks(new PmmGravity
        { InitialKeyframe = Gravity(0, acceleration, Vector3.UnitX), Keyframes = [Gravity(0, 1, Vector3.UnitY)] }));
        Assert.Throws<InvalidDataException>(() => Tracks(new PmmGravity { Acceleration = acceleration, Direction = Vector3.UnitX }));
    }

    [Fact]
    public void InvalidDirectionAndNegativeFrameFailDuringPreparation()
    {
        Assert.Throws<InvalidDataException>(() => Tracks(new PmmGravity { InitialKeyframe = Gravity(0, 0, new(float.NaN, 0, 0)) }));
        Assert.Throws<InvalidDataException>(() => Tracks(new PmmGravity { InitialKeyframe = Gravity(-1, 1, Vector3.UnitX) }));
    }

    [Fact]
    public void CrossTrackAndUnlinkedBoneKeysFailBeforeSolverAllocation()
    {
        var cross = Model(); cross.BoneKeyframes[0].NextIndex = 99;
        Assert.Throws<InvalidDataException>(() => new PmmPhysicsControlTracks(new PmmDocument { Models = [cross] }));
        var unlinked = Model(); unlinked.InitialBoneKeyframes[0].NextIndex = 0;
        Assert.Throws<InvalidDataException>(() => new PmmPhysicsControlTracks(new PmmDocument { Models = [unlinked] }));
    }

    [Fact]
    public void SavedBoneCountMismatchFailsEvenWhenAllFlagsEnabled()
    {
        Assert.Throws<InvalidDataException>(() => new PmmPhysicsControlTracks(new PmmDocument
        { Models = [new() { Index = 3, BoneNames = [new(0, "a", [])], InitialBoneKeyframes = [new()] }] }));
    }

    private static PmmPhysicsControlTracks Tracks(PmmGravity gravity) => new(new PmmDocument { Gravity = gravity });
    private static PmmGravityKeyframe Gravity(int frame, float acceleration, Vector3 direction) =>
        new() { Frame = frame, Acceleration = acceleration, Direction = direction };
    private static PmmModel Model() => new()
    {
        Index = 7, BoneNames = [new(0, "a", []), new(0, "b", [])],
        InitialBoneKeyframes = [new() { RecordIndex = 0, ObjectIndex = 0, NextIndex = 42, PhysicsDisabled = true },
            new() { RecordIndex = 1, ObjectIndex = 1, NextIndex = 99 }],
        BoneKeyframes = [new() { RecordIndex = 2, ObjectIndex = 42, PreviousIndex = 0, Frame = 10 },
            new() { RecordIndex = 3, ObjectIndex = 99, PreviousIndex = 1, Frame = 10, PhysicsDisabled = true }],
        BoneStates = [new(0, Vector3.Zero, Quaternion.Identity, false, false, 0),
            new(0, Vector3.Zero, Quaternion.Identity, false, true, 0)]
    };
}
