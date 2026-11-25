using Mmd.Modern.Application;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>Indexed pipeline and material parameters shared by immediate resolution and cached frame preparation.</summary>
internal readonly struct D3D12ViewMaterialStyleTable
{
    private readonly string[] pipelineIds;
    private readonly D3D12CharacterMaterialStyle[] materials;
    private readonly D3D12CharacterMaterialRole[] roles;

    internal D3D12ViewMaterialStyleTable(int count)
    {
        pipelineIds = new string[count];
        Array.Fill(pipelineIds, D3D12SurfaceStyleMap.DefaultStyleId);
        materials = new D3D12CharacterMaterialStyle[count];
        Array.Fill(materials, new D3D12CharacterMaterialStyle());
        roles = new D3D12CharacterMaterialRole[count];
    }

    private D3D12ViewMaterialStyleTable(string[] pipelineIds, D3D12CharacterMaterialStyle[] materials,
        D3D12CharacterMaterialRole[] roles)
    {
        this.pipelineIds = pipelineIds;
        this.materials = materials;
        this.roles = roles;
    }

    /// <summary>Assigns a validated object range and returns its character pipeline when it needs further resolution.</summary>
    internal string? AssignSurfaceRange(SceneRenderingStyleBinding instance, D3D12StylePresetResult resolution,
        Dictionary<(Guid ObjectId, int LocalMaterialIndex), D3D12ViewCharacterMaterial> characterMaterials,
        List<string> diagnostics)
    {
        string pipelineId = resolution.Definition!.PipelineId;
        if (string.Equals(pipelineId, D3D12SurfaceStyleMap.DefaultStyleId, StringComparison.Ordinal))
        {
            if (instance.Visible && instance.Role == ModelRole.Character && characterMaterials.Count > 0)
            {
                for (int local = 0; local < instance.MaterialCount; local++)
                {
                    if (characterMaterials.TryGetValue((instance.ObjectId, local), out var material))
                        SetMaterial(instance.MaterialStart + local, new D3D12CharacterMaterialStyle(
                            Role: material.Role,
                            FaceNormalShadeStrength: material.FaceNormalShadeStrength,
                            FaceGeometricShadowStrength: material.FaceGeometricShadowStrength,
                            EyeGeometricShadowStrength: material.EyeGeometricShadowStrength));
                }
            }
            return null;
        }

        if (!instance.Visible || instance.MaterialCount == 0)
        {
            diagnostics.Add($"Hidden or empty object '{instance.ObjectId:D}' consumes no surface materials. {resolution.Diagnostic}");
            return null;
        }
        if (instance.Role != ModelRole.Character)
        {
            diagnostics.Add($"Object '{instance.ObjectId:D}' ({instance.Role}) keeps the MMD surface: {resolution.Diagnostic}");
            return null;
        }
        diagnostics.Add($"Object '{instance.ObjectId:D}': {resolution.Diagnostic}");
        Array.Fill(pipelineIds, pipelineId, instance.MaterialStart, instance.MaterialCount);
        return pipelineId;
    }

    /// <summary>Frame evaluation keeps the assigned pipeline ranges and writes its own material parameters.</summary>
    internal D3D12ViewMaterialStyleTable CopyMaterialParameters()
        => new(pipelineIds, (D3D12CharacterMaterialStyle[])materials.Clone(), (D3D12CharacterMaterialRole[])roles.Clone());

    internal void SetMaterial(int index, D3D12CharacterMaterialStyle material)
    {
        materials[index] = material;
        roles[index] = material.Role;
    }

    internal D3D12SurfaceStyleMap ToSurfaceStyleMap(D3D12StylePresetCatalog catalog)
        => new(pipelineIds, materials, catalog) { StylizedRoles = Array.AsReadOnly(roles) };
}
