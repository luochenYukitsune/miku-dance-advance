using System.Collections.ObjectModel;
using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class OutlineChunkCompositionTests
{
    private const int Chunk = 4096;

    [Fact]
    public void CrossChunkCompositionIsByteExactAndHeldOutputsStayUnchanged()
    {
        var source = Outline(Chunk * 3 + 7); var sourceBytes = Bytes(source);
        var first = Compose(source, [(Chunk - 2, 5), (Chunk * 3 + 6, 1)]); var firstBytes = Bytes(first);
        var second = Compose(first, [(2, 2)]);
        var expected = source.Vertices.ToArray();
        Patch(expected.AsSpan(Chunk - 2, 5), 0, 0); Patch(expected.AsSpan(Chunk * 3 + 6, 1), 1, 0);
        Assert.Equal(MemoryMarshal.AsBytes(expected.AsSpan()).ToArray(), firstBytes);
        Assert.Equal(sourceBytes, Bytes(source)); Assert.Equal(firstBytes, Bytes(first));
        Assert.NotEqual(firstBytes, Bytes(second));
        Assert.IsType<ReadOnlyCollection<OriginalOutlineVertex>>(first.Vertices);
        Assert.True(first.VertexRevision.SupportsIncrementalUpdates);
        Assert.Equal(new[] { new SceneVertexRange(0, Chunk * 2), new SceneVertexRange(Chunk * 3, 7) }, first.VertexRevision.GetChangedRanges(source.VertexRevision));
    }

    [Fact]
    public void DirtyOutlinePublicationAllocatesTouchedChunksRatherThanTheWholeMesh()
    {
        var source = Outline(302490); _ = source.VertexRevision;
        _ = Compose(source, [(Chunk - 1, 3)]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var next = Compose(source, [(Chunk - 1, 3)]);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, Chunk * 2L * Marshal.SizeOf<OriginalOutlineVertex>(), 1024 * 1024);
        Assert.Equal(source.Vertices.Count, next.Vertices.Count); GC.KeepAlive(next);
    }

    [Fact]
    public void InvalidValuesMaterialUpdatesAndWriterFailureCannotAlterPublishedOutput()
    {
        var source = Outline(Chunk + 3); var bytes = Bytes(source); var revision = source.VertexRevision;
        Assert.Throws<ArgumentException>(() => Compose(source, [(Chunk - 1, 3)], nameof(InvalidWriter)));
        Assert.Throws<InvalidOperationException>(() => Compose(source, [(1, 1)], nameof(ThrowingWriter)));
        Assert.Throws<ArgumentException>(() => Compose(source, [(0, 1)], materials: [new(-1, Vector4.One)]));
        Assert.Throws<ArgumentException>(() => Compose(source, [(source.Vertices.Count, 1)]));
        Assert.Equal(bytes, Bytes(source)); Assert.Same(revision, source.VertexRevision);
        var materialOnly = Compose(source, [], materials: [new(.2f, Vector4.One)]);
        Assert.Same(source.Vertices, materialOnly.Vertices); Assert.Same(revision, materialOnly.VertexRevision);
        Assert.Equal(.2f, materialOnly.Materials[0].Width);
    }

    private static OriginalOutlineData Outline(int count)
        => new(Enumerable.Range(0, count).Select(i => new OriginalOutlineVertex(new(i, BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)), 0),
            Vector3.UnitY, OriginalOutlineVertexMode.Expand, 1, 0)), [new(.18f, Vector4.One)]);
    private static byte[] Bytes(OriginalOutlineData value) => MemoryMarshal.AsBytes(value.Vertices.ToArray().AsSpan()).ToArray();
    private static OriginalOutlineData Compose(OriginalOutlineData source, (int Start, int Count)[] ranges,
        string writer = nameof(ValidWriter), IReadOnlyList<OriginalOutlineMaterial>? materials = null)
    {
        var method = typeof(OriginalOutlineData).GetMethod("WithComposedVertexChunks", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var callback = Delegate.CreateDelegate(method.GetParameters()[1].ParameterType,
            typeof(OutlineChunkCompositionTests).GetMethod(writer, BindingFlags.Static | BindingFlags.NonPublic)!);
        try { return (OriginalOutlineData)method.Invoke(source, [ranges, callback, materials ?? source.Materials])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static void ValidWriter(int range, int offset, Span<OriginalOutlineVertex> destination) => Patch(destination, range, offset);
    private static void Patch(Span<OriginalOutlineVertex> destination, int range, int offset)
    { for (int i = 0; i < destination.Length; i++) destination[i] = destination[i] with { Position = new(range + 1, offset + i, -0f) }; }
    private static void InvalidWriter(int range, int offset, Span<OriginalOutlineVertex> destination)
    { destination[0] = destination[0] with { SelectedMaterialIndex = 1 }; }
    private static void ThrowingWriter(int range, int offset, Span<OriginalOutlineVertex> destination)
    { destination[0] = destination[0] with { VertexScale = 2 }; throw new InvalidOperationException("writer failed"); }
}
