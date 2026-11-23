using System.Collections.ObjectModel;
using System.Numerics;
using Mmd.Modern.Plugins.Contracts;

namespace Mda.StylePlugin.StarRail;

/// <summary>Honkai: Star Rail rendering style definition matching baseline numerical parity.</summary>
public sealed class StarRailStyleDefinition : RenderingStyleDefinitionBase
{
    public const string CanonicalStyleId = "star-rail";

    public const string ModeDefault = "default";
    public const string ModeFaceSdf = "face-sdf";
    public const string ModeHair = "hair";
    public const string ModeBody = "body";

    public static StarRailStyleDefinition Instance { get; } = new();

    private static readonly LocalizedText DisplayNameText = LocalizedText.From(
        "崩坏：星穹铁道",
        zhCn: "崩坏：星穹铁道",
        en: "Honkai: Star Rail",
        ja: "崩壊：スターレイル");

    private static readonly LocalizedText DescriptionText = LocalizedText.From(
        "《崩坏：星穹铁道》风格渲染：普通 PMX 材质采用线性暗侧色阶与收束高光；另支持灰度 FaceSDF、Star 头发高光与基于 Mikk 切线的 Body MatCap/菲涅尔边缘光。",
        zhCn: "《崩坏：星穹铁道》风格渲染：普通 PMX 材质采用线性暗侧色阶与收束高光；另支持灰度 FaceSDF、Star 头发高光与基于 Mikk 切线的 Body MatCap/菲涅尔边缘光。",
        en: "Honkai: Star Rail style rendering: ordinary PMX materials use a shadow-side linear ramp and banded specular, plus optional GrayRed FaceSDF, Star hair highlight, and StarMcBody tangent MatCap/rim.",
        ja: "崩壊：スターレイルスタイル描画：通常PMXマテリアルはシャドウ側リニアランプとバンドスペキュラを使用し、GrayRed FaceSDF、Star髪ハイライト、StarMcBody接線MatCap・リムライトに対応します。");

    private static readonly StyleOutlineDefaults DefaultOutlineValue = new() { RespectMaterialEdgeFlag = true };
    private static readonly StyleToonResponseDefaults DefaultToonValue = new();
    private static readonly StyleFaceEyeDefaults DefaultFaceEyeValue = new();

    private static readonly StyleStylizedShadingParameters DefaultStylizedShadingValue = new(
        ShadowStrength: 1f,
        ShadowValue: 0.62f,
        ShadowHue: 0.8f,
        ShadowTint: new Vector3(0.84f, 0.87f, 1f),
        FaceFlatten: 0f,
        WarmColor: Vector3.One,
        WarmStrength: 0f,
        RimEdge: 0.58f,
        RimStrength: 0.32f,
        Saturation: 1.2f,
        Contrast: 0.2f,
        Brightness: 1f,
        OutlineValue: 0.38f,
        OutlineSaturation: 1.5f);

    private static readonly IReadOnlyDictionary<StyleMaterialRole, StyleMaterialTreatmentParameters> DefaultRoleTreatmentsValue =
        new Dictionary<StyleMaterialRole, StyleMaterialTreatmentParameters>
        {
            [StyleMaterialRole.Body] = new(
                Saturation: 1f,
                TransitionWidthScale: 0.55f,
                ValueContrast: 0.3f,
                ShadeGamma: 1.7f),
            [StyleMaterialRole.Hair] = new(
                Saturation: 1f,
                TransitionWidthScale: 0.55f,
                ValueContrast: 0.12f,
                ShadeGamma: 1.5f,
                HairHighlightStrength: 0.38f,
                HairHighlightWidth: 0.05f),
            [StyleMaterialRole.Face] = new(),
            [StyleMaterialRole.Eye] = new(),
        }.AsReadOnly();

    private static readonly Vector3 DefaultBodyShadowColor = new(0.62f, 0.66f, 0.72f);
    private static readonly Vector3 DefaultBodyRimColor = new(0.85f, 0.9f, 1f);

    private static readonly StyleBoneAxisDefaults FaceBoneDefaults = new(
        LocalForward: -Vector3.UnitZ,
        LocalUp: Vector3.UnitY,
        WorldForward: -Vector3.UnitZ,
        WorldRight: Vector3.UnitX,
        DefaultUseSelectedBone: true);

    private static readonly IReadOnlyList<StyleMaterialModeDescriptor> ModeDescriptors = BuildModeDescriptors();
    private static readonly IReadOnlyList<StyleParameterDescriptor> StyleGlobalParameterDescriptors = BuildStyleParameters();
    private static readonly IReadOnlyList<StyleShaderModule> ShaderModules = StarRailShaders.CreateAllModules();

    private StarRailStyleDefinition()
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
            foreach (var binding in context.Bindings)
            {
                if (!string.Equals(binding.StyleId, CanonicalStyleId, StringComparison.Ordinal))
                    continue;
                switch (binding.ModeId)
                {
                    case ModeFaceSdf:
                        return new StyleMaterialClassification(StyleMaterialRole.Face);
                    case ModeHair:
                        return new StyleMaterialClassification(StyleMaterialRole.Hair);
                    case ModeBody:
                        if (context.GuessMaterialRoles && !string.IsNullOrWhiteSpace(context.MaterialName))
                            return new StyleMaterialClassification(GuessMaterialRole(context.MaterialName));
                        return new StyleMaterialClassification(StyleMaterialRole.Body);
                }
            }
        }

        return new StyleMaterialClassification(ResolveMaterialRole(context));
    }

    public override StyleValidationResult ValidateMaterial(StyleMaterialValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Bindings is null || context.Bindings.Count == 0)
            return StyleValidationResult.Ok;

        if (context.Bindings.Count > 1)
        {
            return ErrorText(
                "星穹铁道风格同一材质仅支持单一专用模式绑定。",
                "Star Rail style does not allow combining multiple material mode bindings on a single material.",
                "スターレイルスタイルでは同じマテリアルに複数の専用モードを設定できません。");
        }

        var binding = context.Bindings[0];
        if (binding is null)
            return ErrorText("材质绑定列表包含空条目。", "Material bindings cannot contain null.", "マテリアルバインディングに null が含まれています。");

        if (!string.Equals(binding.StyleId, CanonicalStyleId, StringComparison.Ordinal))
            return ErrorText($"绑定 StyleId '{binding.StyleId}' 不属于星穹铁道风格。", $"Binding StyleId '{binding.StyleId}' does not match '{CanonicalStyleId}'.", $"バインディングの StyleId '{binding.StyleId}' が '{CanonicalStyleId}' と一致しません。");

        return binding.ModeId switch
        {
            ModeDefault => StyleValidationResult.Ok,
            ModeFaceSdf => ValidateFaceSdf(context, binding),
            ModeHair => ValidateHair(context, binding),
            ModeBody => ValidateBody(context, binding),
            _ => ErrorText($"星穹铁道风格不支持材质模式 '{binding.ModeId}'。", $"Unknown Star Rail material mode '{binding.ModeId}'.", $"未対応のスターレイルマテリアルモード '{binding.ModeId}' です。"),
        };
    }

    public override IReadOnlyList<StyleSceneTextureRequest> GetSceneTextureRequests(StyleMaterialBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Bindings is null || context.Bindings.Count == 0)
            return Array.Empty<StyleSceneTextureRequest>();

        var seen = new HashSet<int>();
        var list = new List<StyleSceneTextureRequest>(4);

        void Add(int index, StyleTextureEncoding encoding, bool isOptional = false)
        {
            if (index >= 0 && seen.Add(index))
                list.Add(new StyleSceneTextureRequest(index, encoding, StyleTextureMipRule.PointNoMip, IsOptional: isOptional));
        }

        foreach (var binding in context.Bindings)
        {
            if (!string.Equals(binding.StyleId, CanonicalStyleId, StringComparison.Ordinal))
                continue;
            switch (binding.ModeId)
            {
                case ModeFaceSdf:
                    Add(GetRequiredSlot(binding, "primary"), StyleTextureEncoding.Linear);
                    break;
                case ModeHair:
                    Add(GetRequiredSlot(binding, "highlight"), StyleTextureEncoding.Linear);
                    Add(GetRequiredSlot(binding, "shadow"), StyleTextureEncoding.Linear);
                    break;
                case ModeBody:
                    Add(GetRequiredSlot(binding, "normal"), StyleTextureEncoding.Linear, isOptional: true);
                    Add(GetRequiredSlot(binding, "detail"), StyleTextureEncoding.Linear, isOptional: true);
                    Add(GetRequiredSlot(binding, "mask"), StyleTextureEncoding.Linear, isOptional: true);
                    var (matcapSrgb, _) = ParseMatcapEncoding(GetString(binding, "matcapEncoding", "LinearRgb"));
                    Add(GetRequiredSlot(binding, "matcap"), matcapSrgb ? StyleTextureEncoding.SrgbColor : StyleTextureEncoding.Linear, isOptional: true);
                    break;
            }
        }

        return list.Count == 0 ? Array.Empty<StyleSceneTextureRequest>() : Array.AsReadOnly(list.ToArray());
    }

    public override bool RequiresSourceGeometry(StyleMaterialBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Bindings is null)
            return false;

        foreach (var binding in context.Bindings)
        {
            if (binding is not null
                && string.Equals(binding.StyleId, CanonicalStyleId, StringComparison.Ordinal)
                && string.Equals(binding.ModeId, ModeBody, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public override StyleBoneTransformRequest? GetBoneTransformRequest(StyleMaterialBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ResolvedRole != StyleMaterialRole.Face || context.Bindings is null)
            return null;

        foreach (var binding in context.Bindings)
        {
            if (!string.Equals(binding.StyleId, CanonicalStyleId, StringComparison.Ordinal)
                || !string.Equals(binding.ModeId, ModeFaceSdf, StringComparison.Ordinal))
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
        StyleMaterialBindingData? body = null;

        if (context.Bindings is { Count: > 0 })
        {
            foreach (var binding in context.Bindings)
            {
                if (!string.Equals(binding.StyleId, CanonicalStyleId, StringComparison.Ordinal))
                    continue;
                switch (binding.ModeId)
                {
                    case ModeFaceSdf: face = binding; break;
                    case ModeHair: hair = binding; break;
                    case ModeBody: body = binding; break;
                }
            }
        }

        if (face is not null
            && context.ResolvedRole == StyleMaterialRole.Face
            && (context.FaceEyeDefaults?.FaceNormalShadeStrength ?? 1f) != 0f)
        {
            return BuildFaceSdfDrawData(context, face);
        }

        if (hair is not null && context.ResolvedRole == StyleMaterialRole.Hair)
            return BuildHairDrawData(context, hair);

        if (body is not null && context.ResolvedRole is StyleMaterialRole.Body or StyleMaterialRole.Hair)
            return BuildBodyDrawData(context, body);

        var stylizedOverride = ResolveGlobalStylizedOverride(context);
        bool hasDedicatedBinding = face is not null || hair is not null || body is not null;
        if (stylizedOverride is null && !hasDedicatedBinding)
            return null;

        return new StyleDrawData(
            ShaderModuleId: StarRailShaders.DefaultModuleId,
            StylizedShadingOverride: stylizedOverride);
    }

    private static StyleDrawData BuildFaceSdfDrawData(StyleDrawContext context, StyleMaterialBindingData face)
    {
        var (forward, right) = ResolveFaceBasis(context, face.BoneBinding);
        float transition = GetFloat(face, "transition", 1f);
        float offset = GetFloat(face, "offset", 1f);

        var cb = new StyleConstantBufferBuilder();
        cb.Write(new Vector4(forward, 0f));
        cb.Write(new Vector4(right, 0f));
        cb.Write(new Vector4(transition, offset, 0f, 0f));

        int primary = ResolveComposedSlot(context, GetRequiredSlot(face, "primary"));

        return new StyleDrawData(
            ShaderModuleId: StarRailShaders.FaceSdfModuleId,
            ConstantBufferData: cb.ToArray(),
            TextureBindings:
            [
                new StyleTextureBinding(0, primary, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
            ]);
    }

    private static StyleDrawData BuildHairDrawData(StyleDrawContext context, StyleMaterialBindingData hair)
    {
        var cb = new StyleConstantBufferBuilder();
        cb.Write(new Vector4(
            GetFloat(hair, "highlightCoefficient", 0f),
            GetFloat(hair, "highlightBrightness", 2f),
            GetFloat(hair, "shadowCoefficient", 0.5f),
            GetFloat(hair, "shadowSoftness", 0.5f)));
        cb.Write(new Vector4(
            GetFloat(hair, "intensity", 0.1f),
            0f, // Star Rail consumes Blue channel only.
            0f,
            0f));

        int highlight = ResolveComposedSlot(context, GetRequiredSlot(hair, "highlight"));
        int shadow = ResolveComposedSlot(context, GetRequiredSlot(hair, "shadow"));

        return new StyleDrawData(
            ShaderModuleId: StarRailShaders.HairModuleId,
            ConstantBufferData: cb.ToArray(),
            TextureBindings:
            [
                new StyleTextureBinding(0, highlight, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new StyleTextureBinding(1, shadow, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
            ]);
    }

    private static StyleDrawData BuildBodyDrawData(StyleDrawContext context, StyleMaterialBindingData body)
    {
        var (matcapSrgb, swizzleBgr) = ParseMatcapEncoding(GetString(body, "matcapEncoding", "LinearRgb"));
        int uvChannel = Math.Clamp((int)Math.Round(GetDouble(body, "uvChannel", 0.0)), 0, 4);

        Vector3 shadowColor = GetVector3(body, "shadowColor", DefaultBodyShadowColor);
        Vector3 rimColor = GetVector3(body, "rimColor", DefaultBodyRimColor);

        var cb = new StyleConstantBufferBuilder();
        cb.Write(new Vector4(
            GetFloat(body, "detailStrength", 1f),
            GetFloat(body, "metallicSaturation", 1f),
            GetFloat(body, "highlightCoefficient", 0f),
            GetFloat(body, "highlightStrength", 1f)));
        cb.Write(new Vector4(
            GetFloat(body, "shadowCoefficient", 0.5f),
            GetFloat(body, "shadowSoftness", 0.1f),
            GetFloat(body, "shadowColorStrength", 1f),
            GetFloat(body, "warmBandStrength", 0.25f)));
        cb.Write(new Vector4(shadowColor, swizzleBgr ? 1f : 0f));
        cb.Write(new Vector4(rimColor, GetFloat(body, "rimStrength", 0f)));
        cb.Write(new Vector4(
            GetFloat(body, "rimSize", 0.75f),
            GetFloat(body, "rimDirectionRadians", 0f),
            GetFloat(body, "rotationRadians", 0f),
            0f)); // Star Rail forces NormalStrength = 0 in GeometryAdapter(wuthering: false).

        int normal = ResolveComposedSlot(context, GetRequiredSlot(body, "normal"));
        int detail = ResolveComposedSlot(context, GetRequiredSlot(body, "detail"));
        int mask = ResolveComposedSlot(context, GetRequiredSlot(body, "mask"));
        int matcap = ResolveComposedSlot(context, GetRequiredSlot(body, "matcap"));

        return new StyleDrawData(
            ShaderModuleId: StarRailShaders.BodyModuleId,
            ConstantBufferData: cb.ToArray(),
            TextureBindings:
            [
                new StyleTextureBinding(0, normal, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new StyleTextureBinding(1, detail, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new StyleTextureBinding(2, mask, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new StyleTextureBinding(3, matcap, Encoding: matcapSrgb ? StyleTextureEncoding.SrgbColor : StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
            ],
            BufferBindings:
            [
                new StyleBufferBinding(0, StyleBufferKind.MikkTangentCorners, $"uv{uvChannel}"),
            ]);
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

    private static StyleStylizedShadingParameters ResolveDefaultShadingForRole(StyleMaterialRole role) => role switch
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
            return ErrorText("星穹铁道 FaceSDF 必须用于 Face 角色材质。", "Star Rail FaceSDF requires an explicitly selected Face material.", "スターレイルの FaceSDF は Face ロールのマテリアルが必要です。");

        var slotResult = ValidateTextureSlot(context, face, "primary", required: true);
        if (!slotResult.IsValid) return slotResult;

        if (face.TextureSlots is not null && face.TextureSlots.TryGetValue("secondary", out int secIdx) && secIdx != -1)
            return ErrorText("星穹铁道 GrayRed FaceSDF 不支持次级贴图槽位 'secondary'。", "Star Rail GrayRed FaceSDF does not support a secondary texture slot.", "スターレイルの GrayRed FaceSDF は secondary スロットに対応していません。");

        string encoding = GetString(face, "encoding", "GrayRed");
        if (!string.Equals(encoding, "GrayRed", StringComparison.Ordinal))
            return ErrorText($"星穹铁道 FaceSDF 仅支持 'GrayRed' 编码（收到 '{encoding}'）。", $"Star Rail FaceSDF encoding must be 'GrayRed' (got '{encoding}').", $"スターレイルの FaceSDF エンコーディングは 'GrayRed' のみ対応しています（'{encoding}'）。");

        var numResult = ValidateNumberRange(face, "transition", 0.001, 100.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(face, "offset", -2.0, 2.0);
        if (!numResult.IsValid) return numResult;

        return ValidateBoneAxisBinding(face.BoneBinding);
    }

    private static StyleValidationResult ValidateHair(StyleMaterialValidationContext context, StyleMaterialBindingData hair)
    {
        if (context.ResolvedRole != StyleMaterialRole.Hair)
            return ErrorText("星穹铁道专用头发高光必须用于 Hair 角色材质。", "Dedicated Star Rail hair requires an explicitly selected Hair material.", "スターレイルの専用髪ハイライトは Hair ロールのマテリアルが必要です。");

        var slotResult = ValidateTextureSlot(context, hair, "highlight", required: true);
        if (!slotResult.IsValid) return slotResult;
        slotResult = ValidateTextureSlot(context, hair, "shadow", required: true);
        if (!slotResult.IsValid) return slotResult;

        string channel = GetString(hair, "highlightChannel", "Blue");
        if (!string.Equals(channel, "Blue", StringComparison.Ordinal))
            return ErrorText($"星穹铁道头发高光通道仅支持 'Blue'，不允许使用 '{channel}'。", $"Star Rail hair requires 'Blue' highlight channel ('{channel}' is not allowed).", $"スターレイルの髪ハイライトチャンネルは 'Blue' のみ対応しています（'{channel}' は不可）。");

        var numResult = ValidateNumberRange(hair, "highlightCoefficient", 0.0, 1.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(hair, "highlightBrightness", -1.0, 10.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(hair, "shadowCoefficient", 0.0, 1.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(hair, "shadowSoftness", 0.1, 1.0);
        if (!numResult.IsValid) return numResult;
        return ValidateNumberRange(hair, "intensity", 0.0, 16.0);
    }

    private static StyleValidationResult ValidateBody(StyleMaterialValidationContext context, StyleMaterialBindingData body)
    {
        if (context.ResolvedRole is not (StyleMaterialRole.Body or StyleMaterialRole.Hair))
            return ErrorText("星穹铁道 Body 材质仅允许用于 Body 或 Hair 角色（Face 与 Eye 受保护）。", "Star Rail Body mode requires Body or Hair role; Face and Eye are protected.", "スターレイルの Body モードは Body または Hair のみ使用できます（Face と Eye は保護されています）。");

        foreach (string slotKey in new[] { "normal", "detail", "mask", "matcap" })
        {
            var r = ValidateTextureSlot(context, body, slotKey, required: true);
            if (!r.IsValid) return r;
        }

        string encoding = GetString(body, "matcapEncoding", "LinearRgb");
        if (encoding is not ("LinearRgb" or "Srgb" or "SrgbRgb" or "LinearBgr" or "SrgbBgr"))
            return ErrorText($"不支持的 MatCap 颜色编码 '{encoding}'。", $"Unsupported MatCap color encoding '{encoding}'.", $"未対応の MatCap カラーエンコーディング '{encoding}' です。");

        string genRev = GetString(body, "generatorRevision", "mikk-v1");
        if (!string.Equals(genRev, "mikk-v1", StringComparison.Ordinal))
            return ErrorText($"星穹铁道 Body 切线仅支持 'mikk-v1'（收到 '{genRev}'）。", $"Star Rail Body requires 'mikk-v1' tangent generator (got '{genRev}').", $"スターレイル Body の接線生成リビジョンは 'mikk-v1' のみ対応しています（'{genRev}'）。");

        if (body.NumberValues is not null && body.NumberValues.TryGetValue("uvChannel", out double uvChan))
        {
            if (!double.IsFinite(uvChan) || uvChan < 0.0 || uvChan > 4.0 || uvChan != Math.Truncate(uvChan))
                return ErrorText($"参数 'uvChannel' 必须为 [0, 4] 范围内的整数（收到 {uvChan}）。", $"Parameter 'uvChannel' must be an integer in [0, 4] (got {uvChan}).", $"パラメータ 'uvChannel' は [0, 4] の整数である必要があります（値: {uvChan}）。");
        }

        var numResult = ValidateNumberRange(body, "normalStrength", 0.0, 8.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(body, "detailStrength", 0.0, 1.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(body, "metallicSaturation", 0.0, 4.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(body, "highlightCoefficient", -1.0, 1.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(body, "highlightStrength", 0.0, 1.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(body, "rotationRadians", -65536.0, 65536.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(body, "shadowCoefficient", 0.0, 1.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(body, "shadowSoftness", 0.00001, 1.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(body, "shadowColorStrength", 0.0, 1.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(body, "warmBandStrength", 0.0, 1.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(body, "rimStrength", 0.0, 1.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(body, "rimSize", 0.0, 4.0);
        if (!numResult.IsValid) return numResult;
        numResult = ValidateNumberRange(body, "rimDirectionRadians", -65536.0, 65536.0);
        if (!numResult.IsValid) return numResult;

        var vecResult = ValidateVector3Range(body, "shadowColor", 0f, 1f);
        if (!vecResult.IsValid) return vecResult;
        return ValidateVector3Range(body, "rimColor", 0f, 1f);
    }

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

    private static double GetDouble(StyleMaterialBindingData binding, string key, double fallback)
        => binding.NumberValues is not null && binding.NumberValues.TryGetValue(key, out double val) && double.IsFinite(val) ? val : fallback;

    private static float GetFloat(StyleMaterialBindingData binding, string key, float fallback)
        => binding.NumberValues is not null && binding.NumberValues.TryGetValue(key, out double val) && double.IsFinite(val) ? (float)val : fallback;

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
                "普通 PMX 材质的默认星穹铁道三渲二着色与处理。",
                zhCn: "普通 PMX 材质的默认星穹铁道三渲二着色与处理。",
                en: "Default Honkai: Star Rail cel shading and PMX material treatment for ordinary materials.",
                ja: "通常PMXマテリアル向けの標準スターレイルトゥーンシェーディングとマテリアル処理。"));

        var faceSdfMode = new StyleMaterialModeDescriptor(
            ModeId: ModeFaceSdf,
            DisplayName: LocalizedText.From("面部 SDF（灰度）", zhCn: "面部 SDF（灰度）", en: "Face SDF (GrayRed)", ja: "顔 SDF（GrayRed）"),
            AllowedRoles: [StyleMaterialRole.Face],
            Parameters:
            [
                new StyleParameterDescriptor("transition", LocalizedText.From("过渡阈值缩放", zhCn: "过渡阈值缩放", en: "Transition", ja: "遷移幅"), StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: 0.001, MaxNumber: 100.0, Step: 0.01),
                new StyleParameterDescriptor("offset", LocalizedText.From("阴影中心偏移", zhCn: "阴影中心偏移", en: "Offset", ja: "オフセット"), StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: -2.0, MaxNumber: 2.0, Step: 0.01),
                new StyleParameterDescriptor("encoding", LocalizedText.From("数据编码", zhCn: "数据编码", en: "Encoding", ja: "エンコーディング"), StyleParameterKind.Enum, DefaultString: "GrayRed", EnumOptions: [new("GrayRed", LocalizedText.From("GrayRed", en: "GrayRed"))]),
            ],
            TextureInputs:
            [
                new StyleTextureSlotDescriptor("primary", LocalizedText.From("灰度 FaceSDF 贴图", zhCn: "灰度 FaceSDF 贴图", en: "GrayRed Face SDF Map", ja: "GrayRed 顔 SDF マップ"), Required: true, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
            ],
            Description: LocalizedText.From(
                "基于 R 通道灰度 SDF 贴图与头部方向轴的星穹铁道面部阴影。",
                zhCn: "基于 R 通道灰度 SDF 贴图与头部方向轴的星穹铁道面部阴影。",
                en: "Honkai: Star Rail GrayRed FaceSDF lighting driven by single-map Red channel and head axes.",
                ja: "Rチャンネル単体マップと頭部軸によるスターレイルの顔SDFシェーディング。"),
            SupportsBoneTracking: true,
            BoneAxisDefaults: FaceBoneDefaults);

        var hairMode = new StyleMaterialModeDescriptor(
            ModeId: ModeHair,
            DisplayName: LocalizedText.From("头发专用高光", zhCn: "头发专用高光", en: "Star Hair Specular", ja: "髪専用スペキュラ"),
            AllowedRoles: [StyleMaterialRole.Hair],
            Parameters:
            [
                new StyleParameterDescriptor("highlightChannel", LocalizedText.From("高光通道", zhCn: "高光通道", en: "Highlight Channel", ja: "ハイライトチャンネル"), StyleParameterKind.Enum, DefaultString: "Blue", EnumOptions: [new("Blue", LocalizedText.From("B 通道 (Blue)", zhCn: "B 通道 (Blue)", en: "Blue", ja: "Blue"))]),
                new StyleParameterDescriptor("highlightCoefficient", LocalizedText.From("高光系数偏移", zhCn: "高光系数偏移", en: "Highlight Coefficient", ja: "ハイライト係数"), StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: 0.0, MaxNumber: 1.0, Step: 0.01),
                new StyleParameterDescriptor("highlightBrightness", LocalizedText.From("高光亮度", zhCn: "高光亮度", en: "Highlight Brightness", ja: "ハイライト輝度"), StyleParameterKind.Float, DefaultNumber: 2.0, MinNumber: -1.0, MaxNumber: 10.0, Step: 0.05),
                new StyleParameterDescriptor("shadowCoefficient", LocalizedText.From("阴影系数", zhCn: "阴影系数", en: "Shadow Coefficient", ja: "シャドウ係数"), StyleParameterKind.Float, DefaultNumber: 0.5, MinNumber: 0.0, MaxNumber: 1.0, Step: 0.01),
                new StyleParameterDescriptor("shadowSoftness", LocalizedText.From("阴影柔和度", zhCn: "阴影柔和度", en: "Shadow Softness", ja: "シャドウ柔軟度"), StyleParameterKind.Float, DefaultNumber: 0.5, MinNumber: 0.1, MaxNumber: 1.0, Step: 0.01),
                new StyleParameterDescriptor("intensity", LocalizedText.From("高光总强度", zhCn: "高光总强度", en: "Intensity", ja: "強度"), StyleParameterKind.Float, DefaultNumber: 0.1, MinNumber: 0.0, MaxNumber: 16.0, Step: 0.01),
            ],
            TextureInputs:
            [
                new StyleTextureSlotDescriptor("highlight", LocalizedText.From("高光遮罩 (B 通道)", zhCn: "高光遮罩 (B 通道)", en: "Highlight Map (Blue)", ja: "ハイライトマップ (Blue)"), Required: true, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new StyleTextureSlotDescriptor("shadow", LocalizedText.From("阴影遮罩 (R 通道)", zhCn: "阴影遮罩 (R 通道)", en: "Shadow Map (Red)", ja: "シャドウマップ (Red)"), Required: true, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
            ],
            Description: LocalizedText.From(
                "星穹铁道专用头发 B/R 双贴图控制高光。",
                zhCn: "星穹铁道专用头发 B/R 双贴图控制高光。",
                en: "Honkai: Star Rail hair highlight controlled by Blue highlight mask and Red shadow mask.",
                ja: "Blue ハイライトと Red シャドウの2マップによるスターレイル専用髪ハイライト。"));

        var bodyMode = new StyleMaterialModeDescriptor(
            ModeId: ModeBody,
            DisplayName: LocalizedText.From("专用 Body MatCap", zhCn: "专用 Body MatCap", en: "Star Body MatCap", ja: "専用 Body MatCap"),
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
                new StyleParameterDescriptor("generatorRevision", LocalizedText.From("切线生成器", zhCn: "切线生成器", en: "Generator Revision", ja: "接線ジェネレータ"), StyleParameterKind.Enum, DefaultString: "mikk-v1", EnumOptions: [new("mikk-v1", LocalizedText.From("MikkTSpace v1", en: "mikk-v1"))]),
                new StyleParameterDescriptor("uvChannel", LocalizedText.From("UV 通道", zhCn: "UV 通道", en: "UV Channel", ja: "UV チャンネル"), StyleParameterKind.Int, DefaultNumber: 0, MinNumber: 0, MaxNumber: 4, Step: 1),
                new StyleParameterDescriptor("normalStrength", LocalizedText.From("法线强度", zhCn: "法线强度", en: "Normal Strength", ja: "法線強度"), StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: 0.0, MaxNumber: 8.0, Step: 0.01),
                new StyleParameterDescriptor("detailStrength", LocalizedText.From("细节强度", zhCn: "细节强度", en: "Detail Strength", ja: "ディテール強度"), StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: 0.0, MaxNumber: 1.0, Step: 0.01),
                new StyleParameterDescriptor("metallicSaturation", LocalizedText.From("金属饱和度", zhCn: "金属饱和度", en: "Metallic Saturation", ja: "金属彩度"), StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: 0.0, MaxNumber: 4.0, Step: 0.01),
                new StyleParameterDescriptor("highlightCoefficient", LocalizedText.From("高光系数偏移", zhCn: "高光系数偏移", en: "Highlight Coefficient", ja: "ハイライト係数"), StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: -1.0, MaxNumber: 1.0, Step: 0.01),
                new StyleParameterDescriptor("highlightStrength", LocalizedText.From("MatCap 叠加强度", zhCn: "MatCap 叠加强度", en: "Highlight Strength", ja: "MatCap 強度"), StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: 0.0, MaxNumber: 1.0, Step: 0.01),
                new StyleParameterDescriptor("rotationRadians", LocalizedText.From("MatCap 旋转角（弧度）", zhCn: "MatCap 旋转角（弧度）", en: "Rotation (Radians)", ja: "MatCap 回転角（ラジアン）"), StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: -65536.0, MaxNumber: 65536.0, Step: 0.05),
                new StyleParameterDescriptor("shadowCoefficient", LocalizedText.From("阴影偏移系数", zhCn: "阴影偏移系数", en: "Shadow Coefficient", ja: "シャドウ係数"), StyleParameterKind.Float, DefaultNumber: 0.5, MinNumber: 0.0, MaxNumber: 1.0, Step: 0.01),
                new StyleParameterDescriptor("shadowSoftness", LocalizedText.From("阴影柔和度", zhCn: "阴影柔和度", en: "Shadow Softness", ja: "シャドウ柔軟度"), StyleParameterKind.Float, DefaultNumber: 0.1, MinNumber: 0.00001, MaxNumber: 1.0, Step: 0.01),
                new StyleParameterDescriptor("shadowColorStrength", LocalizedText.From("阴影调色强度", zhCn: "阴影调色强度", en: "Shadow Color Strength", ja: "シャドウカラー強度"), StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: 0.0, MaxNumber: 1.0, Step: 0.01),
                new StyleParameterDescriptor("warmBandStrength", LocalizedText.From("暖色交界带强度", zhCn: "暖色交界带强度", en: "Warm Band Strength", ja: "暖色境界帯強度"), StyleParameterKind.Float, DefaultNumber: 0.25, MinNumber: 0.0, MaxNumber: 1.0, Step: 0.01),
                new StyleParameterDescriptor("shadowColor", LocalizedText.From("阴影颜色", zhCn: "阴影颜色", en: "Shadow Color", ja: "シャドウカラー"), StyleParameterKind.Color3, DefaultVector: [DefaultBodyShadowColor.X, DefaultBodyShadowColor.Y, DefaultBodyShadowColor.Z], MinNumber: 0.0, MaxNumber: 1.0),
                new StyleParameterDescriptor("rimColor", LocalizedText.From("边缘光颜色", zhCn: "边缘光颜色", en: "Rim Color", ja: "リムカラー"), StyleParameterKind.Color3, DefaultVector: [DefaultBodyRimColor.X, DefaultBodyRimColor.Y, DefaultBodyRimColor.Z], MinNumber: 0.0, MaxNumber: 1.0),
                new StyleParameterDescriptor("rimStrength", LocalizedText.From("边缘光强度", zhCn: "边缘光强度", en: "Rim Strength", ja: "リム強度"), StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: 0.0, MaxNumber: 1.0, Step: 0.01),
                new StyleParameterDescriptor("rimSize", LocalizedText.From("边缘光范围", zhCn: "边缘光范围", en: "Rim Size", ja: "リムサイズ"), StyleParameterKind.Float, DefaultNumber: 0.75, MinNumber: 0.0, MaxNumber: 4.0, Step: 0.01),
                new StyleParameterDescriptor("rimDirectionRadians", LocalizedText.From("边缘光方向角（弧度）", zhCn: "边缘光方向角（弧度）", en: "Rim Direction (Radians)", ja: "リム方向角（ラジアン）"), StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: -65536.0, MaxNumber: 65536.0, Step: 0.05),
            ],
            TextureInputs:
            [
                new StyleTextureSlotDescriptor("normal", LocalizedText.From("法线贴图", zhCn: "法线贴图", en: "Normal Map", ja: "法線マップ"), Required: true, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new StyleTextureSlotDescriptor("detail", LocalizedText.From("细节贴图", zhCn: "细节贴图", en: "Detail Map", ja: "ディテールマップ"), Required: true, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new StyleTextureSlotDescriptor("mask", LocalizedText.From("遮罩贴图 (R/B)", zhCn: "遮罩贴图 (R/B)", en: "Mask Map (R/B)", ja: "マスクマップ (R/B)"), Required: true, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new StyleTextureSlotDescriptor("matcap", LocalizedText.From("MatCap 贴图", zhCn: "MatCap 贴图", en: "MatCap Texture", ja: "MatCap テクスチャ"), Required: true, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
            ],
            Description: LocalizedText.From(
                "基于 MikkTSpace 切线的星穹铁道角色机身与服饰 MatCap/边缘光。",
                zhCn: "基于 MikkTSpace 切线的星穹铁道角色机身与服饰 MatCap/边缘光。",
                en: "Honkai: Star Rail character body MatCap, warm terminator bands, and dielectric Fresnel rim driven by MikkTSpace tangents.",
                ja: "MikkTSpace接線に基づくスターレイルのボディMatCap・暖色境界帯・フレネルリムライト。"));

        return [defaultMode, faceSdfMode, hairMode, bodyMode];
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
