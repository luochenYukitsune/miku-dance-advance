using System.Collections.ObjectModel;
using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

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
            SetBindingsState(nextState);
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
            && (!existing.UsesPluginStyle || pluginFrame?.MatchesSceneInputs(sceneInputs) == true))
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
            SetBindingsState(bs);
        }

        return ResolveWithBindingsStateLocked(cfg, bs, scene, sceneInputs);
    }

    private void SetBindingsState(PreparedBindings next)
    {
        bindingsState = next;
        pluginFrame = null;
        lastExactScene = null;
        lastExactResult = null;
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

        // A mixed frame's cached plugin map also contains slots resolved by material name.
        // Rebuild those base slots before reusing or recomputing a plugin draw with different names.
        if (bs.UsesPluginStyle && bs.UsesFallbackRoleGuessing
            && !bs.MatchesFallbackMaterialNames(scene.Surface.Snapshot.Materials))
        {
            bs = BuildBindingsState(cfg, scene, bs.Selected, bs.OutlineScales);
            SetBindingsState(bs);
        }

        if (!bs.UsesPluginStyle)
        {
            if (bs.RequiresOptionsForPreset && scene.Options is null)
                throw new NotSupportedException("Character main-light style presets require explicit modern frame options; the simple legacy path does not consume the character toon response.");
            if (bs.RequiresOptionsForSurface && scene.Options is null)
                throw new NotSupportedException("Non-MMD surface shader styles require explicit modern frame options.");

            var surfaceStyles = bs.GetOrUpdateStaticSurfaceStyles(cfg, scene.Surface.Snapshot.Materials);
            var resultScene = bs.ResolveSceneMaterials(cfg, scene, surfaceStyles);
            var result = new D3D12ViewRenderingStylesResult(resultScene, surfaceStyles, bs.StaticDiagnostics);
            lastExactScene = scene;
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

            bool validateGeometry = pf.HasEmptyMikkBufferBinding && !ReferenceEquals(snapshot, pf.LastValidatedSnapshot);
            var resultScene = bs.ResolveSceneMaterials(cfg, scene, pf.SurfaceStyles, validateGeometry);
            pf.LastValidatedSnapshot = snapshot;

            var cachedResult = new D3D12ViewRenderingStylesResult(resultScene, pf.SurfaceStyles, bs.StaticDiagnostics);
            lastExactScene = scene;
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
        var table = bs.BaseMaterials.CopyMaterialParameters();
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
            table.SetMaterial(slot.FrameIndex, resolvedStyle);
        }

        var surfaceStyles = table.ToSurfaceStyleMap(cfg.EffectiveCatalog);

        var resultScene = bs.ApplyTransformations(cfg, scene);
        surfaceStyles.Validate(resultScene);

        pluginFrame = PluginFrameCache.Capture(
            scene,
            inputsMap,
            bs,
            surfaceStyles,
            primaryLightDirection,
            primaryLightColor,
            cameraPosition,
            observedTextureIndices);
        bs.CommitMaterialTransformCache(scene, resultScene, surfaceStyles, bs.HasEffectiveTreatment(surfaceStyles));

        var result = new D3D12ViewRenderingStylesResult(resultScene, surfaceStyles, bs.StaticDiagnostics);
        lastExactScene = scene;
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
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? boneIndicesByObject,
        D3D12StylePresetCatalog? rawCatalog = null,
        BoneObjectEntry[]? boneSnapshot = null)
    {
        return new PreparedConfig
        {
            RawCatalog = rawCatalog ?? effectivePresets,
            StyleCatalog = styleCatalog,
            EffectiveCatalog = effectivePresets,
            Materials = characterMaterials,
            PluginBindings = pluginBindings,
            OutlineWidths = outlineWidths,
            Globals = styleGlobalParameters,
            GlobalKeys = styleGlobalParameters is { Count: > 0 } ? styleGlobalParameters.Keys.ToArray() : null,
            GuessMaterialRoles = guessMaterialRoles,
            BoneIndices = boneIndicesByObject,
            BoneSnapshot = boneSnapshot,
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

        return BuildConfigFromDetached(effectiveCatalog, effectiveStyleCatalog, detachedMaterials, detachedPluginBindings,
            detachedOutlineWidths, detachedGlobals, guessMaterialRoles, boneDict, catalog, boneSnapshot);
    }

    private static PreparedBindings BuildBindingsState(
        PreparedConfig cfg,
        OriginalFrameScene scene,
        SceneRenderingStyleBinding[] selected,
        D3D12ViewOutlineWidths.MaterialScale[] outlineScales)
    {
        var diagnostics = new List<string>();
        D3D12ViewRenderingStyles.ValidateMaterialBindings(selected, cfg.Materials, cfg.PluginBindings,
            cfg.EffectiveCatalog, cfg.StyleCatalog, diagnostics).EnsureAvailable();

        int totalMaterials = scene.Materials.Count;
        var table = new D3D12ViewMaterialStyleTable(totalMaterials);

        if (selected.Length == 0)
        {
            var defaultMap = D3D12ViewRenderingStyles.GetOrCreateDefaultSurfaceStyleMap(cfg.EffectiveCatalog, totalMaterials);
            return new PreparedBindings
            {
                Selected = selected,
                TotalMaterials = totalMaterials,
                OutlineScales = outlineScales,
                BaseMaterials = table,
                StaticDiagnostics = diagnostics.Count == 0 ? D3D12ViewRenderingStylesResult.EmptyDiagnostics : Array.AsReadOnly(diagnostics.ToArray()),
                StaticSurfaceStyles = defaultMap,
                RequiresOptionsForPreset = false,
                RequiresOptionsForSurface = false,
                CanModifySceneMaterials = outlineScales.Length > 0,
                PluginSlots = Array.Empty<PreparedPluginSlot>(),
                PluginObjectIds = Array.Empty<Guid>(),
                OrderedPluginOrDeferredSteps = Array.Empty<OrderedPluginStep>(),
                FallbackSlots = Array.Empty<PreparedFallbackSlot>(),
            };
        }

        bool requiresOptionsForPreset = false;
        bool requiresOptionsForSurface = false;
        bool hasNonMmdVisibleCharacterPreset = false;

        var pluginSlots = new List<PreparedPluginSlot>();
        var orderedSteps = new List<OrderedPluginStep>();
        var pluginObjectIds = new List<Guid>();
        var fallbackSlots = new List<PreparedFallbackSlot>();
        var snapshotMaterials = scene.Surface.Snapshot.Materials;

        foreach (var instance in selected)
        {
            var resolution = cfg.EffectiveCatalog.Find(instance.Preset.StableId);
            var definition = resolution.Definition!;

            if (instance.Visible && instance.MaterialCount > 0 && instance.Role == ModelRole.Character && definition.StableId != D3D12SurfaceStyleMap.DefaultStyleId)
            {
                hasNonMmdVisibleCharacterPreset = true;
                if (definition.CharacterToonResponse is not null)
                    requiresOptionsForPreset = true;
            }

            if (table.AssignSurfaceRange(instance, resolution, cfg.CharMatMap, diagnostics) is not { } pipelineId)
                continue;

            requiresOptionsForSurface = true;
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
                    table.SetMaterial(frameIdx, fallbackStyle);
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
            if (!requiresOptionsForSurface && cfg.CharMatMap.Count == 0)
            {
                staticSurfaceStyles = D3D12ViewRenderingStyles.GetOrCreateDefaultSurfaceStyleMap(cfg.EffectiveCatalog, totalMaterials);
            }
            else
            {
                staticSurfaceStyles = table.ToSurfaceStyleMap(cfg.EffectiveCatalog);
                foreach (var plugin in staticSurfaceStyles.RegisteredStyles.Values)
                    plugin.Validate();
            }
        }

        if (usesFallbackRoleGuessing)
        {
            fallbackMaterialNames = new string[fallbackSlots.Count];
            for (int i = 0; i < fallbackSlots.Count; i++)
                fallbackMaterialNames[i] = snapshotMaterials[fallbackSlots[i].FrameIndex].Name;
        }

        return new PreparedBindings
        {
            Selected = selected,
            TotalMaterials = totalMaterials,
            OutlineScales = outlineScales,
            BaseMaterials = table,
            StaticDiagnostics = diagnostics.Count == 0 ? D3D12ViewRenderingStylesResult.EmptyDiagnostics : Array.AsReadOnly(diagnostics.ToArray()),
            StaticSurfaceStyles = staticSurfaceStyles,
            FallbackMaterialNames = fallbackMaterialNames,
            RequiresOptionsForPreset = requiresOptionsForPreset,
            RequiresOptionsForSurface = requiresOptionsForSurface,
            CanModifySceneMaterials = hasNonMmdVisibleCharacterPreset || outlineScales.Length > 0,
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
        public required D3D12ViewMaterialStyleTable BaseMaterials { get; init; }
        public required IReadOnlyList<string> StaticDiagnostics { get; init; }
        public D3D12SurfaceStyleMap? StaticSurfaceStyles { get; set; }
        public string[]? FallbackMaterialNames { get; set; }
        public bool UsesPluginStyle => PluginSlots.Length != 0;
        public bool UsesFallbackRoleGuessing => FallbackSlots.Length != 0;
        public required bool RequiresOptionsForPreset { get; init; }
        public required bool RequiresOptionsForSurface { get; init; }
        public required bool CanModifySceneMaterials { get; init; }
        public required PreparedPluginSlot[] PluginSlots { get; init; }
        public required Guid[] PluginObjectIds { get; init; }
        public required OrderedPluginStep[] OrderedPluginOrDeferredSteps { get; init; }
        public required PreparedFallbackSlot[] FallbackSlots { get; init; }

        private ReadOnlyCollection<OriginalFrameMaterialFlags>? lastInputMaterials;
        private IReadOnlyList<OriginalOutlineMaterial>? lastOutlineMaterials;
        private ReadOnlyCollection<OriginalFrameMaterialFlags>? lastResultMaterials;
        private D3D12SurfaceStyleMap? lastMaterialStyles;
        private bool lastHasEffectiveTreatment;

        public D3D12SurfaceStyleMap GetOrUpdateStaticSurfaceStyles(
            PreparedConfig cfg,
            IReadOnlyList<SceneMaterial> snapshotMaterials)
        {
            if (StaticSurfaceStyles is not null && (!UsesFallbackRoleGuessing || MatchesFallbackMaterialNames(snapshotMaterials)))
                return StaticSurfaceStyles;

            var updated = BaseMaterials.CopyMaterialParameters();
            var names = FallbackMaterialNames ?? new string[FallbackSlots.Length];
            for (int i = 0; i < FallbackSlots.Length; i++)
            {
                var slot = FallbackSlots[i];
                string matName = snapshotMaterials[slot.FrameIndex].Name;
                names[i] = matName;
                var style = D3D12ViewRenderingStyles.BuildFallbackMaterialStyle(matName, slot.CharMat, cfg.GuessMaterialRoles);
                updated.SetMaterial(slot.FrameIndex, style);
            }
            FallbackMaterialNames = names;
            var map = updated.ToSurfaceStyleMap(cfg.EffectiveCatalog);
            foreach (var plugin in map.RegisteredStyles.Values)
                plugin.Validate();
            StaticSurfaceStyles = map;
            return map;
        }

        public bool MatchesFallbackMaterialNames(IReadOnlyList<SceneMaterial> snapshotMaterials)
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
            bool validateGeometry = false)
        {
            bool hasTreatment = HasEffectiveTreatment(surfaceStyles);
            if (!CanModifySceneMaterials && !hasTreatment && !validateGeometry)
                return scene;

            bool sameInputs = lastInputMaterials is not null
                && SameEdgeFlags(scene.Outline?.Materials, lastOutlineMaterials)
                && SameMaterials(scene.Materials, lastInputMaterials);
            var result = scene;
            if (CanModifySceneMaterials)
            {
                if (!sameInputs)
                    result = ApplyTransformations(cfg, scene);
                else if (lastResultMaterials is not null)
                    result = new OriginalFrameScene(scene.Surface, scene.Outline, scene.OutlineOptions, scene.Options,
                        lastResultMaterials, scene.Lighting, scene.EditorOverlay);
            }

            // SameMaterials includes CharacterToonResponse. Reuse its proof only with the map that passed validation.
            bool sameStyles = ReferenceEquals(surfaceStyles, lastMaterialStyles);
            if (validateGeometry || (hasTreatment && !(sameStyles && sameInputs)))
                surfaceStyles.Validate(result);
            if (!sameStyles || !sameInputs)
                CommitMaterialTransformCache(scene, result, surfaceStyles, hasTreatment);
            return result;
        }

        public bool HasEffectiveTreatment(D3D12SurfaceStyleMap surfaceStyles)
        {
            if (ReferenceEquals(surfaceStyles, lastMaterialStyles)) return lastHasEffectiveTreatment;
            if (RequiresOptionsForSurface)
                for (int i = 0; i < surfaceStyles.Count; i++)
                    if (surfaceStyles.EffectivePmxMaterialTreatment(i) is not null) return true;
            return false;
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

        public void CommitMaterialTransformCache(OriginalFrameScene inputScene, OriginalFrameScene resultScene,
            D3D12SurfaceStyleMap surfaceStyles, bool hasEffectiveTreatment)
        {
            lastMaterialStyles = surfaceStyles;
            lastHasEffectiveTreatment = hasEffectiveTreatment;
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

            return MatchesSceneInputs(sceneInputs);
        }

        public bool MatchesSceneInputs(IReadOnlyDictionary<Guid, SceneObjectRenderInputs>? sceneInputs)
        {
            // The caller may reuse the dictionary while replacing an object's immutable input sample.
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
