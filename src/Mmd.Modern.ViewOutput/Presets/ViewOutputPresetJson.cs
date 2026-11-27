using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Application;
using Mmd.Modern.Core;

namespace Mmd.Modern.ViewOutput;

/// <summary>Strict, bounded portable JSON. Only explicitly listed configuration fields cross this boundary.</summary>
public static class ViewOutputPresetJson
{
    public const int MaxBytes = 65536;
    public const int MaxDepth = 16;
    private const string Format = "mmd-modern-view-output-preset";
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static byte[] Serialize(ViewOutputPresetDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var dto = new PresetDto(Format, definition.SchemaVersion, definition.StableId, definition.Name,
            RenderDto.From(definition.Preview), ExportDto.From(definition.Export), EncodingDto.From(definition.Encoding));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(dto, Options);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("View/output preset exceeds the supported byte budget.");
        return bytes;
    }

    public static ViewOutputPresetDefinition Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaxBytes)
            throw new InvalidDataException("View/output preset exceeds the supported byte budget.");
        try
        {
            _ = ViewOutputPresetDefinition.StrictUtf8.GetCharCount(bytes);
            using var document = JsonDocument.Parse(bytes.ToArray(), new()
            { MaxDepth = MaxDepth, AllowDuplicateProperties = false, AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A view/output preset must be a JSON object.");
            if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out int version))
                throw new InvalidDataException("A view/output preset requires an integer schemaVersion.");
            if (version != ViewOutputPresetDefinition.CurrentSchemaVersion)
                throw new NotSupportedException($"View/output preset schema {version} is not supported.");
            var dto = root.Deserialize<PresetDto>(Options) ?? throw new InvalidDataException("A view/output preset must be an object.");
            if (dto.Format != Format) throw new InvalidDataException("Unknown view/output preset format.");
            try
            {
                return new(dto.StableId, dto.Name, dto.Preview.ToRequest(), dto.Export.ToPreset(),
                    dto.Encoding?.ToPreset(), dto.SchemaVersion);
            }
            catch (NotSupportedException error)
            { throw new InvalidDataException("The view/output preset contains an unsupported quality combination.", error); }
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        { throw new InvalidDataException("Invalid view/output preset data.", error); }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            MaxDepth = MaxDepth,
        };
        options.Converters.Add(new ExactEnumConverter<LightingMode>());
        options.Converters.Add(new ExactEnumConverter<RayGiComposition>());
        options.Converters.Add(new ExactEnumConverter<RayGiDenoiser>());
        options.Converters.Add(new ExactEnumConverter<RayGiToonResponse>());
        options.Converters.Add(new ExactEnumConverter<FrameAntialiasing>());
        options.Converters.Add(new ExactEnumConverter<ShadowFilterMode>());
        options.Converters.Add(new ExactEnumConverter<SmaaQuality>());
        options.Converters.Add(new ExactEnumConverter<SurfaceDrawOrder>());
        options.Converters.Add(new ExactEnumConverter<ViewCaptureMemoryMode>());
        options.Converters.Add(new ExactEnumConverter<ViewUpscalingMode>());
        options.Converters.Add(new ExactEnumConverter<ViewFsr1QualityPreset>());
        options.Converters.Add(new ExactEnumConverter<ViewTemporalUpscalingMode>());
        options.Converters.Add(new ExactEnumConverter<ViewTemporalQualityPreset>());
        options.Converters.Add(new ExactEnumConverter<ViewXeSsQuality>());
        options.Converters.Add(new ExactEnumConverter<ViewDlssQuality>());
        options.Converters.Add(new OpaqueDataConverter());
        return options;
    }

    // Re-escaping provider strings can expand a valid 16 KiB payload beyond its own read budget.
    // Keep the already validated object text; no provider fields are interpreted here.
    private sealed class OpaqueDataConverter : JsonConverter<JsonElement>
    {
        public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return document.RootElement.Clone();
        }
        public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options) =>
            writer.WriteRawValue(value.GetRawText());
    }

    private sealed class ExactEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("Preset enums must be named strings.");
            string? name = reader.GetString();
            if (name is null || !Enum.TryParse<T>(name, false, out var value) || Enum.GetName(value) != name)
                throw new JsonException($"Unknown {typeof(T).Name} value.");
            return value;
        }
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(Enum.GetName(value) ?? throw new JsonException($"Unknown {typeof(T).Name} value."));
    }

    private sealed record PresetDto(string Format, int SchemaVersion, string StableId, string Name,
        RenderDto Preview, ExportDto Export, EncodingDto? Encoding);

    private sealed record RenderDto(int Width, int Height, QualityDto? Quality,
        ViewCaptureMemoryMode CaptureMemory = ViewCaptureMemoryMode.Auto)
    {
        internal static RenderDto From(ViewRenderRequest value) => new(value.Width, value.Height, QualityDto.From(value.Quality), value.CaptureMemory);
        internal ViewRenderRequest ToRequest() => new(Width, Height, Quality?.ToOptions(), CaptureMemory);
    }

    private sealed record ExportDto(int Width, int Height, RateDto FrameRate, QualityDto? Quality,
        ViewCaptureMemoryMode CaptureMemory = ViewCaptureMemoryMode.Auto)
    {
        internal static ExportDto From(ViewExportPreset value) =>
            new(value.Width, value.Height, new(value.FrameRate.Numerator, value.FrameRate.Denominator), QualityDto.From(value.Quality), value.CaptureMemory);
        internal ViewExportPreset ToPreset() => new(Width, Height, new(FrameRate.Numerator, FrameRate.Denominator), Quality?.ToOptions(), CaptureMemory);
    }

    private sealed record RateDto(int Numerator, int Denominator);

    private sealed record EncodingDto(string ProviderId, int SchemaVersion, JsonElement Data)
    {
        internal static EncodingDto? From(ViewOutputEncodingPreset? value)
        {
            if (value is null) return null;
            using var document = JsonDocument.Parse(value.DataJson, new() { MaxDepth = ViewOutputEncodingPreset.MaxDataDepth });
            return new(value.ProviderId, value.SchemaVersion, document.RootElement.Clone());
        }
        internal ViewOutputEncodingPreset ToPreset() => new(ProviderId, SchemaVersion, Data.GetRawText());
    }

    private sealed record QualityDto(GiDto? Gi, FrameAntialiasing? Antialiasing, SmaaQuality? SmaaQuality,
        int? SpatialSamples, UpscalingDto? Upscaling, TemporalDto? TemporalUpscaling,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DiffusionDto? Diffusion = null,
        SurfaceDrawOrder SurfaceOrder = SurfaceDrawOrder.OpaqueFirst,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SharpeningDto? Sharpening = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? AnisotropicFiltering = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ShadowFilterMode? ShadowFilter = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? ShadowSoftness = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScreenSpaceAoDto? ScreenSpaceAo = null)
    {
        internal static QualityDto? From(ViewQualityOptions? value) => value is null ? null : new(GiDto.From(value.Gi),
            value.Antialiasing, value.SmaaQuality, value.SpatialSamples, UpscalingDto.From(value.Upscaling), TemporalDto.From(value.TemporalUpscaling), DiffusionDto.From(value.Diffusion), value.SurfaceOrder,
            SharpeningDto.From(value.Sharpening), value.AnisotropicFiltering, value.ShadowFilter, value.ShadowSoftness, ScreenSpaceAoDto.From(value.ScreenSpaceAo));
        internal ViewQualityOptions ToOptions() => new(Gi?.ToOptions(), Antialiasing, SmaaQuality, SpatialSamples,
            Upscaling?.ToOptions(), TemporalUpscaling?.ToOptions(), Diffusion?.ToOptions(), SurfaceOrder)
        {
            Sharpening = Sharpening?.ToOptions(),
            AnisotropicFiltering = AnisotropicFiltering,
            ShadowFilter = ShadowFilter,
            ShadowSoftness = ShadowSoftness,
            ScreenSpaceAo = ScreenSpaceAo?.ToOptions(),
        };
    }

    private sealed record SharpeningDto(bool Enabled, float Sharpness)
    {
        internal static SharpeningDto? From(ViewSharpeningOptions? value) => value is null ? null :
            new(value.Enabled, value.Sharpness);
        internal ViewSharpeningOptions ToOptions() => new(Enabled, Sharpness);
    }

    private sealed record ScreenSpaceAoDto(bool Enabled, float Strength, float Radius, int SampleCount)
    {
        internal static ScreenSpaceAoDto? From(ScreenSpaceAoOptions? value) => value is null ? null :
            new(value.Enabled, value.Strength, value.Radius, value.SampleCount);
        internal ScreenSpaceAoOptions ToOptions() => new(Enabled, Strength, Radius, SampleCount);
    }

    private sealed record DiffusionDto(bool Enabled, float Strength, float Extent)
    {
        internal static DiffusionDto? From(ViewDiffusionOptions? value) => value is null ? null :
            new(value.Enabled, value.Strength, value.Extent);
        internal ViewDiffusionOptions ToOptions() => new(Enabled, Strength, Extent);
    }

    private sealed record GiDto(LightingMode Mode, float Strength, int SamplesPerPixel, uint Seed, float MaxRayDistance,
        float RayEpsilon, VectorDto? DirectionalLightDirection, VectorDto? DirectionalIrradiance, float DirectScale,
        float OcclusionStrength, float OcclusionRadius, RayGiComposition Composition, RayGiDenoiser Denoiser,
        RayGiToonResponse ToonResponse, bool PreviewAcceleration = false)
    {
        internal static GiDto? From(RayGiOptions? value) => value is null ? null : new(value.Mode, value.Strength,
            value.SamplesPerPixel, value.Seed, value.MaxRayDistance, value.RayEpsilon, VectorDto.From(value.DirectionalLightDirection),
            VectorDto.From(value.DirectionalIrradiance), value.DirectScale, value.OcclusionStrength, value.OcclusionRadius,
            value.Composition, value.Denoiser, value.ToonResponse, value.PreviewAcceleration);
        internal RayGiOptions ToOptions() => new(Mode, Strength, SamplesPerPixel, Seed, MaxRayDistance, RayEpsilon,
            DirectionalLightDirection?.ToVector(), DirectionalIrradiance?.ToVector(), DirectScale, OcclusionStrength,
            OcclusionRadius, Composition, Denoiser) { ToonResponse = ToonResponse, PreviewAcceleration = PreviewAcceleration };
    }

    private sealed record VectorDto(float X, float Y, float Z)
    {
        internal static VectorDto? From(Vector3? value) => value is { } vector ? new(vector.X, vector.Y, vector.Z) : null;
        internal Vector3 ToVector() => new(X, Y, Z);
    }

    private sealed record UpscalingDto(ViewUpscalingMode Mode, ViewFsr1QualityPreset QualityPreset, bool EnableRcas, float RcasSharpness)
    {
        internal static UpscalingDto? From(ViewUpscalingOptions? value) => value is null ? null :
            new(value.Mode, value.QualityPreset, value.EnableRcas, value.RcasSharpness);
        internal ViewUpscalingOptions ToOptions() => new()
        { Mode = Mode, QualityPreset = QualityPreset, EnableRcas = EnableRcas, RcasSharpness = RcasSharpness };
    }

    private sealed record TemporalDto(ViewTemporalUpscalingMode Mode, ViewTemporalQualityPreset QualityPreset,
        ViewXeSsQuality XeSsQuality, ViewDlssQuality DlssQuality, float FrameTimeMilliseconds)
    {
        internal static TemporalDto? From(ViewTemporalUpscalingOptions? value) => value is null ? null :
            new(value.Mode, value.QualityPreset, value.XeSsQuality, value.DlssQuality, value.FrameTimeMilliseconds);
        internal ViewTemporalUpscalingOptions ToOptions() => new()
        { Mode = Mode, QualityPreset = QualityPreset, XeSsQuality = XeSsQuality, DlssQuality = DlssQuality, FrameTimeMilliseconds = FrameTimeMilliseconds };
    }
}
