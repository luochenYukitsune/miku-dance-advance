using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12ExpandedPositionRevisionTests
{
    private const int Chunk = 4096;
    private const int Count = 4 * Chunk + 7;

    [Fact]
    public void BoundsSkipsUnchangedOutputChunksAndClipsTheTail()
    {
        var scene = Scene(); var source = Data(scene); var options = OriginalOutlineTests.Options();
        var positions = new D3D12OutlinePositionCache(); var bounds = new D3D12VisibilityBoundsCache(1);
        var before = positions.Get(source, options); var saved = Bytes(before);
        var firstBounds = bounds.GetOutline(scene, before); var savedBounds = Bytes(firstBounds);
        var changed = Compose(source, [(Chunk - 1, 2)]);
        var next = positions.Get(changed, options);
        EqualBounds(scene, next, bounds.GetOutline(scene, next));
        Assert.Equal(2 * Chunk, bounds.LastComparedVertices); Assert.Equal(1, bounds.LastRebuiltMaterials);
        var tail = Compose(changed, [(4 * Chunk + 2, 1)]);
        next = positions.Get(tail, options);
        EqualBounds(scene, next, bounds.GetOutline(scene, next));
        Assert.Equal(7, bounds.LastComparedVertices); Assert.Equal(1, bounds.LastRebuiltMaterials);
        Assert.Equal(saved, Bytes(before)); Assert.Equal(savedBounds, Bytes(firstBounds));
        Assert.True(D3D12ExpandedPositionRevision.TryGet(next, out var proof));
        Assert.True(proof!.SupportsIncrementalUpdates); Assert.Equal(Count, proof.VertexCount);
    }

    [Fact]
    public void ArbitraryRetainedOutputsUseTheirOwnSourceRevisions()
    {
        var scene = Scene(); var source = Data(scene); var first = Compose(source, [(0, 1)]);
        var skipped = Compose(first, [(Chunk, 1)]); var latest = Compose(skipped, [(2 * Chunk, 1)]);
        var branch = Compose(source, [(3 * Chunk, 1)]); var options = OriginalOutlineTests.Options();
        // Independent producers ensure each retained output has its actual source proof.
        Vector4[] Build(OriginalOutlineData data) => new D3D12OutlinePositionCache().Get(data, options);
        var rootOutput = Build(source); var firstOutput = Build(first); var latestOutput = Build(latest); var branchOutput = Build(branch);
        var cache = new D3D12VisibilityBoundsCache(1); _ = cache.GetOutline(scene, rootOutput);
        (Vector4[] Output, int Compared)[] sequence =
        [(latestOutput, 3 * Chunk), (rootOutput, 3 * Chunk), (firstOutput, Chunk),
            (branchOutput, 2 * Chunk), (latestOutput, 4 * Chunk), (latestOutput, 0)];
        foreach (var step in sequence)
        {
            EqualBounds(scene, step.Output, cache.GetOutline(scene, step.Output));
            Assert.Equal(step.Compared, cache.LastComparedVertices); Assert.False(cache.LastTopologyRebuilt);
        }
    }

    [Fact]
    public void UnknownArraysAndDifferentExpansionParametersUseFullComparison()
    {
        var scene = Scene(); var source = Data(scene); var options = OriginalOutlineTests.Options();
        var producer = new D3D12OutlinePositionCache(); var cache = new D3D12VisibilityBoundsCache(1);
        var before = producer.Get(source, options); _ = cache.GetOutline(scene, before);
        var clone = before.ToArray(); clone[0] += new Vector4(-10, 0, 0, 0);
        Assert.False(D3D12ExpandedPositionRevision.TryGet(clone, out _));
        EqualBounds(scene, clone, cache.GetOutline(scene, clone)); Assert.Equal(Count, cache.LastComparedVertices);
        var changed = Compose(source, [(Chunk, 1)]); var next = producer.Get(changed, options);
        EqualBounds(scene, next, cache.GetOutline(scene, next)); Assert.Equal(Count, cache.LastComparedVertices);
        var widths = changed.Materials.Select(material => material with { Width = .9f }).ToArray();
        var wider = Compose(changed, [], materials: widths); next = producer.Get(wider, options);
        EqualBounds(scene, next, cache.GetOutline(scene, next)); Assert.Equal(Count, cache.LastComparedVertices);
        var moved = options with { CameraPosition = options.CameraPosition + new Vector3(1, 0, -3) };
        Assert.NotEqual(OriginalOutlineMath.ComputeModelBase(options), OriginalOutlineMath.ComputeModelBase(moved));
        next = producer.Get(wider, moved);
        EqualBounds(scene, next, cache.GetOutline(scene, next)); Assert.Equal(Count, cache.LastComparedVertices);
    }

    [Fact]
    public void ReusedOutputKeepsAnOlderConservativeProofAndUnusedChangesAdvanceBounds()
    {
        var scene = Scene(); var source = Data(scene); var options = OriginalOutlineTests.Options();
        var producer = new D3D12OutlinePositionCache(); var cache = new D3D12VisibilityBoundsCache(1);
        var before = producer.Get(source, options); var initialBounds = cache.GetOutline(scene, before);
        Assert.True(D3D12ExpandedPositionRevision.TryGet(before, out var oldProof));
        var unchanged = Compose(source, [(Chunk, 1)], vertex => vertex);
        Assert.Same(before, producer.Get(unchanged, options));
        Assert.True(D3D12ExpandedPositionRevision.TryGet(before, out var sameProof)); Assert.Same(oldProof, sameProof);
        var unused = Compose(unchanged, [(Count - 1, 1)]); var next = producer.Get(unused, options);
        Assert.Same(initialBounds, cache.GetOutline(scene, next));
        Assert.Equal(Chunk + 7, cache.LastComparedVertices); Assert.Equal(0, cache.LastRebuiltMaterials);
        var last = Compose(unused, [(2 * Chunk, 1)]); next = producer.Get(last, options);
        EqualBounds(scene, next, cache.GetOutline(scene, next)); Assert.Equal(Chunk, cache.LastComparedVertices);
        Assert.Equal(1, cache.LastRebuiltMaterials);
        var metadata = scene.WithLighting(Vector3.UnitX).WithWorld(Matrix4x4.CreateTranslation(4, 3, 2));
        Assert.True(cache.TryGetCurrent(metadata, next, out var current));
        EqualBounds(metadata, next, current!); Assert.Equal(0, cache.LastComparedVertices);
    }

    [Fact]
    public void ModernWidthOverridesArePartOfTheProofAndPublishedWidthsAreCopied()
    {
        var scene = Scene(); var source = Data(scene); var options = OriginalOutlineTests.Options();
        var flags = source.Materials.Select(_ => new OriginalFrameMaterialFlags(false, false, false)
            { CharacterOutlineStyle = new(.2f) }).ToArray();
        var producer = new D3D12OutlinePositionCache(); var before = producer.Get(source, options, flags);
        Assert.True(D3D12ExpandedPositionRevision.TryGet(before, out var beforeProof));
        flags[0] = flags[0] with { CharacterOutlineStyle = new(.8f) };
        var next = producer.Get(Compose(source, [(2 * Chunk, 1)]), options, flags);
        Assert.True(D3D12ExpandedPositionRevision.TryGet(next, out var nextProof));
        Assert.Equal(new[] { new SceneVertexRange(0, Count) }, nextProof!.GetChangedRanges(beforeProof!));
        // The cache's private width table must not be a mutable alias of published proof metadata.
        var widthField = typeof(D3D12OutlinePositionCache).GetField("previousWidths", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cacheWidths = (int[])widthField.GetValue(producer)!; cacheWidths[0] = BitConverter.SingleToInt32Bits(.2f);
        Assert.Equal(new[] { new SceneVertexRange(0, Count) }, nextProof.GetChangedRanges(beforeProof!));
    }

    [Fact]
    public void SignedZeroAndNonfiniteExpandedPositionsRetainTheFourBitOracle()
    {
        var scene = Scene(); var source = Data(scene); var options = OriginalOutlineTests.Options() with { ModelWidth = 0 };
        var producer = new D3D12OutlinePositionCache(); var cache = new D3D12VisibilityBoundsCache(1);
        _ = cache.GetOutline(scene, producer.Get(source, options));
        var negativeZero = Compose(source, [(0, 1)], vertex => vertex with
        { Position = vertex.Position with { X = BitConverter.Int32BitsToSingle(int.MinValue) }, Normal = new(-1, 0, 0) });
        var next = producer.Get(negativeZero, options);
        EqualBounds(scene, next, cache.GetOutline(scene, next)); Assert.Equal(Chunk, cache.LastComparedVertices);
        var extreme = Compose(negativeZero, [(2 * Chunk, 1)], vertex => vertex with { Normal = new(float.MaxValue) });
        next = producer.Get(extreme, options with { ModelWidth = float.MaxValue });
        var actual = cache.GetOutline(scene, next); EqualBounds(scene, next, actual);
        Assert.Equal(Count, cache.LastComparedVertices); Assert.True(float.IsNaN(actual[3].Minimum.X));
    }

    [Fact]
    public void RetainedProofDoesNotRetainItsOutputSourceDataOrProducer()
    {
        var (proof, output, source, producer, parentProof) = WeakProof();
        for (int attempt = 0; attempt < 3 && (output.IsAlive || source.IsAlive || producer.IsAlive || parentProof.IsAlive); attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(output.IsAlive); Assert.False(source.IsAlive); Assert.False(producer.IsAlive); Assert.False(parentProof.IsAlive);
        Assert.Equal(Count, proof.VertexCount); GC.KeepAlive(proof);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (D3D12ExpandedPositionRevision Proof, WeakReference Output, WeakReference Source, WeakReference Producer, WeakReference ParentProof) WeakProof()
    {
        var source = Data(Scene()); var producer = new D3D12OutlinePositionCache();
        var before = producer.Get(source, OriginalOutlineTests.Options());
        Assert.True(D3D12ExpandedPositionRevision.TryGet(before, out var parentProof));
        var composed = Compose(source, [(0, 1)]);
        var output = producer.Get(composed, OriginalOutlineTests.Options());
        Assert.True(D3D12ExpandedPositionRevision.TryGet(output, out var proof));
        return (proof!, new(output), new(composed), new(producer), new(parentProof!));
    }

    private sealed class Writer(Func<OriginalOutlineVertex, OriginalOutlineVertex> edit)
    {
        public void Write(int rangeIndex, Span<OriginalOutlineVertex> destination)
        { for (int index = 0; index < destination.Length; index++) destination[index] = edit(destination[index]); }
    }

    private static OriginalOutlineData Compose(OriginalOutlineData source, (int Start, int Count)[] ranges,
        Func<OriginalOutlineVertex, OriginalOutlineVertex>? edit = null, OriginalOutlineMaterial[]? materials = null)
    {
        var method = typeof(OriginalOutlineData).GetMethod("WithComposedVertices", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var writer = Delegate.CreateDelegate(method.GetParameters()[1].ParameterType,
            new Writer(edit ?? (vertex => vertex with { Position = vertex.Position + new Vector3(.25f, -.5f, .75f) })),
            typeof(Writer).GetMethod(nameof(Writer.Write))!);
        try { return (OriginalOutlineData)method.Invoke(source, [ranges, writer, materials ?? source.Materials.ToArray()])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private static OriginalOutlineData Data(SceneSnapshot scene) => new(scene.Vertices.ToArray().Select(vertex =>
        new OriginalOutlineVertex(vertex.Position, Vector3.UnitY, OriginalOutlineVertexMode.Expand, 1, 0)),
        scene.Materials.Select(_ => new OriginalOutlineMaterial(.2f, Vector4.One)));
    private static SceneSnapshot Scene() => new(Enumerable.Range(0, Count).Select(index => new SceneVertex(
        new(index * .00037f, index % 7 * .13f, index % 13 * .11f), Vector3.UnitY, Vector2.Zero)).ToArray(),
        [0, 1, 2, Chunk - 1, Chunk, Chunk + 1, 2 * Chunk, 2 * Chunk + 1, 2 * Chunk + 2,
            0, 3 * Chunk, 3 * Chunk + 1, 4 * Chunk, 4 * Chunk + 1, 4 * Chunk + 2],
        [Material("shared-a", 0, 3), Material("empty", 3, 0), Material("boundary", 3, 3),
            Material("middle", 6, 3), Material("shared-b", 9, 3), Material("tail", 12, 3)]);
    private static SceneMaterial Material(string name, int start, int count) => new(name, start, count, Vector4.One, Vector3.Zero, Vector3.Zero, 1);
    private static byte[] Bytes<T>(T[] values) where T : struct => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    private static void EqualBounds(SceneSnapshot scene, Vector4[] positions, OriginalVisibilityBounds[] actual)
        => Assert.Equal(Bytes(OriginalFrustumVisibility.PositionMaterialBounds(scene, positions)), Bytes(actual));
}
