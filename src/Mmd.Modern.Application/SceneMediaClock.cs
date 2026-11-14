using System.Numerics;
using System.Security.Cryptography;

namespace Mmd.Modern.Application;

public enum SceneMediaKind { Audio, Video, BackgroundImage }

/// <summary>Opaque immutable media bytes; only a decoder host can validate and play their actual format.</summary>
public sealed class SceneMediaAsset
{
    public const int MaxBytes = 256 * 1024 * 1024;
    private readonly byte[] bytes;
    public string Name { get; }
    public string Sha256 { get; }
    public int Length => bytes.Length;
    internal ReadOnlySpan<byte> Bytes => bytes;
    private SceneMediaAsset(byte[] bytes, string name)
    { this.bytes = bytes; Name = name; Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)); }
    internal SceneMediaAsset WithName(string name)
    { ValidateName(name); return new(bytes, name); }
    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name != Path.GetFileName(name) || name.Length > 255 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || string.IsNullOrWhiteSpace(Path.GetExtension(name)))
            throw new ArgumentException("Media requires a base file name with an extension.", nameof(name));
    }
    public static SceneMediaAsset FromBytes(ReadOnlySpan<byte> bytes, string name)
    {
        ValidateName(name);
        if (bytes.Length == 0 || bytes.Length > MaxBytes) throw new InvalidDataException("Media payload exceeds the supported bounded size.");
        return new(bytes.ToArray(), name);
    }
    public static SceneMediaAsset Load(string path, CancellationToken token = default)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("A fully qualified media path is required.", nameof(path));
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > MaxBytes) throw new InvalidDataException("Media payload exceeds the supported bounded size.");
        using var output = new MemoryStream(); byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            token.ThrowIfCancellationRequested(); int count = input.Read(buffer, 0, Math.Min(buffer.Length, MaxBytes - (int)output.Length + 1));
            if (count == 0) break;
            if (output.Length + count > MaxBytes) throw new InvalidDataException("Media payload grew past the supported limit.");
            output.Write(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested(); return FromBytes(output.ToArray(), Path.GetFileName(path));
    }
    /// <summary>A copy for the decoder host; callers cannot mutate the captured project resource.</summary>
    public byte[] CopyBytes() => bytes.ToArray();
}

public sealed record SceneMediaTrack(Guid Id, SceneMediaKind Kind, SceneMediaAsset Asset, long OffsetTicks, bool Enabled)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Asset);
        if (Id == Guid.Empty || !Enum.IsDefined(Kind) || OffsetTicks == long.MinValue)
            throw new ArgumentException("Media requires a stable ID, known kind and representable signed offset.");
    }
}

public sealed record SceneMediaSample(Guid TrackId, SceneMediaKind Kind, string Sha256, bool Active, long MediaTicks, bool Playing);

/// <summary>Deterministic requested media time, not a decoder or proof of audio/video playback. Host supplies monotonic elapsed deltas.</summary>
public sealed class SceneMediaClock
{
    private OfflineFrameTime seconds = new(0, 1);
    public OfflineFrameRate OutputFrameRate { get; private set; }
    public bool IsPlaying { get; private set; }
    public OfflineFrameTime SourceFrame => new(seconds.Numerator * 30, seconds.Denominator);
    public SceneMediaClock(OfflineFrameRate frameRate) => SetOutputFrameRate(frameRate);
    public void SetOutputFrameRate(OfflineFrameRate frameRate)
    {
        if (frameRate.Numerator <= 0 || frameRate.Denominator <= 0) throw new ArgumentException("Initialized output FPS required.", nameof(frameRate));
        OutputFrameRate = frameRate;
    }
    public void Play() => IsPlaying = true;
    public void Pause() => IsPlaying = false;
    public void Seek(OfflineFrameTime sourceFrame)
    {
        if (sourceFrame.Denominator <= 0 || sourceFrame.Numerator > (BigInteger)uint.MaxValue * sourceFrame.Denominator) throw new ArgumentOutOfRangeException(nameof(sourceFrame));
        seconds = new(sourceFrame.Numerator, sourceFrame.Denominator * 30);
    }
    public void SeekOutputFrame(long index)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        Seek(new((BigInteger)index * OutputFrameRate.Denominator * 30, OutputFrameRate.Numerator));
    }
    public void Advance(TimeSpan elapsed)
    {
        if (elapsed.Ticks < 0) throw new ArgumentOutOfRangeException(nameof(elapsed));
        if (!IsPlaying) return;
        var next = new OfflineFrameTime(seconds.Numerator * TimeSpan.TicksPerSecond + (BigInteger)elapsed.Ticks * seconds.Denominator,
            seconds.Denominator * TimeSpan.TicksPerSecond);
        Seek(new(next.Numerator * 30, next.Denominator));
    }
    public SceneMediaSample Sample(SceneMediaTrack track)
    {
        ArgumentNullException.ThrowIfNull(track); track.Validate();
        BigInteger requested = seconds.Numerator * TimeSpan.TicksPerSecond / seconds.Denominator - track.OffsetTicks;
        if (requested > long.MaxValue) throw new OverflowException("Media time cannot be represented by the decoder's tick contract.");
        return new(track.Id, track.Kind, track.Asset.Sha256, track.Enabled && requested >= 0, requested < 0 ? 0 : (long)requested, IsPlaying);
    }
}
