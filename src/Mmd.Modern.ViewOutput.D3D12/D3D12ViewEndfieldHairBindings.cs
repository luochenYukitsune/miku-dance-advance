using System.Collections;
using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>Explicit instance-local Green numerical mask and final HA/HB palette for radial hair.
/// Rest radial coordinates must be supplied on this instance's source. Other selected pipelines keep this
/// host configuration dormant; no image, material, role or generated coordinate is inferred.</summary>
public sealed record D3D12ViewEndfieldHairBinding(Guid ObjectId, int LocalMaterialIndex,
    int LocalTextureIndex, Vector3 PaletteColor, float Brightness = 0);

internal static class D3D12ViewEndfieldHairBindings
{
    internal static IReadOnlyList<D3D12ViewEndfieldHairBinding> Detach(IEnumerable<D3D12ViewEndfieldHairBinding>? bindings)
    {
        if (bindings is DetachedBindings detached) return detached;
        var values = bindings?.ToArray() ?? [];
        var keys = new HashSet<(Guid, int)>();
        foreach (var binding in values)
        {
            if (binding is null || binding.ObjectId == Guid.Empty || binding.LocalMaterialIndex < 0)
                throw new ArgumentException("Endfield hair needs explicit object identity and a nonnegative material index.", nameof(bindings));
            if (!keys.Add((binding.ObjectId, binding.LocalMaterialIndex)))
                throw new ArgumentException("Endfield hair bindings must be unique per object and material.", nameof(bindings));
            Make(binding, binding.LocalTextureIndex).Validate();
        }
        return new DetachedBindings(values);
    }

    internal static IReadOnlyList<D3D12ViewCharacterMaterialBinding> SelectCharacterMaterials(
        IReadOnlyList<D3D12ViewCharacterMaterialBinding> materials, IReadOnlyList<D3D12ViewEndfieldHairBinding> bindings,
        IReadOnlyList<D3D12ViewGenshinHairBinding>? genshin = null, IReadOnlyList<D3D12ViewStarMcHairBinding>? starMc = null)
    {
        if (bindings.Count == 0) return materials;
        var occupied = (genshin ?? []).Select(value => (value.ObjectId, value.LocalMaterialIndex))
            .Concat((starMc ?? []).Select(value => (value.ObjectId, value.LocalMaterialIndex))).ToHashSet();
        var selected = materials.ToDictionary(value => (value.ObjectId, value.MaterialIndex));
        var result = materials.ToList();
        foreach (var binding in bindings)
        {
            if (occupied.Contains((binding.ObjectId, binding.LocalMaterialIndex)))
                throw new ArgumentException("A material cannot combine local Endfield and other dedicated hair inputs.", nameof(bindings));
            if (selected.TryGetValue((binding.ObjectId, binding.LocalMaterialIndex), out var material))
            {
                if (material.Style.Role != D3D12CharacterMaterialRole.Hair)
                    throw new ArgumentException("Endfield hair cannot replace an explicitly selected Body, Face or Eye role.", nameof(bindings));
                if (material.Style.EndfieldHair is not null || material.Style.StarMcHair is not null || material.Style.GenshinNonMetallicSpecular is not null || material.Style.GenshinHairShadowAtlas is not null
                    || material.Style.PmxMaterialTreatment is not null)
                    throw new ArgumentException("A material cannot combine local Endfield hair with composed or ordinary treatment inputs.", nameof(bindings));
            }
            else result.Add(new(binding.ObjectId, binding.LocalMaterialIndex, new(D3D12CharacterMaterialRole.Hair)));
        }
        return D3D12ViewCharacterMaterials.Detach(result);
    }

    internal static IReadOnlyList<SceneObjectRenderRequest> CreateRequests(IReadOnlyList<D3D12ViewEndfieldHairBinding> bindings,
        IReadOnlyList<D3D12ViewFaceSdfBinding>? faces = null, IReadOnlyList<D3D12ViewGenshinHairBinding>? genshin = null,
        IReadOnlyList<D3D12ViewStarMcHairBinding>? starMc = null,
        IReadOnlyList<D3D12ViewCharacterMaterialBinding>? materials = null)
    {
        var existing = D3D12ViewStarMcHairBindings.CreateRequests(starMc ?? [], faces, genshin);
        var requests = bindings.Select(binding => new SceneObjectRenderRequest(binding.ObjectId,
            optionalLocalTextureIndices: [binding.LocalTextureIndex]));
        var composed = (materials ?? []).Where(value => value.Style.EndfieldHair is not null)
            .Select(value => new SceneObjectRenderRequest(value.ObjectId));
        return Array.AsReadOnly(existing.Concat(requests).Concat(composed).GroupBy(request => request.ObjectId)
            .Select(group => new SceneObjectRenderRequest(group.Key,
                group.SelectMany(request => request.BoneIndices), group.SelectMany(request => request.LocalTextureIndices),
                group.SelectMany(request => request.OptionalLocalTextureIndices),
                group.Any(request => request.IncludeSourceGeometry))).ToArray());
    }

    internal static void ValidateConfiguration(D3D12StylePresetCatalog catalog, IReadOnlyList<SceneRenderingStyleBinding> objects,
        IReadOnlyList<D3D12ViewCharacterMaterialBinding> materials, IReadOnlyList<D3D12ViewEndfieldHairBinding> bindings)
    {
        _ = SelectCharacterMaterials(materials, bindings);
        var instances = objects.ToDictionary(instance => instance.ObjectId);
        foreach (var material in materials.Where(value => value.Style.EndfieldHair is not null))
        {
            if (!instances.TryGetValue(material.ObjectId, out var instance) || instance.Role != ModelRole.Character)
                throw new ArgumentException("Composed Endfield hair needs a known explicit Character object.", nameof(materials));
            var definition = catalog.Find(instance.Preset.StableId).Definition;
            if (definition is not null && definition.PipelineId != "endfield")
                throw new ArgumentException("Composed Endfield hair inputs require the selected Endfield pipeline.", nameof(materials));
        }
        foreach (var binding in bindings)
        {
            if (!instances.TryGetValue(binding.ObjectId, out var instance) || instance.Role != ModelRole.Character)
                throw new ArgumentException("Local Endfield hair needs a known explicit Character object.", nameof(bindings));
            if (instance.Visible && binding.LocalMaterialIndex >= instance.MaterialCount)
                throw new ArgumentException("Local Endfield hair material is outside its visible instance.", nameof(bindings));
        }
    }

    internal static IReadOnlyList<D3D12ViewCharacterMaterialBinding> Resolve(OriginalFrameScene scene,
        D3D12StylePresetCatalog catalog, IReadOnlyList<SceneRenderingStyleBinding> objects,
        IReadOnlyList<D3D12ViewCharacterMaterialBinding> materials, IReadOnlyList<D3D12ViewEndfieldHairBinding> bindings,
        IReadOnlyDictionary<Guid, SceneObjectRenderInputs> sceneInputs)
    {
        ValidateConfiguration(catalog, objects, materials, bindings);
        foreach (var material in materials.Where(value => value.Style.EndfieldHair is not null))
        {
            var instance = objects.Single(value => value.ObjectId == material.ObjectId);
            if (!instance.Visible) continue;
            RequireSourceInputs(instance.ObjectId, sceneInputs);
        }
        if (bindings.Count == 0) return materials;
        var selected = SelectCharacterMaterials(materials, bindings);
        var instances = objects.ToDictionary(instance => instance.ObjectId);
        var indices = selected.Select((value, index) => (value, index))
            .ToDictionary(pair => (pair.value.ObjectId, pair.value.MaterialIndex), pair => pair.index);
        var result = selected.ToArray();
        foreach (var binding in bindings)
        {
            var instance = instances[binding.ObjectId];
            if (!instance.Visible || catalog.Find(instance.Preset.StableId).Definition?.PipelineId != "endfield") continue;
            var inputs = RequireSourceInputs(binding.ObjectId, sceneInputs);
            if (!inputs.TextureIndices.TryGetValue(binding.LocalTextureIndex, out int texture)
                || (uint)texture >= (uint)scene.Surface.Snapshot.Textures.Count)
                throw new ArgumentException("Local Endfield Green image lacks a valid same-sample mapping.", nameof(sceneInputs));
            var hair = Make(binding, texture); hair.Validate();
            int index = indices[(binding.ObjectId, binding.LocalMaterialIndex)];
            result[index] = result[index] with { Style = result[index].Style with { EndfieldHair = hair } };
        }
        return Array.AsReadOnly(result);
    }

    private static SceneObjectRenderInputs RequireSourceInputs(Guid id, IReadOnlyDictionary<Guid, SceneObjectRenderInputs> sceneInputs)
    {
        if (!sceneInputs.TryGetValue(id, out var inputs) || inputs.ObjectId != id || !inputs.Visible || !inputs.HasRawRadialSeeds)
            throw new ArgumentException("Visible Endfield hair requires same-sample source radial seeds for the exact object; composed padding is not an authored source.", nameof(sceneInputs));
        return inputs;
    }

    private static D3D12EndfieldHairBinding Make(D3D12ViewEndfieldHairBinding value, int texture)
        => new(texture, value.PaletteColor, value.Brightness);

    private sealed class DetachedBindings(D3D12ViewEndfieldHairBinding[] values) : IReadOnlyList<D3D12ViewEndfieldHairBinding>
    {
        public int Count => values.Length;
        public D3D12ViewEndfieldHairBinding this[int index] => values[index];
        public IEnumerator<D3D12ViewEndfieldHairBinding> GetEnumerator() => ((IEnumerable<D3D12ViewEndfieldHairBinding>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
