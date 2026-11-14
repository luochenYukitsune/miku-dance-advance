using System.Text;
using Xunit;

namespace Mmd.Modern.Formats.Tests;

public sealed class VmdRawNameTests
{
    [Fact]
    public void MixedNamesRoundTripWithExplicitProfileAndIndependentLiteralPayloads()
    {
        byte[] bytes = Fixture();
        bytes[54] = 0xb4; bytes[55] = 0xf8; // CP936: 带.
        bytes[169] = 0xb6; // CP932: ｶ; not a complete CP936 character.
        var options = new VmdReadOptions
        {
            AnsiCodePage = 936,
            NameOverrides = [new(VmdNameKind.Morph, new("ｶ", new byte[] { 0xb6 }, 932))],
        };
        using var input = new MemoryStream(bytes);
        VmdMotion motion = VmdReader.Read(input, options);
        Assert.Equal("带", motion.BoneKeyframes[0].BoneName);
        Assert.Equal("ｶ", motion.MorphKeyframes[0].MorphName);
        Assert.Equal(936, motion.BoneKeyframes[0].OriginalName!.AnsiCodePage);
        Assert.Equal(bytes, VmdWriter.ToBytes(motion));
        using var reopened = new MemoryStream(bytes);
        VmdMotion copy = VmdReader.Read(reopened, VmdNameCompatibility.CreateReadOptions(motion));
        Assert.Equal("带", copy.BoneKeyframes[0].BoneName);
        Assert.Equal("ｶ", copy.MorphKeyframes[0].MorphName);
    }

    [Fact]
    public void MetadataOwnsItsPayloadAndUsesStrictRuntimeDecoding()
    {
        byte[] raw = [0x81, 0xe0]; // U+2252 in the actual .NET CP932 strict decoder.
        var name = new VmdEncodedName("≒", raw, 932);
        raw[0] = 0;
        Assert.Equal(new byte[] { 0x81, 0xe0 }, name.Bytes);
        Assert.Throws<NotSupportedException>(() => ((IList<byte>)name.Bytes)[0] = 0);
        var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = "≒", OriginalName = name }] };
        byte[] bytes = VmdWriter.ToBytes(motion);
        Assert.Equal(new byte[] { 0x81, 0xe0 }, bytes[54..56]);
        using var stream = new MemoryStream(bytes);
        Assert.Equal(new byte[] { 0x81, 0xe0 }, VmdReader.Read(stream).BoneKeyframes[0].OriginalName!.Bytes);
        // Microsoft also defines this duplicate spelling. Keep it byte-for-byte rather than canonicalizing.
        var duplicate = new VmdEncodedName("≒", new byte[] { 0x87, 0x90 }, 932);
        var duplicateMotion = new VmdMotion { BoneKeyframes = [new() { BoneName = "≒", OriginalName = duplicate }] };
        byte[] duplicateBytes = VmdWriter.ToBytes(duplicateMotion);
        Assert.Equal(new byte[] { 0x87, 0x90 }, duplicateBytes[54..56]);
        using var duplicateStream = new MemoryStream(duplicateBytes);
        Assert.Equal(duplicateBytes, VmdWriter.ToBytes(VmdReader.Read(duplicateStream)));
    }

    [Fact]
    public void StaleMetadataFailsBeforeDestinationChangesButExplicitTranscodingCanIgnoreIt()
    {
        var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = "new", OriginalName = new("old", "old"u8.ToArray(), 932) }] };
        using var destination = new MemoryStream(new byte[256], writable: true);
        destination.Position = 7;
        Assert.Throws<ArgumentException>(() => VmdWriter.Write(destination, motion));
        Assert.Equal(7, destination.Position);
        Assert.All(destination.ToArray(), value => Assert.Equal(0, value));
        byte[] result = VmdWriter.ToBytes(motion, new() { PreserveOriginalNames = false });
        Assert.Equal("new"u8.ToArray(), result[54..57]);
        Assert.Throws<ArgumentException>(() => VmdNameCompatibility.CreateReadOptions(motion));
    }

    [Fact]
    public void StrictTranscodingDoesNotReplaceUnencodableNames()
    {
        var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = "带", OriginalName = new("带", new byte[] { 0xb4, 0xf8 }, 936) }] };
        Assert.Equal(new byte[] { 0xb4, 0xf8 }, VmdWriter.ToBytes(motion)[54..56]);
        Assert.Throws<ArgumentException>(() => VmdWriter.ToBytes(motion, new() { PreserveOriginalNames = false }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void InvalidProfilesFailBeforeInputMoves(int kind)
    {
        var one = new VmdNameOverride(VmdNameKind.Bone, new("a", new byte[] { 97 }, 932));
        IReadOnlyList<VmdNameOverride> entries = kind switch
        {
            0 => [one, one],
            1 => [new((VmdNameKind)99, one.Name)],
            2 => [new(VmdNameKind.Bone, new(new string('a', 16), new byte[16].Select(_ => (byte)97).ToArray(), 932))],
            _ => [null!],
        };
        using var stream = new MemoryStream(Fixture());
        Assert.ThrowsAny<ArgumentException>(() => VmdReader.Read(stream, new() { NameOverrides = entries }));
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void MetadataRejectsInvalidEncodingPayloadAndOversize()
    {
        Assert.ThrowsAny<ArgumentException>(() => new VmdEncodedName("a", new byte[] { 0 }, 932));
        Assert.ThrowsAny<ArgumentException>(() => new VmdEncodedName("a", new byte[] { 0xb4 }, 936));
        Assert.ThrowsAny<ArgumentException>(() => new VmdEncodedName("wrong", "a"u8.ToArray(), 932));
        Assert.ThrowsAny<ArgumentException>(() => new VmdEncodedName("a", new byte[] { 97 }, 65001));
        Assert.ThrowsAny<ArgumentException>(() => new VmdEncodedName(new string('a', 21), new byte[21].Select(_ => (byte)97).ToArray(), 932));
        var tooWide = new VmdMotion { BoneKeyframes = [new() { BoneName = new string('a', 16), OriginalName = new(new string('a', 16), new byte[16].Select(_ => (byte)97).ToArray(), 932) }] };
        Assert.Throws<ArgumentException>(() => VmdWriter.ToBytes(tooWide));
    }

    [Fact]
    public void ProfilesAreCopiedAndHeaderOverrideSupersedesOriginalName()
    {
        var item = new VmdNameOverride(VmdNameKind.Bone, new("a", new byte[] { 97 }, 932));
        var items = new[] { item };
        var options = new VmdReadOptions { NameOverrides = items };
        items[0] = null!;
        Assert.Same(item, options.NameOverrides[0]);
        var motion = new VmdMotion { ModelName = "stale", OriginalModelName = new("old", "old"u8.ToArray(), 932) };
        var write = new VmdWriteOptions { ModelNameOverride = "short" };
        byte[] bytes = VmdWriter.ToBytes(motion, write);
        using var input = new MemoryStream(bytes);
        Assert.Equal("short", VmdReader.Read(input, VmdNameCompatibility.CreateReadOptions(motion, write)).ModelName);
    }

    [Fact]
    public void ModelAwareCandidatesResolveOnlyUniqueExactNames()
    {
        byte[] bytes = Fixture(); bytes[54] = 0xd6; bytes[55] = 0xd0;
        using var unique = new MemoryStream(bytes);
        Assert.Equal("中", VmdReader.Read(unique, new() { BoneNameCandidates = ["中"] }).BoneKeyframes[0].BoneName);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string alternative = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(new byte[] { 0xd6, 0xd0 });
        using var ambiguous = new MemoryStream(bytes);
        Assert.Throws<VmdFormatException>(() => VmdReader.Read(ambiguous, new() { BoneNameCandidates = ["中", alternative] }));
        using var explicitChoice = new MemoryStream(bytes);
        Assert.Equal("中", VmdReader.Read(explicitChoice, new()
        {
            BoneNameCandidates = ["中", alternative],
            NameOverrides = [new(VmdNameKind.Bone, new("中", new byte[] { 0xd6, 0xd0 }, 936))],
        }).BoneKeyframes[0].BoneName);
    }

    [Fact]
    public void DuplicateAndNullModelNamesFailBeforeInputMoves()
    {
        foreach (string[] names in new[] { new[] { "a", "a" }, new[] { "a", null! } })
        {
            using var stream = new MemoryStream(Fixture());
            Assert.ThrowsAny<ArgumentException>(() => VmdReader.Read(stream, new() { BoneNameCandidates = names }));
            Assert.Equal(0, stream.Position);
        }
    }

    [Fact]
    public void AllNameKindsSupportTheirFullWidthsAndIkRetainsItsTwoValueIdentity()
    {
        var shortName = new VmdEncodedName(new string('a', 15), new byte[15].Select(_ => (byte)97).ToArray(), 932);
        var longName = new VmdEncodedName(new string('b', 20), new byte[20].Select(_ => (byte)98).ToArray(), 932);
        var item = new VmdIkSwitch(longName.Value, true) { OriginalName = longName };
        var (boneName, enabled) = item;
        Assert.Equal(longName.Value, boneName);
        Assert.True(enabled);
        Assert.Equal(new VmdIkSwitch(longName.Value, true), item);
        Assert.Equal(new VmdIkSwitch(longName.Value, true).GetHashCode(), item.GetHashCode());
        var motion = new VmdMotion
        {
            ModelName = longName.Value, OriginalModelName = longName,
            BoneKeyframes = [new() { BoneName = shortName.Value, OriginalName = shortName }],
            MorphKeyframes = [new() { MorphName = shortName.Value, OriginalName = shortName }],
            IkKeyframes = [new() { Switches = [item] }],
        };
        using var input = new MemoryStream(VmdWriter.ToBytes(motion));
        VmdMotion read = VmdReader.Read(input, VmdNameCompatibility.CreateReadOptions(motion));
        Assert.Equal(longName.Bytes, read.OriginalModelName!.Bytes);
        Assert.Equal(shortName.Bytes, read.BoneKeyframes[0].OriginalName!.Bytes);
        Assert.Equal(shortName.Bytes, read.MorphKeyframes[0].OriginalName!.Bytes);
        Assert.Equal(longName.Bytes, read.IkKeyframes[0].Switches[0].OriginalName!.Bytes);
    }

    [Fact]
    public void ConflictingRawMeaningsCannotBeCapturedAsAProfile()
    {
        byte[] bytes = [0xd6, 0xd0];
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string japanese = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes);
        var motion = new VmdMotion
        {
            BoneKeyframes =
            [
                new() { BoneName = "中", OriginalName = new("中", bytes, 936) },
                new() { BoneName = japanese, OriginalName = new(japanese, bytes, 932) },
            ],
        };
        Assert.Throws<ArgumentException>(() => VmdNameCompatibility.CreateReadOptions(motion));
    }

    [Fact]
    public void UnmatchedCandidatesKeepSelectedPageAndReturnedMetadataIgnoresPadding()
    {
        byte[] bytes = Fixture(); bytes[54] = 97; bytes[56] = 99;
        using var input = new MemoryStream(bytes);
        VmdMotion read = VmdReader.Read(input, new() { BoneNameCandidates = ["unrelated"] });
        Assert.Equal("a", read.BoneKeyframes[0].BoneName);
        Assert.Equal(new byte[] { 97 }, read.BoneKeyframes[0].OriginalName!.Bytes);
        byte[] written = VmdWriter.ToBytes(read);
        Assert.All(written[55..69], value => Assert.Equal(0, value));
    }

    private static byte[] Fixture()
    {
        byte[] bytes = new byte[208];
        "Vocaloid Motion Data 0002"u8.CopyTo(bytes);
        bytes[50] = 1; // one 111-byte bone, identity quaternion.
        bytes[99] = 0x80; bytes[100] = 0x3f;
        bytes[165] = 1; // one 23-byte morph, five remaining counts all zero.
        return bytes;
    }
}
