using Xunit;

namespace Mmd.Modern.ViewOutput.Tests;

public sealed class FrameBufferLeaseTests
{
    [Fact]
    public void PaddedRowsRetainExplicitFormatAndReturnMemoryExactlyOnce()
    {
        int releases = 0;
        var frame = new FrameBufferLease(2, 3, 12, new byte[40], () => releases++);
        Assert.Equal(36, frame.Pixels.Length);
        Assert.Equal(FramePixelLayout.Rgba8, frame.Layout);
        Assert.Equal(FrameColorSpace.Srgb, frame.ColorSpace);
        Assert.Equal(FrameAlphaMode.Straight, frame.Alpha);
        frame.Dispose(); frame.Dispose();
        Assert.Equal(1, releases);
        Assert.Throws<ObjectDisposedException>(() => frame.Pixels);
    }

    [Fact]
    public void InvalidDimensionsStrideAndShortStorageAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameBufferLease(0, 1, 4, new byte[4]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameBufferLease(2, 1, 7, new byte[8]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameBufferLease(2, 32768, int.MaxValue, new byte[8]));
        Assert.Throws<ArgumentException>(() => new FrameBufferLease(2, 2, 8, new byte[15]));
    }
}
