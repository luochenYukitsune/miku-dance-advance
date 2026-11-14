using System.Collections;
using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>Explicit orientation provenance. FixedWorld directions are already world-space;
/// SelectedBone directions are local to a selected solved bone, never inferred from its name.</summary>
public abstract record D3D12ViewFaceHeadBasis
{
    private D3D12ViewFaceHeadBasis() { }
    public sealed record FixedWorld(Vector3 Forward, Vector3 Right) : D3D12ViewFaceHeadBasis;
    public sealed record SelectedBone(int BoneIndex, Vector3 LocalForward, Vector3 LocalRight) : D3D12ViewFaceHeadBasis;
}

/// <summary>Renderer-owned dedicated face inputs for one explicitly selected Face material.
/// Texture/material indices are source-model local, distinct from D3D12FaceSdfBinding's composed indices.
/// Only textures already included in that source scene can be selected. Transition/Offset retain the
/// encoding's numerical meaning (Genshin Softness/ShadowPosition). No assets or definitions enter PMA.</summary>
public sealed record D3D12ViewFaceSdfBinding(Guid ObjectId, int LocalMaterialIndex, int LocalTextureIndex,
    D3D12ViewFaceHeadBasis HeadBasis, float Transition = 1f, float Offset = 1f,
    D3D12FaceSdfEncoding Encoding = D3D12FaceSdfEncoding.GrayRed, int SecondaryLocalTextureIndex = -1)
{
    public static D3D12ViewFaceSdfBinding GenshinSoft(Guid objectId, int localMaterialIndex,
        int localTextureIndex, int secondaryLocalTextureIndex, D3D12ViewFaceHeadBasis headBasis,
        float softness = .001f, float shadowPosition = .55f)
        => new(objectId, localMaterialIndex, localTextureIndex, headBasis, softness, shadowPosition,
            D3D12FaceSdfEncoding.GenshinSoftFace, secondaryLocalTextureIndex);
}

/// <summary>Pure adapter. It consumes detached data captured with the geometry sample, never a live
/// project callback, a second animator, placement multiplication or guessed contiguous texture offset.</summary>
internal static class D3D12ViewFaceSdfBindings
{
    internal static IReadOnlyList<D3D12ViewFaceSdfBinding> Detach(IEnumerable<D3D12ViewFaceSdfBinding>? bindings)
    {
        if (bindings is DetachedBindings detached) return detached;
        var values = bindings?.ToArray() ?? [];
        var keys = new HashSet<(Guid, int)>();
        foreach (var binding in values)
        {
            if (binding is null || binding.ObjectId == Guid.Empty || binding.LocalMaterialIndex < 0)
                throw new ArgumentException("Local face inputs require nonempty object identity and nonnegative local material index.", nameof(bindings));
            if (!keys.Add((binding.ObjectId, binding.LocalMaterialIndex)))
                throw new ArgumentException("Local face inputs must be unique per object and local material.", nameof(bindings));
            var basis = LocalBasis(binding.HeadBasis);
            Make(binding, binding.LocalTextureIndex, binding.SecondaryLocalTextureIndex, basis.Forward, basis.Right).Validate();
        }
        return new DetachedBindings(values);
    }

    internal static void ValidateCharacterMaterials(IReadOnlyList<D3D12ViewCharacterMaterialBinding> materials,
        IReadOnlyList<D3D12ViewFaceSdfBinding> bindings)
    {
        var selected = materials.ToDictionary(material => (material.ObjectId, material.MaterialIndex));
        foreach (var binding in bindings)
        {
            if (!selected.TryGetValue((binding.ObjectId, binding.LocalMaterialIndex), out var material)
                || material.Style.Role != D3D12CharacterMaterialRole.Face)
                throw new ArgumentException("Model-local FaceSDF requires a matching explicitly selected Face material; Body and Eye are not converted.", nameof(bindings));
            if (material.Style.FaceSdf is not null)
                throw new ArgumentException("A Face material cannot select both model-local and composed-frame FaceSDF inputs.", nameof(bindings));
        }
    }

    internal static IReadOnlyList<SceneObjectRenderRequest> CreateRequests(IReadOnlyList<D3D12ViewFaceSdfBinding> bindings)
        => Array.AsReadOnly(bindings.GroupBy(binding => binding.ObjectId).Select(group =>
            new SceneObjectRenderRequest(group.Key,
                group.Select(binding => binding.HeadBasis).OfType<D3D12ViewFaceHeadBasis.SelectedBone>()
                    .Select(basis => basis.BoneIndex),
                group.SelectMany(binding => binding.SecondaryLocalTextureIndex < 0
                    ? new[] { binding.LocalTextureIndex }
                    : new[] { binding.LocalTextureIndex, binding.SecondaryLocalTextureIndex }))).ToArray());

    internal static void ValidateConfiguration(D3D12StylePresetCatalog catalog,
        IReadOnlyList<SceneRenderingStyleBinding> objects,
        IReadOnlyList<D3D12ViewCharacterMaterialBinding> materials,
        IReadOnlyList<D3D12ViewFaceSdfBinding> bindings)
    {
        if (bindings.Count == 0) return;
        ValidateCharacterMaterials(materials, bindings);
        var byObject = objects.ToDictionary(instance => instance.ObjectId);
        foreach (var binding in bindings)
        {
            if (!byObject.TryGetValue(binding.ObjectId, out var instance))
                throw new ArgumentException($"Local face inputs refer to unknown object '{binding.ObjectId:D}'.", nameof(bindings));
            if (instance.Role != ModelRole.Character)
                throw new ArgumentException("Model-local FaceSDF requires an explicit Character object.", nameof(bindings));
            if (instance.Visible && binding.LocalMaterialIndex >= instance.MaterialCount)
                throw new ArgumentException("A selected local Face material is outside its visible instance.", nameof(bindings));
            var definition = catalog.Find(instance.Preset.StableId).Definition;
            // Unavailable preset reporting is owned by the existing preflight. Never fall back or switch style.
            if (definition is null) continue;
            bool supported = binding.Encoding switch
            {
                D3D12FaceSdfEncoding.GrayRed => definition.PipelineId is "star-rail" or "wuthering-waves",
                D3D12FaceSdfEncoding.EndfieldRgbCmM => definition.PipelineId == "endfield",
                D3D12FaceSdfEncoding.GenshinSoftFace => definition.PipelineId == "genshin",
                _ => false,
            };
            if (!supported)
                throw new ArgumentException($"Face encoding '{binding.Encoding}' is incompatible with selected pipeline '{definition.PipelineId}'. Original MMD remains the default and is never switched automatically.", nameof(bindings));
        }
    }

    internal static IReadOnlyList<D3D12ViewCharacterMaterialBinding> Resolve(OriginalFrameScene scene,
        D3D12StylePresetCatalog catalog, IReadOnlyList<SceneRenderingStyleBinding> objects,
        IReadOnlyList<D3D12ViewCharacterMaterialBinding> materials,
        IReadOnlyList<D3D12ViewFaceSdfBinding> bindings,
        IReadOnlyDictionary<Guid, SceneObjectRenderInputs> sceneInputs)
    {
        if (bindings.Count == 0) return materials;
        ValidateConfiguration(catalog, objects, materials, bindings);
        var instances = objects.ToDictionary(instance => instance.ObjectId);
        var materialIndices = materials.Select((material, index) => (material, index))
            .ToDictionary(pair => (pair.material.ObjectId, pair.material.MaterialIndex), pair => pair.index);
        var result = materials.ToArray();
        foreach (var binding in bindings)
        {
            if (!instances[binding.ObjectId].Visible) continue;
            if (!sceneInputs.TryGetValue(binding.ObjectId, out var inputs)
                || inputs.ObjectId != binding.ObjectId || !inputs.Visible)
                throw new ArgumentException("Visible model-local FaceSDF requires same-sample scene inputs for its exact object identity.", nameof(sceneInputs));
            int TextureIndex(int local)
            {
                if (!inputs.TextureIndices.TryGetValue(local, out int combined)
                    || (uint)combined >= (uint)scene.Surface.Snapshot.Textures.Count)
                    throw new ArgumentException("A selected local face texture has no valid mapping into this composed frame.", nameof(sceneInputs));
                return combined;
            }
            int primary = TextureIndex(binding.LocalTextureIndex);
            int secondary = binding.SecondaryLocalTextureIndex < 0 ? -1 : TextureIndex(binding.SecondaryLocalTextureIndex);
            var basis = LocalBasis(binding.HeadBasis);
            if (binding.HeadBasis is D3D12ViewFaceHeadBasis.SelectedBone bone)
            {
                if (!inputs.BoneWorldMatrices.TryGetValue(bone.BoneIndex, out var world))
                    throw new ArgumentException("A selected face head bone is absent from this geometry sample.", nameof(sceneInputs));
                basis = (Vector3.TransformNormal(basis.Forward, world), Vector3.TransformNormal(basis.Right, world));
            }
            var sdf = Make(binding, primary, secondary, basis.Forward, basis.Right);
            sdf.Validate();
            int index = materialIndices[(binding.ObjectId, binding.LocalMaterialIndex)];
            result[index] = result[index] with { Style = result[index].Style with { FaceSdf = sdf } };
        }
        return Array.AsReadOnly(result);
    }

    private static (Vector3 Forward, Vector3 Right) LocalBasis(D3D12ViewFaceHeadBasis? basis)
        => basis switch
        {
            D3D12ViewFaceHeadBasis.FixedWorld fixedWorld => (fixedWorld.Forward, fixedWorld.Right),
            D3D12ViewFaceHeadBasis.SelectedBone bone when bone.BoneIndex >= 0 => (bone.LocalForward, bone.LocalRight),
            _ => throw new ArgumentException("Face inputs require an explicit fixed-world or nonnegative selected-bone head basis.", nameof(basis)),
        };

    private static D3D12FaceSdfBinding Make(D3D12ViewFaceSdfBinding binding, int primary, int secondary,
        Vector3 forward, Vector3 right)
        => new(primary, forward, right, binding.Transition, binding.Offset,
            Encoding: binding.Encoding, SecondaryCombinedTextureIndex: secondary);

    private sealed class DetachedBindings(D3D12ViewFaceSdfBinding[] values) : IReadOnlyList<D3D12ViewFaceSdfBinding>
    {
        public int Count => values.Length;
        public D3D12ViewFaceSdfBinding this[int index] => values[index];
        public IEnumerator<D3D12ViewFaceSdfBinding> GetEnumerator() => ((IEnumerable<D3D12ViewFaceSdfBinding>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
