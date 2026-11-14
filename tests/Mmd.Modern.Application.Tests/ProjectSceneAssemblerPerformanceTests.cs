using System.Numerics;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class ProjectSceneAssemblerPerformanceTests
{
    [Fact]
    public void CompositionPreservesDrawOrderReferenceTextureIdentityAndEveryExplicitStream()
    {
        var shared = Texture("shared"); var equalPixels = Texture("shared"); var other = Texture("other");
        var first = Frame(3, [shared, equalPixels], streams: true);
        var second = Frame(3, [equalPixels, shared, other], streams: true,
            world: Matrix4x4.CreateScale(-2, 3, .5f) * Matrix4x4.CreateTranslation(7, -4, 9), dynamic: true);
        var result = Combine(first, second);
        Assert.Equal(3, result.Surface.Snapshot.Textures.Count);
        Assert.Same(shared, result.Surface.Snapshot.Textures[0]); Assert.Same(equalPixels, result.Surface.Snapshot.Textures[1]);
        Assert.Same(other, result.Surface.Snapshot.Textures[2]);
        Assert.Equal(new[] { 0, 1 }, result.Surface.Snapshot.Materials.Select(material => material.DiffuseTexture));
        Assert.Equal(new[] { 1, 2 }, result.Surface.Snapshot.Materials.Select(material => material.ToonTexture));
        Assert.Equal(new[] { 1, 2 }, result.Surface.Materials.Select(binding => binding.SphereTexture));
        Assert.Equal(new[] { 0, 3 }, result.Surface.Snapshot.Materials.Select(material => material.StartIndex));
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5 }, result.Surface.Snapshot.Indices.ToArray());
        Assert.True(result.Surface.Snapshot.HasDynamicVertices);
        Assert.Equal(first.Surface.Snapshot.Vertices.ToArray(), result.Surface.Snapshot.Vertices[..3].ToArray());
        for (int i = 0; i < 3; i++)
        {
            var original = second.Surface.Snapshot.Vertices[i]; var transformed = result.Surface.Snapshot.Vertices[3 + i];
            Assert.Equal(new Vector3(original.Position.X * -2 + 7, original.Position.Y * 3 - 4, original.Position.Z * .5f + 9), transformed.Position);
            Near(Vector3.Normalize(new(-original.Normal.X / 2, original.Normal.Y / 3, original.Normal.Z * 2)), transformed.Normal);
            Assert.Equal(original.Uv, transformed.Uv);
            Assert.Equal(1, result.Outline!.Vertices[3 + i].SelectedMaterialIndex);
            Assert.Equal(transformed.Position, result.Outline.Vertices[3 + i].Position);
            Assert.Equal(transformed.Normal, result.Outline.Vertices[3 + i].Normal);
            Assert.Equal(second.Surface.Snapshot.AdditionalUvs!.Vertices[i], result.Surface.Snapshot.AdditionalUvs!.Vertices[3 + i]);
            var seed = second.Surface.Snapshot.RawRadialSeeds!.Vertices[i];
            Assert.Equal(new Vector3(-2 * seed.X, 3 * seed.Y, .5f * seed.Z), result.Surface.Snapshot.RawRadialSeeds!.Vertices[3 + i]);
        }
        Assert.Same(first.Materials[0], result.Materials[0]); Assert.Same(second.Materials[0], result.Materials[1]);
        Assert.Equal(Matrix4x4.Identity, result.Surface.Snapshot.World);
    }

    [Fact]
    public void PoseUpdateMatchesFreshCompositionAndNeverSharesMutableOutputArrays()
    {
        var shared = Texture("shared"); var secondTexture = Texture("second");
        var first = Frame(3, [shared, secondTexture], streams: true);
        var baseline = Combine(first, first);
        var changed = Frame(3, [secondTexture, shared], streams: true,
            world: Matrix4x4.CreateScale(2, .25f, -3) * Matrix4x4.CreateTranslation(50, -20, 13));
        var expected = Combine(first, changed); var actual = ProjectSceneAssembler.WithVertices(baseline, [first, changed]);
        Assert.Equal(expected.Surface.Snapshot.Vertices.ToArray(), actual.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(expected.Surface.Snapshot.Materials, actual.Surface.Snapshot.Materials);
        Assert.Equal(expected.Surface.Materials, actual.Surface.Materials);
        Assert.Equal(expected.Outline!.Vertices, actual.Outline!.Vertices);
        Assert.Equal(expected.Outline.Materials, actual.Outline.Materials);
        Assert.Equal(expected.Materials, actual.Materials);
        Assert.Equal(expected.Surface.Snapshot.AdditionalUvs!.Vertices.ToArray(), actual.Surface.Snapshot.AdditionalUvs!.Vertices.ToArray());
        Assert.Equal(expected.Surface.Snapshot.RawRadialSeeds!.Vertices.ToArray(), actual.Surface.Snapshot.RawRadialSeeds!.Vertices.ToArray());
        Assert.True(actual.Surface.Snapshot.HasDynamicVertices);
        Assert.False(baseline.Surface.Snapshot.HasDynamicVertices);
        Assert.Equal(first.Surface.Snapshot.Vertices[0], baseline.Surface.Snapshot.Vertices[3]);
        Assert.Same(shared, actual.Surface.Snapshot.Textures[0]); Assert.Same(secondTexture, actual.Surface.Snapshot.Textures[1]);
        Assert.Equal(actual.Surface.Snapshot.Vertices.ToArray(), ProjectSceneAssembler.WithVertices(actual, [first, changed]).Surface.Snapshot.Vertices.ToArray());
    }

    [Fact]
    public void EqualPixelsCannotReplaceTextureIdentityDuringFastPoseUpdate()
    {
        var original = Frame(3, [Texture("same")]); var baseline = Combine(original);
        var replaced = Frame(3, [Texture("same")]);
        var error = Assert.Throws<ArgumentException>(() => ProjectSceneAssembler.WithVertices(baseline, [replaced]));
        Assert.Contains("textures changed", error.Message);
        Assert.Same(original.Surface.Snapshot.Textures[0], baseline.Surface.Snapshot.Textures[0]);
    }

    [Fact]
    public void IdentityMeshCompositionAllocatesAtMostTwoFullGeometryCopiesWithoutGrowthBuffers()
    {
        const int count = 9000; var model = Frame(count); OriginalFrameScene[] models = [model];
        _ = Combine(model); // Warm JIT and shared static metadata before measuring thread-local managed allocation.
        long start = GC.GetAllocatedBytesForCurrentThread(); var result = ProjectSceneAssembler.Combine(models, Vector3.One, Vector3.UnitY);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        // 32-byte surface + 36-byte outline + 4-byte index, each output-owned and staging arrays.
        // The margin allows constructor metadata; there is no wall-clock or GC-generation assertion.
        Assert.InRange(allocated, count * 144L, count * 180L + 128 * 1024);
        Assert.Equal(count, result.Surface.Snapshot.Vertices.Length); Assert.Equal(count, result.Outline!.Vertices.Count);
        Assert.Equal(model.Surface.Snapshot.Vertices[count - 1], result.Surface.Snapshot.Vertices[count - 1]);
    }

    [Fact]
    public void PoseAndWorldChangesReuseTheSameCombinedAdditionalUvStorage()
    {
        var first = WithUvs(Frame(3), Uvs(3, 10)); var second = Frame(6);
        var baseline = Combine(first, second); var source = first.Surface.Snapshot;
        var vertices = source.Vertices.ToArray();
        vertices[0] = vertices[0] with { Position = new(11, 12, 13) };
        var changed = Reframe(first, new SceneSnapshot(vertices, source.Indices, source.Materials, source.Textures,
            Matrix4x4.CreateScale(-2, 3, .5f) * Matrix4x4.CreateTranslation(7, -4, 9),
            additionalUvs: source.AdditionalUvs).WithDeformedVertices(vertices));
        var actual = ProjectSceneAssembler.WithVertices(baseline, [changed, second]);
        var repeated = ProjectSceneAssembler.WithVertices(actual, [changed, second]);
        var expected = Combine(changed, second);
        Assert.Same(baseline.Surface.Snapshot.AdditionalUvs, actual.Surface.Snapshot.AdditionalUvs);
        Assert.Same(actual.Surface.Snapshot.AdditionalUvs, repeated.Surface.Snapshot.AdditionalUvs);
        Assert.Equal(expected.Surface.Snapshot.Vertices.ToArray(), actual.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(expected.Surface.Snapshot.AdditionalUvs!.Vertices.ToArray(), actual.Surface.Snapshot.AdditionalUvs!.Vertices.ToArray());
        Assert.Equal(default(SceneAdditionalUvVertex), actual.Surface.Snapshot.AdditionalUvs!.Vertices[3]);
    }

    [Fact]
    public void AdditionalUvReplacementChannelChangesAndPresenceChangesInvalidateTheCombination()
    {
        var original = WithUvs(Frame(3), Uvs(3, 10)); var baseline = Combine(original);
        var sameValues = WithUvs(original, new SceneAdditionalUvs(1, original.Surface.Snapshot.AdditionalUvs!.Vertices));
        var replaced = ProjectSceneAssembler.WithVertices(baseline, [sameValues]);
        Assert.NotSame(baseline.Surface.Snapshot.AdditionalUvs, replaced.Surface.Snapshot.AdditionalUvs);
        Assert.Equal(baseline.Surface.Snapshot.AdditionalUvs!.Vertices.ToArray(), replaced.Surface.Snapshot.AdditionalUvs!.Vertices.ToArray());
        var moreChannels = WithUvs(original, Uvs(3, 20, 2));
        var expanded = ProjectSceneAssembler.WithVertices(replaced, [moreChannels]);
        Assert.NotSame(replaced.Surface.Snapshot.AdditionalUvs, expanded.Surface.Snapshot.AdditionalUvs);
        Assert.Equal(2, expanded.Surface.Snapshot.AdditionalUvs!.ChannelCount);
        Assert.Equal(moreChannels.Surface.Snapshot.AdditionalUvs!.Vertices.ToArray(), expanded.Surface.Snapshot.AdditionalUvs!.Vertices.ToArray());
        var absent = WithUvs(original, null);
        var cleared = ProjectSceneAssembler.WithVertices(expanded, [absent]);
        Assert.Null(cleared.Surface.Snapshot.AdditionalUvs);
        var restored = ProjectSceneAssembler.WithVertices(cleared, [original]);
        Assert.NotSame(baseline.Surface.Snapshot.AdditionalUvs, restored.Surface.Snapshot.AdditionalUvs);
        Assert.Equal(original.Surface.Snapshot.AdditionalUvs!.Vertices.ToArray(), restored.Surface.Snapshot.AdditionalUvs!.Vertices.ToArray());
        Assert.Equal(10f, baseline.Surface.Snapshot.AdditionalUvs!.Vertices[0].Uv1.X);
    }

    [Fact]
    public void ReorderedSourceUvIdentitiesRebuildTheOrderedCombinedValues()
    {
        var first = WithUvs(Frame(3), Uvs(3, 10)); var second = WithUvs(Frame(3), Uvs(3, 20));
        var baseline = Combine(first, second);
        var actual = ProjectSceneAssembler.WithVertices(baseline, [second, first]);
        Assert.NotSame(baseline.Surface.Snapshot.AdditionalUvs, actual.Surface.Snapshot.AdditionalUvs);
        Assert.Equal(Combine(second, first).Surface.Snapshot.AdditionalUvs!.Vertices.ToArray(), actual.Surface.Snapshot.AdditionalUvs!.Vertices.ToArray());
        Assert.Equal(20f, actual.Surface.Snapshot.AdditionalUvs!.Vertices[0].Uv1.X);
        Assert.Equal(10f, actual.Surface.Snapshot.AdditionalUvs!.Vertices[3].Uv1.X);
        Assert.Equal(10f, baseline.Surface.Snapshot.AdditionalUvs!.Vertices[0].Uv1.X);
    }

    [Fact]
    public void ChangedNullSegmentLengthsInvalidateUvsDespiteEqualTotalVertexCount()
    {
        var shortEmpty = Frame(3); var longEmpty = Frame(6); var middle = WithUvs(Frame(3), Uvs(3, 10));
        var baseline = Combine(shortEmpty, middle, longEmpty);
        var actual = ProjectSceneAssembler.WithVertices(baseline, [longEmpty, middle, shortEmpty]);
        Assert.NotSame(baseline.Surface.Snapshot.AdditionalUvs, actual.Surface.Snapshot.AdditionalUvs);
        Assert.Equal(Combine(longEmpty, middle, shortEmpty).Surface.Snapshot.AdditionalUvs!.Vertices.ToArray(), actual.Surface.Snapshot.AdditionalUvs!.Vertices.ToArray());
        Assert.Equal(default(SceneAdditionalUvVertex), actual.Surface.Snapshot.AdditionalUvs!.Vertices[3]);
        Assert.Equal(10f, actual.Surface.Snapshot.AdditionalUvs!.Vertices[6].Uv1.X);
        Assert.Equal(10f, baseline.Surface.Snapshot.AdditionalUvs!.Vertices[3].Uv1.X);
    }

    [Fact]
    public void UvCacheHitAllocatesGeometryWithoutAnotherFullAdditionalUvStream()
    {
        const int count = 9000; var model = WithUvs(Frame(count), Uvs(count, 10));
        var baseline = Combine(model); OriginalFrameScene[] models = [model];
        _ = ProjectSceneAssembler.WithVertices(baseline, models);
        long start = GC.GetAllocatedBytesForCurrentThread(); var result = ProjectSceneAssembler.WithVertices(baseline, models);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        // Two 32-byte vertex arrays and two 36-byte outline arrays remain. A repeated UV
        // concatenation would add two 64-byte streams per vertex and exceed this margin.
        Assert.InRange(allocated, count * 136L, count * 136L + 128 * 1024);
        Assert.Same(baseline.Surface.Snapshot.AdditionalUvs, result.Surface.Snapshot.AdditionalUvs);
        Assert.Equal(count, result.Surface.Snapshot.AdditionalUvs!.Vertices.Length);
    }

    private static SceneAdditionalUvs Uvs(int count, float tag, int channels = 1) => new(channels,
        Enumerable.Range(0, count).Select(index => new SceneAdditionalUvVertex(new(tag + index, 2, 3, 4),
            channels > 1 ? new(tag, 5, 6, 7) : Vector4.Zero, Vector4.Zero, Vector4.Zero)).ToArray());
    private static OriginalFrameScene WithUvs(OriginalFrameScene frame, SceneAdditionalUvs? uvs)
        => Reframe(frame, frame.Surface.Snapshot.WithAdditionalUvs(uvs));
    private static OriginalFrameScene Reframe(OriginalFrameScene frame, SceneSnapshot snapshot)
        => new(new(snapshot, frame.Surface.Materials, frame.Surface.Profile), frame.Outline, frame.OutlineOptions,
            frame.Options, frame.Materials, frame.Lighting, frame.EditorOverlay);

    private static SceneTexture Texture(string name) => new(name, 1, 1, new byte[] { 32, 64, 128, 255 });
    private static OriginalFrameScene Combine(params OriginalFrameScene[] models) => ProjectSceneAssembler.Combine(models, Vector3.One, Vector3.UnitY);
    private static OriginalFrameScene Frame(int count, SceneTexture[]? textures = null, bool streams = false, Matrix4x4? world = null, bool dynamic = false)
    {
        textures ??= [];
        var vertices = Enumerable.Range(0, count).Select(index => new SceneVertex(new(index % 3, index % 5, index % 7),
            new(2, 3, 4), new(index % 2, .5f))).ToArray();
        var indices = Enumerable.Range(0, count).Select(index => (uint)index).ToArray();
        var material = new SceneMaterial("first", 0, count, Vector4.One, Vector3.One, new(.2f), 4,
            textures.Length == 0 ? -1 : 0, textures.Length == 0 ? -1 : textures.Length - 1);
        var uvs = streams ? new SceneAdditionalUvs(1, Enumerable.Range(0, count).Select(index =>
            new SceneAdditionalUvVertex(new(index, 2, 3, 4), Vector4.Zero, Vector4.Zero, Vector4.Zero)).ToArray()) : null;
        var seeds = streams ? new SceneRawRadialSeeds(Enumerable.Range(0, count).Select(index => new Vector3(index, 2, 3)).ToArray()) : null;
        var snapshot = new SceneSnapshot(vertices, indices, [material], textures, world, additionalUvs: uvs, rawRadialSeeds: seeds);
        if (dynamic) snapshot = snapshot.WithDeformedVertices(vertices);
        var binding = new OriginalStyleMaterialBinding(new(.4f), textures.Length > 1 ? textures.Length - 1 : -1,
            textures.Length > 1 ? OriginalSphereMode.Multiply : OriginalSphereMode.None);
        var outline = new OriginalOutlineData(vertices.Select(vertex => new OriginalOutlineVertex(vertex.Position, vertex.Normal,
            OriginalOutlineVertexMode.Expand, .7f, 0)), [new OriginalOutlineMaterial(.18f, Vector4.One)]);
        return new(new(snapshot, [binding]), outline, new(Vector4.One, Matrix4x4.Identity, new(0, 10, -30), 30, 1));
    }
    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0, 1e-6f);
}
