using System.Collections.ObjectModel;
using System.Numerics;
using Mmd.Modern.Plugins.Contracts;

namespace Mda.CustomStylePlugin;

/// <summary>
/// Reference third-party rendering style ("custom-ink-cel") built exclusively on <c>Mmd.Modern.Plugins.Contracts</c>.
/// Demonstrates procedural inline LUT textures, tunable style-wide parameters, per-material custom modes,
/// constant buffer packing with <see cref="StyleConstantBufferBuilder"/>, and HLSL shading hooks.
/// </summary>
public sealed class CustomInkCelStyleDefinition : RenderingStyleDefinitionBase
{
    public const string CanonicalStyleId = "custom-ink-cel";

    private const string DefaultModuleId = "default";
    private const string AccentModeId = "ink-rim-accent";

    private const string InkEdgeStrengthKey = "InkEdgeStrength";
    private const string InkWarmTintKey = "InkWarmTint";
    private const string RimHighlightIntensityKey = "RimHighlightIntensity";
    private const string AccentBoostKey = "AccentBoost";

    private const float DefaultInkEdgeStrength = 0.38f;
    private static readonly Vector3 DefaultInkWarmTint = new(0.96f, 0.90f, 0.82f);
    private const float DefaultRimHighlightIntensity = 0.24f;
    private const float DefaultAccentBoost = 1.25f;

    private static readonly LocalizedText StyleDisplayName = LocalizedText.From(
        "Ink Cel (Example)", zhCn: "水墨赛璐璐（示例）", en: "Ink Cel (Example)", ja: "水墨セル（例）");

    private static readonly LocalizedText StyleDescription = LocalizedText.From(
        "Two-band ink-cel shading with procedural LUT ramp, grazing ink contour darkening, and optional accent rim.",
        zhCn: "二阶水墨赛璐璐着色示例：内置程序化 LUT、掠射水墨轮廓压暗与可选边缘高光。",
        en: "Two-band ink-cel shading with procedural LUT ramp, grazing ink contour darkening, and optional accent rim.",
        ja: "プロシージャル LUT、かすめ角度の墨輪郭減衰、アクセントリムを備えた二階調水墨セルシェーディング例。");

    private static readonly StyleOutlineDefaults OutlineDefaults = new(Width: 0.22f, Color: null, Enabled: true);
    private static readonly StyleToonResponseDefaults ToonDefaults = new(Threshold: 0.52f, TransitionWidth: 0.06f);
    private static readonly StyleFaceEyeDefaults FaceEyeDefaults = new(
        FaceNormalShadeStrength: 0.40f,
        FaceGeometricShadowStrength: 0.85f,
        EyeGeometricShadowStrength: 0.20f);

    private static readonly StyleStylizedShadingParameters StylizedDefaults = new(
        ShadowStrength: 0.85f,
        ShadowValue: 0.70f,
        ShadowHue: 0.58f,
        ShadowTint: new Vector3(0.84f, 0.87f, 0.96f),
        FaceFlatten: 0.30f,
        WarmColor: new Vector3(1.0f, 0.93f, 0.84f),
        WarmStrength: 0.32f,
        RimEdge: 0.62f,
        RimStrength: 0.14f,
        Saturation: 1.04f,
        Contrast: 0.10f,
        Brightness: 1.02f,
        OutlineValue: 0.32f,
        OutlineSaturation: 1.10f);

    private static readonly IReadOnlyDictionary<StyleMaterialRole, StyleMaterialTreatmentParameters> RoleTreatments =
        new ReadOnlyDictionary<StyleMaterialRole, StyleMaterialTreatmentParameters>(
            new Dictionary<StyleMaterialRole, StyleMaterialTreatmentParameters>
            {
                [StyleMaterialRole.Body] = new(Saturation: 0.94f, TransitionWidthScale: 0.95f, ValueContrast: 0.18f, ShadeGamma: 1.15f),
                [StyleMaterialRole.Hair] = new(Saturation: 0.96f, TransitionWidthScale: 0.90f, ValueContrast: 0.16f, ShadeGamma: 1.10f, HairHighlightStrength: 0.14f, HairHighlightWidth: 0.22f),
                [StyleMaterialRole.Face] = new(),
                [StyleMaterialRole.Eye] = new(),
            });

    // Procedural 256x4 LUT texture shared across draws via an immutable StyleInlineTexture asset key.
    private static readonly StyleInlineTexture InkRampLutTexture = CreateInkRampLut();

    private static readonly StyleTextureBinding[] SharedTextureBindings =
    [
        new(
            SlotIndex: 0,
            InlineTexture: InkRampLutTexture,
            Encoding: StyleTextureEncoding.Linear,
            MipRule: StyleTextureMipRule.PointNoMip)
    ];

    private static readonly IReadOnlyList<StyleParameterDescriptor> GlobalStyleParameters =
    [
        new(
            Key: InkEdgeStrengthKey,
            DisplayName: LocalizedText.From("Ink Edge Strength", zhCn: "水墨轮廓强度", en: "Ink Edge Strength", ja: "墨輪郭強度"),
            Kind: StyleParameterKind.Float,
            Description: LocalizedText.From(
                "Darkens grazing silhouettes toward warm sumi ink.",
                zhCn: "控制模型边缘掠射角处向浓墨过渡压暗的强度。",
                en: "Darkens grazing silhouettes toward warm sumi ink.",
                ja: "視線とのかすめ角付近を墨色へ落とす強さを調整します。"),
            DefaultNumber: DefaultInkEdgeStrength,
            MinNumber: 0.0,
            MaxNumber: 1.0,
            Step: 0.02),
        new(
            Key: InkWarmTintKey,
            DisplayName: LocalizedText.From("Paper Warm Tint", zhCn: "宣纸暖调", en: "Paper Warm Tint", ja: "和紙暖色ティント"),
            Kind: StyleParameterKind.Color3,
            Description: LocalizedText.From(
                "Subtle warm washi paper tint blended into lit surfaces.",
                zhCn: "混合到受光面上的柔和宣纸暖色调。",
                en: "Subtle warm washi paper tint blended into lit surfaces.",
                ja: "明部へ控えめに重畳する和紙風の暖色ティント。"),
            DefaultVector: [DefaultInkWarmTint.X, DefaultInkWarmTint.Y, DefaultInkWarmTint.Z]),
        new(
            Key: RimHighlightIntensityKey,
            DisplayName: LocalizedText.From("Rim Highlight Intensity", zhCn: "边缘高光强度", en: "Rim Highlight Intensity", ja: "リムハイライト強度"),
            Kind: StyleParameterKind.Float,
            Description: LocalizedText.From(
                "Global crisp cel rim intensity amplified on materials using Ink Rim Accent.",
                zhCn: "全局清透边缘高光强度；启用“水墨边缘高光”模式的材质会在此基础上继续增益。",
                en: "Global crisp cel rim intensity amplified on materials using Ink Rim Accent.",
                ja: "全体のリムハイライト強度。Ink Rim Accent モードのマテリアルでさらに増幅されます。"),
            DefaultNumber: DefaultRimHighlightIntensity,
            MinNumber: 0.0,
            MaxNumber: 1.5,
            Step: 0.02),
    ];

    private static readonly IReadOnlyList<StyleMaterialModeDescriptor> Modes =
    [
        new(
            ModeId: AccentModeId,
            DisplayName: LocalizedText.From("Ink Rim Accent", zhCn: "水墨边缘高光", en: "Ink Rim Accent", ja: "墨リム強調"),
            AllowedRoles: [StyleMaterialRole.Body, StyleMaterialRole.Hair, StyleMaterialRole.Face, StyleMaterialRole.Eye],
            Parameters:
            [
                new(
                    Key: AccentBoostKey,
                    DisplayName: LocalizedText.From("Accent Boost", zhCn: "高光倍率", en: "Accent Boost", ja: "強調ブースト"),
                    Kind: StyleParameterKind.Float,
                    Description: LocalizedText.From(
                        "Additional multiplier for the accent rim and ink edge crispness on this material.",
                        zhCn: "当前材质的边缘高光与水墨轮廓增益倍率。",
                        en: "Additional multiplier for the accent rim and ink edge crispness on this material.",
                        ja: "対象マテリアルのリムハイライトと墨エッジの追加倍率。"),
                    DefaultNumber: DefaultAccentBoost,
                    MinNumber: 0.0,
                    MaxNumber: 3.0,
                    Step: 0.05)
            ],
            TextureInputs: Array.Empty<StyleTextureSlotDescriptor>(),
            Description: LocalizedText.From(
                "Adds an accented cel rim highlight and crisper grazing ink edge to selected materials.",
                zhCn: "为指定材质追加赛璐璐边缘强调高光并强化掠射墨边。",
                en: "Adds an accented cel rim highlight and crisper grazing ink edge to selected materials.",
                ja: "指定マテリアルに強調セルリムライトと鋭い墨輪郭を追加します。"))
    ];

    // Custom HLSL implementing MDA's documented style hooks:
    // - MdaStyleLightResponse: sharp 2-band cel light ramp driven by the 256x4 LUT (t0, space2)
    // - MdaStylePrimarySurfacePost: warm paper wash tinting on lit regions
    // - MdaStyleFinalEntryPost: grazing sumi-ink contour darkening + optional accent rim highlight
    private const string InkCelShaderSource = """
        cbuffer PluginStyleConstants : register(b0, space2) {
            float4 InkEdgeAndRimParams;
            float4 InkPaperTintParams;
        };

        Texture2D<float4> InkRampLut : register(t0, space2);

        float2 ClampLutUv(float u, int rowIndex) {
            float clampedU = clamp(u, 0.5 / 256.0, 255.5 / 256.0);
            float centerV = ((float)rowIndex + 0.5) / 4.0;
            return float2(clampedU, centerV);
        }

        float3 SafeNormalizeOrFallback(float3 value, float3 fallback) {
            float lenSq = dot(value, value);
            return lenSq > 1e-8 ? value * rsqrt(lenSq) : fallback;
        }

        float MdaStyleLightResponse(SurfaceOutput v, float3 n, float3 l, float visibility) {
            float halfLambert = saturate(dot(n, l) * 0.5 + 0.5);
            float threshold = ShadowOptions.w;
            float transition = max(IncomingLight.w, 0.015);

            // Remap half-Lambert around the host ToonThreshold so LUT Row 0 centered at 0.5
            // produces a crisp 2-band cel transition with slight sumi bleed.
            float rampCoord = saturate(0.5 + (halfLambert - threshold) / max(transition * 2.0, 0.04));
            float4 lutRow0 = InkRampLut.SampleLevel(MdaLinearClampSampler, ClampLutUv(rampCoord, 0), 0);
            float band = lerp(lutRow0.g * 0.18, 1.0, lutRow0.r);

            if (EyePosition.w < 1.0)
                band = lerp(1.0, band, EyePosition.w);
            if (GroundDirection.w < 1.0)
                visibility = lerp(1.0, visibility, GroundDirection.w);

            return band * visibility;
        }

        float4 MdaStylePrimarySurfacePost(SurfaceOutput v, float4 color) {
            float3 n = SafeNormalizeOrFallback(v.Normal, float3(0, 1, 0));
            float luma = dot(color.rgb, float3(0.2126, 0.7152, 0.0722));
            float4 lutRow1 = InkRampLut.SampleLevel(MdaLinearClampSampler, ClampLutUv(saturate(luma), 1), 0);

            // Apply warm paper wash gently to mid-to-lit regions while leaving deep shadows crisp.
            float washWeight = saturate(lutRow1.r * (0.35 + 0.65 * abs(n.y)));
            float3 tinted = color.rgb * lerp(float3(1.0, 1.0, 1.0), InkPaperTintParams.rgb, washWeight * 0.55);
            return float4(max(tinted, 0.0), color.a);
        }

        float4 MdaStyleFinalEntryPost(SurfaceOutput v, uint mdaPrimitive, bool mdaFront, float4 color) {
            float3 worldN = SafeNormalizeOrFallback(v.Normal, float3(0, 1, 0));
            if (!mdaFront)
                worldN = -worldN;

            float3 viewDir = SafeNormalizeOrFallback(EyePosition.xyz - v.WorldPosition, float3(0, 0, -1));
            float vdotn = saturate(dot(worldN, viewDir));
            float grazing = 1.0 - vdotn;

            float4 lutRow2 = InkRampLut.SampleLevel(MdaLinearClampSampler, ClampLutUv(grazing, 2), 0);
            float4 lutRow3 = InkRampLut.SampleLevel(MdaLinearClampSampler, ClampLutUv(grazing, 3), 0);

            // Subtle deterministic ink-paper grain modulated per triangle along silhouettes.
            float stipple = 1.0 + ((float)(mdaPrimitive & 15u) / 15.0 - 0.5) * 0.08 * lutRow2.g;
            float inkMask = saturate(lutRow2.r * InkEdgeAndRimParams.x * stipple);
            float3 sumiInkColor = float3(0.085, 0.075, 0.105);
            float3 result = lerp(color.rgb, sumiInkColor, inkMask * 0.85);

            float rimStrength = InkEdgeAndRimParams.y;
        #ifdef INK_RIM_ACCENT
            rimStrength *= (1.0 + InkEdgeAndRimParams.z);
        #endif
            float rimMask = lutRow3.r * max(rimStrength, 0.0);
            float3 rimTint = lerp(InkPaperTintParams.rgb, float3(0.86, 0.94, 1.0), 0.35);
            result += rimTint * rimMask * (0.28 + 0.72 * color.rgb);

            return float4(max(result, 0.0), color.a);
        }
        """;

    private static readonly IReadOnlyList<StyleShaderModule> ShaderModules =
    [
        new(
            ModuleId: DefaultModuleId,
            HlslSource: InkCelShaderSource,
            Hooks: StyleShaderHookMask.LightResponse
                | StyleShaderHookMask.PrimarySurfacePost
                | StyleShaderHookMask.FinalEntryPost
                | StyleShaderHookMask.AllowStylizedShadingWrapper
                | StyleShaderHookMask.HasStyleShaderCode
                | StyleShaderHookMask.RequiresSkinShadeBlend,
            GeometryRequirements: StyleGeometryRequirements.FrontFace | StyleGeometryRequirements.PrimitiveId),
        new(
            ModuleId: AccentModeId,
            HlslSource: InkCelShaderSource,
            Hooks: StyleShaderHookMask.LightResponse
                | StyleShaderHookMask.PrimarySurfacePost
                | StyleShaderHookMask.FinalEntryPost
                | StyleShaderHookMask.AllowStylizedShadingWrapper
                | StyleShaderHookMask.HasStyleShaderCode
                | StyleShaderHookMask.RequiresSkinShadeBlend,
            GeometryRequirements: StyleGeometryRequirements.FrontFace | StyleGeometryRequirements.PrimitiveId,
            Defines: new ReadOnlyDictionary<string, string>(new Dictionary<string, string>
            {
                ["INK_RIM_ACCENT"] = "1"
            }),
            CacheKeySuffix: "accent")
    ];

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
    public override IReadOnlyList<StyleParameterDescriptor> StyleParameters => GlobalStyleParameters;
    public override IReadOnlyList<StyleMaterialModeDescriptor> MaterialModes => Modes;

    public override IReadOnlyList<StyleShaderModule> GetShaderModules() => ShaderModules;

    public override StyleValidationResult ValidateMaterial(StyleMaterialValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var active = context.Bindings
            .Where(b => b is not null && string.Equals(b.StyleId, CanonicalStyleId, StringComparison.Ordinal))
            .ToArray();
        if (active.Length == 0)
            return StyleValidationResult.Ok;

        if (active.Length > 1)
        {
            return StyleValidationResult.Error(LocalizedText.From(
                "Only one Ink Cel material binding can be active per material.",
                zhCn: "单个材质只能启用一个水墨赛璐璐附加模式。",
                en: "Only one Ink Cel material binding can be active per material.",
                ja: "1つのマテリアルで有効にできる水墨セルモードは1つまでです。"));
        }

        var binding = active[0];
        if (!string.Equals(binding.ModeId, AccentModeId, StringComparison.Ordinal))
        {
            return StyleValidationResult.Error(LocalizedText.From(
                $"Unsupported material mode '{binding.ModeId}'.",
                zhCn: $"不支持的材质模式“{binding.ModeId}”。",
                en: $"Unsupported material mode '{binding.ModeId}'.",
                ja: $"未対応のマテリアルモード '{binding.ModeId}' です。"));
        }

        if (binding.NumberValues is not null
            && binding.NumberValues.TryGetValue(AccentBoostKey, out double boost)
            && (!double.IsFinite(boost) || boost < 0.0 || boost > 3.0))
        {
            return StyleValidationResult.Error(LocalizedText.From(
                "Accent Boost must be a finite value between 0.0 and 3.0.",
                zhCn: "高光倍率必须为 0.0 到 3.0 之间的有限数值。",
                en: "Accent Boost must be a finite value between 0.0 and 3.0.",
                ja: "強調ブーストは 0.0 ～ 3.0 の有限値である必要があります。"));
        }

        return StyleValidationResult.Ok;
    }

    public override StyleDrawData BuildMaterialDrawData(StyleDrawContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var accentBinding = context.Bindings.FirstOrDefault(
            b => b is not null
                && string.Equals(b.StyleId, CanonicalStyleId, StringComparison.Ordinal)
                && string.Equals(b.ModeId, AccentModeId, StringComparison.Ordinal));

        float inkEdgeStrength = ClampFinite(
            ReadNumber(context.StyleGlobalParameters, InkEdgeStrengthKey, DefaultInkEdgeStrength), 0f, 1f);
        float rimHighlightIntensity = ClampFinite(
            ReadNumber(context.StyleGlobalParameters, RimHighlightIntensityKey, DefaultRimHighlightIntensity), 0f, 1.5f);
        Vector3 inkWarmTint = ReadVector3(context.StyleGlobalParameters, InkWarmTintKey, DefaultInkWarmTint);
        float accentBoost = accentBinding is not null
            ? ClampFinite(ReadNumber(accentBinding, AccentBoostKey, DefaultAccentBoost), 0f, 3f)
            : 0f;

        var cbuffer = new StyleConstantBufferBuilder()
            .Write(new Vector4(
                inkEdgeStrength,
                rimHighlightIntensity,
                accentBoost,
                accentBinding is not null ? 1f : 0f))
            .Write(new Vector4(inkWarmTint, 1f))
            .PadTo16()
            .ToArray();

        return new StyleDrawData(
            ShaderModuleId: accentBinding is not null ? AccentModeId : DefaultModuleId,
            ConstantBufferData: cbuffer,
            TextureBindings: SharedTextureBindings);
    }

    private static StyleInlineTexture CreateInkRampLut()
    {
        const int width = 256;
        const int height = 4;
        var pixels = new byte[width * height * 4];

        for (int x = 0; x < width; x++)
        {
            float u = x / (float)(width - 1);

            // Row 0: crisp 2-band cel step with a narrow ink-paper bleed around u = 0.5.
            float step = SmoothStep(0.44f, 0.56f, u);
            float coolShadow = 1f - SmoothStep(0.15f, 0.52f, u);
            WriteRgba(pixels, x, row: 0, width, step, coolShadow, 1f - MathF.Abs(u * 2f - 1f), 1f);

            // Row 1: warm paper highlight envelope favoring midtones and sunlit surfaces.
            float warmWash = SmoothStep(0.30f, 0.88f, u);
            float satKeep = 1f - 0.15f * SmoothStep(0.75f, 1f, u);
            WriteRgba(pixels, x, row: 1, width, warmWash, satKeep, 0f, 1f);

            // Row 2: grazing silhouette ink contour curve (u = 1 - NdotV).
            float grazingInk = MathF.Pow(SmoothStep(0.55f, 0.94f, u), 1.35f);
            float stippleGate = SmoothStep(0.50f, 0.85f, u);
            WriteRgba(pixels, x, row: 2, width, grazingInk, stippleGate, grazingInk * 0.5f, 1f);

            // Row 3: accent cel rim highlight curve (tight, sharp edge gleam).
            float rimStep = SmoothStep(0.68f, 0.88f, u);
            float rimEnvelope = MathF.Pow(SmoothStep(0.58f, 0.98f, u), 1.2f);
            WriteRgba(pixels, x, row: 3, width, rimStep, rimEnvelope, 1f, 1f);
        }

        return new StyleInlineTexture(
            AssetKey: "custom-ink-cel:lut:256x4",
            Width: width,
            Height: height,
            Format: StyleTexturePixelFormat.Rgba8Unorm,
            Pixels: pixels,
            MipRule: StyleTextureMipRule.PointNoMip);
    }

    private static void WriteRgba(byte[] pixels, int x, int row, int width, float r, float g, float b, float a)
    {
        int offset = (row * width + x) * 4;
        pixels[offset] = ToByte(r);
        pixels[offset + 1] = ToByte(g);
        pixels[offset + 2] = ToByte(b);
        pixels[offset + 3] = ToByte(a);
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float ReadNumber(StyleMaterialBindingData? binding, string key, float fallback)
        => binding?.NumberValues is not null && binding.NumberValues.TryGetValue(key, out double value) ? (float)value : fallback;

    private static Vector3 ReadVector3(StyleMaterialBindingData? binding, string key, Vector3 fallback)
    {
        if (binding?.VectorValues is null || !binding.VectorValues.TryGetValue(key, out float[]? vec) || vec.Length < 3)
            return fallback;
        float x = float.IsFinite(vec[0]) ? Math.Clamp(vec[0], 0f, 2f) : fallback.X;
        float y = float.IsFinite(vec[1]) ? Math.Clamp(vec[1], 0f, 2f) : fallback.Y;
        float z = float.IsFinite(vec[2]) ? Math.Clamp(vec[2], 0f, 2f) : fallback.Z;
        return new Vector3(x, y, z);
    }

    private static float ClampFinite(float value, float min, float max)
        => float.IsFinite(value) ? Math.Clamp(value, min, max) : min;
}
