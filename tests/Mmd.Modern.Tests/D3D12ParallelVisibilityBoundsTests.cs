using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12ParallelVisibilityBoundsTests
{
    private const int Chunk = 4096;
    private const int MaterialIndices = 3 * Chunk + 3;
    private const int Materials = 4;
    private const int Count = Materials * MaterialIndices + 7;
    private const long Budget = D3D12VisibilityBoundsCache.MaximumLeafCacheBytes;
    private static readonly float NegativeZero = BitConverter.Int32BitsToSingle(int.MinValue);

    [Theory]
    [InlineData(8, 0L, 0)]
    [InlineData(16, 0L, 0)]
    [InlineData(8, Budget, 0)]
    [InlineData(16, Budget, 0)]
    [InlineData(8, 0L, 1)]
    [InlineData(16, 0L, 1)]
    [InlineData(8, Budget, 1)]
    [InlineData(16, Budget, 1)]
    [InlineData(8, 0L, 2)]
    [InlineData(16, 0L, 2)]
    [InlineData(8, Budget, 2)]
    [InlineData(16, Budget, 2)]
    public void InitialAndDenseBuildsMatchSerialAndIndependentOrderedOracle(int degree, long budget, int reader)
    {
        var source = Scene(); var serial = NewCache(1, budget); var parallel = NewCache(degree, budget);
        var root = Input(source, reader);
        var expected = Get(serial, source, root, reader); var first = Get(parallel, source, root, reader);
        EqualOracle(source, root, reader, first); Equal(expected, first); EqualStatistics(serial, parallel);
        AssertParallel(parallel, degree); Assert.Equal(0, ParallelMaterials(serial));
        var saved = Bytes(first);

        var dense = Dense(source); var next = Input(dense, reader);
        var changed = Get(parallel, dense, next, reader);
        Equal(Get(serial, dense, next, reader), changed); EqualOracle(dense, next, reader, changed);
        EqualStatistics(serial, parallel); AssertParallel(parallel, degree);
        Assert.Equal(Materials, parallel.LastRebuiltMaterials);
        if (budget != 0) Assert.Equal(Materials, parallel.LastFullScanMaterials);
        Assert.Equal(saved, Bytes(first)); Assert.False(IsMaterialized(dense)); AssertUnmaterialized(root); AssertUnmaterialized(next);

        Assert.Same(changed, Get(parallel, dense, next, reader)); Assert.Equal(0, ParallelMaterials(parallel));
        bool current = next is D3D12ExpandedPositions owned
            ? parallel.TryGetCurrentExpanded(dense, owned, out var cached)
            : parallel.TryGetCurrent(dense, reader == 0 ? null : (Vector4[])next, out cached);
        Assert.True(current); Assert.Same(changed, cached); Assert.Equal(0, ParallelMaterials(parallel));
    }

    [Theory]
    [InlineData(4, 0L, 0)]
    [InlineData(16, 0L, 0)]
    [InlineData(4, Budget, 0)]
    [InlineData(16, Budget, 0)]
    [InlineData(4, 0L, 1)]
    [InlineData(16, 0L, 1)]
    [InlineData(4, Budget, 1)]
    [InlineData(16, Budget, 1)]
    [InlineData(4, 0L, 2)]
    [InlineData(16, 0L, 2)]
    [InlineData(4, Budget, 2)]
    [InlineData(16, Budget, 2)]
    public void ManySmallDenseMaterialsJoinParallelBatchAndKeepBackwardOutputs(int degree, long budget, int reader)
    {
        // Each material misses the former 4096-index gate, but their combined scan is substantial.
        const int materials = 24, materialIndices = 2049;
        var source = Scene(materials, materialIndices); var serial = NewCache(1, budget); var parallel = NewCache(degree, budget);
        var root = Input(source, reader); var first = Get(parallel, source, root, reader);
        Equal(Get(serial, source, root, reader), first); EqualOracle(source, root, reader, first);
        EqualStatistics(serial, parallel);
        AssertParallel(parallel, degree, materials); Assert.Equal(0, ParallelMaterials(serial));
        var saved = Bytes(first);

        var dense = Dense(source); var next = Input(dense, reader);
        var changed = Get(parallel, dense, next, reader); var changedSaved = Bytes(changed);
        Equal(Get(serial, dense, next, reader), changed); EqualOracle(dense, next, reader, changed);
        EqualStatistics(serial, parallel);
        AssertParallel(parallel, degree, materials);
        Assert.Equal(materials, parallel.LastRebuiltMaterials);
        if (budget != 0) Assert.Equal(materials, parallel.LastFullScanMaterials);

        foreach (var (scene, input) in new[] { (source, root), (dense, next) })
        {
            var actual = Get(parallel, scene, input, reader);
            Equal(Get(serial, scene, input, reader), actual); EqualOracle(scene, input, reader, actual);
            EqualStatistics(serial, parallel); Assert.False(parallel.LastTopologyRebuilt);
            Assert.Equal(materials, parallel.LastRebuiltMaterials);
            AssertParallel(parallel, degree, materials);
            Assert.Equal(saved, Bytes(first)); Assert.Equal(changedSaved, Bytes(changed)); AssertUnmaterialized(input);
        }
        Assert.False(IsMaterialized(dense)); AssertUnmaterialized(root); AssertUnmaterialized(next);
        var current = Get(parallel, dense, next, reader); Assert.Same(current, Get(parallel, dense, next, reader));
        Assert.Equal(0, ParallelMaterials(parallel));
    }

    [Theory]
    [InlineData(8, 0)]
    [InlineData(16, 0)]
    [InlineData(8, 1)]
    [InlineData(16, 1)]
    [InlineData(8, 2)]
    [InlineData(16, 2)]
    public void SharedSparseDenseSkippedAndBackwardFramesKeepActualBaselineAndOldOutputs(int degree, int reader)
    {
        var source = Scene(); var serial = NewCache(1); var parallel = NewCache(degree);
        var root = Input(source, reader); var held = Get(parallel, source, root, reader);
        Get(serial, source, root, reader); var saved = Bytes(held);
        var dense = Dense(source);
        // One shared vertex touches all four materials but only a bounded set of their leaves.
        var sparse = Compose(dense, 0, v => v with { Position = new(-100, NegativeZero, 3) });
        var later = Compose(sparse, Chunk - 1, v => v with { Position = new(-200, 2, 4) });
        var skipped = Compose(later, Chunk, v => v with { Position = new(-300, 3, 5) });
        var latest = Compose(skipped, Count - 1, v => v with { Position = new(-400, 4, 6) });
        var branch = Compose(source, 2 * MaterialIndices + 300, v => v with { Position = new(-500) });
        foreach (var scene in new[] { dense, sparse, later, latest, source, branch, skipped, latest, source })
        {
            var input = Input(scene, reader);
            var actual = Get(parallel, scene, input, reader);
            Equal(Get(serial, scene, input, reader), actual); EqualOracle(scene, input, reader, actual);
            EqualStatistics(serial, parallel); Assert.False(parallel.LastTopologyRebuilt);
            if (ReferenceEquals(scene, sparse) || ReferenceEquals(scene, later) || ReferenceEquals(scene, branch))
                Assert.Equal(0, ParallelMaterials(parallel));
            Assert.Equal(saved, Bytes(held)); Assert.False(IsMaterialized(scene)); AssertUnmaterialized(input);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SmallSingleAndSparseMaterialWorkStaysSerialAndCountersReset(int reader)
    {
        var parallel = NewCache(16);
        foreach (var scene in new[] { Scene(1, 3), Scene(1, MaterialIndices), Scene(Materials, 3) })
        {
            var input = Input(scene, reader); EqualOracle(scene, input, reader, Get(parallel, scene, input, reader));
            Assert.Equal(0, ParallelMaterials(parallel));
        }
        var source = Scene(); var root = Input(source, reader); Get(parallel, source, root, reader);
        AssertParallel(parallel, 16);
        var sparse = Compose(source, 0, v => v with { Position = new(-50) }); var next = Input(sparse, reader);
        EqualOracle(sparse, next, reader, Get(parallel, sparse, next, reader));
        Assert.Equal(Materials, parallel.LastRebuiltMaterials); Assert.Equal(0, ParallelMaterials(parallel));
        var single = DenseOneMaterial(sparse); var singleInput = Input(single, reader);
        EqualOracle(single, singleInput, reader, Get(parallel, single, singleInput, reader));
        Assert.Equal(1, parallel.LastFullScanMaterials); Assert.Equal(0, ParallelMaterials(parallel));
        var empty = SceneSnapshot.CreateEmpty(); var emptyInput = Input(empty, reader);
        Assert.Empty(Get(parallel, empty, emptyInput, reader)); Assert.Equal(0, ParallelMaterials(parallel));
    }

    [Theory]
    [InlineData(0L, 0)]
    [InlineData(Budget, 0)]
    [InlineData(0L, 1)]
    [InlineData(Budget, 1)]
    [InlineData(0L, 2)]
    [InlineData(Budget, 2)]
    public void SignedZerosNonfinitePayloadsAndInvalidWKeepExactOrderedBits(long budget, int reader)
    {
        var source = Scene(); var vertices = Vertices(source);
        float[] values = [0, NegativeZero, float.Epsilon, -float.Epsilon, float.MaxValue, -float.MaxValue];
        for (int index = 0; index < vertices.Length; index++) vertices[index] = vertices[index] with
        { Position = new(values[index % values.Length], values[(index + 1) % values.Length], values[(index + 2) % values.Length]) };
        var scene = source.WithDeformedVertices(vertices); var serial = NewCache(1, budget); var parallel = NewCache(16, budget);
        var root = Input(scene, reader); var held = Get(parallel, scene, root, reader); var saved = Bytes(held);
        Equal(Get(serial, scene, root, reader), held); EqualOracle(scene, root, reader, held); AssertParallel(parallel, 16);
        for (int step = 0; step < 4; step++)
        {
            var unusual = Dense(scene); var positions = Positions(unusual);
            for (int material = 0; material < Materials; material++)
            {
                int index = material * MaterialIndices + 300;
                var p = positions[index];
                positions[index] = step switch
                {
                    0 => new(BitConverter.Int32BitsToSingle(0x7fc00001 + material), p.Y, p.Z, 1),
                    1 => new(p.X, float.PositiveInfinity, p.Z, 1),
                    2 => new(p.X, p.Y, float.NegativeInfinity, 1),
                    _ => new(p.X, p.Y, p.Z, 0)
                };
                if (reader == 0) CorruptPosition(unusual, index, new(positions[index].X, positions[index].Y, positions[index].Z));
            }
            object input = reader == 2 ? Owned(unusual, positions) : positions;
            var actual = Get(parallel, unusual, input, reader);
            Equal(Get(serial, unusual, input, reader), actual); EqualOracle(unusual, input, reader, actual);
            EqualStatistics(serial, parallel); AssertParallel(parallel, 16); AssertUnmaterialized(input);
            Equal(Get(serial, scene, root, reader), Get(parallel, scene, root, reader));
            Assert.Equal(saved, Bytes(held));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    public void FailedRebuildEvictsOnlyThePartiallyUpdatedEntryAndKeepsPublishedBounds(int degree)
    {
        var source = Scene(); var cache = NewCache(degree, capacity: 2);
        var first = cache.GetSurface(source); var saved = Bytes(first);
        var other = new SceneSnapshot(Vertices(source), source.Indices.ToArray().Reverse().ToArray(), source.Materials);
        var otherBounds = cache.GetSurface(other); var otherSaved = Bytes(otherBounds);
        var dense = Dense(source);
        // Deliberately corrupt the immutable test fixture after map construction so an ordered
        // rebuild fails after private leaf state may have advanced. Restore it before recovery.
        var indices = MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(dense.Indices), dense.Indices.Length);
        uint last = indices[^1]; indices[^1] = (uint)dense.VertexCount;
        try { Assert.NotNull(Record.Exception(() => cache.GetSurface(dense))); }
        finally { indices[^1] = last; }
        Assert.Equal(1, cache.EntryCount);
        Assert.False(cache.TryGetCurrent(source, null, out _));
        Assert.True(cache.TryGetCurrent(other, null, out var retained)); Assert.Same(otherBounds, retained);
        EqualOracle(dense, Array.Empty<Vector4>(), 0, cache.GetSurface(dense)); Assert.True(cache.LastTopologyRebuilt);
        Assert.Equal(saved, Bytes(first)); Assert.Equal(otherSaved, Bytes(otherBounds));
    }

    [Fact]
    public void SkewedMaterialSizesMatchSerialAndOracleAcrossAllAvailableDegrees()
    {
        int[] counts = [3, 6 * Chunk, 9, 3 * Chunk + 3, 6, 9 * Chunk, 12, 3 * Chunk];
        int totalVertices = counts.Sum() + 5;
        var vertices = Enumerable.Range(0, totalVertices)
            .Select(i => new SceneVertex(new(i % 2 == 0 ? NegativeZero : i * .125f, (i % 11) - 5, .25f * (i % 7)), Vector3.UnitY, Vector2.Zero))
            .ToArray();
        var indices = new List<uint>(counts.Sum());
        var materials = new List<SceneMaterial>(counts.Length);
        int cursor = 0;
        for (int m = 0; m < counts.Length; m++)
        {
            materials.Add(new($"m{m}", cursor, counts[m], Vector4.One, Vector3.Zero, Vector3.Zero, 1));
            for (int k = 0; k < counts[m]; k++) indices.Add((uint)((cursor + k) % totalVertices));
            cursor += counts[m];
        }
        var scene = new SceneSnapshot(vertices, indices.ToArray(), materials);
        var dense = Dense(scene);
        var serial = NewCache(1, Budget);
        var expectedFirst = serial.GetSurface(scene);
        var expectedDense = serial.GetSurface(dense);
        EqualOracle(scene, Array.Empty<Vector4>(), 0, expectedFirst);
        EqualOracle(dense, Array.Empty<Vector4>(), 0, expectedDense);
        for (int degree = 2; degree <= Math.Min(16, Environment.ProcessorCount); degree++)
        {
            var parallel = NewCache(degree, Budget);
            Equal(expectedFirst, parallel.GetSurface(scene));
            Equal(expectedDense, parallel.GetSurface(dense));
            EqualStatistics(serial, parallel);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void ExplicitDegreeRejectsValuesOutsideOneThroughSixteen(int degree)
        => Assert.Throws<ArgumentOutOfRangeException>(() => NewCache(degree));

    private static D3D12VisibilityBoundsCache NewCache(int degree, long budget = Budget, int capacity = 1)
    {
        var constructor = typeof(D3D12VisibilityBoundsCache).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, [typeof(int), typeof(long), typeof(int)], modifiers: null);
        Assert.NotNull(constructor);
        try { return (D3D12VisibilityBoundsCache)constructor.Invoke([capacity, budget, degree]); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static int ParallelMaterials(D3D12VisibilityBoundsCache cache)
    {
        var property = typeof(D3D12VisibilityBoundsCache).GetProperty("LastParallelMaterialCount", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(property); return (int)property.GetValue(cache)!;
    }
    private static void AssertParallel(D3D12VisibilityBoundsCache cache, int degree, int materials = Materials)
        => Assert.Equal(Math.Min(Environment.ProcessorCount, degree) > 1 ? materials : 0, ParallelMaterials(cache));
    private static void EqualStatistics(D3D12VisibilityBoundsCache expected, D3D12VisibilityBoundsCache actual)
    {
        Assert.Equal(expected.LastComparedVertices, actual.LastComparedVertices);
        Assert.Equal(expected.LastPositionComparisons, actual.LastPositionComparisons);
        Assert.Equal(expected.LastRebuiltMaterials, actual.LastRebuiltMaterials);
        Assert.Equal(expected.LastMaterialRangeComparisons, actual.LastMaterialRangeComparisons);
        Assert.Equal(expected.LastRebuiltLeaves, actual.LastRebuiltLeaves);
        Assert.Equal(expected.LastScannedIndices, actual.LastScannedIndices);
        Assert.Equal(expected.LastFallbackMaterials, actual.LastFallbackMaterials);
        Assert.Equal(expected.LastFullScanMaterials, actual.LastFullScanMaterials);
        Assert.Equal(expected.LastMarkedLeafIncidences, actual.LastMarkedLeafIncidences);
        Assert.Equal(expected.LastTopologyRebuilt, actual.LastTopologyRebuilt); Assert.Equal(expected.EntryCount, actual.EntryCount);
    }
    private static SceneSnapshot Scene(int materials = Materials, int materialIndices = MaterialIndices)
    {
        int count = materials * materialIndices + 7;
        var vertices = Enumerable.Range(0, count).Select(index => new SceneVertex(new(index * .125f, index % 7, .5f), Vector3.UnitY, Vector2.Zero)).ToArray();
        var indices = Enumerable.Range(0, materials * materialIndices).Select(index => (uint)index).ToArray();
        if (materialIndices > Chunk)
            for (int material = 0; material < materials; material++)
            {
                int start = material * materialIndices;
                indices[start] = 0; indices[start + 255] = Chunk - 1; indices[start + 256] = Chunk;
                indices[start + 257] = Chunk; indices[start + materialIndices - 1] = (uint)(count - 1);
            }
        return new(vertices, indices, Enumerable.Range(0, materials + 1).Select(index =>
            new SceneMaterial($"material{index}", index * materialIndices, index == materials ? 0 : materialIndices,
                Vector4.One, Vector3.Zero, Vector3.Zero, 1)));
    }
    private static SceneVertex[] Vertices(SceneSnapshot scene)
    { var vertices = new SceneVertex[scene.VertexCount]; scene.CopyVertices(0, vertices); return vertices; }
    private static Vector4[] Positions(SceneSnapshot scene) => Vertices(scene).Select(v => new Vector4(v.Position, 1)).ToArray();
    private static SceneSnapshot Dense(SceneSnapshot scene)
        => scene.WithDeformedVertices(Vertices(scene).Select(v => v with { Position = v.Position + new Vector3(2, 3, 4) }).ToArray());
    private static SceneSnapshot DenseOneMaterial(SceneSnapshot scene)
    {
        var vertices = Vertices(scene);
        for (int index = MaterialIndices + 300; index < 2 * MaterialIndices - 1; index++)
            vertices[index] = vertices[index] with { Position = vertices[index].Position + Vector3.One };
        return scene.WithDeformedVertices(vertices);
    }
    private static object Input(SceneSnapshot scene, int reader)
    { var positions = Positions(scene); return reader == 2 ? Owned(scene, positions) : positions; }
    private static D3D12ExpandedPositions Owned(SceneSnapshot scene, Vector4[] positions)
    {
        var chunks = new Vector4[(positions.Length + Chunk - 1) / Chunk][];
        for (int index = 0; index < chunks.Length; index++)
            chunks[index] = positions.AsSpan(index * Chunk, Math.Min(Chunk, positions.Length - index * Chunk)).ToArray();
        var independent = scene.VertexCount == 0 ? SceneSnapshot.CreateEmpty()
            : new SceneSnapshot(Vertices(scene), scene.Indices, scene.Materials);
        return new(positions.Length, chunks, D3D12ExpandedPositionRevision.Create(independent.VertexRevision, 0, ReadOnlySpan<int>.Empty));
    }
    private static OriginalVisibilityBounds[] Get(D3D12VisibilityBoundsCache cache, SceneSnapshot scene, object input, int reader)
        => reader == 0 ? cache.GetSurface(scene) : input is D3D12ExpandedPositions owned
            ? cache.GetOutline(scene, owned) : cache.GetOutline(scene, (Vector4[])input);
    private static void EqualOracle(SceneSnapshot scene, object input, int reader, OriginalVisibilityBounds[] actual)
    {
        var positions = input is D3D12ExpandedPositions owned ? new Vector4[owned.Length] : (Vector4[])input;
        if (input is D3D12ExpandedPositions output) output.CopyTo(positions);
        Equal(reader == 0 ? OriginalFrustumVisibility.SurfaceMaterialBounds(scene)
            : OriginalFrustumVisibility.PositionMaterialBounds(scene, positions), actual);
    }
    private static byte[] Bytes(OriginalVisibilityBounds[] bounds) => MemoryMarshal.AsBytes(bounds.AsSpan()).ToArray();
    private static void Equal(OriginalVisibilityBounds[] expected, OriginalVisibilityBounds[] actual) => Assert.Equal(Bytes(expected), Bytes(actual));
    private static void AssertUnmaterialized(object input)
    { if (input is D3D12ExpandedPositions owned) Assert.False(owned.IsMaterialized); }
    private static bool IsMaterialized(SceneSnapshot scene)
    {
        var storage = typeof(SceneSnapshot).GetField("vertexStorage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
        return (bool)storage.GetType().GetProperty("IsMaterialized", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(storage)!;
    }
    private static void CorruptPosition(SceneSnapshot scene, int index, Vector3 value)
    {
        var chunk = scene.GetVertexChunk(index / Chunk);
        var writable = MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(chunk), chunk.Length);
        writable[index % Chunk] = writable[index % Chunk] with { Position = value };
    }
    private sealed class Writer(Func<SceneVertex, SceneVertex> edit)
    { public void Write(int _, Span<SceneVertex> destination) { for (int index = 0; index < destination.Length; index++) destination[index] = edit(destination[index]); } }
    private static SceneSnapshot Compose(SceneSnapshot scene, int index, Func<SceneVertex, SceneVertex> edit)
    {
        var method = typeof(SceneSnapshot).GetMethod("WithComposedVertices", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var writer = Delegate.CreateDelegate(method.GetParameters()[1].ParameterType, new Writer(edit), typeof(Writer).GetMethod(nameof(Writer.Write))!);
        try { return (SceneSnapshot)method.Invoke(scene, [new[] { (index, 1) }, writer])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
}
