using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneChunkCompositionTests
{
    [Fact]
    public void CrossChunkWritesPreserveBytesAndOldFramesWithoutContiguousMaterialization()
    {
        var source = Scene(12301); var baseline = Bytes(source);
        var first = Compose(source, [(4094, 7), (12299, 2)]); var firstBytes = Bytes(first);
        var next = Compose(first, [(3, 2)]);
        var expected = new SceneVertex[source.VertexCount]; source.CopyVertices(0, expected);
        Patch(0, 0, expected.AsSpan(4094, 7)); Patch(1, 0, expected.AsSpan(12299, 2));
        Assert.Equal(MemoryMarshal.AsBytes(expected.AsSpan()).ToArray(), firstBytes);
        Assert.Equal(baseline, Bytes(source)); Assert.Equal(firstBytes, Bytes(first));
        Assert.NotEqual(firstBytes, Bytes(next));
        Assert.False(IsMaterialized(first)); Assert.False(IsMaterialized(next));
        Assert.Equal(new[] { new SceneVertexRange(0, 8192), new SceneVertexRange(12288, 13) }, first.VertexRevision.GetChangedRanges(source.VertexRevision));
    }

    [Fact]
    public void PublicSpanIsAnExactOnceMaterializedCompatibilityViewSharedByMetadata()
    {
        var source = Compose(Scene(8200), [(4095, 3)]); var expected = Bytes(source);
        var metadata = source.WithCamera(SceneCamera.Default).WithLighting(Vector3.UnitY).WithWorld(Matrix4x4.CreateTranslation(1, 0, 0));
        Assert.False(IsMaterialized(source)); Assert.False(IsMaterialized(metadata));
        Assert.Same(source.VertexRevision, metadata.VertexRevision);
        Assert.Equal(expected, MemoryMarshal.AsBytes(metadata.Vertices).ToArray());
        Assert.True(IsMaterialized(source)); Assert.True(IsMaterialized(metadata));
        Assert.Equal(expected, MemoryMarshal.AsBytes(source.Vertices).ToArray());
    }

    [Fact]
    public async Task ConcurrentCompatibilityReadersSeeTheSameCompleteValue()
    {
        var source = Compose(Scene(10000), [(4095, 7)]); var expected = Bytes(source);
        var actual = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => MemoryMarshal.AsBytes(source.Vertices).ToArray())));
        foreach (var bytes in actual) Assert.Equal(expected, bytes);
        Assert.True(IsMaterialized(source));
    }

    [Fact]
    public void DirtyPublicationAllocationDependsOnTouchedChunks()
    {
        var source = Scene(302490); _ = source.VertexRevision;
        _ = Compose(source, [(4095, 3)]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var next = Compose(source, [(4095, 3)]);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 8192L * 32, 600000);
        Assert.False(IsMaterialized(next)); Assert.Equal(source.VertexCount, next.VertexCount);
        GC.KeepAlive(next);
    }

    [Fact]
    public void CopyAndChunkReadsCoverTailWithoutFlatteningOrExposingInputMutation()
    {
        var source = Scene(8200); var next = Compose(source, [(4094, 8)]);
        var copy = new SceneVertex[13]; next.CopyVertices(4093, copy);
        for (int i = 0; i < copy.Length; i++) Assert.Equal(next.GetVertex(i + 4093), copy[i]);
        Assert.Equal(3, next.VertexChunkCount); Assert.Equal(8, next.GetVertexChunk(2).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => next.GetVertex(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => next.GetVertex(next.VertexCount));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReadChunk(next, 3));
        Assert.Throws<ArgumentException>(() => next.CopyVertices(8199, new SceneVertex[2]));
        Assert.False(IsMaterialized(next));
    }

    [Fact]
    public void InvalidRangeValueAndThrowingWriterCannotChangePublishedGeometry()
    {
        var source = Scene(8200); var bytes = Bytes(source); var revision = source.VertexRevision;
        Assert.Throws<ArgumentException>(() => Compose(source, [(8200, 1)]));
        Assert.Throws<ArgumentException>(() => Compose(source, [(0, 2), (1, 1)]));
        Assert.Throws<ArgumentException>(() => Compose(source, [(4095, 3)], nameof(Invalid)));
        Assert.Throws<InvalidOperationException>(() => Compose(source, [(4095, 3)], nameof(Throwing)));
        Assert.Equal(bytes, Bytes(source)); Assert.Same(revision, source.VertexRevision);
        Assert.False(IsMaterialized(source));
    }

    [Fact]
    public void PublicDeformationDefensivelyCopiesCallerArrayAndRetainsChunkSemantics()
    {
        var source = Scene(9000); var values = new SceneVertex[source.VertexCount]; source.CopyVertices(0, values);
        var deformed = source.WithDeformedVertices(values); var original = deformed.GetVertex(0);
        values[0] = new(new(999), Vector3.UnitZ, Vector2.One);
        Assert.Equal(original, deformed.GetVertex(0)); Assert.True(deformed.HasDynamicVertices);
        Assert.False(deformed.VertexRevision.SupportsIncrementalUpdates);
        var child = Compose(deformed, [(4095, 3)]);
        Assert.True(child.VertexRevision.SupportsIncrementalUpdates);
        Assert.False(IsMaterialized(child));
    }

    private static SceneSnapshot Scene(int count) => new(Enumerable.Range(0, count)
        .Select(i => new SceneVertex(new(i, -0f, 1), Vector3.UnitY, new(i % 3, 0))).ToArray(),
        [0u, 1u, 2u], [new("mesh", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 0)]);
    private static byte[] Bytes(SceneSnapshot source)
    { var values = new SceneVertex[source.VertexCount]; source.CopyVertices(0, values); return MemoryMarshal.AsBytes(values.AsSpan()).ToArray(); }
    private static bool IsMaterialized(SceneSnapshot scene)
    {
        var storage = typeof(SceneSnapshot).GetField("vertexStorage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
        return (bool)storage.GetType().GetProperty("IsMaterialized", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(storage)!;
    }
    private static void ReadChunk(SceneSnapshot scene, int chunk) { _ = scene.GetVertexChunk(chunk).Length; }
    private static SceneSnapshot Compose(SceneSnapshot source, (int Start, int Count)[] ranges, string writer = nameof(Valid))
    {
        var method = typeof(SceneSnapshot).GetMethod("WithComposedVertexChunks", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var callback = Delegate.CreateDelegate(method.GetParameters()[1].ParameterType, typeof(SceneChunkCompositionTests).GetMethod(writer, BindingFlags.Static | BindingFlags.NonPublic)!);
        try { return (SceneSnapshot)method.Invoke(source, [ranges, callback])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static void Valid(int range, int offset, Span<SceneVertex> destination) => Patch(range, offset, destination);
    private static void Patch(int range, int offset, Span<SceneVertex> destination)
    { for (int i = 0; i < destination.Length; i++) destination[i] = destination[i] with { Position = new(range + 1, offset + i, BitConverter.Int32BitsToSingle(unchecked((int)0x80000000))) }; }
    private static void Invalid(int range, int offset, Span<SceneVertex> destination) { destination[0] = destination[0] with { Normal = new(float.NaN) }; }
    private static void Throwing(int range, int offset, Span<SceneVertex> destination) { destination[0] = default; throw new InvalidOperationException("failed writer"); }
}
