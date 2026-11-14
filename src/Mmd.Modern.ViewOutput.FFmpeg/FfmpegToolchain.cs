using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mmd.Modern.Application;

namespace Mmd.Modern.ViewOutput.FFmpeg;

public sealed record VideoValidation(string Codec, string PixelFormat, int Width, int Height,
    long Frames, string FrameRate, double StartSeconds, double DurationSeconds, string TimeBase, bool Decoded,
    string? Profile = null, int? Level = null, AudioValidation? Audio = null);
public sealed record AudioValidation(string Codec, int Channels, int SampleRate, long ExpectedSamples,
    long DecodedSamples, double StartSeconds, double DurationSeconds, string TimeBase, bool Decoded);
public sealed record EncoderExecutionCapability(VideoEncoder Encoder, bool Executed, string? Failure, VideoValidation? Validation);
public sealed record FfmpegToolIdentity(string FfmpegPath, string FfprobePath, string FfmpegSha256, string FfprobeSha256,
    string SourceUrl, string Version, string BuildConfiguration, string License, string Encoders, string Muxers);

/// <summary>External user-selected executable pair. Probing never installs tools or changes PATH.</summary>
public sealed class FfmpegToolchain
{
    public string FfmpegPath { get; }
    public string FfprobePath { get; }
    public FfmpegToolchain(string ffmpegPath, string ffprobePath)
    {
        FfmpegPath = Resolve(ffmpegPath); FfprobePath = Resolve(ffprobePath);
    }
    private static string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string absolute = Path.GetFullPath(path);
        if (!File.Exists(absolute)) throw new FileNotFoundException("An explicit existing FFmpeg tool path is required.", absolute);
        return absolute;
    }
    public async Task<FfmpegToolIdentity> InspectAsync(string sourceUrl, CancellationToken cancellationToken = default)
    {
        string version = await RunTextAsync(FfmpegPath, ["-version"], cancellationToken).ConfigureAwait(false);
        string build = await RunTextAsync(FfmpegPath, ["-buildconf"], cancellationToken).ConfigureAwait(false);
        string license = await RunTextAsync(FfmpegPath, ["-L"], cancellationToken).ConfigureAwait(false);
        string encoders = await RunTextAsync(FfmpegPath, ["-encoders"], cancellationToken).ConfigureAwait(false);
        string muxers = await RunTextAsync(FfmpegPath, ["-muxers"], cancellationToken).ConfigureAwait(false);
        return new(FfmpegPath, FfprobePath, Hash(FfmpegPath), Hash(FfprobePath), sourceUrl, version, build, license, encoders, muxers);
    }
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream));
    }
    internal static async Task<string> RunTextAsync(string executable, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        await using var process = FfmpegProcess.Start(executable, args);
        var result = await process.FinishAsync(cancellationToken).ConfigureAwait(false);
        return result.Output + result.Diagnostic;
    }

    /// <summary>Executes each explicitly requested encoder with a real clip, probes and fully decodes it.
    /// Software uses 64x64; NVENC uses 192x192 to satisfy hardware minimum dimension constraints.
    /// A listed encoder is not considered available until this succeeds. Failures remain distinct per encoder.</summary>
    public async Task<IReadOnlyList<EncoderExecutionCapability>> ProbeEncodersAsync(string directory,
        IEnumerable<VideoEncoder>? encoders = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        List<EncoderExecutionCapability> results = [];
        foreach (VideoEncoder encoder in encoders ?? Enum.GetValues<VideoEncoder>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            int size = encoder is VideoEncoder.H264Nvenc or VideoEncoder.HevcNvenc or VideoEncoder.Av1Nvenc ? 192 : 64;
            var plan = new OfflineFramePlan(size, size, new(30000, 1001), 0, 2);
            string extension = encoder == VideoEncoder.RawVideo ? ".avi" : ".mp4";
            var options = new FfmpegOutputOptions(Path.Combine(directory, encoder + extension), encoder) { Timeout = TimeSpan.FromSeconds(45) };
            try
            {
                await using var sink = new FfmpegFrameSink(this, options);
                await sink.BeginAsync(new(plan), cancellationToken).ConfigureAwait(false);
                byte[] data = new byte[size * size * 4];
                for (long i = 0; i < plan.FrameCount; i++)
                {
                    for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
                    {
                        int p = (y * size + x) * 4; data[p] = (byte)(x * 255 / size); data[p + 1] = (byte)(y * 255 / size);
                        data[p + 2] = (byte)(i * 60); data[p + 3] = 255;
                    }
                    using var frame = new FrameBufferLease(size, size, size * 4, data);
                    await sink.WriteAsync(plan.GetSample(i), frame, cancellationToken).ConfigureAwait(false);
                }
                await sink.CompleteAsync(cancellationToken).ConfigureAwait(false);
                results.Add(new(encoder, true, null, sink.Validation));
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            { results.Add(new(encoder, false, error.ToString(), null)); }
        }
        return results;
    }

    public async Task<VideoValidation> ValidateAsync(string path, OfflineFramePlan plan, FfmpegOutputOptions options,
        CancellationToken cancellationToken = default)
    {
        options.Validate(plan);
        string json = await RunTextAsync(FfprobePath, ["-v", "error", "-count_frames", "-show_streams", "-show_format", "-of", "json", path], cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var videoStreams = streams.Where(item => item.GetProperty("codec_type").GetString() == "video").ToArray();
        var audioStreams = streams.Where(item => item.GetProperty("codec_type").GetString() == "audio").ToArray();
        int expectedAudio = options.AudioInput is null ? 0 : 1;
        if (streams.Length != 1 + expectedAudio || videoStreams.Length != 1 || audioStreams.Length != expectedAudio)
            throw new FfmpegException($"Expected exactly one video stream and {expectedAudio} explicitly requested audio stream(s).", json);
        var stream = videoStreams[0];
        string codec = stream.GetProperty("codec_name").GetString()!;
        string pixel = stream.GetProperty("pix_fmt").GetString()!;
        int width = stream.GetProperty("width").GetInt32(), height = stream.GetProperty("height").GetInt32();
        long frames = long.Parse(stream.GetProperty("nb_read_frames").GetString()!, CultureInfo.InvariantCulture);
        string fps = stream.GetProperty("avg_frame_rate").GetString()!;
        string timeBase = stream.GetProperty("time_base").GetString()!;
        string? profile = stream.TryGetProperty("profile", out var profileValue) ? profileValue.GetString() : null;
        int? level = stream.TryGetProperty("level", out var levelValue) ? levelValue.GetInt32() : null;
        string? requestedProfile = options.Profile switch { VideoProfile.H264High => "High", VideoProfile.Automatic => null, _ => "Main" };
        if ((requestedProfile is not null && profile != requestedProfile) || (options.Level == VideoLevel.H264Level41 && level != 41))
            throw new FfmpegException("Encoded output profile/level differs from the explicit request.", json);
        if (options.Encoder != VideoEncoder.RawVideo &&
            (stream.GetProperty("color_space").GetString() != "bt709" || stream.GetProperty("color_transfer").GetString() != "bt709"
                || stream.GetProperty("color_primaries").GetString() != "bt709" || stream.GetProperty("color_range").GetString() != "tv"))
            throw new FfmpegException("Encoded output does not carry the requested limited-range BT.709 color metadata.", json);
        double start = stream.TryGetProperty("start_time", out var startValue) ? Parse(startValue) : 0;
        double duration = stream.TryGetProperty("duration", out var durationValue) ? Parse(durationValue)
            : Parse(document.RootElement.GetProperty("format").GetProperty("duration"));
        double tolerance = Math.Max(Rational(timeBase), 0.000001) + 0.000001;
        if (codec != options.CodecName || pixel != options.PixelFormat || width != plan.Width || height != plan.Height || frames != plan.FrameCount
            || !SameRational(fps, plan.FrameRate.Numerator, plan.FrameRate.Denominator)
            || Math.Abs(start) > tolerance || Math.Abs(duration - plan.FrameCount * (double)plan.FrameRate.Denominator / plan.FrameRate.Numerator) > tolerance)
            throw new FfmpegException("Encoded output does not match the requested codec, pixels, dimensions, frames or rational timeline.", json);
        await RunTextAsync(FfmpegPath, ["-hide_banner", "-v", "error", "-xerror", "-i", path, "-map", "0:v:0", "-an", "-f", "null", "-"], cancellationToken).ConfigureAwait(false);
        AudioValidation? audio = options.AudioInput is null ? null
            : await ValidateAudioAsync(path, audioStreams[0], document.RootElement.GetProperty("format"), plan, options, json, cancellationToken).ConfigureAwait(false);
        return new(codec, pixel, width, height, frames, fps, start, duration, timeBase, true, profile, level, audio);
    }

    private async Task<AudioValidation> ValidateAudioAsync(string path, JsonElement stream, JsonElement format,
        OfflineFramePlan plan, FfmpegOutputOptions options, string diagnostic, CancellationToken token)
    {
        var input = options.AudioInput!;
        string codec = stream.GetProperty("codec_name").GetString()!;
        int channels = stream.GetProperty("channels").GetInt32();
        int rate = int.Parse(stream.GetProperty("sample_rate").GetString()!, CultureInfo.InvariantCulture);
        string timeBase = stream.GetProperty("time_base").GetString()!;
        double start = stream.TryGetProperty("start_time", out var startValue) ? Parse(startValue) : 0;
        double duration = stream.TryGetProperty("duration", out var durationValue) ? Parse(durationValue)
            : Parse(format.GetProperty("duration"));
        long expected = input.GetRange(plan).OutputSampleCount;
        string expectedCodec = options.Encoder == VideoEncoder.RawVideo ? FfmpegAudioInput.Codec(input.Audio) : "aac";
        double tolerance = Math.Max(Rational(timeBase), 1.0 / input.Audio.SampleRate) + 0.000001;
        if (codec != expectedCodec || channels != input.Audio.Channels || rate != input.Audio.SampleRate
            || !double.IsFinite(start) || !double.IsFinite(duration)
            || Math.Abs(start) > tolerance || Math.Abs(duration - (double)expected / rate) > tolerance)
            throw new FfmpegException("Encoded audio does not match the requested codec, channels, sample rate or selected source duration.", diagnostic);

        // Drain binary output with bounded memory, preserving channel count and rate. This executes the decoder;
        // ffprobe stream presence alone is insufficient to accept a mux. AAC may expose its final 1024-sample
        // coded frame's zero padding after decoding; the stream duration above must still end at the requested sample.
        await using var decoder = FfmpegProcess.Start(FfmpegPath,
            ["-hide_banner", "-v", "error", "-xerror", "-i", path, "-map", "0:a:0", "-vn", "-c:a", "pcm_s32le", "-f", "s32le", "pipe:1"], countBinaryOutput: true);
        await decoder.FinishAsync(token).ConfigureAwait(false);
        long bytes = await decoder.OutputByteCountAsync().ConfigureAwait(false);
        long block = checked(channels * 4L);
        long decoded = bytes / block;
        long padding = options.Encoder == VideoEncoder.RawVideo ? 0 : 1023;
        if (bytes % block != 0 || decoded < expected || decoded > expected + padding)
            throw new FfmpegException("Decoded audio sample count does not match the requested finite sample interval.", diagnostic);
        return new(codec, channels, rate, expected, decoded, start, duration, timeBase, true);
    }
    private static double Parse(JsonElement value) => double.Parse(value.GetString()!, CultureInfo.InvariantCulture);
    private static double Rational(string value)
    { var parts = value.Split('/'); return double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture); }
    private static bool SameRational(string value, int numerator, int denominator)
    {
        var parts = value.Split('/'); return parts.Length == 2 && long.TryParse(parts[0], out long n) && long.TryParse(parts[1], out long d)
            && d > 0 && n * denominator == (long)numerator * d;
    }
}
