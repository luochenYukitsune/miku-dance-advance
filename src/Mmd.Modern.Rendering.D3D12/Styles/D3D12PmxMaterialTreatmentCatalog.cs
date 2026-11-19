namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Generic helpers for applying ordinary-PMX material treatments to unclassified or Body/Hair rows.</summary>
public static class D3D12PmxMaterialTreatmentCatalog
{
    /// <summary>Fills one explicitly classified ordinary material row without overwriting existing treatments
    /// or draws carrying custom plugin draw data.</summary>
    public static D3D12CharacterMaterialStyle ApplyDefault(
        D3D12CharacterMaterialStyle row,
        D3D12PmxMaterialTreatment? bodyTreatment,
        D3D12PmxMaterialTreatment? hairTreatment)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.PmxMaterialTreatment is not null || row.PluginDraw is not null)
            return row;
        var resolved = row.Role switch
        {
            D3D12CharacterMaterialRole.Body => bodyTreatment,
            D3D12CharacterMaterialRole.Hair => hairTreatment,
            _ => null,
        };
        return resolved is null ? row : row with { PmxMaterialTreatment = resolved };
    }

    /// <summary>Returns a new immutable map with absent ordinary Body/Hair treatments filled on non-default rows.</summary>
    public static D3D12SurfaceStyleMap ApplyDefaults(
        D3D12SurfaceStyleMap map,
        D3D12PmxMaterialTreatment? bodyTreatment,
        D3D12PmxMaterialTreatment? hairTreatment)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.CharacterMaterials.Count != map.Count)
            throw new ArgumentException("One explicit character material row per surface style is required.", nameof(map));
        var rows = new D3D12CharacterMaterialStyle[map.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            var row = map.CharacterMaterials[i]
                ?? throw new ArgumentException("Character material settings cannot contain null entries.", nameof(map));
            rows[i] = map.IsDefaultStyle(i) ? row : ApplyDefault(row, bodyTreatment, hairTreatment);
        }
        return new D3D12SurfaceStyleMap(map.StyleIds, rows, map.RegisteredStyles)
        {
            StylizedRoles = map.StylizedRoles,
        };
    }
}
