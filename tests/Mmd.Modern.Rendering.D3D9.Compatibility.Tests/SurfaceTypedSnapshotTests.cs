using System.Buffers.Binary;
using System.Security.Cryptography;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.Rendering.D3D9.Compatibility.Tests;

public sealed class SurfaceTypedSnapshotTests
{
    [Theory]
    [InlineData(0U, 1UL)]
    [InlineData(1U, 3UL)]
    [InlineData(2U, 17UL)]
    [InlineData(3U, 19UL)]
    public void TypedHeaderDeclaresUvAndAccessoryFeaturesIndependentlyFromScope(uint flags, ulong expected)
    {
        var packets = Packets(flags);
        Assert.Equal(expected, D3D9SurfaceSnapshot.CopyIsolatedSurface(packets.Frame, packets.Visual,
            D3D9SurfaceIsolation.NoConfiguredMmeOrEmm).RequiredFeatures);
        Assert.Equal(expected | 256UL, D3D9SurfaceSnapshot.CopyConfiguredObjectFx(packets.Frame, packets.Visual).RequiredFeatures);
        Assert.Equal(expected | 1280UL, D3D9SurfaceSnapshot.CopyConfiguredSceneFx(packets.Frame, packets.Visual).RequiredFeatures);
    }

    [Fact]
    public void TypedPacketsRequireTheirPairedVersionAndDigestIncludesSplitAndFlags()
    {
        var packets = Packets(2);
        packets.Frame[212] ^= 1;
        Assert.Throws<ArgumentException>(() => Copy(packets));
        SHA256.HashData(packets.Frame).CopyTo(packets.Visual.AsSpan(20));
        Assert.Equal(3U, BinaryPrimitives.ReadUInt32LittleEndian(Copy(packets).FrameBytes.AsSpan(8)));
        BinaryPrimitives.WriteUInt32LittleEndian(packets.Visual.AsSpan(8), 3);
        Assert.Throws<ArgumentException>(() => Copy(packets));
        var legacy = SurfaceSessionTests.Packets();
        BinaryPrimitives.WriteUInt32LittleEndian(legacy.Visual.AsSpan(8), 4);
        Assert.Throws<ArgumentException>(() => Copy(legacy));
        var unknown = Packets(4);
        Assert.Throws<ArgumentException>(() => Copy(unknown));
    }

    // Header admission remains separate from the native topology validator: these zero-row packets
    // exercise the capability declaration only, and are never submitted to a native session.
    private static (byte[] Frame, byte[] Visual) Packets(uint flags)
    {
        var packets = SurfaceSessionTests.Packets();
        var frame = new byte[packets.Frame.Length + 8]; packets.Frame.CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(208), flags);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(212), -1);
        var visual = new byte[164]; packets.Visual.CopyTo(visual, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(visual.AsSpan(8), 4);
        SHA256.HashData(frame).CopyTo(visual.AsSpan(20));
        return (frame, visual);
    }

    private static D3D9SurfaceSnapshot Copy((byte[] Frame, byte[] Visual) packets) =>
        D3D9SurfaceSnapshot.CopyIsolatedSurface(packets.Frame, packets.Visual, D3D9SurfaceIsolation.NoConfiguredMmeOrEmm);
}
