using System.Globalization;
using Mmd.Modern.Application;

namespace Mmd.Modern.ViewOutput.FFmpeg;

public enum VideoEncoder { RawVideo, H264Software, HevcSoftware, Av1AomSoftware, Av1SvtSoftware, H264Nvenc, HevcNvenc, Av1Nvenc }
public enum VideoRateControl { ConstantQuality, TargetBitrate }
public enum VideoSpeed { Fast, Balanced, Slow }
public enum VideoProfile { Automatic, H264Main, H264High, HevcMain, Av1Main }
public enum VideoLevel { Automatic, H264Level41 }

/// <summary>Explicit encoder selection. No encoder or codec fallback is performed. Audio is opt-in frozen PCM WAV.</summary>
public sealed record FfmpegOutputOptions(string OutputPath, VideoEncoder Encoder = VideoEncoder.HevcSoftware)
{
    public byte BackgroundRed { get; init; }
    public byte BackgroundGreen { get; init; }
    public byte BackgroundBlue { get; init; }
    public int Quality { get; init; } = 23;
    public VideoRateControl RateControl { get; init; } = VideoRateControl.ConstantQuality;
    public VideoSpeed Speed { get; init; } = VideoSpeed.Balanced;
    public long? TargetBitrate { get; init; }
    public long? MaximumBitrate { get; init; }
    public long? BufferSize { get; init; }
    public int? KeyframeInterval { get; init; }
    /// <summary>Software encoding worker limit. NVENC's dedicated hardware scheduling is not controlled by this value.</summary>
    public int Threads { get; init; } = 2;
    public VideoProfile Profile { get; init; }
    public VideoLevel Level { get; init; }
    /// <summary>One enabled frozen PCM track, trimmed to the same selected source interval as video.
    /// A null value preserves video-only output. PNG outputs do not consume this option.</summary>
    public SceneAudioExportInput? AudioInput { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
    public string EncoderName => Encoder switch
    {
        VideoEncoder.RawVideo => "rawvideo", VideoEncoder.H264Software => "libx264",
        VideoEncoder.HevcSoftware => "libx265", VideoEncoder.Av1AomSoftware => "libaom-av1",
        VideoEncoder.Av1SvtSoftware => "libsvtav1", VideoEncoder.H264Nvenc => "h264_nvenc",
        VideoEncoder.HevcNvenc => "hevc_nvenc", VideoEncoder.Av1Nvenc => "av1_nvenc",
        _ => throw new ArgumentOutOfRangeException(nameof(Encoder))
    };
    public string CodecName => Encoder switch
    {
        VideoEncoder.RawVideo => "rawvideo", VideoEncoder.H264Software or VideoEncoder.H264Nvenc => "h264",
        VideoEncoder.HevcSoftware or VideoEncoder.HevcNvenc => "hevc", _ => "av1"
    };
    public string Container => Encoder == VideoEncoder.RawVideo ? "avi" : "mp4";
    public string PixelFormat => Encoder == VideoEncoder.RawVideo ? "bgr24" : "yuv420p";

    public void Validate(OfflineFramePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(OutputPath);
        _ = EncoderName;
        if (RateControl == VideoRateControl.ConstantQuality)
        {
            int minimumQuality = Encoder is VideoEncoder.H264Nvenc or VideoEncoder.HevcNvenc or VideoEncoder.Av1Nvenc ? 1 : 0;
            int maximumQuality = Encoder is VideoEncoder.Av1AomSoftware or VideoEncoder.Av1SvtSoftware or VideoEncoder.Av1Nvenc ? 63 : 51;
            if (Quality < minimumQuality || Quality > maximumQuality)
                throw new ArgumentOutOfRangeException(nameof(Quality), $"{EncoderName} explicit constant quality must be {minimumQuality}..{maximumQuality}; NVENC quality 0 means automatic.");
        }
        if (!Enum.IsDefined(RateControl) || !Enum.IsDefined(Speed) || !Enum.IsDefined(Profile) || !Enum.IsDefined(Level))
            throw new ArgumentException("Unknown encoding option.");
        if (Threads is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(Threads));
        if (KeyframeInterval is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(KeyframeInterval));
        foreach (long? bits in new[] { TargetBitrate, MaximumBitrate, BufferSize })
            if (bits is < 1000 or > 2_000_000_000) throw new ArgumentOutOfRangeException(nameof(TargetBitrate), "Bitrate/buffer values are positive bits, 1000..2,000,000,000.");
        if ((MaximumBitrate is null) != (BufferSize is null)) throw new ArgumentException("Maximum bitrate and buffer size must be supplied together.");
        if ((RateControl == VideoRateControl.TargetBitrate) != (TargetBitrate is not null))
            throw new ArgumentException("Target bitrate mode requires a target; constant quality must not supply a target.");
        if (TargetBitrate > MaximumBitrate) throw new ArgumentException("Target bitrate must not exceed maximum bitrate.");
        if (Encoder == VideoEncoder.RawVideo && (RateControl != VideoRateControl.ConstantQuality || MaximumBitrate is not null
            || KeyframeInterval is not null || Profile != VideoProfile.Automatic || Level != VideoLevel.Automatic))
            throw new ArgumentException("Uncompressed rawvideo does not accept compressed-video rate, GOP, profile or level settings.");
        if (Profile != VideoProfile.Automatic && (Profile switch
            {
                VideoProfile.H264Main or VideoProfile.H264High => CodecName != "h264",
                VideoProfile.HevcMain => CodecName != "hevc",
                VideoProfile.Av1Main => CodecName != "av1",
                _ => true
            })) throw new ArgumentException("The selected profile is incompatible with the codec.");
        if (Profile != VideoProfile.Automatic && Encoder is not (VideoEncoder.H264Software or VideoEncoder.HevcSoftware or VideoEncoder.Av1AomSoftware))
            throw new NotSupportedException("Explicit profiles are verified only for libx264, libx265 and libaom-av1; select automatic profile for this encoder.");
        if (Profile is VideoProfile.H264Main or VideoProfile.H264High && RateControl == VideoRateControl.ConstantQuality && Quality == 0)
            throw new ArgumentException("The selected H.264 profile does not support the requested lossless quality.");
        if (Level == VideoLevel.H264Level41)
        {
            long blocks = ((plan.Width + 15L) / 16) * ((plan.Height + 15L) / 16);
            if (Encoder != VideoEncoder.H264Software || blocks > 8192 || (plan.Width + 15) / 16 > 256 || (plan.Height + 15) / 16 > 256
                || blocks * plan.FrameRate.Numerator > 245760L * plan.FrameRate.Denominator
                || TargetBitrate > 50_000_000 || MaximumBitrate > 50_000_000)
                throw new ArgumentException("H.264 level 4.1 limits dimensions, macroblocks/second and bitrate; select automatic level or compatible output.");
        }
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(Timeout));
        if (Encoder != VideoEncoder.RawVideo && ((plan.Width & 1) != 0 || (plan.Height & 1) != 0))
            throw new ArgumentException("YUV 4:2:0 requires even width and height. Select even dimensions explicitly; no cropping or padding is applied.");
        if (!string.Equals(Path.GetExtension(OutputPath), "." + Container, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{EncoderName} requires an explicit .{Container} output path.", nameof(OutputPath));
        if (AudioInput is not null) FfmpegAudioInput.Validate(AudioInput, plan, Encoder == VideoEncoder.RawVideo);
    }

    public IReadOnlyList<string> CreateArguments(OfflineFramePlan plan, string temporaryOutput, string? preparedAudioPath = null)
    {
        Validate(plan);
        List<string> args = ["-hide_banner", "-nostdin", "-v", "warning", "-y", "-f", "rawvideo", "-pixel_format", "rgba",
            "-video_size", $"{plan.Width}x{plan.Height}", "-framerate", FormattableString.Invariant($"{plan.FrameRate.Numerator}/{plan.FrameRate.Denominator}"),
            "-i", "pipe:0"];
        if (AudioInput is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(preparedAudioPath);
            FfmpegAudioInput.AddInputArguments(args, AudioInput, plan, preparedAudioPath);
        }
        args.AddRange(["-map", "0:v:0"]);
        if (AudioInput is null) args.Add("-an");
        else
        {
            args.AddRange(["-map", "[exportaudio]", "-c:a", Encoder == VideoEncoder.RawVideo ? FfmpegAudioInput.Codec(AudioInput.Audio) : "aac"]);
            if (Encoder != VideoEncoder.RawVideo)
            {
                // Native AAC codes 1024 samples with at most 6144 bits/channel. Avoid asking the
                // encoder to silently clamp its bitrate for low-rate WAV sources.
                long bitrate = Math.Min(192000L, AudioInput.Audio.SampleRate * 6L * AudioInput.Audio.Channels);
                args.AddRange(["-b:a", bitrate.ToString(CultureInfo.InvariantCulture)]);
            }
        }
        args.AddRange(["-c:v", EncoderName, "-pix_fmt", PixelFormat]);
        if (Encoder != VideoEncoder.RawVideo)
        {
            // Input bytes are full-range sRGB. Convert the transfer and YUV matrix explicitly to limited BT.709.
            args.AddRange(["-vf", "zscale=matrixin=gbr:transferin=iec61966-2-1:primariesin=bt709:rangein=full:matrix=bt709:transfer=bt709:primaries=bt709:range=limited,format=yuv420p",
                "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-color_range", "tv"]);
            string quality = Quality.ToString(CultureInfo.InvariantCulture);
            string threads = Threads.ToString(CultureInfo.InvariantCulture);
            if (Encoder is VideoEncoder.H264Nvenc or VideoEncoder.HevcNvenc or VideoEncoder.Av1Nvenc)
            {
                args.AddRange(["-preset", Speed switch { VideoSpeed.Fast => "p4", VideoSpeed.Balanced => "p5", _ => "p6" }, "-rc", "vbr"]);
                if (RateControl == VideoRateControl.ConstantQuality) args.AddRange(["-cq", quality, "-b:v", "0"]);
            }
            else
            {
                if (RateControl == VideoRateControl.ConstantQuality) args.AddRange(["-crf", quality]);
                if (Encoder == VideoEncoder.Av1AomSoftware)
                {
                    args.AddRange(["-cpu-used", Speed switch { VideoSpeed.Fast => "8", VideoSpeed.Balanced => "6", _ => "4" }]);
                    if (RateControl == VideoRateControl.ConstantQuality) args.AddRange(["-b:v", "0"]);
                }
                else if (Encoder == VideoEncoder.Av1SvtSoftware) args.AddRange(["-preset", Speed switch { VideoSpeed.Fast => "10", VideoSpeed.Balanced => "8", _ => "6" }, "-svtav1-params", "lp=" + threads]);
                else args.AddRange(["-preset", Speed switch { VideoSpeed.Fast => "fast", VideoSpeed.Balanced => "medium", _ => "slow" }]);
                args.AddRange(["-threads", threads]);
                if (Encoder == VideoEncoder.HevcSoftware) args.AddRange(["-x265-params", "pools=" + threads + ":frame-threads=1:log-level=error"]);
            }
            if (RateControl == VideoRateControl.TargetBitrate) args.AddRange(["-b:v", TargetBitrate!.Value.ToString(CultureInfo.InvariantCulture)]);
            if (MaximumBitrate is not null) args.AddRange(["-maxrate", MaximumBitrate.Value.ToString(CultureInfo.InvariantCulture), "-bufsize", BufferSize!.Value.ToString(CultureInfo.InvariantCulture)]);
            if (KeyframeInterval is not null) args.AddRange(["-g", KeyframeInterval.Value.ToString(CultureInfo.InvariantCulture)]);
            if (Profile != VideoProfile.Automatic) args.AddRange(["-profile:v", Profile switch
                { VideoProfile.H264High => "high", VideoProfile.Av1Main when Encoder == VideoEncoder.Av1AomSoftware => "0", _ => "main" }]);
            if (Level == VideoLevel.H264Level41) args.AddRange(["-level:v", "4.1"]);
            if (CodecName == "hevc") args.AddRange(["-tag:v", "hvc1"]);
            args.AddRange(["-movflags", "+faststart"]);
        }
        args.AddRange(["-f", Container, temporaryOutput]);
        return args;
    }
}
