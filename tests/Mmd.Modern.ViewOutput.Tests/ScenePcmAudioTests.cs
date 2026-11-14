using System.Buffers.Binary;
using Mmd.Modern.Application;
using Xunit;

namespace Mmd.Modern.ViewOutput.Tests;

public sealed class ScenePcmAudioTests
{
    [Theory]
    [InlineData(1, 8, 8000)]
    [InlineData(2, 16, 44100)]
    [InlineData(1, 24, 48000)]
    [InlineData(2, 32, 192000)]
    public void FrozenPcmSupportsWholeFrameCropAndIndependentCopies(int channels, int bits, int rate)
    {
        int align = channels * bits / 8;
        byte[] data = Enumerable.Range(0, align * 4).Select(value => (byte)value).ToArray();
        byte[] wav = Wav(data, channels, bits, rate, oddJunk: true);
        var asset = SceneMediaAsset.FromBytes(wav, "test.wav");
        var pcm = ScenePcmAudio.FromAsset(asset);
        Array.Fill(wav, (byte)0);
        Assert.Equal(rate, pcm.SampleRate); Assert.Equal(channels, pcm.Channels);
        Assert.Equal(bits, pcm.BitsPerSample); Assert.Equal(align, pcm.BlockAlign);
        Assert.Equal(4, pcm.SampleCount); Assert.Equal(asset.Sha256, pcm.AssetSha256);
        Assert.Equal(data.AsSpan(align, align * 2).ToArray(), pcm.CopyFrames(1, 2));
        byte[] copy = pcm.CopyData(); Array.Fill(copy, (byte)0);
        Assert.Equal(data, pcm.CopyData());
        Assert.Empty(pcm.CopyFrames(4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => pcm.CopyFrames(3, 2));
    }

    [Theory]
    [InlineData(3)] // IEEE float
    [InlineData(65534)] // WAVE_FORMAT_EXTENSIBLE
    [InlineData(2)] // compressed
    public void NonIntegerPcmTagsFailExplicitly(int tag)
    {
        byte[] wav = Wav(new byte[8]);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), (ushort)tag);
        Assert.Throws<InvalidDataException>(() => ScenePcmAudio.FromAsset(SceneMediaAsset.FromBytes(wav, "bad.wav")));
    }

    [Fact]
    public void TruncatedChunksInvalidAlignmentAndAmbiguousDataAreRejected()
    {
        byte[] valid = Wav(new byte[8]);
        byte[] badAlignment = valid.ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(badAlignment.AsSpan(32), 1);
        byte[] badRate = valid.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(badRate.AsSpan(28), 1);
        byte[] partialFrame = Wav(new byte[3]);
        byte[] truncated = valid[..^1];
        byte[] rf64 = valid.ToArray(); "RF64"u8.CopyTo(rf64);
        byte[] duplicate = new byte[valid.Length + 8]; valid.CopyTo(duplicate, 0);
        "data"u8.CopyTo(duplicate.AsSpan(valid.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(duplicate.AsSpan(4), (uint)(duplicate.Length - 8));
        foreach (byte[] bad in new[] { badAlignment, badRate, partialFrame, truncated, rf64, duplicate })
            Assert.Throws<InvalidDataException>(() => ScenePcmAudio.FromAsset(SceneMediaAsset.FromBytes(bad, "bad.wav")));
    }

    internal static byte[] Wav(byte[] data, int channels = 1, int bits = 16, int rate = 8000, bool oddJunk = false)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(0u); writer.Write("WAVE"u8);
        if (oddJunk) { writer.Write("JUNK"u8); writer.Write(1u); writer.Write((byte)42); writer.Write((byte)0); }
        writer.Write("fmt "u8); writer.Write(16u); writer.Write((ushort)1); writer.Write((ushort)channels);
        writer.Write((uint)rate); writer.Write((uint)(rate * channels * bits / 8));
        writer.Write((ushort)(channels * bits / 8)); writer.Write((ushort)bits);
        writer.Write("data"u8); writer.Write((uint)data.Length); writer.Write(data);
        if ((data.Length & 1) != 0) writer.Write((byte)0);
        writer.Flush(); byte[] bytes = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8)); return bytes;
    }
}
