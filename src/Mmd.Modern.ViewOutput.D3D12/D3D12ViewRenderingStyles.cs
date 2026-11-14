using Mmd.Modern.Application;
using Mmd.Modern.Core;
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

/// <summary>Pure adapter from explicit scene-instance metadata to the existing material-range applicator.
/// A nonempty binding list must describe every composed visible material exactly once. Hidden instances retain
/// their references for preflight but consume no material range. Empty legacy metadata keeps the input unchanged.</summary>
public static class D3D12ViewRenderingStyles
{
    public static D3D12ViewRenderingStylesPreflight Preflight(OriginalFrameScene scene,
        D3D12StylePresetCatalog catalog, IEnumerable<SceneRenderingStyleBinding> bindings,
        IEnumerable<D3D12ViewCharacterMaterialBinding>? characterMaterials = null,
        IEnumerable<D3D12ViewFaceSdfBinding>? faceDataBindings = null,
        IEnumerable<D3D12ViewGenshinHairBinding>? hairDataBindings = null,
        IEnumerable<D3D12ViewStarMcHairBinding>? starMcHairDataBindings = null,
        IEnumerable<D3D12ViewEndfieldHairBinding>? endfieldHairDataBindings = null,
        IEnumerable<D3D12ViewGenshinHairAtlasBinding>? genshinHairAtlasDataBindings = null,
        IEnumerable<D3D12ViewEndfieldMatcapBinding>? endfieldMatcapDataBindings = null,
        IEnumerable<D3D12ViewGenshinMetallicBinding>? genshinMetallicDataBindings = null,
        IEnumerable<D3D12ViewStarMcBodyBinding>? starMcBodyDataBindings = null,
        IEnumerable<D3D12ViewOutlineWidthBinding>? outlineWidths = null)
    {
        var selected = Validate(scene, catalog, bindings);
        _ = D3D12ViewOutlineWidths.Resolve(selected, D3D12ViewOutlineWidths.Detach(outlineWidths));
        var hairBindings = D3D12ViewGenshinHairBindings.Detach(hairDataBindings);
        var starMcBindings = D3D12ViewStarMcHairBindings.Detach(starMcHairDataBindings);
        var endfieldBindings = D3D12ViewEndfieldHairBindings.Detach(endfieldHairDataBindings);
        var atlasBindings = D3D12ViewGenshinHairAtlasBindings.Detach(genshinHairAtlasDataBindings);
        var matcapBindings = D3D12ViewEndfieldMatcapBindings.Detach(endfieldMatcapDataBindings);
        var metallicBindings = D3D12ViewGenshinMetallicBindings.Detach(genshinMetallicDataBindings);
        var bodyBindings = D3D12ViewStarMcBodyBindings.Detach(starMcBodyDataBindings);
        var materialBindings = D3D12ViewGenshinHairAtlasBindings.SelectCharacterMaterials(
            D3D12ViewEndfieldHairBindings.SelectCharacterMaterials(
            D3D12ViewStarMcHairBindings.SelectCharacterMaterials(
            D3D12ViewGenshinHairBindings.SelectCharacterMaterials(
                D3D12ViewCharacterMaterials.Detach(characterMaterials), hairBindings), starMcBindings, hairBindings), endfieldBindings, hairBindings, starMcBindings), atlasBindings, starMcBindings, endfieldBindings);
        materialBindings = D3D12ViewEndfieldMatcapBindings.SelectCharacterMaterials(materialBindings, matcapBindings,
            hairBindings, starMcBindings, atlasBindings, endfieldBindings);
        materialBindings = D3D12ViewGenshinMetallicBindings.SelectCharacterMaterials(materialBindings, metallicBindings,
            hairBindings, atlasBindings, starMcBindings, endfieldBindings, matcapBindings);
        materialBindings = D3D12ViewStarMcBodyBindings.SelectCharacterMaterials(materialBindings, bodyBindings, hairBindings.Select(v => (v.ObjectId, v.LocalMaterialIndex))
                .Concat(starMcBindings.Select(v => (v.ObjectId, v.LocalMaterialIndex)))
                .Concat(endfieldBindings.Select(v => (v.ObjectId, v.LocalMaterialIndex)))
                .Concat(atlasBindings.Select(v => (v.ObjectId, v.LocalMaterialIndex)))
                .Concat(matcapBindings.Select(v => (v.ObjectId, v.LocalMaterialIndex)))
                .Concat(metallicBindings.Select(v => (v.ObjectId, v.LocalMaterialIndex))));
        D3D12ViewStarMcBodyBindings.ValidateConfiguration(catalog, selected, materialBindings, bodyBindings);
        D3D12ViewGenshinMetallicBindings.ValidateConfiguration(catalog, selected, materialBindings, metallicBindings);
        D3D12ViewEndfieldMatcapBindings.ValidateConfiguration(catalog, selected, materialBindings, matcapBindings);
        _ = D3D12ViewCharacterMaterials.Resolve(scene.Materials.Count, selected,
            materialBindings);
        D3D12ViewCharacterMaterials.ValidateGenshinHairConfiguration(catalog, selected, materialBindings);
        D3D12ViewCharacterMaterials.ValidateStarMcHairConfiguration(catalog, selected, materialBindings);
        D3D12ViewCharacterMaterials.ValidateEndfieldRimConfiguration(catalog, selected, materialBindings);
        D3D12ViewEndfieldHairBindings.ValidateConfiguration(catalog, selected, materialBindings, endfieldBindings);
        D3D12ViewStarMcHairBindings.ValidateConfiguration(catalog, selected, materialBindings, starMcBindings);
        D3D12ViewGenshinHairBindings.ValidateConfiguration(selected, materialBindings, hairBindings);
        D3D12ViewGenshinHairAtlasBindings.ValidateConfiguration(selected, materialBindings, atlasBindings);
        D3D12ViewFaceSdfBindings.ValidateConfiguration(catalog, selected, materialBindings,
            D3D12ViewFaceSdfBindings.Detach(faceDataBindings));
        return ResolveAvailability(catalog, selected);
    }

    /// <summary>Compatibility entry for callers consuming material parameters only. Surface dispatch consumers
    /// must pass both values returned by Resolve to the rendering session.</summary>
    public static OriginalFrameScene Apply(OriginalFrameScene scene, D3D12StylePresetCatalog catalog,
        IEnumerable<SceneRenderingStyleBinding> bindings,
        IEnumerable<D3D12ViewCharacterMaterialBinding>? characterMaterials = null,
        IEnumerable<D3D12ViewOutlineWidthBinding>? outlineWidths = null)
        => Resolve(scene, catalog, bindings, characterMaterials, outlineWidths: outlineWidths).Scene;

    public static D3D12ViewRenderingStylesResult Resolve(OriginalFrameScene scene, D3D12StylePresetCatalog catalog,
        IEnumerable<SceneRenderingStyleBinding> bindings,
        IEnumerable<D3D12ViewCharacterMaterialBinding>? characterMaterials = null,
        IEnumerable<D3D12ViewFaceSdfBinding>? faceDataBindings = null,
        IReadOnlyDictionary<Guid, SceneObjectRenderInputs>? sceneInputs = null,
        IEnumerable<D3D12ViewGenshinHairBinding>? hairDataBindings = null,
        IEnumerable<D3D12ViewStarMcHairBinding>? starMcHairDataBindings = null,
        IEnumerable<D3D12ViewEndfieldHairBinding>? endfieldHairDataBindings = null,
        IEnumerable<D3D12ViewGenshinHairAtlasBinding>? genshinHairAtlasDataBindings = null,
        IEnumerable<D3D12ViewEndfieldMatcapBinding>? endfieldMatcapDataBindings = null,
        IEnumerable<D3D12ViewGenshinMetallicBinding>? genshinMetallicDataBindings = null,
        IEnumerable<D3D12ViewStarMcBodyBinding>? starMcBodyDataBindings = null,
        IEnumerable<D3D12ViewOutlineWidthBinding>? outlineWidths = null)
    {
        var selected = Validate(scene, catalog, bindings);
        var outlineScales = D3D12ViewOutlineWidths.Resolve(selected, D3D12ViewOutlineWidths.Detach(outlineWidths));
        ResolveAvailability(catalog, selected).EnsureAvailable();
        var surfaces = new D3D12SurfaceStyle[scene.Materials.Count];
        var diagnostics = new List<string>();
        var explicitMaterials = D3D12ViewCharacterMaterials.Detach(characterMaterials);
        var hairBindings = D3D12ViewGenshinHairBindings.Detach(hairDataBindings);
        var starMcBindings = D3D12ViewStarMcHairBindings.Detach(starMcHairDataBindings);
        var endfieldBindings = D3D12ViewEndfieldHairBindings.Detach(endfieldHairDataBindings);
        var atlasBindings = D3D12ViewGenshinHairAtlasBindings.Detach(genshinHairAtlasDataBindings);
        var matcapBindings = D3D12ViewEndfieldMatcapBindings.Detach(endfieldMatcapDataBindings);
        var metallicBindings = D3D12ViewGenshinMetallicBindings.Detach(genshinMetallicDataBindings);
        var bodyBindings = D3D12ViewStarMcBodyBindings.Detach(starMcBodyDataBindings);
        var selectedMaterials = D3D12ViewGenshinHairAtlasBindings.SelectCharacterMaterials(
            D3D12ViewEndfieldHairBindings.SelectCharacterMaterials(
            D3D12ViewStarMcHairBindings.SelectCharacterMaterials(
            D3D12ViewGenshinHairBindings.SelectCharacterMaterials(
                explicitMaterials, hairBindings), starMcBindings, hairBindings), endfieldBindings, hairBindings, starMcBindings), atlasBindings, starMcBindings, endfieldBindings);
        selectedMaterials = D3D12ViewEndfieldMatcapBindings.SelectCharacterMaterials(selectedMaterials, matcapBindings,
            hairBindings, starMcBindings, atlasBindings, endfieldBindings);
        selectedMaterials = D3D12ViewGenshinMetallicBindings.SelectCharacterMaterials(selectedMaterials, metallicBindings,
            hairBindings, atlasBindings, starMcBindings, endfieldBindings, matcapBindings);
        selectedMaterials = D3D12ViewStarMcBodyBindings.SelectCharacterMaterials(selectedMaterials, bodyBindings, hairBindings.Select(v => (v.ObjectId, v.LocalMaterialIndex))
                .Concat(starMcBindings.Select(v => (v.ObjectId, v.LocalMaterialIndex)))
                .Concat(endfieldBindings.Select(v => (v.ObjectId, v.LocalMaterialIndex)))
                .Concat(atlasBindings.Select(v => (v.ObjectId, v.LocalMaterialIndex)))
                .Concat(matcapBindings.Select(v => (v.ObjectId, v.LocalMaterialIndex)))
                .Concat(metallicBindings.Select(v => (v.ObjectId, v.LocalMaterialIndex))));
        D3D12ViewStarMcBodyBindings.ValidateConfiguration(catalog, selected, selectedMaterials, bodyBindings);
        D3D12ViewGenshinMetallicBindings.ValidateConfiguration(catalog, selected, selectedMaterials, metallicBindings);
        D3D12ViewCharacterMaterials.ValidateGenshinHairConfiguration(catalog, selected, selectedMaterials);
        D3D12ViewCharacterMaterials.ValidateStarMcHairConfiguration(catalog, selected, selectedMaterials);
        D3D12ViewCharacterMaterials.ValidateEndfieldRimConfiguration(catalog, selected, selectedMaterials);
        var materialBindings = D3D12ViewFaceSdfBindings.Resolve(scene, catalog, selected,
            selectedMaterials, D3D12ViewFaceSdfBindings.Detach(faceDataBindings),
            sceneInputs ?? SceneObjectRenderInputs.Empty);
        materialBindings = D3D12ViewGenshinHairBindings.Resolve(scene, catalog, selected, materialBindings,
            hairBindings, sceneInputs ?? SceneObjectRenderInputs.Empty);
        materialBindings = D3D12ViewStarMcHairBindings.Resolve(scene, catalog, selected, materialBindings,
            starMcBindings, sceneInputs ?? SceneObjectRenderInputs.Empty);
        materialBindings = D3D12ViewEndfieldHairBindings.Resolve(scene, catalog, selected, materialBindings,
            endfieldBindings, sceneInputs ?? SceneObjectRenderInputs.Empty);
        materialBindings = D3D12ViewGenshinHairAtlasBindings.Resolve(scene, catalog, selected, materialBindings,
            atlasBindings, sceneInputs ?? SceneObjectRenderInputs.Empty);
        materialBindings = D3D12ViewEndfieldMatcapBindings.Resolve(scene, catalog, selected, materialBindings,
            matcapBindings, sceneInputs ?? SceneObjectRenderInputs.Empty);
        materialBindings = D3D12ViewGenshinMetallicBindings.Resolve(scene, catalog, selected, materialBindings,
            metallicBindings, sceneInputs ?? SceneObjectRenderInputs.Empty);
        materialBindings = D3D12ViewStarMcBodyBindings.Resolve(scene, catalog, selected, materialBindings,
            bodyBindings, sceneInputs ?? SceneObjectRenderInputs.Empty);
        var materialStyles = D3D12ViewCharacterMaterials.Resolve(scene.Materials.Count, selected,
            materialBindings, diagnostics);
        foreach (var binding in selected)
        {
            var resolution = catalog.Find(binding.Preset.StableId);
            var surfaceStyle = resolution.Definition!.PipelineId switch
            {
                "genshin" => D3D12SurfaceStyle.GenshinPmx,
                "star-rail" => D3D12SurfaceStyle.StarRailPmx,
                "wuthering-waves" => D3D12SurfaceStyle.WutheringPmx,
                "endfield" => D3D12SurfaceStyle.EndfieldPmx,
                _ => D3D12SurfaceStyle.Mmd,
            };
            if (surfaceStyle == D3D12SurfaceStyle.Mmd) continue;
            if (!binding.Visible || binding.MaterialCount == 0)
            {
                diagnostics.Add($"Hidden or empty object '{binding.ObjectId:D}' consumes no surface materials. {resolution.Diagnostic}");
                continue;
            }
            if (binding.Role != ModelRole.Character)
            {
                diagnostics.Add($"Object '{binding.ObjectId:D}' ({binding.Role}) keeps the MMD surface: {resolution.Diagnostic}");
                continue;
            }
            diagnostics.Add($"Object '{binding.ObjectId:D}': {resolution.Diagnostic}");
            Array.Fill(surfaces, surfaceStyle, binding.MaterialStart, binding.MaterialCount);
        }
        // Only explicit material classification opts into ordinary default treatment.
        // Local typed inputs synthesize role rows while dormant; those rows must not
        // activate another family's generic processing or affect unclassified pixels.
        var selectedById = selected.ToDictionary(value => value.ObjectId);
        foreach (var mapped in explicitMaterials)
            if (selectedById.TryGetValue(mapped.ObjectId, out var instance)
                && instance.Visible && instance.Role == ModelRole.Character)
            {
                int index = instance.MaterialStart + mapped.MaterialIndex;
                materialStyles[index] = D3D12PmxMaterialTreatmentCatalog.ApplyDefault(surfaces[index], materialStyles[index]);
            }
        var surfaceStyles = new D3D12SurfaceStyleMap(surfaces, materialStyles);
        if (selected.Length == 0)
        {
            surfaceStyles.Validate(scene);
            return new(scene, surfaceStyles, diagnostics);
        }
        var styled = D3D12StylePresetApplicator.Apply(scene, catalog,
            selected.Where(binding => binding.Visible && binding.MaterialCount > 0).Select(binding =>
                new D3D12StylePresetMaterialRange(binding.ObjectId, binding.MaterialStart, binding.MaterialCount,
                    binding.Preset.StableId, binding.Role == ModelRole.Character)));
        OriginalFrameMaterialFlags[]? materials = null;
        // The built-in project default supplies missing styles without undoing the source's explicit outline.
        // A custom preset remains an explicit replacement even when it uses the MMD pipeline.
        foreach (var binding in selected.Where(binding => binding.Visible && binding.Role == ModelRole.Character
            && binding.Preset.StableId == "mmd"))
            for (int index = binding.MaterialStart; index < binding.MaterialStart + binding.MaterialCount; index++)
            {
                if (scene.Materials[index].CharacterOutlineStyle is not { } outline
                    || styled.Materials[index].CharacterOutlineStyle == outline) continue;
                materials ??= styled.Materials.ToArray();
                materials[index] = materials[index] with { CharacterOutlineStyle = outline };
            }
        OriginalFrameScene result = materials is null ? styled : new(styled.Surface, styled.Outline, styled.OutlineOptions, styled.Options,
            materials, styled.Lighting, styled.EditorOverlay);
        result = D3D12ViewOutlineWidths.Apply(result, outlineScales);
        // Validate the consumed response after the preset supplies its actual
        // threshold/width. Validating the incoming source interval can reject a
        // valid replacement or miss an invalid resulting treatment interval.
        surfaceStyles.Validate(result);
        return new(result, surfaceStyles, diagnostics);
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
        foreach (var binding in selected.Where(binding => binding.Visible && binding.MaterialCount > 0)
            .OrderBy(binding => binding.MaterialStart))
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
        foreach (var group in selected.GroupBy(binding => binding.Preset))
        {
            var resolution = catalog.Find(group.Key.StableId);
            if (!resolution.IsAvailable)
                failures.Add(new(group.Key, resolution, group.Select(binding => binding.ObjectId)));
        }
        return new(failures);
    }
}
