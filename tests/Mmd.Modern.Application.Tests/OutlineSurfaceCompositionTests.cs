using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class OutlineSurfaceCompositionTests
{
    private const int Chunk = 4096;
    private const int Count = Chunk * 32 + 19;
    private static float NegativeZero => BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));

    [Fact]
    public void SurfaceRefreshMatchesTheExistingWriterBitForBitAtOneAndSixteenWorkers()
    {
        var source = Outline(Count); var surface = Surface(Count, 20);
        var sourceBytes = Bytes(source); var sourceRevision = source.VertexRevision;
        (int Start, int Count)[] ranges =
        [
            (3, 5), (8, 7), (21, 9),
            (Chunk - 2, Chunk * 26 + 11),
            (Chunk * 29 + 5, Chunk + 3), (Count - 3, 3)
        ];
        OriginalOutlineMaterial[] materials = [new(.7f, Vector4.One, false), new(.1f, new(.1f, .2f, .3f, .4f)), new(0, Vector4.Zero)];
        var oracle = ComposeWithExistingWriter(source, surface, ranges, materials);
        var serial = Refresh(source, surface, ranges, materials, 1);
        var parallel = Refresh(source, surface, ranges, materials, 16);
        Assert.Equal(Bytes(oracle), Bytes(serial)); Assert.Equal(Bytes(oracle), Bytes(parallel));

        var expected = source.Vertices.ToArray();
        foreach (var range in ranges)
            for (int i = range.Start; i < range.Start + range.Count; i++)
            {
                var vertex = surface.GetVertex(i);
                expected[i] = expected[i] with { Position = vertex.Position, Normal = vertex.Normal };
            }
        Assert.Equal(VertexBytes(expected), Bytes(parallel));
        Assert.Equal(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits(parallel.Vertices[3].Position.Y));
        Assert.Equal(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits(parallel.Vertices[3].Normal.X));
        Assert.Equal(materials, parallel.Materials);
        Assert.False(IsMaterialized(surface));

        var oldChunks = Memories(source); var newChunks = Memories(parallel);
        Assert.Equal(19, newChunks[^1].Length);
        for (int chunk = 0; chunk < oldChunks.Length; chunk++)
        {
            int start = chunk * Chunk, end = start + oldChunks[chunk].Length;
            bool touched = ranges.Any(range => range.Start < end && range.Start + range.Count > start);
            Assert.Equal(!touched, oldChunks[chunk].Equals(newChunks[chunk]));
        }
        Assert.Equal(oracle.VertexRevision.GetChangedRanges(sourceRevision), parallel.VertexRevision.GetChangedRanges(sourceRevision));

        var held = Bytes(parallel); var nextSurface = Surface(Count, 50);
        var next = Refresh(parallel, nextSurface, [(Chunk * 28 + 1, 3)], materials, 16);
        Assert.NotEqual(held, Bytes(next));
        Assert.Equal(sourceBytes, Bytes(source)); Assert.Same(sourceRevision, source.VertexRevision);
        Assert.Equal(held, Bytes(parallel));
        materials[0] = new(9, Vector4.Zero);
        Assert.Equal(.7f, parallel.Materials[0].Width); Assert.False(parallel.Materials[0].DrawEnabled);
    }

    [Fact]
    public void EmptyRangesSharePublishedVerticesAndOnlyFreezeChangedMaterials()
    {
        var source = Outline(7); var surface = Surface(7, 10); var revision = source.VertexRevision;
        Assert.Same(source, Refresh(source, surface, [], source.Materials, 16));
        var materials = source.Materials.ToArray(); materials[1] = new(.125f, new(.4f, .3f, .2f, .1f), false);
        var result = Refresh(source, surface, [], materials, 16);
        Assert.Same(source.Vertices, result.Vertices); Assert.Same(revision, result.VertexRevision);
        Assert.Equal(.125f, result.Materials[1].Width); Assert.False(result.Materials[1].DrawEnabled);
        materials[1] = new(9, Vector4.One);
        Assert.Equal(.125f, result.Materials[1].Width);
        Assert.False(IsMaterialized(surface));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    public void InvalidSurfaceRangesAndMaterialsLeavePublishedGenerationsUntouched(int degree)
    {
        var source = Outline(Count); var surface = Surface(Count, 10);
        var held = Refresh(source, surface, [(0, Chunk * 9)], source.Materials, degree);
        var sourceBytes = Bytes(source); var heldBytes = Bytes(held); var revision = held.VertexRevision;
        foreach (var invalid in new (int Start, int Count)[][]
        {
            [(-1, 1)], [(0, 0)], [(0, -1)], [(Count, 1)], [(1, int.MaxValue)],
            [(0, 2), (1, 1)], [(2, 1), (0, 1)], [(0, Chunk * 9), (Count, 1)]
        })
            Assert.Throws<ArgumentException>(() => Refresh(held, surface, invalid, held.Materials, degree));
        foreach (var invalid in new OriginalOutlineMaterial[][]
        {
            [], [new(.1f, Vector4.One)],
            [new(-1, Vector4.One), held.Materials[1], held.Materials[2]],
            [new(float.NaN, Vector4.One), held.Materials[1], held.Materials[2]],
            [new(.1f, new(float.PositiveInfinity, 0, 0, 1)), held.Materials[1], held.Materials[2]],
            [null!, held.Materials[1], held.Materials[2]]
        })
            Assert.Throws<ArgumentException>(() => Refresh(held, surface, [(0, Chunk * 9)], invalid, degree));
        Assert.Throws<ArgumentException>(() => Refresh(held, Surface(Count - 1, 10), [(0, 1)], held.Materials, degree));
        Assert.Throws<ArgumentNullException>(() => Refresh(held, null!, [], held.Materials, degree));
        Assert.Throws<ArgumentNullException>(() => Refresh(held, surface, null!, held.Materials, degree));
        Assert.Throws<ArgumentNullException>(() => Refresh(held, surface, [], null!, degree));
        Assert.Equal(sourceBytes, Bytes(source)); Assert.Equal(heldBytes, Bytes(held));
        Assert.Same(revision, held.VertexRevision); Assert.False(IsMaterialized(surface));
    }

    [Fact]
    public void TrustedWorkersPreflightAllRangesJoinFailuresAndRetainGenericCallbackOrder()
    {
        var source = CreateIntStorage(Enumerable.Range(0, Count).ToArray()); var writer = new FailureWriter();
        Assert.Throws<ArgumentException>(() => ComposeInts(source, [(0, Chunk * 9), (Count, 1)], writer, true));
        Assert.Equal(0, writer.Calls);
        var failure = Assert.Throws<InvalidOperationException>(() => ComposeInts(source, [(0, Count)], writer, true));
        Assert.Equal("chunk 0", failure.Message);
        Assert.Equal(Environment.ProcessorCount > 1 ? 33 : 1, writer.Calls);
        Assert.Equal(Enumerable.Range(0, Count), source);

        var serialFailure = new FailureWriter();
        Assert.Equal("chunk 0", Assert.Throws<InvalidOperationException>(
            () => ComposeInts(source, [(0, Count)], serialFailure, false)).Message);
        Assert.Equal(1, serialFailure.Calls);
        var ordered = new OrderedWriter();
        var unchanged = ComposeInts(source, [(0, Count)], ordered, false);
        Assert.Equal(Enumerable.Range(0, 33).Select(chunk => chunk * Chunk), ordered.Offsets);
        Assert.All(ordered.Threads, thread => Assert.Equal(Environment.CurrentManagedThreadId, thread));
        Assert.Equal(source, unchanged);
    }

    private static OriginalOutlineData Outline(int count)
        => new(Enumerable.Range(0, count).Select(i => new OriginalOutlineVertex(new(-100 - i, NegativeZero, .5f),
            new(2, NegativeZero, -.75f), i % 2 == 0 ? OriginalOutlineVertexMode.Expand : OriginalOutlineVertexMode.Inward,
            i % 5 == 0 ? NegativeZero : .25f + i % 7, i % 3)),
            [new(.18f, Vector4.One), new(.3f, new(.1f, .2f, .3f, 1)), new(.5f, Vector4.Zero, false)]);
    private static SceneSnapshot Surface(int count, int seed)
        => new(Enumerable.Range(0, count).Select(i => new SceneVertex(new(seed + i * .25f, NegativeZero, BitConverter.Int32BitsToSingle(1 + i % 17)),
            new(NegativeZero, i % 2 == 0 ? 2.5f : 0, -.75f), new(.1f, .2f))).ToArray(),
            [0u, 0u, 0u], [new("surface", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 0)]);
    private static byte[] Bytes(OriginalOutlineData value) => VertexBytes(value.Vertices.ToArray());
    private static byte[] VertexBytes(OriginalOutlineVertex[] values) => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    private static ReadOnlyMemory<OriginalOutlineVertex>[] Memories(OriginalOutlineData value)
    {
        var storage = typeof(OriginalOutlineData).GetProperty("vertices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
        return (ReadOnlyMemory<OriginalOutlineVertex>[])storage.GetType().GetField("chunks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(storage)!;
    }
    private static bool IsMaterialized(SceneSnapshot scene)
    {
        var storage = typeof(SceneSnapshot).GetField("vertexStorage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
        return (bool)storage.GetType().GetProperty("IsMaterialized", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(storage)!;
    }
    private static OriginalOutlineData Refresh(OriginalOutlineData source, SceneSnapshot surface,
        IReadOnlyList<(int Start, int Count)> ranges, IReadOnlyList<OriginalOutlineMaterial> materials, int degree)
        => (OriginalOutlineData)Invoke(typeof(OriginalOutlineData).GetMethod("WithSurfaceVertexChunks", BindingFlags.Instance | BindingFlags.NonPublic)!,
            source, [surface, ranges, materials, degree]);
    private static OriginalOutlineData ComposeWithExistingWriter(OriginalOutlineData source, SceneSnapshot surface,
        (int Start, int Count)[] ranges, IReadOnlyList<OriginalOutlineMaterial> materials)
    {
        var method = typeof(OriginalOutlineData).GetMethod("WithComposedVertexChunks", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var writer = new SurfaceWriter(surface, ranges);
        var callback = Delegate.CreateDelegate(method.GetParameters()[1].ParameterType, writer, typeof(SurfaceWriter).GetMethod(nameof(SurfaceWriter.Write))!);
        return (OriginalOutlineData)Invoke(method, source, [ranges, callback, materials]);
    }
    private sealed class SurfaceWriter(SceneSnapshot surface, (int Start, int Count)[] ranges)
    {
        public void Write(int range, int offset, Span<OriginalOutlineVertex> destination)
        {
            for (int i = 0; i < destination.Length; i++)
            {
                var vertex = surface.GetVertex(ranges[range].Start + offset + i);
                destination[i] = destination[i] with { Position = vertex.Position, Normal = vertex.Normal };
            }
        }
    }
    private static readonly Type IntStorageType = typeof(SceneSnapshot).Assembly.GetType("Mmd.Modern.Core.OwnedVertexChunks`1")!.MakeGenericType(typeof(int));
    private static IList<int> CreateIntStorage(int[] owned)
        => (IList<int>)Activator.CreateInstance(IntStorageType, BindingFlags.Instance | BindingFlags.NonPublic, null, [owned], null)!;
    private static IList<int> ComposeInts(IList<int> source, (int Start, int Count)[] ranges, object writer, bool trusted)
    {
        var method = IntStorageType.GetMethod(trusted ? "WithTrustedParallelRanges" : "WithRanges", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var callback = Delegate.CreateDelegate(method.GetParameters()[1].ParameterType, writer, writer.GetType().GetMethod("Write")!);
        return (IList<int>)Invoke(method, source, trusted ? [ranges, callback, 16] : [ranges, callback]);
    }
    private sealed class FailureWriter
    {
        public int Calls;
        public void Write(int range, int offset, Span<int> destination)
        {
            Interlocked.Increment(ref Calls); destination[0] = -1;
            throw new InvalidOperationException($"chunk {offset / Chunk}");
        }
    }
    private sealed class OrderedWriter
    {
        public readonly List<int> Offsets = [];
        public readonly List<int> Threads = [];
        public void Write(int range, int offset, Span<int> destination)
        { Offsets.Add(offset); Threads.Add(Environment.CurrentManagedThreadId); }
    }
    private static object Invoke(MethodInfo method, object owner, object?[] arguments)
    {
        try { return method.Invoke(owner, arguments)!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
}
