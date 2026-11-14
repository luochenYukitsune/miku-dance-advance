namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Renderer-owned ordinary-PMX profiles. Values adapt existing diffuse color and
/// lighting without requiring images or inferred material roles. These are MDA profiles,
/// not reconstructions of dedicated game materials.</summary>
public static class D3D12PmxMaterialTreatmentCatalog
{
    /// <summary>Resolves only explicitly selected modern Body/Hair materials. MMD, Face,
    /// Eye and unknown selections have no generic treatment.</summary>
    public static D3D12PmxMaterialTreatment? Resolve(D3D12SurfaceStyle family, D3D12CharacterMaterialRole role)
        => (family, role) switch
        {
            (D3D12SurfaceStyle.GenshinPmx, D3D12CharacterMaterialRole.Body) => new(1.04f, 1.1f, -.25f, .6f),
            (D3D12SurfaceStyle.GenshinPmx, D3D12CharacterMaterialRole.Hair) => new(1.08f, .9f, -.2f, .65f, .26f, .22f),
            (D3D12SurfaceStyle.StarRailPmx, D3D12CharacterMaterialRole.Body) => new(1f, .55f, .3f, 1.7f),
            (D3D12SurfaceStyle.StarRailPmx, D3D12CharacterMaterialRole.Hair) => new(1.03f, .55f, .12f, 1.5f, .38f, .05f),
            (D3D12SurfaceStyle.WutheringPmx, D3D12CharacterMaterialRole.Body) => new(.75f, 1.2f, .32f, 1.35f),
            (D3D12SurfaceStyle.WutheringPmx, D3D12CharacterMaterialRole.Hair) => new(.82f, 1.15f, .24f, 1.25f, .12f, .3f),
            (D3D12SurfaceStyle.EndfieldPmx, D3D12CharacterMaterialRole.Body) => new(.92f, .6f, -.2f, 2f),
            (D3D12SurfaceStyle.EndfieldPmx, D3D12CharacterMaterialRole.Hair) => new(.94f, .7f, -.14f, 1.8f, .3f, .12f),
            _ => null
        };

    /// <summary>Fills one explicitly classified ordinary material without allocating a
    /// replacement for MMD, protected roles, dedicated bindings or existing treatments.</summary>
    public static D3D12CharacterMaterialStyle ApplyDefault(D3D12SurfaceStyle family, D3D12CharacterMaterialStyle row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.PmxMaterialTreatment is null && !HasDedicatedBinding(row)
            && Resolve(family, row.Role) is { } treatment
                ? row with { PmxMaterialTreatment = treatment } : row;
    }

    /// <summary>Explicit host opt-in for an already classified material map. Returns a new
    /// immutable map, filling absent ordinary Body/Hair treatments only. The host must supply
    /// classifications; this method does not inspect names, colors, images or model geometry.</summary>
    public static D3D12SurfaceStyleMap ApplyDefaults(D3D12SurfaceStyleMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.CharacterMaterials.Count != map.Count)
            throw new ArgumentException("One explicit character material row per surface family is required.", nameof(map));
        var rows = new D3D12CharacterMaterialStyle[map.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            var row = map.CharacterMaterials[i]
                ?? throw new ArgumentException("Character material settings cannot contain null entries.", nameof(map));
            rows[i] = ApplyDefault(map.Materials[i], row);
        }
        return new D3D12SurfaceStyleMap(map.Materials, rows);
    }

    private static bool HasDedicatedBinding(D3D12CharacterMaterialStyle row)
        => row.FaceSdf is not null || row.GenshinNonMetallicSpecular is not null
            || row.StarMcHair is not null || row.EndfieldHair is not null || row.EndfieldRim is not null
            || row.GenshinHairShadowAtlas is not null || row.EndfieldMatcap is not null || row.GenshinMetallic is not null || row.StarMcBody is not null;
}
