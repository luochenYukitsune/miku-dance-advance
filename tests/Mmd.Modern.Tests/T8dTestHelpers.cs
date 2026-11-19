using System.Collections.ObjectModel;
using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;

namespace Mmd.Modern.Tests;

internal sealed record EndfieldHairSpec(
    int TextureSlot = 0,
    Vector3? PaletteColor = null,
    float Brightness = 0f)
{
    public Vector3 EffectivePaletteColor => PaletteColor ?? new Vector3(0.2f);
    public StyleMaterialBindingData ToBinding()
        => T8dTestHelpers.EndfieldHair(TextureSlot, PaletteColor, Brightness);
}

public sealed record EndfieldRimSpec(
    Vector3 BaseColor,
    Vector3 RimColor,
    float EdgeSize = .2f,
    float EdgeStrength = 0f,
    float DirectionRadians = 5.5f,
    float RangeStrength = 1f,
    float RadialStrength = 0f)
{
    public StyleMaterialBindingData ToBinding()
        => T8dTestHelpers.EndfieldRim(BaseColor, RimColor, EdgeSize, EdgeStrength, DirectionRadians, RangeStrength, RadialStrength);
}

internal sealed record EndfieldMatcapSpec(
    int NormalSlot = 0,
    int PSlot = 1,
    int MatcapSlot = 2,
    string MatcapEncoding = T8dTestHelpers.EncodingLinearRgb,
    int UvChannel = 0,
    Vector3? BaseColor = null,
    Vector3? HighlightColor = null,
    float NormalStrength = 2f,
    float RotationRadians = 0f,
    float HighlightCoefficient = 0f,
    float ShadowHardness = .5f,
    Vector3? LightControlDirection = null,
    float BaselineStrength = 1f,
    bool CoupleEndfieldPalette = false,
    string? GeneratorRevision = null,
    EndfieldHighlightRangeSpec? HighlightRange = null,
    EndfieldPaletteSpec? Palette = null,
    EndfieldBaseImageSpec? BaseImage = null)
{
    public Vector3 EffectiveBaseColor => BaseColor ?? new Vector3(.5f);
    public Vector3 EffectiveHighlightColor => HighlightColor ?? Vector3.Zero;
    public StyleMaterialBindingData ToBinding()
        => T8dTestHelpers.EndfieldMatcap(
            NormalSlot, PSlot, MatcapSlot, MatcapEncoding, UvChannel, BaseColor, HighlightColor,
            NormalStrength, RotationRadians, HighlightCoefficient, ShadowHardness, LightControlDirection,
            BaselineStrength, CoupleEndfieldPalette, GeneratorRevision, HighlightRange, Palette, BaseImage);
}

internal sealed record EndfieldDirectionalShadowSpec(
    Vector3 WorldDirection,
    float Width = 0.3f,
    float NormalStrength = 2f);

internal sealed record EndfieldHighlightRangeSpec(
    float OverallBrightness,
    float HighlightBrightness,
    EndfieldDirectionalShadowSpec? DirectionalShadow,
    bool HasDirectionalShadow = true)
{
    public StyleMaterialBindingData ToBinding()
    {
        var numbers = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["highlightRangeOverallBrightness"] = OverallBrightness,
            ["highlightRangeHighlightBrightness"] = HighlightBrightness,
        };
        var bools = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["hasHighlightRangeDirectionalShadow"] = HasDirectionalShadow && DirectionalShadow is not null,
        };
        Dictionary<string, float[]>? vectors = null;
        if (DirectionalShadow is not null)
        {
            vectors = new Dictionary<string, float[]>(StringComparer.Ordinal)
            {
                ["highlightRangeDirectionalShadowWorldDirection"] = [DirectionalShadow.WorldDirection.X, DirectionalShadow.WorldDirection.Y, DirectionalShadow.WorldDirection.Z],
            };
            numbers["highlightRangeDirectionalShadowWidth"] = DirectionalShadow.Width;
            numbers["highlightRangeDirectionalShadowNormalStrength"] = DirectionalShadow.NormalStrength;
        }
        return new StyleMaterialBindingData(
            "endfield",
            "highlight-range",
            NumberValues: new ReadOnlyDictionary<string, double>(numbers),
            BoolValues: new ReadOnlyDictionary<string, bool>(bools),
            VectorValues: vectors is null ? null : new ReadOnlyDictionary<string, float[]>(vectors));
    }
}

internal sealed record EndfieldPaletteSpec(
    Vector3 ReceivedLightLinearRgb,
    float EmissionStrength = 0f,
    float UniformEmissionBlue = 0f,
    float LightInfluence = 0f,
    float Density = 0f,
    string EmissionInputMode = T8dTestHelpers.ModeUniform,
    int? EmissionSlot = null,
    string ReceivedLightInputMode = T8dTestHelpers.ModeUniform,
    int? ReceivedLightSlot = null,
    EndfieldBaseImageSpec? BaseImage = null)
{
    public StyleMaterialBindingData ToBinding()
    {
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);
        if (EmissionSlot is { } eSlot)
            slots["paletteEmission"] = eSlot;
        if (ReceivedLightSlot is { } rSlot)
            slots["paletteReceivedLight"] = rSlot;

        var numbers = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["paletteEmissionStrength"] = EmissionStrength,
            ["paletteUniformEmissionBlue"] = UniformEmissionBlue,
            ["paletteLightInfluence"] = LightInfluence,
            ["paletteDensity"] = Density,
        };
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal)
        {
            ["paletteReceivedLightLinearRgb"] = [ReceivedLightLinearRgb.X, ReceivedLightLinearRgb.Y, ReceivedLightLinearRgb.Z],
        };
        var strings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["paletteEmissionInputMode"] = EmissionInputMode,
            ["paletteReceivedLightInputMode"] = ReceivedLightInputMode,
        };
        return new StyleMaterialBindingData(
            "endfield",
            "palette",
            TextureSlots: slots.Count == 0 ? null : new ReadOnlyDictionary<string, int>(slots),
            NumberValues: new ReadOnlyDictionary<string, double>(numbers),
            VectorValues: new ReadOnlyDictionary<string, float[]>(vectors),
            StringValues: new ReadOnlyDictionary<string, string>(strings));
    }
}

internal sealed record EndfieldFacingBaseSpec(float Blend);

internal sealed record EndfieldBaseImageSpec(
    Vector3 BaseImageLinearRgb,
    Vector3 ModelColor,
    Vector3 ShadowColor,
    Vector3 ColorMultiplier,
    float ShadowFactor = 1f,
    float WarmTint = 0f,
    float Contrast = 0f,
    float Saturation = 1f,
    float DetailBlue = 1f,
    float ShadowStrength = 1f,
    string BaseInputMode = T8dTestHelpers.ModeUniform,
    int? BaseSlot = null,
    string BaseEncoding = T8dTestHelpers.EncodingLinearRgb,
    string DetailInputMode = T8dTestHelpers.ModeUniform,
    int? DetailSlot = null,
    string ShadowInputMode = T8dTestHelpers.ModeUniform,
    int? ShadowSlot = null,
    EndfieldFacingBaseSpec? FacingBase = null,
    EndfieldDirectionalShadowSpec? DirectionalShadow = null)
{
    public StyleMaterialBindingData ToBinding()
    {
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);
        if (BaseSlot is { } bSlot) slots["baseImage"] = bSlot;
        if (DetailSlot is { } dSlot) slots["baseImageDetail"] = dSlot;
        if (ShadowSlot is { } sSlot) slots["baseImageShadow"] = sSlot;

        var numbers = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["baseShadowFactor"] = ShadowFactor,
            ["baseWarmTint"] = WarmTint,
            ["baseContrast"] = Contrast,
            ["baseSaturation"] = Saturation,
            ["baseDetailBlue"] = DetailBlue,
            ["baseShadowStrength"] = ShadowStrength,
        };
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal)
        {
            ["baseImageLinearRgb"] = [BaseImageLinearRgb.X, BaseImageLinearRgb.Y, BaseImageLinearRgb.Z],
            ["baseModelColor"] = [ModelColor.X, ModelColor.Y, ModelColor.Z],
            ["baseShadowColor"] = [ShadowColor.X, ShadowColor.Y, ShadowColor.Z],
            ["baseColorMultiplier"] = [ColorMultiplier.X, ColorMultiplier.Y, ColorMultiplier.Z],
        };
        var strings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["baseInputMode"] = BaseInputMode,
            ["baseEncoding"] = BaseEncoding,
            ["detailInputMode"] = DetailInputMode,
            ["shadowInputMode"] = ShadowInputMode,
        };
        var bools = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (FacingBase is not null)
        {
            bools["hasFacingBase"] = true;
            numbers["facingBaseBlend"] = FacingBase.Blend;
        }
        if (DirectionalShadow is not null)
        {
            bools["hasBaseDirectionalShadow"] = true;
            vectors["baseDirectionalShadowWorldDirection"] = [DirectionalShadow.WorldDirection.X, DirectionalShadow.WorldDirection.Y, DirectionalShadow.WorldDirection.Z];
            numbers["baseDirectionalShadowWidth"] = DirectionalShadow.Width;
            numbers["baseDirectionalShadowNormalStrength"] = DirectionalShadow.NormalStrength;
        }
        return new StyleMaterialBindingData(
            "endfield",
            "base-image",
            TextureSlots: slots.Count == 0 ? null : new ReadOnlyDictionary<string, int>(slots),
            NumberValues: new ReadOnlyDictionary<string, double>(numbers),
            BoolValues: bools.Count == 0 ? null : new ReadOnlyDictionary<string, bool>(bools),
            VectorValues: new ReadOnlyDictionary<string, float[]>(vectors),
            StringValues: new ReadOnlyDictionary<string, string>(strings));
    }
}

internal static class T8dTestHelpers
{
    internal const string EncodingLinearRgb = "LinearRgb";
    internal const string EncodingSrgbRgb = "SrgbRgb";
    internal const string EncodingSrgb = "Srgb";
    internal const string EncodingLinearBgr = "LinearBgr";
    internal const string EncodingSrgbBgr = "SrgbBgr";

    internal const string ModeUniform = "Uniform";
    internal const string ModeTexture = "Texture";
    internal const string ModeRendererDiffuse = "RendererDiffuse";

    internal static float[] Vec(Vector3 v) => [v.X, v.Y, v.Z];

    internal static StyleMaterialBindingData EndfieldFaceSdf(
        int primaryTextureSlot = 0,
        int secondaryTextureSlot = 1,
        float transition = 1f,
        float offset = 1f,
        string encoding = "EndfieldRgbCmM",
        StyleBoneTrackingMode trackingMode = StyleBoneTrackingMode.FixedWorld,
        string? boneName = null,
        Vector3? localForward = null,
        Vector3? localUp = null,
        Vector3? worldForward = null,
        Vector3? worldRight = null)
        => TestStylePlugins.FaceSdf(
            "endfield",
            primaryTextureSlot,
            secondaryTextureSlot,
            encoding,
            transition,
            offset,
            trackingMode,
            boneName,
            localForward,
            localUp,
            worldForward,
            worldRight);

    internal static StyleMaterialBindingData EndfieldHair(
        int textureSlot = 0,
        Vector3? paletteColor = null,
        float brightness = 0f)
    {
        var color = paletteColor ?? new Vector3(0.2f);
        return new StyleMaterialBindingData(
            "endfield",
            "hair",
            TextureSlots: new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["texture"] = textureSlot,
            }),
            NumberValues: new ReadOnlyDictionary<string, double>(new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["brightness"] = brightness,
            }),
            VectorValues: new ReadOnlyDictionary<string, float[]>(new Dictionary<string, float[]>(StringComparer.Ordinal)
            {
                ["paletteColor"] = Vec(color),
            }));
    }

    internal static StyleMaterialBindingData EndfieldRim(
        Vector3? baseColor = null,
        Vector3? rimColor = null,
        float edgeSize = 0.2f,
        float edgeStrength = 0f,
        float directionRadians = 5.5f,
        float rangeStrength = 1f,
        float radialStrength = 0f)
    {
        var bc = baseColor ?? new Vector3(0.5f);
        var rc = rimColor ?? Vector3.One;
        return new StyleMaterialBindingData(
            "endfield",
            "rim",
            NumberValues: new ReadOnlyDictionary<string, double>(new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["edgeSize"] = edgeSize,
                ["edgeStrength"] = edgeStrength,
                ["directionRadians"] = directionRadians,
                ["rangeStrength"] = rangeStrength,
                ["radialStrength"] = radialStrength,
            }),
            VectorValues: new ReadOnlyDictionary<string, float[]>(new Dictionary<string, float[]>(StringComparer.Ordinal)
            {
                ["baseColor"] = Vec(bc),
                ["rimColor"] = Vec(rc),
            }));
    }

    internal static StyleMaterialBindingData EndfieldMatcap(
        int normalSlot = 0,
        int pSlot = 1,
        int matcapSlot = 2,
        string matcapEncoding = EncodingLinearRgb,
        int uvChannel = 0,
        Vector3? baseColor = null,
        Vector3? highlightColor = null,
        float normalStrength = 2f,
        float rotationRadians = 0f,
        float highlightCoefficient = 0f,
        float shadowHardness = 0.5f,
        Vector3? lightControlDirection = null,
        float baselineStrength = 1f,
        bool coupleEndfieldPalette = false,
        string? generatorRevision = null,
        EndfieldHighlightRangeSpec? highlightRange = null,
        EndfieldPaletteSpec? palette = null,
        EndfieldBaseImageSpec? baseImage = null)
    {
        var bc = baseColor ?? new Vector3(0.5f);
        var hc = highlightColor ?? Vector3.Zero;

        var slots = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["normal"] = normalSlot,
            ["p"] = pSlot,
            ["matcap"] = matcapSlot,
        };
        var numbers = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["uvChannel"] = uvChannel,
            ["normalStrength"] = normalStrength,
            ["rotationRadians"] = rotationRadians,
            ["highlightCoefficient"] = highlightCoefficient,
            ["shadowHardness"] = shadowHardness,
            ["baselineStrength"] = baselineStrength,
        };
        var bools = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["coupleEndfieldPalette"] = coupleEndfieldPalette,
        };
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal)
        {
            ["baseColor"] = Vec(bc),
            ["highlightColor"] = Vec(hc),
        };
        if (lightControlDirection is { } lcd)
            vectors["lightControlDirection"] = Vec(lcd);
        var strings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["matcapEncoding"] = matcapEncoding,
        };
        if (generatorRevision is not null)
            strings["generatorRevision"] = generatorRevision;

        if (highlightRange is not null)
        {
            bools["hasHighlightRange"] = true;
            MergeInto(highlightRange.ToBinding(), slots, numbers, bools, vectors, strings);
        }
        if (palette is not null)
        {
            bools["hasPalette"] = true;
            MergeInto(palette.ToBinding(), slots, numbers, bools, vectors, strings);
        }
        var effectiveBaseImage = baseImage ?? palette?.BaseImage;
        if (effectiveBaseImage is not null)
        {
            bools["hasBaseImage"] = true;
            MergeInto(effectiveBaseImage.ToBinding(), slots, numbers, bools, vectors, strings);
        }

        return new StyleMaterialBindingData(
            "endfield",
            "matcap",
            new ReadOnlyDictionary<string, int>(slots),
            new ReadOnlyDictionary<string, double>(numbers),
            new ReadOnlyDictionary<string, bool>(bools),
            new ReadOnlyDictionary<string, float[]>(vectors),
            new ReadOnlyDictionary<string, string>(strings));
    }

    private static void MergeInto(
        StyleMaterialBindingData src,
        Dictionary<string, int> slots,
        Dictionary<string, double> numbers,
        Dictionary<string, bool> bools,
        Dictionary<string, float[]> vectors,
        Dictionary<string, string> strings)
    {
        if (src.TextureSlots is not null)
            foreach (var (k, v) in src.TextureSlots) slots[k] = v;
        if (src.NumberValues is not null)
            foreach (var (k, v) in src.NumberValues) numbers[k] = v;
        if (src.BoolValues is not null)
            foreach (var (k, v) in src.BoolValues) bools[k] = v;
        if (src.VectorValues is not null)
            foreach (var (k, v) in src.VectorValues) vectors[k] = v;
        if (src.StringValues is not null)
            foreach (var (k, v) in src.StringValues) strings[k] = v;
    }

    internal static StyleValidationResult ValidateEndfield(
        StyleMaterialBindingData binding,
        StyleMaterialRole role = StyleMaterialRole.Body,
        int sourceTextureCount = 16,
        IReadOnlyList<(int Width, int Height)>? textureDimensions = null)
        => ValidateEndfield([binding], role, sourceTextureCount, textureDimensions);

    internal static StyleValidationResult ValidateEndfield(
        IReadOnlyList<StyleMaterialBindingData> bindings,
        StyleMaterialRole role = StyleMaterialRole.Body,
        int sourceTextureCount = 16,
        IReadOnlyList<(int Width, int Height)>? textureDimensions = null,
        int materialIndex = 0,
        string? materialName = null)
        => TestStylePlugins.Endfield.ValidateMaterial(new StyleMaterialValidationContext(
            Guid.Empty,
            materialIndex,
            materialName,
            role,
            bindings,
            sourceTextureCount,
            textureDimensions));

    internal static void EnsureValidEndfield(
        IReadOnlyList<StyleMaterialBindingData> bindings,
        StyleMaterialRole role = StyleMaterialRole.Body,
        int sourceTextureCount = 16,
        OriginalFrameScene? frame = null,
        int materialIndex = 0)
    {
        int texCount = frame?.Surface.Snapshot.Textures.Count ?? sourceTextureCount;
        var dims = frame?.Surface.Snapshot.Textures.Select(static t => (t.Width, t.Height)).ToArray();
        var result = TestStylePlugins.Endfield.ValidateMaterial(new StyleMaterialValidationContext(
            Guid.Empty,
            materialIndex,
            frame?.Surface.Snapshot.Materials[materialIndex].Name,
            role,
            bindings,
            texCount,
            dims));
        if (!result.IsValid)
            throw new ArgumentException(result.ErrorMessage ?? "Endfield material validation failed.");
    }

    internal static StyleDrawData? BuildEndfieldDrawData(
        IReadOnlyList<StyleMaterialBindingData> bindings,
        StyleMaterialRole role = StyleMaterialRole.Body,
        OriginalFrameScene? frame = null,
        int materialIndex = 0,
        float faceNormalShadeStrength = 0.35f,
        float faceGeometricShadowStrength = 1f,
        float eyeGeometricShadowStrength = 0.2f,
        StyleEvaluatedBoneTransform? evaluatedBone = null,
        StyleMaterialBindingData? styleGlobalParameters = null)
    {
        StyleDrawContext context;
        if (frame is not null)
        {
            var snapshot = frame.Surface.Snapshot;
            var mat = snapshot.Materials[materialIndex];
            var surf = frame.Surface.Materials[materialIndex];
            var flags = frame.Materials[materialIndex];
            context = new StyleDrawContext(
                Guid.Empty,
                materialIndex,
                mat.Name,
                role,
                bindings,
                mat.Diffuse,
                mat.Specular,
                mat.Shininess,
                mat.Ambient,
                mat.DiffuseTexture,
                surf.SphereTexture,
                (int)surf.SphereMode,
                mat.ToonTexture,
                surf.SphereMode == OriginalSphereMode.SubTexture ? surf.SphereTexture : -1,
                mat.Diffuse.W < 1f || flags.CullTransparentSurface,
                Vector3.Normalize(snapshot.LightDirection),
                frame.Surface.Profile.LightAmbient,
                (frame.Options?.CameraOverride ?? snapshot.Camera).Eye,
                null,
                null,
                new StyleFaceEyeDefaults(faceNormalShadeStrength, faceGeometricShadowStrength, eyeGeometricShadowStrength),
                null,
                evaluatedBone,
                styleGlobalParameters);
        }
        else
        {
            context = new StyleDrawContext(
                Guid.Empty,
                materialIndex,
                null,
                role,
                bindings,
                Vector4.One,
                Vector3.Zero,
                1f,
                Vector3.Zero,
                -1,
                -1,
                0,
                -1,
                -1,
                false,
                -Vector3.UnitZ,
                Vector3.Zero,
                new Vector3(0f, 0f, -5f),
                null,
                null,
                new StyleFaceEyeDefaults(faceNormalShadeStrength, faceGeometricShadowStrength, eyeGeometricShadowStrength),
                null,
                evaluatedBone,
                styleGlobalParameters);
        }
        return TestStylePlugins.Endfield.BuildMaterialDrawData(context);
    }

    internal static D3D12SurfaceStyleMap CreateDirectSurfaceStyleMap(
        OriginalFrameScene frame,
        IReadOnlyList<string> styleIds,
        IReadOnlyList<D3D12CharacterMaterialStyle>? materials = null,
        IReadOnlyList<IReadOnlyList<StyleMaterialBindingData>?>? bindingsByMaterial = null,
        IReadOnlyList<StyleEvaluatedBoneTransform?>? evaluatedBones = null,
        bool validatePlugin = false,
        D3D12StylePresetCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(styleIds);

        var activePresetCatalog = catalog ?? TestStylePlugins.AllPresetCatalog;
        var resolvedMaterials = new D3D12CharacterMaterialStyle[styleIds.Count];
        var snapshot = frame.Surface.Snapshot;

        for (int i = 0; i < styleIds.Count; i++)
        {
            var baseStyle = materials is not null ? materials[i] : new D3D12CharacterMaterialStyle();
            string styleId = styleIds[i];

            if (string.Equals(styleId, D3D12SurfaceStyleMap.DefaultStyleId, StringComparison.Ordinal))
            {
                resolvedMaterials[i] = baseStyle;
                continue;
            }

            if (baseStyle.PluginDraw is not null)
            {
                resolvedMaterials[i] = baseStyle;
                continue;
            }

            var plugin = TestStylePlugins.OfficialStyles.FirstOrDefault(s => string.Equals(s.StyleId, styleId, StringComparison.Ordinal));
            if (plugin is null)
            {
                resolvedMaterials[i] = baseStyle;
                continue;
            }

            var matBindings = bindingsByMaterial is not null && bindingsByMaterial[i] is { } b
                ? b
                : Array.Empty<StyleMaterialBindingData>();
            var role = D3D12StylePluginAdapter.ToContractRole(baseStyle.Role);

            if (validatePlugin && matBindings.Count > 0)
            {
                var dims = snapshot.Textures.Select(static t => (t.Width, t.Height)).ToArray();
                var validation = plugin.ValidateMaterial(new StyleMaterialValidationContext(
                    Guid.Empty,
                    i,
                    i < snapshot.Materials.Count ? snapshot.Materials[i].Name : null,
                    role,
                    matBindings,
                    snapshot.Textures.Count,
                    dims));
                if (!validation.IsValid)
                    throw new ArgumentException(validation.ErrorMessage ?? $"Material {i} validation failed for style '{styleId}'.");
            }

            StyleDrawData? rawDraw = null;
            if (i < snapshot.Materials.Count)
            {
                var mat = snapshot.Materials[i];
                var surf = frame.Surface.Materials[i];
                var flags = frame.Materials[i];
                var ctx = new StyleDrawContext(
                    Guid.Empty,
                    i,
                    mat.Name,
                    role,
                    matBindings,
                    mat.Diffuse,
                    mat.Specular,
                    mat.Shininess,
                    mat.Ambient,
                    mat.DiffuseTexture,
                    surf.SphereTexture,
                    (int)surf.SphereMode,
                    mat.ToonTexture,
                    surf.SphereMode == OriginalSphereMode.SubTexture ? surf.SphereTexture : -1,
                    mat.Diffuse.W < 1f || flags.CullTransparentSurface,
                    Vector3.Normalize(snapshot.LightDirection),
                    frame.Surface.Profile.LightAmbient,
                    (frame.Options?.CameraOverride ?? snapshot.Camera).Eye,
                    baseStyle.StylizedShading is { } ss ? D3D12StylePluginAdapter.ToContractStylizedShading(ss) : null,
                    baseStyle.PmxMaterialTreatment is { } tr ? D3D12StylePluginAdapter.ToContractTreatment(tr) : null,
                    new StyleFaceEyeDefaults(baseStyle.FaceNormalShadeStrength, baseStyle.FaceGeometricShadowStrength, baseStyle.EyeGeometricShadowStrength),
                    null,
                    evaluatedBones is not null ? evaluatedBones[i] : null,
                    null);
                rawDraw = plugin.BuildMaterialDrawData(ctx);
            }

            resolvedMaterials[i] = rawDraw is null
                ? baseStyle
                : baseStyle with { PluginDraw = D3D12StylePluginAdapter.ToD3D12DrawData(rawDraw) };
        }

        return new D3D12SurfaceStyleMap(styleIds, resolvedMaterials, activePresetCatalog);
    }

    internal static (StyleBufferBinding Binding, Vector4[] Texels) GetEndfieldShadowLut()
    {
        var palette = new EndfieldPaletteSpec(Vector3.Zero).ToBinding();
        var baseImg = new EndfieldBaseImageSpec(Vector3.One, Vector3.One, Vector3.One, Vector3.One).ToBinding();
        var matcap = EndfieldMatcap(coupleEndfieldPalette: true);
        var draw = BuildEndfieldDrawData([matcap, palette, baseImg], StyleMaterialRole.Body)
            ?? throw new InvalidOperationException("Expected Endfield draw data.");
        var lut = draw.BufferBindings?.FirstOrDefault(static b =>
            b.Kind == StyleBufferKind.CustomStructured && string.Equals(b.DataKey, "endfield-shadow-lut-v1", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Endfield shadow LUT buffer binding was not emitted.");
        var texels = MemoryMarshal.Cast<byte, Vector4>(lut.Data.Span).ToArray();
        return (lut, texels);
    }

    internal static Vector4[] ReadCbvVectors(StyleDrawData draw)
        => MemoryMarshal.Cast<byte, Vector4>(draw.ConstantBufferData.Span).ToArray();

    internal static D3D12PluginShaderModule GetEndfieldModule(string moduleId)
    {
        var reg = TestStylePlugins.AllViewCatalog.FindRegistration("endfield")
            ?? throw new InvalidOperationException("Endfield plugin registration missing.");
        if (reg.ShaderModules is null || !reg.ShaderModules.TryGetValue(moduleId, out var module))
            throw new InvalidOperationException($"Endfield shader module '{moduleId}' missing.");
        return module;
    }

    internal static string ResolveEndfieldModuleId(bool hair, bool rim, bool matcap, bool palette, bool baseImage)
    {
        if (!hair && !rim && !matcap) return "default";
        var parts = new List<string>(5);
        if (hair) parts.Add("hair");
        if (rim) parts.Add("rim");
        if (matcap) parts.Add("matcap");
        if (palette) parts.Add("palette");
        if (baseImage) parts.Add("base");
        return string.Join('-', parts);
    }

    internal static IEnumerable<object[]> SurfaceVariants()
    {
        foreach (bool native in new[] { false, true })
        foreach (bool gi in new[] { false, true })
        foreach (var variant in new[] { (Morph: false, Sub: false), (Morph: true, Sub: false), (Morph: true, Sub: true) })
            yield return [native ? (gi ? "PSSurfaceGiNative" : "PSSurfaceNative") : (gi ? "PSSurfaceGi" : "PSSurface"), native, gi, variant.Morph, variant.Sub];
    }

    internal static byte[] CompileEndfieldPixel(
        string moduleId,
        string entry,
        bool native,
        bool gi,
        bool morph,
        bool sub,
        bool characterToon = false,
        bool preserveDirectLightPalette = false)
    {
        var module = GetEndfieldModule(moduleId);
        var hooks = D3D12StyleShaderAssembler.ResolveEffectiveHooks(module, null);
        var geom = D3D12StyleShaderAssembler.ResolveEffectiveGeometry(module, sub);
        return D3D12StyleShaderAssembler.CompilePixel("endfield", entry, module, hooks, geom, native, gi, morph, sub, characterToon, preserveDirectLightPalette);
    }

    internal static string AssembleEndfieldPixelSource(
        string moduleId,
        string entry,
        bool native,
        bool gi,
        bool morph,
        bool sub,
        bool characterToon = false,
        bool preserveDirectLightPalette = false)
    {
        var module = GetEndfieldModule(moduleId);
        var hooks = D3D12StyleShaderAssembler.ResolveEffectiveHooks(module, null);
        var geom = D3D12StyleShaderAssembler.ResolveEffectiveGeometry(module, sub);
        return D3D12StyleShaderAssembler.AssemblePixelSource(entry, module, hooks, geom, native, gi, morph, sub, characterToon, preserveDirectLightPalette);
    }

    internal static byte[] CompileEndfieldVertex(string moduleId, bool native, bool gi, bool sub)
    {
        var module = GetEndfieldModule(moduleId);
        var geom = D3D12StyleShaderAssembler.ResolveEffectiveGeometry(module, sub);
        return D3D12StyleShaderAssembler.CompileVertex(module, geom, native, gi, sub);
    }

    internal static string AssembleEndfieldVertexSource(string moduleId, bool native, bool gi, bool sub)
    {
        var module = GetEndfieldModule(moduleId);
        var geom = D3D12StyleShaderAssembler.ResolveEffectiveGeometry(module, sub);
        return D3D12StyleShaderAssembler.AssembleVertexSource(geom, native, gi, sub);
    }
}
