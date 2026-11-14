using System.Collections;
using Mmd.Modern.Application;
using Mmd.Modern.Core;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>Explicit modern outline multiplier for one scene instance's local material. A scale of one
/// retains the selected rendering style's effective outline; zero disables that material's outline.</summary>
public sealed record D3D12ViewOutlineWidthBinding(Guid ObjectId, int MaterialIndex, float Scale = 1f)
{
    public void Validate()
    {
        if (ObjectId == Guid.Empty || MaterialIndex < 0)
            throw new ArgumentException("Outline width bindings require a nonempty object identity and nonnegative local material index.");
        if (!float.IsFinite(Scale) || Scale < 0 || Scale > 4)
            throw new ArgumentOutOfRangeException(nameof(Scale), "Outline width scale must be finite and between zero and four.");
    }
}

internal static class D3D12ViewOutlineWidths
{
    internal readonly record struct MaterialScale(int MaterialIndex, float Scale);

    /// <summary>Freezes host input before a device is created; duplicate local selections are rejected.</summary>
    public static IReadOnlyList<D3D12ViewOutlineWidthBinding> Detach(IEnumerable<D3D12ViewOutlineWidthBinding>? bindings)
    {
        if (bindings is DetachedBindings detached) return detached;
        var values = bindings?.ToArray() ?? [];
        var keys = new HashSet<(Guid, int)>();
        foreach (var binding in values)
        {
            if (binding is null) throw new ArgumentException("Outline width bindings cannot contain null.", nameof(bindings));
            binding.Validate();
            if (!keys.Add((binding.ObjectId, binding.MaterialIndex)))
                throw new ArgumentException("Outline width bindings must be unique for each object identity and local material index.", nameof(bindings));
        }
        return new DetachedBindings(values);
    }

    /// <summary>Uses the already validated scene-instance ranges without changing any material role.
    /// Hidden selections remain dormant until their source material table is visible.</summary>
    public static MaterialScale[] Resolve(IReadOnlyList<SceneRenderingStyleBinding> objects,
        IReadOnlyList<D3D12ViewOutlineWidthBinding> bindings)
    {
        if (bindings.Count == 0) return [];
        var byObject = objects.ToDictionary(value => value.ObjectId);
        var result = new List<MaterialScale>(bindings.Count);
        foreach (var binding in bindings)
        {
            if (!byObject.TryGetValue(binding.ObjectId, out var instance))
                throw new ArgumentException($"Outline width binding refers to unknown object '{binding.ObjectId:D}'.", nameof(bindings));
            if (!instance.Visible) continue;
            if (binding.MaterialIndex >= instance.MaterialCount)
                throw new ArgumentException($"Local material {binding.MaterialIndex} is outside visible object '{binding.ObjectId:D}' with {instance.MaterialCount} materials.", nameof(bindings));
            result.Add(new(instance.MaterialStart + binding.MaterialIndex, binding.Scale));
        }
        return result.ToArray();
    }

    /// <summary>Applies an independent per-draw factor after style resolution. Shared base widths,
    /// selected vertex materials, outline recorders, colors and drawing eligibility remain unchanged.</summary>
    public static OriginalFrameScene Apply(OriginalFrameScene scene, IReadOnlyList<MaterialScale> bindings)
    {
        if (scene.Outline is null || bindings.Count == 0) return scene;
        OriginalFrameMaterialFlags[]? flags = null;
        foreach (var binding in bindings)
        {
            int index = binding.MaterialIndex;
            if (scene.Materials[index].OutlineWidthScale == binding.Scale) continue;
            flags ??= scene.Materials.ToArray();
            flags[index] = flags[index] with { OutlineWidthScale = binding.Scale };
        }
        if (flags is null) return scene;
        return new(scene.Surface, scene.Outline, scene.OutlineOptions, scene.Options, flags,
            scene.Lighting, scene.EditorOverlay);
    }

    private sealed class DetachedBindings(D3D12ViewOutlineWidthBinding[] values) : IReadOnlyList<D3D12ViewOutlineWidthBinding>
    {
        public int Count => values.Length;
        public D3D12ViewOutlineWidthBinding this[int index] => values[index];
        public IEnumerator<D3D12ViewOutlineWidthBinding> GetEnumerator()
            => ((IEnumerable<D3D12ViewOutlineWidthBinding>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
