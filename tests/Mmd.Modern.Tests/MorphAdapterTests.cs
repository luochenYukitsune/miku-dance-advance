using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Formats;
using Xunit;
using static Mmd.Modern.Tests.MorphApplicationTests;

namespace Mmd.Modern.Tests;

public sealed class MorphAdapterTests
{
    [Fact]
    public void VmdWeightsAreLinearUnclampedAndLastDuplicateWinsWithoutAFrameZeroSeed()
    {
        var adapter = new PmxMorphAdapter(Model([Position("smile", Vector3.UnitY)]));
        var motion = new MmdMorphMotionAdapter(adapter, new()
        {
            MorphKeyframes = [Key("smile", 10, .5f), Key("smile", 10, -1), Key("smile", 20, 3), Key("unknown", 40, 1)]
        });
        Assert.Equal(40u, motion.DurationFrames); Assert.Equal(1, motion.MatchedTrackCount);
        Assert.Equal(-1, motion.Evaluate(0)[0]); Assert.Equal(-1, motion.Evaluate(10)[0]);
        Assert.Equal(0, motion.Evaluate(12.5)[0]); Assert.Equal(1, motion.Evaluate(15)[0]); Assert.Equal(3, motion.Evaluate(99)[0]);
        Assert.Contains(motion.Diagnostics, d => d.Contains("last record in file order wins", StringComparison.Ordinal));
        Assert.Contains(motion.Diagnostics, d => d.Contains("'unknown'", StringComparison.Ordinal));
        Assert.Throws<ArgumentOutOfRangeException>(() => motion.Evaluate(double.NaN));
        Assert.Throws<ArgumentException>(() => new MmdMorphMotionAdapter(adapter, new() { MorphKeyframes = [Key("smile", 0, float.PositiveInfinity)] }));
    }

    [Fact]
    public void DuplicatePmxNamesUseTheFirstMorphAndDoNotBypassAnUnsupportedFirstMatch()
    {
        var adapter = new PmxMorphAdapter(Model([Position("same", Vector3.UnitY), Position("same", new(0, 8, 0))]));
        var motion = new MmdMorphMotionAdapter(adapter, new() { MorphKeyframes = [Key("same", 0, 1)] });
        var pose = Assert.IsType<Mmd.Modern.Animation.MorphPose>(adapter.Evaluate(motion.Evaluate(0)));
        Assert.Equal(Vector3.UnitY, pose.VertexDeltas[0]);
        Assert.Contains(adapter.Diagnostics, d => d.Contains("only the first PMX morph", StringComparison.Ordinal));
        var blocked = new PmxMorphAdapter(Model([new() { Name = "same", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Group }, Position("same", Vector3.UnitY)]));
        Assert.False(new MmdMorphMotionAdapter(blocked, new() { MorphKeyframes = [Key("same", 0, 1)] }).HasMatchedTracks);
    }

    [Fact]
    public void VertexOffsetsAddInPmxOrderAndImportedOffsetsAreCopiedOnce()
    {
        PmxMorph.PositionOffset[] source = [new(0, Vector3.UnitY), new(0, new(0, 2, 0))];
        var model = Model([new() { Name = "a", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Position, PositionOffsets = source }, Position("b", Vector3.UnitX)]);
        var adapter = new PmxMorphAdapter(model); source[0] = new(0, new(99));
        var pose = Assert.IsType<Mmd.Modern.Animation.MorphPose>(adapter.Evaluate(new Dictionary<int, float> { [0] = -1, [1] = 2 }));
        Assert.Equal(new Vector3(2, -3, 0), pose.VertexDeltas[0]);
        Assert.Null(adapter.Evaluate(new Dictionary<int, float> { [0] = 0 }));
        Assert.Throws<ArgumentException>(() => adapter.Evaluate(new Dictionary<int, float> { [0] = float.NaN }));
    }

    [Fact]
    public void BoneMorphsAccumulateWeightedNoncommutingRotationsInOriginalD3dxOrder()
    {
        var x = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2);
        var y = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var adapter = new PmxMorphAdapter(Model([Bone("x", Vector3.UnitX, x), Bone("y", Vector3.UnitY, y)]));
        var pose = Assert.IsType<Mmd.Modern.Animation.MorphPose>(adapter.Evaluate(new Dictionary<int, float> { [0] = .5f, [1] = 1 }));
        Assert.Equal(new Vector3(.5f, 1, 0), pose.BoneTranslations[0]);
        var expected = new Vector3(MathF.Sqrt(.5f), -MathF.Sqrt(.5f), 0);
        Assert.InRange(Vector3.Distance(expected, Vector3.Transform(Vector3.UnitZ, pose.BoneRotations[0])), 0, 1e-5f);
        // Successive slerp to Y at full weight would erase the earlier X rotation.
        Assert.True(Vector3.Distance(Vector3.UnitX, Vector3.Transform(Vector3.UnitZ, pose.BoneRotations[0])) > .5f);
    }

    [Fact]
    public void NegativeWUsesTheOriginalThreePointOneFourConstant()
    {
        var adapter = new PmxMorphAdapter(Model([Bone("negative-w", Vector3.Zero, new(0, 0, MathF.Sqrt(.75f), -.5f))]));
        var pose = Assert.IsType<Mmd.Modern.Animation.MorphPose>(adapter.Evaluate(new Dictionary<int, float> { [0] = .5f }));
        // Original BD770 uses acos(-.5) - 3.14, rather than a full-precision pi or shortest-path slerp.
        float angle = MathF.Acos(-.5f) - 3.14f;
        var actual = Vector3.Transform(Vector3.UnitX, pose.BoneRotations[0]);
        Assert.InRange(Vector3.Distance(new(MathF.Cos(angle), MathF.Sin(angle), 0), actual), 0, 1e-6f);
        Assert.True(Vector3.Distance(new(.5f, -MathF.Sqrt(.75f), 0), actual) > .001f);
    }

    [Fact]
    public void NearIdentityRawQuaternionUsesTheOriginalZeroHalfAngleBranchAtLargeWeight()
    {
        var adapter = new PmxMorphAdapter(Model([Bone("tiny", Vector3.Zero, new(0, 0, 1e-5f, 1))]));
        var pose = Assert.IsType<Mmd.Modern.Animation.MorphPose>(adapter.Evaluate(new Dictionary<int, float> { [0] = 100_000 }));
        Assert.Equal(Quaternion.Identity, pose.BoneRotations[0]);
    }
}
