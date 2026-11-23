using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Fixed universal resource limits for plugin surface shaders using space2 bindings.</summary>
public static class D3D12PluginResourceLimits
{
    public const int MaxTextureSlots = 12;
    public const int MaxStructuredBufferSlots = 4;
    public const int MaxConstantBufferBytes = 4096;
}

/// <summary>Well-defined HLSL hook points and behavioral flags declared by a plugin shader module.</summary>
[Flags]
public enum D3D12StyleShaderHookMask
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
    PreserveDirectLightPalette = 1 << 12,
}

/// <summary>Optional vertex/pixel geometry streams and system-value inputs requested by a plugin shader.</summary>
[Flags]
public enum D3D12StyleGeometryRequirements
{
    None = 0,
    SubTextureUv = 1 << 0,
    FrontFace = 1 << 1,
    PrimitiveId = 1 << 2,
    MikkTangentCorners = 1 << 3,
    RawRadialSeeds = 1 << 4,
    ModelSpacePosition = 1 << 5,
    ModelSpaceNormal = 1 << 6,
}

public enum D3D12PluginTextureEncoding
{
    Linear,
    SrgbColor,
}

public enum D3D12PluginTextureMipRule
{
    FromSource,
    PreserveMips,
    PointNoMip,
    GenerateMips,
}

public enum D3D12PluginTexturePixelFormat
{
    Rgba8Unorm,
    Rgba8UnormSrgb,
    Rgba16Float,
    Rgba32Float,
    R8Unorm,
}

public sealed record D3D12PluginInlineTexture(
    string AssetKey,
    int Width,
    int Height,
    D3D12PluginTexturePixelFormat Format,
    ReadOnlyMemory<byte> Pixels,
    D3D12PluginTextureMipRule MipRule = D3D12PluginTextureMipRule.FromSource)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(AssetKey);
        if (Width is < 1 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(Width), "Inline texture width must be in 1..16384.");
        if (Height is < 1 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(Height), "Inline texture height must be in 1..16384.");
        if (!Enum.IsDefined(Format))
            throw new ArgumentOutOfRangeException(nameof(Format));
        if (!Enum.IsDefined(MipRule))
            throw new ArgumentOutOfRangeException(nameof(MipRule));
        long expectedBytes = (long)Width * Height * BytesPerPixel(Format);
        if (Pixels.Length != expectedBytes)
            throw new ArgumentException($"Inline texture '{AssetKey}' expects {expectedBytes} pixel bytes for {Width}x{Height} {Format}, got {Pixels.Length}.", nameof(Pixels));
    }

    internal static int BytesPerPixel(D3D12PluginTexturePixelFormat format) => format switch
    {
        D3D12PluginTexturePixelFormat.Rgba8Unorm => 4,
        D3D12PluginTexturePixelFormat.Rgba8UnormSrgb => 4,
        D3D12PluginTexturePixelFormat.Rgba16Float => 8,
        D3D12PluginTexturePixelFormat.Rgba32Float => 16,
        D3D12PluginTexturePixelFormat.R8Unorm => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    internal static bool Equivalent(D3D12PluginInlineTexture? a, D3D12PluginInlineTexture? b)
        => ReferenceEquals(a, b) || (a is not null && b is not null
            && string.Equals(a.AssetKey, b.AssetKey, StringComparison.Ordinal)
            && a.Width == b.Width
            && a.Height == b.Height
            && a.Format == b.Format
            && a.MipRule == b.MipRule
            && a.Pixels.Span.SequenceEqual(b.Pixels.Span));
}

public sealed record D3D12PluginTextureBinding
{
    private readonly int slotIndex;

    public D3D12PluginTextureBinding(
        int SlotIndex,
        int SceneTextureIndex = -1,
        D3D12PluginInlineTexture? InlineTexture = null,
        D3D12PluginTextureEncoding Encoding = D3D12PluginTextureEncoding.Linear,
        D3D12PluginTextureMipRule MipRule = D3D12PluginTextureMipRule.FromSource)
    {
        this.SlotIndex = SlotIndex;
        this.SceneTextureIndex = SceneTextureIndex;
        this.InlineTexture = InlineTexture;
        this.Encoding = Encoding;
        this.MipRule = MipRule;
    }

    public int SlotIndex
    {
        get => slotIndex;
        init
        {
            if (value < 0 || value >= D3D12PluginResourceLimits.MaxTextureSlots)
                throw new ArgumentOutOfRangeException(nameof(SlotIndex),
                    $"Plugin texture slot index must be in 0..{D3D12PluginResourceLimits.MaxTextureSlots - 1}.");
            slotIndex = value;
        }
    }

    public int SceneTextureIndex { get; init; }
    public D3D12PluginInlineTexture? InlineTexture { get; init; }
    public D3D12PluginTextureEncoding Encoding { get; init; }
    public D3D12PluginTextureMipRule MipRule { get; init; }

    public void Validate()
    {
        if (SlotIndex < 0 || SlotIndex >= D3D12PluginResourceLimits.MaxTextureSlots)
            throw new ArgumentOutOfRangeException(nameof(SlotIndex),
                $"Plugin texture slot index must be in 0..{D3D12PluginResourceLimits.MaxTextureSlots - 1}.");
        if (SceneTextureIndex < -1)
            throw new ArgumentOutOfRangeException(nameof(SceneTextureIndex));
        if (SceneTextureIndex >= 0 && InlineTexture is not null)
            throw new ArgumentException("A plugin texture binding cannot specify both SceneTextureIndex and InlineTexture.");
        if (!Enum.IsDefined(Encoding))
            throw new ArgumentOutOfRangeException(nameof(Encoding));
        if (!Enum.IsDefined(MipRule))
            throw new ArgumentOutOfRangeException(nameof(MipRule));
        InlineTexture?.Validate();
    }

    internal static bool Equivalent(D3D12PluginTextureBinding a, D3D12PluginTextureBinding b, bool compareSceneIndex)
        => a.SlotIndex == b.SlotIndex
            && (!compareSceneIndex || a.SceneTextureIndex == b.SceneTextureIndex)
            && a.Encoding == b.Encoding
            && a.MipRule == b.MipRule
            && D3D12PluginInlineTexture.Equivalent(a.InlineTexture, b.InlineTexture);
}

public enum D3D12PluginBufferKind
{
    MikkTangentCorners,
    CustomStructured,
}

public sealed record D3D12PluginBufferBinding
{
    private readonly int slotIndex;

    public D3D12PluginBufferBinding(
        int SlotIndex,
        D3D12PluginBufferKind Kind,
        string? DataKey = null,
        int ElementStrideBytes = 0,
        int ElementCount = 0,
        ReadOnlyMemory<byte> Data = default)
    {
        this.SlotIndex = SlotIndex;
        this.Kind = Kind;
        this.DataKey = DataKey;
        this.ElementStrideBytes = ElementStrideBytes;
        this.ElementCount = ElementCount;
        this.Data = Data;
    }

    public int SlotIndex
    {
        get => slotIndex;
        init
        {
            if (value < 0 || value >= D3D12PluginResourceLimits.MaxStructuredBufferSlots)
                throw new ArgumentOutOfRangeException(nameof(SlotIndex),
                    $"Plugin structured buffer slot index must be in 0..{D3D12PluginResourceLimits.MaxStructuredBufferSlots - 1}.");
            slotIndex = value;
        }
    }

    public D3D12PluginBufferKind Kind { get; init; }
    public string? DataKey { get; init; }
    public int ElementStrideBytes { get; init; }
    public int ElementCount { get; init; }
    public ReadOnlyMemory<byte> Data { get; init; }

    public void Validate()
    {
        if (SlotIndex < 0 || SlotIndex >= D3D12PluginResourceLimits.MaxStructuredBufferSlots)
            throw new ArgumentOutOfRangeException(nameof(SlotIndex),
                $"Plugin structured buffer slot index must be in 0..{D3D12PluginResourceLimits.MaxStructuredBufferSlots - 1}.");
        if (!Enum.IsDefined(Kind))
            throw new ArgumentOutOfRangeException(nameof(Kind));
        if (ElementStrideBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(ElementStrideBytes));
        if (ElementCount < 0)
            throw new ArgumentOutOfRangeException(nameof(ElementCount));

        if (Kind == D3D12PluginBufferKind.CustomStructured)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(DataKey);
            if (Data.IsEmpty)
            {
                if (ElementStrideBytes <= 0 || ElementCount <= 0)
                    throw new ArgumentException("CustomStructured buffer without inline Data requires positive ElementStrideBytes and ElementCount.");
            }
            else if (ElementStrideBytes > 0 && ElementCount > 0)
            {
                long expected = (long)ElementStrideBytes * ElementCount;
                if (Data.Length != expected)
                    throw new ArgumentException($"CustomStructured buffer '{DataKey}' expects {expected} bytes, got {Data.Length}.", nameof(Data));
            }
        }
        else
        {
            _ = D3D12TangentCornerGeometry.ResolveUvChannel(this);
            if (!Data.IsEmpty && Data.Length % 64 != 0)
                throw new ArgumentException("Explicit MikkTangentCorners buffer bytes must be a multiple of 64 bytes.", nameof(Data));
        }
    }

    internal static bool Equivalent(D3D12PluginBufferBinding a, D3D12PluginBufferBinding b)
        => a.SlotIndex == b.SlotIndex
            && a.Kind == b.Kind
            && string.Equals(a.DataKey, b.DataKey, StringComparison.Ordinal)
            && a.ElementStrideBytes == b.ElementStrideBytes
            && a.ElementCount == b.ElementCount
            && a.Data.Span.SequenceEqual(b.Data.Span);
}

public sealed record D3D12PluginShaderModule(
    string ModuleId,
    string? HlslSource,
    D3D12StyleShaderHookMask Hooks = D3D12StyleShaderHookMask.AllowStylizedShadingWrapper,
    D3D12StyleGeometryRequirements GeometryRequirements = D3D12StyleGeometryRequirements.None,
    IReadOnlyDictionary<string, string>? Defines = null,
    string? CacheKeySuffix = null,
    bool DisableStylizedSphereMaps = false,
    string? ContentHash = null)
{
    private string? computedHash;

    internal D3D12StyleShaderHookMask EffectiveHooks
    {
        get
        {
            var mask = Hooks;
            if (DisableStylizedSphereMaps)
                mask |= D3D12StyleShaderHookMask.DisableStylizedSphereMaps;
            if (!string.IsNullOrWhiteSpace(HlslSource))
                mask |= D3D12StyleShaderHookMask.HasStyleShaderCode;
            return mask;
        }
    }

    internal string EffectiveContentHash => !string.IsNullOrEmpty(ContentHash) ? ContentHash : (computedHash ??= ComputeContentHash());

    public void Validate()
    {
        D3D12StylePresetDefinition.ValidateIdentifier(ModuleId, nameof(ModuleId));
        var hooks = EffectiveHooks;
        bool needsCode = (hooks & (
            D3D12StyleShaderHookMask.LightResponse |
            D3D12StyleShaderHookMask.PaletteFactor |
            D3D12StyleShaderHookMask.Specular |
            D3D12StyleShaderHookMask.AlbedoTransform |
            D3D12StyleShaderHookMask.PrimarySurfacePost |
            D3D12StyleShaderHookMask.FinalEntryPre |
            D3D12StyleShaderHookMask.FinalEntryPost |
            D3D12StyleShaderHookMask.HasStyleShaderCode)) != 0;
        if (needsCode && string.IsNullOrWhiteSpace(HlslSource))
            throw new ArgumentException($"Shader module '{ModuleId}' declares shader hooks without HlslSource.", nameof(HlslSource));
        if (Defines is not null)
        {
            foreach (var (key, value) in Defines)
            {
                if (string.IsNullOrWhiteSpace(key) || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') || char.IsAsciiDigit(key[0]))
                    throw new ArgumentException($"Invalid shader macro identifier '{key}'.", nameof(Defines));
                if (value is null || value.Contains('\n') || value.Contains('\r'))
                    throw new ArgumentException($"Shader macro '{key}' value cannot be null or multiline.", nameof(Defines));
            }
        }
    }

    private string ComputeContentHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        static void FeedString(IncrementalHash h, string? s)
        {
            byte[] bytes = s is null ? [] : Encoding.UTF8.GetBytes(s);
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(len, s is null ? -1 : bytes.Length);
            h.AppendData(len);
            if (bytes.Length > 0) h.AppendData(bytes);
        }
        static void FeedInt(IncrementalHash h, int value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(buf, value);
            h.AppendData(buf);
        }

        FeedString(hash, ModuleId);
        FeedString(hash, HlslSource);
        FeedInt(hash, (int)EffectiveHooks);
        FeedInt(hash, (int)GeometryRequirements);
        FeedString(hash, CacheKeySuffix);
        if (Defines is not null)
        {
            foreach (var pair in Defines.OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                FeedString(hash, pair.Key);
                FeedString(hash, pair.Value);
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

public sealed record D3D12PluginDrawData(
    string ShaderModuleId,
    ReadOnlyMemory<byte> ConstantBufferData = default,
    IReadOnlyList<D3D12PluginTextureBinding>? TextureBindings = null,
    IReadOnlyList<D3D12PluginBufferBinding>? BufferBindings = null,
    D3D12StylizedShading? StylizedShadingOverride = null,
    D3D12PmxMaterialTreatment? TreatmentOverride = null,
    D3D12StyleShaderHookMask? HookMaskOverride = null,
    bool? DisableStylizedSphereMaps = null,
    Vector4? ExtraSurfaceParams = null,
    bool? PreserveDirectLightPalette = null)
{
    public void Validate()
    {
        D3D12StylePresetDefinition.ValidateIdentifier(ShaderModuleId, nameof(ShaderModuleId));
        if (ConstantBufferData.Length > D3D12PluginResourceLimits.MaxConstantBufferBytes)
            throw new ArgumentOutOfRangeException(nameof(ConstantBufferData),
                $"Plugin constant buffer exceeds {D3D12PluginResourceLimits.MaxConstantBufferBytes} bytes.");
        if (TextureBindings is not null)
        {
            if (TextureBindings.Count > D3D12PluginResourceLimits.MaxTextureSlots)
                throw new ArgumentOutOfRangeException(nameof(TextureBindings),
                    $"Plugin texture bindings exceed {D3D12PluginResourceLimits.MaxTextureSlots} slots.");
            int usedMask = 0;
            foreach (var binding in TextureBindings)
            {
                if (binding is null) throw new ArgumentException("TextureBindings cannot contain null.", nameof(TextureBindings));
                binding.Validate();
                int bit = 1 << binding.SlotIndex;
                if ((usedMask & bit) != 0)
                    throw new ArgumentException($"Duplicate plugin texture binding for slot {binding.SlotIndex}.", nameof(TextureBindings));
                usedMask |= bit;
            }
        }
        if (BufferBindings is not null)
        {
            if (BufferBindings.Count > D3D12PluginResourceLimits.MaxStructuredBufferSlots)
                throw new ArgumentOutOfRangeException(nameof(BufferBindings),
                    $"Plugin buffer bindings exceed {D3D12PluginResourceLimits.MaxStructuredBufferSlots} slots.");
            int usedMask = 0;
            foreach (var binding in BufferBindings)
            {
                if (binding is null) throw new ArgumentException("BufferBindings cannot contain null.", nameof(BufferBindings));
                binding.Validate();
                int bit = 1 << binding.SlotIndex;
                if ((usedMask & bit) != 0)
                    throw new ArgumentException($"Duplicate plugin structured buffer binding for slot {binding.SlotIndex}.", nameof(BufferBindings));
                usedMask |= bit;
            }
        }
        StylizedShadingOverride?.Validate();
        TreatmentOverride?.Validate();
        if (ExtraSurfaceParams is { } extra
            && (!float.IsFinite(extra.X) || !float.IsFinite(extra.Y) || !float.IsFinite(extra.Z) || !float.IsFinite(extra.W)))
            throw new ArgumentOutOfRangeException(nameof(ExtraSurfaceParams), "ExtraSurfaceParams must be finite.");
    }
}

public sealed record D3D12PluginStyleRegistration(
    string StyleId,
    string DisplayName,
    CharacterOutlineStyle? DefaultOutline = null,
    CharacterToonResponse? DefaultToonResponse = null,
    D3D12StylizedShading? DefaultStylizedShading = null,
    IReadOnlyDictionary<string, D3D12PluginShaderModule>? ShaderModules = null,
    string? Diagnostic = null)
{
    public bool RespectMaterialEdgeFlag { get; init; }

    public void Validate()
    {
        D3D12StylePresetDefinition.ValidateIdentifier(StyleId, nameof(StyleId));
        if (string.Equals(StyleId, D3D12SurfaceStyleMap.DefaultStyleId, StringComparison.Ordinal))
            throw new ArgumentException($"Plugin style cannot override reserved built-in '{D3D12SurfaceStyleMap.DefaultStyleId}'.", nameof(StyleId));
        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > D3D12StylePresetDefinition.MaxNameLength || DisplayName.Any(char.IsControl))
            throw new ArgumentException("Plugin DisplayName must contain 1 to 128 characters without control characters.", nameof(DisplayName));
        if (DefaultOutline is not null) CharacterOutlineMath.Validate(DefaultOutline);
        if (DefaultToonResponse is not null) CharacterToonMath.Validate(DefaultToonResponse);
        DefaultStylizedShading?.Validate();
        if (ShaderModules is not null)
        {
            foreach (var (key, module) in ShaderModules)
            {
                if (module is null)
                    throw new ArgumentException("ShaderModules cannot contain null values.", nameof(ShaderModules));
                module.Validate();
                if (!string.Equals(key, module.ModuleId, StringComparison.Ordinal))
                    throw new ArgumentException($"Shader module dictionary key '{key}' must match ModuleId '{module.ModuleId}'.", nameof(ShaderModules));
            }
        }
    }

    internal static IReadOnlyDictionary<string, D3D12PluginStyleRegistration> BuildDictionary(IEnumerable<D3D12PluginStyleRegistration>? plugins)
    {
        if (plugins is null) return new ReadOnlyDictionary<string, D3D12PluginStyleRegistration>(new Dictionary<string, D3D12PluginStyleRegistration>(StringComparer.Ordinal));
        var map = new Dictionary<string, D3D12PluginStyleRegistration>(StringComparer.Ordinal);
        foreach (var plugin in plugins)
        {
            if (plugin is null) throw new ArgumentException("Plugin registrations cannot contain null entries.", nameof(plugins));
            plugin.Validate();
            if (!map.TryAdd(plugin.StyleId, plugin))
                throw new ArgumentException($"Duplicate plugin style registration '{plugin.StyleId}'.", nameof(plugins));
        }
        return new ReadOnlyDictionary<string, D3D12PluginStyleRegistration>(map);
    }
}
