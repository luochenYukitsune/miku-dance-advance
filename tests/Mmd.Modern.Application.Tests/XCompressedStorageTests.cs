using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class XCompressedStorageTests
{
    // Generated with Python 3.12 zlib.compressobj(level=9, wbits=-15, zdict=firstChunk).
    // First chunk: '#' + 32766 spaces + LF. Second: 32 spaces + the triangle mesh below.
    // Independent zlib.decompress(secondRaw, -15) rejects: invalid distance too far back.
    private const string DictionaryFixture = "eG9mIDAzMDN0emlwMDAzMlmAAAAAgDYAQ0vtwTENAAAMA6B/KprMv8cKKfABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgHlXSQAiAENLI6TAN7U4Q6Ha2NoADHUMIZSBNYgBJIASOoY6RtbWtQA=";

    [Theory]
    [InlineData(false, 32)]
    [InlineData(false, 64)]
    [InlineData(true, 32)]
    [InlineData(true, 64)]
    public void MszipTextAndBinaryReuseExistingDocumentSemantics(bool binary, int precision)
    {
        byte[] source = binary ? XBinaryStorageTests.Triangle(precision)
            : Encoding.UTF8.GetBytes(XAccessoryTests.Triangle.Replace("0032", precision == 32 ? "0032" : "0064"));
        var expected = XTextReader.Read(new MemoryStream(source), new() { AnsiCodePage = 65001 });
        var actual = XTextReader.Read(new MemoryStream(Compress(source)), new() { AnsiCodePage = 65001 });
        Assert.Equal(expected.Vertices.ToArray(), actual.Vertices.ToArray());
        Assert.Equal(expected.Indices.ToArray(), actual.Indices.ToArray());
        Assert.Equal(expected.Materials.ToArray(), actual.Materials.ToArray());
        Assert.Equal(expected.VertexColors.ToArray(), actual.VertexColors.ToArray());
    }

    [Fact]
    public void RollingDictionarySurvivesMszipChunkBoundary()
    {
        var actual = XTextReader.Read(new MemoryStream(Convert.FromBase64String(DictionaryFixture)));
        Assert.Equal(new Vector3(1, 0, 0), actual.Vertices[1].Position);
        Assert.Equal(3, actual.Indices.Count);
    }

    [Fact]
    public void EveryTruncatedMszipPrefixRejects()
    {
        byte[] bytes = Convert.FromBase64String(DictionaryFixture);
        for (int count = 16; count < bytes.Length; count++)
            Assert.Throws<InvalidDataException>(() => XTextReader.Read(new MemoryStream(bytes, 0, count)));
    }

    [Theory]
    [InlineData(16)] // Total decoded length.
    [InlineData(20)] // Per-chunk decoded length.
    [InlineData(22)] // Per-chunk compressed length.
    [InlineData(24)] // CK signature.
    public void CorruptChunkFramingRejects(int offset)
    {
        byte[] bytes = Convert.FromBase64String(DictionaryFixture); bytes[offset] ^= 0xff;
        Assert.Throws<InvalidDataException>(() => XTextReader.Read(new MemoryStream(bytes)));
    }

    [Fact]
    public void OversizedDeclaredDecompressionRejectsBeforeAllocation()
    {
        byte[] bytes = Convert.FromBase64String(DictionaryFixture);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => XTextReader.Read(new MemoryStream(bytes)));
    }

    [Fact]
    public void ExactOutputWithoutFinalEndMarkerStillRejects()
    {
        // A valid nonfinal stored block contains a complete mesh, but has no final block.
        byte[] body = Encoding.ASCII.GetBytes("Mesh {3;0;0;0;,1;0;0;,0;1;0;;1;3;0,1,2;;}");
        using var deflated = new MemoryStream(); using var writer = new BinaryWriter(deflated);
        writer.Write((byte)0); writer.Write((ushort)body.Length); writer.Write((ushort)~body.Length); writer.Write(body);
        Assert.Throws<InvalidDataException>(() => XTextReader.Read(new MemoryStream(Container(body.Length, deflated.ToArray()))));
    }

    [Fact]
    public void ExtraBytesAfterFinalDeflateBlockReject()
    {
        byte[] source = Encoding.UTF8.GetBytes(XAccessoryTests.Triangle);
        byte[] bytes = Compress(source); Array.Resize(ref bytes, bytes.Length + 1); bytes[^1] = 42;
        ushort compressed = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22, 2));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), (ushort)(compressed + 1));
        Assert.Throws<InvalidDataException>(() => XTextReader.Read(new MemoryStream(bytes)));
    }

    [Fact]
    public void ChunkCannotInflatePastItsDeclaredOutput()
    {
        byte[] source = Encoding.UTF8.GetBytes(XAccessoryTests.Triangle);
        byte[] bytes = Compress(source);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 17);
        Assert.Throws<InvalidDataException>(() => XTextReader.Read(new MemoryStream(bytes)));
    }

    [Fact]
    public void FirstChunkCannotReferToMissingHistoryEvenIfZeroFilledWindowWouldParseAsValidMesh()
    {
        // Python zlib encoded a valid binary triangle with zdict=32768 zero bytes.
        // First MSZIP chunk has no dictionary: native zlib rejects this raw stream.
        byte[] bytes = Convert.FromBase64String("eG9mIDAzMDNiemlwMDAzMmgAAABYACsAQ0tjZGABkr6pxRlcDMxAyMDAzsCJoarBHhufjYEVbAIDWB8DlM0ExNwMAA==");
        Assert.Throws<InvalidDataException>(() => XTextReader.Read(new MemoryStream(bytes)));
    }

    [Fact]
    public void DecodedByteAndChunkCountLimitsApplyIndependentlyOfCompressedInputSize()
    {
        byte[] bytes = Convert.FromBase64String(DictionaryFixture);
        Assert.Throws<InvalidDataException>(() => XTextReader.Read(new MemoryStream(bytes), new() { MaxDecompressedBytes = 128 }));
        Assert.Throws<InvalidDataException>(() => XTextReader.Read(new MemoryStream(bytes), new() { MaxCompressedChunks = 1 }));
    }

    private static byte[] Compress(byte[] source)
    {
        using var raw = new MemoryStream();
        using (var deflater = new DeflateStream(raw, CompressionLevel.Optimal, leaveOpen: true)) deflater.Write(source.AsSpan(16));
        byte[] result = Container(source.Length - 16, raw.ToArray());
        Encoding.ASCII.GetBytes(source.AsSpan(8, 4).SequenceEqual("bin "u8) ? "bzip" : "tzip").CopyTo(result, 8);
        source.AsSpan(12, 4).CopyTo(result.AsSpan(12, 4));
        return result;
    }

    private static byte[] Container(int decodedLength, byte[] deflated)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("xof 0303tzip0032")); writer.Write(decodedLength + 16);
        writer.Write((ushort)decodedLength); writer.Write((ushort)(deflated.Length + 2)); writer.Write((byte)'C'); writer.Write((byte)'K'); writer.Write(deflated);
        return stream.ToArray();
    }
}
