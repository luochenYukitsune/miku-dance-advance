using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.ViewOutput.Tests;

public sealed class ViewOutputPresetJsonTests
{
    [Fact]
    public void NullOverridesAndExplicitDefaultsRemainDistinctAfterRoundTrip()
    {
        var source = new ViewOutputPresetDefinition("local.default", "默认 / default", new(1280, 720),
            new(3840, 2160, new(60), new(new(), FrameAntialiasing.Off, SmaaQuality.High, 1, new(), new())));
        var copy = RoundTrip(source);
        Assert.Equal(source.StableId, copy.StableId); Assert.Equal(source.Name, copy.Name);
        Assert.Equal(source.SchemaVersion, copy.SchemaVersion); Assert.Equal(source.Preview, copy.Preview);
        Assert.Equal(source.Export, copy.Export); Assert.Null(copy.Encoding);
        Assert.Null(copy.Preview.Quality);
        Assert.NotNull(copy.Export.Quality!.Gi); Assert.NotNull(copy.Export.Quality.Upscaling);
        Assert.NotNull(copy.Export.Quality.TemporalUpscaling);
        var qualityNulls = RoundTrip(new("nulls", "Nulls", new(640, 360, new()), new(640, 360, new(30))));
        Assert.Equal(new ViewQualityOptions(), qualityNulls.Preview.Quality);
        Assert.Null(qualityNulls.Export.Quality);
    }

    [Fact]
    public void AllGiSettingsAndNullableVectorsRoundTripWithoutComputedOrRuntimeProperties()
    {
        var gi = new RayGiOptions(LightingMode.SoftwareRayTracing, 6.25f, 23, 987, 900, .003f,
            new Vector3(-.25f, 2, .75f), new Vector3(1, 2.5f, 3), .75f, .35f, 2.75f,
            RayGiComposition.ToonPalette, RayGiDenoiser.TemporalSpatial)
        { ToonResponse = RayGiToonResponse.PreservePaletteBands, TemporalHistoryGeneration = 15,
            TemporalCameraCutKey = 28, ResetTemporalHistory = true };
        var source = new ViewOutputPresetDefinition("all-gi", "GI", new(1920, 1080, new(gi)),
            new(2560, 1440, new(30000, 1001), new(gi with { DirectionalLightDirection = null, DirectionalIrradiance = null })));
        var copy = RoundTrip(source);
        var normalized = gi with { TemporalHistoryGeneration = 0, TemporalCameraCutKey = 0, ResetTemporalHistory = false };
        Assert.Equal(normalized, copy.Preview.Quality!.Gi);
        Assert.Equal(normalized with { DirectionalLightDirection = null, DirectionalIrradiance = null }, copy.Export.Quality!.Gi);
        Assert.Equal(normalized, source.Preview.Quality!.Gi);
        Assert.Equal(15, gi.TemporalHistoryGeneration); Assert.True(gi.ResetTemporalHistory);
        string json = Encoding.UTF8.GetString(ViewOutputPresetJson.Serialize(source));
        foreach (string transient in new[] { "temporalHistoryGeneration", "temporalCameraCutKey", "resetTemporalHistory",
            "lightDirection", "lightIrradiance", "effective", "startFrame", "endFrame", "physicsPreRollFrames",
            "outputPath", "model", "style", "sdk", "history", "wallTime" })
            Assert.DoesNotContain(transient, json, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewAndExportHaveIndependentQualityAndExactFrameRate()
    {
        var preview = new ViewRenderRequest(1280, 720, new(Antialiasing: FrameAntialiasing.Smaa1X,
            SmaaQuality: SmaaQuality.Ultra, SpatialSamples: 4,
            Upscaling: new() { Mode = ViewUpscalingMode.Fsr1, QualityPreset = ViewFsr1QualityPreset.UltraQuality,
                EnableRcas = false, RcasSharpness = 1.75f }));
        var export = new ViewExportPreset(3840, 2160, new(30000, 1001), new(Antialiasing: FrameAntialiasing.Off,
            SpatialSamples: 1, TemporalUpscaling: new() { Mode = ViewTemporalUpscalingMode.Dlss,
                QualityPreset = ViewTemporalQualityPreset.Balanced, XeSsQuality = ViewXeSsQuality.UltraQualityPlus,
                DlssQuality = ViewDlssQuality.Quality, FrameTimeMilliseconds = 47.5f }));
        var copy = RoundTrip(new("video", "Video", preview, export));
        Assert.Equal(preview, copy.Preview); Assert.Equal(export, copy.Export);
        var request = copy.Export.CreateRequest(45, 90, 30);
        Assert.Equal(30000, request.Plan.FrameRate.Numerator); Assert.Equal(1001, request.Plan.FrameRate.Denominator);
        Assert.Equal(45u, request.Plan.StartFrame); Assert.Equal(90u, request.Plan.EndFrame);
        Assert.Equal(30u, request.Plan.PhysicsPreRollFrames);
        Assert.Equal(1000f * 1001 / 30000, request.Render.Quality!.TemporalUpscaling!.FrameTimeMilliseconds);
        Assert.Equal(47.5f, copy.Export.Quality!.TemporalUpscaling!.FrameTimeMilliseconds);
        Assert.Equal(4, copy.Preview.Quality!.SpatialSamples);
        Assert.Throws<ArgumentOutOfRangeException>(() => copy.Export.CreateRequest(0, 5, 1));
    }

    [Theory]
    [InlineData(ViewTemporalUpscalingMode.Fsr)] [InlineData(ViewTemporalUpscalingMode.XeSs)]
    [InlineData(ViewTemporalUpscalingMode.Dlss)]
    public void EveryTemporalProviderRetainsItsOwnNondefaultSettings(ViewTemporalUpscalingMode mode)
    {
        var options = new ViewTemporalUpscalingOptions { Mode = mode, QualityPreset = ViewTemporalQualityPreset.Performance,
            XeSsQuality = ViewXeSsQuality.UltraQuality, DlssQuality = ViewDlssQuality.UltraPerformance,
            FrameTimeMilliseconds = 12.5f };
        var copy = RoundTrip(new("temporal", "Temporal", new(1280, 720, new(TemporalUpscaling: options)),
            new(1920, 1080, new(24))));
        Assert.Equal(options, copy.Preview.Quality!.TemporalUpscaling);
    }

    [Fact]
    public void EncodingIsAnOpaqueDetachedJsonObjectWithoutAdapterDependencies()
    {
        const string data = "{\"codec\":\"hevc\",\"quality\":23.5,\"enabled\":true,\"extra\":{\"items\":[null,1,\"字\"]}}";
        var encoding = new ViewOutputEncodingPreset("third-party.encoder", 3, data);
        var copy = RoundTrip(new("encoding", "Encoding", new(1, 1), new(1, 1, new(30)), encoding));
        Assert.Equal(encoding.ProviderId, copy.Encoding!.ProviderId);
        Assert.Equal(3, copy.Encoding.SchemaVersion);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(data), JsonNode.Parse(copy.Encoding.DataJson)));
        _ = RoundTrip(copy);
    }

    [Fact]
    public void ProviderTextIsNotReescapedPastItsOwnByteBudget()
    {
        string data = "{\"symbols\":\"" + new string('<', 16000) + "字😀\"}";
        var copy = RoundTrip(new("large-data", "Large data", new(1, 1), new(1, 1, new(30)), new("encoder", 1, data)));
        Assert.Equal(data, copy.Encoding!.DataJson);
        Assert.Equal(data, RoundTrip(copy).Encoding!.DataJson);
    }

    [Fact]
    public void InvalidQualityCombinationInKnownSchemaIsInvalidData()
    {
        var root = RichJson();
        root["preview"]!["quality"]!["spatialSamples"] = 2;
        root["preview"]!["quality"]!["temporalUpscaling"]!["mode"] = "Fsr";
        var error = Assert.Throws<InvalidDataException>(() => Read(root));
        Assert.IsType<NotSupportedException>(error.InnerException);
    }

    [Theory]
    [InlineData("mode", "NotInstalled")]
    [InlineData("mode", "softwareraytracing")]
    [InlineData("mode", "1")]
    [InlineData("composition", "ToonPalette, Additive")]
    public void InvalidEnumStringsAreRejected(string field, string value)
    {
        var root = RichJson(); root["preview"]!["quality"]!["gi"]![field] = value;
        Assert.Throws<InvalidDataException>(() => Read(root));
    }

    [Theory]
    [InlineData("mode")] [InlineData("composition")] [InlineData("denoiser")] [InlineData("toonResponse")]
    public void EnumNumbersAreRejectedEvenForDefaultValues(string field)
    {
        var root = RichJson(); root["preview"]!["quality"]!["gi"]![field] = 0;
        Assert.Throws<InvalidDataException>(() => Read(root));
    }

    [Theory]
    [InlineData("root")] [InlineData("preview")] [InlineData("quality")] [InlineData("gi")]
    [InlineData("vector")] [InlineData("upscaling")] [InlineData("temporalUpscaling")]
    [InlineData("export")] [InlineData("frameRate")] [InlineData("encoding")]
    public void UnknownFieldsAreRejectedAtEveryOwnedLevel(string level)
    {
        var root = RichJson();
        JsonObject target = level switch
        {
            "root" => root,
            "preview" => root["preview"]!.AsObject(),
            "quality" => root["preview"]!["quality"]!.AsObject(),
            "gi" => root["preview"]!["quality"]!["gi"]!.AsObject(),
            "vector" => root["preview"]!["quality"]!["gi"]!["directionalIrradiance"]!.AsObject(),
            "upscaling" => root["preview"]!["quality"]!["upscaling"]!.AsObject(),
            "temporalUpscaling" => root["preview"]!["quality"]!["temporalUpscaling"]!.AsObject(),
            "export" => root["export"]!.AsObject(),
            "frameRate" => root["export"]!["frameRate"]!.AsObject(),
            _ => root["encoding"]!.AsObject(),
        };
        target["future"] = true;
        Assert.Throws<InvalidDataException>(() => Read(root));
    }

    [Theory]
    [InlineData("schemaVersion")] [InlineData("stableId")] [InlineData("name")]
    [InlineData("format")] [InlineData("preview")] [InlineData("export")] [InlineData("encoding")]
    public void MissingRequiredRootFieldsAreRejected(string field)
    {
        var root = RichJson(); root.Remove(field);
        Assert.Throws<InvalidDataException>(() => Read(root));
    }

    [Fact]
    public void MissingNestedFieldsAndWrongTypesAreRejected()
    {
        var root = RichJson(); root["preview"]!["quality"]!["gi"]!.AsObject().Remove("seed");
        Assert.Throws<InvalidDataException>(() => Read(root));
        root = RichJson(); root["preview"]!["width"] = "1920";
        Assert.Throws<InvalidDataException>(() => Read(root));
        root = RichJson(); root["preview"]!["quality"]!["gi"]!["strength"] = "NaN";
        Assert.Throws<InvalidDataException>(() => Read(root));
        root = RichJson(); root["preview"]!["quality"]!["gi"]!["directionalIrradiance"]!["x"] = 1e100;
        Assert.Throws<InvalidDataException>(() => Read(root));
        root = RichJson(); root["preview"] = null;
        Assert.Throws<InvalidDataException>(() => Read(root));
        root = RichJson(); root["export"]!["frameRate"]!["denominator"] = 0;
        Assert.Throws<InvalidDataException>(() => Read(root));
    }

    [Theory]
    [InlineData(0)] [InlineData(2)] [InlineData(100)]
    public void UnsupportedSchemaCannotBeLoadedLossily(int version)
    {
        var root = RichJson(); root["schemaVersion"] = version;
        Assert.Throws<NotSupportedException>(() => Read(root));
        Assert.Throws<NotSupportedException>(() => new ViewOutputPresetDefinition("id", "name", new(1, 1), new(1, 1, new(30)), schemaVersion: version));
    }

    [Theory]
    [InlineData("")] [InlineData("{}")] [InlineData("null")] [InlineData("[]")]
    [InlineData("{\"format\":\"a\",\"format\":\"b\"}")]
    [InlineData("{\"format\":\"a\",}")] [InlineData("/* comment */ {}")]
    public void MalformedOrIncompleteJsonIsRejected(string json) =>
        Assert.Throws<InvalidDataException>(() => ViewOutputPresetJson.Deserialize(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void DuplicateNestedAndOpaqueFieldsAreRejected()
    {
        string json = Encoding.UTF8.GetString(ViewOutputPresetJson.Serialize(RichDefinition()));
        Assert.Throws<InvalidDataException>(() => ViewOutputPresetJson.Deserialize(Encoding.UTF8.GetBytes(
            json.Replace("\"width\":1920", "\"width\":1920,\"width\":1920", StringComparison.Ordinal))));
        Assert.Throws<ArgumentException>(() => new ViewOutputEncodingPreset("encoder", 1, "{\"a\":1,\"a\":2}"));
    }

    [Fact]
    public void ByteDepthAndEncodingObjectBudgetsAreEnforced()
    {
        Assert.Throws<InvalidDataException>(() => ViewOutputPresetJson.Deserialize(new byte[ViewOutputPresetJson.MaxBytes + 1]));
        Assert.Throws<InvalidDataException>(() => ViewOutputPresetJson.Deserialize([0xff]));
        Assert.Throws<InvalidDataException>(() => ViewOutputPresetJson.Deserialize(Encoding.UTF8.GetBytes(new string('[', 17) + new string(']', 17))));
        Assert.Throws<ArgumentException>(() => new ViewOutputEncodingPreset("encoder", 1, "[]"));
        Assert.Throws<ArgumentException>(() => new ViewOutputEncodingPreset("encoder", 1, "{\"x\":\"" + new string('x', 16384) + "\"}"));
        Assert.Throws<ArgumentException>(() => new ViewOutputEncodingPreset("encoder", 1,
            string.Concat(Enumerable.Repeat("{\"x\":", 12)) + "{}" + new string('}', 12)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewOutputEncodingPreset("encoder", 0, "{}"));
    }

    [Theory]
    [InlineData("")] [InlineData("../path")] [InlineData("white space")] [InlineData("非ASCII")]
    public void InvalidIdentifiersAreRejected(string id)
    {
        Assert.Throws<ArgumentException>(() => new ViewOutputPresetDefinition(id, "Name", new(1, 1), new(1, 1, new(30))));
        Assert.Throws<ArgumentException>(() => new ViewOutputEncodingPreset(id, 1, "{}"));
    }

    [Theory]
    [InlineData("")] [InlineData("\r\n")] [InlineData("name\u0001")]
    public void InvalidNamesAreRejected(string name) =>
        Assert.Throws<ArgumentException>(() => new ViewOutputPresetDefinition("id", name, new(1, 1), new(1, 1, new(30))));

    [Fact]
    public void ConstructorReusesExistingQualityAndAllocationValidation()
    {
        Assert.Throws<ArgumentException>(() => new ViewOutputPresetDefinition("id", new string('n', 129), new(1, 1), new(1, 1, new(30))));
        Assert.Throws<ArgumentException>(() => new ViewOutputPresetDefinition("id", "bad" + '\ud800', new(1, 1), new(1, 1, new(30))));
        Assert.Throws<ArgumentException>(() => new ViewOutputPresetDefinition(new string('i', 129), "Name", new(1, 1), new(1, 1, new(30))));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewExportPreset(0, 1, new(30)).Validate());
        Assert.Throws<ArgumentException>(() => new ViewExportPreset(1, 1, default).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewOutputPresetDefinition("id", "Name", new(1, 1, new(new(Strength: float.NaN))), new(1, 1, new(30))));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewOutputPresetDefinition("id", "Name", new(1, 1), new(1, 1, new(30), new(SmaaQuality: (SmaaQuality)99))));
        Assert.Throws<NotSupportedException>(() => new ViewOutputPresetDefinition("id", "Name", new(1, 1,
            new(Antialiasing: FrameAntialiasing.Smaa1X, TemporalUpscaling: new() { Mode = ViewTemporalUpscalingMode.Fsr })), new(1, 1, new(30))));
    }

    private static ViewOutputPresetDefinition RoundTrip(ViewOutputPresetDefinition source) =>
        ViewOutputPresetJson.Deserialize(ViewOutputPresetJson.Serialize(source));
    private static ViewOutputPresetDefinition Read(JsonObject root) => ViewOutputPresetJson.Deserialize(Encoding.UTF8.GetBytes(root.ToJsonString()));
    private static JsonObject RichJson() => JsonNode.Parse(ViewOutputPresetJson.Serialize(RichDefinition()))!.AsObject();
    private static ViewOutputPresetDefinition RichDefinition() => new("rich", "Rich", new(1920, 1080,
        new(new(DirectionalIrradiance: new Vector3(1, 2, 3)), FrameAntialiasing.Off, SmaaQuality.Low, 1, new(), new())),
        new(2560, 1440, new(30000, 1001), new()), new("encoder", 1, "{}"));
}
