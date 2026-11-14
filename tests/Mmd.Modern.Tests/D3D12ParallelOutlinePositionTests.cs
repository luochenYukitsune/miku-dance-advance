using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12ParallelOutlinePositionTests
{
    private const int Chunk = 4096;
    private const int Count = 64 * Chunk + 7;

    [Theory]
    [InlineData(false, 8)]
    [InlineData(true, 16)]
    public void LargeBuildAndChangedBlocksMatchSerialOracleAndPreservePublishedChunks(bool modern, int degree)
    {
        var serial = NewCache(1);
        var parallel = NewCache(degree);
        var options = OriginalOutlineTests.Options();
        var flags = modern ? Flags() : null;
        var root = ExistingData(Count);
        var serialFirst = serial.GetChunks(root, options, flags);
        var first = parallel.GetChunks(root, options, flags);
        AssertParallelBranch(parallel, degree);
        Assert.Equal(0, ParallelChunks(serial));
        Equal(serialFirst, first);
        EqualOracle(first, root, options, flags);
        EqualCounters(serial, parallel);
        var held = Bytes(first);

        // Multiple changed actor-like ranges, including two updates in the same chunk,
        // an inter-chunk boundary, and the short final chunk.
        var changed = ExistingPatch(root, [(Chunk - 1, 2), (5 * Chunk + 2, 1),
            (5 * Chunk + 20, 2), (16 * Chunk, 32 * Chunk), (Count - 1, 1)]);
        var serialNext = serial.GetChunks(changed, options, flags);
        var next = parallel.GetChunks(changed, options, flags);
        AssertParallelBranch(parallel, degree);
        Equal(serialNext, next);
        EqualOracle(next, changed, options, flags);
        EqualCounters(serial, parallel);
        Assert.Equal(held, Bytes(first));
        Assert.False(first.IsMaterialized);
        Assert.False(next.IsMaterialized);
        for (int index = 0; index < first.ChunkCount; index++)
            Assert.Equal(serialFirst.SharesChunk(serialNext, index), first.SharesChunk(next, index));

        // Backtracking to a retained root requires comparing the actual last output,
        // including independently changed branches and untouched chunks.
        var branch = ExistingPatch(root, [(8 * Chunk, 24 * Chunk)]);
        foreach (var data in new[] { root, branch, changed, root })
        {
            var expected = serial.GetChunks(data, options, flags);
            var actual = parallel.GetChunks(data, options, flags);
            Equal(expected, actual);
            EqualOracle(actual, data, options, flags);
            EqualCounters(serial, parallel);
            Assert.Equal(held, Bytes(first));
        }
    }

    [Fact]
    public void WidthAndModelBaseChangesRemainBitExactForBothModesAndKeepPriorOutputImmutable()
    {
        var serial = NewCache(1);
        var parallel = NewCache(16);
        var root = ExistingData(Count);
        var options = OriginalOutlineTests.Options();
        var flags = Flags();
        var first = parallel.GetChunks(root, options, flags);
        _ = serial.GetChunks(root, options, flags);
        var held = Bytes(first);
        flags[0] = flags[0] with { CharacterOutlineStyle = flags[0].CharacterOutlineStyle! with { Width = .5f } };
        var changed = parallel.GetChunks(root, options, flags);
        var expected = serial.GetChunks(root, options, flags);
        AssertParallelBranch(parallel, 16);
        Equal(expected, changed); EqualOracle(changed, root, options, flags); EqualCounters(serial, parallel);
        options = options with { CameraPosition = options.CameraPosition + new Vector3(.2f, -.3f, .5f) };
        changed = parallel.GetChunks(root, options, flags);
        expected = serial.GetChunks(root, options, flags);
        AssertParallelBranch(parallel, 16);
        Equal(expected, changed); EqualOracle(changed, root, options, flags); EqualCounters(serial, parallel);
        Assert.Equal(held, Bytes(first));
    }

    [Fact]
    public void SingleDirtyBlockAndSmallInputUseSerialPath()
    {
        var cache = NewCache(16);
        var options = OriginalOutlineTests.Options();
        var small = ExistingData(Chunk + 7);
        EqualOracle(cache.GetChunks(small, options), small, options, null);
        Assert.Equal(0, ParallelChunks(cache));
        var large = ExistingData(Count);
        var old = cache.GetChunks(large, options);
        AssertParallelBranch(cache, 16);
        var changed = ExistingPatch(large, [(2 * Chunk + 7, 1)]);
        var next = cache.GetChunks(changed, options);
        Assert.Equal(0, ParallelChunks(cache));
        Assert.Equal(1, cache.LastExpandedVertices);
        Assert.Equal(1, cache.LastCopiedChunks);
        Assert.False(old.SharesChunk(next, 2));
        Assert.True(old.SharesChunk(next, 1));
        EqualOracle(next, changed, options, null);
    }

    private static D3D12OutlinePositionCache NewCache(int degree)
    {
        var constructor = typeof(D3D12OutlinePositionCache).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [typeof(int)], null);
        Assert.NotNull(constructor);
        return (D3D12OutlinePositionCache)constructor.Invoke([degree]);
    }

    // Reports the number of disjoint chunks scheduled by the parallel branch, not the
    // number of distinct OS threads or proof of any particular speedup.
    private static int ParallelChunks(D3D12OutlinePositionCache cache)
    {
        var property = typeof(D3D12OutlinePositionCache).GetProperty("LastParallelChunkCount",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return Assert.IsType<int>(property.GetValue(cache));
    }

    private static void AssertParallelBranch(D3D12OutlinePositionCache cache, int degree)
    {
        int effective = Math.Min(Math.Min(Environment.ProcessorCount, degree), 16);
        if (effective > 1) Assert.True(ParallelChunks(cache) >= 2);
        else Assert.Equal(0, ParallelChunks(cache));
    }

    private static OriginalOutlineData ExistingData(int count)
    {
        var method = typeof(D3D12ExpandedPositionChunksTests).GetMethod("Data", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.CreateDelegate<Func<int, OriginalOutlineData>>()(count);
    }

    private static OriginalOutlineData ExistingPatch(OriginalOutlineData source, (int Start, int Count)[] ranges)
    {
        var method = typeof(D3D12ExpandedPositionChunksTests).GetMethod("Patch", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.CreateDelegate<Func<OriginalOutlineData, (int Start, int Count)[], OriginalOutlineData>>()(source, ranges);
    }

    private static OriginalFrameMaterialFlags[] Flags() =>
        [new(false, false, false) { CharacterOutlineStyle = new(.2f) }, new(false, false, false)];

    private static void EqualCounters(D3D12OutlinePositionCache expected, D3D12OutlinePositionCache actual)
    {
        Assert.Equal(expected.LastComparedVertices, actual.LastComparedVertices);
        Assert.Equal(expected.LastExpandedVertices, actual.LastExpandedVertices);
        Assert.Equal(expected.LastCopiedChunks, actual.LastCopiedChunks);
    }

    private static void Equal(D3D12ExpandedPositions expected, D3D12ExpandedPositions actual)
        => Assert.Equal(Bytes(expected), Bytes(actual));

    private static byte[] Bytes(D3D12ExpandedPositions value)
    {
        var data = new Vector4[value.Length]; value.CopyTo(data);
        return MemoryMarshal.AsBytes(data.AsSpan()).ToArray();
    }

    private static void EqualOracle(D3D12ExpandedPositions actual, OriginalOutlineData data,
        OriginalOutlineOptions options, IReadOnlyList<OriginalFrameMaterialFlags>? flags)
    {
        var expected = flags is null ? OriginalOutlineMath.BuildPositions(data, options)
            : CharacterOutlineMath.BuildPositions(data, options, flags);
        Assert.Equal(MemoryMarshal.AsBytes(expected.AsSpan()).ToArray(), Bytes(actual));
    }
}
