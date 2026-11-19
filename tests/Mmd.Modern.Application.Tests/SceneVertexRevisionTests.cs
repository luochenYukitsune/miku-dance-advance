using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneVertexRevisionTests
{
    private const int Chunk = 4096;

    [Fact]
    public void MetadataWrappersKeepTheOwnedVertexRevision()
    {
        var source = Snapshot(Chunk + 3);
        var revision = source.VertexRevision;
        Assert.False(revision.SupportsIncrementalUpdates);
        Assert.Same(revision, source.WithCamera(SceneCamera.Default).VertexRevision);
        Assert.Same(revision, source.WithLighting(Vector3.UnitX).VertexRevision);
        Assert.Same(revision, source.WithWorld(Matrix4x4.CreateTranslation(1, 2, 3)).VertexRevision);
        Assert.Same(revision, source.WithMaterials(source.Materials).VertexRevision);
        Assert.Same(revision, source.WithAdditionalUvs(new(1, new SceneAdditionalUvVertex[Chunk + 3])).VertexRevision);
        Assert.Same(revision, source.WithRawRadialSeeds(new(new Vector3[Chunk + 3])).VertexRevision);
        Assert.Empty(revision.GetChangedRanges(revision));
    }

    [Fact]
    public void FreshAndPublicDeformationRootsRequireTheWholePayloadAndKeepDefensiveCopies()
    {
        var source = Snapshot(Chunk + 3);
        var input = source.Vertices.ToArray();
        var deformed = source.WithDeformedVertices(input);
        var independentlyConstructed = new SceneSnapshot(input, source.Indices, source.Materials);
        input[0] = input[0] with { Position = new(100, 200, 300) };
        Assert.Equal(source.Vertices[0], deformed.Vertices[0]);
        Assert.Equal(source.Vertices[0], independentlyConstructed.Vertices[0]);
        Assert.False(deformed.VertexRevision.SupportsIncrementalUpdates);
        Assert.False(independentlyConstructed.VertexRevision.SupportsIncrementalUpdates);
        Ranges(deformed.VertexRevision, source.VertexRevision, new SceneVertexRange(0, Chunk + 3));
        Ranges(independentlyConstructed.VertexRevision, source.VertexRevision, new SceneVertexRange(0, Chunk + 3));
    }

    [Fact]
    public void SkippedAndBackwardComparisonsCoverBothChangedChunksWithoutAnAncestorWalk()
    {
        var source = Snapshot(Chunk * 3 + 7);
        var first = Compose(source, [(2, 1)]);
        var second = Compose(first, [(Chunk * 2 + 5, 1)]);
        Assert.True(first.VertexRevision.SupportsIncrementalUpdates);
        Assert.True(second.VertexRevision.SupportsIncrementalUpdates);
        Assert.Same(second.VertexRevision, second.WithCamera(SceneCamera.Default).VertexRevision);
        Ranges(second.VertexRevision, source.VertexRevision, new SceneVertexRange(0, Chunk), new SceneVertexRange(Chunk * 2, Chunk));
        Ranges(source.VertexRevision, second.VertexRevision, new SceneVertexRange(0, Chunk), new SceneVertexRange(Chunk * 2, Chunk));
        Ranges(second.VertexRevision, first.VertexRevision, new SceneVertexRange(Chunk * 2, Chunk));
        Assert.NotEqual(source.Vertices[2].Position, first.Vertices[2].Position);
        Assert.Equal(source.Vertices[Chunk * 2 + 5].Position, first.Vertices[Chunk * 2 + 5].Position);
    }

    [Fact]
    public void BranchesCompareTheirIndependentDirtyChunks()
    {
        var source = Snapshot(Chunk * 4);
        var left = Compose(source, [(1, 1)]);
        var right = Compose(source, [(Chunk * 2 + 1, 1)]);
        Ranges(left.VertexRevision, right.VertexRevision, new SceneVertexRange(0, Chunk), new SceneVertexRange(Chunk * 2, Chunk));
        Ranges(right.VertexRevision, left.VertexRevision, new SceneVertexRange(0, Chunk), new SceneVertexRange(Chunk * 2, Chunk));
        var sameChunkOtherBranch = Compose(source, [(1, 1)]);
        Ranges(left.VertexRevision, sameChunkOtherBranch.VertexRevision, new SceneVertexRange(0, Chunk));
    }

    [Fact]
    public void AdjacentDirtyChunksCoalesceAndTheFinalChunkClipsToVertexCount()
    {
        var source = Snapshot(Chunk * 3 + 5);
        var acrossBoundary = Compose(source, [(Chunk - 1, 2)]);
        Ranges(acrossBoundary.VertexRevision, source.VertexRevision, new SceneVertexRange(0, Chunk * 2));
        var final = Compose(source, [(Chunk * 3 + 4, 1)]);
        Ranges(final.VertexRevision, source.VertexRevision, new SceneVertexRange(Chunk * 3, 5));
        Assert.Equal(Chunk * 3 + 5, final.VertexRevision.VertexCount);
    }

    [Fact]
    public void CountMismatchRequiresTheCurrentPayloadAndEmptyCurrentNeedsNoUpload()
    {
        var small = Snapshot(3).VertexRevision;
        var large = Snapshot(Chunk + 3).VertexRevision;
        var empty = SceneSnapshot.CreateEmpty().VertexRevision;
        Ranges(large, small, new SceneVertexRange(0, Chunk + 3));
        Ranges(small, large, new SceneVertexRange(0, 3));
        Ranges(small, empty, new SceneVertexRange(0, 3));
        Assert.Empty(empty.GetChangedRanges(large));
        Assert.Throws<ArgumentNullException>(() => small.GetChangedRanges(null!));
    }

    [Fact]
    public void EmptyCompositionSharesRevisionAndConservativeWritesStillMarkTheirChunk()
    {
        var source = Snapshot(Chunk * 2);
        Assert.Same(source.VertexRevision, Compose(source, []).VertexRevision);
        Assert.False(Compose(source, []).VertexRevision.SupportsIncrementalUpdates);
        var unchangedValues = Compose(source, [(Chunk + 2, 1)], nameof(UnchangedWriter));
        Assert.True(unchangedValues.VertexRevision.SupportsIncrementalUpdates);
        Assert.Same(unchangedValues.VertexRevision, Compose(unchangedValues, []).VertexRevision);
        Assert.Equal(source.Vertices.ToArray(), unchangedValues.Vertices.ToArray());
        Ranges(unchangedValues.VertexRevision, source.VertexRevision, new SceneVertexRange(Chunk, Chunk));
    }

    [Fact]
    public void ReturnedRangesCannotBeMutatedAndEarlierRangeResultsStayStable()
    {
        var source = Snapshot(Chunk * 3);
        var changed = Compose(source, [(1, 1)]);
        var ranges = changed.VertexRevision.GetChangedRanges(source.VertexRevision);
        Assert.False(ranges is SceneVertexRange[]);
        var collection = Assert.IsAssignableFrom<IList<SceneVertexRange>>(ranges);
        Assert.True(collection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => collection[0] = new(0, 1));
        _ = Compose(changed, [(Chunk * 2 + 1, 1)]);
        Assert.Equal(new SceneVertexRange(0, Chunk), Assert.Single(ranges));
        Ranges(changed.VertexRevision, source.VertexRevision, new SceneVertexRange(0, Chunk));
    }

    [Fact]
    public void InvalidComposedValuesPreserveThePublishedSourceAndItsRevision()
    {
        var source = Snapshot(Chunk + 3); var revision = source.VertexRevision;
        var before = source.Vertices.ToArray();
        Assert.Throws<ArgumentException>(() => Compose(source, [(1, 1)], nameof(InvalidWriter)));
        Assert.Same(revision, source.VertexRevision);
        Assert.Equal(before, source.Vertices.ToArray());
        Assert.Empty(revision.GetChangedRanges(source.VertexRevision));
        Ranges(Compose(source, [(Chunk, 1)]).VertexRevision, revision, new SceneVertexRange(Chunk, 3));
    }

    [Fact]
    public void OutlineLineageSharesMaterialOnlyUpdatesAndComparesSkippedFrames()
    {
        var source = Outline(Chunk * 3 + 5);
        var materialOnly = Compose(source, [], [new(.42f, Vector4.One)]);
        Assert.Same(source.VertexRevision, materialOnly.VertexRevision);
        Assert.False(materialOnly.VertexRevision.SupportsIncrementalUpdates);
        Assert.Same(source.Vertices, materialOnly.Vertices);
        var first = Compose(source, [(Chunk - 1, 2)]);
        var second = Compose(first, [(Chunk * 3 + 4, 1)]);
        Assert.True(first.VertexRevision.SupportsIncrementalUpdates);
        Assert.True(second.VertexRevision.SupportsIncrementalUpdates);
        Assert.Same(second.VertexRevision, Compose(second, [], [new(.43f, Vector4.One)]).VertexRevision);
        Ranges(second.VertexRevision, source.VertexRevision, new SceneVertexRange(0, Chunk * 2), new SceneVertexRange(Chunk * 3, 5));
        Ranges(source.VertexRevision, second.VertexRevision, new SceneVertexRange(0, Chunk * 2), new SceneVertexRange(Chunk * 3, 5));
        Ranges(new OriginalOutlineData(source.Vertices, source.Materials).VertexRevision,
            source.VertexRevision, new SceneVertexRange(0, Chunk * 3 + 5));
        Assert.NotEqual(source.Vertices[Chunk - 1].Position, first.Vertices[Chunk - 1].Position);
        Assert.Equal(Vector3.Zero, source.Vertices[Chunk - 1].Position);
    }

    [Fact]
    public void OutlineInvalidValuesAndCallerArraysCannotMutatePublishedVerticesOrLineage()
    {
        var vertices = new[] { new OriginalOutlineVertex(Vector3.Zero, Vector3.UnitY, OriginalOutlineVertexMode.Expand, 1, 0) };
        var source = new OriginalOutlineData(vertices, [new(.18f, Vector4.One)]);
        var revision = source.VertexRevision;
        vertices[0] = vertices[0] with { Position = new(90, 80, 70) };
        Assert.Equal(Vector3.Zero, source.Vertices[0].Position);
        Assert.Throws<ArgumentException>(() => Compose(source, [(0, 1)], writer: nameof(InvalidOutlineWriter)));
        Assert.Same(revision, source.VertexRevision);
        Assert.Equal(Vector3.Zero, source.Vertices[0].Position);
    }

    [Fact]
    public void RetainingRevisionsDoesNotRetainSnapshotsVertexStorageOrPriorRevisions()
    {
        var retained = RetainRevisionsOnly();
        for (int attempt = 0; attempt < 3 && retained.References.Any(reference => reference.IsAlive); attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        Assert.All(retained.References, reference => Assert.False(reference.IsAlive));
        Assert.Equal(Chunk + 3, retained.Surface.VertexCount);
        Assert.Equal(Chunk + 3, retained.Outline.VertexCount);
        GC.KeepAlive(retained);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (SceneVertexRevision Surface, SceneVertexRevision Outline, WeakReference[] References) RetainRevisionsOnly()
    {
        var source = Snapshot(Chunk + 3); var previous = source.VertexRevision;
        var next = Compose(source, [(0, 1)]);
        var sourceStorage = typeof(SceneSnapshot).GetField("vertexStorage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
        var nextStorage = typeof(SceneSnapshot).GetField("vertexStorage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(next)!;
        var outline = Outline(Chunk + 3); var previousOutline = outline.VertexRevision;
        var nextOutline = Compose(outline, [(0, 1)]);
        return (next.VertexRevision, nextOutline.VertexRevision,
            new WeakReference[] { new(source), new(next), new(sourceStorage), new(nextStorage), new(previous),
                new(outline), new(nextOutline), new(outline.Vertices), new(nextOutline.Vertices), new(previousOutline) }
                .Concat(PayloadReferences(sourceStorage)).Concat(PayloadReferences(nextStorage)).ToArray());
    }

    private static IEnumerable<WeakReference> PayloadReferences(object storage)
    {
        var chunks = storage.GetType().GetProperty("Chunks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(storage)!;
        var payloads = (ReadOnlyMemory<SceneVertex>[])chunks.GetType().GetField("chunks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chunks)!;
        foreach (var payload in payloads)
        {
            Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(payload, out var segment));
            yield return new(segment.Array!);
        }
    }

    private static void Ranges(SceneVertexRevision current, SceneVertexRevision previous, params SceneVertexRange[] expected)
        => Assert.Equal(expected, current.GetChangedRanges(previous));

    private static SceneSnapshot Snapshot(int count)
        => new(new SceneVertex[count], [0u, 1u, 2u],
            [new("material", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 0)]);
    private static OriginalOutlineData Outline(int count)
        => new(Enumerable.Repeat(new OriginalOutlineVertex(Vector3.Zero, Vector3.UnitY, OriginalOutlineVertexMode.Expand, 1, 0), count),
            [new(.18f, Vector4.One)]);

    private static SceneSnapshot Compose(SceneSnapshot source, (int Start, int Count)[] ranges, string writer = nameof(PatchWriter))
        => InvokeComposition<SceneSnapshot>(source, ranges, writer);
    private static OriginalOutlineData Compose(OriginalOutlineData source, (int Start, int Count)[] ranges,
        IReadOnlyList<OriginalOutlineMaterial>? materials = null, string writer = nameof(PatchOutlineWriter))
        => InvokeComposition<OriginalOutlineData>(source, ranges, writer, materials ?? source.Materials);
    private static T InvokeComposition<T>(T source, (int Start, int Count)[] ranges, string writer,
        IReadOnlyList<OriginalOutlineMaterial>? materials = null) where T : class
    {
        var method = typeof(T).GetMethod("WithComposedVertices", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var write = Delegate.CreateDelegate(method.GetParameters()[1].ParameterType,
            typeof(SceneVertexRevisionTests).GetMethod(writer, BindingFlags.Static | BindingFlags.NonPublic)!);
        try { return (T)method.Invoke(source, materials is null ? [ranges, write] : [ranges, write, materials])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static void PatchWriter(int range, Span<SceneVertex> destination)
    {
        for (int index = 0; index < destination.Length; index++)
            destination[index] = destination[index] with { Position = new(range + 1, 2, 3) };
    }
    private static void UnchangedWriter(int range, Span<SceneVertex> destination) { _ = range; _ = destination; }
    private static void InvalidWriter(int range, Span<SceneVertex> destination)
    { _ = range; destination[0] = destination[0] with { Normal = new(float.NaN, 0, 0) }; }
    private static void PatchOutlineWriter(int range, Span<OriginalOutlineVertex> destination)
    {
        for (int index = 0; index < destination.Length; index++)
            destination[index] = destination[index] with { Position = new(range + 1, 2, 3) };
    }
    private static void InvalidOutlineWriter(int range, Span<OriginalOutlineVertex> destination)
    { _ = range; destination[0] = destination[0] with { VertexScale = -1 }; }
}
