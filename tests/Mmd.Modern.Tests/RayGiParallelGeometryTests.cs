using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class RayGiParallelGeometryTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(8, 8)]
    [InlineData(16, 16)]
    [InlineData(32, 16)]
    public void ProductionPolicyUsesAvailableProcessorsUpToTheRequestedSixteen(int processors, int expected)
    {
        var method = typeof(RayGiScene).GetMethod("TriangleConversionParallelism", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(expected, method.CreateDelegate<Func<int, int>>()(processors));
    }
    internal static RayGiTriangleGpu[] Build(SceneSnapshot source, int workers)
    {
        var method = typeof(RayGiScene).GetMethod("BuildTriangleRows", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method.CreateDelegate<Func<SceneSnapshot, int, RayGiTriangleGpu[]>>()(source, workers);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32767)]
    [InlineData(32768)]
    [InlineData(65539)]
    public void IndependentBlocksPreserveTheOriginalTriangleBytes(int count)
    {
        var source = Scene(count);
        var expected = Legacy(source);
        foreach (int workers in new[] { 1, 2, 4, 8, 16 })
            Assert.True(MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(Build(source, workers).AsSpan())));
    }

    [Fact]
    public void ConcurrentConversionsNeverShareWritableOutput()
    {
        var source = Scene(32771);
        var expected = Legacy(source);
        var results = new RayGiTriangleGpu[4][];
        Parallel.For(0, results.Length, i => results[i] = Build(source, 2));
        foreach (var result in results)
        {
            Assert.NotSame(expected, result);
            Assert.True(MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(result.AsSpan())));
        }
        Assert.NotSame(results[0], results[1]);
    }

    [Fact]
    public void RecordingScratchReusesExactRowsWithoutChangingImmutableTransport()
    {
        var source = Scene(32771);
        var vertices = source.Vertices.ToArray();
        var firstPose = source.WithDeformedVertices(vertices);
        vertices[0] = vertices[0] with { Position = vertices[0].Position + Vector3.UnitX, Uv = new(.25f, -.5f) };
        var secondPose = source.WithDeformedVertices(vertices);
        var firstFrame = Frame(firstPose);
        var secondFrame = Frame(secondPose);
        var expectedFirst = MemoryMarshal.AsBytes(Legacy(firstPose).AsSpan());
        var expectedSecond = MemoryMarshal.AsBytes(Legacy(secondPose).AsSpan());
        Assert.False(expectedFirst.SequenceEqual(expectedSecond));

        var forGpu = typeof(RayGiScene).GetMethod("FromOriginalForGpu", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<OriginalFrameScene, RayGiScene>>();
        var write = typeof(RayGiScene).GetMethod("WriteTriangleRows", BindingFlags.NonPublic | BindingFlags.Instance)!
            .CreateDelegate<Action<RayGiScene, RayGiTriangleGpu[]>>();
        var scratch = new D3D12RayGiTriangleScratch();
        var first = forGpu(firstFrame);
        var firstRows = scratch.GetBuffer(firstPose.Indices.Length / 3);
        Assert.NotNull(firstRows);
        write(first, firstRows);
        Assert.True(expectedFirst.SequenceEqual(MemoryMarshal.AsBytes(firstRows.AsSpan())));
        var firstImmutable = RayGiScene.FromOriginal(firstFrame);
        Assert.Equal(first.GeometryIdentity, firstImmutable.GeometryIdentity);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var second = forGpu(secondFrame);
        var secondRows = scratch.GetBuffer(secondPose.Indices.Length / 3);
        write(second, secondRows!);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Same(firstRows, secondRows);
        Assert.NotEqual(first.GeometryIdentity, second.GeometryIdentity);
        Assert.Equal(first.TexelIdentity, second.TexelIdentity);
        Assert.True(allocated < 512 * 1024, $"The second recording allocated {allocated} bytes on the calling thread.");
        Assert.True(expectedSecond.SequenceEqual(MemoryMarshal.AsBytes(secondRows.AsSpan())));
        Assert.True(expectedFirst.SequenceEqual(MemoryMarshal.AsBytes(firstImmutable.Triangles)));
        var secondImmutable = RayGiScene.FromOriginal(secondFrame);
        Assert.Equal(second.GeometryIdentity, secondImmutable.GeometryIdentity);
        Assert.True(expectedSecond.SequenceEqual(MemoryMarshal.AsBytes(secondImmutable.Triangles)));
    }

    [Fact]
    public void RecordingScratchReturnsExactSizeAfterGeometryChangesAndCapsRetention()
    {
        var scratch = new D3D12RayGiTriangleScratch();
        var first = scratch.GetBuffer(32771);
        Assert.NotNull(first);
        Assert.Equal(32771, first.Length);

        var smaller = scratch.GetBuffer(4097);
        Assert.NotNull(smaller);
        Assert.Equal(4097, smaller.Length);
        Assert.Same(smaller, scratch.GetBuffer(4097));

        int tooMany = 128 * 1024 * 1024 / Marshal.SizeOf<RayGiTriangleGpu>() + 1;
        Assert.Null(scratch.GetBuffer(tooMany));
        Assert.Null(scratch.GetBuffer(0));
    }

    [Fact]
    public void ColdChunkedInputPreservesTriangleBytesWithoutMaterializingItsCompatibilityView()
    {
        var source = Scene(32771);
        var vertices = source.Vertices.ToArray();
        vertices[0] = vertices[0] with { Position = vertices[0].Position + Vector3.UnitX };
        var expected = Legacy(new(vertices, source.Indices, source.Materials, world: source.World));
        var compose = typeof(SceneSnapshot).GetMethod("WithComposedVertices", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var writer = new PatchWriter();
        var callback = Delegate.CreateDelegate(compose.GetParameters()[1].ParameterType, writer, typeof(PatchWriter).GetMethod(nameof(PatchWriter.Write))!);
        var chunked = (SceneSnapshot)compose.Invoke(source, [new[] { (0, 1) }, callback])!;
        var storage = typeof(SceneSnapshot).GetField("vertexStorage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chunked)!;
        var materialized = storage.GetType().GetProperty("IsMaterialized", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        Assert.False((bool)materialized.GetValue(storage)!);
        var actual = Build(chunked, 4);
        Assert.False((bool)materialized.GetValue(storage)!);
        Assert.True(MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.AsSpan())));
    }

    private sealed class PatchWriter
    {
        public void Write(int _, Span<SceneVertex> destination)
            => destination[0] = destination[0] with { Position = destination[0].Position + Vector3.UnitX };
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    public void OverflowKeepsTheOriginalArgumentException(int workers)
    {
        var source = Scene(32771);
        var vertices = source.Vertices.ToArray();
        vertices[0] = vertices[0] with { Position = new(float.MaxValue, 0, 0) };
        source = new(vertices, source.Indices, source.Materials, world: Matrix4x4.CreateScale(2));
        var error = Assert.Throws<ArgumentException>(() => Build(source, workers));
        Assert.Equal("Transformed GI positions and normals must remain finite.", error.Message);
    }

    private static OriginalFrameScene Frame(SceneSnapshot source)
        => new(new(source, source.Materials.Select(_ => new OriginalStyleMaterialBinding(Vector3.One))));

    private static SceneSnapshot Scene(int triangles)
    {
        SceneVertex[] vertices = [
            new(new(-0f, 2.5f, -3.125f), new(1, 2, -0f), new(-0f, .8f)),
            new(new(4, -0f, .125f), new(.25f, -2, 3), new(.7f, -0f)),
            new(new(-2, 3, 4), new(2, 0, -1), new(1.7f, -.6f))];
        uint[] indices = Enumerable.Range(0, triangles * 3).Select(i => (uint)(i % 3)).ToArray();
        int split = Math.Min(4101, triangles) * 3;
        SceneMaterial[] materials = [
            new("empty", 0, 0, Vector4.One, Vector3.Zero, Vector3.Zero, 0),
            new("first", 0, split, Vector4.One, Vector3.Zero, Vector3.Zero, 0),
            new("tail", split, indices.Length - split, Vector4.One, Vector3.Zero, Vector3.Zero, 0),
            new("empty-tail", indices.Length, 0, Vector4.One, Vector3.Zero, Vector3.Zero, 0)];
        var world = Matrix4x4.CreateScale(2, 3, -4) * Matrix4x4.CreateRotationY(.37f) * Matrix4x4.CreateTranslation(13, -6, 29);
        return new(vertices, indices, materials, world: world);
    }

    // Accepted pre-parallel implementation, independent of the new block scheduler.
    internal static RayGiTriangleGpu[] Legacy(SceneSnapshot source)
    {
        Matrix4x4.Invert(source.World, out var inverse);
        var normalWorld = Matrix4x4.Transpose(inverse);
        var rows = new RayGiTriangleGpu[source.Indices.Length / 3];
        for (int m = 0; m < source.Materials.Count; m++)
        {
            var material = source.Materials[m];
            for (int i = material.StartIndex; i < material.StartIndex + material.IndexCount; i += 3)
            {
                var a = source.Vertices[(int)source.Indices[i]]; var b = source.Vertices[(int)source.Indices[i + 1]]; var c = source.Vertices[(int)source.Indices[i + 2]];
                rows[i / 3] = new(new(Vector3.Transform(a.Position, source.World), 0), new(Vector3.Transform(b.Position, source.World), 0), new(Vector3.Transform(c.Position, source.World), 0),
                    new(Vector3.TransformNormal(a.Normal, normalWorld), a.Uv.X), new(Vector3.TransformNormal(b.Normal, normalWorld), b.Uv.X), new(Vector3.TransformNormal(c.Normal, normalWorld), c.Uv.X),
                    new(a.Uv.Y, b.Uv.Y, c.Uv.Y, c.Normal.Z), new(a.Normal, b.Normal.X), new(b.Normal.Y, b.Normal.Z, c.Normal.X, c.Normal.Y), new((uint)m, (uint)(i / 3), 1, 0));
            }
        }
        return rows;
    }
}
