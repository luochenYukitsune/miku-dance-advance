using System.Collections.ObjectModel;
using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;

namespace Mmd.Modern.Tests;

internal static class T8bTestHelpers
{
    internal static D3D12PluginDrawData? BuildDrawData(
        string styleId,
        D3D12CharacterMaterialRole role,
        IReadOnlyList<StyleMaterialBindingData>? bindings = null,
        float faceNormalShadeStrength = 0.35f,
        float faceGeometricShadowStrength = 1f,
        float eyeGeometricShadowStrength = 0.2f,
        int materialIndex = 0)
    {
        if (string.Equals(styleId, D3D12SurfaceStyleMap.DefaultStyleId, StringComparison.Ordinal))
            return null;

        var style = TestStylePlugins.GetStyle(styleId);
        var active = bindings ?? Array.Empty<StyleMaterialBindingData>();
        var context = new StyleDrawContext(
            ObjectId: Guid.Empty,
            MaterialIndex: materialIndex,
            MaterialName: null,
            ResolvedRole: D3D12StylePluginAdapter.ToContractRole(role),
            Bindings: active,
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
            CameraPosition: new Vector3(0f, 0f, -5f),
            StylizedShading: null,
            Treatment: null,
            FaceEyeDefaults: new StyleFaceEyeDefaults(
                faceNormalShadeStrength,
                faceGeometricShadowStrength,
                eyeGeometricShadowStrength));
        var raw = style.BuildMaterialDrawData(context);
        return raw is null ? null : D3D12StylePluginAdapter.ToD3D12DrawData(raw);
    }

    internal static D3D12PluginDrawData? BuildGenshinDrawData(
        D3D12CharacterMaterialRole role,
        StyleMaterialBindingData? binding)
        => BuildGenshinDrawData(role, 0.35f, 1f, 0.2f, binding);

    internal static D3D12PluginDrawData? BuildGenshinDrawData(
        D3D12CharacterMaterialRole role,
        float faceNormalShadeStrength = 0.35f,
        float faceGeometricShadowStrength = 1f,
        float eyeGeometricShadowStrength = 0.2f,
        params StyleMaterialBindingData?[] bindings)
    {
        var nonNull = bindings.Where(static b => b is not null).Select(static b => b!).ToArray();
        return BuildDrawData("genshin", role, nonNull, faceNormalShadeStrength, faceGeometricShadowStrength, eyeGeometricShadowStrength);
    }

    internal static D3D12SurfaceStyleMap CreateDirectMap(params string[] styleIds)
    {
        var materials = new D3D12CharacterMaterialStyle[styleIds.Length];
        for (int i = 0; i < styleIds.Length; i++)
        {
            materials[i] = new D3D12CharacterMaterialStyle(
                PluginDraw: BuildDrawData(styleIds[i], D3D12CharacterMaterialRole.Body, materialIndex: i));
        }
        return new D3D12SurfaceStyleMap(styleIds, materials, TestStylePlugins.AllPresetCatalog);
    }

    internal static D3D12SurfaceStyleMap CreateDirectMap(
        string[] styleIds,
        params (D3D12CharacterMaterialRole Role, StyleMaterialBindingData?[]? Bindings, float FaceNormalShadeStrength, float FaceGeometricShadowStrength, float EyeGeometricShadowStrength)[] materials)
    {
        var styles = new D3D12CharacterMaterialStyle[materials.Length];
        for (int i = 0; i < materials.Length; i++)
        {
            var entry = materials[i];
            var active = entry.Bindings?.Where(static b => b is not null).Select(static b => b!).ToArray();
            styles[i] = new D3D12CharacterMaterialStyle(
                Role: entry.Role,
                FaceNormalShadeStrength: entry.FaceNormalShadeStrength,
                FaceGeometricShadowStrength: entry.FaceGeometricShadowStrength,
                EyeGeometricShadowStrength: entry.EyeGeometricShadowStrength,
                PluginDraw: BuildDrawData(styleIds[i], entry.Role, active,
                    entry.FaceNormalShadeStrength, entry.FaceGeometricShadowStrength, entry.EyeGeometricShadowStrength, i));
        }
        return new D3D12SurfaceStyleMap(styleIds, styles, TestStylePlugins.AllPresetCatalog);
    }

    internal static D3D12SurfaceStyleMap CreateGenshinMap(
        D3D12CharacterMaterialRole role,
        params StyleMaterialBindingData?[] bindings)
        => CreateDirectMap(["genshin"], (role, bindings, 0.35f, 1f, 0.2f));

    internal static D3D12SurfaceStyleMap CreateGenshinTwoMap(
        string secondStyleId,
        D3D12CharacterMaterialRole role0,
        StyleMaterialBindingData?[]? bindings0,
        float faceNormalShadeStrength0 = 0.35f,
        float faceGeometricShadowStrength0 = 1f)
        => CreateDirectMap(
            ["genshin", secondStyleId],
            (role0, bindings0, faceNormalShadeStrength0, faceGeometricShadowStrength0, 0.2f),
            (D3D12CharacterMaterialRole.Body, null, 0.35f, 1f, 0.2f));

    internal static StyleValidationResult ValidateStyle(
        IRenderingStyleDefinition style,
        StyleMaterialRole role,
        OriginalFrameScene? scene = null,
        params StyleMaterialBindingData?[] bindings)
    {
        var nonNull = bindings.Where(static b => b is not null).Select(static b => b!).ToArray();
        int texCount = scene?.Surface.Snapshot.Textures.Count ?? int.MaxValue;
        (int Width, int Height)[]? dims = scene is null
            ? null
            : scene.Surface.Snapshot.Textures.Select(static t => (t.Width, t.Height)).ToArray();
        return style.ValidateMaterial(new StyleMaterialValidationContext(
            Guid.Empty,
            0,
            MaterialName: null,
            ResolvedRole: role,
            Bindings: nonNull,
            SourceTextureCount: texCount,
            SourceTextureDimensions: dims));
    }

    internal static StyleValidationResult ValidateGenshin(
        StyleMaterialRole role,
        OriginalFrameScene? scene = null,
        params StyleMaterialBindingData?[] bindings)
        => ValidateStyle(TestStylePlugins.Genshin, role, scene, bindings);

    internal static D3D12PluginShaderModule GetGenshinModule(string moduleId)
    {
        var contract = TestStylePlugins.Genshin.GetShaderModules().Single(m => string.Equals(m.ModuleId, moduleId, StringComparison.Ordinal));
        return D3D12StylePluginAdapter.ToD3D12ShaderModule(contract);
    }

    internal static string AssembleGenshinPixelSource(
        string moduleId,
        string entryPoint,
        bool nativeLights,
        bool gi,
        bool textureMorph,
        bool subTexture)
    {
        var module = GetGenshinModule(moduleId);
        var hooks = module.EffectiveHooks;
        var geom = D3D12StyleShaderAssembler.ResolveEffectiveGeometry(module, subTexture);
        return D3D12StyleShaderAssembler.AssemblePixelSource(
            entryPoint, module, hooks, geom,
            nativeLights, gi, textureMorph, subTexture,
            characterToon: true, preserveDirectLightPalette: false);
    }

    internal static byte[] CompileGenshinPixel(
        string moduleId,
        string entryPoint,
        bool nativeLights,
        bool gi,
        bool textureMorph,
        bool subTexture)
    {
        var module = GetGenshinModule(moduleId);
        var hooks = module.EffectiveHooks;
        var geom = D3D12StyleShaderAssembler.ResolveEffectiveGeometry(module, subTexture);
        return D3D12StyleShaderAssembler.CompilePixel(
            "genshin", entryPoint, module, hooks, geom,
            nativeLights, gi, textureMorph, subTexture,
            characterToon: true, preserveDirectLightPalette: false);
    }

    internal static StyleMaterialBindingData CreateEndfieldMatcapBinding(
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
        float shadowHardness = 0.5f,
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
        var baseVec = baseColor ?? Vector3.One;
        var hlVec = highlightColor ?? Vector3.Zero;
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal)
        {
            ["baseColor"] = [baseVec.X, baseVec.Y, baseVec.Z],
            ["highlightColor"] = [hlVec.X, hlVec.Y, hlVec.Z],
        };
        if (lightControlDirection is { } ldir)
            vectors["lightControlDirection"] = [ldir.X, ldir.Y, ldir.Z];
        var strings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["matcapEncoding"] = matcapEncoding,
            ["generatorRevision"] = generatorRevision,
        };
        return new StyleMaterialBindingData(
            "endfield",
            "matcap",
            new ReadOnlyDictionary<string, int>(slots),
            new ReadOnlyDictionary<string, double>(numbers),
            new ReadOnlyDictionary<string, bool>(bools),
            new ReadOnlyDictionary<string, float[]>(vectors),
            new ReadOnlyDictionary<string, string>(strings));
    }

    internal static OriginalFrameScene CreateMetallicFrame(SceneTexture[]? textures = null, bool seeds = false, Matrix4x4? world = null)
    {
        SceneVertex[] vertices =
        [
            new(new(-1.5f, -1.1f, 0), -Vector3.UnitZ, new(0, 1)),
            new(new(1.5f, -1.1f, 0), -Vector3.UnitZ, new(1, 1)),
            new(new(1.5f, 1.1f, 0), -Vector3.UnitZ, new(1, 0)),
            new(new(-1.5f, 1.1f, 0), -Vector3.UnitZ, new(0, 0)),
        ];
        var snapshot = new SceneSnapshot(
            vertices,
            [0, 2, 1, 0, 3, 2],
            [new("explicit receiver", 0, 6, new(0.25f, 0.18f, 0.12f, 1), Vector3.Zero, Vector3.Zero, 10)],
            textures ?? [new("explicit image", 1, 1, [240, 70, 40, 230])],
            world: world,
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 4, 0.1f, 30),
            lightDirection: -Vector3.UnitZ,
            rawRadialSeeds: seeds ? new SceneRawRadialSeeds(Enumerable.Repeat(Vector3.UnitX, 4).ToArray()) : null);
        return new(
            new(snapshot, [new(new(0.22f), DoubleSided: true)], new(Vector3.One, Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: new(0, 0, 0, 1)),
            materials: [new(false, false, false)]);
    }
}
