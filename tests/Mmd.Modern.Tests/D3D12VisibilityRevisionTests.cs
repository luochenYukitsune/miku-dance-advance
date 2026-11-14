using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12VisibilityRevisionTests
{
    private const int Chunk = 4096;
    private const int VertexCount = 4 * Chunk + 7;

    [Fact]
    public void SurfaceComparesOnlyChangedChunksAndClipsTheFinalChunk()
    {
        var source = Scene(); var cache = new D3D12VisibilityBoundsCache(2);
        var before = cache.GetSurface(source); var saved = Bytes(before);
        var boundary = Compose(source, [(Chunk - 1, 2)]);
        var actual = cache.GetSurface(boundary);
        EqualSurface(boundary, actual);
        Assert.Equal(2 * Chunk, cache.LastComparedVertices);
        Assert.Equal(1, cache.LastRebuiltMaterials); Assert.False(cache.LastTopologyRebuilt);
        Assert.Equal(saved, Bytes(before));

        var tail = Compose(boundary, [(4 * Chunk + 2, 1)]);
        EqualSurface(tail, cache.GetSurface(tail));
        Assert.Equal(7, cache.LastComparedVertices); Assert.Equal(1, cache.LastRebuiltMaterials);
        Assert.Equal(saved, Bytes(before));
    }

    [Fact]
    public void SkippedBackwardAndBranchedFramesCompareAgainstTheActualCachedRevision()
    {
        var source = Scene(); var first = Compose(source, [(0, 1)]);
        var skipped = Compose(first, [(Chunk, 1)]);
        var latest = Compose(skipped, [(2 * Chunk, 1)]);
        var branch = Compose(source, [(3 * Chunk, 1)]);
        var cache = new D3D12VisibilityBoundsCache(2); _ = cache.GetSurface(source);
        (SceneSnapshot Scene, int Compared)[] sequence =
        [
            (latest, 3 * Chunk), (source, 3 * Chunk), (first, Chunk),
            (branch, 2 * Chunk), (latest, 4 * Chunk), (latest, 0)
        ];
        foreach (var step in sequence)
        {
            EqualSurface(step.Scene, cache.GetSurface(step.Scene));
            Assert.Equal(step.Compared, cache.LastComparedVertices);
            Assert.False(cache.LastTopologyRebuilt); Assert.Equal(1, cache.EntryCount);
        }
    }

    [Fact]
    public void NormalUvAndUnusedPositionChangesAdvanceTheBaselineWithoutRebuildingMaterials()
    {
        var source = Scene(); var cache = new D3D12VisibilityBoundsCache(2);
        var before = cache.GetSurface(source);
        var shading = Compose(source, [(Chunk, 1)], vertex => vertex with
        { Normal = -vertex.Normal, Uv = vertex.Uv + new Vector2(.25f, -.5f) });
        Assert.Same(before, cache.GetSurface(shading)); EqualSurface(shading, before);
        Assert.Equal(Chunk, cache.LastComparedVertices); Assert.Equal(0, cache.LastRebuiltMaterials);

        var changed = Compose(shading, [(3 * Chunk, 1)]);
        var actual = cache.GetSurface(changed); EqualSurface(changed, actual);
        Assert.Equal(Chunk, cache.LastComparedVertices); Assert.Equal(1, cache.LastRebuiltMaterials);
        var unused = Compose(changed, [(VertexCount - 1, 1)]);
        Assert.Same(actual, cache.GetSurface(unused)); EqualSurface(unused, actual);
        Assert.Equal(7, cache.LastComparedVertices); Assert.Equal(0, cache.LastRebuiltMaterials);

        var afterUnused = Compose(unused, [(2 * Chunk, 1)]);
        actual = cache.GetSurface(afterUnused); EqualSurface(afterUnused, actual);
        Assert.Equal(Chunk, cache.LastComparedVertices); Assert.Equal(1, cache.LastRebuiltMaterials);
        var wrapped = afterUnused.WithWorld(Matrix4x4.CreateTranslation(1, 2, 3))
            .WithCamera(source.Camera with { Eye = source.Camera.Eye + Vector3.UnitX })
            .WithLighting(Vector3.UnitX)
            .WithMaterials(afterUnused.Materials.Select(material => material with { Diffuse = new(.2f, .3f, .4f, 1) }));
        Assert.Same(actual, cache.GetSurface(wrapped)); EqualSurface(wrapped, actual);
        Assert.Equal(0, cache.LastComparedVertices); Assert.Equal(0, cache.LastRebuiltMaterials);
        Assert.True(cache.TryGetCurrent(wrapped, null, out var current)); Assert.Same(actual, current);
    }

    [Fact]
    public void PublicDeformationRootsCompareEveryVertexEvenWhenThePayloadIsEqual()
    {
        var source = Scene(); var cache = new D3D12VisibilityBoundsCache(2);
        var before = cache.GetSurface(source);
        var equal = source.WithDeformedVertices(source.Vertices.ToArray());
        Assert.Same(before, cache.GetSurface(equal)); EqualSurface(equal, before);
        Assert.Equal(VertexCount, cache.LastComparedVertices); Assert.Equal(0, cache.LastRebuiltMaterials);

        var payload = equal.Vertices.ToArray();
        payload[2 * Chunk] = Shift(payload[2 * Chunk]);
        var changed = equal.WithDeformedVertices(payload);
        EqualSurface(changed, cache.GetSurface(changed));
        Assert.Equal(VertexCount, cache.LastComparedVertices); Assert.Equal(1, cache.LastRebuiltMaterials);
        var trusted = Compose(changed, [(3 * Chunk, 1)]);
        EqualSurface(trusted, cache.GetSurface(trusted));
        Assert.Equal(Chunk, cache.LastComparedVertices);
        EqualSurface(source, cache.GetSurface(source));
        Assert.Equal(VertexCount, cache.LastComparedVertices); Assert.False(cache.LastTopologyRebuilt);
    }

    [Fact]
    public void SignedZeroAndUnchangedConservativeWritesRetainExactPositionBitComparison()
    {
        var source = Scene(); var cache = new D3D12VisibilityBoundsCache(2);
        var before = cache.GetSurface(source); var saved = Bytes(before);
        var negativeZero = Compose(source, [(0, 1)], vertex => vertex with
        { Position = vertex.Position with { X = BitConverter.Int32BitsToSingle(int.MinValue) } });
        EqualSurface(negativeZero, cache.GetSurface(negativeZero));
        Assert.Equal(Chunk, cache.LastComparedVertices); Assert.Equal(2, cache.LastRebuiltMaterials);
        EqualSurface(source, cache.GetSurface(source));
        Assert.Equal(Chunk, cache.LastComparedVertices); Assert.Equal(2, cache.LastRebuiltMaterials);
        var unchanged = Compose(source, [(0, 1)], vertex => vertex);
        var previous = cache.GetSurface(source);
        Assert.Same(previous, cache.GetSurface(unchanged)); EqualSurface(unchanged, previous);
        Assert.Equal(Chunk, cache.LastComparedVertices); Assert.Equal(0, cache.LastRebuiltMaterials);
        Assert.Equal(saved, Bytes(before));
        Assert.True(float.IsPositiveInfinity(before[1].Minimum.X));
        Assert.True(float.IsNegativeInfinity(before[1].Maximum.X));
    }

    [Fact]
    public void MaterialRangeEntriesUseTheirOwnRevisionsAndTopologyReplacementStaysBounded()
    {
        var source = Scene(); var cache = new D3D12VisibilityBoundsCache(2); _ = cache.GetSurface(source);
        var ranged = source.WithMaterials(
        [
            Material("combined", 0, 6), Material("empty", 6, 0), Material("also-empty", 6, 0),
            Material("middle", 6, 3), Material("shared-b", 9, 3), Material("tail", 12, 3)
        ]);
        EqualSurface(ranged, cache.GetSurface(ranged));
        Assert.True(cache.LastTopologyRebuilt); Assert.Equal(6, cache.LastRebuiltMaterials);
        var rangedChanged = Compose(ranged, [(2 * Chunk, 1)]);
        EqualSurface(rangedChanged, cache.GetSurface(rangedChanged));
        Assert.Equal(Chunk, cache.LastComparedVertices); Assert.Equal(1, cache.LastRebuiltMaterials);

        var originalRangeChanged = Compose(source, [(Chunk, 1)]);
        EqualSurface(originalRangeChanged, cache.GetSurface(originalRangeChanged));
        Assert.Equal(Chunk, cache.LastComparedVertices); Assert.False(cache.LastTopologyRebuilt);
        Assert.Equal(2, cache.EntryCount);
        var indices = originalRangeChanged.Indices.ToArray(); (indices[0], indices[6]) = (indices[6], indices[0]);
        var topology = new SceneSnapshot(originalRangeChanged.Vertices, indices, originalRangeChanged.Materials);
        EqualSurface(topology, cache.GetSurface(topology));
        Assert.True(cache.LastTopologyRebuilt); Assert.Equal(0, cache.LastComparedVertices);
        Assert.Equal(2, cache.EntryCount);
        var longer = new SceneSnapshot([..topology.Vertices.ToArray(), new(Vector3.One, Vector3.UnitY, Vector2.Zero)],
            topology.Indices, topology.Materials);
        EqualSurface(longer, cache.GetSurface(longer));
        Assert.True(cache.LastTopologyRebuilt); Assert.Equal(2, cache.EntryCount);
    }

    [Fact]
    public void OutlineArraysStillCompareEveryVertexOutsideTheSceneRevisionRanges()
    {
        var source = Scene(); var cache = new D3D12VisibilityBoundsCache(1);
        var positions = source.Vertices.ToArray().Select(vertex => new Vector4(vertex.Position, 1)).ToArray();
        _ = cache.GetOutline(source, positions);
        var sceneChanged = Compose(source, [(Chunk, 1)]);
        var changed = positions.ToArray(); changed[0] += new Vector4(-9, 2, 3, 0);
        var actual = cache.GetOutline(sceneChanged, changed);
        Assert.Equal(Bytes(OriginalFrustumVisibility.PositionMaterialBounds(sceneChanged, changed)), Bytes(actual));
        Assert.Equal(VertexCount, cache.LastComparedVertices); Assert.Equal(2, cache.LastRebuiltMaterials);
        Assert.Same(actual, cache.GetOutline(sceneChanged.WithLighting(Vector3.UnitX), changed));
        Assert.Equal(0, cache.LastComparedVertices);
    }

    private sealed class Writer(Func<SceneVertex, SceneVertex> edit)
    {
        public void Write(int rangeIndex, Span<SceneVertex> destination)
        {
            _ = rangeIndex;
            for (int index = 0; index < destination.Length; index++) destination[index] = edit(destination[index]);
        }
    }

    private static SceneSnapshot Compose(SceneSnapshot source, (int Start, int Count)[] ranges,
        Func<SceneVertex, SceneVertex>? edit = null)
    {
        var method = typeof(SceneSnapshot).GetMethod("WithComposedVertices", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var writer = Delegate.CreateDelegate(method.GetParameters()[1].ParameterType,
            new Writer(edit ?? Shift), typeof(Writer).GetMethod(nameof(Writer.Write))!);
        try { return (SceneSnapshot)method.Invoke(source, [ranges, writer])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private static SceneSnapshot Scene() => new(
        Enumerable.Range(0, VertexCount).Select(index => new SceneVertex(
            new(index * .00037f, index % 7 * .13f, index % 13 * .11f), Vector3.UnitY, Vector2.Zero)).ToArray(),
        [0, 1, 2, Chunk - 1, Chunk, Chunk + 1, 2 * Chunk, 2 * Chunk + 1, 2 * Chunk + 2,
            0, 3 * Chunk, 3 * Chunk + 1, 4 * Chunk, 4 * Chunk + 1, 4 * Chunk + 2],
        [Material("shared-a", 0, 3), Material("empty", 3, 0), Material("boundary", 3, 3),
            Material("middle", 6, 3), Material("shared-b", 9, 3), Material("tail", 12, 3)]);
    private static SceneVertex Shift(SceneVertex vertex)
        => vertex with { Position = vertex.Position + new Vector3(.25f, -.5f, .75f) };
    private static SceneMaterial Material(string name, int start, int count)
        => new(name, start, count, Vector4.One, Vector3.Zero, Vector3.Zero, 1);
    private static byte[] Bytes(OriginalVisibilityBounds[] values) => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    private static void EqualSurface(SceneSnapshot scene, OriginalVisibilityBounds[] actual)
        => Assert.Equal(Bytes(OriginalFrustumVisibility.SurfaceMaterialBounds(scene)), Bytes(actual));
}
