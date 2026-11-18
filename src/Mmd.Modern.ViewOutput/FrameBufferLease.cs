namespace Mmd.Modern.ViewOutput;

public enum FramePixelLayout { Rgba8 }
public enum FrameColorSpace { Srgb }
public enum FrameAlphaMode { Straight }

/// <summary>Owned top-to-bottom, full-range RGBA8 sRGB pixels with straight alpha. A row may contain padding.
/// The producer must relinquish access to the supplied memory. The sink may borrow it only until WriteAsync returns.</summary>
public sealed class FrameBufferLease : IDisposable
{
    private readonly Memory<byte> pixels;
    private Action? release;
    private int disposed;
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public FramePixelLayout Layout => FramePixelLayout.Rgba8;
    public FrameColorSpace ColorSpace => FrameColorSpace.Srgb;
    public FrameAlphaMode Alpha => FrameAlphaMode.Straight;
    public bool IsDisposed => Volatile.Read(ref disposed) != 0;
    public ReadOnlyMemory<byte> Pixels
    {
        get { ObjectDisposedException.ThrowIf(IsDisposed, this); return pixels; }
    }

    public FrameBufferLease(int width, int height, int stride, Memory<byte> pixels, Action? release = null)
    {
        int length = ValidateShape(width, height, stride);
        if (pixels.Length < length) throw new ArgumentException("Pixel storage is shorter than stride times height.", nameof(pixels));
        Width = width; Height = height; Stride = stride; this.pixels = pixels[..length]; this.release = release;
    }

    internal static int ValidateShape(int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || width > 32768 || height > 32768 || (long)width * height > 268_435_456)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (stride < (long)width * 4 || (long)stride * height > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(stride));
        return checked(stride * height);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) Interlocked.Exchange(ref release, null)?.Invoke();
    }
}
