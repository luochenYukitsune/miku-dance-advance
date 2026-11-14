using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Application;

namespace Mmd.Modern.ViewOutput.FFmpeg;

/// <summary>Portable, explicit encoding parameters. Output and executable paths and runtime timeouts belong to the caller.</summary>
public static class FfmpegEncodingPreset
{
    public const string ProviderId = "ffmpeg";
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        MaxDepth = 4
    };

    /// <summary>Snapshots only portable parameters. Their compatibility with an actual output plan is checked by ToOptions.</summary>
    public static ViewOutputEncodingPreset FromOptions(FfmpegOutputOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var data = new EncodingData
        {
            Encoder = Name(options.Encoder), RateControl = Name(options.RateControl), Quality = options.Quality,
            Speed = Name(options.Speed), TargetBitrate = options.TargetBitrate, MaximumBitrate = options.MaximumBitrate,
            BufferSize = options.BufferSize, KeyframeInterval = options.KeyframeInterval, Profile = Name(options.Profile),
            Level = Name(options.Level), BackgroundRed = options.BackgroundRed, BackgroundGreen = options.BackgroundGreen,
            BackgroundBlue = options.BackgroundBlue, Threads = options.Threads
        };
        return new(ProviderId, SchemaVersion, JsonSerializer.Serialize(data, JsonOptions));
    }

    /// <summary>Binds the caller's destination and validates the restored settings using the existing encoder contract.
    /// Parsing never discovers or runs an encoder. Unsupported providers, schemas and parameters fail explicitly.</summary>
    public static FfmpegOutputOptions ToOptions(ViewOutputEncodingPreset preset, string outputPath, OfflineFramePlan plan)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.Equals(preset.ProviderId, ProviderId, StringComparison.Ordinal))
            throw new NotSupportedException($"Encoding provider '{preset.ProviderId}' is not supported by the FFmpeg adapter.");
        if (preset.SchemaVersion != SchemaVersion)
            throw new NotSupportedException($"FFmpeg encoding preset schema {preset.SchemaVersion} is not supported.");
        var data = JsonSerializer.Deserialize<EncodingData>(preset.DataJson, JsonOptions)
            ?? throw new JsonException("FFmpeg encoding data must be an object.");
        var options = new FfmpegOutputOptions(outputPath, Parse<VideoEncoder>(data.Encoder, "encoder"))
        {
            RateControl = Parse<VideoRateControl>(data.RateControl, "rateControl"), Quality = data.Quality,
            Speed = Parse<VideoSpeed>(data.Speed, "speed"), TargetBitrate = data.TargetBitrate,
            MaximumBitrate = data.MaximumBitrate, BufferSize = data.BufferSize, KeyframeInterval = data.KeyframeInterval,
            Profile = Parse<VideoProfile>(data.Profile, "profile"), Level = Parse<VideoLevel>(data.Level, "level"),
            BackgroundRed = data.BackgroundRed, BackgroundGreen = data.BackgroundGreen, BackgroundBlue = data.BackgroundBlue,
            Threads = data.Threads
        };
        options.Validate(plan);
        return options;
    }

    private static string Name<T>(T value) where T : struct, Enum => Enum.IsDefined(value)
        ? value.ToString() : throw new ArgumentOutOfRangeException(nameof(value), "Unknown encoding parameter.");

    private static T Parse<T>(string name, string field) where T : struct, Enum
    {
        if (!Enum.TryParse<T>(name, out var value) || !Enum.IsDefined(value)
            || !string.Equals(name, value.ToString(), StringComparison.Ordinal))
            throw new JsonException($"FFmpeg encoding field '{field}' must contain a supported enum name.");
        return value;
    }

    // Required includes nullable fields: omission is a malformed preset, never an implicit default.
    private sealed class EncodingData
    {
        public required string Encoder { get; init; }
        public required string RateControl { get; init; }
        public required int Quality { get; init; }
        public required string Speed { get; init; }
        public required long? TargetBitrate { get; init; }
        public required long? MaximumBitrate { get; init; }
        public required long? BufferSize { get; init; }
        public required int? KeyframeInterval { get; init; }
        public required string Profile { get; init; }
        public required string Level { get; init; }
        public required byte BackgroundRed { get; init; }
        public required byte BackgroundGreen { get; init; }
        public required byte BackgroundBlue { get; init; }
        public required int Threads { get; init; }
    }
}
