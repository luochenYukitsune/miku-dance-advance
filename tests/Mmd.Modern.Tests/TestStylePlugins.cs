using System.Collections.ObjectModel;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;

namespace Mmd.Modern.Tests;

/// <summary>
/// Loads the real compiled rendering style plugins from <c>plugins/</c> and <c>ExampleStylePlugins/</c>
/// through <see cref="PluginManager"/> for integration, shader, ViewOutput, UI, and GPU tests.
/// </summary>
internal static class TestStylePlugins
{
    public static readonly string[] OfficialPluginIds =
    [
        "mda.style.genshin",
        "mda.style.star-rail",
        "mda.style.wuthering-waves",
        "mda.style.endfield",
    ];

    public static readonly string[] OfficialStyleIds =
    [
        "genshin",
        "star-rail",
        "wuthering-waves",
        "endfield",
    ];

    public static string BundledPluginsRoot => Path.Combine(AppContext.BaseDirectory, "plugins");
    public static string ExamplePluginsRoot => Path.Combine(AppContext.BaseDirectory, "ExampleStylePlugins");

    private static readonly Lazy<LoadedStyleSuite> SharedOfficial = new(LoadOfficialSuite, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<LoadedStyleSuite> SharedWithExample = new(LoadOfficialPlusExampleSuite, LazyThreadSafetyMode.ExecutionAndPublication);

    public static D3D12ViewStyleCatalog AllViewCatalog => SharedOfficial.Value.ViewCatalog;
    public static D3D12StylePresetCatalog AllPresetCatalog => SharedOfficial.Value.PresetCatalog;
    public static IReadOnlyList<IRenderingStyleDefinition> OfficialStyles => SharedOfficial.Value.Styles;

    public static IRenderingStyleDefinition Genshin => GetStyle("genshin");
    public static IRenderingStyleDefinition StarRail => GetStyle("star-rail");
    public static IRenderingStyleDefinition WutheringWaves => GetStyle("wuthering-waves");
    public static IRenderingStyleDefinition Endfield => GetStyle("endfield");

    public static D3D12ViewStyleCatalog ViewCatalogWithExample => SharedWithExample.Value.ViewCatalog;
    public static D3D12StylePresetCatalog PresetCatalogWithExample => SharedWithExample.Value.PresetCatalog;
    public static IRenderingStyleDefinition CustomInkCel => SharedWithExample.Value.Styles.Single(s => s.StyleId == "custom-ink-cel");

    public static IRenderingStyleDefinition GetStyle(string styleId) =>
        SharedOfficial.Value.Styles.FirstOrDefault(s => string.Equals(s.StyleId, styleId, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Official style plugin '{styleId}' is not loaded from '{BundledPluginsRoot}'.");

    public static D3D12ViewStyleCatalog CreateViewCatalog(params string[] styleIds)
    {
        if (styleIds is null || styleIds.Length == 0)
            return D3D12ViewStyleCatalog.Empty;
        var selected = new List<IRenderingStyleDefinition>(styleIds.Length);
        foreach (string id in styleIds)
        {
            if (string.Equals(id, "mmd", StringComparison.Ordinal))
                continue;
            var found = SharedWithExample.Value.Styles.FirstOrDefault(s => string.Equals(s.StyleId, id, StringComparison.Ordinal))
                ?? throw new ArgumentException($"Unknown style '{id}'.", nameof(styleIds));
            selected.Add(found);
        }
        return new D3D12ViewStyleCatalog(selected);
    }

    public static D3D12StylePresetCatalog CreatePresetCatalog(params string[] styleIds)
        => CreateViewCatalog(styleIds).ToD3D12PresetCatalog();

    public static PluginManager CreateLoadedPluginManager(bool includeExampleCustom = false, string language = "zh-CN")
        => LoadSuitePluginManager(includeExampleCustom ? [BundledPluginsRoot, ExamplePluginsRoot] : [BundledPluginsRoot], language);

    /// <summary>
    /// Resolves a single-model <see cref="D3D12SurfaceStyleMap"/> using the real loaded style plugins
    /// and <see cref="D3D12ViewRenderingStyles.Resolve"/>.
    /// </summary>
    public static D3D12SurfaceStyleMap CreateSurfaceStyleMap(
        OriginalFrameScene scene,
        string styleId,
        IEnumerable<D3D12ViewCharacterMaterial>? characterMaterials = null,
        IEnumerable<D3D12ViewPluginMaterialBinding>? pluginBindings = null,
        IEnumerable<D3D12ViewOutlineWidthBinding>? outlineWidths = null,
        IReadOnlyDictionary<Guid, SceneObjectRenderInputs>? sceneInputs = null,
        D3D12ViewStyleCatalog? catalog = null,
        IReadOnlyDictionary<string, StyleMaterialBindingData>? styleGlobalParameters = null,
        bool guessMaterialRoles = true,
        Guid? objectId = null,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? boneIndicesByObject = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        Guid targetId = objectId ?? Guid.NewGuid();
        var activeCatalog = catalog ?? AllViewCatalog;
        var presets = activeCatalog.ToD3D12PresetCatalog();
        int matCount = scene.Surface.Snapshot.Materials.Count;
        var binding = new SceneRenderingStyleBinding(
            targetId,
            new RenderingPresetReference(styleId),
            ModelRole.Character,
            0,
            matCount,
            true);

        var resolvedInputs = sceneInputs ?? new Dictionary<Guid, SceneObjectRenderInputs>
        {
            [targetId] = CreateSceneInputs(targetId, scene.Surface.Snapshot.Textures.Count)
        };

        var result = D3D12ViewRenderingStyles.Resolve(
            scene,
            presets,
            [binding],
            characterMaterials,
            pluginBindings,
            resolvedInputs,
            outlineWidths,
            activeCatalog,
            styleGlobalParameters,
            guessMaterialRoles,
            boneIndicesByObject);
        return result.SurfaceStyles;
    }

    public static SceneObjectRenderInputs CreateSceneInputs(
        Guid objectId,
        int textureCount,
        IReadOnlyDictionary<int, Matrix4x4>? bones = null,
        bool hasRawRadialSeeds = false,
        SceneSnapshot? sourceGeometry = null)
    {
        var boneDict = bones is null
            ? new Dictionary<int, Matrix4x4>()
            : new Dictionary<int, Matrix4x4>(bones);
        var texDict = new Dictionary<int, int>(textureCount);
        for (int i = 0; i < textureCount; i++)
            texDict[i] = i;
        var ctor = typeof(SceneObjectRenderInputs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)[0];
        return (SceneObjectRenderInputs)ctor.Invoke([objectId, true, boneDict, texDict, hasRawRadialSeeds, sourceGeometry, null, null]);
    }

    public static SceneSnapshot WithTextures(SceneSnapshot snapshot, IEnumerable<SceneTexture> textures)
        => new(snapshot.Vertices, snapshot.Indices, snapshot.Materials, textures,
            snapshot.World, snapshot.Camera, snapshot.LightDirection, snapshot.AdditionalUvs, snapshot.RawRadialSeeds);

    public static D3D12ViewPluginMaterialBinding Bind(Guid objectId, int localMaterialIndex, StyleMaterialBindingData data)
        => new(objectId, localMaterialIndex, data);

    public static StyleMaterialBindingData FaceSdf(
        string styleId,
        int primaryTextureSlot,
        int secondaryTextureSlot = -1,
        string? encoding = null,
        float transition = 0.01f,
        float offset = 0.5f,
        StyleBoneTrackingMode trackingMode = StyleBoneTrackingMode.FixedWorld,
        string? boneName = null,
        Vector3? localForward = null,
        Vector3? localUp = null,
        Vector3? worldForward = null,
        Vector3? worldRight = null)
    {
        var slots = new Dictionary<string, int>(StringComparer.Ordinal) { ["primary"] = primaryTextureSlot };
        if (secondaryTextureSlot >= 0)
            slots["secondary"] = secondaryTextureSlot;

        string resolvedEncoding = encoding ?? styleId switch
        {
            "genshin" => "GenshinSoftFace",
            "endfield" => "EndfieldRgbCmM",
            _ => "GrayRed",
        };

        var numbers = Dict(("transition", transition), ("offset", offset));

        var strings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["encoding"] = resolvedEncoding
        };

        var fwd = worldForward ?? new Vector3(0f, 0f, -1f);
        var right = worldRight ?? new Vector3(1f, 0f, 0f);
        var boneBinding = trackingMode == StyleBoneTrackingMode.SelectedBone
            ? new StyleBoneAxisBinding(StyleBoneTrackingMode.SelectedBone, boneName, localForward ?? new Vector3(0f, 0f, -1f), localUp ?? Vector3.UnitY, fwd, right)
            : new StyleBoneAxisBinding(StyleBoneTrackingMode.FixedWorld, FixedWorldForward: fwd, FixedWorldRight: right);

        return new StyleMaterialBindingData(
            styleId,
            "face-sdf",
            new ReadOnlyDictionary<string, int>(slots),
            numbers,
            StringValues: new ReadOnlyDictionary<string, string>(strings),
            BoneBinding: boneBinding);
    }

    public static StyleMaterialBindingData GenshinHair(
        int textureSlot,
        float globalSpecularScale = 1f,
        float shininess = 10f,
        float multiplier = 0.1f,
        float intensity = 1f) =>
        new("genshin", "hair",
            SingleSlot("texture", textureSlot),
            Dict(("globalSpecularScale", globalSpecularScale), ("shininess", shininess), ("multiplier", multiplier), ("intensity", intensity)));

    public static StyleMaterialBindingData GenshinHairAtlas(
        int lightmapSlot,
        int atlasSlot,
        string atlasEncoding = "Srgb",
        float explicitRowId = 1f,
        float dayNight = 0f,
        float shadowPosition = 0.5f,
        float shadowRampWidth = 0.1f,
        float vertexWidth = 1f,
        float vertexAo = 1f,
        float useLightmapAo = 1f,
        float useVertexColorAo = 0f) =>
        new("genshin", "hair-atlas",
            new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["lightmap"] = lightmapSlot,
                ["atlas"] = atlasSlot,
            }),
            Dict(
                ("explicitRowId", explicitRowId),
                ("dayNight", dayNight),
                ("shadowPosition", shadowPosition),
                ("shadowRampWidth", shadowRampWidth),
                ("vertexWidth", vertexWidth),
                ("vertexAo", vertexAo),
                ("useLightmapAo", useLightmapAo),
                ("useVertexColorAo", useVertexColorAo)),
            StringValues: SingleString("atlasEncoding", atlasEncoding));

    public static StyleMaterialBindingData GenshinMetallic(
        int lightmapSlot,
        int matcapSlot,
        string matcapEncoding = "SrgbBgr",
        Vector3? darkColor = null,
        Vector3? lightColor = null,
        Vector3? shadowMultiplyColor = null,
        float enableMetallics = 1f,
        float tileScale = 1f,
        float brightness = 1f,
        string litMode = "PrimaryResponse",
        float uniformLitFactor = 1f,
        bool hasSpecular = false,
        Vector3? specularColor = null,
        Vector3? specularSharpColor = null,
        float specularShininess = 24f,
        float specularScale = 1f,
        float specularAttenuation = 1f,
        float specularSharpOffset = 0.8f,
        int specularRampSlot = -1,
        float specularRampMix = 0f)
    {
        var slots = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["lightmap"] = lightmapSlot,
            ["matcap"] = matcapSlot,
        };
        if (hasSpecular && specularRampSlot >= 0)
            slots["specularRamp"] = specularRampSlot;

        var numbers = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["enableMetallics"] = enableMetallics,
            ["tileScale"] = tileScale,
            ["brightness"] = brightness,
            ["uniformLitFactor"] = uniformLitFactor,
        };
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal)
        {
            ["darkColor"] = Vec(darkColor ?? new Vector3(0.2f, 0.2f, 0.2f)),
            ["lightColor"] = Vec(lightColor ?? Vector3.One),
            ["shadowMultiplyColor"] = Vec(shadowMultiplyColor ?? Vector3.One),
        };
        var strings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["matcapEncoding"] = matcapEncoding,
            ["litMode"] = litMode,
        };
        Dictionary<string, bool>? bools = null;
        if (hasSpecular)
        {
            bools = new Dictionary<string, bool>(StringComparer.Ordinal) { ["hasSpecular"] = true };
            vectors["specularColor"] = Vec(specularColor ?? Vector3.One);
            vectors["specularSharpColor"] = Vec(specularSharpColor ?? Vector3.One);
            numbers["specularShininess"] = specularShininess;
            numbers["specularScale"] = specularScale;
            numbers["specularAttenuation"] = specularAttenuation;
            numbers["specularSharpOffset"] = specularSharpOffset;
            numbers["specularRampMix"] = specularRampMix;
        }

        return new StyleMaterialBindingData(
            "genshin",
            "metallic",
            new ReadOnlyDictionary<string, int>(slots),
            new ReadOnlyDictionary<string, double>(numbers),
            bools is null ? null : new ReadOnlyDictionary<string, bool>(bools),
            new ReadOnlyDictionary<string, float[]>(vectors),
            new ReadOnlyDictionary<string, string>(strings));
    }

    public static StyleMaterialBindingData StarMcHair(
        string styleId,
        int highlightSlot,
        int shadowSlot,
        string highlightChannel = "Blue",
        float highlightCoefficient = 0.1f,
        float highlightBrightness = 2f,
        float shadowCoefficient = 0.5f,
        float shadowSoftness = 0.5f,
        float intensity = 0.1f) =>
        new(styleId, "hair",
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

    private static LoadedStyleSuite LoadOfficialSuite()
    {
        var manager = LoadSuitePluginManager([BundledPluginsRoot], "en");
        var styles = ExtractStyles(manager);
        var viewCatalog = new D3D12ViewStyleCatalog(styles);
        var presetCatalog = viewCatalog.ToD3D12PresetCatalog();
        return new LoadedStyleSuite(manager, styles, viewCatalog, presetCatalog);
    }

    private static LoadedStyleSuite LoadOfficialPlusExampleSuite()
    {
        var manager = LoadSuitePluginManager([BundledPluginsRoot, ExamplePluginsRoot], "en");
        var styles = ExtractStyles(manager);
        var viewCatalog = new D3D12ViewStyleCatalog(styles);
        var presetCatalog = viewCatalog.ToD3D12PresetCatalog();
        return new LoadedStyleSuite(manager, styles, viewCatalog, presetCatalog);
    }

    private static PluginManager LoadSuitePluginManager(string[] roots, string language)
    {
        foreach (string root in roots)
        {
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException($"Plugin staging directory '{root}' does not exist; ensure Mmd.Modern.Tests staged plugins on build.");
        }

        var manager = new PluginManager(
            new PluginHostInfo(new Version(0, 1, 22), 1, language),
            [
                new PluginCapability(PluginCapabilities.EditorCommands, typeof(IPluginCommand)),
                new PluginCapability(PluginCapabilities.RenderingStyles, typeof(IRenderingStyleProvider)),
            ]);
        var refresh = manager.RefreshAsync(roots).GetAwaiter().GetResult();
        if (!refresh.Succeeded)
            throw new InvalidOperationException($"Test plugin RefreshAsync failed: {refresh.Diagnostic}");
        foreach (var plugin in manager.Plugins.ToArray())
        {
            var enable = manager.EnableAsync(plugin.Id).GetAwaiter().GetResult();
            if (!enable.Succeeded)
                throw new InvalidOperationException($"Test plugin EnableAsync('{plugin.Id}') failed: {enable.Diagnostic}");
        }
        return manager;
    }

    private static IReadOnlyList<IRenderingStyleDefinition> ExtractStyles(PluginManager manager)
        => Array.AsReadOnly(manager.Extensions
            .GetExtensions<IRenderingStyleProvider>(PluginCapabilities.RenderingStyles)
            .SelectMany(ext => ext.Value.GetStyles())
            .OrderBy(s =>
            {
                int idx = Array.IndexOf(OfficialStyleIds, s.StyleId);
                return idx >= 0 ? idx : int.MaxValue;
            })
            .ThenBy(s => s.StyleId, StringComparer.Ordinal)
            .ToArray());

    private sealed record LoadedStyleSuite(
        PluginManager Manager,
        IReadOnlyList<IRenderingStyleDefinition> Styles,
        D3D12ViewStyleCatalog ViewCatalog,
        D3D12StylePresetCatalog PresetCatalog);

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
