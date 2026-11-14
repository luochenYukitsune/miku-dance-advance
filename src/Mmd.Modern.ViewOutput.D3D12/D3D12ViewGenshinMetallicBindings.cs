using System.Collections;
using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>Explicit source-local UV0 numerical lightmap and ChannelPacked color MatCap.
/// Palette colors are source numeric inputs, before the adapter's Gamma 2.2 conversion.
/// Specular.RampCombinedTextureIndex is a SOURCE-LOCAL texture ordinal in this View record,
/// despite its renderer type's name; it is rebased only from the same captured object inputs.</summary>
public sealed record D3D12ViewGenshinMetallicBinding(Guid ObjectId, int LocalMaterialIndex,
    int LightmapLocalTextureIndex, int MatcapLocalTextureIndex, D3D12MatcapColorEncoding MatcapEncoding,
    Vector3 DarkColor, Vector3 LightColor, Vector3 ShadowMultiplyColor, float EnableMetallics = 1f,
    float TileScale = 1f, float Brightness = 1f,
    D3D12GenshinMetallicLitMode LitMode = D3D12GenshinMetallicLitMode.PrimaryResponse, float UniformLitFactor = 1f,
    D3D12GenshinMetallicSpecularBinding? Specular = null);

internal static class D3D12ViewGenshinMetallicBindings
{
    internal static IReadOnlyList<D3D12ViewGenshinMetallicBinding> Detach(IEnumerable<D3D12ViewGenshinMetallicBinding>? bindings)
    {
        if (bindings is DetachedBindings detached) return detached;
        var values = bindings?.ToArray() ?? [];
        var keys = new HashSet<(Guid, int)>();
        foreach (var value in values)
        {
            if (value is null || value.ObjectId == Guid.Empty || value.LocalMaterialIndex < 0)
                throw new ArgumentException("Metallic requires explicit object identity and a nonnegative source-local material.", nameof(bindings));
            if (!keys.Add((value.ObjectId, value.LocalMaterialIndex)))
                throw new ArgumentException("Metallic mappings must be unique per object and material.", nameof(bindings));
            Make(value, value.LightmapLocalTextureIndex, value.MatcapLocalTextureIndex, value.Specular?.RampCombinedTextureIndex).Validate();
        }
        return new DetachedBindings(values);
    }

    internal static IReadOnlyList<D3D12ViewCharacterMaterialBinding> SelectCharacterMaterials(
        IReadOnlyList<D3D12ViewCharacterMaterialBinding> materials, IReadOnlyList<D3D12ViewGenshinMetallicBinding> bindings,
        IReadOnlyList<D3D12ViewGenshinHairBinding>? genshin = null, IReadOnlyList<D3D12ViewGenshinHairAtlasBinding>? atlas = null,
        IReadOnlyList<D3D12ViewStarMcHairBinding>? starMc = null, IReadOnlyList<D3D12ViewEndfieldHairBinding>? radial = null,
        IReadOnlyList<D3D12ViewEndfieldMatcapBinding>? matcap = null)
    {
        if (bindings.Count == 0) return materials;
        var occupied = (starMc ?? []).Select(v => (v.ObjectId, v.LocalMaterialIndex))
            .Concat((radial ?? []).Select(v => (v.ObjectId, v.LocalMaterialIndex)))
            .Concat((matcap ?? []).Select(v => (v.ObjectId, v.LocalMaterialIndex))).ToHashSet();
        var selected = materials.ToDictionary(v => (v.ObjectId, v.MaterialIndex));
        var result = materials.ToList();
        foreach (var value in bindings)
        {
            if (occupied.Contains((value.ObjectId, value.LocalMaterialIndex)))
                throw new ArgumentException("Genshin metallic cannot share other-family dedicated images.", nameof(bindings));
            selected.TryGetValue((value.ObjectId, value.LocalMaterialIndex), out var material);
            var style = material?.Style ?? new(D3D12CharacterMaterialRole.Body);
            if (style.Role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair)
                || style.GenshinMetallic is not null || style.FaceSdf is not null || style.PmxMaterialTreatment is not null
                || style.StarMcHair is not null || style.EndfieldHair is not null || style.EndfieldMatcap is not null || style.EndfieldRim is not null)
                throw new ArgumentException("Metallic requires explicit Body/Hair without conflicting composed or other-family inputs.", nameof(bindings));
            var hair = genshin?.SingleOrDefault(v => v.ObjectId == value.ObjectId && v.LocalMaterialIndex == value.LocalMaterialIndex);
            var shadow = atlas?.SingleOrDefault(v => v.ObjectId == value.ObjectId && v.LocalMaterialIndex == value.LocalMaterialIndex);
            if ((hair is not null && hair.LocalTextureIndex != value.LightmapLocalTextureIndex)
                || (shadow is not null && shadow.LightmapLocalTextureIndex != value.LightmapLocalTextureIndex))
                throw new ArgumentException("Co-bound Genshin metallic, R/B and atlas require the identical explicit UV0 source lightmap.", nameof(bindings));
            if (material is null) result.Add(new(value.ObjectId, value.LocalMaterialIndex, style));
        }
        return D3D12ViewCharacterMaterials.Detach(result);
    }

    internal static IReadOnlyList<SceneObjectRenderRequest> CreateRequests(IReadOnlyList<D3D12ViewGenshinMetallicBinding> bindings,
        IReadOnlyList<SceneObjectRenderRequest>? existing = null)
    {
        var requests = bindings.Select(v => new SceneObjectRenderRequest(v.ObjectId,
            optionalLocalTextureIndices: v.Specular?.RampCombinedTextureIndex is { } ramp
                ? [v.LightmapLocalTextureIndex, v.MatcapLocalTextureIndex, ramp]
                : [v.LightmapLocalTextureIndex, v.MatcapLocalTextureIndex]));
        return Array.AsReadOnly((existing ?? []).Concat(requests).GroupBy(v => v.ObjectId)
            .Select(group => new SceneObjectRenderRequest(group.Key, group.SelectMany(v => v.BoneIndices),
                group.SelectMany(v => v.LocalTextureIndices), group.SelectMany(v => v.OptionalLocalTextureIndices),
                group.Any(v => v.IncludeSourceGeometry))).ToArray());
    }

    internal static void ValidateConfiguration(D3D12StylePresetCatalog catalog, IReadOnlyList<SceneRenderingStyleBinding> objects,
        IReadOnlyList<D3D12ViewCharacterMaterialBinding> materials, IReadOnlyList<D3D12ViewGenshinMetallicBinding> bindings)
    {
        _ = SelectCharacterMaterials(materials, bindings);
        var instances = objects.ToDictionary(v => v.ObjectId);
        foreach (var value in bindings)
        {
            if (!instances.TryGetValue(value.ObjectId, out var instance) || instance.Role != ModelRole.Character)
                throw new ArgumentException("Metallic requires a known explicit Character object.", nameof(bindings));
            if (instance.Visible && value.LocalMaterialIndex >= instance.MaterialCount)
                throw new ArgumentException("Metallic source-local material is outside the visible instance.", nameof(bindings));
        }
        foreach (var value in materials.Where(v => v.Style.GenshinMetallic is not null))
        {
            if (!instances.TryGetValue(value.ObjectId, out var instance) || instance.Role != ModelRole.Character)
                throw new ArgumentException("Composed metallic requires a known Character object.", nameof(materials));
            var definition = catalog.Find(instance.Preset.StableId).Definition;
            if (definition is not null && definition.PipelineId != "genshin")
                throw new ArgumentException("Composed metallic requires the explicitly selected Genshin pipeline.", nameof(materials));
        }
    }

    internal static IReadOnlyList<D3D12ViewCharacterMaterialBinding> Resolve(OriginalFrameScene scene,
        D3D12StylePresetCatalog catalog, IReadOnlyList<SceneRenderingStyleBinding> objects,
        IReadOnlyList<D3D12ViewCharacterMaterialBinding> materials, IReadOnlyList<D3D12ViewGenshinMetallicBinding> bindings,
        IReadOnlyDictionary<Guid, SceneObjectRenderInputs> sceneInputs)
    {
        ValidateConfiguration(catalog, objects, materials, bindings);
        if (bindings.Count == 0) return materials;
        var selected = SelectCharacterMaterials(materials, bindings);
        var result = selected.ToArray();
        var instances = objects.ToDictionary(v => v.ObjectId);
        var indices = selected.Select((v, i) => (v, i)).ToDictionary(v => (v.v.ObjectId, v.v.MaterialIndex), v => v.i);
        foreach (var value in bindings)
        {
            var instance = instances[value.ObjectId];
            if (!instance.Visible || catalog.Find(instance.Preset.StableId).Definition?.PipelineId != "genshin") continue;
            if (!sceneInputs.TryGetValue(value.ObjectId, out var inputs) || inputs.ObjectId != value.ObjectId || !inputs.Visible)
                throw new ArgumentException("Metallic requires same-sample texture mappings for its exact object identity.", nameof(sceneInputs));
            int Map(int local)
            {
                if (!inputs.TextureIndices.TryGetValue(local, out int combined) || (uint)combined >= (uint)scene.Surface.Snapshot.Textures.Count
                    || scene.Surface.Snapshot.Textures[combined].Kind != SceneTextureKind.Image)
                    throw new ArgumentException("Metallic lightmap/color/specular ramp requires explicit same-sample Image mappings.", nameof(sceneInputs));
                return combined;
            }
            var payload = Make(value, Map(value.LightmapLocalTextureIndex), Map(value.MatcapLocalTextureIndex),
                value.Specular?.RampCombinedTextureIndex is { } ramp ? Map(ramp) : null);
            payload.Validate();
            int index = indices[(value.ObjectId, value.LocalMaterialIndex)];
            var style = result[index].Style;
            if ((style.GenshinNonMetallicSpecular is { } hair && hair.CombinedTextureIndex != payload.LightmapCombinedTextureIndex)
                || (style.GenshinHairShadowAtlas is { } atlas && atlas.LightmapCombinedTextureIndex != payload.LightmapCombinedTextureIndex))
                throw new ArgumentException("Co-bound Genshin metallic, R/B and atlas require the identical current UV0 lightmap.", nameof(materials));
            result[index] = result[index] with { Style = style with { GenshinMetallic = payload } };
        }
        return Array.AsReadOnly(result);
    }

    private static D3D12GenshinMetallicBinding Make(D3D12ViewGenshinMetallicBinding v, int lightmap, int matcap, int? ramp)
        => new(lightmap, matcap, v.MatcapEncoding, v.DarkColor, v.LightColor, v.ShadowMultiplyColor,
            v.EnableMetallics, v.TileScale, v.Brightness, v.LitMode, v.UniformLitFactor,
            v.Specular is { } specular ? specular with { RampCombinedTextureIndex = ramp } : null);

    private sealed class DetachedBindings(D3D12ViewGenshinMetallicBinding[] values) : IReadOnlyList<D3D12ViewGenshinMetallicBinding>
    {
        public int Count => values.Length;
        public D3D12ViewGenshinMetallicBinding this[int index] => values[index];
        public IEnumerator<D3D12ViewGenshinMetallicBinding> GetEnumerator() => ((IEnumerable<D3D12ViewGenshinMetallicBinding>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
