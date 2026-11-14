using System.Buffers.Binary;
using System.Security.Cryptography;
using Xunit;

namespace Mmd.Modern.Rendering.D3D9.Compatibility.Tests;

public sealed class SurfaceModelMaterialSnapshotTests
{
    [Theory]
    [InlineData(4U, 7425UL)]
    [InlineData(5U, 7427UL)]
    [InlineData(6U, 7441UL)]
    [InlineData(7U, 7443UL)]
    public void MetadataVersionRequiresExplicitEmmScopeAndIndependentCapability(uint flags, ulong expectedFeatures)
    {
        var packets = Packets(flags);
        var snapshot = D3D9SurfaceSnapshot.CopyConfiguredEmm(packets.Frame, packets.Visual);
        Assert.Equal(expectedFeatures, snapshot.RequiredFeatures);
        Assert.Throws<ArgumentException>(() => D3D9SurfaceSnapshot.CopyConfiguredSceneFx(packets.Frame, packets.Visual));
        Assert.Throws<ArgumentException>(() => D3D9SurfaceSnapshot.CopyConfiguredObjectFx(packets.Frame, packets.Visual));
        Assert.Throws<ArgumentException>(() => D3D9SurfaceSnapshot.CopyIsolatedSurface(packets.Frame, packets.Visual,
            D3D9SurfaceIsolation.NoConfiguredMmeOrEmm));
    }

    [Fact]
    public void MissingMetadataFlagWrongVersionPairUnknownFlagsAndChangedFrameAreRejected()
    {
        foreach (uint flags in new uint[] { 0, 1, 2, 3, 12 })
        {
            var invalid = Packets(flags);
            Assert.Throws<ArgumentException>(() => D3D9SurfaceSnapshot.CopyConfiguredEmm(invalid.Frame, invalid.Visual));
        }
        var packets = Packets(4);
        BinaryPrimitives.WriteUInt32LittleEndian(packets.Visual.AsSpan(8), 4);
        Assert.Throws<ArgumentException>(() => D3D9SurfaceSnapshot.CopyConfiguredEmm(packets.Frame, packets.Visual));
        BinaryPrimitives.WriteUInt32LittleEndian(packets.Visual.AsSpan(8), 5);
        packets.Frame[212] ^= 1;
        Assert.Throws<ArgumentException>(() => D3D9SurfaceSnapshot.CopyConfiguredEmm(packets.Frame, packets.Visual));
        SHA256.HashData(packets.Frame).CopyTo(packets.Visual.AsSpan(20));
        Assert.Equal(4U, BinaryPrimitives.ReadUInt32LittleEndian(D3D9SurfaceSnapshot.CopyConfiguredEmm(packets.Frame, packets.Visual).FrameBytes.AsSpan(8)));
        BinaryPrimitives.WriteUInt32LittleEndian(packets.Frame.AsSpan(8), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(packets.Visual.AsSpan(8), 4);
        SHA256.HashData(packets.Frame).CopyTo(packets.Visual.AsSpan(20));
        Assert.Throws<ArgumentException>(() => D3D9SurfaceSnapshot.CopyConfiguredEmm(packets.Frame, packets.Visual));
    }

    // Header-only capability fixtures never enter a native session.
    private static (byte[] Frame, byte[] Visual) Packets(uint flags)
    {
        var legacy = SurfaceSessionTests.Packets();
        var frame = new byte[216]; legacy.Frame.CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(208), flags);
        var visual = new byte[164]; legacy.Visual.CopyTo(visual, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(visual.AsSpan(8), 5);
        SHA256.HashData(frame).CopyTo(visual.AsSpan(20));
        return (frame, visual);
    }
}
