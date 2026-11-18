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

    [Fact]
    public void PoolReusesReturnedBuffersWithinCapAndRejectsMismatches()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameBufferPool(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameBufferPool(16, -1));
        var pool = new FrameBufferPool(16, maximumRetained: 2);
        var a = pool.Rent();
        var b = pool.Rent();
        var c = pool.Rent();
        Assert.NotSame(a, b);
        Assert.Equal(3, pool.TotalAllocated);
        Assert.Equal(0, pool.RetainedCount);
        Assert.Throws<ArgumentNullException>(() => pool.Return(null!));
        Assert.Throws<ArgumentException>(() => pool.Return(new byte[12]));
        pool.Return(a);
        pool.Return(b);
        pool.Return(c); // Surplus return beyond MaximumRetained is left to GC.
        Assert.Equal(2, pool.RetainedCount);
        var reused = pool.Rent();
        Assert.True(ReferenceEquals(reused, a) || ReferenceEquals(reused, b));
        Assert.Equal(1, pool.RetainedCount);
        Assert.Equal(3, pool.TotalAllocated);
    }

    [Fact]
    public void PoolRentLeaseAndCreateLeaseReturnBuffersOnDisposeAndPopulateFailure()
    {
        var pool = new FrameBufferPool(32, maximumRetained: 2);
        using (var lease = pool.RentLease(2, 2, 12, span =>
        {
            Assert.Equal(24, span.Length);
            span.Fill(42);
        }))
        {
            Assert.Equal(0, pool.RetainedCount);
            Assert.Equal(42, lease.Pixels.Span[0]);
            Assert.Equal(42, lease.Pixels.Span[23]);
        }
        Assert.Equal(1, pool.RetainedCount);

        byte[] rented = pool.Rent();
        Assert.Equal(0, pool.RetainedCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.CreateLease(0, 2, 8, rented));
        Assert.Equal(1, pool.RetainedCount);

        Assert.Throws<InvalidOperationException>(() => pool.RentLease(2, 2, 8, _ => throw new InvalidOperationException("fill failed")));
        Assert.Equal(1, pool.RetainedCount);
        Assert.Throws<ArgumentException>(() => pool.RentLease(4, 4, 16, _ => { }));
        Assert.Equal(1, pool.RetainedCount);
        Assert.Equal(1, pool.TotalAllocated);
    }
}
