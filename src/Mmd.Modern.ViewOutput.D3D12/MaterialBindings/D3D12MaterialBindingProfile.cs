using System.Collections.ObjectModel;
using System.Numerics;
using System.Text;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>Explicit source-scene texture ordinal and immutable content guard. It is neither a PMX
/// texture ordinal nor a composed-frame slot; digest equality never selects a different slot.</summary>
public sealed record D3D12MaterialBindingProfileTexture(int SourceSceneTextureOrdinal, int Width, int Height,
    SceneTextureKind Kind, string RgbaSha256)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(SourceSceneTextureOrdinal);
        if (Width is < 1 or > 4096 || Height is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(Width), "Texture dimensions must be in [1,4096].");
        if (!Enum.IsDefined(Kind)) throw new ArgumentOutOfRangeException(nameof(Kind));
        D3D12MaterialBindingProfile.ValidateDigest(RgbaSha256, nameof(RgbaSha256));
    }
}

/// <summary>Persistent numerical FaceSDF selection. Version 1 accepts explicit fixed world axes only;
/// selected-bone provenance requires a separate guarded skeleton contract.</summary>
public sealed record D3D12MaterialBindingProfileFace(string PipelineId,
    D3D12MaterialBindingProfileTexture Primary, D3D12MaterialBindingProfileTexture? Secondary,
    Vector3 HeadWorldForward, Vector3 HeadWorldRight, float Transition = 1f, float Offset = 1f,
    D3D12FaceSdfEncoding Encoding = D3D12FaceSdfEncoding.GrayRed)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Primary);
        Primary.Validate(); Secondary?.Validate();
        bool compatible = Encoding switch
        {
            D3D12FaceSdfEncoding.GrayRed => PipelineId is "star-rail" or "wuthering-waves",
            D3D12FaceSdfEncoding.GenshinSoftFace => PipelineId == "genshin",
            D3D12FaceSdfEncoding.EndfieldRgbCmM => PipelineId == "endfield",
            _ => false,
        };
        if (!compatible) throw new ArgumentException("Face encoding must match its explicitly selected pipeline.", nameof(PipelineId));
        // Temporary zero slots validate existing numerical rules without persisting composed slots.
        new D3D12FaceSdfBinding(0, HeadWorldForward, HeadWorldRight, Transition, Offset,
            Encoding: Encoding, SecondaryCombinedTextureIndex: Secondary is null ? -1 : 0).Validate();
    }
}

public sealed record D3D12MaterialBindingProfileGenshinHair(D3D12MaterialBindingProfileTexture Texture,
    float GlobalSpecularScale = 1f, float Shininess = 10f, float Multiplier = .1f, float Intensity = 1f)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Texture); Texture.Validate();
        new D3D12GenshinNonMetallicSpecularBinding(0, GlobalSpecularScale, Shininess, Multiplier, Intensity).Validate();
    }
}

/// <summary>Two separately guarded numerical/color source images and explicit source scalars.</summary>
public sealed record D3D12MaterialBindingProfileGenshinHairAtlas(D3D12MaterialBindingProfileTexture Lightmap,
    D3D12MaterialBindingProfileTexture Atlas, D3D12GenshinAtlasColorEncoding AtlasEncoding,
    float ExplicitRowId, float DayNight, float ShadowPosition, float ShadowRampWidth,
    float VertexWidth, float VertexAo, float UseLightmapAo, float UseVertexColorAo)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Lightmap); ArgumentNullException.ThrowIfNull(Atlas);
        Lightmap.Validate(); Atlas.Validate();
        new D3D12GenshinHairShadowAtlasBinding(0, 0, AtlasEncoding, ExplicitRowId, DayNight,
            ShadowPosition, ShadowRampWidth, VertexWidth, VertexAo, UseLightmapAo, UseVertexColorAo).Validate();
    }
}

public sealed record D3D12MaterialBindingProfileStarMcHair(string PipelineId,
    D3D12MaterialBindingProfileTexture Highlight, D3D12MaterialBindingProfileTexture Shadow,
    D3D12HairHighlightChannel HighlightChannel = D3D12HairHighlightChannel.Blue,
    float HighlightCoefficient = 0f, float HighlightBrightness = 2f, float ShadowCoefficient = .5f,
    float ShadowSoftness = .5f, float Intensity = .1f)
{
    public void Validate()
    {
        if (PipelineId is not ("star-rail" or "wuthering-waves"))
            throw new ArgumentException("Star/MC hair requires an explicit supported pipeline.", nameof(PipelineId));
        if (PipelineId == "star-rail" && HighlightChannel != D3D12HairHighlightChannel.Blue)
            throw new ArgumentException("Star Rail hair consumes Blue; Alpha is an explicit MC-only selection.", nameof(HighlightChannel));
        ArgumentNullException.ThrowIfNull(Highlight); ArgumentNullException.ThrowIfNull(Shadow);
        Highlight.Validate(); Shadow.Validate();
        new D3D12StarMcHairBinding(0, 0, HighlightChannel, HighlightCoefficient, HighlightBrightness,
            ShadowCoefficient, ShadowSoftness, Intensity).Validate();
    }
}

/// <summary>Explicit Green numerical source image and linear radial-hair palette. Authored rest
/// seeds are a separately bounded PMA resource in source vertex order.</summary>
public sealed record D3D12MaterialBindingProfileEndfieldHair(D3D12MaterialBindingProfileTexture Texture,
    Vector3 PaletteColor, float Brightness = 0f)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Texture); Texture.Validate();
        new D3D12EndfieldHairBinding(0, PaletteColor, Brightness).Validate();
    }
}

/// <summary>Portable explicit Cpre provider. All selected source images remain guarded even when
/// shadow/detail factors consume no pixels; effective UV and geometry are inherited from MatCap.</summary>
public sealed record D3D12MaterialBindingProfileEndfieldBaseImage(Vector3 BaseImageLinearRgb, Vector3 ModelColor,
    Vector3 ShadowColor, Vector3 ColorMultiplier, float ShadowFactor = 1f, float WarmTint = 0f,
    float Contrast = 0f, float Saturation = 1f, float DetailBlue = 1f, float ShadowStrength = 1f,
    D3D12EndfieldPaletteInputMode BaseInputMode = D3D12EndfieldPaletteInputMode.Uniform,
    D3D12MaterialBindingProfileTexture? BaseTexture = null,
    D3D12MatcapColorEncoding BaseEncoding = D3D12MatcapColorEncoding.LinearRgb,
    D3D12EndfieldPaletteInputMode DetailInputMode = D3D12EndfieldPaletteInputMode.Uniform,
    D3D12MaterialBindingProfileTexture? DetailTexture = null,
    D3D12EndfieldPaletteInputMode ShadowInputMode = D3D12EndfieldPaletteInputMode.Uniform,
    D3D12MaterialBindingProfileTexture? ShadowTexture = null,
    D3D12EndfieldFacingBaseBinding? FacingBase = null,
    D3D12EndfieldDirectionalShadowBinding? DirectionalShadow = null)
{
    public void Validate()
    {
        BaseTexture?.Validate(); DetailTexture?.Validate(); ShadowTexture?.Validate();
        if (BaseTexture is { Kind: not SceneTextureKind.Image } || DetailTexture is { Kind: not SceneTextureKind.Image }
            || ShadowTexture is { Kind: not SceneTextureKind.Image })
            throw new ArgumentException("Endfield Cpre inputs require explicit original Image resources.");
        ToBinding().Validate();
    }

    internal D3D12EndfieldBaseImageBinding ToBinding() => new(BaseImageLinearRgb, ModelColor, ShadowColor,
        ColorMultiplier, ShadowFactor, WarmTint, Contrast, Saturation, DetailBlue, ShadowStrength,
        BaseInputMode, BaseTexture?.SourceSceneTextureOrdinal, BaseEncoding,
        DetailInputMode, DetailTexture?.SourceSceneTextureOrdinal, ShadowInputMode, ShadowTexture?.SourceSceneTextureOrdinal,
        FacingBase, DirectionalShadow);
}

/// <summary>Guarded numerical emission Blue and LinearRGB received-light adapter. Selected images
/// remain required even at zero consumption. UV and current geometry are inherited from MatCap.</summary>
public sealed record D3D12MaterialBindingProfileEndfieldPalette(Vector3 ReceivedLightLinearRgb,
    float EmissionStrength = 0f, float UniformEmissionBlue = 0f, float LightInfluence = 0f, float Density = 0f,
    D3D12EndfieldPaletteInputMode EmissionInputMode = D3D12EndfieldPaletteInputMode.Uniform,
    D3D12MaterialBindingProfileTexture? EmissionTexture = null,
    D3D12EndfieldPaletteInputMode ReceivedLightInputMode = D3D12EndfieldPaletteInputMode.Uniform,
    D3D12MaterialBindingProfileTexture? ReceivedLightTexture = null,
    D3D12MaterialBindingProfileEndfieldBaseImage? BaseImage = null)
{
    public void Validate()
    {
        EmissionTexture?.Validate(); ReceivedLightTexture?.Validate(); BaseImage?.Validate();
        if (EmissionTexture is { Kind: not SceneTextureKind.Image } || ReceivedLightTexture is { Kind: not SceneTextureKind.Image })
            throw new ArgumentException("Endfield palette inputs require original numerical Image resources.");
        ToBinding().Validate();
    }

    // View retains both source-local ordinals until the exact object's current capture is available.
    internal D3D12EndfieldPaletteBinding ToBinding() => new(ReceivedLightLinearRgb, EmissionStrength,
        UniformEmissionBlue, LightInfluence, Density, EmissionInputMode, EmissionTexture?.SourceSceneTextureOrdinal,
        ReceivedLightInputMode, ReceivedLightTexture?.SourceSceneTextureOrdinal, BaseImage?.ToBinding());
}

/// <summary>Three original Image guards and explicit authored MatCap parameters. Tangents are
/// deterministically rebuilt at the selected UV from the current pose with the pinned generator.</summary>
public sealed record D3D12MaterialBindingProfileEndfieldMatcap(D3D12MaterialBindingProfileTexture Normal,
    D3D12MaterialBindingProfileTexture P, D3D12MaterialBindingProfileTexture Matcap,
    D3D12MatcapColorEncoding MatcapEncoding, int UvChannel, Vector3 BaseColor, Vector3 HighlightColor,
    float NormalStrength = 2f, float RotationRadians = 0f, float HighlightCoefficient = 0f,
    float ShadowHardness = .5f, Vector3? LightControlDirection = null, float BaselineStrength = 1f,
    bool CoupleEndfieldPalette = false, string GeneratorRevision = "mikk-v1",
    D3D12MaterialBindingProfileEndfieldPalette? Palette = null,
    D3D12EndfieldHighlightRangeBinding? HighlightRange = null)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Normal); ArgumentNullException.ThrowIfNull(P); ArgumentNullException.ThrowIfNull(Matcap);
        Normal.Validate(); P.Validate(); Matcap.Validate(); Palette?.Validate();
        if (Normal.Kind != SceneTextureKind.Image || P.Kind != SceneTextureKind.Image || Matcap.Kind != SceneTextureKind.Image)
            throw new ArgumentException("MatCap normal/P/color require original Image resources; procedural toon is not an authored image.");
        if (GeneratorRevision != "mikk-v1") throw new ArgumentException("MatCap requires the supported mikk-v1 tangent generator revision.", nameof(GeneratorRevision));
        new D3D12EndfieldMatcapBinding(0, 0, 0, MatcapEncoding, UvChannel, BaseColor, HighlightColor,
            NormalStrength, RotationRadians, HighlightCoefficient, ShadowHardness, LightControlDirection,
            BaselineStrength, CoupleEndfieldPalette, Palette: Palette?.ToBinding(), HighlightRange: HighlightRange).Validate();
    }
}

/// <summary>Source numeric metallic lobe parameters and optional independently guarded Non-Color
/// RGB ramp. An explicitly selected ramp is guarded even when RampMix consumes no pixels.</summary>
public sealed record D3D12MaterialBindingProfileGenshinMetallicSpecular(Vector3 Color, Vector3 SharpColor,
    float Shininess = 16f, float Scale = 1f, float Attenuation = .5f, float SharpOffset = 1f,
    D3D12MaterialBindingProfileTexture? Ramp = null, float RampMix = 0f)
{
    public void Validate()
    {
        Ramp?.Validate();
        if (Ramp is { Kind: not SceneTextureKind.Image })
            throw new ArgumentException("Metallic specular ramp requires an original Non-Color Image resource.");
        ToBinding().Validate();
    }

    // The restored View binding intentionally carries this source-local ordinal until capture.
    internal D3D12GenshinMetallicSpecularBinding ToBinding()
        => new(Color, SharpColor, Shininess, Scale, Attenuation, SharpOffset, Ramp?.SourceSceneTextureOrdinal, RampMix);
}

/// <summary>UV0 numerical lightmap and ChannelPacked color MatCap with source numeric palette inputs.</summary>
public sealed record D3D12MaterialBindingProfileGenshinMetallic(D3D12MaterialBindingProfileTexture Lightmap,
    D3D12MaterialBindingProfileTexture Matcap, D3D12MatcapColorEncoding MatcapEncoding,
    Vector3 DarkColor, Vector3 LightColor, Vector3 ShadowMultiplyColor, float EnableMetallics = 1f,
    float TileScale = 1f, float Brightness = 1f,
    D3D12GenshinMetallicLitMode LitMode = D3D12GenshinMetallicLitMode.PrimaryResponse, float UniformLitFactor = 1f,
    D3D12MaterialBindingProfileGenshinMetallicSpecular? Specular = null)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Lightmap); ArgumentNullException.ThrowIfNull(Matcap);
        Lightmap.Validate(); Matcap.Validate();
        if (Lightmap.Kind != SceneTextureKind.Image || Matcap.Kind != SceneTextureKind.Image)
            throw new ArgumentException("Metallic lightmap/color require original Image resources.");
        Specular?.Validate();
        new D3D12GenshinMetallicBinding(0, 0, MatcapEncoding, DarkColor, LightColor, ShadowMultiplyColor,
            EnableMetallics, TileScale, Brightness, LitMode, UniformLitFactor, Specular?.ToBinding()).Validate();
    }
}

/// <summary>One explicit source-local material choice. Dedicated payloads stay dormant outside their
/// selected pipeline; ordinary treatment and dedicated material data are mutually exclusive.</summary>
public sealed record D3D12MaterialBindingProfileRow(int LocalMaterialIndex, D3D12CharacterMaterialRole Role,
    float FaceNormalShadeStrength = .35f, float FaceGeometricShadowStrength = 1f,
    float EyeGeometricShadowStrength = .2f, D3D12PmxMaterialTreatment? PmxMaterialTreatment = null,
    D3D12MaterialBindingProfileFace? Face = null, D3D12MaterialBindingProfileGenshinHair? GenshinHair = null,
    D3D12MaterialBindingProfileStarMcHair? StarMcHair = null,
    D3D12EndfieldRimBinding? EndfieldRim = null,
    D3D12MaterialBindingProfileGenshinHairAtlas? GenshinHairAtlas = null,
    D3D12MaterialBindingProfileEndfieldHair? EndfieldHair = null,
    D3D12MaterialBindingProfileEndfieldMatcap? EndfieldMatcap = null,
    D3D12MaterialBindingProfileGenshinMetallic? GenshinMetallic = null,
    D3D12MaterialBindingProfileStarMcBody? StarMcBody = null)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(LocalMaterialIndex);
        new D3D12CharacterMaterialStyle(Role, FaceNormalShadeStrength, FaceGeometricShadowStrength,
            EyeGeometricShadowStrength: EyeGeometricShadowStrength, PmxMaterialTreatment: PmxMaterialTreatment,
            EndfieldRim: EndfieldRim).Validate();
        Face?.Validate(); GenshinHair?.Validate(); StarMcHair?.Validate(); GenshinHairAtlas?.Validate(); EndfieldHair?.Validate(); EndfieldMatcap?.Validate(); GenshinMetallic?.Validate(); StarMcBody?.Validate();
        int dedicated = (Face is null ? 0 : 1) + (GenshinHair is null && GenshinHairAtlas is null && GenshinMetallic is null ? 0 : 1) + (StarMcHair is null ? 0 : 1)
            + (EndfieldRim is null && EndfieldHair is null && EndfieldMatcap is null ? 0 : 1) + (StarMcBody is null ? 0 : 1);
        if (dedicated > 1 || (dedicated != 0 && PmxMaterialTreatment is not null))
            throw new ArgumentException("A material must not combine dedicated inputs or ordinary treatment with dedicated inputs.");
        if (Face is not null && Role != D3D12CharacterMaterialRole.Face)
            throw new ArgumentException("FaceSDF requires an explicit Face role.", nameof(Role));
        if ((GenshinHair is not null || StarMcHair is not null || EndfieldHair is not null) && Role != D3D12CharacterMaterialRole.Hair)
            throw new ArgumentException("Dedicated hair requires an explicit Hair role.", nameof(Role));
        if (GenshinHairAtlas is not null && Role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair))
            throw new ArgumentException("Generated atlas adaptation requires explicit Body/Hair; Face and Eye are protected.", nameof(Role));
        if (StarMcBody is not null && Role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair))
            throw new ArgumentException("Star/MC Body adaptation requires an explicit Body/Hair role.", nameof(Role));
        if (GenshinMetallic is { } metallic)
        {
            if (Role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair))
                throw new ArgumentException("Genshin metallic requires an explicit Body/Hair role.", nameof(Role));
            if ((GenshinHair is { } hair && hair.Texture != metallic.Lightmap)
                || (GenshinHairAtlas is { } atlas && atlas.Lightmap != metallic.Lightmap))
                throw new ArgumentException("Co-bound metallic, R/B and atlas require the identical guarded UV0 lightmap.");
        }
        if (EndfieldMatcap is { } matcap)
        {
            if (Role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair))
                throw new ArgumentException("Endfield MatCap requires an explicit Body/Hair role.", nameof(Role));
            D3D12ViewEndfieldMatcapBindings.ValidatePalette(matcap.BaseColor, matcap.CoupleEndfieldPalette,
                matcap.BaselineStrength, EndfieldRim, EndfieldHair?.PaletteColor);
        }
    }
}

/// <summary>Independent source-local outline multiplier. It does not classify the material or
/// replace its current rendering style's effective outline width.</summary>
public sealed record D3D12MaterialBindingProfileOutline(int LocalMaterialIndex, float Scale = 1f)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(LocalMaterialIndex);
        if (!float.IsFinite(Scale) || Scale is < 0f or > 4f)
            throw new ArgumentOutOfRangeException(nameof(Scale), "Outline scale must be finite and in [0,4].");
    }
}

/// <summary>Detached renderer-owned model mapping, independent of GPU objects and evaluated animation.
/// The codec can be used by a portable project or a local library. It contains no paths, image bytes,
/// object identity or frame-global slots. Rest-seed payloads use a separate bounded PMA resource.</summary>
public sealed class D3D12MaterialBindingProfile
{
    public const int CurrentSchemaVersion = 13;
    public const int MaxMaterials = 4096;
    public string StableId { get; }
    public string Name { get; }
    public int SchemaVersion => OutlineWidths.Count != 0 ? 13
        : Materials.Any(row => row.StarMcBody is not null || row.EndfieldMatcap?.HighlightRange is not null
        || row.EndfieldMatcap?.Palette?.ReceivedLightInputMode == D3D12EndfieldPaletteInputMode.RendererDiffuse) ? 12
        : Materials.Any(row => row.PmxMaterialTreatment is { } treatment
        && (treatment.ValueContrast != 0f || treatment.ShadeGamma != 1f
            || treatment.HairHighlightStrength != 0f || treatment.HairHighlightWidth != .12f)) ? 11
        : Materials.Any(row => row.EndfieldMatcap?.Palette?.BaseImage is { } basis
        && (basis.FacingBase is not null || basis.DirectionalShadow is not null)) ? 10
        : Materials.Any(row => row.EndfieldMatcap?.Palette?.BaseImage is not null) ? 9
        : Materials.Any(row => row.EndfieldMatcap?.Palette is not null) ? 8
        : Materials.Any(row => row.GenshinMetallic?.Specular is not null) ? 7
        : Materials.Any(row => row.GenshinMetallic is not null) ? 6
        : Materials.Any(row => row.EndfieldMatcap is not null) ? 5
        : Materials.Any(row => row.EndfieldHair is not null) ? 4
        : Materials.Any(row => row.GenshinHairAtlas is not null) ? 3
        : Materials.Any(row => row.EndfieldRim is not null) ? 2 : 1;
    public int LayoutVersion { get; }
    public string LayoutFingerprint { get; }
    public IReadOnlyList<D3D12MaterialBindingProfileRow> Materials { get; }
    public IReadOnlyList<D3D12MaterialBindingProfileOutline> OutlineWidths { get; }

    public D3D12MaterialBindingProfile(string stableId, string name, string layoutFingerprint,
        IEnumerable<D3D12MaterialBindingProfileRow> materials, int layoutVersion = 1,
        IEnumerable<D3D12MaterialBindingProfileOutline>? outlineWidths = null)
    {
        ValidateIdentifier(stableId);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl))
            throw new ArgumentException("A profile name must contain 1 to 128 characters without control characters.", nameof(name));
        try { _ = new UTF8Encoding(false, true).GetByteCount(name); }
        catch (EncoderFallbackException error) { throw new ArgumentException("A profile name must be valid Unicode.", nameof(name), error); }
        ValidateDigest(layoutFingerprint, nameof(layoutFingerprint));
        if (layoutVersion != 1) throw new NotSupportedException("Only material layout version 1 is supported.");
        ArgumentNullException.ThrowIfNull(materials);
        var rows = materials.Take(MaxMaterials + 1).ToArray();
        if (rows.Length > MaxMaterials) throw new ArgumentException("A profile exceeds the 4096-material budget.", nameof(materials));
        var selected = new HashSet<int>();
        foreach (var row in rows)
        {
            ArgumentNullException.ThrowIfNull(row); row.Validate();
            if (!selected.Add(row.LocalMaterialIndex))
                throw new ArgumentException("A source-local material may be mapped only once in one profile.", nameof(materials));
        }
        var outlines = outlineWidths?.Take(MaxMaterials + 1).ToArray() ?? [];
        if (outlines.Length > MaxMaterials)
            throw new ArgumentException("A profile exceeds the 4096-outline budget.", nameof(outlineWidths));
        selected.Clear();
        foreach (var outline in outlines)
        {
            ArgumentNullException.ThrowIfNull(outline); outline.Validate();
            if (!selected.Add(outline.LocalMaterialIndex))
                throw new ArgumentException("A source-local material may have only one outline multiplier in one profile.", nameof(outlineWidths));
        }
        StableId = stableId; Name = name; LayoutFingerprint = layoutFingerprint; LayoutVersion = layoutVersion;
        Materials = new ReadOnlyCollection<D3D12MaterialBindingProfileRow>(rows);
        OutlineWidths = new ReadOnlyCollection<D3D12MaterialBindingProfileOutline>(outlines);
    }

    internal static void ValidateIdentifier(string id)
    {
        if (id is null || id.Length is < 1 or > 128 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw new ArgumentException("A profile ID must be 1 to 128 ASCII letters, digits, dots, underscores or hyphens.", nameof(id));
    }

    internal static void ValidateDigest(string value, string parameter)
    {
        if (value is null || value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("A fingerprint must be a canonical lowercase SHA256 hex digest.", parameter);
    }
}
