using System.Collections;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>
/// Explicit per-material character rendering overrides for one scene instance.
/// <see cref="LocalMaterialIndex"/> is local to the instance's source material table, not the composed frame.
/// </summary>
public sealed record D3D12ViewCharacterMaterial(
    Guid ObjectId,
    int LocalMaterialIndex,
    D3D12CharacterMaterialRole Role = D3D12CharacterMaterialRole.Body,
    bool ExplicitRole = true,
    float FaceNormalShadeStrength = 0.35f,
    float FaceGeometricShadowStrength = 1f,
    float EyeGeometricShadowStrength = 0.2f,
    CharacterToonResponse? ToonResponse = null,
    D3D12PmxMaterialTreatment? PmxTreatment = null,
    D3D12StylizedShading? StylizedShading = null,
    bool PreserveDirectLightPalette = false)
{
    public void Validate()
    {
        if (ObjectId == Guid.Empty || LocalMaterialIndex < 0)
            throw new ArgumentException("Character material overrides require a nonempty object identity and nonnegative local material index.");
        if (!Enum.IsDefined(Role))
            throw new ArgumentOutOfRangeException(nameof(Role), "Unknown character material role.");
        if (!float.IsFinite(FaceNormalShadeStrength) || FaceNormalShadeStrength is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(FaceNormalShadeStrength), "Face normal shade strength must be finite and between zero and one.");
        if (!float.IsFinite(FaceGeometricShadowStrength) || FaceGeometricShadowStrength is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(FaceGeometricShadowStrength), "Face geometric shadow strength must be finite and between zero and one.");
        if (!float.IsFinite(EyeGeometricShadowStrength) || EyeGeometricShadowStrength is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(EyeGeometricShadowStrength), "Eye geometric shadow strength must be finite and between zero and one.");
        if (ToonResponse is not null)
            CharacterToonMath.Validate(ToonResponse);
        PmxTreatment?.Validate();
        if (ExplicitRole)
        {
            if (PmxTreatment is { HairHighlightStrength: > 0f } && Role != D3D12CharacterMaterialRole.Hair)
                throw new ArgumentException("Generic hair highlights require an explicit Hair material role.", nameof(PmxTreatment));
            if (PmxTreatment is not null && Role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair))
                throw new ArgumentException("Ordinary-PMX material treatment requires explicitly selected Body or Hair.", nameof(PmxTreatment));
        }
        StylizedShading?.Validate();
    }
}

internal static class D3D12ViewCharacterMaterials
{
    /// <summary>Snapshots host input before a device is created; already detached adapter input is reused.</summary>
    public static IReadOnlyList<D3D12ViewCharacterMaterial> Detach(
        IEnumerable<D3D12ViewCharacterMaterial>? materials)
    {
        if (materials is DetachedMaterials detached)
            return detached;

        var values = materials?.ToArray() ?? [];
        var keys = new HashSet<(Guid, int)>();
        foreach (var material in values)
        {
            if (material is null)
                throw new ArgumentException("Character material overrides cannot contain null.", nameof(materials));
            material.Validate();
            if (!keys.Add((material.ObjectId, material.LocalMaterialIndex)))
                throw new ArgumentException("Character material overrides must be unique for each object identity and local material index.", nameof(materials));
        }
        return new DetachedMaterials(values);
    }

    public static void ValidateObjects(
        IReadOnlyList<SceneRenderingStyleBinding> objects,
        IReadOnlyList<D3D12ViewCharacterMaterial> materials,
        List<string>? diagnostics = null)
    {
        if (materials.Count == 0)
            return;

        var byObject = objects.ToDictionary(instance => instance.ObjectId);
        foreach (var material in materials)
        {
            if (!byObject.TryGetValue(material.ObjectId, out var instance))
                throw new ArgumentException($"Character material override refers to unknown object '{material.ObjectId:D}'.", nameof(materials));
            if (instance.Role != ModelRole.Character)
                throw new ArgumentException($"Object '{material.ObjectId:D}' is {instance.Role}; character material overrides require an explicit Character role.", nameof(materials));
            if (!instance.Visible)
            {
                diagnostics?.Add($"Hidden object '{material.ObjectId:D}' retains local material {material.LocalMaterialIndex} ({material.Role}) without consuming a frame material; its upper bound is checked when the object is visible.");
                continue;
            }
            if (material.LocalMaterialIndex >= instance.MaterialCount)
                throw new ArgumentException($"Local material {material.LocalMaterialIndex} is outside visible object '{material.ObjectId:D}' with {instance.MaterialCount} materials.", nameof(materials));
        }
    }

    private sealed class DetachedMaterials(D3D12ViewCharacterMaterial[] values)
        : IReadOnlyList<D3D12ViewCharacterMaterial>
    {
        public int Count => values.Length;
        public D3D12ViewCharacterMaterial this[int index] => values[index];
        public IEnumerator<D3D12ViewCharacterMaterial> GetEnumerator()
            => ((IEnumerable<D3D12ViewCharacterMaterial>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
