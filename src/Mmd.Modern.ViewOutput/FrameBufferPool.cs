namespace Mmd.Modern.ViewOutput;

/// <summary>Thread-safe reuse of equally sized CPU frame buffers. Rented contents are unspecified; the renter
/// overwrites them. Retention is bounded: rentals beyond it allocate and surplus returns are left to the GC.
/// A producer may wrap a rented buffer through CreateLease or RentLease; disposing that lease returns it.</summary>
public sealed class FrameBufferPool
{
    private readonly Stack<byte[]> free = new();
    private int totalAllocated;
    public int BufferLength { get; }
    public int MaximumRetained { get; }
    internal int RetainedCount { get { lock (free) return free.Count; } }
    internal int TotalAllocated => Volatile.Read(ref totalAllocated);

    public FrameBufferPool(int bufferLength, int maximumRetained)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferLength);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRetained);
        BufferLength = bufferLength; MaximumRetained = maximumRetained;
    }

    public byte[] Rent()
    {
        lock (free) if (free.Count != 0) return free.Pop();
        byte[] buffer = GC.AllocateUninitializedArray<byte>(BufferLength);
        Interlocked.Increment(ref totalAllocated);
        return buffer;
    }

    public void Return(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length != BufferLength) throw new ArgumentException("The buffer does not belong to this pool.", nameof(buffer));
        lock (free) if (free.Count < MaximumRetained) free.Push(buffer);
    }

    /// <summary>Transfers a previously rented buffer into a FrameBufferLease whose disposal returns it.</summary>
    public FrameBufferLease CreateLease(int width, int height, int stride, byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length != BufferLength) throw new ArgumentException("The buffer does not belong to this pool.", nameof(buffer));
        try { return new(width, height, stride, buffer, () => Return(buffer)); }
        catch { Return(buffer); throw; }
    }

    /// <summary>Rents a buffer, lets <paramref name="populate"/> fill the exact stride * height region,
    /// and transfers ownership to a lease whose disposal returns the buffer to this pool.</summary>
    public FrameBufferLease RentLease(int width, int height, int stride, Action<Span<byte>> populate)
    {
        ArgumentNullException.ThrowIfNull(populate);
        int length = FrameBufferLease.ValidateShape(width, height, stride);
        if (length > BufferLength) throw new ArgumentException("Requested lease exceeds pool buffer length.", nameof(stride));
        byte[] buffer = Rent();
        try
        {
            populate(buffer.AsSpan(0, length));
            return new(width, height, stride, buffer, () => Return(buffer));
        }
        catch { Return(buffer); throw; }
    }
}
