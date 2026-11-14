using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class SceneAdditionalUvTests
{
    [Fact]
    public void StreamCopiesAllFourVectorsWithoutChangingBaseVertexLayout()
    {
        SceneAdditionalUvVertex[] input = [new(new(1, 2, 3, 4), new(5, 6, 7, 8), new(9, 10, 11, 12), new(13, 14, 15, 16))];
        var expected = input[0];
        var stream = new SceneAdditionalUvs(4, input);
        input[0] = default;
        Assert.Equal(4, stream.ChannelCount);
        Assert.Equal(expected, stream.Vertices[0]);
        Assert.Equal(32, Marshal.SizeOf<SceneVertex>());
        Assert.Equal(64, Marshal.SizeOf<SceneAdditionalUvVertex>());
    }

    [Fact]
    public void StreamRejectsInvalidChannelCountsAndNonfiniteComponents()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneAdditionalUvs(0, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneAdditionalUvs(5, []));
        Assert.Throws<ArgumentException>(() => new SceneAdditionalUvs(1, [new(new(float.NaN, 0, 0, 0), default, default, default)]));
        Assert.Throws<ArgumentException>(() => new SceneAdditionalUvs(4, [new(default, default, default, new(0, 0, 0, float.PositiveInfinity))]));
    }

    [Fact]
    public void ClonesRetainImmutableStreamAndReplacementDoesNotMutateOldSnapshots()
    {
        var original = Triangle();
        var stream = new SceneAdditionalUvs(1, new SceneAdditionalUvVertex[3]);
        var scene = original.WithAdditionalUvs(stream);
        Assert.Null(original.AdditionalUvs);
        Assert.Same(stream, scene.AdditionalUvs);
        Assert.Same(stream, scene.WithLighting(Vector3.UnitX).AdditionalUvs);
        Assert.Same(stream, scene.WithCamera(SceneCamera.Default).AdditionalUvs);
        Assert.Same(stream, scene.WithMaterials(scene.Materials).AdditionalUvs);
        Assert.Same(stream, SceneAppearance.Apply(scene).AdditionalUvs);
        var deformed = scene.WithDeformedVertices(scene.Vertices.ToArray());
        Assert.True(deformed.HasDynamicVertices);
        Assert.Same(stream, deformed.AdditionalUvs);
        var cleared = deformed.WithAdditionalUvs(null);
        Assert.Null(cleared.AdditionalUvs);
        Assert.True(cleared.HasDynamicVertices);
        Assert.Same(stream, deformed.AdditionalUvs);
        Assert.Same(scene, scene.WithAdditionalUvs(stream));
        Assert.Throws<ArgumentException>(() => scene.WithAdditionalUvs(new(1, new SceneAdditionalUvVertex[2])));
    }

    [Fact]
    public void ConstructorValidatesStreamVertexCountAndEmptySnapshotsCanRetainEmptyStream()
    {
        var mesh = Triangle();
        var stream = new SceneAdditionalUvs(2, new SceneAdditionalUvVertex[3]);
        var scene = new SceneSnapshot(mesh.Vertices, mesh.Indices, mesh.Materials, additionalUvs: stream);
        Assert.Same(stream, scene.AdditionalUvs);
        Assert.Throws<ArgumentException>(() => new SceneSnapshot(mesh.Vertices, mesh.Indices, mesh.Materials,
            additionalUvs: new(1, new SceneAdditionalUvVertex[2])));
        var empty = SceneSnapshot.CreateEmpty().WithAdditionalUvs(new(1, []));
        Assert.Empty(empty.WithLighting(Vector3.UnitY).AdditionalUvs!.Vertices.ToArray());
    }

    private static SceneSnapshot Triangle() => new(
        [new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero), new(Vector3.UnitX, Vector3.UnitZ, Vector2.UnitX), new(Vector3.UnitY, Vector3.UnitZ, Vector2.UnitY)],
        [0, 1, 2], [new("triangle", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1)]);
}
