using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneCompositionCacheTests
{
    private sealed class Cache
    {
        private static readonly Type Type = typeof(ProjectSceneAssembler).GetNestedType("CompositionCache", BindingFlags.NonPublic)!;
        private static readonly MethodInfo Method = Type.GetMethod("Compose", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly object owner = Activator.CreateInstance(Type, [null])!;
        internal OriginalFrameScene Compose(OriginalFrameScene? baseline, Guid[] ids, OriginalFrameScene[] sources, params OriginalFrameScene[] frames)
        {
            try { return (OriginalFrameScene)Method.Invoke(owner, [baseline, ids, sources, frames, Vector3.One, Vector3.UnitY])!; }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }

    [Fact]
    public void UnchangedInputsAndMetadataWrappersReuseThePublishedBaseline()
    {
        var cache = new Cache(); var a = Frame(3); var b = Frame(6);
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()]; OriginalFrameScene[] sources = [a, b];
        var baseline = cache.Compose(null, ids, sources, a, b);
        Assert.Same(baseline, cache.Compose(baseline, ids, sources, a, b));
        var wrapper = Reframe(a, a.Surface.Snapshot.WithCamera(SceneCamera.Default).WithLighting(Vector3.UnitX));
        Assert.Same(baseline, cache.Compose(baseline, ids, sources, wrapper, b));
    }

    [Fact]
    public void DirtyActorMatchesIndependentCompositionBitForBitAndNeverChangesOldSnapshots()
    {
        var cache = new Cache(); var a = Frame(3); var b = Frame(9, world: Matrix4x4.CreateScale(-2, 3, .5f));
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()]; OriginalFrameScene[] sources = [a, b];
        var baseline = cache.Compose(null, ids, sources, a, b); var oldVertices = Bytes(baseline.Surface.Snapshot.Vertices);
        var oldEdges = EdgeBytes(baseline); var vertices = a.Surface.Snapshot.Vertices.ToArray();
        vertices[0] = vertices[0] with { Position = new(11, -0f, 13), Normal = Vector3.Zero, Uv = new(-0f, .75f) };
        var changed = Reframe(a, a.Surface.Snapshot.WithDeformedVertices(vertices)
            .WithWorld(Matrix4x4.CreateScale(-1, .25f, 3) * Matrix4x4.CreateTranslation(4, 5, 6)));
        var actual = cache.Compose(baseline, ids, sources, changed, b);
        Equal(ProjectSceneAssembler.WithVertices(baseline, [changed, b]), actual);
        Equal(Combine(changed, b), actual);
        Assert.Equal(oldVertices, Bytes(baseline.Surface.Snapshot.Vertices)); Assert.Equal(oldEdges, EdgeBytes(baseline));
        Assert.Equal(Bytes(b.Surface.Snapshot.Vertices), Bytes(sources[1].Surface.Snapshot.Vertices));
        Assert.Same(actual, cache.Compose(actual, ids, sources, changed, b));
    }

    [Theory]
    [InlineData("world")]
    [InlineData("uv")]
    [InlineData("uv-absent")]
    [InlineData("seed")]
    [InlineData("seed-absent")]
    [InlineData("material")]
    [InlineData("outline-material")]
    public void IndependentConsumedInputsInvalidateTheirOutput(string kind)
    {
        var cache = new Cache(); var source = Frame(6); Guid[] ids = [Guid.NewGuid()]; OriginalFrameScene[] sources = [source];
        var baseline = cache.Compose(null, ids, sources, source); var snapshot = source.Surface.Snapshot;
        OriginalFrameScene changed = kind switch
        {
            "world" => Reframe(source, snapshot.WithWorld(Matrix4x4.CreateScale(-2, .5f, 3) * Matrix4x4.CreateTranslation(7, -0f, 9))),
            "uv" => Reframe(source, snapshot.WithAdditionalUvs(new(1, Enumerable.Repeat(new SceneAdditionalUvVertex(new(9, 8, 7, 6), default, default, default), 6).ToArray()))),
            "uv-absent" => Reframe(source, snapshot.WithAdditionalUvs(null)),
            "seed" => Reframe(source, snapshot.WithRawRadialSeeds(new(Enumerable.Repeat(new Vector3(8, 9, 10), 6).ToArray()))),
            "seed-absent" => Reframe(source, snapshot.WithRawRadialSeeds(null)),
            "material" => Reframe(source, snapshot.WithMaterials(snapshot.Materials.Select(m => m with { Diffuse = new(.4f, .6f, .8f, .5f) }))),
            _ => new(source.Surface, new(source.Outline!.Vertices, [new(.42f, new(.2f, .3f, .4f, 1))]), source.OutlineOptions, materials: source.Materials)
        };
        var actual = cache.Compose(baseline, ids, sources, changed);
        Equal(ProjectSceneAssembler.WithVertices(baseline, [changed]), actual);
        Assert.NotSame(baseline, actual);
        if (kind is "material" or "outline-material" or "uv" or "uv-absent")
            Assert.Same(baseline.Surface.Snapshot.RawRadialSeeds, actual.Surface.Snapshot.RawRadialSeeds);
    }

    [Fact]
    public void SignedZeroWorldChangesMatchTheUncachedArithmetic()
    {
        var cache = new Cache(); var source = Frame(3); var positive = Matrix4x4.CreateScale(2, 3, 4);
        positive.M12 = 0; var negative = positive; negative.M12 = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
        Guid[] ids = [Guid.NewGuid()]; OriginalFrameScene[] sources = [source];
        var a = Reframe(source, source.Surface.Snapshot.WithWorld(positive));
        // Derive both placements from the identity source: applying one after the other would let
        // WithWorld's numerical equality intentionally preserve the first signed-zero wrapper.
        var s = source.Surface.Snapshot;
        var b = Reframe(source, s.WithWorld(negative));
        var baseline = cache.Compose(null, ids, sources, a);
        Equal(Combine(b), cache.Compose(baseline, ids, sources, b));
    }

    [Theory]
    [InlineData("order")]
    [InlineData("source")]
    [InlineData("topology")]
    [InlineData("visibility")]
    [InlineData("texture")]
    public void ChangedLayoutRebuildsEvenWhenTotalCountsMatch(string kind)
    {
        var cache = new Cache(); var a = Frame(3); var b = Frame(3);
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()]; OriginalFrameScene[] sources = [a, b];
        var baseline = cache.Compose(null, ids, sources, a, b);
        OriginalFrameScene[] frames = [a, b];
        switch (kind)
        {
            case "order": ids = [ids[1], ids[0]]; sources = [b, a]; frames = [b, a]; break;
            case "source": sources = [Frame(3), b]; frames = [sources[0], b]; break;
            case "topology":
                var s = a.Surface.Snapshot;
                frames = [Reframe(a, new(s.Vertices, [0u, 2u, 1u], s.Materials, s.Textures,
                    additionalUvs: s.AdditionalUvs, rawRadialSeeds: s.RawRadialSeeds)), b]; break;
            case "visibility": ids = [ids[1]]; sources = [b]; frames = [b]; break;
            case "texture": frames = [Frame(3, texture: new("replacement", 1, 1, [9, 8, 7, 255])), b]; break;
        }
        Equal(Combine(frames), cache.Compose(baseline, ids, sources, frames));
        Assert.Equal(6, baseline.Surface.Snapshot.Vertices.Length);
    }

    [Fact]
    public void AbandonedCandidateAndFailedTransformCannotPoisonTheCommittedBaseline()
    {
        var cache = new Cache(); var source = Frame(3); Guid[] ids = [Guid.NewGuid()]; OriginalFrameScene[] sources = [source];
        var baseline = cache.Compose(null, ids, sources, source); var before = Bytes(baseline.Surface.Snapshot.Vertices);
        var candidate = Reframe(source, source.Surface.Snapshot.WithWorld(Matrix4x4.CreateTranslation(20, 0, 0)));
        _ = cache.Compose(baseline, ids, sources, candidate); // A caller cancels after Compose, before publication.
        baseline = cache.Compose(baseline, ids, sources, source);
        Equal(Combine(source), baseline);
        var huge = Reframe(source, source.Surface.Snapshot.WithWorld(Matrix4x4.CreateScale(1e10f)));
        var enormousVertices = source.Surface.Snapshot.Vertices.ToArray();
        enormousVertices[0] = enormousVertices[0] with { Position = new(1e30f, 0, 0) };
        huge = Reframe(huge, huge.Surface.Snapshot.WithDeformedVertices(enormousVertices));
        Assert.ThrowsAny<ArgumentException>(() => cache.Compose(baseline, ids, sources, huge));
        Equal(Combine(source), cache.Compose(baseline, ids, sources, source));
        Assert.Equal(before, Bytes(baseline.Surface.Snapshot.Vertices));
    }

    [Fact]
    public void OneDirtyActorClonesOnlyTouchedSurfaceAndOutlineChunks()
    {
        const int count = 9000; var a = Frame(3); var b = Frame(count); var cache = new Cache();
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()]; OriginalFrameScene[] sources = [a, b];
        var baseline = cache.Compose(null, ids, sources, a, b);
        var changedVertices = a.Surface.Snapshot.Vertices.ToArray();
        changedVertices[0] = changedVertices[0] with { Position = new(1, 2, 3) };
        var changed = Reframe(a, a.Surface.Snapshot.WithDeformedVertices(changedVertices));
        _ = cache.Compose(baseline, ids, sources, changed, b);
        baseline = cache.Compose(null, ids, sources, a, b);
        long start = GC.GetAllocatedBytesForCurrentThread(); var actual = cache.Compose(baseline, ids, sources, changed, b);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        // This three-vertex actor touches one 4096-value chunk in each stream;
        // the other actor's immutable payload is shared. No timing/GC assertion.
        Assert.InRange(allocated, 4096 * 68L, 4096 * 68L + 128 * 1024);
        Assert.Equal(count + 3, actual.Surface.Snapshot.VertexCount);
        start = GC.GetAllocatedBytesForCurrentThread(); var unchanged = cache.Compose(actual, ids, sources, changed, b);
        long unchangedAllocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Same(actual, unchanged); Assert.InRange(unchangedAllocated, 0, 32 * 1024);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 4)]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(0, 0)]
    public void InternalRangeFactoryRejectsInvalidBoundsWithoutChangingItsSource(int start, int count)
    {
        var source = Frame(3).Surface.Snapshot; var before = Bytes(source.Vertices);
        Assert.ThrowsAny<ArgumentException>(() => ComposeRanges(source, [(start, count)]));
        Assert.Equal(before, Bytes(source.Vertices));
    }

    [Fact]
    public void InternalRangeFactoryValidatesDirtyValuesAndKeepsItsBaselineImmutable()
    {
        var source = Frame(3).Surface.Snapshot; var before = Bytes(source.Vertices);
        Assert.ThrowsAny<ArgumentException>(() => ComposeRanges(source, [(0, 1)]));
        Assert.Equal(before, Bytes(source.Vertices));
    }

    private static SceneSnapshot ComposeRanges(SceneSnapshot source, (int Start, int Count)[] ranges)
    {
        var method = typeof(SceneSnapshot).GetMethod("WithComposedVertices", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var writer = Delegate.CreateDelegate(method.GetParameters()[1].ParameterType,
            typeof(SceneCompositionCacheTests).GetMethod(nameof(InvalidVertexWriter), BindingFlags.Static | BindingFlags.NonPublic)!);
        try { return (SceneSnapshot)method.Invoke(source, [ranges, writer])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static void InvalidVertexWriter(int range, Span<SceneVertex> destination)
    { _ = range; destination[0] = destination[0] with { Normal = new(float.NaN, 0, 0) }; }

    private static OriginalFrameScene Combine(params OriginalFrameScene[] frames) => ProjectSceneAssembler.Combine(frames, Vector3.One, Vector3.UnitY);
    private static byte[] Bytes<T>(ReadOnlySpan<T> values) where T : unmanaged => MemoryMarshal.AsBytes(values).ToArray();
    private static byte[] EdgeBytes(OriginalFrameScene frame) => Bytes<OriginalOutlineVertex>(frame.Outline!.Vertices.ToArray());
    private static void Equal(OriginalFrameScene expected, OriginalFrameScene actual)
    {
        Assert.Equal(Bytes(expected.Surface.Snapshot.Vertices), Bytes(actual.Surface.Snapshot.Vertices));
        Assert.Equal(EdgeBytes(expected), EdgeBytes(actual));
        Assert.Equal(expected.Surface.Snapshot.Indices.ToArray(), actual.Surface.Snapshot.Indices.ToArray());
        Assert.Equal(expected.Surface.Snapshot.Materials, actual.Surface.Snapshot.Materials);
        Assert.Equal(expected.Surface.Materials, actual.Surface.Materials); Assert.Equal(expected.Materials, actual.Materials);
        Assert.Equal(expected.Outline!.Materials, actual.Outline!.Materials);
        if (expected.Surface.Snapshot.AdditionalUvs is { } uv)
            Assert.Equal(Bytes(uv.Vertices), Bytes(actual.Surface.Snapshot.AdditionalUvs!.Vertices));
        else Assert.Null(actual.Surface.Snapshot.AdditionalUvs);
        if (expected.Surface.Snapshot.RawRadialSeeds is { } seeds)
            Assert.Equal(Bytes(seeds.Vertices), Bytes(actual.Surface.Snapshot.RawRadialSeeds!.Vertices));
        else Assert.Null(actual.Surface.Snapshot.RawRadialSeeds);
        Assert.Equal(expected.Surface.Snapshot.Textures.ToArray(), actual.Surface.Snapshot.Textures.ToArray());
    }
    private static OriginalFrameScene Reframe(OriginalFrameScene frame, SceneSnapshot snapshot)
        => new(new(snapshot, frame.Surface.Materials, frame.Surface.Profile), frame.Outline, frame.OutlineOptions,
            frame.Options, frame.Materials, frame.Lighting, frame.EditorOverlay);
    private static OriginalFrameScene Frame(int count, Matrix4x4? world = null, SceneTexture? texture = null)
    {
        texture ??= new("shared", 1, 1, [1, 2, 3, 255]);
        var vertices = Enumerable.Range(0, count).Select(i => new SceneVertex(new(i % 3, i % 5, i % 7), new(2, 3, 4), new(-0f, .5f))).ToArray();
        var indices = Enumerable.Range(0, count).Select(i => (uint)i).ToArray();
        var material = new SceneMaterial("material", 0, count, Vector4.One, Vector3.One, new(.2f), 4, 0, 0);
        var snapshot = new SceneSnapshot(vertices, indices, [material], [texture], world,
            additionalUvs: new(1, Enumerable.Repeat(new SceneAdditionalUvVertex(new(1, 2, 3, 4), default, default, default), count).ToArray()),
            rawRadialSeeds: new(Enumerable.Repeat(new Vector3(2, 3, 4), count).ToArray()));
        return new(new(snapshot, [new(new(.4f))]),
            new(vertices.Select(v => new OriginalOutlineVertex(v.Position, v.Normal, OriginalOutlineVertexMode.Expand, .7f, 0)), [new(.18f, Vector4.One)]),
            new(Vector4.Zero, Matrix4x4.Identity, new(0, 9, -30), 30, 1));
    }
}
