using System.Buffers.Binary;
using System.Numerics;

namespace Mmd.Modern.Plugins.Contracts;

/// <summary>Standard capability identifiers defined by the host contracts.</summary>
public static class PluginCapabilities
{
    public const string EditorCommands = "editor.commands";
    public const string RenderingStyles = "rendering.styles";
}

/// <summary>Text with optional language-tagged translations.</summary>
public sealed record LocalizedText(
    string DefaultText,
    IReadOnlyDictionary<string, string>? Translations = null)
{
    /// <summary>Creates localized text with optional Simplified Chinese, English, and Japanese entries.</summary>
    public static LocalizedText From(string defaultText, string? zhCn = null, string? en = null, string? ja = null)
    {
        ArgumentNullException.ThrowIfNull(defaultText);

        Dictionary<string, string>? translations = null;
        AddIfPresent(ref translations, "zh-CN", zhCn);
        AddIfPresent(ref translations, "en", en);
        AddIfPresent(ref translations, "ja", ja);
        return new LocalizedText(defaultText, translations?.AsReadOnly());
    }

    /// <summary>Resolves the best matching text for a BCP-47 language tag, falling back to <see cref="DefaultText"/>.</summary>
    public string Resolve(string? language)
    {
        if (Translations is null || Translations.Count == 0 || string.IsNullOrWhiteSpace(language))
            return DefaultText;

        var trimmed = language.Trim();
        if (Translations.TryGetValue(trimmed, out var direct) && !string.IsNullOrWhiteSpace(direct))
            return direct;

        var normalized = NormalizeLanguageTag(trimmed);
        foreach (var (key, value) in Translations)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            if (string.Equals(NormalizeLanguageTag(key), normalized, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        // Progressive subtag fallback: e.g. "en-US" -> "en", "ja-JP" -> "ja".
        var candidate = normalized;
        while (true)
        {
            var separator = candidate.LastIndexOf('-');
            if (separator <= 0)
                break;
            candidate = candidate[..separator];
            foreach (var (key, value) in Translations)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                if (string.Equals(NormalizeLanguageTag(key), candidate, StringComparison.OrdinalIgnoreCase))
                    return value;
            }
        }

        // Primary subtag fallback against region-qualified keys: e.g. "zh" -> "zh-CN".
        var primary = GetPrimarySubtag(normalized);
        foreach (var (key, value) in Translations)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            if (string.Equals(GetPrimarySubtag(NormalizeLanguageTag(key)), primary, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return DefaultText;
    }

    private static void AddIfPresent(ref Dictionary<string, string>? map, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        map ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        map[key] = value;
    }

    private static string NormalizeLanguageTag(string tag) => tag.Trim().Replace('_', '-');

    private static string GetPrimarySubtag(string normalizedTag)
    {
        var separator = normalizedTag.IndexOf('-');
        return separator >= 0 ? normalizedTag[..separator] : normalizedTag;
    }
}

/// <summary>Semantic material role used by stylized rendering pipelines.</summary>
public enum StyleMaterialRole
{
    Body,
    Face,
    Eye,
    Hair
}

/// <summary>Default outline configuration for a rendering style.</summary>
public sealed record StyleOutlineDefaults(
    float Width = 0.18f,
    Vector4? Color = null,
    bool Enabled = true)
{
    /// <summary>Apply this style's outline only to materials with an authored edge.</summary>
    public bool RespectMaterialEdgeFlag { get; init; }
}

/// <summary>Default toon response ramp parameters for a rendering style.</summary>
public sealed record StyleToonResponseDefaults(
    float Threshold = 0.5f,
    float TransitionWidth = 0.1f);

/// <summary>Stylized shading parameters shared across materials and style overrides.</summary>
public sealed record StyleStylizedShadingParameters(
    float ShadowStrength,
    float ShadowValue,
    float ShadowHue,
    Vector3 ShadowTint,
    float FaceFlatten,
    Vector3 WarmColor,
    float WarmStrength,
    float RimEdge,
    float RimStrength,
    float Saturation,
    float Contrast,
    float Brightness,
    float OutlineValue = 0.5f,
    float OutlineSaturation = 1.3f);

/// <summary>Per-role material treatment adjustments for stylized rendering.</summary>
public sealed record StyleMaterialTreatmentParameters(
    float Saturation = 1f,
    float TransitionWidthScale = 1f,
    float ValueContrast = 0f,
    float ShadeGamma = 1f,
    float HairHighlightStrength = 0f,
    float HairHighlightWidth = 0.12f);

/// <summary>Default shading attenuation parameters for face and eye materials.</summary>
public sealed record StyleFaceEyeDefaults(
    float FaceNormalShadeStrength = 0.35f,
    float FaceGeometricShadowStrength = 1.0f,
    float EyeGeometricShadowStrength = 0.2f);

/// <summary>Resource binding budgets enforced by the host renderer for style plugins.</summary>
public static class StyleResourceLimits
{
    public const int MaxTextureSlots = 12;
    public const int MaxStructuredBufferSlots = 4;
    public const int MaxConstantBufferBytes = 4096;
}

/// <summary>Shader extension points and integration flags declared by a style module.</summary>
[Flags]
public enum StyleShaderHookMask
{
    None = 0,
    LightResponse = 1 << 0,
    PaletteFactor = 1 << 1,
    Specular = 1 << 2,
    AlbedoTransform = 1 << 3,
    PrimarySurfacePost = 1 << 4,
    FinalEntryPre = 1 << 5,
    FinalEntryPost = 1 << 6,
    AllowStylizedShadingWrapper = 1 << 7,
    DisableStylizedSphereMaps = 1 << 8,
    HasStyleShaderCode = 1 << 9,
    RequiresOutlineColorScale = 1 << 10,
    RequiresSkinShadeBlend = 1 << 11,
    PreserveDirectLightPalette = 1 << 12
}

/// <summary>Additional per-vertex or per-primitive geometry inputs required by a style shader module.</summary>
[Flags]
public enum StyleGeometryRequirements
{
    None = 0,
    SubTextureUv = 1 << 0,
    FrontFace = 1 << 1,
    PrimitiveId = 1 << 2,
    MikkTangentCorners = 1 << 3,
    RawRadialSeeds = 1 << 4,
    ModelSpacePosition = 1 << 5,
    ModelSpaceNormal = 1 << 6
}

public enum StyleTextureEncoding
{
    Linear = 0,
    SrgbColor = 1
}

public enum StyleTextureMipRule
{
    FromSource = 0,
    PreserveMips = 1,
    PointNoMip = 2,
    GenerateMips = 3
}

public enum StyleTexturePixelFormat
{
    Rgba8Unorm = 0,
    Rgba8UnormSrgb = 1,
    Rgba16Float = 2,
    Rgba32Float = 3,
    R8Unorm = 4
}

/// <summary>CPU-provided texture asset bound into a style texture slot.</summary>
public sealed record StyleInlineTexture(
    string AssetKey,
    int Width,
    int Height,
    StyleTexturePixelFormat Format,
    ReadOnlyMemory<byte> Pixels,
    StyleTextureMipRule MipRule = StyleTextureMipRule.FromSource);

/// <summary>Binds either a scene-composed texture or an inline texture asset to a shader slot.</summary>
public sealed record StyleTextureBinding(
    int SlotIndex,
    int SceneTextureIndex = -1,
    StyleInlineTexture? InlineTexture = null,
    StyleTextureEncoding Encoding = StyleTextureEncoding.Linear,
    StyleTextureMipRule MipRule = StyleTextureMipRule.FromSource);

public enum StyleBufferKind
{
    MikkTangentCorners = 0,
    CustomStructured = 1
}

/// <summary>Binds a host-managed or custom structured buffer to a shader slot.</summary>
public sealed record StyleBufferBinding(
    int SlotIndex,
    StyleBufferKind Kind,
    string? DataKey = null,
    int ElementStrideBytes = 0,
    int ElementCount = 0,
    ReadOnlyMemory<byte> Data = default);

/// <summary>GPU-neutral HLSL shader snippet and compilation metadata exposed by a rendering style.</summary>
public sealed record StyleShaderModule(
    string ModuleId,
    string? HlslSource,
    StyleShaderHookMask Hooks = StyleShaderHookMask.AllowStylizedShadingWrapper,
    StyleGeometryRequirements GeometryRequirements = StyleGeometryRequirements.None,
    IReadOnlyDictionary<string, string>? Defines = null,
    string? CacheKeySuffix = null,
    bool DisableStylizedSphereMaps = false);

/// <summary>
/// Builds a little-endian byte payload obeying HLSL <c>cbuffer</c> 16-byte register packing rules
/// within <see cref="StyleResourceLimits.MaxConstantBufferBytes"/>.
/// </summary>
public sealed class StyleConstantBufferBuilder
{
    private byte[] _buffer = new byte[256];
    private int _byteCount;

    /// <summary>Current byte length written into the constant buffer.</summary>
    public int ByteCount => _byteCount;

    public StyleConstantBufferBuilder Write(float value)
    {
        EnsureWriteCapacity(4, alignToRegisterIfStraddling: false);
        BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(_byteCount, 4), CanonicalFloat(value));
        _byteCount += 4;
        return this;
    }

    public StyleConstantBufferBuilder Write(int value)
    {
        EnsureWriteCapacity(4, alignToRegisterIfStraddling: false);
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_byteCount, 4), value);
        _byteCount += 4;
        return this;
    }

    public StyleConstantBufferBuilder Write(uint value)
    {
        EnsureWriteCapacity(4, alignToRegisterIfStraddling: false);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_byteCount, 4), value);
        _byteCount += 4;
        return this;
    }

    public StyleConstantBufferBuilder Write(Vector2 value)
    {
        var alignedStart = EnsureWriteCapacity(8, alignToRegisterIfStraddling: true);
        _byteCount = alignedStart;
        var span = _buffer.AsSpan(_byteCount, 8);
        BinaryPrimitives.WriteSingleLittleEndian(span[..4], CanonicalFloat(value.X));
        BinaryPrimitives.WriteSingleLittleEndian(span[4..8], CanonicalFloat(value.Y));
        _byteCount += 8;
        return this;
    }

    public StyleConstantBufferBuilder Write(Vector3 value)
    {
        var alignedStart = EnsureWriteCapacity(12, alignToRegisterIfStraddling: true);
        _byteCount = alignedStart;
        var span = _buffer.AsSpan(_byteCount, 12);
        BinaryPrimitives.WriteSingleLittleEndian(span[..4], CanonicalFloat(value.X));
        BinaryPrimitives.WriteSingleLittleEndian(span[4..8], CanonicalFloat(value.Y));
        BinaryPrimitives.WriteSingleLittleEndian(span[8..12], CanonicalFloat(value.Z));
        _byteCount += 12;
        return this;
    }

    public StyleConstantBufferBuilder Write(Vector4 value)
    {
        var alignedStart = EnsureWriteCapacity(16, alignToRegisterIfStraddling: true);
        _byteCount = alignedStart;
        WriteVector4Core(_buffer.AsSpan(_byteCount, 16), value);
        _byteCount += 16;
        return this;
    }

    /// <summary>Writes 16 little-endian floats in <c>M11..M44</c> order aligned to a 16-byte register.</summary>
    public StyleConstantBufferBuilder Write(Matrix4x4 value)
    {
        var alignedStart = EnsureWriteCapacity(64, alignToRegisterIfStraddling: true, registerElementBytes: 16);
        _byteCount = alignedStart;
        var span = _buffer.AsSpan(_byteCount, 64);
        WriteVector4Core(span[..16], new Vector4(value.M11, value.M12, value.M13, value.M14));
        WriteVector4Core(span[16..32], new Vector4(value.M21, value.M22, value.M23, value.M24));
        WriteVector4Core(span[32..48], new Vector4(value.M31, value.M32, value.M33, value.M34));
        WriteVector4Core(span[48..64], new Vector4(value.M41, value.M42, value.M43, value.M44));
        _byteCount += 64;
        return this;
    }

    public StyleConstantBufferBuilder WriteFloat(float value) => Write(value);
    public StyleConstantBufferBuilder WriteInt(int value) => Write(value);
    public StyleConstantBufferBuilder WriteUInt(uint value) => Write(value);
    public StyleConstantBufferBuilder WriteVector2(Vector2 value) => Write(value);
    public StyleConstantBufferBuilder WriteVector3(Vector3 value) => Write(value);
    public StyleConstantBufferBuilder WriteVector4(Vector4 value) => Write(value);
    public StyleConstantBufferBuilder WriteMatrix4x4(Matrix4x4 value) => Write(value);

    /// <summary>Zero-pads the current write offset to the next 16-byte register boundary.</summary>
    public StyleConstantBufferBuilder PadTo16()
    {
        var aligned = AlignUp16(_byteCount);
        if (aligned == _byteCount)
            return this;
        if (aligned > StyleResourceLimits.MaxConstantBufferBytes)
            throw BudgetExceeded();
        EnsureCapacity(aligned);
        _buffer.AsSpan(_byteCount, aligned - _byteCount).Clear();
        _byteCount = aligned;
        return this;
    }

    /// <summary>Returns a copy of the packed constant buffer bytes.</summary>
    public byte[] ToArray() => _byteCount == 0 ? [] : _buffer[.._byteCount];

    private int EnsureWriteCapacity(int totalBytes, bool alignToRegisterIfStraddling, int? registerElementBytes = null)
    {
        var elementBytes = registerElementBytes ?? totalBytes;
        var start = _byteCount;
        var rem = start & 15;
        if (alignToRegisterIfStraddling && rem != 0 && rem + elementBytes > 16)
            start = AlignUp16(start);

        var end = start + totalBytes;
        if (end > StyleResourceLimits.MaxConstantBufferBytes)
            throw BudgetExceeded();

        EnsureCapacity(end);
        if (start > _byteCount)
            _buffer.AsSpan(_byteCount, start - _byteCount).Clear();
        return start;
    }

    private void EnsureCapacity(int requiredBytes)
    {
        if (requiredBytes <= _buffer.Length)
            return;
        var nextLength = _buffer.Length;
        while (nextLength < requiredBytes)
            nextLength = Math.Min(StyleResourceLimits.MaxConstantBufferBytes, nextLength * 2);
        Array.Resize(ref _buffer, nextLength);
    }

    private static void WriteVector4Core(Span<byte> destination, Vector4 value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(destination[..4], CanonicalFloat(value.X));
        BinaryPrimitives.WriteSingleLittleEndian(destination[4..8], CanonicalFloat(value.Y));
        BinaryPrimitives.WriteSingleLittleEndian(destination[8..12], CanonicalFloat(value.Z));
        BinaryPrimitives.WriteSingleLittleEndian(destination[12..16], CanonicalFloat(value.W));
    }

    private static float CanonicalFloat(float value) => value == 0f ? 0f : value;

    private static int AlignUp16(int value) => (value + 15) & ~15;

    private static InvalidOperationException BudgetExceeded() =>
        new($"Constant buffer exceeds the {StyleResourceLimits.MaxConstantBufferBytes}-byte limit.");
}

public enum StyleParameterKind
{
    Float = 0,
    Int = 1,
    Bool = 2,
    Enum = 3,
    Color3 = 4,
    Color4 = 5,
    Vector2 = 6,
    Vector3 = 7,
    Vector4 = 8,
    TextureSlot = 9,
    BoneName = 10
}

public sealed record StyleEnumOption(string Value, LocalizedText DisplayName);

public sealed record StyleParameterDescriptor(
    string Key,
    LocalizedText DisplayName,
    StyleParameterKind Kind,
    LocalizedText? Description = null,
    double DefaultNumber = 0,
    double MinNumber = 0,
    double MaxNumber = 1,
    double Step = 0.01,
    bool DefaultBool = false,
    float[]? DefaultVector = null,
    string? DefaultString = null,
    IReadOnlyList<StyleEnumOption>? EnumOptions = null);

public sealed record StyleTextureSlotDescriptor(
    string SlotKey,
    LocalizedText DisplayName,
    bool Required = false,
    StyleTextureEncoding Encoding = StyleTextureEncoding.Linear,
    StyleTextureMipRule MipRule = StyleTextureMipRule.FromSource,
    bool SupportsSecondaryTexture = false,
    LocalizedText? SecondaryDisplayName = null,
    LocalizedText? Description = null);

public sealed record StyleBoneAxisDefaults(
    Vector3 LocalForward,
    Vector3 LocalUp,
    Vector3 WorldForward,
    Vector3 WorldRight,
    bool DefaultUseSelectedBone = true);

public sealed record StyleMaterialModeDescriptor(
    string ModeId,
    LocalizedText DisplayName,
    IReadOnlyList<StyleMaterialRole> AllowedRoles,
    IReadOnlyList<StyleParameterDescriptor> Parameters,
    IReadOnlyList<StyleTextureSlotDescriptor> TextureInputs,
    LocalizedText? Description = null,
    bool SupportsBoneTracking = false,
    StyleBoneAxisDefaults? BoneAxisDefaults = null,
    IReadOnlyList<string>? LegacyAliases = null);

public enum StyleBoneTrackingMode
{
    FixedWorld = 0,
    SelectedBone = 1
}

public sealed record StyleBoneAxisBinding(
    StyleBoneTrackingMode Mode,
    string? BoneName = null,
    Vector3? LocalForward = null,
    Vector3? LocalUp = null,
    Vector3? FixedWorldForward = null,
    Vector3? FixedWorldRight = null);

public sealed record StyleMaterialBindingData(
    string StyleId,
    string ModeId,
    IReadOnlyDictionary<string, int>? TextureSlots = null,
    IReadOnlyDictionary<string, double>? NumberValues = null,
    IReadOnlyDictionary<string, bool>? BoolValues = null,
    IReadOnlyDictionary<string, float[]>? VectorValues = null,
    IReadOnlyDictionary<string, string>? StringValues = null,
    StyleBoneAxisBinding? BoneBinding = null);

public sealed record StyleSceneTextureRequest(
    int LocalTextureIndex,
    StyleTextureEncoding Encoding = StyleTextureEncoding.Linear,
    StyleTextureMipRule MipRule = StyleTextureMipRule.FromSource,
    int? ExpectedWidth = null,
    int? ExpectedHeight = null,
    bool IsOptional = false);

public sealed record StyleSceneTextureInfo(
    int LocalTextureIndex,
    int ComposedTextureIndex,
    int Width,
    int Height);

public sealed record StyleBoneTransformRequest(
    string BoneName,
    Vector3 LocalForward,
    Vector3 LocalUp);

public sealed record StyleEvaluatedBoneTransform(
    string BoneName,
    Vector3 WorldForward,
    Vector3 WorldRight,
    Vector3 WorldUp,
    Matrix4x4 WorldMatrix);

public sealed record StyleMaterialInspectionContext(
    Guid ObjectId,
    int MaterialIndex,
    string? MaterialName,
    StyleMaterialRole? ExplicitRole,
    bool GuessMaterialRoles,
    IReadOnlyList<StyleMaterialBindingData>? Bindings = null);

public sealed record StyleMaterialClassification(
    StyleMaterialRole ResolvedRole,
    StyleStylizedShadingParameters? StylizedShadingOverride = null,
    StyleMaterialTreatmentParameters? TreatmentOverride = null,
    StyleFaceEyeDefaults? FaceEyeOverride = null,
    Vector4? SurfaceParamsOverride = null);

public sealed record StyleMaterialBindingContext(
    Guid ObjectId,
    int MaterialIndex,
    string? MaterialName,
    StyleMaterialRole ResolvedRole,
    IReadOnlyList<StyleMaterialBindingData> Bindings,
    int SourceTextureCount = 0);

public sealed record StyleValidationResult(
    bool IsValid,
    string? ErrorMessage = null,
    LocalizedText? LocalizedError = null)
{
    public static StyleValidationResult Ok { get; } = new(true);

    public static StyleValidationResult Error(string errorMessage, LocalizedText? localizedError = null)
    {
        ArgumentNullException.ThrowIfNull(errorMessage);
        return new(false, errorMessage, localizedError);
    }

    public static StyleValidationResult Error(LocalizedText localizedError)
    {
        ArgumentNullException.ThrowIfNull(localizedError);
        return new(false, localizedError.DefaultText, localizedError);
    }
}

public sealed record StyleMaterialValidationContext(
    Guid ObjectId,
    int MaterialIndex,
    string? MaterialName,
    StyleMaterialRole ResolvedRole,
    IReadOnlyList<StyleMaterialBindingData> Bindings,
    int SourceTextureCount,
    IReadOnlyList<(int Width, int Height)>? SourceTextureDimensions = null);

public sealed record StyleDrawContext(
    Guid ObjectId,
    int MaterialIndex,
    string? MaterialName,
    StyleMaterialRole ResolvedRole,
    IReadOnlyList<StyleMaterialBindingData> Bindings,
    Vector4 BaseDiffuseColor,
    Vector3 BaseSpecularColor,
    float BaseSpecularPower,
    Vector3 BaseAmbientColor,
    int BaseTextureIndex,
    int SphereTextureIndex,
    int SphereMode,
    int ToonTextureIndex,
    int SubTextureIndex,
    bool IsTransparent,
    Vector3 PrimaryLightDirection,
    Vector3 PrimaryLightColor,
    Vector3 CameraPosition,
    StyleStylizedShadingParameters? StylizedShading,
    StyleMaterialTreatmentParameters? Treatment,
    StyleFaceEyeDefaults? FaceEyeDefaults,
    IReadOnlyList<StyleSceneTextureInfo>? ResolvedTextures = null,
    StyleEvaluatedBoneTransform? EvaluatedBone = null,
    StyleMaterialBindingData? StyleGlobalParameters = null);

public sealed record StyleDrawData(
    string ShaderModuleId,
    ReadOnlyMemory<byte> ConstantBufferData = default,
    IReadOnlyList<StyleTextureBinding>? TextureBindings = null,
    IReadOnlyList<StyleBufferBinding>? BufferBindings = null,
    StyleStylizedShadingParameters? StylizedShadingOverride = null,
    StyleMaterialTreatmentParameters? TreatmentOverride = null,
    StyleFaceEyeDefaults? FaceEyeOverride = null,
    StyleShaderHookMask? HookMaskOverride = null,
    bool? DisableStylizedSphereMaps = null,
    Vector4? ExtraSurfaceParams = null,
    bool? PreserveDirectLightPalette = null);

/// <summary>Extension entry point registered by plugins declaring <see cref="PluginCapabilities.RenderingStyles"/>.</summary>
public interface IRenderingStyleProvider
{
    IReadOnlyList<IRenderingStyleDefinition> GetStyles();
}

/// <summary>Defines a complete stylized rendering mode, its UI parameters, shader modules, and per-draw bindings.</summary>
public interface IRenderingStyleDefinition
{
    string StyleId { get; }
    LocalizedText DisplayName { get; }
    LocalizedText? Description { get; }
    string? Author { get; }
    Version Version { get; }
    bool SupportsMaterialRoleGuessing { get; }
    StyleOutlineDefaults DefaultOutline { get; }
    StyleToonResponseDefaults DefaultToonResponse { get; }
    StyleStylizedShadingParameters DefaultStylizedShading { get; }
    StyleFaceEyeDefaults DefaultFaceEye { get; }
    IReadOnlyDictionary<StyleMaterialRole, StyleMaterialTreatmentParameters> DefaultRoleTreatments { get; }
    IReadOnlyList<StyleMaterialModeDescriptor> MaterialModes { get; }
    IReadOnlyList<StyleParameterDescriptor> StyleParameters { get; }

    IReadOnlyList<StyleShaderModule> GetShaderModules();
    StyleMaterialClassification ClassifyMaterial(StyleMaterialInspectionContext context);
    StyleValidationResult ValidateMaterial(StyleMaterialValidationContext context);
    IReadOnlyList<StyleSceneTextureRequest> GetSceneTextureRequests(StyleMaterialBindingContext context);
    bool RequiresSourceGeometry(StyleMaterialBindingContext context);
    StyleBoneTransformRequest? GetBoneTransformRequest(StyleMaterialBindingContext context);
    StyleDrawData? BuildMaterialDrawData(StyleDrawContext context);
}

/// <summary>Base implementation of <see cref="IRenderingStyleDefinition"/> with sensible defaults and JP/CN/EN role guessing.</summary>
public abstract class RenderingStyleDefinitionBase : IRenderingStyleDefinition
{
    private static readonly StyleOutlineDefaults DefaultOutlineValue = new();
    private static readonly StyleToonResponseDefaults DefaultToonResponseValue = new();
    private static readonly StyleFaceEyeDefaults DefaultFaceEyeValue = new();
    private static readonly StyleStylizedShadingParameters DefaultStylizedShadingValue = new(
        ShadowStrength: 0.5f,
        ShadowValue: 0.85f,
        ShadowHue: 0f,
        ShadowTint: new Vector3(0.95f, 0.9f, 0.95f),
        FaceFlatten: 0.35f,
        WarmColor: new Vector3(1f, 0.96f, 0.9f),
        WarmStrength: 0.1f,
        RimEdge: 0.5f,
        RimStrength: 0f,
        Saturation: 1f,
        Contrast: 1f,
        Brightness: 1f);

    private static readonly IReadOnlyDictionary<StyleMaterialRole, StyleMaterialTreatmentParameters> DefaultRoleTreatmentsValue =
        new Dictionary<StyleMaterialRole, StyleMaterialTreatmentParameters>
        {
            [StyleMaterialRole.Body] = new(),
            [StyleMaterialRole.Face] = new(),
            [StyleMaterialRole.Eye] = new(),
            [StyleMaterialRole.Hair] = new()
        }.AsReadOnly();

    private static readonly string[] BrowKeywords = ["brow", "眉", "まゆ", "マユ"];
    private static readonly string[] EyeKeywords = ["eye", "iris", "pupil", "sclera", "cornea", "瞳", "白目", "黒目", "目玉", "眼球", "眼", "まつげ", "まつ毛", "マツゲ", "まつ", "ハイライト", "lash", "睫毛", "睫", "白眼", "黑眼", "目"];
    private static readonly string[] HairKeywords = ["hair", "ahoge", "ponytail", "twintail", "bangs", "fringe", "髪", "髪の毛", "かみのけ", "ヘア", "アホ毛", "あほ毛", "前髪", "後髪", "後ろ髪", "頭髪", "もみあげ", "ツインテ", "ポニテ", "髪型", "髮", "头发", "前发", "后发", "发丝", "刘海", "劉海", "马尾", "馬尾"];
    private static readonly string[] FaceKeywords = ["face", "顔", "顏", "颜", "かお", "カオ", "フェイス", "脸", "臉", "面部", "脸部", "臉部", "mouth", "tongue", "teeth", "tooth", "lip", "lips", "口", "くち", "マウス", "唇", "舌", "歯", "齿", "齒", "牙", "嘴", "cheek", "blush", "頬", "ほほ", "ほっぺ", "チーク", "照れ", "赤み", "腮", "鼻", "表情", "biaoq"];

    public abstract string StyleId { get; }
    public abstract LocalizedText DisplayName { get; }
    public virtual LocalizedText? Description => null;
    public virtual string? Author => null;
    public virtual Version Version { get; } = new(1, 0, 0);
    public virtual bool SupportsMaterialRoleGuessing => true;
    public virtual StyleOutlineDefaults DefaultOutline => DefaultOutlineValue;
    public virtual StyleToonResponseDefaults DefaultToonResponse => DefaultToonResponseValue;
    public virtual StyleStylizedShadingParameters DefaultStylizedShading => DefaultStylizedShadingValue;
    public virtual StyleFaceEyeDefaults DefaultFaceEye => DefaultFaceEyeValue;
    public virtual IReadOnlyDictionary<StyleMaterialRole, StyleMaterialTreatmentParameters> DefaultRoleTreatments => DefaultRoleTreatmentsValue;
    public virtual IReadOnlyList<StyleMaterialModeDescriptor> MaterialModes => Array.Empty<StyleMaterialModeDescriptor>();
    public virtual IReadOnlyList<StyleParameterDescriptor> StyleParameters => Array.Empty<StyleParameterDescriptor>();

    public virtual IReadOnlyList<StyleShaderModule> GetShaderModules() => Array.Empty<StyleShaderModule>();

    public virtual StyleMaterialClassification ClassifyMaterial(StyleMaterialInspectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new StyleMaterialClassification(ResolveMaterialRole(context));
    }

    public virtual StyleValidationResult ValidateMaterial(StyleMaterialValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return StyleValidationResult.Ok;
    }

    public virtual IReadOnlyList<StyleSceneTextureRequest> GetSceneTextureRequests(StyleMaterialBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Array.Empty<StyleSceneTextureRequest>();
    }

    public virtual bool RequiresSourceGeometry(StyleMaterialBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return false;
    }

    public virtual StyleBoneTransformRequest? GetBoneTransformRequest(StyleMaterialBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return null;
    }

    public virtual StyleDrawData? BuildMaterialDrawData(StyleDrawContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return null;
    }

    protected StyleMaterialRole ResolveMaterialRole(StyleMaterialInspectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ExplicitRole is { } explicitRole)
            return explicitRole;
        if (!context.GuessMaterialRoles || !SupportsMaterialRoleGuessing)
            return StyleMaterialRole.Body;
        return GuessMaterialRole(context.MaterialName);
    }

    /// <summary>Guesses a material role from JP/CN/EN material names, falling back to <see cref="StyleMaterialRole.Body"/>.</summary>
    public static StyleMaterialRole GuessMaterialRole(string? materialName)
    {
        if (string.IsNullOrWhiteSpace(materialName))
            return StyleMaterialRole.Body;

        // Check brow before eye so "eyebrow" maps to Face instead of Eye.
        if (ContainsAnyKeyword(materialName, BrowKeywords))
            return StyleMaterialRole.Face;
        if (ContainsAnyKeyword(materialName, EyeKeywords))
            return StyleMaterialRole.Eye;
        if (ContainsAnyKeyword(materialName, HairKeywords))
            return StyleMaterialRole.Hair;
        if (ContainsAnyKeyword(materialName, FaceKeywords))
            return StyleMaterialRole.Face;

        return StyleMaterialRole.Body;
    }

    private static bool ContainsAnyKeyword(string text, string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
