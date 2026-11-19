using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Animation;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;
using static Mmd.Modern.Tests.MorphApplicationTests;

namespace Mmd.Modern.Tests;

public sealed class MorphZeroUvChannelTests
{
    [Fact]
    public void PartialPositionRangeRetainsOriginalIndicesAcrossConcurrentDenseReads()
    {
        Vector3[] values = [new(2, -1, 0), new(-0f, 0, 0)];
        var pose = MorphPose.FromOwnedPositionRange(values, 3, [], [], new(8));
        var expected = new Vector3[8];
        values.CopyTo(expected, 3);
        Assert.Equal(3, pose.FirstVertex);
        Assert.True(pose.PositionRange.Overlaps(values));
        Parallel.For(0, 16, i =>
        {
            if (i % 2 == 0) Assert.Equal(Bytes(expected), Bytes(pose.VertexDeltas));
            Assert.True(MemoryMarshal.AsBytes(pose.PositionRange).SequenceEqual(MemoryMarshal.AsBytes(values.AsSpan())));
        });
        Assert.Same(pose.VertexDeltas, pose.VertexDeltas);
        Assert.Equal(Bytes(expected), MemoryMarshal.AsBytes(pose.VertexDeltaSpan).ToArray());
        Assert.Throws<ArgumentException>(() => MorphPose.FromOwnedPositionRange([new(float.NaN)], 3, [], [], new(8)));
    }

    [Fact]
    public void AdapterStoresOnlyTheTouchedPositionRangeAndDenseViewKeepsOriginalIndices()
    {
        var adapter = new PmxMorphAdapter(Model([new() { Name = "tail", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Position,
            PositionOffsets = [new(2, new(.5f, -.25f, 1)), new(1, new(-0f, 0, 0))] }]));
        var pose = Pose(adapter, new() { [0] = .75f });
        Assert.Equal(1, pose.FirstVertex);
        Assert.Equal(2, pose.PositionRange.Length);
        Assert.True(pose.HasVertexEffects);
        Assert.Equal(3, pose.VertexDeltas.Count);
        Assert.Equal(Vector3.Zero, pose.VertexDeltas[0]);
        Assert.Equal(new Vector3(.375f, -.1875f, .75f), pose.VertexDeltas[2]);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public void CompactPositionRangeSkinsAndDefersExactlyLikeTheDenseEquivalent(int degree, bool defer)
    {
        const int count = 40_000, first = 4090;
        var bones = new[] { new RigBone("a", new(1, 2, 3), -1), new RigBone("b", new(-2, 1, 4), -1) };
        var vertices = Enumerable.Range(0, count).Select(i => new SceneVertex(new(i * .001f, 1 - i * .0005f, .25f),
            Vector3.Normalize(new(.3f, 1, i % 7)), new(i * 1e-5f, .5f)));
        var bindings = Enumerable.Range(0, count).Select(i => new SkinBinding(SkinningKind.Bdef2, i % 2, 1 - i % 2, -1, -1, new(.625f, .375f, 0, 0)));
        var rig = new CharacterRig(bones, vertices, bindings);
        Matrix4x4[] worlds = [Matrix4x4.CreateRotationY(.3f) * Matrix4x4.CreateTranslation(1, 2, 3),
            Matrix4x4.CreateRotationX(-.4f) * Matrix4x4.CreateTranslation(-2, .5f, 1)];
        var range = new Vector3[count - first];
        range[0] = new(.5f, -.25f, 1); range[5] = new(-0f, 0, -0f); range[6] = new(.125f, .75f, -.5f); range[^1] = new(-1, 2, .25f);
        var dense = new Vector3[count]; range.CopyTo(dense, first);
        Quaternion[] identity = [Quaternion.Identity, Quaternion.Identity];
        var compact = MorphPose.FromOwnedPositionRange(range, first, new Vector3[2], identity.ToArray(), new(count));
        var reference = new MorphPose(dense, new Vector3[2], identity);

        var expected = Sample(reference);
        var actual = Sample(compact);   // compact storage, never expanded yet
        Assert.Equal(count - first, compact.PositionRange.Length);
        Assert.Equal(VertexBytes(expected), VertexBytes(actual));
        _ = compact.VertexDeltas;       // expansion publishes the full array; skinning must still match
        Assert.Equal(VertexBytes(expected), VertexBytes(Sample(compact)));
        Assert.Equal(count - first, compact.PositionRange.Length);

        CharacterSample Sample(MorphPose morphs)
        {
            var animator = new CharacterAnimator(rig, new([]), degree) { DeferVertexSkinning = defer };
            var sample = animator.SkinWorldPose(worlds, morphs);
            if (defer)
            {
                var deltas = Assert.IsType<SceneMorphDeltas>(sample.Deformation!.Morphs);
                Assert.Equal(count, deltas.VertexCount);
                Assert.Equal(new[] { first, first + 6, count - 1 }, deltas.Indices.ToArray());
            }
            return sample;
        }
    }

    [Fact]
    public void PositionBoneAndGroupsShareOnlyTheImmutableZeroUvChannelAcrossUvTransitions()
    {
        PmxMorph.PositionOffset[] positions = [new(0, new(.25f, -.5f, 0))];
        PmxMorph.BoneOffset[] bones = [new(0, new(.125f, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .2f))];
        PmxMorph.UvOffset[] uvs = [new(0, new(.5f, -.25f, 9, 8))];
        var model = Model([
            new() { Name = "position", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Position, PositionOffsets = positions },
            new() { Name = "bone", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Bone, BoneOffsets = bones },
            Uv("uv", uvs), Group("position-bone", new(0, 1), new(1, 1)), Group("all", new(3, 1), new(2, 1))]);
        var adapter = new PmxMorphAdapter(model);
        var first = Pose(adapter, new() { [0] = 1 });
        var grouped = Pose(adapter, new() { [3] = .5f });
        var active = Pose(adapter, new() { [4] = -1 });
        var last = Pose(adapter, new() { [3] = 2, [2] = 0 });

        Assert.Same(first.VertexUvDeltas, grouped.VertexUvDeltas);
        Assert.Same(first.VertexUvDeltas, last.VertexUvDeltas);
        Assert.NotSame(first.VertexUvDeltas, active.VertexUvDeltas);
        Assert.NotSame(first.VertexDeltas, grouped.VertexDeltas);
        Assert.NotSame(grouped.BoneTranslations, last.BoneTranslations);
        Assert.Equal(new Vector3(.25f, -.5f, 0), first.VertexDeltas[0]);
        Assert.Equal(new Vector3(.125f, -.25f, 0), grouped.VertexDeltas[0]);
        Assert.Equal(new Vector3(.0625f, 0, 0), grouped.BoneTranslations[0]);
        Assert.Equal(new Vector3(.25f, 0, 0), last.BoneTranslations[0]);
        Assert.False(first.HasBoneEffects); Assert.True(grouped.HasBoneEffects);
        Assert.False(first.HasUvEffects); Assert.False(last.HasUvEffects); Assert.True(active.HasUvEffects);
        Assert.Equal(new Vector2(-.5f, .25f), active.VertexUvDeltas[0]);
        Assert.Equal(new byte[model.Vertices.Count * 8], Bytes(first.VertexUvDeltas));
        Assert.Throws<NotSupportedException>(() => ((IList<Vector2>)first.VertexUvDeltas)[0] = Vector2.One);

        // Import arrays belong to the caller; later mutation cannot alter the copied definitions or old poses.
        positions[0] = new(0, new(99)); bones[0] = new(0, new(99), Quaternion.Identity); uvs[0] = new(0, new(99));
        var repeated = Pose(adapter, new() { [4] = -1 });
        Assert.Equal(active.VertexDeltas, repeated.VertexDeltas);
        Assert.Equal(active.BoneTranslations, repeated.BoneTranslations);
        Assert.Equal(active.BoneRotations, repeated.BoneRotations);
        Assert.Equal(Bytes(active.VertexUvDeltas), Bytes(repeated.VertexUvDeltas));
        Assert.Equal(new Vector3(.25f, -.5f, 0), first.VertexDeltas[0]);
        Assert.NotSame(first.VertexUvDeltas, Pose(new PmxMorphAdapter(model), new() { [0] = 1 }).VertexUvDeltas);
    }

    [Fact]
    public void ActiveUvOffsetsKeepLegacyAccumulationBytesEvenWhenTheirResultIsZero()
    {
        PmxMorph.UvOffset[] first = [new(0, new(.25f, -.5f, 100, -100)), new(0, new(.5f, .25f, 2, 3))];
        PmxMorph.UvOffset[] second = [new(0, new(1, .5f, 4, 5))];
        var adapter = new PmxMorphAdapter(Model([Position("position", Vector3.UnitY), Uv("a", first), Uv("b", second),
            Uv("zw-only", new PmxMorph.UvOffset(0, new(-0f, 0, 5, 6)))]));
        var expected = new Vector2[3];
        foreach (var offset in first) expected[offset.VertexIndex] += new Vector2(offset.Uv.X, offset.Uv.Y) * -1;
        foreach (var offset in second) expected[offset.VertexIndex] += new Vector2(offset.Uv.X, offset.Uv.Y) * 2;
        var actual = Pose(adapter, new() { [1] = -1, [2] = 2 });
        Assert.Equal(Bytes(expected), Bytes(actual.VertexUvDeltas));
        var zero = Pose(adapter, new() { [0] = 1 });
        var zwOnly = Pose(adapter, new() { [3] = -1 });
        Assert.Same(zwOnly, Pose(adapter, new() { [3] = -1 }));
        // A complete validated-pose cache hit may share its active UV result. After
        // another pose replaces that entry, the active zero-result channel is still
        // accumulated into fresh storage rather than treated as an absent channel.
        Pose(adapter, new() { [0] = 1 });
        var zwAgain = Pose(adapter, new() { [3] = -1 });
        Assert.False(zwOnly.HasUvEffects);
        Assert.NotSame(zero.VertexUvDeltas, zwOnly.VertexUvDeltas);
        Assert.NotSame(zwOnly.VertexUvDeltas, zwAgain.VertexUvDeltas);
        expected = new Vector2[3]; expected[0] += new Vector2(-0f, 0) * -1;
        Assert.Equal(Bytes(expected), Bytes(zwOnly.VertexUvDeltas));
        Assert.Equal(new byte[24], Bytes(zero.VertexUvDeltas));
    }

    [Fact]
    public void PublicCopiesAndOwnedArrayTransfersKeepTheirExistingOwnershipAndNormalization()
    {
        Vector3[] vertices = [Vector3.UnitX]; Vector3[] translations = [Vector3.UnitY];
        Quaternion[] rotations = [new(0, 0, 2, 2)]; Vector2[] uvs = [new(-0f, .25f)];
        var copied = new MorphPose(vertices, translations, rotations, uvs);
        Assert.Equal(new Quaternion(0, 0, 2, 2), rotations[0]);
        vertices[0] = new(99); translations[0] = new(99); rotations[0] = Quaternion.Identity; uvs[0] = new(99);
        Assert.Equal(Vector3.UnitX, copied.VertexDeltas[0]); Assert.Equal(Vector3.UnitY, copied.BoneTranslations[0]);
        Assert.Equal(Bytes(new Vector2[] { new(-0f, .25f) }), Bytes(copied.VertexUvDeltas));
        Assert.NotEqual(Quaternion.Identity, copied.BoneRotations[0]);

        vertices = [Vector3.UnitX]; translations = [Vector3.UnitY]; rotations = [new(0, 0, 2, 2)]; uvs = [new(.5f)];
        var owned = MorphPose.FromOwnedArrays(vertices, translations, rotations, uvs);
        Assert.True(owned.VertexDeltaSpan.Overlaps(vertices)); Assert.True(owned.VertexUvDeltaSpan.Overlaps(uvs));
        Assert.Equal(rotations[0], owned.BoneRotations[0]); Assert.Equal(copied.BoneRotations[0], owned.BoneRotations[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<Vector2>)owned.VertexUvDeltas)[0] = Vector2.Zero);
    }

    [Fact]
    public void ZeroOwnerPreservesOtherChannelValidationOrderAndNormalizesOwnedRotations()
    {
        var zero = new MorphPose.ImmutableZeroUvChannel(1);
        var wrongCount = Assert.Throws<ArgumentException>(() => MorphPose.FromOwnedArraysWithZeroUvs([], [], [default], zero));
        Assert.Equal("vertexUvDeltas", wrongCount.ParamName);
        var wrongBones = Assert.Throws<ArgumentException>(() => MorphPose.FromOwnedArraysWithZeroUvs([new(float.NaN)], [], [default], zero));
        Assert.StartsWith("Morph bone translation and rotation counts differ.", wrongBones.Message);
        var invalidVertex = Assert.Throws<ArgumentException>(() => MorphPose.FromOwnedArraysWithZeroUvs([new(float.NaN)], [new(float.NaN)], [default], zero));
        Assert.Equal("vertexDeltas", invalidVertex.ParamName);
        var invalidBone = Assert.Throws<ArgumentException>(() => MorphPose.FromOwnedArraysWithZeroUvs([Vector3.Zero], [new(float.NaN)], [default], zero));
        Assert.Equal("boneTranslations", invalidBone.ParamName);
        var invalidRotation = Assert.Throws<ArgumentException>(() => MorphPose.FromOwnedArraysWithZeroUvs([Vector3.Zero], [Vector3.Zero], [default], zero));
        Assert.Equal("boneRotations", invalidRotation.ParamName);
        Quaternion[] source = [new(0, 0, 2, 2)];
        var actual = MorphPose.FromOwnedArraysWithZeroUvs([Vector3.Zero], [Vector3.Zero], source, zero);
        var expected = new MorphPose([Vector3.Zero], [Vector3.Zero], [new(0, 0, 2, 2)]);
        Assert.Equal(expected.BoneRotations, actual.BoneRotations);
        Assert.Equal(source[0], actual.BoneRotations[0]); Assert.False(actual.HasUvEffects);
    }

    [Fact]
    public void AllWeightAndExpansionValidationStillPrecedesPoseConstruction()
    {
        var adapter = new PmxMorphAdapter(Model([Position("overflow-position", new(float.MaxValue)),
            Group("overflow-group", new PmxMorph.GroupOffset(0, float.MaxValue)), Uv("uv", new PmxMorph.UvOffset(0, Vector4.One))]));
        var nonfinite = Assert.Throws<ArgumentException>(() => adapter.Evaluate(new Dictionary<int, float>
            { [0] = 2, [1] = float.MaxValue, [99] = float.NaN }));
        Assert.Equal("weights", nonfinite.ParamName); Assert.StartsWith("Morph weights must be finite.", nonfinite.Message);
        var expanded = Assert.Throws<ArgumentException>(() => adapter.Evaluate(new Dictionary<int, float> { [0] = 2, [1] = float.MaxValue }));
        Assert.Equal("weights", expanded.ParamName); Assert.StartsWith("PMX Group morph expansion produced a non-finite weight.", expanded.Message);
        var position = Assert.Throws<ArgumentException>(() => adapter.Evaluate(new Dictionary<int, float> { [0] = 2 }));
        Assert.Equal("vertexDeltas", position.ParamName);
        var valid = Pose(adapter, new() { [0] = 1 });
        Assert.False(valid.HasUvEffects); Assert.Equal(new byte[24], Bytes(valid.VertexUvDeltas));
        Assert.Null(adapter.Evaluate(new Dictionary<int, float> { [2] = 0 }));
    }

    private static byte[] VertexBytes(CharacterSample sample) => MemoryMarshal.AsBytes(sample.Vertices.AsSpan()).ToArray();

    private static MorphPose Pose(PmxMorphAdapter adapter, Dictionary<int, float> weights)
        => Assert.IsType<MorphPose>(adapter.Evaluate(weights));

    private static byte[] Bytes<T>(IReadOnlyList<T> values) where T : unmanaged
        => MemoryMarshal.AsBytes(values.ToArray().AsSpan()).ToArray();

    private static PmxMorph Uv(string name, params PmxMorph.UvOffset[] offsets) => new()
        { Name = name, EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Uv, UvOffsets = offsets };

    private static PmxMorph Group(string name, params PmxMorph.GroupOffset[] offsets) => new()
        { Name = name, EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Group, GroupOffsets = offsets };
}
