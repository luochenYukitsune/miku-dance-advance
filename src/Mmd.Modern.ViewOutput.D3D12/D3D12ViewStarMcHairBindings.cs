using System.Collections;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>Explicit source-model-local numerical Hair inputs for the Star A response ramp or MC response.
/// UV0/linear-repeat/mip0 and the PMX highlight palette are MDA adapter policies. This host configuration
/// remains dormant on other pipelines; selected Alpha data is available on MC only and never changes coverage.</summary>
public sealed record D3D12ViewStarMcHairBinding(Guid ObjectId, int LocalMaterialIndex,
    int HighlightLocalTextureIndex, int ShadowLocalTextureIndex,
    D3D12HairHighlightChannel HighlightChannel = D3D12HairHighlightChannel.Blue,
    float HighlightCoefficient = 0, float HighlightBrightness = 2, float ShadowCoefficient = .5f,
    float ShadowSoftness = .5f, float Intensity = .1f);

internal static class D3D12ViewStarMcHairBindings
{
    internal static IReadOnlyList<D3D12ViewStarMcHairBinding> Detach(IEnumerable<D3D12ViewStarMcHairBinding>? bindings)
    {
        if (bindings is DetachedBindings detached) return detached;
        var values = bindings?.ToArray() ?? [];
        var keys = new HashSet<(Guid, int)>();
        foreach (var binding in values)
        {
            if (binding is null || binding.ObjectId == Guid.Empty || binding.LocalMaterialIndex < 0)
                throw new ArgumentException("Local Star/MC hair needs explicit object identity and a nonnegative material index.", nameof(bindings));
            if (!keys.Add((binding.ObjectId, binding.LocalMaterialIndex)))
                throw new ArgumentException("Star/MC hair bindings must be unique per object and material.", nameof(bindings));
            Make(binding, binding.HighlightLocalTextureIndex, binding.ShadowLocalTextureIndex).Validate();
        }
        return new DetachedBindings(values);
    }

    internal static IReadOnlyList<D3D12ViewCharacterMaterialBinding> SelectCharacterMaterials(
        IReadOnlyList<D3D12ViewCharacterMaterialBinding> materials, IReadOnlyList<D3D12ViewStarMcHairBinding> bindings,
        IReadOnlyList<D3D12ViewGenshinHairBinding>? genshin = null)
    {
        if (bindings.Count == 0) return materials;
        var occupied = (genshin ?? []).Select(value => (value.ObjectId, value.LocalMaterialIndex)).ToHashSet();
        var selected = materials.ToDictionary(value => (value.ObjectId, value.MaterialIndex));
        var result = materials.ToList();
        foreach (var binding in bindings)
        {
            if (occupied.Contains((binding.ObjectId, binding.LocalMaterialIndex)))
                throw new ArgumentException("A material cannot simultaneously select local Genshin and Star/MC hair inputs.", nameof(bindings));
            if (selected.TryGetValue((binding.ObjectId, binding.LocalMaterialIndex), out var material))
            {
                if (material.Style.Role != D3D12CharacterMaterialRole.Hair)
                    throw new ArgumentException("Star/MC hair cannot replace an explicitly selected Body, Face or Eye role.", nameof(bindings));
                if (material.Style.StarMcHair is not null || material.Style.GenshinNonMetallicSpecular is not null || material.Style.GenshinHairShadowAtlas is not null || material.Style.EndfieldHair is not null)
                    throw new ArgumentException("A material cannot combine local and composed-frame hair inputs.", nameof(bindings));
            }
            else result.Add(new(binding.ObjectId, binding.LocalMaterialIndex, new(D3D12CharacterMaterialRole.Hair)));
        }
        return D3D12ViewCharacterMaterials.Detach(result);
    }

    internal static IReadOnlyList<SceneObjectRenderRequest> CreateRequests(IReadOnlyList<D3D12ViewStarMcHairBinding> bindings,
        IReadOnlyList<D3D12ViewFaceSdfBinding>? faces = null, IReadOnlyList<D3D12ViewGenshinHairBinding>? genshin = null)
    {
        var existing = D3D12ViewGenshinHairBindings.CreateRequests(genshin ?? [], faces);
        var requests = bindings.Select(binding => new SceneObjectRenderRequest(binding.ObjectId,
            localTextureIndices: [binding.HighlightLocalTextureIndex, binding.ShadowLocalTextureIndex]));
        return Array.AsReadOnly(existing.Concat(requests).GroupBy(request => request.ObjectId)
            .Select(group => new SceneObjectRenderRequest(group.Key,
                group.SelectMany(request => request.BoneIndices), group.SelectMany(request => request.LocalTextureIndices),
                group.SelectMany(request => request.OptionalLocalTextureIndices), group.Any(request => request.IncludeSourceGeometry))).ToArray());
    }

    internal static void ValidateConfiguration(D3D12StylePresetCatalog catalog, IReadOnlyList<SceneRenderingStyleBinding> objects,
        IReadOnlyList<D3D12ViewCharacterMaterialBinding> materials, IReadOnlyList<D3D12ViewStarMcHairBinding> bindings)
    {
        if (bindings.Count == 0) return;
        _ = SelectCharacterMaterials(materials, bindings);
        var instances = objects.ToDictionary(instance => instance.ObjectId);
        foreach (var binding in bindings)
        {
            if (!instances.TryGetValue(binding.ObjectId, out var instance))
                throw new ArgumentException("Local Star/MC hair refers to an unknown object.", nameof(bindings));
            if (instance.Role != ModelRole.Character)
                throw new ArgumentException("Star/MC hair requires an explicitly classified Character object.", nameof(bindings));
            if (instance.Visible && binding.LocalMaterialIndex >= instance.MaterialCount)
                throw new ArgumentException("Local hair material is outside its visible instance.", nameof(bindings));
            if (catalog.Find(instance.Preset.StableId).Definition?.PipelineId == "star-rail"
                && binding.HighlightChannel != D3D12HairHighlightChannel.Blue)
                throw new ArgumentException("Star A hair consumes numerical Blue; explicit Alpha is an MC-only data selection.", nameof(bindings));
        }
    }

    internal static IReadOnlyList<D3D12ViewCharacterMaterialBinding> Resolve(OriginalFrameScene scene,
        D3D12StylePresetCatalog catalog, IReadOnlyList<SceneRenderingStyleBinding> objects,
        IReadOnlyList<D3D12ViewCharacterMaterialBinding> materials, IReadOnlyList<D3D12ViewStarMcHairBinding> bindings,
        IReadOnlyDictionary<Guid, SceneObjectRenderInputs> sceneInputs)
    {
        if (bindings.Count == 0) return materials;
        ValidateConfiguration(catalog, objects, materials, bindings);
        var selected = SelectCharacterMaterials(materials, bindings);
        var instances = objects.ToDictionary(instance => instance.ObjectId);
        var indices = selected.Select((value, index) => (value, index))
            .ToDictionary(pair => (pair.value.ObjectId, pair.value.MaterialIndex), pair => pair.index);
        var result = selected.ToArray();
        foreach (var binding in bindings)
        {
            var instance = instances[binding.ObjectId];
            string? family = catalog.Find(instance.Preset.StableId).Definition?.PipelineId;
            if (!instance.Visible || family is not ("star-rail" or "wuthering-waves")) continue;
            if (!sceneInputs.TryGetValue(binding.ObjectId, out var inputs)
                || inputs.ObjectId != binding.ObjectId || !inputs.Visible)
                throw new ArgumentException("Visible local hair requires same-sample inputs for the exact object.", nameof(sceneInputs));
            int Texture(int local)
            {
                if (!inputs.TextureIndices.TryGetValue(local, out int combined)
                    || (uint)combined >= (uint)scene.Surface.Snapshot.Textures.Count)
                    throw new ArgumentException("Local hair image lacks a valid mapping into this frame's texture table.", nameof(sceneInputs));
                return combined;
            }
            var hair = Make(binding, Texture(binding.HighlightLocalTextureIndex), Texture(binding.ShadowLocalTextureIndex));
            hair.Validate();
            int index = indices[(binding.ObjectId, binding.LocalMaterialIndex)];
            result[index] = result[index] with { Style = result[index].Style with { StarMcHair = hair } };
        }
        return Array.AsReadOnly(result);
    }

    private static D3D12StarMcHairBinding Make(D3D12ViewStarMcHairBinding value, int highlight, int shadow)
        => new(highlight, shadow, value.HighlightChannel, value.HighlightCoefficient, value.HighlightBrightness,
            value.ShadowCoefficient, value.ShadowSoftness, value.Intensity);

    private sealed class DetachedBindings(D3D12ViewStarMcHairBinding[] values) : IReadOnlyList<D3D12ViewStarMcHairBinding>
    {
        public int Count => values.Length;
        public D3D12ViewStarMcHairBinding this[int index] => values[index];
        public IEnumerator<D3D12ViewStarMcHairBinding> GetEnumerator() => ((IEnumerable<D3D12ViewStarMcHairBinding>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
