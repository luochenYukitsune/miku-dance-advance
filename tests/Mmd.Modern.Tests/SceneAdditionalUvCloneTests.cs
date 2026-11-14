using System.Numerics;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class SceneAdditionalUvCloneTests
{
    [Theory]
    [InlineData(1, 0f)]
    [InlineData(1, .25f)]
    [InlineData(4, 0f)]
    [InlineData(4, .25f)]
    public void RoomRetainsSubTextureUvPrefixAndZeroFillsEveryAddedVertex(int channels, float emission)
    {
        var inputUvs = Enumerable.Range(0, 3).Select(i => new SceneAdditionalUvVertex(
            new(i + .1f, i + .2f, i + .3f, i + .4f),
            new(i + 1, i + 2, i + 3, i + 4),
            new(i + 5, i + 6, i + 7, i + 8),
            new(i + 9, i + 10, i + 11, i + 12))).ToArray();
        var stream = new SceneAdditionalUvs(channels, inputUvs);
        var source = Triangle(stream);
        var binding = new OriginalStyleMaterialBinding(new(.4f), 0, OriginalSphereMode.SubTexture)
            { SubTextureAdditive = true };
        var character = new OriginalFrameScene(new OriginalStyleScene(source, [binding]));

        // A missing canonical stream would make the retained SubTexture binding invalid.
        var room = RayGiDemoScene.AddRoom(character, emission);
        var snapshot = room.Surface.Snapshot;
        var expanded = Assert.IsType<SceneAdditionalUvs>(snapshot.AdditionalUvs);
        Assert.NotSame(stream, expanded);
        Assert.Equal(channels, expanded.ChannelCount);
        Assert.Equal(snapshot.Vertices.Length, expanded.Vertices.Length);
        Assert.Equal(inputUvs, expanded.Vertices[..source.Vertices.Length].ToArray());
        Assert.True(expanded.Vertices.Length > source.Vertices.Length);
        Assert.All(expanded.Vertices[source.Vertices.Length..].ToArray(),
            vertex => Assert.Equal(default(SceneAdditionalUvVertex), vertex));
        Assert.Same(binding, room.Surface.Materials[0]);
        Assert.Equal(source.Vertices.ToArray(), snapshot.Vertices[..source.Vertices.Length].ToArray());
        Assert.Equal(source.Indices.ToArray(), snapshot.Indices[..source.Indices.Length].ToArray());
        Assert.Equal(source.World, snapshot.World);

        Assert.Same(stream, source.AdditionalUvs);
        Assert.Equal(inputUvs, stream.Vertices.ToArray());
        Assert.Equal(3, source.Vertices.Length);
    }

    [Fact]
    public void RoomDoesNotInventAnAdditionalUvStreamForModelsWithoutOne()
    {
        var source = Triangle(null);
        var frame = new OriginalFrameScene(new OriginalStyleScene(source, [new(new(.4f))]));
        Assert.Null(RayGiDemoScene.AddRoom(frame, .25f).Surface.Snapshot.AdditionalUvs);
        Assert.Null(source.AdditionalUvs);
    }

    private static SceneSnapshot Triangle(SceneAdditionalUvs? stream) => new(
        [new(new(-1, 2, 0), Vector3.UnitZ, Vector2.Zero),
         new(new(1, 2, 0), Vector3.UnitZ, Vector2.UnitX),
         new(new(0, 4, 0), Vector3.UnitZ, Vector2.UnitY)],
        [0, 1, 2], [new("character", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1)],
        [new SceneTexture("subtexture", 1, 1, [255, 255, 255, 255])],
        world: Matrix4x4.CreateScale(1.3f, .8f, 1.1f) * Matrix4x4.CreateRotationZ(.2f)
            * Matrix4x4.CreateTranslation(7, -3, 9), additionalUvs: stream);
}
