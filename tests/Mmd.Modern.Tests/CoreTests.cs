using System.Numerics;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class CoreTests
{
    private static PmdModel Load()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd"));
        return PmdReader.Read(stream);
    }

    [Fact]
    public void OriginalFixtureHasNontrivialGeometryAndExactMaterialSpans()
    {
        var model = Load();
        Assert.Equal("Shell Humanoid", model.Name);
        Assert.Equal(2618, model.Vertices.Length);
        Assert.Equal(13440, model.Indices.Length);
        Assert.Equal(14, model.Materials.Length);
        Assert.Equal(6, model.Bones.Length);
        Assert.Equal(model.Indices.Length, model.Materials.Sum(m => m.IndexCount));
        Assert.Equal(0, model.Materials[0].StartIndex);
        Assert.Equal(model.Indices.Length, model.Materials[^1].StartIndex + model.Materials[^1].IndexCount);
        Assert.Equal(-6.2f, model.Vertices.Min(v => v.Position.X), 3);
        Assert.Equal(16.9f, model.Vertices.Max(v => v.Position.Y), 3);
    }

    [Fact]
    public void RestPosePreservesPositionsAndNormals()
    {
        var model = Load();
        var transformed = Skinning.Apply(model, Skinning.CreateSkinMatrices(model));
        for (int i = 0; i < model.Vertices.Length; i++)
        {
            Assert.True(Vector3.Distance(model.Vertices[i].Position, transformed[i].Position) < 0.00001f);
            Assert.True(Vector3.Distance(model.Vertices[i].Normal, transformed[i].Normal) < 0.00001f);
        }
    }

    [Fact]
    public void RowVectorOrderRotatesAboutBindPositionAndInheritsParent()
    {
        var model = new PmdModel("two bones", [], [], [], [new("root", -1, new(0, 1, 0)), new("child", 0, new(0, 2, 0))], 0);
        var matrices = Skinning.CreateSkinMatrices(model, [Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), Quaternion.Identity]);
        var moved = Vector3.Transform(new Vector3(0, 3, 0), matrices[1]);
        Assert.True(Vector3.Distance(new Vector3(-2, 1, 0), moved) < 0.00001f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(100)]
    [InlineData(500)]
    public void TruncatedInputsAreRejected(int length)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd"));
        Assert.Throws<InvalidDataException>(() => PmdReader.Read(new MemoryStream(bytes, 0, length)));
    }

    [Fact]
    public void HugeUntrustedCountIsRejectedBeforeAllocation()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd"));
        BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, 283);
        Assert.Throws<InvalidDataException>(() => PmdReader.Read(new MemoryStream(bytes)));
    }

    [Fact]
    public void InvalidTriangleIndexIsRejected()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd"));
        BitConverter.GetBytes(ushort.MaxValue).CopyTo(bytes, 287 + 2618 * 38 + 4);
        Assert.Throws<InvalidDataException>(() => PmdReader.Read(new MemoryStream(bytes)));
    }

    [Fact]
    public void WeightedSkinningBlendsTwoBonesAndNormalizesNormals()
    {
        var model = new PmdModel("blend", [new(Vector3.UnitX, Vector3.UnitY, Vector2.Zero, 0, 1, .25f)], [], [], [new("a", -1, Vector3.Zero), new("b", -1, Vector3.Zero)], 0);
        var transformed = Skinning.Apply(model, [Matrix4x4.CreateTranslation(4, 0, 0), Matrix4x4.CreateRotationZ(MathF.PI / 2)]);
        Assert.True(Vector3.Distance(transformed[0].Position, new Vector3(1.25f, .75f, 0)) < .00001f);
        Assert.True(Vector3.Distance(transformed[0].Normal, Vector3.Normalize(new Vector3(-.75f, .25f, 0))) < .00001f);
    }

    [Fact]
    public void CyclicBoneHierarchyIsRejected()
    {
        var model = new PmdModel("cycle", [], [], [], [new("a", 1, Vector3.Zero), new("b", 0, Vector3.Zero)], 0);
        Assert.Throws<InvalidDataException>(() => Skinning.CreateSkinMatrices(model));
    }

    [Fact]
    public void NonFiniteCoordinatesAreRejected()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd"));
        BitConverter.GetBytes(float.NaN).CopyTo(bytes, 287);
        Assert.Throws<InvalidDataException>(() => PmdReader.Read(new MemoryStream(bytes)));
    }

    [Fact]
    public void MaterialCoverageMismatchIsRejected()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd"));
        int firstMaterial = 287 + 2618 * 38 + 4 + 13440 * 2 + 4;
        BitConverter.GetBytes((uint)0).CopyTo(bytes, firstMaterial + 46);
        Assert.Throws<InvalidDataException>(() => PmdReader.Read(new MemoryStream(bytes)));
    }
}
