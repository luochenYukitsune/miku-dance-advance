using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneIdentityVertexCopyTests
{
    private const int Chunk = 4096;

    [Fact]
    public void CopiedRangesKeepAdjacentRangesGapsUnalignedSourcesAndShortTailExact()
    {
        var baseline = IntStorage(Enumerable.Range(0, Chunk * 5 + 19).ToArray());
        (int Start, IList<int> Source)[] ranges =
        [
            (3, IntStorage([101, 102, 103, 104])),
            (7, IntStorage([201, 202, 203, 204, 205, 206])),
            (15, IntStorage(Enumerable.Range(10000, Chunk + 10).ToArray())),
            (Chunk + 29, IntStorage(Enumerable.Range(20000, Chunk + 8).ToArray())),
            (Chunk * 5 + 17, IntStorage([301, 302]))
        ];
        var expected = baseline.ToArray();
        foreach (var range in ranges) range.Source.CopyTo(expected, range.Start);
        var first = CopyIntRanges(baseline, ranges);
        var held = first.ToArray();
        var second = CopyIntRanges(first, [(Chunk * 3 + 1, IntStorage([401, 402]))]);
        Assert.Equal(expected, first);
        Assert.Equal(Enumerable.Range(0, baseline.Count), baseline);
        Assert.Equal(held, first);
        Assert.Equal(401, second[Chunk * 3 + 1]);
        var oldChunks = IntMemories(baseline); var chunks = IntMemories(first);
        Assert.Equal(19, chunks[^1].Length);
        Assert.Equal(oldChunks[3], chunks[3]); Assert.Equal(oldChunks[4], chunks[4]);
        foreach (int changed in new[] { 0, 1, 2, 5 }) Assert.NotEqual(oldChunks[changed], chunks[changed]);
        Assert.Same(first, CopyIntRanges(first, []));
    }

    [Fact]
    public void IdentityCopiesPreserveRawBitsOldGenerationsAndSkippedOrBranchedRevisionsWithoutFlatViews()
    {
        var baseline = Scene(Chunk * 4 + 13, 1); var before = Bytes(baseline);
        var replacement = Scene(9001, 100);
        var first = CopyIdentity(baseline, [(3, replacement), (Chunk * 4 + 10, Scene(3, 200))]);
        var expected = Values(baseline); replacement.CopyVertices(0, expected.AsSpan(3, replacement.VertexCount));
        Scene(3, 200).CopyVertices(0, expected.AsSpan(Chunk * 4 + 10, 3));
        Assert.Equal(VertexBytes(expected), Bytes(first));
        var held = Bytes(first);
        var second = CopyIdentity(first, [(Chunk * 3 + 1, Scene(2, 300)), (Chunk * 4 + 12, Scene(1, 400))]);
        var branch = CopyIdentity(first, [(4, Scene(3, 500))]);
        Assert.Equal(before, Bytes(baseline)); Assert.Equal(held, Bytes(first));
        Assert.Equal(new[] { new SceneVertexRange(0, Chunk * 3), new SceneVertexRange(Chunk * 4, 13) },
            first.VertexRevision.GetChangedRanges(baseline.VertexRevision));
        Assert.Equal(new[] { new SceneVertexRange(0, baseline.VertexCount) },
            second.VertexRevision.GetChangedRanges(baseline.VertexRevision));
        Assert.Equal(new[] { new SceneVertexRange(0, Chunk), new SceneVertexRange(Chunk * 3, Chunk + 13) },
            branch.VertexRevision.GetChangedRanges(second.VertexRevision));
        Assert.True(first.HasDynamicVertices); Assert.True(first.VertexRevision.SupportsIncrementalUpdates);
        Assert.Same(baseline.Camera, first.Camera); Assert.Equal(baseline.World, first.World);
        Assert.Same(baseline.Materials, first.Materials); Assert.Same(baseline.Textures, first.Textures);
        Assert.Same(baseline.AdditionalUvs, first.AdditionalUvs); Assert.Same(baseline.RawRadialSeeds, first.RawRadialSeeds);
        foreach (var scene in new[] { baseline, replacement, first, second, branch }) Assert.False(IsMaterialized(scene));
        var empty = CopyIdentity(first, []);
        Assert.Same(Storage(first), Storage(empty)); Assert.Same(first.VertexRevision, empty.VertexRevision);
    }

    [Fact]
    public void InvalidIdentityDescriptorsRejectBeforePublicationAndLeaveTheBaselineUntouched()
    {
        var baseline = Scene(Chunk + 7, 1); var source = Scene(3, 2);
        var before = Bytes(baseline); var revision = baseline.VertexRevision;
        foreach (var ranges in new (int Start, SceneSnapshot Source)[][]
        {
            [(-1, source)], [(baseline.VertexCount, source)], [(int.MaxValue, source)],
            [(0, source), (baseline.VertexCount - 1, source)], [(0, source), (2, source)],
            [(4, source), (0, source)], [(0, source), (4, null!)], [(0, SceneSnapshot.CreateEmpty())],
            [(0, source.WithWorld(Matrix4x4.CreateTranslation(1, 0, 0)))]
        })
            Assert.Throws<ArgumentException>(() => CopyIdentity(baseline, ranges));
        Assert.Throws<ArgumentNullException>(() => CopyIdentity(baseline, null!));
        Assert.Throws<ArgumentException>(() => CopyIdentity(baseline.WithWorld(Matrix4x4.CreateScale(2)), [(0, source)]));
        Assert.Equal(before, Bytes(baseline)); Assert.Same(revision, baseline.VertexRevision);
        Assert.False(IsMaterialized(baseline)); Assert.False(IsMaterialized(source));
    }

    [Fact]
    public void NumericIdentityIncludingSignedZeroCopiesWithoutArithmetic()
    {
        var world = Matrix4x4.Identity; world.M12 = NegativeZero;
        var source = Scene(3, 2, world); var baseline = Scene(7, 1);
        var copied = CopyIdentity(baseline, [(1, source)]);
        Assert.Equal(Bytes(source), VertexBytes(Values(copied).AsSpan(1, source.VertexCount)));
        Assert.False(IsMaterialized(copied));
    }

    [Fact]
    public void DeformationValidatesThePrivatePayloadAndKeepsAllPublicInputChecks()
    {
        var source = Scene(3, 1); var before = Bytes(source);
        Assert.Throws<ArgumentNullException>(() => source.WithDeformedVertices(null!));
        Assert.Throws<ArgumentException>(() => source.WithDeformedVertices(new SceneVertex[2]));
        foreach (var invalid in new[]
        {
            source.GetVertex(0) with { Position = new(float.NaN, 0, 0) },
            source.GetVertex(0) with { Normal = new(float.PositiveInfinity, 0, 0) },
            source.GetVertex(0) with { Normal = new(float.MaxValue, 1, 0) },
            source.GetVertex(0) with { Uv = new(0, float.NegativeInfinity) }
        })
        {
            var input = Values(source); input[0] = invalid;
            var error = Assert.Throws<ArgumentException>(() => source.WithDeformedVertices(input));
            Assert.Equal("deformed", error.ParamName);
        }
        var caller = Values(source); var deformed = source.WithDeformedVertices(caller);
        caller[0] = caller[0] with { Position = new(float.NaN) };
        Assert.Equal(before, Bytes(deformed)); Assert.Equal(before, Bytes(source));
        var copied = CopyIdentity(Scene(7, 3), [(2, deformed)]);
        Assert.Equal(before, VertexBytes(Values(copied).AsSpan(2, 3)));
    }

    [Fact]
    public void ReplacementResultsRetainPayloadsWithoutSourceOrParentOwnerObjects()
    {
        var retained = RetainOnlyResult();
        for (int attempt = 0; attempt < 3 && retained.Old.Any(reference => reference.IsAlive); attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(retained.Old, reference => Assert.False(reference.IsAlive));
        Assert.Equal(Chunk * 3 + 7, retained.Latest.VertexCount);
        Assert.False(IsMaterialized(retained.Latest));
        GC.KeepAlive(retained);
    }

    [Fact]
    public void MultipleIdentityActorsMatchCompleteCombineAndPreserveHeldFramesAndOutlineMetadata()
    {
        var cache = new Cache(); OriginalFrameScene[] original = [Frame(3, 1), Frame(9001, 2), Frame(5, 3), Frame(4103, 4), Frame(7, 5)];
        var ids = original.Select(_ => Guid.NewGuid()).ToArray();
        var baseline = cache.Compose(null, ids, original, original); var before = Bytes(baseline.Surface.Snapshot); var beforeEdges = EdgeBytes(baseline);
        OriginalFrameScene[] changed = [original[0], Deform(original[1], 20), Deform(original[2], 30), original[3], Deform(original[4], 50)];
        var first = cache.Compose(baseline, ids, original, changed);
        Assert.False(IsMaterialized(first.Surface.Snapshot));
        Equal(Combine(changed), first); Equal(ProjectSceneAssembler.WithVertices(baseline, changed), first);
        var held = Bytes(first.Surface.Snapshot); var heldEdges = EdgeBytes(first);
        var nextInputs = changed.ToArray(); nextInputs[3] = Deform(original[3], 40);
        var next = cache.Compose(first, ids, original, nextInputs);
        Assert.False(IsMaterialized(next.Surface.Snapshot)); Equal(Combine(nextInputs), next);
        Assert.Equal(before, Bytes(baseline.Surface.Snapshot)); Assert.Equal(beforeEdges, EdgeBytes(baseline));
        Assert.Equal(held, Bytes(first.Surface.Snapshot)); Assert.Equal(heldEdges, EdgeBytes(first));
        Assert.Equal(new[] { new SceneVertexRange(0, next.Surface.Snapshot.VertexCount) },
            next.Surface.Snapshot.VertexRevision.GetChangedRanges(baseline.Surface.Snapshot.VertexRevision));
        Assert.Same(next, cache.Compose(next, ids, original, nextInputs));
    }

    [Fact]
    public void MixedWorldActorsKeepTransformsAndOverflowValidationOnTheGenericPath()
    {
        var cache = new Cache(); OriginalFrameScene[] original = [Frame(3, 1), Frame(5001, 2)];
        var ids = original.Select(_ => Guid.NewGuid()).ToArray(); var baseline = cache.Compose(null, ids, original, original);
        var a = Deform(original[0], 10); var b = Deform(original[1], 20);
        b = Reframe(b, b.Surface.Snapshot.WithWorld(Matrix4x4.CreateScale(-2, 3, .5f) * Matrix4x4.CreateTranslation(4, 5, 6)));
        var actual = cache.Compose(baseline, ids, original, [a, b]);
        Assert.False(IsMaterialized(actual.Surface.Snapshot)); Equal(Combine(a, b), actual);
        Equal(ProjectSceneAssembler.WithVertices(baseline, [a, b]), actual);
        var held = Bytes(actual.Surface.Snapshot); var heldEdges = EdgeBytes(actual);
        var huge = Values(b.Surface.Snapshot); huge[0] = huge[0] with { Position = new(1e30f, 0, 0) };
        var invalid = Reframe(b, b.Surface.Snapshot.WithDeformedVertices(huge).WithWorld(Matrix4x4.CreateScale(1e10f)));
        Assert.Throws<ArgumentException>(() => cache.Compose(actual, ids, original, [Deform(a, 30), invalid]));
        Assert.Equal(held, Bytes(actual.Surface.Snapshot)); Assert.Equal(heldEdges, EdgeBytes(actual));
        Assert.Same(actual, cache.Compose(actual, ids, original, [a, b]));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (SceneSnapshot Latest, WeakReference[] Old) RetainOnlyResult()
    {
        var baseline = Scene(Chunk * 3 + 7, 1); var source = Scene(9001, 2);
        var first = CopyIdentity(baseline, [(3, source)]); var nextSource = Scene(3, 3);
        var latest = CopyIdentity(first, [(Chunk * 2 + 1, nextSource)]);
        return (latest, [new(baseline), new(Storage(baseline)), new(source), new(Storage(source)),
            new(first), new(Storage(first)), new(nextSource), new(Storage(nextSource))]);
    }

    private static readonly Type IntStorageType = typeof(SceneSnapshot).Assembly.GetType("Mmd.Modern.Core.OwnedVertexChunks`1")!.MakeGenericType(typeof(int));
    private static IList<int> IntStorage(int[] owned)
        => (IList<int>)Activator.CreateInstance(IntStorageType, BindingFlags.Instance | BindingFlags.NonPublic, null, [owned], null)!;
    private static ReadOnlyMemory<int>[] IntMemories(IList<int> value)
        => (ReadOnlyMemory<int>[])IntStorageType.GetField("chunks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static IList<int> CopyIntRanges(IList<int> baseline, (int Start, IList<int> Source)[] ranges)
    {
        var tuple = typeof(ValueTuple<,>).MakeGenericType(typeof(int), IntStorageType); var values = Array.CreateInstance(tuple, ranges.Length);
        for (int i = 0; i < ranges.Length; i++) values.SetValue(Activator.CreateInstance(tuple, ranges[i].Start, ranges[i].Source), i);
        return (IList<int>)Invoke(IntStorageType.GetMethod("WithCopiedRanges", BindingFlags.Instance | BindingFlags.NonPublic)!, baseline, [values]);
    }
    private static SceneSnapshot CopyIdentity(SceneSnapshot baseline, (int Start, SceneSnapshot Source)[] ranges)
        => (SceneSnapshot)Invoke(typeof(SceneSnapshot).GetMethod("WithIdentityVertexChunks", BindingFlags.Instance | BindingFlags.NonPublic)!, baseline, [ranges]);
    private static object Invoke(MethodInfo method, object owner, object?[] arguments)
    {
        try { return method.Invoke(owner, arguments)!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private sealed class Cache
    {
        private static readonly Type Type = typeof(ProjectSceneAssembler).GetNestedType("CompositionCache", BindingFlags.NonPublic)!;
        private readonly object owner = Activator.CreateInstance(Type, [null])!;
        internal OriginalFrameScene Compose(OriginalFrameScene? baseline, Guid[] ids, OriginalFrameScene[] sources, OriginalFrameScene[] inputs)
            => (OriginalFrameScene)Invoke(Type.GetMethod("Compose", BindingFlags.Instance | BindingFlags.NonPublic)!, owner,
                [baseline, ids, sources, inputs, Vector3.One, Vector3.UnitY]);
    }
    private static object Storage(SceneSnapshot scene) => typeof(SceneSnapshot).GetField("vertexStorage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
    private static bool IsMaterialized(SceneSnapshot scene)
    { var storage = Storage(scene); return (bool)storage.GetType().GetProperty("IsMaterialized", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(storage)!; }
    private static SceneVertex[] Values(SceneSnapshot scene)
    { var values = new SceneVertex[scene.VertexCount]; scene.CopyVertices(0, values); return values; }
    private static byte[] Bytes(SceneSnapshot scene) => VertexBytes(Values(scene));
    private static byte[] VertexBytes(ReadOnlySpan<SceneVertex> vertices) => MemoryMarshal.AsBytes(vertices).ToArray();
    private static byte[] EdgeBytes(OriginalFrameScene frame) => MemoryMarshal.AsBytes(frame.Outline!.Vertices.ToArray().AsSpan()).ToArray();
    private static float NegativeZero => BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
    private static SceneSnapshot Scene(int count, int seed, Matrix4x4? world = null)
    {
        var vertices = Enumerable.Range(0, count).Select(i => new SceneVertex(new(seed + i % 7, NegativeZero, BitConverter.Int32BitsToSingle(1 + i % 17)),
            new(2.1f, -.3f, .7f), new(NegativeZero, BitConverter.Int32BitsToSingle(3 + i % 19)))).ToArray();
        return new(vertices, [0u, 0u, 0u], [new("mesh", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 0)], world: world,
            additionalUvs: new(1, Enumerable.Repeat(new SceneAdditionalUvVertex(new(seed, 2, 3, 4), default, default, default), count).ToArray()),
            rawRadialSeeds: new(Enumerable.Repeat(new Vector3(seed, 3, 4), count).ToArray()));
    }
    private static OriginalFrameScene Frame(int count, int seed)
    {
        var scene = Scene(count, seed); var values = Values(scene);
        return new(new(scene, [new(new(.4f))]), new(values.Select((v, i) => new OriginalOutlineVertex(v.Position, v.Normal,
            i % 2 == 0 ? OriginalOutlineVertexMode.Expand : OriginalOutlineVertexMode.Inward, .5f + i % 5 * .1f, 0)), [new(.18f, Vector4.One)]),
            new(Vector4.Zero, Matrix4x4.Identity, new(0, 9, -30), 30, 1));
    }
    private static OriginalFrameScene Deform(OriginalFrameScene frame, int seed)
    {
        var values = Values(frame.Surface.Snapshot);
        for (int i = 0; i < values.Length; i++) values[i] = values[i] with { Position = new(seed + i % 11, NegativeZero, BitConverter.Int32BitsToSingle(7 + i % 13)) };
        return Reframe(frame, frame.Surface.Snapshot.WithDeformedVertices(values));
    }
    private static OriginalFrameScene Reframe(OriginalFrameScene frame, SceneSnapshot scene)
        => new(new(scene, frame.Surface.Materials, frame.Surface.Profile), frame.Outline, frame.OutlineOptions,
            frame.Options, frame.Materials, frame.Lighting, frame.EditorOverlay);
    private static OriginalFrameScene Combine(params OriginalFrameScene[] frames) => ProjectSceneAssembler.Combine(frames, Vector3.One, Vector3.UnitY);
    private static void Equal(OriginalFrameScene expected, OriginalFrameScene actual)
    {
        Assert.Equal(Bytes(expected.Surface.Snapshot), Bytes(actual.Surface.Snapshot)); Assert.Equal(EdgeBytes(expected), EdgeBytes(actual));
        Assert.Equal(expected.Surface.Snapshot.Indices.ToArray(), actual.Surface.Snapshot.Indices.ToArray());
        Assert.Equal(expected.Surface.Snapshot.Materials, actual.Surface.Snapshot.Materials); Assert.Equal(expected.Surface.Materials, actual.Surface.Materials);
        Assert.Equal(expected.Materials, actual.Materials); Assert.Equal(expected.Outline!.Materials, actual.Outline!.Materials);
        Assert.Equal(MemoryMarshal.AsBytes(expected.Surface.Snapshot.AdditionalUvs!.Vertices).ToArray(), MemoryMarshal.AsBytes(actual.Surface.Snapshot.AdditionalUvs!.Vertices).ToArray());
        Assert.Equal(MemoryMarshal.AsBytes(expected.Surface.Snapshot.RawRadialSeeds!.Vertices).ToArray(), MemoryMarshal.AsBytes(actual.Surface.Snapshot.RawRadialSeeds!.Vertices).ToArray());
    }
}
