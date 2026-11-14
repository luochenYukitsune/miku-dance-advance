using System.Text;
using Xunit;

namespace Mmd.Modern.Formats.Tests;

public sealed class LegacyAnsiEncodingTests
{
    [Fact]
    public void EveryDocumentedDuplicateDecodesWithoutCanonicalizingOriginalNames()
    {
        using var stream = typeof(LegacyAnsiEncoding).Assembly.GetManifestResourceStream("Mmd.Modern.Formats.cp932-duplicates.bin")!;
        using var reader = new BinaryReader(stream);
        var encoding = LegacyAnsiEncoding.Create(932);
        int count = 0;
        while (stream.Position < stream.Length)
        {
            ushort code = reader.ReadUInt16(); char value = (char)reader.ReadUInt16();
            byte[] bytes = [(byte)(code >> 8), (byte)code];
            Assert.Equal(value.ToString(), encoding.GetString(bytes));
            var original = new VmdEncodedName(value.ToString(), bytes, 932);
            Assert.Equal(bytes, original.Bytes); count++;
        }
        Assert.Equal(794, count);
    }

    [Theory]
    [InlineData(0xed, 0x40, "纊")]
    [InlineData(0x87, 0x90, "≒")]
    public void DuplicateSpellingsAlsoWorkAcrossStreamingDecoderBoundaries(int lead, int tail, string expected)
    {
        var decoder = LegacyAnsiEncoding.Create(932).GetDecoder();
        var chars = new char[1];
        Assert.Equal(0, decoder.GetChars([(byte)lead], 0, 1, chars, 0, false));
        Assert.Equal(1, decoder.GetChars([(byte)tail], 0, 1, chars, 0, true));
        Assert.Equal(expected, new string(chars));
        decoder.Reset();
        Assert.Equal(1, decoder.GetChars([65], 0, 1, chars, 0, true));
        Assert.Equal('A', chars[0]);
    }

    [Fact]
    public void UnknownAndTruncatedSequencesStillThrowAndEncoderRemainsStrict()
    {
        var japanese = LegacyAnsiEncoding.Create(932);
        Assert.Throws<DecoderFallbackException>(() => japanese.GetString([0x81]));
        Assert.Throws<DecoderFallbackException>(() => japanese.GetString([0x81, 0x30]));
        Assert.Throws<EncoderFallbackException>(() => japanese.GetBytes("😀"));
        Assert.Equal("Aセンター纊≒Z", japanese.GetString([65, 0x83, 0x5a, 0x83, 0x93, 0x83, 0x5e, 0x81, 0x5b, 0xed, 0x40, 0x87, 0x90, 90]));
        Assert.Equal("带", LegacyAnsiEncoding.Create(936).GetString([0xb4, 0xf8]));
        Assert.Throws<DecoderFallbackException>(() => LegacyAnsiEncoding.Create(936).GetString([0xb4]));
        Assert.Throws<ArgumentOutOfRangeException>(() => LegacyAnsiEncoding.Create(65001));
    }
}
