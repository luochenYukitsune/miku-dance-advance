using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>Source-local ordinals; current evaluated geometry and frame slots are captured together.</summary>
public sealed record D3D12ViewStarMcBodyBinding(Guid ObjectId, int LocalMaterialIndex,
    int NormalLocalTextureIndex, int DetailLocalTextureIndex, int MaskLocalTextureIndex, int MatcapLocalTextureIndex,
    D3D12MatcapColorEncoding MatcapEncoding, int UvChannel = 0, float NormalStrength = 1f,
    float DetailStrength = 1f, float MetallicSaturation = 1f, float HighlightCoefficient = 0f,
    float HighlightStrength = 1f, float RotationRadians = 0f, float ShadowCoefficient = .5f,
    float ShadowSoftness = .1f, float ShadowColorStrength = 1f, float WarmBandStrength = .25f,
    Vector3? ShadowColor = null, Vector3? RimColor = null, float RimStrength = 0f,
    float RimSize = .75f, float RimDirectionRadians = 0f);

internal static class D3D12ViewStarMcBodyBindings
{
    internal static IReadOnlyList<D3D12ViewStarMcBodyBinding> Detach(IEnumerable<D3D12ViewStarMcBodyBinding>? bindings)
    {
        var values = bindings?.ToArray() ?? [];
        var keys = new HashSet<(Guid, int)>();
        foreach (var value in values)
        {
            if (value is null || value.ObjectId == Guid.Empty || value.LocalMaterialIndex < 0
                || !keys.Add((value.ObjectId, value.LocalMaterialIndex)))
                throw new ArgumentException("Body inputs need unique explicit object/material identities.", nameof(bindings));
            Make(value, value.NormalLocalTextureIndex, value.DetailLocalTextureIndex,
                value.MaskLocalTextureIndex, value.MatcapLocalTextureIndex).Validate();
        }
        return Array.AsReadOnly(values);
    }

    internal static IReadOnlyList<D3D12ViewCharacterMaterialBinding> SelectCharacterMaterials(
        IReadOnlyList<D3D12ViewCharacterMaterialBinding> materials, IReadOnlyList<D3D12ViewStarMcBodyBinding> bindings,
        IEnumerable<(Guid, int)>? occupied = null)
    {
        var conflicts = (occupied ?? []).ToHashSet();
        var result = materials.ToList();
        var selected = materials.ToDictionary(v => (v.ObjectId, v.MaterialIndex));
        foreach (var value in bindings)
        {
            selected.TryGetValue((value.ObjectId, value.LocalMaterialIndex), out var material);
            var style = material?.Style ?? new(D3D12CharacterMaterialRole.Body);
            if (conflicts.Contains((value.ObjectId, value.LocalMaterialIndex))
                || style.Role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair)
                || style.StarMcBody is not null || style.FaceSdf is not null || style.PmxMaterialTreatment is not null
                || style.GenshinNonMetallicSpecular is not null || style.GenshinHairShadowAtlas is not null
                || style.GenshinMetallic is not null || style.StarMcHair is not null || style.EndfieldHair is not null
                || style.EndfieldMatcap is not null || style.EndfieldRim is not null)
                throw new ArgumentException("Star/MC Body adaptation requires Body/Hair without conflicting dedicated or composed inputs.", nameof(bindings));
            if (material is null) result.Add(new(value.ObjectId, value.LocalMaterialIndex, style));
        }
        return D3D12ViewCharacterMaterials.Detach(result);
    }

    internal static IReadOnlyList<SceneObjectRenderRequest> CreateRequests(IReadOnlyList<D3D12ViewStarMcBodyBinding> bindings,
        IReadOnlyList<SceneObjectRenderRequest> existing, IReadOnlyList<D3D12ViewCharacterMaterialBinding> materials)
    {
        var local = bindings.Select(v => new SceneObjectRenderRequest(v.ObjectId,
            optionalLocalTextureIndices: [v.NormalLocalTextureIndex, v.DetailLocalTextureIndex,
                v.MaskLocalTextureIndex, v.MatcapLocalTextureIndex], includeSourceGeometry: true));
        var composed = materials.Where(v => v.Style.StarMcBody is not null)
            .Select(v => new SceneObjectRenderRequest(v.ObjectId, includeSourceGeometry: true));
        return Array.AsReadOnly(existing.Concat(local).Concat(composed).GroupBy(v => v.ObjectId)
            .Select(group => new SceneObjectRenderRequest(group.Key, group.SelectMany(v => v.BoneIndices),
                group.SelectMany(v => v.LocalTextureIndices), group.SelectMany(v => v.OptionalLocalTextureIndices),
                group.Any(v => v.IncludeSourceGeometry))).ToArray());
    }

    internal static void ValidateConfiguration(D3D12StylePresetCatalog catalog, IReadOnlyList<SceneRenderingStyleBinding> objects,
        IReadOnlyList<D3D12ViewCharacterMaterialBinding> materials, IReadOnlyList<D3D12ViewStarMcBodyBinding> bindings)
    {
        _ = SelectCharacterMaterials(materials, bindings);
        var instances = objects.ToDictionary(v => v.ObjectId);
        foreach (var (id, local) in bindings.Select(v => (v.ObjectId, v.LocalMaterialIndex))
            .Concat(materials.Where(v => v.Style.StarMcBody is not null).Select(v => (v.ObjectId, v.MaterialIndex))))
        {
            if (!instances.TryGetValue(id, out var instance) || instance.Role != ModelRole.Character
                || (instance.Visible && local >= instance.MaterialCount))
                throw new ArgumentException("Body input requires a known Character and valid visible local material.");
        }
        foreach (var value in materials.Where(v => v.Style.StarMcBody is not null))
            if (catalog.Find(instances[value.ObjectId].Preset.StableId).Definition is { } definition
                && definition.PipelineId is not ("star-rail" or "wuthering-waves"))
                throw new ArgumentException("Composed Body data requires the selected Star/MC pipeline.", nameof(materials));
    }

    internal static IReadOnlyList<D3D12ViewCharacterMaterialBinding> Resolve(OriginalFrameScene scene,
        D3D12StylePresetCatalog catalog, IReadOnlyList<SceneRenderingStyleBinding> objects,
        IReadOnlyList<D3D12ViewCharacterMaterialBinding> materials, IReadOnlyList<D3D12ViewStarMcBodyBinding> bindings,
        IReadOnlyDictionary<Guid, SceneObjectRenderInputs> sceneInputs)
    {
        ValidateConfiguration(catalog, objects, materials, bindings);
        var result = SelectCharacterMaterials(materials, bindings).ToArray();
        var instances = objects.ToDictionary(v => v.ObjectId);
        var indices = result.Select((v, i) => (v, i)).ToDictionary(p => (p.v.ObjectId, p.v.MaterialIndex), p => p.i);
        SceneObjectRenderInputs Inputs(Guid id, int uv)
        {
            if (!sceneInputs.TryGetValue(id, out var inputs) || inputs.ObjectId != id || !inputs.Visible
                || inputs.SourceGeometry is not { } geometry)
                throw new ArgumentException("Body data requires exact-object same-sample evaluated source geometry.", nameof(sceneInputs));
            D3D12ViewEndfieldMatcapBindings.ValidateUv(geometry, uv);
            return inputs;
        }
        foreach (var material in materials.Where(v => v.Style.StarMcBody is not null && instances[v.ObjectId].Visible))
        {
            var payload = material.Style.StarMcBody!;
            var inputs = Inputs(material.ObjectId, payload.UvChannel);
            result[indices[(material.ObjectId, material.MaterialIndex)]] = material with
            { Style = material.Style with { StarMcBody = payload with { Geometry = new(inputs.SourceGeometry!, material.MaterialIndex) } } };
        }
        foreach (var value in bindings)
        {
            var instance = instances[value.ObjectId];
            if (!instance.Visible || catalog.Find(instance.Preset.StableId).Definition?.PipelineId is not ("star-rail" or "wuthering-waves")) continue;
            var inputs = Inputs(value.ObjectId, value.UvChannel);
            int Map(int local)
            {
                if (!inputs.TextureIndices.TryGetValue(local, out int combined) || (uint)combined >= (uint)scene.Surface.Snapshot.Textures.Count
                    || scene.Surface.Snapshot.Textures[combined].Kind != SceneTextureKind.Image)
                    throw new ArgumentException("Every selected Body image needs an exact-object same-sample mapping.", nameof(sceneInputs));
                return combined;
            }
            var payload = Make(value, Map(value.NormalLocalTextureIndex), Map(value.DetailLocalTextureIndex),
                Map(value.MaskLocalTextureIndex), Map(value.MatcapLocalTextureIndex), new(inputs.SourceGeometry!, value.LocalMaterialIndex));
            payload.Validate();
            int index = indices[(value.ObjectId, value.LocalMaterialIndex)];
            result[index] = result[index] with { Style = result[index].Style with { StarMcBody = payload } };
        }
        return Array.AsReadOnly(result);
    }

    private static D3D12StarMcBodyBinding Make(D3D12ViewStarMcBodyBinding v, int n, int detail, int mask, int matcap,
        D3D12EndfieldMatcapGeometry? geometry = null) => new(n, detail, mask, matcap, v.MatcapEncoding, v.UvChannel,
        v.NormalStrength, v.DetailStrength, v.MetallicSaturation, v.HighlightCoefficient, v.HighlightStrength,
        v.RotationRadians, v.ShadowCoefficient, v.ShadowSoftness, v.ShadowColorStrength, v.WarmBandStrength,
        v.ShadowColor, v.RimColor, v.RimStrength, v.RimSize, v.RimDirectionRadians, geometry);
}
