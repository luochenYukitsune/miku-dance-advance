using System.Collections.ObjectModel;
using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;

namespace Mmd.Modern.Tests;

internal static class T8cTestHelpers
{
    public const string MmdStyleId = D3D12SurfaceStyleMap.DefaultStyleId;
    public const string GenshinStyleId = "genshin";
    public const string StarRailStyleId = "star-rail";
    public const string WutheringStyleId = "wuthering-waves";
    public const string EndfieldStyleId = "endfield";

    public static readonly string[] ModernStyleIds =
    [
        GenshinStyleId,
        StarRailStyleId,
        WutheringStyleId,
        EndfieldStyleId,
    ];

    public static IEnumerable<object[]> SurfaceVariants()
    {
        foreach (bool native in new[] { false, true })
        foreach (bool gi in new[] { false, true })
        foreach (var variant in new[] { (Morph: false, Sub: false), (Morph: true, Sub: false), (Morph: true, Sub: true) })
            yield return [native ? (gi ? "PSSurfaceGiNative" : "PSSurfaceNative") : (gi ? "PSSurfaceGi" : "PSSurface"), native, gi, variant.Morph, variant.Sub];
    }

    public static OriginalFrameScene FrameWithTextures(
        SceneTexture? texture = null,
        bool seeds = true,
        Vector3? seed = null,
        SceneTexture[]? textures = null,
        Matrix4x4? world = null)
    {
        SceneVertex[] vertices =
        [
            new(new(-1.5f, -1.1f, 0), -Vector3.UnitZ, new(0, 1)),
            new(new(1.5f, -1.1f, 0), -Vector3.UnitZ, new(1, 1)),
            new(new(1.5f, 1.1f, 0), -Vector3.UnitZ, new(1, 0)),
            new(new(-1.5f, 1.1f, 0), -Vector3.UnitZ, new(0, 0)),
        ];
        SceneTexture defaultTex = texture ?? new("explicit green mask", 1, 1, [90, 255, 12, 0]);
        var snapshot = new SceneSnapshot(
            vertices,
            [0, 2, 1, 0, 3, 2],
            [new("explicit radial receiver", 0, 6, new(.25f, .18f, .12f, 1), Vector3.Zero, Vector3.Zero, 10)],
            textures ?? [defaultTex],
            world: world,
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 4, .1f, 30),
            lightDirection: -Vector3.UnitZ,
            rawRadialSeeds: seeds ? new SceneRawRadialSeeds(Enumerable.Repeat(seed ?? Vector3.UnitX, 4).ToArray()) : null);
        return new(
            new(snapshot, [new(new(.22f), DoubleSided: true)], new(Vector3.One, Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: new(0, 0, 0, 1)),
            materials: [new(false, false, false)]);
    }

    public static D3D12PluginShaderModule GetD3D12ShaderModule(string styleId, string moduleId = "default")
    {
        var style = TestStylePlugins.GetStyle(styleId);
        var module = style.GetShaderModules().Single(m => string.Equals(m.ModuleId, moduleId, StringComparison.Ordinal));
        return D3D12StylePluginAdapter.ToD3D12ShaderModule(module);
    }

    public static byte[] CompilePluginSurface(
        string styleId,
        string moduleId,
        string entryPoint,
        bool nativeLights,
        bool gi,
        bool textureMorph,
        bool subTexture,
        bool characterToon = false,
        bool preserveDirectLightPalette = false)
    {
        var module = GetD3D12ShaderModule(styleId, moduleId);
        var hooks = D3D12StyleShaderAssembler.ResolveEffectiveHooks(module, null);
        var geometry = D3D12StyleShaderAssembler.ResolveEffectiveGeometry(module, subTexture);
        return D3D12StyleShaderAssembler.CompilePixel(
            styleId,
            entryPoint,
            module,
            hooks,
            geometry,
            nativeLights,
            gi,
            textureMorph,
            subTexture,
            characterToon,
            preserveDirectLightPalette);
    }

    public static string AssemblePluginSurfaceSource(
        string styleId,
        string moduleId,
        string entryPoint,
        bool nativeLights,
        bool gi,
        bool textureMorph,
        bool subTexture,
        bool characterToon = false,
        bool preserveDirectLightPalette = false)
    {
        var module = GetD3D12ShaderModule(styleId, moduleId);
        var hooks = D3D12StyleShaderAssembler.ResolveEffectiveHooks(module, null);
        var geometry = D3D12StyleShaderAssembler.ResolveEffectiveGeometry(module, subTexture);
        bool directPalette = nativeLights && (preserveDirectLightPalette || hooks.HasFlag(D3D12StyleShaderHookMask.PreserveDirectLightPalette));
        return D3D12StyleShaderAssembler.AssemblePixelSource(
            entryPoint,
            module,
            hooks,
            geometry,
            nativeLights,
            gi,
            textureMorph || subTexture,
            subTexture,
            characterToon,
            directPalette);
    }

    public static byte[] CompilePluginVertex(
        string styleId,
        string moduleId,
        bool nativeLights,
        bool gi,
        bool subTexture)
    {
        var module = GetD3D12ShaderModule(styleId, moduleId);
        var geometry = D3D12StyleShaderAssembler.ResolveEffectiveGeometry(module, subTexture);
        return D3D12StyleShaderAssembler.CompileVertex(module, geometry, nativeLights, gi, subTexture);
    }

    public static D3D12PmxMaterialTreatment? GetDefaultTreatment(string styleId, D3D12CharacterMaterialRole role)
    {
        if (string.Equals(styleId, MmdStyleId, StringComparison.Ordinal)
            || role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair))
            return null;
        var style = TestStylePlugins.GetStyle(styleId);
        var contractRole = D3D12StylePluginAdapter.ToContractRole(role);
        if (!style.DefaultRoleTreatments.TryGetValue(contractRole, out var raw))
            return null;
        var converted = D3D12StylePluginAdapter.ToD3D12Treatment(raw);
        return D3D12StylePluginAdapter.IsIdentityTreatment(converted) ? null : converted;
    }

    public static D3D12SurfaceStyleMap ApplyPluginDefaults(D3D12SurfaceStyleMap map, string styleId)
    {
        if (string.Equals(styleId, MmdStyleId, StringComparison.Ordinal))
            return map;
        return D3D12PmxMaterialTreatmentCatalog.ApplyDefaults(
            map,
            GetDefaultTreatment(styleId, D3D12CharacterMaterialRole.Body),
            GetDefaultTreatment(styleId, D3D12CharacterMaterialRole.Hair));
    }

    public static StyleValidationResult ValidateMaterialBinding(
        string styleId,
        StyleMaterialBindingData? binding,
        D3D12CharacterMaterialRole role = D3D12CharacterMaterialRole.Body,
        int sourceTextureCount = int.MaxValue,
        string? materialName = null)
    {
        var style = TestStylePlugins.GetStyle(styleId);
        var contractRole = D3D12StylePluginAdapter.ToContractRole(role);
        IReadOnlyList<StyleMaterialBindingData> list = binding is null ? [] : [binding];
        return style.ValidateMaterial(new StyleMaterialValidationContext(
            Guid.Empty,
            0,
            materialName,
            contractRole,
            list,
            sourceTextureCount));
    }

    public static D3D12PluginDrawData? BuildPluginDraw(
        string styleId,
        StyleMaterialBindingData? binding,
        D3D12CharacterMaterialRole role = D3D12CharacterMaterialRole.Body,
        float faceNormalShadeStrength = .35f,
        float faceGeometricShadowStrength = 1f,
        float eyeGeometricShadowStrength = .2f,
        OriginalFrameScene? frame = null,
        StyleEvaluatedBoneTransform? evaluatedBone = null,
        bool buildWhenNull = false)
    {
        if (string.Equals(styleId, MmdStyleId, StringComparison.Ordinal))
            return null;
        if (binding is null && !buildWhenNull)
            return null;

        var style = TestStylePlugins.GetStyle(styleId);
        var contractRole = D3D12StylePluginAdapter.ToContractRole(role);
        IReadOnlyList<StyleSceneTextureInfo>? resolvedTextures = null;
        if (frame is not null)
        {
            var textures = frame.Surface.Snapshot.Textures;
            var list = new StyleSceneTextureInfo[textures.Count];
            for (int i = 0; i < textures.Count; i++)
                list[i] = new StyleSceneTextureInfo(i, i, textures[i].Width, textures[i].Height);
            resolvedTextures = list;
        }

        IReadOnlyList<StyleMaterialBindingData> bindings = binding is null ? [] : [binding];
        var context = new StyleDrawContext(
            ObjectId: Guid.Empty,
            MaterialIndex: 0,
            MaterialName: null,
            ResolvedRole: contractRole,
            Bindings: bindings,
            BaseDiffuseColor: Vector4.One,
            BaseSpecularColor: Vector3.Zero,
            BaseSpecularPower: 1f,
            BaseAmbientColor: Vector3.Zero,
            BaseTextureIndex: -1,
            SphereTextureIndex: -1,
            SphereMode: 0,
            ToonTextureIndex: -1,
            SubTextureIndex: -1,
            IsTransparent: false,
            PrimaryLightDirection: -Vector3.UnitZ,
            PrimaryLightColor: Vector3.One,
            CameraPosition: new Vector3(0, 0, -5),
            StylizedShading: null,
            Treatment: null,
            FaceEyeDefaults: new StyleFaceEyeDefaults(
                faceNormalShadeStrength,
                faceGeometricShadowStrength,
                eyeGeometricShadowStrength),
            ResolvedTextures: resolvedTextures,
            EvaluatedBone: evaluatedBone);

        var raw = style.BuildMaterialDrawData(context);
        return raw is null ? null : D3D12StylePluginAdapter.ToD3D12DrawData(raw);
    }

    public static D3D12CharacterMaterialStyle MaterialWithBinding(
        string styleId,
        StyleMaterialBindingData? binding,
        D3D12CharacterMaterialRole role = D3D12CharacterMaterialRole.Body,
        float faceNormalShadeStrength = .35f,
        float faceGeometricShadowStrength = 1f,
        float eyeGeometricShadowStrength = .2f,
        D3D12PmxMaterialTreatment? pmxMaterialTreatment = null,
        D3D12StylizedShading? stylizedShading = null,
        CharacterToonResponse? toonResponse = null,
        bool preserveDirectLightPalette = false,
        OriginalFrameScene? frame = null,
        bool validateWithPlugin = false,
        bool buildWhenNull = false)
    {
        if (validateWithPlugin && binding is not null && !string.Equals(styleId, MmdStyleId, StringComparison.Ordinal))
        {
            var validation = ValidateMaterialBinding(
                styleId,
                binding,
                role,
                frame?.Surface.Snapshot.Textures.Count ?? int.MaxValue);
            if (!validation.IsValid)
                throw new ArgumentException(validation.ErrorMessage ?? "Invalid plugin material binding.", nameof(binding));
        }

        D3D12PluginDrawData? pluginDraw = BuildPluginDraw(
            styleId,
            binding,
            role,
            faceNormalShadeStrength,
            faceGeometricShadowStrength,
            eyeGeometricShadowStrength,
            frame,
            buildWhenNull: buildWhenNull);

        return new D3D12CharacterMaterialStyle(
            role,
            faceNormalShadeStrength,
            faceGeometricShadowStrength,
            eyeGeometricShadowStrength,
            pmxMaterialTreatment,
            stylizedShading,
            toonResponse,
            preserveDirectLightPalette,
            pluginDraw);
    }

    public static D3D12SurfaceStyleMap CreateDirectMap(
        IEnumerable<string> styleIds,
        IEnumerable<D3D12CharacterMaterialStyle>? characterMaterials = null)
        => new(styleIds, characterMaterials, TestStylePlugins.AllPresetCatalog);

    public static D3D12SurfaceStyleMap CreateSingleBindingMap(
        string styleId,
        StyleMaterialBindingData? binding,
        D3D12CharacterMaterialRole role = D3D12CharacterMaterialRole.Body,
        float faceNormalShadeStrength = .35f,
        float faceGeometricShadowStrength = 1f,
        float eyeGeometricShadowStrength = .2f,
        OriginalFrameScene? frame = null)
        => CreateDirectMap(
            [styleId],
            [MaterialWithBinding(styleId, binding, role, faceNormalShadeStrength, faceGeometricShadowStrength, eyeGeometricShadowStrength, frame: frame)]);

    public static StyleMaterialBindingData FaceSdfBinding(
        string styleId,
        int primaryTextureSlot,
        int secondaryTextureSlot = -1,
        Vector3? forward = null,
        Vector3? right = null,
        float transition = 1f,
        float offset = 1f,
        string? encoding = null,
        StyleBoneTrackingMode trackingMode = StyleBoneTrackingMode.FixedWorld,
        string? boneName = null,
        Vector3? localForward = null,
        Vector3? localUp = null)
    {
        var slots = new Dictionary<string, int>(StringComparer.Ordinal) { ["primary"] = primaryTextureSlot };
        if (secondaryTextureSlot >= 0)
            slots["secondary"] = secondaryTextureSlot;

        string resolvedEncoding = encoding ?? styleId switch
        {
            GenshinStyleId => "GenshinSoftFace",
            EndfieldStyleId => "EndfieldRgbCmM",
            _ => "GrayRed",
        };

        var fwd = forward ?? -Vector3.UnitZ;
        var rt = right ?? Vector3.UnitX;
        var boneBinding = trackingMode == StyleBoneTrackingMode.SelectedBone
            ? new StyleBoneAxisBinding(
                StyleBoneTrackingMode.SelectedBone,
                boneName,
                localForward ?? -Vector3.UnitZ,
                localUp ?? Vector3.UnitY,
                fwd,
                rt)
            : new StyleBoneAxisBinding(StyleBoneTrackingMode.FixedWorld, FixedWorldForward: fwd, FixedWorldRight: rt);

        return new StyleMaterialBindingData(
            styleId,
            "face-sdf",
            new ReadOnlyDictionary<string, int>(slots),
            Dict(("transition", transition), ("offset", offset)),
            StringValues: SingleString("encoding", resolvedEncoding),
            BoneBinding: boneBinding);
    }

    public static StyleMaterialBindingData GenshinSoftFaceBinding(
        int primaryTextureSlot,
        int combinedTextureSlot,
        Vector3? forward = null,
        Vector3? right = null,
        float transition = 0.001f,
        float offset = 0.55f)
        => FaceSdfBinding(GenshinStyleId, primaryTextureSlot, combinedTextureSlot, forward, right, transition, offset, "GenshinSoftFace");

    public static StyleMaterialBindingData EndfieldFaceSdfBinding(
        int primaryTextureSlot,
        int secondaryTextureSlot,
        Vector3? forward = null,
        Vector3? right = null,
        float transition = 0.1f,
        float offset = 1f)
        => FaceSdfBinding(EndfieldStyleId, primaryTextureSlot, secondaryTextureSlot, forward, right, transition, offset, "EndfieldRgbCmM");

    public static StyleMaterialBindingData StarMcHairBinding(
        string styleId,
        int highlightSlot,
        int shadowSlot,
        string highlightChannel = "Blue",
        float highlightCoefficient = 0f,
        float highlightBrightness = 2f,
        float shadowCoefficient = 0.5f,
        float shadowSoftness = 0.5f,
        float intensity = 0.1f)
        => new(
            styleId,
            "hair",
            new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["highlight"] = highlightSlot,
                ["shadow"] = shadowSlot,
            }),
            Dict(
                ("highlightCoefficient", highlightCoefficient),
                ("highlightBrightness", highlightBrightness),
                ("shadowCoefficient", shadowCoefficient),
                ("shadowSoftness", shadowSoftness),
                ("intensity", intensity)),
            StringValues: SingleString("highlightChannel", highlightChannel));

    public static StyleMaterialBindingData StarMcBodyBinding(
        string styleId,
        int normalSlot,
        int detailSlot,
        int maskSlot,
        int matcapSlot,
        string matcapEncoding = "LinearRgb",
        int uvChannel = 0,
        float normalStrength = 1f,
        float detailStrength = 1f,
        float metallicSaturation = 1f,
        float highlightCoefficient = 0f,
        float highlightStrength = 1f,
        float rotationRadians = 0f,
        float shadowCoefficient = 0.5f,
        float shadowSoftness = 0.1f,
        float shadowColorStrength = 1f,
        float warmBandStrength = 0.25f,
        Vector3? shadowColor = null,
        Vector3? rimColor = null,
        float rimStrength = 0f,
        float rimSize = 0.75f,
        float rimDirectionRadians = 0f,
        string? generatorRevision = "mikk-v1")
    {
        var slots = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["normal"] = normalSlot,
            ["detail"] = detailSlot,
            ["mask"] = maskSlot,
            ["matcap"] = matcapSlot,
        };
        var numbers = Dict(
            ("uvChannel", uvChannel),
            ("normalStrength", normalStrength),
            ("detailStrength", detailStrength),
            ("metallicSaturation", metallicSaturation),
            ("highlightCoefficient", highlightCoefficient),
            ("highlightStrength", highlightStrength),
            ("rotationRadians", rotationRadians),
            ("shadowCoefficient", shadowCoefficient),
            ("shadowSoftness", shadowSoftness),
            ("shadowColorStrength", shadowColorStrength),
            ("warmBandStrength", warmBandStrength),
            ("rimStrength", rimStrength),
            ("rimSize", rimSize),
            ("rimDirectionRadians", rimDirectionRadians));
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal)
        {
            ["shadowColor"] = Vec(shadowColor ?? new Vector3(.62f, .66f, .72f)),
            ["rimColor"] = Vec(rimColor ?? new Vector3(.85f, .9f, 1f)),
        };
        var strings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["matcapEncoding"] = matcapEncoding,
        };
        if (generatorRevision is not null)
            strings["generatorRevision"] = generatorRevision;

        return new StyleMaterialBindingData(
            styleId,
            "body",
            new ReadOnlyDictionary<string, int>(slots),
            numbers,
            VectorValues: new ReadOnlyDictionary<string, float[]>(vectors),
            StringValues: new ReadOnlyDictionary<string, string>(strings));
    }

    public static StyleMaterialBindingData EndfieldHairBinding(
        int textureSlot,
        Vector3? paletteColor = null,
        float brightness = 0f)
        => new(
            EndfieldStyleId,
            "hair",
            SingleSlot("texture", textureSlot),
            Dict(("brightness", brightness)),
            VectorValues: new ReadOnlyDictionary<string, float[]>(
                new Dictionary<string, float[]>(StringComparer.Ordinal)
                {
                    ["paletteColor"] = Vec(paletteColor ?? Vector3.One),
                }));

    public static StyleMaterialBindingData EndfieldRimBinding(
        Vector3 baseColor,
        Vector3 rimColor,
        float edgeSize = .2f,
        float edgeStrength = 0f,
        float directionRadians = 5.5f,
        float rangeStrength = 1f,
        float radialStrength = 0f)
        => new(
            EndfieldStyleId,
            "rim",
            NumberValues: Dict(
                ("edgeSize", edgeSize),
                ("edgeStrength", edgeStrength),
                ("directionRadians", directionRadians),
                ("rangeStrength", rangeStrength),
                ("radialStrength", radialStrength)),
            VectorValues: new ReadOnlyDictionary<string, float[]>(
                new Dictionary<string, float[]>(StringComparer.Ordinal)
                {
                    ["baseColor"] = Vec(baseColor),
                    ["rimColor"] = Vec(rimColor),
                }));

    public static StyleMaterialBindingData EndfieldMatcapBinding(
        int normalSlot,
        int pSlot,
        int matcapSlot,
        string matcapEncoding = "LinearRgb",
        int uvChannel = 0,
        Vector3? baseColor = null,
        Vector3? highlightColor = null,
        float normalStrength = 2f,
        float rotationRadians = 0f,
        float highlightCoefficient = 0f,
        float shadowHardness = .5f,
        Vector3? lightControlDirection = null,
        float baselineStrength = 1f,
        bool coupleEndfieldPalette = false,
        string generatorRevision = "mikk-v1")
    {
        var slots = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["normal"] = normalSlot,
            ["p"] = pSlot,
            ["matcap"] = matcapSlot,
        };
        var numbers = Dict(
            ("uvChannel", uvChannel),
            ("normalStrength", normalStrength),
            ("rotationRadians", rotationRadians),
            ("highlightCoefficient", highlightCoefficient),
            ("shadowHardness", shadowHardness),
            ("baselineStrength", baselineStrength));
        var bools = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["coupleEndfieldPalette"] = coupleEndfieldPalette,
        };
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal)
        {
            ["baseColor"] = Vec(baseColor ?? new Vector3(.5f)),
            ["highlightColor"] = Vec(highlightColor ?? Vector3.Zero),
        };
        if (lightControlDirection is { } ldir)
            vectors["lightControlDirection"] = Vec(ldir);
        var strings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["matcapEncoding"] = matcapEncoding,
            ["generatorRevision"] = generatorRevision,
        };
        return new StyleMaterialBindingData(
            EndfieldStyleId,
            "matcap",
            new ReadOnlyDictionary<string, int>(slots),
            numbers,
            new ReadOnlyDictionary<string, bool>(bools),
            new ReadOnlyDictionary<string, float[]>(vectors),
            new ReadOnlyDictionary<string, string>(strings));
    }

    public static StyleMaterialBindingData EndfieldPaletteBinding(
        Vector3 receivedLightLinearRgb,
        float emissionStrength = 0f,
        float uniformEmissionBlue = 0f,
        float lightInfluence = 1f,
        float density = 0f,
        string emissionInputMode = "Uniform",
        int emissionSlot = -1,
        string receivedLightInputMode = "RendererDiffuse",
        int receivedLightSlot = -1)
    {
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);
        if (emissionSlot >= 0) slots["paletteEmission"] = emissionSlot;
        if (receivedLightSlot >= 0) slots["paletteReceivedLight"] = receivedLightSlot;
        return new StyleMaterialBindingData(
            EndfieldStyleId,
            "palette",
            slots.Count == 0 ? null : new ReadOnlyDictionary<string, int>(slots),
            Dict(
                ("paletteEmissionStrength", emissionStrength),
                ("paletteUniformEmissionBlue", uniformEmissionBlue),
                ("paletteLightInfluence", lightInfluence),
                ("paletteDensity", density)),
            VectorValues: new ReadOnlyDictionary<string, float[]>(
                new Dictionary<string, float[]>(StringComparer.Ordinal)
                {
                    ["paletteReceivedLightLinearRgb"] = Vec(receivedLightLinearRgb),
                }),
            StringValues: new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["paletteEmissionInputMode"] = emissionInputMode,
                    ["paletteReceivedLightInputMode"] = receivedLightInputMode,
                }));
    }

    public static StyleMaterialBindingData EndfieldHighlightRangeBinding(
        float overallBrightness,
        float highlightBrightness,
        Vector3 directionalShadowWorldDirection,
        float directionalShadowWidth = .3f,
        float directionalShadowNormalStrength = 2f)
        => new(
            EndfieldStyleId,
            "highlight-range",
            NumberValues: Dict(
                ("highlightRangeOverallBrightness", overallBrightness),
                ("highlightRangeHighlightBrightness", highlightBrightness),
                ("highlightRangeDirectionalShadowWidth", directionalShadowWidth),
                ("highlightRangeDirectionalShadowNormalStrength", directionalShadowNormalStrength)),
            BoolValues: new ReadOnlyDictionary<string, bool>(
                new Dictionary<string, bool>(StringComparer.Ordinal)
                {
                    ["hasHighlightRangeDirectionalShadow"] = true,
                }),
            VectorValues: new ReadOnlyDictionary<string, float[]>(
                new Dictionary<string, float[]>(StringComparer.Ordinal)
                {
                    ["highlightRangeDirectionalShadowWorldDirection"] = Vec(directionalShadowWorldDirection),
                }));

    public static (Vector3 Forward, Vector3 Right, float Transition, float Offset) ReadFaceSdfConstants(D3D12PluginDrawData draw)
    {
        var floats = MemoryMarshal.Cast<byte, float>(draw.ConstantBufferData.Span);
        return (
            new Vector3(floats[0], floats[1], floats[2]),
            new Vector3(floats[4], floats[5], floats[6]),
            floats[8],
            floats[9]);
    }

    private static float[] Vec(Vector3 v) => [v.X, v.Y, v.Z];

    private static IReadOnlyDictionary<string, int> SingleSlot(string key, int slot)
        => new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal) { [key] = slot });

    private static IReadOnlyDictionary<string, string> SingleString(string key, string value)
        => new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value });

    private static IReadOnlyDictionary<string, double> Dict(params (string Key, double Value)[] entries)
    {
        var dict = new Dictionary<string, double>(entries.Length, StringComparer.Ordinal);
        foreach (var (key, value) in entries)
            dict.Add(key, value);
        return new ReadOnlyDictionary<string, double>(dict);
    }
}
