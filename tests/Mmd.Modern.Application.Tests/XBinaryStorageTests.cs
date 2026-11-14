using System.Numerics;
using System.Text;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class XBinaryStorageTests
{
    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    public void BinaryNumericListsFeedExistingFrameMeshAndMaterialSemantics(int precision)
    {
        var document = XTextReader.Read(new MemoryStream(Triangle(precision)), new() { AnsiCodePage = 65001 });
        Assert.Equal(new Vector3(2, 3, 4), document.Vertices[0].Position);
        Assert.Equal(new Vector3(3, 3, 4), document.Vertices[1].Position);
        Assert.Equal(Vector3.UnitZ, document.Vertices[0].Normal);
        Assert.Equal(Vector2.UnitX, document.Vertices[1].Uv);
        var material = Assert.Single(document.Materials).Material;
        Assert.Equal(new Vector4(1, .5f, .25f, .8f), material.Diffuse);
        Assert.Equal("texture.png", material.Texture);
        Assert.Equal(8, material.SpecularPower);
    }

    [Fact]
    public void EveryTruncatedBinaryPrefixRejectsWithoutPublishingPartialDocument()
    {
        byte[] bytes = Triangle(32);
        for (int length = 16; length < bytes.Length; length++)
            Assert.Throws<InvalidDataException>(() => XTextReader.Read(new MemoryStream(bytes, 0, length)));
    }

    [Fact]
    public void OversizedBinaryListAndNameLengthRejectBeforeAllocation()
    {
        foreach (ushort token in new ushort[] { 1, 2, 6, 7 })
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Encoding.ASCII.GetBytes("xof 0303bin 0032")); writer.Write(token); writer.Write(uint.MaxValue);
            Assert.Throws<InvalidDataException>(() => XTextReader.Read(new MemoryStream(stream.ToArray())));
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1e40)]
    public void Float64BinaryRejectsNonfiniteCoreNumbers(double value)
        => Assert.Throws<InvalidDataException>(() => XTextReader.Read(new MemoryStream(Triangle(64, value))));

    [Fact]
    public void UnknownBinaryTokenRejectsEvenInsideIgnoredBlock()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("xof 0303bin 0032"));
        Name(writer, "Unknown"); writer.Write((ushort)10); writer.Write(ushort.MaxValue); writer.Write((ushort)11);
        writer.Write(Triangle(32).AsSpan(16));
        Assert.Throws<InvalidDataException>(() => XTextReader.Read(new MemoryStream(stream.ToArray())));
    }

    [Fact]
    public void BinaryTokenBudgetAndInputBudgetReject()
    {
        Assert.Throws<InvalidDataException>(() => XTextReader.Read(new MemoryStream(Triangle(32)), new() { MaxTokens = 5 }));
        Assert.Throws<InvalidDataException>(() => XTextReader.Read(new MemoryStream(Triangle(32)), new() { MaxInputBytes = 64 }));
    }

    [Fact]
    public void BinaryFloatListCannotSupplyAnIntegerVertexCount()
    {
        byte[] source = Triangle(32);
        int countToken = source.AsSpan().IndexOf("triangle"u8) + "triangle".Length + 2;
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(source.AsSpan(0, countToken));
        writer.Write((ushort)7); writer.Write(1); writer.Write(3f);
        writer.Write(source.AsSpan(countToken + 6));
        Assert.Throws<InvalidDataException>(() => XTextReader.Read(new MemoryStream(stream.ToArray())));
    }

    [Fact]
    public void BinaryIntegerListCannotSupplyFloatPositionData()
    {
        byte[] source = Triangle(32);
        int countToken = source.AsSpan().IndexOf("triangle"u8) + "triangle".Length + 2;
        source[countToken + 6] = 6; // INTEGER_LIST instead of FLOAT_LIST; numeric payload remains present.
        Assert.Throws<InvalidDataException>(() => XTextReader.Read(new MemoryStream(source)));
    }

    internal static byte[] Triangle(int precision, double firstMatrixValue = 1)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes($"xof 0303bin 00{precision}"));
        void Open(string type, string? name = null) { Name(writer, type); if (name is not null) Name(writer, name); writer.Write((ushort)10); }
        void Close() => writer.Write((ushort)11);
        void Ints(params int[] values) { writer.Write((ushort)6); writer.Write(values.Length); foreach (int value in values) writer.Write(value); }
        void Floats(params double[] values) { writer.Write((ushort)7); writer.Write(values.Length); foreach (double value in values) { if (precision == 32) writer.Write((float)value); else writer.Write(value); } }
        Open("Frame", "root");
        Open("FrameTransformMatrix"); Floats(firstMatrixValue,0,0,0,0,1,0,0,0,0,1,0,2,3,4,1); Close();
        Open("Mesh", "triangle");
        writer.Write((ushort)3); writer.Write(3);
        Floats(0,0,0,1,0,0,0,1,0); Ints(1,3,0,1,2);
        Open("MeshNormals"); Ints(1); Floats(0,0,1); Ints(1,3,0,0,0); Close();
        Open("MeshTextureCoords"); Ints(3); Floats(0,0,1,0,0,1); Close();
        Open("MeshMaterialList"); Ints(1,1,0);
        Open("Material", "mat"); Floats(1,.5,.25,.8,8,.1,.2,.3,0,0,0);
        Open("TextureFilename"); writer.Write((ushort)2); byte[] path = Encoding.ASCII.GetBytes("texture.png"); writer.Write(path.Length); writer.Write(path); writer.Write((ushort)20); Close();
        Close(); Close(); Close(); Close();
        return stream.ToArray();
    }

    private static void Name(BinaryWriter writer, string name)
    {
        writer.Write((ushort)1); byte[] bytes = Encoding.ASCII.GetBytes(name); writer.Write(bytes.Length); writer.Write(bytes);
    }
}
