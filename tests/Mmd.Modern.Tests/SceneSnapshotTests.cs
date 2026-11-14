using System.Numerics;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class SceneSnapshotTests
{
    private static SceneVertex[] Triangle() => [new(new(-1, 0, 0), -Vector3.UnitZ, Vector2.Zero), new(new(1, 0, 0), -Vector3.UnitZ, Vector2.UnitX), new(new(0, 1, 0), -Vector3.UnitZ, Vector2.UnitY)];
    private static SceneMaterial Material(int count = 3) => new("triangle", 0, count, Vector4.One, Vector3.Zero, Vector3.Zero, 1);

    [Fact]
    public void SnapshotCopiesMutableInputsAndUsesUintIndices()
    {
        var vertices = new SceneVertex[65539];
        Array.Fill(vertices, Triangle()[0]);
        uint[] indices = [65536, 65537, 65538];
        SceneMaterial[] materials = [Material()];
        byte[] pixels = [255, 0, 0, 255];
        var texture = new SceneTexture("original", 1, 1, pixels);
        SceneTexture[] textures = [texture];
        var snapshot = new SceneSnapshot(vertices, indices, materials, textures);
        vertices[65536] = default; indices[0] = 0; materials[0] = Material(0); pixels[0] = 0; textures[0] = new("other", 1, 1, pixels);
        Assert.Equal(65536u, snapshot.Indices[0]);
        Assert.Equal(-1, snapshot.Vertices[65536].Position.X);
        Assert.Equal(3, snapshot.Materials[0].IndexCount);
        Assert.Equal(255, snapshot.Textures[0].Rgba[0]);
    }

    [Fact]
    public void RejectsInvalidGeometryRangesTexturesAndNonFiniteState()
    {
        Assert.Throws<ArgumentException>(() => new SceneSnapshot([], [], []));
        Assert.Throws<ArgumentException>(() => new SceneSnapshot(Triangle(), [0, 1, 3], [Material()]));
        Assert.Throws<ArgumentException>(() => new SceneSnapshot(Triangle(), [0, 1, 2], [Material(2)]));
        Assert.Throws<ArgumentException>(() => new SceneSnapshot(Triangle(), [0, 1, 2], [Material() with { StartIndex = 1 }]));
        Assert.Throws<ArgumentException>(() => new SceneSnapshot(Triangle(), [0, 1, 2], [Material() with { DiffuseTexture = 0 }]));
        Assert.Throws<ArgumentException>(() => new SceneTexture("bad", 2, 1, [0, 0, 0, 0]));
        var bad = Triangle(); bad[0] = bad[0] with { Uv = new(float.NaN, 0) };
        Assert.Throws<ArgumentException>(() => new SceneSnapshot(bad, [0, 1, 2], [Material()]));
        Assert.Throws<ArgumentException>(() => new SceneSnapshot(Triangle(), [0, 1, 2], [Material()], world: default(Matrix4x4)));
        Assert.Throws<ArgumentException>(() => new SceneSnapshot(Triangle(), [0, 1, 2], [Material()], camera: SceneCamera.Default with { Eye = SceneCamera.Default.Target }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorldPlacementSharesPrivateStreamsAndRetainsAllRenderMetadata(bool dynamic)
    {
        var input = Triangle();
        var uvs = new SceneAdditionalUvs(1, new SceneAdditionalUvVertex[3]);
        var seeds = new SceneRawRadialSeeds([Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ]);
        var source = new SceneSnapshot(input, [0, 1, 2], [Material()],
            additionalUvs: uvs, rawRadialSeeds: seeds);
        if (dynamic) source = source.WithDeformedVertices(input);
        var world = Matrix4x4.CreateScale(-2, 3, .5f) * Matrix4x4.CreateRotationY(.37f)
            * Matrix4x4.CreateTranslation(4, -5, 6);
        var placed = source.WithWorld(world);
        Assert.Equal(world, placed.World);
        Assert.Equal(Matrix4x4.Identity, source.World);
        Assert.Equal(dynamic, placed.HasDynamicVertices);
        Assert.True(source.Vertices.Overlaps(placed.Vertices, out int vertexOffset)); Assert.Equal(0, vertexOffset);
        Assert.True(source.Indices.Overlaps(placed.Indices, out int indexOffset)); Assert.Equal(0, indexOffset);
        Assert.Same(source.Materials, placed.Materials); Assert.Same(source.Textures, placed.Textures);
        Assert.Same(source.Camera, placed.Camera); Assert.Equal(source.LightDirection, placed.LightDirection);
        Assert.Same(uvs, placed.AdditionalUvs); Assert.Same(seeds, placed.RawRadialSeeds);
        input[0] = default;
        Assert.Equal(Triangle()[0], placed.Vertices[0]);
        Assert.Same(placed, placed.WithWorld(world));
    }

    [Fact]
    public void WorldPlacementRejectsNonAffineSingularAndNonfiniteMatricesWithoutMutatingSource()
    {
        var source = new SceneSnapshot(Triangle(), [0, 1, 2], [Material()]);
        Matrix4x4[] invalid = [default, Matrix4x4.Identity with { M14 = .01f },
            Matrix4x4.Identity with { M22 = float.NaN }, Matrix4x4.Identity with { M41 = float.PositiveInfinity }];
        foreach (var world in invalid) Assert.Throws<ArgumentException>(() => source.WithWorld(world));
        Assert.Equal(Matrix4x4.Identity, source.World);
        var empty = SceneSnapshot.CreateEmpty().WithWorld(Matrix4x4.CreateTranslation(1, 2, 3));
        Assert.True(empty.IsEmpty); Assert.False(empty.HasDynamicVertices);
        Assert.Equal(new Vector3(1, 2, 3), empty.World.Translation);
    }

    [Fact]
    public void PmdAdapterPreservesSkinnedVerticesAndReportsUnloadedTextures()
    {
        using var input = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd"));
        var model = PmdReader.Read(input);
        var vertices = Skinning.Apply(model, Skinning.CreateSkinMatrices(model));
        var adapted = PmdSceneAdapter.FromSkinned(model, vertices, .4f);
        Assert.Equal(2618, adapted.Snapshot.Vertices.Length);
        Assert.Equal(14, adapted.Snapshot.Materials.Count);
        Assert.Equal((uint)model.Indices[^1], adapted.Snapshot.Indices[^1]);
        Assert.Equal(vertices[374].Position, adapted.Snapshot.Vertices[374].Position);
        Assert.Equal(Matrix4x4.CreateRotationY(.4f), adapted.Snapshot.World);
        Assert.Contains(adapted.Diagnostics, d => d.Contains("toon", StringComparison.OrdinalIgnoreCase));
    }
}
