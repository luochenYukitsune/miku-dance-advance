using System.Collections;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>Explicit source-local Genshin Hair shadow atlas. Both texture ordinals belong to the
/// exact source object, not PMX file indices or composed slots. Scalars and color encoding are authored;
/// dormant configuration never selects a different pipeline or guesses textures or palette rows.</summary>
public sealed record D3D12ViewGenshinHairAtlasBinding(Guid ObjectId, int LocalMaterialIndex,
    int LightmapLocalTextureIndex, int AtlasLocalTextureIndex, D3D12GenshinAtlasColorEncoding AtlasEncoding,
    float ExplicitRowId, float DayNight, float ShadowPosition, float ShadowRampWidth,
    float VertexWidth, float VertexAo, float UseLightmapAo, float UseVertexColorAo);

/// <summary>Detached model-local adapter. All rebasing uses inputs captured with the geometry sample;
/// this does not evaluate animation or construct a second texture table.</summary>
internal static class D3D12ViewGenshinHairAtlasBindings
{
    internal static IReadOnlyList<D3D12ViewGenshinHairAtlasBinding> Detach(IEnumerable<D3D12ViewGenshinHairAtlasBinding>? bindings)
    {
        if (bindings is DetachedBindings detached) return detached;
        var values = bindings?.ToArray() ?? [];
        var keys = new HashSet<(Guid, int)>();
        foreach (var binding in values)
        {
            if (binding is null || binding.ObjectId == Guid.Empty || binding.LocalMaterialIndex < 0)
                throw new ArgumentException("Local hair inputs require nonempty object identity and a nonnegative local material index.", nameof(bindings));
            if (!keys.Add((binding.ObjectId, binding.LocalMaterialIndex)))
                throw new ArgumentException("Local hair inputs must be unique per object and local material.", nameof(bindings));
            Make(binding, binding.LightmapLocalTextureIndex, binding.AtlasLocalTextureIndex).Validate();
        }
        return new DetachedBindings(values);
    }

    /// <summary>The explicit host binding itself selects Hair. Existing separately authored Hair settings
    /// remain intact, but selecting the same material through a conflicting role or composed binding is invalid.</summary>
    internal static IReadOnlyList<D3D12ViewCharacterMaterialBinding> SelectCharacterMaterials(
        IReadOnlyList<D3D12ViewCharacterMaterialBinding> materials, IReadOnlyList<D3D12ViewGenshinHairAtlasBinding> bindings,
        IReadOnlyList<D3D12ViewStarMcHairBinding>? starMc = null, IReadOnlyList<D3D12ViewEndfieldHairBinding>? endfield = null)
    {
        if (bindings.Count == 0) return materials;
        var occupied = (starMc ?? []).Select(value => (value.ObjectId, value.LocalMaterialIndex))
            .Concat((endfield ?? []).Select(value => (value.ObjectId, value.LocalMaterialIndex))).ToHashSet();
        var selected = materials.ToDictionary(material => (material.ObjectId, material.MaterialIndex));
        var result = materials.ToList();
        foreach (var binding in bindings)
        {
            if (occupied.Contains((binding.ObjectId, binding.LocalMaterialIndex)))
                throw new ArgumentException("A local Genshin atlas cannot combine other-family hair inputs.", nameof(bindings));
            if (selected.TryGetValue((binding.ObjectId, binding.LocalMaterialIndex), out var material))
            {
                if (material.Style.Role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair))
                    throw new ArgumentException("Model-local atlas inputs require explicit Body/Hair; Face and Eye are not converted.", nameof(bindings));
                if (material.Style.GenshinHairShadowAtlas is not null || material.Style.StarMcHair is not null || material.Style.EndfieldHair is not null
                    || material.Style.EndfieldRim is not null || material.Style.PmxMaterialTreatment is not null)
                    throw new ArgumentException("A Hair material cannot select both model-local and composed-frame numerical inputs.", nameof(bindings));
            }
            else
                result.Add(new(binding.ObjectId, binding.LocalMaterialIndex, new(D3D12CharacterMaterialRole.Hair)));
        }
        return D3D12ViewCharacterMaterials.Detach(result);
    }

    internal static IReadOnlyList<SceneObjectRenderRequest> CreateRequests(
        IReadOnlyList<D3D12ViewGenshinHairAtlasBinding> bindings, IReadOnlyList<SceneObjectRenderRequest>? existing = null)
    {
        var requests = bindings.Select(binding => new SceneObjectRenderRequest(binding.ObjectId,
            localTextureIndices: [binding.LightmapLocalTextureIndex, binding.AtlasLocalTextureIndex]));
        return Array.AsReadOnly((existing ?? []).Concat(requests).GroupBy(request => request.ObjectId)
            .Select(group => new SceneObjectRenderRequest(group.Key,
                group.SelectMany(request => request.BoneIndices), group.SelectMany(request => request.LocalTextureIndices),
                group.SelectMany(request => request.OptionalLocalTextureIndices),
                group.Any(request => request.IncludeSourceGeometry))).ToArray());
    }

    internal static void ValidateConfiguration(IReadOnlyList<SceneRenderingStyleBinding> objects,
        IReadOnlyList<D3D12ViewCharacterMaterialBinding> materials, IReadOnlyList<D3D12ViewGenshinHairAtlasBinding> bindings)
    {
        if (bindings.Count == 0) return;
        // Also catches conflicts in pure-adapter callers that did not use a renderer constructor.
        _ = SelectCharacterMaterials(materials, bindings);
        var byObject = objects.ToDictionary(instance => instance.ObjectId);
        foreach (var binding in bindings)
        {
            if (!byObject.TryGetValue(binding.ObjectId, out var instance))
                throw new ArgumentException($"Local hair inputs refer to unknown object '{binding.ObjectId:D}'.", nameof(bindings));
            if (instance.Role != ModelRole.Character)
                throw new ArgumentException("Model-local hair inputs require an explicit Character object.", nameof(bindings));
            if (instance.Visible && binding.LocalMaterialIndex >= instance.MaterialCount)
                throw new ArgumentException("A selected local Hair material is outside its visible instance.", nameof(bindings));
        }
    }

    internal static IReadOnlyList<D3D12ViewCharacterMaterialBinding> Resolve(OriginalFrameScene scene,
        D3D12StylePresetCatalog catalog, IReadOnlyList<SceneRenderingStyleBinding> objects,
        IReadOnlyList<D3D12ViewCharacterMaterialBinding> materials, IReadOnlyList<D3D12ViewGenshinHairAtlasBinding> bindings,
        IReadOnlyDictionary<Guid, SceneObjectRenderInputs> sceneInputs)
    {
        if (bindings.Count == 0) return materials;
        ValidateConfiguration(objects, materials, bindings);
        var selected = SelectCharacterMaterials(materials, bindings);
        var instances = objects.ToDictionary(instance => instance.ObjectId);
        var materialIndices = selected.Select((material, index) => (material, index))
            .ToDictionary(pair => (pair.material.ObjectId, pair.material.MaterialIndex), pair => pair.index);
        var result = selected.ToArray();
        foreach (var binding in bindings)
        {
            var instance = instances[binding.ObjectId];
            // Stored host configuration is dormant on other families. It never changes a preset or
            // requires per-sample texture mappings until Genshin is explicitly active for this instance.
            if (!instance.Visible || catalog.Find(instance.Preset.StableId).Definition?.PipelineId != "genshin") continue;
            if (!sceneInputs.TryGetValue(binding.ObjectId, out var inputs)
                || inputs.ObjectId != binding.ObjectId || !inputs.Visible)
                throw new ArgumentException("Visible model-local hair requires same-sample scene inputs for its exact object identity.", nameof(sceneInputs));
            int Map(int local)
            {
                if (!inputs.TextureIndices.TryGetValue(local, out int combined)
                    || (uint)combined >= (uint)scene.Surface.Snapshot.Textures.Count)
                    throw new ArgumentException("A selected local atlas texture has no valid mapping into this composed frame.", nameof(sceneInputs));
                return combined;
            }
            var hair = Make(binding, Map(binding.LightmapLocalTextureIndex), Map(binding.AtlasLocalTextureIndex)); hair.Validate();
            int index = materialIndices[(binding.ObjectId, binding.LocalMaterialIndex)];
            result[index] = result[index] with { Style = result[index].Style with { GenshinHairShadowAtlas = hair } };
        }
        return Array.AsReadOnly(result);
    }

    private static D3D12GenshinHairShadowAtlasBinding Make(D3D12ViewGenshinHairAtlasBinding binding, int lightmap, int atlas)
        => new(lightmap, atlas, binding.AtlasEncoding, binding.ExplicitRowId, binding.DayNight,
            binding.ShadowPosition, binding.ShadowRampWidth, binding.VertexWidth, binding.VertexAo,
            binding.UseLightmapAo, binding.UseVertexColorAo);

    private sealed class DetachedBindings(D3D12ViewGenshinHairAtlasBinding[] values) : IReadOnlyList<D3D12ViewGenshinHairAtlasBinding>
    {
        public int Count => values.Length;
        public D3D12ViewGenshinHairAtlasBinding this[int index] => values[index];
        public IEnumerator<D3D12ViewGenshinHairAtlasBinding> GetEnumerator() => ((IEnumerable<D3D12ViewGenshinHairAtlasBinding>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
