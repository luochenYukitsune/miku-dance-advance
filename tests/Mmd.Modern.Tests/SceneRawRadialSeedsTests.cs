using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>CPU-only explicit production and Core copy/extension seams; no hair consumer or GPU claim.</summary>
public sealed class SceneRawRadialSeedsTests
{
    [Fact]
    public void StreamOwnsItsInputAndKeepsTheBaseVertexLayout()
    {
        Vector3[] input = [new(2, -3, 4), Vector3.Zero];
        var seeds = new SceneRawRadialSeeds(input);
        input[0] = new(99);
        Assert.Equal(new Vector3(2, -3, 4), seeds.Vertices[0]);
        Assert.Equal(Vector3.Zero, seeds.Vertices[1]);
        var copied = seeds.Vertices.ToArray();
        copied[1] = Vector3.One;
        Assert.Equal(Vector3.Zero, seeds.Vertices[1]);
        Assert.Equal(12, Marshal.SizeOf<Vector3>());
        Assert.Equal(32, Marshal.SizeOf<SceneVertex>());
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void ConstructorRejectsEveryNonfiniteComponent(float invalid)
    {
        Assert.Throws<ArgumentException>(() => new SceneRawRadialSeeds([new(invalid, 0, 0)]));
        Assert.Throws<ArgumentException>(() => new SceneRawRadialSeeds([new(0, invalid, 0)]));
        Assert.Throws<ArgumentException>(() => new SceneRawRadialSeeds([new(0, 0, invalid)]));
    }

    [Fact]
    public void ProducerUsesExplicitSourceTextureSpaceAndSignedSourceZToSceneYBasis()
    {
        // Row-vector basis: source X -> scene X, source Y -> scene -Z, source Z -> scene Y.
        var basis = new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
        Vector3[] rest = [new(9, 10, 99), new(1, 2, 3), new(9, 10, -999)];
        var seeds = SceneRawRadialSeeds.FromRestPositions(rest, new(1, 2, 3), new(2, 4, 8), basis);
        // Independently calculated source seed is (-8/16, 8/8, 0), without normalization.
        Assert.Equal(new Vector3(-.5f, 0, -1), seeds.Vertices[0]);
        Assert.Equal(Vector3.Zero, seeds.Vertices[1]);
        Assert.Equal(seeds.Vertices[0], seeds.Vertices[2]); // Source Z is not the radial plane.
        Assert.True(seeds.Vertices[0].LengthSquared() > 1);
        rest[0] = Vector3.Zero;
        Assert.Equal(new Vector3(-.5f, 0, -1), seeds.Vertices[0]);

        // This signed permutation has determinant -1 and must also remain legal.
        basis.M23 = 1;
        var mirrored = SceneRawRadialSeeds.FromRestPositions([new(9, 10, 99)], new(1, 2, 3), new(2, 4, 8), basis);
        Assert.Equal(new Vector3(-.5f, 0, 1), mirrored.Vertices[0]);
    }

    [Fact]
    public void ProducerSupportsExplicitNegativeHalfSizesAndFiniteExtremeIntermediates()
    {
        var negative = SceneRawRadialSeeds.FromRestPositions([new(8, 8, 0)], Vector3.Zero, new(-2, -4, 1), Matrix4x4.Identity);
        Assert.Equal(new Vector3(.5f, -1, 0), negative.Vertices[0]);
        var large = SceneRawRadialSeeds.FromRestPositions([new(float.MaxValue, 0, 0)],
            new(-float.MaxValue, 0, 0), new(float.MaxValue, 1, 1), Matrix4x4.Identity);
        Assert.Equal(new Vector3(0, .5f, 0), large.Vertices[0]);
    }

    [Fact]
    public void ProducerRejectsNonfiniteRestOrTextureSpaceAndUnrepresentableSeeds()
    {
        Assert.Throws<ArgumentException>(() => Produce([new(float.NaN, 0, 0)]));
        Assert.Throws<ArgumentException>(() => Produce([new(0, 0, float.PositiveInfinity)]));
        Assert.Throws<ArgumentException>(() => SceneRawRadialSeeds.FromRestPositions([Vector3.One],
            new(0, float.NaN, 0), Vector3.One, Matrix4x4.Identity));
        foreach (var halfSize in new[] { new Vector3(0, 1, 1), new(1, 0, 1), new(1, 1, 0),
            new(float.NaN, 1, 1), new(1, float.PositiveInfinity, 1), new(1, 1, float.NegativeInfinity) })
            Assert.Throws<ArgumentException>(() => SceneRawRadialSeeds.FromRestPositions([Vector3.One],
                Vector3.Zero, halfSize, Matrix4x4.Identity));
        Assert.Throws<ArgumentException>(() => SceneRawRadialSeeds.FromRestPositions([new(float.MaxValue, 0, 0)],
            Vector3.Zero, new(float.Epsilon, 1, 1), Matrix4x4.Identity));
    }

    [Fact]
    public void ProducerRequiresAnOrthogonalDirectionOnlyBasis()
    {
        var shear = Matrix4x4.Identity; shear.M12 = .2f;
        var perspective = Matrix4x4.Identity; perspective.M14 = .1f;
        var badHomogeneous = Matrix4x4.Identity; badHomogeneous.M44 = 2;
        var nonfinite = Matrix4x4.Identity; nonfinite.M33 = float.NaN;
        foreach (var invalid in new[] { new Matrix4x4(), Matrix4x4.CreateScale(2),
            Matrix4x4.CreateTranslation(1, 2, 3), shear, perspective, badHomogeneous, nonfinite })
            Assert.Throws<ArgumentException>(() => SceneRawRadialSeeds.FromRestPositions([Vector3.One],
                Vector3.Zero, Vector3.One, invalid));
        var rotation = Matrix4x4.CreateRotationX(.37f) * Matrix4x4.CreateRotationY(-.61f);
        var seed = SceneRawRadialSeeds.FromRestPositions([new(4, 8, 1)], Vector3.Zero, Vector3.One, rotation);
        Near(Vector3.TransformNormal(new(-2, 1, 0), rotation), seed.Vertices[0]);
    }

    [Fact]
    public void SnapshotConstructorAndReplacementRequireExactCount()
    {
        var scene = Scene();
        var shortSeeds = new SceneRawRadialSeeds([Vector3.Zero]);
        Assert.Throws<ArgumentException>(() => new SceneSnapshot(scene.Vertices, scene.Indices,
            scene.Materials, rawRadialSeeds: shortSeeds));
        Assert.Throws<ArgumentException>(() => scene.WithRawRadialSeeds(shortSeeds));
        Assert.Throws<ArgumentException>(() => scene.WithRawRadialSeeds(Seeds()).WithDeformedVertices([scene.Vertices[0]]));
    }

    [Fact]
    public void EverySnapshotCopyPreservesTheSameRestSeedAndIndependentAdditionalUvs()
    {
        var seeds = Seeds();
        var uvs = Uvs(1);
        var source = Scene(seeds, uvs);
        var deformed = source.Vertices.ToArray();
        deformed[0] = deformed[0] with { Position = new(100, 200, 300), Normal = Vector3.UnitX };
        var snapshots = new List<SceneSnapshot>();
        var current = source.WithDeformedVertices(deformed); snapshots.Add(current);
        current = current.WithLighting(new(1, 2, -3)); snapshots.Add(current);
        current = current.WithCamera(source.Camera with { OrthographicHeight = 13 }); snapshots.Add(current);
        current = current.WithMaterials(current.Materials.Select(m => m with { Diffuse = new(.3f, .4f, .5f, .6f) })); snapshots.Add(current);
        var changedUvs = Uvs(2);
        current = current.WithAdditionalUvs(changedUvs); snapshots.Add(current);
        foreach (var copy in snapshots)
        {
            Assert.Same(seeds, copy.RawRadialSeeds);
            Assert.Equal(seeds.Vertices.ToArray(), copy.RawRadialSeeds!.Vertices.ToArray());
            Assert.True(copy.HasDynamicVertices);
            Assert.Equal(source.World, copy.World);
            Assert.Equal(deformed, copy.Vertices.ToArray());
        }
        Assert.Same(uvs, snapshots[0].AdditionalUvs);
        Assert.Same(changedUvs, current.AdditionalUvs);
        Assert.Equal(Uvs(1).Vertices.ToArray(), source.AdditionalUvs!.Vertices.ToArray());
        Assert.Equal(new Vector3(2, -3, 4), seeds.Vertices[0]);
    }

    [Fact]
    public void SeedOnlyReplacementAndRemovalPreserveAllOtherSnapshotState()
    {
        var source = Scene(Seeds(), Uvs(1));
        source = source.WithDeformedVertices(source.Vertices.ToArray());
        Assert.Same(source, source.WithRawRadialSeeds(source.RawRadialSeeds));
        var replacement = new SceneRawRadialSeeds([new(7, 8, 9), Vector3.Zero, new(-2, -4, 8)]);
        var changed = source.WithRawRadialSeeds(replacement);
        Assert.Same(replacement, changed.RawRadialSeeds);
        foreach (var copy in new[] { changed, changed.WithRawRadialSeeds(null) })
        {
            Assert.Equal(source.Vertices.ToArray(), copy.Vertices.ToArray());
            Assert.Equal(source.Indices.ToArray(), copy.Indices.ToArray());
            Assert.Same(source.Materials, copy.Materials);
            Assert.Same(source.Textures, copy.Textures);
            Assert.Same(source.AdditionalUvs, copy.AdditionalUvs);
            Assert.Equal(source.World, copy.World);
            Assert.Same(source.Camera, copy.Camera);
            Assert.Equal(source.LightDirection, copy.LightDirection);
            Assert.True(copy.HasDynamicVertices);
        }
        Assert.Null(changed.WithRawRadialSeeds(null).RawRadialSeeds);
        Assert.NotNull(source.RawRadialSeeds);
    }

    [Fact]
    public void OrdinaryAndEmptyCopiesKeepAbsenceAndIdentityReplacementAllocatesNothing()
    {
        var ordinary = Scene();
        Assert.Null(ordinary.RawRadialSeeds);
        // The reference-equal early return provides a deterministic no-allocation assertion without GC counters.
        Assert.Same(ordinary, ordinary.WithRawRadialSeeds(null));
        Assert.Null(ordinary.WithDeformedVertices(ordinary.Vertices.ToArray())
            .WithLighting(Vector3.UnitY).WithCamera(SceneCamera.Default)
            .WithMaterials(ordinary.Materials).WithAdditionalUvs(Uvs(1)).RawRadialSeeds);
        var empty = SceneSnapshot.CreateEmpty();
        Assert.Null(empty.RawRadialSeeds);
        Assert.Same(empty, empty.WithRawRadialSeeds(null));
        Assert.Null(empty.WithCamera(SceneCamera.Default).WithLighting(Vector3.UnitY).RawRadialSeeds);
    }

    [Fact]
    public void AppearanceReconstructionForwardsTheExactSeedAndUvStreams()
    {
        var seeds = Seeds(); var uvs = Uvs(1);
        var source = Scene(seeds, uvs);
        var changed = SceneAppearance.Apply(source);
        Assert.NotSame(source, changed);
        Assert.Same(seeds, changed.RawRadialSeeds);
        Assert.Same(uvs, changed.AdditionalUvs);
        Assert.Equal(source.Vertices.ToArray(), changed.Vertices.ToArray());
        Assert.Equal(source.Indices.ToArray(), changed.Indices.ToArray());
        Assert.Equal(source.World, changed.World);
        Assert.Same(source.Camera, changed.Camera);
        Assert.Equal(source.LightDirection, changed.LightDirection);
        Assert.Same(source, SceneAppearance.Apply(source, SceneAppearanceMode.MaterialMapped));
        Assert.Null(SceneAppearance.Apply(Scene()).RawRadialSeeds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(.5f)]
    public void RoomKeepsTheOriginalLocalSeedPrefixAndZeroPadsOnlyNewVertices(float emission)
    {
        var seeds = Seeds(); var uvs = Uvs(1);
        var source = Scene(seeds, uvs);
        var frame = new OriginalFrameScene(new(source, [new(Vector3.One)]));
        var room = RayGiDemoScene.AddRoom(frame, emission).Surface.Snapshot;
        var expanded = Assert.IsType<SceneRawRadialSeeds>(room.RawRadialSeeds);
        Assert.Equal(room.Vertices.Length, expanded.Vertices.Length);
        Assert.Equal(seeds.Vertices.ToArray(), expanded.Vertices[..source.Vertices.Length].ToArray());
        foreach (var seed in expanded.Vertices[source.Vertices.Length..]) Assert.Equal(Vector3.Zero, seed);
        Assert.Equal(source.Vertices.ToArray(), room.Vertices[..source.Vertices.Length].ToArray());
        Assert.Equal(source.World, room.World); // No direction transform is baked into the retained local prefix.
        Assert.Equal(uvs.Vertices.ToArray(), room.AdditionalUvs!.Vertices[..source.Vertices.Length].ToArray());
        Assert.Same(seeds, source.RawRadialSeeds);
        var ordinary = new OriginalFrameScene(new(Scene(), [new(Vector3.One)]));
        Assert.Null(RayGiDemoScene.AddRoom(ordinary, emission).Surface.Snapshot.RawRadialSeeds);
    }

    private static SceneRawRadialSeeds Produce(Vector3[] rest)
        => SceneRawRadialSeeds.FromRestPositions(rest, Vector3.Zero, Vector3.One, Matrix4x4.Identity);
    private static SceneRawRadialSeeds Seeds() => new([new(2, -3, 4), Vector3.Zero, new(-1, 7, 2)]);
    private static SceneAdditionalUvs Uvs(float value) => new(4,
        Enumerable.Range(0, 3).Select(i => new SceneAdditionalUvVertex(new(value, i, 2, 3), new(4, 5, 6, 7), new(8, 9, 10, 11), new(12, 13, 14, 15))).ToArray());
    private static SceneSnapshot Scene(SceneRawRadialSeeds? seeds = null, SceneAdditionalUvs? uvs = null)
    {
        SceneVertex[] vertices = [new(new(-1, 0, 0), -Vector3.UnitZ, Vector2.Zero),
            new(new(1, 0, 0), -Vector3.UnitZ, Vector2.UnitX), new(new(0, 2, 0), -Vector3.UnitZ, Vector2.UnitY)];
        return new(vertices, [0, 1, 2], [new("fixture", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1)],
            world: Matrix4x4.CreateScale(-2, 3, .5f) * Matrix4x4.CreateRotationY(.2f) * Matrix4x4.CreateTranslation(5, 6, 7),
            additionalUvs: uvs, rawRadialSeeds: seeds);
    }
    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0, 1e-5f);
}
