using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>
/// Generic host binding associating a scene instance and source-local material index with a plugin-defined
/// <see cref="StyleMaterialBindingData"/> payload. Multiple bindings may target the same material
/// (for example composed Hair + Rim + MatCap modes within a style).
/// </summary>
public sealed record D3D12ViewPluginMaterialBinding(
    Guid ObjectId,
    int LocalMaterialIndex,
    StyleMaterialBindingData Binding);

/// <summary>
/// Validation, detachment, and scene-input request generation for <see cref="D3D12ViewPluginMaterialBinding"/> collections.
/// </summary>
public static class D3D12ViewPluginMaterialBindings
{
    public static IReadOnlyList<D3D12ViewPluginMaterialBinding> Detach(
        IEnumerable<D3D12ViewPluginMaterialBinding>? bindings)
    {
        if (bindings is DetachedBindings detached)
            return detached;

        var raw = bindings?.ToArray() ?? [];
        if (raw.Length == 0)
            return DetachedBindings.Empty;

        var copy = new D3D12ViewPluginMaterialBinding[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            var entry = raw[i];
            if (entry is null || entry.ObjectId == Guid.Empty || entry.LocalMaterialIndex < 0)
                throw new ArgumentException("Plugin material bindings require a nonempty object identity and nonnegative local material index.", nameof(bindings));
            copy[i] = new D3D12ViewPluginMaterialBinding(
                entry.ObjectId,
                entry.LocalMaterialIndex,
                D3D12StylePluginAdapter.DetachBindingData(entry.Binding));
        }
        return new DetachedBindings(copy);
    }

    public static IReadOnlyDictionary<string, StyleMaterialBindingData>? DetachGlobalParameters(
        IReadOnlyDictionary<string, StyleMaterialBindingData>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
            return null;

        var copy = new Dictionary<string, StyleMaterialBindingData>(parameters.Count, StringComparer.Ordinal);
        foreach (var (styleId, data) in parameters)
        {
            D3D12StylePresetDefinition.ValidateIdentifier(styleId, nameof(parameters));
            if (data is null)
                throw new ArgumentException($"Global parameters for style '{styleId}' cannot be null.", nameof(parameters));
            var detached = D3D12StylePluginAdapter.DetachBindingData(data);
            if (!string.Equals(detached.StyleId, styleId, StringComparison.Ordinal))
                throw new ArgumentException($"Global parameters dictionary key '{styleId}' must match StyleId '{detached.StyleId}'.", nameof(parameters));
            copy.Add(styleId, detached);
        }
        return new ReadOnlyDictionary<string, StyleMaterialBindingData>(copy);
    }

    public static void Validate(
        IReadOnlyList<D3D12ViewCharacterMaterial> characterMaterials,
        IReadOnlyList<D3D12ViewPluginMaterialBinding> bindings,
        D3D12ViewStyleCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(characterMaterials);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(catalog);

        var matMap = new Dictionary<(Guid ObjectId, int LocalMaterialIndex), D3D12ViewCharacterMaterial>(characterMaterials.Count);
        foreach (var material in characterMaterials)
        {
            if (material is null)
                throw new ArgumentException("Character material overrides cannot contain null.", nameof(characterMaterials));
            material.Validate();
            if (!matMap.TryAdd((material.ObjectId, material.LocalMaterialIndex), material))
                throw new ArgumentException("Character material overrides must be unique per object identity and local material index.", nameof(characterMaterials));
        }

        if (bindings.Count == 0)
            return;

        var detached = Detach(bindings);
        foreach (var materialGroup in detached.GroupBy(static b => (b.ObjectId, b.LocalMaterialIndex)))
        {
            matMap.TryGetValue(materialGroup.Key, out var charMat);
            foreach (var styleGroup in materialGroup.GroupBy(static b => b.Binding.StyleId, StringComparer.Ordinal))
            {
                if (string.Equals(styleGroup.Key, D3D12SurfaceStyleMap.DefaultStyleId, StringComparison.Ordinal))
                    throw new ArgumentException("Built-in 'mmd' style does not accept plugin material bindings.", nameof(bindings));

                var style = catalog.FindStyle(styleGroup.Key);
                if (style is null)
                    continue;

                var styleBindings = styleGroup.Select(static b => b.Binding).ToArray();
                var role = ResolveInspectionRole(
                    style,
                    materialGroup.Key.ObjectId,
                    materialGroup.Key.LocalMaterialIndex,
                    materialName: null,
                    charMat,
                    guessMaterialRoles: false,
                    styleBindings).ResolvedRole;

                var validation = style.ValidateMaterial(new StyleMaterialValidationContext(
                    materialGroup.Key.ObjectId,
                    materialGroup.Key.LocalMaterialIndex,
                    MaterialName: null,
                    ResolvedRole: role,
                    Bindings: styleBindings,
                    SourceTextureCount: int.MaxValue,
                    SourceTextureDimensions: null));

                if (!validation.IsValid)
                    throw new ArgumentException(
                        validation.ErrorMessage ?? $"Material {materialGroup.Key.LocalMaterialIndex} validation failed for style '{styleGroup.Key}'.",
                        nameof(bindings));
            }
        }
    }

    public static IReadOnlyList<SceneObjectRenderRequest> CreateRequests(
        IReadOnlyList<D3D12ViewPluginMaterialBinding> bindings,
        D3D12ViewStyleCatalog catalog)
        => CreateRequests(bindings, catalog, characterMaterials: null, boneIndicesByObject: null);

    public static IReadOnlyList<SceneObjectRenderRequest> CreateRequests(
        IReadOnlyList<D3D12ViewPluginMaterialBinding> bindings,
        D3D12ViewStyleCatalog catalog,
        IReadOnlyList<D3D12ViewCharacterMaterial>? characterMaterials,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? boneIndicesByObject = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(catalog);

        if (bindings.Count == 0)
            return Array.Empty<SceneObjectRenderRequest>();

        var detached = Detach(bindings);
        var charMatMap = new Dictionary<(Guid, int), D3D12ViewCharacterMaterial>();
        if (characterMaterials is not null)
        {
            foreach (var cm in characterMaterials)
            {
                if (cm is not null)
                    charMatMap.TryAdd((cm.ObjectId, cm.LocalMaterialIndex), cm);
            }
        }

        var objectRequests = new Dictionary<Guid, ObjectRequestAccumulator>();

        foreach (var materialGroup in detached.GroupBy(static b => (b.ObjectId, b.LocalMaterialIndex)))
        {
            charMatMap.TryGetValue(materialGroup.Key, out var charMat);
            foreach (var styleGroup in materialGroup.GroupBy(static b => b.Binding.StyleId, StringComparer.Ordinal))
            {
                var style = catalog.FindStyle(styleGroup.Key);
                if (style is null)
                    continue;

                var styleBindings = styleGroup.Select(static b => b.Binding).ToArray();
                var role = ResolveInspectionRole(
                    style,
                    materialGroup.Key.ObjectId,
                    materialGroup.Key.LocalMaterialIndex,
                    materialName: null,
                    charMat,
                    guessMaterialRoles: false,
                    styleBindings).ResolvedRole;

                var bindingContext = new StyleMaterialBindingContext(
                    materialGroup.Key.ObjectId,
                    materialGroup.Key.LocalMaterialIndex,
                    MaterialName: null,
                    ResolvedRole: role,
                    Bindings: styleBindings);

                var texRequests = style.GetSceneTextureRequests(bindingContext);
                var boneRequest = style.GetBoneTransformRequest(bindingContext);
                bool needsSourceGeometry = style.RequiresSourceGeometry(bindingContext);

                if ((texRequests is null || texRequests.Count == 0) && boneRequest is null && !needsSourceGeometry)
                    continue;

                if (!objectRequests.TryGetValue(materialGroup.Key.ObjectId, out var acc))
                {
                    acc = new ObjectRequestAccumulator();
                    objectRequests.Add(materialGroup.Key.ObjectId, acc);
                }

                acc.IncludeSourceGeometry |= needsSourceGeometry;

                if (texRequests is not null)
                {
                    foreach (var texReq in texRequests)
                    {
                        if (texReq is null)
                            throw new ArgumentException($"Style '{styleGroup.Key}' returned a null scene texture request.", nameof(catalog));
                        if (texReq.LocalTextureIndex < -1)
                            throw new ArgumentOutOfRangeException(nameof(catalog), $"Invalid local texture index {texReq.LocalTextureIndex} requested by style '{styleGroup.Key}'.");
                        if (texReq.LocalTextureIndex >= 0)
                        {
                            if (texReq.IsOptional)
                                acc.OptionalTextures.Add(texReq.LocalTextureIndex);
                            else
                                acc.RequiredTextures.Add(texReq.LocalTextureIndex);
                        }
                    }
                }

                if (boneRequest is not null
                    && TryResolveBoneIndex(materialGroup.Key.ObjectId, boneRequest.BoneName, boneIndicesByObject, out int boneIndex))
                {
                    acc.Bones.Add(boneIndex);
                }
            }
        }

        if (objectRequests.Count == 0)
            return Array.Empty<SceneObjectRenderRequest>();

        var result = new List<SceneObjectRenderRequest>(objectRequests.Count);
        foreach (var (objectId, acc) in objectRequests)
        {
            if (acc.Bones.Count == 0
                && acc.RequiredTextures.Count == 0
                && acc.OptionalTextures.Count == 0
                && !acc.IncludeSourceGeometry)
            {
                continue;
            }
            result.Add(new SceneObjectRenderRequest(
                objectId,
                boneIndices: acc.Bones,
                localTextureIndices: acc.RequiredTextures,
                optionalLocalTextureIndices: acc.OptionalTextures,
                includeSourceGeometry: acc.IncludeSourceGeometry));
        }

        return Array.AsReadOnly(result.ToArray());
    }

    private sealed class ObjectRequestAccumulator
    {
        public List<int> Bones { get; } = [];
        public List<int> RequiredTextures { get; } = [];
        public List<int> OptionalTextures { get; } = [];
        public bool IncludeSourceGeometry { get; set; }
    }

    internal static void ValidateSceneObjects(
        IReadOnlyList<SceneRenderingStyleBinding> objects,
        IReadOnlyList<D3D12ViewPluginMaterialBinding> bindings,
        D3D12StylePresetCatalog presetCatalog,
        D3D12ViewStyleCatalog styleCatalog,
        IReadOnlyList<D3D12ViewCharacterMaterial> characterMaterials)
    {
        if (bindings.Count == 0)
            return;

        var byObject = objects.ToDictionary(static instance => instance.ObjectId);
        var charMatMap = characterMaterials.ToDictionary(static cm => (cm.ObjectId, cm.LocalMaterialIndex));

        foreach (var binding in bindings)
        {
            if (string.Equals(binding.Binding.StyleId, D3D12SurfaceStyleMap.DefaultStyleId, StringComparison.Ordinal))
                throw new ArgumentException("Built-in 'mmd' style does not accept plugin material bindings.", nameof(bindings));
            if (!byObject.TryGetValue(binding.ObjectId, out var instance))
                throw new ArgumentException($"Plugin material binding refers to unknown object '{binding.ObjectId:D}'.", nameof(bindings));
            if (instance.Role != ModelRole.Character)
                throw new ArgumentException($"Object '{binding.ObjectId:D}' is {instance.Role}; plugin material bindings require an explicit Character role.", nameof(bindings));
            if (instance.Visible && binding.LocalMaterialIndex >= instance.MaterialCount)
                throw new ArgumentException($"Local material {binding.LocalMaterialIndex} is outside visible object '{binding.ObjectId:D}' with {instance.MaterialCount} materials.", nameof(bindings));
        }

        foreach (var materialGroup in bindings.GroupBy(static b => (b.ObjectId, b.LocalMaterialIndex)))
        {
            var instance = byObject[materialGroup.Key.ObjectId];
            if (!instance.Visible)
                continue;

            var definition = presetCatalog.Find(instance.Preset.StableId).Definition;
            if (definition is null || string.Equals(definition.PipelineId, D3D12SurfaceStyleMap.DefaultStyleId, StringComparison.Ordinal))
                continue;

            var style = styleCatalog.FindStyle(definition.PipelineId);
            if (style is null)
                continue;

            var activeBindings = materialGroup
                .Where(b => string.Equals(b.Binding.StyleId, definition.PipelineId, StringComparison.Ordinal))
                .Select(static b => b.Binding)
                .ToArray();
            if (activeBindings.Length == 0)
                continue;

            charMatMap.TryGetValue(materialGroup.Key, out var charMat);
            var role = ResolveInspectionRole(
                style,
                materialGroup.Key.ObjectId,
                materialGroup.Key.LocalMaterialIndex,
                materialName: null,
                charMat,
                guessMaterialRoles: false,
                activeBindings).ResolvedRole;

            var validation = style.ValidateMaterial(new StyleMaterialValidationContext(
                materialGroup.Key.ObjectId,
                materialGroup.Key.LocalMaterialIndex,
                MaterialName: null,
                ResolvedRole: role,
                Bindings: activeBindings,
                SourceTextureCount: int.MaxValue,
                SourceTextureDimensions: null));

            if (!validation.IsValid)
                throw new ArgumentException(
                    validation.ErrorMessage ?? $"Material {materialGroup.Key.LocalMaterialIndex} validation failed for style '{definition.PipelineId}'.",
                    nameof(bindings));
        }
    }

    internal static StyleMaterialClassification ResolveInspectionRole(
        IRenderingStyleDefinition style,
        Guid objectId,
        int localMaterialIndex,
        string? materialName,
        D3D12ViewCharacterMaterial? charMat,
        bool guessMaterialRoles,
        IReadOnlyList<StyleMaterialBindingData>? activeBindings)
    {
        StyleMaterialRole? explicitRole = charMat is { ExplicitRole: true }
            ? D3D12StylePluginAdapter.ToContractRole(charMat.Role)
            : null;
        return style.ClassifyMaterial(new StyleMaterialInspectionContext(
            objectId,
            localMaterialIndex,
            materialName,
            explicitRole,
            guessMaterialRoles,
            activeBindings));
    }

    internal static StyleSceneTextureInfo[]? ResolveSceneTextures(
        IRenderingStyleDefinition style,
        StyleMaterialBindingContext bindingContext,
        OriginalFrameScene scene,
        IReadOnlyDictionary<Guid, SceneObjectRenderInputs> sceneInputs)
    {
        var requests = style.GetSceneTextureRequests(bindingContext);
        if (requests is null || requests.Count == 0)
            return null;

        if (!sceneInputs.TryGetValue(bindingContext.ObjectId, out var inputs)
            || inputs.ObjectId != bindingContext.ObjectId
            || !inputs.Visible)
        {
            throw new ArgumentException(
                $"Visible material {bindingContext.MaterialIndex} on object '{bindingContext.ObjectId:D}' requires same-sample scene inputs for style '{style.StyleId}'.",
                nameof(sceneInputs));
        }

        var snapshotTextures = scene.Surface.Snapshot.Textures;
        var resolved = new List<StyleSceneTextureInfo>(requests.Count);
        foreach (var req in requests)
        {
            if (req is null)
                throw new ArgumentException($"Style '{style.StyleId}' returned a null texture request.", nameof(style));
            if (req.LocalTextureIndex < 0)
                throw new ArgumentException($"Style '{style.StyleId}' requested invalid local texture index {req.LocalTextureIndex}.", nameof(style));
            if (!inputs.TextureIndices.TryGetValue(req.LocalTextureIndex, out int combined)
                || (uint)combined >= (uint)snapshotTextures.Count)
            {
                throw new ArgumentException(
                    $"Requested local texture {req.LocalTextureIndex} on object '{bindingContext.ObjectId:D}' has no valid mapping into this composed frame.",
                    nameof(sceneInputs));
            }
            var texture = snapshotTextures[combined];
            if ((req.ExpectedWidth is { } expectedW && texture.Width != expectedW)
                || (req.ExpectedHeight is { } expectedH && texture.Height != expectedH))
            {
                throw new ArgumentException(
                    $"Requested local texture {req.LocalTextureIndex} on object '{bindingContext.ObjectId:D}' has dimensions {texture.Width}x{texture.Height}, expected {req.ExpectedWidth?.ToString(CultureInfo.InvariantCulture) ?? "*"}x{req.ExpectedHeight?.ToString(CultureInfo.InvariantCulture) ?? "*"}.",
                    nameof(sceneInputs));
            }
            resolved.Add(new StyleSceneTextureInfo(req.LocalTextureIndex, combined, texture.Width, texture.Height));
        }

        return resolved.ToArray();
    }

    internal static StyleEvaluatedBoneTransform? ResolveEvaluatedBone(
        IRenderingStyleDefinition style,
        StyleMaterialBindingContext bindingContext,
        IReadOnlyDictionary<Guid, SceneObjectRenderInputs> sceneInputs,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? boneIndicesByObject)
    {
        var boneRequest = style.GetBoneTransformRequest(bindingContext);
        if (boneRequest is null)
            return null;

        if (!sceneInputs.TryGetValue(bindingContext.ObjectId, out var inputs)
            || inputs.ObjectId != bindingContext.ObjectId
            || !inputs.Visible)
        {
            throw new ArgumentException(
                $"Visible material {bindingContext.MaterialIndex} on object '{bindingContext.ObjectId:D}' requires same-sample scene inputs for bone '{boneRequest.BoneName}'.",
                nameof(sceneInputs));
        }

        if (!TryResolveBoneIndex(bindingContext.ObjectId, boneRequest.BoneName, boneIndicesByObject, out int boneIndex)
            || !inputs.BoneWorldMatrices.TryGetValue(boneIndex, out var world))
        {
            throw new ArgumentException(
                $"Requested bone '{boneRequest.BoneName}' on object '{bindingContext.ObjectId:D}' is absent from this geometry sample.",
                nameof(sceneInputs));
        }

        var forward = NormalizeDirection(Vector3.TransformNormal(boneRequest.LocalForward, world), boneRequest.LocalForward);
        var up = NormalizeDirection(Vector3.TransformNormal(boneRequest.LocalUp, world), boneRequest.LocalUp);
        var right = NormalizeDirection(Vector3.Cross(forward, up), Vector3.UnitX);
        return new StyleEvaluatedBoneTransform(boneRequest.BoneName, forward, right, up, world);
    }

    internal static bool TryResolveBoneIndex(
        Guid objectId,
        string? boneName,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>? boneIndicesByObject,
        out int boneIndex)
    {
        if (!string.IsNullOrWhiteSpace(boneName))
        {
            if (boneIndicesByObject is not null
                && boneIndicesByObject.TryGetValue(objectId, out var objectBones)
                && objectBones.TryGetValue(boneName, out int mapped))
            {
                if (mapped < 0)
                    throw new ArgumentOutOfRangeException(nameof(boneIndicesByObject), $"Mapped bone index for '{boneName}' must be nonnegative.");
                boneIndex = mapped;
                return true;
            }

            if (int.TryParse(boneName.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
            {
                boneIndex = parsed;
                return true;
            }
        }

        boneIndex = -1;
        return false;
    }

    private static Vector3 NormalizeDirection(Vector3 candidate, Vector3 fallback)
    {
        float lenSq = candidate.LengthSquared();
        if (float.IsFinite(lenSq) && lenSq > 1e-12f)
            return candidate / MathF.Sqrt(lenSq);
        float fallbackLenSq = fallback.LengthSquared();
        return float.IsFinite(fallbackLenSq) && fallbackLenSq > 1e-12f
            ? fallback / MathF.Sqrt(fallbackLenSq)
            : Vector3.UnitZ;
    }

    private sealed class DetachedBindings(D3D12ViewPluginMaterialBinding[] values)
        : IReadOnlyList<D3D12ViewPluginMaterialBinding>
    {
        internal static DetachedBindings Empty { get; } = new([]);
        public int Count => values.Length;
        public D3D12ViewPluginMaterialBinding this[int index] => values[index];
        public IEnumerator<D3D12ViewPluginMaterialBinding> GetEnumerator()
            => ((IEnumerable<D3D12ViewPluginMaterialBinding>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
