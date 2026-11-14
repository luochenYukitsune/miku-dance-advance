using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Animation;
using Mmd.Modern.Application;
using Mmd.Modern.Formats;
using Xunit;
using static Mmd.Modern.Tests.MorphApplicationTests;

namespace Mmd.Modern.Tests;

public sealed class PmxMorphPoseCacheTests
{
    [Fact]
    public void EqualExpandedContributionsReuseTheCompleteImmutablePoseAcrossDifferentInputDictionaries()
    {
        PmxMorph.PositionOffset[] positions = [new(0, new(.25f, -.5f, -0f))];
        PmxMorph.BoneOffset[] bones = [new(0, new(.125f, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .2f))];
        PmxMorph.UvOffset[] uvs = [new(0, new(.5f, -.25f, 9, 8))];
        PmxMorph.GroupOffset[] offsets = [new(0, .5f), new(1, 2), new(2, -1)];
        var adapter = new PmxMorphAdapter(Model([
            new() { Name = "position", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Position, PositionOffsets = positions },
            new() { Name = "bone", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Bone, BoneOffsets = bones },
            Uv("uv", uvs), Group("all", offsets), Unsupported("material")]));
        var first = Pose(adapter, new() { [3] = 1 });
        byte[] original = Bytes(first);
        var equal = Pose(adapter, new() { [2] = -1, [1] = 2, [0] = .5f, [4] = 42, [99] = -13 });
        Assert.Same(first, equal);
        Assert.Same(first.VertexDeltas, equal.VertexDeltas);
        Assert.Same(first.BoneTranslations, equal.BoneTranslations);
        Assert.Same(first.BoneRotations, equal.BoneRotations);
        Assert.Same(first.VertexUvDeltas, equal.VertexUvDeltas);

        // Caller-owned import arrays and groups cannot change either the frozen definitions or cached pose.
        positions[0] = new(0, new(99)); bones[0] = new(0, new(99), Quaternion.Identity);
        uvs[0] = new(0, new(99)); offsets[0] = new(2, 99);
        Assert.Same(first, Pose(adapter, new() { [3] = 1 }));
        Assert.Equal(original, Bytes(first));
        Assert.Throws<NotSupportedException>(() => ((IList<Vector3>)first.VertexDeltas)[0] = Vector3.One);
        Assert.Throws<NotSupportedException>(() => ((IList<Vector3>)first.BoneTranslations)[0] = Vector3.One);
        Assert.Throws<NotSupportedException>(() => ((IList<Quaternion>)first.BoneRotations)[0] = Quaternion.Identity);
        Assert.Throws<NotSupportedException>(() => ((IList<Vector2>)first.VertexUvDeltas)[0] = Vector2.One);
        var changed = Pose(adapter, new() { [0] = 1 });
        Assert.Equal(new Vector3(.25f, -.5f, 0), changed.VertexDeltas[0]);
        Assert.Equal(original, Bytes(first));
    }

    [Fact]
    public void MutatingTheSameWeightDictionaryReplacesOnlyTheLastEntryAndLeavesEarlierPoseBytesUnchanged()
    {
        var adapter = new PmxMorphAdapter(Model([Position("position", Vector3.UnitY)]));
        var weights = new Dictionary<int, float> { [0] = 1 };
        var first = Pose(adapter, weights); byte[] original = Bytes(first);
        Assert.Same(first, Pose(adapter, weights));
        weights[0] = MathF.BitIncrement(1);
        var changed = Pose(adapter, weights);
        Assert.NotSame(first, changed);
        Assert.Equal(new Vector3(0, weights[0], 0), changed.VertexDeltas[0]);
        Assert.Equal(original, Bytes(first));
        weights[0] = 1;
        var returned = Pose(adapter, weights);
        Assert.NotSame(first, returned); Assert.NotSame(changed, returned);
        Assert.Equal(original, Bytes(returned)); Assert.Equal(original, Bytes(first));
        Assert.Same(returned, Pose(adapter, weights));
    }

    [Fact]
    public void PathOrderAndSeparateCancellingContributionsArePartOfTheCacheIdentity()
    {
        var model = Model([
            Bone("x", Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitX, .9f)),
            Bone("y", Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, .7f)),
            Position("position", Vector3.UnitX),
            Group("xy", new(0, .5f), new(1, .75f)), Group("yx", new(1, .75f), new(0, .5f)),
            Group("cancel", new(2, 1), new(2, -1)), Group("cancel-twice", new(2, 2), new(2, -2))]);
        var adapter = new PmxMorphAdapter(model);
        var xy = Pose(adapter, new() { [3] = 1 });
        Assert.Same(xy, Pose(adapter, new() { [0] = .5f, [1] = .75f }));
        var yx = Pose(adapter, new() { [4] = 1 });
        Assert.NotSame(xy, yx); Assert.NotEqual(xy.BoneRotations[0], yx.BoneRotations[0]);
        Assert.Equal(Bytes(Pose(new PmxMorphAdapter(model), new() { [4] = 1 })), Bytes(yx));

        var cancelled = Pose(adapter, new() { [5] = 1 });
        Assert.Equal(Vector3.Zero, cancelled.VertexDeltas[0]);
        Assert.Same(cancelled, Pose(adapter, new() { [5] = 1 }));
        var twice = Pose(adapter, new() { [6] = 1 });
        Assert.NotSame(cancelled, twice); Assert.Equal(Bytes(cancelled), Bytes(twice));
        Assert.Null(adapter.Evaluate(new Dictionary<int, float> { [2] = 0 }));
    }

    [Fact]
    public void SignedZeroAndUnderflowRemainInactiveAndSignedOutputBytesMatchAnUncachedEvaluation()
    {
        var model = Model([
            Uv("uv", new PmxMorph.UvOffset(0, new(-0f, .25f, 5, 6))),
            Bone("bone", Vector3.Zero, new(-0f, .5f, -0f, MathF.Sqrt(.75f))),
            Group("underflow", new PmxMorph.GroupOffset(0, float.Epsilon))]);
        var adapter = new PmxMorphAdapter(model);
        var active = Pose(adapter, new() { [0] = -1, [1] = -.5f });
        byte[] original = Bytes(active);
        Assert.Same(active, Pose(adapter, new() { [0] = -1, [1] = -.5f }));
        Assert.Equal(Bytes(Pose(new PmxMorphAdapter(model), new() { [0] = -1, [1] = -.5f })), original);
        Assert.Null(adapter.Evaluate(new Dictionary<int, float> { [0] = 0f, [1] = -0f }));
        Assert.Null(adapter.Evaluate(new Dictionary<int, float> { [0] = -0f, [1] = 0f }));
        Assert.Null(adapter.Evaluate(new Dictionary<int, float> { [2] = float.Epsilon }));
        Assert.Null(adapter.Evaluate(new Dictionary<int, float> { [2] = -float.Epsilon }));
        Assert.Equal(original, Bytes(active));
    }

    [Fact]
    public void CacheHitsStillValidateEveryInputAndDerivedWeightAndFailuresDoNotReplaceTheSuccessfulEntry()
    {
        var adapter = new PmxMorphAdapter(Model([
            Position("valid", Vector3.UnitX), Unsupported("material"),
            Group("derived-overflow", new PmxMorph.GroupOffset(0, float.MaxValue)), Position("pose-overflow", new(float.MaxValue))]));
        var first = Pose(adapter, new() { [0] = 1 }); byte[] original = Bytes(first);
        foreach (int index in new[] { 1, 99, -1 })
        {
            var bad = Assert.Throws<ArgumentException>(() => adapter.Evaluate(new Dictionary<int, float> { [0] = 1, [index] = float.NaN }));
            Assert.Equal("weights", bad.ParamName); Assert.StartsWith("Morph weights must be finite.", bad.Message);
            Assert.Same(first, Pose(adapter, new() { [0] = 1 }));
        }
        var invalidFirst = Assert.Throws<ArgumentException>(() => adapter.Evaluate(new Dictionary<int, float>
            { [0] = 1, [2] = 2, [3] = 2, [99] = float.PositiveInfinity }));
        Assert.Equal("weights", invalidFirst.ParamName); Assert.StartsWith("Morph weights must be finite.", invalidFirst.Message);
        var derived = Assert.Throws<ArgumentException>(() => adapter.Evaluate(new Dictionary<int, float> { [0] = 1, [2] = 2, [3] = 2 }));
        Assert.Equal("weights", derived.ParamName); Assert.StartsWith("PMX Group morph expansion produced a non-finite weight.", derived.Message);
        Assert.Same(first, Pose(adapter, new() { [0] = 1 }));
        var pose = Assert.Throws<ArgumentException>(() => adapter.Evaluate(new Dictionary<int, float> { [3] = 2 }));
        Assert.Equal("vertexDeltas", pose.ParamName);
        Assert.Throws<ArgumentNullException>(() => adapter.Evaluate(null!));
        Assert.Same(first, Pose(adapter, new() { [0] = 1 })); Assert.Equal(original, Bytes(first));
    }

    [Theory]
    [InlineData(209_687, true)]
    [InlineData(209_688, false)]
    public void PoseRetentionHonorsTheFourMiBBudgetIncludingTheConservativeActiveUvChannel(int vertexCount, bool cached)
    {
        var source = Model([Position("position", Vector3.UnitY), Uv("uv", new PmxMorph.UvOffset(0, Vector4.One))]);
        var adapter = new PmxMorphAdapter(WithVertices(source, vertexCount));
        var first = Pose(adapter, new() { [0] = 1 });
        var again = Pose(adapter, new() { [0] = 1 });
        if (cached) Assert.Same(first, again); else Assert.NotSame(first, again);
        Assert.Equal(Vector3.UnitY, first.VertexDeltas[0]); Assert.Equal(Vector3.Zero, first.VertexDeltas[^1]);
        Assert.Equal(first.VertexDeltas.Count, again.VertexDeltas.Count);
        // The same boundary also covers active UV storage, rather than relying on the zero-UV owner.
        first = Pose(adapter, new() { [1] = 1 }); again = Pose(adapter, new() { [1] = 1 });
        if (cached) Assert.Same(first, again); else Assert.NotSame(first, again);
        Assert.Equal(Vector2.One, first.VertexUvDeltas[0]); Assert.Equal(Vector2.Zero, again.VertexUvDeltas[^1]);
    }

    [Fact]
    public void ContributionRetentionIsBoundedAndSuccessfulOversizeEvaluationClearsThePreviousEntry()
    {
        var model = Model([Position("leaf", Vector3.UnitX),
            Group("at-limit", Enumerable.Repeat(new PmxMorph.GroupOffset(0, 1), 65_536).ToArray()),
            Group("over-limit", Enumerable.Repeat(new PmxMorph.GroupOffset(0, 1), 65_537).ToArray())]);
        var adapter = new PmxMorphAdapter(model);
        var atLimit = Pose(adapter, new() { [1] = 1 });
        Assert.Same(atLimit, Pose(adapter, new() { [1] = 1 }));
        var overLimit = Pose(adapter, new() { [2] = 1 });
        Assert.Equal(new Vector3(65_537, 0, 0), overLimit.VertexDeltas[0]);
        var reloaded = Pose(adapter, new() { [1] = 1 });
        Assert.NotSame(atLimit, reloaded); Assert.Equal(Bytes(atLimit), Bytes(reloaded));
        overLimit = Pose(adapter, new() { [2] = 1 });
        var overAgain = Pose(adapter, new() { [2] = 1 });
        Assert.NotSame(overLimit, overAgain); Assert.Equal(Bytes(overLimit), Bytes(overAgain));
    }

    [Fact]
    public void ConcurrentPureReadsSeeCompleteImmutableEntriesAndMixedWeightsKeepTheirOwnResults()
    {
        var model = Model([Position("position", new(.25f, -.5f, 0)),
            Bone("bone", Vector3.UnitX, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .3f)),
            Uv("uv", new PmxMorph.UvOffset(0, new(.25f, -.5f, 0, 0)))]);
        var adapter = new PmxMorphAdapter(model);
        Dictionary<int, float>[] weights = [new() { [0] = 1, [1] = .5f, [2] = -.25f }, new() { [0] = -2, [1] = .75f, [2] = 1 }];
        var first = Pose(adapter, weights[0]); byte[] original = Bytes(first);
        Parallel.For(0, 128, _ => Assert.Same(first, Pose(adapter, weights[0])));
        byte[][] expected = weights.Select(values => Bytes(Pose(new PmxMorphAdapter(model), values))).ToArray();
        Parallel.For(0, 256, i => Assert.Equal(expected[i % 2], Bytes(Pose(adapter, weights[i % 2]))));
        Assert.Equal(original, Bytes(first));
    }

    private static MorphPose Pose(PmxMorphAdapter adapter, Dictionary<int, float> weights)
        => Assert.IsType<MorphPose>(adapter.Evaluate(weights));

    private static byte[] Bytes(MorphPose pose) => Bytes(pose.VertexDeltas).Concat(Bytes(pose.VertexUvDeltas))
        .Concat(Bytes(pose.BoneTranslations)).Concat(Bytes(pose.BoneRotations)).ToArray();
    private static byte[] Bytes<T>(IReadOnlyList<T> values) where T : struct
        => MemoryMarshal.AsBytes(values.ToArray().AsSpan()).ToArray();

    private static PmxMorph Uv(string name, params PmxMorph.UvOffset[] offsets) => new()
        { Name = name, EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Uv, UvOffsets = offsets };
    private static PmxMorph Group(string name, params PmxMorph.GroupOffset[] offsets) => new()
        { Name = name, EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Group, GroupOffsets = offsets };
    private static PmxMorph Unsupported(string name) => new()
        { Name = name, EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Material };

    private static PmxModel WithVertices(PmxModel source, int count) => new()
    {
        Header = source.Header, Name = source.Name, EnglishName = source.EnglishName, Comment = source.Comment, EnglishComment = source.EnglishComment,
        Vertices = Enumerable.Repeat(source.Vertices[0], count).ToArray(), Faces = source.Faces, Textures = source.Textures, Materials = source.Materials,
        Bones = source.Bones, Morphs = source.Morphs, DisplayFrames = source.DisplayFrames, RigidBodies = source.RigidBodies,
        Joints = source.Joints, SoftBodies = source.SoftBodies
    };
}
