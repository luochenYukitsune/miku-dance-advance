using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class RayGiEmissionTests
{
    private static readonly RayGiOptions Palette = new(LightingMode.SoftwareRayTracing, Strength: 1,
        SamplesPerPixel: 1, DirectionalIrradiance: Vector3.Zero, DirectScale: 0, Composition: RayGiComposition.ToonPalette);

    [Fact]
    public void DefaultZeroKeepsFrozenRoomGeometryAndTransportApartFromProvedAlphaFlag()
    {
        var character = Character();
        var room = RayGiDemoScene.AddRoom(character);
        var snapshot = room.Surface.Snapshot;
        var transport = RayGiScene.FromOriginal(room);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(MemoryMarshal.AsBytes(snapshot.Vertices));
        hash.AppendData(MemoryMarshal.AsBytes(snapshot.Indices));
        hash.AppendData(MemoryMarshal.AsBytes(transport.Triangles));
        var legacyMaterials = transport.Materials.ToArray();
        for (int i = 0; i < legacyMaterials.Length; i++)
        {
            var material = legacyMaterials[i];
            // This fixture uses only the opaque white fallback. Verify the new proof separately,
            // then retain the original pre-emission hash for every other transport byte.
            Assert.Equal(new RayGiUInt4(1, 0, 0, 0), material.Flags);
            legacyMaterials[i] = new(material.DiffuseFactor, material.Emission, material.DiffuseTexture,
                material.SphereTexture, material.Parameters, default);
        }
        hash.AppendData(MemoryMarshal.AsBytes(legacyMaterials.AsSpan()));
        hash.AppendData(MemoryMarshal.AsBytes(transport.Texels));
        // Captured from the pre-emission Core assembly retained by the Red build.
        Assert.Equal("ba63f370e4255138d06833e343d33c17e418c86f1ba8fa1c409215c705d1593e", Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        var explicitZero = Room(character, 0);
        Assert.Equal(snapshot.Vertices.ToArray(), explicitZero.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(snapshot.Indices.ToArray(), explicitZero.Surface.Snapshot.Indices.ToArray());
        Assert.Equal(snapshot.Materials, explicitZero.Surface.Snapshot.Materials);
        Assert.Equal(room.Surface.Materials, explicitZero.Surface.Materials);
        Assert.Equal(room.Materials, explicitZero.Materials);
        Assert.Equal(character.Surface.Materials.Count + 6, snapshot.Materials.Count);
        foreach (var material in snapshot.Materials) Assert.Equal(Vector3.Zero, Emission(material));
        foreach (var material in transport.Materials) Assert.Equal(Vector4.Zero, material.Emission);
    }

    [Theory]
    [InlineData(-.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void SnapshotRejectsNegativeOrNonFiniteEmissionInEveryChannel(float bad)
    {
        for (int channel = 0; channel < 3; channel++)
        {
            var emission = Vector3.Zero;
            emission[channel] = bad;
            Assert.Throws<ArgumentException>(() => Snapshot(WithEmission(Material(), emission)));
        }
    }

    [Theory]
    [InlineData(-.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void ExplicitTransportRejectsNegativeOrNonFiniteEmissionInEveryChannel(float bad)
    {
        var original = RayGiScene.FromOriginal(Character());
        var source = original.Materials[0];
        for (int channel = 0; channel < 3; channel++)
        {
            var emission = Vector4.Zero;
            emission[channel] = bad;
            var material = new RayGiMaterialGpu(source.DiffuseFactor, emission, source.DiffuseTexture,
                source.SphereTexture, source.Parameters, source.Flags);
            Assert.Throws<ArgumentException>(() => new RayGiScene(original.Triangles, [material], original.Texels));
        }
    }

    [Theory]
    [InlineData(-.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void RoomRejectsStrengthOutsideFiniteUnitInterval(float strength)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Room(Character(), strength));

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void SmallPanelsRetainAllPreviousInputsAndEmitOnlyIntoRoom(int worldSign)
    {
        var world = Matrix4x4.CreateScale(1.4f * worldSign, .8f, 2) * Matrix4x4.CreateRotationZ(.19f) * Matrix4x4.CreateTranslation(9, 3, -5);
        var character = Character(world);
        var oldRoom = RayGiDemoScene.AddRoom(character);
        var room = Room(character, .4f);
        var old = oldRoom.Surface.Snapshot;
        var snapshot = room.Surface.Snapshot;
        Assert.Equal(old.Vertices.ToArray(), snapshot.Vertices[..old.Vertices.Length].ToArray());
        Assert.Equal(old.Indices.ToArray(), snapshot.Indices[..old.Indices.Length].ToArray());
        Assert.Equal(old.Materials, snapshot.Materials.Take(old.Materials.Count));
        Assert.Equal(oldRoom.Surface.Materials, room.Surface.Materials.Take(old.Materials.Count));
        Assert.Equal(oldRoom.Materials, room.Materials.Take(old.Materials.Count));
        Assert.Equal(character.Outline!.Vertices, room.Outline!.Vertices.Take(character.Outline.Vertices.Count));
        Assert.Equal(character.Outline.Materials, room.Outline.Materials.Take(character.Outline.Materials.Count));
        Assert.Equal(old.Vertices.Length + 8, snapshot.Vertices.Length);
        Assert.Equal(old.Indices.Length + 12, snapshot.Indices.Length);
        Assert.Equal(old.Materials.Count + 2, snapshot.Materials.Count);
        Assert.Equal(character.Surface.Snapshot.World, snapshot.World);
        Assert.Same(character.Surface.Snapshot.Camera, snapshot.Camera);
        Assert.Same(character.Surface.Profile, room.Surface.Profile);
        var transport = RayGiScene.FromOriginal(room);
        Vector3[] expected = [new Vector3(1, .32f, .1f) * .4f, new Vector3(.1f, .55f, 1) * .4f];
        for (int panel = 0; panel < 2; panel++)
        {
            int m = old.Materials.Count + panel;
            var material = snapshot.Materials[m];
            Assert.Equal(new Vector4(0, 0, 0, 1), material.Diffuse);
            Assert.Equal(Vector3.Zero, material.Specular);
            Assert.Equal(-1, material.DiffuseTexture);
            Assert.Equal(-1, material.ToonTexture);
            Assert.Equal(-1, room.Surface.Materials[m].SphereTexture);
            Assert.Equal(expected[panel], Emission(material));
            Assert.Equal(expected[panel], material.Ambient);
            Assert.Equal(new Vector4(expected[panel], 0), transport.Materials[m].Emission);
            Assert.Equal(Vector3.One, room.Surface.Materials[m].ToonShadowColor);
            Assert.False(room.Surface.Materials[m].DoubleSided);
            Assert.True(room.Materials[m].CastSelfShadow);
            Assert.False(room.Materials[m].ReceiveSelfShadow);
            Assert.False(room.Outline.Materials[m].DrawEnabled);
            uint triangleIndex = (uint)(material.StartIndex / 3);
            var triangle = transport.Triangles[(int)triangleIndex];
            var a = Xyz(triangle.P0); var b = Xyz(triangle.P1); var c = Xyz(triangle.P2);
            var inward = panel == 0 ? Vector3.UnitX : -Vector3.UnitX;
            Assert.InRange(Vector3.Dot(Vector3.Normalize(Vector3.Cross(b - a, c - a)), inward), .99999f, 1.00001f);
            Assert.True(transport.AcceptCandidate(triangleIndex, new(.25f, .25f), -inward));
            Assert.False(transport.AcceptCandidate(triangleIndex, new(.25f, .25f), inward));
            var hitPoint = (a + b + c) / 3;
            Assert.True(transport.Bvh.TraceClosest(new(hitPoint + inward, -inward, .001f, 2), out var hit));
            Assert.Equal((uint)m, transport.Triangles[(int)hit.TriangleIndex].Meta.X);
        }
        D3D12RayGiRenderer.Validate(room, Palette);
    }

    [Fact]
    public void ImmutableEmissionCopiesToTransportAndCachedFramesShareStorage()
    {
        var original = Material();
        var emitted = WithEmission(original, new(.2f, .4f, .8f));
        SceneMaterial[] inputs = [emitted];
        var snapshot = Snapshot(inputs);
        var style = new OriginalStyleScene(snapshot, [new(Vector3.One)]);
        var first = RayGiScene.FromOriginal(style);
        inputs[0] = WithEmission(original, new(.9f));
        Assert.Equal(new Vector4(.2f, .4f, .8f, 0), first.Materials[0].Emission);
        Assert.Equal(new Vector3(.2f, .4f, .8f), Emission(snapshot.Materials[0]));
        Assert.Equal(Vector3.Zero, Emission(original));
        Assert.Same(first, RayGiScene.FromOriginal(style));
        var rotated = RayGiScene.FromOriginal(new OriginalFrameScene(style,
            options: new(OriginalAlphaBlendMode.Normal, true, false, SphereRotationOverride: Matrix4x4.CreateRotationY(.3f))));
        Assert.Equal(first.MaterialIdentity, rotated.MaterialIdentity);
        Assert.Equal(first.GeometryIdentity, rotated.GeometryIdentity);
        Assert.Equal(first.Materials.ToArray(), rotated.Materials.ToArray());
        var otherSnapshot = Snapshot(inputs);
        var other = RayGiScene.FromOriginal(new OriginalStyleScene(otherSnapshot, [new(Vector3.One)]));
        Assert.NotEqual(first.MaterialIdentity, other.MaterialIdentity);
        Assert.Equal(new Vector4(.9f, .9f, .9f, 0), other.Materials[0].Emission);
        Assert.Equal(new Vector4(.2f, .4f, .8f, 0), first.Materials[0].Emission);
        var sameSnapshotStyle = RayGiScene.FromOriginal(new OriginalStyleScene(snapshot, [new(Vector3.One)]));
        Assert.Equal(first.GeometryIdentity, sameSnapshotStyle.GeometryIdentity);
        Assert.Equal(first.Materials.ToArray(), sameSnapshotStyle.Materials.ToArray());
    }

    [Fact]
    public void ToonAccumulationIncludesEmissionEvenWithNoDirectLight()
    {
        var frame = Frame(new(float.MaxValue));
        Assert.Throws<NotSupportedException>(() => D3D12RayGiRenderer.Validate(frame, Palette));
        Assert.Throws<NotSupportedException>(() => D3D12RayGiRenderer.Validate(frame, Palette with { Composition = RayGiComposition.Additive }));
        D3D12RayGiRenderer.Validate(Frame(new(float.MaxValue / 8)), Palette);
        Assert.Throws<NotSupportedException>(() => D3D12RayGiRenderer.Validate(Frame(new(float.MaxValue / 8)), Palette with { SamplesPerPixel = 3 }));
        Assert.Throws<NotSupportedException>(() => D3D12RayGiRenderer.Validate(Frame(new(0, float.MaxValue, 0)), Palette));
        Assert.Throws<NotSupportedException>(() => D3D12RayGiRenderer.Validate(Frame(new(0, 0, float.MaxValue)), Palette));
    }

    [Fact]
    public void ToonAccumulationAddsEmissionAndScaledDirectBoundsInDoublePrecision()
    {
        var value = new Vector3(float.MaxValue / 8);
        var options = Palette with { DirectionalIrradiance = value, DirectScale = 1.1f };
        D3D12RayGiRenderer.Validate(Frame(Vector3.Zero), options);
        D3D12RayGiRenderer.Validate(Frame(value), Palette);
        Assert.Throws<NotSupportedException>(() => D3D12RayGiRenderer.Validate(Frame(value), options));
    }

    [Fact]
    public void AdditivePreservesZeroEmissionInputsAndBoundsNewNonzeroEmission()
    {
        var hugeDirect = Palette with { Composition = RayGiComposition.Additive, DirectionalIrradiance = new(float.MaxValue), DirectScale = 1, SamplesPerPixel = 1024 };
        D3D12RayGiRenderer.Validate(Frame(Vector3.Zero), hugeDirect);
        Assert.Throws<NotSupportedException>(() => D3D12RayGiRenderer.Validate(Frame(new(.1f)), hugeDirect));
        var additive = Palette with { Composition = RayGiComposition.Additive, SamplesPerPixel = 256 };
        D3D12RayGiRenderer.Validate(Frame(new(.3f)), additive);
        Assert.Throws<NotSupportedException>(() => D3D12RayGiRenderer.Validate(Frame(new(float.MaxValue / 8)), additive));
    }

    [Theory]
    [InlineData(RayGiComposition.Additive)]
    [InlineData(RayGiComposition.ToonPalette)]
    public void NonzeroEmissionAlsoBoundsFinalStrengthMultiplication(RayGiComposition composition)
    {
        var options = Palette with { Composition = composition, SamplesPerPixel = 1, Strength = 100 };
        Assert.Throws<NotSupportedException>(() => D3D12RayGiRenderer.Validate(Frame(new(float.MaxValue / 8)), options));
        D3D12RayGiRenderer.Validate(Frame(new(float.MaxValue / 1024)), options);
    }

    [Fact]
    public void DisabledOrEmptyMaterialsDoNotIncreaseEmissionBound()
    {
        var active = Material();
        var large = WithEmission(Material() with { StartIndex = 3, IndexCount = 0 }, new(float.MaxValue));
        var snapshot = Snapshot(active, large);
        var style = new OriginalStyleScene(snapshot, [new(Vector3.One), new(Vector3.One)]);
        D3D12RayGiRenderer.Validate(new(style), Palette);
        var disabled = new OriginalFrameScene(Frame(new(float.MaxValue)).Surface,
            materials: [new(false, false, false, SurfaceEnabled: false)]);
        D3D12RayGiRenderer.Validate(disabled, Palette);
    }

    private static SceneMaterial Material() => new("character", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1);
    private static SceneSnapshot Snapshot(params SceneMaterial[] materials) => new(
        [new(new(-2, 3, -1), Vector3.UnitZ, Vector2.Zero), new(new(2, 3, -1), Vector3.UnitZ, Vector2.UnitX), new(new(0, 13, 1), Vector3.UnitZ, Vector2.UnitY)],
        [0, 1, 2], materials);
    private static OriginalFrameScene Frame(Vector3 emission) => new(new(Snapshot(WithEmission(Material(), emission)), [new(Vector3.One)]));
    private static OriginalFrameScene Character(Matrix4x4? world = null)
    {
        var source = Snapshot(Material());
        var snapshot = world is null ? source : new SceneSnapshot(source.Vertices, source.Indices, source.Materials, world: world);
        var outline = new OriginalOutlineData(snapshot.Vertices.ToArray().Select(v => new OriginalOutlineVertex(v.Position, v.Normal, OriginalOutlineVertexMode.Expand, 1, 0)), [new(1, Vector4.One)]);
        return new(new(snapshot, [new(new(.2f))]), outline, new(Vector4.One, Matrix4x4.Identity, new(0, 9, -30), 23, 1));
    }
    private static Vector3 Xyz(Vector4 value) => new(value.X, value.Y, value.Z);
    private static Vector3 Emission(SceneMaterial material) => material.Emission;
    private static SceneMaterial WithEmission(SceneMaterial material, Vector3 emission) => material with { Emission = emission };
    private static OriginalFrameScene Room(OriginalFrameScene character, float strength) => RayGiDemoScene.AddRoom(character, strength);
}
