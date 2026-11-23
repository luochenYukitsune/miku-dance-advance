using System.Collections.ObjectModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>One unavailable reference and every scene instance affected by it, including hidden instances.</summary>
public sealed class D3D12ViewRenderingStyleFailure
{
    public RenderingPresetReference Preset { get; }
    public D3D12StylePresetResult Resolution { get; }
    public IReadOnlyList<Guid> AffectedObjectIds { get; }

    internal D3D12ViewRenderingStyleFailure(RenderingPresetReference preset, D3D12StylePresetResult resolution,
        IEnumerable<Guid> objectIds)
    { Preset = preset; Resolution = resolution; AffectedObjectIds = Array.AsReadOnly(objectIds.ToArray()); }
}

/// <summary>Detached availability report. Examining it never opens files, changes project references or draws.</summary>
public sealed class D3D12ViewRenderingStylesPreflight
{
    public IReadOnlyList<D3D12ViewRenderingStyleFailure> Failures { get; }
    public bool IsAvailable => Failures.Count == 0;

    internal D3D12ViewRenderingStylesPreflight(IEnumerable<D3D12ViewRenderingStyleFailure> failures)
        => Failures = Array.AsReadOnly(failures.ToArray());

    public void EnsureAvailable()
    {
        if (!IsAvailable) throw new D3D12ViewRenderingStylesException(this);
    }
}

/// <summary>The complete frame is rejected before GPU submission; replacement requires an explicit host decision.</summary>
public sealed class D3D12ViewRenderingStylesException : InvalidOperationException
{
    public D3D12ViewRenderingStylesPreflight Preflight { get; }
    public IReadOnlyList<D3D12ViewRenderingStyleFailure> Failures => Preflight.Failures;

    internal D3D12ViewRenderingStylesException(D3D12ViewRenderingStylesPreflight preflight)
        : base("Rendering presets require resolution before this frame can be rendered: " + string.Join("; ",
            preflight.Failures.Select(failure => $"'{failure.Preset.StableId}' ({failure.Resolution.Status}): {failure.Resolution.Diagnostic}")))
        => Preflight = preflight;
}

/// <summary>Immutable frame parameters and the actual indexed surface dispatch consumed by the renderer.
/// Diagnostics describe bounded capabilities without changing a model's explicit role.</summary>
public sealed class D3D12ViewRenderingStylesResult
{
    internal static readonly ReadOnlyCollection<string> EmptyDiagnostics = Array.AsReadOnly(Array.Empty<string>());

    public OriginalFrameScene Scene { get; }
    public D3D12SurfaceStyleMap SurfaceStyles { get; }
    public IReadOnlyList<string> Diagnostics { get; }

    internal D3D12ViewRenderingStylesResult(OriginalFrameScene scene, D3D12SurfaceStyleMap surfaceStyles,
        IEnumerable<string> diagnostics)
    {
        Scene = scene;
        SurfaceStyles = surfaceStyles;
        Diagnostics = diagnostics is ReadOnlyCollection<string> frozen ? frozen : Array.AsReadOnly(diagnostics.ToArray());
    }
}

/// <summary>
/// Pure plugin-driven adapter from scene-instance metadata, style plugins, and per-material bindings
/// to <see cref="D3D12SurfaceStyleMap"/> and <see cref="D3D12StylePresetApplicator"/>.
/// A nonempty binding list must describe every composed visible material exactly once.
/// </summary>
public static class D3D12ViewRenderingStyles
{
    public static D3D12ViewRenderingStylesPreflight Preflight(
        OriginalFrameScene scene,
        D3D12StylePresetCatalog catalog,
        IEnumerable<SceneRenderingStyleBinding> bindings,
        IEnumerable<D3D12ViewCharacterMaterial>? characterMaterials = null,
        IEnumerable<D3D12ViewPluginMaterialBinding>? pluginBindings = null,
        IEnumerable<D3D12ViewOutlineWidthBinding>? outlineWidths = null,
        D3D12ViewStyleCatalog? styleCatalog = null)
    {
        var selected = Validate(scene, catalog, bindings);
        var effectiveStyleCatalog = styleCatalog ?? D3D12ViewStyleCatalog.Empty;
        var effectiveCatalog = MergePresetCatalog(catalog, effectiveStyleCatalog);
        _ = D3D12ViewOutlineWidths.Resolve(selected, D3D12ViewOutlineWidths.Detach(outlineWidths));
        var detachedMaterials = D3D12ViewCharacterMaterials.Detach(characterMaterials);
        var detachedPluginBindings = D3D12ViewPluginMaterialBindings.Detach(pluginBindings);
        D3D12ViewCharacterMaterials.ValidateObjects(selected, detachedMaterials);
        D3D12ViewPluginMaterialBindings.ValidateSceneObjects(selected, detachedPluginBindings, effectiveCatalog, effectiveStyleCatalog, detachedMaterials);
        D3D12ViewPluginMaterialBindings.Validate(detachedMaterials, FilterVisibleBindings(selected, detachedPluginBindings), effectiveStyleCatalog);
        return ResolveAvailability(effectiveCatalog, selected);
    }

    /// <summary>Compatibility entry for callers consuming material parameters only. Surface dispatch consumers
    /// must pass both values returned by Resolve to the rendering session.</summary>
    public static OriginalFrameScene Apply(
        OriginalFrameScene scene,
        D3D12StylePresetCatalog catalog,
        IEnumerable<SceneRenderingStyleBinding> bindings,
        IEnumerable<D3D12ViewCharacterMaterial>? characterMaterials = null,
        IEnumerable<D3D12ViewOutlineWidthBinding>? outlineWidths = null,
        D3D12ViewStyleCatalog? styleCatalog = null)
        => Resolve(scene, catalog, bindings, characterMaterials, outlineWidths: outlineWidths, styleCatalog: styleCatalog).Scene;

    private static readonly ConditionalWeakTable<D3D12StylePresetCatalog, DefaultSurfaceStyleMapCache> DefaultStyleMapCaches = new();

    public static D3D12ViewRenderingStylesResult Resolve(
        OriginalFrameScene scene,
        D3D12StylePresetCatalog catalog,
        IEnumerable<SceneRenderingStyleBinding> bindings,
        IEnumerable<D3D12ViewCharacterMaterial>? characterMaterials = null,
        IEnumerable<D3D12ViewPluginMaterialBinding>? pluginBindings = null,
        IReadOnlyDictionary<Guid, SceneObjectRenderInputs>? sceneInputs = null,
        IEnumerable<D3D12ViewOutlineWidthBinding>? outlineWidths = null,
        D3D12ViewStyleCatalog? styleCatalog = null,
        IReadOnlyDictionary<string, StyleMaterialBindingData>? styleGlobalParameters = null,
        bool guessMaterialRoles = true,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? boneIndicesByObject = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(bindings);

        var effectiveStyleCatalog = styleCatalog ?? D3D12ViewStyleCatalog.Empty;
        var effectiveCatalog = MergePresetCatalog(catalog, effectiveStyleCatalog);

        if (IsEmptyCollection(characterMaterials)
            && IsEmptyCollection(pluginBindings)
            && IsEmptyCollection(outlineWidths)
            && IsEmptyDictionary(styleGlobalParameters)
            && bindings is IReadOnlyList<SceneRenderingStyleBinding> bindingList
            && TryResolveDefaultMmdFastPath(scene, effectiveCatalog, bindingList, out var fastResult))
        {
            return fastResult;
        }

        var selected = Validate(scene, catalog, bindings);
        var outlineScales = D3D12ViewOutlineWidths.Resolve(selected, D3D12ViewOutlineWidths.Detach(outlineWidths));
        var detachedMaterials = D3D12ViewCharacterMaterials.Detach(characterMaterials);
        var detachedPluginBindings = D3D12ViewPluginMaterialBindings.Detach(pluginBindings);
        var detachedGlobals = D3D12ViewPluginMaterialBindings.DetachGlobalParameters(styleGlobalParameters);
        var diagnostics = new List<string>();

        D3D12ViewCharacterMaterials.ValidateObjects(selected, detachedMaterials, diagnostics);
        D3D12ViewPluginMaterialBindings.ValidateSceneObjects(selected, detachedPluginBindings, effectiveCatalog, effectiveStyleCatalog, detachedMaterials);
        D3D12ViewPluginMaterialBindings.Validate(detachedMaterials, FilterVisibleBindings(selected, detachedPluginBindings), effectiveStyleCatalog);
        ResolveAvailability(effectiveCatalog, selected).EnsureAvailable();

        int totalMaterials = scene.Materials.Count;
        if (selected.Length == 0)
        {
            var emptyStyles = GetOrCreateDefaultSurfaceStyleMap(effectiveCatalog, totalMaterials);
            emptyStyles.Validate(scene);
            return new(scene, emptyStyles, diagnostics);
        }

        var charMatMap = BuildCharacterMaterialMap(detachedMaterials);
        var pluginBindingMap = BuildPluginBindingMap(detachedPluginBindings);

        return ResolveCore(
            scene,
            effectiveCatalog,
            effectiveStyleCatalog,
            selected,
            outlineScales,
            charMatMap,
            pluginBindingMap,
            detachedGlobals,
            sceneInputs,
            guessMaterialRoles,
            boneIndicesByObject,
            diagnostics);
    }

    internal static D3D12ViewRenderingStylesResult ResolveCore(
        OriginalFrameScene scene,
        D3D12StylePresetCatalog effectiveCatalog,
        D3D12ViewStyleCatalog effectiveStyleCatalog,
        SceneRenderingStyleBinding[] selected,
        D3D12ViewOutlineWidths.MaterialScale[] outlineScales,
        Dictionary<(Guid ObjectId, int LocalMaterialIndex), D3D12ViewCharacterMaterial> charMatMap,
        Dictionary<(Guid ObjectId, int LocalMaterialIndex), D3D12ViewPluginMaterialBinding[]> pluginBindingMap,
        IReadOnlyDictionary<string, StyleMaterialBindingData>? detachedGlobals,
        IReadOnlyDictionary<Guid, SceneObjectRenderInputs>? sceneInputs,
        bool guessMaterialRoles,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? boneIndicesByObject,
        List<string> diagnostics)
    {
        int totalMaterials = scene.Materials.Count;
        var styleIds = new string[totalMaterials];
        Array.Fill(styleIds, D3D12SurfaceStyleMap.DefaultStyleId);
        var materialStyles = new D3D12CharacterMaterialStyle[totalMaterials];
        Array.Fill(materialStyles, new D3D12CharacterMaterialStyle());
        var stylizedRoles = new D3D12CharacterMaterialRole[totalMaterials];

        var snapshot = scene.Surface.Snapshot;
        var inputsMap = sceneInputs ?? SceneObjectRenderInputs.Empty;
        Vector3 primaryLightDirection = Vector3.Normalize(snapshot.LightDirection);
        Vector3 primaryLightColor = scene.Surface.Profile.LightAmbient;
        Vector3 cameraPosition = (scene.Options?.CameraOverride ?? snapshot.Camera).Eye;

        foreach (var instance in selected)
        {
            var resolution = effectiveCatalog.Find(instance.Preset.StableId);
            string pipelineId = resolution.Definition!.PipelineId;
            if (string.Equals(pipelineId, D3D12SurfaceStyleMap.DefaultStyleId, StringComparison.Ordinal))
            {
                if (instance.Visible && instance.Role == ModelRole.Character && charMatMap.Count > 0)
                {
                    for (int localIdx = 0; localIdx < instance.MaterialCount; localIdx++)
                    {
                        if (!charMatMap.TryGetValue((instance.ObjectId, localIdx), out var cm))
                            continue;
                        int frameIdx = instance.MaterialStart + localIdx;
                        materialStyles[frameIdx] = new D3D12CharacterMaterialStyle(
                            Role: cm.Role,
                            FaceNormalShadeStrength: cm.FaceNormalShadeStrength,
                            FaceGeometricShadowStrength: cm.FaceGeometricShadowStrength,
                            EyeGeometricShadowStrength: cm.EyeGeometricShadowStrength);
                        stylizedRoles[frameIdx] = cm.Role;
                    }
                }
                continue;
            }

            if (!instance.Visible || instance.MaterialCount == 0)
            {
                diagnostics.Add($"Hidden or empty object '{instance.ObjectId:D}' consumes no surface materials. {resolution.Diagnostic}");
                continue;
            }

            if (instance.Role != ModelRole.Character)
            {
                diagnostics.Add($"Object '{instance.ObjectId:D}' ({instance.Role}) keeps the MMD surface: {resolution.Diagnostic}");
                continue;
            }

            diagnostics.Add($"Object '{instance.ObjectId:D}': {resolution.Diagnostic}");
            Array.Fill(styleIds, pipelineId, instance.MaterialStart, instance.MaterialCount);

            var style = effectiveStyleCatalog.FindStyle(pipelineId);
            StyleMaterialBindingData? globalParams = detachedGlobals is not null && detachedGlobals.TryGetValue(pipelineId, out var gp)
                ? gp
                : null;

            for (int localIdx = 0; localIdx < instance.MaterialCount; localIdx++)
            {
                int frameIdx = instance.MaterialStart + localIdx;
                var mat = snapshot.Materials[frameIdx];
                charMatMap.TryGetValue((instance.ObjectId, localIdx), out var charMat);

                StyleMaterialBindingData[] activeBindings = GetActiveBindings(pluginBindingMap, instance.ObjectId, localIdx, pipelineId);

                if (style is null)
                {
                    if (activeBindings.Length > 0)
                    {
                        throw new NotSupportedException(
                            $"Style '{pipelineId}' has active material bindings on object '{instance.ObjectId:D}' but no loaded IRenderingStyleDefinition in the style catalog.");
                    }

                    var fallbackStyle = BuildFallbackMaterialStyle(mat.Name, charMat, guessMaterialRoles);
                    materialStyles[frameIdx] = fallbackStyle;
                    stylizedRoles[frameIdx] = fallbackStyle.Role;
                    continue;
                }

                var resolvedStyle = BuildPluginMaterialStyle(
                    style,
                    instance.ObjectId,
                    localIdx,
                    pipelineId,
                    scene,
                    snapshot,
                    frameIdx,
                    charMat,
                    activeBindings,
                    inputsMap,
                    boneIndicesByObject,
                    globalParams,
                    primaryLightDirection,
                    primaryLightColor,
                    cameraPosition,
                    guessMaterialRoles);
                materialStyles[frameIdx] = resolvedStyle;
                stylizedRoles[frameIdx] = resolvedStyle.Role;
            }
        }

        var surfaceStyles = new D3D12SurfaceStyleMap(styleIds, materialStyles, effectiveCatalog)
        {
            StylizedRoles = Array.AsReadOnly(stylizedRoles),
        };

        var result = ApplySceneTransformations(scene, effectiveCatalog, selected, outlineScales);

        // Validate the consumed response after the preset supplies its actual
        // threshold/width. Validating the incoming source interval can reject a
        // valid replacement or miss an invalid resulting treatment interval.
        surfaceStyles.Validate(result);
        return new(result, surfaceStyles, diagnostics);
    }

    internal static D3D12CharacterMaterialStyle BuildFallbackMaterialStyle(
        string materialName,
        D3D12ViewCharacterMaterial? charMat,
        bool guessMaterialRoles)
    {
        var fallbackRole = charMat is { ExplicitRole: true }
            ? charMat.Role
            : (guessMaterialRoles ? D3D12CharacterMaterialRoleGuess.Guess(materialName) : D3D12CharacterMaterialRole.Body);
        D3D12PmxMaterialTreatment? fallbackTreatment = fallbackRole is D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair
            ? charMat?.PmxTreatment
            : null;
        return new D3D12CharacterMaterialStyle(
            Role: fallbackRole,
            FaceNormalShadeStrength: charMat?.FaceNormalShadeStrength ?? 0.35f,
            FaceGeometricShadowStrength: charMat?.FaceGeometricShadowStrength ?? 1f,
            EyeGeometricShadowStrength: charMat?.EyeGeometricShadowStrength ?? 0.2f,
            PmxMaterialTreatment: fallbackTreatment,
            StylizedShading: charMat?.StylizedShading,
            ToonResponse: charMat?.ToonResponse,
            PreserveDirectLightPalette: charMat?.PreserveDirectLightPalette ?? false);
    }

    internal static D3D12CharacterMaterialStyle BuildPluginMaterialStyle(
        IRenderingStyleDefinition style,
        Guid objectId,
        int localIdx,
        string pipelineId,
        OriginalFrameScene scene,
        SceneSnapshot snapshot,
        int frameIdx,
        D3D12ViewCharacterMaterial? charMat,
        StyleMaterialBindingData[] activeBindings,
        IReadOnlyDictionary<Guid, SceneObjectRenderInputs> inputsMap,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? boneIndicesByObject,
        StyleMaterialBindingData? globalParams,
        Vector3 primaryLightDirection,
        Vector3 primaryLightColor,
        Vector3 cameraPosition,
        bool guessMaterialRoles,
        PluginSlotClassificationCache? classificationCache = null,
        List<int>? observedTextureIndices = null)
    {
        var mat = snapshot.Materials[frameIdx];
        var surfaceMat = scene.Surface.Materials[frameIdx];
        var frameFlags = scene.Materials[frameIdx];

        StyleMaterialClassification classification;
        StyleMaterialRole contractRole;
        D3D12CharacterMaterialRole d3dRole;
        StyleStylizedShadingParameters? contextShading;
        StyleMaterialTreatmentParameters? contextTreatment;
        StyleFaceEyeDefaults baseFaceEye;
        bool hasDedicatedBinding;

        if (classificationCache is not null
            && classificationCache.HasValue
            && string.Equals(classificationCache.MaterialName, mat.Name, StringComparison.Ordinal))
        {
            classification = classificationCache.Classification;
            contractRole = classificationCache.ContractRole;
            d3dRole = classificationCache.D3DRole;
            contextShading = classificationCache.ContextShading;
            contextTreatment = classificationCache.ContextTreatment;
            baseFaceEye = classificationCache.BaseFaceEye;
            hasDedicatedBinding = classificationCache.HasDedicatedBinding;
        }
        else
        {
            classification = D3D12ViewPluginMaterialBindings.ResolveInspectionRole(
                style,
                objectId,
                localIdx,
                mat.Name,
                charMat,
                guessMaterialRoles,
                activeBindings);

            contractRole = classification.ResolvedRole;
            d3dRole = D3D12StylePluginAdapter.ToD3D12Role(contractRole);

            var defaultShading = D3D12StylePluginAdapter.ToD3D12StylizedShading(style.DefaultStylizedShading);
            var roleShading = D3D12StylizedShadingCatalog.ResolveForRole(defaultShading, d3dRole);
            contextShading = charMat?.StylizedShading is { } explicitShading
                ? D3D12StylePluginAdapter.ToContractStylizedShading(explicitShading)
                : classification.StylizedShadingOverride
                    ?? (roleShading is not null ? D3D12StylePluginAdapter.ToContractStylizedShading(roleShading) : null);

            contextTreatment = charMat?.PmxTreatment is { } explicitTreatment
                ? D3D12StylePluginAdapter.ToContractTreatment(explicitTreatment)
                : classification.TreatmentOverride
                    ?? (style.DefaultRoleTreatments.TryGetValue(contractRole, out var defaultRoleTreatment)
                        ? defaultRoleTreatment
                        : null);

            baseFaceEye = charMat is not null
                ? new StyleFaceEyeDefaults(
                    charMat.FaceNormalShadeStrength,
                    charMat.FaceGeometricShadowStrength,
                    charMat.EyeGeometricShadowStrength)
                : classification.FaceEyeOverride ?? style.DefaultFaceEye;

            hasDedicatedBinding = activeBindings.Any(static b =>
                !string.Equals(b.ModeId, "default", StringComparison.Ordinal));

            if (classificationCache is not null)
            {
                classificationCache.Set(mat.Name, classification, contractRole, d3dRole,
                    contextShading, contextTreatment, baseFaceEye, hasDedicatedBinding);
            }
        }

        var bindingContext = new StyleMaterialBindingContext(
            objectId,
            localIdx,
            mat.Name,
            contractRole,
            activeBindings,
            snapshot.Textures.Count);

        var resolvedTextures = D3D12ViewPluginMaterialBindings.ResolveSceneTextures(
            style, bindingContext, scene, inputsMap);
        if (observedTextureIndices is not null && resolvedTextures is not null)
        {
            for (int t = 0; t < resolvedTextures.Length; t++)
                observedTextureIndices.Add(resolvedTextures[t].ComposedTextureIndex);
        }

        var evaluatedBone = D3D12ViewPluginMaterialBindings.ResolveEvaluatedBone(
            style, bindingContext, inputsMap, boneIndicesByObject);

        if (activeBindings.Length > 0)
        {
            int validationTexCount = snapshot.Textures.Count;
            if (inputsMap.TryGetValue(objectId, out var objInputs) && objInputs.TextureIndices.Count > 0)
            {
                int maxLocal = objInputs.TextureIndices.Keys.Max();
                if (maxLocal >= validationTexCount)
                    validationTexCount = maxLocal + 1;
            }
            var validation = style.ValidateMaterial(new StyleMaterialValidationContext(
                objectId,
                localIdx,
                mat.Name,
                contractRole,
                activeBindings,
                validationTexCount,
                SourceTextureDimensions: null));
            if (!validation.IsValid)
                throw new ArgumentException(
                    validation.ErrorMessage ?? $"Material {localIdx} validation failed for style '{pipelineId}'.",
                    "pluginBindings");
        }

        var drawContext = new StyleDrawContext(
            ObjectId: objectId,
            MaterialIndex: localIdx,
            MaterialName: mat.Name,
            ResolvedRole: contractRole,
            Bindings: activeBindings,
            BaseDiffuseColor: mat.Diffuse,
            BaseSpecularColor: mat.Specular,
            BaseSpecularPower: mat.Shininess,
            BaseAmbientColor: mat.Ambient,
            BaseTextureIndex: mat.DiffuseTexture,
            SphereTextureIndex: surfaceMat.SphereTexture,
            SphereMode: (int)surfaceMat.SphereMode,
            ToonTextureIndex: mat.ToonTexture,
            SubTextureIndex: surfaceMat.SphereMode == OriginalSphereMode.SubTexture ? surfaceMat.SphereTexture : -1,
            IsTransparent: mat.Diffuse.W < 1f || frameFlags.CullTransparentSurface,
            PrimaryLightDirection: primaryLightDirection,
            PrimaryLightColor: primaryLightColor,
            CameraPosition: cameraPosition,
            StylizedShading: contextShading,
            Treatment: contextTreatment,
            FaceEyeDefaults: baseFaceEye,
            ResolvedTextures: resolvedTextures,
            EvaluatedBone: evaluatedBone,
            StyleGlobalParameters: globalParams);

        var rawDrawData = style.BuildMaterialDrawData(drawContext);
        var pluginDraw = rawDrawData is not null
            ? D3D12StylePluginAdapter.ToD3D12DrawData(rawDrawData)
            : null;

        D3D12PmxMaterialTreatment? pmxTreatmentForStyle = ResolveCharacterMaterialTreatment(
            d3dRole, charMat, classification, rawDrawData?.TreatmentOverride, style, contractRole, hasDedicatedBinding);

        D3D12StylizedShading? stylizedOverrideForStyle = charMat?.StylizedShading
            ?? (rawDrawData?.StylizedShadingOverride is { } drawShading
                ? D3D12StylePluginAdapter.ToD3D12StylizedShading(drawShading)
                : classification.StylizedShadingOverride is { } classShading
                    ? D3D12StylePluginAdapter.ToD3D12StylizedShading(classShading)
                    : null);

        StyleFaceEyeDefaults finalFaceEye = rawDrawData?.FaceEyeOverride ?? baseFaceEye;
        D3D12StylePluginAdapter.ValidateFaceEye(finalFaceEye);

        return new D3D12CharacterMaterialStyle(
            Role: d3dRole,
            FaceNormalShadeStrength: finalFaceEye.FaceNormalShadeStrength,
            FaceGeometricShadowStrength: finalFaceEye.FaceGeometricShadowStrength,
            EyeGeometricShadowStrength: finalFaceEye.EyeGeometricShadowStrength,
            PmxMaterialTreatment: pmxTreatmentForStyle,
            StylizedShading: stylizedOverrideForStyle,
            ToonResponse: charMat?.ToonResponse,
            PreserveDirectLightPalette: (charMat?.PreserveDirectLightPalette ?? false) || (rawDrawData?.PreserveDirectLightPalette ?? false),
            PluginDraw: pluginDraw);
    }

    internal static OriginalFrameScene ApplySceneTransformations(
        OriginalFrameScene scene,
        D3D12StylePresetCatalog effectiveCatalog,
        SceneRenderingStyleBinding[] selected,
        D3D12ViewOutlineWidths.MaterialScale[] outlineScales)
    {
        var styled = D3D12StylePresetApplicator.Apply(scene, effectiveCatalog,
            selected.Where(static binding => binding.Visible && binding.MaterialCount > 0).Select(static binding =>
                new D3D12StylePresetMaterialRange(binding.ObjectId, binding.MaterialStart, binding.MaterialCount,
                    binding.Preset.StableId, binding.Role == ModelRole.Character)));

        OriginalFrameMaterialFlags[]? materials = null;
        // The built-in project default supplies missing styles without undoing the source's explicit outline.
        // A custom preset remains an explicit replacement even when it uses the MMD pipeline.
        foreach (var binding in selected.Where(static binding => binding.Visible && binding.Role == ModelRole.Character
            && binding.Preset.StableId == D3D12SurfaceStyleMap.DefaultStyleId))
        {
            for (int index = binding.MaterialStart; index < binding.MaterialStart + binding.MaterialCount; index++)
            {
                if (scene.Materials[index].CharacterOutlineStyle is not { } outline
                    || styled.Materials[index].CharacterOutlineStyle == outline)
                    continue;
                materials ??= styled.Materials.ToArray();
                materials[index] = materials[index] with { CharacterOutlineStyle = outline };
            }
        }

        OriginalFrameScene result = materials is null
            ? styled
            : new(styled.Surface, styled.Outline, styled.OutlineOptions, styled.Options,
                materials, styled.Lighting, styled.EditorOverlay);
        return D3D12ViewOutlineWidths.Apply(result, outlineScales);
    }

    internal static Dictionary<(Guid ObjectId, int LocalMaterialIndex), D3D12ViewCharacterMaterial> BuildCharacterMaterialMap(
        IReadOnlyList<D3D12ViewCharacterMaterial> detachedMaterials)
        => detachedMaterials.Count == 0
            ? EmptyCharMatMap
            : detachedMaterials.ToDictionary(static m => (m.ObjectId, m.LocalMaterialIndex));

    internal static Dictionary<(Guid ObjectId, int LocalMaterialIndex), D3D12ViewPluginMaterialBinding[]> BuildPluginBindingMap(
        IReadOnlyList<D3D12ViewPluginMaterialBinding> detachedPluginBindings)
        => detachedPluginBindings.Count == 0
            ? EmptyPluginBindingMap
            : detachedPluginBindings
                .GroupBy(static b => (b.ObjectId, b.LocalMaterialIndex))
                .ToDictionary(static g => g.Key, static g => g.ToArray());

    internal static StyleMaterialBindingData[] GetActiveBindings(
        Dictionary<(Guid ObjectId, int LocalMaterialIndex), D3D12ViewPluginMaterialBinding[]> pluginBindingMap,
        Guid objectId,
        int localIdx,
        string pipelineId)
    {
        if (pluginBindingMap.Count == 0 || !pluginBindingMap.TryGetValue((objectId, localIdx), out var candidates))
            return Array.Empty<StyleMaterialBindingData>();

        int count = 0;
        for (int i = 0; i < candidates.Length; i++)
        {
            if (string.Equals(candidates[i].Binding.StyleId, pipelineId, StringComparison.Ordinal))
                count++;
        }
        if (count == 0)
            return Array.Empty<StyleMaterialBindingData>();
        var active = new StyleMaterialBindingData[count];
        int write = 0;
        for (int i = 0; i < candidates.Length; i++)
        {
            if (string.Equals(candidates[i].Binding.StyleId, pipelineId, StringComparison.Ordinal))
                active[write++] = candidates[i].Binding;
        }
        return active;
    }

    private static readonly Dictionary<(Guid ObjectId, int LocalMaterialIndex), D3D12ViewCharacterMaterial> EmptyCharMatMap = new();
    private static readonly Dictionary<(Guid ObjectId, int LocalMaterialIndex), D3D12ViewPluginMaterialBinding[]> EmptyPluginBindingMap = new();

    internal static bool IsEmptyCollection<T>(IEnumerable<T>? source) => source switch
    {
        null => true,
        IReadOnlyCollection<T> roc => roc.Count == 0,
        ICollection<T> col => col.Count == 0,
        _ => false,
    };

    private static bool IsEmptyDictionary<TKey, TValue>(IReadOnlyDictionary<TKey, TValue>? source)
        => source is null || source.Count == 0;

    private static bool TryResolveDefaultMmdFastPath(
        OriginalFrameScene scene,
        D3D12StylePresetCatalog effectiveCatalog,
        IReadOnlyList<SceneRenderingStyleBinding> bindings,
        out D3D12ViewRenderingStylesResult result)
    {
        result = null!;
        int totalMaterials = scene.Materials.Count;
        if (totalMaterials != scene.Surface.Materials.Count
            || totalMaterials != scene.Surface.Snapshot.Materials.Count
            || (scene.Outline is not null && totalMaterials != scene.Outline.Materials.Count))
            return false;

        int bindingCount = bindings.Count;
        if (bindingCount > 16)
            return false;

        int end = 0;
        for (int i = 0; i < bindingCount; i++)
        {
            var binding = bindings[i];
            if (binding is null || binding.ObjectId == Guid.Empty)
                return false;
            for (int j = 0; j < i; j++)
            {
                if (bindings[j].ObjectId == binding.ObjectId)
                    return false;
            }
            if (!Enum.IsDefined(binding.Role))
                return false;
            if (!string.Equals(binding.Preset.StableId, D3D12SurfaceStyleMap.DefaultStyleId, StringComparison.Ordinal))
                return false;
            if (binding.MaterialStart < 0 || binding.MaterialStart > totalMaterials || binding.MaterialCount < 0
                || binding.MaterialCount > totalMaterials - binding.MaterialStart || (!binding.Visible && binding.MaterialCount != 0))
                return false;
            if (binding.Visible && binding.MaterialCount > 0)
            {
                if (binding.MaterialStart != end)
                    return false;
                end += binding.MaterialCount;
            }
        }
        if (bindingCount > 0 && end != totalMaterials)
            return false;

        var defaultMap = GetOrCreateDefaultSurfaceStyleMap(effectiveCatalog, totalMaterials);
        result = new D3D12ViewRenderingStylesResult(scene, defaultMap, D3D12ViewRenderingStylesResult.EmptyDiagnostics);
        return true;
    }

    internal static D3D12SurfaceStyleMap GetOrCreateDefaultSurfaceStyleMap(D3D12StylePresetCatalog effectiveCatalog, int totalMaterials)
        => DefaultStyleMapCaches.GetValue(effectiveCatalog, static _ => new DefaultSurfaceStyleMapCache())
            .GetOrCreate(effectiveCatalog, totalMaterials);

    private sealed class DefaultSurfaceStyleMapCache
    {
        private readonly object gate = new();
        private int lastCount = -1;
        private D3D12SurfaceStyleMap? lastMap;
        private Dictionary<int, D3D12SurfaceStyleMap>? byCount;

        internal D3D12SurfaceStyleMap GetOrCreate(D3D12StylePresetCatalog effectiveCatalog, int totalMaterials)
        {
            lock (gate)
            {
                if (totalMaterials == lastCount && lastMap is not null)
                    return lastMap;
                if (byCount is not null && byCount.TryGetValue(totalMaterials, out var existing))
                {
                    lastCount = totalMaterials;
                    lastMap = existing;
                    return existing;
                }
            }

            var styleIds = new string[totalMaterials];
            Array.Fill(styleIds, D3D12SurfaceStyleMap.DefaultStyleId);
            var materialStyles = new D3D12CharacterMaterialStyle[totalMaterials];
            Array.Fill(materialStyles, new D3D12CharacterMaterialStyle());
            var stylizedRoles = Array.AsReadOnly(new D3D12CharacterMaterialRole[totalMaterials]);
            var created = new D3D12SurfaceStyleMap(styleIds, materialStyles, effectiveCatalog)
            {
                StylizedRoles = stylizedRoles,
            };
            foreach (var plugin in created.RegisteredStyles.Values)
                plugin.Validate();

            lock (gate)
            {
                if (byCount is not null && byCount.TryGetValue(totalMaterials, out var raceWinner))
                {
                    lastCount = totalMaterials;
                    lastMap = raceWinner;
                    return raceWinner;
                }
                if (lastMap is not null && lastCount != totalMaterials)
                {
                    byCount ??= new Dictionary<int, D3D12SurfaceStyleMap> { [lastCount] = lastMap };
                    if (byCount.Count < 64)
                        byCount[totalMaterials] = created;
                }
                lastCount = totalMaterials;
                lastMap = created;
                return created;
            }
        }
    }

    internal sealed class PluginSlotClassificationCache
    {
        internal bool HasValue { get; private set; }
        internal string MaterialName { get; private set; } = string.Empty;
        internal StyleMaterialClassification Classification { get; private set; } = null!;
        internal StyleMaterialRole ContractRole { get; private set; }
        internal D3D12CharacterMaterialRole D3DRole { get; private set; }
        internal StyleStylizedShadingParameters? ContextShading { get; private set; }
        internal StyleMaterialTreatmentParameters? ContextTreatment { get; private set; }
        internal StyleFaceEyeDefaults BaseFaceEye { get; private set; } = null!;
        internal bool HasDedicatedBinding { get; private set; }

        internal void Set(
            string materialName,
            StyleMaterialClassification classification,
            StyleMaterialRole contractRole,
            D3D12CharacterMaterialRole d3dRole,
            StyleStylizedShadingParameters? contextShading,
            StyleMaterialTreatmentParameters? contextTreatment,
            StyleFaceEyeDefaults baseFaceEye,
            bool hasDedicatedBinding)
        {
            MaterialName = materialName;
            Classification = classification;
            ContractRole = contractRole;
            D3DRole = d3dRole;
            ContextShading = contextShading;
            ContextTreatment = contextTreatment;
            BaseFaceEye = baseFaceEye;
            HasDedicatedBinding = hasDedicatedBinding;
            HasValue = true;
        }
    }

    internal static D3D12StylePresetCatalog MergePresetCatalog(
        D3D12StylePresetCatalog catalog,
        D3D12ViewStyleCatalog styleCatalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(styleCatalog);

        if (styleCatalog.Registrations.Count == 0)
            return catalog;

        if (catalog.List().Count == 1 && catalog.Plugins.Count == 0)
            return styleCatalog.ToD3D12PresetCatalog();

        bool allAlreadyPresent = true;
        foreach (var reg in styleCatalog.Registrations)
        {
            if (!catalog.Plugins.TryGetValue(reg.StyleId, out var existing) || !ReferenceEquals(existing, reg))
            {
                allAlreadyPresent = false;
                break;
            }
        }
        if (allAlreadyPresent)
            return catalog;

        var mergedPlugins = new Dictionary<string, D3D12PluginStyleRegistration>(StringComparer.Ordinal);
        foreach (var reg in styleCatalog.Registrations)
            mergedPlugins[reg.StyleId] = reg;
        foreach (var (styleId, reg) in catalog.Plugins)
            mergedPlugins.TryAdd(styleId, reg);

        var local = catalog.List()
            .Where(r => r.Definition is null
                || (!string.Equals(r.StableId, D3D12SurfaceStyleMap.DefaultStyleId, StringComparison.Ordinal)
                    && !mergedPlugins.ContainsKey(r.StableId!)));

        return D3D12StylePresetCatalog.WithPlugins(mergedPlugins.Values, local);
    }

    internal static IReadOnlyList<D3D12ViewPluginMaterialBinding> FilterVisibleBindings(
        SceneRenderingStyleBinding[] selected,
        IReadOnlyList<D3D12ViewPluginMaterialBinding> bindings)
    {
        if (bindings.Count == 0)
            return bindings;

        var visibleObjectIds = new HashSet<Guid>();
        foreach (var instance in selected)
        {
            if (instance.Visible)
                visibleObjectIds.Add(instance.ObjectId);
        }

        return bindings.Where(b => visibleObjectIds.Contains(b.ObjectId)).ToArray();
    }

    private static D3D12PmxMaterialTreatment? ResolveCharacterMaterialTreatment(
        D3D12CharacterMaterialRole d3dRole,
        D3D12ViewCharacterMaterial? charMat,
        StyleMaterialClassification classification,
        StyleMaterialTreatmentParameters? drawTreatmentOverride,
        IRenderingStyleDefinition style,
        StyleMaterialRole contractRole,
        bool hasDedicatedBinding)
    {
        if (d3dRole is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair))
            return null;

        if (charMat?.PmxTreatment is { } explicitTreatment)
        {
            if (explicitTreatment.HairHighlightStrength > 0f && d3dRole != D3D12CharacterMaterialRole.Hair)
                throw new ArgumentException("Generic hair highlights require a Hair material role.", nameof(charMat));
            return explicitTreatment;
        }

        if (drawTreatmentOverride is { } drawTreatment && !D3D12StylePluginAdapter.IsIdentityTreatment(drawTreatment))
            return D3D12StylePluginAdapter.ToD3D12Treatment(drawTreatment);

        if (classification.TreatmentOverride is { } classTreatment && !D3D12StylePluginAdapter.IsIdentityTreatment(classTreatment))
            return D3D12StylePluginAdapter.ToD3D12Treatment(classTreatment);

        if (!hasDedicatedBinding
            && style.DefaultRoleTreatments.TryGetValue(contractRole, out var defaultTreatment)
            && !D3D12StylePluginAdapter.IsIdentityTreatment(defaultTreatment))
            return D3D12StylePluginAdapter.ToD3D12Treatment(defaultTreatment);

        return null;
    }

    internal static SceneRenderingStyleBinding[] Validate(OriginalFrameScene scene, D3D12StylePresetCatalog catalog,
        IEnumerable<SceneRenderingStyleBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(bindings);
        var selected = bindings.ToArray();
        if (selected.Length == 0) return selected;
        int materialCount = scene.Materials.Count;
        if (materialCount != scene.Surface.Materials.Count || materialCount != scene.Surface.Snapshot.Materials.Count)
            throw new ArgumentException("Frame flags, surface bindings and scene materials must have matching counts.", nameof(scene));
        var objectIds = new HashSet<Guid>();
        foreach (var binding in selected)
        {
            if (binding is null || binding.ObjectId == Guid.Empty || !objectIds.Add(binding.ObjectId))
                throw new ArgumentException("Rendering style bindings require unique, nonempty scene instance identities.", nameof(bindings));
            if (!Enum.IsDefined(binding.Role))
                throw new ArgumentException("Each rendering style binding requires an explicit, defined model role.", nameof(bindings));
            // Constructing a reference also rejects the otherwise possible default(struct) value.
            _ = new RenderingPresetReference(binding.Preset.StableId);
            if (binding.MaterialStart < 0 || binding.MaterialStart > materialCount || binding.MaterialCount < 0
                || binding.MaterialCount > materialCount - binding.MaterialStart || (!binding.Visible && binding.MaterialCount != 0))
                throw new ArgumentException("Visible material ranges must be within the frame; hidden instances must have zero materials.", nameof(bindings));
        }
        int end = 0;
        foreach (var binding in selected.Where(static binding => binding.Visible && binding.MaterialCount > 0)
            .OrderBy(static binding => binding.MaterialStart))
        {
            if (binding.MaterialStart != end)
                throw new ArgumentException("Visible rendering style bindings must cover every frame material exactly once without gaps or overlap.", nameof(bindings));
            end += binding.MaterialCount;
        }
        if (end != materialCount)
            throw new ArgumentException("Visible rendering style bindings must cover every frame material exactly once.", nameof(bindings));
        return selected;
    }

    internal static D3D12ViewRenderingStylesPreflight ResolveAvailability(D3D12StylePresetCatalog catalog,
        SceneRenderingStyleBinding[] selected)
    {
        var failures = new List<D3D12ViewRenderingStyleFailure>();
        foreach (var group in selected.GroupBy(static binding => binding.Preset))
        {
            var resolution = catalog.Find(group.Key.StableId);
            if (!resolution.IsAvailable)
                failures.Add(new(group.Key, resolution, group.Select(static binding => binding.ObjectId)));
        }
        return new(failures);
    }
}

/// <summary>
/// Instance-level cache for <see cref="D3D12ViewRenderingStyles.Resolve"/>.
/// Reuses detached configuration maps, validated bindings, and immutable <see cref="D3D12SurfaceStyleMap"/>
/// results without per-frame allocation when inputs are unchanged, while recomputing accurately on any change.
/// </summary>
internal sealed class D3D12ViewRenderingStylesCache
{
    private readonly object gate = new();
    private readonly bool hasFixedConfig;
    private PreparedConfig? config;
    private PreparedBindings? bindingsState;
    private PluginFrameCache? pluginFrame;
    private OriginalFrameScene? lastExactScene;
    private IReadOnlyDictionary<Guid, SceneObjectRenderInputs>? lastExactSceneInputs;
    private D3D12ViewRenderingStylesResult? lastExactResult;

    public D3D12ViewRenderingStylesCache()
    {
    }

    public D3D12ViewRenderingStylesCache(
        D3D12StylePresetCatalog effectivePresets,
        IReadOnlyList<D3D12ViewCharacterMaterial> characterMaterials,
        IReadOnlyList<D3D12ViewPluginMaterialBinding> pluginBindings,
        IReadOnlyList<D3D12ViewOutlineWidthBinding> outlineWidths,
        D3D12ViewStyleCatalog styleCatalog,
        IReadOnlyDictionary<string, StyleMaterialBindingData>? styleGlobalParameters,
        bool guessMaterialRoles = true,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? boneIndicesByObject = null)
    {
        ArgumentNullException.ThrowIfNull(effectivePresets);
        ArgumentNullException.ThrowIfNull(characterMaterials);
        ArgumentNullException.ThrowIfNull(pluginBindings);
        ArgumentNullException.ThrowIfNull(outlineWidths);
        ArgumentNullException.ThrowIfNull(styleCatalog);
        hasFixedConfig = true;
        config = BuildConfigFromDetached(
            effectivePresets,
            styleCatalog,
            characterMaterials,
            pluginBindings,
            outlineWidths,
            styleGlobalParameters,
            guessMaterialRoles,
            boneIndicesByObject);
    }

    public D3D12ViewRenderingStylesResult Resolve(
        OriginalFrameScene scene,
        IEnumerable<SceneRenderingStyleBinding> bindings,
        IReadOnlyDictionary<Guid, SceneObjectRenderInputs>? sceneInputs = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(bindings);
        if (!hasFixedConfig || config is null)
            throw new InvalidOperationException("Fixed style configuration is required for this overload.");

        lock (gate)
        {
            return ResolveLocked(config, scene, bindings, sceneInputs);
        }
    }

    public D3D12ViewRenderingStylesResult Resolve(
        OriginalFrameScene scene,
        D3D12StylePresetCatalog catalog,
        IEnumerable<SceneRenderingStyleBinding> bindings,
        IEnumerable<D3D12ViewCharacterMaterial>? characterMaterials = null,
        IEnumerable<D3D12ViewPluginMaterialBinding>? pluginBindings = null,
        IReadOnlyDictionary<Guid, SceneObjectRenderInputs>? sceneInputs = null,
        IEnumerable<D3D12ViewOutlineWidthBinding>? outlineWidths = null,
        D3D12ViewStyleCatalog? styleCatalog = null,
        IReadOnlyDictionary<string, StyleMaterialBindingData>? styleGlobalParameters = null,
        bool guessMaterialRoles = true,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? boneIndicesByObject = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(bindings);
        var effectiveStyleCatalog = styleCatalog ?? D3D12ViewStyleCatalog.Empty;

        lock (gate)
        {
            if (config is { } current && ConfigMatches(
                current,
                catalog,
                effectiveStyleCatalog,
                characterMaterials,
                pluginBindings,
                outlineWidths,
                styleGlobalParameters,
                guessMaterialRoles,
                boneIndicesByObject))
            {
                return ResolveLocked(current, scene, bindings, sceneInputs);
            }

            var selected = bindingsState is { } bs && SameBindings(bindings, bs.Selected) && scene.Materials.Count == bs.TotalMaterials
                ? bs.Selected
                : D3D12ViewRenderingStyles.Validate(scene, catalog, bindings);

            var detachedOutlineWidths = D3D12ViewOutlineWidths.Detach(outlineWidths);
            var outlineScales = D3D12ViewOutlineWidths.Resolve(selected, detachedOutlineWidths);
            var nextConfig = BuildConfig(
                catalog,
                effectiveStyleCatalog,
                characterMaterials,
                pluginBindings,
                detachedOutlineWidths,
                styleGlobalParameters,
                guessMaterialRoles,
                boneIndicesByObject);
            var nextState = BuildBindingsState(nextConfig, scene, selected, outlineScales);

            config = nextConfig;
            bindingsState = nextState;
            pluginFrame = null;
            lastExactScene = null;
            lastExactSceneInputs = null;
            lastExactResult = null;
            return ResolveWithBindingsStateLocked(nextConfig, nextState, scene, sceneInputs);
        }
    }

    private D3D12ViewRenderingStylesResult ResolveLocked(
        PreparedConfig cfg,
        OriginalFrameScene scene,
        IEnumerable<SceneRenderingStyleBinding> bindings,
        IReadOnlyDictionary<Guid, SceneObjectRenderInputs>? sceneInputs)
    {
        if (bindingsState is { } existing
            && lastExactResult is { } exact
            && ReferenceEquals(scene, lastExactScene)
            && SameBindings(bindings, existing.Selected)
            && (!existing.UsesPluginStyle || SameSceneInputs(sceneInputs, lastExactSceneInputs, existing.PluginObjectIds)))
        {
            return exact;
        }

        PreparedBindings bs;
        if (bindingsState is { } current && SameBindings(bindings, current.Selected) && scene.Materials.Count == current.TotalMaterials)
        {
            bs = current;
        }
        else
        {
            var selected = D3D12ViewRenderingStyles.Validate(scene, cfg.RawCatalog, bindings);
            var outlineScales = D3D12ViewOutlineWidths.Resolve(selected, cfg.OutlineWidths);
            bs = BuildBindingsState(cfg, scene, selected, outlineScales);
            bindingsState = bs;
            pluginFrame = null;
            lastExactScene = null;
            lastExactSceneInputs = null;
            lastExactResult = null;
        }

        return ResolveWithBindingsStateLocked(cfg, bs, scene, sceneInputs);
    }

    private D3D12ViewRenderingStylesResult ResolveWithBindingsStateLocked(
        PreparedConfig cfg,
        PreparedBindings bs,
        OriginalFrameScene scene,
        IReadOnlyDictionary<Guid, SceneObjectRenderInputs>? sceneInputs)
    {
        int totalMaterials = scene.Materials.Count;
        if (totalMaterials != scene.Surface.Materials.Count || totalMaterials != scene.Surface.Snapshot.Materials.Count)
            throw new ArgumentException("Frame flags, surface bindings and scene materials must have matching counts.", nameof(scene));
        if (scene.Outline is not null && totalMaterials != scene.Outline.Materials.Count)
            throw new ArgumentException("One surface style entry per frame flag, surface binding and scene material is required.", "frame");

        if (!bs.UsesPluginStyle)
        {
            if (bs.RequiresOptionsForPreset && scene.Options is null)
                throw new NotSupportedException("Character main-light style presets require explicit modern frame options; the simple legacy path does not consume the character toon response.");
            if (bs.RequiresOptionsForSurface && scene.Options is null)
                throw new NotSupportedException("Non-MMD surface shader styles require explicit modern frame options.");

            var surfaceStyles = bs.GetOrUpdateStaticSurfaceStyles(cfg, scene.Surface.Snapshot.Materials);
            var resultScene = bs.ResolveSceneMaterials(cfg, scene, surfaceStyles, out _);
            var result = new D3D12ViewRenderingStylesResult(resultScene, surfaceStyles, bs.StaticDiagnostics);
            lastExactScene = scene;
            lastExactSceneInputs = null;
            lastExactResult = result;
            return result;
        }

        if (pluginFrame is { } pf && pf.Matches(scene, sceneInputs, bs))
        {
            if (bs.RequiresOptionsForPreset && scene.Options is null)
                throw new NotSupportedException("Character main-light style presets require explicit modern frame options; the simple legacy path does not consume the character toon response.");
            if (scene.Options is null)
                throw new NotSupportedException("Non-MMD surface shader styles require explicit modern frame options.");

            var snapshot = scene.Surface.Snapshot;
            if (pf.NeedsAdditionalUvs && snapshot.AdditionalUvs is null)
                throw new ArgumentException("Plugin shader requires SubTextureUv (UV1 stream), which is absent from the snapshot.", "frame");
            if (pf.MaxPluginTextureIndex >= snapshot.Textures.Count)
                throw new ArgumentOutOfRangeException("CharacterMaterials",
                    $"Plugin texture index {pf.MaxPluginTextureIndex} is outside the actual frame texture table.");
            if (pf.NeedsRawRadialSeeds)
                _ = D3D12TangentCornerGeometry.GetOrComputeRawRadialSeeds(snapshot, out _);

            var resultScene = bs.ResolveSceneMaterials(cfg, scene, pf.SurfaceStyles, out bool materialsReused);
            if ((pf.HasEmptyMikkBufferBinding && !ReferenceEquals(snapshot, pf.LastValidatedSnapshot))
                || (pf.HasEffectiveTreatment && !materialsReused))
            {
                pf.SurfaceStyles.Validate(resultScene);
                pf.LastValidatedSnapshot = snapshot;
            }

            var cachedResult = new D3D12ViewRenderingStylesResult(resultScene, pf.SurfaceStyles, bs.StaticDiagnostics);
            lastExactScene = scene;
            lastExactSceneInputs = sceneInputs;
            lastExactResult = cachedResult;
            return cachedResult;
        }

        return ResolvePluginMissLocked(cfg, bs, scene, sceneInputs);
    }

    private D3D12ViewRenderingStylesResult ResolvePluginMissLocked(
        PreparedConfig cfg,
        PreparedBindings bs,
        OriginalFrameScene scene,
        IReadOnlyDictionary<Guid, SceneObjectRenderInputs>? sceneInputs)
    {
        int totalMaterials = scene.Materials.Count;
        var styleIds = new string[totalMaterials];
        Array.Copy(bs.BaseStyleIds, styleIds, totalMaterials);
        var materialStyles = new D3D12CharacterMaterialStyle[totalMaterials];
        Array.Copy(bs.BaseMaterialStyles, materialStyles, totalMaterials);
        var stylizedRoles = new D3D12CharacterMaterialRole[totalMaterials];
        Array.Copy(bs.BaseStylizedRoles, stylizedRoles, totalMaterials);

        var snapshot = scene.Surface.Snapshot;
        var inputsMap = sceneInputs ?? SceneObjectRenderInputs.Empty;
        Vector3 primaryLightDirection = Vector3.Normalize(snapshot.LightDirection);
        Vector3 primaryLightColor = scene.Surface.Profile.LightAmbient;
        Vector3 cameraPosition = (scene.Options?.CameraOverride ?? snapshot.Camera).Eye;

        List<int>? observedTextureIndices = null;
        for (int i = 0; i < bs.OrderedPluginOrDeferredSteps.Length; i++)
        {
            var step = bs.OrderedPluginOrDeferredSteps[i];
            if (step.MissingStyleObjectId is { } missingObjectId)
            {
                throw new NotSupportedException(
                    $"Style '{step.MissingStylePipelineId}' has active material bindings on object '{missingObjectId:D}' but no loaded IRenderingStyleDefinition in the style catalog.");
            }

            var slot = step.PluginSlot!;
            observedTextureIndices ??= [];
            var resolvedStyle = D3D12ViewRenderingStyles.BuildPluginMaterialStyle(
                slot.Style,
                slot.ObjectId,
                slot.LocalMaterialIndex,
                slot.PipelineId,
                scene,
                snapshot,
                slot.FrameIndex,
                slot.CharMat,
                slot.ActiveBindings,
                inputsMap,
                cfg.BoneIndices,
                slot.GlobalParams,
                primaryLightDirection,
                primaryLightColor,
                cameraPosition,
                cfg.GuessMaterialRoles,
                slot.ClassificationCache,
                observedTextureIndices);
            materialStyles[slot.FrameIndex] = resolvedStyle;
            stylizedRoles[slot.FrameIndex] = resolvedStyle.Role;
        }

        var surfaceStyles = new D3D12SurfaceStyleMap(styleIds, materialStyles, cfg.EffectiveCatalog)
        {
            StylizedRoles = Array.AsReadOnly(stylizedRoles),
        };

        var resultScene = bs.ApplyTransformations(cfg, scene);
        surfaceStyles.Validate(resultScene);
        bs.CommitMaterialTransformCache(scene, resultScene);

        pluginFrame = PluginFrameCache.Capture(
            scene,
            inputsMap,
            bs,
            surfaceStyles,
            primaryLightDirection,
            primaryLightColor,
            cameraPosition,
            observedTextureIndices);

        var result = new D3D12ViewRenderingStylesResult(resultScene, surfaceStyles, bs.StaticDiagnostics);
        lastExactScene = scene;
        lastExactSceneInputs = sceneInputs;
        lastExactResult = result;
        return result;
    }

    private static PreparedConfig BuildConfigFromDetached(
        D3D12StylePresetCatalog effectivePresets,
        D3D12ViewStyleCatalog styleCatalog,
        IReadOnlyList<D3D12ViewCharacterMaterial> characterMaterials,
        IReadOnlyList<D3D12ViewPluginMaterialBinding> pluginBindings,
        IReadOnlyList<D3D12ViewOutlineWidthBinding> outlineWidths,
        IReadOnlyDictionary<string, StyleMaterialBindingData>? styleGlobalParameters,
        bool guessMaterialRoles,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? boneIndicesByObject)
    {
        return new PreparedConfig
        {
            RawCatalog = effectivePresets,
            StyleCatalog = styleCatalog,
            EffectiveCatalog = effectivePresets,
            Materials = characterMaterials,
            PluginBindings = pluginBindings,
            OutlineWidths = outlineWidths,
            Globals = styleGlobalParameters,
            GlobalKeys = styleGlobalParameters is { Count: > 0 } ? styleGlobalParameters.Keys.ToArray() : null,
            GuessMaterialRoles = guessMaterialRoles,
            BoneIndices = boneIndicesByObject,
            BoneSnapshot = null,
            CharMatMap = D3D12ViewRenderingStyles.BuildCharacterMaterialMap(characterMaterials),
            PluginBindingMap = D3D12ViewRenderingStyles.BuildPluginBindingMap(pluginBindings),
        };
    }

    private static PreparedConfig BuildConfig(
        D3D12StylePresetCatalog catalog,
        D3D12ViewStyleCatalog effectiveStyleCatalog,
        IEnumerable<D3D12ViewCharacterMaterial>? characterMaterials,
        IEnumerable<D3D12ViewPluginMaterialBinding>? pluginBindings,
        IReadOnlyList<D3D12ViewOutlineWidthBinding> detachedOutlineWidths,
        IReadOnlyDictionary<string, StyleMaterialBindingData>? styleGlobalParameters,
        bool guessMaterialRoles,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? boneIndicesByObject)
    {
        var effectiveCatalog = D3D12ViewRenderingStyles.MergePresetCatalog(catalog, effectiveStyleCatalog);
        var detachedMaterials = D3D12ViewCharacterMaterials.Detach(characterMaterials);
        var detachedPluginBindings = D3D12ViewPluginMaterialBindings.Detach(pluginBindings);
        var detachedGlobals = D3D12ViewPluginMaterialBindings.DetachGlobalParameters(styleGlobalParameters);
        var (boneDict, boneSnapshot) = SnapshotBoneIndices(boneIndicesByObject);

        return new PreparedConfig
        {
            RawCatalog = catalog,
            StyleCatalog = effectiveStyleCatalog,
            EffectiveCatalog = effectiveCatalog,
            Materials = detachedMaterials,
            PluginBindings = detachedPluginBindings,
            OutlineWidths = detachedOutlineWidths,
            Globals = detachedGlobals,
            GlobalKeys = detachedGlobals is { Count: > 0 } ? detachedGlobals.Keys.ToArray() : null,
            GuessMaterialRoles = guessMaterialRoles,
            BoneIndices = boneDict,
            BoneSnapshot = boneSnapshot,
            CharMatMap = D3D12ViewRenderingStyles.BuildCharacterMaterialMap(detachedMaterials),
            PluginBindingMap = D3D12ViewRenderingStyles.BuildPluginBindingMap(detachedPluginBindings),
        };
    }

    private static PreparedBindings BuildBindingsState(
        PreparedConfig cfg,
        OriginalFrameScene scene,
        SceneRenderingStyleBinding[] selected,
        D3D12ViewOutlineWidths.MaterialScale[] outlineScales)
    {
        var diagnostics = new List<string>();
        D3D12ViewCharacterMaterials.ValidateObjects(selected, cfg.Materials, diagnostics);
        D3D12ViewPluginMaterialBindings.ValidateSceneObjects(selected, cfg.PluginBindings, cfg.EffectiveCatalog, cfg.StyleCatalog, cfg.Materials);
        D3D12ViewPluginMaterialBindings.Validate(cfg.Materials, D3D12ViewRenderingStyles.FilterVisibleBindings(selected, cfg.PluginBindings), cfg.StyleCatalog);
        D3D12ViewRenderingStyles.ResolveAvailability(cfg.EffectiveCatalog, selected).EnsureAvailable();

        int totalMaterials = scene.Materials.Count;
        var baseStyleIds = new string[totalMaterials];
        Array.Fill(baseStyleIds, D3D12SurfaceStyleMap.DefaultStyleId);
        var baseMaterialStyles = new D3D12CharacterMaterialStyle[totalMaterials];
        Array.Fill(baseMaterialStyles, new D3D12CharacterMaterialStyle());
        var baseStylizedRoles = new D3D12CharacterMaterialRole[totalMaterials];

        if (selected.Length == 0)
        {
            var defaultMap = D3D12ViewRenderingStyles.GetOrCreateDefaultSurfaceStyleMap(cfg.EffectiveCatalog, totalMaterials);
            return new PreparedBindings
            {
                Selected = selected,
                TotalMaterials = totalMaterials,
                OutlineScales = outlineScales,
                BaseStyleIds = baseStyleIds,
                BaseMaterialStyles = baseMaterialStyles,
                BaseStylizedRoles = baseStylizedRoles,
                StaticDiagnostics = diagnostics.Count == 0 ? D3D12ViewRenderingStylesResult.EmptyDiagnostics : Array.AsReadOnly(diagnostics.ToArray()),
                StaticSurfaceStyles = defaultMap,
                UsesPluginStyle = false,
                UsesFallbackRoleGuessing = false,
                RequiresOptionsForPreset = false,
                RequiresOptionsForSurface = false,
                CanModifySceneMaterials = outlineScales.Length > 0,
                HasEffectiveTreatment = false,
                PluginSlots = Array.Empty<PreparedPluginSlot>(),
                PluginObjectIds = Array.Empty<Guid>(),
                OrderedPluginOrDeferredSteps = Array.Empty<OrderedPluginStep>(),
                FallbackSlots = Array.Empty<PreparedFallbackSlot>(),
            };
        }

        bool requiresOptionsForPreset = false;
        bool requiresOptionsForSurface = false;
        bool hasNonMmdVisibleCharacterPreset = false;
        bool hasAnyNonDefaultSurfaceStyle = false;
        bool hasEffectiveTreatment = false;

        var pluginSlots = new List<PreparedPluginSlot>();
        var orderedSteps = new List<OrderedPluginStep>();
        var pluginObjectIds = new List<Guid>();
        var fallbackSlots = new List<PreparedFallbackSlot>();
        var snapshotMaterials = scene.Surface.Snapshot.Materials;

        foreach (var instance in selected)
        {
            var resolution = cfg.EffectiveCatalog.Find(instance.Preset.StableId);
            var definition = resolution.Definition!;
            string pipelineId = definition.PipelineId;

            if (instance.Visible && instance.MaterialCount > 0 && instance.Role == ModelRole.Character && definition.StableId != D3D12SurfaceStyleMap.DefaultStyleId)
            {
                hasNonMmdVisibleCharacterPreset = true;
                if (definition.CharacterToonResponse is not null)
                    requiresOptionsForPreset = true;
            }

            if (string.Equals(pipelineId, D3D12SurfaceStyleMap.DefaultStyleId, StringComparison.Ordinal))
            {
                if (instance.Visible && instance.Role == ModelRole.Character && cfg.CharMatMap.Count > 0)
                {
                    for (int localIdx = 0; localIdx < instance.MaterialCount; localIdx++)
                    {
                        if (!cfg.CharMatMap.TryGetValue((instance.ObjectId, localIdx), out var cm))
                            continue;
                        int frameIdx = instance.MaterialStart + localIdx;
                        baseMaterialStyles[frameIdx] = new D3D12CharacterMaterialStyle(
                            Role: cm.Role,
                            FaceNormalShadeStrength: cm.FaceNormalShadeStrength,
                            FaceGeometricShadowStrength: cm.FaceGeometricShadowStrength,
                            EyeGeometricShadowStrength: cm.EyeGeometricShadowStrength);
                        baseStylizedRoles[frameIdx] = cm.Role;
                    }
                }
                continue;
            }

            if (!instance.Visible || instance.MaterialCount == 0)
            {
                diagnostics.Add($"Hidden or empty object '{instance.ObjectId:D}' consumes no surface materials. {resolution.Diagnostic}");
                continue;
            }

            if (instance.Role != ModelRole.Character)
            {
                diagnostics.Add($"Object '{instance.ObjectId:D}' ({instance.Role}) keeps the MMD surface: {resolution.Diagnostic}");
                continue;
            }

            requiresOptionsForSurface = true;
            hasAnyNonDefaultSurfaceStyle = true;
            diagnostics.Add($"Object '{instance.ObjectId:D}': {resolution.Diagnostic}");
            Array.Fill(baseStyleIds, pipelineId, instance.MaterialStart, instance.MaterialCount);

            var style = cfg.StyleCatalog.FindStyle(pipelineId);
            StyleMaterialBindingData? globalParams = cfg.Globals is not null && cfg.Globals.TryGetValue(pipelineId, out var gp)
                ? gp
                : null;

            bool addedPluginObjectId = false;
            for (int localIdx = 0; localIdx < instance.MaterialCount; localIdx++)
            {
                int frameIdx = instance.MaterialStart + localIdx;
                cfg.CharMatMap.TryGetValue((instance.ObjectId, localIdx), out var charMat);
                var activeBindings = D3D12ViewRenderingStyles.GetActiveBindings(cfg.PluginBindingMap, instance.ObjectId, localIdx, pipelineId);

                if (style is null)
                {
                    if (activeBindings.Length > 0)
                    {
                        if (pluginSlots.Count == 0)
                        {
                            throw new NotSupportedException(
                                $"Style '{pipelineId}' has active material bindings on object '{instance.ObjectId:D}' but no loaded IRenderingStyleDefinition in the style catalog.");
                        }
                        orderedSteps.Add(new OrderedPluginStep(null, instance.ObjectId, pipelineId));
                        continue;
                    }

                    bool needsGuess = charMat is not { ExplicitRole: true } && cfg.GuessMaterialRoles;
                    if (needsGuess)
                        fallbackSlots.Add(new PreparedFallbackSlot(frameIdx, charMat));
                    var fallbackStyle = D3D12ViewRenderingStyles.BuildFallbackMaterialStyle(
                        snapshotMaterials[frameIdx].Name, charMat, cfg.GuessMaterialRoles);
                    baseMaterialStyles[frameIdx] = fallbackStyle;
                    baseStylizedRoles[frameIdx] = fallbackStyle.Role;
                    if (fallbackStyle.PmxMaterialTreatment is { } tr
                        && (tr.Saturation != 1 || tr.TransitionWidthScale != 1 || tr.ValueContrast != 0 || tr.ShadeGamma != 1 || tr.HairHighlightStrength != 0)
                        && fallbackStyle.Role is D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair)
                    {
                        hasEffectiveTreatment = true;
                    }
                    continue;
                }

                if (!addedPluginObjectId)
                {
                    pluginObjectIds.Add(instance.ObjectId);
                    addedPluginObjectId = true;
                }

                var slot = new PreparedPluginSlot(
                    frameIdx,
                    instance.ObjectId,
                    localIdx,
                    pipelineId,
                    style,
                    charMat,
                    activeBindings,
                    globalParams);
                pluginSlots.Add(slot);
                orderedSteps.Add(new OrderedPluginStep(slot, null, null));
            }
        }

        bool usesPluginStyle = pluginSlots.Count > 0;
        bool usesFallbackRoleGuessing = fallbackSlots.Count > 0;
        D3D12SurfaceStyleMap? staticSurfaceStyles = null;
        string[]? fallbackMaterialNames = null;

        if (!usesPluginStyle)
        {
            if (!hasAnyNonDefaultSurfaceStyle && cfg.CharMatMap.Count == 0)
            {
                staticSurfaceStyles = D3D12ViewRenderingStyles.GetOrCreateDefaultSurfaceStyleMap(cfg.EffectiveCatalog, totalMaterials);
            }
            else
            {
                staticSurfaceStyles = new D3D12SurfaceStyleMap(baseStyleIds, baseMaterialStyles, cfg.EffectiveCatalog)
                {
                    StylizedRoles = Array.AsReadOnly(baseStylizedRoles),
                };
                foreach (var plugin in staticSurfaceStyles.RegisteredStyles.Values)
                    plugin.Validate();
            }

            if (usesFallbackRoleGuessing)
            {
                fallbackMaterialNames = new string[fallbackSlots.Count];
                for (int i = 0; i < fallbackSlots.Count; i++)
                    fallbackMaterialNames[i] = snapshotMaterials[fallbackSlots[i].FrameIndex].Name;
            }
        }

        return new PreparedBindings
        {
            Selected = selected,
            TotalMaterials = totalMaterials,
            OutlineScales = outlineScales,
            BaseStyleIds = baseStyleIds,
            BaseMaterialStyles = baseMaterialStyles,
            BaseStylizedRoles = baseStylizedRoles,
            StaticDiagnostics = diagnostics.Count == 0 ? D3D12ViewRenderingStylesResult.EmptyDiagnostics : Array.AsReadOnly(diagnostics.ToArray()),
            StaticSurfaceStyles = staticSurfaceStyles,
            FallbackMaterialNames = fallbackMaterialNames,
            UsesPluginStyle = usesPluginStyle,
            UsesFallbackRoleGuessing = usesFallbackRoleGuessing,
            RequiresOptionsForPreset = requiresOptionsForPreset,
            RequiresOptionsForSurface = requiresOptionsForSurface,
            CanModifySceneMaterials = hasNonMmdVisibleCharacterPreset || outlineScales.Length > 0,
            HasEffectiveTreatment = hasEffectiveTreatment,
            PluginSlots = pluginSlots.ToArray(),
            PluginObjectIds = pluginObjectIds.ToArray(),
            OrderedPluginOrDeferredSteps = orderedSteps.ToArray(),
            FallbackSlots = fallbackSlots.ToArray(),
        };
    }

    private static bool ConfigMatches(
        PreparedConfig current,
        D3D12StylePresetCatalog catalog,
        D3D12ViewStyleCatalog styleCatalog,
        IEnumerable<D3D12ViewCharacterMaterial>? characterMaterials,
        IEnumerable<D3D12ViewPluginMaterialBinding>? pluginBindings,
        IEnumerable<D3D12ViewOutlineWidthBinding>? outlineWidths,
        IReadOnlyDictionary<string, StyleMaterialBindingData>? styleGlobalParameters,
        bool guessMaterialRoles,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? boneIndicesByObject)
    {
        return ReferenceEquals(catalog, current.RawCatalog)
            && ReferenceEquals(styleCatalog, current.StyleCatalog)
            && guessMaterialRoles == current.GuessMaterialRoles
            && MatchesCharacterMaterials(characterMaterials, current.Materials)
            && MatchesOutlineWidths(outlineWidths, current.OutlineWidths)
            && MatchesPluginBindings(pluginBindings, current.PluginBindings)
            && MatchesGlobals(styleGlobalParameters, current.Globals, current.GlobalKeys)
            && MatchesBoneIndices(boneIndicesByObject, current.BoneIndices, current.BoneSnapshot);
    }

    private static bool MatchesCharacterMaterials(
        IEnumerable<D3D12ViewCharacterMaterial>? input,
        IReadOnlyList<D3D12ViewCharacterMaterial> cached)
    {
        if (ReferenceEquals(input, cached)) return true;
        if (input is null) return cached.Count == 0;
        if (input is IReadOnlyList<D3D12ViewCharacterMaterial> list)
        {
            if (list.Count != cached.Count) return false;
            for (int i = 0; i < cached.Count; i++)
                if (list[i] != cached[i]) return false;
            return true;
        }
        return input is ICollection<D3D12ViewCharacterMaterial> col && col.Count == 0 && cached.Count == 0;
    }

    private static bool MatchesOutlineWidths(
        IEnumerable<D3D12ViewOutlineWidthBinding>? input,
        IReadOnlyList<D3D12ViewOutlineWidthBinding> cached)
    {
        if (ReferenceEquals(input, cached)) return true;
        if (input is null) return cached.Count == 0;
        if (input is IReadOnlyList<D3D12ViewOutlineWidthBinding> list)
        {
            if (list.Count != cached.Count) return false;
            for (int i = 0; i < cached.Count; i++)
            {
                var a = list[i];
                var b = cached[i];
                if (a is null || a.ObjectId != b.ObjectId || a.MaterialIndex != b.MaterialIndex
                    || BitConverter.SingleToInt32Bits(a.Scale) != BitConverter.SingleToInt32Bits(b.Scale))
                    return false;
            }
            return true;
        }
        return input is ICollection<D3D12ViewOutlineWidthBinding> col && col.Count == 0 && cached.Count == 0;
    }

    private static bool MatchesPluginBindings(
        IEnumerable<D3D12ViewPluginMaterialBinding>? input,
        IReadOnlyList<D3D12ViewPluginMaterialBinding> cached)
    {
        if (ReferenceEquals(input, cached)) return true;
        if (input is null) return cached.Count == 0;
        if (input is IReadOnlyList<D3D12ViewPluginMaterialBinding> list)
        {
            if (list.Count != cached.Count) return false;
            for (int i = 0; i < cached.Count; i++)
            {
                var a = list[i];
                var b = cached[i];
                if (a is null || a.ObjectId != b.ObjectId || a.LocalMaterialIndex != b.LocalMaterialIndex
                    || !D3D12StylePluginAdapter.BindingDataEquivalent(a.Binding, b.Binding))
                    return false;
            }
            return true;
        }
        return input is ICollection<D3D12ViewPluginMaterialBinding> col && col.Count == 0 && cached.Count == 0;
    }

    private static bool MatchesGlobals(
        IReadOnlyDictionary<string, StyleMaterialBindingData>? input,
        IReadOnlyDictionary<string, StyleMaterialBindingData>? cached,
        string[]? keys)
    {
        int count = input?.Count ?? 0;
        int cachedCount = cached?.Count ?? 0;
        if (count != cachedCount) return false;
        if (count == 0) return true;
        if (ReferenceEquals(input, cached)) return true;
        if (keys is null || input is null || cached is null) return false;
        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i];
            if (!input.TryGetValue(key, out var val) || !D3D12StylePluginAdapter.BindingDataEquivalent(val, cached[key]))
                return false;
        }
        return true;
    }

    private static bool MatchesBoneIndices(
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? input,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? cached,
        BoneObjectEntry[]? snapshot)
    {
        int count = input?.Count ?? 0;
        int cachedCount = cached?.Count ?? 0;
        if (count != cachedCount) return false;
        if (count == 0) return true;
        if (ReferenceEquals(input, cached) && snapshot is null) return true;
        if (input is null || snapshot is null) return false;
        for (int i = 0; i < snapshot.Length; i++)
        {
            var entry = snapshot[i];
            if (!input.TryGetValue(entry.ObjectId, out var bones) || bones is null || bones.Count != entry.Bones.Length)
                return false;
            for (int j = 0; j < entry.Bones.Length; j++)
            {
                var pair = entry.Bones[j];
                if (!bones.TryGetValue(pair.Name, out int idx) || idx != pair.Index)
                    return false;
            }
        }
        return true;
    }

    private static (IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? Dict, BoneObjectEntry[]? Snapshot) SnapshotBoneIndices(
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? input)
    {
        if (input is null || input.Count == 0)
            return (null, null);

        var copy = new Dictionary<Guid, IReadOnlyDictionary<string, int>>(input.Count);
        var entries = new BoneObjectEntry[input.Count];
        int objIdx = 0;
        foreach (var (objectId, bones) in input)
        {
            if (bones is null)
            {
                copy[objectId] = null!;
                continue;
            }
            var boneCopy = new Dictionary<string, int>(bones.Count, StringComparer.Ordinal);
            var pairList = new (string Name, int Index)[bones.Count];
            int boneIdx = 0;
            foreach (var (name, idx) in bones)
            {
                boneCopy[name] = idx;
                pairList[boneIdx++] = (name, idx);
            }
            copy[objectId] = new ReadOnlyDictionary<string, int>(boneCopy);
            entries[objIdx++] = new BoneObjectEntry(objectId, pairList);
        }
        return (new ReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>(copy), entries);
    }

    private static bool SameBindings(IEnumerable<SceneRenderingStyleBinding> input, SceneRenderingStyleBinding[] cached)
    {
        if (ReferenceEquals(input, cached)) return true;
        if (input is IReadOnlyList<SceneRenderingStyleBinding> list)
        {
            if (list.Count != cached.Length) return false;
            for (int i = 0; i < cached.Length; i++)
                if (list[i] != cached[i]) return false;
            return true;
        }
        return input is ICollection<SceneRenderingStyleBinding> col && col.Count == 0 && cached.Length == 0;
    }

    private static bool SameSceneInputs(
        IReadOnlyDictionary<Guid, SceneObjectRenderInputs>? a,
        IReadOnlyDictionary<Guid, SceneObjectRenderInputs>? b,
        Guid[] pluginObjectIds)
    {
        if (ReferenceEquals(a, b)) return true;
        if (pluginObjectIds.Length == 0) return true;
        int countA = a?.Count ?? 0, countB = b?.Count ?? 0;
        if (countA == 0 && countB == 0) return true;
        if (a is null || b is null) return false;
        for (int i = 0; i < pluginObjectIds.Length; i++)
        {
            Guid objectId = pluginObjectIds[i];
            bool hasA = a.TryGetValue(objectId, out var valA);
            bool hasB = b.TryGetValue(objectId, out var valB);
            if (hasA != hasB) return false;
            if (!hasA || ReferenceEquals(valA, valB)) continue;
            if (valA is null || valB is null) return false;
            if (valA.ObjectId != valB.ObjectId || valA.Visible != valB.Visible) return false;
            if (valA.TextureIndices.Count != valB.TextureIndices.Count
                || valA.BoneWorldMatrices.Count != valB.BoneWorldMatrices.Count)
                return false;
            if (valA.TextureIndices.Count > 0)
            {
                foreach (var (k, vA) in valA.TextureIndices)
                {
                    if (!valB.TextureIndices.TryGetValue(k, out int vB) || vA != vB)
                        return false;
                }
            }
            if (valA.BoneWorldMatrices.Count > 0)
            {
                foreach (var (k, mA) in valA.BoneWorldMatrices)
                {
                    if (!valB.BoneWorldMatrices.TryGetValue(k, out var mB) || !SameMatrixBits(mA, mB))
                        return false;
                }
            }
        }
        return true;
    }

    private static bool SameMaterials(
        IReadOnlyList<OriginalFrameMaterialFlags> a,
        ReadOnlyCollection<OriginalFrameMaterialFlags> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (ReferenceEquals(x, y)) continue;
            if (x != y || BitConverter.SingleToInt32Bits(x.OutlineWidthScale) != BitConverter.SingleToInt32Bits(y.OutlineWidthScale))
                return false;
        }
        return true;
    }

    internal static bool SameBits(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    internal static bool SameBits(Vector3 a, Vector3 b) => SameBits(a.X, b.X) && SameBits(a.Y, b.Y) && SameBits(a.Z, b.Z);
    internal static bool SameBits(Vector4 a, Vector4 b) => SameBits(a.X, b.X) && SameBits(a.Y, b.Y) && SameBits(a.Z, b.Z) && SameBits(a.W, b.W);
    internal static bool SameMatrixBits(Matrix4x4 a, Matrix4x4 b)
        => SameBits(a.M11, b.M11) && SameBits(a.M12, b.M12) && SameBits(a.M13, b.M13) && SameBits(a.M14, b.M14)
            && SameBits(a.M21, b.M21) && SameBits(a.M22, b.M22) && SameBits(a.M23, b.M23) && SameBits(a.M24, b.M24)
            && SameBits(a.M31, b.M31) && SameBits(a.M32, b.M32) && SameBits(a.M33, b.M33) && SameBits(a.M34, b.M34)
            && SameBits(a.M41, b.M41) && SameBits(a.M42, b.M42) && SameBits(a.M43, b.M43) && SameBits(a.M44, b.M44);

    private readonly record struct BoneObjectEntry(Guid ObjectId, (string Name, int Index)[] Bones);
    private readonly record struct PreparedFallbackSlot(int FrameIndex, D3D12ViewCharacterMaterial? CharMat);
    private readonly record struct OrderedPluginStep(PreparedPluginSlot? PluginSlot, Guid? MissingStyleObjectId, string? MissingStylePipelineId);

    private sealed class PreparedConfig
    {
        public required D3D12StylePresetCatalog RawCatalog { get; init; }
        public required D3D12ViewStyleCatalog StyleCatalog { get; init; }
        public required D3D12StylePresetCatalog EffectiveCatalog { get; init; }
        public required IReadOnlyList<D3D12ViewCharacterMaterial> Materials { get; init; }
        public required IReadOnlyList<D3D12ViewPluginMaterialBinding> PluginBindings { get; init; }
        public required IReadOnlyList<D3D12ViewOutlineWidthBinding> OutlineWidths { get; init; }
        public required IReadOnlyDictionary<string, StyleMaterialBindingData>? Globals { get; init; }
        public required string[]? GlobalKeys { get; init; }
        public required bool GuessMaterialRoles { get; init; }
        public required IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? BoneIndices { get; init; }
        public required BoneObjectEntry[]? BoneSnapshot { get; init; }
        public required Dictionary<(Guid ObjectId, int LocalMaterialIndex), D3D12ViewCharacterMaterial> CharMatMap { get; init; }
        public required Dictionary<(Guid ObjectId, int LocalMaterialIndex), D3D12ViewPluginMaterialBinding[]> PluginBindingMap { get; init; }
    }

    private sealed class PreparedPluginSlot(
        int frameIndex,
        Guid objectId,
        int localMaterialIndex,
        string pipelineId,
        IRenderingStyleDefinition style,
        D3D12ViewCharacterMaterial? charMat,
        StyleMaterialBindingData[] activeBindings,
        StyleMaterialBindingData? globalParams)
    {
        public int FrameIndex { get; } = frameIndex;
        public Guid ObjectId { get; } = objectId;
        public int LocalMaterialIndex { get; } = localMaterialIndex;
        public string PipelineId { get; } = pipelineId;
        public IRenderingStyleDefinition Style { get; } = style;
        public D3D12ViewCharacterMaterial? CharMat { get; } = charMat;
        public StyleMaterialBindingData[] ActiveBindings { get; } = activeBindings;
        public StyleMaterialBindingData? GlobalParams { get; } = globalParams;
        public D3D12ViewRenderingStyles.PluginSlotClassificationCache ClassificationCache { get; } = new();
    }

    private sealed class PreparedBindings
    {
        public required SceneRenderingStyleBinding[] Selected { get; init; }
        public required int TotalMaterials { get; init; }
        public required D3D12ViewOutlineWidths.MaterialScale[] OutlineScales { get; init; }
        public required string[] BaseStyleIds { get; init; }
        public required D3D12CharacterMaterialStyle[] BaseMaterialStyles { get; init; }
        public required D3D12CharacterMaterialRole[] BaseStylizedRoles { get; init; }
        public required IReadOnlyList<string> StaticDiagnostics { get; init; }
        public D3D12SurfaceStyleMap? StaticSurfaceStyles { get; set; }
        public string[]? FallbackMaterialNames { get; set; }
        public required bool UsesPluginStyle { get; init; }
        public required bool UsesFallbackRoleGuessing { get; init; }
        public required bool RequiresOptionsForPreset { get; init; }
        public required bool RequiresOptionsForSurface { get; init; }
        public required bool CanModifySceneMaterials { get; init; }
        public required bool HasEffectiveTreatment { get; init; }
        public required PreparedPluginSlot[] PluginSlots { get; init; }
        public required Guid[] PluginObjectIds { get; init; }
        public required OrderedPluginStep[] OrderedPluginOrDeferredSteps { get; init; }
        public required PreparedFallbackSlot[] FallbackSlots { get; init; }

        private ReadOnlyCollection<OriginalFrameMaterialFlags>? lastInputMaterials;
        private IReadOnlyList<OriginalOutlineMaterial>? lastOutlineMaterials;
        private ReadOnlyCollection<OriginalFrameMaterialFlags>? lastResultMaterials;

        public D3D12SurfaceStyleMap GetOrUpdateStaticSurfaceStyles(
            PreparedConfig cfg,
            IReadOnlyList<SceneMaterial> snapshotMaterials)
        {
            if (StaticSurfaceStyles is not null && (!UsesFallbackRoleGuessing || MatchesFallbackMaterialNames(snapshotMaterials)))
                return StaticSurfaceStyles;

            var updatedStyles = (D3D12CharacterMaterialStyle[])BaseMaterialStyles.Clone();
            var updatedRoles = (D3D12CharacterMaterialRole[])BaseStylizedRoles.Clone();
            var names = FallbackMaterialNames ?? new string[FallbackSlots.Length];
            for (int i = 0; i < FallbackSlots.Length; i++)
            {
                var slot = FallbackSlots[i];
                string matName = snapshotMaterials[slot.FrameIndex].Name;
                names[i] = matName;
                var style = D3D12ViewRenderingStyles.BuildFallbackMaterialStyle(matName, slot.CharMat, cfg.GuessMaterialRoles);
                updatedStyles[slot.FrameIndex] = style;
                updatedRoles[slot.FrameIndex] = style.Role;
            }
            FallbackMaterialNames = names;
            var map = new D3D12SurfaceStyleMap(BaseStyleIds, updatedStyles, cfg.EffectiveCatalog)
            {
                StylizedRoles = Array.AsReadOnly(updatedRoles),
            };
            foreach (var plugin in map.RegisteredStyles.Values)
                plugin.Validate();
            StaticSurfaceStyles = map;
            return map;
        }

        private bool MatchesFallbackMaterialNames(IReadOnlyList<SceneMaterial> snapshotMaterials)
        {
            if (FallbackMaterialNames is null) return false;
            for (int i = 0; i < FallbackSlots.Length; i++)
            {
                if (!string.Equals(snapshotMaterials[FallbackSlots[i].FrameIndex].Name, FallbackMaterialNames[i], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        public OriginalFrameScene ResolveSceneMaterials(
            PreparedConfig cfg,
            OriginalFrameScene scene,
            D3D12SurfaceStyleMap surfaceStyles,
            out bool materialsReused)
        {
            if (!CanModifySceneMaterials)
            {
                materialsReused = true;
                return scene;
            }

            var outlineMaterials = scene.Outline?.Materials;
            if (lastInputMaterials is not null
                && SameEdgeFlags(outlineMaterials, lastOutlineMaterials)
                && SameMaterials(scene.Materials, lastInputMaterials))
            {
                materialsReused = true;
                return lastResultMaterials is null
                    ? scene
                    : new OriginalFrameScene(scene.Surface, scene.Outline, scene.OutlineOptions, scene.Options,
                        lastResultMaterials, scene.Lighting, scene.EditorOverlay);
            }

            materialsReused = false;
            var transformed = ApplyTransformations(cfg, scene);
            if (!UsesPluginStyle && HasEffectiveTreatment)
                surfaceStyles.Validate(transformed);
            CommitMaterialTransformCache(scene, transformed);
            return transformed;
        }

        private static bool SameEdgeFlags(IReadOnlyList<OriginalOutlineMaterial>? current, IReadOnlyList<OriginalOutlineMaterial>? previous)
        {
            if (ReferenceEquals(current, previous)) return true;
            if (current is null || previous is null || current.Count != previous.Count) return false;
            for (int i = 0; i < current.Count; i++)
                if (current[i].DrawEnabled != previous[i].DrawEnabled) return false;
            return true;
        }

        public OriginalFrameScene ApplyTransformations(PreparedConfig cfg, OriginalFrameScene scene)
            => D3D12ViewRenderingStyles.ApplySceneTransformations(scene, cfg.EffectiveCatalog, Selected, OutlineScales);

        public void CommitMaterialTransformCache(OriginalFrameScene inputScene, OriginalFrameScene resultScene)
        {
            lastInputMaterials = inputScene.Materials;
            lastOutlineMaterials = inputScene.Outline?.Materials;
            lastResultMaterials = ReferenceEquals(resultScene, inputScene) ? null : resultScene.Materials;
        }
    }

    private sealed class PluginFrameCache
    {
        private readonly Vector3 primaryLightDirection;
        private readonly Vector3 primaryLightColor;
        private readonly Vector3 cameraPosition;
        private readonly int textureCount;
        private readonly (int Index, int Width, int Height)[] observedTextures;
        private readonly MaterialSnapshot[] materialSnapshots;
        private readonly SceneInputSnapshot[] sceneInputSnapshots;

        public D3D12SurfaceStyleMap SurfaceStyles { get; }
        public bool NeedsAdditionalUvs { get; }
        public bool NeedsRawRadialSeeds { get; }
        public bool HasEmptyMikkBufferBinding { get; }
        public bool HasEffectiveTreatment { get; }
        public int MaxPluginTextureIndex { get; }
        public SceneSnapshot LastValidatedSnapshot { get; set; }

        private PluginFrameCache(
            Vector3 primaryLightDirection,
            Vector3 primaryLightColor,
            Vector3 cameraPosition,
            int textureCount,
            (int Index, int Width, int Height)[] observedTextures,
            MaterialSnapshot[] materialSnapshots,
            SceneInputSnapshot[] sceneInputSnapshots,
            D3D12SurfaceStyleMap surfaceStyles,
            bool needsAdditionalUvs,
            bool needsRawRadialSeeds,
            bool hasEmptyMikkBufferBinding,
            bool hasEffectiveTreatment,
            int maxPluginTextureIndex,
            SceneSnapshot lastValidatedSnapshot)
        {
            this.primaryLightDirection = primaryLightDirection;
            this.primaryLightColor = primaryLightColor;
            this.cameraPosition = cameraPosition;
            this.textureCount = textureCount;
            this.observedTextures = observedTextures;
            this.materialSnapshots = materialSnapshots;
            this.sceneInputSnapshots = sceneInputSnapshots;
            SurfaceStyles = surfaceStyles;
            NeedsAdditionalUvs = needsAdditionalUvs;
            NeedsRawRadialSeeds = needsRawRadialSeeds;
            HasEmptyMikkBufferBinding = hasEmptyMikkBufferBinding;
            HasEffectiveTreatment = hasEffectiveTreatment;
            MaxPluginTextureIndex = maxPluginTextureIndex;
            LastValidatedSnapshot = lastValidatedSnapshot;
        }

        public static PluginFrameCache Capture(
            OriginalFrameScene scene,
            IReadOnlyDictionary<Guid, SceneObjectRenderInputs> inputsMap,
            PreparedBindings bs,
            D3D12SurfaceStyleMap surfaceStyles,
            Vector3 primaryLightDirection,
            Vector3 primaryLightColor,
            Vector3 cameraPosition,
            List<int>? observedTextureIndices)
        {
            var snapshot = scene.Surface.Snapshot;
            (int Index, int Width, int Height)[] observedTextures;
            if (observedTextureIndices is null || observedTextureIndices.Count == 0)
            {
                observedTextures = Array.Empty<(int, int, int)>();
            }
            else
            {
                var unique = new HashSet<int>(observedTextureIndices);
                observedTextures = new (int, int, int)[unique.Count];
                int write = 0;
                foreach (int idx in unique)
                {
                    var tex = snapshot.Textures[idx];
                    observedTextures[write++] = (idx, tex.Width, tex.Height);
                }
            }

            var matSnapshots = new MaterialSnapshot[bs.PluginSlots.Length];
            bool needsAdditionalUvs = false;
            bool needsRawRadialSeeds = false;
            bool hasEmptyMikkBufferBinding = false;
            bool hasEffectiveTreatment = false;
            int maxPluginTextureIndex = -1;

            for (int i = 0; i < bs.PluginSlots.Length; i++)
            {
                int frameIdx = bs.PluginSlots[i].FrameIndex;
                var mat = snapshot.Materials[frameIdx];
                var surfaceMat = scene.Surface.Materials[frameIdx];
                var frameFlags = scene.Materials[frameIdx];
                matSnapshots[i] = new MaterialSnapshot(
                    mat.Name,
                    mat.Diffuse,
                    mat.Specular,
                    mat.Shininess,
                    mat.Ambient,
                    mat.DiffuseTexture,
                    surfaceMat.SphereTexture,
                    surfaceMat.SphereMode,
                    mat.ToonTexture,
                    mat.Diffuse.W < 1f || frameFlags.CullTransparentSurface);

                var geom = surfaceStyles.EffectiveGeometryRequirements(frameIdx);
                needsAdditionalUvs |= geom.HasFlag(D3D12StyleGeometryRequirements.SubTextureUv);
                needsRawRadialSeeds |= geom.HasFlag(D3D12StyleGeometryRequirements.RawRadialSeeds);
                if (surfaceStyles.EffectivePmxMaterialTreatment(frameIdx) is not null)
                    hasEffectiveTreatment = true;
                if (surfaceStyles.GetPluginDraw(frameIdx) is { } draw)
                {
                    if (draw.TextureBindings is { Count: > 0 } textures)
                    {
                        for (int j = 0; j < textures.Count; j++)
                        {
                            if (textures[j].SceneTextureIndex > maxPluginTextureIndex)
                                maxPluginTextureIndex = textures[j].SceneTextureIndex;
                        }
                    }
                    if (draw.BufferBindings is { Count: > 0 } buffers)
                    {
                        for (int j = 0; j < buffers.Count; j++)
                        {
                            if (buffers[j].Kind == D3D12PluginBufferKind.MikkTangentCorners && buffers[j].Data.IsEmpty)
                                hasEmptyMikkBufferBinding = true;
                        }
                    }
                }
            }

            var inputSnapshots = new SceneInputSnapshot[bs.PluginObjectIds.Length];
            for (int i = 0; i < bs.PluginObjectIds.Length; i++)
            {
                Guid objectId = bs.PluginObjectIds[i];
                inputSnapshots[i] = inputsMap.TryGetValue(objectId, out var objInputs)
                    ? SceneInputSnapshot.From(objectId, objInputs)
                    : SceneInputSnapshot.Absent(objectId);
            }

            return new PluginFrameCache(
                primaryLightDirection,
                primaryLightColor,
                cameraPosition,
                snapshot.Textures.Count,
                observedTextures,
                matSnapshots,
                inputSnapshots,
                surfaceStyles,
                needsAdditionalUvs,
                needsRawRadialSeeds,
                hasEmptyMikkBufferBinding,
                hasEffectiveTreatment,
                maxPluginTextureIndex,
                snapshot);
        }

        public bool Matches(
            OriginalFrameScene scene,
            IReadOnlyDictionary<Guid, SceneObjectRenderInputs>? sceneInputs,
            PreparedBindings bs)
        {
            var snapshot = scene.Surface.Snapshot;
            if (snapshot.Textures.Count != textureCount)
                return false;

            Vector3 lightDir = Vector3.Normalize(snapshot.LightDirection);
            Vector3 lightColor = scene.Surface.Profile.LightAmbient;
            Vector3 camPos = (scene.Options?.CameraOverride ?? snapshot.Camera).Eye;
            if (!SameBits(lightDir, primaryLightDirection)
                || !SameBits(lightColor, primaryLightColor)
                || !SameBits(camPos, cameraPosition))
                return false;

            for (int i = 0; i < observedTextures.Length; i++)
            {
                var (idx, w, h) = observedTextures[i];
                var tex = snapshot.Textures[idx];
                if (tex.Width != w || tex.Height != h)
                    return false;
            }

            for (int i = 0; i < bs.PluginSlots.Length; i++)
            {
                int frameIdx = bs.PluginSlots[i].FrameIndex;
                var mat = snapshot.Materials[frameIdx];
                var surfaceMat = scene.Surface.Materials[frameIdx];
                var frameFlags = scene.Materials[frameIdx];
                if (!materialSnapshots[i].Matches(mat, surfaceMat, frameFlags))
                    return false;
            }

            var inputsMap = sceneInputs ?? SceneObjectRenderInputs.Empty;
            for (int i = 0; i < sceneInputSnapshots.Length; i++)
            {
                if (!sceneInputSnapshots[i].Matches(inputsMap))
                    return false;
            }

            return true;
        }

        private readonly record struct MaterialSnapshot(
            string Name,
            Vector4 Diffuse,
            Vector3 Specular,
            float Shininess,
            Vector3 Ambient,
            int DiffuseTexture,
            int SphereTexture,
            OriginalSphereMode SphereMode,
            int ToonTexture,
            bool IsTransparent)
        {
            public bool Matches(SceneMaterial mat, OriginalStyleMaterialBinding surfaceMat, OriginalFrameMaterialFlags frameFlags)
            {
                bool transparent = mat.Diffuse.W < 1f || frameFlags.CullTransparentSurface;
                return transparent == IsTransparent
                    && mat.DiffuseTexture == DiffuseTexture
                    && surfaceMat.SphereTexture == SphereTexture
                    && surfaceMat.SphereMode == SphereMode
                    && mat.ToonTexture == ToonTexture
                    && SameBits(mat.Shininess, Shininess)
                    && SameBits(mat.Diffuse, Diffuse)
                    && SameBits(mat.Specular, Specular)
                    && SameBits(mat.Ambient, Ambient)
                    && string.Equals(mat.Name, Name, StringComparison.Ordinal);
            }
        }

        private sealed class SceneInputSnapshot
        {
            private readonly Guid objectId;
            private readonly bool present;
            private readonly SceneObjectRenderInputs? rawInputs;
            private readonly bool visible;
            private readonly bool objectIdMatch;
            private readonly int[] texKeys;
            private readonly int[] texValues;
            private readonly int[] boneKeys;
            private readonly Matrix4x4[] boneMatrices;

            private SceneInputSnapshot(
                Guid objectId,
                bool present,
                SceneObjectRenderInputs? rawInputs,
                bool visible,
                bool objectIdMatch,
                int[] texKeys,
                int[] texValues,
                int[] boneKeys,
                Matrix4x4[] boneMatrices)
            {
                this.objectId = objectId;
                this.present = present;
                this.rawInputs = rawInputs;
                this.visible = visible;
                this.objectIdMatch = objectIdMatch;
                this.texKeys = texKeys;
                this.texValues = texValues;
                this.boneKeys = boneKeys;
                this.boneMatrices = boneMatrices;
            }

            public static SceneInputSnapshot Absent(Guid objectId)
                => new(objectId, false, null, false, false, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<Matrix4x4>());

            public static SceneInputSnapshot From(Guid objectId, SceneObjectRenderInputs inputs)
            {
                var texKeys = inputs.TextureIndices.Count == 0 ? Array.Empty<int>() : new int[inputs.TextureIndices.Count];
                var texValues = inputs.TextureIndices.Count == 0 ? Array.Empty<int>() : new int[inputs.TextureIndices.Count];
                int t = 0;
                foreach (var (k, v) in inputs.TextureIndices)
                {
                    texKeys[t] = k;
                    texValues[t] = v;
                    t++;
                }

                var boneKeys = inputs.BoneWorldMatrices.Count == 0 ? Array.Empty<int>() : new int[inputs.BoneWorldMatrices.Count];
                var boneMatrices = inputs.BoneWorldMatrices.Count == 0 ? Array.Empty<Matrix4x4>() : new Matrix4x4[inputs.BoneWorldMatrices.Count];
                int b = 0;
                foreach (var (k, m) in inputs.BoneWorldMatrices)
                {
                    boneKeys[b] = k;
                    boneMatrices[b] = m;
                    b++;
                }

                return new SceneInputSnapshot(objectId, true, inputs, inputs.Visible, inputs.ObjectId == objectId,
                    texKeys, texValues, boneKeys, boneMatrices);
            }

            public bool Matches(IReadOnlyDictionary<Guid, SceneObjectRenderInputs> inputsMap)
            {
                bool has = inputsMap.TryGetValue(objectId, out var current);
                if (has != present)
                    return false;
                if (!present)
                    return true;
                if (ReferenceEquals(current, rawInputs))
                    return true;
                if (current is null || current.Visible != visible || (current.ObjectId == objectId) != objectIdMatch)
                    return false;
                if (current.TextureIndices.Count != texKeys.Length || current.BoneWorldMatrices.Count != boneKeys.Length)
                    return false;
                for (int i = 0; i < texKeys.Length; i++)
                {
                    if (!current.TextureIndices.TryGetValue(texKeys[i], out int val) || val != texValues[i])
                        return false;
                }
                for (int i = 0; i < boneKeys.Length; i++)
                {
                    if (!current.BoneWorldMatrices.TryGetValue(boneKeys[i], out var mat) || !SameMatrixBits(mat, boneMatrices[i]))
                        return false;
                }
                return true;
            }
        }
    }
}
