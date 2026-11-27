using System.Text;
using System.Text.Json;
using Mmd.Modern.Application;

namespace Mmd.Modern.ViewOutput;

/// <summary>Portable output settings. Source intervals and pre-roll belong to each export operation.</summary>
public sealed record ViewExportPreset(int Width, int Height, OfflineFrameRate FrameRate, ViewQualityOptions? Quality = null,
    ViewCaptureMemoryMode CaptureMemory = ViewCaptureMemoryMode.Auto)
{
    public void Validate()
    {
        _ = new OfflineFramePlan(Width, Height, FrameRate, 0, 0);
        if (!Enum.IsDefined(CaptureMemory)) throw new ArgumentOutOfRangeException(nameof(CaptureMemory));
        Quality?.Validate();
    }

    public ExportRequest CreateRequest(uint startFrame, uint endFrame, uint physicsPreRollFrames = 0, bool includeEndFrame = true)
    {
        Validate();
        var request = new ExportRequest(new(Width, Height, FrameRate, startFrame, endFrame, physicsPreRollFrames, includeEndFrame), Quality, CaptureMemory);
        request.Validate();
        return request;
    }
}

/// <summary>A detached provider-owned JSON object. Executable paths and output destinations belong to the host.</summary>
public sealed class ViewOutputEncodingPreset
{
    public const int MaxDataBytes = 16 * 1024;
    public const int MaxDataDepth = 12;
    public string ProviderId { get; }
    public int SchemaVersion { get; }
    public string DataJson { get; }

    public ViewOutputEncodingPreset(string providerId, int schemaVersion, string dataJson)
    {
        ViewOutputPresetDefinition.ValidateIdentifier(providerId, nameof(providerId));
        if (schemaVersion < 1) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        ArgumentNullException.ThrowIfNull(dataJson);
        try
        {
            if (ViewOutputPresetDefinition.StrictUtf8.GetByteCount(dataJson) > MaxDataBytes)
                throw new ArgumentException("Encoding data exceeds the supported byte budget.", nameof(dataJson));
            using var document = JsonDocument.Parse(dataJson, new()
            { MaxDepth = MaxDataDepth, AllowDuplicateProperties = false, AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Encoding data must be a JSON object.", nameof(dataJson));
            ValidateStrings(document.RootElement);
        }
        catch (Exception error) when (error is JsonException or EncoderFallbackException or InvalidOperationException)
        { throw new ArgumentException("Encoding data must be bounded, valid JSON without duplicate fields.", nameof(dataJson), error); }
        ProviderId = providerId; SchemaVersion = schemaVersion; DataJson = dataJson;
    }

    private static void ValidateStrings(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) _ = ViewOutputPresetDefinition.StrictUtf8.GetByteCount(value.GetString()!);
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                _ = ViewOutputPresetDefinition.StrictUtf8.GetByteCount(property.Name);
                ValidateStrings(property.Value);
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateStrings(item);
    }
}

/// <summary>Immutable independent preview and output configuration, without project selections or live GPU history.</summary>
public sealed class ViewOutputPresetDefinition
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxIdentifierLength = 128;
    public const int MaxNameLength = 128;
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public string StableId { get; }
    public string Name { get; }
    public ViewRenderRequest Preview { get; }
    public ViewExportPreset Export { get; }
    public ViewOutputEncodingPreset? Encoding { get; }
    public int SchemaVersion { get; }

    public ViewOutputPresetDefinition(string stableId, string name, ViewRenderRequest preview, ViewExportPreset export,
        ViewOutputEncodingPreset? encoding = null, int schemaVersion = CurrentSchemaVersion)
    {
        ValidateIdentifier(stableId, nameof(stableId));
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength || name.Any(char.IsControl))
            throw new ArgumentException("A preset name must contain 1 to 128 characters without control characters.", nameof(name));
        try { _ = StrictUtf8.GetByteCount(name); }
        catch (EncoderFallbackException error) { throw new ArgumentException("A preset name must be valid Unicode.", nameof(name), error); }
        if (schemaVersion != CurrentSchemaVersion)
            throw new NotSupportedException($"View/output preset schema {schemaVersion} is not supported.");
        ArgumentNullException.ThrowIfNull(preview); ArgumentNullException.ThrowIfNull(export);
        preview.Validate(); export.Validate();
        StableId = stableId; Name = name; SchemaVersion = schemaVersion; Encoding = encoding;
        Preview = preview with { Quality = PortableQuality(preview.Quality) };
        Export = export with { Quality = PortableQuality(export.Quality) };
    }

    private static ViewQualityOptions? PortableQuality(ViewQualityOptions? quality) => quality is null ? null : quality with
    {
        Diffusion = quality.Diffusion is { } diffusion ? diffusion with { } : null,
        Sharpening = quality.Sharpening is { } sharpening ? sharpening with { } : null,
        ScreenSpaceAo = quality.ScreenSpaceAo is { } ssao ? ssao with { } : null,
        Gi = quality.Gi is { } gi ? gi with
        { TemporalHistoryGeneration = 0, TemporalCameraCutKey = 0, ResetTemporalHistory = false } : null,
        PathTracing = quality.PathTracing is { } pathTracing ? pathTracing.StripRuntimeState() : null,
    };

    internal static void ValidateIdentifier(string id, string parameter)
    {
        if (id is null || id.Length is < 1 or > MaxIdentifierLength
            || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw new ArgumentException("A preset or provider ID must be 1 to 128 ASCII letters, digits, dots, underscores or hyphens.", parameter);
    }
}
