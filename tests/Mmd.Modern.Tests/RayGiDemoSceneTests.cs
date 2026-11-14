using System.Numerics;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class RayGiDemoSceneTests
{
    [Fact]
    public void RoomKeepsCharacterPrefixTexturesAppearanceAndOptionsWithoutMutatingInput()
    {
        var character = Character();
        var source = character.Surface.Snapshot;
        var vertices = source.Vertices.ToArray();
        var indices = source.Indices.ToArray();
        var frame = RayGiDemoScene.AddRoom(character);
        var combined = frame.Surface.Snapshot;

        Assert.NotSame(source, combined);
        Assert.Equal(vertices, combined.Vertices[..vertices.Length].ToArray());
        Assert.Equal(indices, combined.Indices[..indices.Length].ToArray());
        Assert.Equal(source.Materials, combined.Materials.Take(source.Materials.Count));
        Assert.Equal(character.Surface.Materials, frame.Surface.Materials.Take(source.Materials.Count));
        Assert.Equal(character.Materials, frame.Materials.Take(source.Materials.Count));
        Assert.Same(source.Textures[0], combined.Textures[0]);
        Assert.Equal(source.Textures.Count, combined.Textures.Count);
        Assert.Same(source.Camera, combined.Camera);
        Assert.Equal(source.World, combined.World);
        Assert.Equal(source.LightDirection, combined.LightDirection);
        Assert.Same(character.Surface.Profile, frame.Surface.Profile);
        Assert.Same(character.OutlineOptions, frame.OutlineOptions);
        Assert.Equal(character.Options! with { GroundShadow = null }, frame.Options);
        Assert.NotNull(character.Options!.GroundShadow);
        Assert.Equal(vertices, source.Vertices.ToArray());
        Assert.Equal(indices, source.Indices.ToArray());

        var second = RayGiDemoScene.AddRoom(character);
        Assert.Equal(combined.Vertices.ToArray(), second.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(combined.Indices.ToArray(), second.Surface.Snapshot.Indices.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoomFitsReferencedWorldBoundsAndIgnoresUnusedOutlierVertices(bool transformed)
    {
        var world = transformed
            ? Matrix4x4.CreateScale(1.7f, .6f, 1.1f) * Matrix4x4.CreateRotationY(.31f) * Matrix4x4.CreateTranslation(12, -4, 9)
            : Matrix4x4.Identity;
        var character = Character(world);
        var source = character.Surface.Snapshot;
        var (min, max) = ReferencedBounds(source);
        float height = max.Y - min.Y;
        var room = RayGiDemoScene.AddRoom(character).Surface.Snapshot;
        var (roomMin, roomMax) = Bounds(room.Vertices[source.Vertices.Length..].ToArray().Select(v => Vector3.Transform(v.Position, world)));
        float halfWidth = MathF.Max(.8f * height, (max.X - min.X) / 2 + .5f * height);

        Near(new((min.X + max.X) / 2 - halfWidth, min.Y - .015f * height, min.Z - .8f * height), roomMin);
        Near(new((min.X + max.X) / 2 + halfWidth, min.Y + 1.3f * height, max.Z + .45f * height), roomMax);
        Assert.True(roomMax.X < 100);
        Assert.Equal(source.World, room.World);
    }

    [Fact]
    public void AppendedOutlineBindingsKeepOriginalRawInputsAndDisableAllRoomEdges()
    {
        var character = Character();
        var frame = RayGiDemoScene.AddRoom(character);
        var source = character.Surface.Snapshot;
        var combined = frame.Surface.Snapshot;
        var outline = Assert.IsType<OriginalOutlineData>(frame.Outline);

        Assert.Equal(combined.Vertices.Length, outline.Vertices.Count);
        Assert.Equal(combined.Materials.Count, outline.Materials.Count);
        Assert.Equal(character.Outline!.Vertices, outline.Vertices.Take(source.Vertices.Length));
        Assert.Equal(character.Outline.Materials, outline.Materials.Take(source.Materials.Count));
        var positions = OriginalOutlineMath.BuildPositions(outline, frame.OutlineOptions!);
        var originalPositions = OriginalOutlineMath.BuildPositions(character.Outline, character.OutlineOptions!);
        Assert.Equal(originalPositions, positions[..source.Vertices.Length]);
        for (int m = source.Materials.Count; m < combined.Materials.Count; m++)
        {
            Assert.False(outline.Materials[m].DrawEnabled);
            var material = combined.Materials[m];
            for (int i = material.StartIndex; i < material.StartIndex + material.IndexCount; i++)
            {
                int vertex = (int)combined.Indices[i];
                Assert.Equal(m, outline.Vertices[vertex].SelectedMaterialIndex);
                Assert.Equal(0, outline.Vertices[vertex].VertexScale);
                Assert.Equal(new Vector4(combined.Vertices[vertex].Position, 1), positions[vertex]);
            }
        }
    }

    [Fact]
    public void RoomSurfacesParticipateInSecondaryHitsWithColoredWallsAndRealOccluders()
    {
        var character = Character(withOutline: false);
        var frame = RayGiDemoScene.AddRoom(character);
        var source = character.Surface.Snapshot;
        var snapshot = frame.Surface.Snapshot;
        var transport = RayGiScene.FromOriginal(frame);
        Assert.Equal(source.Materials.Count + 6, snapshot.Materials.Count);
        Assert.Equal(source.Indices.Length / 3 + 32, transport.Triangles.Length);
        Assert.Null(frame.Outline);
        Assert.Null(frame.OutlineOptions);
        Assert.Null(frame.Options);
        for (int m = source.Materials.Count; m < snapshot.Materials.Count; m++)
        {
            Assert.True(frame.Materials[m].SurfaceEnabled);
            Assert.True(frame.Materials[m].CastSelfShadow);
            Assert.True(frame.Materials[m].ReceiveSelfShadow);
            Assert.False(frame.Materials[m].CastGroundShadow);
            Assert.Equal(1, transport.Materials[m].Parameters.Z);
            Assert.Equal(1, snapshot.Materials[m].Diffuse.W);
            Assert.Equal(-1, snapshot.Materials[m].DiffuseTexture);
            Assert.True(frame.Surface.Materials[m].ToonShadowColor.X < .5f);
        }
        int first = source.Materials.Count;
        Assert.True(snapshot.Materials[first + 1].Diffuse.X > 4 * snapshot.Materials[first + 1].Diffuse.Y);
        Assert.True(snapshot.Materials[first + 2].Diffuse.Z > 4 * snapshot.Materials[first + 2].Diffuse.X);

        // The fixture's indexed bounds are [-2,3,-1]..[2,13,1], H=10.
        AssertHit(new(-7.5f, 12, -5), -Vector3.UnitX, first + 1);
        AssertHit(new(7.5f, 12, -5), Vector3.UnitX, first + 2);
        AssertHit(new(5, 12, 0), Vector3.UnitZ, first + 3);
        AssertHit(new(0, 4, -5), -Vector3.UnitY, first);
        AssertHit(new(-5.7f, 12, 1), -Vector3.UnitY, first + 4);
        AssertHit(new(5.7f, 12, -1.7f), -Vector3.UnitY, first + 5);
        Assert.False(transport.Bvh.TraceAny(new(new(0, 12, -5), -Vector3.UnitZ, .001f, 100)));
        Assert.False(transport.Bvh.TraceAny(new(new(5, 17, 0), Vector3.UnitY, .001f, 100)));

        void AssertHit(Vector3 origin, Vector3 direction, int material)
        {
            Assert.True(transport.Bvh.TraceClosest(new(origin, direction, .001f, 100), out var hit));
            Assert.Equal((uint)material, transport.Triangles[(int)hit.TriangleIndex].Meta.X);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void RoomWindingAndNormalsStayInWorldSpaceUnderNonuniformOrMirroredTransforms(int sign)
    {
        var world = Matrix4x4.CreateScale(sign * 1.4f, .8f, 2) * Matrix4x4.CreateRotationZ(.19f) * Matrix4x4.CreateTranslation(9, 3, -5);
        var character = Character(world);
        var source = character.Surface.Snapshot;
        var transport = RayGiScene.FromOriginal(RayGiDemoScene.AddRoom(character));
        foreach (var triangle in transport.Triangles[(source.Indices.Length / 3)..])
        {
            var a = Xyz(triangle.P0); var b = Xyz(triangle.P1); var c = Xyz(triangle.P2);
            var winding = Vector3.Cross(b - a, c - a);
            Assert.True(winding.LengthSquared() > 1e-5f);
            var normal = Vector3.Normalize(Xyz(triangle.N0U0));
            Assert.InRange(Vector3.Dot(Vector3.Normalize(winding), normal), .99999f, 1.00001f);
            Assert.True(MathF.Max(MathF.Abs(normal.X), MathF.Max(MathF.Abs(normal.Y), MathF.Abs(normal.Z))) > .99999f);
        }
    }

    [Fact]
    public void FlatCharacterUsesFiniteFallbackExtentAndNullInputIsExplicit()
    {
        SceneVertex[] vertices = [new(new(-2, 0, -1), Vector3.UnitY, Vector2.Zero), new(new(2, 0, -1), Vector3.UnitY, Vector2.UnitX), new(new(0, 0, 1), Vector3.UnitY, Vector2.UnitY)];
        var snapshot = new SceneSnapshot(vertices, [0, 1, 2], [new("flat", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1)]);
        var frame = RayGiDemoScene.AddRoom(new(new(snapshot, [new(Vector3.One)])));
        var (min, max) = Bounds(frame.Surface.Snapshot.Vertices[vertices.Length..].ToArray().Select(v => v.Position));
        Near(new(-4, -.06f, -4.2f), min);
        Near(new(4, 5.2f, 2.8f), max);
        Assert.Throws<ArgumentNullException>(() => RayGiDemoScene.AddRoom(null!));
    }

    private static OriginalFrameScene Character(Matrix4x4? world = null, bool withOutline = true)
    {
        SceneVertex[] vertices =
        [
            new(new(-2, 3, -1), Vector3.UnitZ, Vector2.Zero), new(new(2, 3, -1), Vector3.UnitZ, Vector2.UnitX), new(new(0, 13, 1), Vector3.UnitZ, Vector2.UnitY),
            new(new(-1, 5, 0), -Vector3.UnitZ, Vector2.Zero), new(new(1, 5, 0), -Vector3.UnitZ, Vector2.UnitX), new(new(0, 11, 0), -Vector3.UnitZ, Vector2.UnitY),
            new(new(10000, 20000, 30000), Vector3.UnitY, Vector2.Zero)
        ];
        var texture = new SceneTexture("character", 1, 1, [120, 180, 240, 180]);
        SceneMaterial[] materials = [new("skin", 0, 3, new(.8f, .7f, .6f, 1), new(.4f), new(.2f), 8, 0, 0), new("cloth", 3, 3, new(.9f, .7f, .3f, .6f), new(.1f), new(.15f), 4)];
        var snapshot = new SceneSnapshot(vertices, [0, 1, 2, 3, 4, 5], materials, [texture], world, lightDirection: new(.3f, .8f, -.5f));
        var style = new OriginalStyleScene(snapshot, [new(new(.7f), 0, OriginalSphereMode.Multiply), new(Vector3.One, DoubleSided: true)],
            new(new(.52f), new(.34f), Matrix4x4.Identity, OriginalTextureSampling.PointClamp));
        OriginalFrameMaterialFlags[] flags = [new(true, false, true), new(false, true, false, SurfaceEnabled: false, CullTransparentSurface: true)];
        if (!withOutline) return new(style, materials: flags);
        var outline = new OriginalOutlineData(vertices.Select((v, i) => new OriginalOutlineVertex(v.Position, new(0, 2, -.5f),
            i == 0 ? OriginalOutlineVertexMode.Inward : OriginalOutlineVertexMode.Expand, .7f, i < 3 ? 0 : 1)), [new(.8f, new(0, 0, 0, .7f)), new(1.2f, Vector4.One, false)]);
        var options = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false,
            new(Matrix4x4.Identity, 64, 32, true, false, OriginalTextureSampling.PointClamp, OriginalDepthComparison.Less),
            new(new(-1, -2, .5f), new(0, 0, 0, .5f), true, true),
            SceneCamera.Default with { OrthographicHeight = 30 }, Matrix4x4.CreateRotationY(.4f), false,
            new(Mode: LightingMode.SoftwareRayTracing, Strength: .5f));
        return new(style, outline, new(new(1, 2, 3, 1), Matrix4x4.Identity, new(0, 9, -30), 30, 2), options, flags);
    }

    private static (Vector3 Min, Vector3 Max) ReferencedBounds(SceneSnapshot snapshot)
        => Bounds(snapshot.Indices.ToArray().Select(i => Vector3.Transform(snapshot.Vertices[(int)i].Position, snapshot.World)));
    private static (Vector3 Min, Vector3 Max) Bounds(IEnumerable<Vector3> positions)
    {
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        foreach (var position in positions) { min = Vector3.Min(min, position); max = Vector3.Max(max, position); }
        return (min, max);
    }
    private static Vector3 Xyz(Vector4 value) => new(value.X, value.Y, value.Z);
    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0, 1e-4f);
}
