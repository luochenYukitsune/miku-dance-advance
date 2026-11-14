using Mmd.Modern.Application;

namespace Mmd.Modern.ViewOutput;

/// <summary>Decodes captured image/video bytes. The caller supplies media time sampled from the
/// scene animation clock; this contract never owns a playback clock, audio device or composition policy.</summary>
public interface ISceneVideoDecoder
{
    Task<ISceneVideoDecodeSession> OpenAsync(SceneMediaAsset asset, string absoluteScratchRoot,
        CancellationToken cancellationToken = default);
}

public interface ISceneVideoDecodeSession : IAsyncDisposable
{
    string SourceSha256 { get; }
    int Width { get; }
    int Height { get; }

    /// <summary>Returns the latest decoded presentation frame at or before the requested media time.
    /// A still image and the last video frame remain available. IsLastFrame lets the consumer apply its
    /// own end-of-media policy. Seeking and decoding do not advance the scene.</summary>
    ValueTask<SceneVideoFrame> DecodeAsync(long mediaTicks, CancellationToken cancellationToken = default);
}

/// <summary>Owned, top-to-bottom, straight-alpha RGBA8 pixels. PresentationTicks are relative to the
/// first decoded presentation frame. The decoder converts the source matrix/range to packed RGB;
/// it does not rotate, scale, tone-map or change the source transfer function/primaries. Unspecified
/// metadata remains unknown. A compositor must interpret these characteristics before mixing
/// colors; these pixels must not be wrapped as sRGB solely because the byte layout is RGBA.</summary>
public sealed class SceneVideoFrame : IDisposable
{
    private readonly byte[] pixels;
    private bool disposed;
    public int Width { get; }
    public int Height { get; }
    public int Stride => checked(Width * 4);
    public long PresentationTicks { get; }
    public bool IsLastFrame { get; }
    public string? ColorTransfer { get; }
    public string? ColorPrimaries { get; }
    /// <summary>The source's matrix name for provenance; its YUV-to-RGB matrix has already been
    /// applied by the decoder and must not be applied again to these RGB pixels.</summary>
    public string? SourceColorSpace { get; }
    public ReadOnlyMemory<byte> Pixels
    {
        get { ObjectDisposedException.ThrowIf(disposed, this); return pixels; }
    }

    public SceneVideoFrame(int width, int height, ReadOnlySpan<byte> pixels, long presentationTicks,
        bool isLastFrame, string? colorTransfer = null, string? colorPrimaries = null, string? colorSpace = null)
    {
        if (width <= 0 || height <= 0 || width > 32768 || height > 32768
            || (long)width * height * 4 > int.MaxValue || presentationTicks < 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (pixels.Length != checked(width * height * 4)) throw new ArgumentException("RGBA storage must match the decoded dimensions.", nameof(pixels));
        Width = width; Height = height; this.pixels = pixels.ToArray(); PresentationTicks = presentationTicks;
        IsLastFrame = isLastFrame; ColorTransfer = colorTransfer; ColorPrimaries = colorPrimaries; SourceColorSpace = colorSpace;
    }

    public void Dispose() => disposed = true;
}
