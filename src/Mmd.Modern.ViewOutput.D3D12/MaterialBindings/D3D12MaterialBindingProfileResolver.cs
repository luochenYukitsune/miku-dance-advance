using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>
/// Detached source-local inputs for the D3D12 View renderers. No device, evaluated skeleton,
/// or composed-frame texture ordinal is created while restoring a model mapping.
/// </summary>
public sealed class D3D12ResolvedMaterialBindingProfile
{
    public IReadOnlyList<D3D12ViewCharacterMaterial> CharacterMaterials { get; }
    public IReadOnlyList<D3D12ViewPluginMaterialBinding> PluginBindings { get; }
    public IReadOnlyList<D3D12ViewOutlineWidthBinding> OutlineWidths { get; }

    internal D3D12ResolvedMaterialBindingProfile(
        IEnumerable<D3D12ViewCharacterMaterial> materials,
        IEnumerable<D3D12ViewPluginMaterialBinding> pluginBindings,
        IEnumerable<D3D12ViewOutlineWidthBinding> outlineWidths)
    {
        CharacterMaterials = D3D12ViewCharacterMaterials.Detach(materials);
        PluginBindings = D3D12ViewPluginMaterialBindings.Detach(pluginBindings);
        OutlineWidths = Array.AsReadOnly(outlineWidths.ToArray());
    }
}

/// <summary>
/// Validates an explicit immutable rest input before creating any renderer. A profile is
/// model-specific rather than a general style preset. All saved image guards are checked even for
/// dormant style bindings; selecting MMD or an unloaded style never injects plugin surface payloads.
/// </summary>
public static class D3D12MaterialBindingProfileResolver
{
    public static D3D12ResolvedMaterialBindingProfile Resolve(
        D3D12MaterialBindingProfile profile,
        OriginalFrameScene sourceRest,
        Guid objectId,
        string pipelineId,
        D3D12ViewStyleCatalog? styleCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(sourceRest);
        if (objectId == Guid.Empty)
            throw new ArgumentException("A model mapping requires an explicit object identity.", nameof(objectId));
        D3D12StylePresetDefinition.ValidateIdentifier(pipelineId, nameof(pipelineId));

        var catalog = styleCatalog ?? D3D12ViewStyleCatalog.Empty;
        var layout = D3D12MaterialBindingLayout.Capture(sourceRest);
        layout.ValidateFingerprint(profile.LayoutFingerprint, profile.LayoutVersion);

        var materials = new List<D3D12ViewCharacterMaterial>(profile.Materials.Count);
        var activeBindings = new List<D3D12ViewPluginMaterialBinding>();
        var outlineWidths = new List<D3D12ViewOutlineWidthBinding>(profile.OutlineWidths.Count);

        foreach (var outline in profile.OutlineWidths)
        {
            if ((uint)outline.LocalMaterialIndex >= (uint)layout.MaterialCount)
                throw new ArgumentException("The selected outline material is outside the explicit source-rest layout.", nameof(profile));
            outlineWidths.Add(new(objectId, outline.LocalMaterialIndex, outline.Scale));
        }

        var activeBindingsByMaterial = new Dictionary<int, (D3D12ViewCharacterMaterial CharMat, List<StyleMaterialBindingData> Bindings)>();

        foreach (var row in profile.Materials)
        {
            if ((uint)row.LocalMaterialIndex >= (uint)layout.MaterialCount)
                throw new ArgumentException("The selected material is outside the explicit source-rest layout.", nameof(profile));

            var charMat = new D3D12ViewCharacterMaterial(
                objectId,
                row.LocalMaterialIndex,
                row.Role,
                row.ExplicitRole,
                row.FaceNormalShadeStrength,
                row.FaceGeometricShadowStrength,
                row.EyeGeometricShadowStrength,
                PmxTreatment: row.PmxMaterialTreatment);
            materials.Add(charMat);

            foreach (var guard in row.TextureGuards)
                layout.ValidateTexture(guard);

            foreach (var binding in row.PluginBindings)
            {
                if (binding.TextureSlots is not null)
                {
                    foreach (var (slotName, ordinal) in binding.TextureSlots)
                    {
                        if ((uint)ordinal >= (uint)layout.TextureCount)
                            throw new ArgumentOutOfRangeException(
                                nameof(profile),
                                $"Plugin binding '{binding.StyleId}/{binding.ModeId}' slot '{slotName}' references texture ordinal {ordinal} outside the {layout.TextureCount}-texture source layout.");
                    }
                }

                if (string.Equals(binding.StyleId, pipelineId, StringComparison.Ordinal))
                {
                    activeBindings.Add(new D3D12ViewPluginMaterialBinding(objectId, row.LocalMaterialIndex, binding));
                    if (!activeBindingsByMaterial.TryGetValue(row.LocalMaterialIndex, out var entry))
                    {
                        entry = (charMat, new List<StyleMaterialBindingData>());
                        activeBindingsByMaterial.Add(row.LocalMaterialIndex, entry);
                    }
                    entry.Bindings.Add(binding);
                }
            }
        }

        if (activeBindingsByMaterial.Count > 0 && catalog.FindStyle(pipelineId) is { } activeStyle)
        {
            var textures = sourceRest.Surface.Snapshot.Textures;
            var dimensions = new (int Width, int Height)[textures.Count];
            for (int i = 0; i < textures.Count; i++)
                dimensions[i] = (textures[i].Width, textures[i].Height);

            foreach (var (localMatIdx, (charMat, matBindings)) in activeBindingsByMaterial)
            {
                var bindingArray = matBindings.ToArray();
                var role = D3D12ViewPluginMaterialBindings.ResolveInspectionRole(
                    activeStyle,
                    objectId,
                    localMatIdx,
                    materialName: null,
                    charMat,
                    profile.GuessMaterialRoles,
                    bindingArray).ResolvedRole;

                var validation = activeStyle.ValidateMaterial(new StyleMaterialValidationContext(
                    objectId,
                    localMatIdx,
                    MaterialName: null,
                    ResolvedRole: role,
                    Bindings: bindingArray,
                    SourceTextureCount: textures.Count,
                    SourceTextureDimensions: dimensions));

                if (!validation.IsValid)
                    throw new ArgumentException(
                        validation.ErrorMessage ?? $"Material {localMatIdx} validation failed for style '{pipelineId}'.",
                        nameof(profile));

                var requests = activeStyle.GetSceneTextureRequests(new StyleMaterialBindingContext(
                    objectId,
                    localMatIdx,
                    MaterialName: null,
                    ResolvedRole: role,
                    Bindings: bindingArray,
                    SourceTextureCount: textures.Count));

                if (requests is not null)
                {
                    foreach (var req in requests)
                    {
                        if (req is null)
                            throw new ArgumentException($"Style '{pipelineId}' returned a null scene texture request.", nameof(styleCatalog));
                        if (req.LocalTextureIndex < -1)
                            throw new ArgumentOutOfRangeException(nameof(styleCatalog), $"Style '{pipelineId}' requested invalid texture ordinal {req.LocalTextureIndex}.");
                        if (req.LocalTextureIndex < 0)
                            continue;
                        if (req.LocalTextureIndex >= textures.Count)
                            throw new ArgumentOutOfRangeException(nameof(profile), $"Requested source texture ordinal {req.LocalTextureIndex} is out of range.");
                        var tex = textures[req.LocalTextureIndex];
                        if (req.ExpectedWidth is { } ew && tex.Width != ew)
                            throw new ArgumentException($"Source texture {req.LocalTextureIndex} width {tex.Width} does not match expected width {ew}.", nameof(profile));
                        if (req.ExpectedHeight is { } eh && tex.Height != eh)
                            throw new ArgumentException($"Source texture {req.LocalTextureIndex} height {tex.Height} does not match expected height {eh}.", nameof(profile));
                    }
                }
            }
        }

        return new D3D12ResolvedMaterialBindingProfile(materials, activeBindings, outlineWidths);
    }
}
