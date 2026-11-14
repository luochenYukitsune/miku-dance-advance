using System.Numerics;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class GravityParityTests
{
    [Fact]
    public void OriginalNativeEvaluatorOneHundredEightySamplesHaveIdenticalScalarBits()
    {
        foreach (var row in GravityParityOracleData.Cases)
        {
            float F(uint value) => BitConverter.UInt32BitsToSingle(value);
            var tracks = new PmmPhysicsControlTracks(new() { Gravity = new()
            {
                InitialKeyframe = new() { Frame = row.Start, Acceleration = F(row.Left[0]),
                    Direction = new(F(row.Left[1]), F(row.Left[2]), F(row.Left[3])), Noise = row.LeftNoise, NoiseEnabled = row.LeftEnabled },
                Keyframes = [new() { Frame = row.End, Acceleration = F(row.Right[0]),
                    Direction = new(F(row.Right[1]), F(row.Right[2]), F(row.Right[3])), Noise = row.RightNoise, NoiseEnabled = row.RightEnabled }]
            } });
            var actual = tracks.GravityAt(row.Frame);
            Assert.Equal(row.Expected, new[] { actual.Acceleration, actual.Direction.X, actual.Direction.Y, actual.Direction.Z }.Select(BitConverter.SingleToUInt32Bits));
            Assert.Equal(row.Noise, actual.Noise); Assert.Equal(row.Enabled, actual.NoiseEnabled);
        }
    }
    [Fact]
    public void IntermediateGravityInterpolatesRawScalarsAndTruncatesNoiseDifferenceBeforeAddingLeft()
    {
        var tracks = new PmmPhysicsControlTracks(new()
        {
            Gravity = new()
            {
                InitialKeyframe = new() { Acceleration = 1, Direction = Vector3.UnitX, Noise = 9, NoiseEnabled = true },
                Keyframes = [new() { Frame = 10, Acceleration = 3, Direction = Vector3.UnitY, Noise = 0 }]
            }
        });
        var pose = tracks.GravityAt(5);
        Assert.Equal(2, pose.Acceleration); Assert.Equal(new Vector3(.5f, .5f, 0), pose.Direction);
        Assert.Equal(5, pose.Noise); Assert.True(pose.NoiseEnabled);
        Assert.False(tracks.GravityAt(10).NoiseEnabled); Assert.Equal(0, tracks.GravityAt(10).Noise);
    }
    [Fact]
    public void FractionalFrameUsesTheSameLinearPolicyWithoutIntegerFlooring()
    {
        var tracks = new PmmPhysicsControlTracks(new() { Gravity = new()
        { InitialKeyframe = new() { Acceleration = 1, Direction = Vector3.UnitX },
            Keyframes = [new() { Frame = 2, Acceleration = 5, Direction = Vector3.UnitY }] } });
        Assert.Equal(2, tracks.GravityAt(.5).Acceleration);
        Assert.Equal(new Vector3(.75f, .25f, 0), tracks.GravityAt(.5).Direction);
    }
    [Fact]
    public void NoiseUsesTheUnchangedOriginalBlocksFourRandomDrawsAndScalarBitPattern()
    {
        var pose = new PmmGravityPose(3, new(1, -2, .3f), true, 7, PmmPhysicsSettings.ToGravity(3, new(1, -2, .3f)));
        var noise = new PmmGravityNoise(1); noise.Reset(pose);
        Assert.Equal(pose.WorldGravity, noise.Advance(pose, .5)); Assert.Equal(0ul, noise.RandomDrawCount);
        var actual = noise.Advance(pose, 1d / 120);
        Assert.Equal(new Vector3(3.1268529891967773f, -6.364566326141357f, 1.1169291734695435f), actual);
        Assert.Equal(4ul, noise.RandomDrawCount); Assert.Equal(0, noise.ElapsedSeconds);
        Assert.Equal(actual, noise.Advance(pose, 0));
        noise.Reset(pose); noise.Advance(pose, .5); Assert.Equal(actual, noise.Advance(pose, 1d / 120));
    }
    [Fact]
    public void NoiseDisabledKeepsStreamAndElapsedAndInvalidDeltaDoesNotMutate()
    {
        var pose = new PmmGravityPose(2, Vector3.UnitY, true, 9, new(0, 20, 0));
        var noise = new PmmGravityNoise(); noise.Reset(pose); noise.Advance(pose, .25);
        Assert.Equal(pose.WorldGravity, noise.Advance(pose with { NoiseEnabled = false }, 2));
        Assert.Equal(.25f, noise.ElapsedSeconds); Assert.Equal(0ul, noise.RandomDrawCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => noise.Advance(pose, double.NaN));
        Assert.Equal(.25f, noise.ElapsedSeconds); Assert.Equal(0ul, noise.RandomDrawCount);
        noise.Advance(pose, .26); Assert.Equal(4ul, noise.RandomDrawCount);
    }
    [Fact]
    public void FixedStepSeekReplayIsDeterministicWithoutClaimingOriginalGlobalScheduling()
    {
        var pose = new PmmGravityPose(2, -Vector3.UnitY, true, 9, new(0, -20, 0));
        var noise = new PmmGravityNoise(42); noise.Reset(pose);
        var samples = Enumerable.Range(0, 240).Select(_ => noise.Advance(pose, 1d / 120)).ToArray();
        noise.Reset(pose);
        Assert.Equal(samples, Enumerable.Range(0, 240).Select(_ => noise.Advance(pose, 1d / 120)));
        Assert.True(noise.RandomDrawCount > 0);
    }
}
