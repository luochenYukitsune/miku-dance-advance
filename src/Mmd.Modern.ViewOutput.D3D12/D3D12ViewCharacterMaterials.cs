using System.Collections;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>Explicit renderer input for one material of one scene instance. MaterialIndex is local to the
/// instance's PMX material table, not the composed frame. It is not inferred from names or written to PMA.</summary>
public sealed record D3D12ViewCharacterMaterialBinding(Guid ObjectId, int MaterialIndex,
    D3D12CharacterMaterialStyle Style);

internal static class D3D12ViewCharacterMaterials
{
    /// <summary>Snapshots host input before a device is created; already detached adapter input is reused.</summary>
    public static IReadOnlyList<D3D12ViewCharacterMaterialBinding> Detach(
        IEnumerable<D3D12ViewCharacterMaterialBinding>? bindings)
    {
        if (bindings is DetachedBindings detached) return detached;
        var values = bindings?.ToArray() ?? [];
        var keys = new HashSet<(Guid, int)>();
        foreach (var binding in values)
        {
            if (binding is null || binding.ObjectId == Guid.Empty || binding.MaterialIndex < 0)
                throw new ArgumentException("Character material bindings require a nonempty object identity and nonnegative local material index.", nameof(bindings));
            if (!keys.Add((binding.ObjectId, binding.MaterialIndex)))
                throw new ArgumentException("Character material bindings must be unique for each object identity and local material index.", nameof(bindings));
            if (binding.Style is null)
                throw new ArgumentException("Character material bindings require an explicit material style.", nameof(bindings));
            binding.Style.Validate();
        }
        return new DetachedBindings(values);
    }

    /// <summary>Composed-frame numerical payloads are active renderer requests. Unlike dormant local
    /// host configuration, these must already match the selected pipeline, even during preflight.</summary>
    internal static void ValidateGenshinHairConfiguration(D3D12StylePresetCatalog catalog,
        IReadOnlyList<SceneRenderingStyleBinding> objects, IReadOnlyList<D3D12ViewCharacterMaterialBinding> bindings)
    {
        var byObject = objects.ToDictionary(instance => instance.ObjectId);
        foreach (var binding in bindings)
        {
            if (binding.Style.GenshinNonMetallicSpecular is null && binding.Style.GenshinHairShadowAtlas is null) continue;
            if (!byObject.TryGetValue(binding.ObjectId, out var instance))
                throw new ArgumentException($"Hair material binding refers to unknown object '{binding.ObjectId:D}'.", nameof(bindings));
            if (instance.Role != ModelRole.Character)
                throw new ArgumentException("Dedicated hair inputs require an explicit Character object.", nameof(bindings));
            var definition = catalog.Find(instance.Preset.StableId).Definition;
            if (definition is not null && definition.PipelineId != "genshin")
                throw new ArgumentException("Composed Genshin hair inputs require the explicitly selected Genshin pipeline.", nameof(bindings));
        }
    }

    internal static void ValidateStarMcHairConfiguration(D3D12StylePresetCatalog catalog,
        IReadOnlyList<SceneRenderingStyleBinding> objects, IReadOnlyList<D3D12ViewCharacterMaterialBinding> bindings)
    {
        var instances = objects.ToDictionary(instance => instance.ObjectId);
        foreach (var binding in bindings)
        {
            if (binding.Style.StarMcHair is not { } hair) continue;
            if (!instances.TryGetValue(binding.ObjectId, out var instance) || instance.Role != ModelRole.Character)
                throw new ArgumentException("Composed Star/MC hair needs an explicit known Character object.", nameof(bindings));
            var definition = catalog.Find(instance.Preset.StableId).Definition;
            if (definition is null) continue; // Existing missing-preset workflow reports availability separately.
            if (definition.PipelineId is not ("star-rail" or "wuthering-waves"))
                throw new ArgumentException("Composed hair inputs must match the Star or MC pipeline.", nameof(bindings));
            if (definition.PipelineId == "star-rail" && hair.HighlightChannel != D3D12HairHighlightChannel.Blue)
                throw new ArgumentException("Star A hair consumes Blue, while Alpha data is MC-only.", nameof(bindings));
        }
    }

    internal static void ValidateEndfieldRimConfiguration(D3D12StylePresetCatalog catalog,
        IReadOnlyList<SceneRenderingStyleBinding> objects, IReadOnlyList<D3D12ViewCharacterMaterialBinding> bindings)
    {
        var instances = objects.ToDictionary(instance => instance.ObjectId);
        foreach (var binding in bindings)
        {
            if (binding.Style.EndfieldRim is null) continue;
            if (!instances.TryGetValue(binding.ObjectId, out var instance) || instance.Role != ModelRole.Character)
                throw new ArgumentException("Composed Endfield rim needs an explicitly selected Character object.", nameof(bindings));
            var definition = catalog.Find(instance.Preset.StableId).Definition;
            if (definition is not null && definition.PipelineId != "endfield")
                throw new ArgumentException("Composed Endfield rim requires the explicitly selected Endfield pipeline.", nameof(bindings));
        }
    }

    public static D3D12CharacterMaterialStyle[] Resolve(int materialCount,
        IReadOnlyList<SceneRenderingStyleBinding> objects,
        IReadOnlyList<D3D12ViewCharacterMaterialBinding> bindings, List<string>? diagnostics = null)
    {
        var result = new D3D12CharacterMaterialStyle[materialCount];
        Array.Fill(result, new D3D12CharacterMaterialStyle());
        if (bindings.Count == 0) return result;
        var byObject = objects.ToDictionary(binding => binding.ObjectId);
        foreach (var binding in bindings)
        {
            if (!byObject.TryGetValue(binding.ObjectId, out var instance))
                throw new ArgumentException($"Character material binding refers to unknown object '{binding.ObjectId:D}'.", nameof(bindings));
            if (instance.Role != ModelRole.Character)
                throw new ArgumentException($"Object '{binding.ObjectId:D}' is {instance.Role}; character material bindings require an explicit Character role.", nameof(bindings));
            if (!instance.Visible)
            {
                diagnostics?.Add($"Hidden object '{binding.ObjectId:D}' retains local material {binding.MaterialIndex} ({binding.Style.Role}) without consuming a frame material; its upper bound is checked when the object is visible.");
                continue;
            }
            if (binding.MaterialIndex >= instance.MaterialCount)
                throw new ArgumentException($"Local material {binding.MaterialIndex} is outside visible object '{binding.ObjectId:D}' with {instance.MaterialCount} materials.", nameof(bindings));
            result[instance.MaterialStart + binding.MaterialIndex] = binding.Style;
            if (binding.Style.Role == D3D12CharacterMaterialRole.Face)
                diagnostics?.Add($"Object '{binding.ObjectId:D}', local material {binding.MaterialIndex}: explicit Face normal and geometric shadow strengths are consumed by the Genshin, Star Rail, Wuthering Waves and Endfield ordinary PMX surfaces; MMD preserves its existing response. Material-level assignment affects the whole draw and does not split mixed face/clothing geometry.");
            else if (binding.Style.Role == D3D12CharacterMaterialRole.Eye)
                diagnostics?.Add($"Object '{binding.ObjectId:D}', local material {binding.MaterialIndex}: explicit Eye geometric shadow strength preserves computed shadow shape with reduced contrast in Genshin, Star Rail, Wuthering Waves and Endfield surfaces; authored normal response, specular and coverage are retained. MMD preserves its existing response. The whole material draw is selected explicitly; FaceSDF is not an Eye input.");
            else if (binding.Style.Role == D3D12CharacterMaterialRole.Hair)
                diagnostics?.Add($"Object '{binding.ObjectId:D}', local material {binding.MaterialIndex}: explicit Hair selection consumes dedicated family-specific numerical inputs only when their pipeline is selected and a compatible binding is active; otherwise the ordinary surface response is retained.");
        }
        return result;
    }

    private sealed class DetachedBindings(D3D12ViewCharacterMaterialBinding[] values)
        : IReadOnlyList<D3D12ViewCharacterMaterialBinding>
    {
        public int Count => values.Length;
        public D3D12ViewCharacterMaterialBinding this[int index] => values[index];
        public IEnumerator<D3D12ViewCharacterMaterialBinding> GetEnumerator()
            => ((IEnumerable<D3D12ViewCharacterMaterialBinding>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
