using System.Diagnostics;
using System.Text.Json;
using Mmd.Modern.Application;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.FFmpeg;
using Xunit;

namespace Mmd.Modern.ViewOutput.FFmpeg.Tests;

public sealed class FfmpegTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public void ExplicitCodecContainerEvenDimensionsAndArgumentBoundaries()
    {
        var odd = new OfflineFramePlan(63, 65, new(30000, 1001), 0, 2);
        Assert.Throws<ArgumentException>(() => new FfmpegOutputOptions("x.mp4", VideoEncoder.H264Software).Validate(odd));
        new FfmpegOutputOptions("x.avi", VideoEncoder.RawVideo).Validate(odd);
        Assert.Throws<ArgumentException>(() => new FfmpegOutputOptions("x.mp4", VideoEncoder.RawVideo).Validate(odd));
        string target = Path.Combine(Path.GetTempPath(), "video with spaces; & x.mp4");
        var args = new FfmpegOutputOptions(target, VideoEncoder.Av1AomSoftware).CreateArguments(
            new(64, 64, new(30000, 1001), 0, 2), target);
        Assert.Equal(target, args[^1]); Assert.Contains("30000/1001", args);
        Assert.Contains("libaom-av1", args); Assert.DoesNotContain("libx264", args);
        Assert.Contains("-an", args);
    }

    [Fact]
    public async Task RealEncoderMatrixIncludesFourCodecsAndRecordsActualHardwareFailures()
    {
        var tools = Tools(); string directory = ArtifactDirectory();
        var identity = await tools.InspectAsync("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip", Token);
        await File.WriteAllTextAsync(Path.Combine(directory, "tool-identity.json"), JsonSerializer.Serialize(identity, JsonOptions), Token);
        var capabilities = await tools.ProbeEncodersAsync(directory,
            [VideoEncoder.RawVideo, VideoEncoder.H264Software, VideoEncoder.HevcSoftware, VideoEncoder.Av1AomSoftware,
                VideoEncoder.H264Nvenc, VideoEncoder.HevcNvenc, VideoEncoder.Av1Nvenc], Token);
        await File.WriteAllTextAsync(Path.Combine(directory, "encoder-execution.json"), JsonSerializer.Serialize(capabilities, JsonOptions), Token);
        foreach (var software in capabilities.Take(4))
        {
            Assert.True(software.Executed, software.Failure);
            Assert.True(software.Validation!.Decoded); Assert.Equal("30000/1001", software.Validation.FrameRate);
        }
        // Every hardware row must be recorded, and a row reported as executed must have produced a decodable stream
        // (the previous `Executed || Failure != null` assertion could never fail).
        Assert.Equal(3, capabilities.Skip(4).Count());
        foreach (var hardware in capabilities.Skip(4))
        {
            if (hardware.Executed)
            {
                Assert.Null(hardware.Failure);
                Assert.True(hardware.Validation!.Decoded); Assert.Equal("30000/1001", hardware.Validation.FrameRate);
            }
            else Assert.False(string.IsNullOrWhiteSpace(hardware.Failure));
        }
        // On a machine that declares NVENC support, a failed hardware encode is a regression, not a recorded skip.
        if (Environment.GetEnvironmentVariable("MMD_EXPECT_NVENC") == "1")
            Assert.All(capabilities.Skip(4), hardware => Assert.True(hardware.Executed, hardware.Failure));
    }

    [Fact]
    public async Task StrideAndStraightAlphaProduceExpectedRawPixels()
    {
        var tools = Tools(); string path = Path.Combine(ArtifactDirectory(), "stride-alpha.avi");
        await File.WriteAllBytesAsync(path, [99, 98, 97], Token); // Successful completion must atomically replace this old target.
        var plan = new OfflineFramePlan(4, 2, new(24), 0, 0);
        var options = new FfmpegOutputOptions(path, VideoEncoder.RawVideo) { BackgroundBlue = 255 };
        byte[] pixels = Enumerable.Repeat((byte)239, 48).ToArray();
        for (int y = 0; y < 2; y++) for (int x = 0; x < 4; x++)
        { int p = y * 24 + x * 4; pixels[p] = 255; pixels[p + 1] = 0; pixels[p + 2] = 0; pixels[p + 3] = 128; }
        await using var sink = new FfmpegFrameSink(tools, options);
        await sink.BeginAsync(new(plan), Token);
        using var frame = new FrameBufferLease(4, 2, 24, pixels);
        await sink.WriteAsync(plan.GetSample(0), frame, Token); Assert.False(frame.IsDisposed);
        await sink.CompleteAsync(Token);
        byte[] decoded = await Decode(tools.FfmpegPath, path);
        Assert.Equal(24, decoded.Length);
        for (int p = 0; p < decoded.Length; p += 3)
        { Assert.InRange(decoded[p], (byte)187, (byte)189); Assert.Equal(0, decoded[p + 1]); Assert.InRange(decoded[p + 2], (byte)186, (byte)188); }
    }

    [Fact]
    public async Task FailureAndCancellationPreserveExistingDestinationAndDiscardPartial()
    {
        var tools = Tools(); string directory = Path.Combine(ArtifactDirectory(), "transaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, "existing.avi");
        byte[] original = [11, 22, 33, 44]; await File.WriteAllBytesAsync(path, original, Token);
        var plan = new OfflineFramePlan(64, 64, new(30), 0, 1);
        await using (var sink = new FfmpegFrameSink(tools, new(path, VideoEncoder.RawVideo)))
        {
            await sink.BeginAsync(new(plan), Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => sink.CompleteAsync(Token).AsTask());
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(path, Token));
        await using (var sink = new FfmpegFrameSink(tools, new(path, VideoEncoder.RawVideo)))
        {
            await sink.BeginAsync(new(plan), Token);
            using var frame = new FrameBufferLease(64, 64, 256, new byte[16384]);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sink.WriteAsync(plan.GetSample(0), frame, cancellation.Token).AsTask());
            await sink.AbortAsync(null, Token); await sink.AbortAsync(null, Token);
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(path, Token));
        Assert.Single(Directory.GetFiles(directory));
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    [Fact]
    public void DetailedOptionsRejectContradictionsAndRespectCodecQualityRanges()
    {
        var plan = new OfflineFramePlan(64, 64, new(30), 0, 0);
        Assert.Equal(VideoEncoder.HevcSoftware, new FfmpegOutputOptions("x.mp4").Encoder);
        Assert.Equal(VideoRateControl.ConstantQuality, new FfmpegOutputOptions("x.mp4").RateControl);
        Assert.Equal(VideoSpeed.Balanced, new FfmpegOutputOptions("x.mp4").Speed);
        Assert.Throws<ArgumentException>(() => new FfmpegOutputOptions("x.mp4") { RateControl = VideoRateControl.TargetBitrate }.Validate(plan));
        Assert.Throws<ArgumentException>(() => new FfmpegOutputOptions("x.mp4") { MaximumBitrate = 1000000 }.Validate(plan));
        Assert.Throws<ArgumentException>(() => new FfmpegOutputOptions("x.mp4") { Profile = VideoProfile.H264High }.Validate(plan));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FfmpegOutputOptions("x.mp4") { Quality = 63 }.Validate(plan));
        new FfmpegOutputOptions("x.mp4", VideoEncoder.Av1AomSoftware) { Quality = 63 }.Validate(plan);
        Assert.Throws<ArgumentException>(() => new FfmpegOutputOptions("x.mp4") { Level = VideoLevel.H264Level41 }.Validate(new(4096, 2160, new(60), 0, 0)));
        var args = new FfmpegOutputOptions("x.mp4") { RateControl = VideoRateControl.TargetBitrate, TargetBitrate = 1000000 }.CreateArguments(plan, "temporary");
        Assert.Contains("1000000", args); Assert.DoesNotContain("-crf", args);
    }

    [Theory]
    [InlineData(VideoEncoder.H264Nvenc, 51)]
    [InlineData(VideoEncoder.HevcNvenc, 51)]
    [InlineData(VideoEncoder.Av1Nvenc, 63)]
    public void NvencQualityIsExplicitAndUsesEachCodecsDomain(VideoEncoder encoder, int maximum)
    {
        var plan = new OfflineFramePlan(192, 192, new(30), 0, 0);
        var options = new FfmpegOutputOptions("x.mp4", encoder);
        Assert.Throws<ArgumentOutOfRangeException>(() => (options with { Quality = 0 }).Validate(plan));
        Assert.Throws<ArgumentOutOfRangeException>(() => (options with { Quality = maximum + 1 }).Validate(plan));
        (options with { Quality = 1 }).Validate(plan);
        var args = (options with { Quality = maximum }).CreateArguments(plan, "temporary");
        Assert.Equal(maximum.ToString(System.Globalization.CultureInfo.InvariantCulture), args[args.ToList().IndexOf("-cq") + 1]);
        foreach (int unusedQuality in new[] { 0, -1, maximum + 1 })
        {
            var bitrateArgs = (options with { Quality = unusedQuality, RateControl = VideoRateControl.TargetBitrate,
                TargetBitrate = 1000000 }).CreateArguments(plan, "temporary");
            Assert.DoesNotContain("-cq", bitrateArgs);
            Assert.Contains("1000000", bitrateArgs);
        }
        new FfmpegOutputOptions("x.mp4", VideoEncoder.H264Software) { Quality = 0 }.Validate(plan);
        new FfmpegOutputOptions("x.mp4", VideoEncoder.HevcSoftware) { Quality = 0 }.Validate(plan);
    }

    [Fact]
    public async Task DefaultOptionsReallyEncodeHevcConstantQualityAtBalancedSpeed()
    {
        var tools = Tools(); string path = Path.Combine(ArtifactDirectory(), "default-hevc-balanced.mp4");
        var options = new FfmpegOutputOptions(path);
        var plan = new OfflineFramePlan(64, 64, new(30000, 1001), 0, 2);
        var args = options.CreateArguments(plan, path);
        Assert.Contains("libx265", args); Assert.Contains("-crf", args); Assert.Contains("medium", args);
        Assert.DoesNotContain("-b:v", args);
        await using var sink = new FfmpegFrameSink(tools, options);
        await sink.BeginAsync(new(plan), Token);
        for (long i = 0; i < plan.FrameCount; i++)
        {
            byte[] pixels = new byte[64 * 64 * 4];
            for (int p = 0; p < pixels.Length; p += 4) { pixels[p] = (byte)(p % 256); pixels[p + 1] = (byte)(i * 80); pixels[p + 3] = 255; }
            using var frame = new FrameBufferLease(64, 64, 256, pixels);
            await sink.WriteAsync(plan.GetSample(i), frame, Token);
        }
        await sink.CompleteAsync(Token);
        Assert.Equal("hevc", sink.Validation!.Codec); Assert.True(sink.Validation.Decoded);
        Assert.Equal(plan.FrameCount, sink.Validation.Frames);
        await File.WriteAllTextAsync(path + ".json", JsonSerializer.Serialize(new { Options = options,
            Validation = sink.Validation, Arguments = args }, JsonOptions), Token);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LockedPartialPreservesCancellationOrIncompleteFailureAndDisposesProcess(bool cancelWrite)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows sharing violations are the regression boundary.");
        var tools = Tools(); string directory = Path.Combine(ArtifactDirectory(), "locked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, "existing.avi");
        byte[] original = [11, 22, 33]; await File.WriteAllBytesAsync(path, original, Token);
        var plan = new OfflineFramePlan(64, 64, new(30), 0, 1);
        var sink = new FfmpegFrameSink(tools, new(path, VideoEncoder.RawVideo));
        await sink.BeginAsync(new(plan), Token);
        // Inspect only to inject a deterministic Windows deletion fault before FFmpeg creates its output.
        string partial = PrivateField<string>(sink, "temporary");
        var encoderProcess = PrivateField<Process>(PrivateField<object>(sink, "process"), "process");
        var handle = encoderProcess.SafeHandle;
        try
        {
            using (var fileLock = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                Exception failure;
                if (cancelWrite)
                {
                    using var frame = new FrameBufferLease(64, 64, 256, new byte[16384]);
                    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token); cancellation.Cancel();
                    var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sink.WriteAsync(plan.GetSample(0), frame, cancellation.Token).AsTask());
                    Assert.True(cancelled.CancellationToken.IsCancellationRequested); failure = cancelled;
                }
                else
                {
                    var incomplete = await Assert.ThrowsAsync<InvalidOperationException>(() => sink.CompleteAsync(Token).AsTask());
                    Assert.Contains("Expected 2 frames; received 0", incomplete.Message); failure = incomplete;
                }
                Assert.IsType<string>(failure.Data["FfmpegDiagnostic"]);
                Assert.Contains(Assert.IsAssignableFrom<IReadOnlyList<Exception>>(failure.Data["FfmpegCleanupErrors"]), error => error is IOException);
                await Assert.ThrowsAnyAsync<IOException>(() => sink.DisposeAsync().AsTask());
                Assert.True(handle.IsClosed); // Deletion failure must never skip disposing the native process handle.
                Assert.True(File.Exists(partial));
            }
            await sink.DisposeAsync(); // Releasing the lock must allow retrying cleanup after process disposal.
            Assert.False(File.Exists(partial));
            Assert.Equal(original, await File.ReadAllBytesAsync(path, Token));
        }
        finally { await sink.DisposeAsync(); }
    }

    [Fact]
    public async Task LockedPartialDoesNotMaskEncoderPipeErrorOrItsStderr()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows sharing violations are the regression boundary.");
        var tools = Tools(); string directory = Path.Combine(ArtifactDirectory(), "locked-encoder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // ffprobe rejects the encoder arguments and deterministically exits with stderr, independent of GPU availability.
        var rejectingTools = new FfmpegToolchain(tools.FfprobePath, tools.FfprobePath);
        var plan = new OfflineFramePlan(64, 64, new(30), 0, 0);
        var sink = new FfmpegFrameSink(rejectingTools, new(Path.Combine(directory, "output.avi"), VideoEncoder.RawVideo));
        await sink.BeginAsync(new(plan), Token);
        string partial = PrivateField<string>(sink, "temporary");
        var encoderProcess = PrivateField<Process>(PrivateField<object>(sink, "process"), "process");
        await encoderProcess.WaitForExitAsync(Token);
        try
        {
            using (var fileLock = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                using var frame = new FrameBufferLease(64, 64, 256, new byte[16384]);
                var failure = await Assert.ThrowsAsync<FfmpegException>(() => sink.WriteAsync(plan.GetSample(0), frame, Token).AsTask());
                Assert.IsAssignableFrom<IOException>(failure.InnerException);
                Assert.False(string.IsNullOrWhiteSpace(failure.Diagnostic));
                Assert.Contains(failure.Diagnostic, failure.Message);
                Assert.Contains(Assert.IsAssignableFrom<IReadOnlyList<Exception>>(failure.Data["FfmpegCleanupErrors"]), error => error is IOException);
            }
            await sink.DisposeAsync(); Assert.False(File.Exists(partial));
        }
        finally { await sink.DisposeAsync(); }
    }

    private static T PrivateField<T>(object instance, string name) => (T)instance.GetType()
        .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(instance)!;

    [Fact]
    public async Task CancellationDuringActivePipeWriteRetainsCancellationAndExistingDestination()
    {
        var tools = Tools(); string directory = Path.Combine(ArtifactDirectory(), "active-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, "existing.avi");
        byte[] original = [44, 55, 66]; await File.WriteAllBytesAsync(path, original, Token);
        var plan = new OfflineFramePlan(2048, 2048, new(30), 0, 0);
        using var frame = new FrameBufferLease(2048, 2048, 8192, new byte[2048 * 2048 * 4]);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await using var sink = new FfmpegFrameSink(tools, new(path, VideoEncoder.RawVideo));
            await sink.BeginAsync(new(plan), Token);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(2));
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sink.WriteAsync(plan.GetSample(0), frame, cancellation.Token).AsTask());
            Assert.True(cancellation.IsCancellationRequested);
            Assert.True(failure.CancellationToken.IsCancellationRequested);
            Assert.IsType<string>(failure.Data["FfmpegDiagnostic"]);
            // Either the pipe honors cancellation directly, or the kill races it and the IO cause remains attached.
            if (failure.InnerException is not null) Assert.IsAssignableFrom<IOException>(failure.InnerException);
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(path, Token));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData(VideoEncoder.H264Software, VideoProfile.H264High, VideoSpeed.Balanced, true)]
    [InlineData(VideoEncoder.HevcSoftware, VideoProfile.HevcMain, VideoSpeed.Slow, true)]
    [InlineData(VideoEncoder.Av1AomSoftware, VideoProfile.Av1Main, VideoSpeed.Balanced, true)]
    [InlineData(VideoEncoder.H264Software, VideoProfile.H264Main, VideoSpeed.Slow, false)]
    public async Task DetailedParametersExecuteAndProfileLevelAreVerified(VideoEncoder encoder, VideoProfile profile, VideoSpeed speed, bool bitrate)
    {
        var tools = Tools(); string path = Path.Combine(ArtifactDirectory(), $"detailed-{encoder}-{bitrate}.mp4");
        var options = new FfmpegOutputOptions(path, encoder)
        {
            RateControl = bitrate ? VideoRateControl.TargetBitrate : VideoRateControl.ConstantQuality,
            TargetBitrate = bitrate ? 500000 : null, MaximumBitrate = 1000000, BufferSize = 2000000,
            KeyframeInterval = 2, Threads = 2, Profile = profile, Speed = speed,
            Level = encoder == VideoEncoder.H264Software ? VideoLevel.H264Level41 : VideoLevel.Automatic
        };
        var plan = new OfflineFramePlan(64, 64, new(30000, 1001), 0, 4);
        await using var sink = new FfmpegFrameSink(tools, options);
        await sink.BeginAsync(new(plan), Token);
        byte[] pixels = new byte[64 * 64 * 4];
        for (long i = 0; i < plan.FrameCount; i++)
        {
            for (int p = 0; p < pixels.Length; p += 4) { pixels[p] = (byte)(p % 256); pixels[p + 1] = (byte)(i * 50); pixels[p + 3] = 255; }
            using var frame = new FrameBufferLease(64, 64, 256, pixels);
            await sink.WriteAsync(plan.GetSample(i), frame, Token);
        }
        await sink.CompleteAsync(Token);
        Assert.Equal(profile == VideoProfile.H264High ? "High" : "Main", sink.Validation!.Profile);
        Assert.True(sink.Validation.Decoded);
        var evidence = new { Options = options, Validation = sink.Validation, Arguments = options.CreateArguments(plan, path) };
        await File.WriteAllTextAsync(path + ".json", JsonSerializer.Serialize(evidence, JsonOptions), Token);
    }

    private static FfmpegToolchain Tools()
    {
        string? ffmpeg = Environment.GetEnvironmentVariable("MMD_FFMPEG_PATH"), probe = Environment.GetEnvironmentVariable("MMD_FFPROBE_PATH");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(ffmpeg) || string.IsNullOrWhiteSpace(probe), "Set MMD_FFMPEG_PATH and MMD_FFPROBE_PATH to execute real codec tests.");
        return new(ffmpeg!, probe!);
    }
    private static string ArtifactDirectory()
    {
        string directory = Environment.GetEnvironmentVariable("MMD_FFMPEG_ARTIFACTS") ?? Path.Combine(Path.GetTempPath(), "mmd-ffmpeg-tests");
        Directory.CreateDirectory(directory); return directory;
    }
    private static async Task<byte[]> Decode(string executable, string path)
    {
        ProcessStartInfo info = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-v", "error", "-i", path, "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!; using var memory = new MemoryStream();
        var error = process.StandardError.ReadToEndAsync(Token);
        await process.StandardOutput.BaseStream.CopyToAsync(memory, Token); await process.WaitForExitAsync(Token);
        Assert.True(process.ExitCode == 0, await error); return memory.ToArray();
    }
}
