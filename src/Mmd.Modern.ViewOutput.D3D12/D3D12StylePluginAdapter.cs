using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>
/// Immutable snapshot of active plugin-provided <see cref="IRenderingStyleDefinition"/> instances
/// and their translated <see cref="D3D12PluginStyleRegistration"/> descriptors.
/// </summary>
public sealed class D3D12ViewStyleCatalog
{
    private readonly IReadOnlyDictionary<string, IRenderingStyleDefinition> stylesById;
    private readonly IReadOnlyDictionary<string, D3D12PluginStyleRegistration> registrationsById;

    public static D3D12ViewStyleCatalog Empty { get; } = new(null);

    public IReadOnlyList<IRenderingStyleDefinition> Styles { get; }
    public IReadOnlyList<D3D12PluginStyleRegistration> Registrations { get; }

    public D3D12ViewStyleCatalog(IEnumerable<IRenderingStyleDefinition>? styles = null, string? displayLanguage = null)
    {
        var styleList = new List<IRenderingStyleDefinition>();
        var regList = new List<D3D12PluginStyleRegistration>();
        var styleMap = new Dictionary<string, IRenderingStyleDefinition>(StringComparer.Ordinal);
        var regMap = new Dictionary<string, D3D12PluginStyleRegistration>(StringComparer.Ordinal);

        if (styles is not null)
        {
            foreach (var style in styles)
            {
                if (style is null)
                    throw new ArgumentException("Style catalog cannot contain null entries.", nameof(styles));
                var registration = D3D12StylePluginAdapter.ToRegistration(style, displayLanguage);
                if (!styleMap.TryAdd(style.StyleId, style))
                    throw new ArgumentException($"Duplicate rendering style definition '{style.StyleId}'.", nameof(styles));
                regMap.Add(registration.StyleId, registration);
                styleList.Add(style);
                regList.Add(registration);
            }
        }

        Styles = Array.AsReadOnly(styleList.ToArray());
        Registrations = Array.AsReadOnly(regList.ToArray());
        stylesById = new ReadOnlyDictionary<string, IRenderingStyleDefinition>(styleMap);
        registrationsById = new ReadOnlyDictionary<string, D3D12PluginStyleRegistration>(regMap);
    }

    public IRenderingStyleDefinition? FindStyle(string styleId)
    {
        D3D12StylePresetDefinition.ValidateIdentifier(styleId, nameof(styleId));
        return stylesById.TryGetValue(styleId, out var style) ? style : null;
    }

    public D3D12PluginStyleRegistration? FindRegistration(string styleId)
    {
        D3D12StylePresetDefinition.ValidateIdentifier(styleId, nameof(styleId));
        return registrationsById.TryGetValue(styleId, out var reg) ? reg : null;
    }

    public D3D12StylePresetCatalog ToD3D12PresetCatalog(IEnumerable<D3D12StylePresetResult>? local = null)
        => D3D12StylePresetCatalog.WithPlugins(Registrations, local);
}

/// <summary>
/// Pure translation and defensive-copy helpers bridging <see cref="Mmd.Modern.Plugins.Contracts"/>
/// and <see cref="Mmd.Modern.Rendering.D3D12"/> types.
/// </summary>
public static class D3D12StylePluginAdapter
{
    private const StyleShaderHookMask AllContractHooks =
        StyleShaderHookMask.LightResponse |
        StyleShaderHookMask.PaletteFactor |
        StyleShaderHookMask.Specular |
        StyleShaderHookMask.AlbedoTransform |
        StyleShaderHookMask.PrimarySurfacePost |
        StyleShaderHookMask.FinalEntryPre |
        StyleShaderHookMask.FinalEntryPost |
        StyleShaderHookMask.AllowStylizedShadingWrapper |
        StyleShaderHookMask.DisableStylizedSphereMaps |
        StyleShaderHookMask.HasStyleShaderCode |
        StyleShaderHookMask.RequiresOutlineColorScale |
        StyleShaderHookMask.RequiresSkinShadeBlend |
        StyleShaderHookMask.PreserveDirectLightPalette;

    private const D3D12StyleShaderHookMask AllD3D12Hooks =
        D3D12StyleShaderHookMask.LightResponse |
        D3D12StyleShaderHookMask.PaletteFactor |
        D3D12StyleShaderHookMask.Specular |
        D3D12StyleShaderHookMask.AlbedoTransform |
        D3D12StyleShaderHookMask.PrimarySurfacePost |
        D3D12StyleShaderHookMask.FinalEntryPre |
        D3D12StyleShaderHookMask.FinalEntryPost |
        D3D12StyleShaderHookMask.AllowStylizedShadingWrapper |
        D3D12StyleShaderHookMask.DisableStylizedSphereMaps |
        D3D12StyleShaderHookMask.HasStyleShaderCode |
        D3D12StyleShaderHookMask.RequiresOutlineColorScale |
        D3D12StyleShaderHookMask.RequiresSkinShadeBlend |
        D3D12StyleShaderHookMask.PreserveDirectLightPalette;

    private const StyleGeometryRequirements AllContractGeometry =
        StyleGeometryRequirements.SubTextureUv |
        StyleGeometryRequirements.FrontFace |
        StyleGeometryRequirements.PrimitiveId |
        StyleGeometryRequirements.MikkTangentCorners |
        StyleGeometryRequirements.RawRadialSeeds |
        StyleGeometryRequirements.ModelSpacePosition |
        StyleGeometryRequirements.ModelSpaceNormal;

    private const D3D12StyleGeometryRequirements AllD3D12Geometry =
        D3D12StyleGeometryRequirements.SubTextureUv |
        D3D12StyleGeometryRequirements.FrontFace |
        D3D12StyleGeometryRequirements.PrimitiveId |
        D3D12StyleGeometryRequirements.MikkTangentCorners |
        D3D12StyleGeometryRequirements.RawRadialSeeds |
        D3D12StyleGeometryRequirements.ModelSpacePosition |
        D3D12StyleGeometryRequirements.ModelSpaceNormal;

    public static D3D12PluginStyleRegistration ToRegistration(IRenderingStyleDefinition style, string? displayLanguage = null)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(style.DisplayName);
        ArgumentNullException.ThrowIfNull(style.DefaultOutline);
        ArgumentNullException.ThrowIfNull(style.DefaultToonResponse);
        ArgumentNullException.ThrowIfNull(style.DefaultStylizedShading);
        ArgumentNullException.ThrowIfNull(style.DefaultFaceEye);
        ArgumentNullException.ThrowIfNull(style.DefaultRoleTreatments);

        ValidateFaceEye(style.DefaultFaceEye);
        foreach (var pair in style.DefaultRoleTreatments)
        {
            if (!Enum.IsDefined(pair.Key))
                throw new ArgumentOutOfRangeException(nameof(style), "DefaultRoleTreatments contains an undefined role.");
            if (pair.Value is null)
                throw new ArgumentException("DefaultRoleTreatments cannot contain null values.", nameof(style));
            ToD3D12Treatment(pair.Value).Validate();
        }

        string resolvedName = style.DisplayName.Resolve(displayLanguage);
        if (string.IsNullOrWhiteSpace(resolvedName))
            resolvedName = style.DisplayName.DefaultText;

        var rawModules = style.GetShaderModules() ?? Array.Empty<StyleShaderModule>();
        var modules = new Dictionary<string, D3D12PluginShaderModule>(StringComparer.Ordinal);
        foreach (var rawModule in rawModules)
        {
            if (rawModule is null)
                throw new ArgumentException($"Style '{style.StyleId}' returned a null shader module.", nameof(style));
            var converted = ToD3D12ShaderModule(rawModule);
            if (!modules.TryAdd(converted.ModuleId, converted))
                throw new ArgumentException($"Style '{style.StyleId}' declared duplicate shader module '{converted.ModuleId}'.", nameof(style));
        }

        var registration = new D3D12PluginStyleRegistration(
            StyleId: style.StyleId,
            DisplayName: resolvedName,
            DefaultOutline: ToD3D12Outline(style.DefaultOutline),
            DefaultToonResponse: ToD3D12ToonResponse(style.DefaultToonResponse),
            DefaultStylizedShading: ToD3D12StylizedShading(style.DefaultStylizedShading),
            ShaderModules: modules.Count == 0 ? null : new ReadOnlyDictionary<string, D3D12PluginShaderModule>(modules))
        { RespectMaterialEdgeFlag = style.DefaultOutline.RespectMaterialEdgeFlag };
        registration.Validate();
        return registration;
    }

    public static D3D12PluginShaderModule ToD3D12ShaderModule(StyleShaderModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        IReadOnlyDictionary<string, string>? detachedDefines = null;
        if (module.Defines is not null)
        {
            var copy = new Dictionary<string, string>(module.Defines.Count, StringComparer.Ordinal);
            foreach (var (key, value) in module.Defines)
            {
                if (key is null)
                    throw new ArgumentException("Shader module define keys cannot be null.", nameof(module));
                if (value is null)
                    throw new ArgumentException($"Shader macro '{key}' value cannot be null.", nameof(module));
                if (!copy.TryAdd(key, value))
                    throw new ArgumentException($"Duplicate shader macro '{key}'.", nameof(module));
            }
            detachedDefines = new ReadOnlyDictionary<string, string>(copy);
        }

        string contentHash = ComputeShaderContentHash(module.HlslSource, detachedDefines, module.CacheKeySuffix);
        var converted = new D3D12PluginShaderModule(
            ModuleId: module.ModuleId,
            HlslSource: module.HlslSource,
            Hooks: ToD3D12HookMask(module.Hooks),
            GeometryRequirements: ToD3D12GeometryRequirements(module.GeometryRequirements),
            Defines: detachedDefines,
            CacheKeySuffix: module.CacheKeySuffix,
            DisableStylizedSphereMaps: module.DisableStylizedSphereMaps,
            ContentHash: contentHash);
        converted.Validate();
        return converted;
    }

    public static string ComputeShaderContentHash(
        string? hlslSource,
        IReadOnlyDictionary<string, string>? defines = null,
        string? cacheKeySuffix = null)
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

        FeedString(hash, hlslSource);
        if (defines is not null && defines.Count > 0)
        {
            foreach (var pair in defines.OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                FeedString(hash, pair.Key);
                FeedString(hash, pair.Value);
            }
        }
        if (!string.IsNullOrEmpty(cacheKeySuffix))
            FeedString(hash, cacheKeySuffix);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static D3D12CharacterMaterialRole ToD3D12Role(StyleMaterialRole role) => role switch
    {
        StyleMaterialRole.Body => D3D12CharacterMaterialRole.Body,
        StyleMaterialRole.Face => D3D12CharacterMaterialRole.Face,
        StyleMaterialRole.Eye => D3D12CharacterMaterialRole.Eye,
        StyleMaterialRole.Hair => D3D12CharacterMaterialRole.Hair,
        _ => throw new ArgumentOutOfRangeException(nameof(role), $"Unknown style material role '{role}'."),
    };

    public static StyleMaterialRole ToContractRole(D3D12CharacterMaterialRole role) => role switch
    {
        D3D12CharacterMaterialRole.Body => StyleMaterialRole.Body,
        D3D12CharacterMaterialRole.Face => StyleMaterialRole.Face,
        D3D12CharacterMaterialRole.Eye => StyleMaterialRole.Eye,
        D3D12CharacterMaterialRole.Hair => StyleMaterialRole.Hair,
        _ => throw new ArgumentOutOfRangeException(nameof(role), $"Unknown character material role '{role}'."),
    };

    public static D3D12StyleShaderHookMask ToD3D12HookMask(StyleShaderHookMask mask)
    {
        if ((mask & ~AllContractHooks) != 0)
            throw new ArgumentOutOfRangeException(nameof(mask), "Unknown StyleShaderHookMask flags.");
        D3D12StyleShaderHookMask result = D3D12StyleShaderHookMask.None;
        if (mask.HasFlag(StyleShaderHookMask.LightResponse)) result |= D3D12StyleShaderHookMask.LightResponse;
        if (mask.HasFlag(StyleShaderHookMask.PaletteFactor)) result |= D3D12StyleShaderHookMask.PaletteFactor;
        if (mask.HasFlag(StyleShaderHookMask.Specular)) result |= D3D12StyleShaderHookMask.Specular;
        if (mask.HasFlag(StyleShaderHookMask.AlbedoTransform)) result |= D3D12StyleShaderHookMask.AlbedoTransform;
        if (mask.HasFlag(StyleShaderHookMask.PrimarySurfacePost)) result |= D3D12StyleShaderHookMask.PrimarySurfacePost;
        if (mask.HasFlag(StyleShaderHookMask.FinalEntryPre)) result |= D3D12StyleShaderHookMask.FinalEntryPre;
        if (mask.HasFlag(StyleShaderHookMask.FinalEntryPost)) result |= D3D12StyleShaderHookMask.FinalEntryPost;
        if (mask.HasFlag(StyleShaderHookMask.AllowStylizedShadingWrapper)) result |= D3D12StyleShaderHookMask.AllowStylizedShadingWrapper;
        if (mask.HasFlag(StyleShaderHookMask.DisableStylizedSphereMaps)) result |= D3D12StyleShaderHookMask.DisableStylizedSphereMaps;
        if (mask.HasFlag(StyleShaderHookMask.HasStyleShaderCode)) result |= D3D12StyleShaderHookMask.HasStyleShaderCode;
        if (mask.HasFlag(StyleShaderHookMask.RequiresOutlineColorScale)) result |= D3D12StyleShaderHookMask.RequiresOutlineColorScale;
        if (mask.HasFlag(StyleShaderHookMask.RequiresSkinShadeBlend)) result |= D3D12StyleShaderHookMask.RequiresSkinShadeBlend;
        if (mask.HasFlag(StyleShaderHookMask.PreserveDirectLightPalette)) result |= D3D12StyleShaderHookMask.PreserveDirectLightPalette;
        return result;
    }

    public static StyleShaderHookMask ToContractHookMask(D3D12StyleShaderHookMask mask)
    {
        if ((mask & ~AllD3D12Hooks) != 0)
            throw new ArgumentOutOfRangeException(nameof(mask), "Unknown D3D12StyleShaderHookMask flags.");
        StyleShaderHookMask result = StyleShaderHookMask.None;
        if (mask.HasFlag(D3D12StyleShaderHookMask.LightResponse)) result |= StyleShaderHookMask.LightResponse;
        if (mask.HasFlag(D3D12StyleShaderHookMask.PaletteFactor)) result |= StyleShaderHookMask.PaletteFactor;
        if (mask.HasFlag(D3D12StyleShaderHookMask.Specular)) result |= StyleShaderHookMask.Specular;
        if (mask.HasFlag(D3D12StyleShaderHookMask.AlbedoTransform)) result |= StyleShaderHookMask.AlbedoTransform;
        if (mask.HasFlag(D3D12StyleShaderHookMask.PrimarySurfacePost)) result |= StyleShaderHookMask.PrimarySurfacePost;
        if (mask.HasFlag(D3D12StyleShaderHookMask.FinalEntryPre)) result |= StyleShaderHookMask.FinalEntryPre;
        if (mask.HasFlag(D3D12StyleShaderHookMask.FinalEntryPost)) result |= StyleShaderHookMask.FinalEntryPost;
        if (mask.HasFlag(D3D12StyleShaderHookMask.AllowStylizedShadingWrapper)) result |= StyleShaderHookMask.AllowStylizedShadingWrapper;
        if (mask.HasFlag(D3D12StyleShaderHookMask.DisableStylizedSphereMaps)) result |= StyleShaderHookMask.DisableStylizedSphereMaps;
        if (mask.HasFlag(D3D12StyleShaderHookMask.HasStyleShaderCode)) result |= StyleShaderHookMask.HasStyleShaderCode;
        if (mask.HasFlag(D3D12StyleShaderHookMask.RequiresOutlineColorScale)) result |= StyleShaderHookMask.RequiresOutlineColorScale;
        if (mask.HasFlag(D3D12StyleShaderHookMask.RequiresSkinShadeBlend)) result |= StyleShaderHookMask.RequiresSkinShadeBlend;
        if (mask.HasFlag(D3D12StyleShaderHookMask.PreserveDirectLightPalette)) result |= StyleShaderHookMask.PreserveDirectLightPalette;
        return result;
    }

    public static D3D12StyleGeometryRequirements ToD3D12GeometryRequirements(StyleGeometryRequirements requirements)
    {
        if ((requirements & ~AllContractGeometry) != 0)
            throw new ArgumentOutOfRangeException(nameof(requirements), "Unknown StyleGeometryRequirements flags.");
        D3D12StyleGeometryRequirements result = D3D12StyleGeometryRequirements.None;
        if (requirements.HasFlag(StyleGeometryRequirements.SubTextureUv)) result |= D3D12StyleGeometryRequirements.SubTextureUv;
        if (requirements.HasFlag(StyleGeometryRequirements.FrontFace)) result |= D3D12StyleGeometryRequirements.FrontFace;
        if (requirements.HasFlag(StyleGeometryRequirements.PrimitiveId)) result |= D3D12StyleGeometryRequirements.PrimitiveId;
        if (requirements.HasFlag(StyleGeometryRequirements.MikkTangentCorners)) result |= D3D12StyleGeometryRequirements.MikkTangentCorners;
        if (requirements.HasFlag(StyleGeometryRequirements.RawRadialSeeds)) result |= D3D12StyleGeometryRequirements.RawRadialSeeds;
        if (requirements.HasFlag(StyleGeometryRequirements.ModelSpacePosition)) result |= D3D12StyleGeometryRequirements.ModelSpacePosition;
        if (requirements.HasFlag(StyleGeometryRequirements.ModelSpaceNormal)) result |= D3D12StyleGeometryRequirements.ModelSpaceNormal;
        return result;
    }

    public static StyleGeometryRequirements ToContractGeometryRequirements(D3D12StyleGeometryRequirements requirements)
    {
        if ((requirements & ~AllD3D12Geometry) != 0)
            throw new ArgumentOutOfRangeException(nameof(requirements), "Unknown D3D12StyleGeometryRequirements flags.");
        StyleGeometryRequirements result = StyleGeometryRequirements.None;
        if (requirements.HasFlag(D3D12StyleGeometryRequirements.SubTextureUv)) result |= StyleGeometryRequirements.SubTextureUv;
        if (requirements.HasFlag(D3D12StyleGeometryRequirements.FrontFace)) result |= StyleGeometryRequirements.FrontFace;
        if (requirements.HasFlag(D3D12StyleGeometryRequirements.PrimitiveId)) result |= StyleGeometryRequirements.PrimitiveId;
        if (requirements.HasFlag(D3D12StyleGeometryRequirements.MikkTangentCorners)) result |= StyleGeometryRequirements.MikkTangentCorners;
        if (requirements.HasFlag(D3D12StyleGeometryRequirements.RawRadialSeeds)) result |= StyleGeometryRequirements.RawRadialSeeds;
        if (requirements.HasFlag(D3D12StyleGeometryRequirements.ModelSpacePosition)) result |= StyleGeometryRequirements.ModelSpacePosition;
        if (requirements.HasFlag(D3D12StyleGeometryRequirements.ModelSpaceNormal)) result |= StyleGeometryRequirements.ModelSpaceNormal;
        return result;
    }

    public static CharacterOutlineStyle ToD3D12Outline(StyleOutlineDefaults outline)
    {
        ArgumentNullException.ThrowIfNull(outline);
        var result = new CharacterOutlineStyle(outline.Width, outline.Color, outline.Enabled);
        CharacterOutlineMath.Validate(result);
        return result;
    }

    public static CharacterToonResponse ToD3D12ToonResponse(StyleToonResponseDefaults toon)
    {
        ArgumentNullException.ThrowIfNull(toon);
        var result = new CharacterToonResponse(toon.Threshold, toon.TransitionWidth);
        CharacterToonMath.Validate(result);
        return result;
    }

    public static D3D12StylizedShading ToD3D12StylizedShading(StyleStylizedShadingParameters shading)
    {
        ArgumentNullException.ThrowIfNull(shading);
        var result = new D3D12StylizedShading(
            shading.ShadowStrength,
            shading.ShadowValue,
            shading.ShadowHue,
            shading.ShadowTint,
            shading.FaceFlatten,
            shading.WarmColor,
            shading.WarmStrength,
            shading.RimEdge,
            shading.RimStrength,
            shading.Saturation,
            shading.Contrast,
            shading.Brightness,
            shading.OutlineValue,
            shading.OutlineSaturation,
            CastShadowStrength: D3D12StylizedPolishDefaults.DefaultCastShadowStrength,
            FaceLightStrength: D3D12StylizedPolishDefaults.DefaultFaceLightStrength);
        result.Validate();
        return result;
    }

    public static StyleStylizedShadingParameters ToContractStylizedShading(D3D12StylizedShading shading)
    {
        ArgumentNullException.ThrowIfNull(shading);
        return new StyleStylizedShadingParameters(
            shading.ShadowStrength,
            shading.ShadowValue,
            shading.ShadowHue,
            shading.ShadowTint,
            shading.FaceFlatten,
            shading.WarmColor,
            shading.WarmStrength,
            shading.RimEdge,
            shading.RimStrength,
            shading.Saturation,
            shading.Contrast,
            shading.Brightness,
            shading.OutlineValue,
            shading.OutlineSaturation);
    }

    public static D3D12PmxMaterialTreatment ToD3D12Treatment(StyleMaterialTreatmentParameters treatment)
    {
        ArgumentNullException.ThrowIfNull(treatment);
        var result = new D3D12PmxMaterialTreatment(
            treatment.Saturation,
            treatment.TransitionWidthScale,
            treatment.ValueContrast,
            treatment.ShadeGamma,
            treatment.HairHighlightStrength,
            treatment.HairHighlightWidth);
        result.Validate();
        return result;
    }

    public static StyleMaterialTreatmentParameters ToContractTreatment(D3D12PmxMaterialTreatment treatment)
    {
        ArgumentNullException.ThrowIfNull(treatment);
        return new StyleMaterialTreatmentParameters(
            treatment.Saturation,
            treatment.TransitionWidthScale,
            treatment.ValueContrast,
            treatment.ShadeGamma,
            treatment.HairHighlightStrength,
            treatment.HairHighlightWidth);
    }

    public static D3D12PluginTextureEncoding ToD3D12TextureEncoding(StyleTextureEncoding encoding) => encoding switch
    {
        StyleTextureEncoding.Linear => D3D12PluginTextureEncoding.Linear,
        StyleTextureEncoding.SrgbColor => D3D12PluginTextureEncoding.SrgbColor,
        _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
    };

    public static D3D12PluginTextureMipRule ToD3D12TextureMipRule(StyleTextureMipRule mipRule) => mipRule switch
    {
        StyleTextureMipRule.FromSource => D3D12PluginTextureMipRule.FromSource,
        StyleTextureMipRule.PreserveMips => D3D12PluginTextureMipRule.PreserveMips,
        StyleTextureMipRule.PointNoMip => D3D12PluginTextureMipRule.PointNoMip,
        StyleTextureMipRule.GenerateMips => D3D12PluginTextureMipRule.GenerateMips,
        _ => throw new ArgumentOutOfRangeException(nameof(mipRule)),
    };

    public static D3D12PluginTexturePixelFormat ToD3D12TexturePixelFormat(StyleTexturePixelFormat format) => format switch
    {
        StyleTexturePixelFormat.Rgba8Unorm => D3D12PluginTexturePixelFormat.Rgba8Unorm,
        StyleTexturePixelFormat.Rgba8UnormSrgb => D3D12PluginTexturePixelFormat.Rgba8UnormSrgb,
        StyleTexturePixelFormat.Rgba16Float => D3D12PluginTexturePixelFormat.Rgba16Float,
        StyleTexturePixelFormat.Rgba32Float => D3D12PluginTexturePixelFormat.Rgba32Float,
        StyleTexturePixelFormat.R8Unorm => D3D12PluginTexturePixelFormat.R8Unorm,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static D3D12PluginInlineTexture ToD3D12InlineTexture(StyleInlineTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        var result = new D3D12PluginInlineTexture(
            texture.AssetKey,
            texture.Width,
            texture.Height,
            ToD3D12TexturePixelFormat(texture.Format),
            texture.Pixels.IsEmpty ? ReadOnlyMemory<byte>.Empty : texture.Pixels.ToArray(),
            ToD3D12TextureMipRule(texture.MipRule));
        result.Validate();
        return result;
    }

    public static D3D12PluginTextureBinding ToD3D12TextureBinding(StyleTextureBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var result = new D3D12PluginTextureBinding(
            binding.SlotIndex,
            binding.SceneTextureIndex,
            binding.InlineTexture is { } inline ? ToD3D12InlineTexture(inline) : null,
            ToD3D12TextureEncoding(binding.Encoding),
            ToD3D12TextureMipRule(binding.MipRule));
        result.Validate();
        return result;
    }

    public static D3D12PluginBufferKind ToD3D12BufferKind(StyleBufferKind kind) => kind switch
    {
        StyleBufferKind.MikkTangentCorners => D3D12PluginBufferKind.MikkTangentCorners,
        StyleBufferKind.CustomStructured => D3D12PluginBufferKind.CustomStructured,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static D3D12PluginBufferBinding ToD3D12BufferBinding(StyleBufferBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var result = new D3D12PluginBufferBinding(
            binding.SlotIndex,
            ToD3D12BufferKind(binding.Kind),
            binding.DataKey,
            binding.ElementStrideBytes,
            binding.ElementCount,
            binding.Data.IsEmpty ? ReadOnlyMemory<byte>.Empty : binding.Data.ToArray());
        result.Validate();
        return result;
    }

    public static D3D12PluginDrawData ToD3D12DrawData(StyleDrawData drawData)
    {
        ArgumentNullException.ThrowIfNull(drawData);

        D3D12PluginTextureBinding[]? textures = null;
        if (drawData.TextureBindings is { Count: > 0 } srcTextures)
        {
            textures = new D3D12PluginTextureBinding[srcTextures.Count];
            for (int i = 0; i < srcTextures.Count; i++)
            {
                if (srcTextures[i] is null)
                    throw new ArgumentException("StyleDrawData.TextureBindings cannot contain null.", nameof(drawData));
                textures[i] = ToD3D12TextureBinding(srcTextures[i]);
            }
        }

        D3D12PluginBufferBinding[]? buffers = null;
        if (drawData.BufferBindings is { Count: > 0 } srcBuffers)
        {
            buffers = new D3D12PluginBufferBinding[srcBuffers.Count];
            for (int i = 0; i < srcBuffers.Count; i++)
            {
                if (srcBuffers[i] is null)
                    throw new ArgumentException("StyleDrawData.BufferBindings cannot contain null.", nameof(drawData));
                buffers[i] = ToD3D12BufferBinding(srcBuffers[i]);
            }
        }

        if (drawData.FaceEyeOverride is { } faceEye)
            ValidateFaceEye(faceEye);

        var result = new D3D12PluginDrawData(
            ShaderModuleId: drawData.ShaderModuleId,
            ConstantBufferData: drawData.ConstantBufferData.IsEmpty ? ReadOnlyMemory<byte>.Empty : drawData.ConstantBufferData.ToArray(),
            TextureBindings: textures is null ? null : Array.AsReadOnly(textures),
            BufferBindings: buffers is null ? null : Array.AsReadOnly(buffers),
            StylizedShadingOverride: drawData.StylizedShadingOverride is { } shading ? ToD3D12StylizedShading(shading) : null,
            TreatmentOverride: drawData.TreatmentOverride is { } treatment ? ToD3D12Treatment(treatment) : null,
            HookMaskOverride: drawData.HookMaskOverride is { } hooks ? ToD3D12HookMask(hooks) : null,
            DisableStylizedSphereMaps: drawData.DisableStylizedSphereMaps,
            ExtraSurfaceParams: drawData.ExtraSurfaceParams,
            PreserveDirectLightPalette: drawData.PreserveDirectLightPalette);
        result.Validate();
        return result;
    }

    public static StyleMaterialBindingData DetachBindingData(StyleMaterialBindingData binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        D3D12StylePresetDefinition.ValidateIdentifier(binding.StyleId, nameof(binding));
        D3D12StylePresetDefinition.ValidateIdentifier(binding.ModeId, nameof(binding));

        IReadOnlyDictionary<string, int>? textureSlots = null;
        if (binding.TextureSlots is { Count: > 0 } srcTex)
        {
            var copy = new Dictionary<string, int>(srcTex.Count, StringComparer.Ordinal);
            foreach (var (key, value) in srcTex)
            {
                ValidateKey(key, nameof(binding.TextureSlots));
                if (value < -1)
                    throw new ArgumentOutOfRangeException(nameof(binding), $"Texture slot '{key}' must be >= -1.");
                copy.Add(key, value);
            }
            textureSlots = new ReadOnlyDictionary<string, int>(copy);
        }

        IReadOnlyDictionary<string, double>? numberValues = null;
        if (binding.NumberValues is { Count: > 0 } srcNums)
        {
            var copy = new Dictionary<string, double>(srcNums.Count, StringComparer.Ordinal);
            foreach (var (key, value) in srcNums)
            {
                ValidateKey(key, nameof(binding.NumberValues));
                if (!double.IsFinite(value))
                    throw new ArgumentOutOfRangeException(nameof(binding), $"Numeric value '{key}' must be finite.");
                copy.Add(key, value);
            }
            numberValues = new ReadOnlyDictionary<string, double>(copy);
        }

        IReadOnlyDictionary<string, bool>? boolValues = null;
        if (binding.BoolValues is { Count: > 0 } srcBools)
        {
            var copy = new Dictionary<string, bool>(srcBools.Count, StringComparer.Ordinal);
            foreach (var (key, value) in srcBools)
            {
                ValidateKey(key, nameof(binding.BoolValues));
                copy.Add(key, value);
            }
            boolValues = new ReadOnlyDictionary<string, bool>(copy);
        }

        IReadOnlyDictionary<string, float[]>? vectorValues = null;
        if (binding.VectorValues is { Count: > 0 } srcVecs)
        {
            var copy = new Dictionary<string, float[]>(srcVecs.Count, StringComparer.Ordinal);
            foreach (var (key, value) in srcVecs)
            {
                ValidateKey(key, nameof(binding.VectorValues));
                if (value is null)
                    throw new ArgumentException($"Vector value '{key}' cannot be null.", nameof(binding));
                if (value.Length is < 1 or > 16)
                    throw new ArgumentException($"Vector value '{key}' must contain 1..16 elements.", nameof(binding));
                var cloned = new float[value.Length];
                for (int i = 0; i < value.Length; i++)
                {
                    if (!float.IsFinite(value[i]))
                        throw new ArgumentOutOfRangeException(nameof(binding), $"Vector value '{key}' contains non-finite element.");
                    cloned[i] = value[i];
                }
                copy.Add(key, cloned);
            }
            vectorValues = new ReadOnlyDictionary<string, float[]>(copy);
        }

        IReadOnlyDictionary<string, string>? stringValues = null;
        if (binding.StringValues is { Count: > 0 } srcStrings)
        {
            var copy = new Dictionary<string, string>(srcStrings.Count, StringComparer.Ordinal);
            foreach (var (key, value) in srcStrings)
            {
                ValidateKey(key, nameof(binding.StringValues));
                if (value is null)
                    throw new ArgumentException($"String value '{key}' cannot be null.", nameof(binding));
                copy.Add(key, value);
            }
            stringValues = new ReadOnlyDictionary<string, string>(copy);
        }

        StyleBoneAxisBinding? boneBinding = null;
        if (binding.BoneBinding is { } srcBone)
        {
            if (!Enum.IsDefined(srcBone.Mode))
                throw new ArgumentOutOfRangeException(nameof(binding), "Unknown StyleBoneTrackingMode.");
            ValidateOptionalVector(srcBone.LocalForward, nameof(srcBone.LocalForward));
            ValidateOptionalVector(srcBone.LocalUp, nameof(srcBone.LocalUp));
            ValidateOptionalVector(srcBone.FixedWorldForward, nameof(srcBone.FixedWorldForward));
            ValidateOptionalVector(srcBone.FixedWorldRight, nameof(srcBone.FixedWorldRight));
            boneBinding = srcBone with { };
        }

        return new StyleMaterialBindingData(
            binding.StyleId,
            binding.ModeId,
            textureSlots,
            numberValues,
            boolValues,
            vectorValues,
            stringValues,
            boneBinding);
    }

    public static bool BindingDataEquivalent(StyleMaterialBindingData? a, StyleMaterialBindingData? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (!string.Equals(a.StyleId, b.StyleId, StringComparison.Ordinal)
            || !string.Equals(a.ModeId, b.ModeId, StringComparison.Ordinal)
            || a.BoneBinding != b.BoneBinding)
            return false;

        return DictionaryEqual(a.TextureSlots, b.TextureSlots)
            && DictionaryEqual(a.NumberValues, b.NumberValues)
            && DictionaryEqual(a.BoolValues, b.BoolValues)
            && VectorDictionaryEqual(a.VectorValues, b.VectorValues)
            && DictionaryEqual(a.StringValues, b.StringValues);
    }

    internal static void ValidateFaceEye(StyleFaceEyeDefaults faceEye)
    {
        if (!float.IsFinite(faceEye.FaceNormalShadeStrength) || faceEye.FaceNormalShadeStrength is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(faceEye), "Face normal shade strength must be finite and in [0,1].");
        if (!float.IsFinite(faceEye.FaceGeometricShadowStrength) || faceEye.FaceGeometricShadowStrength is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(faceEye), "Face geometric shadow strength must be finite and in [0,1].");
        if (!float.IsFinite(faceEye.EyeGeometricShadowStrength) || faceEye.EyeGeometricShadowStrength is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(faceEye), "Eye geometric shadow strength must be finite and in [0,1].");
    }

    internal static bool IsIdentityTreatment(D3D12PmxMaterialTreatment? treatment)
        => treatment is null || (treatment.Saturation == 1f && treatment.TransitionWidthScale == 1f
            && treatment.ValueContrast == 0f && treatment.ShadeGamma == 1f && treatment.HairHighlightStrength == 0f);

    internal static bool IsIdentityTreatment(StyleMaterialTreatmentParameters? treatment)
        => treatment is null || (treatment.Saturation == 1f && treatment.TransitionWidthScale == 1f
            && treatment.ValueContrast == 0f && treatment.ShadeGamma == 1f && treatment.HairHighlightStrength == 0f);

    private static void ValidateKey(string key, string group)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
            throw new ArgumentException($"Parameter key in '{group}' must be 1..128 non-whitespace characters.");
    }

    private static void ValidateOptionalVector(Vector3? value, string name)
    {
        if (value is { } v && (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)))
            throw new ArgumentOutOfRangeException(name, "Bone basis vectors must be finite.");
    }

    private static bool DictionaryEqual<TValue>(
        IReadOnlyDictionary<string, TValue>? a,
        IReadOnlyDictionary<string, TValue>? b)
    {
        int countA = a?.Count ?? 0;
        int countB = b?.Count ?? 0;
        if (countA != countB) return false;
        if (countA == 0) return true;
        var comparer = EqualityComparer<TValue>.Default;
        foreach (var (key, valA) in a!)
        {
            if (!b!.TryGetValue(key, out var valB) || !comparer.Equals(valA, valB))
                return false;
        }
        return true;
    }

    private static bool VectorDictionaryEqual(
        IReadOnlyDictionary<string, float[]>? a,
        IReadOnlyDictionary<string, float[]>? b)
    {
        int countA = a?.Count ?? 0;
        int countB = b?.Count ?? 0;
        if (countA != countB) return false;
        if (countA == 0) return true;
        foreach (var (key, valA) in a!)
        {
            if (!b!.TryGetValue(key, out var valB) || !valA.AsSpan().SequenceEqual(valB))
                return false;
        }
        return true;
    }
}
