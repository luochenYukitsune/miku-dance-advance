using System.Collections.ObjectModel;
using System.Numerics;
using Mmd.Modern.Plugins.Contracts;

namespace Mda.StylePlugin.Endfield;

/// <summary>
/// Rendering style definition for Arknights: Endfield, matching the 9d58eae numerical and shader contract 1:1.
/// </summary>
public sealed class EndfieldStyleDefinition : RenderingStyleDefinitionBase
{
    public const string CanonicalStyleId = "endfield";
    public const string FaceSdfModeId = "face-sdf";
    public const string HairModeId = "hair";
    public const string RimModeId = "rim";
    public const string MatcapModeId = "matcap";
    public const string PaletteModeId = "palette";
    public const string BaseImageModeId = "base-image";
    public const string HighlightRangeModeId = "highlight-range";

    private static readonly LocalizedText StyleDisplayName = LocalizedText.From(
        "Arknights: Endfield", zhCn: "明日方舟：终末地", en: "Arknights: Endfield", ja: "アークナイツ：エンドフィールド");

    private static readonly LocalizedText StyleDescription = LocalizedText.From(
        "Arknights: Endfield stylized rendering with three-knot diffuse response, EndfieldRgbCmM FaceSDF, radial hair, Fresnel rim, MikkTSpace MatCap, downstream palette, and base-image shadow LUT.",
        zhCn: "明日方舟：终末地风格化渲染：三段式漫反射、EndfieldRgbCmM 面部 SDF、径向发丝高光、物理菲涅尔边缘光、MikkTSpace MatCap、下游调色盘与基色阴影 LUT。",
        en: "Arknights: Endfield stylized rendering with three-knot diffuse response, EndfieldRgbCmM FaceSDF, radial hair, Fresnel rim, MikkTSpace MatCap, downstream palette, and base-image shadow LUT.",
        ja: "アークナイツ：エンドフィールドスタイル：3ノット拡散応答、EndfieldRgbCmM FaceSDF、放射状ヘア、フレネルリム、MikkTSpace MatCap、パレット、ベース画像シャドウ LUT。");

    private static readonly StyleOutlineDefaults OutlineDefaults = new(Width: 0.18f, Color: null, Enabled: true);
    private static readonly StyleToonResponseDefaults ToonDefaults = new(Threshold: 0.5f, TransitionWidth: 0.1f);
    private static readonly StyleFaceEyeDefaults FaceEyeDefaults = new(
        FaceNormalShadeStrength: 0.35f,
        FaceGeometricShadowStrength: 1.0f,
        EyeGeometricShadowStrength: 0.2f);

    private static readonly StyleStylizedShadingParameters StylizedDefaults = new(
        ShadowStrength: 1f,
        ShadowValue: 0.84f,
        ShadowHue: 0.4f,
        ShadowTint: new Vector3(1f, 0.96f, 0.94f),
        FaceFlatten: 0f,
        WarmColor: new Vector3(1f, 0.88f, 0.74f),
        WarmStrength: 0.5f,
        RimEdge: 0.56f,
        RimStrength: 0.26f,
        Saturation: 1f,
        Contrast: 0f,
        Brightness: 1.12f,
        OutlineValue: 0.6f,
        OutlineSaturation: 1.1f);

    private static readonly IReadOnlyDictionary<StyleMaterialRole, StyleMaterialTreatmentParameters> RoleTreatments =
        new ReadOnlyDictionary<StyleMaterialRole, StyleMaterialTreatmentParameters>(
            new Dictionary<StyleMaterialRole, StyleMaterialTreatmentParameters>
            {
                [StyleMaterialRole.Body] = new(
                    Saturation: 0.92f,
                    TransitionWidthScale: 0.6f,
                    ValueContrast: -0.2f,
                    ShadeGamma: 2f,
                    HairHighlightStrength: 0f,
                    HairHighlightWidth: 0.12f),
                [StyleMaterialRole.Hair] = new(
                    Saturation: 0.94f,
                    TransitionWidthScale: 0.7f,
                    ValueContrast: -0.14f,
                    ShadeGamma: 1.8f,
                    HairHighlightStrength: 0.3f,
                    HairHighlightWidth: 0.12f),
                [StyleMaterialRole.Face] = new(),
                [StyleMaterialRole.Eye] = new(),
            });

    private static readonly Vector3 DefaultHeadLocalForward = new(0f, 0f, -1f);
    private static readonly Vector3 DefaultHeadLocalUp = new(0f, 1f, 0f);

    private static readonly IReadOnlyList<StyleMaterialModeDescriptor> Modes = CreateMaterialModes();
    private static readonly IReadOnlyList<StyleShaderModule> ShaderModules = EndfieldShaders.CreateModules();

    public override string StyleId => CanonicalStyleId;
    public override LocalizedText DisplayName => StyleDisplayName;
    public override LocalizedText Description => StyleDescription;
    public override string Author => "MDA";
    public override Version Version { get; } = new(1, 0, 0);
    public override StyleOutlineDefaults DefaultOutline => OutlineDefaults;
    public override StyleToonResponseDefaults DefaultToonResponse => ToonDefaults;
    public override StyleStylizedShadingParameters DefaultStylizedShading => StylizedDefaults;
    public override StyleFaceEyeDefaults DefaultFaceEye => FaceEyeDefaults;
    public override IReadOnlyDictionary<StyleMaterialRole, StyleMaterialTreatmentParameters> DefaultRoleTreatments => RoleTreatments;
    public override IReadOnlyList<StyleMaterialModeDescriptor> MaterialModes => Modes;

    public override IReadOnlyList<StyleShaderModule> GetShaderModules() => ShaderModules;

    public override StyleValidationResult ValidateMaterial(StyleMaterialValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Bindings is null || context.Bindings.Count == 0)
            return StyleValidationResult.Ok;

        foreach (var b in context.Bindings)
        {
            if (b is null)
                return Error("Material bindings cannot contain null.",
                    "材质绑定列表不能包含空条目。",
                    "マテリアルバインディングに null を含めることはできません。");
            if (!string.Equals(b.StyleId, CanonicalStyleId, StringComparison.Ordinal))
                return Error($"Binding StyleId '{b.StyleId}' does not match '{CanonicalStyleId}'.",
                    $"绑定 StyleId “{b.StyleId}”不属于终末地风格。",
                    $"バインディングの StyleId '{b.StyleId}' は '{CanonicalStyleId}' と一致しません。");
        }

        if (!TryParseBindings(context.Bindings, out var parsed, out var parseError))
            return parseError!;

        if (parsed.FaceSdf is { } faceSdf)
            return ValidateFaceSdf(context, faceSdf);

        if (parsed.Hair is { } hair)
        {
            var err = ValidateHair(context, hair);
            if (!err.IsValid) return err;
        }

        if (parsed.Rim is { } rim)
        {
            var err = ValidateRim(context, rim);
            if (!err.IsValid) return err;
        }

        if (parsed.Matcap is not null)
        {
            var err = ValidateMatcapChain(context, parsed);
            if (!err.IsValid) return err;
        }
        else
        {
            if (parsed.HighlightRange is not null)
                return Error("Endfield highlight range requires an Endfield MatCap binding on the same material.",
                    "终末地高光范围模式需要同一材质上绑定 Endfield MatCap。",
                    "エンドフィールドのハイライト範囲は同一マテリアル上の Endfield MatCap バインディングを必要とします。");
            if (parsed.Palette is not null)
                return Error("Endfield downstream palette requires an Endfield MatCap binding with CoupleEndfieldPalette enabled.",
                    "终末地下游调色盘需要同一材质上绑定且启用 CoupleEndfieldPalette 的 MatCap。",
                    "エンドフィールドのパレットには CoupleEndfieldPalette を有効にした MatCap が必要です。");
            if (parsed.BaseImage is not null)
                return Error("Endfield BaseImage requires both Endfield MatCap and Palette bindings on the same material.",
                    "终末地 BaseImage 需要同一材质上的 MatCap 与调色盘绑定。",
                    "エンドフィールドの BaseImage には MatCap とパレットの両方のバインディングが必要です。");
        }

        return StyleValidationResult.Ok;
    }

    public override IReadOnlyList<StyleSceneTextureRequest> GetSceneTextureRequests(StyleMaterialBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var active = FilterBindings(context.Bindings);
        if (active.Count == 0 || !TryParseBindings(active, out var parsed, out _))
            return Array.Empty<StyleSceneTextureRequest>();

        var requests = new List<StyleSceneTextureRequest>();
        if (parsed.FaceSdf is { } face)
        {
            if (face.PrimarySlot >= 0)
                requests.Add(new StyleSceneTextureRequest(face.PrimarySlot, StyleTextureEncoding.Linear, StyleTextureMipRule.PointNoMip));
            if (face.SecondarySlot >= 0)
                requests.Add(new StyleSceneTextureRequest(face.SecondarySlot, StyleTextureEncoding.Linear, StyleTextureMipRule.PointNoMip));
            return requests;
        }

        if (parsed.Hair is { } hair && hair.TextureSlot >= 0)
            requests.Add(new StyleSceneTextureRequest(hair.TextureSlot, StyleTextureEncoding.Linear, StyleTextureMipRule.PointNoMip, IsOptional: true));

        if (parsed.Matcap is { } matcap)
        {
            if (matcap.NormalSlot >= 0)
                requests.Add(new StyleSceneTextureRequest(matcap.NormalSlot, StyleTextureEncoding.Linear, StyleTextureMipRule.PointNoMip, IsOptional: true));
            if (matcap.PSlot >= 0)
                requests.Add(new StyleSceneTextureRequest(matcap.PSlot, StyleTextureEncoding.Linear, StyleTextureMipRule.PointNoMip, IsOptional: true));
            if (matcap.MatcapSlot >= 0)
            {
                var enc = ParseColorEncoding(matcap.MatcapEncoding, out _);
                requests.Add(new StyleSceneTextureRequest(matcap.MatcapSlot, enc, StyleTextureMipRule.PointNoMip, IsOptional: true));
            }
        }

        if (parsed.Palette is { } palette)
        {
            if (string.Equals(palette.EmissionInputMode, "Texture", StringComparison.Ordinal) && palette.EmissionSlot is >= 0)
                requests.Add(new StyleSceneTextureRequest(palette.EmissionSlot.Value, StyleTextureEncoding.Linear, StyleTextureMipRule.PointNoMip, IsOptional: true));
            if (string.Equals(palette.ReceivedLightInputMode, "Texture", StringComparison.Ordinal) && palette.ReceivedLightSlot is >= 0)
                requests.Add(new StyleSceneTextureRequest(palette.ReceivedLightSlot.Value, StyleTextureEncoding.Linear, StyleTextureMipRule.PointNoMip, IsOptional: true));
        }

        if (parsed.BaseImage is { } baseImg)
        {
            if (string.Equals(baseImg.BaseInputMode, "Texture", StringComparison.Ordinal) && baseImg.BaseSlot is >= 0)
            {
                var enc = ParseColorEncoding(baseImg.BaseEncoding, out _);
                requests.Add(new StyleSceneTextureRequest(baseImg.BaseSlot.Value, enc, StyleTextureMipRule.PointNoMip, IsOptional: true));
            }
            if (string.Equals(baseImg.DetailInputMode, "Texture", StringComparison.Ordinal) && baseImg.DetailSlot is >= 0)
                requests.Add(new StyleSceneTextureRequest(baseImg.DetailSlot.Value, StyleTextureEncoding.Linear, StyleTextureMipRule.PointNoMip, IsOptional: true));
            if (string.Equals(baseImg.ShadowInputMode, "Texture", StringComparison.Ordinal) && baseImg.ShadowSlot is >= 0)
                requests.Add(new StyleSceneTextureRequest(baseImg.ShadowSlot.Value, StyleTextureEncoding.Linear, StyleTextureMipRule.PointNoMip, IsOptional: true));
        }

        return requests;
    }

    public override bool RequiresSourceGeometry(StyleMaterialBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var active = FilterBindings(context.Bindings);
        return active.Count > 0 && TryParseBindings(active, out var parsed, out _) && parsed.Matcap is not null;
    }

    public override StyleBoneTransformRequest? GetBoneTransformRequest(StyleMaterialBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var active = FilterBindings(context.Bindings);
        if (active.Count == 0 || !TryParseBindings(active, out var parsed, out _) || parsed.FaceSdf is null)
            return null;

        var bone = parsed.FaceSdf.BoneBinding;
        if (bone is { Mode: StyleBoneTrackingMode.SelectedBone } && !string.IsNullOrWhiteSpace(bone.BoneName))
        {
            return new StyleBoneTransformRequest(
                bone.BoneName.Trim(),
                bone.LocalForward ?? DefaultHeadLocalForward,
                bone.LocalUp ?? DefaultHeadLocalUp);
        }

        return null;
    }

    public override StyleDrawData? BuildMaterialDrawData(StyleDrawContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var active = FilterBindings(context.Bindings);
        if (active.Count == 0)
            return null;

        if (!TryParseBindings(active, out var parsed, out _))
            return new StyleDrawData(EndfieldShaders.DefaultModuleId);

        if (parsed.FaceSdf is { } face)
            return BuildFaceSdfDrawData(context, face);

        bool hasHair = parsed.Hair is not null;
        bool hasRim = parsed.Rim is { EdgeStrength: not 0f };
        bool hasMatcap = parsed.Matcap is not null;
        bool hasPalette = hasMatcap && parsed.Palette is not null;
        bool hasBaseImage = hasPalette && parsed.BaseImage is not null;

        if (!hasHair && !hasRim && !hasMatcap)
            return new StyleDrawData(EndfieldShaders.DefaultModuleId);

        string moduleId = EndfieldShaders.ResolveModuleId(hasHair, hasRim, hasMatcap, hasPalette, hasBaseImage);
        var cb = new StyleConstantBufferBuilder();
        var textures = new List<StyleTextureBinding>();
        var buffers = new List<StyleBufferBinding>();

        if (hasHair)
        {
            var h = parsed.Hair!;
            cb.Write(new Vector4(h.PaletteColor, h.Brightness));
            textures.Add(new StyleTextureBinding(
                0,
                ResolveComposedTextureIndex(context, h.TextureSlot),
                Encoding: StyleTextureEncoding.Linear,
                MipRule: StyleTextureMipRule.PointNoMip));
        }

        if (hasRim)
        {
            var r = parsed.Rim!;
            cb.Write(new Vector4(r.BaseColor, r.RangeStrength));
            cb.Write(new Vector4(r.RimColor, r.RadialStrength));
            cb.Write(new Vector4(r.EdgeSize, r.EdgeStrength, r.DirectionRadians, 0f));
        }

        if (hasMatcap)
        {
            var m = parsed.Matcap!;
            var hr = parsed.HighlightRange;
            var lightCtrl = m.LightControlDirection is { } dir ? Vector3.Normalize(dir) : Vector3.UnitZ;
            var matcapEncoding = ParseColorEncoding(m.MatcapEncoding, out bool matcapIsBgr);

            cb.Write(new Vector4(m.BaseColor, m.BaselineStrength));
            cb.Write(new Vector4(m.HighlightColor, m.NormalStrength));
            cb.Write(new Vector4(m.RotationRadians, m.HighlightCoefficient, m.ShadowHardness, m.CoupleEndfieldPalette ? 1f : 0f));
            cb.Write(new Vector4(lightCtrl, 0f));
            cb.Write(hr is not null
                ? new Vector4(1f, hr.OverallBrightness, hr.HighlightBrightness, hr.DirectionalShadowNormalStrength)
                : Vector4.Zero);
            cb.Write(hr is not null
                ? new Vector4(hr.DirectionalShadowWorldDirection, hr.DirectionalShadowWidth)
                : Vector4.Zero);
            cb.Write(new Vector4(matcapIsBgr ? 1f : 0f, 0f, 0f, 0f));

            textures.Add(new StyleTextureBinding(1, ResolveComposedTextureIndex(context, m.NormalSlot), Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip));
            textures.Add(new StyleTextureBinding(2, ResolveComposedTextureIndex(context, m.PSlot), Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip));
            textures.Add(new StyleTextureBinding(3, ResolveComposedTextureIndex(context, m.MatcapSlot), Encoding: matcapEncoding, MipRule: StyleTextureMipRule.PointNoMip));
            buffers.Add(new StyleBufferBinding(0, StyleBufferKind.MikkTangentCorners, $"uv{m.UvChannel}"));
        }

        if (hasPalette)
        {
            var p = parsed.Palette!;
            float emissionMode = InputModeCode(p.EmissionInputMode);
            float receivedMode = InputModeCode(p.ReceivedLightInputMode);
            var primaryRadiance = string.Equals(p.ReceivedLightInputMode, "RendererDiffuse", StringComparison.Ordinal)
                ? context.PrimaryLightColor
                : Vector3.Zero;

            cb.Write(new Vector4(p.EmissionStrength, p.UniformEmissionBlue, p.Density, emissionMode));
            cb.Write(new Vector4(p.ReceivedLightLinearRgb, p.LightInfluence));
            cb.Write(new Vector4(receivedMode, primaryRadiance.X, primaryRadiance.Y, primaryRadiance.Z));

            if (p.ConsumesEmissionTexture)
                textures.Add(new StyleTextureBinding(4, ResolveComposedTextureIndex(context, p.EmissionSlot!.Value), Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip));
            if (p.ConsumesReceivedLightTexture)
                textures.Add(new StyleTextureBinding(5, ResolveComposedTextureIndex(context, p.ReceivedLightSlot!.Value), Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip));
        }

        if (hasBaseImage)
        {
            var b = parsed.BaseImage!;
            var baseEncoding = ParseColorEncoding(b.BaseEncoding, out bool baseIsBgr);

            cb.Write(new Vector4(b.BaseImageLinearRgb, b.Contrast));
            cb.Write(new Vector4(b.ModelColor, b.ShadowFactor));
            cb.Write(new Vector4(b.ShadowColor, b.WarmTint));
            cb.Write(new Vector4(b.ColorMultiplier, b.Saturation));
            cb.Write(new Vector4(b.DetailBlue, b.ShadowStrength, InputModeCode(b.BaseInputMode), InputModeCode(b.DetailInputMode)));
            cb.Write(new Vector4(InputModeCode(b.ShadowInputMode), baseIsBgr ? 1f : 0f, 0f, 0f));
            cb.Write(new Vector4(b.DirectionalShadowWorldDirection ?? Vector3.UnitZ, b.DirectionalShadowWidth ?? 0.3f));
            cb.Write(new Vector4(b.HasFacingBase ? 1f : 0f, b.FacingBaseBlend, b.HasDirectionalShadow ? 1f : 0f, b.DirectionalShadowNormalStrength ?? 2f));

            if (b.ConsumesBaseTexture)
                textures.Add(new StyleTextureBinding(6, ResolveComposedTextureIndex(context, b.BaseSlot!.Value), Encoding: baseEncoding, MipRule: StyleTextureMipRule.PointNoMip));
            if (b.ConsumesDetailTexture)
                textures.Add(new StyleTextureBinding(7, ResolveComposedTextureIndex(context, b.DetailSlot!.Value), Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip));
            if (b.ConsumesShadowTexture)
                textures.Add(new StyleTextureBinding(8, ResolveComposedTextureIndex(context, b.ShadowSlot!.Value), Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip));

            buffers.Add(new StyleBufferBinding(
                SlotIndex: 1,
                Kind: StyleBufferKind.CustomStructured,
                DataKey: EndfieldShadowLut.DataKey,
                ElementStrideBytes: EndfieldShadowLut.ElementStrideBytes,
                ElementCount: EndfieldShadowLut.ElementCount,
                Data: EndfieldShadowLut.Bytes));
        }

        return new StyleDrawData(
            ShaderModuleId: moduleId,
            ConstantBufferData: cb.ToArray(),
            TextureBindings: textures,
            BufferBindings: buffers.Count == 0 ? null : buffers);
    }

    private static StyleDrawData BuildFaceSdfDrawData(StyleDrawContext context, ParsedFaceSdf face)
    {
        if (context.FaceEyeDefaults is { FaceNormalShadeStrength: 0f })
            return new StyleDrawData(EndfieldShaders.DefaultModuleId);

        int primaryComposed = ResolveComposedTextureIndex(context, face.PrimarySlot);
        int secondaryComposed = ResolveComposedTextureIndex(context, face.SecondarySlot);
        var (forward, right) = ResolveFaceBasis(context, face.BoneBinding);

        var cb = new StyleConstantBufferBuilder()
            .Write(new Vector4(forward, 0f))
            .Write(new Vector4(right, 0f))
            .Write(new Vector4(face.Transition, face.Offset, 0f, 0f))
            .ToArray();

        return new StyleDrawData(
            ShaderModuleId: EndfieldShaders.FaceSdfModuleId,
            ConstantBufferData: cb,
            TextureBindings:
            [
                new StyleTextureBinding(0, primaryComposed, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new StyleTextureBinding(1, secondaryComposed, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip)
            ]);
    }

    private sealed record ParsedFaceSdf(
        int PrimarySlot,
        int SecondarySlot,
        float Transition,
        float Offset,
        string Encoding,
        StyleBoneAxisBinding? BoneBinding);

    private sealed record ParsedHair(
        int TextureSlot,
        Vector3 PaletteColor,
        float Brightness);

    private sealed record ParsedRim(
        Vector3 BaseColor,
        Vector3 RimColor,
        float EdgeSize,
        float EdgeStrength,
        float DirectionRadians,
        float RangeStrength,
        float RadialStrength);

    private sealed record ParsedHighlightRange(
        float OverallBrightness,
        float HighlightBrightness,
        bool HasDirectionalShadow,
        Vector3 DirectionalShadowWorldDirection,
        float DirectionalShadowWidth,
        float DirectionalShadowNormalStrength);

    private sealed record ParsedPalette(
        Vector3 ReceivedLightLinearRgb,
        float EmissionStrength,
        float UniformEmissionBlue,
        float LightInfluence,
        float Density,
        string EmissionInputMode,
        int? EmissionSlot,
        string ReceivedLightInputMode,
        int? ReceivedLightSlot)
    {
        internal bool ConsumesEmissionTexture => string.Equals(EmissionInputMode, "Texture", StringComparison.Ordinal) && EmissionStrength != 0f;
        internal bool ConsumesReceivedLightTexture => string.Equals(ReceivedLightInputMode, "Texture", StringComparison.Ordinal) && Math.Clamp(LightInfluence, 0f, 1f) > 0f;
    }

    private sealed record ParsedBaseImage(
        Vector3 BaseImageLinearRgb,
        Vector3 ModelColor,
        Vector3 ShadowColor,
        Vector3 ColorMultiplier,
        float ShadowFactor,
        float WarmTint,
        float Contrast,
        float Saturation,
        float DetailBlue,
        float ShadowStrength,
        string BaseInputMode,
        int? BaseSlot,
        string BaseEncoding,
        string DetailInputMode,
        int? DetailSlot,
        string ShadowInputMode,
        int? ShadowSlot,
        bool HasFacingBase,
        float FacingBaseBlend,
        bool HasDirectionalShadow,
        Vector3? DirectionalShadowWorldDirection,
        float? DirectionalShadowWidth,
        float? DirectionalShadowNormalStrength)
    {
        internal bool ConsumesBaseTexture => string.Equals(BaseInputMode, "Texture", StringComparison.Ordinal);
        internal bool ConsumesDetailTexture => string.Equals(DetailInputMode, "Texture", StringComparison.Ordinal);
        internal bool ConsumesShadowTexture => string.Equals(ShadowInputMode, "Texture", StringComparison.Ordinal) && Math.Clamp(ShadowStrength, 0f, 1f) > 0f;
    }

    private sealed record ParsedMatcap(
        int NormalSlot,
        int PSlot,
        int MatcapSlot,
        string MatcapEncoding,
        int UvChannel,
        bool UvChannelValid,
        string? GeneratorRevision,
        Vector3 BaseColor,
        Vector3 HighlightColor,
        float NormalStrength,
        float RotationRadians,
        float HighlightCoefficient,
        float ShadowHardness,
        Vector3? LightControlDirection,
        float BaselineStrength,
        bool CoupleEndfieldPalette);

    private sealed class ParsedEndfieldBindings
    {
        public ParsedFaceSdf? FaceSdf { get; set; }
        public ParsedHair? Hair { get; set; }
        public ParsedRim? Rim { get; set; }
        public ParsedMatcap? Matcap { get; set; }
        public ParsedHighlightRange? HighlightRange { get; set; }
        public ParsedPalette? Palette { get; set; }
        public ParsedBaseImage? BaseImage { get; set; }
    }

    private static bool TryParseBindings(
        IReadOnlyList<StyleMaterialBindingData> active,
        out ParsedEndfieldBindings parsed,
        out StyleValidationResult? error)
    {
        parsed = new ParsedEndfieldBindings();
        error = null;
        var seenModes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var binding in active)
        {
            if (binding.VectorValues is not null)
            {
                foreach (var kv in binding.VectorValues)
                {
                    if (kv.Value is null || kv.Value.Length != 3 || !float.IsFinite(kv.Value[0]) || !float.IsFinite(kv.Value[1]) || !float.IsFinite(kv.Value[2]))
                    {
                        error = Error(
                            $"Vector parameter '{kv.Key}' must be a finite 3-component vector.",
                            $"向量参数“{kv.Key}”必须是有限的 3 分量向量。",
                            $"ベクトルパラメータ '{kv.Key}' は有限な3要素ベクトルである必要があります。");
                        return false;
                    }
                }
            }

            if (!seenModes.Add(binding.ModeId))
            {
                error = Error($"Duplicate Endfield material mode '{binding.ModeId}'.",
                    $"重复的终末地材质模式“{binding.ModeId}”。",
                    $"エンドフィールドのマテリアルモード '{binding.ModeId}' が重複しています。");
                return false;
            }

            switch (binding.ModeId)
            {
                case FaceSdfModeId:
                    parsed.FaceSdf = new ParsedFaceSdf(
                        TryGetSlot(binding, "primary", out int pSlot) ? pSlot : -1,
                        TryGetSlot(binding, "secondary", out int sSlot) ? sSlot : -1,
                        (float)GetDouble(binding, "transition", 1.0),
                        (float)GetDouble(binding, "offset", 1.0),
                        GetString(binding, "encoding") ?? "EndfieldRgbCmM",
                        binding.BoneBinding);
                    break;

                case HairModeId:
                    parsed.Hair = new ParsedHair(
                        TryGetSlot(binding, "texture", out int hSlot) ? hSlot : -1,
                        GetVector3(binding, "paletteColor", new Vector3(0.2f)),
                        (float)GetDouble(binding, "brightness", 0.0));
                    break;

                case RimModeId:
                    parsed.Rim = new ParsedRim(
                        GetVector3(binding, "baseColor", new Vector3(0.5f)),
                        GetVector3(binding, "rimColor", Vector3.One),
                        (float)GetDouble(binding, "edgeSize", 0.2),
                        (float)GetDouble(binding, "edgeStrength", 0.0),
                        (float)GetDouble(binding, "directionRadians", 5.5),
                        (float)GetDouble(binding, "rangeStrength", 1.0),
                        (float)GetDouble(binding, "radialStrength", 0.0));
                    break;

                case MatcapModeId:
                    double rawUv = GetDouble(binding, "uvChannel", 0.0);
                    bool uvInt = double.IsFinite(rawUv) && rawUv >= 0 && rawUv <= 4 && Math.Abs(rawUv - Math.Round(rawUv)) <= 1e-6;
                    parsed.Matcap = new ParsedMatcap(
                        TryGetSlot(binding, "normal", out int nSlot) ? nSlot : -1,
                        TryGetSlot(binding, "p", out int pMapSlot) ? pMapSlot : -1,
                        TryGetSlot(binding, "matcap", out int mSlot) ? mSlot : -1,
                        GetString(binding, "matcapEncoding") ?? "LinearRgb",
                        uvInt ? (int)Math.Round(rawUv) : -1,
                        uvInt,
                        GetString(binding, "generatorRevision"),
                        GetVector3(binding, "baseColor", new Vector3(0.5f)),
                        GetVector3(binding, "highlightColor", Vector3.Zero),
                        (float)GetDouble(binding, "normalStrength", 2.0),
                        (float)GetDouble(binding, "rotationRadians", 0.0),
                        (float)GetDouble(binding, "highlightCoefficient", 0.0),
                        (float)GetDouble(binding, "shadowHardness", 0.5),
                        TryGetVector3(binding, "lightControlDirection"),
                        (float)GetDouble(binding, "baselineStrength", 1.0),
                        GetBool(binding, "coupleEndfieldPalette", false));

                    if (GetBool(binding, "hasHighlightRange", false))
                    {
                        if (parsed.HighlightRange is not null)
                        {
                            error = Error("Conflicting embedded and standalone Endfield highlightRange bindings.",
                                "同一材质不能同时包含内嵌与独立的 highlightRange 绑定。",
                                "埋め込みと独立した highlightRange バインディングを同時に指定できません。");
                            return false;
                        }
                        parsed.HighlightRange = ParseHighlightRange(binding);
                    }
                    if (GetBool(binding, "hasPalette", false))
                    {
                        if (parsed.Palette is not null)
                        {
                            error = Error("Conflicting embedded and standalone Endfield palette bindings.",
                                "同一材质不能同时包含内嵌与独立的 palette 绑定。",
                                "埋め込みと独立した palette バインディングを同時に指定できません。");
                            return false;
                        }
                        parsed.Palette = ParsePalette(binding);
                    }
                    if (GetBool(binding, "hasBaseImage", false))
                    {
                        if (parsed.BaseImage is not null)
                        {
                            error = Error("Conflicting embedded and standalone Endfield baseImage bindings.",
                                "同一材质不能同时包含内嵌与独立的 baseImage 绑定。",
                                "埋め込みと独立した baseImage バインディングを同時に指定できません。");
                            return false;
                        }
                        parsed.BaseImage = ParseBaseImage(binding);
                    }
                    break;

                case HighlightRangeModeId:
                    if (parsed.HighlightRange is not null)
                    {
                        error = Error("Conflicting embedded and standalone Endfield highlightRange bindings.",
                            "同一材质不能同时包含内嵌与独立的 highlightRange 绑定。",
                            "埋め込みと独立した highlightRange バインディングを同時に指定できません。");
                        return false;
                    }
                    parsed.HighlightRange = ParseHighlightRange(binding);
                    break;

                case PaletteModeId:
                    if (parsed.Palette is not null)
                    {
                        error = Error("Conflicting embedded and standalone Endfield palette bindings.",
                            "同一材质不能同时包含内嵌与独立的 palette 绑定。",
                            "埋め込みと独立した palette バインディングを同時に指定できません。");
                        return false;
                    }
                    parsed.Palette = ParsePalette(binding);
                    break;

                case BaseImageModeId:
                    if (parsed.BaseImage is not null)
                    {
                        error = Error("Conflicting embedded and standalone Endfield baseImage bindings.",
                            "同一材质不能同时包含内嵌与独立的 baseImage 绑定。",
                            "埋め込みと独立した baseImage バインディングを同時に指定できません。");
                        return false;
                    }
                    parsed.BaseImage = ParseBaseImage(binding);
                    break;

                default:
                    error = Error($"Unsupported Endfield material mode '{binding.ModeId}'.",
                        $"不支持的终末地材质模式“{binding.ModeId}”。",
                        $"未対応のエンドフィールドマテリアルモード '{binding.ModeId}' です。");
                    return false;
            }
        }

        if (parsed.FaceSdf is not null && active.Count > 1)
        {
            error = Error("Endfield FaceSDF cannot be combined with other material modes on the same material.",
                "终末地 FaceSDF 不能与其他材质模式同时应用于同一材质。",
                "エンドフィールドの FaceSDF は他のマテリアルモードと同一マテリアル上で併用できません。");
            return false;
        }

        return true;
    }

    private static ParsedHighlightRange ParseHighlightRange(StyleMaterialBindingData binding)
    {
        bool hasDir = GetBool(binding, "hasHighlightRangeDirectionalShadow",
            binding.VectorValues?.ContainsKey("highlightRangeDirectionalShadowWorldDirection") == true);
        return new ParsedHighlightRange(
            (float)GetDouble(binding, "highlightRangeOverallBrightness", 0.0),
            (float)GetDouble(binding, "highlightRangeHighlightBrightness", 0.0),
            hasDir,
            GetVector3(binding, "highlightRangeDirectionalShadowWorldDirection", Vector3.UnitZ),
            (float)GetDouble(binding, "highlightRangeDirectionalShadowWidth", 0.3),
            (float)GetDouble(binding, "highlightRangeDirectionalShadowNormalStrength", 2.0));
    }

    private static ParsedPalette ParsePalette(StyleMaterialBindingData binding)
    {
        int? emSlot = TryGetSlot(binding, "paletteEmission", out int e) ? e : null;
        int? rlSlot = TryGetSlot(binding, "paletteReceivedLight", out int l) ? l : null;
        return new ParsedPalette(
            GetVector3(binding, "paletteReceivedLightLinearRgb", Vector3.Zero),
            (float)GetDouble(binding, "paletteEmissionStrength", 0.0),
            (float)GetDouble(binding, "paletteUniformEmissionBlue", 0.0),
            (float)GetDouble(binding, "paletteLightInfluence", 0.0),
            (float)GetDouble(binding, "paletteDensity", 0.0),
            GetString(binding, "paletteEmissionInputMode") ?? "Uniform",
            emSlot,
            GetString(binding, "paletteReceivedLightInputMode") ?? "Uniform",
            rlSlot);
    }

    private static ParsedBaseImage ParseBaseImage(StyleMaterialBindingData binding)
    {
        int? baseSlot = TryGetSlot(binding, "baseImage", out int b) ? b : null;
        int? detailSlot = TryGetSlot(binding, "baseImageDetail", out int d) ? d : null;
        int? shadowSlot = TryGetSlot(binding, "baseImageShadow", out int s) ? s : null;
        bool hasFacing = GetBool(binding, "hasFacingBase", binding.NumberValues?.ContainsKey("facingBaseBlend") == true);
        bool hasDir = GetBool(binding, "hasBaseDirectionalShadow", binding.VectorValues?.ContainsKey("baseDirectionalShadowWorldDirection") == true);
        return new ParsedBaseImage(
            GetVector3(binding, "baseImageLinearRgb", Vector3.One),
            GetVector3(binding, "baseModelColor", Vector3.One),
            GetVector3(binding, "baseShadowColor", Vector3.One),
            GetVector3(binding, "baseColorMultiplier", Vector3.One),
            (float)GetDouble(binding, "baseShadowFactor", 1.0),
            (float)GetDouble(binding, "baseWarmTint", 0.0),
            (float)GetDouble(binding, "baseContrast", 0.0),
            (float)GetDouble(binding, "baseSaturation", 1.0),
            (float)GetDouble(binding, "baseDetailBlue", 1.0),
            (float)GetDouble(binding, "baseShadowStrength", 1.0),
            GetString(binding, "baseInputMode") ?? "Uniform",
            baseSlot,
            GetString(binding, "baseEncoding") ?? "LinearRgb",
            GetString(binding, "detailInputMode") ?? "Uniform",
            detailSlot,
            GetString(binding, "shadowInputMode") ?? "Uniform",
            shadowSlot,
            hasFacing,
            (float)GetDouble(binding, "facingBaseBlend", 0.0),
            hasDir,
            hasDir ? TryGetVector3(binding, "baseDirectionalShadowWorldDirection") ?? Vector3.UnitZ : null,
            hasDir ? (float)GetDouble(binding, "baseDirectionalShadowWidth", 0.3) : null,
            hasDir ? (float)GetDouble(binding, "baseDirectionalShadowNormalStrength", 2.0) : null);
    }

    private StyleValidationResult ValidateFaceSdf(StyleMaterialValidationContext context, ParsedFaceSdf face)
    {
        if (context.ResolvedRole != StyleMaterialRole.Face)
            return Error("Dedicated FaceSDF requires an explicitly selected Face material role.",
                "专用 FaceSDF 需要材质角色显式指定为 Face。",
                "専用 FaceSDF には Face マテリアルロールの明示的な指定が必要です。");

        if (face.PrimarySlot < 0 || face.PrimarySlot >= context.SourceTextureCount)
            return Error("FaceSDF primary texture slot is out of range.",
                "FaceSDF 主贴图槽位超出有效范围。",
                "FaceSDF プライマリテクスチャスロットが範囲外です。");

        if (face.SecondarySlot < 0 || face.SecondarySlot >= context.SourceTextureCount)
            return Error("EndfieldRgbCmM FaceSDF requires an in-range secondary texture slot.",
                "EndfieldRgbCmM FaceSDF 需要有效的次要（cm_M）贴图槽位。",
                "EndfieldRgbCmM FaceSDF には有効なセカンダリ（cm_M）テクスチャスロットが必要です。");

        if (!string.Equals(face.Encoding, "EndfieldRgbCmM", StringComparison.Ordinal))
            return Error($"Endfield FaceSDF requires 'EndfieldRgbCmM' encoding, got '{face.Encoding}'.",
                $"终末地 FaceSDF 仅支持 'EndfieldRgbCmM' 编码，当前为“{face.Encoding}”。",
                $"エンドフィールド FaceSDF は 'EndfieldRgbCmM' エンコーディングのみサポートします（指定値: '{face.Encoding}'）。");

        if (!float.IsFinite(face.Transition) || face.Transition is < 0.001f or > 100f)
            return Error("FaceSDF transition must be finite and in [0.001, 100].",
                "FaceSDF 过渡宽度必须为有限值且位于 [0.001, 100]。",
                "FaceSDF の遷移幅は [0.001, 100] の範囲の有限値である必要があります。");

        if (!float.IsFinite(face.Offset) || face.Offset is < -2f or > 2f)
            return Error("FaceSDF offset must be finite and in [-2, 2].",
                "FaceSDF 偏移量必须为有限值且位于 [-2, 2]。",
                "FaceSDF オフセットは [-2, 2] の範囲の有限値である必要があります。");

        return ValidateBoneBinding(face.BoneBinding);
    }

    private static StyleValidationResult ValidateHair(StyleMaterialValidationContext context, ParsedHair hair)
    {
        if (context.ResolvedRole != StyleMaterialRole.Hair)
            return Error("Dedicated Endfield radial hair requires an explicitly selected Hair material role.",
                "终末地径向头发模式需要材质角色显式指定为 Hair。",
                "エンドフィールド放射状ヘアには Hair マテリアルロールの明示的な指定が必要です。");

        if (hair.TextureSlot < 0 || hair.TextureSlot >= context.SourceTextureCount)
            return Error("Endfield radial hair texture slot is out of range.",
                "终末地径向头发贴图槽位超出有效范围。",
                "エンドフィールド放射状ヘアのテクスチャスロットが範囲外です。");

        if (!InRange(hair.PaletteColor, 0f, 16f))
            return Error("Endfield radial hair PaletteColor channels must be finite and in [0, 16].",
                "终末地径向头发调色盘颜色分量必须为有限值且位于 [0, 16]。",
                "エンドフィールド放射状ヘアの PaletteColor 各成分は [0, 16] の有限値である必要があります。");

        if (!float.IsFinite(hair.Brightness) || hair.Brightness is < -10f or > 10f)
            return Error("Endfield radial hair Brightness must be finite and in [-10, 10].",
                "终末地径向头发亮度必须为有限值且位于 [-10, 10]。",
                "エンドフィールド放射状ヘアの Brightness は [-10, 10] の有限値である必要があります。");

        return StyleValidationResult.Ok;
    }

    private static StyleValidationResult ValidateRim(StyleMaterialValidationContext context, ParsedRim rim)
    {
        if (context.ResolvedRole is not (StyleMaterialRole.Body or StyleMaterialRole.Hair))
            return Error("Explicit Endfield rim requires Body or Hair role.",
                "终末地边缘光模式需要材质角色为 Body 或 Hair。",
                "エンドフィールドのリムモードには Body または Hair ロールが必要です。");

        if (!InRange(rim.BaseColor, 0f, 16f) || !InRange(rim.RimColor, 0f, 16f))
            return Error("Explicit rim palette channels must be finite and in [0, 16].",
                "边缘光颜色与底色分量必须为有限值且位于 [0, 16]。",
                "リムパレットの各成分は [0, 16] の有限値である必要があります。");

        if (!InScalarBound(rim.EdgeSize, 16f) || !InScalarBound(rim.EdgeStrength, 32f)
            || !InScalarBound(rim.DirectionRadians, 65536f)
            || !InScalarBound(rim.RangeStrength, 32f) || !InScalarBound(rim.RadialStrength, 32f))
            return Error("Explicit rim scalar parameter is outside its finite bounds.",
                "边缘光标量参数超出允许的有限范围。",
                "リムのスカラーパラメータが許容範囲外です。");

        return StyleValidationResult.Ok;
    }

    private StyleValidationResult ValidateMatcapChain(StyleMaterialValidationContext context, ParsedEndfieldBindings parsed)
    {
        if (context.ResolvedRole is not (StyleMaterialRole.Body or StyleMaterialRole.Hair))
            return Error("Explicit Endfield MatCap requires Body or Hair role.",
                "终末地 MatCap 模式需要材质角色为 Body 或 Hair。",
                "エンドフィールド MatCap には Body または Hair ロールが必要です。");

        var m = parsed.Matcap!;
        if (m.NormalSlot < 0 || m.NormalSlot >= context.SourceTextureCount
            || m.PSlot < 0 || m.PSlot >= context.SourceTextureCount
            || m.MatcapSlot < 0 || m.MatcapSlot >= context.SourceTextureCount)
            return Error("Endfield MatCap normal, P, or matcap texture slot is out of range.",
                "终末地 MatCap 的法线、P 或 MatCap 贴图槽位超出范围。",
                "エンドフィールド MatCap の法線・P・MatCap テクスチャスロットが範囲外です。");

        if (m.MatcapEncoding is not ("LinearRgb" or "SrgbRgb" or "Srgb" or "LinearBgr" or "SrgbBgr"))
            return Error($"Unsupported MatCap encoding '{m.MatcapEncoding}'.",
                $"不支持的 MatCap 编码“{m.MatcapEncoding}”。",
                $"未対応の MatCap エンコーディング '{m.MatcapEncoding}' です。");

        if (!m.UvChannelValid)
            return Error("Endfield MatCap UV channel must be an integer in [0, 4].",
                "终末地 MatCap UV 通道必须为 [0, 4] 之间的整数。",
                "エンドフィールド MatCap の UV チャンネルは [0, 4] の整数である必要があります。");

        if (m.GeneratorRevision is not null && !string.Equals(m.GeneratorRevision, "mikk-v1", StringComparison.Ordinal))
            return Error($"MatCap requires the supported 'mikk-v1' tangent generator revision, got '{m.GeneratorRevision}'.",
                $"MatCap 需要受支持的 'mikk-v1' 切线生成器版本，当前为“{m.GeneratorRevision}”。",
                $"MatCap には 'mikk-v1' タンジェント生成リビジョンが必要です（指定値: '{m.GeneratorRevision}'）。");

        if (!InRange(m.BaseColor, 0f, 16f) || !InRange(m.HighlightColor, 0f, 16f))
            return Error("Explicit MatCap palette channels must be finite and in [0, 16].",
                "MatCap 调色盘颜色分量必须为有限值且位于 [0, 16]。",
                "MatCap パレットの各成分は [0, 16] の有限値である必要があります。");

        if (!InScalarBound(m.NormalStrength, 32f) || !InScalarBound(m.RotationRadians, 65536f)
            || !InScalarBound(m.HighlightCoefficient, 32f) || !InScalarBound(m.ShadowHardness, 16f)
            || !InScalarBound(m.BaselineStrength, 32f))
            return Error("Explicit MatCap scalar parameter is outside its finite bounds.",
                "MatCap 标量参数超出允许的有限范围。",
                "MatCap スカラーパラメータが許容範囲外です。");

        if (m.LightControlDirection is { } ldir && (!IsFinite(ldir) || ldir.LengthSquared() is <= 0f or > 1e20f))
            return Error("Explicit world light-control direction must be finite, nonzero, and normalizable.",
                "世界空间光控方向必须为有限、非零且可归一化的向量。",
                "ワールドライト制御方向は有限・非ゼロかつ正規化可能である必要があります。");

        // Coupling validation verbatim from D3D12CharacterMaterialStyle & D3D12ViewEndfieldMatcapBindings
        if (parsed.Rim is not null || parsed.Hair is not null)
        {
            if (!m.CoupleEndfieldPalette)
                return Error("Same-material MatCap plus rim/radial hair requires CoupleEndfieldPalette=true.",
                    "同一材质上组合 MatCap 与边缘光或径向头发时，必须启用 CoupleEndfieldPalette。",
                    "同一マテリアル上で MatCap とリムまたは放射状ヘアを併用する場合、CoupleEndfieldPalette を有効にする必要があります。");
            if (parsed.Rim is { } r && r.BaseColor != m.BaseColor)
                return Error("Coupled Endfield MatCap and rim require the exact same BaseColor.",
                    "耦合的终末地 MatCap 与边缘光必须使用完全相同的 BaseColor。",
                    "連携する Endfield MatCap とリムには完全に同一の BaseColor が必要です。");
            if (parsed.Hair is { } h && h.PaletteColor != m.BaseColor)
                return Error("Coupled Endfield MatCap and radial hair require the exact same BaseColor / PaletteColor.",
                    "耦合的终末地 MatCap 与径向头发必须使用完全相同的 BaseColor / PaletteColor。",
                    "連携する Endfield MatCap と放射状ヘアには同一の BaseColor / PaletteColor が必要です。");
            if (parsed.Rim is { } r2 && m.BaselineStrength != r2.RangeStrength + r2.RadialStrength)
                return Error("Coupled MatCap BaselineStrength must equal rim RangeStrength + RadialStrength.",
                    "耦合的 MatCap BaselineStrength 必须等于边缘光 RangeStrength + RadialStrength。",
                    "連携 MatCap の BaselineStrength はリムの RangeStrength + RadialStrength と等しい必要があります。");
            if (parsed.Hair is not null && parsed.Rim is { RadialStrength: not 0f })
                return Error("Coupled MatCap with radial hair requires rim RadialStrength = 0 to avoid double-counting P.",
                    "耦合 MatCap 与径向头发同时启用时，边缘光的 RadialStrength 必须为 0，以避免重复计算 P。",
                    "放射状ヘアと連携する MatCap では二重加算を防ぐためリムの RadialStrength を 0 にする必要があります。");
        }

        if (parsed.HighlightRange is { } hr)
        {
            if (!float.IsFinite(hr.OverallBrightness) || hr.OverallBrightness is < 0f or > 4f
                || !float.IsFinite(hr.HighlightBrightness) || hr.HighlightBrightness is < 0f or > 4f)
                return Error("HighlightRange OverallBrightness and HighlightBrightness must be finite in [0, 4].",
                    "高光范围亮度参数必须为有限值且位于 [0, 4]。",
                    "HighlightRange の明度パラメータは [0, 4] の有限値である必要があります。");
            if (!hr.HasDirectionalShadow)
                return Error("Endfield HighlightRange requires an explicit directional shadow.",
                    "终末地 HighlightRange 需要显式方向阴影配置。",
                    "エンドフィールド HighlightRange には明示的なディレクショナルシャドウが必要です。");
            var dirErr = ValidateDirectionalShadow(hr.DirectionalShadowWorldDirection, hr.DirectionalShadowWidth, hr.DirectionalShadowNormalStrength);
            if (!dirErr.IsValid) return dirErr;
        }

        if (parsed.Palette is { } palette)
        {
            if (!m.CoupleEndfieldPalette)
                return Error("Downstream palette requires CoupleEndfieldPalette=true on the MatCap binding.",
                    "下游调色盘需要 MatCap 绑定启用 CoupleEndfieldPalette。",
                    "下流パレットには MatCap バインディングで CoupleEndfieldPalette=true が必要です。");

            var paletteErr = ValidatePalette(context, palette);
            if (!paletteErr.IsValid) return paletteErr;

            if (parsed.BaseImage is { } baseImg)
            {
                var baseErr = ValidateBaseImage(context, baseImg);
                if (!baseErr.IsValid) return baseErr;
            }

            var domainErr = ValidatePaletteAdapterDomain(parsed);
            if (!domainErr.IsValid) return domainErr;
        }
        else if (parsed.BaseImage is not null)
        {
            return Error("Endfield BaseImage requires a downstream Palette binding on the same material.",
                "终末地 BaseImage 需要同一材质上的下游调色盘绑定。",
                "エンドフィールド BaseImage には同一マテリアル上の下流パレットバインディングが必要です。");
        }

        return StyleValidationResult.Ok;
    }

    private static StyleValidationResult ValidatePalette(StyleMaterialValidationContext context, ParsedPalette p)
    {
        if (!InRange(p.ReceivedLightLinearRgb, -16f, 16f)
            || !InScalarBound(p.UniformEmissionBlue, 16f)
            || !InScalarBound(p.EmissionStrength, 32f)
            || !InScalarBound(p.LightInfluence, 32f)
            || !InScalarBound(p.Density, 32f))
            return Error("Endfield palette parameter is outside its finite bounds.",
                "终末地调色盘参数超出允许的有限范围。",
                "エンドフィールドパレットパラメータが許容範囲外です。");

        if (p.EmissionInputMode is not ("Uniform" or "Texture"))
            return Error($"Invalid palette EmissionInputMode '{p.EmissionInputMode}'. Expected 'Uniform' or 'Texture'.",
                $"无效的自发光输入模式“{p.EmissionInputMode}”，应为 'Uniform' 或 'Texture'。",
                $"無効な EmissionInputMode '{p.EmissionInputMode}' です。'Uniform' または 'Texture' を指定してください。");

        if (p.ReceivedLightInputMode is not ("Uniform" or "Texture" or "RendererDiffuse"))
            return Error($"Invalid palette ReceivedLightInputMode '{p.ReceivedLightInputMode}'.",
                $"无效的受光输入模式“{p.ReceivedLightInputMode}”。",
                $"無効な ReceivedLightInputMode '{p.ReceivedLightInputMode}' です。");

        if (string.Equals(p.EmissionInputMode, "Texture", StringComparison.Ordinal))
        {
            if (p.EmissionSlot is null || p.EmissionSlot < 0 || p.EmissionSlot >= context.SourceTextureCount)
                return Error("Palette texture emission mode requires an in-range 'paletteEmission' texture slot.",
                    "调色盘贴图自发光模式需要有效的 'paletteEmission' 贴图槽位。",
                    "テクスチャエミッションモードには有効な 'paletteEmission' スロットが必要です。");
        }
        else if (p.EmissionSlot is >= 0)
        {
            return Error("Non-texture emission input cannot carry a 'paletteEmission' texture slot.",
                "非贴图自发光模式不能携带 'paletteEmission' 贴图槽位。",
                "非テクスチャエミッション入力に 'paletteEmission' スロットは指定できません。");
        }

        if (string.Equals(p.ReceivedLightInputMode, "Texture", StringComparison.Ordinal))
        {
            if (p.ReceivedLightSlot is null || p.ReceivedLightSlot < 0 || p.ReceivedLightSlot >= context.SourceTextureCount)
                return Error("Palette texture received-light mode requires an in-range 'paletteReceivedLight' texture slot.",
                    "调色盘贴图受光模式需要有效的 'paletteReceivedLight' 贴图槽位。",
                    "テクスチャ受光モードには有効な 'paletteReceivedLight' スロットが必要です。");
        }
        else if (p.ReceivedLightSlot is >= 0)
        {
            return Error("Non-texture received-light input cannot carry a 'paletteReceivedLight' texture slot.",
                "非贴图受光模式不能携带 'paletteReceivedLight' 贴图槽位。",
                "非テクスチャ受光入力に 'paletteReceivedLight' スロットは指定できません。");
        }

        return StyleValidationResult.Ok;
    }

    private static StyleValidationResult ValidateBaseImage(StyleMaterialValidationContext context, ParsedBaseImage b)
    {
        if (b.HasDirectionalShadow && string.Equals(b.ShadowInputMode, "Texture", StringComparison.Ordinal))
            return Error("Directional shadow and explicit shadow texture are mutually exclusive.",
                "方向阴影与显式阴影贴图不能同时启用。",
                "ディレクショナルシャドウと明示的なシャドウテクスチャは同時に使用できません。");

        if (!InRange(b.BaseImageLinearRgb, -16f, 16f)
            || !InRange(b.ModelColor, -16f, 16f)
            || !InRange(b.ShadowColor, -16f, 16f)
            || !InRange(b.ColorMultiplier, -16f, 16f)
            || !InScalarBound(b.ShadowFactor, 32f)
            || !InScalarBound(b.WarmTint, 32f)
            || !InScalarBound(b.Contrast, 4f)
            || !InScalarBound(b.Saturation, 16f)
            || !InScalarBound(b.DetailBlue, 16f)
            || !InScalarBound(b.ShadowStrength, 32f))
            return Error("Endfield BaseImage parameter is outside its finite bounds.",
                "终末地 BaseImage 参数超出允许的有限范围。",
                "エンドフィールド BaseImage パラメータが許容範囲外です。");

        if (b.BaseInputMode is not ("Uniform" or "Texture")
            || b.DetailInputMode is not ("Uniform" or "Texture")
            || b.ShadowInputMode is not ("Uniform" or "Texture"))
            return Error("BaseImage inputs support only 'Uniform' or 'Texture' mode.",
                "BaseImage 输入仅支持 'Uniform' 或 'Texture' 模式。",
                "BaseImage 入力は 'Uniform' または 'Texture' モードのみサポートします。");

        if (b.BaseEncoding is not ("LinearRgb" or "SrgbRgb" or "Srgb" or "LinearBgr" or "SrgbBgr"))
            return Error($"Unsupported BaseImage encoding '{b.BaseEncoding}'.",
                $"不支持的 BaseImage 编码“{b.BaseEncoding}”。",
                $"未対応の BaseImage エンコーディング '{b.BaseEncoding}' です。");

        if (string.Equals(b.BaseInputMode, "Uniform", StringComparison.Ordinal)
            && !string.Equals(b.BaseEncoding, "LinearRgb", StringComparison.Ordinal))
            return Error("Uniform base RGB must be explicitly 'LinearRgb'.",
                "统一基色数值模式下编码必须显式为 'LinearRgb'。",
                "均一ベース RGB モードではエンコーディングが 'LinearRgb' である必要があります。");

        var slotErr = ValidateInputSlot(b.BaseInputMode, b.BaseSlot, "baseImage", context.SourceTextureCount)
            ?? ValidateInputSlot(b.DetailInputMode, b.DetailSlot, "baseImageDetail", context.SourceTextureCount)
            ?? ValidateInputSlot(b.ShadowInputMode, b.ShadowSlot, "baseImageShadow", context.SourceTextureCount);
        if (slotErr is not null)
            return slotErr;

        if (b.HasFacingBase && (!float.IsFinite(b.FacingBaseBlend) || b.FacingBaseBlend is < 0f or > 1f))
            return Error("FacingBase blend must be finite and in [0, 1].",
                "FacingBase 混合系数必须为有限值且位于 [0, 1]。",
                "FacingBase ブレンドは [0, 1] の範囲の有限値である必要があります。");

        if (b.HasDirectionalShadow)
        {
            var dirErr = ValidateDirectionalShadow(
                b.DirectionalShadowWorldDirection ?? Vector3.UnitZ,
                b.DirectionalShadowWidth ?? 0.3f,
                b.DirectionalShadowNormalStrength ?? 2f);
            if (!dirErr.IsValid) return dirErr;
        }

        return StyleValidationResult.Ok;
    }

    private static StyleValidationResult? ValidateInputSlot(string mode, int? slot, string name, int textureCount)
    {
        if (string.Equals(mode, "Texture", StringComparison.Ordinal))
        {
            if (slot is null || slot < 0 || slot >= textureCount)
                return Error($"Texture mode for '{name}' requires an in-range texture index.",
                    $"贴图模式下的“{name}”需要有效的源贴图索引。",
                    $"テクスチャモードの '{name}' には有効なテクスチャインデックスが必要です。");
        }
        else if (slot is >= 0)
        {
            return Error($"Uniform mode for '{name}' cannot carry a texture index.",
                $"统一数值模式下的“{name}”不能指定贴图索引。",
                $"均一モードの '{name}' にテクスチャインデックスは指定できません。");
        }
        return null;
    }

    private static StyleValidationResult ValidateDirectionalShadow(Vector3 direction, float width, float normalStrength)
    {
        float length = direction.Length();
        if (!IsFinite(direction) || !float.IsFinite(length) || MathF.Abs(length - 1f) > 1e-4f)
            return Error("Directional shadow WorldDirection must be finite and normalized within 0.0001.",
                "方向阴影的世界方向必须为有限单位向量（误差不超过 0.0001）。",
                "ディレクショナルシャドウの WorldDirection は 0.0001 以内で正規化された有限ベクトルである必要があります。");
        if (!float.IsFinite(width) || width is < -4f or > 4f)
            return Error("Directional shadow Width must be finite and in [-4, 4].",
                "方向阴影宽度必须为有限值且位于 [-4, 4]。",
                "ディレクショナルシャドウの幅は [-4, 4] の有限値である必要があります。");
        if (!float.IsFinite(normalStrength) || normalStrength is < 0f or > 16f)
            return Error("Directional shadow NormalStrength must be finite and in [0, 16].",
                "方向阴影法线强度必须为有限值且位于 [0, 16]。",
                "ディレクショナルシャドウの法線強度は [0, 16] の有限値である必要があります。");
        return StyleValidationResult.Ok;
    }

    private static StyleValidationResult ValidatePaletteAdapterDomain(ParsedEndfieldBindings parsed)
    {
        var parent = parsed.Matcap!;
        var palette = parsed.Palette!;
        var rim = parsed.Rim;
        var hair = parsed.Hair;

        double r = Math.Abs((double)(rim?.EdgeStrength ?? 0f));
        double p = hair is not null ? 0.75 * Math.Abs((double)hair.Brightness + 1.5001001358032227) : 0.0;
        double baseline = Math.Abs((double)parent.BaselineStrength);
        double blue = string.Equals(palette.EmissionInputMode, "Texture", StringComparison.Ordinal)
            ? 1.0 : Math.Abs((double)palette.UniformEmissionBlue);
        double influence = Math.Clamp((double)palette.LightInfluence, 0.0, 1.0);
        var initial = parent.BaseColor;
        double? provided = parsed.BaseImage is { } baseImg ? ComputeBaseImageMagnitude(baseImg) : null;
        var highlight = parent.HighlightColor;
        var light = palette.ReceivedLightLinearRgb;
        var tint = rim?.RimColor ?? Vector3.Zero;
        double litBound = 0, oldHairBound = 0, oldRimBound = 0, baselineBound = 0;

        for (int channel = 0; channel < 3; channel++)
        {
            double oldInitial = initial[channel];
            double cpre = provided ?? oldInitial;
            double cmat = cpre * Math.Max(1.0, highlight[channel]);
            double partial = cmat + cpre * 2.0 * blue * Math.Abs((double)palette.EmissionStrength)
                + influence * (string.Equals(palette.ReceivedLightInputMode, "Texture", StringComparison.Ordinal)
                    ? 1.0 : Math.Abs((double)light[channel]));
            double f = Math.Clamp((double)(rim?.EdgeStrength ?? 0f), 0.0, 1.0);
            double coefficient = f * (2.0 * tint[channel] - 1.0);
            double OverlayBound(double a) => a * Math.Max(1.0, Math.Max(Math.Abs(1.0 + coefficient), Math.Abs(1.0 - coefficient)))
                + Math.Abs(coefficient);
            litBound = Math.Max(litBound, OverlayBound(partial));
            oldHairBound = Math.Max(oldHairBound, oldInitial * p);
            oldRimBound = Math.Max(oldRimBound, r == 0 ? 0.0 : OverlayBound(oldInitial) * (baseline + r) + oldInitial * baseline);
            baselineBound = Math.Max(baselineBound, cpre * baseline);
        }

        double final = litBound * (baseline + r + p);
        double delta = final + baselineBound + oldHairBound + oldRimBound;
        if (!double.IsFinite(delta) || delta > 60_000.0)
            return Error("Endfield downstream palette exceeds the MDA FP16 adapter safety domain; reduce combined colors or strengths.",
                "终末地下游调色盘组合亮度超出 FP16 安全域（60000），请调低颜色或强度参数。",
                "エンドフィールドの下流パレットの合成値が FP16 安全領域を超えています。色または強度を下げてください。");

        return StyleValidationResult.Ok;
    }

    private static double ComputeBaseImageMagnitude(ParsedBaseImage b)
    {
        static double Magnitude(Vector3 v) => Math.Max(Math.Abs((double)v.X), Math.Max(Math.Abs((double)v.Y), Math.Abs((double)v.Z)));
        double x = string.Equals(b.BaseInputMode, "Texture", StringComparison.Ordinal) ? 1.0 : Magnitude(b.BaseImageLinearRgb);
        if (b.HasFacingBase && b.FacingBaseBlend > 0f)
            x = Math.Max(x, x * x);
        double back = Math.Max(x, x * x);
        double contrast = Math.Abs((double)b.Contrast);
        double contrasted = back * (1.0 + contrast) + back * back * contrast;
        double detail = string.Equals(b.DetailInputMode, "Texture", StringComparison.Ordinal) ? 1.0 : Math.Abs(0.5 + 0.5 * b.DetailBlue);
        double shadow = Math.Max(1.0, Math.Max(Magnitude(b.ModelColor), Magnitude(b.ShadowColor)));
        double shadowFactor = Math.Clamp((double)b.ShadowStrength, 0.0, 1.0);
        return contrasted * Magnitude(b.ColorMultiplier) * detail * ((1.0 - shadowFactor) + shadowFactor * shadow);
    }

    private static StyleValidationResult ValidateBoneBinding(StyleBoneAxisBinding? bone)
    {
        if (bone is null)
            return Error("FaceSDF requires an explicit FixedWorld or SelectedBone BoneBinding.",
                "FaceSDF 需要显式指定 FixedWorld 或 SelectedBone 骨骼轴向绑定。",
                "FaceSDF には FixedWorld または SelectedBone のボーン軸バインディングが必要です。");

        if (bone.Mode == StyleBoneTrackingMode.FixedWorld)
        {
            if (bone.FixedWorldForward is not { } fwd || bone.FixedWorldRight is not { } right)
                return Error("FixedWorld FaceSDF requires FixedWorldForward and FixedWorldRight vectors.",
                    "固定世界坐标模式的 FaceSDF 需要提供前向与右向向量。",
                    "FixedWorld モードの FaceSDF には前方および右方向ベクトルが必要です。");
            if (!TryOrthonormalize(fwd, right, out _, out _))
                return Error("FixedWorld FaceSDF head axes must be finite, nonzero, and non-parallel.",
                    "FaceSDF 固定世界头部轴向必须为有限、非零且不平行的向量。",
                    "FixedWorld FaceSDF の頭部軸は有限・非ゼロかつ非平行である必要があります。");
            return StyleValidationResult.Ok;
        }

        if (bone.Mode == StyleBoneTrackingMode.SelectedBone)
        {
            if (string.IsNullOrWhiteSpace(bone.BoneName))
                return Error("SelectedBone FaceSDF requires a non-empty BoneName.",
                    "骨骼追踪模式的 FaceSDF 需要提供非空骨骼名称。",
                    "SelectedBone モードの FaceSDF には空でないボーン名が必要です。");
            var localFwd = bone.LocalForward ?? DefaultHeadLocalForward;
            var localUp = bone.LocalUp ?? DefaultHeadLocalUp;
            if (!IsValidDirectionPair(localFwd, localUp))
                return Error("SelectedBone FaceSDF local forward and up axes must be finite, nonzero, and non-parallel.",
                    "FaceSDF 本地骨骼前向与上向轴必须为有限、非零且不平行的向量。",
                    "SelectedBone FaceSDF のローカル前方・上方向軸は有限・非ゼロかつ非平行である必要があります。");
            return StyleValidationResult.Ok;
        }

        return Error($"Unknown FaceSDF bone tracking mode '{bone.Mode}'.",
            $"未知的 FaceSDF 骨骼追踪模式“{bone.Mode}”。",
            $"不明な FaceSDF ボーントラッキングモード '{bone.Mode}' です。");
    }

    private static (Vector3 Forward, Vector3 Right) ResolveFaceBasis(StyleDrawContext context, StyleBoneAxisBinding? bone)
    {
        if (bone is { Mode: StyleBoneTrackingMode.SelectedBone })
        {
            if (context.EvaluatedBone is { } evaluated && TryOrthonormalize(evaluated.WorldForward, evaluated.WorldRight, out var ef, out var er))
                return (ef, er);
            if (bone.FixedWorldForward is { } fwf && bone.FixedWorldRight is { } fwr && TryOrthonormalize(fwf, fwr, out var ff, out var fr))
                return (ff, fr);
            return (new Vector3(0f, 0f, -1f), new Vector3(1f, 0f, 0f));
        }

        if (bone is { FixedWorldForward: { } wf, FixedWorldRight: { } wr } && TryOrthonormalize(wf, wr, out var outF, out var outR))
            return (outF, outR);

        return (new Vector3(0f, 0f, -1f), new Vector3(1f, 0f, 0f));
    }

    private static bool TryOrthonormalize(Vector3 rawForward, Vector3 rawRight, out Vector3 forward, out Vector3 right)
    {
        forward = default;
        right = default;
        if (!IsFinite(rawForward) || !IsFinite(rawRight))
            return false;
        float fLen2 = rawForward.LengthSquared(), rLen2 = rawRight.LengthSquared();
        if (!float.IsFinite(fLen2) || !float.IsFinite(rLen2) || fLen2 < 1e-12f || rLen2 < 1e-12f)
            return false;
        forward = Vector3.Normalize(rawForward);
        var projectedRight = Vector3.Normalize(rawRight);
        projectedRight -= forward * Vector3.Dot(forward, projectedRight);
        float pLen2 = projectedRight.LengthSquared();
        if (!float.IsFinite(pLen2) || pLen2 < 1e-8f)
            return false;
        right = Vector3.Normalize(projectedRight);
        return true;
    }

    private static bool IsValidDirectionPair(Vector3 a, Vector3 b)
    {
        if (!IsFinite(a) || !IsFinite(b))
            return false;
        float al2 = a.LengthSquared(), bl2 = b.LengthSquared();
        if (!float.IsFinite(al2) || !float.IsFinite(bl2) || al2 < 1e-12f || bl2 < 1e-12f)
            return false;
        var cross = Vector3.Cross(Vector3.Normalize(a), Vector3.Normalize(b));
        return float.IsFinite(cross.LengthSquared()) && cross.LengthSquared() >= 1e-8f;
    }

    private static StyleTextureEncoding ParseColorEncoding(string value, out bool isBgr)
    {
        isBgr = value is "LinearBgr" or "SrgbBgr";
        return value is "SrgbRgb" or "Srgb" or "SrgbBgr"
            ? StyleTextureEncoding.SrgbColor
            : StyleTextureEncoding.Linear;
    }

    private static float InputModeCode(string mode) => mode switch
    {
        "Texture" => 1f,
        "RendererDiffuse" => 2f,
        _ => 0f
    };

    private static List<StyleMaterialBindingData> FilterBindings(IReadOnlyList<StyleMaterialBindingData>? bindings)
    {
        var result = new List<StyleMaterialBindingData>();
        if (bindings is null) return result;
        foreach (var b in bindings)
        {
            if (b is not null && string.Equals(b.StyleId, CanonicalStyleId, StringComparison.Ordinal))
                result.Add(b);
        }
        return result;
    }

    private static int ResolveComposedTextureIndex(StyleDrawContext context, int localTextureIndex)
    {
        if (context.ResolvedTextures is { Count: > 0 } resolved)
        {
            for (int i = 0; i < resolved.Count; i++)
            {
                if (resolved[i].LocalTextureIndex == localTextureIndex)
                    return resolved[i].ComposedTextureIndex;
            }
        }
        return localTextureIndex;
    }

    private static bool TryGetSlot(StyleMaterialBindingData binding, string key, out int slot)
    {
        if (binding.TextureSlots is not null && binding.TextureSlots.TryGetValue(key, out slot))
            return true;
        slot = -1;
        return false;
    }

    private static double GetDouble(StyleMaterialBindingData binding, string key, double fallback)
        => binding.NumberValues is not null && binding.NumberValues.TryGetValue(key, out double val) ? val : fallback;

    private static bool GetBool(StyleMaterialBindingData binding, string key, bool fallback)
        => binding.BoolValues is not null && binding.BoolValues.TryGetValue(key, out bool val) ? val : fallback;

    private static string? GetString(StyleMaterialBindingData binding, string key)
        => binding.StringValues is not null && binding.StringValues.TryGetValue(key, out string? val) ? val : null;

    private static Vector3 GetVector3(StyleMaterialBindingData binding, string key, Vector3 fallback)
    {
        if (binding.VectorValues is not null && binding.VectorValues.TryGetValue(key, out var arr) && arr.Length >= 3)
            return new Vector3(arr[0], arr[1], arr[2]);
        return fallback;
    }

    private static Vector3? TryGetVector3(StyleMaterialBindingData binding, string key)
    {
        if (binding.VectorValues is not null && binding.VectorValues.TryGetValue(key, out var arr) && arr.Length >= 3)
            return new Vector3(arr[0], arr[1], arr[2]);
        return null;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static bool InRange(Vector3 v, float min, float max)
        => IsFinite(v) && v.X >= min && v.X <= max && v.Y >= min && v.Y <= max && v.Z >= min && v.Z <= max;

    private static bool InScalarBound(float value, float bound)
        => float.IsFinite(value) && value >= -bound && value <= bound;

    private static StyleValidationResult Error(string en, string zhCn, string ja) =>
        StyleValidationResult.Error(LocalizedText.From(en, zhCn: zhCn, en: en, ja: ja));

    private static IReadOnlyList<StyleMaterialModeDescriptor> CreateMaterialModes() =>
    [
        new StyleMaterialModeDescriptor(
            ModeId: FaceSdfModeId,
            DisplayName: LocalizedText.From("Face SDF (EndfieldRgbCmM)", zhCn: "面部 SDF（EndfieldRgbCmM）", en: "Face SDF (EndfieldRgbCmM)", ja: "顔 SDF（EndfieldRgbCmM）"),
            AllowedRoles: [StyleMaterialRole.Face],
            Parameters:
            [
                new("transition", LocalizedText.From("Transition", zhCn: "过渡宽度", en: "Transition", ja: "遷移幅"), StyleParameterKind.Float,
                    DefaultNumber: 1.0, MinNumber: 0.001, MaxNumber: 100.0, Step: 0.01),
                new("offset", LocalizedText.From("Offset", zhCn: "阴影偏移", en: "Offset", ja: "オフセット"), StyleParameterKind.Float,
                    DefaultNumber: 1.0, MinNumber: -2.0, MaxNumber: 2.0, Step: 0.01),
                new("encoding", LocalizedText.From("Encoding", zhCn: "编码格式", en: "Encoding", ja: "エンコーディング"), StyleParameterKind.Enum,
                    DefaultString: "EndfieldRgbCmM", EnumOptions: [new("EndfieldRgbCmM", LocalizedText.From("EndfieldRgbCmM"))])
            ],
            TextureInputs:
            [
                new("primary", LocalizedText.From("Face SDF RGB Map", zhCn: "面部 SDF RGB 贴图", en: "Face SDF RGB Map", ja: "顔 SDF RGB マップ"),
                    Required: true, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new("secondary", LocalizedText.From("Combined cm_M Map", zhCn: "组合 cm_M 贴图", en: "Combined cm_M Map", ja: "複合 cm_M マップ"),
                    Required: true, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip)
            ],
            SupportsBoneTracking: true,
            BoneAxisDefaults: new StyleBoneAxisDefaults(
                LocalForward: DefaultHeadLocalForward,
                LocalUp: DefaultHeadLocalUp,
                WorldForward: new Vector3(0f, 0f, -1f),
                WorldRight: new Vector3(1f, 0f, 0f),
                DefaultUseSelectedBone: true)),
        new StyleMaterialModeDescriptor(
            ModeId: HairModeId,
            DisplayName: LocalizedText.From("Radial Hair", zhCn: "径向发丝高光", en: "Radial Hair", ja: "放射状ヘア"),
            AllowedRoles: [StyleMaterialRole.Hair],
            Parameters:
            [
                new("paletteColor", LocalizedText.From("Palette Color", zhCn: "调色盘基色", en: "Palette Color", ja: "パレットカラー"),
                    StyleParameterKind.Color3, DefaultVector: [0.2f, 0.2f, 0.2f]),
                new("brightness", LocalizedText.From("Brightness", zhCn: "亮度增量", en: "Brightness", ja: "明度"),
                    StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: -10.0, MaxNumber: 10.0, Step: 0.05)
            ],
            TextureInputs:
            [
                new("texture", LocalizedText.From("Green Radial Mask", zhCn: "Green 径向高光遮罩", en: "Green Radial Mask", ja: "Green 放射マスク"),
                    Required: true, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip)
            ]),
        new StyleMaterialModeDescriptor(
            ModeId: RimModeId,
            DisplayName: LocalizedText.From("Fresnel Rim", zhCn: "物理菲涅尔边缘光", en: "Fresnel Rim", ja: "フレネルリム"),
            AllowedRoles: [StyleMaterialRole.Body, StyleMaterialRole.Hair],
            Parameters:
            [
                new("baseColor", LocalizedText.From("Base Color", zhCn: "基准底色", en: "Base Color", ja: "ベースカラー"),
                    StyleParameterKind.Color3, DefaultVector: [0.5f, 0.5f, 0.5f]),
                new("rimColor", LocalizedText.From("Rim Color", zhCn: "边缘光颜色", en: "Rim Color", ja: "リムカラー"),
                    StyleParameterKind.Color3, DefaultVector: [1f, 1f, 1f]),
                new("edgeSize", LocalizedText.From("Edge Size", zhCn: "边缘宽度", en: "Edge Size", ja: "エッジサイズ"),
                    StyleParameterKind.Float, DefaultNumber: 0.2, MinNumber: -16.0, MaxNumber: 16.0, Step: 0.02),
                new("edgeStrength", LocalizedText.From("Edge Strength", zhCn: "边缘光强度", en: "Edge Strength", ja: "エッジ強度"),
                    StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: -32.0, MaxNumber: 32.0, Step: 0.02),
                new("directionRadians", LocalizedText.From("Direction (rad)", zhCn: "方向（弧度）", en: "Direction (rad)", ja: "方向（ラジアン）"),
                    StyleParameterKind.Float, DefaultNumber: 5.5, MinNumber: -6.283185, MaxNumber: 6.283185, Step: 0.05),
                new("rangeStrength", LocalizedText.From("Range Strength", zhCn: "范围基准强度", en: "Range Strength", ja: "レンジ強度"),
                    StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: -32.0, MaxNumber: 32.0, Step: 0.05),
                new("radialStrength", LocalizedText.From("Radial Strength", zhCn: "径向基准强度", en: "Radial Strength", ja: "ラジアル強度"),
                    StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: -32.0, MaxNumber: 32.0, Step: 0.05)
            ],
            TextureInputs: Array.Empty<StyleTextureSlotDescriptor>()),
        new StyleMaterialModeDescriptor(
            ModeId: MatcapModeId,
            DisplayName: LocalizedText.From("MatCap", zhCn: "MikkTSpace MatCap", en: "MatCap", ja: "MikkTSpace MatCap"),
            AllowedRoles: [StyleMaterialRole.Body, StyleMaterialRole.Hair],
            Parameters:
            [
                new("matcapEncoding", LocalizedText.From("MatCap Encoding", zhCn: "MatCap 色彩编码", en: "MatCap Encoding", ja: "MatCap エンコーディング"),
                    StyleParameterKind.Enum, DefaultString: "LinearRgb",
                    EnumOptions: [new("LinearRgb", LocalizedText.From("Linear RGB")), new("SrgbRgb", LocalizedText.From("sRGB")), new("Srgb", LocalizedText.From("sRGB (Legacy)"))]),
                new("uvChannel", LocalizedText.From("UV Channel", zhCn: "UV 通道", en: "UV Channel", ja: "UV チャンネル"),
                    StyleParameterKind.Int, DefaultNumber: 0, MinNumber: 0, MaxNumber: 4, Step: 1),
                new("baseColor", LocalizedText.From("Base Color", zhCn: "基准底色", en: "Base Color", ja: "ベースカラー"),
                    StyleParameterKind.Color3, DefaultVector: [0.5f, 0.5f, 0.5f]),
                new("highlightColor", LocalizedText.From("Highlight Color", zhCn: "高光叠加色", en: "Highlight Color", ja: "ハイライトカラー"),
                    StyleParameterKind.Color3, DefaultVector: [0f, 0f, 0f]),
                new("normalStrength", LocalizedText.From("Normal Strength", zhCn: "法线强度", en: "Normal Strength", ja: "法線強度"),
                    StyleParameterKind.Float, DefaultNumber: 2.0, MinNumber: -32.0, MaxNumber: 32.0, Step: 0.05),
                new("rotationRadians", LocalizedText.From("Rotation (rad)", zhCn: "旋转（弧度）", en: "Rotation (rad)", ja: "回転（ラジアン）"),
                    StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: -6.283185, MaxNumber: 6.283185, Step: 0.05),
                new("highlightCoefficient", LocalizedText.From("Highlight Coefficient", zhCn: "高光门控系数", en: "Highlight Coefficient", ja: "ハイライト係数"),
                    StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: -32.0, MaxNumber: 32.0, Step: 0.01),
                new("shadowHardness", LocalizedText.From("Shadow Hardness", zhCn: "阴影硬度", en: "Shadow Hardness", ja: "シャドウ硬さ"),
                    StyleParameterKind.Float, DefaultNumber: 0.5, MinNumber: -16.0, MaxNumber: 16.0, Step: 0.01),
                new("lightControlDirection", LocalizedText.From("Light Control Direction", zhCn: "光控方向", en: "Light Control Direction", ja: "ライト制御方向"),
                    StyleParameterKind.Vector3, DefaultVector: [0f, 0f, 1f]),
                new("baselineStrength", LocalizedText.From("Baseline Strength", zhCn: "基准总强度", en: "Baseline Strength", ja: "ベースライン強度"),
                    StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: -32.0, MaxNumber: 32.0, Step: 0.05),
                new("coupleEndfieldPalette", LocalizedText.From("Couple Endfield Palette", zhCn: "耦合终末地调色盘", en: "Couple Endfield Palette", ja: "Endfield パレット連携"),
                    StyleParameterKind.Bool, DefaultBool: false)
            ],
            TextureInputs:
            [
                new("normal", LocalizedText.From("Packed Normal Map", zhCn: "切线空间法线贴图", en: "Packed Normal Map", ja: "法線マップ"),
                    Required: true, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new("p", LocalizedText.From("P Gate Map", zhCn: "P 门控贴图", en: "P Gate Map", ja: "P ゲートマップ"),
                    Required: true, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new("matcap", LocalizedText.From("MatCap Color Map", zhCn: "MatCap 颜色贴图", en: "MatCap Color Map", ja: "MatCap カラーマップ"),
                    Required: true, Encoding: StyleTextureEncoding.SrgbColor, MipRule: StyleTextureMipRule.PointNoMip)
            ]),
        new StyleMaterialModeDescriptor(
            ModeId: PaletteModeId,
            DisplayName: LocalizedText.From("Downstream Palette", zhCn: "下游调色盘", en: "Downstream Palette", ja: "パレット"),
            AllowedRoles: [StyleMaterialRole.Body, StyleMaterialRole.Hair],
            Parameters:
            [
                new("paletteReceivedLightLinearRgb", LocalizedText.From("Received Light (Linear RGB)", zhCn: "受光色（线性 RGB）", en: "Received Light (Linear RGB)", ja: "受光色（リニア RGB）"),
                    StyleParameterKind.Color3, DefaultVector: [0f, 0f, 0f]),
                new("paletteEmissionStrength", LocalizedText.From("Emission Strength", zhCn: "自发光强度", en: "Emission Strength", ja: "エミッション強度"),
                    StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: -32.0, MaxNumber: 32.0, Step: 0.05),
                new("paletteUniformEmissionBlue", LocalizedText.From("Uniform Emission Blue", zhCn: "统一自发光 Blue", en: "Uniform Emission Blue", ja: "均一エミッション Blue"),
                    StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: -16.0, MaxNumber: 16.0, Step: 0.01),
                new("paletteLightInfluence", LocalizedText.From("Light Influence", zhCn: "受光影响度", en: "Light Influence", ja: "ライト影響度"),
                    StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: -32.0, MaxNumber: 32.0, Step: 0.01),
                new("paletteDensity", LocalizedText.From("Wave Density", zhCn: "波纹密度", en: "Wave Density", ja: "波紋密度"),
                    StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: -32.0, MaxNumber: 32.0, Step: 0.05),
                new("paletteEmissionInputMode", LocalizedText.From("Emission Input Mode", zhCn: "自发光输入模式", en: "Emission Input Mode", ja: "エミッション入力モード"),
                    StyleParameterKind.Enum, DefaultString: "Uniform",
                    EnumOptions: [new("Uniform", LocalizedText.From("Uniform")), new("Texture", LocalizedText.From("Texture"))]),
                new("paletteReceivedLightInputMode", LocalizedText.From("Received Light Input Mode", zhCn: "受光输入模式", en: "Received Light Input Mode", ja: "受光入力モード"),
                    StyleParameterKind.Enum, DefaultString: "Uniform",
                    EnumOptions: [new("Uniform", LocalizedText.From("Uniform")), new("Texture", LocalizedText.From("Texture")), new("RendererDiffuse", LocalizedText.From("RendererDiffuse"))])
            ],
            TextureInputs:
            [
                new("paletteEmission", LocalizedText.From("Emission Blue Map", zhCn: "自发光 Blue 贴图", en: "Emission Blue Map", ja: "エミッション Blue マップ"),
                    Required: false, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new("paletteReceivedLight", LocalizedText.From("Received Light Map", zhCn: "受光贴图", en: "Received Light Map", ja: "受光マップ"),
                    Required: false, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip)
            ]),
        new StyleMaterialModeDescriptor(
            ModeId: BaseImageModeId,
            DisplayName: LocalizedText.From("Base Image & Shadow LUT", zhCn: "基色贴图与阴影 LUT", en: "Base Image & Shadow LUT", ja: "ベース画像とシャドウ LUT"),
            AllowedRoles: [StyleMaterialRole.Body, StyleMaterialRole.Hair],
            Parameters:
            [
                new("baseImageLinearRgb", LocalizedText.From("Base Color (Linear RGB)", zhCn: "基色（线性 RGB）", en: "Base Color (Linear RGB)", ja: "ベースカラー（リニア RGB）"),
                    StyleParameterKind.Color3, DefaultVector: [1f, 1f, 1f]),
                new("baseModelColor", LocalizedText.From("Model Color", zhCn: "模型色", en: "Model Color", ja: "モデルカラー"),
                    StyleParameterKind.Color3, DefaultVector: [1f, 1f, 1f]),
                new("baseShadowColor", LocalizedText.From("Shadow Color", zhCn: "阴影色", en: "Shadow Color", ja: "シャドウカラー"),
                    StyleParameterKind.Color3, DefaultVector: [1f, 1f, 1f]),
                new("baseColorMultiplier", LocalizedText.From("Color Multiplier", zhCn: "颜色乘数", en: "Color Multiplier", ja: "カラー乗数"),
                    StyleParameterKind.Color3, DefaultVector: [1f, 1f, 1f]),
                new("baseShadowFactor", LocalizedText.From("Shadow Factor", zhCn: "阴影因子", en: "Shadow Factor", ja: "シャドウ係数"),
                    StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: -32.0, MaxNumber: 32.0, Step: 0.05),
                new("baseWarmTint", LocalizedText.From("Warm Tint", zhCn: "暖色调混合", en: "Warm Tint", ja: "ウォームティント"),
                    StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: -32.0, MaxNumber: 32.0, Step: 0.01),
                new("baseContrast", LocalizedText.From("Contrast", zhCn: "对比度", en: "Contrast", ja: "コントラスト"),
                    StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: -4.0, MaxNumber: 4.0, Step: 0.05),
                new("baseSaturation", LocalizedText.From("Saturation", zhCn: "饱和度", en: "Saturation", ja: "彩度"),
                    StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: -16.0, MaxNumber: 16.0, Step: 0.05),
                new("baseDetailBlue", LocalizedText.From("Detail Blue", zhCn: "细节 Blue", en: "Detail Blue", ja: "ディテール Blue"),
                    StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: -16.0, MaxNumber: 16.0, Step: 0.05),
                new("baseShadowStrength", LocalizedText.From("Shadow Strength", zhCn: "阴影强度", en: "Shadow Strength", ja: "シャドウ強度"),
                    StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: -32.0, MaxNumber: 32.0, Step: 0.05),
                new("baseInputMode", LocalizedText.From("Base Input Mode", zhCn: "基色输入模式", en: "Base Input Mode", ja: "ベース入力モード"),
                    StyleParameterKind.Enum, DefaultString: "Uniform", EnumOptions: [new("Uniform", LocalizedText.From("Uniform")), new("Texture", LocalizedText.From("Texture"))]),
                new("baseEncoding", LocalizedText.From("Base Encoding", zhCn: "基色编码", en: "Base Encoding", ja: "ベースエンコーディング"),
                    StyleParameterKind.Enum, DefaultString: "LinearRgb", EnumOptions: [new("LinearRgb", LocalizedText.From("Linear RGB")), new("SrgbRgb", LocalizedText.From("sRGB")), new("Srgb", LocalizedText.From("sRGB (Legacy)"))]),
                new("detailInputMode", LocalizedText.From("Detail Input Mode", zhCn: "细节输入模式", en: "Detail Input Mode", ja: "ディテール入力モード"),
                    StyleParameterKind.Enum, DefaultString: "Uniform", EnumOptions: [new("Uniform", LocalizedText.From("Uniform")), new("Texture", LocalizedText.From("Texture"))]),
                new("shadowInputMode", LocalizedText.From("Shadow Input Mode", zhCn: "阴影输入模式", en: "Shadow Input Mode", ja: "シャドウ入力モード"),
                    StyleParameterKind.Enum, DefaultString: "Uniform", EnumOptions: [new("Uniform", LocalizedText.From("Uniform")), new("Texture", LocalizedText.From("Texture"))]),
                new("hasFacingBase", LocalizedText.From("Enable Facing Base", zhCn: "启用视角基色乘法", en: "Enable Facing Base", ja: "Facing Base 有効化"),
                    StyleParameterKind.Bool, DefaultBool: false),
                new("facingBaseBlend", LocalizedText.From("Facing Base Blend", zhCn: "视角基色混合", en: "Facing Base Blend", ja: "Facing Base ブレンド"),
                    StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: 0.0, MaxNumber: 1.0, Step: 0.01),
                new("hasBaseDirectionalShadow", LocalizedText.From("Enable Directional Shadow", zhCn: "启用方向阴影", en: "Enable Directional Shadow", ja: "ディレクショナルシャドウ有効化"),
                    StyleParameterKind.Bool, DefaultBool: false),
                new("baseDirectionalShadowWorldDirection", LocalizedText.From("Directional Shadow Direction", zhCn: "方向阴影方向", en: "Directional Shadow Direction", ja: "ディレクショナルシャドウ方向"),
                    StyleParameterKind.Vector3, DefaultVector: [0f, 0f, 1f]),
                new("baseDirectionalShadowWidth", LocalizedText.From("Directional Shadow Width", zhCn: "方向阴影宽度", en: "Directional Shadow Width", ja: "ディレクショナルシャドウ幅"),
                    StyleParameterKind.Float, DefaultNumber: 0.3, MinNumber: -4.0, MaxNumber: 4.0, Step: 0.02),
                new("baseDirectionalShadowNormalStrength", LocalizedText.From("Directional Shadow Normal Strength", zhCn: "方向阴影法线强度", en: "Directional Shadow Normal Strength", ja: "ディレクショナルシャドウ法線強度"),
                    StyleParameterKind.Float, DefaultNumber: 2.0, MinNumber: 0.0, MaxNumber: 16.0, Step: 0.05)
            ],
            TextureInputs:
            [
                new("baseImage", LocalizedText.From("Base Color Map", zhCn: "基色贴图", en: "Base Color Map", ja: "ベースカラーマップ"),
                    Required: false, Encoding: StyleTextureEncoding.SrgbColor, MipRule: StyleTextureMipRule.PointNoMip),
                new("baseImageDetail", LocalizedText.From("Detail Blue Map", zhCn: "细节贴图", en: "Detail Blue Map", ja: "ディテールマップ"),
                    Required: false, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new("baseImageShadow", LocalizedText.From("Shadow Red Map", zhCn: "阴影贴图", en: "Shadow Red Map", ja: "シャドウマップ"),
                    Required: false, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip)
            ]),
        new StyleMaterialModeDescriptor(
            ModeId: HighlightRangeModeId,
            DisplayName: LocalizedText.From("Highlight Range", zhCn: "高光范围", en: "Highlight Range", ja: "ハイライト範囲"),
            AllowedRoles: [StyleMaterialRole.Body, StyleMaterialRole.Hair],
            Parameters:
            [
                new("highlightRangeOverallBrightness", LocalizedText.From("Overall Brightness", zhCn: "整体基准亮度", en: "Overall Brightness", ja: "全体明度"),
                    StyleParameterKind.Float, DefaultNumber: 0.22, MinNumber: 0.0, MaxNumber: 4.0, Step: 0.02),
                new("highlightRangeHighlightBrightness", LocalizedText.From("Highlight Brightness", zhCn: "高光区亮度", en: "Highlight Brightness", ja: "ハイライト明度"),
                    StyleParameterKind.Float, DefaultNumber: 0.12, MinNumber: 0.0, MaxNumber: 4.0, Step: 0.02),
                new("highlightRangeDirectionalShadowWorldDirection", LocalizedText.From("Directional Shadow Direction", zhCn: "方向阴影方向", en: "Directional Shadow Direction", ja: "ディレクショナルシャドウ方向"),
                    StyleParameterKind.Vector3, DefaultVector: [0f, 0f, 1f]),
                new("highlightRangeDirectionalShadowWidth", LocalizedText.From("Directional Shadow Width", zhCn: "方向阴影宽度", en: "Directional Shadow Width", ja: "ディレクショナルシャドウ幅"),
                    StyleParameterKind.Float, DefaultNumber: 0.3, MinNumber: -4.0, MaxNumber: 4.0, Step: 0.02),
                new("highlightRangeDirectionalShadowNormalStrength", LocalizedText.From("Directional Shadow Normal Strength", zhCn: "方向阴影法线强度", en: "Directional Shadow Normal Strength", ja: "ディレクショナルシャドウ法線強度"),
                    StyleParameterKind.Float, DefaultNumber: 2.0, MinNumber: 0.0, MaxNumber: 16.0, Step: 0.05)
            ],
            TextureInputs: Array.Empty<StyleTextureSlotDescriptor>())
    ];
}
