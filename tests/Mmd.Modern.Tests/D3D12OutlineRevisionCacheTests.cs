using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12OutlineRevisionCacheTests
{
    private const int Chunk = 4096;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossChunkEditSkipsUnchangedChunksAndRetainsOldOutput(bool modern)
    {
        var data = Data(); var options = OriginalOutlineTests.Options(); var flags = modern ? Flags() : null;
        var cache = new D3D12OutlinePositionCache(); var before = cache.Get(data, options, flags); var saved = Bytes(before);
        var changed = Compose(data, [(Chunk - 1, 2)]);
        var actual = cache.Get(changed, options, flags);
        Assert.Equal(2 * Chunk, cache.LastComparedVertices); Assert.Equal(2, cache.LastExpandedVertices);
        EqualFull(actual, changed, options, flags); Assert.Equal(saved, Bytes(before));
    }

    [Fact]
    public void SkippedSnapshotsBackwardRenderingAndBranchesCompareAgainstActualCachedInput()
    {
        var root = Data(); var first = Compose(root, [(4, 1)]); var skipped = Compose(first, [(Chunk + 4, 1)]);
        var latest = Compose(skipped, [(2 * Chunk + 4, 1)]); var branch = Compose(root, [(3 * Chunk, 1)]);
        var cache = new D3D12OutlinePositionCache(); var options = OriginalOutlineTests.Options();
        foreach (var input in new[] { root, latest, root, first, branch, latest })
            EqualFull(cache.Get(input, options), input, options, null);
        var expected = Bytes(OriginalOutlineMath.BuildPositions(latest, options));
        Assert.Equal(expected, Bytes(cache.Get(latest, options))); Assert.Equal(0, cache.LastComparedVertices);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WidthAndModelBaseChangesStillInspectAllVertices(bool modern)
    {
        var data = Data(); var options = OriginalOutlineTests.Options(); var flags = modern ? Flags() : null;
        var cache = new D3D12OutlinePositionCache(); _ = cache.Get(data, options, flags);
        var changed = Compose(data, [(5, 1)], [new(.9f, Vector4.One)]);
        if (modern) flags = [new(false, false, false) { CharacterOutlineStyle = new(.9f) }];
        EqualFull(cache.Get(changed, options, flags), changed, options, flags);
        Assert.Equal(data.Vertices.Count, cache.LastComparedVertices);
        var previousBase = OriginalOutlineMath.ComputeModelBase(options);
        options = options with { CameraPosition = options.CameraPosition + new Vector3(1, 0, -3) };
        Assert.NotEqual(previousBase, OriginalOutlineMath.ComputeModelBase(options));
        EqualFull(cache.Get(changed, options, flags), changed, options, flags);
        Assert.Equal(data.Vertices.Count, cache.LastComparedVertices);
    }

    [Fact]
    public void MaterialOnlyChangesReusePositionsButUnknownFreshDataUsesConservativeComparison()
    {
        var data = Data(); var options = OriginalOutlineTests.Options(); var cache = new D3D12OutlinePositionCache();
        var before = cache.Get(data, options);
        var materialOnly = Compose(data, [], [new(.2f, new(.3f, .4f, .5f, 1))]);
        Assert.Same(before, cache.Get(materialOnly, options)); Assert.Equal(0, cache.LastComparedVertices);
        var unknown = new OriginalOutlineData(materialOnly.Vertices, materialOnly.Materials);
        Assert.Same(before, cache.Get(unknown, options)); Assert.Equal(data.Vertices.Count, cache.LastComparedVertices);
        Assert.Equal(0, cache.LastExpandedVertices);
    }

    private sealed class Writer
    {
        public void Write(int rangeIndex, Span<OriginalOutlineVertex> destination)
        {
            _ = rangeIndex;
            for (int i = 0; i < destination.Length; i++)
                destination[i] = destination[i] with { Position = destination[i].Position + new Vector3(.25f, -.125f, .5f) };
        }
    }

    private static OriginalOutlineData Compose(OriginalOutlineData source, (int Start, int Count)[] ranges,
        OriginalOutlineMaterial[]? materials = null)
    {
        var method = typeof(OriginalOutlineData).GetMethod("WithComposedVertices", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var writer = Delegate.CreateDelegate(method.GetParameters()[1].ParameterType, new Writer(), typeof(Writer).GetMethod(nameof(Writer.Write))!);
        try { return (OriginalOutlineData)method.Invoke(source, [ranges, writer, materials ?? source.Materials.ToArray()])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private static OriginalOutlineData Data() => new(Enumerable.Range(0, 3 * Chunk + 3).Select(i => new OriginalOutlineVertex(
        new(i * .00037f, i % 7 * .13f, -i * .0011f), new(i % 3 * .3f, -.2f, i % 5 * .4f),
        i % 9 == 0 ? OriginalOutlineVertexMode.Inward : OriginalOutlineVertexMode.Expand, .3f + i % 5 * .1f, 0)), [new(.2f, Vector4.One)]);
    private static OriginalFrameMaterialFlags[] Flags() => [new(false, false, false) { CharacterOutlineStyle = new(.2f) }];
    private static byte[] Bytes(Vector4[] positions) => MemoryMarshal.AsBytes(positions.AsSpan()).ToArray();
    private static void EqualFull(Vector4[] actual, OriginalOutlineData data, OriginalOutlineOptions options, IReadOnlyList<OriginalFrameMaterialFlags>? flags)
        => Assert.Equal(Bytes(flags is null ? OriginalOutlineMath.BuildPositions(data, options) : CharacterOutlineMath.BuildPositions(data, options, flags)), Bytes(actual));
}
