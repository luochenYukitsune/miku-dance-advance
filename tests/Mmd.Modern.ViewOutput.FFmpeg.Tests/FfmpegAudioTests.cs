using System.Buffers.Binary;
using System.Diagnostics;
using Mmd.Modern.Application;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.ViewOutput.FFmpeg.Tests;

public sealed class FfmpegAudioTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void SampleWindowUsesSelectedSourceStartAndSignedTrackOffset()
    {
        var plan = new OfflineFramePlan(64, 64, new(60), 3, 9, includeEndFrame: false);
        var input = Input(new short[2400]);
        Assert.Equal(new SceneAudioExportRange(800, 1600, 0, 1600), input.GetRange(plan));
        var delayed = Input(new short[2400], TimeSpan.FromMilliseconds(200).Ticks);
        Assert.Equal(new SceneAudioExportRange(0, 800, 800, 1600), delayed.GetRange(plan));
        var shortAudio = Input(new short[1000]);
        Assert.Equal(new SceneAudioExportRange(800, 200, 0, 1600), shortAudio.GetRange(plan));
        Assert.Equal(0, Input(new short[100]).GetRange(plan).SourceSampleCount);
        Assert.Equal(0, Input(new short[100], long.MaxValue).GetRange(plan).SourceSampleCount);
        Assert.Throws<ArgumentException>(() => new SceneAudioExportInput(new(Guid.NewGuid(), SceneMediaKind.Audio,
            SceneMediaAsset.FromBytes(Wave(new short[1]), "owned.wav"), 0, false)));
    }

    [Fact]
    public void FractionalOutputDurationRoundsOnlyOnceOnThePcmSampleGrid()
    {
        var input = Input(new short[10000], sampleRate: 44100);
        var range = input.GetRange(new(64, 64, new(30000, 1001), 1, 4, includeEndFrame: false));
        Assert.Equal(1470, range.SourceStartSample);
        Assert.Equal(4415, range.OutputSampleCount);
        var options = new FfmpegOutputOptions("audio.mp4", VideoEncoder.H264Software) { AudioInput = input };
        var args = options.CreateArguments(new(64, 64, new(30), 0, 1), "out.partial", "owned-private.pcm");
        Assert.DoesNotContain("-an", args);
        Assert.DoesNotContain("-shortest", args);
        Assert.Contains("aac", args);
        Assert.Contains(args, value => value.Contains("apad=whole_len=", StringComparison.Ordinal));
    }

    // One owned fixture exercises both container audio policies, without an encoder capability matrix.
    [Theory]
    [InlineData(VideoEncoder.RawVideo)]
    [InlineData(VideoEncoder.HevcSoftware)]
    public async Task NonzeroIntervalMuxRetainsVideoAndPlacesAudioBeforeShortSourceSilence(VideoEncoder encoder)
    {
        string? ffmpeg = Environment.GetEnvironmentVariable("MMD_FFMPEG_PATH");
        string? ffprobe = Environment.GetEnvironmentVariable("MMD_FFPROBE_PATH");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(ffmpeg) || string.IsNullOrWhiteSpace(ffprobe), "Set the explicit FFmpeg tool paths for the owned audio integration probe.");
        string directory = Path.Combine(Environment.GetEnvironmentVariable("MMD_FFMPEG_ARTIFACTS")
            ?? Path.Combine(Path.GetTempPath(), "mmd-ffmpeg-tests"), "audio-selected-interval");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, encoder + (encoder == VideoEncoder.RawVideo ? ".avi" : ".mp4"));
        short[] source = new short[1500];
        // Distinct bursts permit AAC's lossy ringing while identifying both selected source positions.
        for (int i = 0; i < 16; i++) { source[900 + i] = 24000; source[1300 + i] = -12000; }
        var plan = new OfflineFramePlan(64, 64, new(30), 3, 9, includeEndFrame: false);
        var options = new FfmpegOutputOptions(path, encoder) { AudioInput = Input(source) };
        var tools = new FfmpegToolchain(ffmpeg!, ffprobe!);
        byte[] previous = [11, 22, 33];
        await File.WriteAllBytesAsync(path, previous, Token);
        if (encoder == VideoEncoder.RawVideo)
        {
            await using var incomplete = new FfmpegFrameSink(tools, options);
            await incomplete.BeginAsync(new(plan), Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => incomplete.CompleteAsync(Token).AsTask());
            Assert.Equal(previous, await File.ReadAllBytesAsync(path, Token));
            Assert.Empty(Directory.GetFiles(directory, "*.pcm"));
        }
        await using (var sink = new FfmpegFrameSink(tools, options))
        {
            await sink.BeginAsync(new(plan), Token);
            for (long i = 0; i < plan.FrameCount; i++)
            {
                byte[] pixels = new byte[64 * 64 * 4];
                for (int p = 0; p < pixels.Length; p += 4) { pixels[p] = (byte)(i * 20); pixels[p + 3] = 255; }
                using var frame = new FrameBufferLease(64, 64, 256, pixels);
                await sink.WriteAsync(plan.GetSample(i), frame, Token);
            }
            await sink.CompleteAsync(Token);
            Assert.Equal(plan.FrameCount, sink.Validation!.Frames);
            Assert.NotNull(sink.Validation.Audio);
            Assert.True(sink.Validation.Audio.Decoded);
            Assert.Equal(1600, sink.Validation.Audio.ExpectedSamples);
        }
        short[] decoded = await DecodeAudio(ffmpeg!, path);
        Assert.InRange(decoded.Length, 1600, encoder == VideoEncoder.RawVideo ? 1600 : 2623);
        Assert.True(decoded.Skip(90).Take(40).Max(value => Math.Abs((int)value)) > 10000);
        Assert.True(decoded.Skip(490).Take(40).Max(value => Math.Abs((int)value)) > 5000);
        Assert.True(decoded.Skip(1000).Take(600).Max(value => Math.Abs((int)value)) < (encoder == VideoEncoder.RawVideo ? 1 : 200));
        Assert.Empty(Directory.GetFiles(directory, "*.partial"));
        Assert.Empty(Directory.GetFiles(directory, "*.pcm"));
    }

    private static SceneAudioExportInput Input(short[] data, long offset = 0, int sampleRate = 8000)
        => new(new(Guid.NewGuid(), SceneMediaKind.Audio, SceneMediaAsset.FromBytes(Wave(data, sampleRate), "owned.wav"), offset, true));

    private static byte[] Wave(short[] samples, int rate = 8000)
    {
        byte[] data = new byte[44 + samples.Length * 2];
        "RIFF"u8.CopyTo(data); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)data.Length - 8);
        "WAVEfmt "u8.CopyTo(data.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), (uint)rate); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), (uint)rate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(32), 2); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(34), 16);
        "data"u8.CopyTo(data.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), (uint)samples.Length * 2);
        for (int i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(44 + i * 2), samples[i]);
        return data;
    }

    private static async Task<short[]> DecodeAudio(string executable, string path)
    {
        ProcessStartInfo info = new(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-v", "error", "-i", path, "-map", "0:a:0", "-f", "s16le", "-c:a", "pcm_s16le", "pipe:1" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        using var registration = Token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        using var output = new MemoryStream();
        var diagnostic = process.StandardError.ReadToEndAsync(Token);
        await process.StandardOutput.BaseStream.CopyToAsync(output, Token);
        await process.WaitForExitAsync(Token);
        Assert.Equal(0, process.ExitCode); Assert.Empty(await diagnostic);
        byte[] bytes = output.ToArray(); short[] samples = new short[bytes.Length / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2));
        return samples;
    }
}
