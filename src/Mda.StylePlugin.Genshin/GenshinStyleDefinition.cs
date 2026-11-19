using System.Collections.ObjectModel;
using System.Numerics;
using Mmd.Modern.Plugins.Contracts;

namespace Mda.StylePlugin.Genshin;

/// <summary>Genshin Impact rendering style definition matching baseline numerical parity.</summary>
public sealed class GenshinStyleDefinition : RenderingStyleDefinitionBase
{
    public const string CanonicalStyleId = "genshin";

    public const string ModeDefault = "default";
    public const string ModeFaceSdf = "face-sdf";
    public const string ModeHair = "hair";
    public const string LegacyModeHairAlias = "genshin-hair";
    public const string ModeHairAtlas = "hair-atlas";
    public const string ModeMetallic = "metallic";

    public static GenshinStyleDefinition Instance { get; } = new();

    private static readonly LocalizedText DisplayNameText = LocalizedText.From(
        "原神",
        zhCn: "原神",
        en: "Genshin Impact",
        ja: "原神");

    private static readonly LocalizedText DescriptionText = LocalizedText.From(
        "《原神》风格渲染：普通 PMX 材质采用三渲二色阶与发丝高光处理；另支持软面部 FaceSDF、非金属 RB 发丝高光、Hair Shadow Atlas 与金属 MatCap/高光组合绑定。",
        zhCn: "《原神》风格渲染：普通 PMX 材质采用三渲二色阶与发丝高光处理；另支持软面部 FaceSDF、非金属 RB 发丝高光、Hair Shadow Atlas 与金属 MatCap/高光组合绑定。",
        en: "Genshin Impact style rendering: ordinary PMX materials use cel shading and hair treatment, plus optional composable FaceSDF, non-metallic RB hair specular, Hair Shadow Atlas, and metallic MatCap/specular bindings.",
        ja: "原神スタイル描画：通常PMXマテリアルはトゥーン調陰影と髪ハイライト処理を使用し、合成可能なFaceSDF、非金属RB髪スペキュラ、Hair Shadow Atlas、金属MatCap・スペキュラに対応します。");

    private static readonly StyleOutlineDefaults DefaultOutlineValue = new();
    private static readonly StyleToonResponseDefaults DefaultToonValue = new();
    private static readonly StyleFaceEyeDefaults DefaultFaceEyeValue = new();

    private static readonly StyleStylizedShadingParameters DefaultStylizedShadingValue = new(
        ShadowStrength: 1f,
        ShadowValue: 0.76f,
        ShadowHue: 0.5f,
        ShadowTint: new Vector3(1f, 0.9f, 0.84f),
        FaceFlatten: 0f,
        WarmColor: new Vector3(1f, 0.86f, 0.72f),
        WarmStrength: 0.15f,
        RimEdge: 0.62f,
        RimStrength: 0.2f,
        Saturation: 1.08f,
        Contrast: 0.04f,
        Brightness: 1.06f,
        OutlineValue: 0.5f,
        OutlineSaturation: 1.3f);

    private static readonly IReadOnlyDictionary<StyleMaterialRole, StyleMaterialTreatmentParameters> DefaultRoleTreatmentsValue =
        new Dictionary<StyleMaterialRole, StyleMaterialTreatmentParameters>
        {
            [StyleMaterialRole.Body] = new(
                Saturation: 1.04f,
                TransitionWidthScale: 1.1f,
                ValueContrast: -0.25f,
                ShadeGamma: 0.6f),
            [StyleMaterialRole.Hair] = new(
                Saturation: 1.08f,
                TransitionWidthScale: 0.9f,
                ValueContrast: -0.2f,
                ShadeGamma: 0.65f,
                HairHighlightStrength: 0.26f,
                HairHighlightWidth: 0.22f),
            [StyleMaterialRole.Face] = new(),
            [StyleMaterialRole.Eye] = new(),
        }.AsReadOnly();

    private static readonly StyleBoneAxisDefaults FaceBoneDefaults = new(
        LocalForward: -Vector3.UnitZ,
        LocalUp: Vector3.UnitY,
        WorldForward: -Vector3.UnitZ,
        WorldRight: Vector3.UnitX,
        DefaultUseSelectedBone: true);

    private static readonly IReadOnlyList<StyleMaterialModeDescriptor> ModeDescriptors = BuildModeDescriptors();
    private static readonly IReadOnlyList<StyleParameterDescriptor> StyleGlobalParameterDescriptors = BuildStyleParameters();
    private static readonly IReadOnlyList<StyleShaderModule> ShaderModules = GenshinShaders.CreateAllModules();

    private GenshinStyleDefinition()
    {
    }

    public override string StyleId => CanonicalStyleId;
    public override LocalizedText DisplayName => DisplayNameText;
    public override LocalizedText? Description => DescriptionText;
    public override string? Author => "MDA";
    public override Version Version { get; } = new(1, 0, 0);
    public override bool SupportsMaterialRoleGuessing => true;
    public override StyleOutlineDefaults DefaultOutline => DefaultOutlineValue;
    public override StyleToonResponseDefaults DefaultToonResponse => DefaultToonValue;
    public override StyleStylizedShadingParameters DefaultStylizedShading => DefaultStylizedShadingValue;
    public override StyleFaceEyeDefaults DefaultFaceEye => DefaultFaceEyeValue;
    public override IReadOnlyDictionary<StyleMaterialRole, StyleMaterialTreatmentParameters> DefaultRoleTreatments => DefaultRoleTreatmentsValue;
    public override IReadOnlyList<StyleMaterialModeDescriptor> MaterialModes => ModeDescriptors;
    public override IReadOnlyList<StyleParameterDescriptor> StyleParameters => StyleGlobalParameterDescriptors;

    public override IReadOnlyList<StyleShaderModule> GetShaderModules() => ShaderModules;

    public override StyleMaterialClassification ClassifyMaterial(StyleMaterialInspectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ExplicitRole is { } explicitRole)
            return new StyleMaterialClassification(explicitRole);

        if (context.Bindings is { Count: > 0 })
        {
            StyleMaterialBindingData? face = null;
            StyleMaterialBindingData? hair = null;
            StyleMaterialBindingData? atlas = null;
            StyleMaterialBindingData? metallic = null;

            foreach (var binding in context.Bindings)
            {
                if (!string.Equals(binding.StyleId, CanonicalStyleId, StringComparison.Ordinal))
                    continue;
                string mode = NormalizeModeId(binding.ModeId);
                if (mode == ModeFaceSdf) face = binding;
                else if (mode == ModeHair) hair = binding;
                else if (mode == ModeHairAtlas) atlas = binding;
                else if (mode == ModeMetallic) metallic = binding;
            }

            if (face is not null)
                return new StyleMaterialClassification(StyleMaterialRole.Face);
            if (hair is not null)
                return new StyleMaterialClassification(StyleMaterialRole.Hair);
            if (atlas is not null || metallic is not null)
            {
                if (context.GuessMaterialRoles && !string.IsNullOrWhiteSpace(context.MaterialName))
                    return new StyleMaterialClassification(GuessMaterialRole(context.MaterialName));
                return new StyleMaterialClassification(atlas is not null && metallic is null
                    ? StyleMaterialRole.Hair
                    : StyleMaterialRole.Body);
            }
        }

        return new StyleMaterialClassification(ResolveMaterialRole(context));
    }

    public override StyleValidationResult ValidateMaterial(StyleMaterialValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Bindings is null || context.Bindings.Count == 0)
            return StyleValidationResult.Ok;

        var seenModes = new HashSet<string>(StringComparer.Ordinal);
        StyleMaterialBindingData? defaultBinding = null;
        StyleMaterialBindingData? face = null;
        StyleMaterialBindingData? hair = null;
        StyleMaterialBindingData? atlas = null;
        StyleMaterialBindingData? metallic = null;

        foreach (var binding in context.Bindings)
        {
            if (binding is null)
                return ErrorText("材质绑定列表包含空条目。", "Material bindings cannot contain null.", "マテリアルバインディングに null が含まれています。");
            if (!string.Equals(binding.StyleId, CanonicalStyleId, StringComparison.Ordinal))
                return ErrorText($"绑定 StyleId '{binding.StyleId}' 不属于原神风格。", $"Binding StyleId '{binding.StyleId}' does not match '{CanonicalStyleId}'.", $"バインディングの StyleId '{binding.StyleId}' が '{CanonicalStyleId}' と一致しません。");

            string mode = NormalizeModeId(binding.ModeId);
            if (!seenModes.Add(mode))
                return ErrorText($"同一材质不能重复绑定模式 '{mode}'。", $"Duplicate binding mode '{mode}' on a single material.", $"同じマテリアルにモード '{mode}' を重複して設定できません。");

            switch (mode)
            {
                case ModeDefault:
                    defaultBinding = binding;
                    break;
                case ModeFaceSdf:
                    face = binding;
                    break;
                case ModeHair:
                    hair = binding;
                    break;
                case ModeHairAtlas:
                    atlas = binding;
                    break;
                case ModeMetallic:
                    metallic = binding;
                    break;
                default:
                    return ErrorText($"原神风格不支持材质模式 '{binding.ModeId}'。", $"Unknown Genshin material mode '{binding.ModeId}'.", $"未対応の原神マテリアルモード '{binding.ModeId}' です。");
            }
        }

        bool hasDedicated = face is not null || hair is not null || atlas is not null || metallic is not null;
        if (defaultBinding is not null && hasDedicated)
            return ErrorText("普通默认模式不能与专用材质模式同时绑定。", "Cannot combine default mode with dedicated material modes.", "デフォルトモードと専用マテリアルモードは併用できません。");

        if (face is not null && (hair is not null || atlas is not null || metallic is not null))
            return ErrorText("FaceSDF 不能与头发高光、Shadow Atlas 或金属材质同时绑定。", "FaceSDF cannot share a material with Hair RB, Hair Shadow Atlas, or Metallic bindings.", "FaceSDF は髪RB・Hair Shadow Atlas・金属バインディングと併用できません。");

        if (face is not null)
            return ValidateFaceSdf(context, face);

        StyleValidationResult result;
        if (hair is not null)
        {
            result = ValidateHair(context, hair);
            if (!result.IsValid) return result;
        }
        if (atlas is not null)
        {
            result = ValidateHairAtlas(context, atlas);
            if (!result.IsValid) return result;
        }
        if (metallic is not null)
        {
            result = ValidateMetallic(context, metallic);
            if (!result.IsValid) return result;
        }

        if (metallic is not null)
        {
            int metallicLightmap = GetRequiredSlot(metallic, "lightmap");
            if (hair is not null && GetRequiredSlot(hair, "texture") != metallicLightmap)
                return ErrorText("共用的原神金属与非金属 RB 高光必须使用相同的 UV0 Lightmap 贴图。", "Co-bound Genshin metallic and R/B specular must share the same UV0 lightmap texture.", "併用の原神金属と非金属RBハイライトは同じ UV0 Lightmap を参照する必要があります。");
            if (atlas is not null && GetRequiredSlot(atlas, "lightmap") != metallicLightmap)
                return ErrorText("共用的原神金属与 Hair Shadow Atlas 必须使用相同的 UV0 Lightmap 贴图。", "Co-bound Genshin metallic and Hair Shadow Atlas must share the same UV0 lightmap texture.", "併用の原神金属と Hair Shadow Atlas は同じ UV0 Lightmap を参照する必要があります。");
        }

        return StyleValidationResult.Ok;
    }

    public override IReadOnlyList<StyleSceneTextureRequest> GetSceneTextureRequests(StyleMaterialBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Bindings is null || context.Bindings.Count == 0)
            return Array.Empty<StyleSceneTextureRequest>();

        var seen = new HashSet<int>();
        var list = new List<StyleSceneTextureRequest>(6);

        void Add(int index, StyleTextureEncoding encoding, bool isOptional = false)
        {
            if (index >= 0 && seen.Add(index))
                list.Add(new StyleSceneTextureRequest(index, encoding, StyleTextureMipRule.PointNoMip, IsOptional: isOptional));
        }

        foreach (var binding in context.Bindings)
        {
            if (!string.Equals(binding.StyleId, CanonicalStyleId, StringComparison.Ordinal))
                continue;
            switch (NormalizeModeId(binding.ModeId))
            {
                case ModeFaceSdf:
                    Add(GetRequiredSlot(binding, "primary"), StyleTextureEncoding.Linear);
                    Add(GetRequiredSlot(binding, "secondary"), StyleTextureEncoding.Linear);
                    break;
                case ModeHair:
                    Add(GetRequiredSlot(binding, "texture"), StyleTextureEncoding.Linear);
                    break;
                case ModeHairAtlas:
                    Add(GetRequiredSlot(binding, "lightmap"), StyleTextureEncoding.Linear);
                    Add(GetRequiredSlot(binding, "atlas"), StyleTextureEncoding.Linear);
                    break;
                case ModeMetallic:
                    Add(GetRequiredSlot(binding, "lightmap"), StyleTextureEncoding.Linear, isOptional: true);
                    var (matcapSrgb, _) = ParseMatcapEncoding(GetString(binding, "matcapEncoding", "LinearRgb"));
                    Add(GetRequiredSlot(binding, "matcap"), matcapSrgb ? StyleTextureEncoding.SrgbColor : StyleTextureEncoding.Linear, isOptional: true);
                    if (GetBool(binding, "hasSpecular", false) && GetFloat(binding, "specularRampMix", 0f) > 0f)
                        Add(GetOptionalSlot(binding, "specularRamp"), StyleTextureEncoding.Linear, isOptional: true);
                    break;
            }
        }

        return list.Count == 0 ? Array.Empty<StyleSceneTextureRequest>() : Array.AsReadOnly(list.ToArray());
    }

    public override StyleBoneTransformRequest? GetBoneTransformRequest(StyleMaterialBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ResolvedRole != StyleMaterialRole.Face || context.Bindings is null)
            return null;

        foreach (var binding in context.Bindings)
        {
            if (!string.Equals(binding.StyleId, CanonicalStyleId, StringComparison.Ordinal)
                || NormalizeModeId(binding.ModeId) != ModeFaceSdf)
                continue;

            if (binding.BoneBinding is { Mode: StyleBoneTrackingMode.SelectedBone } bone
                && !string.IsNullOrWhiteSpace(bone.BoneName))
            {
                return new StyleBoneTransformRequest(
                    bone.BoneName.Trim(),
                    bone.LocalForward ?? FaceBoneDefaults.LocalForward,
                    bone.LocalUp ?? FaceBoneDefaults.LocalUp);
            }
        }

        return null;
    }

    public override StyleDrawData? BuildMaterialDrawData(StyleDrawContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        StyleMaterialBindingData? face = null;
        StyleMaterialBindingData? hair = null;
        StyleMaterialBindingData? atlas = null;
        StyleMaterialBindingData? metallic = null;

        if (context.Bindings is { Count: > 0 })
        {
            foreach (var binding in context.Bindings)
            {
                if (!string.Equals(binding.StyleId, CanonicalStyleId, StringComparison.Ordinal))
                    continue;
                switch (NormalizeModeId(binding.ModeId))
                {
                    case ModeFaceSdf: face = binding; break;
                    case ModeHair: hair = binding; break;
                    case ModeHairAtlas: atlas = binding; break;
                    case ModeMetallic: metallic = binding; break;
                }
            }
        }

        bool faceActive = face is not null
            && context.ResolvedRole == StyleMaterialRole.Face
            && (context.FaceEyeDefaults?.FaceNormalShadeStrength ?? 1f) != 0f;

        if (faceActive)
            return BuildFaceSdfDrawData(context, face!);

        bool hasRb = hair is not null && context.ResolvedRole == StyleMaterialRole.Hair;
        bool hasAtlas = atlas is not null && context.ResolvedRole is StyleMaterialRole.Body or StyleMaterialRole.Hair;
        bool hasMetallic = metallic is not null
            && context.ResolvedRole is StyleMaterialRole.Body or StyleMaterialRole.Hair
            && GetFloat(metallic, "enableMetallics", 1f) != 0f;

        if (hasRb || hasAtlas || hasMetallic)
            return BuildDedicatedBodyHairDrawData(context, hasRb ? hair : null, hasAtlas ? atlas : null, hasMetallic ? metallic : null);

        var stylizedOverride = ResolveGlobalStylizedOverride(context);
        bool hasDedicatedBinding = face is not null || hair is not null || atlas is not null || metallic is not null;
        if (stylizedOverride is null && !hasDedicatedBinding)
            return null;

        return new StyleDrawData(
            ShaderModuleId: GenshinShaders.DefaultModuleId,
            StylizedShadingOverride: stylizedOverride);
    }

    private static StyleDrawData BuildFaceSdfDrawData(StyleDrawContext context, StyleMaterialBindingData face)
    {
        var (forward, right) = ResolveFaceBasis(context, face.BoneBinding);
        float transition = GetFloat(face, "transition", 0.001f);
        float offset = GetFloat(face, "offset", 0.55f);

        var cb = new StyleConstantBufferBuilder();
        cb.Write(new Vector4(forward, 0f));
        cb.Write(new Vector4(right, 0f));
        cb.Write(new Vector4(transition, offset, 0f, 0f));

        int primary = ResolveComposedSlot(context, GetRequiredSlot(face, "primary"));
        int secondary = ResolveComposedSlot(context, GetRequiredSlot(face, "secondary"));

        return new StyleDrawData(
            ShaderModuleId: GenshinShaders.FaceSdfModuleId,
            ConstantBufferData: cb.ToArray(),
            TextureBindings:
            [
                new StyleTextureBinding(0, primary, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new StyleTextureBinding(1, secondary, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
            ]);
    }

    private static StyleDrawData BuildDedicatedBodyHairDrawData(
        StyleDrawContext context,
        StyleMaterialBindingData? hair,
        StyleMaterialBindingData? atlas,
        StyleMaterialBindingData? metallic)
    {
        bool hasRb = hair is not null;
        bool hasAtlas = atlas is not null;
        bool hasMetallic = metallic is not null;
        bool hasMetallicSpec = metallic is not null && GetBool(metallic, "hasSpecular", false);

        var cb = new StyleConstantBufferBuilder();
        var textures = new List<StyleTextureBinding>(6);

        if (hair is not null)
        {
            cb.Write(new Vector4(
                GetFloat(hair, "globalSpecularScale", 1f),
                GetFloat(hair, "shininess", 10f),
                GetFloat(hair, "multiplier", 0.1f),
                GetFloat(hair, "intensity", 1f)));
            textures.Add(new StyleTextureBinding(
                0,
                ResolveComposedSlot(context, GetRequiredSlot(hair, "texture")),
                Encoding: StyleTextureEncoding.Linear,
                MipRule: StyleTextureMipRule.PointNoMip));
        }

        if (atlas is not null)
        {
            float atlasSrgb = ParseAtlasEncodingIsSrgb(GetString(atlas, "atlasEncoding", "LinearRgb")) ? 1f : 0f;
            cb.Write(new Vector4(
                GetFloat(atlas, "explicitRowId", 1f),
                GetFloat(atlas, "dayNight", 0f),
                GetFloat(atlas, "shadowPosition", 0.7f),
                GetFloat(atlas, "shadowRampWidth", 0.18f)));
            cb.Write(new Vector4(
                GetFloat(atlas, "vertexWidth", 1f),
                GetFloat(atlas, "vertexAo", 1f),
                GetFloat(atlas, "useLightmapAo", 0f),
                GetFloat(atlas, "useVertexColorAo", 0f)));
            cb.Write(new Vector4(atlasSrgb, 0f, 0f, 0f));

            textures.Add(new StyleTextureBinding(
                1,
                ResolveComposedSlot(context, GetRequiredSlot(atlas, "lightmap")),
                Encoding: StyleTextureEncoding.Linear,
                MipRule: StyleTextureMipRule.PointNoMip));
            textures.Add(new StyleTextureBinding(
                2,
                ResolveComposedSlot(context, GetRequiredSlot(atlas, "atlas")),
                Encoding: StyleTextureEncoding.Linear,
                MipRule: StyleTextureMipRule.PointNoMip));
        }

        if (metallic is not null)
        {
            var (isSrgb, swizzleBgr) = ParseMatcapEncoding(GetString(metallic, "matcapEncoding", "LinearRgb"));
            float litMode = string.Equals(GetString(metallic, "litMode", "PrimaryResponse"), "Uniform", StringComparison.Ordinal) ? 1f : 0f;
            Vector3 darkColor = GetVector3(metallic, "darkColor", new Vector3(0.7f, 0.7f, 0.7f));
            Vector3 lightColor = GetVector3(metallic, "lightColor", Vector3.One);
            Vector3 shadowMul = GetVector3(metallic, "shadowMultiplyColor", new Vector3(0.85f, 0.85f, 0.85f));

            cb.Write(new Vector4(
                GetFloat(metallic, "enableMetallics", 1f),
                GetFloat(metallic, "tileScale", 1f),
                GetFloat(metallic, "brightness", 1f),
                litMode));
            cb.Write(new Vector4(darkColor, GetFloat(metallic, "uniformLitFactor", 1f)));
            cb.Write(new Vector4(lightColor, swizzleBgr ? 1f : 0f));
            cb.Write(new Vector4(shadowMul, 0f));

            textures.Add(new StyleTextureBinding(
                3,
                ResolveComposedSlot(context, GetRequiredSlot(metallic, "lightmap")),
                Encoding: StyleTextureEncoding.Linear,
                MipRule: StyleTextureMipRule.PointNoMip));
            textures.Add(new StyleTextureBinding(
                4,
                ResolveComposedSlot(context, GetRequiredSlot(metallic, "matcap")),
                Encoding: isSrgb ? StyleTextureEncoding.SrgbColor : StyleTextureEncoding.Linear,
                MipRule: StyleTextureMipRule.PointNoMip));

            if (hasMetallicSpec)
            {
                Vector3 specColor = GetVector3(metallic, "specularColor", Vector3.One);
                Vector3 sharpColor = GetVector3(metallic, "specularSharpColor", Vector3.One);
                float shininess = GetFloat(metallic, "specularShininess", 16f);
                float scale = GetFloat(metallic, "specularScale", 1f);
                float attenuation = GetFloat(metallic, "specularAttenuation", 0.5f);
                float sharpOffset = GetFloat(metallic, "specularSharpOffset", 1f);
                float rampMix = GetFloat(metallic, "specularRampMix", 0f);
                int rampLocal = GetOptionalSlot(metallic, "specularRamp");
                bool hasRamp = rampMix > 0f && rampLocal >= 0;

                cb.Write(new Vector4(specColor, shininess));
                cb.Write(new Vector4(sharpColor, scale));
                cb.Write(new Vector4(attenuation, sharpOffset, rampMix, hasRamp ? 1f : 0f));

                if (hasRamp)
                {
                    textures.Add(new StyleTextureBinding(
                        5,
                        ResolveComposedSlot(context, rampLocal),
                        Encoding: StyleTextureEncoding.Linear,
                        MipRule: StyleTextureMipRule.PointNoMip));
                }
            }
        }

        string moduleId = GenshinShaders.ResolveDedicatedModuleId(hasRb, hasAtlas, hasMetallic, hasMetallicSpec);
        return new StyleDrawData(
            ShaderModuleId: moduleId,
            ConstantBufferData: cb.ToArray(),
            TextureBindings: Array.AsReadOnly(textures.ToArray()));
    }

    private StyleStylizedShadingParameters? ResolveGlobalStylizedOverride(StyleDrawContext context)
    {
        var global = context.StyleGlobalParameters;
        if (global is null)
            return null;

        bool hasNums = global.NumberValues is { Count: > 0 };
        bool hasVecs = global.VectorValues is { Count: > 0 };
        if (!hasNums && !hasVecs)
            return null;

        var b = context.StylizedShading ?? ResolveDefaultShadingForRole(context.ResolvedRole);
        float shadowStrength = b.ShadowStrength;
        float shadowValue = b.ShadowValue;
        float shadowHue = b.ShadowHue;
        Vector3 shadowTint = b.ShadowTint;
        float faceFlatten = b.FaceFlatten;
        Vector3 warmColor = b.WarmColor;
        float warmStrength = b.WarmStrength;
        float rimEdge = b.RimEdge;
        float rimStrength = b.RimStrength;
        float saturation = b.Saturation;
        float contrast = b.Contrast;
        float brightness = b.Brightness;
        float outlineValue = b.OutlineValue;
        float outlineSaturation = b.OutlineSaturation;

        bool changed = false;
        changed |= TryApplyClamped(global, "shadowStrength", 0f, 1f, ref shadowStrength);
        changed |= TryApplyClamped(global, "shadowValue", 0f, 1f, ref shadowValue);
        changed |= TryApplyClamped(global, "shadowHue", 0f, 1f, ref shadowHue);
        changed |= TryApplyClamped(global, "faceFlatten", 0f, 1f, ref faceFlatten);
        changed |= TryApplyClamped(global, "warmStrength", 0f, 1f, ref warmStrength);
        changed |= TryApplyClamped(global, "rimEdge", 0f, 1f, ref rimEdge);
        changed |= TryApplyClamped(global, "rimStrength", 0f, 1f, ref rimStrength);
        changed |= TryApplyClamped(global, "saturation", 0f, 2f, ref saturation);
        changed |= TryApplyClamped(global, "contrast", -0.5f, 0.5f, ref contrast);
        changed |= TryApplyClamped(global, "brightness", 0.5f, 2f, ref brightness);
        changed |= TryApplyClamped(global, "outlineValue", 0.05f, 1f, ref outlineValue);
        changed |= TryApplyClamped(global, "outlineSaturation", 0f, 2f, ref outlineSaturation);
        changed |= TryApplyColor01(global, "shadowTint", ref shadowTint);
        changed |= TryApplyColor01(global, "warmColor", ref warmColor);

        if (!changed)
            return null;

        return new StyleStylizedShadingParameters(
            shadowStrength, shadowValue, shadowHue, shadowTint, faceFlatten,
            warmColor, warmStrength, rimEdge, rimStrength, saturation, contrast, brightness,
            outlineValue, outlineSaturation);
    }

    private StyleStylizedShadingParameters ResolveDefaultShadingForRole(StyleMaterialRole role) => role switch
    {
        StyleMaterialRole.Face => DefaultStylizedShadingValue with { FaceFlatten = 0.85f, RimStrength = 0f, WarmStrength = DefaultStylizedShadingValue.WarmStrength * 0.5f },
        StyleMaterialRole.Eye => DefaultStylizedShadingValue with { ShadowStrength = 0f, RimStrength = 0f, WarmStrength = 0f },
        _ => DefaultStylizedShadingValue,
    };

    private static (Vector3 Forward, Vector3 Right) ResolveFaceBasis(StyleDrawContext context, StyleBoneAxisBinding? boneBinding)
    {
        Vector3 rawForward;
        Vector3 rawRight;
        if (context.EvaluatedBone is { } evaluated && boneBinding?.Mode == StyleBoneTrackingMode.SelectedBone)
        {
            rawForward = evaluated.WorldForward;
            rawRight = evaluated.WorldRight;
        }
        else
        {
            rawForward = boneBinding?.FixedWorldForward ?? FaceBoneDefaults.WorldForward;
            rawRight = boneBinding?.FixedWorldRight ?? FaceBoneDefaults.WorldRight;
        }
        return OrthonormalizeBasis(rawForward, rawRight);
    }

    private static StyleValidationResult ValidateFaceSdf(StyleMaterialValidationContext context, StyleMaterialBindingData face)
    {
        if (context.ResolvedRole != StyleMaterialRole.Face)
            return ErrorText("Genshin FaceSDF 必须用于 Face 角色材质。", "Genshin FaceSDF requires an explicitly selected Face material.", "原神の FaceSDF は Face ロールのマテリアルが必要です。");

        var slotResult = ValidateTextureSlot(context, face, "primary", required: true);
        if (!slotResult.IsValid) return slotResult;
        slotResult = ValidateTextureSlot(context, face, "secondary", required: true);
        if (!slotResult.IsValid) return slotResult;

        string encoding = GetString(face, "encoding", "GenshinSoftFace");
        if (!string.Equals(encoding, "GenshinSoftFace", StringComparison.Ordinal))
            return ErrorText($"Genshin FaceSDF 仅支持 'GenshinSoftFace' 编码（收到 '{encoding}'）。", $"Genshin FaceSDF encoding must be 'GenshinSoftFace' (got '{encoding}').", $"原神の FaceSDF エンコーディングは 'GenshinSoftFace' のみ対応しています（'{encoding}'）。");

        var numResult = ValidateNumberRange(face, "transition", 0.0, 100.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(face, "offset", -2.0, 2.0);
        if (!numResult.IsValid) return numResult;

        return ValidateBoneAxisBinding(face.BoneBinding);
    }

    private static StyleValidationResult ValidateHair(StyleMaterialValidationContext context, StyleMaterialBindingData hair)
    {
        if (context.ResolvedRole != StyleMaterialRole.Hair)
            return ErrorText("Genshin 非金属 RB 高光必须用于 Hair 角色材质。", "Genshin non-metallic RB specular requires a Hair material role.", "原神の非金属 RB スペキュラは Hair ロールのマテリアルが必要です。");

        var slotResult = ValidateTextureSlot(context, hair, "texture", required: true);
        if (!slotResult.IsValid) return slotResult;

        var numResult = ValidateNumberRange(hair, "globalSpecularScale", 0.0, 2.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(hair, "shininess", 0.0, 64.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(hair, "multiplier", 0.0, 16.0);
        if (!numResult.IsValid) return numResult;
        return ValidateNumberRange(hair, "intensity", 0.0, 16.0);
    }

    private static StyleValidationResult ValidateHairAtlas(StyleMaterialValidationContext context, StyleMaterialBindingData atlas)
    {
        if (context.ResolvedRole is not (StyleMaterialRole.Body or StyleMaterialRole.Hair))
            return ErrorText("Genshin Hair Shadow Atlas 仅允许用于 Body 或 Hair 材质（Face 与 Eye 受保护）。", "Genshin Hair Shadow Atlas requires Body or Hair role; Face and Eye are protected.", "原神の Hair Shadow Atlas は Body または Hair のみ使用できます（Face と Eye は保護されています）。");

        var slotResult = ValidateTextureSlot(context, atlas, "lightmap", required: true);
        if (!slotResult.IsValid) return slotResult;
        slotResult = ValidateTextureSlot(context, atlas, "atlas", required: true);
        if (!slotResult.IsValid) return slotResult;

        string encoding = GetString(atlas, "atlasEncoding", "LinearRgb");
        if (encoding is not ("LinearRgb" or "Srgb" or "SrgbRgb"))
            return ErrorText($"不支持的 Atlas 颜色编码 '{encoding}'。", $"Unsupported Genshin atlas color encoding '{encoding}'.", $"未対応の Atlas カラーエンコーディング '{encoding}' です。");

        var numResult = ValidateNumberRange(atlas, "explicitRowId", 1.0, 5.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(atlas, "dayNight", 0.0, 1.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(atlas, "useLightmapAo", 0.0, 1.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(atlas, "useVertexColorAo", 0.0, 1.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(atlas, "shadowPosition", -16.0, 16.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(atlas, "shadowRampWidth", -16.0, 16.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(atlas, "vertexWidth", -16.0, 16.0);
        if (!numResult.IsValid) return numResult;
        return ValidateNumberRange(atlas, "vertexAo", -16.0, 16.0);
    }

    private static StyleValidationResult ValidateMetallic(StyleMaterialValidationContext context, StyleMaterialBindingData metallic)
    {
        if (context.ResolvedRole is not (StyleMaterialRole.Body or StyleMaterialRole.Hair))
            return ErrorText("Genshin 金属 MatCap 仅允许用于 Body 或 Hair 材质（Face 与 Eye 受保护）。", "Genshin metallic requires Body or Hair role; Face and Eye are protected.", "原神の金属 MatCap は Body または Hair のみ使用できます（Face と Eye は保護されています）。");

        var slotResult = ValidateTextureSlot(context, metallic, "lightmap", required: true);
        if (!slotResult.IsValid) return slotResult;
        slotResult = ValidateTextureSlot(context, metallic, "matcap", required: true);
        if (!slotResult.IsValid) return slotResult;

        string encoding = GetString(metallic, "matcapEncoding", "LinearRgb");
        if (encoding is not ("LinearRgb" or "Srgb" or "SrgbRgb" or "LinearBgr" or "SrgbBgr"))
            return ErrorText($"不支持的 MatCap 颜色编码 '{encoding}'。", $"Unsupported MatCap color encoding '{encoding}'.", $"未対応の MatCap カラーエンコーディング '{encoding}' です。");

        string litMode = GetString(metallic, "litMode", "PrimaryResponse");
        if (litMode is not ("PrimaryResponse" or "Uniform"))
            return ErrorText($"不支持的金属 LitMode '{litMode}'。", $"Unsupported metallic litMode '{litMode}'.", $"未対応の金属 LitMode '{litMode}' です。");

        foreach (string scalarKey in new[] { "enableMetallics", "tileScale", "brightness", "uniformLitFactor" })
        {
            var r = ValidateNumberRange(metallic, scalarKey, -32.0, 32.0);
            if (!r.IsValid) return r;
        }

        foreach (string colorKey in new[] { "darkColor", "lightColor", "shadowMultiplyColor" })
        {
            var r = ValidateVector3Range(metallic, colorKey, -16f, 16f);
            if (!r.IsValid) return r;
        }

        bool hasSpecular = GetBool(metallic, "hasSpecular", false);
        if (hasSpecular)
        {
            var r = ValidateVector3Range(metallic, "specularColor", -16f, 16f);
            if (!r.IsValid) return r;
            r = ValidateVector3Range(metallic, "specularSharpColor", -16f, 16f);
            if (!r.IsValid) return r;
            r = ValidateNumberRange(metallic, "specularShininess", 0.0, 256.0);
            if (!r.IsValid) return r;
            r = ValidateNumberRange(metallic, "specularScale", 0.0, 32.0);
            if (!r.IsValid) return r;
            r = ValidateNumberRange(metallic, "specularAttenuation", 0.0, 32.0);
            if (!r.IsValid) return r;
            r = ValidateNumberRange(metallic, "specularSharpOffset", -32.0, 32.0);
            if (!r.IsValid) return r;
            r = ValidateNumberRange(metallic, "specularRampMix", -32.0, 32.0);
            if (!r.IsValid) return r;

            double rampMix = GetDouble(metallic, "specularRampMix", 0.0);
            bool rampRequired = rampMix > 0.0;
            r = ValidateTextureSlot(context, metallic, "specularRamp", required: rampRequired);
            if (!r.IsValid) return r;
        }
        else if (GetOptionalSlot(metallic, "specularRamp") >= 0)
        {
            var r = ValidateTextureSlot(context, metallic, "specularRamp", required: false);
            if (!r.IsValid) return r;
        }

        return StyleValidationResult.Ok;
    }

    private static string NormalizeModeId(string modeId)
        => string.Equals(modeId, LegacyModeHairAlias, StringComparison.Ordinal) ? ModeHair : modeId;

    private static bool ParseAtlasEncodingIsSrgb(string encoding)
        => encoding is "Srgb" or "SrgbRgb";

    private static (bool IsSrgb, bool SwizzleBgr) ParseMatcapEncoding(string encoding) => encoding switch
    {
        "Srgb" or "SrgbRgb" => (true, false),
        "LinearBgr" => (false, true),
        "SrgbBgr" => (true, true),
        _ => (false, false),
    };

    private static StyleValidationResult ValidateBoneAxisBinding(StyleBoneAxisBinding? boneBinding)
    {
        if (boneBinding is null)
            return StyleValidationResult.Ok;

        if (!Enum.IsDefined(boneBinding.Mode))
            return ErrorText("未知的骨骼追踪模式。", "Unknown bone tracking mode.", "不明なボーン追従モードです。");

        if (boneBinding.Mode == StyleBoneTrackingMode.SelectedBone)
        {
            if (string.IsNullOrWhiteSpace(boneBinding.BoneName))
                return ErrorText("SelectedBone 模式需要有效的骨骼名称或索引。", "SelectedBone mode requires a non-empty bone name.", "SelectedBone モードには有効なボーン名が必要です。");

            var forward = boneBinding.LocalForward ?? FaceBoneDefaults.LocalForward;
            var up = boneBinding.LocalUp ?? FaceBoneDefaults.LocalUp;
            if (!TryCheckBasisVectors(forward, up, out var err))
                return err!;
            return StyleValidationResult.Ok;
        }

        var worldForward = boneBinding.FixedWorldForward ?? FaceBoneDefaults.WorldForward;
        var worldRight = boneBinding.FixedWorldRight ?? FaceBoneDefaults.WorldRight;
        if (!TryCheckBasisVectors(worldForward, worldRight, out var worldErr))
            return worldErr!;
        return StyleValidationResult.Ok;
    }

    private static bool TryCheckBasisVectors(Vector3 a, Vector3 b, out StyleValidationResult? error)
    {
        if (!IsFinite(a) || !IsFinite(b) || !float.IsFinite(a.LengthSquared()) || !float.IsFinite(b.LengthSquared())
            || a.LengthSquared() < 1e-12f || b.LengthSquared() < 1e-12f)
        {
            error = ErrorText("面部 SDF 方向向量必须为有限且非零的向量。", "FaceSDF axis vectors must be finite and non-zero.", "FaceSDF の方向ベクトルは有限かつ非ゼロである必要があります。");
            return false;
        }
        var na = Vector3.Normalize(a);
        var nb = Vector3.Normalize(b);
        nb -= na * Vector3.Dot(na, nb);
        if (nb.LengthSquared() < 1e-8f)
        {
            error = ErrorText("面部 SDF 前向与右向向量不能平行。", "FaceSDF forward and right axis vectors cannot be parallel.", "FaceSDF の前方ベクトルと右方向ベクトルは平行にできません。");
            return false;
        }
        error = null;
        return true;
    }

    private static (Vector3 Forward, Vector3 Right) OrthonormalizeBasis(Vector3 forward, Vector3 right)
    {
        if (!IsFinite(forward) || forward.LengthSquared() < 1e-12f) forward = FaceBoneDefaults.WorldForward;
        if (!IsFinite(right) || right.LengthSquared() < 1e-12f) right = FaceBoneDefaults.WorldRight;
        var nf = Vector3.Normalize(forward);
        var nr = Vector3.Normalize(right);
        nr -= nf * Vector3.Dot(nf, nr);
        if (nr.LengthSquared() < 1e-8f)
        {
            var fallback = MathF.Abs(nf.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
            nr = Vector3.Normalize(Vector3.Cross(nf, fallback));
        }
        else
        {
            nr = Vector3.Normalize(nr);
        }
        return (nf, nr);
    }

    private static StyleValidationResult ValidateTextureSlot(
        StyleMaterialValidationContext context,
        StyleMaterialBindingData binding,
        string slotKey,
        bool required)
    {
        if (binding.TextureSlots is null || !binding.TextureSlots.TryGetValue(slotKey, out int texIndex))
        {
            return required
                ? ErrorText($"模式 '{binding.ModeId}' 缺少必需的贴图槽位 '{slotKey}'。", $"Mode '{binding.ModeId}' requires texture slot '{slotKey}'.", $"モード '{binding.ModeId}' にはテクスチャスロット '{slotKey}' が必要です。")
                : StyleValidationResult.Ok;
        }

        if (texIndex < 0)
        {
            return required
                ? ErrorText($"贴图槽位 '{slotKey}' 未指定有效贴图（索引 {texIndex}）。", $"Required texture slot '{slotKey}' must reference a valid texture (got {texIndex}).", $"必須テクスチャスロット '{slotKey}' に有効なテクスチャが指定されていません（{texIndex}）。")
                : StyleValidationResult.Ok;
        }

        if (texIndex >= context.SourceTextureCount)
            return ErrorText($"贴图槽位 '{slotKey}' 索引 {texIndex} 超出模型贴图数量范围（{context.SourceTextureCount}）。", $"Texture slot '{slotKey}' index {texIndex} is outside the {context.SourceTextureCount}-texture source layout.", $"テクスチャスロット '{slotKey}' のインデックス {texIndex} が範囲外です（最大 {context.SourceTextureCount}）。");

        if (context.SourceTextureDimensions is { } dims && texIndex < dims.Count)
        {
            var (w, h) = dims[texIndex];
            if (w <= 0 || h <= 0)
                return ErrorText($"贴图槽位 '{slotKey}' 引用了无效尺寸的贴图（{w}x{h}）。", $"Texture slot '{slotKey}' has invalid dimensions {w}x{h}.", $"テクスチャスロット '{slotKey}' のサイズ {w}x{h} が無効です。");
        }

        return StyleValidationResult.Ok;
    }

    private static StyleValidationResult ValidateNumberRange(StyleMaterialBindingData binding, string key, double min, double max)
    {
        if (binding.NumberValues is null || !binding.NumberValues.TryGetValue(key, out double val))
            return StyleValidationResult.Ok;
        if (!double.IsFinite(val) || val < min || val > max)
            return ErrorText($"参数 '{key}' 必须为 [{min}, {max}] 范围内的有限数值（收到 {val}）。", $"Parameter '{key}' must be finite and in [{min}, {max}] (got {val}).", $"パラメータ '{key}' は [{min}, {max}] の有限値である必要があります（値: {val}）。");
        return StyleValidationResult.Ok;
    }

    private static StyleValidationResult ValidateVector3Range(StyleMaterialBindingData binding, string key, float min, float max)
    {
        if (binding.VectorValues is null || !binding.VectorValues.TryGetValue(key, out float[]? vec))
            return StyleValidationResult.Ok;
        if (vec is null || vec.Length < 3)
            return ErrorText($"向量参数 '{key}' 必须包含至少 3 个分量。", $"Vector parameter '{key}' must contain at least 3 components.", $"ベクトルパラメータ '{key}' には 3 成分が必要です。");
        for (int i = 0; i < 3; i++)
        {
            if (!float.IsFinite(vec[i]) || vec[i] < min || vec[i] > max)
                return ErrorText($"向量参数 '{key}[{i}]' 必须在 [{min}, {max}] 范围内（收到 {vec[i]}）。", $"Vector parameter '{key}[{i}]' must be finite and in [{min}, {max}] (got {vec[i]}).", $"ベクトルパラメータ '{key}[{i}]' は [{min}, {max}] の範囲内である必要があります（値: {vec[i]}）。");
        }
        return StyleValidationResult.Ok;
    }

    private static int GetRequiredSlot(StyleMaterialBindingData binding, string key)
        => binding.TextureSlots is not null && binding.TextureSlots.TryGetValue(key, out int slot) ? slot : -1;

    private static int GetOptionalSlot(StyleMaterialBindingData binding, string key)
        => binding.TextureSlots is not null && binding.TextureSlots.TryGetValue(key, out int slot) ? slot : -1;

    private static double GetDouble(StyleMaterialBindingData binding, string key, double fallback)
        => binding.NumberValues is not null && binding.NumberValues.TryGetValue(key, out double val) && double.IsFinite(val) ? val : fallback;

    private static float GetFloat(StyleMaterialBindingData binding, string key, float fallback)
        => binding.NumberValues is not null && binding.NumberValues.TryGetValue(key, out double val) && double.IsFinite(val) ? (float)val : fallback;

    private static bool GetBool(StyleMaterialBindingData binding, string key, bool fallback)
        => binding.BoolValues is not null && binding.BoolValues.TryGetValue(key, out bool val) ? val : fallback;

    private static string GetString(StyleMaterialBindingData binding, string key, string fallback)
        => binding.StringValues is not null && binding.StringValues.TryGetValue(key, out string? val) && val is not null ? val : fallback;

    private static Vector3 GetVector3(StyleMaterialBindingData binding, string key, Vector3 fallback)
        => binding.VectorValues is not null && binding.VectorValues.TryGetValue(key, out float[]? arr) && arr is { Length: >= 3 }
            ? new Vector3(arr[0], arr[1], arr[2])
            : fallback;

    private static int ResolveComposedSlot(StyleDrawContext context, int localTextureIndex)
    {
        if (localTextureIndex < 0)
            return -1;
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

    private static bool TryApplyClamped(StyleMaterialBindingData global, string key, float min, float max, ref float target)
    {
        if (global.NumberValues is null || !global.NumberValues.TryGetValue(key, out double val) || !double.IsFinite(val))
            return false;
        target = Math.Clamp((float)val, min, max);
        return true;
    }

    private static bool TryApplyColor01(StyleMaterialBindingData global, string key, ref Vector3 target)
    {
        if (global.VectorValues is null || !global.VectorValues.TryGetValue(key, out float[]? arr) || arr is not { Length: >= 3 })
            return false;
        if (!float.IsFinite(arr[0]) || !float.IsFinite(arr[1]) || !float.IsFinite(arr[2]))
            return false;
        target = Vector3.Clamp(new Vector3(arr[0], arr[1], arr[2]), Vector3.Zero, Vector3.One);
        return true;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static StyleValidationResult ErrorText(string zhCn, string en, string ja)
        => StyleValidationResult.Error(en, LocalizedText.From(en, zhCn: zhCn, en: en, ja: ja));

    private static IReadOnlyList<StyleMaterialModeDescriptor> BuildModeDescriptors()
    {
        StyleMaterialRole[] bodyOrHair = [StyleMaterialRole.Body, StyleMaterialRole.Hair];
        StyleMaterialRole[] allRoles = [StyleMaterialRole.Body, StyleMaterialRole.Face, StyleMaterialRole.Eye, StyleMaterialRole.Hair];

        var defaultMode = new StyleMaterialModeDescriptor(
            ModeId: ModeDefault,
            DisplayName: LocalizedText.From("标准三渲二", zhCn: "标准三渲二", en: "Standard Cel", ja: "標準トゥーン"),
            AllowedRoles: allRoles,
            Parameters: Array.Empty<StyleParameterDescriptor>(),
            TextureInputs: Array.Empty<StyleTextureSlotDescriptor>(),
            Description: LocalizedText.From(
                "普通 PMX 材质的默认原神三渲二表面着色与角色处理。",
                zhCn: "普通 PMX 材质的默认原神三渲二表面着色与角色处理。",
                en: "Default Genshin Impact cel shading and PMX material treatment for ordinary materials.",
                ja: "通常PMXマテリアル向けの標準原神トゥーンシェーディングとマテリアル処理。"));

        var faceSdfMode = new StyleMaterialModeDescriptor(
            ModeId: ModeFaceSdf,
            DisplayName: LocalizedText.From("面部 SDF（软阴影）", zhCn: "面部 SDF（软阴影）", en: "Face SDF (Soft)", ja: "顔 SDF（ソフト）"),
            AllowedRoles: [StyleMaterialRole.Face],
            Parameters:
            [
                new StyleParameterDescriptor(
                    "transition",
                    LocalizedText.From("过渡柔和度", zhCn: "过渡柔和度", en: "Softness", ja: "ソフトネス"),
                    StyleParameterKind.Float,
                    LocalizedText.From("FaceSDF 阴影过渡柔和宽度。", zhCn: "FaceSDF 阴影过渡柔和宽度。", en: "Softness width for the FaceSDF shadow transition.", ja: "FaceSDF シャドウ境界のソフト幅。"),
                    DefaultNumber: 0.001, MinNumber: 0.0, MaxNumber: 100.0, Step: 0.001),
                new StyleParameterDescriptor(
                    "offset",
                    LocalizedText.From("阴影位置", zhCn: "阴影位置", en: "Shadow Position", ja: "シャドウ位置"),
                    StyleParameterKind.Float,
                    LocalizedText.From("FaceSDF 阴影阈值偏移。", zhCn: "FaceSDF 阴影阈值偏移。", en: "Shadow position offset for FaceSDF.", ja: "FaceSDF のシャドウ位置オフセット。"),
                    DefaultNumber: 0.55, MinNumber: -2.0, MaxNumber: 2.0, Step: 0.01),
                new StyleParameterDescriptor(
                    "encoding",
                    LocalizedText.From("数据编码", zhCn: "数据编码", en: "Encoding", ja: "エンコーディング"),
                    StyleParameterKind.Enum,
                    DefaultString: "GenshinSoftFace",
                    EnumOptions: [new("GenshinSoftFace", LocalizedText.From("GenshinSoftFace", en: "Genshin Soft Face"))]),
            ],
            TextureInputs:
            [
                new StyleTextureSlotDescriptor(
                    "primary",
                    LocalizedText.From("面部 SDF 贴图", zhCn: "面部 SDF 贴图", en: "Face SDF Map", ja: "顔 SDF マップ"),
                    Required: true,
                    Encoding: StyleTextureEncoding.Linear,
                    MipRule: StyleTextureMipRule.PointNoMip),
                new StyleTextureSlotDescriptor(
                    "secondary",
                    LocalizedText.From("面部混合遮罩", zhCn: "面部混合遮罩", en: "Combined Face Mask", ja: "顔コンバインマスク"),
                    Required: true,
                    Encoding: StyleTextureEncoding.Linear,
                    MipRule: StyleTextureMipRule.PointNoMip),
            ],
            Description: LocalizedText.From(
                "基于双张数值贴图与头部方向轴的原神软面部 SDF 阴影。",
                zhCn: "基于双张数值贴图与头部方向轴的原神软面部 SDF 阴影。",
                en: "Soft Genshin FaceSDF lighting driven by SDF/combined maps and head orientation axes.",
                ja: "2枚の数値マップと頭部方向軸による原神ソフト顔SDFシェーディング。"),
            SupportsBoneTracking: true,
            BoneAxisDefaults: FaceBoneDefaults);

        var hairMode = new StyleMaterialModeDescriptor(
            ModeId: ModeHair,
            DisplayName: LocalizedText.From("非金属 RB 高光", zhCn: "非金属 RB 高光", en: "Non-Metallic RB Specular", ja: "非金属 RB スペキュラ"),
            AllowedRoles: [StyleMaterialRole.Hair],
            Parameters:
            [
                new StyleParameterDescriptor("globalSpecularScale", LocalizedText.From("全局高光缩放", zhCn: "全局高光缩放", en: "Global Specular Scale", ja: "グローバルスペキュラ倍率"), StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: 0.0, MaxNumber: 2.0, Step: 0.01),
                new StyleParameterDescriptor("shininess", LocalizedText.From("高光锐度", zhCn: "高光锐度", en: "Shininess", ja: "光沢度"), StyleParameterKind.Float, DefaultNumber: 10.0, MinNumber: 0.0, MaxNumber: 64.0, Step: 0.1),
                new StyleParameterDescriptor("multiplier", LocalizedText.From("高光乘数", zhCn: "高光乘数", en: "Multiplier", ja: "乗数"), StyleParameterKind.Float, DefaultNumber: 0.1, MinNumber: 0.0, MaxNumber: 16.0, Step: 0.01),
                new StyleParameterDescriptor("intensity", LocalizedText.From("强度", zhCn: "强度", en: "Intensity", ja: "強度"), StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: 0.0, MaxNumber: 16.0, Step: 0.01),
            ],
            TextureInputs:
            [
                new StyleTextureSlotDescriptor(
                    "texture",
                    LocalizedText.From("RB 高光 Lightmap", zhCn: "RB 高光 Lightmap", en: "RB Specular Lightmap", ja: "RB スペキュラ Lightmap"),
                    Required: true,
                    Encoding: StyleTextureEncoding.Linear,
                    MipRule: StyleTextureMipRule.PointNoMip),
            ],
            Description: LocalizedText.From(
                "原神头发非金属 R/B 通道高光（可与 Hair Shadow Atlas 或金属绑定共用 Lightmap）。",
                zhCn: "原神头发非金属 R/B 通道高光（可与 Hair Shadow Atlas 或金属绑定共用 Lightmap）。",
                en: "Genshin hair non-metallic R/B channel specular highlight (composable with Hair Shadow Atlas or Metallic sharing Lightmap).",
                ja: "原神の髪向け非金属 R/B チャンネルハイライト（Hair Shadow Atlas や金属バインディングと併用可能）。"),
            LegacyAliases: [LegacyModeHairAlias]);

        var atlasMode = new StyleMaterialModeDescriptor(
            ModeId: ModeHairAtlas,
            DisplayName: LocalizedText.From("阴影图集 (Hair Shadow Atlas)", zhCn: "阴影图集 (Hair Shadow Atlas)", en: "Hair Shadow Atlas", ja: "Hair Shadow Atlas"),
            AllowedRoles: bodyOrHair,
            Parameters:
            [
                new StyleParameterDescriptor(
                    "atlasEncoding",
                    LocalizedText.From("图集色彩空间", zhCn: "图集色彩空间", en: "Atlas Encoding", ja: "アトラスエンコーディング"),
                    StyleParameterKind.Enum,
                    DefaultString: "LinearRgb",
                    EnumOptions:
                    [
                        new("LinearRgb", LocalizedText.From("线性 RGB", zhCn: "线性 RGB", en: "Linear RGB", ja: "リニア RGB")),
                        new("Srgb", LocalizedText.From("sRGB", zhCn: "sRGB", en: "sRGB", ja: "sRGB")),
                    ]),
                new StyleParameterDescriptor("explicitRowId", LocalizedText.From("色带行号", zhCn: "色带行号", en: "Explicit Row ID", ja: "ランプ行番号"), StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: 1.0, MaxNumber: 5.0, Step: 1.0),
                new StyleParameterDescriptor("dayNight", LocalizedText.From("昼夜混合", zhCn: "昼夜混合", en: "Day/Night Mix", ja: "昼夜ブレンド"), StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: 0.0, MaxNumber: 1.0, Step: 0.01),
                new StyleParameterDescriptor("shadowPosition", LocalizedText.From("阴影位置", zhCn: "阴影位置", en: "Shadow Position", ja: "シャドウ位置"), StyleParameterKind.Float, DefaultNumber: 0.7, MinNumber: -16.0, MaxNumber: 16.0, Step: 0.01),
                new StyleParameterDescriptor("shadowRampWidth", LocalizedText.From("色带过渡宽度", zhCn: "色带过渡宽度", en: "Shadow Ramp Width", ja: "シャドウランプ幅"), StyleParameterKind.Float, DefaultNumber: 0.18, MinNumber: -16.0, MaxNumber: 16.0, Step: 0.01),
                new StyleParameterDescriptor("vertexWidth", LocalizedText.From("顶点色宽度系数", zhCn: "顶点色宽度系数", en: "Vertex Width", ja: "頂点幅"), StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: -16.0, MaxNumber: 16.0, Step: 0.01),
                new StyleParameterDescriptor("vertexAo", LocalizedText.From("顶点 AO 系数", zhCn: "顶点 AO 系数", en: "Vertex AO", ja: "頂点 AO"), StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: -16.0, MaxNumber: 16.0, Step: 0.01),
                new StyleParameterDescriptor("useLightmapAo", LocalizedText.From("启用 Lightmap AO", zhCn: "启用 Lightmap AO", en: "Use Lightmap AO", ja: "Lightmap AO 使用"), StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: 0.0, MaxNumber: 1.0, Step: 1.0),
                new StyleParameterDescriptor("useVertexColorAo", LocalizedText.From("启用顶点 AO 混合", zhCn: "启用顶点 AO 混合", en: "Use Vertex Color AO", ja: "頂点カラー AO 使用"), StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: 0.0, MaxNumber: 1.0, Step: 1.0),
            ],
            TextureInputs:
            [
                new StyleTextureSlotDescriptor("lightmap", LocalizedText.From("Lightmap", zhCn: "Lightmap 贴图", en: "Lightmap", ja: "Lightmap"), Required: true, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new StyleTextureSlotDescriptor("atlas", LocalizedText.From("阴影色带图集", zhCn: "阴影色带图集", en: "Shadow Atlas", ja: "シャドウアトラス"), Required: true, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
            ],
            Description: LocalizedText.From(
                "原神昼夜阴影色带图集（显式行号与 AO 控制，可与 RB 高光或金属共用）。",
                zhCn: "原神昼夜阴影色带图集（显式行号与 AO 控制，可与 RB 高光或金属共用）。",
                en: "Genshin day/night Hair Shadow Atlas palette (composable with Hair RB or Metallic).",
                ja: "原神の昼夜シャドウアトラス（RBハイライトや金属マテリアルと併用可能）。"));

        var metallicMode = new StyleMaterialModeDescriptor(
            ModeId: ModeMetallic,
            DisplayName: LocalizedText.From("金属 MatCap 与高光", zhCn: "金属 MatCap 与高光", en: "Metallic MatCap & Specular", ja: "金属 MatCap・スペキュラ"),
            AllowedRoles: bodyOrHair,
            Parameters:
            [
                new StyleParameterDescriptor(
                    "matcapEncoding",
                    LocalizedText.From("MatCap 色彩编码", zhCn: "MatCap 色彩编码", en: "MatCap Encoding", ja: "MatCap エンコーディング"),
                    StyleParameterKind.Enum,
                    DefaultString: "LinearRgb",
                    EnumOptions:
                    [
                        new("LinearRgb", LocalizedText.From("线性 RGB", zhCn: "线性 RGB", en: "Linear RGB", ja: "リニア RGB")),
                        new("Srgb", LocalizedText.From("sRGB", zhCn: "sRGB", en: "sRGB", ja: "sRGB")),
                        new("LinearBgr", LocalizedText.From("线性 BGR", zhCn: "线性 BGR", en: "Linear BGR", ja: "リニア BGR")),
                        new("SrgbBgr", LocalizedText.From("sRGB BGR", zhCn: "sRGB BGR", en: "sRGB BGR", ja: "sRGB BGR")),
                    ]),
                new StyleParameterDescriptor(
                    "litMode",
                    LocalizedText.From("受光模式", zhCn: "受光模式", en: "Lit Mode", ja: "受光モード"),
                    StyleParameterKind.Enum,
                    DefaultString: "PrimaryResponse",
                    EnumOptions:
                    [
                        new("PrimaryResponse", LocalizedText.From("主光源响应", zhCn: "主光源响应", en: "Primary Response", ja: "メインライト応答")),
                        new("Uniform", LocalizedText.From("固定受光因子", zhCn: "固定受光因子", en: "Uniform", ja: "固定受光係数")),
                    ]),
                new StyleParameterDescriptor("enableMetallics", LocalizedText.From("金属度开关", zhCn: "金属度开关", en: "Enable Metallics", ja: "金属有効化"), StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: -32.0, MaxNumber: 32.0, Step: 0.01),
                new StyleParameterDescriptor("tileScale", LocalizedText.From("MatCap 横向平铺", zhCn: "MatCap 横向平铺", en: "Tile Scale", ja: "タイルスケール"), StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: -32.0, MaxNumber: 32.0, Step: 0.01),
                new StyleParameterDescriptor("brightness", LocalizedText.From("MatCap 亮度", zhCn: "MatCap 亮度", en: "Brightness", ja: "輝度"), StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: -32.0, MaxNumber: 32.0, Step: 0.01),
                new StyleParameterDescriptor("uniformLitFactor", LocalizedText.From("固定受光因子", zhCn: "固定受光因子", en: "Uniform Lit Factor", ja: "固定受光係数"), StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: -32.0, MaxNumber: 32.0, Step: 0.01),
                new StyleParameterDescriptor("darkColor", LocalizedText.From("金属暗部色", zhCn: "金属暗部色", en: "Dark Color", ja: "暗部色"), StyleParameterKind.Color3, MinNumber: -16.0, MaxNumber: 16.0, DefaultVector: [0.7f, 0.7f, 0.7f]),
                new StyleParameterDescriptor("lightColor", LocalizedText.From("金属亮部色", zhCn: "金属亮部色", en: "Light Color", ja: "明部色"), StyleParameterKind.Color3, MinNumber: -16.0, MaxNumber: 16.0, DefaultVector: [1f, 1f, 1f]),
                new StyleParameterDescriptor("shadowMultiplyColor", LocalizedText.From("金属阴影叠色", zhCn: "金属阴影叠色", en: "Shadow Multiply Color", ja: "シャドウ乗算色"), StyleParameterKind.Color3, MinNumber: -16.0, MaxNumber: 16.0, DefaultVector: [0.85f, 0.85f, 0.85f]),
                new StyleParameterDescriptor("hasSpecular", LocalizedText.From("启用金属高光", zhCn: "启用金属高光", en: "Has Specular", ja: "金属スペキュラ有効"), StyleParameterKind.Bool, DefaultBool: false),
                new StyleParameterDescriptor("specularColor", LocalizedText.From("高光颜色", zhCn: "高光颜色", en: "Specular Color", ja: "スペキュラ色"), StyleParameterKind.Color3, MinNumber: -16.0, MaxNumber: 16.0, DefaultVector: [1f, 1f, 1f]),
                new StyleParameterDescriptor("specularSharpColor", LocalizedText.From("锐利高光颜色", zhCn: "锐利高光颜色", en: "Specular Sharp Color", ja: "シャープスペキュラ色"), StyleParameterKind.Color3, MinNumber: -16.0, MaxNumber: 16.0, DefaultVector: [1f, 1f, 1f]),
                new StyleParameterDescriptor("specularShininess", LocalizedText.From("高光锐度", zhCn: "高光锐度", en: "Specular Shininess", ja: "スペキュラ光沢度"), StyleParameterKind.Float, DefaultNumber: 16.0, MinNumber: 0.0, MaxNumber: 256.0, Step: 0.1),
                new StyleParameterDescriptor("specularScale", LocalizedText.From("高光缩放", zhCn: "高光缩放", en: "Specular Scale", ja: "スペキュラ倍率"), StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: 0.0, MaxNumber: 32.0, Step: 0.01),
                new StyleParameterDescriptor("specularAttenuation", LocalizedText.From("阴影衰减", zhCn: "阴影衰减", en: "Specular Attenuation", ja: "スペキュラ減衰"), StyleParameterKind.Float, DefaultNumber: 0.5, MinNumber: 0.0, MaxNumber: 32.0, Step: 0.01),
                new StyleParameterDescriptor("specularSharpOffset", LocalizedText.From("锐利高光阈值", zhCn: "锐利高光阈值", en: "Specular Sharp Offset", ja: "シャープ閾値"), StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: -32.0, MaxNumber: 32.0, Step: 0.01),
                new StyleParameterDescriptor("specularRampMix", LocalizedText.From("高光 Ramp 混合", zhCn: "高光 Ramp 混合", en: "Specular Ramp Mix", ja: "ランプミックス"), StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: -32.0, MaxNumber: 32.0, Step: 0.01),
            ],
            TextureInputs:
            [
                new StyleTextureSlotDescriptor("lightmap", LocalizedText.From("Lightmap (R>0.9 金属)", zhCn: "Lightmap (R>0.9 金属)", en: "Lightmap (R>0.9 Metallic)", ja: "Lightmap (R>0.9 金属)"), Required: true, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new StyleTextureSlotDescriptor("matcap", LocalizedText.From("MatCap 贴图", zhCn: "MatCap 贴图", en: "MatCap Texture", ja: "MatCap テクスチャ"), Required: true, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new StyleTextureSlotDescriptor("specularRamp", LocalizedText.From("高光渐变 Ramp（可选）", zhCn: "高光渐变 Ramp（可选）", en: "Specular Ramp (Optional)", ja: "スペキュラランプ（任意）"), Required: false, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
            ],
            Description: LocalizedText.From(
                "原神 R>0.9 金属 MatCap 调色与可选金属高光（支持与 Hair Shadow Atlas 与 RB 高光共绑定）。",
                zhCn: "原神 R>0.9 金属 MatCap 调色与可选金属高光（支持与 Hair Shadow Atlas 与 RB 高光共绑定）。",
                en: "Genshin R>0.9 metallic MatCap palette and optional metallic specular lobe (composable with Hair Shadow Atlas and RB specular).",
                ja: "原神の R>0.9 金属 MatCap パレットと任意金属スペキュラ（Hair Shadow Atlas・RBハイライトと併用可能）。"));

        return [defaultMode, faceSdfMode, hairMode, atlasMode, metallicMode];
    }

    private static IReadOnlyList<StyleParameterDescriptor> BuildStyleParameters() =>
    [
        new StyleParameterDescriptor("shadowStrength", LocalizedText.From("风格化阴影强度", zhCn: "风格化阴影强度", en: "Shadow Strength", ja: "シャドウ強度"), StyleParameterKind.Float, DefaultNumber: DefaultStylizedShadingValue.ShadowStrength, MinNumber: 0, MaxNumber: 1, Step: 0.01),
        new StyleParameterDescriptor("shadowValue", LocalizedText.From("阴影明度", zhCn: "阴影明度", en: "Shadow Value", ja: "シャドウ明度"), StyleParameterKind.Float, DefaultNumber: DefaultStylizedShadingValue.ShadowValue, MinNumber: 0, MaxNumber: 1, Step: 0.01),
        new StyleParameterDescriptor("shadowHue", LocalizedText.From("阴影色相加深", zhCn: "阴影色相加深", en: "Shadow Hue", ja: "シャドウ色相深化"), StyleParameterKind.Float, DefaultNumber: DefaultStylizedShadingValue.ShadowHue, MinNumber: 0, MaxNumber: 1, Step: 0.01),
        new StyleParameterDescriptor("shadowTint", LocalizedText.From("阴影色调", zhCn: "阴影色调", en: "Shadow Tint", ja: "シャドウティント"), StyleParameterKind.Color3, DefaultVector: [DefaultStylizedShadingValue.ShadowTint.X, DefaultStylizedShadingValue.ShadowTint.Y, DefaultStylizedShadingValue.ShadowTint.Z], MinNumber: 0, MaxNumber: 1),
        new StyleParameterDescriptor("warmColor", LocalizedText.From("交界暖带色", zhCn: "交界暖带色", en: "Warm Band Color", ja: "境界暖色バンド"), StyleParameterKind.Color3, DefaultVector: [DefaultStylizedShadingValue.WarmColor.X, DefaultStylizedShadingValue.WarmColor.Y, DefaultStylizedShadingValue.WarmColor.Z], MinNumber: 0, MaxNumber: 1),
        new StyleParameterDescriptor("warmStrength", LocalizedText.From("交界暖带强度", zhCn: "交界暖带强度", en: "Warm Band Strength", ja: "境界暖色強度"), StyleParameterKind.Float, DefaultNumber: DefaultStylizedShadingValue.WarmStrength, MinNumber: 0, MaxNumber: 1, Step: 0.01),
        new StyleParameterDescriptor("rimEdge", LocalizedText.From("边缘光阈值", zhCn: "边缘光阈值", en: "Rim Edge", ja: "リム閾値"), StyleParameterKind.Float, DefaultNumber: DefaultStylizedShadingValue.RimEdge, MinNumber: 0, MaxNumber: 1, Step: 0.01),
        new StyleParameterDescriptor("rimStrength", LocalizedText.From("边缘光强度", zhCn: "边缘光强度", en: "Rim Strength", ja: "リム強度"), StyleParameterKind.Float, DefaultNumber: DefaultStylizedShadingValue.RimStrength, MinNumber: 0, MaxNumber: 1, Step: 0.01),
        new StyleParameterDescriptor("saturation", LocalizedText.From("饱和度", zhCn: "饱和度", en: "Saturation", ja: "彩度"), StyleParameterKind.Float, DefaultNumber: DefaultStylizedShadingValue.Saturation, MinNumber: 0, MaxNumber: 2, Step: 0.01),
        new StyleParameterDescriptor("contrast", LocalizedText.From("对比度", zhCn: "对比度", en: "Contrast", ja: "コントラスト"), StyleParameterKind.Float, DefaultNumber: DefaultStylizedShadingValue.Contrast, MinNumber: -0.5, MaxNumber: 0.5, Step: 0.01),
        new StyleParameterDescriptor("brightness", LocalizedText.From("亮度", zhCn: "亮度", en: "Brightness", ja: "明るさ"), StyleParameterKind.Float, DefaultNumber: DefaultStylizedShadingValue.Brightness, MinNumber: 0.5, MaxNumber: 2, Step: 0.01),
        new StyleParameterDescriptor("outlineValue", LocalizedText.From("描边明度", zhCn: "描边明度", en: "Outline Value", ja: "輪郭線明度"), StyleParameterKind.Float, DefaultNumber: DefaultStylizedShadingValue.OutlineValue, MinNumber: 0.05, MaxNumber: 1, Step: 0.01),
        new StyleParameterDescriptor("outlineSaturation", LocalizedText.From("描边饱和度", zhCn: "描边饱和度", en: "Outline Saturation", ja: "輪郭線彩度"), StyleParameterKind.Float, DefaultNumber: DefaultStylizedShadingValue.OutlineSaturation, MinNumber: 0, MaxNumber: 2, Step: 0.01),
    ];
}
