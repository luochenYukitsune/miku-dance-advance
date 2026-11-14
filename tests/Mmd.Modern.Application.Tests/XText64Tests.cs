using System.Numerics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class XText64Tests
{
    [Theory]
    [InlineData("0302")]
    [InlineData("0303")]
    public void Float64TextPreservesFloat32FrameGeometryAndMaterials(string version)
    {
        string text32 = XAccessoryTests.Triangle.Replace("0303", version, StringComparison.Ordinal);
        var expected = XAccessoryTests.Read(text32);
        var actual = XAccessoryTests.Read(text32.Replace("txt 0032", "txt 0064", StringComparison.Ordinal));

        Assert.Equal(expected.Vertices.ToArray(), actual.Vertices.ToArray());
        Assert.Equal(expected.Indices.ToArray(), actual.Indices.ToArray());
        Assert.Equal(expected.Materials.ToArray(), actual.Materials.ToArray());
        Assert.Equal(expected.Diagnostics.ToArray(), actual.Diagnostics.ToArray());
        Assert.Equal(new Vector3(2, 3, 4), actual.Vertices[0].Position);
        Assert.Equal(Vector3.UnitZ, actual.Vertices[0].Normal);
        Assert.Equal(Vector2.UnitX, actual.Vertices[1].Uv);
        Assert.Equal(new Vector4(1, .5f, .25f, .8f), Assert.Single(actual.Materials).Material.Diffuse);
    }

    [Fact]
    public void Float64TextRoundsStoredNumbersIntoCoreFloat32()
    {
        string text = XAccessoryTests.Triangle.Replace("txt 0032", "txt 0064", StringComparison.Ordinal)
            .Replace("2,3,4,1", "2.0000000000000004,3,4,1", StringComparison.Ordinal)
            .Replace("8; 0.1", "8.000000000000002; 0.1", StringComparison.Ordinal);

        var document = XAccessoryTests.Read(text);

        Assert.Equal(new Vector3(2, 3, 4), document.Vertices[0].Position);
        Assert.Equal(8f, Assert.Single(document.Materials).Material.SpecularPower);
    }

    [Theory]
    [InlineData("1e40")]
    [InlineData("-1e40")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Float64TextRejectsNumbersOutsideFiniteCoreFloat32(string number)
    {
        string text = XAccessoryTests.Triangle.Replace("txt 0032", "txt 0064", StringComparison.Ordinal)
            .Replace("2,3,4,1", number + ",3,4,1", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => XAccessoryTests.Read(text));
    }
}
