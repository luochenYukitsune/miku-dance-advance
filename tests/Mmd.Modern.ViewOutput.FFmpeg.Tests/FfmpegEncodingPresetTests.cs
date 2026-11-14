using System.Text.Json;
using System.Text.Json.Nodes;
using Mmd.Modern.Application;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.FFmpeg;
using Xunit;

namespace Mmd.Modern.ViewOutput.FFmpeg.Tests;

public sealed class FfmpegEncodingPresetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly OfflineFramePlan Plan = new(192, 192, new(30000, 1001), 0, 2);

    [Theory]
    [InlineData(VideoEncoder.RawVideo)]
    [InlineData(VideoEncoder.H264Software)]
    [InlineData(VideoEncoder.HevcSoftware)]
    [InlineData(VideoEncoder.Av1AomSoftware)]
    [InlineData(VideoEncoder.Av1SvtSoftware)]
    [InlineData(VideoEncoder.H264Nvenc)]
    [InlineData(VideoEncoder.HevcNvenc)]
    [InlineData(VideoEncoder.Av1Nvenc)]
    public void EveryExplicitEncoderAndQualitySettingSurvivesNeutralEnvelope(VideoEncoder encoder)
    {
        AssertRoundtrip(DetailedOptions(encoder));
    }

    [Theory]
    [InlineData(VideoEncoder.H264Software)]
    [InlineData(VideoEncoder.HevcSoftware)]
    [InlineData(VideoEncoder.Av1AomSoftware)]
    [InlineData(VideoEncoder.Av1SvtSoftware)]
    [InlineData(VideoEncoder.H264Nvenc)]
    [InlineData(VideoEncoder.HevcNvenc)]
    [InlineData(VideoEncoder.Av1Nvenc)]
    public void ExplicitTargetBitratePreservesAllDetailedOptionsAndExistingArguments(VideoEncoder encoder)
    {
        AssertRoundtrip(DetailedOptions(encoder) with
        {
            RateControl = VideoRateControl.TargetBitrate, TargetBitrate = 1_000_000,
            // The existing target bitrate mode ignores quality; saving must still preserve the user's value.
            Quality = -7
        });
    }

    [Fact]
    public void DefaultHevcQualityAndSpeedRemainExplicitWhileRuntimeStateIsExcluded()
    {
        var original = new FfmpegOutputOptions("original-machine/output.mp4") { Timeout = TimeSpan.FromSeconds(17) };
        var preset = FfmpegEncodingPreset.FromOptions(original);
        var restored = FfmpegEncodingPreset.ToOptions(preset, "new-machine/output.mp4", Plan);
        Assert.Equal("ffmpeg", preset.ProviderId); Assert.Equal(1, preset.SchemaVersion);
        Assert.Equal(VideoEncoder.HevcSoftware, restored.Encoder);
        Assert.Equal(VideoRateControl.ConstantQuality, restored.RateControl);
        Assert.Equal(VideoSpeed.Balanced, restored.Speed); Assert.Equal(23, restored.Quality);
        Assert.Equal(TimeSpan.FromMinutes(2), restored.Timeout);
        using var json = JsonDocument.Parse(preset.DataJson);
        Assert.Equal(new[] { "backgroundBlue", "backgroundGreen", "backgroundRed", "bufferSize", "encoder", "keyframeInterval",
            "level", "maximumBitrate", "profile", "quality", "rateControl", "speed", "targetBitrate", "threads" },
            json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("original-machine", preset.DataJson);
        Assert.DoesNotContain("timeout", preset.DataJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CallerBindsExactOutputPathAndActualPlanWithoutCreatingFilesOrInspectingTools()
    {
        string directory = Path.Combine(Path.GetTempPath(), "not-created-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "chosen ; & output.mp4");
        var preset = FfmpegEncodingPreset.FromOptions(new("not-carried.mp4"));
        Assert.Equal(path, FfmpegEncodingPreset.ToOptions(preset, path, Plan).OutputPath);
        Assert.False(Directory.Exists(directory));
        Assert.Throws<ArgumentException>(() => FfmpegEncodingPreset.ToOptions(preset, "wrong.avi", Plan));
        Assert.Throws<ArgumentException>(() => FfmpegEncodingPreset.ToOptions(preset, "odd.mp4", new(65, 64, new(30), 0, 0)));
        Assert.Throws<ArgumentException>(() => FfmpegEncodingPreset.ToOptions(preset, " ", Plan));
        var limited = FfmpegEncodingPreset.FromOptions(DetailedOptions(VideoEncoder.H264Software));
        Assert.Throws<ArgumentException>(() => FfmpegEncodingPreset.ToOptions(limited, "large.mp4", new(4096, 2160, new(60), 0, 0)));
    }

    [Fact]
    public void UnknownProvidersAndSchemasAreRejectedWithoutFallback()
    {
        string data = FfmpegEncodingPreset.FromOptions(new("source.mp4")).DataJson;
        Assert.Throws<NotSupportedException>(() => FfmpegEncodingPreset.ToOptions(new("other-provider", 1, data), "out.mp4", Plan));
        Assert.Throws<NotSupportedException>(() => FfmpegEncodingPreset.ToOptions(new("ffmpeg", 2, data), "out.mp4", Plan));
    }

    [Theory]
    [InlineData("outputPath", "\"untrusted.mp4\"")]
    [InlineData("ffmpegPath", "\"untrusted.exe\"")]
    [InlineData("ffprobePath", "\"untrusted.exe\"")]
    [InlineData("timeout", "60")]
    [InlineData("encoderName", "\"libx264\"")]
    [InlineData("codecName", "\"h264\"")]
    [InlineData("container", "\"avi\"")]
    [InlineData("pixelFormat", "\"rgb24\"")]
    [InlineData("unknown", "true")]
    [InlineData("Encoder", "\"HevcSoftware\"")]
    public void RuntimeComputedAndUnknownFieldsCannotBeSmuggledIntoPortableData(string field, string value)
    {
        Assert.Throws<JsonException>(() => FfmpegEncodingPreset.ToOptions(Changed(field, value), "out.mp4", Plan));
    }

    [Theory]
    [InlineData("encoder", "\"UnavailableEncoder\"")]
    [InlineData("encoder", "2")]
    [InlineData("encoder", "\"2\"")]
    [InlineData("encoder", "null")]
    [InlineData("rateControl", "\"Automatic\"")]
    [InlineData("rateControl", "1")]
    [InlineData("speed", "\"Turbo\"")]
    [InlineData("profile", "\"Unknown\"")]
    [InlineData("level", "\"Unknown\"")]
    [InlineData("quality", "23.5")]
    [InlineData("quality", "\"NaN\"")]
    [InlineData("quality", "\"23\"")]
    [InlineData("quality", "null")]
    [InlineData("threads", "true")]
    [InlineData("backgroundRed", "256")]
    [InlineData("backgroundGreen", "-1")]
    [InlineData("backgroundBlue", "[]")]
    [InlineData("targetBitrate", "\"1000000\"")]
    [InlineData("bufferSize", "9223372036854775808")]
    [InlineData("keyframeInterval", "{}")]
    public void UnknownEnumsNumericEnumsAndWrongValueTypesAreRejected(string field, string value)
    {
        Assert.Throws<JsonException>(() => FfmpegEncodingPreset.ToOptions(Changed(field, value), "out.mp4", Plan));
    }

    [Fact]
    public void EveryPortableFieldIsRequiredRatherThanReplacedByDefaults()
    {
        var source = JsonNode.Parse(FfmpegEncodingPreset.FromOptions(new("source.mp4")).DataJson)!.AsObject();
        foreach (string field in source.Select(p => p.Key).ToArray())
        {
            var incomplete = source.DeepClone().AsObject(); incomplete.Remove(field);
            Assert.Throws<JsonException>(() => FfmpegEncodingPreset.ToOptions(new("ffmpeg", 1, incomplete.ToJsonString()), "out.mp4", Plan));
        }
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"quality\":23,\"quality\":24}")]
    [InlineData("{\"quality\":NaN}")]
    [InlineData("{\"quality\":Infinity}")]
    public void MalformedDuplicateAndNonfiniteJsonIsRejectedBeforeEncoding(string data)
    {
        // The neutral envelope validates JSON structure before provider-specific parsing can begin.
        Assert.Throws<ArgumentException>(() => FfmpegEncodingPreset.ToOptions(new("ffmpeg", 1, data), "out.mp4", Plan));
    }

    [Theory]
    [InlineData("quality", "-1")]
    [InlineData("threads", "0")]
    [InlineData("keyframeInterval", "0")]
    [InlineData("maximumBitrate", "1000000")]
    [InlineData("targetBitrate", "1000000")]
    [InlineData("profile", "\"H264High\"")]
    [InlineData("level", "\"H264Level41\"")]
    public void ExistingEncoderValidationRejectsContradictoryOrOutOfRangePresets(string field, string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => FfmpegEncodingPreset.ToOptions(Changed(field, value), "out.mp4", Plan));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SavedNeutralPresetEncodesAndProbesRealHevcWithExistingCpuSink()
    {
        string? executable = Environment.GetEnvironmentVariable("MMD_FFMPEG_PATH"), probe = Environment.GetEnvironmentVariable("MMD_FFPROBE_PATH");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(probe),
            "Set MMD_FFMPEG_PATH and MMD_FFPROBE_PATH to execute the saved-preset codec test.");
        var tools = new FfmpegToolchain(executable!, probe!);
        string root = Environment.GetEnvironmentVariable("MMD_FFMPEG_ARTIFACTS") ?? Path.Combine(Path.GetTempPath(), "mmd-ffmpeg-tests");
        string directory = Path.Combine(root, "saved-preset-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "restored HEVC ; & output.mp4");
        var original = new FfmpegOutputOptions("not-saved.mp4") { Profile = VideoProfile.HevcMain, BackgroundBlue = 63 };
        var definition = Definition(FfmpegEncodingPreset.FromOptions(original));
        string libraryPath = Path.Combine(directory, "presets");
        var saved = new ViewOutputPresetStore(libraryPath).SaveAsNew(definition, "Saved HEVC", Token);
        Assert.True(File.Exists(Path.Combine(libraryPath, saved.StableId + ".json")));
        var entry = new ViewOutputPresetStore(libraryPath).Find(saved.StableId, Token);
        Assert.True(entry.IsAvailable, entry.Diagnostic);
        var loaded = Assert.IsType<ViewOutputPresetDefinition>(entry.Definition);
        var request = loaded.Export.CreateRequest(0, 3, 0);
        var options = FfmpegEncodingPreset.ToOptions(loaded.Encoding!, path, request.Plan);
        await using var sink = new FfmpegFrameSink(tools, options);
        await sink.BeginAsync(request, Token);
        byte[] pixels = new byte[64 * 64 * 4];
        for (long index = 0; index < request.Plan.FrameCount; index++)
        {
            for (int p = 0; p < pixels.Length; p += 4)
            { pixels[p] = (byte)(p % 256); pixels[p + 1] = (byte)(index * 50); pixels[p + 3] = 255; }
            using var frame = new FrameBufferLease(64, 64, 256, pixels);
            await sink.WriteAsync(request.Plan.GetSample(index), frame, Token);
        }
        await sink.CompleteAsync(Token);
        Assert.True(File.Exists(path)); Assert.Empty(Directory.GetFiles(directory, "*.partial"));
        var validation = Assert.IsType<VideoValidation>(sink.Validation);
        Assert.Equal("hevc", validation.Codec); Assert.Equal("Main", validation.Profile);
        Assert.Equal("30000/1001", validation.FrameRate); Assert.Equal(3, validation.Frames);
        Assert.Equal(64, validation.Width); Assert.Equal(64, validation.Height); Assert.True(validation.Decoded);
    }

    private static void AssertRoundtrip(FfmpegOutputOptions original)
    {
        var preset = FfmpegEncodingPreset.FromOptions(original);
        var loaded = ViewOutputPresetJson.Deserialize(ViewOutputPresetJson.Serialize(Definition(preset)));
        var loadedEncoding = Assert.IsType<ViewOutputEncodingPreset>(loaded.Encoding);
        Assert.Equal(preset.ProviderId, loadedEncoding.ProviderId);
        Assert.Equal(preset.SchemaVersion, loadedEncoding.SchemaVersion);
        Assert.Equal(preset.DataJson, loadedEncoding.DataJson);
        string path = original.Encoder == VideoEncoder.RawVideo ? "new-machine/chosen.avi" : "new-machine/chosen.mp4";
        var restored = FfmpegEncodingPreset.ToOptions(loadedEncoding, path, Plan);
        Assert.Equal(original with { OutputPath = path, Timeout = TimeSpan.FromMinutes(2) }, restored);
        Assert.Equal(original.CreateArguments(Plan, "exact-temporary-output"), restored.CreateArguments(Plan, "exact-temporary-output"));
    }

    private static ViewOutputPresetDefinition Definition(ViewOutputEncodingPreset encoding) => new("tests.ffmpeg-portable", "Portable encoding",
        new(64, 64), new(64, 64, new(30000, 1001)), encoding);

    private static FfmpegOutputOptions DetailedOptions(VideoEncoder encoder) => new(encoder == VideoEncoder.RawVideo ? "original.avi" : "original.mp4", encoder)
    {
        Quality = 37, Speed = VideoSpeed.Slow, BackgroundRed = 17, BackgroundGreen = 89, BackgroundBlue = 233, Threads = 3,
        MaximumBitrate = encoder == VideoEncoder.RawVideo ? null : 3_000_000,
        BufferSize = encoder == VideoEncoder.RawVideo ? null : 6_000_000,
        KeyframeInterval = encoder == VideoEncoder.RawVideo ? null : 31,
        Profile = encoder switch { VideoEncoder.H264Software => VideoProfile.H264High, VideoEncoder.HevcSoftware => VideoProfile.HevcMain,
            VideoEncoder.Av1AomSoftware => VideoProfile.Av1Main, _ => VideoProfile.Automatic },
        Level = encoder == VideoEncoder.H264Software ? VideoLevel.H264Level41 : VideoLevel.Automatic,
        Timeout = TimeSpan.FromSeconds(17)
    };

    private static ViewOutputEncodingPreset Changed(string field, string value)
    {
        var data = JsonNode.Parse(FfmpegEncodingPreset.FromOptions(new("source.mp4")).DataJson)!.AsObject();
        data[field] = JsonNode.Parse(value);
        return new("ffmpeg", 1, data.ToJsonString());
    }
}
