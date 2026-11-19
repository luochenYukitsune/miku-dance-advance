using System.Numerics;
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
    public OriginalFrameScene Scene { get; }
    public D3D12SurfaceStyleMap SurfaceStyles { get; }
    public IReadOnlyList<string> Diagnostics { get; }

    internal D3D12ViewRenderingStylesResult(OriginalFrameScene scene, D3D12SurfaceStyleMap surfaceStyles,
        IEnumerable<string> diagnostics)
    { Scene = scene; SurfaceStyles = surfaceStyles; Diagnostics = Array.AsReadOnly(diagnostics.ToArray()); }
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
        var selected = Validate(scene, catalog, bindings);
        var effectiveStyleCatalog = styleCatalog ?? D3D12ViewStyleCatalog.Empty;
        var effectiveCatalog = MergePresetCatalog(catalog, effectiveStyleCatalog);
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
        var styleIds = new string[totalMaterials];
        Array.Fill(styleIds, D3D12SurfaceStyleMap.DefaultStyleId);
        var materialStyles = new D3D12CharacterMaterialStyle[totalMaterials];
        Array.Fill(materialStyles, new D3D12CharacterMaterialStyle());
        var stylizedRoles = new D3D12CharacterMaterialRole[totalMaterials];

        if (selected.Length == 0)
        {
            var emptyStyles = new D3D12SurfaceStyleMap(styleIds, materialStyles, effectiveCatalog)
            {
                StylizedRoles = stylizedRoles,
            };
            emptyStyles.Validate(scene);
            return new(scene, emptyStyles, diagnostics);
        }

        var charMatMap = detachedMaterials.ToDictionary(static m => (m.ObjectId, m.LocalMaterialIndex));
        var pluginBindingMap = detachedPluginBindings
            .GroupBy(static b => (b.ObjectId, b.LocalMaterialIndex))
            .ToDictionary(static g => g.Key, static g => g.ToArray());

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
                if (instance.Visible && instance.Role == ModelRole.Character)
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
                var surfaceMat = scene.Surface.Materials[frameIdx];
                var frameFlags = scene.Materials[frameIdx];
                charMatMap.TryGetValue((instance.ObjectId, localIdx), out var charMat);

                StyleMaterialBindingData[] activeBindings = pluginBindingMap.TryGetValue((instance.ObjectId, localIdx), out var candidates)
                    ? candidates.Where(b => string.Equals(b.Binding.StyleId, pipelineId, StringComparison.Ordinal))
                        .Select(static b => b.Binding)
                        .ToArray()
                    : Array.Empty<StyleMaterialBindingData>();

                if (style is null)
                {
                    if (activeBindings.Length > 0)
                    {
                        throw new NotSupportedException(
                            $"Style '{pipelineId}' has active material bindings on object '{instance.ObjectId:D}' but no loaded IRenderingStyleDefinition in the style catalog.");
                    }

                    var fallbackRole = charMat is { ExplicitRole: true }
                        ? charMat.Role
                        : (guessMaterialRoles ? D3D12CharacterMaterialRoleGuess.Guess(mat.Name) : D3D12CharacterMaterialRole.Body);
                    D3D12PmxMaterialTreatment? fallbackTreatment = fallbackRole is D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair
                        ? charMat?.PmxTreatment
                        : null;
                    materialStyles[frameIdx] = new D3D12CharacterMaterialStyle(
                        Role: fallbackRole,
                        FaceNormalShadeStrength: charMat?.FaceNormalShadeStrength ?? 0.35f,
                        FaceGeometricShadowStrength: charMat?.FaceGeometricShadowStrength ?? 1f,
                        EyeGeometricShadowStrength: charMat?.EyeGeometricShadowStrength ?? 0.2f,
                        PmxMaterialTreatment: fallbackTreatment,
                        StylizedShading: charMat?.StylizedShading,
                        ToonResponse: charMat?.ToonResponse,
                        PreserveDirectLightPalette: charMat?.PreserveDirectLightPalette ?? false);
                    stylizedRoles[frameIdx] = fallbackRole;
                    continue;
                }

                var classification = D3D12ViewPluginMaterialBindings.ResolveInspectionRole(
                    style,
                    instance.ObjectId,
                    localIdx,
                    mat.Name,
                    charMat,
                    guessMaterialRoles,
                    activeBindings);

                var contractRole = classification.ResolvedRole;
                var d3dRole = D3D12StylePluginAdapter.ToD3D12Role(contractRole);

                var bindingContext = new StyleMaterialBindingContext(
                    instance.ObjectId,
                    localIdx,
                    mat.Name,
                    contractRole,
                    activeBindings,
                    snapshot.Textures.Count);

                var resolvedTextures = D3D12ViewPluginMaterialBindings.ResolveSceneTextures(
                    style, bindingContext, scene, inputsMap);
                var evaluatedBone = D3D12ViewPluginMaterialBindings.ResolveEvaluatedBone(
                    style, bindingContext, inputsMap, boneIndicesByObject);

                if (activeBindings.Length > 0)
                {
                    int validationTexCount = snapshot.Textures.Count;
                    if (inputsMap.TryGetValue(instance.ObjectId, out var objInputs) && objInputs.TextureIndices.Count > 0)
                    {
                        int maxLocal = objInputs.TextureIndices.Keys.Max();
                        if (maxLocal >= validationTexCount)
                            validationTexCount = maxLocal + 1;
                    }
                    var validation = style.ValidateMaterial(new StyleMaterialValidationContext(
                        instance.ObjectId,
                        localIdx,
                        mat.Name,
                        contractRole,
                        activeBindings,
                        validationTexCount,
                        SourceTextureDimensions: null));
                    if (!validation.IsValid)
                        throw new ArgumentException(
                            validation.ErrorMessage ?? $"Material {localIdx} validation failed for style '{pipelineId}'.",
                            nameof(pluginBindings));
                }

                var defaultShading = D3D12StylePluginAdapter.ToD3D12StylizedShading(style.DefaultStylizedShading);
                var roleShading = D3D12StylizedShadingCatalog.ResolveForRole(defaultShading, d3dRole);
                StyleStylizedShadingParameters? contextShading = charMat?.StylizedShading is { } explicitShading
                    ? D3D12StylePluginAdapter.ToContractStylizedShading(explicitShading)
                    : classification.StylizedShadingOverride
                        ?? (roleShading is not null ? D3D12StylePluginAdapter.ToContractStylizedShading(roleShading) : null);

                StyleMaterialTreatmentParameters? contextTreatment = charMat?.PmxTreatment is { } explicitTreatment
                    ? D3D12StylePluginAdapter.ToContractTreatment(explicitTreatment)
                    : classification.TreatmentOverride
                        ?? (style.DefaultRoleTreatments.TryGetValue(contractRole, out var defaultRoleTreatment)
                            ? defaultRoleTreatment
                            : null);

                StyleFaceEyeDefaults baseFaceEye = charMat is not null
                    ? new StyleFaceEyeDefaults(
                        charMat.FaceNormalShadeStrength,
                        charMat.FaceGeometricShadowStrength,
                        charMat.EyeGeometricShadowStrength)
                    : classification.FaceEyeOverride ?? style.DefaultFaceEye;

                var drawContext = new StyleDrawContext(
                    ObjectId: instance.ObjectId,
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

                bool hasDedicatedBinding = activeBindings.Any(static b =>
                    !string.Equals(b.ModeId, "default", StringComparison.Ordinal));
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

                materialStyles[frameIdx] = new D3D12CharacterMaterialStyle(
                    Role: d3dRole,
                    FaceNormalShadeStrength: finalFaceEye.FaceNormalShadeStrength,
                    FaceGeometricShadowStrength: finalFaceEye.FaceGeometricShadowStrength,
                    EyeGeometricShadowStrength: finalFaceEye.EyeGeometricShadowStrength,
                    PmxMaterialTreatment: pmxTreatmentForStyle,
                    StylizedShading: stylizedOverrideForStyle,
                    ToonResponse: charMat?.ToonResponse,
                    PreserveDirectLightPalette: (charMat?.PreserveDirectLightPalette ?? false) || (rawDrawData?.PreserveDirectLightPalette ?? false),
                    PluginDraw: pluginDraw);
                stylizedRoles[frameIdx] = d3dRole;
            }
        }

        var surfaceStyles = new D3D12SurfaceStyleMap(styleIds, materialStyles, effectiveCatalog)
        {
            StylizedRoles = stylizedRoles,
        };

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
        result = D3D12ViewOutlineWidths.Apply(result, outlineScales);

        // Validate the consumed response after the preset supplies its actual
        // threshold/width. Validating the incoming source interval can reject a
        // valid replacement or miss an invalid resulting treatment interval.
        surfaceStyles.Validate(result);
        return new(result, surfaceStyles, diagnostics);
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

    private static IReadOnlyList<D3D12ViewPluginMaterialBinding> FilterVisibleBindings(
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

    private static SceneRenderingStyleBinding[] Validate(OriginalFrameScene scene, D3D12StylePresetCatalog catalog,
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

    private static D3D12ViewRenderingStylesPreflight ResolveAvailability(D3D12StylePresetCatalog catalog,
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
