using System.Collections;
using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>Explicit original source-scene image ordinals, UV and authored palette. Geometry is
/// always rebuilt from the exact object's current capture; no derived frame or GPU slot is persisted.
/// Palette's EmissionCombinedTextureIndex and ReceivedLightCombinedTextureIndex deliberately carry
/// SOURCE-LOCAL ordinals here, then both rebase using the same ObjectId capture as N/P/MatCap.
/// The child inherits this parent's selected effective UV and geometry; received RGB is LinearRGB.</summary>
public sealed record D3D12ViewEndfieldMatcapBinding(Guid ObjectId, int LocalMaterialIndex,
    int NormalLocalTextureIndex, int PLocalTextureIndex, int MatcapLocalTextureIndex,
    D3D12MatcapColorEncoding MatcapEncoding, int UvChannel, Vector3 BaseColor, Vector3 HighlightColor,
    float NormalStrength = 2f, float RotationRadians = 0f, float HighlightCoefficient = 0f,
    float ShadowHardness = .5f, Vector3? LightControlDirection = null, float BaselineStrength = 1f,
    bool CoupleEndfieldPalette = false, D3D12EndfieldPaletteBinding? Palette = null,
    D3D12EndfieldHighlightRangeBinding? HighlightRange = null);

internal static class D3D12ViewEndfieldMatcapBindings
{
    internal static IReadOnlyList<D3D12ViewEndfieldMatcapBinding> Detach(IEnumerable<D3D12ViewEndfieldMatcapBinding>? bindings)
    {
        if (bindings is DetachedBindings detached) return detached;
        var values = bindings?.ToArray() ?? [];
        var keys = new HashSet<(Guid, int)>();
        foreach (var value in values)
        {
            if (value is null || value.ObjectId == Guid.Empty || value.LocalMaterialIndex < 0)
                throw new ArgumentException("MatCap requires explicit object identity and a nonnegative source-local material.", nameof(bindings));
            if (!keys.Add((value.ObjectId, value.LocalMaterialIndex)))
                throw new ArgumentException("MatCap mappings must be unique per object and material.", nameof(bindings));
            Make(value, value.NormalLocalTextureIndex, value.PLocalTextureIndex, value.MatcapLocalTextureIndex).Validate();
        }
        return new DetachedBindings(values);
    }

    internal static IReadOnlyList<D3D12ViewCharacterMaterialBinding> SelectCharacterMaterials(
        IReadOnlyList<D3D12ViewCharacterMaterialBinding> materials, IReadOnlyList<D3D12ViewEndfieldMatcapBinding> bindings,
        IReadOnlyList<D3D12ViewGenshinHairBinding>? genshin = null, IReadOnlyList<D3D12ViewStarMcHairBinding>? starMc = null,
        IReadOnlyList<D3D12ViewGenshinHairAtlasBinding>? atlas = null, IReadOnlyList<D3D12ViewEndfieldHairBinding>? radial = null)
    {
        if (bindings.Count == 0) return materials;
        var occupied = (genshin ?? []).Select(v => (v.ObjectId, v.LocalMaterialIndex))
            .Concat((starMc ?? []).Select(v => (v.ObjectId, v.LocalMaterialIndex)))
            .Concat((atlas ?? []).Select(v => (v.ObjectId, v.LocalMaterialIndex))).ToHashSet();
        var selected = materials.ToDictionary(v => (v.ObjectId, v.MaterialIndex));
        var result = materials.ToList();
        foreach (var value in bindings)
        {
            if (occupied.Contains((value.ObjectId, value.LocalMaterialIndex)))
                throw new ArgumentException("Endfield MatCap cannot share a material with another family's dedicated image inputs.", nameof(bindings));
            selected.TryGetValue((value.ObjectId, value.LocalMaterialIndex), out var material);
            var style = material?.Style ?? new(D3D12CharacterMaterialRole.Body);
            if (style.Role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair)
                || style.EndfieldMatcap is not null || style.FaceSdf is not null || style.PmxMaterialTreatment is not null
                || style.GenshinNonMetallicSpecular is not null || style.GenshinHairShadowAtlas is not null || style.StarMcHair is not null)
                throw new ArgumentException("MatCap requires explicit Body/Hair without conflicting composed or dedicated inputs.", nameof(bindings));
            var localHair = radial?.SingleOrDefault(v => v.ObjectId == value.ObjectId && v.LocalMaterialIndex == value.LocalMaterialIndex);
            ValidatePalette(value, style.EndfieldRim, style.EndfieldHair?.PaletteColor ?? localHair?.PaletteColor);
            if (material is null) result.Add(new(value.ObjectId, value.LocalMaterialIndex, style));
        }
        return D3D12ViewCharacterMaterials.Detach(result);
    }

    internal static IReadOnlyList<SceneObjectRenderRequest> CreateRequests(IReadOnlyList<D3D12ViewEndfieldMatcapBinding> bindings,
        IReadOnlyList<SceneObjectRenderRequest>? existing = null,
        IReadOnlyList<D3D12ViewCharacterMaterialBinding>? materials = null)
    {
        var requests = bindings.Select(value => new SceneObjectRenderRequest(value.ObjectId,
            optionalLocalTextureIndices: new[] { value.NormalLocalTextureIndex, value.PLocalTextureIndex, value.MatcapLocalTextureIndex }
                .Concat(value.Palette?.EmissionCombinedTextureIndex is { } emission ? [emission] : [])
                .Concat(value.Palette?.ReceivedLightCombinedTextureIndex is { } light ? [light] : [])
                .Concat(value.Palette?.BaseImage?.BaseCombinedTextureIndex is { } baseImage ? [baseImage] : [])
                .Concat(value.Palette?.BaseImage?.DetailCombinedTextureIndex is { } detail ? [detail] : [])
                .Concat(value.Palette?.BaseImage?.ShadowCombinedTextureIndex is { } shadow ? [shadow] : []),
            includeSourceGeometry: true));
        var composed = (materials ?? []).Where(v => v.Style.EndfieldMatcap is not null)
            .Select(v => new SceneObjectRenderRequest(v.ObjectId, includeSourceGeometry: true));
        return Array.AsReadOnly((existing ?? []).Concat(requests).Concat(composed).GroupBy(v => v.ObjectId)
            .Select(group => new SceneObjectRenderRequest(group.Key, group.SelectMany(v => v.BoneIndices),
                group.SelectMany(v => v.LocalTextureIndices), group.SelectMany(v => v.OptionalLocalTextureIndices),
                group.Any(v => v.IncludeSourceGeometry))).ToArray());
    }

    internal static void ValidateConfiguration(D3D12StylePresetCatalog catalog, IReadOnlyList<SceneRenderingStyleBinding> objects,
        IReadOnlyList<D3D12ViewCharacterMaterialBinding> materials, IReadOnlyList<D3D12ViewEndfieldMatcapBinding> bindings)
    {
        _ = SelectCharacterMaterials(materials, bindings);
        var instances = objects.ToDictionary(v => v.ObjectId);
        foreach (var value in bindings)
        {
            if (!instances.TryGetValue(value.ObjectId, out var instance) || instance.Role != ModelRole.Character)
                throw new ArgumentException("MatCap requires a known explicit Character object.", nameof(bindings));
            if (instance.Visible && value.LocalMaterialIndex >= instance.MaterialCount)
                throw new ArgumentException("MatCap source-local material is outside the visible instance.", nameof(bindings));
        }
        foreach (var value in materials.Where(v => v.Style.EndfieldMatcap is not null))
        {
            if (!instances.TryGetValue(value.ObjectId, out var instance) || instance.Role != ModelRole.Character)
                throw new ArgumentException("Composed MatCap requires a known Character object.", nameof(materials));
            var definition = catalog.Find(instance.Preset.StableId).Definition;
            if (definition is not null && definition.PipelineId != "endfield")
                throw new ArgumentException("Composed MatCap requires the explicitly selected Endfield pipeline.", nameof(materials));
        }
    }

    internal static IReadOnlyList<D3D12ViewCharacterMaterialBinding> Resolve(OriginalFrameScene scene,
        D3D12StylePresetCatalog catalog, IReadOnlyList<SceneRenderingStyleBinding> objects,
        IReadOnlyList<D3D12ViewCharacterMaterialBinding> materials, IReadOnlyList<D3D12ViewEndfieldMatcapBinding> bindings,
        IReadOnlyDictionary<Guid, SceneObjectRenderInputs> sceneInputs)
    {
        ValidateConfiguration(catalog, objects, materials, bindings);
        if (bindings.Count == 0 && !materials.Any(v => v.Style.EndfieldMatcap is not null)) return materials;
        var selected = SelectCharacterMaterials(materials, bindings);
        var result = selected.ToArray();
        var instances = objects.ToDictionary(v => v.ObjectId);
        var indices = selected.Select((value, index) => (value, index)).ToDictionary(v => (v.value.ObjectId, v.value.MaterialIndex), v => v.index);
        SceneSnapshot SourceGeometry(Guid id, int uv)
        {
            if (!sceneInputs.TryGetValue(id, out var inputs) || inputs.ObjectId != id
                || !inputs.Visible || inputs.SourceGeometry is not { } source)
                throw new ArgumentException("Active MatCap requires the exact object's same-sample evaluated source geometry.", nameof(sceneInputs));
            ValidateUv(source, uv);
            return source;
        }
        foreach (var material in materials.Where(v => v.Style.EndfieldMatcap is not null))
        {
            if (!instances[material.ObjectId].Visible) continue;
            var payload = material.Style.EndfieldMatcap!;
            var source = SourceGeometry(material.ObjectId, payload.UvChannel);
            int index = indices[(material.ObjectId, material.MaterialIndex)];
            result[index] = material with { Style = material.Style with
            { EndfieldMatcap = payload with { Geometry = new(source, material.MaterialIndex) } } };
        }
        foreach (var value in bindings)
        {
            var instance = instances[value.ObjectId];
            if (!instance.Visible || catalog.Find(instance.Preset.StableId).Definition?.PipelineId != "endfield") continue;
            var geometry = SourceGeometry(value.ObjectId, value.UvChannel);
            var inputs = sceneInputs[value.ObjectId];
            int Map(int local)
            {
                if (!inputs.TextureIndices.TryGetValue(local, out int combined) || (uint)combined >= (uint)scene.Surface.Snapshot.Textures.Count
                    || scene.Surface.Snapshot.Textures[combined].Kind != SceneTextureKind.Image)
                    throw new ArgumentException("MatCap N/P/color and selected palette inputs require explicit same-sample Image mappings for the exact object.", nameof(sceneInputs));
                return combined;
            }
            var payload = Make(value, Map(value.NormalLocalTextureIndex), Map(value.PLocalTextureIndex), Map(value.MatcapLocalTextureIndex),
                new(geometry, value.LocalMaterialIndex),
                value.Palette is { } palette ? palette with
                {
                    EmissionCombinedTextureIndex = palette.EmissionCombinedTextureIndex is { } emission ? Map(emission) : null,
                    ReceivedLightCombinedTextureIndex = palette.ReceivedLightCombinedTextureIndex is { } light ? Map(light) : null,
                    BaseImage = palette.BaseImage is { } basis ? basis with
                    {
                        BaseCombinedTextureIndex = basis.BaseCombinedTextureIndex is { } baseImage ? Map(baseImage) : null,
                        DetailCombinedTextureIndex = basis.DetailCombinedTextureIndex is { } detail ? Map(detail) : null,
                        ShadowCombinedTextureIndex = basis.ShadowCombinedTextureIndex is { } shadow ? Map(shadow) : null
                    } : null
                } : null);
            payload.Validate();
            int index = indices[(value.ObjectId, value.LocalMaterialIndex)];
            result[index] = result[index] with { Style = result[index].Style with { EndfieldMatcap = payload } };
        }
        return Array.AsReadOnly(result);
    }

    internal static void ValidateUv(SceneSnapshot geometry, int channel)
    {
        if (channel < 0 || channel > 4 || channel > (geometry.AdditionalUvs?.ChannelCount ?? 0))
            throw new ArgumentException("MatCap selected UV channel is absent from the exact source geometry.", nameof(channel));
    }

    internal static void ValidatePalette(D3D12ViewEndfieldMatcapBinding value, D3D12EndfieldRimBinding? rim, Vector3? radialColor)
        => ValidatePalette(value.BaseColor, value.CoupleEndfieldPalette, value.BaselineStrength, rim, radialColor);

    internal static void ValidatePalette(Vector3 baseColor, bool couple, float baselineStrength, D3D12EndfieldRimBinding? rim, Vector3? radialColor)
    {
        if (rim is null && radialColor is null) return;
        if (!couple)
            throw new ArgumentException("Same-material MatCap plus rim/radial hair requires explicit CoupleEndfieldPalette.");
        if ((rim is not null && rim.BaseColor != baseColor) || (radialColor is { } color && color != baseColor))
            throw new ArgumentException("Coupled Endfield MatCap, rim and radial hair require an explicitly identical shared BaseColor.");
        if (rim is not null && baselineStrength != rim.RangeStrength + rim.RadialStrength)
            throw new ArgumentException("Coupled MatCap BaselineStrength must explicitly equal rim RangeStrength + RadialStrength.");
        if (radialColor is not null && rim is { RadialStrength: not 0f })
            throw new ArgumentException("Coupled evaluated radial hair requires rim RadialStrength=0 to avoid adding P twice.");
    }

    private static D3D12EndfieldMatcapBinding Make(D3D12ViewEndfieldMatcapBinding v, int normal, int p, int color,
        D3D12EndfieldMatcapGeometry? geometry = null, D3D12EndfieldPaletteBinding? palette = null)
        => new(normal, p, color, v.MatcapEncoding, v.UvChannel, v.BaseColor, v.HighlightColor, v.NormalStrength,
            v.RotationRadians, v.HighlightCoefficient, v.ShadowHardness, v.LightControlDirection, v.BaselineStrength,
            v.CoupleEndfieldPalette, geometry, palette ?? v.Palette, v.HighlightRange);

    private sealed class DetachedBindings(D3D12ViewEndfieldMatcapBinding[] values) : IReadOnlyList<D3D12ViewEndfieldMatcapBinding>
    {
        public int Count => values.Length;
        public D3D12ViewEndfieldMatcapBinding this[int index] => values[index];
        public IEnumerator<D3D12ViewEndfieldMatcapBinding> GetEnumerator() => ((IEnumerable<D3D12ViewEndfieldMatcapBinding>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
