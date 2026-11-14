using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmSelfShadowTrackTests
{
    [Fact]
    public void SavedStatePreservesIndependentEnabledAndRawDistanceWithoutInventingMode()
    {
        var tracks = new PmmSceneTracks(new PmmDocument()
        {
            SelfShadow = new() { Enabled = false, Distance = .03125f,
                InitialKeyframe = new() { Mode = 2, Distance = .01125f } }
        });
        Assert.Equal(new PmmSelfShadowSavedState(false, .03125f), tracks.SavedSelfShadow);
        Assert.Equal(new PmmSelfShadowPose(2, .01125f), tracks.SelfShadowAt(0));
        Assert.True(tracks.SelfShadowAt(0).Enabled);
    }

    [Fact]
    public void ShadowUsesExplicitStepPolicyAndSelectsExactNewKeyAtBoundary()
    {
        var tracks = Tracks(new PmmSelfShadowKeyframe() { Frame = 0, Mode = 1, Distance = .01f },
            new PmmSelfShadowKeyframe() { Frame = 10, Mode = 2, Distance = .03f },
            new PmmSelfShadowKeyframe() { Frame = 20, Mode = 0, Distance = .02f });
        Assert.Equal(new PmmSelfShadowPose(1, .01f), tracks.SelfShadowAt(9.999));
        Assert.Equal(new PmmSelfShadowPose(2, .03f), tracks.SelfShadowAt(10));
        Assert.Equal(new PmmSelfShadowPose(2, .03f), tracks.SelfShadowAt(19.999));
        Assert.Equal(new PmmSelfShadowPose(0, .02f), tracks.SelfShadowAt(20));
        Assert.False(tracks.SelfShadowAt(20).Enabled);
        Assert.Equal(tracks.SelfShadowAt(20), tracks.SelfShadowAt(double.MaxValue));
    }

    [Fact]
    public void ShadowOnlyTailExtendsSceneDurationAndCanBeSampledInReverseOrder()
    {
        var tracks = Tracks(new PmmSelfShadowKeyframe() { Frame = 0, Mode = 1, Distance = .01f },
            new PmmSelfShadowKeyframe() { Frame = 100, Mode = 2, Distance = .02f });
        Assert.Equal(100u, tracks.DurationFrames);
        Assert.Equal((byte)2, tracks.SelfShadowAt(100).Mode);
        Assert.Equal((byte)1, tracks.SelfShadowAt(50).Mode);
        Assert.Equal((byte)1, tracks.SelfShadowAt(0).Mode);
        Assert.Equal((byte)2, tracks.SelfShadowAt(100).Mode);
    }

    [Fact]
    public void DuplicateAndUnsortedKeysUseLastRecordWithoutChangingSource()
    {
        var first = new PmmSelfShadowKeyframe { Frame = 5, Mode = 1, Distance = .01f };
        var extra = new List<PmmSelfShadowKeyframe>
        {
            new PmmSelfShadowKeyframe() { Frame = 20, Mode = 0, Distance = .04f },
            new PmmSelfShadowKeyframe() { Frame = 5, Mode = 2, Distance = .02f },
            new PmmSelfShadowKeyframe() { Frame = 5, Mode = 1, Distance = .03f }
        };
        var tracks = new PmmSceneTracks(new PmmDocument() { SelfShadow = new() { InitialKeyframe = first, Keyframes = extra } });
        Assert.Equal(new PmmSelfShadowPose(1, .03f), tracks.SelfShadowAt(0));
        Assert.Equal(new[] { 20, 5, 5 }, extra.Select(k => k.Frame));
        first.Frame = 999; extra[2].Frame = 777; extra.Clear();
        Assert.Equal(new PmmSelfShadowPose(1, .03f), tracks.SelfShadowAt(5));
        Assert.Equal(new PmmSelfShadowPose(0, .04f), tracks.SelfShadowAt(20));
        Assert.Equal(20u, tracks.DurationFrames);
    }

    [Fact]
    public void RawDistancesAreNotClampedConvertedOrBlended()
    {
        var tracks = Tracks(new PmmSelfShadowKeyframe() { Mode = 1, Distance = -2f },
            new PmmSelfShadowKeyframe() { Frame = int.MaxValue, Mode = 2, Distance = 12345f });
        Assert.Equal(-2f, tracks.SelfShadowAt(.5).Distance);
        Assert.Equal(12345f, tracks.SelfShadowAt(int.MaxValue).Distance);
        Assert.Equal((uint)int.MaxValue, tracks.DurationFrames);
    }

    [Theory]
    [InlineData(-1)] [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)] [InlineData(double.NegativeInfinity)]
    public void InvalidTimeIsRejected(double frame) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PmmSceneTracks(new PmmDocument()).SelfShadowAt(frame));

    [Fact]
    public void InvalidModesAndNegativeFramesAreRejectedEvenWhenDuplicateWouldHideThem()
    {
        Assert.Throws<InvalidDataException>(() => Tracks(new PmmSelfShadowKeyframe() { Mode = 3 }));
        Assert.Throws<InvalidDataException>(() => Tracks(new PmmSelfShadowKeyframe() { Frame = -1 }));
        Assert.Throws<InvalidDataException>(() => Tracks(new PmmSelfShadowKeyframe() { Mode = 255 }, new PmmSelfShadowKeyframe() { Mode = 1 }));
        Assert.Throws<InvalidDataException>(() => Tracks(new PmmSelfShadowKeyframe(), new PmmSelfShadowKeyframe() { Frame = -1 }));
    }

    [Theory]
    [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(float.NegativeInfinity)]
    public void NonFiniteSavedOrAnimatedDistanceFailsBeforePlayback(float distance)
    {
        Assert.Throws<InvalidDataException>(() => new PmmSceneTracks(new PmmDocument() { SelfShadow = new() { Distance = distance } }));
        Assert.Throws<InvalidDataException>(() => Tracks(new PmmSelfShadowKeyframe() { Distance = distance }));
        Assert.Throws<InvalidDataException>(() => Tracks(new PmmSelfShadowKeyframe(), new PmmSelfShadowKeyframe() { Frame = 1, Distance = distance }));
    }

    [Fact]
    public void MissingShadowContainersAndNullKeyRecordsAreRejectedClearly()
    {
        Assert.Throws<InvalidDataException>(() => new PmmSceneTracks(new PmmDocument() { SelfShadow = null! }));
        Assert.Throws<InvalidDataException>(() => new PmmSceneTracks(new PmmDocument() { SelfShadow = new() { InitialKeyframe = null! } }));
        Assert.Throws<InvalidDataException>(() => new PmmSceneTracks(new PmmDocument() { SelfShadow = new() { Keyframes = null! } }));
        Assert.Throws<InvalidDataException>(() => Tracks(new PmmSelfShadowKeyframe(), null!));
    }

    static PmmSceneTracks Tracks(PmmSelfShadowKeyframe first, params PmmSelfShadowKeyframe[] extra) =>
        new(new() { SelfShadow = new() { InitialKeyframe = first, Keyframes = extra } });
}
