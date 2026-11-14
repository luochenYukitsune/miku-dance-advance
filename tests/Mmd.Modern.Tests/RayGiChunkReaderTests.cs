using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class RayGiChunkReaderTests
{
    private const int Chunk = 4096;
    private const int VertexCount = Chunk * 3 + 7;
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly float NegativeZero = BitConverter.Int32BitsToSingle(int.MinValue);
    private static readonly float Subnormal = BitConverter.Int32BitsToSingle(1);
    private static readonly float NegativeSubnormal = BitConverter.Int32BitsToSingle(unchecked((int)0x80000001));

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(16)]
    public void ColdCrossChunkIndicesPreserveFrozenFlatTriangleBytes(int workers)
    {
        var source = Scene(32771);
        var before = VertexBytes(source);
        // The oracle may flatten its independent snapshot, never the input under test.
        var expected = RayGiParallelGeometryTests.Legacy(FlatCopy(source));
        Assert.False(IsMaterialized(source));
        Assert.Equal(4, source.VertexChunkCount);
        Assert.Equal(7, source.GetVertexChunk(3).Length);
        Assert.Equal(int.MinValue, BitConverter.SingleToInt32Bits(source.GetVertex(0).Uv.X));
        Assert.Equal(1, BitConverter.SingleToInt32Bits(source.GetVertex(0).Uv.Y));
        Assert.Equal(unchecked((int)0x80000001), BitConverter.SingleToInt32Bits(source.GetVertex(0).Normal.Z));

        var actual = RayGiParallelGeometryTests.Build(source, workers);

        EqualBytes(expected.AsSpan(), actual.AsSpan());
        Assert.Equal(before, VertexBytes(source));
        Assert.False(IsMaterialized(source));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void FullSubTextureAdaptationPreservesAllUv1RowsWithoutFlattening(int channels)
    {
        var source = Scene(32771, channels);
        var verticesBefore = VertexBytes(source);
        var uvs = source.AdditionalUvs!;
        var uvsBefore = MemoryMarshal.AsBytes(uvs.Vertices).ToArray();
        var expectedTriangles = RayGiParallelGeometryTests.Legacy(FlatCopy(source));
        var expectedUvs = FrozenUv1Rows(source.Indices, uvs);
        var bindings = source.Materials.Select(_ =>
            new OriginalStyleMaterialBinding(Vector3.One, 1, OriginalSphereMode.SubTexture)
            { SubTextureAdditive = true }).ToArray();
        var style = new OriginalStyleScene(source, bindings);
        var frame = new OriginalFrameScene(style, options: new(OriginalAlphaBlendMode.Normal, true, false,
            SphereRotationOverride: Matrix4x4.CreateRotationY(.73f)));
        Assert.False(IsMaterialized(source));

        var fromStyle = RayGiScene.FromOriginal(style);
        var fromFrame = RayGiScene.FromOriginal(frame);

        Assert.Equal(source.Indices.Length / 3, fromStyle.SubTextureUvs.Length);
        Assert.Equal(source.Indices.Length / 3, fromFrame.SubTextureUvs.Length);
        EqualBytes(expectedTriangles.AsSpan(), fromStyle.Triangles);
        EqualBytes(expectedTriangles.AsSpan(), fromFrame.Triangles);
        EqualBytes(expectedUvs.AsSpan(), fromStyle.SubTextureUvs);
        EqualBytes(expectedUvs.AsSpan(), fromFrame.SubTextureUvs);
        Assert.Equal(fromStyle.SubTextureUvIdentity, fromFrame.SubTextureUvIdentity);
        Assert.Equal(uvsBefore, MemoryMarshal.AsBytes(uvs.Vertices).ToArray());
        Assert.Equal(verticesBefore, VertexBytes(source));
        Assert.False(IsMaterialized(source));
    }

    [Fact]
    public void LaterAndBranchedPosesCannotChangeOldSnapshotsOrPublishedTriangleBytes()
    {
        var first = Scene(32771);
        var firstVertices = VertexBytes(first);
        var firstOutput = RayGiParallelGeometryTests.Build(first, 1);
        var firstOutputBytes = MemoryMarshal.AsBytes(firstOutput.AsSpan()).ToArray();
        var later = Patch(first, [(Chunk - 1, 3), (VertexCount - 1, 1)], .125f);
        var branch = Patch(first, [(Chunk * 2 - 1, 4)], -.25f);
        var laterVertices = VertexBytes(later);
        var branchVertices = VertexBytes(branch);

        var laterOutput = RayGiParallelGeometryTests.Build(later, 8);
        var branchOutput = RayGiParallelGeometryTests.Build(branch, 16);

        EqualBytes(RayGiParallelGeometryTests.Legacy(FlatCopy(later)).AsSpan(), laterOutput.AsSpan());
        EqualBytes(RayGiParallelGeometryTests.Legacy(FlatCopy(branch)).AsSpan(), branchOutput.AsSpan());
        Assert.NotSame(firstOutput, laterOutput);
        Assert.NotSame(firstOutput, branchOutput);
        Assert.NotSame(laterOutput, branchOutput);
        Assert.Equal(firstVertices, VertexBytes(first));
        Assert.Equal(laterVertices, VertexBytes(later));
        Assert.Equal(branchVertices, VertexBytes(branch));
        Assert.Equal(firstOutputBytes, MemoryMarshal.AsBytes(firstOutput.AsSpan()).ToArray());
        Assert.False(IsMaterialized(first));
        Assert.False(IsMaterialized(later));
        Assert.False(IsMaterialized(branch));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(16)]
    public void ColdCrossChunkOverflowKeepsTheOriginalArgumentException(int workers)
    {
        var source = Scene(32771);
        source = Patch(source, [(VertexCount - 1, 1)], 0, overflow: true)
            .WithWorld(Matrix4x4.CreateScale(2));
        var before = VertexBytes(source);
        Assert.False(IsMaterialized(source));

        var error = Assert.Throws<ArgumentException>(() => RayGiParallelGeometryTests.Build(source, workers));

        Assert.Equal("Transformed GI positions and normals must remain finite.", error.Message);
        Assert.Equal(before, VertexBytes(source));
        Assert.False(IsMaterialized(source));
    }

    [Fact]
    public void MissingUv1StillRejectsBeforeTransportCanMaterializeTheSnapshot()
    {
        var source = Scene(32771);
        var bindings = source.Materials.Select(_ =>
            new OriginalStyleMaterialBinding(Vector3.One, 1, OriginalSphereMode.SubTexture)).ToArray();

        var error = Assert.Throws<ArgumentException>(() => new OriginalStyleScene(source, bindings));

        Assert.Equal("SubTexture requires an explicit vertex-order UV1 stream.", error.Message);
        Assert.False(IsMaterialized(source));
    }

    private static SceneSnapshot Scene(int triangleCount, int channels = 0)
    {
        var vertices = Enumerable.Range(0, VertexCount).Select(index => new SceneVertex(
            new(index * .001f, index % 11 - 5, index % 7 + .125f),
            new(index % 5 + .25f, index % 13 - 6, index % 3 + .5f),
            new(index % 17 * .0625f, index % 19 * -.03125f))).ToArray();
        vertices[0] = new(new(NegativeZero, Subnormal, NegativeSubnormal),
            new(NegativeZero, 2, NegativeSubnormal), new(NegativeZero, Subnormal));
        vertices[Chunk - 1] = new(new(1, NegativeZero, -.125f),
            new(Subnormal, NegativeZero, -2), new(NegativeSubnormal, NegativeZero));
        vertices[Chunk] = new(new(-3, Subnormal, NegativeZero),
            new(2, NegativeSubnormal, 1), new(.75f, NegativeSubnormal));
        vertices[Chunk * 2] = new(new(Subnormal, -4, 2),
            new(-1, 3, NegativeZero), new(NegativeZero, -.5f));
        vertices[^1] = new(new(-2, 3, 4),
            new(NegativeSubnormal, 0, 4), new(Subnormal, NegativeZero));

        // Includes two/three-chunk triangles, reversed winding, repeated vertices and the short tail.
        uint[] pattern =
        [
            0, Chunk - 1, Chunk,
            Chunk * 2 - 1, Chunk * 2, VertexCount - 1,
            VertexCount - 1, Chunk, 0,
            Chunk, Chunk - 1, 0,
            0, Chunk, Chunk * 2,
            VertexCount - 1, VertexCount - 1, VertexCount - 1,
            Chunk * 2, Chunk, 0,
            VertexCount - 2, Chunk * 3, Chunk * 2 - 1
        ];
        var indices = Enumerable.Range(0, triangleCount * 3).Select(index => pattern[index % pattern.Length]).ToArray();
        int firstEnd = Math.Min(4101, triangleCount) * 3;
        int secondEnd = Math.Min(32760, triangleCount) * 3;
        SceneMaterial[] materials =
        [
            new("empty", 0, 0, Vector4.One, Vector3.Zero, Vector3.Zero, 0, DiffuseTexture: 0),
            new("first", 0, firstEnd, Vector4.One, Vector3.Zero, Vector3.Zero, 0, DiffuseTexture: 0),
            new("empty-middle", firstEnd, 0, Vector4.One, Vector3.Zero, Vector3.Zero, 0, DiffuseTexture: 0),
            new("middle", firstEnd, secondEnd - firstEnd, Vector4.One, Vector3.Zero, Vector3.Zero, 0, DiffuseTexture: 0),
            new("tail", secondEnd, indices.Length - secondEnd, Vector4.One, Vector3.Zero, Vector3.Zero, 0, DiffuseTexture: 0),
            new("empty-tail", indices.Length, 0, Vector4.One, Vector3.Zero, Vector3.Zero, 0, DiffuseTexture: 0)
        ];
        SceneTexture[] textures =
        [
            new("diffuse", 2, 1, [9, 17, 33, 255, 71, 65, 129, 128]),
            new("sphere", 2, 1, [255, 255, 255, 64, 255, 255, 255, 255])
        ];
        SceneAdditionalUvs? uvs = channels == 0 ? null : new(channels,
            Enumerable.Range(0, VertexCount).Select(index => new SceneAdditionalUvVertex(
                new(index == 0 ? NegativeZero : index * .003f, index == 0 ? Subnormal : index % 23 * -.03125f, 99 + index, -77 - index),
                new(index + 31, -42, Subnormal, NegativeZero), new(-53, index + 64, 75, 86), new(97, 108, -119, index))).ToArray());
        var world = Matrix4x4.CreateScale(2, 3, -4) * Matrix4x4.CreateRotationY(.37f)
            * Matrix4x4.CreateTranslation(13, -6, 29);
        var root = new SceneSnapshot(vertices, indices, materials, textures, world: world, additionalUvs: uvs);
        // A composed owner has no existing complete private vertex array to lend.
        return Patch(root, [(Chunk - 1, 3), (Chunk * 2 - 1, 3), (VertexCount - 2, 2)], .0625f);
    }

    private static SceneSnapshot Patch(SceneSnapshot source, (int Start, int Count)[] ranges, float offset, bool overflow = false)
    {
        var method = typeof(SceneSnapshot).GetMethod("WithComposedVertices", PrivateInstance)!;
        var writer = new PatchWriter(offset, overflow);
        var callback = Delegate.CreateDelegate(method.GetParameters()[1].ParameterType, writer,
            typeof(PatchWriter).GetMethod(nameof(PatchWriter.Write))!);
        return (SceneSnapshot)method.Invoke(source, [ranges, callback])!;
    }

    private sealed class PatchWriter(float offset, bool overflow)
    {
        public void Write(int _, Span<SceneVertex> destination)
        {
            for (int index = 0; index < destination.Length; index++)
                destination[index] = destination[index] with
                {
                    Position = overflow ? new(float.MaxValue, 0, 0)
                        : destination[index].Position + new Vector3(offset, 0, 0)
                };
        }
    }

    private static SceneSnapshot FlatCopy(SceneSnapshot source)
    {
        var vertices = new SceneVertex[source.VertexCount];
        source.CopyVertices(0, vertices);
        return new(vertices, source.Indices, source.Materials, source.Textures, world: source.World,
            additionalUvs: source.AdditionalUvs);
    }

    private static byte[] VertexBytes(SceneSnapshot source)
    {
        var vertices = new SceneVertex[source.VertexCount];
        source.CopyVertices(0, vertices);
        return MemoryMarshal.AsBytes(vertices.AsSpan()).ToArray();
    }

    private static bool IsMaterialized(SceneSnapshot source)
    {
        var storage = typeof(SceneSnapshot).GetField("vertexStorage", PrivateInstance)!.GetValue(source)!;
        return (bool)storage.GetType().GetProperty("IsMaterialized", PrivateInstance)!.GetValue(storage)!;
    }

    // Frozen pre-reader UV1 contract: independent vertex-order side stream, triangle-order rows.
    private static RayGiSubTextureUvGpu[] FrozenUv1Rows(ReadOnlySpan<uint> indices, SceneAdditionalUvs uvs)
    {
        var rows = new RayGiSubTextureUvGpu[indices.Length / 3];
        for (int index = 0; index < indices.Length; index += 3)
        {
            var a = uvs.Vertices[(int)indices[index]].Uv1;
            var b = uvs.Vertices[(int)indices[index + 1]].Uv1;
            var c = uvs.Vertices[(int)indices[index + 2]].Uv1;
            rows[index / 3] = new(new(a.X, a.Y, b.X, b.Y), new(c.X, c.Y, 0, 0));
        }
        return rows;
    }

    private static void EqualBytes<T>(ReadOnlySpan<T> expected, ReadOnlySpan<T> actual) where T : unmanaged
        => Assert.True(MemoryMarshal.AsBytes(expected).SequenceEqual(MemoryMarshal.AsBytes(actual)));
}
