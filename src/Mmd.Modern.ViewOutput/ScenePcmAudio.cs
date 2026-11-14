using System.Buffers.Binary;
using Mmd.Modern.Application;

namespace Mmd.Modern.ViewOutput;

/// <summary>Validated, frozen integer PCM WAV data. A sample is one complete interleaved channel frame.
/// RIFF traversal and whole-frame alignment follow the Microsoft WAVEFORMATEX contract; no decoder,
/// resampling, channel mixing or scene clock is owned here.</summary>
public sealed class ScenePcmAudio
{
    private readonly byte[] source;
    private readonly int dataOffset;
    private readonly int dataLength;
    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample { get; }
    public int BlockAlign { get; }
    public long SampleCount => dataLength / BlockAlign;
    public string AssetSha256 { get; }
    internal ReadOnlyMemory<byte> PcmData => source.AsMemory(dataOffset, dataLength);

    private ScenePcmAudio(byte[] source, int dataOffset, int dataLength, int sampleRate,
        int channels, int bitsPerSample, int blockAlign, string sha256)
    {
        this.source = source; this.dataOffset = dataOffset; this.dataLength = dataLength;
        SampleRate = sampleRate; Channels = channels; BitsPerSample = bitsPerSample;
        BlockAlign = blockAlign; AssetSha256 = sha256;
    }

    public static ScenePcmAudio FromAsset(SceneMediaAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        byte[] source = asset.CopyBytes();
        ReadOnlySpan<byte> bytes = source;
        if (bytes.Length < 12 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Audio requires a bounded RIFF WAVE file; RF64 and other containers are unsupported.");
        long end = 8L + BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4));
        if (end != bytes.Length) throw new InvalidDataException("WAV RIFF length does not match the frozen payload.");
        int sampleRate = 0, channels = 0, bits = 0, blockAlign = 0, dataOffset = -1, dataLength = 0;
        bool haveFormat = false;
        for (long position = 12; position < end;)
        {
            if (end - position < 8) throw new InvalidDataException("WAV has a truncated chunk header.");
            int header = (int)position;
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(header + 4, 4));
            long body = position + 8, next = body + length + (length & 1);
            if (next > end) throw new InvalidDataException("WAV chunk data or word padding is truncated.");
            ReadOnlySpan<byte> id = bytes.Slice(header, 4);
            if (id.SequenceEqual("fmt "u8))
            {
                if (haveFormat || length < 16 || length == 17) throw new InvalidDataException("WAV requires one complete PCM format chunk.");
                ReadOnlySpan<byte> format = bytes.Slice((int)body, (int)length);
                ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(format);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
                uint rate = BinaryPrimitives.ReadUInt32LittleEndian(format[4..]);
                uint average = BinaryPrimitives.ReadUInt32LittleEndian(format[8..]);
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(format[12..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(format[14..]);
                if (tag != 1 || channels is not (1 or 2) || rate is < 8000 or > 192000 || bits is not (8 or 16 or 24 or 32))
                    throw new InvalidDataException("Supported audio is mono/stereo integer PCM WAV (8/16/24/32 bit, 8–192 kHz); float, extensible and compressed formats are unsupported.");
                if (blockAlign != channels * (bits / 8) || average != rate * blockAlign)
                    throw new InvalidDataException("WAV block alignment or average byte rate is inconsistent with PCM format.");
                if (length >= 18 && BinaryPrimitives.ReadUInt16LittleEndian(format[16..]) != length - 18)
                    throw new InvalidDataException("WAV extended format size does not match its chunk.");
                sampleRate = (int)rate; haveFormat = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (dataOffset >= 0) throw new InvalidDataException("Multiple WAV data chunks are unsupported.");
                dataOffset = (int)body; dataLength = (int)length;
            }
            position = next;
        }
        if (!haveFormat || dataOffset < 0 || dataLength % blockAlign != 0)
            throw new InvalidDataException("WAV requires a PCM format and a whole-frame data chunk.");
        return new(source, dataOffset, dataLength, sampleRate, channels, bits, blockAlign, asset.Sha256);
    }

    /// <summary>Returns an independent copy; project bytes cannot be changed through this API.</summary>
    public byte[] CopyData() => PcmData.ToArray();

    public byte[] CopyFrames(long start, long count)
    {
        if (start < 0 || count < 0 || start > SampleCount || count > SampleCount - start)
            throw new ArgumentOutOfRangeException(nameof(start), "PCM frame range lies outside the captured audio.");
        return PcmData.Slice(checked((int)(start * BlockAlign)), checked((int)(count * BlockAlign))).ToArray();
    }

    internal long FrameAtTicks(long ticks)
    {
        if (ticks < 0) throw new ArgumentOutOfRangeException(nameof(ticks));
        long frames = (long)((UInt128)(ulong)ticks * (uint)SampleRate / (uint)TimeSpan.TicksPerSecond);
        return Math.Min(frames, SampleCount);
    }
}
