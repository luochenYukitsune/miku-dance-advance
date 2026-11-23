using System.Collections.ObjectModel;
using System.Numerics;
using Mmd.Modern.Plugins.Contracts;

namespace Mda.StylePlugin.WutheringWaves;

/// <summary>
/// Rendering style definition for Wuthering Waves, matching the 9d58eae numerical and shader contract 1:1.
/// </summary>
public sealed class WutheringWavesStyleDefinition : RenderingStyleDefinitionBase
{
    public const string CanonicalStyleId = "wuthering-waves";

    private static readonly LocalizedText StyleDisplayName = LocalizedText.From(
        "Wuthering Waves", zhCn: "鸣潮", en: "Wuthering Waves", ja: "鳴潮");

    private static readonly LocalizedText StyleDescription = LocalizedText.From(
        "Wuthering Waves stylized cel shading with raw-NdotL banding, bounded specular, GrayRed FaceSDF, Hair masks, and MikkTSpace Body MatCap.",
        zhCn: "鸣潮风格化渲染：原始法线亮暗带、受限高光、灰度 FaceSDF、头发高光遮罩与 MikkTSpace 身体 MatCap。",
        en: "Wuthering Waves stylized cel shading with raw-NdotL banding, bounded specular, GrayRed FaceSDF, Hair masks, and MikkTSpace Body MatCap.",
        ja: "鳴潮トゥーンシェーディング：生 NdotL バンド、制限付きスペキュラ、GrayRed FaceSDF、ヘアマスク、MikkTSpace ボディ MatCap。");

    private static readonly StyleOutlineDefaults OutlineDefaults = new() { RespectMaterialEdgeFlag = true };
    private static readonly StyleToonResponseDefaults ToonDefaults = new(Threshold: 0.5f, TransitionWidth: 0.1f);
    private static readonly StyleFaceEyeDefaults FaceEyeDefaults = new(
        FaceNormalShadeStrength: 0.35f,
        FaceGeometricShadowStrength: 1.0f,
        EyeGeometricShadowStrength: 0.2f);

    private static readonly StyleStylizedShadingParameters StylizedDefaults = new(
        ShadowStrength: 1f,
        ShadowValue: 0.64f,
        ShadowHue: 0.6f,
        ShadowTint: new Vector3(0.9f, 0.84f, 1f),
        FaceFlatten: 0f,
        WarmColor: new Vector3(1f, 0.8f, 0.6f),
        WarmStrength: 0.6f,
        RimEdge: 0.66f,
        RimStrength: 0.16f,
        Saturation: 1.12f,
        Contrast: 0.14f,
        Brightness: 1.03f,
        OutlineValue: 0.42f,
        OutlineSaturation: 1.25f);

    private static readonly IReadOnlyDictionary<StyleMaterialRole, StyleMaterialTreatmentParameters> RoleTreatments =
        new ReadOnlyDictionary<StyleMaterialRole, StyleMaterialTreatmentParameters>(
            new Dictionary<StyleMaterialRole, StyleMaterialTreatmentParameters>
            {
                [StyleMaterialRole.Body] = new(
                    Saturation: 1f,
                    TransitionWidthScale: 1.2f,
                    ValueContrast: 0.32f,
                    ShadeGamma: 1.35f,
                    HairHighlightStrength: 0f,
                    HairHighlightWidth: 0.12f),
                [StyleMaterialRole.Hair] = new(
                    Saturation: 1f,
                    TransitionWidthScale: 1.15f,
                    ValueContrast: 0.24f,
                    ShadeGamma: 1.25f,
                    HairHighlightStrength: 0.12f,
                    HairHighlightWidth: 0.3f),
                [StyleMaterialRole.Face] = new(),
                [StyleMaterialRole.Eye] = new(),
            });

    private static readonly Vector3 DefaultHeadLocalForward = new(0f, 0f, -1f);
    private static readonly Vector3 DefaultHeadLocalUp = new(0f, 1f, 0f);
    private static readonly Vector3 DefaultBodyShadowColor = new(0.62f, 0.66f, 0.72f);
    private static readonly Vector3 DefaultBodyRimColor = new(0.85f, 0.9f, 1f);

    private static readonly IReadOnlyList<StyleMaterialModeDescriptor> Modes = CreateMaterialModes();
    private static readonly IReadOnlyList<StyleShaderModule> ShaderModules = WutheringWavesShaders.CreateModules();

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
                    $"绑定 StyleId “{b.StyleId}”不属于鸣潮风格。",
                    $"バインディングの StyleId '{b.StyleId}' は '{CanonicalStyleId}' と一致しません。");
        }

        if (context.Bindings.Count > 1)
            return Error("A material cannot combine multiple dedicated Wuthering Waves modes.",
                "材质不能同时组合多个鸣潮专用绑定模式。",
                "1つのマテリアルに複数の鳴潮専用バインディングを組み合わせることはできません。");

        var binding = context.Bindings[0];
        return binding.ModeId switch
        {
            WutheringWavesShaders.FaceSdfModuleId => ValidateFaceSdf(context, binding),
            WutheringWavesShaders.HairModuleId => ValidateHair(context, binding),
            WutheringWavesShaders.BodyModuleId => ValidateBody(context, binding),
            _ => Error($"Unsupported Wuthering Waves material mode '{binding.ModeId}'.",
                $"不支持的鸣潮材质模式“{binding.ModeId}”。",
                $"未対応の鳴潮マテリアルモード '{binding.ModeId}' です。")
        };
    }

    public override IReadOnlyList<StyleSceneTextureRequest> GetSceneTextureRequests(StyleMaterialBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var active = FilterBindings(context.Bindings);
        if (active.Count == 0)
            return Array.Empty<StyleSceneTextureRequest>();

        var binding = active[0];
        var requests = new List<StyleSceneTextureRequest>();
        switch (binding.ModeId)
        {
            case WutheringWavesShaders.FaceSdfModuleId:
                if (TryGetSlot(binding, "primary", out int primary) && primary >= 0)
                    requests.Add(new StyleSceneTextureRequest(primary, StyleTextureEncoding.Linear, StyleTextureMipRule.PointNoMip));
                break;

            case WutheringWavesShaders.HairModuleId:
                if (TryGetSlot(binding, "highlight", out int highlight) && highlight >= 0)
                    requests.Add(new StyleSceneTextureRequest(highlight, StyleTextureEncoding.Linear, StyleTextureMipRule.PointNoMip));
                if (TryGetSlot(binding, "shadow", out int shadow) && shadow >= 0)
                    requests.Add(new StyleSceneTextureRequest(shadow, StyleTextureEncoding.Linear, StyleTextureMipRule.PointNoMip));
                break;

            case WutheringWavesShaders.BodyModuleId:
                if (TryGetSlot(binding, "normal", out int normal) && normal >= 0)
                    requests.Add(new StyleSceneTextureRequest(normal, StyleTextureEncoding.Linear, StyleTextureMipRule.PointNoMip, IsOptional: true));
                if (TryGetSlot(binding, "detail", out int detail) && detail >= 0)
                    requests.Add(new StyleSceneTextureRequest(detail, StyleTextureEncoding.Linear, StyleTextureMipRule.PointNoMip, IsOptional: true));
                if (TryGetSlot(binding, "mask", out int mask) && mask >= 0)
                    requests.Add(new StyleSceneTextureRequest(mask, StyleTextureEncoding.Linear, StyleTextureMipRule.PointNoMip, IsOptional: true));
                if (TryGetSlot(binding, "matcap", out int matcap) && matcap >= 0)
                {
                    var encoding = ParseMatcapEncoding(GetString(binding, "matcapEncoding") ?? "LinearRgb", out _);
                    requests.Add(new StyleSceneTextureRequest(matcap, encoding, StyleTextureMipRule.PointNoMip, IsOptional: true));
                }
                break;
        }
        return requests;
    }

    public override bool RequiresSourceGeometry(StyleMaterialBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var active = FilterBindings(context.Bindings);
        return active.Count == 1 && string.Equals(active[0].ModeId, WutheringWavesShaders.BodyModuleId, StringComparison.Ordinal);
    }

    public override StyleBoneTransformRequest? GetBoneTransformRequest(StyleMaterialBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var active = FilterBindings(context.Bindings);
        if (active.Count != 1)
            return null;

        var binding = active[0];
        if (!string.Equals(binding.ModeId, WutheringWavesShaders.FaceSdfModuleId, StringComparison.Ordinal))
            return null;

        if (binding.BoneBinding is { Mode: StyleBoneTrackingMode.SelectedBone } bone
            && !string.IsNullOrWhiteSpace(bone.BoneName))
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

        var binding = active[0];
        return binding.ModeId switch
        {
            WutheringWavesShaders.FaceSdfModuleId => BuildFaceSdfDrawData(context, binding),
            WutheringWavesShaders.HairModuleId => BuildHairDrawData(context, binding),
            WutheringWavesShaders.BodyModuleId => BuildBodyDrawData(context, binding),
            _ => new StyleDrawData(WutheringWavesShaders.DefaultModuleId)
        };
    }

    private StyleDrawData BuildFaceSdfDrawData(StyleDrawContext context, StyleMaterialBindingData binding)
    {
        if (context.FaceEyeDefaults is { FaceNormalShadeStrength: 0f })
            return new StyleDrawData(WutheringWavesShaders.DefaultModuleId);

        var primaryComposed = ResolveComposedTextureIndex(context, GetRequiredSlot(binding, "primary"));
        var (forward, right) = ResolveFaceBasis(context, binding.BoneBinding);
        var transition = (float)GetDouble(binding, "transition", 1.0);
        var offset = (float)GetDouble(binding, "offset", 1.0);

        var cb = new StyleConstantBufferBuilder()
            .Write(new Vector4(forward, 0f))
            .Write(new Vector4(right, 0f))
            .Write(new Vector4(transition, offset, 0f, 0f))
            .ToArray();

        return new StyleDrawData(
            ShaderModuleId: WutheringWavesShaders.FaceSdfModuleId,
            ConstantBufferData: cb,
            TextureBindings:
            [
                new StyleTextureBinding(0, primaryComposed, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip)
            ]);
    }

    private static StyleDrawData BuildHairDrawData(StyleDrawContext context, StyleMaterialBindingData binding)
    {
        var highlightComposed = ResolveComposedTextureIndex(context, GetRequiredSlot(binding, "highlight"));
        var shadowComposed = ResolveComposedTextureIndex(context, GetRequiredSlot(binding, "shadow"));
        var channel = GetString(binding, "highlightChannel") ?? "Blue";
        var highlightCoeff = (float)GetDouble(binding, "highlightCoefficient", 0.0);
        var highlightBright = (float)GetDouble(binding, "highlightBrightness", 2.0);
        var shadowCoeff = (float)GetDouble(binding, "shadowCoefficient", 0.5);
        var shadowSoftness = (float)GetDouble(binding, "shadowSoftness", 0.5);
        var intensity = (float)GetDouble(binding, "intensity", 0.1);

        var cb = new StyleConstantBufferBuilder()
            .Write(new Vector4(highlightCoeff, highlightBright, shadowCoeff, shadowSoftness))
            .Write(new Vector4(intensity, string.Equals(channel, "Alpha", StringComparison.Ordinal) ? 1f : 0f, 0f, 0f))
            .ToArray();

        return new StyleDrawData(
            ShaderModuleId: WutheringWavesShaders.HairModuleId,
            ConstantBufferData: cb,
            TextureBindings:
            [
                new StyleTextureBinding(0, highlightComposed, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new StyleTextureBinding(1, shadowComposed, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip)
            ]);
    }

    private static StyleDrawData BuildBodyDrawData(StyleDrawContext context, StyleMaterialBindingData binding)
    {
        var normalComposed = ResolveComposedTextureIndex(context, GetRequiredSlot(binding, "normal"));
        var detailComposed = ResolveComposedTextureIndex(context, GetRequiredSlot(binding, "detail"));
        var maskComposed = ResolveComposedTextureIndex(context, GetRequiredSlot(binding, "mask"));
        var matcapComposed = ResolveComposedTextureIndex(context, GetRequiredSlot(binding, "matcap"));
        var matcapEncoding = ParseMatcapEncoding(GetString(binding, "matcapEncoding") ?? "LinearRgb", out bool isBgr);

        int uvChannel = (int)Math.Round(GetDouble(binding, "uvChannel", 0.0));
        float normalStrength = (float)GetDouble(binding, "normalStrength", 1.0);
        float detailStrength = (float)GetDouble(binding, "detailStrength", 1.0);
        float metallicSaturation = (float)GetDouble(binding, "metallicSaturation", 1.0);
        float highlightCoeff = (float)GetDouble(binding, "highlightCoefficient", 0.0);
        float highlightStrength = (float)GetDouble(binding, "highlightStrength", 1.0);
        float rotationRadians = (float)GetDouble(binding, "rotationRadians", 0.0);
        float shadowCoeff = (float)GetDouble(binding, "shadowCoefficient", 0.5);
        float shadowSoftness = (float)GetDouble(binding, "shadowSoftness", 0.1);
        float shadowColorStrength = (float)GetDouble(binding, "shadowColorStrength", 1.0);
        float warmBandStrength = (float)GetDouble(binding, "warmBandStrength", 0.25);
        var shadowColor = GetVector3(binding, "shadowColor", DefaultBodyShadowColor);
        var rimColor = GetVector3(binding, "rimColor", DefaultBodyRimColor);
        float rimStrength = (float)GetDouble(binding, "rimStrength", 0.0);
        float rimSize = (float)GetDouble(binding, "rimSize", 0.75);
        float rimDirectionRadians = (float)GetDouble(binding, "rimDirectionRadians", 0.0);

        var cb = new StyleConstantBufferBuilder()
            .Write(new Vector4(detailStrength, metallicSaturation, highlightCoeff, highlightStrength))
            .Write(new Vector4(shadowCoeff, shadowSoftness, shadowColorStrength, warmBandStrength))
            .Write(new Vector4(shadowColor, 0f))
            .Write(new Vector4(rimColor, rimStrength))
            .Write(new Vector4(rimSize, rimDirectionRadians, normalStrength, rotationRadians))
            .Write(new Vector4(isBgr ? 1f : 0f, 0f, 0f, 0f))
            .ToArray();

        return new StyleDrawData(
            ShaderModuleId: WutheringWavesShaders.BodyModuleId,
            ConstantBufferData: cb,
            TextureBindings:
            [
                new StyleTextureBinding(0, normalComposed, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new StyleTextureBinding(1, detailComposed, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new StyleTextureBinding(2, maskComposed, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new StyleTextureBinding(3, matcapComposed, Encoding: matcapEncoding, MipRule: StyleTextureMipRule.PointNoMip)
            ],
            BufferBindings:
            [
                new StyleBufferBinding(0, StyleBufferKind.MikkTangentCorners, $"uv{uvChannel}")
            ]);
    }

    private StyleValidationResult ValidateFaceSdf(StyleMaterialValidationContext context, StyleMaterialBindingData binding)
    {
        if (context.ResolvedRole != StyleMaterialRole.Face)
            return Error("Dedicated FaceSDF requires an explicitly selected Face material role.",
                "专用 FaceSDF 需要材质角色显式指定为 Face。",
                "専用 FaceSDF には Face マテリアルロールの明示的な指定が必要です。");

        if (!TryValidateSlot(binding, "primary", context.SourceTextureCount, out var slotError))
            return slotError!;
        if (TryGetSlot(binding, "secondary", out int secondary) && secondary != -1)
            return Error("GrayRed FaceSDF has no secondary numerical image.",
                "GrayRed 编码的 FaceSDF 不接受次要数值贴图。",
                "GrayRed 形式の FaceSDF にセカンダリ数値テクスチャは指定できません。");

        var encoding = GetString(binding, "encoding") ?? "GrayRed";
        if (!string.Equals(encoding, "GrayRed", StringComparison.Ordinal))
            return Error($"Wuthering Waves FaceSDF only supports 'GrayRed' encoding, got '{encoding}'.",
                $"鸣潮 FaceSDF 仅支持 'GrayRed' 编码，当前为“{encoding}”。",
                $"鳴潮 FaceSDF は 'GrayRed' エンコーディングのみサポートします（指定値: '{encoding}'）。");

        var transition = GetDouble(binding, "transition", 1.0);
        if (!double.IsFinite(transition) || transition is < 0.001 or > 100.0)
            return Error("FaceSDF transition must be finite and in [0.001, 100].",
                "FaceSDF 过渡宽度必须为有限值且位于 [0.001, 100]。",
                "FaceSDF の遷移幅は [0.001, 100] の範囲の有限値である必要があります。");

        var offset = GetDouble(binding, "offset", 1.0);
        if (!double.IsFinite(offset) || offset is < -2.0 or > 2.0)
            return Error("FaceSDF offset must be finite and in [-2, 2].",
                "FaceSDF 偏移量必须为有限值且位于 [-2, 2]。",
                "FaceSDF オフセットは [-2, 2] の範囲の有限値である必要があります。");

        return ValidateBoneBinding(binding.BoneBinding);
    }

    private StyleValidationResult ValidateHair(StyleMaterialValidationContext context, StyleMaterialBindingData binding)
    {
        if (context.ResolvedRole != StyleMaterialRole.Hair)
            return Error("Dedicated Wuthering Waves hair requires an explicitly selected Hair material role.",
                "鸣潮专用头发模式需要材质角色显式指定为 Hair。",
                "鳴潮専用ヘアモードには Hair マテリアルロールの明示的な指定が必要です。");

        if (!TryValidateSlot(binding, "highlight", context.SourceTextureCount, out var err)
            || !TryValidateSlot(binding, "shadow", context.SourceTextureCount, out err))
            return err!;

        var channel = GetString(binding, "highlightChannel") ?? "Blue";
        if (channel is not ("Blue" or "Alpha"))
            return Error($"Unknown hair highlight channel '{channel}'. Expected 'Blue' or 'Alpha'.",
                $"未知的头发高光通道“{channel}”，应为 'Blue' 或 'Alpha'。",
                $"不明なヘアハイライトチャンネル '{channel}' です。'Blue' または 'Alpha' を指定してください。");

        if (!CheckRange(binding, "highlightCoefficient", 0.0, 0.0, 1.0, out err)
            || !CheckRange(binding, "highlightBrightness", 2.0, -1.0, 10.0, out err)
            || !CheckRange(binding, "shadowCoefficient", 0.5, 0.0, 1.0, out err)
            || !CheckRange(binding, "shadowSoftness", 0.5, 0.1, 1.0, out err)
            || !CheckRange(binding, "intensity", 0.1, 0.0, 16.0, out err))
            return err!;

        return StyleValidationResult.Ok;
    }

    private StyleValidationResult ValidateBody(StyleMaterialValidationContext context, StyleMaterialBindingData binding)
    {
        if (context.ResolvedRole is not (StyleMaterialRole.Body or StyleMaterialRole.Hair))
            return Error("Wuthering Waves Body MatCap requires an explicitly selected Body or Hair role.",
                "鸣潮身体 MatCap 模式需要材质角色为 Body 或 Hair。",
                "鳴潮ボディ MatCap モードには Body または Hair ロールが必要です。");

        if (!TryValidateSlot(binding, "normal", context.SourceTextureCount, out var err)
            || !TryValidateSlot(binding, "detail", context.SourceTextureCount, out err)
            || !TryValidateSlot(binding, "mask", context.SourceTextureCount, out err)
            || !TryValidateSlot(binding, "matcap", context.SourceTextureCount, out err))
            return err!;

        var matcapEncoding = GetString(binding, "matcapEncoding") ?? "LinearRgb";
        if (matcapEncoding is not ("LinearRgb" or "SrgbRgb" or "Srgb" or "LinearBgr" or "SrgbBgr"))
            return Error($"Unsupported MatCap encoding '{matcapEncoding}'.",
                $"不支持的 MatCap 编码“{matcapEncoding}”。",
                $"未対応の MatCap エンコーディング '{matcapEncoding}' です。");

        if (GetString(binding, "generatorRevision") is { } rev && !string.Equals(rev, "mikk-v1", StringComparison.Ordinal))
            return Error($"Unsupported tangent generator revision '{rev}'. Expected 'mikk-v1'.",
                $"不支持的切线生成器版本“{rev}”，应为 'mikk-v1'。",
                $"未対応のタンジェント生成リビジョン '{rev}' です。'mikk-v1' を指定してください。");

        var uvChannel = GetDouble(binding, "uvChannel", 0.0);
        if (!double.IsFinite(uvChannel) || uvChannel < 0 || uvChannel > 4 || Math.Abs(uvChannel - Math.Round(uvChannel)) > 1e-6)
            return Error("Body UV channel must be an integer in [0, 4].",
                "身体 UV 通道必须为 [0, 4] 之间的整数。",
                "ボディ UV チャンネルは [0, 4] の整数である必要があります。");

        if (!CheckRange(binding, "normalStrength", 1.0, 0.0, 8.0, out err)
            || !CheckRange(binding, "detailStrength", 1.0, 0.0, 1.0, out err)
            || !CheckRange(binding, "metallicSaturation", 1.0, 0.0, 4.0, out err)
            || !CheckRange(binding, "highlightCoefficient", 0.0, -1.0, 1.0, out err)
            || !CheckRange(binding, "highlightStrength", 1.0, 0.0, 1.0, out err)
            || !CheckRange(binding, "rotationRadians", 0.0, -65536.0, 65536.0, out err)
            || !CheckRange(binding, "shadowCoefficient", 0.5, 0.0, 1.0, out err)
            || !CheckRange(binding, "shadowSoftness", 0.1, 0.00001, 1.0, out err)
            || !CheckRange(binding, "shadowColorStrength", 1.0, 0.0, 1.0, out err)
            || !CheckRange(binding, "warmBandStrength", 0.25, 0.0, 1.0, out err)
            || !CheckRange(binding, "rimStrength", 0.0, 0.0, 1.0, out err)
            || !CheckRange(binding, "rimSize", 0.75, 0.0, 4.0, out err)
            || !CheckRange(binding, "rimDirectionRadians", 0.0, -65536.0, 65536.0, out err))
            return err!;

        if (!CheckColor3(binding, "shadowColor", 0f, 1f, out err)
            || !CheckColor3(binding, "rimColor", 0f, 1f, out err))
            return err!;

        return StyleValidationResult.Ok;
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

    private static StyleTextureEncoding ParseMatcapEncoding(string value, out bool isBgr)
    {
        isBgr = value is "LinearBgr" or "SrgbBgr";
        return value is "SrgbRgb" or "Srgb" or "SrgbBgr"
            ? StyleTextureEncoding.SrgbColor
            : StyleTextureEncoding.Linear;
    }

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

    private static int GetRequiredSlot(StyleMaterialBindingData binding, string key)
    {
        if (TryGetSlot(binding, key, out int slot) && slot >= 0)
            return slot;
        throw new InvalidOperationException($"Missing required texture slot '{key}' for mode '{binding.ModeId}'.");
    }

    private static bool TryValidateSlot(StyleMaterialBindingData binding, string key, int sourceTextureCount, out StyleValidationResult? error)
    {
        if (!TryGetSlot(binding, key, out int slot) || slot < 0 || slot >= sourceTextureCount)
        {
            error = Error($"Texture slot '{key}' must reference an existing texture index in [0, {sourceTextureCount - 1}].",
                $"贴图槽位“{key}”必须引用有效范围内的源贴图索引。",
                $"テクスチャスロット '{key}' は有効な範囲内のソーステクスチャインデックスを参照する必要があります。");
            return false;
        }
        error = null;
        return true;
    }

    private static double GetDouble(StyleMaterialBindingData binding, string key, double fallback)
        => binding.NumberValues is not null && binding.NumberValues.TryGetValue(key, out double val) ? val : fallback;

    private static string? GetString(StyleMaterialBindingData binding, string key)
        => binding.StringValues is not null && binding.StringValues.TryGetValue(key, out string? val) ? val : null;

    private static Vector3 GetVector3(StyleMaterialBindingData binding, string key, Vector3 fallback)
    {
        if (binding.VectorValues is not null && binding.VectorValues.TryGetValue(key, out var arr) && arr.Length >= 3)
            return new Vector3(arr[0], arr[1], arr[2]);
        return fallback;
    }

    private static bool CheckRange(StyleMaterialBindingData binding, string key, double defaultVal, double min, double max, out StyleValidationResult? error)
    {
        double val = GetDouble(binding, key, defaultVal);
        if (!double.IsFinite(val) || val < min || val > max)
        {
            error = Error($"Parameter '{key}' must be finite and in [{min}, {max}].",
                $"参数“{key}”必须为有限值且位于 [{min}, {max}]。",
                $"パラメータ '{key}' は [{min}, {max}] の範囲の有限値である必要があります。");
            return false;
        }
        error = null;
        return true;
    }

    private static bool CheckColor3(StyleMaterialBindingData binding, string key, float min, float max, out StyleValidationResult? error)
    {
        if (binding.VectorValues is null || !binding.VectorValues.TryGetValue(key, out var arr))
        {
            error = null;
            return true;
        }
        if (arr.Length != 3
            || !float.IsFinite(arr[0]) || arr[0] < min || arr[0] > max
            || !float.IsFinite(arr[1]) || arr[1] < min || arr[1] > max
            || !float.IsFinite(arr[2]) || arr[2] < min || arr[2] > max)
        {
            error = Error($"Vector parameter '{key}' must contain 3 finite values in [{min}, {max}].",
                $"颜色向量“{key}”必须包含 3 个位于 [{min}, {max}] 的有限数值。",
                $"ベクトルパラメータ '{key}' は [{min}, {max}] の範囲の 3 つの有限値である必要があります。");
            return false;
        }
        error = null;
        return true;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static StyleValidationResult Error(string en, string zhCn, string ja) =>
        StyleValidationResult.Error(LocalizedText.From(en, zhCn: zhCn, en: en, ja: ja));

    private static IReadOnlyList<StyleMaterialModeDescriptor> CreateMaterialModes() =>
    [
        new StyleMaterialModeDescriptor(
            ModeId: WutheringWavesShaders.FaceSdfModuleId,
            DisplayName: LocalizedText.From("Face SDF (GrayRed)", zhCn: "面部 SDF（灰度）", en: "Face SDF (GrayRed)", ja: "顔 SDF（グレースケール）"),
            AllowedRoles: [StyleMaterialRole.Face],
            Parameters:
            [
                new("transition", LocalizedText.From("Transition", zhCn: "过渡宽度", en: "Transition", ja: "遷移幅"), StyleParameterKind.Float,
                    DefaultNumber: 1.0, MinNumber: 0.001, MaxNumber: 100.0, Step: 0.01),
                new("offset", LocalizedText.From("Offset", zhCn: "阴影偏移", en: "Offset", ja: "オフセット"), StyleParameterKind.Float,
                    DefaultNumber: 1.0, MinNumber: -2.0, MaxNumber: 2.0, Step: 0.01),
                new("encoding", LocalizedText.From("Encoding", zhCn: "编码格式", en: "Encoding", ja: "エンコーディング"), StyleParameterKind.Enum,
                    DefaultString: "GrayRed", EnumOptions: [new("GrayRed", LocalizedText.From("GrayRed"))])
            ],
            TextureInputs:
            [
                new("primary", LocalizedText.From("Face SDF Map", zhCn: "面部 SDF 贴图", en: "Face SDF Map", ja: "顔 SDF マップ"),
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
            ModeId: WutheringWavesShaders.HairModuleId,
            DisplayName: LocalizedText.From("Hair Specular Mask", zhCn: "头发高光遮罩", en: "Hair Specular Mask", ja: "ヘアスペキュラマスク"),
            AllowedRoles: [StyleMaterialRole.Hair],
            Parameters:
            [
                new("highlightChannel", LocalizedText.From("Highlight Channel", zhCn: "高光通道", en: "Highlight Channel", ja: "ハイライトチャンネル"),
                    StyleParameterKind.Enum, DefaultString: "Blue",
                    EnumOptions: [new("Blue", LocalizedText.From("Blue")), new("Alpha", LocalizedText.From("Alpha"))]),
                new("highlightCoefficient", LocalizedText.From("Highlight Coefficient", zhCn: "高光系数", en: "Highlight Coefficient", ja: "ハイライト係数"),
                    StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: 0.0, MaxNumber: 1.0, Step: 0.01),
                new("highlightBrightness", LocalizedText.From("Highlight Brightness", zhCn: "高光亮度", en: "Highlight Brightness", ja: "ハイライト明度"),
                    StyleParameterKind.Float, DefaultNumber: 2.0, MinNumber: -1.0, MaxNumber: 10.0, Step: 0.05),
                new("shadowCoefficient", LocalizedText.From("Shadow Coefficient", zhCn: "阴影系数", en: "Shadow Coefficient", ja: "シャドウ係数"),
                    StyleParameterKind.Float, DefaultNumber: 0.5, MinNumber: 0.0, MaxNumber: 1.0, Step: 0.01),
                new("shadowSoftness", LocalizedText.From("Shadow Softness", zhCn: "阴影柔和度", en: "Shadow Softness", ja: "シャドウソフトネス"),
                    StyleParameterKind.Float, DefaultNumber: 0.5, MinNumber: 0.1, MaxNumber: 1.0, Step: 0.01),
                new("intensity", LocalizedText.From("Intensity", zhCn: "强度", en: "Intensity", ja: "強度"),
                    StyleParameterKind.Float, DefaultNumber: 0.1, MinNumber: 0.0, MaxNumber: 16.0, Step: 0.05)
            ],
            TextureInputs:
            [
                new("highlight", LocalizedText.From("Highlight Mask", zhCn: "高光遮罩贴图", en: "Highlight Mask", ja: "ハイライトマスク"),
                    Required: true, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new("shadow", LocalizedText.From("Shadow Mask", zhCn: "阴影遮罩贴图", en: "Shadow Mask", ja: "シャドウマスク"),
                    Required: true, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip)
            ]),
        new StyleMaterialModeDescriptor(
            ModeId: WutheringWavesShaders.BodyModuleId,
            DisplayName: LocalizedText.From("Body MatCap", zhCn: "身体 MatCap", en: "Body MatCap", ja: "ボディ MatCap"),
            AllowedRoles: [StyleMaterialRole.Body, StyleMaterialRole.Hair],
            Parameters:
            [
                new("matcapEncoding", LocalizedText.From("MatCap Encoding", zhCn: "MatCap 色彩编码", en: "MatCap Encoding", ja: "MatCap エンコーディング"),
                    StyleParameterKind.Enum, DefaultString: "LinearRgb",
                    EnumOptions: [new("LinearRgb", LocalizedText.From("Linear RGB")), new("SrgbRgb", LocalizedText.From("sRGB")), new("Srgb", LocalizedText.From("sRGB (Legacy)"))]),
                new("uvChannel", LocalizedText.From("UV Channel", zhCn: "UV 通道", en: "UV Channel", ja: "UV チャンネル"),
                    StyleParameterKind.Int, DefaultNumber: 0, MinNumber: 0, MaxNumber: 4, Step: 1),
                new("normalStrength", LocalizedText.From("Normal Strength", zhCn: "法线强度", en: "Normal Strength", ja: "法線強度"),
                    StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: 0.0, MaxNumber: 8.0, Step: 0.05),
                new("detailStrength", LocalizedText.From("Detail Strength", zhCn: "细节强度", en: "Detail Strength", ja: "ディテール強度"),
                    StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: 0.0, MaxNumber: 1.0, Step: 0.01),
                new("metallicSaturation", LocalizedText.From("Metallic Saturation", zhCn: "金属饱和度", en: "Metallic Saturation", ja: "メタリック彩度"),
                    StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: 0.0, MaxNumber: 4.0, Step: 0.05),
                new("highlightCoefficient", LocalizedText.From("Highlight Coefficient", zhCn: "高光系数", en: "Highlight Coefficient", ja: "ハイライト係数"),
                    StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: -1.0, MaxNumber: 1.0, Step: 0.01),
                new("highlightStrength", LocalizedText.From("Highlight Strength", zhCn: "高光强度", en: "Highlight Strength", ja: "ハイライト強度"),
                    StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: 0.0, MaxNumber: 1.0, Step: 0.01),
                new("rotationRadians", LocalizedText.From("MatCap Rotation (rad)", zhCn: "MatCap 旋转（弧度）", en: "MatCap Rotation (rad)", ja: "MatCap 回転（ラジアン）"),
                    StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: -6.283185, MaxNumber: 6.283185, Step: 0.05),
                new("shadowCoefficient", LocalizedText.From("Shadow Coefficient", zhCn: "阴影系数", en: "Shadow Coefficient", ja: "シャドウ係数"),
                    StyleParameterKind.Float, DefaultNumber: 0.5, MinNumber: 0.0, MaxNumber: 1.0, Step: 0.01),
                new("shadowSoftness", LocalizedText.From("Shadow Softness", zhCn: "阴影柔和度", en: "Shadow Softness", ja: "シャドウソフトネス"),
                    StyleParameterKind.Float, DefaultNumber: 0.1, MinNumber: 0.001, MaxNumber: 1.0, Step: 0.01),
                new("shadowColorStrength", LocalizedText.From("Shadow Color Strength", zhCn: "阴影色强度", en: "Shadow Color Strength", ja: "シャドウカラー強度"),
                    StyleParameterKind.Float, DefaultNumber: 1.0, MinNumber: 0.0, MaxNumber: 1.0, Step: 0.01),
                new("warmBandStrength", LocalizedText.From("Warm Band Strength", zhCn: "暖边强度", en: "Warm Band Strength", ja: "ウォームバンド強度"),
                    StyleParameterKind.Float, DefaultNumber: 0.25, MinNumber: 0.0, MaxNumber: 1.0, Step: 0.01),
                new("shadowColor", LocalizedText.From("Shadow Color", zhCn: "阴影颜色", en: "Shadow Color", ja: "シャドウカラー"),
                    StyleParameterKind.Color3, DefaultVector: [DefaultBodyShadowColor.X, DefaultBodyShadowColor.Y, DefaultBodyShadowColor.Z]),
                new("rimColor", LocalizedText.From("Rim Color", zhCn: "边缘光颜色", en: "Rim Color", ja: "リムカラー"),
                    StyleParameterKind.Color3, DefaultVector: [DefaultBodyRimColor.X, DefaultBodyRimColor.Y, DefaultBodyRimColor.Z]),
                new("rimStrength", LocalizedText.From("Rim Strength", zhCn: "边缘光强度", en: "Rim Strength", ja: "リム強度"),
                    StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: 0.0, MaxNumber: 1.0, Step: 0.01),
                new("rimSize", LocalizedText.From("Rim Size", zhCn: "边缘光范围", en: "Rim Size", ja: "リムサイズ"),
                    StyleParameterKind.Float, DefaultNumber: 0.75, MinNumber: 0.0, MaxNumber: 4.0, Step: 0.05),
                new("rimDirectionRadians", LocalizedText.From("Rim Direction (rad)", zhCn: "边缘光方向（弧度）", en: "Rim Direction (rad)", ja: "リム方向（ラジアン）"),
                    StyleParameterKind.Float, DefaultNumber: 0.0, MinNumber: -6.283185, MaxNumber: 6.283185, Step: 0.05)
            ],
            TextureInputs:
            [
                new("normal", LocalizedText.From("Normal Map", zhCn: "法线贴图", en: "Normal Map", ja: "法線マップ"),
                    Required: true, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new("detail", LocalizedText.From("Detail Map", zhCn: "细节贴图", en: "Detail Map", ja: "ディテールマップ"),
                    Required: true, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new("mask", LocalizedText.From("Mask Map", zhCn: "遮罩贴图", en: "Mask Map", ja: "マスクマップ"),
                    Required: true, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip),
                new("matcap", LocalizedText.From("MatCap Map", zhCn: "MatCap 贴图", en: "MatCap Map", ja: "MatCap マップ"),
                    Required: true, Encoding: StyleTextureEncoding.Linear, MipRule: StyleTextureMipRule.PointNoMip)
            ])
    ];
}
