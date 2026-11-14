using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class MatcapTangentGeometryTests
{
    [Fact]
    public void MirroredFacesKeepDifferentSignsAtTheSameSourceVertex()
    {
        var scene = Mesh();
        var data = D3D12EndfieldMatcapGeometry.Build(scene, 0, 0);
        Assert.Equal(64, Marshal.SizeOf<MatcapCorner>());
        Assert.Equal(6, data.Corners.Length);
        // Both triangles use source vertex zero; its corner sign must not be overwritten.
        Assert.Equal(data.Corners[0].WorldPosition, data.Corners[3].WorldPosition);
        Assert.Equal(-data.Corners[0].Tangent.W, data.Corners[3].Tangent.W);
        Assert.Equal(new uint[] { 0, 1, 2, 0, 2, 3 }, scene.Indices.ToArray());
    }

    [Fact]
    public void WorldNormalKeepsMagnitudeAndTangentUsesTheNormalMatrixExactlyOnce()
    {
        var local = Mesh();
        var world = Matrix4x4.CreateScale(-2, 3, .5f) * Matrix4x4.CreateTranslation(7, 8, 9);
        var transformed = Mesh(world: world);
        var before = D3D12EndfieldMatcapGeometry.Build(local, 0, 0);
        var after = D3D12EndfieldMatcapGeometry.Build(transformed, 0, 0);
        Assert.True(Matrix4x4.Invert(world, out var inverse));
        var normalWorld = Matrix4x4.Transpose(inverse);
        for (int i = 0; i < 6; ++i)
        {
            var sourceNormal = new Vector3(before.Corners[i].WorldNormal.X, before.Corners[i].WorldNormal.Y, before.Corners[i].WorldNormal.Z);
            var sourceTangent = new Vector3(before.Corners[i].Tangent.X, before.Corners[i].Tangent.Y, before.Corners[i].Tangent.Z);
            Near(Vector3.TransformNormal(sourceNormal, normalWorld), Xyz(after.Corners[i].WorldNormal));
            Near(Vector3.Normalize(Vector3.TransformNormal(sourceTangent, normalWorld)), Xyz(after.Corners[i].Tangent));
            Assert.Equal(before.Corners[i].Tangent.W, after.Corners[i].Tangent.W);
            Assert.Equal(-1, after.Corners[i].UvParity.Z);
            Near(Vector3.Transform(Xyz(before.Corners[i].WorldPosition), world), Xyz(after.Corners[i].WorldPosition));
        }
        Assert.Equal(2, after.Corners[0].WorldNormal.Z);
    }

    [Fact]
    public void CurrentDeformationAndUvMorphRegenerateRatherThanReuseRestFrames()
    {
        var source = Mesh();
        var original = D3D12EndfieldMatcapGeometry.Build(source, 0, 0);
        var movedVertices = source.Vertices.ToArray();
        movedVertices[1] = movedVertices[1] with { Position = new(1, .6f, .2f) };
        var deformed = source.WithDeformedVertices(movedVertices);
        var moved = D3D12EndfieldMatcapGeometry.Build(deformed, 0, 0);
        Assert.NotEqual(original.Fingerprint, moved.Fingerprint);
        Assert.NotEqual(original.Corners[0].Tangent, moved.Corners[0].Tangent);
        movedVertices[1] = movedVertices[1] with { Uv = new(0, 1) };
        movedVertices[2] = movedVertices[2] with { Uv = new(1, 0) };
        var uvMorphed = D3D12EndfieldMatcapGeometry.Build(source.WithDeformedVertices(movedVertices), 0, 0);
        Assert.NotEqual(moved.Fingerprint, uvMorphed.Fingerprint);
        Assert.NotEqual(moved.Corners[0].Tangent, uvMorphed.Corners[0].Tangent);
        Assert.Same(original, D3D12EndfieldMatcapGeometry.Build(source, 0, 0));
        Assert.Same(original, D3D12EndfieldMatcapGeometry.Build(source.WithCamera(source.Camera with { Eye = new(1, 9, -30) }), 0, 0));
    }

    [Fact]
    public void SelectedUvChannelDrivesBothCoordinatesAndCornerFrames()
    {
        var source = Mesh();
        var additional = source.Vertices.ToArray().Select(v => new SceneAdditionalUvVertex(new(v.Uv.Y, v.Uv.X, 17, 19), default, default, default)).ToArray();
        var selected = source.WithAdditionalUvs(new SceneAdditionalUvs(1, additional));
        var uv0 = D3D12EndfieldMatcapGeometry.Build(selected, 0, 0);
        var uv1 = D3D12EndfieldMatcapGeometry.Build(selected, 0, 1);
        Assert.Equal(new Vector2(0, 1), new(uv1.Corners[1].UvParity.X, uv1.Corners[1].UvParity.Y));
        Assert.Equal(-uv0.Corners[0].Tangent.W, uv1.Corners[0].Tangent.W);
        Assert.NotEqual(uv0.Fingerprint, uv1.Fingerprint);
        Assert.Throws<ArgumentException>(() => D3D12EndfieldMatcapGeometry.Build(source, 0, 1));
        Assert.Throws<ArgumentException>(() => D3D12EndfieldMatcapGeometry.Build(selected, 0, 2));
    }

    [Fact]
    public void TranslationChangesBufferIdentityButKeepsTheAppearanceIdentity()
    {
        var local = D3D12EndfieldMatcapGeometry.Build(Mesh(), 0, 0);
        var translated = D3D12EndfieldMatcapGeometry.Build(Mesh(world: Matrix4x4.CreateTranslation(7, 8, 9)), 0, 0);
        Assert.NotEqual(local.Fingerprint, translated.Fingerprint);
        Assert.Equal(local.AppearanceFingerprint, translated.AppearanceFingerprint);
        var rotated = D3D12EndfieldMatcapGeometry.Build(Mesh(world: Matrix4x4.CreateRotationY(.4f)), 0, 0);
        Assert.NotEqual(local.AppearanceFingerprint, rotated.AppearanceFingerprint);
    }

    [Fact]
    public void ContinuousUvTangentsRemainSharedAcrossAsymmetricMaterialBoundary()
    {
        var mesh = new SceneSnapshot([
            new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero),
            new(new(2, 0, 0), Vector3.UnitZ, Vector2.UnitX),
            new(Vector3.UnitY, Vector3.UnitZ, Vector2.UnitY),
            new(new(2, 2, 0), Vector3.UnitZ, new(.7f, 1.2f))],
            [0, 1, 2, 1, 3, 2],
            [new("whole", 0, 6, Vector4.One, Vector3.Zero, Vector3.Zero, 1)]);
        var split = new SceneSnapshot(mesh.Vertices, mesh.Indices, [
            new("first", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1),
            new("second", 3, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1)]);
        var whole = D3D12EndfieldMatcapGeometry.Build(mesh, 0, 0);
        var first = D3D12EndfieldMatcapGeometry.Build(split, 0, 0);
        var second = D3D12EndfieldMatcapGeometry.Build(split, 1, 0);
        // The first triangle alone has +X tangents. Its shared corners must also
        // consume the second triangle's unequal UV derivative and corner angles.
        Assert.True(MathF.Abs(whole.Corners[1].Tangent.Y) > .02f);
        for (int i = 0; i < 3; ++i)
        {
            Assert.Equal(whole.Corners[i].Tangent, first.Corners[i].Tangent);
            Assert.Equal(whole.Corners[i + 3].Tangent, second.Corners[i].Tangent);
        }
        Assert.Equal(first.Corners[1].Tangent, second.Corners[0].Tangent);
        var changed = split.Vertices.ToArray();
        changed[3] = changed[3] with { Uv = new(.4f, 1.8f) };
        var changedNeighbor = D3D12EndfieldMatcapGeometry.Build(split.WithDeformedVertices(changed), 0, 0);
        Assert.NotEqual(first.Corners[1].Tangent, changedNeighbor.Corners[1].Tangent);
    }

    [Fact]
    public void WholeSourceCornerBudgetCannotBeBypassedWithSmallMaterialRanges()
    {
        var mesh = Mesh();
        var indices = new uint[D3D12Mikktspace.MaximumCorners + 2];
        var source = new SceneSnapshot(mesh.Vertices, indices, [
            new("small selected draw", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1),
            new("remaining source", 3, indices.Length - 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1)]);
        var error = Assert.Throws<ArgumentException>(() => D3D12EndfieldMatcapGeometry.Build(source, 0, 0));
        Assert.Contains("source mesh exceeds", error.Message);
    }

    [Fact]
    public void MaterialRangeIsLocalAndInvalidNormalFailsBeforeNativeGeneration()
    {
        var mesh = Mesh();
        var split = new SceneSnapshot(mesh.Vertices, mesh.Indices, [
            new("first", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1),
            new("second", 3, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1)]);
        Assert.Equal(3, D3D12EndfieldMatcapGeometry.Build(split, 1, 0).Corners.Length);
        var descriptor = new D3D12EndfieldMatcapGeometry(split, 1);
        Assert.Same(split, descriptor.Source);
        Assert.Equal(1, descriptor.LocalMaterialIndex);
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D12EndfieldMatcapGeometry(split, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => D3D12EndfieldMatcapGeometry.Build(split, 0, 5));
        var invalid = mesh.Vertices.ToArray(); invalid[0] = invalid[0] with { Normal = Vector3.Zero };
        Assert.Throws<ArgumentException>(() => D3D12EndfieldMatcapGeometry.Build(mesh.WithDeformedVertices(invalid), 0, 0));
        var huge = Mesh(world: Matrix4x4.CreateScale(2));
        var hugeVertices = huge.Vertices.ToArray(); hugeVertices[0] = hugeVertices[0] with { Position = new(float.MaxValue, 0, 0) };
        huge = huge.WithDeformedVertices(hugeVertices);
        Assert.Throws<ArgumentException>(() => D3D12EndfieldMatcapGeometry.Build(huge, 0, 0));
    }

    private static SceneSnapshot Mesh(Matrix4x4? world = null)
        => new([
            new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero),
            new(Vector3.UnitX, Vector3.UnitZ, Vector2.UnitX),
            new(Vector3.UnitY, Vector3.UnitZ, Vector2.UnitY),
            new(-Vector3.UnitX, Vector3.UnitZ, Vector2.UnitX)],
            [0, 1, 2, 0, 2, 3],
            [new("mirrored seam", 0, 6, Vector4.One, Vector3.Zero, Vector3.Zero, 1)], world: world);

    private static Vector3 Xyz(Vector4 value) => new(value.X, value.Y, value.Z);
    private static void Near(Vector3 expected, Vector3 actual)
        => Assert.True(Vector3.Distance(expected, actual) < 1e-5f, $"Expected {expected}, got {actual}.");
}
