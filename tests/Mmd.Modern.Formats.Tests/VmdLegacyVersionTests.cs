using System.Buffers.Binary;
using System.Text;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Formats.Tests;

public sealed class VmdLegacyVersionTests
{
    // The legacy signature and ten-byte model field are independent literal header bytes.
    // nanoem nanoem.c:6671-6690 routes both signatures through the same six block parsers.
    private static byte[] LegacyFixture() => Convert.FromHexString(
        "566F63616C6F6964204D6F74696F6E20446174612066696C650000000000" +
        "41000000000000000000" +
        "01000000" +
        "42000000000000000000000000000007000000000000800000803F000000C0" +
        "0000803F000000400000404000008040" +
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F" +
        "202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F" +
        "010000004D00000000000000000000000000000900000000000080" +
        "010000000B0000000000A0C10000803F0000004000004040000080BF000000C0000040C0" +
        "A0A1A2A3A4A5A6A7A8A9AAABACADAEAFB0B1B2B3B4B5B6B72D00000001" +
        "010000000D0000000000803F0000003F0000803E000080BF000000C0000040C0" +
        "010000000F0000000200000080" +
        "01000000110000000001000000490000000000000000000000000000000000000001");

    [Fact]
    public void ReadsTenByteLegacyHeaderAndAllSixSectionsThenExplicitlyWritesLegacy()
    {
        byte[] fixture = LegacyFixture();
        Assert.Equal(326, fixture.Length);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(fixture.AsSpan(40)));
        using var input = new MemoryStream(fixture);
        VmdMotion motion = VmdReader.Read(input);
        Assert.Equal("A", motion.ModelName);
        Assert.Equal("B", Assert.Single(motion.BoneKeyframes).BoneName);
        Assert.Equal("M", Assert.Single(motion.MorphKeyframes).MorphName);
        Assert.Equal(11u, Assert.Single(motion.CameraKeyframes).Frame);
        Assert.Equal(13u, Assert.Single(motion.LightKeyframes).Frame);
        Assert.Equal(15u, Assert.Single(motion.SelfShadowKeyframes).Frame);
        Assert.Equal("I", Assert.Single(Assert.Single(motion.IkKeyframes).Switches).BoneName);
        Assert.Equal(fixture, VmdWriter.ToBytes(motion, new() { Version = 1 }));
    }

    [Fact]
    public void LegacyReadDefaultsToVersionTwoExportWithoutChangingTrackBytes()
    {
        byte[] fixture = LegacyFixture();
        using var input = new MemoryStream(fixture);
        byte[] modern = VmdWriter.ToBytes(VmdReader.Read(input));
        Assert.Equal(336, modern.Length);
        Assert.Equal("Vocaloid Motion Data 0002", Encoding.ASCII.GetString(modern, 0, 25));
        Assert.Equal(fixture.AsSpan(40).ToArray(), modern.AsSpan(50).ToArray());
    }

    [Fact]
    public void EmptyLegacyAndFullWidthHeaderAreSupported()
    {
        byte[] bytes = VmdWriter.ToBytes(new() { ModelName = "1234567890" }, new() { Version = 1 });
        Assert.Equal(64, bytes.Length);
        Assert.Equal("1234567890", Encoding.ASCII.GetString(bytes, 30, 10));
        using var input = new MemoryStream(bytes);
        Assert.Equal("1234567890", VmdReader.Read(input).ModelName);
    }

    [Fact]
    public void LegacyModelHeaderRejectsByteOverflowAndAcceptsExplicitShortOverride()
    {
        var motion = new VmdMotion { ModelName = "あいうえおか" };
        using var destination = new MemoryStream();
        destination.WriteByte(0x55);
        ArgumentException error = Assert.Throws<ArgumentException>(() => VmdWriter.Write(destination, motion, new() { Version = 1 }));
        Assert.Equal("header.modelName", error.ParamName);
        Assert.Equal(new byte[] { 0x55 }, destination.ToArray());
        byte[] bytes = VmdWriter.ToBytes(motion, new() { Version = 1, ModelNameOverride = "あいうえお" });
        using var input = new MemoryStream(bytes);
        Assert.Equal("あいうえお", VmdReader.Read(input).ModelName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public void UnknownExportVersionIsRejectedBeforeDestinationWrite(int version)
    {
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => VmdWriter.Write(stream, new(), new() { Version = version }));
        Assert.Empty(stream.ToArray());
    }

    [Fact]
    public void VersionTwoPreservedHeaderTooWideForLegacyNeedsExplicitOverride()
    {
        byte[] modern = VmdWriter.ToBytes(new() { ModelName = "12345678901" });
        using var input = new MemoryStream(modern);
        VmdMotion motion = VmdReader.Read(input);
        Assert.Throws<ArgumentException>(() => VmdWriter.ToBytes(motion, new() { Version = 1 }));
        byte[] bytes = VmdWriter.ToBytes(motion, new() { Version = 1, ModelNameOverride = "short" });
        using var legacy = new MemoryStream(bytes);
        Assert.Equal("short", VmdReader.Read(legacy).ModelName);
    }
}
