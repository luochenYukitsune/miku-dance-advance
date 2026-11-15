using System.Numerics;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmxSoftBodyTests
{
    [Fact]
    public void RealBulletClothFallsKeepsPinAndResetClearsTrajectory()
    {
        var definition = Cloth();
        using var backend = new SoftBodyBulletBackend(new(0, -9.8f, 0), null);
        backend.Initialize([definition], []);
        var positions = definition.Positions.ToArray();
        backend.Reset(positions, []);
        for (int i = 0; i < 60; i++) backend.Step(1f / 120, positions, []);
        var sample = backend.Sample()[0];
        Assert.Equal(positions[0], sample.Positions[0]);
        Assert.True(sample.Positions[3].Y < positions[3].Y - .01f);
        Assert.All(sample.Normals, value => Assert.True(float.IsFinite(value.LengthSquared())));
        var moved = positions.Select(p => p + new Vector3(2, 3, 4)).ToArray();
        backend.Reset(moved, []);
        Assert.Equal(moved, backend.Sample()[0].Positions);
        for (int i = 0; i < 60; i++) backend.Step(1f / 120, moved, []);
        var repeat = backend.Sample()[0].Positions;
        backend.Reset(moved, []);
        for (int i = 0; i < 60; i++) backend.Step(1f / 120, moved, []);
        Assert.Equal(repeat, backend.Sample()[0].Positions);
    }

    [Fact]
    public void SparseGlobalPinsUseLocalNodeMapAndRopeUsesItsActualNodes()
    {
        var definition = new SoftBodyDefinition("rope", SoftBodyShape.Rope, [4, 8, 12],
            [new(1, 5, 0), new(2, 5, 0), new(3, 5, 0)], [], [4], [], new());
        using var backend = new SoftBodyBulletBackend(new(0, -9.8f, 0), null);
        backend.Initialize([definition], []);
        var targets = new Vector3[13];
        for (int i = 0; i < 3; i++) targets[definition.VertexIndices[i]] = definition.Positions[i];
        backend.Reset(targets, []);
        for (int i = 0; i < 30; i++) backend.Step(1f / 120, targets, []);
        var sample = backend.Sample()[0];
        Assert.Equal(new[] { 4, 8, 12 }, sample.VertexIndices);
        Assert.Equal(new Vector3(1, 5, 0), sample.Positions[0]);
        Assert.True(sample.Positions[2].Y < 5);
    }

    [Theory]
    [InlineData(2e9f)]
    [InlineData(-1.5e6f)]
    public void HugeFiniteVertexCoordinatesFailBeforeNativeAllocation(float coordinate)
        // Finite coordinates near 1.6e9 overflowed the native sparse-SDF cell index and crashed the process.
        => Assert.Throws<ArgumentException>(() => new SoftBodyDefinition("huge", SoftBodyShape.Rope,
            [4, 8], [Vector3.Zero, new(coordinate, 0, 0)], [], [], [], new()));

    [Fact]
    public void TinyMassWhosePerNodeInverseOverflowsFailsBeforeNativeAllocation()
        => Assert.Throws<ArgumentException>(() => new SoftBodyDefinition("light", SoftBodyShape.Rope,
            [4, 8], [Vector3.Zero, Vector3.One], [], [], [], new() { Mass = 1e-45f }));

    [Fact]
    public void InvalidGlobalPinFailsBeforeNativeAllocation()
        => Assert.Throws<ArgumentException>(() => new SoftBodyDefinition("bad", SoftBodyShape.Rope,
            [4, 8], [Vector3.Zero, Vector3.One], [], [0], [], new()));

    [Fact]
    public void PhysicalNearAnchorRequiresReverseFeedback()
    {
        var definition = new SoftBodyDefinition("near", SoftBodyShape.Rope,
            [0, 1], [Vector3.Zero, Vector3.One], [], [], [new(0, 0, true)], new());
        var collider = new PhysicsBodyDefinition("physical", -1, PhysicsShapeKind.Sphere, Vector3.One,
            Vector3.Zero, Vector3.Zero, 1, 0, 0, 0, .5f, 0, ushort.MaxValue, PhysicsBodyMode.Dynamic);
        using var backend = new SoftBodyBulletBackend(Vector3.Zero, null);
        Assert.Throws<NotSupportedException>(() => backend.Initialize([definition], [collider]));
    }

    [Fact]
    public void ClusterModeWithAnalyticGroundIsAdmittedBeforeNativeAllocation()
    {
        var d = new SoftBodyDefinition("cluster", SoftBodyShape.TriMesh, [0, 1, 2],
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0, 1, 2], [], [], new() { Flags = 2, ClusterCount = 1 });
        using var backend = new SoftBodyBulletBackend();
        backend.Initialize([d], []);
    }

    [Fact]
    public void PmxMaterialSubsetMapsGlobalPinsAndPreservesNonsoftVerticesAfterSkinning()
    {
        var model = MaterialFixture();
        var mapped = PmxSoftBodyAdapter.Convert(model);
        var definition = Assert.Single(mapped.Definitions);
        Assert.Equal(new[] { 4, 5, 6 }, definition.VertexIndices);
        Assert.Equal(new[] { 6 }, definition.PinVertexIndices);
        Assert.Equal(new[] { 0, 1, 2 }, definition.TriangleIndices);
        using var playback = new PmxSoftBodyPlayback(model, new SoftBodyBulletBackend(new(0, -9.8f, 0), null));
        var skinned = model.Vertices.Select(v => new SceneVertex(v.Position + new Vector3(1, 2, 3), v.Normal, v.Uv)).ToArray();
        playback.Reset(skinned);
        var output = playback.Advance(.5, skinned);
        Assert.Equal(skinned[0], output[0]); Assert.Equal(skinned[3], output[3]);
        Assert.Equal(skinned[6].Position, output[6].Position);
        Assert.True(output[4].Position.Y < skinned[4].Position.Y - .01f);
        playback.Reset(skinned); Assert.Equal(skinned.Select(v => v.Position), playback.Apply(skinned).Select(v => v.Position));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(2f)]
    public void GroundCollisionKeepsFallingClothAbovePlane(float groundY)
    {
        var d = new SoftBodyDefinition("falling", SoftBodyShape.TriMesh, [0, 1, 2],
            [new(-.3f, groundY + 2, 0), new(.3f, groundY + 2, 0), new(0, groundY + 2, .6f)], [0, 1, 2], [], [], new());
        using var backend = new SoftBodyBulletBackend(new(0, -9.8f, 0), groundY);
        backend.Initialize([d], []); backend.Reset(d.Positions.ToArray(), []);
        for (int i = 0; i < 240; i++) backend.Step(1f / 120, d.Positions.ToArray(), []);
        // Analytic plane distance settles uniformly at authored .02 margin (no extruded concave hull floor).
        Assert.All(backend.Sample()[0].Positions, p => Assert.InRange(MathF.Abs(p.Y - groundY - d.Settings.Margin), 0, .002f));
    }

    [Fact]
    public void RigidAnchorFollowsExternallyDrivenProxy()
    {
        var d = new SoftBodyDefinition("anchored", SoftBodyShape.Rope, [0, 1],
            [new(0, 3, 0), new(1, 3, 0)], [], [], [new(0, 0)], new());
        var collider = new PhysicsBodyDefinition("proxy", -1, PhysicsShapeKind.Sphere, new(.1f), new(0, 3, 0),
            Vector3.Zero, 0, 0, 0, 0, .5f, 0, ushort.MaxValue, PhysicsBodyMode.Kinematic);
        using var backend = new SoftBodyBulletBackend(Vector3.Zero, null);
        backend.Initialize([d], [collider]); backend.Reset(d.Positions.ToArray(), [Matrix4x4.CreateTranslation(0, 3, 0)]);
        var moved = Matrix4x4.CreateTranslation(2, 3, 0);
        for (int i = 0; i < 120; i++) backend.Step(1f / 120, d.Positions.ToArray(), [moved]);
        Assert.InRange(Vector3.Distance(new(2, 3, 0), backend.Sample()[0].Positions[0]), 0, .01f);
    }

    internal static PmxModel MaterialFixture()
    {
        var template = PmmMotionAdapterTests.Pmx(["root"]);
        PmxMaterial Material(int count) => new() { Name = "material", EnglishName = "", Diffuse = Vector4.One,
            Specular = Vector3.Zero, SpecularPower = 1, Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None,
            EdgeColor = Vector4.Zero, EdgeSize = 0, TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None,
            ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = count };
        return new()
        {
            Header = template.Header, Name = "fixture", EnglishName = "", Comment = "", EnglishComment = "",
            // Horizontal suspended cloth: id4 is free to fall, rather than already vertically taut below pin6.
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, new Vector3(99), new Vector3(0, 3, 0), new Vector3(1, 3, 0), new Vector3(0, 3, 1) }
                .Select(p => new PmxVertex { Position = p, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [],
                    WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
            Faces = [new(0, 1, 2), new(4, 5, 6)], Textures = [], Materials = [Material(3), Material(3)],
            Bones = template.Bones, Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = [new()
            { Name = "cloth", EnglishName = "", Type = PmxSoftBodyType.TriMesh, MaterialIndex = 1, Group = 0,
                CollisionGroup = ushort.MaxValue, Flags = PmxSoftBodyFlags.None, BLinkLength = 2, ClusterCount = 0,
                TotalMass = 1, CollisionMargin = .02f, AeroModel = 0, Config = new(1, .01f, 0, 0, 0, 0, .2f, 0, 1, .1f, 1, .7f),
                ClusterConfig = new(1, .1f, .5f, .5f, .5f, .5f), Iterations = new(0, 4, 0, 4), MaterialConfig = new(1, 1, 1),
                Anchors = [], PinVertexIndices = [6] }]
        };
    }

    internal static SoftBodyDefinition Cloth() => new("cloth", SoftBodyShape.TriMesh,
        [0, 1, 2, 3], [new(0, 3, 0), new(1, 3, 0), new(0, 2, 0), new(1, 2, 0)],
        [0, 2, 1, 1, 2, 3], [0], [], new());
}
